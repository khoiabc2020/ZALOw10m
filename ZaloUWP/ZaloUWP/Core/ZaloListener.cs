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
    // ===================================================================== event decoding

    public static class ZaloEventDecoder
    {
        /// <summary>Opens the "data" field of a WebSocket event (plain, compressed and/or encrypted).</summary>
        public static JsonObject Decode(JsonObject parsed, string cipherKey)
        {
            string raw = ZaloAuth.Str(parsed, "data");
            int encrypt = (int)ZaloAuth.Num(parsed, "encrypt");
            if (encrypt == 0) return ZaloJson.ParseObject(raw);

            byte[] buffer = Convert.FromBase64String(encrypt == 1 ? raw : Uri.UnescapeDataString(raw));
            if (encrypt != 1)
            {
                if (string.IsNullOrEmpty(cipherKey) || buffer.Length < 48)
                    throw new InvalidOperationException("Cannot decrypt: missing key or data too short.");
                byte[] iv = new byte[16];
                Array.Copy(buffer, 0, iv, 0, 16);
                int cipherLength = buffer.Length - 32 - 16; // 16 IV + 16 extra data in front, 16-byte tag at the end
                byte[] cipher = new byte[cipherLength];
                Array.Copy(buffer, 32, cipher, 0, cipherLength);
                buffer = ZaloGcm.Decrypt(Convert.FromBase64String(cipherKey), iv, cipher);
            }
            if (encrypt != 3) buffer = Inflate(buffer);
            return ZaloJson.ParseObject(Encoding.UTF8.GetString(buffer, 0, buffer.Length));
        }

        private static byte[] Inflate(byte[] data)
        {
            bool gzip = data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b;
            bool zlib = !gzip && data.Length > 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0;
            int skip = zlib ? 2 : 0;
            using (var input = new MemoryStream(data, skip, data.Length - skip))
            using (Stream z = gzip
                ? (Stream)new GZipStream(input, CompressionMode.Decompress)
                : new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                z.CopyTo(output);
                return output.ToArray();
            }
        }
    }


    // ===================================================================== live connection

    public sealed class ZaloIncoming
    {
        public bool IsGroup;
        public bool IsOld;
        public JsonObject Msg;
    }

    public sealed class ZaloTyping
    {
        public bool IsGroup { get; set; }
        public string ThreadId { get; set; }
        public string UserId { get; set; }
        public string PhoneNumber { get; set; }
    }

    public sealed class ZaloGroupEvent
    {
        public string GroupId { get; set; }
        public string Act { get; set; }
        public string SourceId { get; set; }
        public string GroupName { get; set; }
        public string MemberNames { get; set; }
        public string EventId { get; set; }
        public long Timestamp { get; set; }
    }

    public sealed class ZaloListener
    {
        private readonly ZaloSession _session;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private MessageWebSocket _socket;
        private DataWriter _writer;
        private string _cipherKey;
        private Timer _pingTimer;
        private int _requestId;
        private int _attempt;
        private int _generation;
        private int _frameCount;
        private bool _stopped;
        private bool _gotKey;
        private bool _useExplicitCookie;
        private bool _reconnectPending;
        private readonly object _uploadLock = new object();
        private readonly Dictionary<string, string> _completedUploadUrls = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<string>> _pendingUploads = new Dictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal);
        private readonly object _historyLock = new object();
        private readonly Dictionary<int, TaskCompletionSource<int>> _pendingHistoryRequests = new Dictionary<int, TaskCompletionSource<int>>();

        public event Action<ZaloIncoming> MessageReceived;
        public event Action<ZaloTyping> TypingReceived;
        public event Action<ZaloGroupEvent> GroupEventReceived;
        public event Action<JsonObject, bool> ReceiptReceived;
        public event Action<JsonObject, bool> ReactionReceived;
        public event Action<string> StatusChanged;

        public ZaloListener(ZaloSession session)
        {
            _session = session;
        }

        public Task StartAsync()
        {
            _stopped = false;
            return ConnectAsync();
        }

        public void Stop()
        {
            _stopped = true;
            _generation++;
            CloseSocket();
        }

        /// <summary>Opens a fresh connection (used after the app wakes up from sleep).</summary>
        public Task RestartAsync()
        {
            _stopped = false;
            _attempt = 0;
            return ConnectAsync();
        }

        public async Task<string> WaitForUploadedFileAsync(string fileId, TimeSpan timeout)
        {
            TaskCompletionSource<string> completion;
            lock (_uploadLock)
            {
                string completed;
                if (_completedUploadUrls.TryGetValue(fileId, out completed))
                {
                    _completedUploadUrls.Remove(fileId);
                    return completed;
                }
                completion = new TaskCompletionSource<string>();
                _pendingUploads[fileId] = completion;
            }

            Task finished = await Task.WhenAny(completion.Task, Task.Delay(timeout));
            lock (_uploadLock)
            {
                _pendingUploads.Remove(fileId);
                _completedUploadUrls.Remove(fileId);
            }
            if (finished != completion.Task) throw new TimeoutException("Máy chủ chưa hoàn tất tải tệp ghi âm.");
            return await completion.Task;
        }

        public async Task<int> RequestOlderMessagesAsync(bool isGroup, string lastMessageId, CancellationToken ct)
        {
            int command = isGroup ? 511 : 510;
            if (_writer == null || !_gotKey) throw new InvalidOperationException("Đang mất kết nối tới máy chủ tin nhắn.");
            if (string.IsNullOrWhiteSpace(lastMessageId)) throw new InvalidOperationException("Không tìm thấy mã tin nhắn làm mốc tải lịch sử.");

            var completion = new TaskCompletionSource<int>();
            lock (_historyLock)
            {
                if (_pendingHistoryRequests.ContainsKey(command))
                    throw new InvalidOperationException("Yêu cầu tải lịch sử trước đó vẫn đang xử lý.");
                _pendingHistoryRequests[command] = completion;
            }

            try
            {
                var payload = new JsonObject();
                payload["first"] = JsonValue.CreateBooleanValue(true);
                payload["lastId"] = JsonValue.CreateStringValue(lastMessageId);
                payload["preIds"] = new JsonArray();
                await SendFrameAsync(command, 1, payload.Stringify());

                Task timeout = Task.Delay(TimeSpan.FromSeconds(12), ct);
                Task finished = await Task.WhenAny(completion.Task, timeout);
                if (finished != completion.Task)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException("Máy chủ không phản hồi yêu cầu tải tin nhắn cũ.");
                }
                return await completion.Task;
            }
            finally
            {
                lock (_historyLock)
                {
                    TaskCompletionSource<int> current;
                    if (_pendingHistoryRequests.TryGetValue(command, out current) && ReferenceEquals(current, completion))
                        _pendingHistoryRequests.Remove(command);
                }
            }
        }

        private void SetStatus(string text)
        {
            Action<string> handler = StatusChanged;
            if (handler != null) handler(text);
        }

        private static void TryHeader(MessageWebSocket socket, string name, string value)
        {
            try { socket.SetRequestHeader(name, value); }
            catch (Exception ex) { ZaloLog.Add("Header " + name + " not accepted: " + ex.Message); }
        }

        private async Task ConnectAsync()
        {
            int generation = ++_generation;
            CloseSocket();
            _gotKey = false;
            _cipherKey = null;
            SetStatus("Đang kết nối để nhận tin...");

            var socket = new MessageWebSocket();
            socket.Control.MessageType = SocketMessageType.Binary;
            socket.Control.MaxMessageSize = 8 * 1024 * 1024;
            TryHeader(socket, "Origin", "https://chat.zalo.me");
            TryHeader(socket, "User-Agent", _session.UserAgent);
            if (_useExplicitCookie) TryHeader(socket, "Cookie", _session.Http.CookieHeader());
            socket.MessageReceived += (s, e) => OnSocketMessage(generation, e);
            socket.Closed += (s, e) => OnSocketClosed(generation, e.Code, e.Reason);

            try
            {
                if (_session.WsUrls.Count == 0) throw new InvalidOperationException("Zalo gave no message server address.");
                string baseUrl = _session.WsUrls[0];
                string url = ZaloAuth.ApiUrl(baseUrl,
                    new KeyValuePair<string, string>("t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()));
                await socket.ConnectAsync(new Uri(url));
                if (generation != _generation)
                {
                    socket.Dispose();
                    return;
                }
                _socket = socket;
                _writer = new DataWriter(socket.OutputStream);
                ZaloLog.Add("WebSocket connected" + (_useExplicitCookie ? " (explicit cookie)" : ""));
            }
            catch (Exception ex)
            {
                try { socket.Dispose(); } catch (Exception) { }
                ZaloLog.Add("WebSocket connect failed: " + ex.Message);
                _useExplicitCookie = !_useExplicitCookie;
                ScheduleReconnect();
            }
        }

        private async void ScheduleReconnect()
        {
            if (_stopped || _reconnectPending) return;
            _reconnectPending = true;
            _attempt++;
            int delay = Math.Min(60, 2 * _attempt);
            SetStatus("Mất kết nối, thử lại sau " + delay + " giây...");
            await Task.Delay(delay * 1000);
            _reconnectPending = false;
            if (!_stopped) await ConnectAsync();
        }

        private void CloseSocket()
        {
            StopPing();
            try
            {
                if (_writer != null)
                {
                    _writer.DetachStream();
                    _writer.Dispose();
                }
            }
            catch (Exception) { }
            try
            {
                if (_socket != null)
                {
                    _socket.Close(1000, "");
                    _socket.Dispose();
                }
            }
            catch (Exception) { }
            _writer = null;
            _socket = null;
        }

        private void StopPing()
        {
            if (_pingTimer != null)
            {
                _pingTimer.Dispose();
                _pingTimer = null;
            }
        }

        private void OnSocketClosed(int generation, uint code, string reason)
        {
            if (generation != _generation) return;
            StopPing();
            ZaloLog.Add("WebSocket closed: " + code + " " + reason);
            if (_stopped) return;
            if (code == 3000)
            {
                _stopped = true;
                SetStatus("Tài khoản đang mở ở nơi khác (ví dụ Zalo Web) nên kết nối nhận tin bị đóng.");
                return;
            }
            if (!_gotKey) _useExplicitCookie = !_useExplicitCookie;
            ScheduleReconnect();
        }

        private async void OnSocketMessage(int generation, MessageWebSocketMessageReceivedEventArgs args)
        {
            if (generation != _generation) return;
            try
            {
                byte[] data;
                using (DataReader reader = args.GetDataReader())
                {
                    data = new byte[reader.UnconsumedBufferLength];
                    reader.ReadBytes(data);
                }
                await HandleFrameAsync(data);
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Message error: " + ex.Message);
            }
        }

        private async Task HandleFrameAsync(byte[] data)
        {
            if (data.Length < 4) return;
            int version = data[0];
            int cmd = data[1] | (data[2] << 8);
            int sub = data[3];
            string text = Encoding.UTF8.GetString(data, 4, data.Length - 4);
            if (text.Length == 0) return;

            if (_frameCount < 40) ZaloLog.Add("WS frame v" + version + " cmd " + cmd + "/" + sub + " (" + data.Length + " bytes)");
            _frameCount++;

            JsonObject parsed = ZaloJson.ParseObject(text);
            if (parsed == null) return;

            // Hook for Phone Sync events (act=user_confirm, syncmsg_info, transfer_error)
            if (parsed.ContainsKey("act"))
            {
                ZaloPhoneSync.ProcessEvent(parsed);
                ProcessUploadEvents(parsed);
            }
            else if (!string.IsNullOrEmpty(_cipherKey) && parsed.ContainsKey("data"))
            {
                try
                {
                    JsonObject decoded = ZaloEventDecoder.Decode(parsed, _cipherKey);
                    if (decoded != null)
                    {
                        ZaloPhoneSync.ProcessEvent(decoded);
                        ProcessUploadEvents(decoded);
                    }
                }
                catch { }
            }

            if (version == 1 && cmd == 1 && sub == 1 && parsed.ContainsKey("key"))
            {
                _cipherKey = ZaloAuth.Str(parsed, "key");
                _gotKey = true;
                _attempt = 0;
                StartPing();
                SetStatus("Đã kết nối");
                ZaloLog.Add("Got the message key");
                // ask for the most recent messages so the conversation list is not empty
                await SendFrameAsync(510, 1, "{\"first\":true,\"lastId\":null,\"preIds\":[],\"req_id\":\"req_" + (_requestId++) + "\"}");
                await SendFrameAsync(511, 1, "{\"first\":true,\"lastId\":null,\"preIds\":[],\"req_id\":\"req_" + (_requestId++) + "\"}");
            }
            else if (version == 1 && cmd == 501 && sub == 0) Emit(parsed, "msgs", false, false);
            else if (version == 1 && cmd == 521 && sub == 0) Emit(parsed, "groupMsgs", true, false);
            else if (cmd == 510 && sub == 1) Emit(parsed, "msgs", false, true);
            else if (cmd == 511 && sub == 1) Emit(parsed, "groupMsgs", true, true);
            else if (cmd == 602 && sub == 0) EmitTyping(parsed);
            else if (version == 1 && cmd == 502 && sub == 0) EmitReceipts(parsed, false);
            else if (version == 1 && cmd == 522 && sub == 0) EmitReceipts(parsed, true);
            else if (version == 1 && cmd == 612 && sub == 0) EmitReactions(parsed);
            else if (version == 1 && cmd == 3000 && sub == 0)
            {
                _stopped = true;
                _generation++;
                CloseSocket();
                SetStatus("Tài khoản đang mở ở nơi khác (ví dụ Zalo Web) nên kết nối nhận tin bị đóng.");
            }
        }

        private void EmitTyping(JsonObject parsed)
        {
            JsonObject decoded = ZaloEventDecoder.Decode(parsed, _cipherKey);
            JsonObject data = decoded == null ? null : ObjectFrom(decoded, "data");
            JsonArray actions = data == null ? null : ArrayFrom(data, "actions");
            if (actions == null) return;
            Action<ZaloTyping> handler = TypingReceived;
            if (handler == null) return;
            foreach (IJsonValue value in actions)
            {
                if (value.ValueType != JsonValueType.Object) continue;
                JsonObject action = value.GetObject();
                if (ZaloAuth.Str(action, "act_type") != "typing") continue;
                string act = ZaloAuth.Str(action, "act");
                bool group = act == "gtyping";
                if (!group && act != "typing") continue;
                IJsonValue eventData;
                JsonObject typing = action.TryGetValue("data", out eventData) && eventData.ValueType == JsonValueType.Object
                    ? eventData.GetObject() : null;
                if (typing == null && eventData != null && eventData.ValueType == JsonValueType.String)
                {
                    string raw = eventData.GetString();
                    typing = ZaloJson.ParseObject(raw);
                    if (typing == null) typing = ZaloJson.ParseObject("{" + raw + "}");
                }
                string threadId = group ? ZaloAuth.Str(typing, "gid") : ZaloAuth.Str(typing, "uid");
                if (string.IsNullOrWhiteSpace(threadId)) continue;
                handler(new ZaloTyping { IsGroup = group, ThreadId = threadId, UserId = ZaloAuth.Str(typing, "uid") });
            }
        }

        private void EmitReceipts(JsonObject parsed, bool isGroup)
        {
            try
            {
                JsonObject decoded = ZaloEventDecoder.Decode(parsed, _cipherKey);
                JsonObject payload = ObjectFrom(decoded, "data") ?? decoded;
                if (payload == null) return;
                Action<JsonObject, bool> handler = ReceiptReceived;
                if (handler != null) handler(payload, isGroup);
            }
            catch (Exception ex) { ZaloLog.Add("Receipt event decode failed: " + ex.Message); }
        }

        private void EmitReactions(JsonObject parsed)
        {
            try
            {
                JsonObject decoded = ZaloEventDecoder.Decode(parsed, _cipherKey);
                JsonObject payload = ObjectFrom(decoded, "data") ?? decoded;
                Action<JsonObject, bool> handler = ReactionReceived;
                if (payload != null && handler != null) handler(payload, false);
            }
            catch (Exception ex) { ZaloLog.Add("Reaction event decode failed: " + ex.Message); }
        }

        private static JsonObject ObjectFrom(JsonObject source, string key)
        {
            IJsonValue value;
            return source != null && source.TryGetValue(key, out value) && value.ValueType == JsonValueType.Object
                ? value.GetObject() : null;
        }

        private static JsonArray ArrayFrom(JsonObject source, string key)
        {
            IJsonValue value;
            return source != null && source.TryGetValue(key, out value) && value.ValueType == JsonValueType.Array
                ? value.GetArray() : null;
        }

        private void ProcessUploadEvents(JsonObject decoded)
        {
            if (decoded == null) return;
            JsonObject container = decoded;
            IJsonValue dataValue;
            if (decoded.TryGetValue("data", out dataValue) && dataValue.ValueType == JsonValueType.Object)
                container = dataValue.GetObject();
            IJsonValue controlsValue;
            if (!container.TryGetValue("controls", out controlsValue) || controlsValue.ValueType != JsonValueType.Array) return;

            foreach (IJsonValue controlValue in controlsValue.GetArray())
            {
                if (controlValue.ValueType != JsonValueType.Object) continue;
                JsonObject control = controlValue.GetObject();
                IJsonValue contentValue;
                JsonObject content = control.TryGetValue("content", out contentValue) && contentValue.ValueType == JsonValueType.Object
                    ? contentValue.GetObject() : null;
                if (content == null) continue;
                if (ZaloAuth.Str(content, "act_type") == "group")
                {
                    EmitGroupEvent(content);
                    continue;
                }
                if (ZaloAuth.Str(content, "act_type") != "file_done") continue;
                string fileId = ZaloAuth.Str(content, "fileId");
                IJsonValue fileDataValue;
                JsonObject fileData = content.TryGetValue("data", out fileDataValue) && fileDataValue.ValueType == JsonValueType.Object
                    ? fileDataValue.GetObject() : null;
                string fileUrl = ZaloAuth.Str(fileData, "url");
                if (fileId.Length == 0 || fileUrl.Length == 0) continue;

                TaskCompletionSource<string> pending = null;
                lock (_uploadLock)
                {
                    if (!_pendingUploads.TryGetValue(fileId, out pending)) _completedUploadUrls[fileId] = fileUrl;
                }
                if (pending != null) pending.TrySetResult(fileUrl);
            }
        }

        private void EmitGroupEvent(JsonObject controlContent)
        {
            string act = ZaloAuth.Str(controlContent, "act");
            if (act.Length == 0 || act == "join_reject" || act == "join_request") return;
            IJsonValue raw;
            if (!controlContent.TryGetValue("data", out raw)) return;
            JsonObject data = raw.ValueType == JsonValueType.Object ? raw.GetObject()
                : raw.ValueType == JsonValueType.String ? ZaloJson.ParseObject(raw.GetString()) : null;
            if (data == null) return;
            string groupId = FirstEventString(data, "group_id", "groupId");
            if (groupId.Length == 0) return;

            string sourceId = FirstEventString(data, "sourceId", "source_id");
            string groupName = FirstEventString(data, "groupName", "group_name");
            var memberNames = new List<string>();
            JsonArray members = ArrayFrom(data, "updateMembers");
            if (members == null) members = ArrayFrom(data, "update_members");
            if (members != null)
            {
                foreach (IJsonValue memberValue in members)
                {
                    JsonObject member = memberValue.ValueType == JsonValueType.Object ? memberValue.GetObject() : null;
                    string name = FirstEventString(member, "dName", "name", "displayName");
                    if (name.Length > 0 && !memberNames.Contains(name)) memberNames.Add(name);
                }
            }
            string eventId = FirstEventString(data, "clientId", "client_id");
            string rawTime = ZaloAuth.Str(data, "time");
            long timestamp;
            if (!long.TryParse(rawTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out timestamp))
            {
                DateTimeOffset parsedTime;
                timestamp = DateTimeOffset.TryParse(rawTime, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsedTime)
                    ? parsedTime.ToUnixTimeMilliseconds() : 0;
            }
            if (timestamp > 0 && timestamp < 100000000000L) timestamp *= 1000;
            var handler = GroupEventReceived;
            if (handler == null) return;
            handler(new ZaloGroupEvent
            {
                GroupId = groupId,
                Act = act,
                SourceId = sourceId,
                GroupName = groupName,
                MemberNames = string.Join(", ", memberNames),
                EventId = eventId,
                Timestamp = timestamp > 0 ? timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        private static string FirstEventString(JsonObject source, params string[] keys)
        {
            foreach (string key in keys)
            {
                string value = ZaloAuth.Str(source, key);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return string.Empty;
        }

        private void Emit(JsonObject parsed, string listKey, bool isGroup, bool isOld)
        {
            JsonObject decoded = ZaloEventDecoder.Decode(parsed, _cipherKey);
            if (decoded == null) return;
            IJsonValue dataValue;
            if (!decoded.TryGetValue("data", out dataValue) || dataValue.ValueType != JsonValueType.Object) return;
            IJsonValue list;
            if (!dataValue.GetObject().TryGetValue(listKey, out list) || list.ValueType != JsonValueType.Array) return;

            Action<ZaloIncoming> handler = MessageReceived;
            if (handler == null) return;
            int count = 0;
            foreach (IJsonValue item in list.GetArray())
            {
                if (item.ValueType != JsonValueType.Object) continue;
                handler(new ZaloIncoming { IsGroup = isGroup, IsOld = isOld, Msg = item.GetObject() });
                count++;
            }
            if (isOld)
            {
                ZaloLog.Add("Loaded " + count + (isGroup ? " recent group" : " recent personal") + " messages");
                SetStatus("Đã kết nối · đã tải " + count + " tin " + (isGroup ? "nhóm" : "cá nhân"));
                TaskCompletionSource<int> completion = null;
                lock (_historyLock)
                {
                    _pendingHistoryRequests.TryGetValue(isGroup ? 511 : 510, out completion);
                }
                if (completion != null) completion.TrySetResult(count);
            }
        }

        private void StartPing()
        {
            StopPing();
            int interval = 30000;
            try
            {
                if (_session.Settings != null)
                {
                    double value = _session.Settings.GetNamedObject("features").GetNamedObject("socket").GetNamedNumber("ping_interval");
                    if (value >= 5000) interval = (int)value;
                }
            }
            catch (Exception) { }

            _pingTimer = new Timer(state =>
            {
                var ignored = SendFrameAsync(2, 1, "{\"eventId\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}");
            }, null, interval, interval);
        }

        private async Task SendFrameAsync(int cmd, int sub, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] frame = new byte[4 + body.Length];
            frame[0] = 1;
            frame[1] = (byte)(cmd & 0xFF);
            frame[2] = (byte)((cmd >> 8) & 0xFF);
            frame[3] = (byte)sub;
            Array.Copy(body, 0, frame, 4, body.Length);

            await _sendLock.WaitAsync();
            try
            {
                DataWriter writer = _writer;
                if (writer == null) return;
                writer.WriteBytes(frame);
                await writer.StoreAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Send frame failed: " + ex.Message);
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }


}
