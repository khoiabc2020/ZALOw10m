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
    public static class ZaloFormat
    {
        public static string NormalizeSearch(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            string decomposed = value.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
            var result = new StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category != UnicodeCategory.NonSpacingMark
                    && category != UnicodeCategory.SpacingCombiningMark
                    && category != UnicodeCategory.EnclosingMark)
                    result.Append(c);
            }
            return result.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
        }

        public static bool SearchContains(string value, string normalizedQuery)
        {
            return !string.IsNullOrEmpty(normalizedQuery)
                && NormalizeSearch(value).IndexOf(normalizedQuery, StringComparison.Ordinal) >= 0;
        }

        public static void ReadQuote(JsonObject message, ZaloChatMessage target)
        {
            if (message == null || target == null) return;
            IJsonValue value;
            if (!message.TryGetValue("quote", out value) || value.ValueType != JsonValueType.Object) return;
            JsonObject quote = value.GetObject();
            target.QuotedText = ZaloAuth.Str(quote, "msg");
            target.QuotedSender = ZaloAuth.Str(quote, "fromD");
            if (target.QuotedSender.Length == 0) target.QuotedSender = "Tin nhắn được trích dẫn";
        }

        public static string PlainContent(JsonObject message)
        {
            IJsonValue content;
            if (message == null || !message.TryGetValue("content", out content)
                || content.ValueType != JsonValueType.String) return string.Empty;
            string text = content.GetString();
            // Some message types wrap JSON in a string; only plain text can be quoted safely here.
            if (ZaloJson.ParseObject(text) != null) return string.Empty;
            return text;
        }

        public static void ReadReplyPayload(JsonObject message, ZaloChatMessage target, bool isGroup)
        {
            if (message == null || target == null) return;
            string type = ZaloAuth.Str(message, "msgType");
            if (type == "group.poll" || type == "chat.todo")
            {
                target.ReplyBlocked = true;
                target.ReplyContent = string.Empty;
                return;
            }
            IJsonValue content;
            if (!message.TryGetValue("content", out content)) return;
            if (content.ValueType == JsonValueType.String)
            {
                target.QuoteMessage = content.GetString();
                IJsonValue propertyExt;
                if (message.TryGetValue("propertyExt", out propertyExt))
                {
                    JsonObject properties = propertyExt.ValueType == JsonValueType.Object ? propertyExt.GetObject()
                        : propertyExt.ValueType == JsonValueType.String ? ZaloJson.ParseObject(propertyExt.GetString()) : null;
                    if (properties != null) target.QuoteAttachment = properties.Stringify();
                }
                if (isGroup && ZaloJson.ParseObject(content.GetString()) != null
                    && string.IsNullOrWhiteSpace(target.QuoteAttachment)) target.ReplyBlocked = true;
                return;
            }
            if (content.ValueType != JsonValueType.Object) return;
            if (type == "webchat") { target.ReplyBlocked = true; return; }

            JsonObject source = content.GetObject();
            var attachment = new JsonObject();
            foreach (KeyValuePair<string, IJsonValue> pair in source) attachment[pair.Key] = pair.Value;
            string thumb = FirstString(source, "thumb");
            string href = FirstString(source, "href");
            if (thumb.Length > 0) attachment["thumbUrl"] = JsonValue.CreateStringValue(thumb);
            if (href.Length > 0)
            {
                attachment["oriUrl"] = JsonValue.CreateStringValue(href);
                attachment["normalUrl"] = JsonValue.CreateStringValue(href);
            }
            target.QuoteAttachment = attachment.Stringify();
        }

        public static string StickerId(JsonObject message)
        {
            if (message == null) return string.Empty;
            string[] keys = { "stickerId", "stickerid", "sticker_id", "stickerID", "sid", "eid", "emojiId", "emoji_id", "id" };
            foreach (string key in keys)
            {
                string id = ZaloAuth.Str(message, key);
                if (id.Length > 0) return id;
            }

            IJsonValue value;
            if (!message.TryGetValue("content", out value)) return string.Empty;
            string nestedId = FindStickerId(value, 0);
            if (nestedId.Length > 0) return nestedId;
            return StickerIdFromMediaUrl(FindMediaUrlValue(value));
        }

        private static string StickerIdFromMediaUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return string.Empty;
            foreach (string pair in uri.Query.TrimStart('?').Split('&'))
            {
                int separator = pair.IndexOf('=');
                if (separator <= 0) continue;
                string key = Uri.UnescapeDataString(pair.Substring(0, separator).Replace("+", " "));
                if (!string.Equals(key, "eid", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(key, "sid", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(key, "stickerId", StringComparison.OrdinalIgnoreCase)) continue;
                string id = Uri.UnescapeDataString(pair.Substring(separator + 1).Replace("+", " "));
                if (IsStickerNumber(id)) return id;
            }
            return string.Empty;
        }

        private static string FindStickerId(IJsonValue value, int depth)
        {
            if (value == null || depth > 8) return string.Empty;
            if (value.ValueType == JsonValueType.Object)
                return FindStickerIdInObject(value.GetObject(), depth);
            else if (value.ValueType == JsonValueType.Array)
            {
                foreach (IJsonValue item in value.GetArray())
                {
                    string nested = FindStickerId(item, depth + 1);
                    if (nested.Length > 0) return nested;
                }
            }
            else if (value.ValueType == JsonValueType.String)
            {
                string text = value.GetString();
                JsonObject obj = ZaloJson.ParseObject(text);
                if (obj != null) return FindStickerIdInObject(obj, depth + 1);
                JsonArray array;
                if (JsonArray.TryParse(text, out array))
                {
                    foreach (IJsonValue item in array)
                    {
                        string nested = FindStickerId(item, depth + 1);
                        if (nested.Length > 0) return nested;
                    }
                }
                if (IsStickerNumber(text)) return text;
            }
            return string.Empty;
        }

        private static string FindStickerIdInObject(JsonObject obj, int depth)
        {
            if (obj == null || depth > 8) return string.Empty;
            string[] idKeys = { "stickerId", "stickerid", "sticker_id", "stickerID", "sid", "eid", "emojiId", "emoji_id" };
            foreach (string key in idKeys)
            {
                string id = ZaloAuth.Str(obj, key);
                if (id.Length > 0) return id;
            }
            // Some server payloads expose the sticker's numeric ID as a generic `id`.
            string genericId = ZaloAuth.Str(obj, "id");
            if (IsStickerNumber(genericId)) return genericId;
            foreach (KeyValuePair<string, IJsonValue> pair in obj)
            {
                string nested = FindStickerId(pair.Value, depth + 1);
                if (nested.Length > 0) return nested;
            }
            return string.Empty;
        }

        private static bool IsStickerNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 24) return false;
            foreach (char c in value) if (c < '0' || c > '9') return false;
            return true;
        }

        public static string MediaUrl(JsonObject message)
        {
            if (message == null) return string.Empty;
            string type = ZaloAuth.Str(message, "msgType");
            IJsonValue value;
            JsonObject content = message.TryGetValue("content", out value) && value.ValueType == JsonValueType.Object
                ? value.GetObject()
                : (value != null && value.ValueType == JsonValueType.String ? ZaloJson.ParseObject(value.GetString()) : null);

            if (type == "chat.sticker" || type == "5")
            {
                string direct = FindMediaUrl(content);
                if (direct.Length > 0) return direct;
                string id = StickerId(message);
                return id.Length == 0 ? string.Empty : "https://zalo-api.zadn.vn/api/emoticon/sticker/webpc?eid=" + Uri.EscapeDataString(id) + "&size=130&version=4";
            }
            if (content == null && value != null && value.ValueType == JsonValueType.String)
            {
                string raw = value.GetString();
                if (Uri.IsWellFormedUriString(raw, UriKind.Absolute)) return raw;
                JsonArray array;
                if (JsonArray.TryParse(raw, out array))
                    foreach (IJsonValue item in array)
                        if (item.ValueType == JsonValueType.Object)
                        {
                            string arrayUrl = FindMediaUrl(item.GetObject());
                            if (arrayUrl.Length > 0) return arrayUrl;
                        }
            }
            else if (content == null && value != null && value.ValueType == JsonValueType.Array)
            {
                foreach (IJsonValue item in value.GetArray())
                {
                    string arrayUrl = FindMediaUrlValue(item);
                    if (arrayUrl.Length > 0) return arrayUrl;
                }
            }
            return FindMediaUrl(content);
        }

        public static string MediaFullUrl(JsonObject message)
        {
            string fallback = MediaUrl(message);
            if (message == null || (ZaloAuth.Str(message, "msgType") != "chat.photo"
                && ZaloAuth.Str(message, "msgType") != "chat.gif"
                && ZaloAuth.Str(message, "msgType") != "chat.doodle")) return fallback;
            IJsonValue content;
            if (!message.TryGetValue("content", out content)) return fallback;
            string full = FindMediaUrlByPriority(content, new[]
            {
                "hdUrl", "oriUrl", "rawUrl", "normalUrl", "originUrl", "mediaUrl", "imageUrl",
                "url", "href", "photoUrl", "fileUrl", "downloadUrl", "thumbnailUrl", "thumbnail", "thumbUrl"
            }, 0);
            return full.Length > 0 ? full : fallback;
        }

        private static string FindMediaUrlByPriority(IJsonValue value, string[] keys, int depth)
        {
            if (value == null || depth > 12) return string.Empty;
            if (value.ValueType == JsonValueType.Object)
            {
                JsonObject obj = value.GetObject();
                foreach (string key in keys)
                {
                    IJsonValue field;
                    if (obj.TryGetValue(key, out field) && field.ValueType == JsonValueType.String)
                    {
                        string url = NormalizeMediaUrl(field.GetString());
                        if (url.Length > 0) return url;
                    }
                }
                foreach (KeyValuePair<string, IJsonValue> pair in obj)
                {
                    string url = FindMediaUrlByPriority(pair.Value, keys, depth + 1);
                    if (url.Length > 0) return url;
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                foreach (IJsonValue item in value.GetArray())
                {
                    string url = FindMediaUrlByPriority(item, keys, depth + 1);
                    if (url.Length > 0) return url;
                }
            }
            else if (value.ValueType == JsonValueType.String)
            {
                string raw = value.GetString();
                string direct = NormalizeMediaUrl(raw);
                if (direct.Length > 0) return direct;
                JsonObject obj = ZaloJson.ParseObject(raw);
                if (obj != null) return FindMediaUrlByPriority(obj, keys, depth + 1);
                JsonArray array;
                if (JsonArray.TryParse(raw, out array))
                    foreach (IJsonValue item in array)
                    {
                        string url = FindMediaUrlByPriority(item, keys, depth + 1);
                        if (url.Length > 0) return url;
                    }
            }
            return string.Empty;
        }

        private static string NormalizeMediaUrl(string value)
        {
            string url = (value ?? string.Empty).Trim();
            if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri)
                && (uri.Scheme == "http" || uri.Scheme == "https") ? url : string.Empty;
        }

        private static string FindMediaUrlValue(IJsonValue value)
        {
            if (value == null) return string.Empty;
            if (value.ValueType == JsonValueType.Object) return FindMediaUrl(value.GetObject());
            if (value.ValueType == JsonValueType.Array)
            {
                foreach (IJsonValue item in value.GetArray())
                {
                    string url = FindMediaUrlValue(item);
                    if (url.Length > 0) return url;
                }
            }
            else if (value.ValueType == JsonValueType.String)
            {
                string raw = value.GetString();
                string url = NormalizeMediaUrl(raw);
                if (url.Length > 0) return url;
                JsonObject parsed = ZaloJson.ParseObject(raw);
                if (parsed != null) return FindMediaUrl(parsed);
                JsonArray array;
                if (JsonArray.TryParse(raw, out array))
                    foreach (IJsonValue item in array)
                    {
                        url = FindMediaUrlValue(item);
                        if (url.Length > 0) return url;
                    }
            }
            return string.Empty;
        }

        public static string FileName(JsonObject message)
        {
            if (message == null) return string.Empty;
            IJsonValue value;
            JsonObject content = message.TryGetValue("content", out value) && value.ValueType == JsonValueType.Object
                ? value.GetObject()
                : (value != null && value.ValueType == JsonValueType.String ? ZaloJson.ParseObject(value.GetString()) : null);
            return FindTextField(content, new[] { "fileName", "filename", "name", "title" });
        }

        private static string FindTextField(JsonObject content, string[] keys)
        {
            if (content == null) return string.Empty;
            foreach (string key in keys)
            {
                string value = ZaloAuth.Str(content, key);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            string[] nestedKeys = { "content", "params", "payload", "data", "attachment", "attachments", "file", "media" };
            foreach (string key in nestedKeys)
            {
                IJsonValue nested;
                if (!content.TryGetValue(key, out nested)) continue;
                if (nested.ValueType == JsonValueType.Object)
                {
                    string value = FindTextField(nested.GetObject(), keys);
                    if (value.Length > 0) return value;
                }
                else if (nested.ValueType == JsonValueType.String)
                {
                    JsonObject parsed = ZaloJson.ParseObject(nested.GetString());
                    string value = FindTextField(parsed, keys);
                    if (value.Length > 0) return value;
                    JsonArray array;
                    if (JsonArray.TryParse(nested.GetString(), out array))
                        foreach (IJsonValue item in array)
                            if (item.ValueType == JsonValueType.Object)
                            {
                                value = FindTextField(item.GetObject(), keys);
                                if (value.Length > 0) return value;
                            }
                }
                else if (nested.ValueType == JsonValueType.Array)
                    foreach (IJsonValue item in nested.GetArray())
                        if (item.ValueType == JsonValueType.Object)
                        {
                            string value = FindTextField(item.GetObject(), keys);
                            if (value.Length > 0) return value;
                        }
            }
            return string.Empty;
        }

        private static string FindMediaUrl(JsonObject content)
        {
            if (content == null) return string.Empty;
            string[] keys = { "url", "href", "fileUrl", "photoUrl", "normalUrl", "hdUrl", "oriUrl", "rawUrl", "originUrl", "mediaUrl", "imageUrl", "downloadUrl", "thumbnailUrl", "thumbnail", "thumbUrl", "stickerUrl", "sticker_url", "stickerWebpUrl", "webpUrl", "voiceUrl", "m4aUrl", "thumb" };
            foreach (string key in keys)
            {
                string url = NormalizeMediaUrl(ZaloAuth.Str(content, key));
                if (url.Length > 0) return url;
            }

            string[] nestedKeys = { "content", "params", "attachment", "attachments", "payload", "data", "file", "image", "photo", "media", "sticker" };
            foreach (string key in nestedKeys)
            {
                IJsonValue nested;
                if (!content.TryGetValue(key, out nested)) continue;
                if (nested.ValueType == JsonValueType.Object)
                {
                    string nestedUrl = FindMediaUrl(nested.GetObject());
                    if (nestedUrl.Length > 0) return nestedUrl;
                }
                else if (nested.ValueType == JsonValueType.String)
                {
                    string nestedUrl = FindMediaUrlValue(nested);
                    if (nestedUrl.Length > 0) return nestedUrl;
                }
                else if (nested.ValueType == JsonValueType.Array)
                {
                    string nestedUrl = FindMediaUrlValue(nested);
                    if (nestedUrl.Length > 0) return nestedUrl;
                }
            }
            return string.Empty;
        }

        public static string Time(long unixMs)
        {
            if (unixMs <= 0) return string.Empty;
            DateTime local = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
            return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("dd/MM HH:mm");
        }

        /// <summary>A short text for a message, including placeholders for what this version cannot show.</summary>
        public static string Describe(JsonObject m)
        {
            IJsonValue content;
            if (!m.TryGetValue("content", out content)) return string.Empty;

            switch (ZaloAuth.Str(m, "msgType"))
            {
                case "chat.photo": return "[Ảnh]";
                case "chat.doodle": return "[Bản vẽ]";
                case "chat.sticker":
                case "5": return StickerId(m).Length > 0 ? "" : "[Nhãn dán]";
                case "chat.voice": return "[Tin nhắn thoại]";
                case "31": return "[Tin nhắn thoại]";
                case "chat.video.msg": return "[Video]";
                case "44": return "[Video]";
                case "46": return "[Tệp]";
                case "chat.gif": return "[Ảnh động]";
                case "chat.location.new": return "[Vị trí]";
                case "group.poll": return "[Bình chọn]";
                case "chat.recommended":
                case "6":
                case "38": return "[Danh thiếp]";
            }

            if (content.ValueType == JsonValueType.String)
            {
                string text = content.GetString();
                JsonObject parsed = ZaloJson.ParseObject(text);
                if (parsed != null)
                {
                    string phone = ZaloAuth.Str(parsed, "phone");
                    if (phone.Length == 0) phone = ZaloAuth.Str(parsed, "phoneNumber");
                    string qr = ZaloAuth.Str(parsed, "qrCodeUrl");
                    if (qr.Length == 0) qr = ZaloAuth.Str(parsed, "qrCode");
                    string uid = ZaloAuth.Str(parsed, "contactUid");
                    if (uid.Length == 0) uid = ZaloAuth.Str(parsed, "userId");

                    if (phone.Length > 0 || qr.Length > 0 || uid.Length > 0)
                    {
                        string name = ZaloAuth.Str(parsed, "displayName");
                        if (name.Length == 0) name = ZaloAuth.Str(parsed, "name");
                        if (name.Length == 0) name = phone.Length > 0 ? phone : uid;
                        return "[Danh thiếp] " + name;
                    }

                    string title = ZaloAuth.Str(parsed, "title");
                    string href = ZaloAuth.Str(parsed, "href");
                    if (title.Length > 0 || href.Length > 0)
                        return (title + " " + href).Trim();
                }
                return SanitizePreviewText(text);
            }

            if (content.ValueType == JsonValueType.Object)
            {
                JsonObject o = content.GetObject();
                string title = ZaloAuth.Str(o, "title");
                string href = ZaloAuth.Str(o, "href");
                string text = (title + " " + href).Trim();
                if (ZaloAuth.Str(m, "msgType") == "share.file") return "[Tệp] " + title;
                if (text.Length > 0) return text;
            }
            return "[Tin nhắn chưa hỗ trợ]";
        }

        public static void ReadLinkPreview(JsonObject message, ZaloChatMessage target)
        {
            if (message == null || target == null) return;
            string msgType = ZaloAuth.Str(message, "msgType");
            if (msgType == "chat.gif" || msgType == "chat.photo" || msgType == "chat.sticker" || msgType == "5" || msgType == "chat.doodle") return;
            if (target.HasMedia) return;
            IJsonValue raw;
            if (!message.TryGetValue("content", out raw)) return;
            JsonObject content = raw.ValueType == JsonValueType.Object ? raw.GetObject()
                : raw.ValueType == JsonValueType.String ? ZaloJson.ParseObject(raw.GetString()) : null;
            if (content == null) return;
            string title = FirstString(content, "title", "mediaTitle");
            string description = FirstString(content, "desc", "description");
            string href = FirstString(content, "href", "url", "link");
            string image = FirstString(content, "thumb", "thumbUrl", "src", "stream_icon");
            if (href.Length == 0) href = FirstHttpUrl(target.Text);
            if (title.Length == 0 && href.Length == 0) return;
            if (href.Contains("dlfl.vn") || href.Contains("zadn.vn")) return;
            target.SetLinkPreview(title, description, href, image);
        }

        public static void ReadRecommendedLinkPreview(JsonObject message, ZaloChatMessage target)
        {
            if (message == null || target == null) return;
            string messageType = ZaloAuth.Str(message, "msgType");
            if (messageType != "chat.recommended" && messageType != "6" && messageType != "38") return;
            IJsonValue raw;
            JsonObject content = message.TryGetValue("content", out raw)
                ? (raw.ValueType == JsonValueType.Object ? raw.GetObject()
                    : raw.ValueType == JsonValueType.String ? ZaloJson.ParseObject(raw.GetString()) : null)
                : null;
            if (content == null && message.TryGetValue("msgInfo", out raw))
                content = raw.ValueType == JsonValueType.Object ? raw.GetObject()
                    : raw.ValueType == JsonValueType.String ? ZaloJson.ParseObject(raw.GetString()) : null;
            string rawText = raw != null && raw.ValueType == JsonValueType.String ? raw.GetString() : string.Empty;
            string href = content == null ? FirstHttpUrl(rawText)
                : FindTextField(content, new[] { "href", "url", "link", "shareUrl", "groupUrl", "communityUrl" });
            if (href.Length == 0 && content != null) href = FirstHttpUrl(content.Stringify());
            Uri uri;
            if (!Uri.TryCreate(href, UriKind.Absolute, out uri)
                || (uri.Scheme != "http" && uri.Scheme != "https")) return;

            string title = content == null ? string.Empty
                : FindTextField(content, new[] { "title", "groupName", "communityName", "name" });
            string description = content == null ? rawText
                : FindTextField(content, new[] { "description", "desc", "caption", "message", "msg", "text", "contentText" });
            string thumbnail = content == null ? string.Empty
                : FindTextField(content, new[] { "thumb", "thumbUrl", "thumbnailUrl", "cover", "avatar", "src", "stream_icon" });
            bool isCommunityLink = uri.Host.EndsWith("zalo.me", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/g/", StringComparison.OrdinalIgnoreCase);
            string previewTitle = title.Length > 0 ? title : (isCommunityLink ? "Cộng đồng Zalo" : "Liên kết được chia sẻ");
            target.SetLinkPreview(previewTitle,
                description, href, thumbnail);

            if (target.Text == "[Danh thiếp]")
                target.Text = description.Length > 0 ? description : (title + " " + href).Trim();
        }

        public static void ReadLocation(JsonObject message, ZaloChatMessage target)
        {
            if (message == null || target == null || ZaloAuth.Str(message, "msgType") != "chat.location.new") return;
            IJsonValue raw;
            if (!message.TryGetValue("content", out raw)) return;
            JsonObject content = raw.ValueType == JsonValueType.Object ? raw.GetObject()
                : raw.ValueType == JsonValueType.String ? ZaloJson.ParseObject(raw.GetString()) : null;
            if (content == null) return;
            double latitude, longitude;
            if (!FindLocationCoordinates(content, 0, out latitude, out longitude)) return;
            target.SetLocation(latitude, longitude, FirstString(content, "title", "address", "name", "desc"));
        }

        public static void ReadPoll(JsonObject message, ZaloChatMessage target)
        {
            if (message == null || target == null) return;
            string type = ZaloAuth.Str(message, "msgType");
            if (type.IndexOf("poll", StringComparison.OrdinalIgnoreCase) < 0) return;
            IJsonValue raw;
            if (!message.TryGetValue("content", out raw)) return;
            JsonObject content = raw.ValueType == JsonValueType.Object ? raw.GetObject()
                : raw.ValueType == JsonValueType.String ? ZaloJson.ParseObject(raw.GetString()) : null;
            long pollId;
            if (!FindPollId(content, 0, out pollId)) return;
            target.SetPoll(pollId, FirstString(content, "question", "title", "name"));
        }

        public static string SanitizePreviewText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string prefix = string.Empty;
            string trimmed = text.Trim();
            int colonIdx = trimmed.IndexOf(':');
            if (colonIdx >= 0 && colonIdx < trimmed.Length - 1)
            {
                string afterColon = trimmed.Substring(colonIdx + 1).TrimStart();
                if (afterColon.StartsWith("{") || afterColon.Contains("dlfl.vn") || afterColon.Contains("zadn.vn"))
                {
                    prefix = trimmed.Substring(0, colonIdx + 1) + " ";
                    trimmed = afterColon;
                }
            }

            if (trimmed.StartsWith("{") && (trimmed.Contains("\"phone\"") || trimmed.Contains("\"qrCodeUrl\"") || trimmed.Contains("\"contactUid\"") || trimmed.Contains("\"phoneNumber\"")))
            {
                try
                {
                    JsonObject cardObj = ZaloJson.ParseObject(trimmed);
                    if (cardObj != null)
                    {
                        string phone = ZaloAuth.Str(cardObj, "phone");
                        if (phone.Length == 0) phone = ZaloAuth.Str(cardObj, "phoneNumber");
                        string name = ZaloAuth.Str(cardObj, "displayName");
                        if (name.Length == 0) name = ZaloAuth.Str(cardObj, "name");
                        string display = name.Length > 0 ? name : phone;
                        return prefix + (string.IsNullOrEmpty(display) ? "[Danh thiếp]" : "[Danh thiếp] " + display);
                    }
                }
                catch { }
                return prefix + "[Danh thiếp]";
            }

            if (trimmed.Contains("dlfl.vn") || trimmed.Contains("zadn.vn/api/emoticon") || trimmed.Contains(".zdn.vn/emoticon"))
            {
                return prefix + "[Nhãn dán]";
            }

            return text;
        }

        public static void ReadContactCard(JsonObject message, ZaloChatMessage target)
        {
            if (message == null || target == null || target.HasLinkPreview) return;
            IJsonValue raw;
            JsonObject content = null;
            if (message.TryGetValue("content", out raw))
                content = raw.ValueType == JsonValueType.Object ? raw.GetObject()
                    : raw.ValueType == JsonValueType.String ? ZaloJson.ParseObject(raw.GetString()) : null;
            if (content == null && message.TryGetValue("msgInfo", out raw))
                content = raw.ValueType == JsonValueType.Object ? raw.GetObject()
                    : raw.ValueType == JsonValueType.String ? ZaloJson.ParseObject(raw.GetString()) : null;
            if (content == null) return;

            string uid = FindTextField(content, new[] { "contactUid", "userId", "uid" });
            string qr = FindTextField(content, new[] { "qrCodeUrl", "qrCode", "qr" });
            string phone = FindTextField(content, new[] { "phone", "phoneNumber" });
            if (uid.Length == 0 && phone.Length == 0 && qr.Length == 0) return;
            if (uid.Length == 0) uid = phone;
            ZaloFriend friend = ZaloApp.Friends == null ? null : ZaloApp.Friends.FirstOrDefault(item => 
                (!string.IsNullOrEmpty(uid) && item.UserId == uid) || 
                (!string.IsNullOrEmpty(phone) && item.PhoneNumber == phone));
            string name = FindTextField(content, new[] { "displayName", "name", "title" });
            if (name.Length == 0 && friend != null) name = friend.Name;
            if (name.Length == 0) name = phone.Length > 0 ? phone : uid;
            string avatar = FindTextField(content, new[] { "avatar", "avatarUrl" });
            if (avatar.Length == 0 && friend != null) avatar = friend.Avatar;
            target.SetContactCard(uid, name, avatar, qr, phone);
        }

        private static bool FindPollId(JsonObject obj, int depth, out long pollId)
        {
            pollId = 0;
            if (obj == null || depth > 8) return false;
            foreach (string key in new[] { "poll_id", "pollId" })
            {
                IJsonValue value;
                if (!obj.TryGetValue(key, out value)) continue;
                if (value.ValueType == JsonValueType.Number) pollId = (long)value.GetNumber();
                else if (value.ValueType == JsonValueType.String) long.TryParse(value.GetString(), out pollId);
                if (pollId > 0) return true;
            }
            foreach (KeyValuePair<string, IJsonValue> pair in obj)
            {
                JsonObject nested = pair.Value.ValueType == JsonValueType.Object ? pair.Value.GetObject()
                    : pair.Value.ValueType == JsonValueType.String ? ZaloJson.ParseObject(pair.Value.GetString()) : null;
                if (FindPollId(nested, depth + 1, out pollId)) return true;
                if (pair.Value.ValueType == JsonValueType.Array)
                    foreach (IJsonValue item in pair.Value.GetArray())
                        if (item.ValueType == JsonValueType.Object && FindPollId(item.GetObject(), depth + 1, out pollId)) return true;
            }
            pollId = 0;
            return false;
        }

        private static bool FindLocationCoordinates(JsonObject obj, int depth, out double latitude, out double longitude)
        {
            latitude = 0;
            longitude = 0;
            if (obj == null || depth > 8) return false;
            bool hasLatitude = TryGetNumber(obj, out latitude, "lat", "latitude");
            bool hasLongitude = TryGetNumber(obj, out longitude, "lng", "lon", "longitude");
            if (hasLatitude && hasLongitude && latitude >= -90 && latitude <= 90 && longitude >= -180 && longitude <= 180)
                return true;
            foreach (KeyValuePair<string, IJsonValue> pair in obj)
            {
                JsonObject nested = pair.Value.ValueType == JsonValueType.Object ? pair.Value.GetObject()
                    : pair.Value.ValueType == JsonValueType.String ? ZaloJson.ParseObject(pair.Value.GetString()) : null;
                if (FindLocationCoordinates(nested, depth + 1, out latitude, out longitude)) return true;
                if (pair.Value.ValueType == JsonValueType.Array)
                {
                    foreach (IJsonValue item in pair.Value.GetArray())
                        if (item.ValueType == JsonValueType.Object
                            && FindLocationCoordinates(item.GetObject(), depth + 1, out latitude, out longitude)) return true;
                }
            }
            latitude = 0;
            longitude = 0;
            return false;
        }

        private static bool TryGetNumber(JsonObject obj, out double value, params string[] keys)
        {
            foreach (string key in keys)
            {
                IJsonValue raw;
                if (!obj.TryGetValue(key, out raw)) continue;
                if (raw.ValueType == JsonValueType.Number)
                {
                    value = raw.GetNumber();
                    return true;
                }
                if (raw.ValueType == JsonValueType.String
                    && double.TryParse(raw.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
            }
            value = 0;
            return false;
        }

        private static string FirstString(JsonObject obj, params string[] keys)
        {
            foreach (string key in keys)
            {
                string value = ZaloAuth.Str(obj, key).Trim();
                if (value.Length > 0) return value;
            }
            return string.Empty;
        }

        public static string FirstHttpUrl(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            foreach (string token in text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = token.TrimStart('(', '[', '{', '<').TrimEnd(')', ']', '}', '>', ',', '.', ';', '!', '?', '\'', '"');
                Uri uri;
                if (candidate.Length <= 2048 && Uri.TryCreate(candidate, UriKind.Absolute, out uri)
                    && (uri.Scheme == "http" || uri.Scheme == "https")
                    && !string.IsNullOrWhiteSpace(uri.Host)) return uri.AbsoluteUri;
            }
            return string.Empty;
        }
    }


}
