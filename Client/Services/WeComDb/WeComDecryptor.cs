using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Client.Services.WeComDb
{
    /// <summary>
    /// Incrementally snapshots and decrypts WeCom (WXWork) wxSQLite3 AES-128-CBC databases
    /// (.db + -wal frames) into a persistent local plaintext cache.
    /// Zero writes to original WeCom directories; validates every decrypted file with PRAGMA quick_check.
    /// </summary>
    public static class WeComDecryptor
    {
        private const uint WalMagicLe = 0x377F0682;
        private const uint WalMagicBe = 0x377F0683;
        private const int WalHeaderSize = 32;
        private const int WalFrameHeaderSize = 24;

        public static readonly string[] CoreDatabases =
        [
            "session.db",
            "user.db",
            "message.db",
            "company.db",
            "file.db"
        ];

        public sealed class DecryptResult
        {
            public string OutputDir { get; init; } = string.Empty;
            public List<string> Updated { get; } = [];
            public List<string> Reused { get; } = [];
            public List<(string DbName, string Error)> Failed { get; } = [];
            public int TotalAvailable => Updated.Count + Reused.Count;
        }

        public static string GetCacheDir(string accountId)
        {
            string clean = string.IsNullOrWhiteSpace(accountId) ? "default" : accountId.Trim();
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "weixinORC",
                "wecom_cache",
                clean);
        }

        public static bool HasAnyCoreDbChanged(string dataDir, byte[] rawKey16, string cacheDir)
        {
            var manifest = LoadManifest(cacheDir);
            string keyPrefix = Convert.ToHexString(rawKey16)[..8];

            foreach (string dbName in CoreDatabases)
            {
                string src = Path.Combine(dataDir, dbName);
                if (!File.Exists(src)) continue;
                string dst = Path.Combine(cacheDir, dbName);
                if (!File.Exists(dst)) return true;

                string fp = ComputeFingerprint(src) + "|" + keyPrefix;
                if (!manifest.TryGetValue(dbName, out string? prev) || !string.Equals(prev, fp, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        public static DecryptResult DecryptIncremental(
            string dataDir,
            byte[] rawKey16,
            string outputDir,
            int retries = 3,
            CancellationToken ct = default)
        {
            Directory.CreateDirectory(outputDir);
            var manifest = LoadManifest(outputDir);
            string keyPrefix = Convert.ToHexString(rawKey16)[..8];
            string snapDir = Path.Combine(Path.GetTempPath(), "wxwsnap_" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(snapDir);

            var result = new DecryptResult { OutputDir = outputDir };
            try
            {
                foreach (string dbName in CoreDatabases)
                {
                    ct.ThrowIfCancellationRequested();
                    string src = Path.Combine(dataDir, dbName);
                    string dst = Path.Combine(outputDir, dbName);
                    if (!File.Exists(src)) continue;

                    string fp = ComputeFingerprint(src) + "|" + keyPrefix;
                    if (File.Exists(dst) && manifest.TryGetValue(dbName, out string? cachedFp) &&
                        string.Equals(cachedFp, fp, StringComparison.Ordinal))
                    {
                        result.Reused.Add(dbName);
                        continue;
                    }

                    string tmpDst = dst + ".tmp";
                    string? lastErr = null;
                    for (int attempt = 1; attempt <= retries; attempt++)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            string snap = CopySnapshot(src, snapDir);
                            DecryptSingleFile(snap, tmpDst, rawKey16, ct);
                            if (QuickCheck(tmpDst))
                            {
                                File.Move(tmpDst, dst, overwrite: true);
                                manifest[dbName] = fp;
                                result.Updated.Add(dbName);
                                lastErr = null;
                                break;
                            }
                            lastErr = "SQLite quick_check 未通过";
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            lastErr = ex.Message;
                        }
                        if (attempt < retries) Thread.Sleep(150 * attempt);
                    }

                    if (lastErr is not null)
                    {
                        try { if (File.Exists(tmpDst)) File.Delete(tmpDst); } catch { }
                        if (File.Exists(dst))
                        {
                            result.Reused.Add(dbName);
                        }
                        else
                        {
                            result.Failed.Add((dbName, lastErr));
                        }
                    }
                }

                SaveManifest(outputDir, manifest);
                return result;
            }
            finally
            {
                try { if (Directory.Exists(snapDir)) Directory.Delete(snapDir, recursive: true); } catch { }
            }
        }

        private static string CopySnapshot(string srcDbPath, string snapDir)
        {
            string name = Path.GetFileName(srcDbPath);
            string dstDb = Path.Combine(snapDir, name);
            CopyReadShared(srcDbPath, dstDb);

            string walSrc = srcDbPath + "-wal";
            string walDst = dstDb + "-wal";
            if (File.Exists(walSrc))
            {
                try { CopyReadShared(walSrc, walDst); } catch { }
            }
            else if (File.Exists(walDst))
            {
                try { File.Delete(walDst); } catch { }
            }
            return dstDb;
        }

        private static void CopyReadShared(string src, string dst)
        {
            using var inFs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var outFs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
            inFs.CopyTo(outFs, 128 * 1024);
        }

        private static void DecryptSingleFile(string snapDbPath, string dstPath, byte[] rawKey16, CancellationToken ct)
        {
            byte[] raw = File.ReadAllBytes(snapDbPath);
            if (raw.Length < 512) throw new InvalidDataException("数据库文件长度不足。");

            if (WeComCrypto.IsPlaintextSqlite(raw))
            {
                File.WriteAllBytes(dstPath, raw);
                return;
            }

            int pageSize = WeComCrypto.ReadPageSize(raw);
            int numPages = raw.Length / pageSize;
            var pages = new Dictionary<int, byte[]>(numPages + 16);

            for (int pg = 1; pg <= numPages; pg++)
            {
                ct.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> enc = raw.AsSpan((pg - 1) * pageSize, pageSize);
                pages[pg] = WeComCrypto.DecryptPage(enc, pg, rawKey16);
            }

            // Replay committed WAL frames if present
            string walPath = snapDbPath + "-wal";
            if (File.Exists(walPath))
            {
                byte[] wal = File.ReadAllBytes(walPath);
                if (wal.Length >= WalHeaderSize)
                {
                    uint magic = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(0, 4));
                    if (magic is WalMagicLe or WalMagicBe)
                    {
                        int walPageSize = (int)BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(8, 4));
                        uint salt1 = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(16, 4));
                        uint salt2 = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(20, 4));

                        if (walPageSize == pageSize)
                        {
                            int frameSize = WalFrameHeaderSize + pageSize;
                            int off = WalHeaderSize;
                            var pending = new List<(int PageNo, byte[] Data)>();

                            while (off + frameSize <= wal.Length)
                            {
                                ct.ThrowIfCancellationRequested();
                                int pgNo = (int)BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(off, 4));
                                int dbSizePages = (int)BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(off + 4, 4));
                                uint fSalt1 = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(off + 8, 4));
                                uint fSalt2 = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(off + 12, 4));

                                if (fSalt1 != salt1 || fSalt2 != salt2) break;
                                if (pgNo >= 1)
                                {
                                    byte[] encFrame = wal.AsSpan(off + WalFrameHeaderSize, pageSize).ToArray();
                                    pending.Add((pgNo, encFrame));
                                    if (dbSizePages > 0)
                                    {
                                        foreach (var (pNo, fEnc) in pending)
                                        {
                                            try
                                            {
                                                pages[pNo] = WeComCrypto.DecryptPage(fEnc, pNo, rawKey16);
                                            }
                                            catch
                                            {
                                                // Skip malformed mid-write WAL frame
                                            }
                                        }
                                        // Trim pages beyond committed dbSizePages
                                        var overKeys = pages.Keys.Where(k => k > dbSizePages).ToList();
                                        foreach (int k in overKeys) pages.Remove(k);
                                        pending.Clear();
                                    }
                                }
                                off += frameSize;
                            }
                        }
                    }
                }
            }

            int maxPage = pages.Count > 0 ? pages.Keys.Max() : 0;
            byte[] zeroPage = new byte[pageSize];
            using var outFs = new FileStream(dstPath, FileMode.Create, FileAccess.Write, FileShare.None);
            for (int pg = 1; pg <= maxPage; pg++)
            {
                if (pages.TryGetValue(pg, out var buf))
                {
                    outFs.Write(buf, 0, buf.Length);
                }
                else
                {
                    outFs.Write(zeroPage, 0, zeroPage.Length);
                }
            }
        }

        private static bool QuickCheck(string sqlitePath)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly;Pooling=False");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA quick_check(1)";
                string? res = cmd.ExecuteScalar()?.ToString();
                return string.Equals(res, "ok", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string ComputeFingerprint(string dbPath)
        {
            try
            {
                var fi = new FileInfo(dbPath);
                string mainFp = $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
                string walPath = dbPath + "-wal";
                if (File.Exists(walPath))
                {
                    var wfi = new FileInfo(walPath);
                    return $"{mainFp}|{wfi.Length}:{wfi.LastWriteTimeUtc.Ticks}";
                }
                return mainFp;
            }
            catch
            {
                return Guid.NewGuid().ToString("N");
            }
        }

        private static Dictionary<string, string> LoadManifest(string cacheDir)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(cacheDir, "_manifest.json");
            try
            {
                if (!File.Exists(path)) return dict;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
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

        private static void SaveManifest(string cacheDir, Dictionary<string, string> dict)
        {
            try
            {
                string path = Path.Combine(cacheDir, "_manifest.json");
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    foreach (var kv in dict) w.WriteString(kv.Key, kv.Value);
                    w.WriteEndObject();
                }
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, ms.ToArray());
                File.Move(tmp, path, overwrite: true);
            }
            catch { }
        }
    }
}
