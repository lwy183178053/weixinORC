using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Client.Services.WeChat4Db
{
    /// <summary>
    /// SQLCipher 4 cryptographic primitives used by Windows WeChat 4.x databases.
    /// Page size 4096, PBKDF2-HMAC-SHA512 (256000 iters), HMAC-SHA512, reserve 80 bytes (IV 16 + HMAC 64).
    /// Ported from Moect wx4/common.py &amp; wx4/decrypt.py.
    /// </summary>
    public static class WeChat4Crypto
    {
        public const int PageSize = 4096;
        public const int KeySize = 32;
        public const int SaltSize = 16;
        public const int IvSize = 16;
        public const int HmacSize = 64;
        public const int ReserveSize = 80; // IV (16) + HMAC (64)
        public const int KdfIterations = 256000;

        public static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

        public static bool IsPlaintextSqlite(ReadOnlySpan<byte> page1) =>
            page1.Length >= 16 && page1[..16].SequenceEqual(SqliteHeader);

        public static byte[] DeriveEncKey(byte[] passphrase, byte[] salt) =>
            Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, KdfIterations, HashAlgorithmName.SHA512, KeySize);

        public static byte[] ComputePage1Hmac(byte[] encKey, ReadOnlySpan<byte> page1)
        {
            Span<byte> macSalt = stackalloc byte[SaltSize];
            for (int i = 0; i < SaltSize; i++)
            {
                macSalt[i] = (byte)(page1[i] ^ 0x3A);
            }

            byte[] macKey = Rfc2898DeriveBytes.Pbkdf2(encKey, macSalt, 2, HashAlgorithmName.SHA512, KeySize);

            int dataLen = PageSize - ReserveSize + IvSize - SaltSize;
            Span<byte> pgnoLe = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(pgnoLe, 1);

            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA512, macKey);
            hmac.AppendData(page1.Slice(SaltSize, dataLen));
            hmac.AppendData(pgnoLe);
            return hmac.GetHashAndReset();
        }

        public static bool VerifyEncKey(byte[] encKey, ReadOnlySpan<byte> page1)
        {
            if (encKey.Length != KeySize || page1.Length < PageSize)
            {
                return false;
            }
            byte[] computed = ComputePage1Hmac(encKey, page1);
            return CryptographicOperations.FixedTimeEquals(
                computed,
                page1.Slice(PageSize - HmacSize, HmacSize));
        }

        public static byte[] DecryptPage(Aes aes, ReadOnlySpan<byte> page, int pgno)
        {
            byte[] padded;
            ReadOnlySpan<byte> src = page;
            if (page.Length < PageSize)
            {
                padded = new byte[PageSize];
                page.CopyTo(padded);
                src = padded;
            }

            ReadOnlySpan<byte> iv = src.Slice(PageSize - ReserveSize, IvSize);
            byte[] output = new byte[PageSize];

            if (pgno == 1)
            {
                SqliteHeader.CopyTo(output, 0);
                ReadOnlySpan<byte> body = src.Slice(SaltSize, PageSize - ReserveSize - SaltSize);
                aes.DecryptCbc(body, iv, output.AsSpan(SaltSize, body.Length), PaddingMode.None);
            }
            else
            {
                ReadOnlySpan<byte> body = src[..(PageSize - ReserveSize)];
                aes.DecryptCbc(body, iv, output.AsSpan(0, body.Length), PaddingMode.None);
            }

            return output;
        }
    }
}
