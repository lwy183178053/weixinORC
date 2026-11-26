using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Client.Services.WeChat4Db
{
    /// <summary>
    /// Snapshots and decrypts Windows WeChat 4.x SQLCipher 4 databases (.db + -wal incremental frames)
    /// into temporary plaintext SQLite files. Ported from Moect wx4/decrypt.py.
    /// </summary>
    public static partial class WeChat4Decryptor
    {
        private const uint WalMagicLe = 0x377F0682;
        private const uint WalMagicBe = 0x377F0683;
        private const int WalHeaderSize = 32;
        private const int WalFrameHeaderSize = 24;

        [GeneratedRegex(@"^message(?:_\d+)?\.db$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex MessageDbRegex();

        [GeneratedRegex(@"^media(?:_\d+)?\.db$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex MediaDbRegex();

        /// <summary>
        /// Filters to only the databases needed for contacts, groups, avatars, sessions, voice, resources, and chat messages,
        /// avoiding slow/huge FTS, SNS, favorite, or official-account databases.
        /// </summary>
        public static bool IsCoreSyncDatabase(string relPath)
        {
            string file = Path.GetFileName(relPath).ToLowerInvariant();
            if (file.EndsWith("_fts.db", StringComparison.Ordinal)) return false;
            if (file is "contact.db" or "session.db" or "head_image.db" or "message_resource.db") return true;
            if (MessageDbRegex().IsMatch(file)) return true;
            if (MediaDbRegex().IsMatch(file)) return true;
            return false;
        }

        public sealed class DecryptBatchResult
        {
            public string OutputDir { get; init; } = string.Empty;
            public List<string> Succeeded { get; } = [];
            public List<string> Reused { get; } = [];
            public List<(string RelPath, string Error)> Failed { get; } = [];
            public int TotalAvailable => Succeeded.Count + Reused.Count;
        }

        private sealed class CacheManifestEntry
        {
            public string Fingerprint { get; set; } = string.Empty;
            public string KeyPrefix { get; set; } = string.Empty;
        }

        /// <summary>
        /// Computes a persistent cache directory path for a given WeChat account directory.
        /// Reusing unchanged historical shards (e.g. 100MB+ message_0.db) makes incremental syncs 10x+ faster.
        /// </summary>
        public static string GetPersistentCacheDir(string accountPath)
        {
            string norm = (accountPath ?? string.Empty).Trim().ToLowerInvariant();
            string hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(norm))).ToLowerInvariant()[..12];
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "weixinORC",
                "wx4_cache",
                hash);
        }

        /// <summary>
        /// Returns true if any tracked DB file (.db or -wal) has changed compared to the cached manifest.
        /// Pure FileInfo metadata check (typically &lt; 2ms, zero decryption).
        /// </summary>
        public static bool HasAnyDatabaseChanged(
            string dbStorage,
            Dictionary<string, string> relPathToEncKeyHex,
            string cacheDir)
        {
            var manifest = LoadManifest(cacheDir);
            foreach (var kv in relPathToEncKeyHex)
            {
                string src = Path.Combine(dbStorage, kv.Key);
                if (!File.Exists(src)) continue;
                string dst = Path.Combine(cacheDir, kv.Key);
                if (!File.Exists(dst)) return true;

                string fp = ComputeSingleDbFingerprint(src);
                string kp = kv.Value.Length >= 8 ? kv.Value[..8] : kv.Value;
                if (!manifest.TryGetValue(kv.Key, out var entry) ||
                    !string.Equals(entry.Fingerprint, fp, StringComparison.Ordinal) ||
                    !string.Equals(entry.KeyPrefix, kp, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Incrementally decrypts ONLY databases whose .db or -wal file changed since the last cached run.
        /// Unchanged shards (like historical message_0.db / media_0.db / head_image.db) are reused directly in 0ms.
        /// Callers must close any open SQLite connections pointing to <paramref name="outputDir"/> before calling.
        /// </summary>
        public static DecryptBatchResult DecryptIncremental(
            string dbStorage,
            Dictionary<string, string> relPathToEncKeyHex,
            string outputDir,
            Action<string>? progress = null,
            int retries = 3,
            CancellationToken ct = default)
        {
            Directory.CreateDirectory(outputDir);
            var manifest = LoadManifest(outputDir);
            string snapDir = Path.Combine(Path.GetTempPath(), "wx4snap_" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(snapDir);

            var result = new DecryptBatchResult { OutputDir = outputDir };
            try
            {
                int idx = 0;
                int total = relPathToEncKeyHex.Count;
                foreach (var kv in relPathToEncKeyHex.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    idx++;
                    string rel = kv.Key;
                    string src = Path.Combine(dbStorage, rel);
                    string dst = Path.Combine(outputDir, rel);
                    if (!File.Exists(src)) continue;

                    string fp = ComputeSingleDbFingerprint(src);
                    string keyPrefix = kv.Value.Length >= 8 ? kv.Value[..8] : kv.Value;

                    if (File.Exists(dst) &&
                        manifest.TryGetValue(rel, out var cached) &&
                        string.Equals(cached.Fingerprint, fp, StringComparison.Ordinal) &&
                        string.Equals(cached.KeyPrefix, keyPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Reused.Add(rel);
                        continue;
                    }

                    byte[] encKey = Convert.FromHexString(kv.Value);
                    string? lastErr = null;
                    string tmpDst = dst + ".tmp";

                    for (int attempt = 1; attempt <= retries; attempt++)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                            string snap = CopySnapshot(src, snapDir);
                            var (pages, walFrames) = DecryptFile(snap, tmpDst, encKey, applyWal: true, ct);
                            if (QuickCheck(tmpDst))
                            {
                                File.Move(tmpDst, dst, overwrite: true);
                                manifest[rel] = new CacheManifestEntry { Fingerprint = fp, KeyPrefix = keyPrefix };
                                result.Succeeded.Add(rel);
                                progress?.Invoke($"[{idx}/{total}] 增量读取 {rel}（{pages} 页，WAL {walFrames} 帧）");
                                lastErr = null;
                                break;
                            }
                            lastErr = "SQLite quick_check 未通过";
                        }
                        catch (OperationCanceledException)
                        {
                            try { if (File.Exists(tmpDst)) File.Delete(tmpDst); } catch { }
                            throw;
                        }
                        catch (Exception ex)
                        {
                            lastErr = ex.Message;
                        }
                        finally
                        {
                            try { if (File.Exists(tmpDst)) File.Delete(tmpDst); } catch { }
                        }

                        if (attempt < retries)
                        {
                            Thread.Sleep(250);
                        }
                    }

                    if (lastErr is not null)
                    {
                        if (File.Exists(dst))
                        {
                            // Keep previous valid cached copy instead of losing the whole shard if WeChat held a transient lock
                            result.Reused.Add(rel);
                        }
                        result.Failed.Add((rel, lastErr));
                        progress?.Invoke($"[{idx}/{total}] 读取 {rel} 失败：{lastErr}");
                    }
                }

                SaveManifest(outputDir, manifest);
            }
            finally
            {
                try { Directory.Delete(snapDir, recursive: true); } catch { }
            }

            return result;
        }

        private static string ComputeSingleDbFingerprint(string srcDb)
        {
            try
            {
                var dbFi = new FileInfo(srcDb);
                long dbLen = dbFi.Exists ? dbFi.Length : 0;
                long dbTick = dbFi.Exists ? dbFi.LastWriteTimeUtc.Ticks : 0;

                string walPath = srcDb + "-wal";
                long walLen = 0, walTick = 0;
                if (File.Exists(walPath))
                {
                    var walFi = new FileInfo(walPath);
                    walLen = walFi.Length;
                    walTick = walFi.LastWriteTimeUtc.Ticks;
                }
                return $"{dbLen}:{dbTick}|{walLen}:{walTick}";
            }
            catch
            {
                return Guid.NewGuid().ToString("N");
            }
        }

        private static Dictionary<string, CacheManifestEntry> LoadManifest(string cacheDir)
        {
            try
            {
                string path = Path.Combine(cacheDir, "manifest.json");
                if (!File.Exists(path)) return new Dictionary<string, CacheManifestEntry>(StringComparer.OrdinalIgnoreCase);
                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, CacheManifestEntry>>(json);
                return loaded is not null
                    ? new Dictionary<string, CacheManifestEntry>(loaded, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, CacheManifestEntry>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, CacheManifestEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void SaveManifest(string cacheDir, Dictionary<string, CacheManifestEntry> manifest)
        {
            try
            {
                Directory.CreateDirectory(cacheDir);
                string path = Path.Combine(cacheDir, "manifest.json");
                File.WriteAllText(path, JsonSerializer.Serialize(manifest));
            }
            catch { }
        }

        public static DecryptBatchResult DecryptAll(
            string dbStorage,
            Dictionary<string, string> relPathToEncKeyHex,
            string outputDir,
            Action<string>? progress = null,
            int retries = 3,
            CancellationToken ct = default)
        {
            Directory.CreateDirectory(outputDir);
            string snapDir = Path.Combine(Path.GetTempPath(), "wx4snap_" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(snapDir);

            var result = new DecryptBatchResult { OutputDir = outputDir };
            try
            {
                int idx = 0;
                int total = relPathToEncKeyHex.Count;
                foreach (var kv in relPathToEncKeyHex.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    idx++;
                    string rel = kv.Key;
                    string src = Path.Combine(dbStorage, rel);
                    string dst = Path.Combine(outputDir, rel);
                    if (!File.Exists(src)) continue;

                    byte[] encKey = Convert.FromHexString(kv.Value);
                    string? lastErr = null;

                    for (int attempt = 1; attempt <= retries; attempt++)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                            string snap = CopySnapshot(src, snapDir);
                            var (pages, walFrames) = DecryptFile(snap, dst, encKey, applyWal: true, ct);
                            if (QuickCheck(dst))
                            {
                                result.Succeeded.Add(rel);
                                progress?.Invoke($"[{idx}/{total}] 解密 {rel} 完成（{pages} 页，WAL {walFrames} 帧）");
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

                        if (attempt < retries)
                        {
                            Thread.Sleep(300);
                        }
                    }

                    if (lastErr is not null)
                    {
                        result.Failed.Add((rel, lastErr));
                        progress?.Invoke($"[{idx}/{total}] 解密 {rel} 失败：{lastErr}");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(snapDir, recursive: true); } catch { }
            }

            return result;
        }

        private static string CopySnapshot(string srcDb, string snapDir)
        {
            string baseName = Path.GetFileName(srcDb);
            string dstDb = Path.Combine(snapDir, baseName);

            foreach (var suf in new[] { "-wal", "-shm" })
            {
                string stale = dstDb + suf;
                if (File.Exists(stale))
                {
                    try { File.Delete(stale); } catch { }
                }
            }

            CopySharedFile(srcDb, dstDb);
            foreach (var suf in new[] { "-wal", "-shm" })
            {
                string s = srcDb + suf;
                if (File.Exists(s))
                {
                    try { CopySharedFile(s, dstDb + suf); } catch { }
                }
            }
            return dstDb;
        }

        private static void CopySharedFile(string src, string dst)
        {
            using var fi = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 256 * 1024);
            using var fo = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024);
            fi.CopyTo(fo);
        }

        public static (int Pages, int WalFrames) DecryptFile(
            string src,
            string dst,
            byte[] encKey,
            bool applyWal = true,
            CancellationToken ct = default)
        {
            long fileLen = new FileInfo(src).Length;
            if (fileLen < WeChat4Crypto.PageSize)
            {
                throw new InvalidOperationException($"数据库文件过小：{Path.GetFileName(src)}");
            }

            int totalPages = (int)((fileLen + WeChat4Crypto.PageSize - 1) / WeChat4Crypto.PageSize);
            byte[] pageBuf = new byte[WeChat4Crypto.PageSize];

            using (var checkFs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int r = ReadExactOrPartial(checkFs, pageBuf);
                if (r < WeChat4Crypto.PageSize)
                {
                    throw new InvalidOperationException("无法读取第一页");
                }
                if (WeChat4Crypto.IsPlaintextSqlite(pageBuf))
                {
                    CopySharedFile(src, dst);
                    return (totalPages, 0);
                }
                if (!WeChat4Crypto.VerifyEncKey(encKey, pageBuf))
                {
                    throw new InvalidOperationException("密钥 HMAC-SHA512 校验失败");
                }
            }

            using (var aes = Aes.Create())
            {
                aes.Key = encKey;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;

                using var fi = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024);
                using var fo = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024);

                for (int pgno = 1; pgno <= totalPages; pgno++)
                {
                    if ((pgno & 0xFF) == 1) ct.ThrowIfCancellationRequested();
                    int read = ReadExactOrPartial(fi, pageBuf);
                    if (read <= 0) break;
                    byte[] dec = WeChat4Crypto.DecryptPage(aes, pageBuf.AsSpan(0, read), pgno);
                    fo.Write(dec, 0, dec.Length);
                }
            }

            int walFrames = 0;
            if (applyWal)
            {
                string walPath = src + "-wal";
                if (File.Exists(walPath) && new FileInfo(walPath).Length > WalHeaderSize)
                {
                    walFrames = MergeWal(dst, walPath, encKey, ct);
                }
            }

            return (totalPages, walFrames);
        }

        private static int MergeWal(string dbPath, string walPath, byte[] encKey, CancellationToken ct)
        {
            using var wal = new FileStream(walPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] header = new byte[WalHeaderSize];
            if (ReadExactOrPartial(wal, header) < WalHeaderSize) return 0;

            uint magic = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            uint pageSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
            if (magic != WalMagicLe && magic != WalMagicBe) return 0;
            if (pageSize != 0 && pageSize != WeChat4Crypto.PageSize) return 0;

            uint salt1 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4));
            uint salt2 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
            long walSize = wal.Length;

            byte[] frameHdr = new byte[WalFrameHeaderSize];
            long lastCommitPos = 0;
            uint committedPages = 0;

            while (wal.Position + WalFrameHeaderSize + WeChat4Crypto.PageSize <= walSize)
            {
                ct.ThrowIfCancellationRequested();
                if (ReadExactOrPartial(wal, frameHdr) < WalFrameHeaderSize) break;
                uint pgno = BinaryPrimitives.ReadUInt32BigEndian(frameHdr.AsSpan(0, 4));
                uint commit = BinaryPrimitives.ReadUInt32BigEndian(frameHdr.AsSpan(4, 4));
                uint fSalt1 = BinaryPrimitives.ReadUInt32BigEndian(frameHdr.AsSpan(8, 4));
                uint fSalt2 = BinaryPrimitives.ReadUInt32BigEndian(frameHdr.AsSpan(12, 4));
                wal.Seek(WeChat4Crypto.PageSize, SeekOrigin.Current);

                if (pgno == 0) break;
                if (fSalt1 == salt1 && fSalt2 == salt2 && commit > 0)
                {
                    lastCommitPos = wal.Position;
                    committedPages = commit;
                }
            }

            if (lastCommitPos == 0) return 0;

            wal.Seek(WalHeaderSize, SeekOrigin.Begin);
            byte[] pageBuf = new byte[WeChat4Crypto.PageSize];
            int applied = 0;

            using var aes = Aes.Create();
            aes.Key = encKey;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;

            using var dbFs = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            while (wal.Position < lastCommitPos)
            {
                ct.ThrowIfCancellationRequested();
                if (ReadExactOrPartial(wal, frameHdr) < WalFrameHeaderSize) break;
                if (ReadExactOrPartial(wal, pageBuf) < WeChat4Crypto.PageSize) break;

                uint pgno = BinaryPrimitives.ReadUInt32BigEndian(frameHdr.AsSpan(0, 4));
                uint fSalt1 = BinaryPrimitives.ReadUInt32BigEndian(frameHdr.AsSpan(8, 4));
                uint fSalt2 = BinaryPrimitives.ReadUInt32BigEndian(frameHdr.AsSpan(12, 4));

                if (fSalt1 != salt1 || fSalt2 != salt2 || pgno == 0 || pgno > committedPages)
                {
                    continue;
                }

                dbFs.Seek((long)(pgno - 1) * WeChat4Crypto.PageSize, SeekOrigin.Begin);
                byte[] dec = WeChat4Crypto.DecryptPage(aes, pageBuf, (int)pgno);
                dbFs.Write(dec, 0, dec.Length);
                applied++;
            }

            if (committedPages > 0)
            {
                dbFs.SetLength((long)committedPages * WeChat4Crypto.PageSize);
            }
            return applied;
        }

        private static bool QuickCheck(string dbPath)
        {
            try
            {
                var csb = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                };
                using var conn = new SqliteConnection(csb.ToString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA quick_check(1)";
                var res = cmd.ExecuteScalar()?.ToString();
                return string.Equals(res, "ok", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static int ReadExactOrPartial(Stream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int n = stream.Read(buffer, total, buffer.Length - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }
}
