using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZaloUWP.Models;

namespace ZaloUWP.Core
{
    public class ZaloApiClient
    {
        private static ZaloApiClient _current;
        public static ZaloApiClient Current => _current ?? (_current = new ZaloApiClient());

        private readonly HttpClient _httpClient;

        public ZaloApiClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false // BẮT BUỘC false để không bị .NET tự động xóa Cookie header thủ công
            };

            _httpClient = new HttpClient(handler);
            _httpClient.Timeout = TimeSpan.FromSeconds(20);
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            var cookie = ZaloSession.Current.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookie))
            {
                req.Headers.TryAddWithoutValidation("Cookie", cookie);
            }
            req.Headers.TryAddWithoutValidation("User-Agent", ZaloConstants.UserAgent);
            req.Headers.TryAddWithoutValidation("Origin", ZaloConstants.WebOrigin);
            req.Headers.TryAddWithoutValidation("Referer", ZaloConstants.WebReferer);
            return req;
        }

        /// <summary>
        /// Safe JSON parse:
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

        /// <summary>
        /// Safe extract & decrypt payload từ Zalo API response:
        /// Zalo Web API trả về: { "error_code": 0, "data": "<AES-CBC ciphertext mã hóa bằng zpw_enk>" }
        /// </summary>
        private static JToken SafeExtractData(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            var trimmed = content.Trim();

            try
            {
                // 1. Thử parse outer JSON trước
                var outer = SafeParseJson(trimmed);
                if (outer != null)
                {
                    var dataToken = outer["data"];
                    if (dataToken == null || dataToken.Type == JTokenType.Null)
                    {
                        return outer;
                    }

                    // Nếu data là string -> đó là AES ciphertext cần giải mã
                    if (dataToken.Type == JTokenType.String)
                    {
                        var cipherStr = dataToken.ToString();
                        string decrypted = null;

                        // Thử giải mã bằng zpw_enk trước
                        if (!string.IsNullOrEmpty(ZaloSession.Current.ZpwEnk))
                        {
                            try
                            {
                                decrypted = ZaloCrypto.DecryptAesCbc(cipherStr, ZaloSession.Current.ZpwEnk);
                            }
                            catch { }
                        }

                        // Nếu enk thất bại hoặc trống, thử fallback zpw_sek
                        if (string.IsNullOrWhiteSpace(decrypted) && !string.IsNullOrEmpty(ZaloSession.Current.ZpwSek))
                        {
                            try
                            {
                                decrypted = ZaloCrypto.DecryptAesCbc(cipherStr, ZaloSession.Current.ZpwSek);
                            }
                            catch { }
                        }

                        if (!string.IsNullOrWhiteSpace(decrypted))
                        {
                            var inner = SafeParseJson(decrypted);
                            if (inner != null)
                            {
                                return inner["data"] ?? inner;
                            }
                            else
                            {
                                try
                                {
                                    return JArray.Parse(decrypted);
                                }
                                catch { }
                            }
                        }
                    }
                    else
                    {
                        // data đã là JObject hoặc JArray rồi
                        return dataToken;
                    }
                }

                // 2. Nếu content không phải JSON, thử coi là raw ciphertext
                if (!string.IsNullOrEmpty(ZaloSession.Current.ZpwEnk))
                {
                    try
                    {
                        var decrypted = ZaloCrypto.DecryptAesCbc(trimmed, ZaloSession.Current.ZpwEnk);
                        if (!string.IsNullOrWhiteSpace(decrypted))
                        {
                            var parsed = SafeParseJson(decrypted);
                            if (parsed != null) return parsed["data"] ?? parsed;
                            try { return JArray.Parse(decrypted); } catch { }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloApiClient] SafeExtractData error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 1. Lấy danh sách ID nhóm từ /api/group/getlg/v4
        /// </summary>
        public async Task<List<string>> GetAllGroupIdsAsync()
        {
            var groupIds = new List<string>();
            try
            {
                var baseUrl = ZaloSession.Current.GetServiceUrl("group_poll", "https://group-wpa.chat.zalo.me");
                var url = $"{baseUrl}/api/group/getlg/v4?zpw_ver=671&zpw_type=30&nretry=0";

                var req = CreateRequest(HttpMethod.Get, url);
                var res = await _httpClient.SendAsync(req);
                var content = await res.Content.ReadAsStringAsync();

                var data = SafeExtractData(content);
                if (data != null)
                {
                    var map = data["gridVerMap"] as JObject ?? data as JObject;
                    if (map != null)
                    {
                        foreach (var prop in map.Properties())
                        {
                            if (!string.IsNullOrEmpty(prop.Name))
                                groupIds.Add(prop.Name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloApiClient] GetAllGroupIds warning: {ex.Message}");
            }
            return groupIds;
        }

        /// <summary>
        /// 2. Lấy thông tin chi tiết các nhóm từ /api/group/getmg-v2 (POST)
        /// Theo chuẩn zca-js getGroupInfo
        /// </summary>
        public async Task<List<ConversationItem>> GetGroupDetailsAsync(List<string> groupIds)
        {
            var result = new List<ConversationItem>();
            if (groupIds == null || groupIds.Count == 0) return result;

            try
            {
                var baseUrl = ZaloSession.Current.GetServiceUrl("group", "https://group-wpa.chat.zalo.me");
                var url = $"{baseUrl}/api/group/getmg-v2?zpw_ver=671&zpw_type=30";

                // Chia thành các đợt tối đa 30 nhóm mỗi request
                for (int i = 0; i < groupIds.Count; i += 30)
                {
                    var chunk = groupIds.Skip(i).Take(30).ToList();
                    var gridDict = new Dictionary<string, int>();
                    foreach (var id in chunk)
                    {
                        gridDict[id] = 0;
                    }

                    var innerGridVerMap = JsonConvert.SerializeObject(gridDict);
                    var payload = new Dictionary<string, string>
                    {
                        { "gridVerMap", innerGridVerMap }
                    };
                    var jsonPayload = JsonConvert.SerializeObject(payload);

                    string encryptedParams = null;
                    if (!string.IsNullOrEmpty(ZaloSession.Current.ZpwEnk))
                    {
                        encryptedParams = ZaloCrypto.EncryptAesCbc(jsonPayload, ZaloSession.Current.ZpwEnk);
                    }
                    else if (!string.IsNullOrEmpty(ZaloSession.Current.ZpwSek))
                    {
                        encryptedParams = ZaloCrypto.EncryptAesCbc(jsonPayload, ZaloSession.Current.ZpwSek);
                    }

                    if (string.IsNullOrEmpty(encryptedParams)) continue;

                    var req = CreateRequest(HttpMethod.Post, url);
                    var form = new Dictionary<string, string>
                    {
                        { "params", encryptedParams }
                    };
                    req.Content = new FormUrlEncodedContent(form);

                    var res = await _httpClient.SendAsync(req);
                    var content = await res.Content.ReadAsStringAsync();

                    var data = SafeExtractData(content);
                    if (data != null)
                    {
                        var infoMap = data["gridInfoMap"] as JObject ?? data as JObject;
                        if (infoMap != null)
                        {
                            foreach (var prop in infoMap.Properties())
                            {
                                try
                                {
                                    var g = prop.Value as JObject;
                                    if (g == null) continue;

                                    var gId = g["groupId"]?.ToString() ?? prop.Name;
                                    var name = g["name"]?.ToString() ?? "Nhóm";
                                    var avt = g["avt"]?.ToString() ?? g["fullAvt"]?.ToString() ?? "";
                                    var desc = g["desc"]?.ToString() ?? "";

                                    var lastMsg = "";
                                    long lastTime = 0;

                                    if (g["lastMsg"] != null)
                                    {
                                        lastMsg = g["lastMsg"]?.ToString() ?? "";
                                    }
                                    if (g["lastTime"] != null)
                                    {
                                        lastTime = g["lastTime"]?.Value<long>() ?? 0;
                                    }

                                    if (lastTime == 0 && g["createdTime"] != null)
                                    {
                                        lastTime = g["createdTime"]?.Value<long>() ?? 0;
                                    }
                                    if (lastTime == 0)
                                    {
                                        lastTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                    }

                                    var conv = new ConversationItem
                                    {
                                        Id = gId,
                                        Title = name,
                                        AvatarUrl = avt,
                                        LastMessage = string.IsNullOrEmpty(lastMsg) ? desc : lastMsg,
                                        LastMessageTimestamp = lastTime,
                                        UnreadCount = 0,
                                        IsGroup = true,
                                        IsOnline = false,
                                        IsPinned = false
                                    };
                                    result.Add(conv);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[ZaloApiClient] Parse group info error: {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloApiClient] GetGroupDetails warning: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 3. Lấy danh sách hội thoại ghim từ /api/pinconvers/list
        /// </summary>
        public async Task<List<string>> GetPinnedConversationsAsync()
        {
            var pinnedIds = new List<string>();
            try
            {
                var baseUrl = ZaloSession.Current.GetServiceUrl("conversation", "https://tt-chat1-wpa.chat.zalo.me");
                string url;

                if (!string.IsNullOrEmpty(ZaloSession.Current.ZpwEnk))
                {
                    var payload = new Dictionary<string, object>
                    {
                        { "imei", ZaloSession.Current.Imei ?? "" }
                    };
                    var jsonPayload = JsonConvert.SerializeObject(payload);
                    var enc = ZaloCrypto.EncryptAesCbc(jsonPayload, ZaloSession.Current.ZpwEnk);
                    url = $"{baseUrl}/api/pinconvers/list?zpw_ver=671&zpw_type=30&params={Uri.EscapeDataString(enc)}";
                }
                else
                {
                    url = $"{baseUrl}/api/pinconvers/list?zpw_ver=671&zpw_type=30&imei={Uri.EscapeDataString(ZaloSession.Current.Imei ?? "")}";
                }

                var req = CreateRequest(HttpMethod.Get, url);
                var res = await _httpClient.SendAsync(req);
                var content = await res.Content.ReadAsStringAsync();

                var data = SafeExtractData(content);
                if (data != null)
                {
                    var arr = data["conversations"] as JArray;
                    if (arr != null)
                    {
                        foreach (var item in arr)
                        {
                            var id = item.ToString();
                            if (!string.IsNullOrEmpty(id))
                                pinnedIds.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloApiClient] GetPinnedConversations warning: {ex.Message}");
            }
            return pinnedIds;
        }

        /// <summary>
        /// 4. Lấy toàn bộ danh sách hội thoại thật từ Zalo Server:
        /// - Nhóm từ /api/group/getlg/v4 + /api/group/getmg-v2
        /// - Bạn bè từ /api/social/friend/getfriends
        /// - Trạng thái ghim từ /api/pinconvers/list
        /// </summary>
        public async Task<List<ConversationItem>> GetConversationsAsync(int page = 1, int count = 50)
        {
            var items = new List<ConversationItem>();
            try
            {
                // BƯỚC A: Lấy danh sách hội thoại ghim
                var pinnedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var pins = await GetPinnedConversationsAsync();
                    foreach (var p in pins) pinnedIds.Add(p);
                }
                catch { }

                // BƯỚC B: Lấy tất cả nhóm của tài khoản thật
                try
                {
                    var groupIds = await GetAllGroupIdsAsync();
                    if (groupIds.Count > 0)
                    {
                        var groups = await GetGroupDetailsAsync(groupIds);
                        foreach (var g in groups)
                        {
                            if (pinnedIds.Contains(g.Id))
                            {
                                g.IsPinned = true;
                            }
                            items.Add(g);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZaloApiClient] Fetch groups error: {ex.Message}");
                }

                // BƯỚC C: Lấy danh sách bạn bè
                try
                {
                    var friends = await GetFriendsListAsync();
                    if (friends != null && friends.Count > 0)
                    {
                        foreach (var f in friends)
                        {
                            bool isPinned = pinnedIds.Contains(f.UserId);
                            
                            // Thêm các bạn bè đã ghim hoặc bạn bè vào danh sách hội thoại
                            var c = new ConversationItem
                            {
                                Id = f.UserId,
                                Title = f.DisplayName,
                                AvatarUrl = f.AvatarUrl,
                                LastMessage = !string.IsNullOrEmpty(f.StatusMessage) ? f.StatusMessage : "Nhấn để trò chuyện",
                                LastMessageTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 3600000,
                                UnreadCount = 0,
                                IsGroup = false,
                                IsOnline = f.IsOnline,
                                IsPinned = isPinned
                            };

                            if (isPinned)
                            {
                                items.Insert(0, c);
                            }
                            else if (items.Count < 50)
                            {
                                items.Add(c);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZaloApiClient] Fetch friends for convs warning: {ex.Message}");
                }

                // Sắp xếp: Hội thoại ghim lên đầu, sau đó sắp xếp theo thời gian tin nhắn mới nhất
                items = items
                    .OrderByDescending(x => x.IsPinned)
                    .ThenByDescending(x => x.LastMessageTimestamp)
                    .ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloApiClient] GetConversations error: {ex.Message}");
            }
            return items;
        }

        /// <summary>
        /// 5. Lấy lịch sử tin nhắn thật:
        /// - Nếu là nhóm: Gọi /api/group/history
        /// - Nếu là 1-1: Gọi /api/message/getmsgv2
        /// </summary>
        public async Task<List<MessageItem>> GetMessagesAsync(string conversationId, bool isGroup = false, string minMsgId = "0", int count = 50)
        {
            var messages = new List<MessageItem>();
            if (string.IsNullOrEmpty(conversationId)) return messages;

            try
            {
                if (isGroup)
                {
                    // Lịch sử tin nhắn nhóm theo chuẩn zca-js getGroupChatHistory
                    var baseUrl = ZaloSession.Current.GetServiceUrl("group", "https://group-wpa.chat.zalo.me");
                    var payload = new Dictionary<string, object>
                    {
                        { "grid", conversationId },
                        { "count", count }
                    };
                    var jsonPayload = JsonConvert.SerializeObject(payload);

                    string url;
                    if (!string.IsNullOrEmpty(ZaloSession.Current.ZpwEnk))
                    {
                        var enc = ZaloCrypto.EncryptAesCbc(jsonPayload, ZaloSession.Current.ZpwEnk);
                        url = $"{baseUrl}/api/group/history?zpw_ver=671&zpw_type=30&params={Uri.EscapeDataString(enc)}";
                    }
                    else
                    {
                        url = $"{baseUrl}/api/group/history?grid={Uri.EscapeDataString(conversationId)}&count={count}&zpw_ver=671&zpw_type=30";
                    }

                    var req = CreateRequest(HttpMethod.Get, url);
                    var res = await _httpClient.SendAsync(req);
                    var content = await res.Content.ReadAsStringAsync();

                    var dataToken = SafeExtractData(content);
                    if (dataToken != null)
                    {
                        var msgsArr = dataToken["groupMsgs"] as JArray ?? dataToken["msgs"] as JArray ?? dataToken as JArray;
                        if (msgsArr != null)
                        {
                            foreach (var item in msgsArr)
                            {
                                try
                                {
                                    var fromUid = item["uidFrom"]?.ToString() ?? item["from_uid"]?.ToString() ?? item["from"]?.ToString();
                                    var isOutgoing = (fromUid == ZaloSession.Current.UserId);

                                    var text = item["content"]?.ToString() ?? item["message"]?.ToString() ?? item["text"]?.ToString() ?? "";
                                    
                                    // Xử lý JSON content nếu có
                                    if (text.StartsWith("{") && text.EndsWith("}"))
                                    {
                                        var innerObj = SafeParseJson(text);
                                        if (innerObj != null && innerObj["title"] != null)
                                        {
                                            text = innerObj["title"].ToString();
                                        }
                                    }

                                    var m = new MessageItem
                                    {
                                        MsgId = item["msgId"]?.ToString() ?? item["msg_id"]?.ToString() ?? Guid.NewGuid().ToString("N"),
                                        CliMsgId = item["cliMsgId"]?.ToString() ?? item["cli_msg_id"]?.ToString(),
                                        ConversationId = conversationId,
                                        FromUid = fromUid,
                                        FromName = item["name"]?.ToString() ?? item["dName"]?.ToString() ?? (isOutgoing ? "Tôi" : "Thành viên"),
                                        FromAvatar = item["avatar"]?.ToString() ?? item["avt"]?.ToString(),
                                        Text = text,
                                        Timestamp = item["ts"]?.Value<long>() ?? item["timestamp"]?.Value<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                        IsOutgoing = isOutgoing,
                                        Status = MessageDeliveryStatus.Read
                                    };

                                    var typeStr = item["msgType"]?.ToString() ?? item["type"]?.ToString();
                                    if (typeStr == "chat.photo" || typeStr == "chat.image" || typeStr == "32")
                                    {
                                        m.MsgType = MessageType.Image;
                                        m.MediaUrl = item["url"]?.ToString() ?? item["href"]?.ToString();
                                        m.ThumbUrl = item["thumb"]?.ToString() ?? m.MediaUrl;
                                    }
                                    else if (typeStr == "chat.sticker" || typeStr == "31")
                                    {
                                        m.MsgType = MessageType.Sticker;
                                        m.StickerId = item["id"]?.ToString();
                                        m.MediaUrl = string.Format(ZaloConstants.StickerCdnUrl, m.StickerId);
                                    }

                                    messages.Add(m);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[ZaloApiClient] Parse group msg item error: {ex.Message}");
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Lịch sử tin nhắn cá nhân 1-1
                    var baseUrl = ZaloSession.Current.GetServiceUrl("chat", "https://tt-chat1-wpa.chat.zalo.me");
                    var payload = new Dictionary<string, object>
                    {
                        { "first", true },
                        { "lastId", minMsgId ?? "0" },
                        { "toid", conversationId },
                        { "preIds", new string[0] },
                        { "imei", ZaloSession.Current.Imei ?? "" }
                    };
                    var jsonPayload = JsonConvert.SerializeObject(payload);

                    string url;
                    if (!string.IsNullOrEmpty(ZaloSession.Current.ZpwEnk))
                    {
                        var enc = ZaloCrypto.EncryptAesCbc(jsonPayload, ZaloSession.Current.ZpwEnk);
                        url = $"{baseUrl}/api/message/getmsgv2?zpw_ver=671&zpw_type=30&params={Uri.EscapeDataString(enc)}";
                    }
                    else
                    {
                        url = $"{baseUrl}/api/message/getmsgv2?toid={Uri.EscapeDataString(conversationId)}&zpw_ver=671&zpw_type=30";
                    }

                    var req = CreateRequest(HttpMethod.Get, url);
                    var res = await _httpClient.SendAsync(req);
                    var content = await res.Content.ReadAsStringAsync();

                    var dataToken = SafeExtractData(content);
                    if (dataToken != null)
                    {
                        var msgsArr = dataToken["msgs"] as JArray ?? dataToken["messages"] as JArray ?? dataToken as JArray;
                        if (msgsArr != null)
                        {
                            foreach (var item in msgsArr)
                            {
                                try
                                {
                                    var fromUid = item["uidFrom"]?.ToString() ?? item["from_uid"]?.ToString() ?? item["from"]?.ToString();
                                    var isOutgoing = (fromUid == ZaloSession.Current.UserId);

                                    var m = new MessageItem
                                    {
                                        MsgId = item["msgId"]?.ToString() ?? item["msg_id"]?.ToString() ?? Guid.NewGuid().ToString("N"),
                                        CliMsgId = item["cliMsgId"]?.ToString() ?? item["cli_msg_id"]?.ToString(),
                                        ConversationId = conversationId,
                                        FromUid = fromUid,
                                        FromName = isOutgoing ? "Tôi" : "Bạn bè",
                                        Text = item["message"]?.ToString() ?? item["text"]?.ToString() ?? item["content"]?.ToString() ?? "",
                                        Timestamp = item["ts"]?.Value<long>() ?? item["timestamp"]?.Value<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                        IsOutgoing = isOutgoing,
                                        Status = MessageDeliveryStatus.Read
                                    };
                                    messages.Add(m);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[ZaloApiClient] Parse 1-1 msg item error: {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloApiClient] GetMessages error: {ex.Message}");
            }

            // Sắp xếp tin nhắn theo thời gian tăng dần để hiển thị từ trên xuống dưới
            return messages.OrderBy(m => m.Timestamp).ToList();
        }

        /// <summary>
        /// 6. Gửi tin nhắn thật lên server Zalo:
        /// - Nhóm: POST /api/group/sendmsg
        /// - Cá nhân: POST /api/message/sms
        /// </summary>
        public async Task<MessageItem> SendMessageAsync(string toId, bool isGroup, string text, string quoteMsgId = null)
        {
            var cliMsgId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var sentItem = new MessageItem
            {
                MsgId = cliMsgId,
                CliMsgId = cliMsgId,
                ConversationId = toId,
                FromUid = ZaloSession.Current.UserId,
                FromName = ZaloSession.Current.DisplayName ?? "Tôi",
                Text = text,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsOutgoing = true,
                MsgType = MessageType.Text,
                Status = MessageDeliveryStatus.Sending
            };

            try
            {
                var baseUrl = isGroup 
                    ? ZaloSession.Current.GetServiceUrl("group", "https://group-wpa.chat.zalo.me")
                    : ZaloSession.Current.GetServiceUrl("chat", "https://tt-chat1-wpa.chat.zalo.me");
                var endpoint = isGroup ? "/api/group/sendmsg" : "/api/message/sms";
                var url = $"{baseUrl}{endpoint}?zpw_ver=671&zpw_type=30";

                var payload = new Dictionary<string, object>
                {
                    { isGroup ? "grid" : "toid", toId },
                    { "message", text },
                    { "clientId", cliMsgId },
                    { "imei", ZaloSession.Current.Imei ?? "" },
                    { "visibility", 0 }
                };

                if (!string.IsNullOrEmpty(quoteMsgId))
                {
                    payload["quote"] = quoteMsgId;
                }

                var jsonPayload = JsonConvert.SerializeObject(payload);
                HttpContent httpContent;

                if (!string.IsNullOrEmpty(ZaloSession.Current.ZpwEnk))
                {
                    var encryptedParams = ZaloCrypto.EncryptAesCbc(jsonPayload, ZaloSession.Current.ZpwEnk);
                    var form = new Dictionary<string, string>
                    {
                        { "params", encryptedParams }
                    };
                    httpContent = new FormUrlEncodedContent(form);
                }
                else
                {
                    httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                }

                var req = CreateRequest(HttpMethod.Post, url);
                req.Content = httpContent;

                var res = await _httpClient.SendAsync(req);
                var content = await res.Content.ReadAsStringAsync();

                sentItem.Status = MessageDeliveryStatus.Sent;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloApiClient] SendMessage error: {ex.Message}");
                sentItem.Status = MessageDeliveryStatus.Failed;
            }

            return sentItem;
        }

        /// <summary>
        /// 7. Lấy danh sách bạn bè thật từ /api/social/friend/getfriends
        /// Theo chuẩn zca-js getAllFriends
        /// </summary>
        public async Task<List<FriendItem>> GetFriendsListAsync()
        {
            var list = new List<FriendItem>();
            try
            {
                var baseUrl = ZaloSession.Current.GetServiceUrl("profile", "https://profile-wpa.chat.zalo.me");
                string url;

                var payload = new Dictionary<string, object>
                {
                    { "incInvalid", 1 },
                    { "page", 1 },
                    { "count", 20000 },
                    { "avatar_size", 120 },
                    { "actiontime", 0 },
                    { "imei", ZaloSession.Current.Imei ?? "" }
                };
                var jsonPayload = JsonConvert.SerializeObject(payload);

                if (!string.IsNullOrEmpty(ZaloSession.Current.ZpwEnk))
                {
                    var encryptedParams = ZaloCrypto.EncryptAesCbc(jsonPayload, ZaloSession.Current.ZpwEnk);
                    url = $"{baseUrl}/api/social/friend/getfriends?zpw_ver=671&zpw_type=30&params={Uri.EscapeDataString(encryptedParams)}";
                }
                else
                {
                    url = $"{baseUrl}/api/social/friend/getfriends?zpw_ver=671&zpw_type=30&imei={Uri.EscapeDataString(ZaloSession.Current.Imei ?? "")}";
                }

                var req = CreateRequest(HttpMethod.Get, url);
                var res = await _httpClient.SendAsync(req);
                var content = await res.Content.ReadAsStringAsync();

                var dataToken = SafeExtractData(content);
                if (dataToken != null)
                {
                    var arr = dataToken["items"] as JArray ?? dataToken["friends"] as JArray ?? dataToken as JArray;
                    if (arr != null)
                    {
                        foreach (var f in arr)
                        {
                            try
                            {
                                var uId = f["userId"]?.ToString() ?? f["uid"]?.ToString() ?? f["id"]?.ToString();
                                if (string.IsNullOrEmpty(uId)) continue;

                                var friend = new FriendItem
                                {
                                    UserId = uId,
                                    DisplayName = f["displayName"]?.ToString() ?? f["zaloName"]?.ToString() ?? f["name"]?.ToString() ?? f["dName"]?.ToString() ?? "Bạn Zalo",
                                    AvatarUrl = f["avatar"]?.ToString() ?? f["avatar_url"]?.ToString() ?? f["avt"]?.ToString() ?? "",
                                    PhoneNumber = f["phoneNumber"]?.ToString() ?? f["phone"]?.ToString() ?? "",
                                    StatusMessage = f["status"]?.ToString() ?? "",
                                    IsOnline = f["isOnline"]?.Value<bool>() ?? false
                                };
                                list.Add(friend);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[ZaloApiClient] Parse friend item error: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloApiClient] GetFriendsList error: {ex.Message}");
            }
            return list;
        }

        /// <summary>The legacy phone-transfer endpoint returns 404; do not issue an unsupported request.</summary>
        public Task<bool> RequestPhoneSyncAsync()
        {
            return Task.FromResult(false);
        }
    }
}
