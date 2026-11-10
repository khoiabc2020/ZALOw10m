using System;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace System
{
    public static class WP81DateTimeExtensions
    {
        private static readonly DateTimeOffset UnixEpoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public static long ToUnixTimeMilliseconds(this DateTimeOffset dto)
        {
            return (long)(dto.ToUniversalTime() - UnixEpoch).TotalMilliseconds;
        }

        public static long ToUnixTimeSeconds(this DateTimeOffset dto)
        {
            return (long)(dto.ToUniversalTime() - UnixEpoch).TotalSeconds;
        }
    }

    public static class UnixTime
    {
        private static readonly DateTimeOffset UnixEpoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public static DateTimeOffset FromMilliseconds(long ms)
        {
            return UnixEpoch.AddMilliseconds(ms);
        }

        public static DateTimeOffset FromSeconds(long seconds)
        {
            return UnixEpoch.AddSeconds(seconds);
        }

        public static DateTime ToDateTime(long ms)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime();
        }
    }
}

namespace Windows.Storage
{
    public static class StorageFolderExtensions
    {
        public static async Task<IStorageItem> TryGetItemAsync(this StorageFolder folder, string name)
        {
            if (folder == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                return await folder.GetItemAsync(name);
            }
            catch
            {
                return null;
            }
        }
    }
}

namespace Windows.Data.Json
{
    public static class JsonValueExtensions
    {
        public static JsonValue CreateNullValue()
        {
            return JsonValue.Parse("null");
        }
    }
}
