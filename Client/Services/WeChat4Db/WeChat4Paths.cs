using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Client.Services.WeChat4Db
{
    /// <summary>
    /// Locates Windows WeChat 4.x data directories (xwechat_files/&lt;wxid&gt;_&lt;4hex&gt;/db_storage)
    /// and running Weixin.exe processes, ported from Moect wx4/paths.py.
    /// </summary>
    public static partial class WeChat4Paths
    {
        public const string XWeChatDirName = "xwechat_files";
        public const string DbStorageName = "db_storage";

        [GeneratedRegex(@"^(?<wxid>.+?)_(?<suffix>[0-9a-fA-F]{4})$", RegexOptions.Compiled)]
        private static partial Regex WxidSuffixRegex();

        public static string SplitWxid(string dirName)
        {
            var m = WxidSuffixRegex().Match(dirName);
            return m.Success ? m.Groups["wxid"].Value : dirName;
        }

        public static string GetDocumentsDir()
        {
            string? reg = ReadRegString(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                "Personal");
            if (!string.IsNullOrWhiteSpace(reg))
            {
                string expanded = Environment.ExpandEnvironmentVariables(reg);
                if (Directory.Exists(expanded)) return expanded;
            }

            reg = ReadRegString(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders",
                "Personal");
            if (!string.IsNullOrWhiteSpace(reg) && Directory.Exists(reg))
            {
                return reg;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        public static string? GetXWeChatRoot()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bases = new List<string>();
            bases.AddRange(RootsFromConfig());
            bases.AddRange(RootsFromRegistry());
            bases.Add(GetDocumentsDir());

            foreach (var raw in bases)
            {
                string norm = NormalizePath(raw);
                if (string.IsNullOrEmpty(norm) || !seen.Add(norm)) continue;

                if (Path.GetFileName(norm).Equals(XWeChatDirName, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(norm))
                {
                    return norm;
                }
                string sub = Path.Combine(norm, XWeChatDirName);
                if (Directory.Exists(sub)) return sub;
            }

            foreach (var raw in QuickScanDrives())
            {
                string norm = NormalizePath(raw);
                if (string.IsNullOrEmpty(norm) || !seen.Add(norm)) continue;
                string sub = Path.Combine(norm, XWeChatDirName);
                if (Directory.Exists(sub)) return sub;
            }

            return null;
        }

        public static List<WeChat4AccountDir> ListAccountDirs(string? customRootOrDir = null)
        {
            if (!string.IsNullOrWhiteSpace(customRootOrDir))
            {
                return ResolveSelectedDirs(customRootOrDir);
            }

            string? root = GetXWeChatRoot();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return [];
            }

            var result = new List<WeChat4AccountDir>();
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (name.Equals("all_users", StringComparison.OrdinalIgnoreCase)) continue;
                    string storage = Path.Combine(dir, DbStorageName);
                    if (!Directory.Exists(storage)) continue;

                    result.Add(BuildAccountDir(dir, storage));
                }
            }
            catch
            {
                // Ignore inaccessible dirs
            }

            result.Sort((a, b) => b.LastModified.CompareTo(a.LastModified));
            return result;
        }

        public static List<WeChat4AccountDir> ResolveSelectedDirs(string directory)
        {
            string selected = Path.GetFullPath(directory.Trim());
            if (!Directory.Exists(selected))
            {
                throw new DirectoryNotFoundException("所选目录不存在。请在微信「设置 → 文件管理」查看数据位置。");
            }

            var candidates = new List<string> { selected };
            string name = Path.GetFileName(selected);
            string? parent = Path.GetDirectoryName(selected);

            if ((name.Equals("session", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("message", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("contact", StringComparison.OrdinalIgnoreCase)) &&
                parent is not null &&
                Path.GetFileName(parent).Equals(DbStorageName, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(parent);
            }
            if (name.Equals(DbStorageName, StringComparison.OrdinalIgnoreCase) && parent is not null)
            {
                candidates.Add(parent);
            }
            string xw = Path.Combine(selected, XWeChatDirName);
            if (Directory.Exists(xw))
            {
                candidates.Add(xw);
            }

            foreach (var root in candidates.ToList())
            {
                if (Path.GetFileName(root).Equals(DbStorageName, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    foreach (var sub in Directory.GetDirectories(root))
                    {
                        if (Directory.Exists(Path.Combine(sub, DbStorageName)))
                        {
                            candidates.Add(sub);
                        }
                    }
                }
                catch { }
            }

            var found = new Dictionary<string, WeChat4AccountDir>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in candidates)
            {
                string storage = Path.GetFileName(root).Equals(DbStorageName, StringComparison.OrdinalIgnoreCase)
                    ? root
                    : Path.Combine(root, DbStorageName);
                if (!Directory.Exists(storage)) continue;
                string accountPath = Path.GetDirectoryName(storage) ?? storage;
                found[storage] = BuildAccountDir(accountPath, storage);
            }

            if (found.Count > 0)
            {
                return found.Values.OrderByDescending(a => a.LastModified).ToList();
            }

            if (File.Exists(Path.Combine(selected, "Weixin.exe")) || File.Exists(Path.Combine(selected, "WeChat.exe")))
            {
                throw new InvalidOperationException("这是微信程序安装目录。请在微信「设置 → 文件管理」打开数据目录，再选择 xwechat_files 或账号文件夹。");
            }
            if (Directory.Exists(Path.Combine(selected, "Msg")) || File.Exists(Path.Combine(selected, "MicroMsg.db")))
            {
                throw new InvalidOperationException("检测到微信 3.x 旧版数据结构。当前一键直读面向 Windows 微信 4.x（xwechat_files）。");
            }
            throw new InvalidOperationException("未在所选目录找到 db_storage。请选择 xwechat_files、账号目录或 db_storage 文件夹。");
        }

        public static List<WeChat4ProcessInfo> FindWeixinProcesses()
        {
            var list = new List<WeChat4ProcessInfo>();
            foreach (var p in Process.GetProcessesByName("Weixin"))
            {
                try
                {
                    long memKb = 0;
                    try { memKb = p.WorkingSet64 / 1024; } catch { }
                    list.Add(new WeChat4ProcessInfo
                    {
                        Pid = p.Id,
                        Name = p.ProcessName,
                        MemoryKb = memKb
                    });
                }
                catch
                {
                    // Process exited
                }
                finally
                {
                    p.Dispose();
                }
            }
            list.Sort((a, b) => b.MemoryKb.CompareTo(a.MemoryKb));
            return list;
        }

        private static WeChat4AccountDir BuildAccountDir(string accountPath, string storagePath)
        {
            string dirName = Path.GetFileName(accountPath);
            DateTimeOffset latest = DateTimeOffset.MinValue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(storagePath, "*.db*", SearchOption.AllDirectories))
                {
                    var t = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                    if (t > latest) latest = t;
                }
            }
            catch { }

            return new WeChat4AccountDir
            {
                DirName = dirName,
                Wxid = SplitWxid(dirName),
                AccountPath = accountPath,
                DbStoragePath = storagePath,
                LastModified = latest
            };
        }

        private static IEnumerable<string> RootsFromConfig()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string cfgDir = Path.Combine(appData, "Tencent", "xwechat", "config");
            if (!Directory.Exists(cfgDir)) yield break;

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            foreach (var ini in Directory.GetFiles(cfgDir, "*.ini"))
            {
                string? raw = null;
                foreach (var enc in new[] { Encoding.UTF8, Encoding.GetEncoding("gbk") })
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(ini);
                        int len = Math.Min(bytes.Length, 1024);
                        raw = enc.GetString(bytes, 0, len).Trim('\0', ' ', '\t', '\r', '\n');
                        if (!string.IsNullOrWhiteSpace(raw)) break;
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(raw)) continue;
                string trimmed = raw.TrimEnd('\\', '/');
                if (trimmed.Equals("MyDocument:", StringComparison.OrdinalIgnoreCase))
                {
                    yield return GetDocumentsDir();
                }
                else if (Directory.Exists(trimmed))
                {
                    yield return trimmed;
                }
            }
        }

        private static IEnumerable<string> RootsFromRegistry()
        {
            var keys = new (string Path, string Value)[]
            {
                (@"Software\Tencent\Weixin", "FileSavePath"),
                (@"Software\Tencent\WeChat", "FileSavePath")
            };
            foreach (var (path, value) in keys)
            {
                string? v = ReadRegString(path, value);
                if (string.IsNullOrWhiteSpace(v)) continue;
                v = Environment.ExpandEnvironmentVariables(v).TrimEnd('\\', '/');
                if (v.Equals("MyDocument:", StringComparison.OrdinalIgnoreCase))
                {
                    yield return GetDocumentsDir();
                }
                else if (Directory.Exists(v))
                {
                    yield return v;
                }
            }
        }

        private static IEnumerable<string> QuickScanDrives()
        {
            var found = new List<string>();
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                string root = drive.RootDirectory.FullName;
                candidates.Add(root);
                foreach (var sub in new[] { "Users", "Documents", "WeChat", "Tencent", "ProgramData" })
                {
                    string p = Path.Combine(root, sub);
                    if (Directory.Exists(p)) candidates.Add(p);
                }
            }

            foreach (var baseDir in candidates)
            {
                try
                {
                    foreach (var entry in Directory.GetDirectories(baseDir))
                    {
                        if (Path.GetFileName(entry).Equals(XWeChatDirName, StringComparison.OrdinalIgnoreCase))
                        {
                            found.Add(baseDir);
                            continue;
                        }
                        if (Directory.Exists(Path.Combine(entry, XWeChatDirName)))
                        {
                            found.Add(entry);
                        }
                    }
                }
                catch { }
            }

            return found;
        }

        private static string? ReadRegString(string subKey, string valueName)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(subKey);
                return k?.GetValue(valueName) as string;
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path.Trim()).TrimEnd('\\', '/');
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
