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
            // Upgrading an existing install: the dialogs are skipped (see ResolveUpgradeRoute).
            if (!string.IsNullOrEmpty(session["WIX_UPGRADE_DETECTED"])) session["LARIS_UPGRADE"] = "1";
            using (var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + product.ServiceName))
            {
                var imagePath = services?.GetValue("ImagePath") as string;
                if (string.IsNullOrEmpty(imagePath)) return ActionResult.Success;

                // A service with no matching MSI product was installed by the PowerShell script.
                if (string.IsNullOrEmpty(session["WIX_UPGRADE_DETECTED"]) && string.IsNullOrEmpty(session["Installed"]))
                {
                    session["LEGACYINSTALL"] = "1";
                    session["LARIS_UPGRADE"] = "1";
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

            // The web port and certificate live in appsettings.Production.json; read them so the firewall
            // rule and the dialog match what the server actually uses. INSTALLFOLDER is only known this
            // early when a service already exists, so a config left from an earlier install (or put
            // back by hand) is looked for in the default folder too. The certificate password is never
            // read into a property — the dialog says one is stored, and blank keeps it.
            if (product.Key == "Web")
            {
                var folder = !string.IsNullOrEmpty(session["INSTALLFOLDER"])
                    ? session["INSTALLFOLDER"]
                    : Path.Combine(session["ProgramFiles64Folder"], "LarisVMS", "Web");
                var config = Path.Combine(folder, "appsettings.Production.json");
                string? json = null;
                try { if (File.Exists(config)) json = File.ReadAllText(config); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { session.Log("Could not read " + config + ": " + ex.Message); }
                if (json is not null)
                {
                    var port = Regex.Match(json, "\"HttpsPort\"\\s*:\\s*(\\d+)");
                    if (port.Success) FillIfEmpty(session, "HTTPSPORT", port.Groups[1].Value);
                    if (Util.CertBlock(json) is { } cert)
                    {
                        var certPath = Util.JsonReadString(cert.Value, "Path");
                        if (!string.IsNullOrEmpty(certPath)) FillIfEmpty(session, "CERTPATH", certPath!);
                        if (!string.IsNullOrEmpty(Util.JsonReadString(cert.Value, "Password"))) session["CERTPASSWORDSTORED"] = "1";
                    }
                }
            }

            // A node's or proxy's direct-streaming endpoint lives in its endpoint JSON, which neither the
            // service command line nor (for a script install) the registry remembers. Without reading it
            // here, taking over an existing install deleted the script's "Client Endpoint" firewall rule
            // (RemoveLegacyInstall) and never recreated it, so browsers couldn't reach the endpoint, and
            // a proxy fell back to the default port. Read before the defaults below so the real port wins.
            if (product.Key == "Node" || product.Key == "Proxy")
                LoadEndpointConfig(session, Path.Combine(Util.ProgramDataLaris,
                    product.Key == "Node" ? "client-endpoint.json" : "proxy-endpoint.json"), requireEnabled: product.Key == "Node");

            foreach (var d in product.Defaults) FillIfEmpty(session, d.Key, d.Value);

            if (product.RegistrationFile is not null && File.Exists(Path.Combine(Util.ProgramDataLaris, product.RegistrationFile)))
                session["ALREADYREGISTERED"] = "1";
            NormalizePaths(session);
            return ActionResult.Success;
        }

        /// <summary>Upgrade "Next" handler on the welcome page: the settings are already known, so go
        /// straight to the first page that still fails validation — normally none, so UpgradeReadyDlg.
        /// A service running as a password account still lands on ServiceAccountDlg, since the
        /// upgrade re-creates the service and the password is never stored.</summary>
        [CustomAction]
        public static ActionResult ResolveUpgradeRoute(Session session)
        {
            NormalizePaths(session);
            var product = Product.Get(session["LARIS_PRODUCT"]);
            var pages = product.Key switch
            {
                "Web" => new[] { ("Web", "WebSettingsDlg") },
                "Node" => new[] { ("Server", "NodeServerDlg"), ("Endpoint", "NodeEndpointDlg") },
                _ => new[] { ("Server", "ProxySettingsDlg"), ("Endpoint", "ProxySettingsDlg") },
            };
            var target = "UpgradeReadyDlg";
            foreach (var (page, dialog) in pages.Concat(new[] { ("Account", "ServiceAccountDlg") }))
            {
                var error = Validate(session, page);
                if (error is null) continue;
                session.Log("Upgrade: " + page + " page needs input — " + error);
                target = dialog;
                break;
            }
            session["LARIS_UPGRADE_DLG"] = target;
            return ActionResult.Success;
        }

        /// <summary>Strips whitespace and surrounding quotes from path properties, whether typed in a
        /// dialog or passed on the command line.</summary>
        private static void NormalizePaths(Session session)
        {
            foreach (var prop in new[] { "CERTPATH", "CLIENTPFXPATH", "FFMPEGPATH", "STORAGEROOT", "ARCHIVEROOT" })
            {
                var value = session[prop];
                var clean = Util.CleanPath(value);
                if (clean != value) session[prop] = clean;
            }
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
            NormalizePaths(session);
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
            NormalizePaths(session);
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
            if (All("Web") && product.Key == "Web")
            {
                if (!Util.IsPort(session["HTTPSPORT"])) return "HTTPS port must be a number from 1 to 65535 (property HTTPSPORT).";
                var existing = string.IsNullOrEmpty(session["INSTALLFOLDER"]) ? null : Path.Combine(session["INSTALLFOLDER"], "appsettings.Production.json");
                var certError = CheckCertificate(session, "CERTPATH", "CERTPASSWORD", existing, "Path", "Password");
                if (certError is not null) return certError;
            }
            if (All("Endpoint") && product.Key == "Proxy" && !Util.IsPort(session["CLIENTPORT"]))
                return "Client port must be a number from 1 to 65535 (property CLIENTPORT).";
            if (All("Endpoint") && product.Key == "Node" && !string.IsNullOrEmpty(session["CLIENTPORT"]) && !Util.IsPort(session["CLIENTPORT"]))
                return "Client port must be a number from 1 to 65535 (property CLIENTPORT).";
            if (All("Endpoint") && (product.Key == "Proxy" || (product.Key == "Node" && !string.IsNullOrEmpty(session["CLIENTPORT"]))))
            {
                var existing = Path.Combine(Util.ProgramDataLaris, product.Key == "Node" ? "client-endpoint.json" : "proxy-endpoint.json");
                var certError = CheckCertificate(session, "CLIENTPFXPATH", "CLIENTPFXPASSWORD", existing, "pfxPath", "pfxPassword");
                if (certError is not null) return certError;
            }
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
                // Lets the rollback action tell a file it created (delete it) from one it edited (restore it).
                ["ConfigExisted"] = !string.IsNullOrEmpty(installDir) && File.Exists(Path.Combine(installDir, "appsettings.Production.json")) ? "1" : "0",
            };
            session["SeedWebConfig"] = web.ToString();
            var webUndo = new CustomActionData { ["InstallDir"] = installDir, ["ConfigExisted"] = web["ConfigExisted"] };
            session["SeedWebConfigRollback"] = webUndo.ToString();
            session["SeedWebConfigCommit"] = webUndo.ToString();
            return ActionResult.Success;
        }

        /// <summary>Opens the .pfx with the given password. A blank password means "keep the current
        /// one" (as SeedWebConfig / WriteEndpointConfig do), so the existing config's password is tried;
        /// and an unchanged path with no new password is left alone — nothing about it is changing, and
        /// the installing user may not be able to read a share the service account can.</summary>
        private static string? CheckCertificate(Session session, string pathProp, string passwordProp,
            string? existingConfig, string existingPathKey, string existingPasswordKey)
        {
            var path = session[pathProp];
            if (string.IsNullOrEmpty(path)) return null;
            var password = session[passwordProp];
            if (string.IsNullOrEmpty(password) && existingConfig is not null)
            {
                string? json = null;
                try { if (File.Exists(existingConfig)) json = File.ReadAllText(existingConfig); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                if (json is not null)
                {
                    // appsettings: only the Kestrel certificate block; the endpoint JSON files are flat.
                    var scope = existingPathKey == "Path" ? Util.CertBlock(json)?.Value ?? "" : json;
                    if (string.Equals(Util.CleanPath(Util.JsonReadString(scope, existingPathKey)), path, StringComparison.OrdinalIgnoreCase))
                        return null;
                    password = Util.JsonReadString(scope, existingPasswordKey) ?? "";
                }
            }
            return Util.CheckPfx(path, password, pathProp, passwordProp);
        }

        /// <summary>Fills the CLIENT* properties from an existing endpoint JSON (see LoadSettings). The
        /// password is never read into a property: WriteEndpointConfig keeps the stored one when the
        /// field is left blank.</summary>
        private static void LoadEndpointConfig(Session session, string path, bool requireEnabled)
        {
            string json;
            try
            {
                if (!File.Exists(path)) return;
                json = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                session.Log("Could not read " + path + ": " + ex.Message);
                return;
            }

            var endpoint = Util.ReadEndpointConfig(json, requireEnabled);
            if (endpoint is null) return;
            FillIfEmpty(session, "CLIENTPORT", endpoint.Port);
            if (endpoint.Host is not null) FillIfEmpty(session, "CLIENTENDPOINTHOST", endpoint.Host);
            if (endpoint.PfxPath is not null) FillIfEmpty(session, "CLIENTPFXPATH", endpoint.PfxPath);
            if (endpoint.AllowInsecure) FillIfEmpty(session, "CLIENTALLOWINSECURE", "1");
            session.Log("Existing endpoint config found at " + path + ": port " + endpoint.Port + ".");
        }

        private static void FillIfEmpty(Session session, string property, string value)
        {
            if (string.IsNullOrEmpty(session[property])) session[property] = value;
        }
    }
}
