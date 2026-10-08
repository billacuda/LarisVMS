using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using WixToolset.Dtf.WindowsInstaller;

namespace LarisVMS.Installer
{
    /// <summary>Deferred (elevated, LocalSystem) custom actions. Each reads its inputs from
    /// CustomActionData packed by SettingsActions.PrepareDeferred.</summary>
    public static class InstallActions
    {
        /// <summary>Takes over a script-installed service: stops and deletes it, removes the firewall
        /// rules the script created, and clears the old program files so the MSI's copies are the only
        /// ones left. ProgramData (registration, keys, recordings, endpoint config) is never touched.</summary>
        [CustomAction]
        public static ActionResult RemoveLegacyInstall(Session session)
        {
            var data = session.CustomActionData;
            var product = Product.Get(data["Product"]);
            var installDir = data["InstallDir"];

            StopService(session, product.ServiceName);
            if (product.Key == "Node") KillNodeChildren(session);
            Util.Run("sc.exe", "delete " + product.ServiceName);
            foreach (var rule in product.LegacyFirewallRules)
                Util.Run("netsh.exe", "advfirewall firewall delete rule name=\"" + rule + "\"");

            if (Directory.Exists(installDir) && File.Exists(Path.Combine(installDir, product.ExeName)))
            {
                // Everything here came from the script's publish copy except these, which belong to the
                // operator (config) or are runtime data.
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "setup-generated.json", "appsettings.Production.json", "appsettings.Development.json",
                    "logs", "data-protection-keys", "recordings", "spool", "exports",
                };
                foreach (var entry in Directory.EnumerateFileSystemEntries(installDir))
                {
                    if (keep.Contains(Path.GetFileName(entry))) continue;
                    try
                    {
                        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                        else File.Delete(entry);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        session.Log("Could not remove " + entry + ": " + ex.Message);
                    }
                }
            }
            return ActionResult.Success;
        }

