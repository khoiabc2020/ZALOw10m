using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace ZaloLite
{
    /// <summary>Round avatars: a coloured circle with the first letter, with cached picture laid over it.</summary>
    public static class ZaloAvatar
    {
        private static readonly byte[][] Palette =
        {
            new byte[] { 0x0A, 0x7C, 0xFF }, new byte[] { 0x00, 0xA8, 0x84 }, new byte[] { 0xF2, 0x99, 0x4A },
            new byte[] { 0x9B, 0x51, 0xE0 }, new byte[] { 0xE5, 0x53, 0x3D }, new byte[] { 0x2D, 0x9C, 0xDB },
            new byte[] { 0x21, 0x96, 0x53 }, new byte[] { 0xEB, 0x57, 0x57 }
        };

        private static readonly Dictionary<string, BitmapImage> _bitmapCache = new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Brush> _brushCache = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _downloadingSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _diskCachedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new object();
        private static bool _diskIndexLoaded = false;
        private static bool _diskIndexLoading = false;

        static ZaloAvatar()
        {
            _ = EnsureDiskIndexLoadedAsync();
        }

        public static async Task EnsureDiskIndexLoadedAsync()
        {
            lock (_cacheLock)
            {
                if (_diskIndexLoaded || _diskIndexLoading) return;
                _diskIndexLoading = true;
            }

            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var avaFolder = await folder.CreateFolderAsync("avatars", CreationCollisionOption.OpenIfExists);
                var files = await avaFolder.GetFilesAsync();
                lock (_cacheLock)
                {
                    foreach (var f in files)
                    {
                        _diskCachedSet.Add(f.Name);
                    }
                    _diskIndexLoaded = true;
                }
            }
            catch { }
            finally
            {
                lock (_cacheLock)
                {
                    _diskIndexLoading = false;
                }
            }
        }

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

        private static string GetAvatarFileName(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            uint hash = 5381;
            foreach (char c in url) hash = ((hash << 5) + hash) + c;
            return "ava_" + hash.ToString("x8") + ".jpg";
        }

        public static BitmapImage BitmapFor(string url, int decodeWidth = 96)
        {
            if (string.IsNullOrEmpty(url)) return null;

            lock (_cacheLock)
            {
                BitmapImage cached;
                if (_bitmapCache.TryGetValue(url, out cached))
                {
                    return cached;
                }
            }

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http" && uri.Scheme != "ms-appx" && uri.Scheme != "ms-appdata"))
            {
                return null;
            }

            string fileName = GetAvatarFileName(url);
            bool onDisk = false;
            lock (_cacheLock)
            {
                onDisk = _diskCachedSet.Contains(fileName);
            }

            BitmapImage bmp = null;
            try
            {
                if (onDisk)
                {
                    bmp = new BitmapImage(new Uri("ms-appdata:///local/avatars/" + fileName)) { DecodePixelWidth = decodeWidth };
                }
                else
                {
                    bmp = new BitmapImage(uri) { DecodePixelWidth = decodeWidth };
                    if (uri.Scheme == "https" || uri.Scheme == "http")
                    {
                        _ = CacheAvatarToDiskAsync(url, fileName);
                    }
                }
            }
            catch { }

            if (bmp != null)
            {
                lock (_cacheLock)
                {
                    if (_bitmapCache.Count > 500) _bitmapCache.Clear();
                    _bitmapCache[url] = bmp;
                }
            }

            return bmp;
        }

        private static async Task CacheAvatarToDiskAsync(string url, string fileName)
        {
            lock (_cacheLock)
            {
                if (_downloadingSet.Contains(url)) return;
                _downloadingSet.Add(url);
            }

            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var avaFolder = await folder.CreateFolderAsync("avatars", CreationCollisionOption.OpenIfExists);
                var existing = await avaFolder.TryGetItemAsync(fileName);
                if (existing != null)
                {
                    lock (_cacheLock) { _diskCachedSet.Add(fileName); }
                    return;
                }

                byte[] bytes = null;
                using (var client = new System.Net.Http.HttpClient())
                {
                    bytes = await client.GetByteArrayAsync(url);
                }

                if (bytes != null && bytes.Length > 0)
                {
                    var file = await avaFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteBytesAsync(file, bytes);
                    lock (_cacheLock)
                    {
                        _diskCachedSet.Add(fileName);
                    }
                }
            }
            catch { }
            finally
            {
                lock (_cacheLock)
                {
                    _downloadingSet.Remove(url);
                }
            }
        }

        /// <summary>The picture as a brush, or null when there is no usable address.</summary>
        public static Brush ImageFor(string url, int decodeWidth = 96)
        {
            if (string.IsNullOrEmpty(url)) return null;

            lock (_cacheLock)
            {
                Brush cached;
                if (_brushCache.TryGetValue(url, out cached))
                {
                    return cached;
                }
            }

            var bmp = BitmapFor(url, decodeWidth);
            if (bmp != null)
            {
                var brush = new ImageBrush { ImageSource = bmp, Stretch = Stretch.UniformToFill };
                lock (_cacheLock)
                {
                    if (_brushCache.Count > 500) _brushCache.Clear();
                    _brushCache[url] = brush;
                }
                return brush;
            }
            return null;
        }
    }
}
