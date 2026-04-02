using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CloudFix
{
    // one-time Google Drive OAuth browser flow for CloudRedirect.
    // the DLL handles token refresh -- this just gets the initial refresh_token.
    internal static class GoogleDriveAuth
    {
        // clasp (Google Apps Script CLI) OAuth credentials
        const string ClientId = "1072944905499-vm2v2i5dvn0a0d2o4ca36i1vge8cvbn0.apps.googleusercontent.com";
        const string ClientSecret = "v6V3fKV_zWU7iw1DrpO1rknX";
        const string Scope = "https://www.googleapis.com/auth/drive.file";
        const string AuthEndpoint = "https://accounts.google.com/o/oauth2/auth";
        const string TokenEndpoint = "https://oauth2.googleapis.com/token";

        public static string TokenPath { get; private set; }

        public static void Init(string steamPath)
        {
            // tokens live in %APPDATA%\CloudRedirect\ so each Windows user gets their own
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "CloudRedirect");
            Directory.CreateDirectory(dir);
            TokenPath = Path.Combine(dir, OneDriveAuth.TokenFilename);
        }

        public enum Status { Authenticated, NotAuthenticated, Error }

        public static Status GetStatus()
        {
            if (!File.Exists(TokenPath))
                return Status.NotAuthenticated;

            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(TokenPath));
                var refresh = json.RootElement.GetProperty("refresh_token").GetString();
                return string.IsNullOrEmpty(refresh) ? Status.NotAuthenticated : Status.Authenticated;
            }
            catch
            {
                return Status.Error;
            }
        }

        public static async Task<string> RunSignIn()
        {
            // allocate a random port and start HttpListener on it.
            // retry up to 5 times to avoid TOCTOU race on port allocation.
            HttpListener listener = null;
            string redirectUri = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var tcp = new TcpListener(IPAddress.Loopback, 0);
                tcp.Start();
                int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                tcp.Stop();

                redirectUri = $"http://localhost:{port}/";
                listener = new HttpListener();
                listener.Prefixes.Add(redirectUri);
                try
                {
                    listener.Start();
                    break;
                }
                catch (HttpListenerException) when (attempt < 4)
                {
                    listener = null;
                }
            }
            if (listener == null)
                return "Failed to allocate a local port for OAuth callback after 5 attempts";

            try
            {
                string authUrl =
                    AuthEndpoint +
                    $"?client_id={Uri.EscapeDataString(ClientId)}" +
                    $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                    "&response_type=code" +
                    $"&scope={Uri.EscapeDataString(Scope)}" +
                    "&access_type=offline" +
                    "&prompt=consent";

                Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

                HttpListenerContext ctx;
                try
                {
                    var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    ctx = await listener.GetContextAsync().WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return "Authentication timed out (2 minutes)";
                }

                var query = ctx.Request.Url?.Query ?? "";
                string code = ParseQueryParam(query, "code");
                string error = ParseQueryParam(query, "error");

                string html = !string.IsNullOrEmpty(code)
                    ? "<html><body style='font-family:sans-serif;text-align:center;padding:60px'>" +
                      "<h2>Authenticated! You can close this tab.</h2></body></html>"
                    : "<html><body style='font-family:sans-serif;text-align:center;padding:60px'>" +
                      "<h2>Authentication failed.</h2></body></html>";

                byte[] responseBytes = System.Text.Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html";
                ctx.Response.ContentLength64 = responseBytes.Length;
                await ctx.Response.OutputStream.WriteAsync(responseBytes);
                ctx.Response.Close();

                if (string.IsNullOrEmpty(code))
                    return $"Authentication failed: {error ?? "no authorization code received"}";

                // Google uses client_secret instead of PKCE
                using var http = new HttpClient();
                var tokenReq = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["client_id"] = ClientId,
                    ["client_secret"] = ClientSecret,
                    ["redirect_uri"] = redirectUri,
                    ["grant_type"] = "authorization_code"
                });

                var tokenResp = await http.PostAsync(TokenEndpoint, tokenReq);
                var tokenBody = await tokenResp.Content.ReadAsStringAsync();

                if (!tokenResp.IsSuccessStatusCode)
                    return $"Token exchange failed: HTTP {(int)tokenResp.StatusCode}\n{tokenBody}";

                using var tokenJson = JsonDocument.Parse(tokenBody);
                string accessToken = tokenJson.RootElement.GetProperty("access_token").GetString();
                int expiresIn = tokenJson.RootElement.GetProperty("expires_in").GetInt32();
                string refreshToken = tokenJson.RootElement.GetProperty("refresh_token").GetString();
                long expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn;

                var tokenOut = "{\n" +
                    $"  \"access_token\": \"{EscapeJson(accessToken)}\",\n" +
                    $"  \"refresh_token\": \"{EscapeJson(refreshToken)}\",\n" +
                    $"  \"expires_at\": {expiresAt}\n" +
                    "}";
                File.WriteAllText(TokenPath, tokenOut);

                return null;
            }
            finally
            {
                listener.Stop();
            }
        }

        public static void SignOut()
        {
            if (File.Exists(TokenPath))
                File.Delete(TokenPath);
        }

        static string ParseQueryParam(string query, string name)
        {
            if (string.IsNullOrEmpty(query))
                return null;

            if (query[0] == '?')
                query = query[1..];

            foreach (var pair in query.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var key = Uri.UnescapeDataString(pair[..eq]);
                if (key == name)
                    return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            return null;
        }

        static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }
    }
}
