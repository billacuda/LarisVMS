using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WixToolset.Dtf.WindowsInstaller;

namespace LarisVMS.Installer
{
    /// <summary>Immediate custom actions: work out every setting before the dialogs show and before
    /// the install script runs. Order (both UI and execute sequences): DetectExisting → LoadSettings →
    /// FindFfmpeg. Values given on the command line always win; then whatever the existing service
    /// was running with; then remembered values; then defaults.</summary>
    public static class SettingsActions
    {
        [CustomAction]
        public static ActionResult DetectExisting(Session session)
        {
            var product = Product.Get(session["LARIS_PRODUCT"]);
            using (var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + product.ServiceName))
            {
                var imagePath = services?.GetValue("ImagePath") as string;
                if (string.IsNullOrEmpty(imagePath)) return ActionResult.Success;

                // A service with no matching MSI product was installed by the PowerShell script.
                if (string.IsNullOrEmpty(session["WIX_UPGRADE_DETECTED"]) && string.IsNullOrEmpty(session["Installed"]))
                {
                    session["LEGACYINSTALL"] = "1";
                    session.Log("Found a script-installed " + product.ServiceName + " service; the MSI will take it over.");
                }

                var parts = Util.SplitCommandLine(Environment.ExpandEnvironmentVariables(imagePath!));
                if (parts.Count == 0) return ActionResult.Success;

                var exeDir = Path.GetDirectoryName(parts[0]);
                if (string.IsNullOrEmpty(session["INSTALLFOLDER"]) && !string.IsNullOrEmpty(exeDir))
                    session["INSTALLFOLDER"] = exeDir!.TrimEnd('\\') + "\\";

                var argMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["--server-url"] = "SERVERURL", ["--storage-root"] = "STORAGEROOT", ["--archive-root"] = "ARCHIVEROOT",
                    ["--ffmpeg-path"] = "FFMPEGPATH", ["--live-port"] = "LIVEPORT",
                };
                for (int i = 1; i < parts.Count; i++)
                {
                    if (parts[i].Equals("--insecure-tls", StringComparison.OrdinalIgnoreCase))
                        FillIfEmpty(session, "INSECURETLS", "1");
                    else if (argMap.TryGetValue(parts[i], out var prop) && i + 1 < parts.Count)
                        FillIfEmpty(session, prop, parts[++i]);
                }

