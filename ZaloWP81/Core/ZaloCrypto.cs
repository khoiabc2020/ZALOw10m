using System;
using System.Text;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage.Streams;

namespace ZaloUWP.Core
{
    public static class ZaloCrypto
    {
        private static readonly byte[] ZeroIv = new byte[16];

        public static byte[] NormalizeKey(string keyString)
        {
            if (string.IsNullOrEmpty(keyString))
                return new byte[16];

            try
            {
                // Try Base64 decode first
                var bytes = Convert.FromBase64String(keyString);
                if (bytes.Length == 16 || bytes.Length == 24 || bytes.Length == 32)
                    return bytes;
            }
            catch
            {
                // Fallback to UTF-8
            }

            var utf8 = Encoding.UTF8.GetBytes(keyString);
            if (utf8.Length == 16 || utf8.Length == 24 || utf8.Length == 32)
                return utf8;

            // Pad or truncate to 32 bytes (AES-256) or 16 bytes (AES-128)
            var normalized = new byte[32];
            Array.Copy(utf8, normalized, Math.Min(utf8.Length, 32));
            return normalized;
        }

        public static string EncryptAesCbc(string plainText, string secretKey)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;

            var keyBytes = NormalizeKey(secretKey);
            var provider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesCbcPkcs7);
            var symKey = provider.CreateSymmetricKey(CryptographicBuffer.CreateFromByteArray(keyBytes));
            var plainBuf = CryptographicBuffer.ConvertStringToBinary(plainText, BinaryStringEncoding.Utf8);
            var ivBuf = CryptographicBuffer.CreateFromByteArray(ZeroIv);
            var cipherBuf = CryptographicEngine.Encrypt(symKey, plainBuf, ivBuf);
            return CryptographicBuffer.EncodeToBase64String(cipherBuf);
        }

        public static string DecryptAesCbc(string cipherTextBase64, string secretKey)
        {
            if (string.IsNullOrEmpty(cipherTextBase64))
                return string.Empty;

            var keyBytes = NormalizeKey(secretKey);
            var provider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesCbcPkcs7);
            var symKey = provider.CreateSymmetricKey(CryptographicBuffer.CreateFromByteArray(keyBytes));
            var cipherBuf = CryptographicBuffer.DecodeFromBase64String(cipherTextBase64);
            var ivBuf = CryptographicBuffer.CreateFromByteArray(ZeroIv);
            var plainBuf = CryptographicEngine.Decrypt(symKey, cipherBuf, ivBuf);
            return CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, plainBuf);
        }

        public static string ComputeMd5(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            var provider = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Md5);
            var buf = CryptographicBuffer.ConvertStringToBinary(input, BinaryStringEncoding.Utf8);
            var hash = provider.HashData(buf);
            return CryptographicBuffer.EncodeToHexString(hash).ToLowerInvariant();
        }

        public static string ComputeSha256(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            var provider = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
            var buf = CryptographicBuffer.ConvertStringToBinary(input, BinaryStringEncoding.Utf8);
            var hash = provider.HashData(buf);
            return CryptographicBuffer.EncodeToHexString(hash).ToLowerInvariant();
        }

        public static string ComputeSignKey(string endpoint, string paramStr, long timestamp, string secret)
        {
            var raw = endpoint + paramStr + timestamp + secret;
            return ComputeMd5(raw);
        }
    }
}
