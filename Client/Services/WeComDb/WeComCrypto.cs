using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Client.Services.WeComDb
{
    /// <summary>
    /// Exact wxSQLite3 / SQLite3MultipleCiphers AES-128-CBC page decryption used by Windows WeCom (WXWork 4.x/5.x).
    /// Matches cipher_wxaes128.c &amp; codec_algos.c:
    ///   - Per-page key: MD5(rawKey[16] || littleEndian(pageNo) || "sAlT")
    ///   - Per-page IV : MD5(4x MODMULT(52774, 40692, 3791, 2147483399, seed=pageNo+1))
    ///   - Page 1 layout: bytes[16..23] unencrypted SQLite header; bytes[8..15] hold encrypted bytes[16..23];
    ///                    bytes[0..7] hold first 8 bytes of AES-CBC(pageKey1, iv1, "SQLite format 3\0").
    /// </summary>
    public static class WeComCrypto
    {
        public const int DefaultPageSize = 4096;
        public const int KeySize = 16;

        public static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();
        private static readonly byte[] SaltLiteral = "sAlT"u8.ToArray();
        public static readonly byte[] IvPage1 = GenerateInitialVector(1);

        public static bool IsPlaintextSqlite(ReadOnlySpan<byte> page1) =>
            page1.Length >= 16 && page1[..16].SequenceEqual(SqliteHeader);

        public static bool LooksLikeWxAes128Page1(ReadOnlySpan<byte> page1)
        {
            if (page1.Length < 32 || IsPlaintextSqlite(page1)) return false;
            int ps = (page1[16] << 8) | (page1[17] << 16);
            return ps >= 512 && ps <= 65536 && ((ps - 1) & ps) == 0 &&
                   page1[21] == 0x40 && page1[22] == 0x20 && page1[23] == 0x20;
        }

        public static int ReadPageSize(ReadOnlySpan<byte> page1)
        {
            if (page1.Length < 24) return DefaultPageSize;
            int ps = (page1[16] << 8) | (page1[17] << 16);
            if (ps >= 512 && ps <= 65536 && ((ps - 1) & ps) == 0) return ps;
            return DefaultPageSize;
        }

        public static byte[] GenerateInitialVector(int pageNo)
        {
            Span<byte> initKey = stackalloc byte[16];
            long z = unchecked((int)((uint)pageNo + 1U));
            for (int j = 0; j < 4; j++)
            {
                long q = z / 52774;
                z = 40692 * (z - 52774 * q) - 3791 * q;
                if (z < 0) z += 2147483399L;
                BinaryPrimitives.WriteUInt32LittleEndian(initKey.Slice(j * 4, 4), unchecked((uint)z));
            }
            return MD5.HashData(initKey);
        }

        public static byte[] DerivePageKey(ReadOnlySpan<byte> rawKey16, int pageNo)
        {
            Span<byte> nkey = stackalloc byte[24];
            rawKey16[..KeySize].CopyTo(nkey);
            BinaryPrimitives.WriteInt32LittleEndian(nkey.Slice(16, 4), pageNo);
            SaltLiteral.CopyTo(nkey.Slice(20, 4));
            return MD5.HashData(nkey);
        }

        /// <summary>
        /// Zero-false-positive 128-bit double verification of a candidate 16-byte raw key against Page 1 head (32 bytes):
        /// 1. AES-128-CBC(pk1, iv1, "SQLite format 3\0")[0..7] == page1[0..7]
        /// 2. AES-128-CBC-Decrypt(pk1, iv1, page1[8..15] || page1[24..31])[0..7] == page1[16..23]
        /// </summary>
        public static bool VerifyKeyAgainstPage1(ReadOnlySpan<byte> rawKey16, ReadOnlySpan<byte> page1Head32)
        {
            if (rawKey16.Length != KeySize || page1Head32.Length < 32) return false;

            byte[] pk1 = DerivePageKey(rawKey16, 1);
            using var aes = Aes.Create();
            aes.KeySize = 128;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = pk1;

            Span<byte> c0 = stackalloc byte[16];
            aes.EncryptCbc(SqliteHeader, IvPage1, c0, PaddingMode.None);
            if (!c0[..8].SequenceEqual(page1Head32[..8]))
            {
                return false;
            }

            Span<byte> c1 = stackalloc byte[16];
            page1Head32.Slice(8, 8).CopyTo(c1[..8]);
            page1Head32.Slice(24, 8).CopyTo(c1.Slice(8, 8));

            Span<byte> p1 = stackalloc byte[16];
            aes.DecryptCbc(c1, IvPage1, p1, PaddingMode.None);
            return p1[..8].SequenceEqual(page1Head32.Slice(16, 8));
        }

        public static byte[] DecryptPage(ReadOnlySpan<byte> encryptedPage, int pageNo, ReadOnlySpan<byte> rawKey16)
        {
            int len = encryptedPage.Length;
            byte[] pk = DerivePageKey(rawKey16, pageNo);
            byte[] iv = pageNo == 1 ? IvPage1 : GenerateInitialVector(pageNo);

            using var aes = Aes.Create();
            aes.KeySize = 128;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = pk;

            if (pageNo == 1)
            {
                byte[] dbHeader = encryptedPage.Slice(16, 8).ToArray();
                byte[] bodyIn = new byte[len - 16];
                encryptedPage.Slice(8, 8).CopyTo(bodyIn.AsSpan(0, 8));
                encryptedPage.Slice(24).CopyTo(bodyIn.AsSpan(8));

                byte[] bodyOut = new byte[bodyIn.Length];
                aes.DecryptCbc(bodyIn, iv, bodyOut, PaddingMode.None);
                if (!bodyOut.AsSpan(0, 8).SequenceEqual(dbHeader))
                {
                    throw new CryptographicException("WeCom wxSQLite3 Page 1 header verification failed.");
                }

                byte[] pageOut = new byte[len];
                SqliteHeader.CopyTo(pageOut, 0);
                bodyOut.CopyTo(pageOut, 16);
                return pageOut;
            }
            else
            {
                byte[] pageOut = new byte[len];
                aes.DecryptCbc(encryptedPage, iv, pageOut, PaddingMode.None);
                return pageOut;
            }
        }
    }
}