                var account = services!.GetValue("ObjectName") as string;
                if (!Util.IsLocalSystem(account)) FillIfEmpty(session, "SERVICEACCOUNT", account!);
            }
            return ActionResult.Success;
        }

        [CustomAction]
        public static ActionResult LoadSettings(Session session)
        {
            var product = Product.Get(session["LARIS_PRODUCT"]);
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hklm.OpenSubKey(product.RegistryKey))
            {
                foreach (var prop in product.Remembered)
                    if (key?.GetValue(prop) is string remembered && remembered.Length > 0)
                        FillIfEmpty(session, prop, remembered);
            }

            // The web port lives in appsettings.Production.json; read it so the firewall rule and the
            // dialog match what the server actually listens on.
            if (product.Key == "Web" && string.IsNullOrEmpty(session["HTTPSPORT"]) && !string.IsNullOrEmpty(session["INSTALLFOLDER"]))
            {
                var config = Path.Combine(session["INSTALLFOLDER"], "appsettings.Production.json");
                if (File.Exists(config))
                {
                    var m = Regex.Match(File.ReadAllText(config), "\"HttpsPort\"\\s*:\\s*(\\d+)");
                    if (m.Success) session["HTTPSPORT"] = m.Groups[1].Value;
                }
            }

            foreach (var d in product.Defaults) FillIfEmpty(session, d.Key, d.Value);

            if (product.RegistrationFile is not null && File.Exists(Path.Combine(Util.ProgramDataLaris, product.RegistrationFile)))
                session["ALREADYREGISTERED"] = "1";
            return ActionResult.Success;
        }

        /// <summary>Mirrors install-node.ps1's Find-Ffmpeg and LarisVMS.Media.FfmpegPathResolver: an
        /// explicit path, then PATH, then the newest ffmpeg.exe under a WinGet package folder.</summary>
        [CustomAction]
        public static ActionResult FindFfmpeg(Session session)
        {
            var explicitPath = session["FFMPEGPATH"];
            string? found = null;
            if (!string.IsNullOrEmpty(explicitPath))
            {
                if (File.Exists(explicitPath)) found = explicitPath;
            }
            else
            {
                var path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) + ";" +
                           Environment.GetEnvironmentVariable("Path");
                foreach (var dir in path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe");
                        if (File.Exists(candidate)) { found = candidate; break; }
                    }
                    catch (ArgumentException) { /* malformed PATH entry */ }
                }

                if (found is null)
                {
                    var roots = new[]
                    {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"WinGet\Packages"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages"),
                    };
                    foreach (var root in roots.Where(Directory.Exists))
                    {
                        try
                        {
                            found = Directory.EnumerateFiles(root, "ffmpeg.exe", SearchOption.AllDirectories)
                                .Where(f => f.IndexOf("ffmpeg", StringComparison.OrdinalIgnoreCase) >= 0)
                                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                                .FirstOrDefault();
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) { }
                        if (found is not null) break;
                    }
                }
            }

            session["FFMPEGRESOLVED"] = found ?? "";
            session.Log("FFmpeg: " + (found ?? "not found"));
            return ActionResult.Success;
        }

        /// <summary>Dialog "Next" handler: sets SETTINGS_VALID / SETTINGS_ERROR for the dialog to act on.</summary>
        [CustomAction]
        public static ActionResult ValidateDialog(Session session)
        {
            var page = session["LARIS_PAGE"];
            var error = Validate(session, page);
            session["SETTINGS_VALID"] = error is null ? "1" : "0";
            session["SETTINGS_ERROR"] = error ?? "";
            return ActionResult.Success;
        }

        /// <summary>Execute-sequence check, so a silent install with missing values fails cleanly
        /// instead of installing a service that can't start.</summary>
        [CustomAction]
        public static ActionResult ValidateInstall(Session session)
        {
            var error = Validate(session, page: null);
            if (error is null) return ActionResult.Success;

            session.Log("Validation failed: " + error);
            using (var record = new Record(1))
            {
                record[0] = "[1]";
                record[1] = error;
                session.Message(InstallMessage.Error | (InstallMessage)MessageButtons.OK, record);
            }
            return ActionResult.Failure;
        }

        /// <summary>Checks one dialog page, or everything when page is null.</summary>
        internal static string? Validate(Session session, string? page)
        {
            var product = Product.Get(session["LARIS_PRODUCT"]);
            bool All(string p) => page is null || page == p;
            var registered = session["ALREADYREGISTERED"] == "1";

            if (All("Server") && product.Key != "Web" && !registered)
            {
                if (!Uri.TryCreate(session["SERVERURL"], UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                    return "Enter the LarisVMS server URL, for example https://vms.example.com:8444 (property SERVERURL).";
                if (string.IsNullOrWhiteSpace(session["REGISTRATIONKEY"]))
                    return "Enter the registration key from Settings → Node defaults on the server (property REGISTRATIONKEY).";
            }
            if (All("Server") && product.Key == "Node")
            {
                if (!Util.IsPort(session["LIVEPORT"])) return "Live port must be a number from 1 to 65535 (property LIVEPORT).";
                if (string.IsNullOrEmpty(session["FFMPEGRESOLVED"]))
                    return string.IsNullOrEmpty(session["FFMPEGPATH"])
                        ? "FFmpeg was not found. Install it with 'winget install ffmpeg --scope machine', or set its path (property FFMPEGPATH)."
                        : "FFmpeg was not found at " + session["FFMPEGPATH"] + " (property FFMPEGPATH).";
            }
            if (All("Web") && product.Key == "Web" && !Util.IsPort(session["HTTPSPORT"]))
                return "HTTPS port must be a number from 1 to 65535 (property HTTPSPORT).";
            if (All("Endpoint") && product.Key == "Proxy" && !Util.IsPort(session["CLIENTPORT"]))
                return "Client port must be a number from 1 to 65535 (property CLIENTPORT).";
            if (All("Endpoint") && product.Key == "Node" && !string.IsNullOrEmpty(session["CLIENTPORT"]) && !Util.IsPort(session["CLIENTPORT"]))
                return "Client port must be a number from 1 to 65535 (property CLIENTPORT).";
            if (All("Account") && Util.NeedsPassword(session["SERVICEACCOUNT"]) && string.IsNullOrEmpty(session["SERVICEPASSWORD"]))
                return "Enter the password for " + session["SERVICEACCOUNT"] + " (property SERVICEPASSWORD), or leave the account blank to run as LocalSystem.";
            return null;
        }

        /// <summary>Builds the service command line the same way install-node.ps1 / install-proxy.ps1
        /// do. The server URL and key are only passed while still unregistered — once node.config /
        /// proxy.config exists the service ignores them, and the key shouldn't sit in the registry.</summary>
        [CustomAction]
        public static ActionResult BuildServiceArgs(Session session)
        {
            var product = Product.Get(session["LARIS_PRODUCT"]);
            var args = new List<string>();
            if (!string.IsNullOrEmpty(session["SERVERURL"]) && !string.IsNullOrEmpty(session["REGISTRATIONKEY"]))
            {
                args.Add("--server-url"); args.Add(session["SERVERURL"]);
                args.Add("--registration-key"); args.Add(session["REGISTRATIONKEY"]);
            }
            if (product.Key == "Node")
            {
                args.Add("--live-port"); args.Add(session["LIVEPORT"]);
                if (!string.IsNullOrEmpty(session["FFMPEGPATH"])) { args.Add("--ffmpeg-path"); args.Add(session["FFMPEGRESOLVED"]); }
                if (!string.IsNullOrEmpty(session["STORAGEROOT"])) { args.Add("--storage-root"); args.Add(session["STORAGEROOT"]); }
                if (!string.IsNullOrEmpty(session["ARCHIVEROOT"])) { args.Add("--archive-root"); args.Add(session["ARCHIVEROOT"]); }
            }
            if (session["INSECURETLS"] == "1") args.Add("--insecure-tls");

            session["SERVICEARGS"] = string.Join(" ", args.Select(Util.QuoteArg));
            return ActionResult.Success;
        }

        /// <summary>Packs CustomActionData for every deferred action of this product (DTF escapes the
        /// values, so passwords containing ';' survive).</summary>
        [CustomAction]
        public static ActionResult PrepareDeferred(Session session)
        {
            var product = Product.Get(session["LARIS_PRODUCT"]);
            var installDir = session["INSTALLFOLDER"];

            var common = new CustomActionData
            {
                ["Product"] = product.Key,
                ["InstallDir"] = installDir,
                ["UserLocalAppData"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ["UserAppData"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            };
            session["RemoveLegacyInstall"] = common.ToString();
            session["StopNodeChildren"] = common.ToString();
            session["CopyCudaDeps"] = common.ToString();
            session["RemoveCudaDeps"] = common.ToString();
            session["SetFailureFlag"] = common.ToString();

            var settings = new CustomActionData { ["Product"] = product.Key };
            foreach (var prop in product.Remembered) settings[prop] = session[prop];
            session["SaveSettings"] = settings.ToString();
            session["ForgetSettings"] = settings.ToString();

            var endpoint = new CustomActionData
            {
                ["Product"] = product.Key,
                ["Port"] = session["CLIENTPORT"],
                ["Host"] = session["CLIENTENDPOINTHOST"],
                ["PfxPath"] = session["CLIENTPFXPATH"],
                ["PfxPassword"] = session["CLIENTPFXPASSWORD"],
                ["AllowInsecure"] = session["CLIENTALLOWINSECURE"] == "1" ? "1" : "0",
            };
            session["WriteEndpointConfig"] = endpoint.ToString();

            var web = new CustomActionData
            {
                ["InstallDir"] = installDir,
                ["HttpsPort"] = session["HTTPSPORT"],
                ["CertPath"] = session["CERTPATH"],
                ["CertPassword"] = session["CERTPASSWORD"],
            };
            session["SeedWebConfig"] = web.ToString();
            return ActionResult.Success;
        }

        private static void FillIfEmpty(Session session, string property, string value)
        {
            if (string.IsNullOrEmpty(session[property])) session[property] = value;
        }
    }
}
