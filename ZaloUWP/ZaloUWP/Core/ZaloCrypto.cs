using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

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

            using (var aes = Aes.Create())
            {
                aes.Key = keyBytes;
                aes.IV = ZeroIv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var encryptor = aes.CreateEncryptor())
                {
                    var plainBytes = Encoding.UTF8.GetBytes(plainText);
                    var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                    return Convert.ToBase64String(cipherBytes);
                }
            }
        }

        public static string DecryptAesCbc(string cipherTextBase64, string secretKey)
        {
            if (string.IsNullOrEmpty(cipherTextBase64))
                return string.Empty;

            var keyBytes = NormalizeKey(secretKey);

            using (var aes = Aes.Create())
            {
                aes.Key = keyBytes;
                aes.IV = ZeroIv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var decryptor = aes.CreateDecryptor())
                {
                    var cipherBytes = Convert.FromBase64String(cipherTextBase64);
                    var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                    return Encoding.UTF8.GetString(plainBytes);
                }
            }
        }

        public static string ComputeMd5(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            using (var md5 = MD5.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(input);
                var hash = md5.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        public static string ComputeSha256(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(input);
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        public static string ComputeSignKey(string endpoint, string paramStr, long timestamp, string secret)
        {
            // Standard Zalo Web API sign calculation: md5(endpoint + param + timestamp + secret)
            var raw = $"{endpoint}{paramStr}{timestamp}{secret}";
            return ComputeMd5(raw);
        }
    }
}

