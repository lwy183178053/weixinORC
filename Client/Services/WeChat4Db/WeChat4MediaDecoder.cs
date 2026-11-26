using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konata.Codec.Audio;
using Konata.Codec.Audio.Codecs;

namespace Client.Services.WeChat4Db
{
    /// <summary>
    /// Decodes WeChat 4.x local media:
    /// - Image .dat files (legacy XOR, V1 fixed AES, V2 account-derived AES + XOR with cache-trailer tolerance)
    /// - Voice blobs (Tencent SILK v3 -> standard playable 24kHz mono PCM WAV via Konata.Codec)
    /// </summary>
    public sealed class WeChat4MediaDecoder
    {
        private static readonly byte[] SigV1 = Convert.FromHexString("070856310807");
        private static readonly byte[] SigV2 = Convert.FromHexString("070856320807");
        private static readonly byte[] V1Key = Encoding.ASCII.GetBytes(
            Convert.ToHexString(MD5.HashData("0"u8)).ToLowerInvariant()[..16]);

        private static readonly byte[] SilkHeader = "#!SILK_V3"u8.ToArray();
        private static readonly byte[] TencentSilkHeader = [0x02, (byte)'#', (byte)'!', (byte)'S', (byte)'I', (byte)'L', (byte)'K', (byte)'_', (byte)'V', (byte)'3'];

        private static readonly (byte[] Magic, string Ext)[] ImageSignatures =
        [
            ([0xFF, 0xD8, 0xFF], ".jpg"),
            ([0x89, 0x50, 0x4E, 0x47], ".png"),
            ("GIF8"u8.ToArray(), ".gif"),
            ("RIFF"u8.ToArray(), ".webp"),
            ("BM"u8.ToArray(), ".bmp")
        ];

        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessVmRead = 0x0010;
        private const uint MemCommit = 0x1000;
        private const uint MemPrivate = 0x20000;
        private const uint PageGuard = 0x100;
        private static readonly HashSet<uint> ReadableProtects = [0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80];
        private static readonly HashSet<uint> WritableProtects = [0x04, 0x08, 0x40, 0x80];

        private readonly string _accountDir;
        private readonly string _wxid;
        private readonly Dictionary<string, Dictionary<string, string>> _sessionFileIndex = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<string>>? _videoFileIndex;
        private readonly object _indexLock = new();
        private readonly object _v2KeyLock = new();
        private byte[]? _v2AesKey;
        private byte? _cachedXorKey;
        private bool _v2KeyAttempted;

        public WeChat4MediaDecoder(string accountDir, string wxid = "")
        {
            _accountDir = accountDir;
            _wxid = wxid;
            TryLoadCachedMediaKey();
        }

        private static string MediaKeyCachePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "weixinORC",
                "wx4_media_keys_v2.json");

        private sealed class CachedMediaKeyEntry
        {
            public string AesHex { get; set; } = string.Empty;
            public int? XorKey { get; set; }
        }

