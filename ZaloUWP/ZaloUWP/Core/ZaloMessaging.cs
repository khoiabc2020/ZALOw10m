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
    // ===================================================================== sending and groups

    public sealed class ZaloSticker
    {
        public string Id { get; set; }
        public long CategoryId { get; set; }
        public int Type { get; set; }
        public string ImageUrl { get; set; }
    }

    public sealed class ZaloStickerSendResult
    {
        public string MessageId { get; set; }
        public string ClientMessageId { get; set; }
    }

    public sealed class ZaloVoiceSendResult
    {
        public string MessageId { get; set; }
        public string ClientMessageId { get; set; }
        public string VoiceUrl { get; set; }
        public long FileSize { get; set; }
    }

    public sealed class ZaloFileSendResult
    {
        public string MessageId { get; set; }
        public string ClientMessageId { get; set; }
        public string FileUrl { get; set; }
        public string FileName { get; set; }
        public string MsgType { get; set; }
    }

    public sealed class ZaloContactCardSendResult
    {
        public string MessageId { get; set; }
        public string ClientMessageId { get; set; }
        public string QrCodeUrl { get; set; }
    }

    public sealed class ZaloQuickMessage
    {
        public int Id { get; set; }
        public string Keyword { get; set; }
        public string Title { get; set; }
        public int Type { get; set; }
    }

    public static class ZaloMessaging
    {
        public static async Task<List<ZaloQuickMessage>> GetQuickMessagesAsync(ZaloSession s, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            var request = new JsonObject();
            request["version"] = JsonValue.CreateNumberValue(0);
            request["lang"] = JsonValue.CreateNumberValue(0);
            request["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("quick_message") + "/api/quickmessage/list",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, request.Stringify())));
            IJsonValue response = await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
            JsonArray items = ArrayValue(response, "items");
            var quickMessages = new List<ZaloQuickMessage>();
            if (items == null) return quickMessages;
            foreach (IJsonValue value in items)
            {
                JsonObject item = ObjectValue(value);
                if (item == null) continue;
                JsonObject message = ObjectValue(GetProperty(item, "message"));
                string title = ZaloAuth.Str(message, "title");
                if (title.Length == 0) continue;
                quickMessages.Add(new ZaloQuickMessage
                {
                    Id = (int)ZaloAuth.Num(item, "id"),
                    Keyword = ZaloAuth.Str(item, "keyword"),
                    Title = title,
                    Type = (int)ZaloAuth.Num(item, "type")
                });
            }
            return quickMessages;
        }

        public static async Task AddQuickMessageAsync(ZaloSession s, string keyword, string title, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            string cleanKeyword = (keyword ?? string.Empty).Trim();
            string cleanTitle = (title ?? string.Empty).Trim();
            if (cleanKeyword.Length == 0 || cleanTitle.Length == 0)
                throw new ArgumentException("Nhập từ khóa và nội dung tin nhắn nhanh.");
            var message = new JsonObject();
            message["title"] = JsonValue.CreateStringValue(cleanTitle);
            message["params"] = JsonValue.CreateStringValue(string.Empty);
            var request = new JsonObject();
            request["keyword"] = JsonValue.CreateStringValue(cleanKeyword);
            request["message"] = message;
            request["type"] = JsonValue.CreateNumberValue(0);
            request["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("quick_message") + "/api/quickmessage/create",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, request.Stringify())));
            await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
        }

        public static async Task UpdateQuickMessageAsync(ZaloSession s, int itemId, string keyword, string title, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            string cleanKeyword = (keyword ?? string.Empty).Trim();
            string cleanTitle = (title ?? string.Empty).Trim();
            if (itemId <= 0 || cleanKeyword.Length == 0 || cleanTitle.Length == 0)
                throw new ArgumentException("Nhập từ khóa và nội dung tin nhắn nhanh.");
            var message = new JsonObject();
            message["title"] = JsonValue.CreateStringValue(cleanTitle);
            message["params"] = JsonValue.CreateStringValue(string.Empty);
            var request = new JsonObject();
            request["itemId"] = JsonValue.CreateNumberValue(itemId);
            request["keyword"] = JsonValue.CreateStringValue(cleanKeyword);
            request["message"] = message;
            request["type"] = JsonValue.CreateNumberValue(0);
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("quick_message") + "/api/quickmessage/update",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, request.Stringify())));
            await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
        }

        public static async Task DeleteQuickMessageAsync(ZaloSession s, int itemId, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (itemId <= 0) throw new ArgumentException("Tin nhắn nhanh không hợp lệ.");
            var ids = new JsonArray();
            ids.Add(JsonValue.CreateNumberValue(itemId));
            var request = new JsonObject();
            request["itemIds"] = ids;
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("quick_message") + "/api/quickmessage/delete",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, request.Stringify())));
            await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
        }

        public static async Task<ZaloContactCardSendResult> SendContactCardAsync(ZaloSession s,
            ZaloConversation conversation, ZaloFriend contact, CancellationToken ct)
        {
            if (s == null || conversation == null || contact == null || string.IsNullOrWhiteSpace(contact.UserId))
                throw new InvalidOperationException("Thiếu phiên đăng nhập, cuộc trò chuyện hoặc liên hệ.");

            var qrRequest = new JsonObject();
            var friendIds = new JsonArray();
            friendIds.Add(JsonValue.CreateStringValue(contact.UserId));
            qrRequest["fids"] = friendIds;
            string qrUrl = ZaloAuth.ApiUrl(s.ServiceUrl("friend") + "/api/friend/mget-qr");
            var qrForm = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, qrRequest.Stringify()))
            };
            JsonObject qrResponse = ObjectValue(await ZaloAuth.CallAsync(s, HttpMethod.Post, qrUrl, qrForm, ct));
            string contactQr = ZaloAuth.Str(qrResponse, contact.UserId);
            if (contactQr.Length == 0)
                throw new InvalidOperationException("Zalo không trả mã QR cho liên hệ này.");

            long clientId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var msgInfo = new JsonObject();
            msgInfo["contactUid"] = JsonValue.CreateStringValue(contact.UserId);
            msgInfo["qrCodeUrl"] = JsonValue.CreateStringValue(contactQr);
            var payload = new JsonObject();
            payload["ttl"] = JsonValue.CreateNumberValue(0);
            payload["msgType"] = JsonValue.CreateNumberValue(6);
            payload["clientId"] = JsonValue.CreateStringValue(clientId.ToString(CultureInfo.InvariantCulture));
            payload["msgInfo"] = JsonValue.CreateStringValue(msgInfo.Stringify());
            if (conversation.IsGroup)
            {
                payload["visibility"] = JsonValue.CreateNumberValue(0);
                payload["grid"] = JsonValue.CreateStringValue(conversation.ThreadId);
            }
            else
            {
                payload["toId"] = JsonValue.CreateStringValue(conversation.ThreadId);
                payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            }

            string route = conversation.IsGroup ? "/api/group/forward" : "/api/message/forward";
            string sendUrl = ZaloAuth.ApiUrl(s.ServiceUrl("file") + route);
            var sendForm = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
            };
            JsonObject sent = ObjectValue(await ZaloAuth.CallAsync(s, HttpMethod.Post, sendUrl, sendForm, ct));
            if (sent == null || ZaloAuth.Str(sent, "msgId").Length == 0)
                throw new InvalidOperationException("Máy chủ chưa xác nhận việc gửi danh thiếp.");
            return new ZaloContactCardSendResult
            {
                MessageId = ZaloAuth.Str(sent, "msgId"),
                ClientMessageId = clientId.ToString(CultureInfo.InvariantCulture),
                QrCodeUrl = contactQr
            };
        }

        public static async Task SendTypingAsync(ZaloSession s, ZaloConversation conversation, CancellationToken ct)
        {
            if (s == null || conversation == null || string.IsNullOrWhiteSpace(conversation.ThreadId)) return;
            var payload = new JsonObject();
            payload[conversation.IsGroup ? "grid" : "toid"] = JsonValue.CreateStringValue(conversation.ThreadId);
            if (!conversation.IsGroup) payload["destType"] = JsonValue.CreateNumberValue(3);
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string route = conversation.IsGroup ? "/api/group/typing" : "/api/message/typing";
            string service = conversation.IsGroup ? "group" : "chat";
            string url = ZaloAuth.ApiUrl(s.ServiceUrl(service) + route);
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
            };
            await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
        }

        public static async Task<ZaloVoiceSendResult> SendVoiceAsync(ZaloSession s, ZaloListener listener,
            string threadId, bool isGroup, string fileName, IInputStream input, long totalSize, CancellationToken ct)
        {
            if (s == null || listener == null) throw new InvalidOperationException("Chưa kết nối dịch vụ tin nhắn.");
            if (string.IsNullOrWhiteSpace(threadId)) throw new InvalidOperationException("Thiếu người nhận tin nhắn thoại.");
            if (input == null || totalSize <= 0) throw new InvalidOperationException("Tệp ghi âm rỗng.");
            if (totalSize > 20L * 1024 * 1024) throw new InvalidOperationException("Bản ghi vượt giới hạn 20 MB.");

            int chunkSize = 1024 * 1024;
            try
            {
                chunkSize = (int)s.Settings.GetNamedObject("features").GetNamedObject("sharefile").GetNamedNumber("chunk_size_file");
                if (chunkSize < 64 * 1024) chunkSize = 1024 * 1024;
            }
            catch { }

            long clientId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string uploadFileId = string.Empty;
            int totalChunks = (int)((totalSize + chunkSize - 1) / chunkSize);
            using (var reader = new DataReader(input))
            {
                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    int length = (int)Math.Min(chunkSize, totalSize - (long)chunkIndex * chunkSize);
                    byte[] chunk = await ReadUploadChunkAsync(reader, length, ct);
                    var parameters = new JsonObject();
                    parameters[isGroup ? "grid" : "toid"] = JsonValue.CreateStringValue(threadId);
                    parameters["totalChunk"] = JsonValue.CreateNumberValue(totalChunks);
                    parameters["fileName"] = JsonValue.CreateStringValue(fileName);
                    parameters["clientId"] = JsonValue.CreateNumberValue(clientId);
                    parameters["totalSize"] = JsonValue.CreateNumberValue(totalSize);
                    parameters["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
                    parameters["isE2EE"] = JsonValue.CreateNumberValue(0);
                    parameters["jxl"] = JsonValue.CreateNumberValue(0);
                    parameters["chunkId"] = JsonValue.CreateNumberValue(chunkIndex + 1);
                    string encrypted = ZaloCrypto.Encode(s.SecretKey, parameters.Stringify());
                    string type = isGroup ? "11" : "2";
                    string uploadUrl = ZaloAuth.ApiUrl(s.ServiceUrl("file") + "/api/" + (isGroup ? "group" : "message") + "/asyncfile/upload",
                        new KeyValuePair<string, string>("type", type), new KeyValuePair<string, string>("params", encrypted));
                    IJsonValue uploadData = await ZaloAuth.CallMultipartAsync(s, uploadUrl, fileName, chunk, ct);
                    JsonObject uploadResult = uploadData != null && uploadData.ValueType == JsonValueType.Object ? uploadData.GetObject() : null;
                    if (uploadResult == null) throw new InvalidOperationException("Máy chủ không trả thông tin tải tệp.");
                    string currentFileId = ZaloAuth.Str(uploadResult, "fileId");
                    if (currentFileId.Length == 0 || currentFileId == "-1")
                        throw new InvalidOperationException("Máy chủ từ chối tệp ghi âm.");
                    uploadFileId = currentFileId;
                }
            }

            string voiceUrl = await listener.WaitForUploadedFileAsync(uploadFileId, TimeSpan.FromSeconds(60));
            long voiceClientId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var msgInfo = new JsonObject();
            msgInfo["voiceUrl"] = JsonValue.CreateStringValue(voiceUrl);
            msgInfo["m4aUrl"] = JsonValue.CreateStringValue(voiceUrl);
            msgInfo["fileSize"] = JsonValue.CreateNumberValue(totalSize);

            var voicePayload = new JsonObject();
            if (isGroup)
            {
                voicePayload["grid"] = JsonValue.CreateStringValue(threadId);
                voicePayload["visibility"] = JsonValue.CreateNumberValue(0);
            }
            else voicePayload["toId"] = JsonValue.CreateStringValue(threadId);
            voicePayload["ttl"] = JsonValue.CreateNumberValue(0);
            voicePayload["zsource"] = JsonValue.CreateNumberValue(-1);
            voicePayload["msgType"] = JsonValue.CreateNumberValue(3);
            voicePayload["clientId"] = JsonValue.CreateStringValue(voiceClientId.ToString());
            voicePayload["msgInfo"] = JsonValue.CreateStringValue(msgInfo.Stringify());
            voicePayload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string sendUrl = ZaloAuth.ApiUrl(s.ServiceUrl("file") + "/api/" + (isGroup ? "group" : "message") + "/forward");
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, voicePayload.Stringify()))
            };
            IJsonValue sentData = await ZaloAuth.CallAsync(s, HttpMethod.Post, sendUrl, form, ct);
            JsonObject sent = sentData != null && sentData.ValueType == JsonValueType.Object ? sentData.GetObject() : null;
            if (sent == null) throw new InvalidOperationException("Máy chủ không xác nhận việc gửi tin nhắn thoại.");
            return new ZaloVoiceSendResult
            {
                MessageId = ZaloAuth.Str(sent, "msgId"),
                ClientMessageId = voiceClientId.ToString(),
                VoiceUrl = voiceUrl,
                FileSize = totalSize
            };
        }

        public static async Task<ZaloFileSendResult> SendFileAsync(ZaloSession s, ZaloListener listener,
            string threadId, bool isGroup, string fileName, IInputStream input, long totalSize, CancellationToken ct)
        {
            if (s == null || listener == null) throw new InvalidOperationException("Chưa kết nối dịch vụ tin nhắn.");
            if (string.IsNullOrWhiteSpace(threadId)) throw new InvalidOperationException("Thiếu người nhận tệp.");
            if (input == null || totalSize <= 0) throw new InvalidOperationException("Tệp không có dữ liệu.");
            if (totalSize > 20L * 1024 * 1024) throw new InvalidOperationException("Tệp vượt giới hạn 20 MB.");

            int chunkSize = 1024 * 1024;
            try
            {
                chunkSize = (int)s.Settings.GetNamedObject("features").GetNamedObject("sharefile").GetNamedNumber("chunk_size_file");
                if (chunkSize < 64 * 1024) chunkSize = 1024 * 1024;
            }
            catch { }

            string route = isGroup ? "group" : "message";
            string type = isGroup ? "11" : "2";
            long clientId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int totalChunks = (int)((totalSize + chunkSize - 1) / chunkSize);
            string fileId = string.Empty;
            long clientFileId = clientId;
            string checksum;
            CryptographicHash hash = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Md5).CreateHash();
            using (var reader = new DataReader(input))
            {
                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    int length = (int)Math.Min(chunkSize, totalSize - (long)chunkIndex * chunkSize);
                    byte[] chunk = await ReadUploadChunkAsync(reader, length, ct);
                    hash.Append(CryptographicBuffer.CreateFromByteArray(chunk));

                    var parameters = new JsonObject();
                    parameters[isGroup ? "grid" : "toid"] = JsonValue.CreateStringValue(threadId);
                    parameters["totalChunk"] = JsonValue.CreateNumberValue(totalChunks);
                    parameters["fileName"] = JsonValue.CreateStringValue(fileName);
                    parameters["clientId"] = JsonValue.CreateNumberValue(clientId);
                    parameters["totalSize"] = JsonValue.CreateNumberValue(totalSize);
                    parameters["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
                    parameters["isE2EE"] = JsonValue.CreateNumberValue(0);
                    parameters["jxl"] = JsonValue.CreateNumberValue(0);
                    parameters["chunkId"] = JsonValue.CreateNumberValue(chunkIndex + 1);

                    string uploadUrl = ZaloAuth.ApiUrl(s.ServiceUrl("file") + "/api/" + route + "/asyncfile/upload",
                        new KeyValuePair<string, string>("type", type),
                        new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, parameters.Stringify())));
                    IJsonValue uploadData = await ZaloAuth.CallMultipartAsync(s, uploadUrl, fileName, chunk, ct);
                    JsonObject upload = uploadData != null && uploadData.ValueType == JsonValueType.Object ? uploadData.GetObject() : null;
                    fileId = ZaloAuth.Str(upload, "fileId");
                    if (string.IsNullOrWhiteSpace(fileId) || fileId == "-1")
                        throw new InvalidOperationException("Máy chủ từ chối tải tệp lên.");
                    double responseClientId = ZaloAuth.Num(upload, "clientFileId");
                    if (responseClientId > 0) clientFileId = (long)responseClientId;
                }
                checksum = CryptographicBuffer.EncodeToHexString(hash.GetValueAndReset()).ToLowerInvariant();
            }

            string fileUrl = await listener.WaitForUploadedFileAsync(fileId, TimeSpan.FromSeconds(60));
            string extension = Path.GetExtension(fileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
            var send = new JsonObject();
            send["fileId"] = JsonValue.CreateStringValue(fileId);
            send["checksum"] = JsonValue.CreateStringValue(checksum);
            send["checksumSha"] = JsonValue.CreateStringValue(string.Empty);
            send["extention"] = JsonValue.CreateStringValue(extension);
            send["totalSize"] = JsonValue.CreateNumberValue(totalSize);
            send["fileName"] = JsonValue.CreateStringValue(fileName);
            send["clientId"] = JsonValue.CreateNumberValue(clientFileId);
            send["fType"] = JsonValue.CreateNumberValue(1);
            send["fileCount"] = JsonValue.CreateNumberValue(0);
            send["fdata"] = JsonValue.CreateStringValue("{}");
            send[isGroup ? "grid" : "toid"] = JsonValue.CreateStringValue(threadId);
            send["fileUrl"] = JsonValue.CreateStringValue(fileUrl);
            send["zsource"] = JsonValue.CreateNumberValue(-1);
            send["ttl"] = JsonValue.CreateNumberValue(0);
            string sendUrl = ZaloAuth.ApiUrl(s.ServiceUrl("file") + "/api/" + route + "/asyncfile/msg",
                new KeyValuePair<string, string>("nretry", "0"),
                new KeyValuePair<string, string>("type", type),
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, send.Stringify())));
            IJsonValue sentData = await ZaloAuth.CallAsync(s, HttpMethod.Post, sendUrl, new List<KeyValuePair<string, string>>(), ct);
            JsonObject sent = sentData != null && sentData.ValueType == JsonValueType.Object ? sentData.GetObject() : null;
            if (sent == null) throw new InvalidOperationException("Máy chủ không xác nhận việc gửi tệp.");

            return new ZaloFileSendResult
            {
                MessageId = ZaloAuth.Str(sent, "msgId"),
                ClientMessageId = clientFileId.ToString(),
                FileUrl = fileUrl,
                FileName = fileName,
                MsgType = string.Equals(extension, "mp4", StringComparison.OrdinalIgnoreCase) ? "chat.video.msg" : "share.file"
            };
        }

        public static async Task AddReactionAsync(ZaloSession s, ZaloConversation conversation,
            ZaloChatMessage message, string icon, int reactionType, CancellationToken ct)
        {
            if (s == null || conversation == null || message == null)
                throw new InvalidOperationException("Thiếu thông tin tin nhắn.");
            long messageId, clientMessageId;
            if (!long.TryParse(message.GlobalMessageId, out messageId)
                || !long.TryParse(message.ClientMessageId, out clientMessageId))
                throw new InvalidOperationException("Tin nhắn này thiếu mã cần thiết để thả cảm xúc.");

            var reaction = new JsonObject();
            var reactionMessage = new JsonObject();
            var targets = new JsonArray();
            reactionMessage["gMsgID"] = JsonValue.CreateNumberValue(messageId);
            reactionMessage["cMsgID"] = JsonValue.CreateNumberValue(clientMessageId);
            reactionMessage["msgType"] = JsonValue.CreateNumberValue(1);
            targets.Add(reactionMessage);
            reaction["rMsg"] = JsonValue.CreateStringValue(targets.Stringify());
            reaction["rIcon"] = JsonValue.CreateStringValue(icon);
            reaction["rType"] = JsonValue.CreateNumberValue(reactionType);
            reaction["source"] = JsonValue.CreateNumberValue(6);

            var reactionItem = new JsonObject();
            reactionItem["message"] = JsonValue.CreateStringValue(reaction.Stringify());
            reactionItem["clientId"] = JsonValue.CreateNumberValue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var reactionList = new JsonArray();
            reactionList.Add(reactionItem);

            var payload = new JsonObject();
            payload["react_list"] = reactionList;
            payload[conversation.IsGroup ? "grid" : "toid"] = JsonValue.CreateStringValue(conversation.ThreadId);
            if (conversation.IsGroup) payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);

            string route = conversation.IsGroup ? "/api/group/reaction" : "/api/message/reaction";
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("reaction") + route);
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
            };
            await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
        }

        public static async Task UndoMessageAsync(ZaloSession s, ZaloConversation conversation,
            ZaloChatMessage message, CancellationToken ct)
        {
            if (s == null || conversation == null || message == null || !message.FromMe)
                throw new InvalidOperationException("Chỉ có thể thu hồi tin nhắn của bạn.");
            if (!message.CanRecall)
                throw new InvalidOperationException("Tin nhắn này thiếu mã máy chủ để thu hồi.");

            var payload = new JsonObject();
            payload["msgId"] = JsonValue.CreateStringValue(message.GlobalMessageId);
            payload["clientId"] = JsonValue.CreateNumberValue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            payload["cliMsgIdUndo"] = JsonValue.CreateStringValue(message.ClientMessageId);
            if (conversation.IsGroup)
            {
                payload["grid"] = JsonValue.CreateStringValue(conversation.ThreadId);
                payload["visibility"] = JsonValue.CreateNumberValue(0);
                payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            }
            else payload["toid"] = JsonValue.CreateStringValue(conversation.ThreadId);

            string route = conversation.IsGroup ? "/api/group/undomsg" : "/api/message/undo";
            string service = conversation.IsGroup ? "group" : "chat";
            string url = ZaloAuth.ApiUrl(s.ServiceUrl(service) + route);
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
            };
            await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
        }

        public static async Task DeleteMessageForMeAsync(ZaloSession s, ZaloConversation conversation,
            ZaloChatMessage message, CancellationToken ct)
        {
            if (s == null || conversation == null || message == null)
                throw new InvalidOperationException("Thiếu thông tin tin nhắn.");
            if (!message.CanDeleteForMe)
                throw new InvalidOperationException("Tin nhắn này thiếu mã máy chủ để xóa.");

            long clientId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var record = new JsonObject();
            record["cliMsgId"] = JsonValue.CreateStringValue(message.ClientMessageId);
            record["globalMsgId"] = JsonValue.CreateStringValue(message.GlobalMessageId);
            record["ownerId"] = JsonValue.CreateStringValue(message.SenderUid);
            record["destId"] = JsonValue.CreateStringValue(conversation.ThreadId);
            var records = new JsonArray();
            records.Add(record);

            var payload = new JsonObject();
            payload[conversation.IsGroup ? "grid" : "toid"] = JsonValue.CreateStringValue(conversation.ThreadId);
            payload["cliMsgId"] = JsonValue.CreateNumberValue(clientId);
            payload["msgs"] = records;
            payload["onlyMe"] = JsonValue.CreateNumberValue(1);
            if (!conversation.IsGroup) payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);

            string route = conversation.IsGroup ? "/api/group/deletemsg" : "/api/message/delete";
            string service = conversation.IsGroup ? "group" : "chat";
            string url = ZaloAuth.ApiUrl(s.ServiceUrl(service) + route);
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
            };
            await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
        }

        public static async Task<HashSet<string>> GetPinnedConversationKeysAsync(ZaloSession s, CancellationToken ct)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (s == null) return keys;
            var query = new JsonObject();
            query["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("conversation") + "/api/pinconvers/list",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, query.Stringify())));
            IJsonValue data = await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
            JsonObject root = ObjectValue(data);
            JsonArray conversations = ArrayValue(root != null && root.ContainsKey("conversations") ? root["conversations"] : data);
            if (conversations == null) return keys;
            foreach (IJsonValue item in conversations)
            {
                if (item.ValueType != JsonValueType.String) continue;
                string raw = item.GetString();
                if (raw.Length < 2) continue;
                string prefix = raw.Substring(0, 1);
                string id = raw.Substring(1);
                if ((prefix == "u" || prefix == "g") && id.Length > 0) keys.Add(prefix + ":" + id);
            }
            return keys;
        }

        public static async Task<HashSet<string>> GetArchivedConversationKeysAsync(ZaloSession s, CancellationToken ct)
        {
            if (s == null) return null;
            var query = new JsonObject();
            query["version"] = JsonValue.CreateNumberValue(1);
            query["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("label") + "/api/archivedchat/list",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, query.Stringify())));
            IJsonValue response = await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);

            JsonArray items = ArrayValue(response, "items");
            JsonObject root = ObjectValue(response);
            IJsonValue nested;
            if (items == null && root != null && root.TryGetValue("data", out nested))
                items = ArrayValue(nested, "items", "data");
            if (items == null) items = ArrayValue(response);
            if (items == null) return null;

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (IJsonValue item in items)
            {
                string key = ReadArchivedConversationKey(item);
                if (key != null) keys.Add(key);
                else if (items.Count > 0) return null;
            }
            return keys;
        }

        private static string ReadArchivedConversationKey(IJsonValue item)
        {
            if (item == null) return null;
            if (item.ValueType == JsonValueType.String)
            {
                string raw = item.GetString();
                if (raw.Length > 1 && (raw[0] == 'u' || raw[0] == 'g')) return raw[0] + ":" + raw.Substring(1);
                return null;
            }
            if (item.ValueType != JsonValueType.Object) return null;

            JsonObject obj = item.GetObject();
            string id = ZaloAuth.Str(obj, "id");
            if (string.IsNullOrWhiteSpace(id)) id = ZaloAuth.Str(obj, "thread_id");
            if (string.IsNullOrWhiteSpace(id)) id = ZaloAuth.Str(obj, "threadId");
            if (string.IsNullOrWhiteSpace(id)) return null;

            if (id.Length > 1 && (id[0] == 'u' || id[0] == 'g'))
                return id[0] + ":" + id.Substring(1);

            IJsonValue typeValue;
            if (obj.TryGetValue("is_group", out typeValue) || obj.TryGetValue("isGroup", out typeValue))
            {
                bool isGroup = typeValue.ValueType == JsonValueType.Boolean
                    ? typeValue.GetBoolean()
                    : typeValue.ValueType == JsonValueType.Number && typeValue.GetNumber() != 0;
                return (isGroup ? "g:" : "u:") + id;
            }
            if (obj.TryGetValue("type", out typeValue))
            {
                if (typeValue.ValueType == JsonValueType.Number)
                    return (typeValue.GetNumber() == 1 ? "g:" : "u:") + id;
                if (typeValue.ValueType == JsonValueType.String)
                {
                    string type = typeValue.GetString();
                    if (string.Equals(type, "group", StringComparison.OrdinalIgnoreCase)) return "g:" + id;
                    if (string.Equals(type, "user", StringComparison.OrdinalIgnoreCase)) return "u:" + id;
                }
            }
            return null;
        }

        public static async Task SetPinnedConversationAsync(ZaloSession s, ZaloConversation conversation, bool pinned, CancellationToken ct)
        {
            if (s == null || conversation == null || string.IsNullOrWhiteSpace(conversation.ThreadId))
                throw new InvalidOperationException("Thiếu thông tin cuộc trò chuyện.");
            var conversations = new JsonArray();
            conversations.Add(JsonValue.CreateStringValue((conversation.IsGroup ? "g" : "u") + conversation.ThreadId));
            var payload = new JsonObject();
            payload["actionType"] = JsonValue.CreateNumberValue(pinned ? 1 : 2);
            payload["conversations"] = conversations;
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("conversation") + "/api/pinconvers/updatev2");
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
            };
            await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
        }

        public static async Task SetArchivedConversationAsync(ZaloSession s, ZaloConversation conversation, bool archived, CancellationToken ct)
        {
            if (s == null || conversation == null || string.IsNullOrWhiteSpace(conversation.ThreadId))
                throw new InvalidOperationException("Thiếu thông tin cuộc trò chuyện.");
            var ids = new JsonArray();
            var target = new JsonObject();
            target["id"] = JsonValue.CreateStringValue(conversation.ThreadId);
            target["type"] = JsonValue.CreateNumberValue(conversation.IsGroup ? 1 : 0);
            ids.Add(target);
            var payload = new JsonObject();
            payload["actionType"] = JsonValue.CreateNumberValue(archived ? 0 : 1);
            payload["ids"] = ids;
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            payload["version"] = JsonValue.CreateNumberValue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("label") + "/api/archivedchat/update");
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
            };
            await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
        }

        public static async Task DeleteConversationAsync(ZaloSession s, ZaloConversation conversation, CancellationToken ct)
        {
            if (s == null || conversation == null || string.IsNullOrWhiteSpace(conversation.ThreadId))
                throw new InvalidOperationException("Thiếu thông tin cuộc trò chuyện.");
            ZaloChatMessage last = conversation.Messages.LastOrDefault(m => m.CanDeleteForMe);
            if (last == null)
                throw new InvalidOperationException("Cần tải lịch sử tin nhắn trước khi xóa cuộc trò chuyện.");

            var lastMessage = new JsonObject();
            lastMessage["ownerId"] = JsonValue.CreateStringValue(last.SenderUid);
            lastMessage["cliMsgId"] = JsonValue.CreateStringValue(last.ClientMessageId);
            lastMessage["globalMsgId"] = JsonValue.CreateStringValue(last.GlobalMessageId);
            var payload = new JsonObject();
            payload[conversation.IsGroup ? "grid" : "toid"] = JsonValue.CreateStringValue(conversation.ThreadId);
            payload["cliMsgId"] = JsonValue.CreateStringValue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            payload["conver"] = lastMessage;
            payload["onlyMe"] = JsonValue.CreateNumberValue(1);
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string route = conversation.IsGroup ? "/api/group/deleteconver" : "/api/message/deleteconver";
            string service = conversation.IsGroup ? "group" : "chat";
            string url = ZaloAuth.ApiUrl(s.ServiceUrl(service) + route,
                new KeyValuePair<string, string>("nretry", "0"));
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
            };
            await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
        }

        public static async Task<HashSet<string>> GetUnreadMarkKeysAsync(ZaloSession s, CancellationToken ct)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (s == null) return keys;
            var empty = new JsonObject();
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("conversation") + "/api/conv/getUnreadMark",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, empty.Stringify())));
            IJsonValue result = await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
            JsonObject root = ObjectValue(result);
            IJsonValue dataValue;
            JsonObject data = root != null && root.TryGetValue("data", out dataValue)
                ? (dataValue.ValueType == JsonValueType.Object ? dataValue.GetObject()
                    : dataValue.ValueType == JsonValueType.String ? ZaloJson.ParseObject(dataValue.GetString()) : null)
                : root;
            AddUnreadKeys(data, "convsUser", "u:", keys);
            AddUnreadKeys(data, "convsGroup", "g:", keys);
            return keys;
        }

        private static void AddUnreadKeys(JsonObject data, string field, string prefix, HashSet<string> keys)
        {
            IJsonValue value;
            if (data == null || !data.TryGetValue(field, out value) || value.ValueType != JsonValueType.Array) return;
            foreach (IJsonValue entry in value.GetArray())
            {
                string id = entry.ValueType == JsonValueType.Object ? ZaloAuth.Str(entry.GetObject(), "id")
                    : entry.ValueType == JsonValueType.String || entry.ValueType == JsonValueType.Number ? entry.ToString().Trim('"') : string.Empty;
                if (!string.IsNullOrWhiteSpace(id)) keys.Add(prefix + id);
            }
        }

        public static async Task AddUnreadMarkAsync(ZaloSession s, ZaloConversation conversation, CancellationToken ct)
        {
            if (s == null || conversation == null) throw new InvalidOperationException("Thiếu thông tin cuộc trò chuyện.");
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var entry = new JsonObject();
            entry["id"] = JsonValue.CreateStringValue(conversation.ThreadId);
            entry["cliMsgId"] = JsonValue.CreateStringValue(timestamp.ToString());
            entry["fromUid"] = JsonValue.CreateStringValue("0");
            entry["ts"] = JsonValue.CreateNumberValue(timestamp);
            var inner = new JsonObject();
            inner[conversation.IsGroup ? "convsGroup" : "convsUser"] = new JsonArray { entry };
            inner[conversation.IsGroup ? "convsUser" : "convsGroup"] = new JsonArray();
            inner["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            var outer = new JsonObject();
            outer["param"] = JsonValue.CreateStringValue(inner.Stringify());
            await SendUnreadMarkRequestAsync(s, "/api/conv/addUnreadMark", outer, ct);
        }

        public static async Task RemoveUnreadMarkAsync(ZaloSession s, ZaloConversation conversation, CancellationToken ct)
        {
            if (s == null || conversation == null) return;
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ids = new JsonArray();
            ids.Add(JsonValue.CreateStringValue(conversation.ThreadId));
            var dataEntry = new JsonObject();
            dataEntry["id"] = JsonValue.CreateStringValue(conversation.ThreadId);
            dataEntry["ts"] = JsonValue.CreateNumberValue(timestamp);
            var dataEntries = new JsonArray();
            dataEntries.Add(dataEntry);
            var inner = new JsonObject();
            inner[conversation.IsGroup ? "convsGroup" : "convsUser"] = ids;
            inner[conversation.IsGroup ? "convsUser" : "convsGroup"] = new JsonArray();
            inner[conversation.IsGroup ? "convsGroupData" : "convsUserData"] = dataEntries;
            inner[conversation.IsGroup ? "convsUserData" : "convsGroupData"] = new JsonArray();
            var outer = new JsonObject();
            outer["param"] = JsonValue.CreateStringValue(inner.Stringify());
            await SendUnreadMarkRequestAsync(s, "/api/conv/removeUnreadMark", outer, ct);
        }

        private static async Task SendUnreadMarkRequestAsync(ZaloSession s, string route, JsonObject payload, CancellationToken ct)
        {
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("conversation") + route);
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
            };
            await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
        }

        public static async Task MarkMessagesSeenAsync(ZaloSession s, ZaloConversation conversation,
            IEnumerable<ZaloChatMessage> sourceMessages, CancellationToken ct)
        {
            if (s == null || conversation == null) return;
            var messages = (sourceMessages ?? Enumerable.Empty<ZaloChatMessage>())
                .Where(m => m != null && !m.FromMe && m.CanSendSeen)
                .Take(50).ToList();
            if (messages.Count == 0) return;

            var records = new JsonArray();
            foreach (ZaloChatMessage message in messages)
            {
                var record = new JsonObject();
                record["gmi"] = JsonValue.CreateStringValue(message.GlobalMessageId);
                record["cmi"] = JsonValue.CreateStringValue(message.ClientMessageId);
                record["si"] = JsonValue.CreateStringValue(message.SenderUid);
                record["di"] = JsonValue.CreateStringValue(message.RecipientUid == s.Uid ? "0" : message.RecipientUid);
                record["mt"] = JsonValue.CreateStringValue(message.MsgType ?? string.Empty);
                record["st"] = JsonValue.CreateNumberValue(message.Status);
                record["at"] = JsonValue.CreateNumberValue(message.At);
                record["cmd"] = JsonValue.CreateNumberValue(message.Command);
                record["ts"] = JsonValue.CreateNumberValue(message.Ts);
                records.Add(record);
            }

            var infos = new JsonObject();
            infos["data"] = records;
            infos[conversation.IsGroup ? "grid" : "senderId"] = JsonValue.CreateStringValue(conversation.ThreadId);
            var payload = new JsonObject();
            payload["msgInfos"] = JsonValue.CreateStringValue(infos.Stringify());
            if (conversation.IsGroup) payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string route = conversation.IsGroup ? "/api/group/seenv2" : "/api/message/seenv2";
            string host = conversation.IsGroup ? s.ServiceUrl("group") : s.ServiceUrl("chat");
            string url = ZaloAuth.ApiUrl(host + route, new KeyValuePair<string, string>("nretry", "0"));
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
            };
            await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
        }

        public static async Task<List<ZaloSticker>> SearchStickersAsync(ZaloSession s, string keyword, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(keyword)) return new List<ZaloSticker>();
            var payload = new JsonObject();
            payload["keyword"] = JsonValue.CreateStringValue(keyword.Trim());
            payload["gif"] = JsonValue.CreateNumberValue(1);
            payload["guggy"] = JsonValue.CreateNumberValue(0);
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string baseUrl = s.ServiceUrl("sticker") + "/api/message/sticker/suggest/stickers";
            string url = ZaloAuth.ApiUrl(baseUrl,
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify())));
            IJsonValue result = await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
            JsonObject root = ObjectValue(result);
            var stickers = new List<ZaloSticker>();
            IJsonValue suggestions;
            if (root == null || !root.TryGetValue("sugg_sticker", out suggestions)
                || suggestions.ValueType != JsonValueType.Array) return stickers;
            foreach (IJsonValue item in suggestions.GetArray())
            {
                if (item.ValueType != JsonValueType.Object) continue;
                JsonObject sticker = item.GetObject();
                long id = (long)ZaloAuth.Num(sticker, "sticker_id");
                if (id <= 0) continue;
                long categoryId = (long)ZaloAuth.Num(sticker, "cate_id");
                int type = (int)ZaloAuth.Num(sticker, "type");
                if (type <= 0) continue;
                stickers.Add(new ZaloSticker
                {
                    Id = id.ToString(),
                    CategoryId = categoryId,
                    Type = type,
                    ImageUrl = "https://zalo-api.zadn.vn/api/emoticon/sticker/webpc?eid="
                        + id + "&size=130&version=4"
                });
            }
            return stickers;
        }

        public static async Task<JsonObject> ParseLinkAsync(ZaloSession s, string link, CancellationToken ct)
        {
            if (s == null || string.IsNullOrWhiteSpace(link)) return null;
            var payload = new JsonObject();
            payload["link"] = JsonValue.CreateStringValue(link);
            payload["version"] = JsonValue.CreateNumberValue(1);
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("file") + "/api/message/parselink",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify())));
            JsonObject response = ObjectValue(await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct));
            return response == null ? null : ObjectValue(response.TryGetValue("data", out IJsonValue value) ? value : null);
        }

        public static async Task<ZaloStickerSendResult> SendStickerAsync(ZaloSession s, string threadId, bool isGroup,
            ZaloSticker sticker, CancellationToken ct)
        {
            if (s == null || sticker == null || string.IsNullOrWhiteSpace(sticker.Id))
                throw new InvalidOperationException("Không có nhãn dán hợp lệ để gửi.");
            var payload = new JsonObject();
            payload["stickerId"] = JsonValue.CreateNumberValue(long.Parse(sticker.Id));
            payload["cateId"] = JsonValue.CreateNumberValue(sticker.CategoryId);
            payload["type"] = JsonValue.CreateNumberValue(sticker.Type);
            long clientId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            payload["clientId"] = JsonValue.CreateNumberValue(clientId);
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            payload["zsource"] = JsonValue.CreateNumberValue(101);
            payload[isGroup ? "grid" : "toid"] = JsonValue.CreateStringValue(threadId);
            string baseUrl = isGroup ? s.ServiceUrl("group") + "/api/group/sticker"
                : s.ServiceUrl("chat") + "/api/message/sticker";
            string url = ZaloAuth.ApiUrl(baseUrl, new KeyValuePair<string, string>("nretry", "0"));
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
            };
            IJsonValue result = await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
            JsonObject response = ObjectValue(result);
            return new ZaloStickerSendResult
            {
                MessageId = response == null ? string.Empty : ZaloAuth.Str(response, "msgId"),
                ClientMessageId = clientId.ToString()
            };
        }

        public static async Task<List<string>> GetGroupIdsAsync(ZaloSession s, CancellationToken ct)
        {
            var ids = new List<string>();
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group_poll") + "/api/group/getlg/v4",
                new KeyValuePair<string, string>("nretry", "0"));
            IJsonValue data = await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
            JsonObject root = ObjectValue(data);
            if (root == null) return ids;

            IJsonValue map;
            if (root.TryGetValue("gridVerMap", out map) && map.ValueType == JsonValueType.Object)
            {
                foreach (KeyValuePair<string, IJsonValue> pair in map.GetObject())
                    if (!string.IsNullOrWhiteSpace(pair.Key)) ids.Add(pair.Key);
            }
            return ids;
        }

        public static async Task<string> GetStickerImageUrlAsync(ZaloSession s, string stickerId, CancellationToken ct)
        {
            if (s == null || string.IsNullOrWhiteSpace(stickerId)) return string.Empty;
            var payload = new JsonObject();
            long numericId;
            payload["sid"] = long.TryParse(stickerId, out numericId)
                ? JsonValue.CreateNumberValue(numericId)
                : JsonValue.CreateStringValue(stickerId);
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("sticker") + "/api/message/sticker/sticker_detail",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify())));
            IJsonValue data = await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
            JsonObject detail = ObjectValue(data);
            if (detail == null && data != null && data.ValueType == JsonValueType.Array && data.GetArray().Count > 0)
                detail = ObjectValue(data.GetArray()[0]);
            if (detail == null) return string.Empty;

            IJsonValue nested;
            if (detail.TryGetValue("sticker", out nested) && nested.ValueType == JsonValueType.Object)
                detail = nested.GetObject();
            string imageUrl = ZaloAuth.Str(detail, "stickerUrl");
            if (string.IsNullOrEmpty(imageUrl)) imageUrl = ZaloAuth.Str(detail, "stickerSpriteUrl");
            if (string.IsNullOrEmpty(imageUrl)) imageUrl = ZaloAuth.Str(detail, "stickerWebpUrl");
            return imageUrl.StartsWith("//") ? "https:" + imageUrl : imageUrl;
        }

        public static async Task<List<ZaloChatMessage>> GetHistoryAsync(ZaloSession s, string threadId,
            bool isGroup, int count, CancellationToken ct)
        {
            var messages = new List<ZaloChatMessage>();
            if (s == null || string.IsNullOrWhiteSpace(threadId)) return messages;

            IJsonValue data;
            if (isGroup)
            {
                var payload = new JsonObject();
                payload["grid"] = JsonValue.CreateStringValue(threadId);
                payload["count"] = JsonValue.CreateNumberValue(count);
                string url = ZaloAuth.ApiUrl(s.ServiceUrl("group") + "/api/group/history",
                    new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify())));
                data = await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
            }
            else
            {
                // This route and payload are retained from the workspace's legacy API client.
                // The request is session-bound and failures remain non-fatal until live-verified.
                var payload = new JsonObject();
                payload["first"] = JsonValue.CreateBooleanValue(true);
                payload["lastId"] = JsonValue.CreateStringValue("0");
                payload["toid"] = JsonValue.CreateStringValue(threadId);
                payload["preIds"] = new JsonArray();
                payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
                string url = ZaloAuth.ApiUrl(s.ServiceUrl("chat") + "/api/message/getmsgv2",
                    new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify())));
                data = await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
            }

            JsonObject response = ObjectValue(data);
            JsonArray array = ArrayValue(data, isGroup ? "groupMsgs" : "msgs", "messages");
            if (array == null && response != null)
                array = ArrayValue(GetProperty(response, "data"), isGroup ? "groupMsgs" : "msgs", "messages");
            if (array == null) return messages;

            int index = 0;
            foreach (IJsonValue item in array)
            {
                if (item.ValueType != JsonValueType.Object) continue;
                JsonObject message = ZaloJson.ParseObject(item.GetObject().Stringify());
                if (message == null) continue;
                if (!message.ContainsKey("content"))
                {
                    IJsonValue legacyContent;
                    if (message.TryGetValue("message", out legacyContent)) message["content"] = legacyContent;
                }

                string fromUid = ZaloAuth.Str(message, "uidFrom");
                bool fromMe = fromUid == "0" || fromUid == s.Uid;
                string msgId = ZaloAuth.Str(message, "msgId");
                if (msgId.Length == 0) msgId = ZaloAuth.Str(message, "cliMsgId");
                string timestamp = ZaloAuth.Str(message, "ts");
                long ts;
                if (!long.TryParse(timestamp, out ts) || ts <= 0)
                    ts = (long)ZaloAuth.Num(message, "timestamp");
                if (ts > 0 && ts < 100000000000L) ts *= 1000;

                IJsonValue eventContent;
                if (message.TryGetValue("content", out eventContent) && eventContent.ValueType == JsonValueType.Object
                    && eventContent.GetObject().ContainsKey("deleteMsg"))
                {
                    JsonObject undo = eventContent.GetObject();
                    messages.Add(new ZaloChatMessage
                    {
                        IsRecallEvent = true,
                        GlobalMessageId = ZaloAuth.Str(undo, "globalMsgId"),
                        ClientMessageId = ZaloAuth.Str(undo, "cliMsgId"),
                        Ts = ts
                    });
                    continue;
                }

                string sender = fromMe ? "Tôi" : ZaloAuth.Str(message, "dName");
                if (sender.Length == 0) sender = ZaloAuth.Str(message, "name");
                if (sender.Length == 0) sender = isGroup ? "Thành viên" : "Bạn bè";

                var chatMessage = new ZaloChatMessage
                {
                    Id = msgId.Length > 0 ? msgId : "history-" + ts + "-" + index,
                    GlobalMessageId = ZaloAuth.Str(message, "msgId"),
                    ClientMessageId = ZaloAuth.Str(message, "cliMsgId"),
                    SenderUid = fromUid == "0" ? s.Uid : fromUid,
                    RecipientUid = ZaloAuth.Str(message, "idTo") == "0" ? s.Uid : ZaloAuth.Str(message, "idTo"),
                    Sender = sender,
                    Text = ZaloFormat.Describe(message),
                    ReplyContent = ZaloFormat.PlainContent(message),
                    MsgType = ZaloAuth.Str(message, "msgType"),
                    StickerId = ZaloFormat.StickerId(message),
                    FileName = ZaloFormat.FileName(message),
                    MediaUrl = ZaloFormat.MediaUrl(message),
                    FromMe = fromMe,
                    ShowSender = isGroup && !fromMe,
                    Status = message.ContainsKey("st") ? 0 : -1,
                    At = message.ContainsKey("at") ? 0 : -1,
                    Command = message.ContainsKey("cmd") ? 0 : -1,
                    Ts = ts
                };
                ZaloFormat.ReadQuote(message, chatMessage);
                ZaloFormat.ReadReplyPayload(message, chatMessage, isGroup);
                ZaloFormat.ReadLinkPreview(message, chatMessage);
                ZaloFormat.ReadLocation(message, chatMessage);
                ZaloFormat.ReadPoll(message, chatMessage);
                ZaloFormat.ReadRecommendedLinkPreview(message, chatMessage);
                ZaloFormat.ReadContactCard(message, chatMessage);
                messages.Add(chatMessage);
                index++;
            }

            return messages.OrderBy(m => m.Ts).ToList();
        }

        private static JsonObject ObjectValue(IJsonValue value)
        {
            if (value == null) return null;
            if (value.ValueType == JsonValueType.Object) return value.GetObject();
            if (value.ValueType == JsonValueType.String) return ZaloJson.ParseObject(value.GetString());
            return null;
        }

        private static JsonArray ArrayValue(IJsonValue value, params string[] keys)
        {
            if (value == null) return null;
            if (value.ValueType == JsonValueType.Array) return value.GetArray();
            JsonObject obj = ObjectValue(value);
            if (obj == null) return null;
            foreach (string key in keys)
            {
                IJsonValue item;
                if (obj.TryGetValue(key, out item))
                {
                    if (item.ValueType == JsonValueType.Array) return item.GetArray();
                    JsonArray parsed;
                    if (item.ValueType == JsonValueType.String && JsonArray.TryParse(item.GetString(), out parsed)) return parsed;
                }
            }
            return null;
        }

        private static IJsonValue GetProperty(JsonObject obj, string key)
        {
            IJsonValue value;
            return obj != null && obj.TryGetValue(key, out value) ? value : null;
        }

        public sealed class ZaloSentText
        {
            public string MessageId;
            public string ClientMessageId;
        }

        public sealed class ZaloForwardResult
        {
            public int Succeeded;
            public int Failed;
        }

        /// <summary>Forwards a plain text message using Zalo's multi-target forwarding endpoint.</summary>
        public static async Task<ZaloForwardResult> ForwardTextAsync(ZaloSession s, ZaloChatMessage message,
            IList<ZaloConversation> targets, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (message == null || !message.HasText) throw new InvalidOperationException("Phiên bản này chỉ chuyển tiếp tin nhắn văn bản.");
            if (targets == null || targets.Count == 0) throw new InvalidOperationException("Chưa chọn người nhận.");

            var result = new ZaloForwardResult();
            foreach (bool isGroup in new[] { false, true })
            {
                List<ZaloConversation> batch = targets.Where(t => t != null && t.IsGroup == isGroup).ToList();
                if (batch.Count == 0) continue;

                long clientId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var entries = new JsonArray();
                foreach (ZaloConversation target in batch)
                {
                    var entry = new JsonObject();
                    entry[isGroup ? "grid" : "toUid"] = JsonValue.CreateStringValue(target.ThreadId);
                    entry["clientId"] = JsonValue.CreateNumberValue(clientId);
                    entry["ttl"] = JsonValue.CreateNumberValue(0);
                    entries.Add(entry);
                }

                var msgInfo = new JsonObject();
                string forwardedText = string.IsNullOrWhiteSpace(message.ReplyContent) ? message.Text : message.ReplyContent;
                msgInfo["message"] = JsonValue.CreateStringValue(forwardedText ?? string.Empty);
                var payload = new JsonObject();
                payload[isGroup ? "grids" : "toIds"] = entries;
                if (!isGroup) payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
                payload["ttl"] = JsonValue.CreateNumberValue(0);
                payload["msgType"] = JsonValue.CreateStringValue("1");
                payload["totalIds"] = JsonValue.CreateNumberValue(batch.Count);
                payload["msgInfo"] = JsonValue.CreateStringValue(msgInfo.Stringify());
                payload["decorLog"] = JsonValue.CreateStringValue("null");

                string route = isGroup ? "/api/group/mforward" : "/api/message/mforward";
                string url = ZaloAuth.ApiUrl(s.ServiceUrl("file") + route);
                IJsonValue response = await ZaloAuth.CallAsync(s, HttpMethod.Post, url,
                    new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
                    }, ct);
                JsonObject responseObject = response != null && response.ValueType == JsonValueType.Object ? response.GetObject() : null;
                JsonArray success = null;
                JsonArray fail = null;
                IJsonValue value;
                if (responseObject != null && responseObject.TryGetValue("success", out value) && value.ValueType == JsonValueType.Array)
                    success = value.GetArray();
                if (responseObject != null && responseObject.TryGetValue("fail", out value) && value.ValueType == JsonValueType.Array)
                    fail = value.GetArray();
                result.Succeeded += success == null ? 0 : success.Count;
                result.Failed += fail == null ? Math.Max(0, batch.Count - (success == null ? 0 : success.Count)) : fail.Count;
            }
            return result;
        }

        /// <summary>Sends a plain text message, optionally quoting a supported text or media message.</summary>
        public static async Task<ZaloSentText> SendTextAsync(ZaloSession s, string threadId, bool isGroup,
            string text, ZaloChatMessage quote, CancellationToken ct, long clientId = 0)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (clientId <= 0) clientId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var payload = new JsonObject();
            payload["message"] = JsonValue.CreateStringValue(text);
            payload["clientId"] = JsonValue.CreateNumberValue(clientId);
            payload["ttl"] = JsonValue.CreateNumberValue(0);
            payload[isGroup ? "grid" : "toid"] = JsonValue.CreateStringValue(threadId);
            if (isGroup) payload["visibility"] = JsonValue.CreateNumberValue(0);
            else payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);

            string route;
            if (quote != null)
            {
                if (!quote.CanReply) throw new InvalidOperationException("Tin nhắn này chưa có đủ dữ liệu để trả lời.");
                long quotedClientId;
                if (!long.TryParse(quote.ClientMessageId, out quotedClientId))
                    throw new InvalidOperationException("Thiếu mã tin nhắn cần trích dẫn.");
                payload["qmsgOwner"] = JsonValue.CreateStringValue(quote.SenderUid);
                payload["qmsgId"] = JsonValue.CreateStringValue(quote.GlobalMessageId);
                payload["qmsgCliId"] = JsonValue.CreateNumberValue(quotedClientId);
                payload["qmsgType"] = JsonValue.CreateNumberValue(ClientMessageType(quote.MsgType));
                payload["qmsgTs"] = JsonValue.CreateNumberValue(quote.Ts);
                payload["qmsg"] = JsonValue.CreateStringValue(
                    string.IsNullOrEmpty(quote.QuoteMessage) ? (quote.ReplyContent ?? string.Empty) : quote.QuoteMessage);
                if (isGroup && !string.IsNullOrWhiteSpace(quote.QuoteAttachment))
                    payload["qmsgAttach"] = JsonValue.CreateStringValue(quote.QuoteAttachment);
                route = isGroup ? "/api/group/quote" : "/api/message/quote";
            }
            else
            {
                route = isGroup ? "/api/group/sendmsg" : "/api/message/sms";
            }

            string baseUrl = isGroup ? s.ServiceUrl("group") : s.ServiceUrl("chat");
            string url = ZaloAuth.ApiUrl(baseUrl + route, new KeyValuePair<string, string>("nretry", "0"));
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
            };
            IJsonValue data = await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
            string serverMsgId = string.Empty;
            if (data != null)
            {
                if (data.ValueType == JsonValueType.Object)
                {
                    var obj = data.GetObject();
                    serverMsgId = ZaloAuth.Str(obj, "msgId");
                    if (string.IsNullOrEmpty(serverMsgId)) serverMsgId = ZaloAuth.Str(obj, "messageId");
                    if (string.IsNullOrEmpty(serverMsgId)) serverMsgId = ZaloAuth.Str(obj, "globalMsgId");
                    if (string.IsNullOrEmpty(serverMsgId)) serverMsgId = ZaloAuth.Str(obj, "gMsgId");
                    if (string.IsNullOrEmpty(serverMsgId)) serverMsgId = ZaloAuth.Str(obj, "id");
                }
                else if (data.ValueType == JsonValueType.String)
                {
                    serverMsgId = data.GetString();
                }
                else if (data.ValueType == JsonValueType.Number)
                {
                    serverMsgId = data.GetNumber().ToString("F0");
                }
            }
            return new ZaloSentText
            {
                MessageId = serverMsgId,
                ClientMessageId = clientId.ToString()
            };
        }

        private static int ClientMessageType(string messageType)
        {
            switch (messageType)
            {
                case "chat.voice": return 31;
                case "chat.photo": return 32;
                case "chat.sticker": return 36;
                case "chat.doodle": return 37;
                case "chat.recommended": return 38;
                case "chat.link": return 38;
                case "chat.video.msg": return 44;
                case "share.file": return 46;
                case "chat.gif": return 49;
                case "chat.location.new": return 43;
                default: return 1;
            }
        }

        public static async Task<JsonObject> SendImageAsync(ZaloSession s, string threadId, bool isGroup,
            string fileName, IInputStream input, long totalSize, uint width, uint height, string caption, CancellationToken ct)
        {
            if (s == null || input == null || totalSize <= 0) throw new ArgumentException("Ảnh không có dữ liệu.");
            int chunkSize = 1024 * 1024;
            try
            {
                chunkSize = (int)s.Settings.GetNamedObject("features").GetNamedObject("sharefile").GetNamedNumber("chunk_size_file");
                if (chunkSize < 64 * 1024) chunkSize = 1024 * 1024;
            }
            catch { }
            if (totalSize > 20L * 1024 * 1024) throw new InvalidOperationException("Ảnh vượt giới hạn 20 MB của ứng dụng.");

            long clientId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var upload = new JsonObject();
            upload[isGroup ? "grid" : "toid"] = JsonValue.CreateStringValue(threadId);
            int totalChunks = (int)((totalSize + chunkSize - 1) / chunkSize);
            upload["totalChunk"] = JsonValue.CreateNumberValue(totalChunks);
            upload["fileName"] = JsonValue.CreateStringValue(fileName);
            upload["clientId"] = JsonValue.CreateNumberValue(clientId);
            upload["totalSize"] = JsonValue.CreateNumberValue(totalSize);
            upload["imei"] = JsonValue.CreateStringValue(s.Imei);
            upload["isE2EE"] = JsonValue.CreateNumberValue(0);
            upload["jxl"] = JsonValue.CreateNumberValue(0);

            string route = isGroup ? "group" : "message";
            JsonObject uploaded = null;
            using (var reader = new DataReader(input))
            {
                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    upload["chunkId"] = JsonValue.CreateNumberValue(chunkIndex + 1);
                    string url = ZaloAuth.ApiUrl(s.ServiceUrl("file") + "/api/" + route + "/photo_original/upload",
                        new KeyValuePair<string, string>("type", isGroup ? "11" : "2"),
                        new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, upload.Stringify())));
                    int length = (int)Math.Min(chunkSize, totalSize - (long)chunkIndex * chunkSize);
                    byte[] part = await ReadUploadChunkAsync(reader, length, ct);
                    IJsonValue response = await ZaloAuth.CallMultipartAsync(s, url, fileName, part, ct);
                    uploaded = ObjectValue(response);
                    if (uploaded == null || ZaloAuth.Str(uploaded, "photoId").Length == 0)
                        throw new InvalidOperationException("Máy chủ chưa trả thông tin ảnh đã tải lên.");
                }
            }

            string normalUrl = ZaloAuth.Str(uploaded, "normalUrl");
            var send = new JsonObject();
            send["photoId"] = JsonValue.CreateStringValue(ZaloAuth.Str(uploaded, "photoId"));
            send["clientId"] = JsonValue.CreateStringValue(clientId.ToString());
            send["desc"] = JsonValue.CreateStringValue(caption ?? string.Empty);
            send["width"] = JsonValue.CreateNumberValue(width);
            send["height"] = JsonValue.CreateNumberValue(height);
            send[isGroup ? "grid" : "toid"] = JsonValue.CreateStringValue(threadId);
            send["rawUrl"] = JsonValue.CreateStringValue(normalUrl);
            send["hdUrl"] = JsonValue.CreateStringValue(ZaloAuth.Str(uploaded, "hdUrl"));
            send["thumbUrl"] = JsonValue.CreateStringValue(ZaloAuth.Str(uploaded, "thumbUrl"));
            send[isGroup ? "oriUrl" : "normalUrl"] = JsonValue.CreateStringValue(normalUrl);
            send["hdSize"] = JsonValue.CreateStringValue(totalSize.ToString());
            send["zsource"] = JsonValue.CreateNumberValue(-1);
            send["ttl"] = JsonValue.CreateNumberValue(0);
            send["jcp"] = JsonValue.CreateStringValue("{\"convertible\":\"jxl\"}");
            // Image attachments are finalized on the file service. The Zalo client
            // protocol carries encrypted params in the query, with an empty POST body.
            string sendUrl = ZaloAuth.ApiUrl(s.ServiceUrl("file") +
                (isGroup ? "/api/group/photo_original/send" : "/api/message/photo_original/send"),
                new KeyValuePair<string, string>("nretry", "0"),
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, send.Stringify())));
            IJsonValue sent = await ZaloAuth.CallAsync(s, HttpMethod.Post, sendUrl,
                new List<KeyValuePair<string, string>>(), ct);
            JsonObject result = ObjectValue(sent) ?? new JsonObject();
            result["photoId"] = JsonValue.CreateStringValue(ZaloAuth.Str(uploaded, "photoId"));
            result["normalUrl"] = JsonValue.CreateStringValue(normalUrl);
            result["hdUrl"] = JsonValue.CreateStringValue(ZaloAuth.Str(uploaded, "hdUrl"));
            result["oriUrl"] = JsonValue.CreateStringValue(normalUrl);
            result["thumbUrl"] = JsonValue.CreateStringValue(ZaloAuth.Str(uploaded, "thumbUrl"));
            result["caption"] = JsonValue.CreateStringValue(caption ?? string.Empty);
            result["width"] = JsonValue.CreateNumberValue(width);
            result["height"] = JsonValue.CreateNumberValue(height);
            return result;
        }

        public static async Task<JsonObject> SendGifAsync(ZaloSession s, string threadId, bool isGroup,
            string fileName, IInputStream input, long totalSize, uint width, uint height, CancellationToken ct)
        {
            if (s == null || input == null || totalSize <= 0) throw new ArgumentException("GIF không có dữ liệu.");
            if (totalSize > 20L * 1024 * 1024) throw new InvalidOperationException("GIF vượt giới hạn 20 MB.");

            byte[] bytes;
            using (var reader = new DataReader(input))
            {
                uint loaded = await reader.LoadAsync((uint)totalSize).AsTask(ct);
                if (loaded != (uint)totalSize) throw new EndOfStreamException("Không đọc đủ dữ liệu GIF.");
                bytes = new byte[loaded];
                reader.ReadBytes(bytes);
            }

            long clientId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var thumbParams = new JsonObject();
            thumbParams["clientId"] = JsonValue.CreateNumberValue(clientId);
            thumbParams["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string thumbUrl = ZaloAuth.ApiUrl(s.ServiceUrl("file") + "/api/message/upthumb",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, thumbParams.Stringify())));
            IJsonValue thumbValue = await ZaloAuth.CallMultipartAsync(s, thumbUrl, "blob", bytes, ct,
                "fileContent", "image/png");
            JsonObject thumbData = ObjectValue(thumbValue);
            string uploadedThumb = ZaloAuth.Str(thumbData, "url");
            if (string.IsNullOrWhiteSpace(uploadedThumb))
                throw new InvalidOperationException("Máy chủ không trả ảnh thu nhỏ cho GIF.");

            CryptographicHash hash = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Md5).CreateHash();
            hash.Append(CryptographicBuffer.CreateFromByteArray(bytes));
            string checksum = CryptographicBuffer.EncodeToHexString(hash.GetValueAndReset()).ToLowerInvariant();

            var parameters = new JsonObject();
            parameters["clientId"] = JsonValue.CreateStringValue(clientId.ToString(CultureInfo.InvariantCulture));
            parameters["fileName"] = JsonValue.CreateStringValue(fileName);
            parameters["totalSize"] = JsonValue.CreateNumberValue(totalSize);
            parameters["width"] = JsonValue.CreateNumberValue(width);
            parameters["height"] = JsonValue.CreateNumberValue(height);
            parameters["msg"] = JsonValue.CreateStringValue(string.Empty);
            parameters["type"] = JsonValue.CreateNumberValue(1);
            parameters["ttl"] = JsonValue.CreateNumberValue(0);
            if (isGroup) parameters["visibility"] = JsonValue.CreateNumberValue(0);
            parameters[isGroup ? "grid" : "toid"] = JsonValue.CreateStringValue(threadId);
            parameters["thumb"] = JsonValue.CreateStringValue(uploadedThumb);
            parameters["checksum"] = JsonValue.CreateStringValue(checksum);
            parameters["totalChunk"] = JsonValue.CreateNumberValue(1);
            parameters["chunkId"] = JsonValue.CreateNumberValue(1);

            string route = isGroup ? "group" : "message";
            string sendUrl = ZaloAuth.ApiUrl(s.ServiceUrl("file") + "/api/" + route + "/gif",
                new KeyValuePair<string, string>("nretry", "0"),
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, parameters.Stringify())),
                new KeyValuePair<string, string>("type", "1"));
            IJsonValue sent = await ZaloAuth.CallMultipartAsync(s, sendUrl, fileName, bytes, ct);
            JsonObject result = ObjectValue(sent) ?? new JsonObject();
            result["previewUrl"] = JsonValue.CreateStringValue("ms-appx:///Assets/Emoticons/" + fileName);
            return result;
        }

        private static async Task<byte[]> ReadUploadChunkAsync(DataReader reader, int length, CancellationToken ct)
        {
            if (length <= 0) throw new InvalidOperationException("Kích thước phần tải lên không hợp lệ.");
            uint loaded = await reader.LoadAsync((uint)length).AsTask(ct);
            if (loaded != (uint)length) throw new EndOfStreamException("Tệp nguồn đã thay đổi trong khi tải lên.");
            var bytes = new byte[loaded];
            reader.ReadBytes(bytes);
            return bytes;
        }

        public static async Task<Dictionary<string, JsonObject>> GetGroupInfoMapAsync(ZaloSession s, List<string> ids, CancellationToken ct)
        {
            var details = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            if (ids.Count == 0) return details;

            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group") + "/api/group/getmg-v2");
            for (int offset = 0; offset < ids.Count; offset += 30)
            {
                List<string> batch = ids.Skip(offset).Take(30).ToList();
                string inner = "{" + string.Join(",", batch.Select(i => JsonValue.CreateStringValue(i).Stringify() + ":0")) + "}";
                string json = "{\"gridVerMap\":" + JsonValue.CreateStringValue(inner).Stringify() + "}";
                var form = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, json))
                };
                IJsonValue data = await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
                if (data.ValueType != JsonValueType.Object) continue;

                IJsonValue map;
                if (data.GetObject().TryGetValue("gridInfoMap", out map) && map.ValueType == JsonValueType.Object)
                {
                    foreach (KeyValuePair<string, IJsonValue> pair in map.GetObject())
                    {
                        if (pair.Value.ValueType != JsonValueType.Object) continue;
                        details[pair.Key] = pair.Value.GetObject();
                    }
                }
            }
            return details;
        }

        public static async Task<JsonObject> GetGroupInfoAsync(ZaloSession s, string groupId, CancellationToken ct)
        {
            var versions = new JsonObject();
            versions[groupId] = JsonValue.CreateNumberValue(0);
            var payload = new JsonObject();
            payload["gridVerMap"] = JsonValue.CreateStringValue(versions.Stringify());
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group") + "/api/group/getmg-v2");
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
            };
            IJsonValue data = await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
            JsonObject root = ObjectValue(data);
            IJsonValue map;
            if (root == null || !root.TryGetValue("gridInfoMap", out map) || map.ValueType != JsonValueType.Object)
                return null;
            IJsonValue info;
            return map.GetObject().TryGetValue(groupId, out info) && info.ValueType == JsonValueType.Object
                ? info.GetObject() : null;
        }

        public static async Task<ZaloGroupLinkInfo> GetGroupLinkInfoAsync(ZaloSession s, string groupId, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            var payload = new JsonObject();
            payload["grid"] = JsonValue.CreateStringValue(groupId);
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group") + "/api/group/link/detail",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify())));
            IJsonValue result = await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
            JsonObject data = ObjectValue(result);
            if (data == null) throw new InvalidOperationException("Máy chủ không trả thông tin link mời nhóm.");
            return new ZaloGroupLinkInfo
            {
                Enabled = ZaloAuth.Num(data, "enabled") == 1,
                Link = ZaloAuth.Str(data, "link"),
                Expiration = (long)ZaloAuth.Num(data, "expiration_date")
            };
        }

        public static async Task<ZaloGroupLinkInfo> EnableGroupLinkAsync(ZaloSession s, string groupId, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            var payload = new JsonObject();
            payload["grid"] = JsonValue.CreateStringValue(groupId);
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group") + "/api/group/link/new",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify())));
            IJsonValue result = await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
            JsonObject data = ObjectValue(result);
            if (data == null || string.IsNullOrWhiteSpace(ZaloAuth.Str(data, "link")))
                throw new InvalidOperationException("Zalo chưa tạo được link mời nhóm.");
            return new ZaloGroupLinkInfo
            {
                Enabled = ZaloAuth.Num(data, "enabled") != 0,
                Link = ZaloAuth.Str(data, "link"),
                Expiration = (long)ZaloAuth.Num(data, "expiration_date")
            };
        }

        public static async Task DisableGroupLinkAsync(ZaloSession s, string groupId, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            var payload = new JsonObject();
            payload["grid"] = JsonValue.CreateStringValue(groupId);
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group") + "/api/group/link/disable",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify())));
            await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
        }

        public static async Task<List<JsonObject>> GetFriendBoardListAsync(ZaloSession s, string friendId, CancellationToken ct)
        {
            if (s == null || string.IsNullOrWhiteSpace(friendId)) throw new InvalidOperationException("Missing friend board account.");
            var parameters = new JsonObject();
            parameters["conversationId"] = JsonValue.CreateStringValue(friendId);
            parameters["version"] = JsonValue.CreateNumberValue(0);
            parameters["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("friend_board") + "/api/friendboard/list",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, parameters.Stringify())));
            IJsonValue response = await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
            JsonObject root = ObjectValue(response);
            JsonArray entries = root == null ? null : ArrayValue(GetProperty(root, "data"));
            var posts = new List<JsonObject>();
            if (entries == null) return posts;
            foreach (IJsonValue entry in entries)
            {
                JsonObject post = ObjectValue(entry);
                if (post == null && entry.ValueType == JsonValueType.String)
                {
                    post = ZaloJson.ParseObject(entry.GetString());
                    if (post == null)
                    {
                        post = new JsonObject();
                        post["content"] = JsonValue.CreateStringValue(entry.GetString());
                    }
                }
                if (post != null) posts.Add(post);
            }
            return posts;
        }

        public static async Task<List<ZaloGroupBoardEntry>> GetGroupBoardAsync(ZaloSession s, string groupId,
            int page, int count, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(groupId)) throw new ArgumentException("Thiếu mã nhóm.", "groupId");
            var payload = new JsonObject();
            payload["group_id"] = JsonValue.CreateStringValue(groupId);
            payload["board_type"] = JsonValue.CreateNumberValue(0);
            payload["page"] = JsonValue.CreateNumberValue(Math.Max(1, page));
            payload["count"] = JsonValue.CreateNumberValue(Math.Max(1, Math.Min(50, count)));
            payload["last_id"] = JsonValue.CreateNumberValue(0);
            payload["last_type"] = JsonValue.CreateNumberValue(0);
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group_board") + "/api/board/list",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify())));
            IJsonValue result = await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
            JsonObject root = ObjectValue(result);
            JsonArray items = ArrayValue(result, "items");
            if (items == null && root != null)
            {
                IJsonValue data;
                if (root.TryGetValue("data", out data)) items = ArrayValue(data, "items");
            }
            var entries = new List<ZaloGroupBoardEntry>();
            if (items == null) return entries;
            foreach (IJsonValue value in items)
            {
                if (value.ValueType != JsonValueType.Object) continue;
                JsonObject item = value.GetObject();
                JsonObject data = ObjectValue(GetProperty(item, "data"));
                if (data == null) continue;
                int type = (int)ZaloAuth.Num(item, "boardType");
                JsonObject parameters = ObjectValue(GetProperty(data, "params"));
                if (parameters == null && GetProperty(data, "params") != null
                    && GetProperty(data, "params").ValueType == JsonValueType.String)
                    parameters = ZaloJson.ParseObject(GetProperty(data, "params").GetString());
                string title;
                string subtitle;
                string icon;
                if (type == 3)
                {
                    title = FirstNonBlank(ZaloAuth.Str(data, "question"), "Bình chọn");
                    subtitle = ZaloAuth.Num(data, "num_vote") + " lượt bình chọn";
                    icon = "📊";
                }
                else if (type == 1)
                {
                    title = FirstNonBlank(ZaloAuth.Str(parameters, "title"), ZaloAuth.Str(data, "title"), "Ghi chú");
                    subtitle = "Ghi chú nhóm";
                    icon = "📝";
                }
                else if (type == 2)
                {
                    title = FirstNonBlank(ZaloAuth.Str(parameters, "title"), ZaloAuth.Str(parameters, "msg"),
                        ZaloAuth.Str(parameters, "content"), ZaloAuth.Str(data, "title"), "Tin nhắn đã ghim");
                    subtitle = "Tin nhắn đã ghim";
                    icon = "📌";
                }
                else
                {
                    title = FirstNonBlank(ZaloAuth.Str(parameters, "title"), ZaloAuth.Str(data, "title"), "Hoạt động nhóm");
                    subtitle = "Bảng nhóm";
                    icon = "📋";
                }
                string timeValue = FirstNonBlank(ZaloAuth.Str(data, "createTime"), ZaloAuth.Str(data, "created_time"),
                    ZaloAuth.Str(data, "startTime"));
                string timeText = string.Empty;
                long timestamp;
                if (long.TryParse(timeValue, out timestamp) && timestamp > 0)
                {
                    if (timestamp < 100000000000L) timestamp *= 1000;
                    try { timeText = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime().ToString("dd/MM HH:mm"); }
                    catch (ArgumentOutOfRangeException) { }
                }
                long pollId = (long)ZaloAuth.Num(data, "poll_id");
                if (pollId <= 0) pollId = (long)ZaloAuth.Num(parameters, "poll_id");
                entries.Add(new ZaloGroupBoardEntry
                {
                    Icon = icon,
                    Title = title,
                    Subtitle = subtitle,
                    TimeText = timeText,
                    PollId = type == 3 ? pollId : 0,
                    TopicId = type == 1 ? FirstNonBlank(ZaloAuth.Str(data, "id"), ZaloAuth.Str(data, "topicId")) : string.Empty
                });
            }
            return entries;
        }

        public static async Task<List<ZaloGroupReminder>> GetGroupRemindersAsync(ZaloSession s, string groupId,
            int page, int count, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(groupId)) throw new ArgumentException("Thiếu mã nhóm.", "groupId");
            var requestData = new JsonObject();
            requestData["group_id"] = JsonValue.CreateStringValue(groupId);
            requestData["board_type"] = JsonValue.CreateNumberValue(1);
            requestData["page"] = JsonValue.CreateNumberValue(Math.Max(1, page));
            requestData["count"] = JsonValue.CreateNumberValue(Math.Max(1, Math.Min(50, count)));
            requestData["last_id"] = JsonValue.CreateNumberValue(0);
            requestData["last_type"] = JsonValue.CreateNumberValue(0);
            var payload = new JsonObject();
            payload["objectData"] = JsonValue.CreateStringValue(requestData.Stringify());
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group_board") + "/api/board/listReminder",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify())));
            IJsonValue result = await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
            JsonArray items = result != null && result.ValueType == JsonValueType.Array ? result.GetArray() : null;
            if (items == null && result != null && result.ValueType == JsonValueType.String)
                JsonArray.TryParse(result.GetString(), out items);
            var reminders = new List<ZaloGroupReminder>();
            if (items == null) return reminders;
            foreach (IJsonValue value in items)
            {
                if (value.ValueType != JsonValueType.Object) continue;
                JsonObject reminder = value.GetObject();
                JsonObject parameters = ObjectValue(GetProperty(reminder, "params"));
                if (parameters == null)
                {
                    string rawParams = ZaloAuth.Str(reminder, "params");
                    parameters = ZaloJson.ParseObject(rawParams);
                }
                long startTime = (long)ZaloAuth.Num(reminder, "startTime");
                if (startTime > 0 && startTime < 100000000000L) startTime *= 1000;
                string timeText = string.Empty;
                if (startTime > 0)
                {
                    try { timeText = DateTimeOffset.FromUnixTimeMilliseconds(startTime).ToLocalTime().ToString("dd/MM/yyyy HH:mm"); }
                    catch (ArgumentOutOfRangeException) { }
                }
                string id = FirstNonBlank(ZaloAuth.Str(reminder, "id"), ZaloAuth.Str(reminder, "topicId"), ZaloAuth.Str(reminder, "reminderId"));
                string title = FirstNonBlank(ZaloAuth.Str(parameters, "title"), "Nhắc hẹn nhóm");
                string repeat = "Không lặp";
                switch ((int)ZaloAuth.Num(reminder, "repeat"))
                {
                    case 1: repeat = "Lặp hằng ngày"; break;
                    case 2: repeat = "Lặp hằng tuần"; break;
                    case 3: repeat = "Lặp hằng tháng"; break;
                }
                JsonObject responses = ObjectValue(GetProperty(reminder, "responseMem"));
                int accepted = (int)ZaloAuth.Num(responses, "acceptMember");
                int rejected = (int)ZaloAuth.Num(responses, "rejectMember");
                reminders.Add(new ZaloGroupReminder
                {
                    Id = id,
                    Title = title,
                    TimeText = timeText,
                    StartTime = startTime,
                    RepeatMode = (int)ZaloAuth.Num(reminder, "repeat"),
                    RepeatText = repeat,
                    ResponseText = accepted + " đồng ý · " + rejected + " từ chối",
                    CanManage = ZaloAuth.Str(reminder, "creatorId") == s.Uid
                });
            }
            return reminders;
        }

        public static async Task CreateGroupReminderAsync(ZaloSession s, string groupId, string title,
            long startTime, int repeat, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(groupId)) throw new ArgumentException("Thiếu mã nhóm.", "groupId");
            string trimmedTitle = (title ?? string.Empty).Trim();
            if (trimmedTitle.Length == 0) throw new ArgumentException("Nhập nội dung nhắc hẹn.", "title");
            if (startTime <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                throw new ArgumentException("Thời gian nhắc hẹn phải ở tương lai.", "startTime");
            var reminderParams = new JsonObject();
            reminderParams["title"] = JsonValue.CreateStringValue(trimmedTitle);
            var payload = new JsonObject();
            payload["grid"] = JsonValue.CreateStringValue(groupId);
            payload["type"] = JsonValue.CreateNumberValue(0);
            payload["color"] = JsonValue.CreateNumberValue(-16245706);
            payload["emoji"] = JsonValue.CreateStringValue("⏰");
            payload["startTime"] = JsonValue.CreateNumberValue(startTime);
            payload["duration"] = JsonValue.CreateNumberValue(-1);
            payload["params"] = JsonValue.CreateStringValue(reminderParams.Stringify());
            payload["repeat"] = JsonValue.CreateNumberValue(Math.Max(0, Math.Min(3, repeat)));
            payload["src"] = JsonValue.CreateNumberValue(1);
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            payload["pinAct"] = JsonValue.CreateNumberValue(0);
            string encrypted = ZaloCrypto.Encode(s.SecretKey, payload.Stringify());
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group_board") + "/api/board/topic/createv2");
            var form = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("params", encrypted) };
            await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
        }

        public static async Task UpdateGroupReminderAsync(ZaloSession s, string groupId, string reminderId,
            string title, long startTime, int repeat, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(reminderId))
                throw new ArgumentException("Thiếu mã nhóm hoặc mã nhắc hẹn.");
            string trimmedTitle = (title ?? string.Empty).Trim();
            if (trimmedTitle.Length == 0) throw new ArgumentException("Nhập nội dung nhắc hẹn.", "title");
            if (startTime <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                throw new ArgumentException("Thời gian nhắc hẹn phải ở tương lai.", "startTime");
            var reminderParams = new JsonObject();
            reminderParams["title"] = JsonValue.CreateStringValue(trimmedTitle);
            var payload = new JsonObject();
            payload["grid"] = JsonValue.CreateStringValue(groupId);
            payload["type"] = JsonValue.CreateNumberValue(0);
            payload["color"] = JsonValue.CreateNumberValue(-16777216);
            payload["emoji"] = JsonValue.CreateStringValue(string.Empty);
            payload["startTime"] = JsonValue.CreateNumberValue(startTime);
            payload["duration"] = JsonValue.CreateNumberValue(-1);
            payload["params"] = JsonValue.CreateStringValue(reminderParams.Stringify());
            payload["topicId"] = JsonValue.CreateStringValue(reminderId);
            payload["repeat"] = JsonValue.CreateNumberValue(Math.Max(0, Math.Min(3, repeat)));
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            payload["pinAct"] = JsonValue.CreateNumberValue(2);
            string encrypted = ZaloCrypto.Encode(s.SecretKey, payload.Stringify());
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group_board") + "/api/board/topic/updatev2");
            var form = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("params", encrypted) };
            await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
        }

        public static async Task RemoveGroupReminderAsync(ZaloSession s, string groupId, string reminderId, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(reminderId))
                throw new ArgumentException("Thiếu mã nhóm hoặc mã nhắc hẹn.");
            var payload = new JsonObject();
            payload["grid"] = JsonValue.CreateStringValue(groupId);
            payload["topicId"] = JsonValue.CreateStringValue(reminderId);
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string encrypted = ZaloCrypto.Encode(s.SecretKey, payload.Stringify());
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group_board") + "/api/board/topic/remove");
            var form = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("params", encrypted) };
            await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
        }

        public static async Task<ZaloPollDetail> GetPollDetailAsync(ZaloSession s, long pollId, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (pollId <= 0) throw new ArgumentException("Mã bình chọn không hợp lệ.", "pollId");
            var payload = new JsonObject();
            payload["poll_id"] = JsonValue.CreateNumberValue(pollId);
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string encrypted = ZaloCrypto.Encode(s.SecretKey, payload.Stringify());
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group") + "/api/poll/detail");
            var form = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("params", encrypted) };
            JsonObject detail = ObjectValue(await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct));
            if (detail == null) throw new InvalidOperationException("Zalo không trả chi tiết bình chọn.");
            var poll = new ZaloPollDetail
            {
                PollId = (long)ZaloAuth.Num(detail, "poll_id"),
                Question = FirstNonBlank(ZaloAuth.Str(detail, "question"), "Bình chọn"),
                Creator = ZaloAuth.Str(detail, "creator"),
                Joined = ZaloAuth.Str(detail, "joined") == "true" || ZaloAuth.Num(detail, "joined") > 0,
                Closed = ZaloAuth.Str(detail, "closed") == "true" || ZaloAuth.Num(detail, "closed") > 0,
                AllowMultiple = ZaloAuth.Str(detail, "allow_multi_choices") == "true" || ZaloAuth.Num(detail, "allow_multi_choices") > 0,
                VoteCount = (int)ZaloAuth.Num(detail, "num_vote")
            };
            IJsonValue optionsValue;
            if (detail.TryGetValue("options", out optionsValue) && optionsValue.ValueType == JsonValueType.Array)
            {
                foreach (IJsonValue value in optionsValue.GetArray())
                {
                    if (value.ValueType != JsonValueType.Object) continue;
                    JsonObject option = value.GetObject();
                    poll.Options.Add(new ZaloPollOption
                    {
                        OptionId = (long)ZaloAuth.Num(option, "option_id"),
                        Content = ZaloAuth.Str(option, "content"),
                        Votes = (int)ZaloAuth.Num(option, "votes"),
                        Voted = ZaloAuth.Str(option, "voted") == "true" || ZaloAuth.Num(option, "voted") > 0
                    });
                }
            }
            if (poll.Options.Count == 0) throw new InvalidOperationException("Bình chọn không có lựa chọn nào.");
            return poll;
        }

        public static async Task VotePollAsync(ZaloSession s, long pollId, IEnumerable<long> selectedOptionIds, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (pollId <= 0) throw new ArgumentException("Mã bình chọn không hợp lệ.", "pollId");
            var options = new JsonArray();
            foreach (long optionId in (selectedOptionIds ?? Enumerable.Empty<long>()).Where(id => id > 0).Distinct())
                options.Add(JsonValue.CreateNumberValue(optionId));
            var payload = new JsonObject();
            payload["poll_id"] = JsonValue.CreateNumberValue(pollId);
            payload["option_ids"] = options;
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string encrypted = ZaloCrypto.Encode(s.SecretKey, payload.Stringify());
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group") + "/api/poll/vote",
                new KeyValuePair<string, string>("params", encrypted));
            await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
        }

        public static async Task CreatePollAsync(ZaloSession s, string groupId, string question,
            IEnumerable<string> optionTexts, bool allowMultiple, bool isAnonymous, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(groupId)) throw new ArgumentException("Thiếu mã nhóm.", "groupId");
            string trimmedQuestion = (question ?? string.Empty).Trim();
            if (trimmedQuestion.Length == 0) throw new ArgumentException("Nhập câu hỏi bình chọn.", "question");
            List<string> options = (optionTexts ?? Enumerable.Empty<string>())
                .Select(value => (value ?? string.Empty).Trim()).Where(value => value.Length > 0).Take(10).ToList();
            if (options.Count < 2) throw new ArgumentException("Bình chọn cần ít nhất hai lựa chọn.", "optionTexts");

            var optionArray = new JsonArray();
            foreach (string option in options) optionArray.Add(JsonValue.CreateStringValue(option));
            var payload = new JsonObject();
            payload["group_id"] = JsonValue.CreateStringValue(groupId);
            payload["question"] = JsonValue.CreateStringValue(trimmedQuestion);
            payload["options"] = optionArray;
            payload["expired_time"] = JsonValue.CreateNumberValue(0);
            payload["pinAct"] = JsonValue.CreateBooleanValue(false);
            payload["allow_multi_choices"] = JsonValue.CreateBooleanValue(allowMultiple);
            payload["allow_add_new_option"] = JsonValue.CreateBooleanValue(false);
            payload["is_hide_vote_preview"] = JsonValue.CreateBooleanValue(false);
            payload["is_anonymous"] = JsonValue.CreateBooleanValue(isAnonymous);
            payload["poll_type"] = JsonValue.CreateNumberValue(0);
            payload["src"] = JsonValue.CreateNumberValue(1);
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string encrypted = ZaloCrypto.Encode(s.SecretKey, payload.Stringify());
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group") + "/api/poll/create");
            var form = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("params", encrypted) };
            await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
        }

        public static async Task CreateGroupNoteAsync(ZaloSession s, string groupId, string title,
            bool pin, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(groupId)) throw new ArgumentException("Thiếu mã nhóm.", "groupId");
            string trimmedTitle = (title ?? string.Empty).Trim();
            if (trimmedTitle.Length == 0) throw new ArgumentException("Nhập nội dung ghi chú.", "title");
            var noteParams = new JsonObject();
            noteParams["title"] = JsonValue.CreateStringValue(trimmedTitle);
            var payload = new JsonObject();
            payload["grid"] = JsonValue.CreateStringValue(groupId);
            payload["type"] = JsonValue.CreateNumberValue(0);
            payload["color"] = JsonValue.CreateNumberValue(-16777216);
            payload["emoji"] = JsonValue.CreateStringValue(string.Empty);
            payload["startTime"] = JsonValue.CreateNumberValue(-1);
            payload["duration"] = JsonValue.CreateNumberValue(-1);
            payload["params"] = JsonValue.CreateStringValue(noteParams.Stringify());
            payload["repeat"] = JsonValue.CreateNumberValue(0);
            payload["src"] = JsonValue.CreateNumberValue(1);
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            payload["pinAct"] = JsonValue.CreateNumberValue(pin ? 1 : 0);
            string encrypted = ZaloCrypto.Encode(s.SecretKey, payload.Stringify());
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group_board") + "/api/board/topic/createv2");
            var form = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("params", encrypted) };
            await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
        }

        public static async Task UpdateGroupNoteAsync(ZaloSession s, string groupId, string topicId,
            string title, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(topicId))
                throw new ArgumentException("Thiếu mã nhóm hoặc mã ghi chú.");
            string trimmedTitle = (title ?? string.Empty).Trim();
            if (trimmedTitle.Length == 0) throw new ArgumentException("Nhập nội dung ghi chú.", "title");
            var noteParams = new JsonObject();
            noteParams["title"] = JsonValue.CreateStringValue(trimmedTitle);
            var payload = new JsonObject();
            payload["grid"] = JsonValue.CreateStringValue(groupId);
            payload["type"] = JsonValue.CreateNumberValue(0);
            payload["color"] = JsonValue.CreateNumberValue(-16777216);
            payload["emoji"] = JsonValue.CreateStringValue(string.Empty);
            payload["startTime"] = JsonValue.CreateNumberValue(-1);
            payload["duration"] = JsonValue.CreateNumberValue(-1);
            payload["params"] = JsonValue.CreateStringValue(noteParams.Stringify());
            payload["topicId"] = JsonValue.CreateStringValue(topicId);
            payload["repeat"] = JsonValue.CreateNumberValue(0);
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            payload["pinAct"] = JsonValue.CreateNumberValue(2);
            string encrypted = ZaloCrypto.Encode(s.SecretKey, payload.Stringify());
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group_board") + "/api/board/topic/updatev2");
            var form = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("params", encrypted) };
            await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
        }

        private static string FirstNonBlank(params string[] values)
        {
            foreach (string value in values)
                if (!string.IsNullOrWhiteSpace(value)) return value;
            return string.Empty;
        }

        public static async Task ChangeGroupNameAsync(ZaloSession s, string groupId, string name, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(groupId)) throw new ArgumentException("Thiếu mã nhóm.", "groupId");
            string trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > 50) throw new ArgumentException("Tên nhóm phải có từ 1 đến 50 ký tự.", "name");
            var payload = new JsonObject();
            payload["grid"] = JsonValue.CreateStringValue(groupId);
            payload["gname"] = JsonValue.CreateStringValue(trimmed);
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group") + "/api/group/updateinfo");
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
            };
            await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
        }

        public static async Task SetGroupDeputyAsync(ZaloSession s, string groupId, string memberId, bool deputy, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(memberId))
                throw new ArgumentException("Thiếu nhóm hoặc thành viên.");
            var members = new JsonArray();
            members.Add(JsonValue.CreateStringValue(memberId));
            var payload = new JsonObject();
            payload["grid"] = JsonValue.CreateStringValue(groupId);
            payload["members"] = members;
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string route = deputy ? "/api/group/admins/add" : "/api/group/admins/remove";
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group") + route,
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify())));
            await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
        }

        public static async Task ChangeGroupOwnerAsync(ZaloSession s, string groupId, string newOwnerId, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(newOwnerId))
                throw new ArgumentException("Thiếu nhóm hoặc thành viên.");
            var payload = new JsonObject();
            payload["grid"] = JsonValue.CreateStringValue(groupId);
            payload["newAdminId"] = JsonValue.CreateStringValue(newOwnerId);
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            payload["language"] = JsonValue.CreateStringValue("vi");
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group") + "/api/group/change-owner",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify())));
            await ZaloAuth.CallAsync(s, HttpMethod.Get, url, null, ct);
        }

        public static async Task RemoveGroupMemberAsync(ZaloSession s, string groupId, string memberId, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(memberId))
                throw new ArgumentException("Thiếu nhóm hoặc thành viên.");
            var members = new JsonArray();
            members.Add(JsonValue.CreateStringValue(memberId));
            var payload = new JsonObject();
            payload["grid"] = JsonValue.CreateStringValue(groupId);
            payload["members"] = members;
            payload["imei"] = JsonValue.CreateStringValue(s.Imei ?? string.Empty);
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group") + "/api/group/kickout");
            IJsonValue result = await ZaloAuth.CallAsync(s, HttpMethod.Post, url,
                new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
                }, ct);
            JsonObject data = ObjectValue(result);
            IJsonValue errors;
            if (data != null && data.TryGetValue("errorMembers", out errors)
                && errors.ValueType == JsonValueType.Array && errors.GetArray().Count > 0)
                throw new InvalidOperationException("Zalo từ chối xóa thành viên khỏi nhóm.");
        }

        public static async Task InviteGroupMembersAsync(ZaloSession s, string groupId, List<string> memberIds, CancellationToken ct)
        {
            if (memberIds == null || memberIds.Count == 0) throw new ArgumentException("Chọn ít nhất một người bạn.");
            var members = new JsonArray();
            var memberTypes = new JsonArray();
            foreach (string id in memberIds)
            {
                members.Add(JsonValue.CreateStringValue(id));
                memberTypes.Add(JsonValue.CreateNumberValue(-1));
            }
            var payload = new JsonObject();
            payload["grid"] = JsonValue.CreateStringValue(groupId);
            payload["members"] = members;
            payload["memberTypes"] = memberTypes;
            payload["imei"] = JsonValue.CreateStringValue(s.Imei);
            payload["clientLang"] = JsonValue.CreateStringValue("vi");
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group") + "/api/group/invite/v2");
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
            };
            IJsonValue result = await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
            JsonObject data = ObjectValue(result);
            IJsonValue errors;
            if (data != null && data.TryGetValue("errorMembers", out errors)
                && errors.ValueType == JsonValueType.Array && errors.GetArray().Count > 0)
                throw new InvalidOperationException("Zalo không thêm được một số thành viên. Hãy tải lại danh sách để kiểm tra.");
        }

        public static async Task LeaveGroupAsync(ZaloSession s, string groupId, CancellationToken ct)
        {
            var groups = new JsonArray();
            groups.Add(JsonValue.CreateStringValue(groupId));
            var payload = new JsonObject();
            payload["grids"] = groups;
            payload["imei"] = JsonValue.CreateStringValue(s.Imei);
            payload["silent"] = JsonValue.CreateNumberValue(0);
            payload["language"] = JsonValue.CreateStringValue("vi");
            string url = ZaloAuth.ApiUrl(s.ServiceUrl("group") + "/api/group/leave");
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(s.SecretKey, payload.Stringify()))
            };
            IJsonValue response = await ZaloAuth.CallAsync(s, HttpMethod.Post, url, form, ct);
            JsonObject result = ObjectValue(response);
            IJsonValue errors;
            if (result != null && result.TryGetValue("memberError", out errors)
                && errors.ValueType == JsonValueType.Array && errors.GetArray().Count > 0)
                throw new InvalidOperationException("Zalo chưa xác nhận việc rời nhóm.");
        }
    }


}
