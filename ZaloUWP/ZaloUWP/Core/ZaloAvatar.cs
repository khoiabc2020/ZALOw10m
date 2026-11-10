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
    /// <summary>Round avatars: a coloured circle with the first letter, with the picture laid over it when there is one.</summary>
    public static class ZaloAvatar
    {
        private static readonly byte[][] Palette =
        {
            new byte[] { 0x0A, 0x7C, 0xFF }, new byte[] { 0x00, 0xA8, 0x84 }, new byte[] { 0xF2, 0x99, 0x4A },
            new byte[] { 0x9B, 0x51, 0xE0 }, new byte[] { 0xE5, 0x53, 0x3D }, new byte[] { 0x2D, 0x9C, 0xDB },
            new byte[] { 0x21, 0x96, 0x53 }, new byte[] { 0xEB, 0x57, 0x57 }
        };

        public static Brush ColorFor(string name)
        {
            int hash = 0;
            foreach (char c in name ?? string.Empty) hash = hash * 31 + c;
            byte[] rgb = Palette[(hash & 0x7FFFFFFF) % Palette.Length];
            return new SolidColorBrush(Color.FromArgb(0xFF, rgb[0], rgb[1], rgb[2]));
        }

        public static string InitialOf(string name)
        {
            string text = (name ?? string.Empty).Trim();
            if (text.Length == 0) return "?";
            int length = char.IsHighSurrogate(text[0]) && text.Length > 1 ? 2 : 1;
            return text.Substring(0, length).ToUpperInvariant();
        }

        /// <summary>The picture as a brush, or null when there is no usable address.</summary>
        public static Brush ImageFor(string url)
        {
            Uri uri;
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out uri)
                && (uri.Scheme == "https" || uri.Scheme == "http"))
            {
                // Avatars render at 40–56 device-independent pixels. Decoding the server's
                // full-resolution photo for each visible row wastes memory on ARM phones.
                var bitmap = new BitmapImage { DecodePixelWidth = 96 };
                bitmap.UriSource = uri;
                return new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
            }
            return null;
        }
    }

}