        public byte[]? TryResolveSessionImage(string sessionUsername, IReadOnlyList<string> hints)
        {
            if (string.IsNullOrWhiteSpace(_accountDir) || hints.Count == 0) return null;

            var index = GetOrBuildSessionIndex(sessionUsername);
            var triedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawHint in hints)
            {
                if (string.IsNullOrWhiteSpace(rawHint)) continue;
                string trimmedHint = rawHint.Trim();
                if (trimmedHint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    trimmedHint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string hint = trimmedHint.ToLowerInvariant();

                // Build ordered candidate paths: direct path -> _h (HD) -> exact -> _t (thumb JPEG fallback when exact is WXGF) -> partial matches
                var candidates = new List<string>(4);
                void AddCand(string? path)
                {
                    if (!string.IsNullOrWhiteSpace(path) && triedPaths.Add(path))
                    {
                        candidates.Add(path);
                    }
                }

                if (trimmedHint.Contains('/') || trimmedHint.Contains('\\'))
                {
                    string relCandidate = Path.Combine(_accountDir, trimmedHint.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(relCandidate)) AddCand(relCandidate);
                }

                if (index.TryGetValue(hint + "_h", out var hd)) AddCand(hd);
                if (index.TryGetValue(hint, out var exact)) AddCand(exact);
                if (index.TryGetValue(hint + "_t", out var thumb)) AddCand(thumb);

                if (candidates.Count == 0 && hint.Length >= 8)
                {
                    var partials = new List<string>();
                    foreach (var kv in index)
                    {
                        if (kv.Key.Contains(hint, StringComparison.OrdinalIgnoreCase))
                        {
                            partials.Add(kv.Value);
                        }
                    }
                    partials.Sort((a, b) =>
                    {
                        static int Tier(string p)
                        {
                            string s = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                            if (s.EndsWith("_h", StringComparison.Ordinal)) return 0;
                            if (s.EndsWith("_t", StringComparison.Ordinal) || s.Contains("thumb", StringComparison.Ordinal)) return 2;
                            return 1;
                        }
                        return Tier(a).CompareTo(Tier(b));
                    });
                    foreach (var p in partials) AddCand(p);
                }

                // Try every candidate in priority order (if main .dat is WXGF, automatically falls back to _t.dat JPEG!)
                foreach (var matchedPath in candidates)
                {
                    try
                    {
                        var fi = new FileInfo(matchedPath);
                        if (!fi.Exists || fi.Length is < 32 or > 8 * 1024 * 1024) continue;
                        byte[] raw = File.ReadAllBytes(matchedPath);

                        if (raw.Length >= 31 && raw.AsSpan(0, 6).SequenceEqual(SigV2) && _v2AesKey is null)
                        {
                            EnsureV2AesKeyDiscovered(raw);
                        }

                        byte[]? decoded = DecodeDat(raw, _v2AesKey, _cachedXorKey, out byte? discoveredXor);
                        if (discoveredXor.HasValue && _cachedXorKey != discoveredXor)
                        {
                            _cachedXorKey = discoveredXor;
                            if (_v2AesKey is not null) SaveCachedMediaKey(_v2AesKey, _cachedXorKey);
                        }
                        if (decoded is { Length: >= 32 and <= 8 * 1024 * 1024 } && IsWebSafeImage(decoded))
                        {
                            return decoded;
                        }
                    }
                    catch { }
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves plaintext cover thumbnail (.jpg / _thumb.jpg) and video file (.mp4 / .mov)
        /// from WeChat's msg/video directory using the message's MD5 hints.
        /// </summary>
        public (byte[]? CoverImageBytes, string? VideoFilePath) TryResolveVideoAssets(IReadOnlyList<string> hints)
        {
            if (string.IsNullOrWhiteSpace(_accountDir) || hints.Count == 0) return (null, null);

            var vIndex = GetOrBuildVideoIndex();
            if (vIndex.Count == 0) return (null, null);

            string? bestCoverPath = null;
            string? bestVideoPath = null;

            foreach (var rawHint in hints)
            {
                if (string.IsNullOrWhiteSpace(rawHint)) continue;
                string key = Path.GetFileNameWithoutExtension(rawHint.Trim()).ToLowerInvariant();
                if (key.EndsWith("_thumb", StringComparison.Ordinal)) key = key[..^6];
                if (!vIndex.TryGetValue(key, out var paths)) continue;

                foreach (var p in paths)
                {
                    string ext = Path.GetExtension(p).ToLowerInvariant();
                    string stem = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                    if (ext is ".mp4" or ".mov" or ".webm")
                    {
                        bestVideoPath ??= p;
                    }
                    else if (ext is ".jpg" or ".jpeg" or ".png" or ".webp")
                    {
                        // Prefer full cover .jpg over smaller _thumb.jpg
                        if (bestCoverPath is null || !stem.EndsWith("_thumb", StringComparison.Ordinal))
                        {
                            bestCoverPath = p;
                        }
                    }
                }
                if (bestCoverPath is not null && bestVideoPath is not null) break;
            }

            byte[]? coverBytes = null;
            if (!string.IsNullOrWhiteSpace(bestCoverPath))
            {
                try
                {
                    var fi = new FileInfo(bestCoverPath);
                    if (fi.Exists && fi.Length is >= 32 and <= 8 * 1024 * 1024)
                    {
                        byte[] raw = File.ReadAllBytes(bestCoverPath);
                        if (IsWebSafeImage(raw)) coverBytes = TrimRasterTrailer(raw);
                    }
                }
                catch { }
            }

            return (coverBytes, bestVideoPath);
        }

        private Dictionary<string, List<string>> GetOrBuildVideoIndex()
        {
            lock (_indexLock)
            {
                if (_videoFileIndex is not null) return _videoFileIndex;
            }

            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string videoRoot = Path.Combine(_accountDir, "msg", "video");
            if (Directory.Exists(videoRoot))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(videoRoot, "*.*", SearchOption.AllDirectories))
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext is not (".mp4" or ".mov" or ".webm" or ".jpg" or ".jpeg" or ".png" or ".webp")) continue;
                        string stem = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                        string key = stem.EndsWith("_thumb", StringComparison.Ordinal) ? stem[..^6] : stem;
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        if (!map.TryGetValue(key, out var list))
                        {
                            list = [];
                            map[key] = list;
                        }
                        list.Add(file);
                    }
                }
                catch { }
            }

            lock (_indexLock)
            {
                _videoFileIndex = map;
            }
            return map;
        }

        /// <summary>
        /// Decodes Tencent SILK v3 voice bytes (from media_N.db VoiceInfo) into a spec-compliant
        /// 44-byte RIFF/WAVE 24kHz 16-bit mono PCM buffer.
        /// Note: We intentionally bypass Konata.Codec's WavCodec.Encoder because it emits a malformed
        /// 42-byte header ("fmt\0" + wrong RIFF/data sizes) that Windows MediaPlayer rejects.
        /// </summary>
        public static byte[]? DecodeSilkToWav(byte[] rawVoiceBytes)
        {
            if (rawVoiceBytes is null || rawVoiceBytes.Length < 12 || rawVoiceBytes.Length > 16 * 1024 * 1024)
                return null;

            // Already standard WAV or MP3
            if (rawVoiceBytes.Length >= 44 &&
                rawVoiceBytes[0] == 'R' && rawVoiceBytes[1] == 'I' && rawVoiceBytes[2] == 'F' && rawVoiceBytes[3] == 'F' &&
                rawVoiceBytes[8] == 'W' && rawVoiceBytes[9] == 'A' && rawVoiceBytes[10] == 'V' && rawVoiceBytes[11] == 'E')
            {
                return RepairMalformedKonataWav(rawVoiceBytes);
            }
            if ((rawVoiceBytes[0] == 'I' && rawVoiceBytes[1] == 'D' && rawVoiceBytes[2] == '3') ||
                (rawVoiceBytes[0] == 0xFF && (rawVoiceBytes[1] & 0xE0) == 0xE0))
            {
                return rawVoiceBytes;
            }

            bool isTencentSilk = rawVoiceBytes.AsSpan().StartsWith(TencentSilkHeader);
            bool isStdSilk = !isTencentSilk && rawVoiceBytes.AsSpan().StartsWith(SilkHeader);
            if (!isTencentSilk && !isStdSilk)
            {
                return null;
            }

            // Ensure Konata.Codec receives standard Tencent SILK header (\x02#!SILK_V3)
            byte[] normalized = isTencentSilk ? rawVoiceBytes : [0x02, .. rawVoiceBytes];

            try
            {
                using var input = new MemoryStream(normalized, writable: false);
                using var pcmOut = new MemoryStream();
                using var pipeline = new AudioPipeline
                {
                    input,
                    new SilkV3Codec.Decoder(),
                    pcmOut
                };

                if (pipeline.Start().GetAwaiter().GetResult())
                {
                    byte[] pcm = pcmOut.ToArray();
                    if (pcm.Length >= 32)
                    {
                        return BuildStandardPcmWav(pcm, sampleRate: 24000, channels: 1, bitsPerSample: 16);
                    }
                }
            }
            catch { }

            return null;
        }

        public static byte[] BuildStandardPcmWav(ReadOnlySpan<byte> pcm, int sampleRate = 24000, short channels = 1, short bitsPerSample = 16)
        {
            int blockAlign = channels * (bitsPerSample / 8);
            int byteRate = sampleRate * blockAlign;
            byte[] wav = new byte[44 + pcm.Length];

            "RIFF"u8.CopyTo(wav.AsSpan(0, 4));
            BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4, 4), 36 + pcm.Length);
            "WAVE"u8.CopyTo(wav.AsSpan(8, 4));
            "fmt "u8.CopyTo(wav.AsSpan(12, 4));
            BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), 16);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20, 2), 1); // PCM
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22, 2), channels);
            BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24, 4), sampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28, 4), byteRate);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32, 2), (short)blockAlign);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34, 2), bitsPerSample);
            "data"u8.CopyTo(wav.AsSpan(36, 4));
            BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), pcm.Length);
            pcm.CopyTo(wav.AsSpan(44));
            return wav;
        }

        public static byte[] RepairMalformedKonataWav(byte[] raw)
        {
            if (raw.Length <= 42 ||
                raw[0] != 'R' || raw[1] != 'I' || raw[2] != 'F' || raw[3] != 'F' ||
                raw[8] != 'W' || raw[9] != 'A' || raw[10] != 'V' || raw[11] != 'E' ||
                raw[12] != 'f' || raw[13] != 'm' || raw[14] != 't' || raw[15] != 0 ||
                raw[34] != 'd' || raw[35] != 'a' || raw[36] != 't' || raw[37] != 'a')
            {
                return raw;
            }
            int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(24, 4));
            if (sampleRate is < 8000 or > 48000) sampleRate = 24000;
            short channels = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(22, 2));
            if (channels is < 1 or > 2) channels = 1;
            return BuildStandardPcmWav(raw.AsSpan(42), sampleRate, channels, 16);
        }

        private Dictionary<string, string> GetOrBuildSessionIndex(string sessionUsername)
        {
            lock (_indexLock)
            {
                if (_sessionFileIndex.TryGetValue(sessionUsername, out var cached))
                {
                    return cached;
                }
            }

            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string hash = WeChat4Reader.MsgTableFor(sessionUsername)[4..];
            var dirsToScan = new[]
            {
                Path.Combine(_accountDir, "msg", "attach", hash),
                Path.Combine(_accountDir, "msg", "video", hash)
            };

            foreach (var dir in dirsToScan)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext is not (".dat" or ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp")) continue;
                        string name = Path.GetFileName(file).ToLowerInvariant();
                        index.TryAdd(name, file);
                        string stem = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                        index.TryAdd(stem, file);
                    }
                }
                catch { }
            }

            lock (_indexLock)
            {
                _sessionFileIndex[sessionUsername] = index;
            }
            return index;
        }

        /// <summary>
        /// Recovers the 16-byte V2 image AES key from running WeChat heap memory:
        /// Pass 1: Account config struct derivation (WeChat 4.1 / 4.2: uint32 seed 8 bytes before wxid std::string -> MD5(seed + wxid)[..16]).
        /// Pass 2: Fallback 16/32/64-char ASCII &amp; hex string + protobuf 16-byte field scan.
        /// Every candidate is strictly validated against the full PKCS#7 AES prefix + image magic.
        /// </summary>
        private void EnsureV2AesKeyDiscovered(byte[] sampleV2Dat)
        {
            lock (_v2KeyLock)
            {
                if (_v2AesKey is not null || _v2KeyAttempted) return;
                _v2KeyAttempted = true;

                if (sampleV2Dat.Length < 31 || !sampleV2Dat.AsSpan(0, 6).SequenceEqual(SigV2)) return;
                uint aesSize = BinaryPrimitives.ReadUInt32LittleEndian(sampleV2Dat.AsSpan(6, 4));
                int aligned = (int)((aesSize / 16 + 1) * 16);
                if (aesSize == 0 || 15 + aligned > sampleV2Dat.Length || aligned > 4 * 1024 * 1024) return;
                byte[] cipherPrefix = sampleV2Dat.AsSpan(15, aligned).ToArray();
                int expectedAesLen = (int)aesSize;

                var procs = WeChat4Paths.FindWeixinProcesses();
                if (procs.Count == 0) return;

                var deadline = DateTime.UtcNow.AddSeconds(12);
                var tested = new HashSet<string>(StringComparer.Ordinal);
                byte[] candAscii = new byte[16];

                bool TryValidateKey(byte[] candKey)
                {
                    if (candKey.Length != 16) return false;
                    string tag = Convert.ToHexString(candKey);
                    if (!tested.Add(tag)) return false;
                    byte[]? plain = DecryptAesEcbPkcs7(cipherPrefix, candKey, expectedAesLen);
                    if (plain is not null && IsStrictWebImagePrefix(plain))
                    {
                        _v2AesKey = candKey.ToArray();
                        SaveCachedMediaKey(_v2AesKey, _cachedXorKey);
                        return true;
                    }
                    return false;
                }

                foreach (var proc in procs.Take(3))
                {
                    if (DateTime.UtcNow > deadline) break;
                    nint h = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, (uint)proc.Pid);
                    if (h == 0) continue;
                    try
                    {
                        var regions = EnumerateReadableRegions(h);

                        // Pass 1: WeChat 4.1/4.2 account-config seed + wxid derivation (super fast & accurate!)
                        if (!string.IsNullOrWhiteSpace(_wxid))
                        {
                            var pass1Deadline = DateTime.UtcNow.AddSeconds(5);
                            if (pass1Deadline > deadline) pass1Deadline = deadline;
                            if (TryDiscoverKeyFromAccountConfig(h, regions, _wxid, pass1Deadline, TryValidateKey))
                            {
                                return;
                            }
                        }

                        // Pass 2: Fallback string/hex/protobuf scan on private RW heaps first
                        byte[] buf = new byte[2 * 1024 * 1024];
                        foreach (var (_, baseAddr, regSize) in regions)
                        {
                            if (DateTime.UtcNow > deadline) break;
                            ulong off = 0;
                            while (off < regSize && DateTime.UtcNow < deadline)
                            {
                                int toRead = (int)Math.Min((ulong)buf.Length, regSize - off);
                                if (!ReadProcessMemory(h, (nint)(baseAddr + off), buf, (nuint)toRead, out nuint read) || read < 32)
                                {
                                    off += (ulong)toRead;
                                    continue;
                                }

                                int len = (int)read;
                                int run = 0;
                                for (int i = 0; i < len; i++)
                                {
                                    byte b = buf[i];
                                    bool isAlphaNum = (b >= (byte)'0' && b <= (byte)'9') ||
                                                      (b >= (byte)'a' && b <= (byte)'z') ||
                                                      (b >= (byte)'A' && b <= (byte)'Z');
                                    if (isAlphaNum)
                                    {
                                        run++;
                                    }
                                    else
                                    {
                                        if (run is 16 or 32 or 64)
                                        {
                                            int start = i - run;
                                            buf.AsSpan(start, 16).CopyTo(candAscii);
                                            if (TryValidateKey(candAscii.ToArray())) return;

                                            if (run is 32 or 64)
                                            {
                                                string hex32 = Encoding.ASCII.GetString(buf, start, 32);
                                                if (IsHexString(hex32))
                                                {
                                                    try
                                                    {
                                                        if (TryValidateKey(Convert.FromHexString(hex32))) return;
                                                    }
                                                    catch { }
                                                }
                                            }
                                        }
                                        run = 0;
                                    }
                                }
                                off += (ulong)toRead;
                            }
                        }
                    }
                    finally
                    {
                        CloseHandle(h);
                    }
                }
            }
        }

        private static List<(int Priority, ulong BaseAddr, ulong Size)> EnumerateReadableRegions(nint h)
        {
            var regions = new List<(int, ulong, ulong)>();
            ulong addr = 0;
            int mbiSize = Marshal.SizeOf<MemoryBasicInformation>();
            while (addr < 0x7FFF_FFFF_FFFFUL)
            {
                if (VirtualQueryEx(h, (nint)addr, out var mbi, (nuint)mbiSize) == 0) break;
                ulong next = mbi.BaseAddress + mbi.RegionSize;
                if (next <= addr) break;
                addr = next;

                uint prot = mbi.Protect & 0xFF;
                if (mbi.State != MemCommit || (mbi.Protect & PageGuard) != 0 || !ReadableProtects.Contains(prot))
                    continue;
                if (mbi.RegionSize == 0 || mbi.RegionSize > 128UL * 1024 * 1024)
                    continue;

                int priority = (mbi.Type == MemPrivate && WritableProtects.Contains(prot)) ? 0 : 1;
                regions.Add((priority, mbi.BaseAddress, mbi.RegionSize));
            }
            regions.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return regions;
        }

        private static bool TryDiscoverKeyFromAccountConfig(
            nint h,
            List<(int Priority, ulong BaseAddr, ulong Size)> regions,
            string wxid,
            DateTime deadline,
            Func<byte[], bool> tryValidate)
        {
            byte[] accountBytes = Encoding.UTF8.GetBytes(wxid);
            if (accountBytes.Length is < 1 or > 128) return false;
            byte[] needle = [.. accountBytes, 0];
            bool longAccount = accountBytes.Length > 15;

            var heapRegions = regions.Where(r => r.Priority == 0).ToList();
            var addresses = new HashSet<ulong>();
            var checkedObjects = new HashSet<ulong>();
            byte[] recordBuf = new byte[40];

            bool CheckConfigAt(ulong objAddr)
            {
                if (objAddr < 0x10008 || !checkedObjects.Add(objAddr)) return false;
                if (!ReadProcessMemory(h, (nint)(objAddr - 8), recordBuf, 40, out nuint got) || got != 40)
                    return false;

                ulong len = BinaryPrimitives.ReadUInt64LittleEndian(recordBuf.AsSpan(24, 8));
                ulong cap = BinaryPrimitives.ReadUInt64LittleEndian(recordBuf.AsSpan(32, 8));
                if (len != (ulong)accountBytes.Length || cap < len || cap >= 0x100000)
                    return false;

                uint seed = BinaryPrimitives.ReadUInt32LittleEndian(recordBuf.AsSpan(0, 4));
                string seedWxid = seed.ToString() + wxid;
                string md5Hex = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(seedWxid))).ToLowerInvariant();
                byte[] candKey = Encoding.ASCII.GetBytes(md5Hex[..16]);
                return tryValidate(candKey);
            }

            var discoveryDeadline = longAccount
                ? DateTime.UtcNow.AddMilliseconds(Math.Max(500, (deadline - DateTime.UtcNow).TotalMilliseconds / 2))
                : deadline;

            byte[] chunk = new byte[4 * 1024 * 1024 + 128];

            // Scan private RW heaps for wxid\0
            foreach (var (_, baseAddr, regSize) in heapRegions)
            {
                if (DateTime.UtcNow >= discoveryDeadline || addresses.Count >= 256) break;
                for (ulong off = 0; off < regSize && DateTime.UtcNow < discoveryDeadline && addresses.Count < 256; off += 4 * 1024 * 1024)
                {
                    int toRead = (int)Math.Min((ulong)chunk.Length, regSize - off);
                    if (!ReadProcessMemory(h, (nint)(baseAddr + off), chunk, (nuint)toRead, out nuint read) || read < (nuint)needle.Length)
                        continue;

                    var span = chunk.AsSpan(0, (int)read);
                    int pos = 0;
                    while (pos <= span.Length - needle.Length)
                    {
                        int idx = span[pos..].IndexOf(needle);
                        if (idx < 0) break;
                        int absIdx = pos + idx;
                        ulong strAddr = baseAddr + off + (ulong)absIdx;
                        addresses.Add(strAddr);

                        if (!longAccount && CheckConfigAt(strAddr))
                            return true;

                        pos = absIdx + 1;
                        if (addresses.Count >= 256) break;
                    }
                }
            }

            if (!longAccount || addresses.Count == 0) return false;

            // For long wxid (>15 bytes), std::string stores a heap pointer to strAddr; scan heaps for 8-byte pointers to any strAddr
            var ptrNeedles = addresses.Select(a =>
            {
                byte[] b = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(b, a);
                return b;
            }).ToList();

            foreach (var (_, baseAddr, regSize) in heapRegions)
            {
                if (DateTime.UtcNow >= deadline) break;
                for (ulong off = 0; off < regSize && DateTime.UtcNow < deadline; off += 4 * 1024 * 1024)
                {
                    int toRead = (int)Math.Min((ulong)chunk.Length, regSize - off);
                    if (!ReadProcessMemory(h, (nint)(baseAddr + off), chunk, (nuint)toRead, out nuint read) || read < 8)
                        continue;

                    var span = chunk.AsSpan(0, (int)read);
                    foreach (var ptrBytes in ptrNeedles)
                    {
                        if (DateTime.UtcNow >= deadline) return false;
                        int pos = 0;
                        while (pos <= span.Length - 8)
                        {
                            int idx = span[pos..].IndexOf(ptrBytes);
                            if (idx < 0) break;
                            int absIdx = pos + idx;
                            if (CheckConfigAt(baseAddr + off + (ulong)absIdx))
                                return true;
                            pos = absIdx + 1;
                        }
                    }
                }
            }

            return false;
        }

        private static bool IsHexString(string s)
        {
            foreach (char c in s)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return s.Length > 0;
        }

        private void TryLoadCachedMediaKey()
        {
            if (string.IsNullOrWhiteSpace(_accountDir) || !File.Exists(MediaKeyCachePath)) return;
            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, CachedMediaKeyEntry>>(File.ReadAllText(MediaKeyCachePath));
                if (map is not null && map.TryGetValue(Path.GetFullPath(_accountDir), out var entry))
                {
                    if (!string.IsNullOrWhiteSpace(entry.AesHex) && entry.AesHex.Length == 32)
                    {
                        _v2AesKey = Convert.FromHexString(entry.AesHex);
                    }
                    if (entry.XorKey is >= 0 and <= 255)
                    {
                        _cachedXorKey = (byte)entry.XorKey.Value;
                    }
                }
            }
            catch { }
        }

        private void SaveCachedMediaKey(byte[] key, byte? xorKey)
        {
            if (string.IsNullOrWhiteSpace(_accountDir)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MediaKeyCachePath)!);
                var map = File.Exists(MediaKeyCachePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, CachedMediaKeyEntry>>(File.ReadAllText(MediaKeyCachePath)) ?? new(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, CachedMediaKeyEntry>(StringComparer.OrdinalIgnoreCase);
                map[Path.GetFullPath(_accountDir)] = new CachedMediaKeyEntry
                {
                    AesHex = Convert.ToHexString(key).ToLowerInvariant(),
                    XorKey = xorKey.HasValue ? xorKey.Value : null
                };
                File.WriteAllText(MediaKeyCachePath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static byte[]? DecodeDat(byte[] data, byte[]? accountAesKey = null, byte? fallbackXorKey = null) =>
            DecodeDat(data, accountAesKey, fallbackXorKey, out _);

        public static byte[]? DecodeDat(byte[] data, byte[]? accountAesKey, byte? fallbackXorKey, out byte? discoveredXorKey)
        {
            discoveredXorKey = null;
            if (data.Length < 16) return null;
            if (IsImagePrefix(data)) return TrimRasterTrailer(data);

            if (data.Length >= 31 && (data.AsSpan(0, 6).SequenceEqual(SigV1) || data.AsSpan(0, 6).SequenceEqual(SigV2)))
            {
                bool isV1 = data.AsSpan(0, 6).SequenceEqual(SigV1);
                byte[]? key = isV1 ? V1Key : accountAesKey;
                if (key is null || key.Length != 16) return null;

                uint aesSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(6, 4));
                uint xorSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10, 4));
                int aligned = (int)((aesSize / 16 + 1) * 16);
                int end = 15 + aligned;
                int tailStart = data.Length - (int)xorSize;
                if (aesSize == 0 || end > data.Length || tailStart < end) return null;

                byte[]? plainPrefix = DecryptAesEcbPkcs7(data.AsSpan(15, aligned), key, (int)aesSize);
                if (plainPrefix is null || !IsImagePrefix(plainPrefix)) return null;

                int midLen = tailStart - end;
                if (xorSize == 0)
                {
                    byte[] combined = new byte[plainPrefix.Length + midLen];
                    Buffer.BlockCopy(plainPrefix, 0, combined, 0, plainPrefix.Length);
                    if (midLen > 0) Buffer.BlockCopy(data, end, combined, plainPrefix.Length, midLen);
                    return TrimRasterTrailer(combined);
                }

                // Check if prefix + mid ALREADY contains a complete raster (cache metadata sits only in XOR tail)
                byte[] baseBytes = new byte[plainPrefix.Length + midLen];
                Buffer.BlockCopy(plainPrefix, 0, baseBytes, 0, plainPrefix.Length);
                if (midLen > 0) Buffer.BlockCopy(data, end, baseBytes, plainPrefix.Length, midLen);
                if (HasCompleteImageEnding(baseBytes))
                {
                    return TrimRasterTrailer(baseBytes);
                }

                var tailSpan = data.AsSpan(tailStart, (int)xorSize);
                byte? xk = InferXorKeyFlexible(tailSpan, plainPrefix) ?? fallbackXorKey;
                if (xk is null) return null;
                discoveredXorKey = xk;

                byte keyByte = xk.Value;
                byte[] result = new byte[baseBytes.Length + (int)xorSize];
                Buffer.BlockCopy(baseBytes, 0, result, 0, baseBytes.Length);
                for (int i = 0; i < (int)xorSize; i++)
                {
                    result[baseBytes.Length + i] = (byte)(data[tailStart + i] ^ keyByte);
                }
                return TrimRasterTrailer(result);
            }

            Span<byte> head = stackalloc byte[16];
            foreach (var (magic, _) in ImageSignatures)
            {
                byte k = (byte)(data[0] ^ magic[0]);
                for (int i = 0; i < 16; i++) head[i] = (byte)(data[i] ^ k);
                if (IsImagePrefix(head))
                {
                    byte[] output = new byte[data.Length];
                    for (int i = 0; i < data.Length; i++) output[i] = (byte)(data[i] ^ k);
                    return TrimRasterTrailer(output);
                }
            }

            return null;
        }

        private static byte[]? DecryptAesEcbPkcs7(ReadOnlySpan<byte> cipher, byte[] key, int expectedLen)
        {
            try
            {
                using var aes = Aes.Create();
                aes.Key = key;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                byte[] plain = aes.DecryptEcb(cipher, PaddingMode.None);
                if (plain.Length == 0) return null;
                byte pad = plain[^1];
                if (pad < 1 || pad > 16 || pad > plain.Length) return null;
                for (int i = plain.Length - pad; i < plain.Length; i++)
                {
                    if (plain[i] != pad) return null;
                }
                int actualLen = plain.Length - pad;
                if (actualLen != expectedLen) return null;
                Array.Resize(ref plain, actualLen);
                return plain;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Infers single-byte XOR key even when WeChat appends cache metadata after JPEG EOI (FF D9) or PNG IEND.
        /// </summary>
        private static byte? InferXorKeyFlexible(ReadOnlySpan<byte> tail, ReadOnlySpan<byte> prefix)
        {
            if (tail.Length < 2) return null;

            // PNG: exact 8-byte IEND + CRC trailer (almost zero false positives!)
            if (prefix.Length >= 4 && prefix[0] == 0x89 && prefix[1] == 0x50)
            {
                ReadOnlySpan<byte> iend = [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
                int windowStart = Math.Max(0, tail.Length - 8192);
                for (int pos = tail.Length - iend.Length; pos >= windowStart; pos--)
                {
                    byte k = (byte)(tail[pos] ^ iend[0]);
                    bool match = true;
                    for (int j = 1; j < iend.Length; j++)
                    {
                        if ((byte)(tail[pos + j] ^ iend[j]) != k) { match = false; break; }
                    }
                    if (match) return k;
                }
                return null;
            }

            // JPEG: check exact end first, then scan backwards up to 128 bytes for trailing cache metadata after FF D9
            if (prefix.Length >= 2 && prefix[0] == 0xFF && prefix[1] == 0xD8)
            {
                byte exactK = (byte)(tail[^2] ^ 0xFF);
                if ((byte)(tail[^1] ^ 0xD9) == exactK) return exactK;

                int scanStart = Math.Max(0, tail.Length - 256);
                for (int pos = tail.Length - 3; pos >= scanStart; pos--)
                {
                    byte k = (byte)(tail[pos] ^ 0xFF);
                    if ((byte)(tail[pos + 1] ^ 0xD9) == k)
                    {
                        return k;
                    }
                }
                return null;
            }

            // GIF: trailer byte 0x3B
            if (prefix.Length >= 4 && prefix[0] == 'G' && prefix[1] == 'I' && prefix[2] == 'F')
            {
                return (byte)(tail[^1] ^ 0x3B);
            }

            return null;
        }

        private static bool HasCompleteImageEnding(ReadOnlySpan<byte> data)
        {
            if (data.Length < 16) return false;
            if (data[0] == 0xFF && data[1] == 0xD8)
            {
                return data[^2] == 0xFF && data[^1] == 0xD9;
            }
            if (data[0] == 0x89 && data[1] == 0x50)
            {
                ReadOnlySpan<byte> iend = [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
                return data.Length >= 20 && data[^8..].SequenceEqual(iend);
            }
            return false;
        }

        private static byte[] TrimRasterTrailer(byte[] data)
        {
            if (data.Length < 16) return data;
            // JPEG: segment-aware EOI scan (strips WeChat trailing 24-byte cache metadata without cutting EXIF thumbnails)
            if (data[0] == 0xFF && data[1] == 0xD8)
            {
                int pos = 2;
                bool inScan = false;
                bool sawScan = false;
                while (pos < data.Length)
                {
                    if (inScan)
                    {
                        int nextFf = Array.IndexOf(data, (byte)0xFF, pos);
                        if (nextFf < 0) break;
                        pos = nextFf;
                    }
                    else if (data[pos] != 0xFF)
                    {
                        break;
                    }
                    while (pos < data.Length && data[pos] == 0xFF) pos++;
                    if (pos >= data.Length) break;
                    byte marker = data[pos++];
                    if (inScan && (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7))) continue;
                    if (marker == 0xD9)
                    {
                        if (sawScan && pos < data.Length) return data[..pos];
                        break;
                    }
                    if (marker is 0x00 or 0xD8 || (marker >= 0xD0 && marker <= 0xD7)) break;
                    if (marker == 0x01) continue;
                    if (pos + 2 > data.Length) break;
                    int segLen = (data[pos] << 8) | data[pos + 1];
                    if (segLen < 2 || pos + segLen > data.Length) break;
                    pos += segLen;
                    inScan = marker == 0xDA || (inScan && marker == 0xDC);
                    if (inScan) sawScan = true;
                }
                return data;
            }
            // PNG: trim strictly after IEND chunk
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                ReadOnlySpan<byte> iend = [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
                int idx = data.AsSpan().LastIndexOf(iend);
                if (idx > 0 && idx + 8 < data.Length)
                {
                    return data[..(idx + 8)];
                }
            }
            // WEBP: RIFF size header
            if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
                data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
            {
                uint riffLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
                long end = (long)riffLen + 8;
                if (end >= 12 && end < data.Length)
                {
                    return data[..(int)end];
                }
            }
            return data;
        }

        private static bool IsStrictWebImagePrefix(ReadOnlySpan<byte> data) =>
            data.Length >= 12 &&
            ((data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) ||
             (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 && data[4] == 0x0D && data[5] == 0x0A) ||
             (data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8') ||
             (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
              data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P'));

        private static bool IsImagePrefix(ReadOnlySpan<byte> data) =>
            IsStrictWebImagePrefix(data) || (data.Length >= 2 && data[0] == 'B' && data[1] == 'M');

        private static bool IsWebSafeImage(ReadOnlySpan<byte> data) => IsStrictWebImagePrefix(data);

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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, [Out] byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nuint VirtualQueryEx(nint hProcess, nint lpAddress, out MemoryBasicInformation lpBuffer, nuint dwLength);
    }
}
