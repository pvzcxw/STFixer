using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace CloudFix
{
    internal class Program
    {
        static readonly string _version;

        static Program()
        {
            var raw = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "0.0.0";
            // strip source link hash (everything after +)
            int plus = raw.IndexOf('+');
            _version = plus > 0 ? raw[..plus] : raw;
        }

        static string _steamPath;

        static void Main(string[] args)
        {
            try { Console.Title = "CloudFix"; } catch { }
            ClearScreen();
            PrintHeader();

            _steamPath = DetectSteamPath();
            if (_steamPath == null)
            {
                PrintLine("Steam: not found");
                PrintLine("Could not locate Steam install. Check your registry.");
                Console.ReadKey(true);
                return;
            }

            PrintLine($"Steam: {_steamPath}");

            // non-blocking update check, don't stall the menu
            try
            {
                var updateTask = Updater.CheckForUpdate(_version);
                if (updateTask.Wait(TimeSpan.FromSeconds(4)) && updateTask.Result != null)
                {
                    PrintSep();
                    PrintLine($"Update available: {updateTask.Result.TagName}");
                    if (!string.IsNullOrWhiteSpace(updateTask.Result.Body))
                    {
                        Console.WriteLine();
                        foreach (var line in updateTask.Result.Body.Split('\n'))
                            PrintLine(line.TrimEnd('\r'));
                        Console.WriteLine();
                    }
                    Console.Write("  Download and install? [Y/n] ");
                    var key = Console.ReadKey(true);
                    Console.WriteLine(key.KeyChar);
                    if (key.KeyChar is not ('n' or 'N'))
                    {
                        Updater.ApplyUpdate(updateTask.Result).GetAwaiter().GetResult();
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // update check failed silently - network down, rate limited, whatever
            }

            PrintSep();

            var patcher = new Patcher(_steamPath);

            while (true)
            {
                ClearScreen();
                PrintHeader();
                PrintLine($"Steam: {_steamPath}");
                PrintSep();
                RunDiagnostics(patcher);
                PrintSep();
                Console.WriteLine();
                Console.WriteLine("  1. Enable  (patch SteamTools)");
                Console.WriteLine("  2. Disable (restore originals)");
                Console.WriteLine("  3. Exit");
                Console.WriteLine();
                Console.Write("  > ");

                var choice = Console.ReadKey(true);
                Console.WriteLine(choice.KeyChar);
                Console.WriteLine();

                switch (choice.KeyChar)
                {
                    case '1':
                        ClearScreen();
                        PrintHeader();
                        PrintLine($"Steam: {_steamPath}");
                        PrintSep();
                        Console.WriteLine();
                        var applyResult = patcher.Apply();
                        Console.WriteLine();
                        if (applyResult.Succeeded)
                        {
                            PrintGreen("Cloud Fix: enabled");
                            OfferSteamRestart();
                        }
                        else
                        {
                            PrintRed($"Error: {applyResult.Error}");
                            WaitForKey();
                        }
                        break;

                    case '2':
                        ClearScreen();
                        PrintHeader();
                        PrintLine($"Steam: {_steamPath}");
                        PrintSep();
                        Console.WriteLine();
                        var restoreResult = patcher.Restore();
                        Console.WriteLine();
                        if (restoreResult.Succeeded)
                        {
                            PrintRed("Cloud Fix: disabled");
                            OfferSteamRestart();
                        }
                        else
                        {
                            PrintRed($"Error: {restoreResult.Error}");
                            WaitForKey();
                        }
                        break;

                    case '3':
                        return;

                    default:
                        break;
                }
            }
        }

        static void RunDiagnostics(Patcher patcher)
        {
            var state = patcher.GetPatchState();

            string label = state switch
            {
                PatchState.NotInstalled => "SteamTools not detected",
                PatchState.Unpatched => "not patched",
                PatchState.Patched => "patched",
                PatchState.PartiallyPatched => "partially patched",
                PatchState.UnknownVersion => "unknown SteamTools version",
                _ => "unknown"
            };

            bool good = state == PatchState.Patched;
            if (good)
                PrintGreen($"Cloud Fix: {label}");
            else
                PrintRed($"Cloud Fix: {label}");
        }

        static string DetectSteamPath()
        {
            // check both 64 and 32 bit registry views
            string[] keys =
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
                @"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam",
            };

            foreach (var key in keys)
            {
                try
                {
                    var val = Registry.GetValue(key, "InstallPath", null) as string;
                    if (val != null && Directory.Exists(val))
                        return val;
                }
                catch (Exception) { }
            }

            string[] guesses =
            {
                @"C:\Program Files (x86)\Steam",
                @"C:\Program Files\Steam",
                @"C:\games\steam",
            };

            foreach (var path in guesses)
            {
                if (Directory.Exists(path))
                    return path;
            }

            return null;
        }

        static void PrintHeader()
        {
            Console.WriteLine();
            Console.WriteLine($"  CloudFix v{_version}");
            Console.WriteLine("  Steam Cloud save fix for SteamTools");
            Console.WriteLine();
        }

        static void ClearScreen()
        {
            try { Console.Clear(); } catch { }
        }

        static void WaitForKey()
        {
            Console.WriteLine();
            Console.Write("  Press any key to continue..");
            Console.ReadKey(true);
        }

        static readonly string[] SteamProcessNames =
        {
            "steam", "steamservice", "steamwebhelper",
            "SteamService", "GameOverlayUI",
        };

        static bool OfferSteamRestart()
        {
            if (!AnySteamRunning())
                return false;

            Console.WriteLine();
            Console.Write("  Restart Steam now? [Y/n] ");
            var key = Console.ReadKey(true);
            Console.WriteLine(key.KeyChar);

            if (key.KeyChar is 'n' or 'N')
                return false;

            PrintLine("Shutting down Steam..");
            try
            {
                var steamExe = Path.Combine(_steamPath, "steam.exe");
                Process.Start(new ProcessStartInfo(steamExe, "-shutdown") { UseShellExecute = true });

                // wait for all steam processes to exit
                var deadline = Environment.TickCount64 + 15000;
                while (Environment.TickCount64 < deadline && AnySteamRunning())
                    System.Threading.Thread.Sleep(500);

                // kill anything still hanging around
                KillAllSteam();

                PrintLine("Restarting Steam..");
                Process.Start(new ProcessStartInfo(steamExe) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                PrintLine($"Warning: could not restart Steam - {ex.Message}");
                return false;
            }

            return true;
        }

        static bool AnySteamRunning()
        {
            foreach (var name in SteamProcessNames)
            {
                var procs = Process.GetProcessesByName(name);
                bool found = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (found) return true;
            }
            return false;
        }

        static void KillAllSteam()
        {
            foreach (var name in SteamProcessNames)
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(); } catch { }
                    p.Dispose();
                }
            }
        }

        internal static void PrintSep()
        {
            Console.WriteLine("==================================================");
        }

        internal static void PrintLine(string msg)
        {
            Console.WriteLine($"  {msg}");
        }

        internal static void PrintGreen(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  {msg}");
            Console.ResetColor();
        }

        internal static void PrintRed(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  {msg}");
            Console.ResetColor();
        }
    }
}