        /// <summary>FFmpeg and the vision service are children of the node; when the node stops
        /// abruptly they can outlive it and keep the install folder's files locked. Same as
        /// install-node.ps1 L124-145. Also removes leftover .bak files from auto-update swaps.</summary>
        [CustomAction]
        public static ActionResult StopNodeChildren(Session session)
        {
            KillNodeChildren(session);
            var installDir = session.CustomActionData["InstallDir"];
            // *.exe.old: what the service's own in-place update swap moves the previous binary to.
            if (Directory.Exists(installDir))
                foreach (var leftover in Directory.EnumerateFiles(installDir, "*.bak").Concat(Directory.EnumerateFiles(installDir, "*.exe.old")))
                    try { File.Delete(leftover); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            return ActionResult.Success;
        }

        private const string CudaManifest = "msi-copied-cuda.txt";

        /// <summary>NVIDIA nodes only: copies the CUDA Toolkit / cuDNN runtime DLLs next to the CUDA
        /// backend, from wherever they're installed — a port of install-node.ps1's
        /// Install-VisionNativeDependencies. Never fails the install; without them the node uses DirectML.</summary>
        [CustomAction]
        public static ActionResult CopyCudaDeps(Session session)
        {
            try
            {
                var data = session.CustomActionData;
                var installDir = data["InstallDir"];
                var cudaDir = Path.Combine(installDir, @"onnxruntime-backends\cuda");
                if (!Directory.Exists(cudaDir) || !HasNvidiaGpu()) return ActionResult.Success;

                var required = new[] { "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll", "cufft64_11.dll", "cudnn64_9.dll" };
                var searchDirs = new List<string> { cudaDir, installDir, Environment.SystemDirectory };
                searchDirs.AddRange(PathEntries());
                var missing = required.Where(dll => !searchDirs.Any(d => SafeExists(d, dll))).ToList();
                if (missing.Count == 0) return ActionResult.Success;

                var sources = CudaSourceDirectories(data["UserLocalAppData"], data["UserAppData"]);
                var copied = new List<string>();
                foreach (var dll in missing)
                {
                    var source = sources.FirstOrDefault(d => SafeExists(d, dll));
                    if (source is null) { session.Log("CUDA dependency not found: " + dll); continue; }
                    var pattern = dll.StartsWith("cudnn", StringComparison.OrdinalIgnoreCase) ? "cudnn*.dll" : dll;
                    foreach (var file in Directory.EnumerateFiles(source, pattern))
                    {
                        var dest = Path.Combine(cudaDir, Path.GetFileName(file));
                        File.Copy(file, dest, overwrite: true);
                        copied.Add(Path.GetFileName(file));
                    }
                }
                if (copied.Count > 0)
                    File.WriteAllLines(Path.Combine(cudaDir, CudaManifest), copied.Distinct(StringComparer.OrdinalIgnoreCase));
                session.Log("Copied CUDA dependencies: " + string.Join(", ", copied));
            }
            catch (Exception ex)
            {
                session.Log("CUDA dependency setup skipped: " + ex.Message);
            }
            return ActionResult.Success;
        }

        /// <summary>Uninstall: removes the CUDA DLLs CopyCudaDeps added (they're not MSI-owned files).</summary>
        [CustomAction]
        public static ActionResult RemoveCudaDeps(Session session)
        {
            try
            {
                var cudaDir = Path.Combine(session.CustomActionData["InstallDir"], @"onnxruntime-backends\cuda");
                var manifest = Path.Combine(cudaDir, CudaManifest);
                if (!File.Exists(manifest)) return ActionResult.Success;
                foreach (var name in File.ReadAllLines(manifest).Where(n => n.Length > 0))
                    try { File.Delete(Path.Combine(cudaDir, Path.GetFileName(name))); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                File.Delete(manifest);
            }
            catch (Exception ex)
            {
                session.Log("Could not remove copied CUDA dependencies: " + ex.Message);
            }
            return ActionResult.Success;
        }

        /// <summary>Writes %ProgramData%\LarisVMS\client-endpoint.json (node) or proxy-endpoint.json
        /// (proxy) — the same keys install-node.ps1 / install-proxy.ps1 write. A blank PFX password
        /// keeps the one already in the file, since passwords aren't remembered between installs.</summary>
        [CustomAction]
        public static ActionResult WriteEndpointConfig(Session session)
        {
            var data = session.CustomActionData;
            var isNode = data["Product"] == "Node";
            var path = Path.Combine(Util.ProgramDataLaris, isNode ? "client-endpoint.json" : "proxy-endpoint.json");

            var password = data["PfxPassword"];
            if (string.IsNullOrEmpty(password) && File.Exists(path))
                password = Util.JsonReadString(File.ReadAllText(path), "pfxPassword") ?? "";

            var fields = new List<string>();
            if (isNode) fields.Add("\"enabled\": true");
            fields.Add("\"port\": " + int.Parse(data["Port"]));
            fields.Add("\"allowInsecure\": " + (data["AllowInsecure"] == "1" ? "true" : "false"));
            if (!string.IsNullOrEmpty(data["PfxPath"])) fields.Add("\"pfxPath\": " + Util.JsonString(data["PfxPath"]));
            if (!string.IsNullOrEmpty(password)) fields.Add("\"pfxPassword\": " + Util.JsonString(password));
            if (!string.IsNullOrEmpty(data["Host"])) fields.Add("\"host\": " + Util.JsonString(data["Host"]));

            Directory.CreateDirectory(Util.ProgramDataLaris);
            File.WriteAllText(path, "{\r\n  " + string.Join(",\r\n  ", fields) + "\r\n}\r\n", new UTF8Encoding(false));
            return ActionResult.Success;
        }

        /// <summary>Web: creates appsettings.Production.json from the shipped .example on a fresh
        /// install; on an existing one only the port (and the certificate, when given) are updated —
        /// everything else in that file belongs to the operator.</summary>
        [CustomAction]
        public static ActionResult SeedWebConfig(Session session)
        {
            var data = session.CustomActionData;
            var installDir = data["InstallDir"];
            var config = Path.Combine(installDir, "appsettings.Production.json");
            var example = Path.Combine(installDir, "appsettings.Production.json.example");
            var certPath = data["CertPath"];
            var certPassword = data["CertPassword"];

            string json;
            if (File.Exists(config))
            {
                json = File.ReadAllText(config);
                File.Copy(config, config + RollbackSuffix, overwrite: true); // for SeedWebConfigRollback
            }
            else
            {
                if (!File.Exists(example)) { session.Log("No appsettings.Production.json.example to seed from."); return ActionResult.Success; }
                json = File.ReadAllText(example);
                // No certificate given: blank the example's placeholders so the server goes straight to
                // its self-signed fallback instead of trying a share path that doesn't exist.
                if (string.IsNullOrEmpty(certPath)) json = ReplaceCertValue(json, "Path", "");
                if (string.IsNullOrEmpty(certPassword)) json = ReplaceCertValue(json, "Password", "");
                // Same for the example's "Server=." connection string: the setup wizard collects the real
                // one. Left in, a machine with no local SQL Server spent ~16 s timing out on every
                // request's setup check (and on startup) instead of showing the wizard.
                json = ReplaceJsonString(json, "DefaultConnection", "");
            }

            json = Regex.Replace(json, "(\"HttpsPort\"\\s*:\\s*)\\d+", m => m.Groups[1].Value + int.Parse(data["HttpsPort"]));
            if (!string.IsNullOrEmpty(certPath)) json = ReplaceCertValue(json, "Path", certPath);
            if (!string.IsNullOrEmpty(certPassword)) json = ReplaceCertValue(json, "Password", certPassword);

            File.WriteAllText(config, json, new UTF8Encoding(false));
            return ActionResult.Success;
        }

        private const string RollbackSuffix = ".rollback";

        /// <summary>Rollback for SeedWebConfig: the config is written outside the component table, so a
        /// failed install would otherwise leave it (and the certificate password in it) behind. Deletes
        /// a file SeedWebConfig created; restores one it edited.</summary>
        [CustomAction]
        public static ActionResult SeedWebConfigRollback(Session session)
        {
            var data = session.CustomActionData;
            var config = Path.Combine(data["InstallDir"], "appsettings.Production.json");
            try
            {
                if (data["ConfigExisted"] == "1")
                {
                    if (File.Exists(config + RollbackSuffix))
                    {
                        File.Copy(config + RollbackSuffix, config, overwrite: true);
                        File.Delete(config + RollbackSuffix);
                    }
                }
                else if (File.Exists(config))
                {
                    File.Delete(config);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                session.Log("Could not undo appsettings.Production.json: " + ex.Message);
            }
            return ActionResult.Success;
        }

        /// <summary>Commit for SeedWebConfig: drops the backup so no extra copy of the password stays.</summary>
        [CustomAction]
        public static ActionResult SeedWebConfigCommit(Session session)
        {
            var config = Path.Combine(session.CustomActionData["InstallDir"], "appsettings.Production.json");
            try { File.Delete(config + RollbackSuffix); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                session.Log("Could not delete " + config + RollbackSuffix + ": " + ex.Message);
            }
            return ActionResult.Success;
        }

        /// <summary>Treat a non-zero exit as a failure too, so SCM recovery restarts the service (same
        /// as the scripts' `sc failureflag ... 1`). util:ServiceConfig can't set this flag.</summary>
        [CustomAction]
        public static ActionResult SetFailureFlag(Session session)
        {
            var product = Product.Get(session.CustomActionData["Product"]);
            Util.Run("sc.exe", "failureflag " + product.ServiceName + " 1");
            return ActionResult.Success;
        }

        /// <summary>Remembers non-secret settings for the next upgrade.</summary>
        [CustomAction]
        public static ActionResult SaveSettings(Session session)
        {
            var data = session.CustomActionData;
            var product = Product.Get(data["Product"]);
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hklm.CreateSubKey(product.RegistryKey))
            {
                foreach (var prop in product.Remembered)
                {
                    var value = data.ContainsKey(prop) ? data[prop] : "";
                    if (string.IsNullOrEmpty(value)) key.DeleteValue(prop, throwOnMissingValue: false);
                    else key.SetValue(prop, value);
                }
            }
            return ActionResult.Success;
        }

        /// <summary>Full uninstall (not an upgrade): forget the remembered settings.</summary>
        [CustomAction]
        public static ActionResult ForgetSettings(Session session)
        {
            var product = Product.Get(session.CustomActionData["Product"]);
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                hklm.DeleteSubKeyTree(product.RegistryKey, throwOnMissingSubKey: false);
            return ActionResult.Success;
        }

        /// <summary>Replaces Path/Password inside the Kestrel certificate block only, so another key of the
        /// same name elsewhere in an operator's appsettings file is never touched.</summary>
        private static string ReplaceCertValue(string json, string name, string value)
        {
            var block = Util.CertBlock(json);
            if (block is null) return json;
            return json.Substring(0, block.Index) + ReplaceJsonString(block.Value, name, value) + json.Substring(block.Index + block.Length);
        }

        private static string ReplaceJsonString(string json, string name, string value)
        {
            var pattern = "(\"" + Regex.Escape(name) + "\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"";
            return new Regex(pattern).Replace(json, m => m.Groups[1].Value + Util.JsonString(value), 1);
        }

        private static void StopService(Session session, string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ServiceProcess.TimeoutException)
            {
                session.Log("Stopping " + name + ": " + ex.Message);
            }
        }

        private static void KillNodeChildren(Session session)
        {
            foreach (var name in new[] { "ffmpeg", "LarisVMS.Vision.Service" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(); p.WaitForExit(10000); }
                    catch (Exception ex) { session.Log("Could not stop " + name + " (" + p.Id + "): " + ex.Message); }
                    finally { p.Dispose(); }
                }
            }
            Thread.Sleep(500);
        }

