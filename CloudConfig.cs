using System;
using System.IO;
using System.Text.Json;

namespace CloudFix
{
    // reads/writes %APPDATA%\CloudRedirect\config.json for backend selection.
    // the DLL reads the same file at init time.
    internal static class CloudConfig
    {
        static string _configPath;

        public static void Init()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "CloudRedirect");
            Directory.CreateDirectory(dir);
            _configPath = Path.Combine(dir, "config.json");
        }

        // returns "gdrive" or "onedrive"
        public static string GetBackend()
        {
            if (_configPath == null || !File.Exists(_configPath))
                return "onedrive";

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_configPath));
                if (doc.RootElement.TryGetProperty("backend", out var val))
                {
                    var b = val.GetString();
                    if (b == "gdrive" || b == "onedrive")
                        return b;
                }
            }
            catch { }

            return "onedrive";
        }

        public static void SetBackend(string backend)
        {
            File.WriteAllText(_configPath, $"{{\n  \"backend\": \"{backend}\"\n}}");

            // switching backends invalidates tokens — delete so the DLL re-auths
            var tokenPath = Path.Combine(Path.GetDirectoryName(_configPath), "tokens.json");
            try { if (File.Exists(tokenPath)) File.Delete(tokenPath); }
            catch { }
        }

        // display name for the current backend
        public static string BackendDisplayName(string backend = null)
        {
            return (backend ?? GetBackend()) switch
            {
                "gdrive" => "Google Drive",
                "onedrive" => "OneDrive",
                _ => "Unknown"
            };
        }
    }
}
