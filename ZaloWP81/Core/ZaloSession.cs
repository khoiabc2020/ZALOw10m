using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace ZaloUWP.Core
{
    public class ZaloSession
    {
        private static ZaloSession _current;
        public static ZaloSession Current => _current ?? (_current = new ZaloSession());

        public string UserId { get; set; }
        public string DisplayName { get; set; }
        public string AvatarUrl { get; set; }
        public string PhoneNumber { get; set; }
        public string ZpwSek { get; set; }
        public string ZpsId { get; set; }
        public string ZpwEnk { get; set; }
        public string Imei { get; set; }
        public string WebSocketUrl { get; set; }
        public Dictionary<string, string> ServiceMap { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Phiên đăng nhập hợp lệ: Đã có zpw_sek hoặc đã có UserId thật từ phiên trước
        /// Không bao giờ bắt quét lại QR khi chuyển đổi đa nhiệm hoặc mở lại app!
        /// </summary>
        public bool IsLoggedIn => !string.IsNullOrEmpty(ZpwSek) || (!string.IsNullOrEmpty(UserId) && UserId != "me" && UserId.Length > 2);

        public ZaloSession()
        {
            Load();
            if (string.IsNullOrEmpty(Imei))
            {
                Imei = Guid.NewGuid().ToString("D");
                Save();
            }
        }

        public string GetCookieHeader()
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(ZpwSek))
                parts.Add("zpw_sek=" + ZpwSek);
            if (!string.IsNullOrEmpty(ZpsId))
                parts.Add("zpsid=" + ZpsId);
            if (!string.IsNullOrEmpty(UserId))
                parts.Add("uid=" + UserId);

            return string.Join("; ", parts);
        }

        public static string CleanUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            var trimmed = url.Trim();

            // Strip brackets, quotes, newlines, commas
            trimmed = trimmed.Trim('[', ']', '"', '\'', '\r', '\n', ' ', ',');
            int quoteIdx = trimmed.IndexOfAny(new[] { '"', '\'' });
            if (quoteIdx > 0) trimmed = trimmed.Substring(0, quoteIdx).Trim();
            int commaIdx = trimmed.IndexOf(',');
            if (commaIdx > 0) trimmed = trimmed.Substring(0, commaIdx).Trim();

            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "https://" + trimmed;
            }

            return trimmed.TrimEnd('/');
        }

        public string GetServiceUrl(string serviceName, string fallbackUrl = "")
        {
            if (ServiceMap != null && ServiceMap.TryGetValue(serviceName, out var url) && !string.IsNullOrWhiteSpace(url))
            {
                return CleanUrl(url);
            }
            return CleanUrl(fallbackUrl);
        }

        public void UpdateFromLogin(string zpwSek, string zpsId, string zpwEnk, string uid, Dictionary<string, string> serviceMap)
        {
            if (!string.IsNullOrEmpty(zpwSek)) ZpwSek = zpwSek;
            if (!string.IsNullOrEmpty(zpsId)) ZpsId = zpsId;
            if (!string.IsNullOrEmpty(zpwEnk)) ZpwEnk = zpwEnk;
            if (!string.IsNullOrEmpty(uid)) UserId = uid;
            if (serviceMap != null && serviceMap.Count > 0)
            {
                foreach (var kv in serviceMap)
                {
                    ServiceMap[kv.Key] = CleanUrl(kv.Value);
                }
            }
            Save();
        }

        public void Save()
        {
            try
            {
                // 1. Lưu vào LocalSettings
                var settings = ApplicationData.Current.LocalSettings.Values;
                settings["UserId"] = UserId ?? "";
                settings["DisplayName"] = DisplayName ?? "";
                settings["AvatarUrl"] = AvatarUrl ?? "";
                settings["PhoneNumber"] = PhoneNumber ?? "";
                settings["ZpwSek"] = ZpwSek ?? "";
                settings["ZpsId"] = ZpsId ?? "";
                settings["ZpwEnk"] = ZpwEnk ?? "";
                settings["Imei"] = Imei ?? "";
                settings["WebSocketUrl"] = WebSocketUrl ?? "";

                var o = new JsonObject();
                o["UserId"] = JsonValue.CreateStringValue(UserId ?? "");
                o["DisplayName"] = JsonValue.CreateStringValue(DisplayName ?? "");
                o["AvatarUrl"] = JsonValue.CreateStringValue(AvatarUrl ?? "");
                o["PhoneNumber"] = JsonValue.CreateStringValue(PhoneNumber ?? "");
                o["ZpwSek"] = JsonValue.CreateStringValue(ZpwSek ?? "");
                o["ZpsId"] = JsonValue.CreateStringValue(ZpsId ?? "");
                o["ZpwEnk"] = JsonValue.CreateStringValue(ZpwEnk ?? "");
                o["Imei"] = JsonValue.CreateStringValue(Imei ?? "");
                o["WebSocketUrl"] = JsonValue.CreateStringValue(WebSocketUrl ?? "");

                if (ServiceMap != null)
                {
                    var mapObj = new JsonObject();
                    foreach (var kv in ServiceMap)
                    {
                        mapObj[kv.Key] = JsonValue.CreateStringValue(kv.Value ?? "");
                    }
                    settings["ServiceMap"] = mapObj.Stringify();
                    o["ServiceMap"] = mapObj;
                }

                // 2. Ghi trực tiếp file zalo_session.json vào LocalFolder
                string content = o.Stringify();
                var ignored = ApplicationData.Current.LocalFolder.CreateFileAsync("zalo_session.json", CreationCollisionOption.ReplaceExisting)
                    .AsTask().ContinueWith(async t =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                        {
                            await FileIO.WriteTextAsync(t.Result, content);
                        }
                    });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ZaloSession] Save error: " + ex.Message);
            }
        }

        public void Load()
        {
            try
            {
                // Thử đọc từ LocalSettings
                var settings = ApplicationData.Current.LocalSettings.Values;
                if (settings.ContainsKey("ZpwSek") || settings.ContainsKey("UserId"))
                {
                    UserId = settings["UserId"] as string;
                    DisplayName = settings["DisplayName"] as string;
                    AvatarUrl = settings["AvatarUrl"] as string;
                    PhoneNumber = settings["PhoneNumber"] as string;
                    ZpwSek = settings["ZpwSek"] as string;
                    ZpsId = settings["ZpsId"] as string;
                    ZpwEnk = settings["ZpwEnk"] as string;
                    Imei = settings["Imei"] as string;
                    WebSocketUrl = settings["WebSocketUrl"] as string;

                    if (settings.ContainsKey("ServiceMap"))
                    {
                        var mapJson = settings["ServiceMap"] as string;
                        if (!string.IsNullOrEmpty(mapJson))
                        {
                            try
                            {
                                JsonObject parsed;
                                if (JsonObject.TryParse(mapJson, out parsed))
                                {
                                    ServiceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                    foreach (var key in parsed.Keys)
                                    {
                                        ServiceMap[key] = CleanUrl(parsed.GetNamedString(key, ""));
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ZaloSession] Load error: " + ex.Message);
            }
        }

        public void Clear()
        {
            UserId = null;
            DisplayName = null;
            AvatarUrl = null;
            PhoneNumber = null;
            ZpwSek = null;
            ZpsId = null;
            ZpwEnk = null;
            WebSocketUrl = null;
            ServiceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var settings = ApplicationData.Current.LocalSettings.Values;
            settings.Remove("UserId");
            settings.Remove("DisplayName");
            settings.Remove("AvatarUrl");
            settings.Remove("PhoneNumber");
            settings.Remove("ZpwSek");
            settings.Remove("ZpsId");
            settings.Remove("ZpwEnk");
            settings.Remove("WebSocketUrl");
            settings.Remove("ServiceMap");

            try
            {
                var ignored = ApplicationData.Current.LocalFolder.TryGetItemAsync("zalo_session.json")
                    .ContinueWith(t => (t.Result as StorageFile)?.DeleteAsync());
            }
            catch { }
        }
    }
}
