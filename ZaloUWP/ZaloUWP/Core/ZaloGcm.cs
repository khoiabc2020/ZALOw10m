using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Networking.Sockets;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Web.Http;
using ZaloUWP.Services;

namespace ZaloLite
{
    /// <summary>
    /// AES-GCM decryption with a 16-byte IV, written out by hand because the phone's crypto library
    /// only accepts 12-byte IVs. The authentication tag is not checked (the data already arrived over TLS).
    /// </summary>
    public static class ZaloGcm
    {
        public static byte[] Decrypt(byte[] key, byte[] iv, byte[] cipher)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.Key = key;
                using (ICryptoTransform enc = aes.CreateEncryptor())
                {
                    byte[] h = EncryptBlock(enc, new byte[16]);
                    byte[] counter;
                    if (iv.Length == 12)
                    {
                        counter = new byte[16];
                        Array.Copy(iv, counter, 12);
                        counter[15] = 1;
                    }
                    else
                    {
                        counter = GHashIv(h, iv);
                    }

                    byte[] output = new byte[cipher.Length];
                    for (int offset = 0; offset < cipher.Length; offset += 16)
                    {
                        Increment(counter);
                        byte[] keyStream = EncryptBlock(enc, counter);
                        int n = Math.Min(16, cipher.Length - offset);
                        for (int k = 0; k < n; k++) output[offset + k] = (byte)(cipher[offset + k] ^ keyStream[k]);
                    }
                    return output;
                }
            }
        }

        private static byte[] EncryptBlock(ICryptoTransform enc, byte[] input)
        {
            byte[] output = new byte[16];
            enc.TransformBlock(input, 0, 16, output, 0);
            return output;
        }

        private static void Increment(byte[] counter)
        {
            for (int k = 15; k >= 12; k--)
            {
                counter[k]++;
                if (counter[k] != 0) break;
            }
        }

        private static byte[] GHashIv(byte[] h, byte[] iv)
        {
            int padded = (iv.Length + 15) / 16 * 16;
            byte[] x = new byte[padded + 16];
            Array.Copy(iv, x, iv.Length);
            ulong bits = (ulong)iv.Length * 8UL;
            for (int k = 0; k < 8; k++) x[x.Length - 1 - k] = (byte)(bits >> (8 * k));

            byte[] y = new byte[16];
            for (int offset = 0; offset < x.Length; offset += 16)
            {
                for (int k = 0; k < 16; k++) y[k] ^= x[offset + k];
                y = Multiply(y, h);
            }
            return y;
        }

        // multiplication in GF(2^128) as defined for GCM
        private static byte[] Multiply(byte[] x, byte[] y)
        {
            byte[] z = new byte[16];
            byte[] v = (byte[])y.Clone();
            for (int i = 0; i < 128; i++)
            {
                if (((x[i / 8] >> (7 - (i % 8))) & 1) == 1)
                    for (int k = 0; k < 16; k++) z[k] ^= v[k];

                bool lsb = (v[15] & 1) == 1;
                for (int k = 15; k > 0; k--) v[k] = (byte)((v[k] >> 1) | (v[k - 1] << 7));
                v[0] = (byte)(v[0] >> 1);
                if (lsb) v[0] ^= 0xE1;
            }
            return z;
        }
    }



}
