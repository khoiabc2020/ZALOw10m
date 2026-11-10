using System;
using System.Collections.Generic;
using System.IO;
using Windows.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
                parts.Add($"zpw_sek={ZpwSek}");
            if (!string.IsNullOrEmpty(ZpsId))
                parts.Add($"zpsid={ZpsId}");
            if (!string.IsNullOrEmpty(UserId))
                parts.Add($"uid={UserId}");

            return string.Join("; ", parts);
        }

        public static string CleanUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            var trimmed = url.Trim();

            // Strip brackets, quotes, newlines, commas
            trimmed = trimmed.Trim('[', ']', '"', '\'', ' ', '\r', '\n', '\t');
            if (trimmed.Contains(","))
            {
                trimmed = trimmed.Split(',')[0].Trim('[', ']', '"', '\'', ' ', '\r', '\n', '\t');
            }
            trimmed = trimmed.TrimEnd('/');

            if (!string.IsNullOrEmpty(trimmed) &&
                !trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
                !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "https://" + trimmed;
            }
            return trimmed;
        }

        public string GetServiceUrl(string serviceName, string fallbackUrl)
        {
            if (ServiceMap != null && ServiceMap.TryGetValue(serviceName, out var url) && !string.IsNullOrWhiteSpace(url))
            {
                var clean = CleanUrl(url);
                if (!string.IsNullOrEmpty(clean)) return clean;
            }

            // Defaults based on known Zalo clusters
            if (serviceName.Equals("group_poll", StringComparison.OrdinalIgnoreCase) ||
                serviceName.Equals("group", StringComparison.OrdinalIgnoreCase))
            {
                if (ServiceMap != null && ServiceMap.TryGetValue("group", out var gUrl) && !string.IsNullOrWhiteSpace(gUrl))
                    return CleanUrl(gUrl);
                return "https://group-wpa.chat.zalo.me";
            }
            if (serviceName.Equals("profile", StringComparison.OrdinalIgnoreCase) ||
                serviceName.Equals("friend", StringComparison.OrdinalIgnoreCase))
            {
                return "https://profile-wpa.chat.zalo.me";
            }
            if (serviceName.Equals("chat", StringComparison.OrdinalIgnoreCase) ||
                serviceName.Equals("conversation", StringComparison.OrdinalIgnoreCase))
            {
                if (ServiceMap != null && ServiceMap.TryGetValue("chat", out var cUrl) && !string.IsNullOrWhiteSpace(cUrl))
                    return CleanUrl(cUrl);
                return "https://tt-chat1-wpa.chat.zalo.me";
            }

            return CleanUrl(fallbackUrl);
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
                if (ServiceMap != null)
                {
                    settings["ServiceMap"] = JsonConvert.SerializeObject(ServiceMap);
                }

                // 2. Đồng thời ghi trực tiếp file zalo_session.json vào LocalFolder để không bao giờ mất khi tắt đa nhiệm
                var sessionModel = new
                {
                    UserId = UserId ?? "",
                    DisplayName = DisplayName ?? "",
                    AvatarUrl = AvatarUrl ?? "",
                    PhoneNumber = PhoneNumber ?? "",
                    ZpwSek = ZpwSek ?? "",
                    ZpsId = ZpsId ?? "",
                    ZpwEnk = ZpwEnk ?? "",
                    Imei = Imei ?? "",
                    WebSocketUrl = WebSocketUrl ?? "",
                    ServiceMap = ServiceMap ?? new Dictionary<string, string>()
                };
                var json = JsonConvert.SerializeObject(sessionModel, Formatting.Indented);
                var folderPath = ApplicationData.Current.LocalFolder.Path;
                var filePath = Path.Combine(folderPath, "zalo_session.json");
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloSession] Save error: {ex.Message}");
            }
        }

        public void Load()
        {
            try
            {
                bool loadedFromSettings = false;

                // 1. Thử đọc từ LocalSettings
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
                                var parsed = JsonConvert.DeserializeObject<Dictionary<string, object>>(mapJson);
                                if (parsed != null)
                                {
                                    ServiceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                    foreach (var kv in parsed)
                                    {
                                        if (kv.Value is JArray jarr && jarr.Count > 0)
                                            ServiceMap[kv.Key] = CleanUrl(jarr[0]?.ToString());
                                        else
                                            ServiceMap[kv.Key] = CleanUrl(kv.Value?.ToString());
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    if (!string.IsNullOrEmpty(ZpwSek) || !string.IsNullOrEmpty(UserId))
                    {
                        loadedFromSettings = true;
                    }
                }

                // 2. Nếu LocalSettings rỗng hoặc thiếu, nạp ngay từ file zalo_session.json
                if (!loadedFromSettings || string.IsNullOrEmpty(ZpwSek))
                {
                    try
                    {
                        var folderPath = ApplicationData.Current.LocalFolder.Path;
                        var filePath = Path.Combine(folderPath, "zalo_session.json");
                        if (File.Exists(filePath))
                        {
                            var json = File.ReadAllText(filePath);
                            var obj = JObject.Parse(json);
                            if (obj != null)
                            {
                                var fSek = obj["ZpwSek"]?.ToString();
                                var fUid = obj["UserId"]?.ToString();
                                if (!string.IsNullOrEmpty(fSek)) ZpwSek = fSek;
                                if (!string.IsNullOrEmpty(fUid)) UserId = fUid;

                                var fName = obj["DisplayName"]?.ToString();
                                if (!string.IsNullOrEmpty(fName)) DisplayName = fName;
                                var fAvt = obj["AvatarUrl"]?.ToString();
                                if (!string.IsNullOrEmpty(fAvt)) AvatarUrl = fAvt;
                                var fPhone = obj["PhoneNumber"]?.ToString();
                                if (!string.IsNullOrEmpty(fPhone)) PhoneNumber = fPhone;
                                var fZps = obj["ZpsId"]?.ToString();
                                if (!string.IsNullOrEmpty(fZps)) ZpsId = fZps;
                                var fEnk = obj["ZpwEnk"]?.ToString();
                                if (!string.IsNullOrEmpty(fEnk)) ZpwEnk = fEnk;
                                var fImei = obj["Imei"]?.ToString();
                                if (!string.IsNullOrEmpty(fImei)) Imei = fImei;
                                var fWs = obj["WebSocketUrl"]?.ToString();
                                if (!string.IsNullOrEmpty(fWs)) WebSocketUrl = fWs;

                                var sMap = obj["ServiceMap"] as JObject;
                                if (sMap != null)
                                {
                                    ServiceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                    foreach (var prop in sMap.Properties())
                                    {
                                        ServiceMap[prop.Name] = CleanUrl(prop.Value?.ToString());
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ZaloSession] Load from file warning: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloSession] Load error: {ex.Message}");
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
            ServiceMap.Clear();

            try
            {
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

                var folderPath = ApplicationData.Current.LocalFolder.Path;
                var filePath = Path.Combine(folderPath, "zalo_session.json");
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloSession] Clear error: {ex.Message}");
            }
        }
    }
}