        private static bool HasNvidiaGpu()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                    return searcher.Get().Cast<ManagementObject>()
                        .Any(o => (o["Name"] as string ?? "").IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return false; }
        }

        private static IEnumerable<string> PathEntries() =>
            ((Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "") + ";" +
             (Environment.GetEnvironmentVariable("Path") ?? ""))
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().Trim('"'));

        private static bool SafeExists(string dir, string file)
        {
            try { return File.Exists(Path.Combine(dir, file)); }
            catch (ArgumentException) { return false; }
        }

        /// <summary>Where CUDA Toolkit / cuDNN DLLs land, newest first; toolkit before pip copies (see
        /// install-node.ps1's Get-NativeDllSourceDirectories for why).</summary>
        private static List<string> CudaSourceDirectories(string userLocalAppData, string userAppData)
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var result = new List<string>();
            void AddGlob(string root, Func<string, IEnumerable<string>> expand)
            {
                try { if (Directory.Exists(root)) result.AddRange(expand(root).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            IEnumerable<string> Sub(string dir, string pattern) =>
                Directory.Exists(dir) ? Directory.EnumerateDirectories(dir, pattern) : Enumerable.Empty<string>();

            AddGlob(Path.Combine(programFiles, @"NVIDIA GPU Computing Toolkit\CUDA"),
                r => Sub(r, "v*").Select(v => Path.Combine(v, "bin")).Where(Directory.Exists));
            AddGlob(Path.Combine(programFiles, @"NVIDIA\CUDNN"),
                r => Sub(r, "v*").SelectMany(v => Sub(Path.Combine(v, "bin"), "*").Concat(new[] { Path.Combine(v, "bin") })).Where(Directory.Exists));
            foreach (var pythonRoot in new[] { programFiles, Path.Combine(userLocalAppData, @"Programs\Python") })
                AddGlob(pythonRoot, r => Sub(r, "Python*").SelectMany(py => Sub(Path.Combine(py, @"Lib\site-packages\nvidia"), "*"))
                    .Select(d => Path.Combine(d, "bin")).Where(Directory.Exists));
            AddGlob(Path.Combine(userAppData, "Python"),
                r => Sub(r, "Python*").SelectMany(py => Sub(Path.Combine(py, @"site-packages\nvidia"), "*"))
                    .Select(d => Path.Combine(d, "bin")).Where(Directory.Exists));
            return result;
        }
    }
}
