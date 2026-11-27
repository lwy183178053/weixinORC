using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Client.Services.WeComDb
{
    /// <summary>
    /// Discovers Windows WeCom (企业微信 / WXWork) local data directories (Documents\WXWork\&lt;AccountId&gt;\Data)
    /// and running WXWork.exe processes without touching or injecting into the process.
    /// </summary>
    public static partial class WeComPaths
    {
        [GeneratedRegex(@"^\d{10,}$", RegexOptions.Compiled)]
        private static partial Regex AccountDirRegex();

        public static string? GetWXWorkRoot()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<string>();

            // 1. Registry custom data path if configured
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Tencent\WXWork");
                string? customPath = key?.GetValue("DataLocationPath") as string
                    ?? key?.GetValue("FileSavePath") as string
                    ?? key?.GetValue("DataPath") as string;
                if (!string.IsNullOrWhiteSpace(customPath))
                {
                    candidates.Add(customPath.Trim());
                    candidates.Add(Path.Combine(customPath.Trim(), "WXWork"));
                }
            }
            catch { }

            // 2. Standard User Documents\WXWork
            string docs = Client.Services.WeChat4Db.WeChat4Paths.GetDocumentsDir();
            if (!string.IsNullOrWhiteSpace(docs))
            {
                candidates.Add(Path.Combine(docs, "WXWork"));
            }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WXWork"));

            // 3. Quick root scan on fixed drives
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                    candidates.Add(Path.Combine(d.RootDirectory.FullName, "WXWork"));
                }
                catch { }
            }

            foreach (var raw in candidates)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string norm;
                try { norm = Path.GetFullPath(raw.Trim()); }
                catch { continue; }
                if (!seen.Add(norm)) continue;
                if (Directory.Exists(norm) && HasAnyAccountDataDir(norm))
                {
                    return norm;
                }
            }

            foreach (var raw in candidates)
            {
                if (!string.IsNullOrWhiteSpace(raw) && Directory.Exists(raw))
                {
                    return Path.GetFullPath(raw);
                }
            }

            return null;
        }

        private static bool HasAnyAccountDataDir(string root)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (!AccountDirRegex().IsMatch(name)) continue;
                    string dataDir = Path.Combine(dir, "Data");
                    if (File.Exists(Path.Combine(dataDir, "session.db")) ||
                        File.Exists(Path.Combine(dataDir, "message.db")))
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static List<WeComAccountDir> ListAccountDirs(string? customRootOrAccountDir = null)
        {
            var result = new List<WeComAccountDir>();

            if (!string.IsNullOrWhiteSpace(customRootOrAccountDir) && Directory.Exists(customRootOrAccountDir))
            {
                string norm = Path.GetFullPath(customRootOrAccountDir.Trim());
                string directData = Path.GetFileName(norm).Equals("Data", StringComparison.OrdinalIgnoreCase)
                    ? norm
                    : Path.Combine(norm, "Data");
                if (Directory.Exists(directData) &&
                    (File.Exists(Path.Combine(directData, "session.db")) || File.Exists(Path.Combine(directData, "message.db"))))
                {
                    string accPath = Path.GetFileName(norm).Equals("Data", StringComparison.OrdinalIgnoreCase)
                        ? (Path.GetDirectoryName(norm) ?? norm)
                        : norm;
                    result.Add(BuildAccountDir(accPath, directData));
                    return result;
                }
                // Otherwise treat as WXWork root
                CollectUnderRoot(norm, result);
                result.Sort((a, b) => b.LastModified.CompareTo(a.LastModified));
                return result;
            }

            string? root = GetWXWorkRoot();
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                CollectUnderRoot(root, result);
            }

            result.Sort((a, b) => b.LastModified.CompareTo(a.LastModified));
            return result;
        }

        private static void CollectUnderRoot(string root, List<WeComAccountDir> result)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (!AccountDirRegex().IsMatch(name)) continue;
                    string dataDir = Path.Combine(dir, "Data");
                    if (!Directory.Exists(dataDir)) continue;
                    if (!File.Exists(Path.Combine(dataDir, "session.db")) &&
                        !File.Exists(Path.Combine(dataDir, "message.db")))
                    {
                        continue;
                    }
                    result.Add(BuildAccountDir(dir, dataDir));
                }
            }
            catch { }
        }

        private static WeComAccountDir BuildAccountDir(string accountPath, string dataPath)
        {
            DateTimeOffset latest = DateTimeOffset.MinValue;
            int dbCount = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dataPath, "*.db*"))
                {
                    string fn = Path.GetFileName(f).ToLowerInvariant();
                    if (fn.EndsWith(".db", StringComparison.Ordinal)) dbCount++;
                    if (!fn.StartsWith("message.db", StringComparison.Ordinal) &&
                        !fn.StartsWith("session.db", StringComparison.Ordinal) &&
                        !fn.StartsWith("user.db", StringComparison.Ordinal) &&
                        !fn.StartsWith("kv.db", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var t = new FileInfo(f).LastWriteTimeUtc;
                    if (t > latest) latest = t;
                }
            }
            catch { }

            if (latest == DateTimeOffset.MinValue)
            {
                try { latest = new DirectoryInfo(dataPath).LastWriteTimeUtc; } catch { }
            }

            return new WeComAccountDir
            {
                AccountId = Path.GetFileName(accountPath),
                AccountPath = accountPath,
                DataPath = dataPath,
                CachePath = Path.Combine(accountPath, "Cache"),
                LastModified = latest,
                DbCount = dbCount
            };
        }

        public static List<WeComProcessInfo> FindWXWorkProcesses()
        {
            var list = new List<WeComProcessInfo>();
            int brokerPid = ReadBrokerPidFromRegistry();
            try
            {
                foreach (var p in Process.GetProcessesByName("WXWork"))
                {
                    using (p)
                    {
                        try
                        {
                            long memKb = p.WorkingSet64 / 1024;
                            string exe = string.Empty;
                            try { exe = p.MainModule?.FileName ?? string.Empty; } catch { }
                            list.Add(new WeComProcessInfo
                            {
                                Pid = p.Id,
                                MemoryKb = memKb,
                                IsBroker = p.Id == brokerPid,
                                ExePath = exe
                            });
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // Non-broker account processes with highest memory first (most likely active logged-in window)
            list.Sort((a, b) =>
            {
                int cmp = a.IsBroker.CompareTo(b.IsBroker);
                if (cmp != 0) return cmp;
                return b.MemoryKb.CompareTo(a.MemoryKb);
            });
            return list;
        }

        public static int GetPrimaryWXWorkPid()
        {
            var procs = FindWXWorkProcesses();
            return procs.Count > 0 ? procs[0].Pid : 0;
        }

        private static int ReadBrokerPidFromRegistry()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Tencent\WXWork");
                object? val = key?.GetValue("latest_broker_processid");
                if (val is int i) return i;
                if (val is string s && int.TryParse(s, out int parsed)) return parsed;
            }
            catch { }
            return 0;
        }
    }
}
