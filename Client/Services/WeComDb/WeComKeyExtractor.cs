using System.Runtime.InteropServices;
using System.Text.Json;

namespace Client.Services.WeComDb
{
    /// <summary>
    /// Extracts 16-byte wxSQLite3 AES-128-CBC raw keys from running 32-bit WXWork.exe processes
    /// using purely read-only ReadProcessMemory scanning (zero DLL injection, zero hooks).
    /// Caches verified keys locally and re-validates via 128-bit double check before reuse.
    /// </summary>
    public sealed class WeComKeyExtractor
    {
        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessVmRead = 0x0010;
        private const uint MemCommit = 0x1000;
        private const uint MemPrivate = 0x20000;
        private const uint PageReadWrite = 0x04;
        private const ulong MaxX86UserAddress = 0x7FFF_0000UL;

        public bool EncounteredAccessDenied { get; private set; }

        public static string DefaultCachePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "weixinORC",
                "wecom_keys_cache.json");

        public static byte[]? ReadAccountSamplePage1Header(WeComAccountDir account)
        {
            string[] candidates = ["session.db", "message.db", "user.db", "company.db"];
            foreach (string name in candidates)
            {
                string p = Path.Combine(account.DataPath, name);
                if (!File.Exists(p)) continue;
                try
                {
                    using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length < WeComCrypto.DefaultPageSize) continue;
                    byte[] hdr = new byte[32];
                    if (fs.Read(hdr, 0, 32) == 32 && WeComCrypto.LooksLikeWxAes128Page1(hdr))
                    {
                        return hdr;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Resolves verified 16-byte raw key for the target WeCom account (cache first, then read-only process memory scan).
        /// </summary>
        public byte[]? ExtractForAccount(WeComAccountDir account, bool useCache = true, CancellationToken ct = default)
        {
            var map = ExtractForAccounts(new[] { account }, useCache, ct);
            return map.TryGetValue(account.AccountId, out var key) ? key : null;
        }

        /// <summary>
        /// Resolves verified 16-byte raw keys for all provided WeCom accounts in a single pass across running WXWork processes.
        /// </summary>
        public Dictionary<string, byte[]> ExtractForAccounts(
            IReadOnlyList<WeComAccountDir> accounts,
            bool useCache = true,
            CancellationToken ct = default)
        {
            var targets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var acc in accounts)
            {
                byte[]? hdr = ReadAccountSamplePage1Header(acc);
                if (hdr is not null)
                {
                    targets[acc.AccountId] = hdr;
                }
            }

            var resolved = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (targets.Count == 0) return resolved;

            // 1. Try cached keys verified via 128-bit double check
            var cache = useCache ? LoadCache() : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (useCache && cache.Count > 0)
            {
                foreach (var (accId, hdr) in targets)
                {
                    if (cache.TryGetValue(accId, out string? hex) && TryDecodeKeyHex(hex, out var keyBytes))
                    {
                        if (WeComCrypto.VerifyKeyAgainstPage1(keyBytes, hdr))
                        {
                            resolved[accId] = keyBytes;
                        }
                    }
                }
            }

            if (resolved.Count == targets.Count)
            {
                return resolved;
            }

            // 2. Read-only scan running WXWork.exe processes
            var procs = WeComPaths.FindWXWorkProcesses();
            foreach (var proc in procs)
            {
                ct.ThrowIfCancellationRequested();
                if (resolved.Count == targets.Count) break;

                ScanProcessForKeys(proc.Pid, targets, resolved, ct);
            }

            if (resolved.Count > 0)
            {
                foreach (var (accId, keyBytes) in resolved)
                {
                    cache[accId] = Convert.ToHexString(keyBytes).ToLowerInvariant();
                }
                SaveCache(cache);
            }

            return resolved;
        }

        private void ScanProcessForKeys(
            int pid,
            Dictionary<string, byte[]> targets,
            Dictionary<string, byte[]> resolved,
            CancellationToken ct)
        {
            nint hProc = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, (uint)pid);
            if (hProc == nint.Zero)
            {
                if (Marshal.GetLastWin32Error() == 5) EncounteredAccessDenied = true;
                return;
            }

            var candidates = new HashSet<string>(StringComparer.Ordinal);
            var candidateBytes = new List<byte[]>();
            int[] deltas = [16, 20, 24, 17, 32];

            try
            {
                ulong addr = 0x10000;
                int mbiSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
                byte[] buffer = [];

                while (addr < MaxX86UserAddress)
                {
                    ct.ThrowIfCancellationRequested();
                    if (VirtualQueryEx(hProc, (nint)addr, out var mbi, (nuint)mbiSize) == 0) break;

                    ulong regionBase = (ulong)mbi.BaseAddress;
                    ulong regionSize = mbi.RegionSize;
                    ulong next = regionBase + regionSize;
                    if (next <= addr) break;
                    addr = next;

                    if (mbi.State != MemCommit || (mbi.Protect & 0xFF) != PageReadWrite || mbi.Type != MemPrivate)
                    {
                        continue;
                    }
                    if (regionSize == 0 || regionSize > 32UL * 1024 * 1024) continue;

                    int toRead = (int)regionSize;
                    if (buffer.Length < toRead) buffer = new byte[toRead];
                    if (!ReadProcessMemory(hProc, (nint)regionBase, buffer, (nuint)toRead, out nuint bytesRead))
                    {
                        continue;
                    }

                    int n = (int)bytesRead;
                    if (n < 48) continue;

                    ReadOnlySpan<byte> span = buffer.AsSpan(0, n);
                    int limit = n - 48;
                    for (int off = 0; off <= limit; off += 4)
                    {
                        ReadOnlySpan<byte> b0 = span.Slice(off, 16);
                        // Fast skip trivial zero/repeating blocks
                        if (b0[..4].SequenceEqual(b0.Slice(4, 4)) || b0.Slice(8, 4).SequenceEqual(b0.Slice(12, 4)))
                        {
                            continue;
                        }

                        bool matchedPair = false;
                        for (int d = 0; d < deltas.Length; d++)
                        {
                            int delta = deltas[d];
                            if (off + delta + 16 <= n && span.Slice(off + delta, 16).SequenceEqual(b0))
                            {
                                matchedPair = true;
                                break;
                            }
                        }

                        if (!matchedPair) continue;

                        string hex = Convert.ToHexString(b0);
                        if (candidates.Add(hex))
                        {
                            candidateBytes.Add(b0.ToArray());
                        }
                    }
                }
            }
            finally
            {
                CloseHandle(hProc);
            }

            foreach (var cand in candidateBytes)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var (accId, hdr) in targets)
                {
                    if (resolved.ContainsKey(accId)) continue;
                    if (WeComCrypto.VerifyKeyAgainstPage1(cand, hdr))
                    {
                        resolved[accId] = cand;
                    }
                }
                if (resolved.Count == targets.Count) break;
            }
        }

        private static bool TryDecodeKeyHex(string? hex, out byte[] bytes)
        {
            bytes = [];
            if (string.IsNullOrWhiteSpace(hex) || hex.Trim().Length != 32) return false;
            try
            {
                bytes = Convert.FromHexString(hex.Trim());
                return bytes.Length == WeComCrypto.KeySize;
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, string> LoadCache()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(DefaultCachePath)) return dict;
                using var doc = JsonDocument.Parse(File.ReadAllText(DefaultCachePath));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return dict;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        dict[prop.Name] = prop.Value.GetString() ?? string.Empty;
                    }
                }
            }
            catch { }
            return dict;
        }

        private static void SaveCache(Dictionary<string, string> dict)
        {
            try
            {
                string? dir = Path.GetDirectoryName(DefaultCachePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    foreach (var kv in dict)
                    {
                        w.WriteString(kv.Key, kv.Value);
                    }
                    w.WriteEndObject();
                }
                string tmp = DefaultCachePath + ".tmp";
                File.WriteAllBytes(tmp, ms.ToArray());
                File.Move(tmp, DefaultCachePath, overwrite: true);
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public nint BaseAddress;
            public nint AllocationBase;
            public uint AllocationProtect;
            public ushort PartitionId;
            public nuint RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nuint VirtualQueryEx(nint hProcess, nint lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, nuint dwLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, [Out] byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(nint hObject);
    }
}
