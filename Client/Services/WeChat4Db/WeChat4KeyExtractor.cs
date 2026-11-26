using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Client.Services.WeChat4Db
{
    /// <summary>
    /// Extracts SQLCipher 4 database encryption keys from a running Windows WeChat 4.x process
    /// using read-only ReadProcessMemory scanning (no DLL injection, no hooks).
    /// Ported from Moect wx4/key.py (supports WeChat 4.1+/4.2+ Config.Cipher XOR blob,
    /// 4.0.x raw hex literal fallback, and PBKDF2 passphrase derivation).
    /// </summary>
    public sealed partial class WeChat4KeyExtractor
    {
        private static readonly byte[] ConfigCipherName = "com.Tencent.WCDB.Config.Cipher"u8.ToArray();
        private static readonly byte[] ConfigXorMask = Convert.FromHexString(
            "d2c7442458020000004889442450488b" +
            "450048844c2448488944254048584c24");

        private const ulong MaxUserAddress = 0x0000_8000_0000_0000UL;
        private const int ConfigBlobMax = 1024;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessVmRead = 0x0010;
        private const uint MemCommit = 0x1000;

        private static readonly HashSet<uint> ReadableProtects = [0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80];

        [GeneratedRegex(@"[xX]'([0-9a-fA-F]{64,192})'", RegexOptions.Compiled)]
        private static partial Regex HexLiteralRegex();

        private sealed class DbTarget
        {
            public required string RelPath { get; init; }
            public required string FullPath { get; init; }
            public required string SaltHex { get; init; }
            public required byte[] Page1 { get; init; }
        }

        private sealed class CachedKeyStore
        {
            public string DbStorage { get; set; } = string.Empty;
            public Dictionary<string, CachedDbKey> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class CachedDbKey
        {
            public string EncKey { get; set; } = string.Empty;
            public string Salt { get; set; } = string.Empty;
        }

        private readonly string _dbStorage;
        private readonly Action<string>? _progress;
        private readonly List<DbTarget> _targets = [];
        private readonly Dictionary<string, List<DbTarget>> _saltIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _resolvedBySalt = new(StringComparer.OrdinalIgnoreCase);
        private int _passphraseAttempts;

        public bool EncounteredAccessDenied { get; private set; }
        public int TotalSalts => _saltIndex.Count;
        public int ResolvedSalts => _resolvedBySalt.Count;

        public WeChat4KeyExtractor(string dbStorage, Action<string>? progress = null)
        {
            _dbStorage = Path.GetFullPath(dbStorage);
            _progress = progress;
        }

        public static string DefaultCachePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "weixinORC",
                "wx4_keys_cache.json");

        /// <summary>
        /// Collects core encrypted database files needed for contacts, groups, avatars, and messages.
        /// </summary>
        public void CollectTargets(bool coreOnly = true, CancellationToken ct = default)
        {
            _targets.Clear();
            _saltIndex.Clear();

            foreach (var file in Directory.EnumerateFiles(_dbStorage, "*.db", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(_dbStorage, file);
                if (coreOnly && !WeChat4Decryptor.IsCoreSyncDatabase(rel))
                {
                    continue;
                }

                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length < WeChat4Crypto.PageSize) continue;

                    byte[] page1 = new byte[WeChat4Crypto.PageSize];
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        int read = fs.Read(page1, 0, page1.Length);
                        if (read < WeChat4Crypto.PageSize) continue;
                    }

                    if (WeChat4Crypto.IsPlaintextSqlite(page1)) continue;

                    string saltHex = Convert.ToHexString(page1.AsSpan(0, WeChat4Crypto.SaltSize)).ToLowerInvariant();
                    var target = new DbTarget
                    {
                        RelPath = rel,
                        FullPath = file,
                        SaltHex = saltHex,
                        Page1 = page1
                    };
                    _targets.Add(target);
                    if (!_saltIndex.TryGetValue(saltHex, out var bucket))
                    {
                        bucket = [];
                        _saltIndex[saltHex] = bucket;
                    }
                    bucket.Add(target);
                }
                catch
                {
                    // Skip unreadable file
                }
            }

            _progress?.Invoke($"发现 {_targets.Count} 个目标加密库（{_saltIndex.Count} 个独立 salt）");
        }

        /// <summary>
        /// Extracts verified database keys mapping RelativeDbPath -> 64-char hex enc_key.
        /// </summary>
        public Dictionary<string, string> Extract(
            string? manualPassphraseHex = null,
            bool useCache = true,
            CancellationToken ct = default)
        {
            CollectTargets(coreOnly: true, ct: ct);
            if (_targets.Count == 0)
            {
                return [];
            }

            // 1. Optional manual 64-hex key / passphrase
            if (!string.IsNullOrWhiteSpace(manualPassphraseHex))
            {
                string clean = manualPassphraseHex.Trim();
                if (clean.Length == 64 && IsHexString(clean))
                {
                    _progress?.Invoke("正在校验手工输入的 64 位密钥 / Passphrase...");
                    TryKey(clean.ToLowerInvariant(), null, allowPassphrase: true, ct);
                }
            }

            // 2. Reuse cached keys verified via HMAC-SHA512
            if (useCache && _resolvedBySalt.Count < _saltIndex.Count)
            {
                TryLoadCachedKeys(ct);
                if (_resolvedBySalt.Count > 0)
                {
                    _progress?.Invoke($"本地缓存命中 {_resolvedBySalt.Count}/{_saltIndex.Count} 个库密钥");
                }
            }

            if (_resolvedBySalt.Count == _saltIndex.Count)
            {
                SaveCachedKeys();
                return BuildRelPathKeyMap();
            }

            // 3. Scan running Weixin.exe processes
            EnableDebugPrivilege();
            var procs = WeChat4Paths.FindWeixinProcesses();
            if (procs.Count == 0)
            {
                throw new InvalidOperationException("未检测到运行中的微信 4.x 进程 (Weixin.exe)，请先启动并登录电脑微信。");
            }

            _progress?.Invoke($"检测到 {procs.Count} 个 Weixin.exe 进程，开始只读扫描密钥...");

            foreach (var proc in procs)
            {
                ct.ThrowIfCancellationRequested();
                if (_resolvedBySalt.Count == _saltIndex.Count) break;
                _progress?.Invoke($"扫描进程 PID={proc.Pid} ({proc.MemoryKb / 1024} MB) [Config.Cipher 主路径]...");
                int hit = ScanProcessConfigCipher(proc.Pid, allowPassphrase: false, ct);
                if (hit > 0)
                {
                    _progress?.Invoke($"  命中 {hit} 个密钥（累计 {_resolvedBySalt.Count}/{_saltIndex.Count}）");
                }
            }

            // 4. Fallback for WeChat 4.0.x plaintext raw keys
            if (_resolvedBySalt.Count < _saltIndex.Count)
            {
                _progress?.Invoke("尝试 4.0.x 兼容路径（扫描内存明文密钥）...");
                foreach (var proc in procs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_resolvedBySalt.Count == _saltIndex.Count) break;
                    int hit = ScanProcessLegacy(proc.Pid, ct);
                    if (hit > 0)
                    {
                        _progress?.Invoke($"  兼容路径命中 {hit} 个密钥（累计 {_resolvedBySalt.Count}/{_saltIndex.Count}）");
                    }
                }
            }

            // 5. Passphrase PBKDF2 fallback when nothing matched
            if (_resolvedBySalt.Count == 0)
            {
                _progress?.Invoke("尝试 Passphrase 派生路径（PBKDF2-SHA512）...");
                foreach (var proc in procs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_resolvedBySalt.Count > 0) break;
                    int hit = ScanProcessConfigCipher(proc.Pid, allowPassphrase: true, ct);
                    if (hit > 0)
                    {
                        _progress?.Invoke($"  Passphrase 派生命中 {hit} 个密钥");
                    }
                }
            }

            if (_resolvedBySalt.Count > 0)
            {
                SaveCachedKeys();
            }

            return BuildRelPathKeyMap();
        }

        private Dictionary<string, string> BuildRelPathKeyMap()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _targets)
            {
                if (_resolvedBySalt.TryGetValue(t.SaltHex, out var encKey))
                {
                    result[t.RelPath] = encKey;
                }
            }
            return result;
        }

        private int ScanProcessConfigCipher(int pid, bool allowPassphrase, CancellationToken ct)
        {
            nint hProc = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, (uint)pid);
            if (hProc == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 5) EncounteredAccessDenied = true;
                _progress?.Invoke($"  OpenProcess(PID={pid}) 失败（Win32 错误码 {err}，如为 5 请使用管理员身份运行）");
                return 0;
            }

            int totalHit = 0;
            try
            {
                var regions = EnumerateReadableRegions(hProc);
                var needleAddrs = new HashSet<ulong>();

                foreach (var (chunkBase, data) in ReadChunks(hProc, regions, 2 * 1024 * 1024, ConfigCipherName.Length - 1, ct))
                {
                    ReadOnlySpan<byte> span = data;
                    int offset = 0;
                    while (offset <= span.Length - ConfigCipherName.Length)
                    {
                        int idx = span[offset..].IndexOf(ConfigCipherName);
                        if (idx < 0) break;
                        needleAddrs.Add(chunkBase + (ulong)(offset + idx));
                        offset += idx + 1;
                    }
                }

                if (needleAddrs.Count == 0) return 0;

                var patterns = new List<byte[]>(needleAddrs.Count);
                foreach (var addr in needleAddrs)
                {
                    byte[] pair = new byte[16];
                    BinaryPrimitives.WriteUInt64LittleEndian(pair.AsSpan(0, 8), addr);
                    BinaryPrimitives.WriteUInt64LittleEndian(pair.AsSpan(8, 8), (ulong)ConfigCipherName.Length);
                    patterns.Add(pair);
                }

                foreach (var (chunkBase, data) in ReadChunks(hProc, regions, 2 * 1024 * 1024, 0x80, ct))
                {
                    if (_resolvedBySalt.Count == _saltIndex.Count) break;
                    if (allowPassphrase && (_resolvedBySalt.Count > 0 || _passphraseAttempts >= 64)) break;

                    ReadOnlySpan<byte> span = data;
                    foreach (var pat in patterns)
                    {
                        int offset = 0;
                        while (offset <= span.Length - pat.Length)
                        {
                            int idx = span[offset..].IndexOf(pat);
                            if (idx < 0) break;
                            int pos = offset + idx;
                            offset = pos + 1;

                            if (_resolvedBySalt.Count == _saltIndex.Count) break;
                            if (allowPassphrase && (_resolvedBySalt.Count > 0 || _passphraseAttempts >= 64)) break;

                            ulong refAddr = chunkBase + (ulong)pos;
                            if (refAddr < 0x10) continue;

                            byte[]? node = ReadMemory(hProc, refAddr - 0x10, 0x50);
                            if (node is null || node.Length < 0x40) continue;

                            ulong pStr = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(0x10, 8));
                            ulong pLen = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(0x18, 8));
                            if (!needleAddrs.Contains(pStr) || pLen != (ulong)ConfigCipherName.Length) continue;

                            ulong cfg = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(0x28, 8));
                            if (cfg < 0x10000 || cfg >= MaxUserAddress) continue;

                            byte[]? obj = ReadMemory(hProc, cfg + 0x88, 0x28);
                            if (obj is null || obj.Length < 0x18) continue;

                            ulong dptr = BinaryPrimitives.ReadUInt64LittleEndian(obj.AsSpan(0x08, 8));
                            ulong dlen = BinaryPrimitives.ReadUInt64LittleEndian(obj.AsSpan(0x10, 8));
                            if (dlen == 0 || dlen > ConfigBlobMax || dptr < 0x10000 || dptr >= MaxUserAddress) continue;

                            byte[]? blob = ReadMemory(hProc, dptr, (int)dlen);
                            if (blob is null || blob.Length != (int)dlen) continue;

                            foreach (var (keyHex, saltHint) in ExtractCandidatesFromConfigBlob(blob))
                            {
                                totalHit += TryKey(keyHex, saltHint, allowPassphrase, ct);
                            }
                        }
                    }
                }
            }
            finally
            {
                CloseHandle(hProc);
            }

            return totalHit;
        }

        private int ScanProcessLegacy(int pid, CancellationToken ct)
        {
            nint hProc = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, (uint)pid);
            if (hProc == 0)
            {
                if (Marshal.GetLastWin32Error() == 5) EncounteredAccessDenied = true;
                return 0;
            }

            int hit = 0;
            try
            {
                var regions = EnumerateReadableRegions(hProc);
                foreach (var (_, data) in ReadChunks(hProc, regions, 2 * 1024 * 1024, 200, ct))
                {
                    if (_resolvedBySalt.Count == _saltIndex.Count) break;
                    // Quick ASCII pre-check for "'" before regex allocation
                    if (data.AsSpan().IndexOf((byte)'\'') < 0) continue;

                    string ascii = Encoding.Latin1.GetString(data);
                    foreach (Match m in HexLiteralRegex().Matches(ascii))
                    {
                        string run = m.Groups[1].Value.ToLowerInvariant();
                        if (run.Length < 96) continue;
                        hit += TryKey(run[..64], run[64..96], allowPassphrase: false, ct);
                    }
                }
            }
            finally
            {
                CloseHandle(hProc);
            }
            return hit;
        }

        private static IEnumerable<(string KeyHex, string? SaltHint)> ExtractCandidatesFromConfigBlob(byte[] blob)
        {
            if (blob.Length == 0 || blob.Length > ConfigBlobMax) yield break;

            byte[] decoded = new byte[blob.Length];
            for (int i = 0; i < blob.Length; i++)
            {
                decoded[i] = (byte)(blob[i] ^ ConfigXorMask[i % ConfigXorMask.Length]);
            }

            string text = Encoding.Latin1.GetString(decoded);
            foreach (Match m in HexLiteralRegex().Matches(text))
            {
                string run = m.Groups[1].Value.ToLowerInvariant();
                foreach (var cand in CandidatesFromLiteralRun(run))
                {
                    yield return cand;
                }
            }
        }

        private static IEnumerable<(string KeyHex, string? SaltHint)> CandidatesFromLiteralRun(string run)
        {
            var starts = new HashSet<int> { 0 };
            if (run.Length > 96)
            {
                for (int st = 0; st <= run.Length - 64; st += 32) starts.Add(st);
                starts.Add(run.Length - 64);
            }

            foreach (int st in starts)
            {
                if (st < 0 || st + 64 > run.Length) continue;
                string keyHex = run.Substring(st, 64);
                byte[] keyBytes;
                try { keyBytes = Convert.FromHexString(keyHex); }
                catch { continue; }
                if (!LooksLikeKey(keyBytes)) continue;

                string? salt = st + 96 <= run.Length ? run.Substring(st + 64, 32) : null;
                yield return (keyHex, salt);
            }
        }

        private int TryKey(string keyHex, string? saltHint, bool allowPassphrase, CancellationToken ct)
        {
            byte[] keyBytes;
            try { keyBytes = Convert.FromHexString(keyHex); }
            catch { return 0; }
            if (!LooksLikeKey(keyBytes)) return 0;

            IEnumerable<string> targetSalts = !string.IsNullOrEmpty(saltHint) &&
                                              _saltIndex.ContainsKey(saltHint) &&
                                              !_resolvedBySalt.ContainsKey(saltHint)
                ? [saltHint]
                : _saltIndex.Keys.Where(s => !_resolvedBySalt.ContainsKey(s)).ToList();

            int hit = 0;
            foreach (var salt in targetSalts)
            {
                ct.ThrowIfCancellationRequested();
                if (_resolvedBySalt.ContainsKey(salt)) continue;

                byte[] page1 = _saltIndex[salt][0].Page1;
                if (WeChat4Crypto.VerifyEncKey(keyBytes, page1))
                {
                    _resolvedBySalt[salt] = keyHex;
                    hit++;
                    continue;
                }

                if (allowPassphrase && _passphraseAttempts < 64)
                {
                    _passphraseAttempts++;
                    byte[] derived = WeChat4Crypto.DeriveEncKey(keyBytes, Convert.FromHexString(salt));
                    if (WeChat4Crypto.VerifyEncKey(derived, page1))
                    {
                        _resolvedBySalt[salt] = Convert.ToHexString(derived).ToLowerInvariant();
                        hit++;
                    }
                }
            }

            return hit;
        }

        private void TryLoadCachedKeys(CancellationToken ct)
        {
            string cachePath = DefaultCachePath;
            if (!File.Exists(cachePath)) return;
            try
            {
                string json = File.ReadAllText(cachePath, Encoding.UTF8);
                var cache = JsonSerializer.Deserialize<CachedKeyStore>(json);
                if (cache is null || string.IsNullOrWhiteSpace(cache.DbStorage)) return;
                if (!Path.GetFullPath(cache.DbStorage).Equals(_dbStorage, StringComparison.OrdinalIgnoreCase)) return;

                foreach (var entry in cache.Entries.Values)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(entry.EncKey) && entry.EncKey.Length == 64 && IsHexString(entry.EncKey))
                    {
                        TryKey(entry.EncKey.ToLowerInvariant(), entry.Salt, allowPassphrase: false, ct);
                    }
                }
            }
            catch
            {
                // Ignore corrupted cache
            }
        }

        private void SaveCachedKeys()
        {
            try
            {
                string cachePath = DefaultCachePath;
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                var payload = new CachedKeyStore { DbStorage = _dbStorage };
                foreach (var t in _targets)
                {
                    if (_resolvedBySalt.TryGetValue(t.SaltHex, out var key))
                    {
                        payload.Entries[t.RelPath] = new CachedDbKey { EncKey = key, Salt = t.SaltHex };
                    }
                }
                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(cachePath, json, Encoding.UTF8);
            }
            catch
            {
                // Non-fatal
            }
        }

        private static bool LooksLikeKey(byte[] key)
        {
            if (key.Length != WeChat4Crypto.KeySize) return false;
            var distinct = new HashSet<byte>(key);
            return distinct.Count >= 15;
        }

        private static bool IsHexString(string s)
        {
            foreach (char c in s)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }

        // ---- Win32 P/Invoke ----

        private static bool EnableDebugPrivilege()
        {
            const uint TokenAdjustPrivileges = 0x0020;
            const uint TokenQuery = 0x0008;
            const uint SePrivilegeEnabled = 0x00000002;

            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out nint hToken))
                return false;

            try
            {
                if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out Luid luid))
                    return false;

                var tp = new TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SePrivilegeEnabled
                };
                bool ok = AdjustTokenPrivileges(hToken, false, ref tp, 0, 0, 0);
                return ok && Marshal.GetLastWin32Error() == 0;
            }
            finally
            {
                CloseHandle(hToken);
            }
        }

        private static List<(ulong BaseAddr, ulong Size)> EnumerateReadableRegions(nint hProc)
        {
            var regions = new List<(ulong, ulong)>();
            ulong addr = 0;
            int mbiSize = Marshal.SizeOf<MemoryBasicInformation>();

            while (addr < 0x7FFF_FFFF_FFFFUL)
            {
                nuint res = VirtualQueryEx(hProc, (nint)addr, out MemoryBasicInformation mbi, (nuint)mbiSize);
                if (res == 0) break;

                if (mbi.State == MemCommit &&
                    ReadableProtects.Contains(mbi.Protect) &&
                    mbi.RegionSize > 0 &&
                    mbi.RegionSize < 500UL * 1024 * 1024)
                {
                    regions.Add((mbi.BaseAddress, mbi.RegionSize));
                }

                ulong next = mbi.BaseAddress + mbi.RegionSize;
                if (next <= addr) break;
                addr = next;
            }

            return regions;
        }

        private static IEnumerable<(ulong BaseAddr, byte[] Data)> ReadChunks(
            nint hProc,
            List<(ulong BaseAddr, ulong Size)> regions,
            int chunkSize,
            int overlap,
            CancellationToken ct)
        {
            foreach (var (baseAddr, size) in regions)
            {
                ulong off = 0;
                byte[] tail = [];
                ulong tailBase = baseAddr;

                while (off < size)
                {
                    ct.ThrowIfCancellationRequested();
                    int cur = (int)Math.Min((ulong)chunkSize, size - off);
                    byte[]? chunk = ReadMemory(hProc, baseAddr + off, cur);

                    ulong dataBase = tail.Length > 0 ? tailBase : baseAddr + off;
                    byte[] data;
                    if (tail.Length > 0 && chunk is { Length: > 0 })
                    {
                        data = new byte[tail.Length + chunk.Length];
                        Buffer.BlockCopy(tail, 0, data, 0, tail.Length);
                        Buffer.BlockCopy(chunk, 0, data, tail.Length, chunk.Length);
                    }
                    else if (chunk is { Length: > 0 })
                    {
                        data = chunk;
                    }
                    else
                    {
                        data = tail;
                    }

                    if (data.Length > 0)
                    {
                        yield return (dataBase, data);
                        if (overlap > 0)
                        {
                            int take = Math.Min(overlap, data.Length);
                            tail = new byte[take];
                            Buffer.BlockCopy(data, data.Length - take, tail, 0, take);
                            tailBase = dataBase + (ulong)(data.Length - take);
                        }
                        else
                        {
                            tail = [];
                            tailBase = baseAddr + off + (ulong)cur;
                        }
                    }
                    else
                    {
                        tail = [];
                        tailBase = baseAddr + off + (ulong)cur;
                    }

                    off += (ulong)cur;
                }
            }
        }

        private static byte[]? ReadMemory(nint hProc, ulong address, int size)
        {
            if (size <= 0) return null;
            byte[] buf = new byte[size];
            if (ReadProcessMemory(hProc, (nint)address, buf, (nuint)size, out nuint read) && read > 0)
            {
                if ((int)read == size) return buf;
                Array.Resize(ref buf, (int)read);
                return buf;
            }
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivileges
        {
            public uint PrivilegeCount;
            public Luid Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public ulong BaseAddress;
            public ulong AllocationBase;
            public uint AllocationProtect;
            private uint _pad1;
            public ulong RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            private uint _pad2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, [Out] byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nuint VirtualQueryEx(nint hProcess, nint lpAddress, out MemoryBasicInformation lpBuffer, nuint dwLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(nint ProcessHandle, uint DesiredAccess, out nint TokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out Luid lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(nint TokenHandle, [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges, ref TokenPrivileges NewState, uint BufferLength, nint PreviousState, nint ReturnLength);
    }
}
