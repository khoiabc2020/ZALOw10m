using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Storage;
using Newtonsoft.Json.Linq;
using ZaloUWP.Models;

namespace ZaloUWP.Core
{
    public class ZaloAuthService
    {
        private readonly HttpClient _httpClient;
        private readonly CookieContainer _cookieContainer;
        private string _loginVersion = "2606161423";
        private bool _isQrScanned = false;

        public ZaloAuthService()
        {
            _cookieContainer = new CookieContainer();
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                CookieContainer = _cookieContainer
            };

            _httpClient = new HttpClient(handler);
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", ZaloConstants.UserAgent);
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");
        }

        /// <summary>
        /// Safe JSON parse giống Zalo10.so:
        /// var __o = null; try { __o = JSON.parse(__raw); } catch(e) { __o = null; }
        /// </summary>
        private static JObject SafeParseJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var trimmed = raw.Trim();
            if (!trimmed.StartsWith("{") && !trimmed.StartsWith("[")) return null;
            try
            {
                return JObject.Parse(trimmed);
            }
            catch
            {
                return null;
            }
        }

        public async Task<QrLoginSession> GenerateQrAsync()
        {
            _isQrScanned = false;
            try
            {
                // Bước 1: Lấy phiên bản login script từ trang đăng nhập Zalo
                try
                {
                    var req1 = new HttpRequestMessage(HttpMethod.Get, "https://id.zalo.me/account?continue=https%3A%2F%2Fchat.zalo.me%2F");
                    req1.Headers.Add("Referer", "https://chat.zalo.me/");
                    var r1 = await _httpClient.SendAsync(req1);
                    var html = await r1.Content.ReadAsStringAsync();
                    var match = Regex.Match(html, @"https://stc-zlogin\.zdn\.vn/main-([\d.]+)\.js");
                    if (match.Success)
                    {
                        _loginVersion = match.Groups[1].Value;
                    }
                    else
                    {
                        // Fallback: thử chunkMain pattern giống Zalo10
                        match = Regex.Match(html, @"main[.-]([\w.-]+)\.js");
                        if (match.Success)
                            _loginVersion = match.Groups[1].Value;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZaloAuthService] LoadLoginPage warning: {ex.Message}");
                }

                // Bước 2: Gọi logininfo
                try
                {
                    var req2 = new HttpRequestMessage(HttpMethod.Post, "https://id.zalo.me/account/logininfo")
                    {
                        Content = new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            { "continue", "https://zalo.me/pc" },
                            { "v", _loginVersion }
                        })
                    };
                    req2.Headers.Add("Origin", "https://id.zalo.me");
                    req2.Headers.Add("Referer", "https://id.zalo.me/account?continue=https%3A%2F%2Fzalo.me%2Fpc");
                    await _httpClient.SendAsync(req2);
                }
                catch { }

                // Bước 3: Gọi verify-client
                try
                {
                    var req3 = new HttpRequestMessage(HttpMethod.Post, "https://id.zalo.me/account/verify-client")
                    {
                        Content = new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            { "type", "device" },
                            { "continue", "https://zalo.me/pc" },
                            { "v", _loginVersion }
                        })
                    };
                    req3.Headers.Add("Origin", "https://id.zalo.me");
                    req3.Headers.Add("Referer", "https://id.zalo.me/account?continue=https%3A%2F%2Fzalo.me%2Fpc");
                    await _httpClient.SendAsync(req3);
                }
                catch { }

                // Bước 4: Tạo mã QR chính thức từ Zalo
                var reqQr = new HttpRequestMessage(HttpMethod.Post, "https://id.zalo.me/account/authen/qr/generate")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        { "continue", "https://zalo.me/pc" },
                        { "v", _loginVersion }
                    })
                };
                reqQr.Headers.Add("Origin", "https://id.zalo.me");
                reqQr.Headers.Add("Referer", "https://id.zalo.me/account?continue=https%3A%2F%2Fzalo.me%2Fpc");

                var resQr = await _httpClient.SendAsync(reqQr);
                var content = await resQr.Content.ReadAsStringAsync();

                var json = SafeParseJson(content);
                if (json != null && json["error_code"]?.Value<int>() == 0 && json["data"] != null)
                {
                    var data = json["data"];
                    string code = data["code"]?.ToString();
                    string token = data["token"]?.ToString();
                    string base64Img = data["image"]?.ToString();

                    if (!string.IsNullOrEmpty(base64Img))
                    {
                        if (base64Img.Contains(","))
                        {
                            base64Img = base64Img.Substring(base64Img.IndexOf(",") + 1);
                        }
                        byte[] imageBytes = Convert.FromBase64String(base64Img);

                        var localFolder = ApplicationData.Current.LocalFolder;
                        var file = await localFolder.CreateFileAsync("zalo_official_qr.png", CreationCollisionOption.ReplaceExisting);
                        await FileIO.WriteBytesAsync(file, imageBytes);

                        return new QrLoginSession
                        {
                            Code = code,
                            Token = token,
                            QrImageUrl = $"ms-appdata:///local/zalo_official_qr.png?t={DateTime.UtcNow.Ticks}",
                            ExpireTime = DateTime.Now.AddMinutes(2),
                            Status = "waiting_scan"
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloAuthService] GenerateQr error: {ex.Message}");
            }

            return null;
        }

        public async Task<string> CheckQrStatusAsync(string code)
        {
            if (string.IsNullOrEmpty(code))
                return "expired";

            try
            {
                // Bước 1: Nếu chưa quét, gọi waiting-scan
                if (!_isQrScanned)
                {
                    var reqScan = new HttpRequestMessage(HttpMethod.Post, "https://id.zalo.me/account/authen/qr/waiting-scan")
                    {
                        Content = new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            { "code", code },
                            { "continue", "https://chat.zalo.me/" },
                            { "v", _loginVersion }
                        })
                    };
                    reqScan.Headers.Add("Origin", "https://id.zalo.me");
                    reqScan.Headers.Add("Referer", "https://id.zalo.me/account?continue=https%3A%2F%2Fchat.zalo.me%2F");

                    var resScan = await _httpClient.SendAsync(reqScan);
                    var strScan = await resScan.Content.ReadAsStringAsync();
                    var jsonScan = SafeParseJson(strScan);

                    if (jsonScan == null) return "waiting_scan";

                    int scanErr = jsonScan["error_code"]?.Value<int>() ?? -1;
                    if (scanErr == 0)
                    {
                        // Điện thoại đã quét mã!
                        _isQrScanned = true;
                        return "waiting_confirm";
                    }
                    else if (scanErr == 8)
                    {
                        // Chưa quét, tiếp tục chờ
                        return "waiting_scan";
                    }
                    else
                    {
                        return "expired";
                    }
                }

                // Bước 2: Khi đã quét rồi, gọi waiting-confirm chờ bấm "Xác nhận đăng nhập"
                var reqConfirm = new HttpRequestMessage(HttpMethod.Post, "https://id.zalo.me/account/authen/qr/waiting-confirm")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        { "code", code },
                        { "gToken", "" },
                        { "gAction", "CONFIRM_QR" },
                        { "continue", "https://chat.zalo.me/" },
                        { "v", _loginVersion }
                    })
                };
                reqConfirm.Headers.Add("Origin", "https://id.zalo.me");
                reqConfirm.Headers.Add("Referer", "https://id.zalo.me/account?continue=https%3A%2F%2Fchat.zalo.me%2F");

                var resConfirm = await _httpClient.SendAsync(reqConfirm);
                var strConfirm = await resConfirm.Content.ReadAsStringAsync();
                var jsonConfirm = SafeParseJson(strConfirm);

                if (jsonConfirm == null) return "waiting_confirm";

                int confirmErr = jsonConfirm["error_code"]?.Value<int>() ?? -1;
                if (confirmErr == 0)
                {
                    // ====== ĐĂNG NHẬP THÀNH CÔNG ======
                    // Step 7: checksession (Chuẩn Zalo10.so onStep7Done & zca-js)
                    // URL: https://id.zalo.me/account/checksession?continue=https%3A%2F%2Fchat.zalo.me%2Findex.html
                    // HTTP 302 redirect sets zpw_sek, zpsid, uid cookies!
                    // QUAN TRỌNG: Phải set AllowAutoRedirect = false để đọc trực tiếp Set-Cookie header từ response 302
                    try
                    {
                        var checkHandler = new HttpClientHandler
                        {
                            AllowAutoRedirect = false,
                            UseCookies = true,
                            CookieContainer = _cookieContainer
                        };

                        using (var checkClient = new HttpClient(checkHandler))
                        {
                            checkClient.Timeout = TimeSpan.FromSeconds(15);
                            checkClient.DefaultRequestHeaders.Add("User-Agent", ZaloConstants.UserAgent);

                            var reqCheck = new HttpRequestMessage(HttpMethod.Get, ZaloConstants.CheckSessionUrl);
                            reqCheck.Headers.TryAddWithoutValidation("Referer", "https://id.zalo.me/account?continue=https%3A%2F%2Fchat.zalo.me%2F");
                            reqCheck.Headers.TryAddWithoutValidation("sec-fetch-dest", "document");
                            reqCheck.Headers.TryAddWithoutValidation("sec-fetch-mode", "navigate");
                            reqCheck.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
                            reqCheck.Headers.TryAddWithoutValidation("upgrade-insecure-requests", "1");

                            var resCheck = await checkClient.SendAsync(reqCheck);

                            if (resCheck.Headers.TryGetValues("Set-Cookie", out var setCookies))
                            {
                                foreach (var sc in setCookies)
                                {
                                    var parts = sc.Split(';');
                                    if (parts.Length > 0)
                                    {
                                        var kv = parts[0].Trim().Split('=');
                                        if (kv.Length >= 2)
                                        {
                                            var name = kv[0].Trim();
                                            var val = kv[1].Trim();
                                            if (name.Equals("zpw_sek", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(val))
                                                ZaloSession.Current.ZpwSek = val;
                                            else if (name.Equals("zpsid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(val))
                                                ZaloSession.Current.ZpsId = val;
                                            else if (name.Equals("uid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(val))
                                                ZaloSession.Current.UserId = val;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ZaloAuthService] Step7 checksession warning: {ex.Message}");
                    }

                    // Trích xuất bổ sung từ CookieContainer
                    var data = jsonConfirm["data"];
                    var zpwSek = data?["zpw_sek"]?.ToString() ?? data?["session_key"]?.ToString();
                    var uid = data?["uid"]?.ToString() ?? data?["userId"]?.ToString();

                    try
                    {
                        var uriId = new Uri("https://id.zalo.me");
                        var uriChat = new Uri("https://chat.zalo.me");
                        var uriZalo = new Uri("https://zalo.me");

                        var cookies = new List<Cookie>();
                        foreach (Cookie c in _cookieContainer.GetCookies(uriId)) cookies.Add(c);
                        foreach (Cookie c in _cookieContainer.GetCookies(uriChat)) cookies.Add(c);
                        foreach (Cookie c in _cookieContainer.GetCookies(uriZalo)) cookies.Add(c);

                        foreach (var c in cookies)
                        {
                            if (c.Name.Equals("zpw_sek", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(c.Value))
                            {
                                zpwSek = c.Value;
                            }
                            else if (c.Name.Equals("zpsid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(c.Value))
                            {
                                ZaloSession.Current.ZpsId = c.Value;
                            }
                            else if (c.Name.Equals("uid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(c.Value))
                            {
                                uid = c.Value;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ZaloAuthService] Cookie extraction warning: {ex.Message}");
                    }

                    if (!string.IsNullOrEmpty(zpwSek))
                    {
                        ZaloSession.Current.ZpwSek = zpwSek;
                    }
                    if (!string.IsNullOrEmpty(uid))
                    {
                        ZaloSession.Current.UserId = uid;
                    }
                    else if (string.IsNullOrEmpty(ZaloSession.Current.UserId))
                    {
                        ZaloSession.Current.UserId = "me";
                    }

                    // Đồng bộ cookie vào CookieContainer cho domain chat.zalo.me & wpa.chat.zalo.me
                    try
                    {
                        var chatUri = new Uri("https://chat.zalo.me");
                        var wpaUri = new Uri("https://wpa.chat.zalo.me");
                        var zaloUri = new Uri("https://zalo.me");

                        if (!string.IsNullOrEmpty(ZaloSession.Current.ZpwSek))
                        {
                            _cookieContainer.Add(chatUri, new Cookie("zpw_sek", ZaloSession.Current.ZpwSek));
                            _cookieContainer.Add(wpaUri, new Cookie("zpw_sek", ZaloSession.Current.ZpwSek));
                            _cookieContainer.Add(zaloUri, new Cookie("zpw_sek", ZaloSession.Current.ZpwSek));
                        }
                        if (!string.IsNullOrEmpty(ZaloSession.Current.ZpsId))
                        {
                            _cookieContainer.Add(chatUri, new Cookie("zpsid", ZaloSession.Current.ZpsId));
                            _cookieContainer.Add(wpaUri, new Cookie("zpsid", ZaloSession.Current.ZpsId));
                            _cookieContainer.Add(zaloUri, new Cookie("zpsid", ZaloSession.Current.ZpsId));
                        }
                        if (!string.IsNullOrEmpty(ZaloSession.Current.UserId))
                        {
                            _cookieContainer.Add(chatUri, new Cookie("uid", ZaloSession.Current.UserId));
                            _cookieContainer.Add(wpaUri, new Cookie("uid", ZaloSession.Current.UserId));
                            _cookieContainer.Add(zaloUri, new Cookie("uid", ZaloSession.Current.UserId));
                        }
                    }
                    catch { }

                    // Step 8: getLoginInfo (giống Zalo10 onStep8Done)
                    // Lấy zpw_enk, zpw_ws, zpw_service_map_v3 và profile thật
                    try
                    {
                        await GetLoginInfoAsync();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ZaloAuthService] Step8 getLoginInfo warning: {ex.Message}");
                    }

                    // Step 9: getServerInfo (giống Zalo10 onStep9Done)
                    try
                    {
                        await GetServerInfoAsync();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ZaloAuthService] Step9 getServerInfo warning: {ex.Message}");
                    }

                    // Lưu toàn bộ session thật vào LocalSettings
                    ZaloSession.Current.Save();

                    // ĐĂNG NHẬP VÀ ĐỒNG BỘ THÀNH CÔNG -> Chuyển vào MainPage
                    return "success";
                }
                else if (confirmErr == 8)
                {
                    // Đang chờ người dùng bấm Xác nhận trên điện thoại
                    return "waiting_confirm";
                }
                else if (confirmErr == -13)
                {
                    // Người dùng bấm Từ chối
                    return "declined";
                }
                else
                {
                    return "expired";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloAuthService] CheckQrStatus error: {ex.Message}");
                return _isQrScanned ? "waiting_confirm" : "waiting_scan";
            }
        }

        /// <summary>
        /// BƯỚC 8: getLoginInfo (Chuẩn Zalo10.so onStep8Done)
        /// Gửi POST https://wpa.chat.zalo.me/api/login/getLoginInfo
        /// Lấy zpw_enk (key mã hóa API chat/friend), zpw_service_map_v3 (cluster backend routing), profile thật
        /// </summary>
        public async Task<bool> GetLoginInfoAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(ZaloSession.Current.ZpwSek))
                {
                    System.Diagnostics.Debug.WriteLine("[ZaloAuthService] GetLoginInfo: ZpwSek is empty, skipping");
                    return false;
                }

                var req = new HttpRequestMessage(HttpMethod.Post, ZaloConstants.GetLoginInfoUrl)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        { "zpw_ver", "671" },
                        { "zpw_type", "30" },
                        { "nretry", "0" },
                        { "imei", ZaloSession.Current.Imei ?? "" }
                    })
                };

                req.Headers.TryAddWithoutValidation("Cookie", ZaloSession.Current.GetCookieHeader());
                req.Headers.TryAddWithoutValidation("Origin", ZaloConstants.WebOrigin);
                req.Headers.TryAddWithoutValidation("Referer", ZaloConstants.WebReferer);

                var res = await _httpClient.SendAsync(req);
                var content = await res.Content.ReadAsStringAsync();

                var json = SafeParseJson(content);
                if (json == null)
                {
                    System.Diagnostics.Debug.WriteLine("[ZaloAuthService] GetLoginInfo: invalid JSON response");
                    return false;
                }

                int errCode = json["error_code"]?.Value<int>() ?? -1;
                if (errCode == 0 && json["data"] != null)
                {
                    JObject dataObj = null;

                    // Giống Zalo10.so: data có thể là AES-CBC ciphertext mã hóa bằng zpw_sek
                    if (json["data"].Type == JTokenType.String)
                    {
                        var cipherStr = json["data"].ToString();
                        var decStr = ZaloCrypto.DecryptAesCbc(cipherStr, ZaloSession.Current.ZpwSek);
                        if (!string.IsNullOrWhiteSpace(decStr))
                        {
                            var parsed = SafeParseJson(decStr);
                            if (parsed != null)
                            {
                                dataObj = parsed["data"] as JObject ?? parsed;
                            }
                        }
                    }
                    else if (json["data"] is JObject dObj)
                    {
                        dataObj = dObj;
                    }

                    if (dataObj != null)
                    {
                        // 1. zpw_enk (Key giải mã các endpoint hội thoại & bạn bè)
                        var enk = dataObj["zpw_enk"]?.ToString() ?? dataObj["enk"]?.ToString();
                        if (!string.IsNullOrEmpty(enk))
                        {
                            ZaloSession.Current.ZpwEnk = enk;
                        }

                        // 2. zpw_ws (WebSocket endpoint URL)
                        var ws = dataObj["zpw_ws"];
                        if (ws != null)
                        {
                            if (ws is JArray wsArr && wsArr.Count > 0)
                            {
                                ZaloSession.Current.WebSocketUrl = wsArr[0]?.ToString();
                            }
                            else
                            {
                                ZaloSession.Current.WebSocketUrl = ws.ToString();
                            }
                        }

                        // 3. zpw_service_map_v3 (Routing cluster thực tế của Zalo)
                        var sMap = dataObj["zpw_service_map_v3"] as JObject ?? dataObj["zpw_service_map"] as JObject;
                        if (sMap != null)
                        {
                            foreach (var prop in sMap.Properties())
                            {
                                string urlStr = null;
                                if (prop.Value is JArray jarr && jarr.Count > 0)
                                {
                                    urlStr = jarr[0]?.ToString();
                                }
                                else
                                {
                                    urlStr = prop.Value?.ToString();
                                }
                                var cleaned = ZaloSession.CleanUrl(urlStr);
                                if (!string.IsNullOrEmpty(cleaned))
                                {
                                    ZaloSession.Current.ServiceMap[prop.Name] = cleaned;
                                }
                            }
                        }

                        // 4. Thông tin cá nhân thật (profile)
                        var profile = dataObj["profile"] ?? dataObj["user_info"];
                        if (profile != null)
                        {
                            var pUid = profile["uid"]?.ToString() ?? profile["userId"]?.ToString();
                            if (!string.IsNullOrEmpty(pUid))
                                ZaloSession.Current.UserId = pUid;

                            var dName = profile["display_name"]?.ToString() ?? profile["name"]?.ToString() ?? profile["zalo_name"]?.ToString();
                            if (!string.IsNullOrEmpty(dName))
                                ZaloSession.Current.DisplayName = dName;

                            var avt = profile["avatar"]?.ToString() ?? profile["avatar_url"]?.ToString();
                            if (!string.IsNullOrEmpty(avt))
                                ZaloSession.Current.AvatarUrl = avt;

                            var phone = profile["phone"]?.ToString() ?? profile["phoneNumber"]?.ToString();
                            if (!string.IsNullOrEmpty(phone))
                                ZaloSession.Current.PhoneNumber = phone;
                        }

                        ZaloSession.Current.Save();
                        return true;
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[ZaloAuthService] GetLoginInfo error_code={errCode}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloAuthService] GetLoginInfo error (swallowed): {ex.Message}");
            }

            return false;
        }

        public async Task<bool> GetServerInfoAsync()
        {
            try
            {
                // Giống Zalo10 onStep9Done: gọi getServerInfo với đầy đủ query params
                var imei = Uri.EscapeDataString(ZaloSession.Current.Imei ?? "");
                var signkey = ""; // Zalo10 tính signkey, ta để trống cho giản lược
                var url = $"{ZaloConstants.GetServerInfoUrl}?imei={imei}&client_version=5.6.1&computer_name=Web&signkey={signkey}";

                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Cookie", ZaloSession.Current.GetCookieHeader());

                var res = await _httpClient.SendAsync(req);
                var content = await res.Content.ReadAsStringAsync();

                // Safe parse giống Zalo10:
                // var __o = null; try { __o = JSON.parse(__raw); } catch(e) { __o = null; }
                var json = SafeParseJson(content);
                if (json == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZaloAuthService] GetServerInfo: response is not valid JSON, ignoring");
                    return false;
                }

                if (json["error_code"]?.Value<int>() == 0 && json["data"] != null)
                {
                    var data = json["data"];
                    var enk = data["zpw_enk"]?.ToString();
                    if (!string.IsNullOrEmpty(enk))
                    {
                        ZaloSession.Current.ZpwEnk = enk;
                    }

                    var wsUrl = data["ws"]?.ToString() ?? data["websocket"]?.ToString() ?? data["zpw_ws"]?.ToString();
                    if (!string.IsNullOrEmpty(wsUrl))
                    {
                        ZaloSession.Current.WebSocketUrl = wsUrl;
                    }

                    // zpw_service_map_v3 (giống Zalo10)
                    var sMap = data["zpw_service_map_v3"] as JObject ?? data["zpw_service_map"] as JObject;
                    if (sMap != null)
                    {
                        foreach (var prop in sMap.Properties())
                        {
                            string urlStr = null;
                            if (prop.Value is JArray jarr && jarr.Count > 0)
                            {
                                urlStr = jarr[0]?.ToString();
                            }
                            else
                            {
                                urlStr = prop.Value?.ToString();
                            }
                            var cleaned = ZaloSession.CleanUrl(urlStr);
                            if (!string.IsNullOrEmpty(cleaned))
                            {
                                ZaloSession.Current.ServiceMap[prop.Name] = cleaned;
                            }
                        }
                    }

                    // Profile info
                    var profile = data["profile"] ?? data["user_info"];
                    if (profile != null)
                    {
                        var profileUid = profile["uid"]?.ToString();
                        if (!string.IsNullOrEmpty(profileUid))
                            ZaloSession.Current.UserId = profileUid;

                        var displayName = profile["display_name"]?.ToString() ?? profile["name"]?.ToString() ?? profile["zalo_name"]?.ToString();
                        if (!string.IsNullOrEmpty(displayName))
                            ZaloSession.Current.DisplayName = displayName;

                        var avatar = profile["avatar"]?.ToString();
                        if (!string.IsNullOrEmpty(avatar))
                            ZaloSession.Current.AvatarUrl = avatar;

                        var phone = profile["phone"]?.ToString();
                        if (!string.IsNullOrEmpty(phone))
                            ZaloSession.Current.PhoneNumber = phone;
                    }

                    ZaloSession.Current.Save();
                    return true;
                }
                else
                {
                    var errCode = json["error_code"]?.Value<int>() ?? -999;
                    var errMsg = json["error_message"]?.ToString() ?? "unknown";
                    System.Diagnostics.Debug.WriteLine($"[ZaloAuthService] GetServerInfo: error_code={errCode}, error_message={errMsg}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloAuthService] GetServerInfo error: {ex.Message}");
            }
            return false;
        }

        public async Task<bool> LoginWithSessionTokenAsync(string zpwSek, string zpsId = null)
        {
            if (string.IsNullOrWhiteSpace(zpwSek))
                return false;

            ZaloSession.Current.ZpwSek = zpwSek.Trim();
            if (!string.IsNullOrWhiteSpace(zpsId))
            {
                ZaloSession.Current.ZpsId = zpsId.Trim();
            }
            ZaloSession.Current.Save();

            try
            {
                await GetLoginInfoAsync();
                return await GetServerInfoAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloAuthService] LoginWithSessionToken error: {ex.Message}");
                // Vẫn coi là thành công nếu session đã lưu
                return ZaloSession.Current.IsLoggedIn;
            }
        }

        public async Task<bool> ParseAndApplyCookiesAsync(string rawCookies)
        {
            if (string.IsNullOrEmpty(rawCookies))
                return false;

            bool foundSek = false;
            var parts = rawCookies.Split(';');
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("zpw_sek=", StringComparison.OrdinalIgnoreCase))
                {
                    ZaloSession.Current.ZpwSek = trimmed.Substring("zpw_sek=".Length);
                    foundSek = true;
                }
                else if (trimmed.StartsWith("zpsid=", StringComparison.OrdinalIgnoreCase))
                {
                    ZaloSession.Current.ZpsId = trimmed.Substring("zpsid=".Length);
                }
                else if (trimmed.StartsWith("uid=", StringComparison.OrdinalIgnoreCase))
                {
                    ZaloSession.Current.UserId = trimmed.Substring("uid=".Length);
                }
            }

            if (foundSek)
            {
                ZaloSession.Current.Save();
                try
                {
                    await GetLoginInfoAsync();
                    await GetServerInfoAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZaloAuthService] ParseAndApplyCookies GetServerInfo warning: {ex.Message}");
                }
                return true;
            }

            return false;
        }

        public void Logout()
        {
            ZaloSession.Current.Clear();
        }
    }
}
