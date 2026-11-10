// Stage 2: receive messages live (WebSocket), keep conversations, send text messages.
// Protocol details follow the open-source zca-js library (MIT licence).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Globalization;
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
    // ===================================================================== JSON helper

    public static class ZaloJson
    {
        // Message and user ids are integers longer than 15 digits. Read as numbers they would lose
        // their last digits, so such numbers are turned into strings before parsing.
        public static string QuoteBigNumbers(string json)
        {
            var sb = new StringBuilder(json.Length + 16);
            bool inString = false;
            int i = 0;
            while (i < json.Length)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length)
                    {
                        sb.Append(json[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (c == '"') inString = false;
                    i++;
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                    sb.Append(c);
                    i++;
                    continue;
                }
                if (c == '-' || (c >= '0' && c <= '9'))
                {
                    int j = i;
                    while (j < json.Length && "+-.eE0123456789".IndexOf(json[j]) >= 0) j++;
                    string token = json.Substring(i, j - i);
                    if (IsBigInteger(token)) sb.Append('"').Append(token).Append('"');
                    else sb.Append(token);
                    i = j;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static bool IsBigInteger(string token)
        {
            int start = token.StartsWith("-") ? 1 : 0;
            if (token.Length - start < 16) return false;
            for (int k = start; k < token.Length; k++)
                if (token[k] < '0' || token[k] > '9') return false;
            return true;
        }

        public static JsonObject ParseObject(string text)
        {
            JsonObject result;
            return JsonObject.TryParse(QuoteBigNumbers(text), out result) ? result : null;
        }
    }


    // ===================================================================== AES-GCM (decrypt only)

    /// <summary>
    /// AES-GCM decryption with a 16-byte IV, written out by hand because the phone's crypto library
    /// only accepts 12-byte IVs. The authentication tag is not checked (the data already arrived over TLS).
    /// </summary>
    public static class ZaloGcm
    {
        public static byte[] Decrypt(byte[] key, byte[] iv, byte[] cipher)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.Key = key;
                using (ICryptoTransform enc = aes.CreateEncryptor())
                {
                    byte[] h = EncryptBlock(enc, new byte[16]);
                    byte[] counter;
                    if (iv.Length == 12)
                    {
                        counter = new byte[16];
                        Array.Copy(iv, counter, 12);
                        counter[15] = 1;
                    }
                    else
                    {
                        counter = GHashIv(h, iv);
                    }

                    byte[] output = new byte[cipher.Length];
                    for (int offset = 0; offset < cipher.Length; offset += 16)
                    {
                        Increment(counter);
                        byte[] keyStream = EncryptBlock(enc, counter);
                        int n = Math.Min(16, cipher.Length - offset);
                        for (int k = 0; k < n; k++) output[offset + k] = (byte)(cipher[offset + k] ^ keyStream[k]);
                    }
                    return output;
                }
            }
        }

        private static byte[] EncryptBlock(ICryptoTransform enc, byte[] input)
        {
            byte[] output = new byte[16];
            enc.TransformBlock(input, 0, 16, output, 0);
            return output;
        }

        private static void Increment(byte[] counter)
        {
            for (int k = 15; k >= 12; k--)
            {
                counter[k]++;
                if (counter[k] != 0) break;
            }
        }

        private static byte[] GHashIv(byte[] h, byte[] iv)
        {
            int padded = (iv.Length + 15) / 16 * 16;
            byte[] x = new byte[padded + 16];
            Array.Copy(iv, x, iv.Length);
            ulong bits = (ulong)iv.Length * 8UL;
            for (int k = 0; k < 8; k++) x[x.Length - 1 - k] = (byte)(bits >> (8 * k));

            byte[] y = new byte[16];
            for (int offset = 0; offset < x.Length; offset += 16)
            {
                for (int k = 0; k < 16; k++) y[k] ^= x[offset + k];
                y = Multiply(y, h);
            }
            return y;
        }

        // multiplication in GF(2^128) as defined for GCM
        private static byte[] Multiply(byte[] x, byte[] y)
        {
            byte[] z = new byte[16];
            byte[] v = (byte[])y.Clone();
            for (int i = 0; i < 128; i++)
            {
                if (((x[i / 8] >> (7 - (i % 8))) & 1) == 1)
                    for (int k = 0; k < 16; k++) z[k] ^= v[k];

                bool lsb = (v[15] & 1) == 1;
                for (int k = 15; k > 0; k--) v[k] = (byte)((v[k] >> 1) | (v[k - 1] << 7));
                v[0] = (byte)(v[0] >> 1);
                if (lsb) v[0] ^= 0xE1;
            }
            return z;
        }
    }


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
            string text, ZaloChatMessage quote, CancellationToken ct)
        {
            if (s == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            long clientId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
            return new ZaloSentText
            {
                MessageId = data.ValueType == JsonValueType.Object ? ZaloAuth.Str(data.GetObject(), "msgId") : string.Empty,
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


    // ===================================================================== app state

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



    public static class ZaloEmoticonMap
    {
        private static readonly Dictionary<string, string> CodeToUri = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static ZaloEmoticonMap()
        {
            try
            {
                RegisterCode(":)", "emo_00");
                RegisterCode(":-)", "emo_00");
                RegisterCode(":~", "emo_01");
                RegisterCode(":B", "emo_02");
                RegisterCode(":')", "emo_03");
                RegisterCode("8-)", "emo_04");
                RegisterCode("8)", "emo_04");
                RegisterCode(":-((", "emo_05");
                RegisterCode(":$", "emo_06");
                RegisterCode(":3", "emo_07");
                RegisterCode(":Z", "emo_08");
                RegisterCode(":((", "emo_09");
                RegisterCode("&-(", "emo_10");
                RegisterCode(":-H", "emo_11");
                RegisterCode(":P", "emo_12");
                RegisterCode(":-P", "emo_12");
                RegisterCode(":D", "emo_13");
                RegisterCode(":-D", "emo_13");
                RegisterCode(":O", "emo_14");
                RegisterCode(":-O", "emo_14");
                RegisterCode(":(", "emo_15");
                RegisterCode(":-(", "emo_15");
                RegisterCode(";)", "emo_16");
                RegisterCode(";-)", "emo_16");
                RegisterCode(";-))", "emo_16");
                RegisterCode("--B", "emo_17");
                RegisterCode(":))", "emo_18");
                RegisterCode(":)))", "emo_18");
                RegisterCode(":-))", "emo_18");
                RegisterCode(":*", "emo_19");
                RegisterCode(":-*", "emo_19");
                RegisterCode(";P", "emo_20");
                RegisterCode(";-P", "emo_20");
                RegisterCode(";-D", "emo_21");
                RegisterCode("/-showlove", "emo_22");
                RegisterCode("<3", "emo_22");
                RegisterCode(";D", "emo_23");
                RegisterCode(";O", "emo_24");
                RegisterCode(";G", "emo_25");
                RegisterCode("|-)", "emo_26");
                RegisterCode(":!", "emo_27");
                RegisterCode(":L", "emo_28");
                RegisterCode(":>", "emo_29");
                RegisterCode(":;", "emo_30");
                RegisterCode(";F", "emo_31");
                RegisterCode(":V", "emo_32");
                RegisterCode(":WIPE", "emo_33");
                RegisterCode(":-DIG", "emo_34");
                RegisterCode(":handclap", "emo_35");
                RegisterCode("B-)", "emo_36");
                RegisterCode(":-R", "emo_37");
                RegisterCode(":-<", "emo_38");
                RegisterCode(":-0", "emo_39");
                RegisterCode(":0", "emo_39");
                RegisterCode(";-S", "emo_40");
                RegisterCode(";?", "emo_41");
                RegisterCode(";-X", "emo_42");
                RegisterCode(":-F", "emo_43");
                RegisterCode("8*)", "emo_44");
                RegisterCode(";!", "emo_45");
                RegisterCode(";-!", "emo_46");
                RegisterCode(";XX", "emo_47");
                RegisterCode(":-BYE", "emo_48");
                RegisterCode(">-|", "emo_49");
                RegisterCode("P-(", "emo_50");
                RegisterCode(":--|", "emo_51");
                RegisterCode(":Q", "emo_52");
                RegisterCode("X-)", "emo_53");
                RegisterCode(";-A", "emo_55");
                RegisterCode("8*", "emo_56");
                RegisterCode(":|", "emo_57");
                RegisterCode(":-|", "emo_57");
                RegisterCode(":X", "emo_58");
                RegisterCode(":T", "emo_59");
                RegisterCode(";-/", "emo_60");
                RegisterCode(":-L", "emo_61");
                RegisterCode("$-)", "emo_62");
                RegisterCode("/-beer", "emo_63");
                RegisterCode("/-coffee", "emo_64");
                RegisterCode("/-rose", "emo_65");
                RegisterCode("/-fade", "emo_66");
                RegisterCode("/-bd", "emo_67");
                RegisterCode("/-bome", "emo_68");
                RegisterCode("/-cake", "emo_69");
                RegisterCode("/-heart", "emo_70");
                RegisterCode("/-break", "emo_71");
                RegisterCode("/-shit", "emo_72");
                RegisterCode("/-li", "emo_73");
                RegisterCode("/-flag", "emo_74");
                RegisterCode("/-strong", "emo_75");
                RegisterCode("(y)", "emo_75");
                RegisterCode("/-weak", "emo_76");
                RegisterCode("(n)", "emo_76");
                RegisterCode("/-ok", "emo_77");
                RegisterCode("(ok)", "emo_77");
                RegisterCode("/-v", "emo_78");
                RegisterCode("/-thanks", "emo_79");
                RegisterCode("/-punch", "emo_80");
                RegisterCode("/-share", "emo_81");
                RegisterCode("_()_", "emo_82");
                RegisterCode("/-no", "emo_83");
                RegisterCode("/-bad", "emo_84");
                RegisterCode("/-loveu", "emo_85");
                RegisterCode(":+", "emo_87");
                RegisterCode(";8", "emo_88");
                RegisterCode("/-jj", "emo_89");

                for (int i = 0; i < 90; i++)
                {
                    string key = "emo_" + i.ToString("D2");
                    RegisterCode(key, key);
                }
            }
            catch (Exception ex)
            {
                ZaloLog.Add("ZaloEmoticonMap init error: " + ex.Message);
            }
        }

        private static void RegisterCode(string code, string emoFileName)
        {
            CodeToUri[code] = "ms-appx:///Assets/Emoticons/Emoji/" + emoFileName + ".png";
        }

        public static string GetEmoticonUri(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                string trimmed = text.Trim();
                string uri;
                if (CodeToUri.TryGetValue(trimmed, out uri)) return uri;
            }
            catch { }
            return null;
        }
    }

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


    public sealed class ZaloFriend
    {
        private Brush _color;
        private Brush _image;
        private bool _imageMade;

        // These must be properties (not fields) so the lists on screen can show them.
        public string UserId { get; set; }
        public string PhoneNumber { get; set; }
        public string Name { get; set; }
        public string OriginalName { get; set; }
        public string Alias { get; set; }
        public string Avatar { get; set; }
        public string Subtitle { get; set; }
        public bool IsOnline { get; set; }
        public Visibility OnlineDotVisibility { get { return IsOnline ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility SubtitleVisibility { get { return string.IsNullOrWhiteSpace(Subtitle) ? Visibility.Collapsed : Visibility.Visible; } }
        public string SectionHeader { get; set; }
        public bool ShowSectionHeader { get; set; }
        public Visibility SectionHeaderVisibility { get { return ShowSectionHeader ? Visibility.Visible : Visibility.Collapsed; } }

        public string Initial { get { return ZaloAvatar.InitialOf(Name); } }

        public Brush AvatarColor
        {
            get
            {
                if (_color == null) _color = ZaloAvatar.ColorFor(Name);
                return _color;
            }
        }

        public Brush AvatarImageBrush
        {
            get
            {
                if (!_imageMade)
                {
                    _image = ZaloAvatar.ImageFor(Avatar);
                    _imageMade = true;
                }
                return _image;
            }
        }
    }


    public sealed class ZaloFriendRequest
    {
        public string UserId { get; set; }
        public string PhoneNumber { get; set; }
        public string Name { get; set; }
        public string Avatar { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
    }

    public abstract class ZaloObservable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }


    public sealed class ZaloGroupBoardEntry
    {
        public string Icon { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string TimeText { get; set; }
        public long PollId { get; set; }
        public string TopicId { get; set; }
        public Visibility ContentActionVisibility
        {
            get { return string.IsNullOrWhiteSpace(Title) || Title.Length <= 180 ? Visibility.Collapsed : Visibility.Visible; }
        }
        public Visibility PollActionVisibility { get { return PollId > 0 ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility NoteActionVisibility { get { return !string.IsNullOrWhiteSpace(TopicId) ? Visibility.Visible : Visibility.Collapsed; } }
    }

    public sealed class ZaloGroupReminder
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string TimeText { get; set; }
        public long StartTime { get; set; }
        public int RepeatMode { get; set; }
        public string RepeatText { get; set; }
        public string ResponseText { get; set; }
        public bool CanManage { get; set; }
        public Visibility ManageVisibility { get { return CanManage ? Visibility.Visible : Visibility.Collapsed; } }
    }

    public sealed class ZaloGroupLinkInfo
    {
        public bool Enabled { get; set; }
        public string Link { get; set; }
        public long Expiration { get; set; }
    }

    public sealed class ZaloPollDetail
    {
        public long PollId { get; set; }
        public string Question { get; set; }
        public string Creator { get; set; }
        public bool Joined { get; set; }
        public bool Closed { get; set; }
        public bool AllowMultiple { get; set; }
        public int VoteCount { get; set; }
        public List<ZaloPollOption> Options { get; private set; } = new List<ZaloPollOption>();
    }

    public sealed class ZaloPollOption
    {
        public long OptionId { get; set; }
        public string Content { get; set; }
        public int Votes { get; set; }
        public bool Voted { get; set; }
    }

    public sealed class ZaloChatMessage : ZaloObservable
    {
        private static readonly Brush IncomingBubbleBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        private static readonly Brush OutgoingBubbleBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xD8, 0xF1, 0xFF));
        private static readonly Brush ReceiptSeenBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x16, 0x88, 0xF8));
        private static readonly Brush ReceiptPendingBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x82, 0x90, 0x9E));
        private static readonly Brush MediaBubbleBrush = new SolidColorBrush(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
        private static readonly Brush MessageBorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xD8, 0xE0, 0xE8));
        private string _text;
        private string _reactionIcon;
        private string _mediaUrl;
        private string _mediaFullUrl;
        private Windows.UI.Xaml.Media.Imaging.BitmapImage _mediaImage;
        private bool _mediaLoadFailed;
        private bool _playbackFailed;
        private bool _playbackFailedVideo;
        private bool _isRecalled;
        private Brush _senderAvatarBrush;
        private Brush _senderAvatarImageBrush;
        private string _senderAvatarUrl;
        private int _receiptState;
        private string _deliveryAvatarUrl;
        private Brush _deliveryAvatarBrush;
        public string Id { get; set; }
        public string GlobalMessageId { get; set; }
        public string ClientMessageId { get; set; }
        public string SenderUid { get; set; }
        public string RecipientUid { get; set; }
        public string Sender { get; set; }
        public int Status { get; set; }
        public int At { get; set; }
        public int Command { get; set; }
        public bool SeenSent { get; set; }
        public int ReceiptState
        {
            get { return _receiptState; }
            set
            {
                int normalized = Math.Max(0, Math.Min(2, value));
                if (_receiptState == normalized) return;
                _receiptState = normalized;
                Raise("ReceiptState");
                Raise("DeliveryText");
                Raise("DeliveryForeground");
                Raise("DeliveryAvatarVisibility");
                Raise("DeliveryVisibility");
            }
        }
        public string DeliveryAvatarUrl
        {
            get { return _deliveryAvatarUrl ?? string.Empty; }
            set
            {
                if (_deliveryAvatarUrl == value) return;
                _deliveryAvatarUrl = value;
                _deliveryAvatarBrush = null;
                Raise("DeliveryAvatarUrl");
                Raise("DeliveryAvatarBrush");
                Raise("DeliveryAvatarVisibility");
            }
        }
        public Brush DeliveryAvatarBrush
        {
            get
            {
                if (_deliveryAvatarBrush == null && !string.IsNullOrWhiteSpace(DeliveryAvatarUrl))
                    _deliveryAvatarBrush = ZaloAvatar.ImageFor(DeliveryAvatarUrl);
                return _deliveryAvatarBrush;
            }
        }
        public string ReplyContent { get; set; }
        public string QuoteMessage { get; set; }
        public string QuoteAttachment { get; set; }
        public bool ReplyBlocked { get; set; }
        public string QuotedSender { get; set; }
        public string QuotedText { get; set; }
        private string _linkPreviewTitle;
        private string _linkPreviewDescription;
        private string _linkPreviewUrl;
        private string _linkPreviewImageUrl;
        private Windows.UI.Xaml.Media.Imaging.BitmapImage _linkPreviewImage;
        private double? _locationLatitude;
        private double? _locationLongitude;
        private string _locationLabel;
        private long _pollId;
        private string _pollQuestion;
        private string _contactUid;
        private string _contactName;
        private string _contactAvatarUrl;
        private string _contactQrUrl;
        private string _contactPhone;
        private Windows.UI.Xaml.Media.Imaging.BitmapImage _contactAvatarImage;
        private Brush _contactAvatarBrush;
        private string _phonePreviewNumber;
        private string _phonePreviewUid;
        private string _phonePreviewName;
        private string _phonePreviewAvatarUrl;
        private Windows.UI.Xaml.Media.Imaging.BitmapImage _phonePreviewAvatar;
        private bool _phonePreviewFriendRequestSent;
        private bool _phonePreviewAlreadyFriend;
        private static readonly Regex VietnamesePhoneRegex = new Regex(@"(?<!\d)(?:\+?84|0)(?:[ .-]*\d){8,10}(?!\d)", RegexOptions.Compiled);
        public string LinkPreviewTitle { get { return _linkPreviewTitle ?? string.Empty; } }
        public string LinkPreviewDescription { get { return _linkPreviewDescription ?? string.Empty; } }
        public string LinkPreviewUrl { get { return _linkPreviewUrl ?? string.Empty; } }
        public string LinkPreviewImageUrl { get { return _linkPreviewImageUrl ?? string.Empty; } }
        public Uri LinkPreviewUri
        {
            get
            {
                Uri uri;
                return Uri.TryCreate(LinkPreviewUrl, UriKind.Absolute, out uri)
                    && (uri.Scheme == "http" || uri.Scheme == "https") ? uri : null;
            }
        }
        public Windows.UI.Xaml.Media.Imaging.BitmapImage LinkPreviewImage
        {
            get
            {
                if (_linkPreviewImage == null && !string.IsNullOrEmpty(_linkPreviewImageUrl))
                {
                    try
                    {
                        Uri imageUri;
                        if (Uri.TryCreate(_linkPreviewImageUrl, UriKind.Absolute, out imageUri)
                            && (imageUri.Scheme == "http" || imageUri.Scheme == "https"))
                        {
                            _linkPreviewImage = new Windows.UI.Xaml.Media.Imaging.BitmapImage(imageUri);
                        }
                    }
                    catch { }
                }
                return _linkPreviewImage;
            }
        }
        public bool HasLinkPreview { get { return LinkPreviewTitle.Length > 0 || LinkPreviewUrl.Length > 0; } }
        public bool HasPhotoAttachment { get { return MsgType == "chat.photo" || MsgType == "chat.gif"
            || MsgType == "chat.doodle" || MsgType == "chat.sticker" || MsgType == "5"; } }
        private bool IsDuplicatePhotoLink
        {
            get
            {
                if (!HasPhotoAttachment && !HasMedia) return false;
                if (string.Equals(LinkPreviewUrl, MediaUrl, StringComparison.OrdinalIgnoreCase)) return true;
                Uri uri;
                if (!Uri.TryCreate(LinkPreviewUrl, UriKind.Absolute, out uri)) return false;
                string host = uri.Host ?? string.Empty;
                return host.EndsWith(".zdn.vn", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".zaloapp.com", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".dlfl.vn", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".zadn.vn", StringComparison.OrdinalIgnoreCase);
            }
        }
        public Visibility LinkPreviewVisibility { get { return HasLinkPreview && !HasVoice && !HasVideo && !HasFile && !IsDuplicatePhotoLink && !IsSticker && !HasMedia && !HasPhotoAttachment
            ? Visibility.Visible : Visibility.Collapsed; } }
        public bool HasLocation { get { return _locationLatitude.HasValue && _locationLongitude.HasValue; } }
        public double? LocationLatitude { get { return _locationLatitude; } }
        public double? LocationLongitude { get { return _locationLongitude; } }
        public string LocationLabel { get { return string.IsNullOrWhiteSpace(_locationLabel) ? "Vị trí được chia sẻ" : _locationLabel; } }
        public string LocationCoordinates
        {
            get { return !HasLocation ? string.Empty : _locationLatitude.Value.ToString("0.######", CultureInfo.InvariantCulture)
                + ", " + _locationLongitude.Value.ToString("0.######", CultureInfo.InvariantCulture); }
        }
        public Uri LocationUri
        {
            get
            {
                if (!HasLocation) return null;
                return new Uri("https://www.google.com/maps/search/?api=1&query="
                    + Uri.EscapeDataString(LocationCoordinates.Replace(" ", string.Empty)));
            }
        }
        public Visibility LocationVisibility { get { return HasLocation ? Visibility.Visible : Visibility.Collapsed; } }
        public long PollId { get { return _pollId; } }
        public string PollQuestion { get { return string.IsNullOrWhiteSpace(_pollQuestion) ? "Bình chọn trong nhóm" : _pollQuestion; } }
        public Visibility PollVisibility { get { return PollId > 0 ? Visibility.Visible : Visibility.Collapsed; } }
        public string ContactUid { get { return _contactUid ?? string.Empty; } }
        public string ContactName { get { return _contactName ?? string.Empty; } }
        public string ContactPhone { get { return _contactPhone ?? string.Empty; } }
        public string ContactAvatarUrl { get { return _contactAvatarUrl ?? string.Empty; } }
        public string ContactQrUrl { get { return _contactQrUrl ?? string.Empty; } }
        public Windows.UI.Xaml.Media.Imaging.BitmapImage ContactAvatarImage
        {
            get
            {
                if (_contactAvatarImage == null && !string.IsNullOrEmpty(_contactAvatarUrl))
                {
                    try
                    {
                        Uri avatarUri;
                        if (Uri.TryCreate(_contactAvatarUrl, UriKind.Absolute, out avatarUri)
                            && (avatarUri.Scheme == "http" || avatarUri.Scheme == "https"))
                        {
                            _contactAvatarImage = new Windows.UI.Xaml.Media.Imaging.BitmapImage(avatarUri);
                        }
                    }
                    catch { }
                }
                return _contactAvatarImage;
            }
        }
        public Brush ContactAvatarBrush
        {
            get
            {
                if (_contactAvatarBrush == null)
                {
                    try
                    {
                        if (ContactAvatarImage != null)
                        {
                            _contactAvatarBrush = new ImageBrush { ImageSource = ContactAvatarImage, Stretch = Stretch.UniformToFill };
                        }
                        else
                        {
                            _contactAvatarBrush = ZaloAvatar.ColorFor(_contactName);
                        }
                    }
                    catch
                    {
                        _contactAvatarBrush = ZaloAvatar.ColorFor(_contactName);
                    }
                }
                return _contactAvatarBrush;
            }
        }
        public bool HasContactCard { get { return !string.IsNullOrWhiteSpace(ContactUid); } }
        public Visibility ContactCardVisibility { get { return HasContactCard ? Visibility.Visible : Visibility.Collapsed; } }
        public string DetectedPhoneNumber
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Text) || HasContactCard || HasLinkPreview) return string.Empty;
                Match match = VietnamesePhoneRegex.Match(Text);
                return match.Success ? Regex.Replace(match.Value, @"[^0-9+]", string.Empty) : string.Empty;
            }
        }
        public bool HasPhonePreview { get { return !string.IsNullOrWhiteSpace(_phonePreviewUid); } }
        public Visibility PhonePreviewVisibility { get { return HasPhonePreview ? Visibility.Visible : Visibility.Collapsed; } }
        public string PhonePreviewNumber { get { return _phonePreviewNumber ?? DetectedPhoneNumber; } }
        public string PhonePreviewUid { get { return _phonePreviewUid ?? string.Empty; } }
        public string PhonePreviewName { get { return _phonePreviewName ?? string.Empty; } }
        public string PhonePreviewAvatarUrl { get { return _phonePreviewAvatarUrl ?? string.Empty; } }
        public Windows.UI.Xaml.Media.Imaging.BitmapImage PhonePreviewAvatar
        {
            get
            {
                if (_phonePreviewAvatar == null && !string.IsNullOrEmpty(_phonePreviewAvatarUrl))
                {
                    try
                    {
                        Uri avatarUri;
                        if (Uri.TryCreate(_phonePreviewAvatarUrl, UriKind.Absolute, out avatarUri)
                            && (avatarUri.Scheme == "http" || avatarUri.Scheme == "https"))
                        {
                            _phonePreviewAvatar = new Windows.UI.Xaml.Media.Imaging.BitmapImage(avatarUri);
                        }
                    }
                    catch { }
                }
                return _phonePreviewAvatar;
            }
        }
        public string PhonePreviewFriendAction
        {
            get { return _phonePreviewAlreadyFriend ? "Đã là bạn" : _phonePreviewFriendRequestSent ? "Đã gửi lời mời" : "Kết bạn"; }
        }
        public void SetPhonePreview(ZaloFriend friend, string phone)
        {
            if (friend == null || string.IsNullOrWhiteSpace(friend.UserId)) return;
            _phonePreviewUid = friend.UserId;
            _phonePreviewName = string.IsNullOrWhiteSpace(friend.Name) ? friend.UserId : friend.Name;
            _phonePreviewAvatarUrl = friend.Avatar ?? string.Empty;
            _phonePreviewNumber = phone ?? DetectedPhoneNumber;
            _phonePreviewAvatar = null;
            Raise("PhonePreviewVisibility");
            Raise("PhonePreviewNumber");
            Raise("PhonePreviewUid");
            Raise("PhonePreviewName");
            Raise("PhonePreviewAvatarUrl");
            Raise("PhonePreviewAvatar");
            Raise("PhonePreviewFriendAction");
        }
        public void MarkPhoneFriendRequestSent()
        {
            _phonePreviewFriendRequestSent = true;
            Raise("PhonePreviewFriendAction");
        }
        public void MarkPhonePreviewAlreadyFriend()
        {
            _phonePreviewAlreadyFriend = true;
            Raise("PhonePreviewFriendAction");
        }
        public void SetContactCard(string uid, string name, string avatarUrl, string qrUrl, string phone)
        {
            _contactUid = uid;
            _contactName = name;
            _contactAvatarUrl = avatarUrl;
            _contactQrUrl = qrUrl;
            _contactPhone = phone;
            _contactAvatarImage = null;
            _contactAvatarBrush = null;
            Raise("ContactUid");
            Raise("ContactName");
            Raise("ContactPhone");
            Raise("ContactAvatarImage");
            Raise("ContactAvatarBrush");
            Raise("HasContactCard");
            Raise("ContactCardVisibility");
            Text = "[Danh thiếp] " + (_contactName ?? _contactUid);
            Raise("HasText");
        }
        public void SetPoll(long pollId, string question)
        {
            if (pollId <= 0) return;
            _pollId = pollId;
            _pollQuestion = question;
            Raise("PollId");
            Raise("PollQuestion");
            Raise("PollVisibility");
            Raise("HasText");
        }
        public void SetLocation(double latitude, double longitude, string label)
        {
            if (latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180) return;
            CoreDispatcher dispatcher = Windows.ApplicationModel.Core.CoreApplication.MainView?.CoreWindow?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasThreadAccess)
            {
                var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                    () => SetLocationCore(latitude, longitude, label));
                return;
            }
            SetLocationCore(latitude, longitude, label);
        }
        private void SetLocationCore(double latitude, double longitude, string label)
        {
            _locationLatitude = latitude;
            _locationLongitude = longitude;
            _locationLabel = label;
            Raise("HasLocation");
            Raise("LocationLabel");
            Raise("LocationCoordinates");
            Raise("LocationUri");
            Raise("LocationVisibility");
            Raise("HasText");
        }
        public async Task OpenLocationAsync()
        {
            Uri target = LocationUri;
            if (target != null) await Windows.System.Launcher.LaunchUriAsync(target);
        }
        public void SetLinkPreview(string title, string description, string href, string imageUrl)
        {
            CoreDispatcher dispatcher = Windows.ApplicationModel.Core.CoreApplication.MainView?.CoreWindow?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasThreadAccess)
            {
                var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                    () => SetLinkPreviewCore(title, description, href, imageUrl));
                return;
            }
            SetLinkPreviewCore(title, description, href, imageUrl);
        }

        private void SetLinkPreviewCore(string title, string description, string href, string imageUrl)
        {
            _linkPreviewTitle = title;
            _linkPreviewDescription = description;
            _linkPreviewUrl = href;
            _linkPreviewImageUrl = imageUrl;
            _linkPreviewImage = null;
            Raise("LinkPreviewTitle");
            Raise("LinkPreviewDescription");
            Raise("LinkPreviewUrl");
            Raise("LinkPreviewUri");
            Raise("LinkPreviewImage");
            Raise("HasLinkPreview");
            Raise("LinkPreviewVisibility");
        }
        public bool CanReply { get { return !ReplyBlocked && !IsRecalled && !string.IsNullOrEmpty(GlobalMessageId)
            && !string.IsNullOrEmpty(ClientMessageId) && !string.IsNullOrEmpty(SenderUid)
            && (!string.IsNullOrEmpty(ReplyContent) || !string.IsNullOrEmpty(QuoteMessage)
                || !string.IsNullOrEmpty(QuoteAttachment)); } }
        public bool CanSendSeen { get { return !string.IsNullOrEmpty(GlobalMessageId) && !string.IsNullOrEmpty(ClientMessageId)
            && !string.IsNullOrEmpty(SenderUid) && !string.IsNullOrEmpty(RecipientUid); } }
        public bool CanReact
        {
            get
            {
                long globalId, clientId;
                return !IsRecalled && long.TryParse(GlobalMessageId, out globalId) && long.TryParse(ClientMessageId, out clientId);
            }
        }
        public bool IsRecalled
        {
            get { return _isRecalled; }
            set
            {
                if (_isRecalled == value) return;
                _isRecalled = value;
                Raise("IsRecalled");
                Raise("CanRecall");
                Raise("CanReply");
                Raise("DeliveryText");
                Raise("DeliveryVisibility");
                Raise("DeliveryAvatarVisibility");
            }
        }
        public bool IsRecallEvent { get; set; }
        public bool CanRecall
        {
            get
            {
                long globalId, clientId;
                return FromMe && !IsRecalled && long.TryParse(GlobalMessageId, out globalId)
                    && long.TryParse(ClientMessageId, out clientId);
            }
        }
        public bool CanDeleteForMe
        {
            get
            {
                long globalId, clientId;
                return long.TryParse(GlobalMessageId, out globalId) && long.TryParse(ClientMessageId, out clientId)
                    && !string.IsNullOrWhiteSpace(SenderUid);
            }
        }
        public string ReactionIcon
        {
            get { return _reactionIcon ?? string.Empty; }
            set
            {
                if (_reactionIcon == value) return;
                _reactionIcon = value;
                Raise("ReactionIcon");
                Raise("ReactionDisplayIcon");
                Raise("ReactionVisibility");
            }
        }
        public string ReactionDisplayIcon
        {
            get
            {
                switch (ReactionIcon)
                {
                    case "/-strong": return "\U0001F44D";
                    case "/-heart": return "\u2764\uFE0F";
                    case ":>": return "\U0001F602";
                    case ":o": return "\U0001F62E";
                    case ":-((":
                    case ":-(": return "\U0001F62D";
                    case ":-h": return "\U0001F621";
                    default: return string.IsNullOrEmpty(ReactionIcon) || ReactionIcon.StartsWith("/", StringComparison.Ordinal)
                        ? string.Empty : ReactionIcon;
                }
            }
        }
        public Visibility ReactionVisibility { get { return string.IsNullOrEmpty(ReactionDisplayIcon) ? Visibility.Collapsed : Visibility.Visible; } }
        public double MediaMaxWidth { get { return IsSticker || MsgType == "chat.gif" ? 200 : 260; } }
        public double MediaMaxHeight { get { return IsSticker || MsgType == "chat.gif" ? 230 : 390; } }
        public Visibility QuotedVisibility { get { return string.IsNullOrEmpty(QuotedText) ? Visibility.Collapsed : Visibility.Visible; } }
        public Brush SenderAvatarBrush
        {
            get
            {
                if (_senderAvatarBrush == null) _senderAvatarBrush = ZaloAvatar.ColorFor(Sender);
                return _senderAvatarBrush;
            }
            set { _senderAvatarBrush = value; }
        }
        public string SenderInitial { get { return ZaloAvatar.InitialOf(Sender); } }
        public Brush SenderAvatarImageBrush
        {
            get
            {
                if (_senderAvatarImageBrush == null && !string.IsNullOrEmpty(_senderAvatarUrl))
                {
                    try
                    {
                        _senderAvatarImageBrush = ZaloAvatar.ImageFor(_senderAvatarUrl);
                    }
                    catch { }
                }
                return _senderAvatarImageBrush;
            }
        }
        public string SenderAvatarUrl
        {
            get { return _senderAvatarUrl ?? string.Empty; }
            set
            {
                if (string.Equals(_senderAvatarUrl, value, StringComparison.Ordinal)) return;
                _senderAvatarUrl = value;
                _senderAvatarImageBrush = null;
                Raise("SenderAvatarUrl");
                Raise("SenderAvatarImageBrush");
            }
        }
        private string _emoticonUriString;
        private Windows.UI.Xaml.Media.Imaging.BitmapImage _emoticonImage;
        public Windows.UI.Xaml.Media.Imaging.BitmapImage EmoticonImage
        {
            get
            {
                if (_emoticonImage == null && !string.IsNullOrEmpty(_emoticonUriString))
                {
                    try
                    {
                        _emoticonImage = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(_emoticonUriString));
                    }
                    catch { }
                }
                return _emoticonImage;
            }
        }
        public bool HasEmoticon { get { return !string.IsNullOrEmpty(_emoticonUriString); } }
        public Visibility EmoticonVisibility { get { return HasEmoticon ? Visibility.Visible : Visibility.Collapsed; } }

        private void UpdateEmoticon()
        {
            _emoticonUriString = ZaloEmoticonMap.GetEmoticonUri(_text);
            _emoticonImage = null;
            Raise("EmoticonImage");
            Raise("HasEmoticon");
            Raise("EmoticonVisibility");
            Raise("HasText");
            Raise("Bubble");
            Raise("BubblePadding");
            Raise("BubbleBorderBrush");
            Raise("BubbleBorderThickness");
        }

        public string Text
        {
            get { return _text; }
            set
            {
                if (_text == value) return;
                _text = value;
                UpdateEmoticon();
                Raise("Text");
                Raise("HasText");
                Raise("DetectedPhoneNumber");
                Raise("Bubble");
                Raise("BubblePadding");
                Raise("BubbleBorderBrush");
                Raise("BubbleBorderThickness");
            }
        }
        public string MsgType { get; set; }
        public string StickerId { get; set; }
        public string FileName { get; set; }
        public string FileDisplayName { get { return string.IsNullOrWhiteSpace(FileName) ? "Tệp đính kèm" : FileName; } }
        public string MediaUrl
        {
            get { return _mediaUrl; }
            set
            {
                if (_mediaUrl == value) return;
                _mediaUrl = value;
                _mediaImage = null;
                _mediaLoadFailed = false;
                _playbackFailed = false;
                Raise("MediaUrl");
                Raise("MediaImage");
                Raise("HasHdPhoto");
                Raise("MediaVisibility");
                Raise("MediaRetryVisibility");
                Raise("MediaUri");
                Raise("HasVoice");
                Raise("HasMedia");
                Raise("LinkPreviewVisibility");
                Raise("HasVideo");
                Raise("VideoPlayerVisibility");
                Raise("VoicePlayerVisibility");
                Raise("PlaybackRetryVisibility");
                Raise("HasFile");
                Raise("HasText");
                Raise("Bubble");
                Raise("BubblePadding");
                Raise("BubbleBorderBrush");
                Raise("BubbleBorderThickness");
            }
        }
        public string MediaFullUrl
        {
            get { return string.IsNullOrWhiteSpace(_mediaFullUrl) ? _mediaUrl : _mediaFullUrl; }
            set
            {
                if (_mediaFullUrl == value) return;
                _mediaFullUrl = value;
                Raise("MediaFullUrl");
                Raise("HasHdPhoto");
            }
        }
        public bool HasHdPhoto
        {
            get { return MsgType == "chat.photo" && !string.IsNullOrWhiteSpace(_mediaFullUrl)
                && !string.Equals(_mediaFullUrl, _mediaUrl, StringComparison.OrdinalIgnoreCase); }
        }
        public string ViewerUrl { get { return string.IsNullOrWhiteSpace(MediaFullUrl) ? MediaUrl : MediaFullUrl; } }
        public Windows.UI.Xaml.Media.Imaging.BitmapImage MediaImage
        {
            get
            {
                if (_mediaImage == null && !string.IsNullOrWhiteSpace(_mediaUrl))
                {
                    Uri uri;
                    bool imageMessage = MsgType == "chat.photo" || MsgType == "chat.gif" || MsgType == "chat.doodle"
                        || MsgType == "chat.sticker" || MsgType == "5";
                    if (imageMessage && Uri.TryCreate(_mediaUrl, UriKind.Absolute, out uri)
                        && (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "ms-appx"))
                    {
                        _mediaImage = CreateChatImage(uri);
                    }
                }
                return _mediaImage;
            }
        }
        public Visibility MediaVisibility { get { return HasMedia && !_mediaLoadFailed ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility MediaRetryVisibility { get { return _mediaLoadFailed ? Visibility.Visible : Visibility.Collapsed; } }
        public string MediaRetryText
        {
            get
            {
                if (IsSticker) return "Không tải được nhãn dán · Thử lại";
                return MsgType == "chat.doodle" ? "Không tải được bản vẽ · Thử lại" : "Không tải được ảnh · Thử lại";
            }
        }
        public Uri MediaUri
        {
            get
            {
                Uri uri;
                return Uri.TryCreate(MediaUrl, UriKind.Absolute, out uri)
                    && (uri.Scheme == "http" || uri.Scheme == "https" || (MsgType == "chat.gif" && uri.Scheme == "ms-appx"))
                    ? uri : null;
            }
        }
        private static Windows.UI.Xaml.Media.Imaging.BitmapImage CreateChatImage(Uri uri)
        {
            if (uri == null) return null;
            try
            {
                var bitmap = new Windows.UI.Xaml.Media.Imaging.BitmapImage();
                bool isGif = uri.AbsoluteUri.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
                if (!isGif && (uri.Scheme == "http" || uri.Scheme == "https"))
                    bitmap.DecodePixelWidth = 520;
                bitmap.UriSource = uri;
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
        public bool HasVoice { get { return (MsgType == "chat.voice" || MsgType == "31") && MediaUri != null; } }
        public bool HasVideo { get { return (MsgType == "chat.video.msg" || MsgType == "44") && MediaUri != null; } }
        public Visibility VideoPlayerVisibility { get { return HasVideo && !_playbackFailed ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility VoicePlayerVisibility { get { return HasVoice && !_playbackFailed ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility PlaybackRetryVisibility { get { return _playbackFailed ? Visibility.Visible : Visibility.Collapsed; } }
        public string PlaybackFailureText { get { return _playbackFailedVideo ? "Video này chưa phát được trên thiết bị." : "Tin nhắn thoại chưa phát được."; } }
        public void SetPlaybackFailure(bool video)
        {
            _playbackFailed = true;
            _playbackFailedVideo = video;
            Raise("VideoPlayerVisibility");
            Raise("VoicePlayerVisibility");
            Raise("PlaybackRetryVisibility");
            Raise("PlaybackFailureText");
            Raise("HasText");
            Raise("Bubble");
            Raise("BubblePadding");
            Raise("BubbleBorderBrush");
            Raise("BubbleBorderThickness");
        }
        public void ResetPlaybackFailure()
        {
            if (!_playbackFailed) return;
            _playbackFailed = false;
            Raise("VideoPlayerVisibility");
            Raise("VoicePlayerVisibility");
            Raise("PlaybackRetryVisibility");
        }
        public bool HasFile { get { return (MsgType == "share.file" || MsgType == "46") && MediaUri != null; } }
        public bool IsSticker { get { return MsgType == "chat.sticker" || MsgType == "5"; } }
        public bool HasMedia
        {
            get
            {
                bool imageMessage = MsgType == "chat.photo" || MsgType == "chat.gif" || MsgType == "chat.doodle"
                    || MsgType == "chat.sticker" || MsgType == "5";
                return imageMessage && !string.IsNullOrWhiteSpace(_mediaUrl);
            }
        }
        public void MarkMediaLoadFailed()
        {
            _mediaLoadFailed = true;
            Raise("MediaVisibility");
            Raise("MediaRetryVisibility");
            Raise("Bubble");
            Raise("BubblePadding");
            Raise("BubbleBorderBrush");
            Raise("BubbleBorderThickness");
        }
        public void SetDownloadedMediaImage(Windows.UI.Xaml.Media.Imaging.BitmapImage image)
        {
            if (image == null) return;
            _mediaImage = image;
            _mediaLoadFailed = false;
            Raise("MediaImage");
            Raise("MediaVisibility");
            Raise("MediaRetryVisibility");
            Raise("HasMedia");
            Raise("LinkPreviewVisibility");
            Raise("Bubble");
            Raise("BubblePadding");
            Raise("BubbleBorderBrush");
            Raise("BubbleBorderThickness");
        }
        public void RetryMediaLoad()
        {
            if (string.IsNullOrWhiteSpace(_mediaUrl)) return;
            Uri uri;
            bool imageMessage = MsgType == "chat.photo" || MsgType == "chat.gif" || MsgType == "chat.doodle"
                || MsgType == "chat.sticker" || MsgType == "5";
            _mediaImage = imageMessage && Uri.TryCreate(_mediaUrl, UriKind.Absolute, out uri)
                && (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "ms-appx") ? CreateChatImage(uri) : null;
            _mediaLoadFailed = false;
            Raise("MediaImage");
            Raise("MediaVisibility");
            Raise("MediaRetryVisibility");
            Raise("HasMedia");
            Raise("LinkPreviewVisibility");
            Raise("Bubble");
            Raise("BubblePadding");
            Raise("BubbleBorderBrush");
            Raise("BubbleBorderThickness");
        }
        public bool HasText
        {
            get
            {
                bool imagePlaceholder = (MsgType == "chat.photo" || MsgType == "chat.gif" || MsgType == "chat.doodle" || MsgType == "chat.sticker" || MsgType == "5")
                    && (Text == "[\u1EA2nh]" || Text == "[\u1EA2nh \u0111\u1ED9ng]" || Text == "[Nh\u00E3n d\u00E1n]" || Text == "[B\u1EA3n v\u1EBD]");
                bool voicePlaceholder = HasVoice && Text == "[Tin nh\u1EAFn tho\u1EA1i]";
                return !string.IsNullOrWhiteSpace(Text) && !HasVoice && !HasVideo && !HasFile && !HasLocation && PollId <= 0
                    && !HasContactCard && !HasEmoticon && !imagePlaceholder && !voicePlaceholder;
            }
        }
        public bool FromMe { get; set; }
        public bool IsSystemEvent { get { return MsgType == "system.group_event"; } }
        public Visibility RegularMessageVisibility { get { return IsSystemEvent ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility SystemEventVisibility { get { return IsSystemEvent ? Visibility.Visible : Visibility.Collapsed; } }
        public long Ts { get; set; }

        public bool ShowSender { get; set; }
        public Visibility MessageAvatarVisibility { get { return !FromMe && !IsSystemEvent ? Visibility.Visible : Visibility.Collapsed; } }
        public int MessageColumn { get { return FromMe ? 0 : 1; } }
        public int MessageColumnSpan { get { return FromMe ? 2 : 1; } }
        private bool _showDateHeader;
        private string _dateHeaderText = string.Empty;
        public bool ShowDateHeader
        {
            get { return _showDateHeader; }
            set { if (_showDateHeader != value) { _showDateHeader = value; Raise("ShowDateHeader"); Raise("DateHeaderVisibility"); } }
        }
        public string DateHeaderText
        {
            get { return _dateHeaderText; }
            set { if (_dateHeaderText != value) { _dateHeaderText = value ?? string.Empty; Raise("DateHeaderText"); } }
        }
        public Visibility DateHeaderVisibility { get { return ShowDateHeader ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility SenderVisibility { get { return ShowSender ? Visibility.Visible : Visibility.Collapsed; } }
        public string TimeText { get { return ZaloFormat.Time(Ts); } }
        public string DeliveryText
        {
            get
            {
                if (!FromMe || IsRecalled || (string.IsNullOrWhiteSpace(GlobalMessageId)
                    && string.IsNullOrWhiteSpace(ClientMessageId))) return string.Empty;
                return ReceiptState >= 2 ? "✓✓  Đã xem" : ReceiptState == 1 ? "✓✓  Đã nhận" : "✓  Đã gửi";
            }
        }
        public Brush DeliveryForeground { get { return ReceiptState >= 2
            ? ReceiptSeenBrush : ReceiptPendingBrush; } }
        public Visibility DeliveryAvatarVisibility { get { return ReceiptState >= 2 && DeliveryAvatarBrush != null
            ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility DeliveryVisibility
        {
            get { return string.IsNullOrEmpty(DeliveryText) && DeliveryAvatarVisibility == Visibility.Collapsed
                ? Visibility.Collapsed : Visibility.Visible; }
        }

        public void SetDeliveryReceipt(int state, string avatarUrl)
        {
            ReceiptState = Math.Max(ReceiptState, state);
            if (!string.IsNullOrWhiteSpace(avatarUrl)) DeliveryAvatarUrl = avatarUrl;
        }

        public HorizontalAlignment Align
        {
            get { return IsSystemEvent ? HorizontalAlignment.Center : FromMe ? HorizontalAlignment.Right : HorizontalAlignment.Left; }
        }

        public Brush Bubble
        {
            get
            {
                if (HasEmoticon) return MediaBubbleBrush;
                if (HasMedia && !HasText && !_mediaLoadFailed) return MediaBubbleBrush;
                return FromMe ? OutgoingBubbleBrush : IncomingBubbleBrush;
            }
        }
        public Thickness BubblePadding
        {
            get
            {
                if (HasEmoticon) return new Thickness(2);
                return HasMedia && !HasText && !_mediaLoadFailed ? new Thickness(0) : new Thickness(11, 8, 11, 8);
            }
        }
        public Brush BubbleBorderBrush
        {
            get
            {
                if (HasEmoticon) return MediaBubbleBrush;
                return HasMedia && !HasText && !_mediaLoadFailed ? MediaBubbleBrush : MessageBorderBrush;
            }
        }
        public Thickness BubbleBorderThickness
        {
            get
            {
                if (HasEmoticon) return new Thickness(0);
                return HasMedia && !HasText && !_mediaLoadFailed ? new Thickness(0) : new Thickness(0.35);
            }
        }
    }


    public sealed class ZaloConversation : ZaloObservable
    {
        private string _name = string.Empty;
        private string _lastText = string.Empty;
        private long _lastTs;
        private int _unread;
        private string _avatarUrl;
        private bool _isPinned;
        private bool _isMarkedUnread;
        private bool _isMuted;
        private bool _friendRequestSent;
        private bool _isTyping;
        private bool _hasMoreHistory;
        private int _groupMemberCount;
        private bool _groupSettingsLoaded;
        private bool _groupPostingRestricted;
        private string _groupPinnedPreview = string.Empty;
        private DateTime _groupPinnedPreviewUpdatedUtc = DateTime.MinValue;
        private int _typingRevision;
        private Brush _colorBrush;
        private Brush _imageBrush;
        private bool _imageMade;

        public string Key { get; set; }
        public string ThreadId { get; set; }
        public bool IsGroup { get; set; }
        public bool FriendRequestSent
        {
            get { return _friendRequestSent; }
            set { if (_friendRequestSent != value) { _friendRequestSent = value; Raise("FriendRequestSent"); } }
        }
        public int GroupMemberCount
        {
            get { return _groupMemberCount; }
            set { if (_groupMemberCount != value) { _groupMemberCount = Math.Max(0, value); Raise("GroupMemberCount"); } }
        }
        public bool GroupSettingsLoaded
        {
            get { return _groupSettingsLoaded; }
            set { if (_groupSettingsLoaded != value) { _groupSettingsLoaded = value; Raise("GroupSettingsLoaded"); } }
        }
        public bool GroupPostingRestricted
        {
            get { return _groupPostingRestricted; }
            set { if (_groupPostingRestricted != value) { _groupPostingRestricted = value; Raise("GroupPostingRestricted"); } }
        }
        public void UpdateGroupMessagePermission(bool settingsLoaded, bool lockSendMessage, bool currentUserIsAdmin)
        {
            GroupPostingRestricted = settingsLoaded && lockSendMessage && !currentUserIsAdmin;
            GroupSettingsLoaded = settingsLoaded;
        }
        public string GroupPinnedPreview
        {
            get { return _groupPinnedPreview; }
            set { if (_groupPinnedPreview != value) { _groupPinnedPreview = value ?? string.Empty; Raise("GroupPinnedPreview"); } }
        }
        public DateTime GroupPinnedPreviewUpdatedUtc
        {
            get { return _groupPinnedPreviewUpdatedUtc; }
            set { _groupPinnedPreviewUpdatedUtc = value; }
        }
        public int HistoryLoadedCount { get; set; }
        public bool HasMoreHistory
        {
            get { return _hasMoreHistory; }
            set { if (_hasMoreHistory != value) { _hasMoreHistory = value; Raise("HasMoreHistory"); } }
        }
        public bool IsPinned
        {
            get { return _isPinned; }
            set { if (_isPinned != value) { _isPinned = value; Raise("IsPinned"); Raise("PinnedVisibility"); } }
        }
        public bool IsMuted
        {
            get { return _isMuted; }
            set { if (_isMuted != value) { _isMuted = value; Raise("IsMuted"); Raise("MutedVisibility"); } }
        }
        public bool CanDeleteFromInbox { get { return Messages.Any(m => m.CanDeleteForMe); } }
        public Visibility PinnedVisibility { get { return IsPinned ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility MutedVisibility { get { return IsMuted ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility GroupVisibility { get { return IsGroup ? Visibility.Visible : Visibility.Collapsed; } }
        public bool IsMarkedUnread
        {
            get { return _isMarkedUnread; }
            set
            {
                if (_isMarkedUnread != value)
                {
                    _isMarkedUnread = value;
                    Raise("IsMarkedUnread");
                    Raise("UnreadVisibility");
                    Raise("UnreadLabel");
                }
            }
        }
        public bool IsTyping
        {
            get { return _isTyping; }
            set { if (_isTyping != value) { _isTyping = value; Raise("IsTyping"); } }
        }
        public int TypingRevision { get { return _typingRevision; } set { _typingRevision = value; } }
        public ObservableCollection<ZaloChatMessage> Messages { get; private set; }
        public HashSet<string> SeenIds { get; private set; }

        public ZaloConversation()
        {
            Messages = new ObservableCollection<ZaloChatMessage>();
            SeenIds = new HashSet<string>();
        }

        public string Name
        {
            get { return _name; }
            set { if (_name != value) { _name = value; _colorBrush = null; Raise("Name"); Raise("Initial"); Raise("AvatarColor"); } }
        }

        public string LastText
        {
            get { return _lastText; }
            set
            {
                string clean = ZaloFormat.SanitizePreviewText(value);
                if (_lastText != clean) { _lastText = clean; Raise("LastText"); }
            }
        }

        public long LastTs
        {
            get { return _lastTs; }
            set { if (_lastTs != value) { _lastTs = value; Raise("LastTs"); Raise("TimeText"); } }
        }

        public int Unread
        {
            get { return _unread; }
            set
            {
                if (_unread != value)
                {
                    _unread = value;
                    Raise("Unread");
                    Raise("UnreadVisibility");
                    Raise("UnreadLabel");
                }
            }
        }

        public string AvatarUrl
        {
            get { return _avatarUrl; }
            set
            {
                if (_avatarUrl == value) return;
                _avatarUrl = value;
                _imageBrush = null;
                _imageMade = false;
                Raise("AvatarImageBrush");
            }
        }

        public string Initial { get { return ZaloAvatar.InitialOf(_name); } }

        public Brush AvatarColor
        {
            get
            {
                if (_colorBrush == null) _colorBrush = ZaloAvatar.ColorFor(_name);
                return _colorBrush;
            }
        }

        public Brush AvatarImageBrush
        {
            get
            {
                if (!_imageMade)
                {
                    _imageBrush = ZaloAvatar.ImageFor(_avatarUrl);
                    _imageMade = true;
                }
                return _imageBrush;
            }
        }

        public string TimeText { get { return ZaloFormat.Time(_lastTs); } }
        public string UnreadLabel
        {
            get { return _unread > 99 ? "99+" : _unread > 0 ? _unread.ToString() : IsMarkedUnread ? "1" : string.Empty; }
        }
        public Visibility UnreadVisibility { get { return _unread > 0 || IsMarkedUnread ? Visibility.Visible : Visibility.Collapsed; } }
    }

    public static class ZaloApp
    {
        private static CoreDispatcher _dispatcher;

        public static void SetDispatcher(CoreDispatcher dispatcher)
        {
            if (dispatcher != null)
            {
                _dispatcher = dispatcher;
                ZaloPhoneSync.Initialize(dispatcher);
            }
        }
        private static readonly SemaphoreSlim ConversationCacheWriteLock = new SemaphoreSlim(1, 1);
        private static readonly Dictionary<string, ZaloConversation> ByKey = new Dictionary<string, ZaloConversation>();
        private static readonly HashSet<string> PendingGroups = new HashSet<string>();
        private static HashSet<string> PinnedConversationKeys = new HashSet<string>(StringComparer.Ordinal);
        private static HashSet<string> ArchivedConversationKeys = new HashSet<string>(StringComparer.Ordinal);
        private static HashSet<string> UnreadMarkedKeys = new HashSet<string>(StringComparer.Ordinal);
        private static HashSet<string> MutedConversationKeys = new HashSet<string>(StringComparer.Ordinal);
        private static HashSet<string> HiddenConversationKeys = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, DateTime> LastTypingSentUtc = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly HashSet<string> LoadingHistory = new HashSet<string>();
        private static readonly HashSet<string> LoadedHistory = new HashSet<string>();
        private static readonly object StickerUrlLock = new object();
        private static readonly HashSet<string> LoadingStickerUrls = new HashSet<string>();
        private static readonly Dictionary<string, string> StickerUrlCache = new Dictionary<string, string>();
        private static readonly object LinkPreviewLock = new object();
        private static readonly Dictionary<string, JsonObject> LinkPreviewCache = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LoadingLinkPreviews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _friendNames = new Dictionary<string, string>();
        private static Dictionary<string, string> _friendAvatars = new Dictionary<string, string>();
        private static string _cachedDataUid;
        private static bool _batchScheduled;
        private static bool _resolvingGroups;
        private static bool _resumeHooked;

        public static ZaloSession Session { get; private set; }
        public static ZaloListener Listener { get; private set; }
        public static ZaloConversation CurrentChat { get; set; }
        public static string ConnectionStatus { get; private set; }
        public static List<ZaloFriend> Friends { get; set; }
        public static bool FriendsLoaded { get; private set; }
        public static ObservableCollection<ZaloConversation> Conversations { get; private set; }
        public static ObservableCollection<ZaloConversation> Groups { get; private set; }
        public static bool GroupsLoaded { get; private set; }
        public static bool GroupsLoadFailed { get; private set; }
        public static bool IsRestoringCache { get; private set; }

        public static bool IsFriendOnline(string userId)
        {
            return !string.IsNullOrEmpty(userId) && Friends != null
                && Friends.Any(friend => friend.UserId == userId && friend.IsOnline);
        }

        public static bool IsFriend(string userId)
        {
            return !string.IsNullOrWhiteSpace(userId) && FriendsLoaded && Friends != null
                && Friends.Any(friend => string.Equals(friend.UserId, userId, StringComparison.Ordinal));
        }

        public static void UpdateConversationPreviewFromMessage(ZaloConversation conversation, ZaloChatMessage message)
        {
            if (conversation == null || message == null) return;
            if (message.Ts > 0 && (conversation.LastTs <= 0 || message.Ts >= conversation.LastTs))
            {
                conversation.LastTs = message.Ts;
            }

            string preview = message.Text;
            if (string.IsNullOrWhiteSpace(preview) || preview.Contains("dlfl.vn") || preview.Contains("zadn.vn/api/emoticon"))
            {
                preview = message.HasMedia || message.MsgType == "chat.photo" ? "[Ảnh]"
                    : message.IsSticker || message.MsgType == "5" || message.MsgType == "chat.sticker" ? "[Nhãn dán]"
                    : message.HasVoice ? "[Tin nhắn thoại]"
                    : message.HasVideo ? "[Video]"
                    : message.HasFile ? "[Tệp] " + (message.FileName ?? string.Empty)
                    : message.HasLocation ? "[Vị trí]"
                    : message.HasContactCard ? (string.IsNullOrEmpty(message.ContactName) ? "[Danh thiếp]" : "[Danh thiếp] " + message.ContactName)
                    : string.Empty;
            }
            else
            {
                preview = ZaloFormat.SanitizePreviewText(preview);
            }

            conversation.LastText = conversation.IsGroup && !message.FromMe && !message.IsSystemEvent
                ? message.Sender + ": " + preview
                : preview;
        }

        public static event Action StatusChanged;
        public static event Action GroupsLoadChanged;
        public static event Action FriendsLoadChanged;
        public static event Action CacheRestored;
        public static event Action<ZaloConversation> TypingChanged;

        static ZaloApp()
        {
            Conversations = new ObservableCollection<ZaloConversation>();
            Groups = new ObservableCollection<ZaloConversation>();
            Friends = new List<ZaloFriend>();
            FriendsLoaded = false;
            ConnectionStatus = string.Empty;
        }

        private const string LegacyCacheFile = "zalo-conversations.json";

        private static string CacheFileFor(ZaloSession session)
        {
            // Keep cached conversations separated between accounts on a shared device.
            return CacheFileForUid(session == null ? string.Empty : session.Uid);
        }

        private static string CacheFileForUid(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return null;
            return "zalo-conversations-" + ZaloCrypto.Md5Hex(uid).Substring(0, 16) + ".json";
        }

        private static string FriendCacheFileFor(ZaloSession session)
        {
            return FriendCacheFileForUid(session == null ? string.Empty : session.Uid);
        }

        private static string FriendCacheFileForUid(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return null;
            return "zalo-friends-" + ZaloCrypto.Md5Hex(uid).Substring(0, 16) + ".json";
        }

        public static async Task LoadCachedFriendsAsync(ZaloSession session)
        {
            await LoadCachedFriendsAsync(session == null ? string.Empty : session.Uid);
        }

        private static async Task LoadCachedFriendsAsync(string userId)
        {
            try
            {
                string cacheFile = FriendCacheFileForUid(userId);
                if (cacheFile == null) return;
                IStorageItem item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(cacheFile);
                StorageFile file = item as StorageFile;
                if (file == null) return;

                string json = await FileIO.ReadTextAsync(file);
                JsonArray root;
                if (!JsonArray.TryParse(json, out root)) return;
                FriendsLoaded = true;
                var cached = new List<ZaloFriend>();
                foreach (IJsonValue value in root)
                {
                    if (value.ValueType != JsonValueType.Object) continue;
                    JsonObject friend = value.GetObject();
                    string uid = ZaloAuth.Str(friend, "userId");
                    if (string.IsNullOrWhiteSpace(uid)) continue;
                    cached.Add(new ZaloFriend
                    {
                        UserId = uid,
                        Name = ZaloAuth.Str(friend, "name"),
                        OriginalName = ZaloAuth.Str(friend, "originalName"),
                        Alias = ZaloAuth.Str(friend, "alias"),
                        Avatar = ZaloAuth.Str(friend, "avatar"),
                        Subtitle = ZaloAuth.Str(friend, "subtitle"),
                        IsOnline = false
                    });
                }
                if (cached.Count == 0) return;
                Friends = cached;
                UpdateFriendLookupCaches();
                ZaloLog.Add("Restored cached friends: " + Friends.Count);
                if (FriendsLoadChanged != null) FriendsLoadChanged();
            }
            catch (Exception ex) { ZaloLog.Add("Friend cache load failed: " + ex.Message); }
        }

        public static async Task SaveCachedFriendsAsync()
        {
            try
            {
                string cacheFile = FriendCacheFileFor(Session);
                if (cacheFile == null || Friends == null || Friends.Count == 0) return;
                var root = new JsonArray();
                foreach (ZaloFriend friend in Friends)
                {
                    if (friend == null || string.IsNullOrWhiteSpace(friend.UserId)) continue;
                    var value = new JsonObject();
                    value["userId"] = JsonValue.CreateStringValue(friend.UserId);
                    value["name"] = JsonValue.CreateStringValue(friend.Name ?? string.Empty);
                    value["originalName"] = JsonValue.CreateStringValue(friend.OriginalName ?? string.Empty);
                    value["alias"] = JsonValue.CreateStringValue(friend.Alias ?? string.Empty);
                    value["avatar"] = JsonValue.CreateStringValue(friend.Avatar ?? string.Empty);
                    value["subtitle"] = JsonValue.CreateStringValue(friend.Subtitle ?? string.Empty);
                    root.Add(value);
                }
                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    cacheFile, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, root.Stringify());
            }
            catch (Exception ex) { ZaloLog.Add("Friend cache save failed: " + ex.Message); }
        }

        private static void UpdateFriendLookupCaches()
        {
            _friendNames = (Friends ?? new List<ZaloFriend>())
                .Where(friend => !string.IsNullOrWhiteSpace(friend.UserId))
                .GroupBy(friend => friend.UserId)
                .ToDictionary(group => group.Key, group => group.First().Name);
            _friendAvatars = (Friends ?? new List<ZaloFriend>())
                .Where(friend => !string.IsNullOrWhiteSpace(friend.UserId) && !string.IsNullOrWhiteSpace(friend.Avatar))
                .GroupBy(friend => friend.UserId)
                .ToDictionary(group => group.Key, group => group.First().Avatar);
        }

        private static async Task DeleteLegacyCacheAsync()
        {
            try
            {
                IStorageItem legacy = await Windows.Storage.ApplicationData.Current.LocalFolder.TryGetItemAsync(LegacyCacheFile);
                if (legacy != null) await legacy.DeleteAsync();
            }
            catch { }
        }

        public static async Task LoadCachedConversationsAsync()
        {
            await LoadCachedConversationsAsync(Session == null ? string.Empty : Session.Uid);
        }

        private static async Task LoadCachedConversationsAsync(string userId)
        {
            try
            {
                string cacheFile = CacheFileForUid(userId);
                if (cacheFile == null) return;
                var item = await Windows.Storage.ApplicationData.Current.LocalFolder.TryGetItemAsync(cacheFile);
                if (item is Windows.Storage.StorageFile file)
                {
                    string json = await Windows.Storage.FileIO.ReadTextAsync(file);
                    JsonArray root;
                    if (JsonArray.TryParse(json, out root))
                    {
                        foreach (var itemVal in root)
                        {
                            try
                            {
                                if (itemVal.ValueType != JsonValueType.Object) continue;
                                var o = itemVal.GetObject();
                                string threadId = ZaloAuth.Str(o, "threadId");
                                bool isGroup = o.GetNamedBoolean("isGroup", false);
                                if (string.IsNullOrEmpty(threadId)) continue;

                                var conv = GetOrCreate(threadId, isGroup);
                                string name = ZaloAuth.Str(o, "name");
                                string avatar = ZaloAuth.Str(o, "avatar");
                                if (!string.IsNullOrEmpty(name)) conv.Name = name;
                                if (!string.IsNullOrEmpty(avatar)) conv.AvatarUrl = avatar;
                                conv.LastText = ZaloAuth.Str(o, "lastText");
                                conv.LastTs = (long)ZaloAuth.Num(o, "lastTs");
                                conv.Unread = (int)ZaloAuth.Num(o, "unread");
                                conv.IsMarkedUnread = o.GetNamedBoolean("isMarkedUnread", false);
                                conv.IsMuted = o.GetNamedBoolean("isMuted", conv.IsMuted);
                                conv.GroupPostingRestricted = o.GetNamedBoolean("groupPostingRestricted", false);
                                conv.GroupSettingsLoaded = o.GetNamedBoolean("groupSettingsLoaded", false);

                                IJsonValue msgsVal;
                                if (o.TryGetValue("msgs", out msgsVal) && msgsVal.ValueType == JsonValueType.Array)
                                {
                                    foreach (var mVal in msgsVal.GetArray())
                                    {
                                        try
                                        {
                                            if (mVal.ValueType != JsonValueType.Object) continue;
                                            var mo = mVal.GetObject();
                                            string msgId = ZaloAuth.Str(mo, "id");
                                            if (!string.IsNullOrEmpty(msgId) && conv.SeenIds.Add(msgId))
                                            {
                                                var cachedMessage = new ZaloChatMessage
                                                {
                                                    Id = msgId,
                                                    GlobalMessageId = ZaloAuth.Str(mo, "globalMessageId"),
                                                    ClientMessageId = ZaloAuth.Str(mo, "clientMsgId"),
                                                    SenderUid = ZaloAuth.Str(mo, "senderUid"),
                                                    RecipientUid = ZaloAuth.Str(mo, "recipientUid"),
                                                    Sender = ZaloAuth.Str(mo, "sender"),
                                                    Status = (int)ZaloAuth.Num(mo, "status"),
                                                    At = (int)ZaloAuth.Num(mo, "at"),
                                                    Command = (int)ZaloAuth.Num(mo, "command"),
                                                    SeenSent = mo.GetNamedBoolean("seenSent", false),
                                                    ReceiptState = (int)ZaloAuth.Num(mo, "receiptState"),
                                                    DeliveryAvatarUrl = ZaloAuth.Str(mo, "deliveryAvatarUrl"),
                                                    SenderAvatarUrl = ZaloAuth.Str(mo, "senderAvatarUrl"),
                                                    Text = ZaloAuth.Str(mo, "text"),
                                                    ReplyContent = ZaloAuth.Str(mo, "replyContent"),
                                                    QuoteMessage = ZaloAuth.Str(mo, "quoteMessage"),
                                                    QuoteAttachment = ZaloAuth.Str(mo, "quoteAttachment"),
                                                    ReplyBlocked = mo.GetNamedBoolean("replyBlocked", false),
                                                    QuotedSender = ZaloAuth.Str(mo, "quotedSender"),
                                                    QuotedText = ZaloAuth.Str(mo, "quotedText"),
                                                    ReactionIcon = ZaloAuth.Str(mo, "reactionIcon"),
                                                    IsRecalled = mo.GetNamedBoolean("isRecalled", false),
                                                    MsgType = ZaloAuth.Str(mo, "msgType"),
                                                    StickerId = ZaloAuth.Str(mo, "stickerId"),
                                                    FileName = ZaloAuth.Str(mo, "fileName"),
                                                    MediaUrl = ZaloAuth.Str(mo, "mediaUrl"),
                                                    MediaFullUrl = ZaloAuth.Str(mo, "mediaFullUrl"),
                                                    FromMe = mo.GetNamedBoolean("fromMe", false),
                                                    ShowSender = mo.GetNamedBoolean("showSender", false),
                                                    Ts = (long)ZaloAuth.Num(mo, "ts")
                                                };
                                                if (mo.GetNamedBoolean("hasLocation", false))
                                                    cachedMessage.SetLocation(ZaloAuth.Num(mo, "locationLatitude"),
                                                        ZaloAuth.Num(mo, "locationLongitude"), ZaloAuth.Str(mo, "locationLabel"));
                                                string cachedLinkUrl = ZaloAuth.Str(mo, "linkPreviewUrl");
                                                if (cachedLinkUrl.Length > 0)
                                                    cachedMessage.SetLinkPreview(ZaloAuth.Str(mo, "linkPreviewTitle"),
                                                        ZaloAuth.Str(mo, "linkPreviewDescription"), cachedLinkUrl,
                                                        ZaloAuth.Str(mo, "linkPreviewImageUrl"));
                                                long cachedPollId = (long)ZaloAuth.Num(mo, "pollId");
                                                if (cachedPollId > 0) cachedMessage.SetPoll(cachedPollId, ZaloAuth.Str(mo, "pollQuestion"));
                                                string cachedContactUid = ZaloAuth.Str(mo, "contactUid");
                                                if (cachedContactUid.Length > 0)
                                                    cachedMessage.SetContactCard(cachedContactUid, ZaloAuth.Str(mo, "contactName"),
                                                        ZaloAuth.Str(mo, "contactAvatarUrl"), ZaloAuth.Str(mo, "contactQrUrl"),
                                                        ZaloAuth.Str(mo, "contactPhone"));
                                                else if (!cachedMessage.HasContactCard && !string.IsNullOrEmpty(cachedMessage.Text) && cachedMessage.Text.TrimStart().StartsWith("{") && (cachedMessage.Text.Contains("\"phone\"") || cachedMessage.Text.Contains("\"qrCodeUrl\"")))
                                                {
                                                    JsonObject cardObj = ZaloJson.ParseObject(cachedMessage.Text);
                                                    if (cardObj != null)
                                                    {
                                                        string cardPhone = ZaloAuth.Str(cardObj, "phone");
                                                        if (cardPhone.Length == 0) cardPhone = ZaloAuth.Str(cardObj, "phoneNumber");
                                                        string cardQr = ZaloAuth.Str(cardObj, "qrCodeUrl");
                                                        string cardName = ZaloAuth.Str(cardObj, "displayName");
                                                        if (cardName.Length == 0) cardName = ZaloAuth.Str(cardObj, "name");
                                                        if (cardName.Length == 0) cardName = cardPhone;
                                                        string cardUid = ZaloAuth.Str(cardObj, "contactUid");
                                                        if (cardUid.Length == 0) cardUid = cardPhone;
                                                        cachedMessage.SetContactCard(cardUid, cardName, ZaloAuth.Str(cardObj, "avatar"), cardQr, cardPhone);
                                                    }
                                                }
                                                conv.Messages.Add(cachedMessage);
                                            }
                                        }
                                        catch (Exception exMsg)
                                        {
                                            ZaloLog.Add("Cached msg error: " + exMsg.Message);
                                        }
                                    }
                                }

                                if ((string.IsNullOrWhiteSpace(conv.LastText) || conv.LastTs <= 0) && conv.Messages.Count > 0)
                                {
                                    var latest = conv.Messages.OrderBy(m => m.Ts).LastOrDefault();
                                    if (latest != null) UpdateConversationPreviewFromMessage(conv, latest);
                                }
                            }
                            catch (Exception exConv)
                            {
                                ZaloLog.Add("Cached conv error: " + exConv.Message);
                            }
                        }

                        foreach (var c in Conversations)
                        {
                            if (string.IsNullOrWhiteSpace(c.LastText))
                            {
                                if (c.Messages.Count > 0)
                                {
                                    var latest = c.Messages.OrderBy(m => m.Ts).LastOrDefault();
                                    if (latest != null) UpdateConversationPreviewFromMessage(c, latest);
                                }
                                else if (!c.IsGroup)
                                {
                                    c.LastText = "Nhấn để trò chuyện";
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Load cache error: " + ex.Message);
            }
        }

        public static async Task SaveCachedConversationsAsync()
        {
            await ConversationCacheWriteLock.WaitAsync();
            try { await SaveCachedConversationsCoreAsync(); }
            finally { ConversationCacheWriteLock.Release(); }
        }

        private static async Task SaveCachedConversationsCoreAsync()
        {
            try
            {
                string cacheFile = CacheFileFor(Session);
                if (cacheFile == null) return;
                var arr = new JsonArray();
                var list = Conversations.ToList();
                foreach (var conv in list)
                {
                    var o = new JsonObject();
                    o["key"] = JsonValue.CreateStringValue(conv.Key ?? string.Empty);
                    o["threadId"] = JsonValue.CreateStringValue(conv.ThreadId ?? string.Empty);
                    o["isGroup"] = JsonValue.CreateBooleanValue(conv.IsGroup);
                    o["name"] = JsonValue.CreateStringValue(conv.Name ?? string.Empty);
                    o["avatar"] = JsonValue.CreateStringValue(conv.AvatarUrl ?? string.Empty);
                    o["lastText"] = JsonValue.CreateStringValue(conv.LastText ?? string.Empty);
                    o["lastTs"] = JsonValue.CreateNumberValue(conv.LastTs);
                    o["unread"] = JsonValue.CreateNumberValue(conv.Unread);
                    o["isMarkedUnread"] = JsonValue.CreateBooleanValue(conv.IsMarkedUnread);
                    o["isMuted"] = JsonValue.CreateBooleanValue(conv.IsMuted);
                    o["groupPostingRestricted"] = JsonValue.CreateBooleanValue(conv.GroupPostingRestricted);
                    o["groupSettingsLoaded"] = JsonValue.CreateBooleanValue(conv.GroupSettingsLoaded);

                    var msgs = new JsonArray();
                    int startIdx = Math.Max(0, conv.Messages.Count - 30);
                    for (int i = startIdx; i < conv.Messages.Count; i++)
                    {
                        var m = conv.Messages[i];
                        var mo = new JsonObject();
                        mo["id"] = JsonValue.CreateStringValue(m.Id ?? string.Empty);
                        mo["globalMessageId"] = JsonValue.CreateStringValue(m.GlobalMessageId ?? string.Empty);
                        mo["clientMsgId"] = JsonValue.CreateStringValue(m.ClientMessageId ?? string.Empty);
                        mo["senderUid"] = JsonValue.CreateStringValue(m.SenderUid ?? string.Empty);
                        mo["recipientUid"] = JsonValue.CreateStringValue(m.RecipientUid ?? string.Empty);
                        mo["sender"] = JsonValue.CreateStringValue(m.Sender ?? string.Empty);
                        mo["senderAvatarUrl"] = JsonValue.CreateStringValue(m.SenderAvatarUrl ?? string.Empty);
                        mo["status"] = JsonValue.CreateNumberValue(m.Status);
                        mo["at"] = JsonValue.CreateNumberValue(m.At);
                        mo["command"] = JsonValue.CreateNumberValue(m.Command);
                        mo["seenSent"] = JsonValue.CreateBooleanValue(m.SeenSent);
                        mo["receiptState"] = JsonValue.CreateNumberValue(m.ReceiptState);
                        mo["deliveryAvatarUrl"] = JsonValue.CreateStringValue(m.DeliveryAvatarUrl ?? string.Empty);
                        mo["text"] = JsonValue.CreateStringValue(m.Text ?? string.Empty);
                        mo["replyContent"] = JsonValue.CreateStringValue(m.ReplyContent ?? string.Empty);
                        mo["quoteMessage"] = JsonValue.CreateStringValue(m.QuoteMessage ?? string.Empty);
                        mo["quoteAttachment"] = JsonValue.CreateStringValue(m.QuoteAttachment ?? string.Empty);
                        mo["replyBlocked"] = JsonValue.CreateBooleanValue(m.ReplyBlocked);
                        mo["quotedSender"] = JsonValue.CreateStringValue(m.QuotedSender ?? string.Empty);
                        mo["quotedText"] = JsonValue.CreateStringValue(m.QuotedText ?? string.Empty);
                        mo["reactionIcon"] = JsonValue.CreateStringValue(m.ReactionIcon ?? string.Empty);
                        mo["isRecalled"] = JsonValue.CreateBooleanValue(m.IsRecalled);
                        mo["msgType"] = JsonValue.CreateStringValue(m.MsgType ?? string.Empty);
                        mo["stickerId"] = JsonValue.CreateStringValue(m.StickerId ?? string.Empty);
                        mo["fileName"] = JsonValue.CreateStringValue(m.FileName ?? string.Empty);
                        mo["mediaUrl"] = JsonValue.CreateStringValue(m.MediaUrl ?? string.Empty);
                        mo["mediaFullUrl"] = JsonValue.CreateStringValue(m.MediaFullUrl ?? string.Empty);
                        mo["hasLocation"] = JsonValue.CreateBooleanValue(m.HasLocation);
                        if (m.HasLocation)
                        {
                            mo["locationLatitude"] = JsonValue.CreateNumberValue(m.LocationLatitude.Value);
                            mo["locationLongitude"] = JsonValue.CreateNumberValue(m.LocationLongitude.Value);
                            mo["locationLabel"] = JsonValue.CreateStringValue(m.LocationLabel ?? string.Empty);
                        }
                        mo["pollId"] = JsonValue.CreateNumberValue(m.PollId);
                        mo["pollQuestion"] = JsonValue.CreateStringValue(m.PollQuestion ?? string.Empty);
                        mo["linkPreviewTitle"] = JsonValue.CreateStringValue(m.LinkPreviewTitle);
                        mo["linkPreviewDescription"] = JsonValue.CreateStringValue(m.LinkPreviewDescription);
                        mo["linkPreviewUrl"] = JsonValue.CreateStringValue(m.LinkPreviewUrl);
                        mo["linkPreviewImageUrl"] = JsonValue.CreateStringValue(m.LinkPreviewImageUrl);
                        mo["contactUid"] = JsonValue.CreateStringValue(m.ContactUid);
                        mo["contactName"] = JsonValue.CreateStringValue(m.ContactName);
                        mo["contactAvatarUrl"] = JsonValue.CreateStringValue(m.ContactAvatarUrl);
                        mo["contactQrUrl"] = JsonValue.CreateStringValue(m.ContactQrUrl);
                        mo["contactPhone"] = JsonValue.CreateStringValue(m.ContactPhone);
                        mo["fromMe"] = JsonValue.CreateBooleanValue(m.FromMe);
                        mo["showSender"] = JsonValue.CreateBooleanValue(m.ShowSender);
                        mo["ts"] = JsonValue.CreateNumberValue(m.Ts);
                        msgs.Add(mo);
                    }
                    o["msgs"] = msgs;
                    arr.Add(o);
                }

                var file = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFileAsync(
                    cacheFile, Windows.Storage.CreationCollisionOption.ReplaceExisting);
                await Windows.Storage.FileIO.WriteTextAsync(file, arr.Stringify());
            }
            catch { }
        }

        public static async Task ClearLocalCacheAsync()
        {
            _cachedDataUid = null;
            foreach (ZaloConversation conversation in Conversations)
            {
                conversation.Messages.Clear();
                conversation.SeenIds.Clear();
                conversation.LastText = string.Empty;
                conversation.LastTs = 0;
                conversation.Unread = 0;
            }
            Conversations.Clear();
            Groups.Clear();
            ByKey.Clear();
            PendingGroups.Clear();
            LoadingHistory.Clear();
            LoadedHistory.Clear();
            GroupsLoaded = false;
            GroupsLoadFailed = false;

            string cacheFile = CacheFileFor(Session);
            IStorageItem item = cacheFile == null ? null
                : await Windows.Storage.ApplicationData.Current.LocalFolder.TryGetItemAsync(cacheFile);
            if (item != null) await item.DeleteAsync();
            if (Session != null) _ = LoadGroupsAsync(Session);
        }

        public static async Task PreloadOfflineCacheAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)
                || string.Equals(_cachedDataUid, userId, StringComparison.Ordinal)) return;

            IsRestoringCache = true;
            try
            {
                // A different cached account must not see the previous account's in-memory rows.
                Conversations.Clear();
                Groups.Clear();
                ByKey.Clear();
                PendingGroups.Clear();
                LoadingHistory.Clear();
                LoadedHistory.Clear();
                Friends = new List<ZaloFriend>();
                FriendsLoaded = false;
                _friendNames.Clear();
                _friendAvatars.Clear();

                await LoadCachedConversationsAsync(userId);
                await LoadCachedFriendsAsync(userId);
                ArchivedConversationKeys = new HashSet<string>(
                    ZaloSettingsService.Current.GetArchivedConversationKeys(userId), StringComparer.Ordinal);
                _cachedDataUid = userId;
            }
            finally
            {
                IsRestoringCache = false;
                if (CacheRestored != null) CacheRestored();
            }
        }

        public static async Task StartAsync(ZaloSession session)
        {
            if (ReferenceEquals(Session, session) && Listener != null) return;

            Session = session;
            bool cacheAlreadyLoaded = session != null
                && string.Equals(_cachedDataUid, session.Uid, StringComparison.Ordinal);
            if (!cacheAlreadyLoaded)
            {
                Conversations.Clear();
                Groups.Clear();
                ByKey.Clear();
                PendingGroups.Clear();
                LoadingHistory.Clear();
                LoadedHistory.Clear();
                FriendsLoaded = false;
                Friends = new List<ZaloFriend>();
                _friendNames.Clear();
                _friendAvatars.Clear();
            }
            if (_dispatcher == null)
            {
                try
                {
                    _dispatcher = Window.Current?.Dispatcher ?? Windows.ApplicationModel.Core.CoreApplication.MainView?.CoreWindow?.Dispatcher;
                }
                catch { }
            }
            if (_dispatcher != null)
            {
                ZaloPhoneSync.Initialize(_dispatcher);
            }

            // Render local data before making any network request. The old unscoped cache is
            // cleanup only and must never hold the first screen behind storage/network work.
            if (!cacheAlreadyLoaded)
            {
                IsRestoringCache = true;
                try
                {
                    await LoadCachedConversationsAsync();
                    await LoadCachedFriendsAsync(session);
                    _cachedDataUid = session == null ? null : session.Uid;
                }
                catch { }
                finally
                {
                    IsRestoringCache = false;
                    if (CacheRestored != null) CacheRestored();
                }
            }

            ArchivedConversationKeys = new HashSet<string>(ZaloSettingsService.Current.GetArchivedConversationKeys(session?.Uid), StringComparer.Ordinal);

            Listener = new ZaloListener(session);
            Listener.MessageReceived += OnIncoming;
            Listener.TypingReceived += OnTyping;
            Listener.GroupEventReceived += OnGroupEvent;
            Listener.ReceiptReceived += OnReceiptReceived;
            Listener.ReactionReceived += OnReactionReceived;
            Listener.StatusChanged += OnStatus;

            if (!_resumeHooked)
            {
                _resumeHooked = true;
                Application.Current.Resuming += (s, e) =>
                {
                    ZaloListener l = Listener;
                    if (l != null) { var ignored = l.RestartAsync(); }
                };
            }

            ConnectionStatus = "Đang kết nối…";
            if (StatusChanged != null) StatusChanged();

            // Start the socket and refresh server-only state after the cached screen is visible.
            ZaloListener listener = Listener;
            _ = StartListenerInBackgroundAsync(listener);
            _ = RefreshStartupDataAsync(session);
            _ = LoadGroupsAsync(session);

            // Old account-agnostic data is no longer used; remove it in the background.
            _ = DeleteLegacyCacheAsync();
        }

        private static async Task StartListenerInBackgroundAsync(ZaloListener listener)
        {
            try
            {
                await listener.StartAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Listener startup failed: " + ex.Message);
            }
        }

        private static async Task RefreshStartupDataAsync(ZaloSession session)
        {
            try
            {
                HashSet<string> pinned = await ZaloMessaging.GetPinnedConversationKeysAsync(session, CancellationToken.None);
                if (pinned != null)
                {
                    PinnedConversationKeys = pinned;
                    foreach (ZaloConversation conversation in Conversations)
                        conversation.IsPinned = pinned.Contains(conversation.Key);
                    if (StatusChanged != null) StatusChanged();
                }
            }
            catch (Exception ex) { ZaloLog.Add("Pinned conversations unavailable: " + ex.Message); }

            try
            {
                HashSet<string> archived = await ZaloMessaging.GetArchivedConversationKeysAsync(session, CancellationToken.None);
                if (archived != null)
                {
                    ArchivedConversationKeys = archived;
                    ZaloSettingsService.Current.SetArchivedConversationKeys(session.Uid, archived.ToList());
                }
            }
            catch (Exception ex) { ZaloLog.Add("Archived conversations unavailable: " + ex.Message); }

            try
            {
                UnreadMarkedKeys = await ZaloMessaging.GetUnreadMarkKeysAsync(session, CancellationToken.None);
                foreach (ZaloConversation conversation in Conversations)
                    conversation.IsMarkedUnread = UnreadMarkedKeys.Contains(conversation.Key);
            }
            catch (Exception ex) { ZaloLog.Add("Unread marks unavailable: " + ex.Message); }

            try
            {
                MutedConversationKeys = await ZaloAuth.GetMutedConversationKeysAsync(session, CancellationToken.None);
                foreach (ZaloConversation conversation in Conversations)
                    conversation.IsMuted = MutedConversationKeys.Contains(conversation.Key);
            }
            catch (Exception ex) { ZaloLog.Add("Conversation mute state unavailable: " + ex.Message); }

            try
            {
                HiddenConversationKeys = await ZaloAuth.GetHiddenConversationKeysAsync(session, CancellationToken.None);
            }
            catch (Exception ex)
            {
                HiddenConversationKeys = new HashSet<string>(StringComparer.Ordinal);
                ZaloLog.Add("Hidden conversation state unavailable: " + ex.Message);
            }

            try
            {
                List<ZaloFriend> refreshedFriends = await ZaloAuth.GetFriendsAsync(session, CancellationToken.None);
                if (refreshedFriends != null)
                {
                    Friends = refreshedFriends;
                    FriendsLoaded = true;
                    UpdateFriendLookupCaches();
                    if (FriendsLoadChanged != null) FriendsLoadChanged();
                    await SaveCachedFriendsAsync();
                }
                ZaloLog.Add("Friends loaded: " + (Friends == null ? 0 : Friends.Count));
            }
            catch (Exception ex) { ZaloLog.Add("Friend list refresh failed; using account cache when available: " + ex.Message); }

            try
            {
                HashSet<string> onlineIds = await ZaloAuth.GetFriendOnlineIdsAsync(session, CancellationToken.None);
                foreach (ZaloFriend friend in Friends) friend.IsOnline = onlineIds.Contains(friend.UserId ?? string.Empty);
            }
            catch (Exception ex) { ZaloLog.Add("Friend presence unavailable: " + ex.Message); }

            try { await ZaloAuth.LoadMyProfileAsync(session, CancellationToken.None); }
            catch (Exception ex) { ZaloLog.Add("Profile load failed: " + ex.Message); }

            // The profile request runs after cached startup. Notify the shell so its name and
            // avatar can replace the temporary account-ID fallback without blocking launch.
            if (_dispatcher != null)
            {
                try
                {
                    await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        if (!ReferenceEquals(Session, session)) return;
                        Action handler = StatusChanged;
                        if (handler != null) handler();
                    });
                }
                catch { }
            }
        }

        public static void Reset()
        {
            _cachedDataUid = null;
            string cacheFile = CacheFileFor(Session);
            if (Listener != null) Listener.Stop();
            if (Session != null) Session.Http.ClearCookies();
            Listener = null;
            Session = null;
            CurrentChat = null;
            ByKey.Clear();
            PendingGroups.Clear();
            LoadingHistory.Clear();
            LoadedHistory.Clear();
            Conversations.Clear();
            Groups.Clear();
            GroupsLoaded = false;
            GroupsLoadFailed = false;
            NotifyGroupsLoadChanged();
            PinnedConversationKeys = new HashSet<string>(StringComparer.Ordinal);
            ArchivedConversationKeys = new HashSet<string>(StringComparer.Ordinal);
            Friends = new List<ZaloFriend>();
            _friendNames = new Dictionary<string, string>();
            _friendAvatars = new Dictionary<string, string>();
            LastTypingSentUtc.Clear();
            UnreadMarkedKeys = new HashSet<string>(StringComparer.Ordinal);
            MutedConversationKeys = new HashSet<string>(StringComparer.Ordinal);
            HiddenConversationKeys = new HashSet<string>(StringComparer.Ordinal);
            ConnectionStatus = string.Empty;
            if (cacheFile != null) try
            {
                var ignored = Windows.Storage.ApplicationData.Current.LocalFolder.TryGetItemAsync(cacheFile)
                    .AsTask().ContinueWith(t => (t.Result as Windows.Storage.StorageFile)?.DeleteAsync());
            }
            catch { }
        }

        private static void OnStatus(string status)
        {
            var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ConnectionStatus = status;
                Action handler = StatusChanged;
                if (handler != null) handler();
            });
        }

        private static void OnIncoming(ZaloIncoming incoming)
        {
            var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try { Handle(incoming); }
                catch (Exception ex) { ZaloLog.Add("Could not show a message: " + ex.Message); }
            });
        }

        private static void OnTyping(ZaloTyping typing)
        {
            if (typing == null || string.IsNullOrWhiteSpace(typing.ThreadId) || _dispatcher == null) return;
            string key = (typing.IsGroup ? "g:" : "u:") + typing.ThreadId;
            var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                ZaloConversation conversation = Find(key);
                if (conversation == null) return;
                int revision = ++conversation.TypingRevision;
                conversation.IsTyping = true;
                Action<ZaloConversation> handler = TypingChanged;
                if (handler != null) handler(conversation);
                await Task.Delay(2600);
                if (conversation.TypingRevision != revision) return;
                conversation.IsTyping = false;
                Action<ZaloConversation> updatedHandler = TypingChanged;
                if (updatedHandler != null) updatedHandler(conversation);
            });
        }

        private static void OnReceiptReceived(JsonObject payload, bool isGroup)
        {
            if (payload == null || _dispatcher == null) return;
            var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                bool changed = false;
                JsonArray delivered = ArrayValue(payload, "delivereds");
                if (delivered != null)
                {
                    foreach (IJsonValue value in delivered)
                    {
                        if (value.ValueType != JsonValueType.Object) continue;
                        JsonObject receipt = value.GetObject();
                        string threadId = isGroup ? ZaloAuth.Str(receipt, "groupId") : FirstStringValue(receipt, "deliveredUids");
                        if (threadId.Length == 0) threadId = ZaloAuth.Str(receipt, "idTo");
                        if (threadId.Length == 0) continue;
                        ZaloConversation conversation = Find((isGroup ? "g:" : "u:") + threadId);
                        if (conversation == null) continue;

                        string[] seenUids = StringArrayValue(receipt, "seenUids");
                        string[] deliveredUids = StringArrayValue(receipt, "deliveredUids");
                        if (isGroup && Session != null)
                        {
                            seenUids = seenUids.Where(uid => uid != Session.Uid).ToArray();
                            deliveredUids = deliveredUids.Where(uid => uid != Session.Uid).ToArray();
                        }
                        int state = (!isGroup && ZaloAuth.Num(receipt, "seen") > 0) || seenUids.Length > 0 ? 2
                            : deliveredUids.Length > 0 ? 1 : 0;
                        string avatarUid = isGroup
                            ? (seenUids.FirstOrDefault() ?? deliveredUids.FirstOrDefault() ?? string.Empty)
                            : threadId;
                        changed |= ApplyReceiptToMessage(conversation, receipt, state, AvatarForUser(avatarUid));
                    }
                }

                JsonArray seen = ArrayValue(payload, isGroup ? "groupSeens" : "seens");
                if (seen != null)
                {
                    foreach (IJsonValue value in seen)
                    {
                        if (value.ValueType != JsonValueType.Object) continue;
                        JsonObject receipt = value.GetObject();
                        string threadId = isGroup ? ZaloAuth.Str(receipt, "groupId") : ZaloAuth.Str(receipt, "idTo");
                        if (threadId.Length == 0) continue;
                        ZaloConversation conversation = Find((isGroup ? "g:" : "u:") + threadId);
                        if (conversation == null) continue;
                        string avatarUid = isGroup
                            ? (StringArrayValue(receipt, "seenUids").FirstOrDefault() ?? string.Empty)
                            : threadId;
                        if (isGroup && Session != null && avatarUid == Session.Uid)
                            avatarUid = StringArrayValue(receipt, "seenUids").FirstOrDefault(uid => uid != Session.Uid) ?? string.Empty;
                        changed |= ApplyReceiptToMessage(conversation, receipt, 2, AvatarForUser(avatarUid));
                    }
                }

                if (changed)
                {
                    if (StatusChanged != null) StatusChanged();
                    _ = SaveCachedConversationsAsync();
                }
            });
        }

        private static void OnReactionReceived(JsonObject payload, bool ignoredGroupFlag)
        {
            if (payload == null || _dispatcher == null) return;
            IJsonValue userValues, groupValues;
            JsonArray userReactions = payload.TryGetValue("reacts", out userValues) && userValues.ValueType == JsonValueType.Array ? userValues.GetArray() : null;
            JsonArray groupReactions = payload.TryGetValue("reactGroups", out groupValues) && groupValues.ValueType == JsonValueType.Array ? groupValues.GetArray() : null;
            if ((userReactions == null || userReactions.Count == 0) && (groupReactions == null || groupReactions.Count == 0)) return;
            var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                bool changed = ApplyReactionArray(userReactions, false);
                changed |= ApplyReactionArray(groupReactions, true);
                if (changed) _ = SaveCachedConversationsAsync();
            });
        }

        private static bool ApplyReactionArray(JsonArray reactions, bool isGroup)
        {
            bool changed = false;
            if (reactions == null) return changed;
            foreach (IJsonValue value in reactions)
            {
                if (value.ValueType != JsonValueType.Object) continue;
                JsonObject reaction = value.GetObject();
                IJsonValue contentValue;
                JsonObject content = reaction.TryGetValue("content", out contentValue) && contentValue.ValueType == JsonValueType.Object
                    ? contentValue.GetObject() : contentValue != null && contentValue.ValueType == JsonValueType.String
                        ? ZaloJson.ParseObject(contentValue.GetString()) : null;
                if (content == null) continue;
                string sender = ZaloAuth.Str(reaction, "uidFrom");
                string threadId = isGroup || sender == "0" ? ZaloAuth.Str(reaction, "idTo") : sender;
                if (string.IsNullOrEmpty(threadId) || threadId == "0") continue;
                ZaloConversation conversation = Find((isGroup ? "g:" : "u:") + threadId);
                if (conversation == null) continue;
                string icon = ZaloAuth.Str(content, "rIcon");
                IJsonValue targetValue;
                JsonArray targets = content.TryGetValue("rMsg", out targetValue) && targetValue.ValueType == JsonValueType.Array
                    ? targetValue.GetArray() : null;
                if (targets == null && targetValue != null && targetValue.ValueType == JsonValueType.String)
                    JsonArray.TryParse(targetValue.GetString(), out targets);
                if (targets == null || icon.Length == 0) continue;
                foreach (IJsonValue targetItem in targets)
                {
                    if (targetItem.ValueType != JsonValueType.Object) continue;
                    JsonObject target = targetItem.GetObject();
                    string globalId = ZaloAuth.Str(target, "gMsgID"), clientId = ZaloAuth.Str(target, "cMsgID");
                    ZaloChatMessage message = conversation.Messages.FirstOrDefault(m =>
                        (!string.IsNullOrEmpty(globalId) && m.GlobalMessageId == globalId)
                        || (!string.IsNullOrEmpty(clientId) && m.ClientMessageId == clientId));
                    if (message == null) continue;
                    message.ReactionIcon = icon;
                    changed = true;
                }
            }
            return changed;
        }
        private static bool ApplyReceiptToMessage(ZaloConversation conversation, JsonObject receipt, int state, string avatarUrl)
        {
            string messageId = ZaloAuth.Str(receipt, "msgId");
            string realMessageId = ZaloAuth.Str(receipt, "realMsgId");
            if (messageId.Length == 0 && realMessageId.Length == 0) return false;
            ZaloChatMessage message = conversation.Messages.FirstOrDefault(item => item.FromMe
                && ((!string.IsNullOrEmpty(messageId) && (item.GlobalMessageId == messageId || item.Id == messageId))
                    || (!string.IsNullOrEmpty(realMessageId) && (item.GlobalMessageId == realMessageId || item.Id == realMessageId))));
            if (message == null) return false;
            int previousState = message.ReceiptState;
            string previousAvatar = message.DeliveryAvatarUrl;
            message.SetDeliveryReceipt(state, avatarUrl);
            return previousState != message.ReceiptState || previousAvatar != message.DeliveryAvatarUrl;
        }

        private static string AvatarForUser(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return string.Empty;
            string avatar;
            if (_friendAvatars.TryGetValue(userId, out avatar)) return avatar ?? string.Empty;
            ZaloFriend friend = Friends == null ? null : Friends.FirstOrDefault(item => item.UserId == userId);
            if (friend != null && !string.IsNullOrWhiteSpace(friend.Avatar)) return friend.Avatar;
            ZaloConversation conversation = Find("u:" + userId);
            return conversation == null ? string.Empty : conversation.AvatarUrl ?? string.Empty;
        }

        private static JsonArray ArrayValue(JsonObject source, string key)
        {
            IJsonValue value;
            return source != null && source.TryGetValue(key, out value) && value.ValueType == JsonValueType.Array
                ? value.GetArray() : null;
        }

        private static string[] StringArrayValue(JsonObject source, string key)
        {
            JsonArray array = ArrayValue(source, key);
            if (array == null) return new string[0];
            return array.Where(item => item.ValueType == JsonValueType.String)
                .Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
        }

        private static string FirstStringValue(JsonObject source, string key)
        {
            return StringArrayValue(source, key).FirstOrDefault() ?? string.Empty;
        }

        private static void OnGroupEvent(ZaloGroupEvent groupEvent)
        {
            if (groupEvent == null || string.IsNullOrWhiteSpace(groupEvent.GroupId) || _dispatcher == null) return;
            var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    string member = string.IsNullOrWhiteSpace(groupEvent.MemberNames) ? "Thành viên" : groupEvent.MemberNames;
                    string text;
                    switch (groupEvent.Act)
                    {
                        case "join": text = member + " đã tham gia nhóm"; break;
                        case "leave": text = member + " đã rời nhóm"; break;
                        case "remove_member": text = member + " đã bị xóa khỏi nhóm"; break;
                        case "block_member": text = member + " đã bị chặn khỏi nhóm"; break;
                        case "add_admin": text = member + " được bổ nhiệm làm phó nhóm"; break;
                        case "remove_admin": text = member + " không còn là phó nhóm"; break;
                        case "update": text = "Thông tin nhóm đã được cập nhật"; break;
                        case "update_setting": text = "Cài đặt nhóm đã được cập nhật"; break;
                        case "update_avatar": text = "Ảnh đại diện nhóm đã được thay đổi"; break;
                        case "new_link": text = "Liên kết mời tham gia nhóm đã được tạo"; break;
                        default: return;
                    }

                    ZaloConversation conversation = GetOrCreate(groupEvent.GroupId, true);
                    if (!string.IsNullOrWhiteSpace(groupEvent.GroupName) && groupEvent.Act == "update")
                        conversation.Name = groupEvent.GroupName;
                    string id = "group-event-" + (string.IsNullOrWhiteSpace(groupEvent.EventId)
                        ? groupEvent.Timestamp + "-" + groupEvent.Act + "-" + groupEvent.MemberNames
                        : groupEvent.EventId);
                    var message = new ZaloChatMessage
                    {
                        Id = id,
                        GlobalMessageId = id,
                        Sender = string.Empty,
                        Text = text,
                        MsgType = "system.group_event",
                        Ts = groupEvent.Timestamp
                    };
                    AddMessage(conversation, message, false);
                }
                catch (Exception ex) { ZaloLog.Add("Could not show group event: " + ex.Message); }
            });
        }

        public static ZaloConversation Find(string key)
        {
            ZaloConversation conversation;
            return key != null && ByKey.TryGetValue(key, out conversation) ? conversation : null;
        }

        public static ZaloConversation GetOrCreate(string threadId, bool isGroup, bool includeInConversationList = true)
        {
            string key = (isGroup ? "g:" : "u:") + threadId;
            ZaloConversation conversation;
            if (ByKey.TryGetValue(key, out conversation))
            {
                if (includeInConversationList && !Conversations.Contains(conversation)) Conversations.Add(conversation);
                if (isGroup && !Groups.Contains(conversation)) Groups.Add(conversation);
                return conversation;
            }

            string friendName;
            conversation = new ZaloConversation
            {
                Key = key,
                ThreadId = threadId,
                IsGroup = isGroup,
                IsPinned = PinnedConversationKeys.Contains(key),
                IsMarkedUnread = UnreadMarkedKeys.Contains(key),
                IsMuted = MutedConversationKeys.Contains(key),
                Name = isGroup
                    ? "Nhóm " + (threadId.Length > 4 ? threadId.Substring(threadId.Length - 4) : threadId)
                    : (_friendNames.TryGetValue(threadId, out friendName) ? friendName : threadId)
            };
            string avatar;
            if (!isGroup && _friendAvatars.TryGetValue(threadId, out avatar)) conversation.AvatarUrl = avatar;
            ByKey[key] = conversation;
            if (includeInConversationList) Conversations.Add(conversation);
            if (isGroup) Groups.Add(conversation);
            if (isGroup) PendingGroups.Add(threadId);
            return conversation;
        }

        private static async Task LoadGroupsAsync(ZaloSession session)
        {
            GroupsLoaded = false;
            GroupsLoadFailed = false;
            NotifyGroupsLoadChanged();
            try
            {
                List<string> ids;
                try { ids = await ZaloMessaging.GetGroupIdsAsync(session, CancellationToken.None); }
                catch (Exception ex)
                {
                    ids = new List<string>();
                    ZaloLog.Add("Group directory unavailable; using cached conversation groups: " + ex.Message);
                }
                foreach (ZaloConversation cachedGroup in Conversations.Where(item => item.IsGroup))
                    if (!ids.Contains(cachedGroup.ThreadId)) ids.Add(cachedGroup.ThreadId);
                Dictionary<string, JsonObject> details = ids.Count == 0
                    ? new Dictionary<string, JsonObject>(StringComparer.Ordinal)
                    : await ZaloMessaging.GetGroupInfoMapAsync(session, ids, CancellationToken.None);

                await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    var currentIds = new HashSet<string>(ids, StringComparer.Ordinal);
                    foreach (ZaloConversation stale in Groups.Where(g => !currentIds.Contains(g.ThreadId)).ToList())
                        Groups.Remove(stale);
                    foreach (string id in ids)
                    {
                        ZaloConversation group = GetOrCreate(id, true, false);
                        JsonObject info;
                        if (!details.TryGetValue(id, out info)) continue;
                        string name = ZaloAuth.Str(info, "name");
                        string avatar = ZaloAuth.Str(info, "fullAvt");
                        if (string.IsNullOrWhiteSpace(avatar)) avatar = ZaloAuth.Str(info, "avt");
                        if (string.IsNullOrWhiteSpace(avatar)) avatar = ZaloAuth.Str(info, "avatarUrl");
                        if (string.IsNullOrWhiteSpace(avatar)) avatar = ZaloAuth.Str(info, "avatar");
                        if (!string.IsNullOrWhiteSpace(name)) group.Name = name;
                        if (!string.IsNullOrWhiteSpace(avatar)) group.AvatarUrl = avatar;
                        int memberCount = (int)ZaloAuth.Num(info, "totalMember");
                        if (memberCount > 0) group.GroupMemberCount = memberCount;
                        UpdateGroupMessagePermission(group, info, session.Uid);
                    }
                    GroupsLoaded = true;
                    GroupsLoadFailed = false;
                    NotifyGroupsLoadChanged();
                });
                CoreDispatcher dispatcher = _dispatcher;
                if (dispatcher != null)
                    _ = dispatcher.RunAsync(CoreDispatcherPriority.Low, () => { var ignored = SaveCachedConversationsAsync(); });
                ZaloLog.Add("Groups loaded: " + ids.Count);
            }
            catch (Exception ex)
            {
                await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    GroupsLoaded = true;
                    GroupsLoadFailed = true;
                    NotifyGroupsLoadChanged();
                });
                ZaloLog.Add("Group list failed: " + ex.Message);
            }
        }

        private static void UpdateGroupMessagePermission(ZaloConversation group, JsonObject info, string userId)
        {
            if (group == null || info == null) return;
            IJsonValue settingValue;
            if (!info.TryGetValue("setting", out settingValue) || settingValue.ValueType != JsonValueType.Object)
                return;

            JsonObject setting = settingValue.GetObject();
            bool locked = ZaloAuth.Num(setting, "lockSendMsg") == 1;
            bool isAdmin = !string.IsNullOrWhiteSpace(userId)
                && string.Equals(ZaloAuth.Str(info, "creatorId"), userId, StringComparison.Ordinal);
            IJsonValue adminValue;
            if (!isAdmin && info.TryGetValue("adminIds", out adminValue) && adminValue.ValueType == JsonValueType.Array)
                isAdmin = adminValue.GetArray().Any(value => value.ValueType == JsonValueType.String
                    && string.Equals(value.GetString(), userId, StringComparison.Ordinal));
            group.UpdateGroupMessagePermission(true, locked, isAdmin);
        }

        private static void NotifyGroupsLoadChanged()
        {
            Action handler = GroupsLoadChanged;
            if (handler != null) handler();
        }

        public static async Task LoadHistoryAsync(ZaloConversation conversation, CancellationToken ct)
        {
            if (conversation == null || Session == null || conversation.ThreadId == Session.Uid) return;
            string key = conversation.Key;
            if (string.IsNullOrEmpty(key) || LoadedHistory.Contains(key) || !LoadingHistory.Add(key)) return;

            try
            {
                List<ZaloChatMessage> history = await ZaloMessaging.GetHistoryAsync(
                    Session, conversation.ThreadId, conversation.IsGroup, 50, ct);
                await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    conversation.HistoryLoadedCount = history.Count;
                    conversation.HasMoreHistory = conversation.IsGroup
                        ? history.Count >= 50
                        : history.Count > 0 || conversation.Messages.Count > 0;
                    if (history.Count > 0 && !Conversations.Contains(conversation))
                        Conversations.Add(conversation);
                    foreach (ZaloChatMessage recall in history.Where(m => m.IsRecallEvent))
                    {
                        ZaloChatMessage target = conversation.Messages.Concat(history.Where(m => !m.IsRecallEvent))
                            .FirstOrDefault(m => (!string.IsNullOrEmpty(recall.GlobalMessageId) && m.GlobalMessageId == recall.GlobalMessageId)
                                || (!string.IsNullOrEmpty(recall.ClientMessageId) && m.ClientMessageId == recall.ClientMessageId));
                        if (target != null) MarkRecalled(conversation, target);
                    }
                    foreach (ZaloChatMessage message in history.Where(m => !m.IsRecallEvent)) AddMessage(conversation, message, false);
                    if (conversation.Messages.Count > 0 && (string.IsNullOrWhiteSpace(conversation.LastText) || conversation.LastTs <= 0))
                    {
                        var latest = conversation.Messages.OrderBy(m => m.Ts).LastOrDefault();
                        if (latest != null) UpdateConversationPreviewFromMessage(conversation, latest);
                    }
                    LoadedHistory.Add(key);
                });
            }
            catch (Exception ex)
            {
                ZaloLog.Add("History load failed: " + ex.Message);
            }
            finally
            {
                LoadingHistory.Remove(key);
            }
        }

        public static async Task LoadOlderGroupHistoryAsync(ZaloConversation conversation, CancellationToken ct)
        {
            if (conversation == null || !conversation.IsGroup || Session == null || !conversation.HasMoreHistory) return;
            string key = conversation.Key;
            if (string.IsNullOrEmpty(key) || !LoadingHistory.Add(key)) return;
            int targetCount = Math.Min(500, Math.Max(50, conversation.HistoryLoadedCount) + 50);
            try
            {
                List<ZaloChatMessage> history = await ZaloMessaging.GetHistoryAsync(
                    Session, conversation.ThreadId, true, targetCount, ct);
                await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    foreach (ZaloChatMessage recall in history.Where(m => m.IsRecallEvent))
                    {
                        ZaloChatMessage target = conversation.Messages.Concat(history.Where(m => !m.IsRecallEvent))
                            .FirstOrDefault(m => (!string.IsNullOrEmpty(recall.GlobalMessageId) && m.GlobalMessageId == recall.GlobalMessageId)
                                || (!string.IsNullOrEmpty(recall.ClientMessageId) && m.ClientMessageId == recall.ClientMessageId));
                        if (target != null) MarkRecalled(conversation, target);
                    }
                    foreach (ZaloChatMessage message in history.Where(m => !m.IsRecallEvent)) AddMessage(conversation, message, false);
                    conversation.HistoryLoadedCount = targetCount;
                    conversation.HasMoreHistory = targetCount < 500 && history.Count >= targetCount;
                });
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Older group history load failed: " + ex.Message);
                throw;
            }
            finally
            {
                LoadingHistory.Remove(key);
            }
        }

        public static async Task<int> LoadOlderDirectHistoryAsync(ZaloConversation conversation, CancellationToken ct)
        {
            if (conversation == null || conversation.IsGroup || Session == null || !conversation.HasMoreHistory) return 0;
            if (Listener == null) throw new InvalidOperationException("Kết nối tin nhắn chưa sẵn sàng.");
            string key = conversation.Key;
            if (string.IsNullOrEmpty(key) || !LoadingHistory.Add(key)) return 0;
            try
            {
                // The protocol's cmd=510 cursor covers personal messages across conversations,
                // so continue from the oldest known personal message rather than this chat alone.
                ZaloChatMessage oldest = Conversations
                    .Where(item => !item.IsGroup)
                    .SelectMany(item => item.Messages)
                    .Where(message => !string.IsNullOrWhiteSpace(message.Id))
                    .OrderBy(message => message.Ts)
                    .FirstOrDefault();
                if (oldest == null) return 0;

                int received = await Listener.RequestOlderMessagesAsync(false, oldest.Id, ct);
                await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    foreach (ZaloConversation directConversation in Conversations.Where(item => !item.IsGroup))
                    {
                        directConversation.HistoryLoadedCount += received;
                        directConversation.HasMoreHistory = received > 0;
                    }
                });
                return received;
            }
            finally
            {
                LoadingHistory.Remove(key);
            }
        }

        public static async Task MarkConversationSeenAsync(ZaloConversation conversation)
        {
            if (conversation == null || Session == null) return;
            conversation.Unread = 0;
            bool changed = false;
            if (conversation.IsMarkedUnread)
            {
                try
                {
                    await ZaloMessaging.RemoveUnreadMarkAsync(Session, conversation, CancellationToken.None);
                    conversation.IsMarkedUnread = false;
                    UnreadMarkedKeys.Remove(conversation.Key);
                    changed = true;
                }
                catch (Exception ex) { ZaloLog.Add("Remove unread mark failed: " + ex.Message); }
            }
            Action statusHandler = StatusChanged;
            if (statusHandler != null) statusHandler();
            List<ZaloChatMessage> pending = conversation.Messages
                .Where(m => !m.FromMe && !m.SeenSent && m.CanSendSeen)
                .OrderBy(m => m.Ts).Take(50).ToList();
            if (pending.Count == 0)
            {
                if (changed || conversation.Unread == 0) await SaveCachedConversationsAsync();
                return;
            }
            foreach (ZaloChatMessage message in pending) message.SeenSent = true;
            try
            {
                await ZaloMessaging.MarkMessagesSeenAsync(Session, conversation, pending, CancellationToken.None);
                await SaveCachedConversationsAsync();
            }
            catch (Exception ex)
            {
                foreach (ZaloChatMessage message in pending) message.SeenSent = false;
                ZaloLog.Add("Seen event failed: " + ex.Message);
            }
        }

        private static void Handle(ZaloIncoming incoming)
        {
            JsonObject m = incoming.Msg;

            IJsonValue content;
            if (m.TryGetValue("content", out content) && content.ValueType == JsonValueType.Object
                && content.GetObject().ContainsKey("deleteMsg"))
            {
                HandleUndo(incoming, content.GetObject());
                return;
            }

            string uidFrom = ZaloAuth.Str(m, "uidFrom");
            string idTo = ZaloAuth.Str(m, "idTo");
            bool isSelf = uidFrom == "0" || (Session != null && !string.IsNullOrEmpty(Session.Uid) && uidFrom == Session.Uid);
            string threadId = incoming.IsGroup ? idTo : (isSelf ? idTo : uidFrom);
            if (threadId.Length == 0 || threadId == "0") return;

            long ts;
            if (!long.TryParse(ZaloAuth.Str(m, "ts"), out ts) || ts <= 0) ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            string senderName = isSelf ? "Tôi" : ZaloAuth.Str(m, "dName");
            if (senderName.Length == 0) senderName = "Zalo";

            ZaloConversation conversation = GetOrCreate(threadId, incoming.IsGroup);
            if (!incoming.IsGroup && !isSelf && !_friendNames.ContainsKey(threadId) && senderName != "Zalo")
                conversation.Name = senderName;

            var chatMessage = new ZaloChatMessage
            {
                Id = ZaloAuth.Str(m, "msgId"),
                GlobalMessageId = ZaloAuth.Str(m, "msgId"),
                ClientMessageId = ZaloAuth.Str(m, "cliMsgId"),
                SenderUid = uidFrom == "0" ? Session?.Uid : uidFrom,
                RecipientUid = idTo == "0" ? Session?.Uid : idTo,
                Sender = senderName,
                SenderAvatarBrush = ZaloAvatar.ColorFor(senderName),
                SenderAvatarUrl = !incoming.IsGroup && !isSelf ? conversation.AvatarUrl : string.Empty,
                Text = ZaloFormat.Describe(m),
                ReplyContent = ZaloFormat.PlainContent(m),
                MsgType = ZaloAuth.Str(m, "msgType"),
                StickerId = ZaloFormat.StickerId(m),
                FileName = ZaloFormat.FileName(m),
                MediaUrl = ZaloFormat.MediaUrl(m),
                MediaFullUrl = ZaloFormat.MediaFullUrl(m),
                FromMe = isSelf,
                ShowSender = incoming.IsGroup && !isSelf,
                Status = m.ContainsKey("st") ? 0 : -1,
                At = m.ContainsKey("at") ? 0 : -1,
                Command = m.ContainsKey("cmd") ? 0 : -1,
                Ts = ts
            };
            ZaloFormat.ReadQuote(m, chatMessage);
            ZaloFormat.ReadReplyPayload(m, chatMessage, incoming.IsGroup);
            ZaloFormat.ReadLinkPreview(m, chatMessage);
            ZaloFormat.ReadLocation(m, chatMessage);
            ZaloFormat.ReadPoll(m, chatMessage);
            ZaloFormat.ReadRecommendedLinkPreview(m, chatMessage);
            ZaloFormat.ReadContactCard(m, chatMessage);
            AddMessage(conversation, chatMessage, !incoming.IsOld && !isSelf);
            if (!incoming.IsOld) _ = ResolveLinkPreviewAsync(chatMessage);
            if (ReferenceEquals(conversation, CurrentChat) && !isSelf)
                _ = MarkConversationSeenAsync(conversation);
        }

        private static void HandleUndo(ZaloIncoming incoming, JsonObject undo)
        {
            JsonObject source = incoming == null ? null : incoming.Msg;
            if (source == null || undo == null) return;
            string fromUid = ZaloAuth.Str(source, "uidFrom");
            string toId = ZaloAuth.Str(source, "idTo");
            bool isSelf = fromUid == "0" || (Session != null && fromUid == Session.Uid);
            string threadId = incoming.IsGroup ? toId : (isSelf ? toId : fromUid);
            if (string.IsNullOrWhiteSpace(threadId) || threadId == "0") return;

            ZaloConversation conversation = Find((incoming.IsGroup ? "g:" : "u:") + threadId);
            if (conversation == null) return;
            string globalId = ZaloAuth.Str(undo, "globalMsgId");
            string clientId = ZaloAuth.Str(undo, "cliMsgId");
            ZaloChatMessage message = conversation.Messages.FirstOrDefault(m =>
                (!string.IsNullOrEmpty(globalId) && m.GlobalMessageId == globalId)
                || (!string.IsNullOrEmpty(clientId) && m.ClientMessageId == clientId));
            if (message == null) return;
            MarkRecalled(conversation, message);
            var ignored = SaveCachedConversationsAsync();
        }

        private static void MarkRecalled(ZaloConversation conversation, ZaloChatMessage message)
        {
            message.IsRecalled = true;
            message.Text = "Tin nhắn đã được thu hồi";
            message.MsgType = "chat.recalled";
            message.MediaUrl = string.Empty;
            message.FileName = string.Empty;
            message.StickerId = string.Empty;
            message.ReactionIcon = string.Empty;
            if (conversation.Messages.Count > 0 && ReferenceEquals(conversation.Messages[conversation.Messages.Count - 1], message))
                conversation.LastText = conversation.IsGroup && !message.FromMe
                    ? message.Sender + ": " + message.Text : message.Text;
        }

        private static void AddMessage(ZaloConversation conversation, ZaloChatMessage message, bool countUnread)
        {
            if (!string.IsNullOrEmpty(message.Id) && !conversation.SeenIds.Add(message.Id))
            {
                // History may enrich a lightweight message restored from disk. Preserve the
                // existing row/scroll position while replacing old generic contact placeholders
                // with the link/contact payload now available from the server.
                ZaloChatMessage existing = conversation.Messages.FirstOrDefault(item => item.Id == message.Id);
                if (existing != null)
                {
                    if (message.HasLinkPreview && !existing.HasLinkPreview)
                        existing.SetLinkPreview(message.LinkPreviewTitle, message.LinkPreviewDescription,
                            message.LinkPreviewUrl, message.LinkPreviewImageUrl);
                    if (message.HasContactCard && !existing.HasContactCard)
                        existing.SetContactCard(message.ContactUid, message.ContactName, message.ContactAvatarUrl,
                            message.ContactQrUrl, message.ContactPhone);
                    if (existing.Text == "[Danh thiếp]" && message.Text != "[Danh thiếp]")
                        existing.Text = message.Text;
                }
                if (string.IsNullOrWhiteSpace(conversation.LastText) || conversation.LastTs <= 0 || message.Ts >= conversation.LastTs)
                {
                    UpdateConversationPreviewFromMessage(conversation, message);
                }
                return;
            }

            int index = conversation.Messages.Count;
            while (index > 0 && conversation.Messages[index - 1].Ts > message.Ts) index--;
            conversation.Messages.Insert(index, message);

            if (message.Ts >= conversation.LastTs || string.IsNullOrWhiteSpace(conversation.LastText))
            {
                UpdateConversationPreviewFromMessage(conversation, message);
            }
            if (countUnread && !ReferenceEquals(conversation, CurrentChat)) conversation.Unread++;
            if (countUnread && !ReferenceEquals(conversation, CurrentChat) && !conversation.IsMuted && ZaloSettingsService.Current.IsSoundEnabled)
                ZaloAudioService.Current.PlayMessageSent();

            if (countUnread)
            {
                Action statusHandler = StatusChanged;
                if (statusHandler != null) statusHandler();
            }

            if (message.IsSticker && !string.IsNullOrEmpty(message.StickerId))
                _ = ResolveStickerUrlAsync(message);

            ScheduleBatchWork();
        }

        private static async Task ResolveStickerUrlAsync(ZaloChatMessage message)
        {
            string stickerId = message.StickerId;
            string cachedUrl;
            lock (StickerUrlLock)
            {
                if (StickerUrlCache.TryGetValue(stickerId, out cachedUrl))
                {
                    message.MediaUrl = cachedUrl;
                    return;
                }
                if (!LoadingStickerUrls.Add(stickerId)) return;
            }

            try
            {
                string imageUrl = await ZaloMessaging.GetStickerImageUrlAsync(Session, stickerId, CancellationToken.None);
                Uri uri;
                if (Uri.TryCreate(imageUrl, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                {
                    lock (StickerUrlLock) StickerUrlCache[stickerId] = imageUrl;
                    await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        foreach (ZaloConversation conversation in Conversations)
                            foreach (ZaloChatMessage sticker in conversation.Messages.Where(m => m.StickerId == stickerId))
                                sticker.MediaUrl = imageUrl;
                        message.MediaUrl = imageUrl;
                    });
                }
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Sticker image lookup failed: " + ex.Message);
            }
            finally
            {
                lock (StickerUrlLock) LoadingStickerUrls.Remove(stickerId);
            }
        }

        // Sorting the list and looking up group names is done once after a burst of messages.
        private static async void ScheduleBatchWork()
        {
            if (_batchScheduled) return;
            _batchScheduled = true;
            await Task.Delay(400);
            _batchScheduled = false;

            SortConversations();

            await ResolveGroupNamesAsync();
            var ignored = SaveCachedConversationsAsync();
        }

        private static async Task ResolveGroupNamesAsync()
        {
            if (_resolvingGroups || PendingGroups.Count == 0 || Session == null) return;
            _resolvingGroups = true;
            try
            {
                var ids = PendingGroups.ToList();
                PendingGroups.Clear();
                Dictionary<string, JsonObject> details = await ZaloMessaging.GetGroupInfoMapAsync(Session, ids, CancellationToken.None);
                foreach (KeyValuePair<string, JsonObject> pair in details)
                {
                    ZaloConversation conversation = Find("g:" + pair.Key);
                    if (conversation == null) continue;
                    string name = ZaloAuth.Str(pair.Value, "name");
                    string avatar = ZaloAuth.Str(pair.Value, "avt");
                    if (string.IsNullOrWhiteSpace(avatar)) avatar = ZaloAuth.Str(pair.Value, "fullAvt");
                    if (string.IsNullOrWhiteSpace(avatar)) avatar = ZaloAuth.Str(pair.Value, "avatar");
                    if (!string.IsNullOrWhiteSpace(name)) conversation.Name = name;
                    if (!string.IsNullOrWhiteSpace(avatar)) conversation.AvatarUrl = avatar;
                }
                CoreDispatcher dispatcher = _dispatcher;
                if (dispatcher != null)
                    _ = dispatcher.RunAsync(CoreDispatcherPriority.Low, () => { var ignored = SaveCachedConversationsAsync(); });
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Group names failed: " + ex.Message);
            }
            finally
            {
                _resolvingGroups = false;
            }
        }

        public static ZaloConversation OpenFriend(ZaloFriend friend)
        {
            ZaloConversation conversation = GetOrCreate(friend.UserId, false, false);
            if (friend.Name.Length > 0) conversation.Name = friend.Name;
            return conversation;
        }

        public static void ResolveRecentLinkPreviews(ZaloConversation conversation)
        {
            if (conversation == null) return;
            foreach (ZaloChatMessage message in conversation.Messages.Reverse()
                .Where(m => (!m.HasLinkPreview && ZaloFormat.FirstHttpUrl(m.Text).Length > 0)
                    || (m.MsgType == "chat.recommended" && m.HasLinkPreview && m.LinkPreviewImage == null)).Take(5))
                _ = ResolveLinkPreviewAsync(message);
        }

        private static async Task ResolveLinkPreviewAsync(ZaloChatMessage message)
        {
            if (message == null || Session == null || message.HasVoice || message.HasVideo || message.HasFile
                || message.HasMedia || message.MsgType == "chat.gif" || message.MsgType == "chat.photo"
                || message.MsgType == "chat.sticker" || message.MsgType == "5") return;
            string link = message.HasLinkPreview ? message.LinkPreviewUrl : ZaloFormat.FirstHttpUrl(message.Text);
            if (link.Length == 0) return;
            if (link.Contains("dlfl.vn") || link.Contains("zadn.vn")) return;
            if (message.HasLinkPreview && message.LinkPreviewImage != null
                && message.LinkPreviewTitle != "Cộng đồng Zalo" && message.LinkPreviewTitle != "Liên kết được chia sẻ") return;
            JsonObject preview;
            bool cached;
            lock (LinkPreviewLock)
            {
                cached = LinkPreviewCache.TryGetValue(link, out preview);
                if (!cached && !LoadingLinkPreviews.Add(link)) return;
            }

            if (cached)
            {
                await ApplyLinkPreviewAsync(message, preview);
                return;
            }

            try
            {
                preview = await ZaloMessaging.ParseLinkAsync(Session, link, CancellationToken.None);
                if (preview == null) return;
                string title = ZaloAuth.Str(preview, "title");
                string description = ZaloAuth.Str(preview, "desc");
                string href = ZaloAuth.Str(preview, "href");
                string thumb = ZaloAuth.Str(preview, "thumb");
                if (title.Length == 0 && href.Length == 0) return;
                var normalized = new JsonObject();
                normalized["title"] = JsonValue.CreateStringValue(title);
                normalized["desc"] = JsonValue.CreateStringValue(description);
                normalized["href"] = JsonValue.CreateStringValue(href.Length == 0 ? link : href);
                normalized["thumb"] = JsonValue.CreateStringValue(thumb);
                lock (LinkPreviewLock) LinkPreviewCache[link] = normalized;
                await ApplyLinkPreviewAsync(message, normalized);
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Link preview failed: " + ex.Message);
            }
            finally
            {
                lock (LinkPreviewLock) LoadingLinkPreviews.Remove(link);
            }
        }

        private static async Task ApplyLinkPreviewAsync(ZaloChatMessage message, JsonObject preview)
        {
            string title = ZaloAuth.Str(preview, "title");
            string description = ZaloAuth.Str(preview, "desc");
            string href = ZaloAuth.Str(preview, "href");
            string thumb = ZaloAuth.Str(preview, "thumb");
            Action apply = () => message.SetLinkPreview(title, description, href, thumb);
            CoreDispatcher dispatcher = _dispatcher;
            if (dispatcher != null && !dispatcher.HasThreadAccess)
                await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => apply());
            else apply();
        }

        private static void EnsureCanSendMessage(ZaloConversation conversation)
        {
            if (conversation == null || !conversation.IsGroup) return;
            if (!conversation.GroupSettingsLoaded)
                throw new InvalidOperationException("Chưa xác minh được quyền gửi tin nhắn của nhóm. Hãy mở lại nhóm và thử lại.");
            if (conversation.GroupPostingRestricted)
                throw new InvalidOperationException("Chỉ trưởng và phó nhóm được gửi tin nhắn trong nhóm.");
        }

        public static async Task SendAsync(ZaloConversation conversation, string text, ZaloChatMessage quote = null)
        {
            EnsureCanSendMessage(conversation);
            long localTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string localId = "pending_" + localTs;
            var outgoing = new ZaloChatMessage
            {
                Id = localId,
                GlobalMessageId = localId,
                ClientMessageId = localTs.ToString(),
                SenderUid = Session?.Uid,
                Sender = "Tôi",
                Text = text,
                ReplyContent = text,
                QuoteMessage = text,
                QuotedSender = quote == null ? string.Empty : quote.Sender,
                QuotedText = quote == null ? string.Empty : quote.Text,
                FromMe = true,
                Ts = localTs
            };
            if (!Conversations.Contains(conversation)) Conversations.Add(conversation);
            AddMessage(conversation, outgoing, false);

            try
            {
                ZaloMessaging.ZaloSentText sent = await ZaloMessaging.SendTextAsync(
                    Session, conversation.ThreadId, conversation.IsGroup, text, quote, CancellationToken.None);
                outgoing.Id = sent.MessageId;
                outgoing.GlobalMessageId = sent.MessageId;
                outgoing.ClientMessageId = sent.ClientMessageId;
                _ = ResolveLinkPreviewAsync(outgoing);
                if (ZaloSettingsService.Current.IsSoundEnabled) ZaloAudioService.Current.PlayMessageSent();
                var ignored = SaveCachedConversationsAsync();
            }
            catch (Exception)
            {
                throw;
            }
        }

        public static async Task SendContactCardAsync(ZaloConversation conversation, ZaloFriend contact)
        {
            if (conversation == null || contact == null) throw new InvalidOperationException("Chưa chọn cuộc trò chuyện hoặc liên hệ.");
            EnsureCanSendMessage(conversation);
            ZaloContactCardSendResult sent = await ZaloMessaging.SendContactCardAsync(
                Session, conversation, contact, CancellationToken.None);
            if (!Conversations.Contains(conversation)) Conversations.Add(conversation);
            var message = new ZaloChatMessage
            {
                Id = sent.MessageId,
                GlobalMessageId = sent.MessageId,
                ClientMessageId = sent.ClientMessageId,
                SenderUid = Session == null ? string.Empty : Session.Uid,
                Sender = "Tôi",
                MsgType = "chat.recommended",
                Text = "[Danh thiếp] " + contact.Name,
                FromMe = true,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            message.SetContactCard(contact.UserId, contact.Name, contact.Avatar, sent.QrCodeUrl, string.Empty);
            AddMessage(conversation, message, false);
            if (ZaloSettingsService.Current.IsSoundEnabled) ZaloAudioService.Current.PlayMessageSent();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SendImageAsync(ZaloConversation conversation, string fileName, IInputStream input,
            long totalSize, uint width, uint height, string caption)
        {
            EnsureCanSendMessage(conversation);
            JsonObject result = await ZaloMessaging.SendImageAsync(Session, conversation.ThreadId, conversation.IsGroup,
                fileName, input, totalSize, width, height, caption, CancellationToken.None);
            if (!Conversations.Contains(conversation)) Conversations.Add(conversation);
            AddMessage(conversation, new ZaloChatMessage
            {
                Id = ZaloAuth.Str(result, "msgId"),
                Sender = "Tôi",
                MsgType = "chat.photo",
                MediaUrl = ZaloAuth.Str(result, "normalUrl"),
                MediaFullUrl = string.IsNullOrWhiteSpace(ZaloAuth.Str(result, "hdUrl"))
                    ? ZaloAuth.Str(result, "normalUrl") : ZaloAuth.Str(result, "hdUrl"),
                Text = caption ?? string.Empty,
                FromMe = true,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, false);
            if (ZaloSettingsService.Current.IsSoundEnabled) ZaloAudioService.Current.PlayMessageSent();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SendGifAsync(ZaloConversation conversation, string fileName, IInputStream input,
            long totalSize, uint width, uint height, string previewUrl)
        {
            EnsureCanSendMessage(conversation);
            JsonObject result = await ZaloMessaging.SendGifAsync(Session, conversation.ThreadId, conversation.IsGroup,
                fileName, input, totalSize, width, height, CancellationToken.None);
            if (!Conversations.Contains(conversation)) Conversations.Add(conversation);
            string mediaUrl = string.IsNullOrWhiteSpace(previewUrl) ? ZaloAuth.Str(result, "previewUrl") : previewUrl;
            if (string.IsNullOrWhiteSpace(mediaUrl)) mediaUrl = "ms-appx:///Assets/Emoticons/" + fileName;
            AddMessage(conversation, new ZaloChatMessage
            {
                Id = ZaloAuth.Str(result, "msgId"),
                GlobalMessageId = ZaloAuth.Str(result, "msgId"),
                SenderUid = Session == null ? string.Empty : Session.Uid,
                Sender = "Tôi",
                MsgType = "chat.gif",
                Text = string.Empty,
                MediaUrl = mediaUrl,
                FromMe = true,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, false);
            if (ZaloSettingsService.Current.IsSoundEnabled) ZaloAudioService.Current.PlayMessageSent();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SendStickerAsync(ZaloConversation conversation, ZaloSticker sticker)
        {
            if (conversation == null || sticker == null) throw new InvalidOperationException("Chưa chọn cuộc trò chuyện hoặc nhãn dán.");
            EnsureCanSendMessage(conversation);
            ZaloStickerSendResult sent = await ZaloMessaging.SendStickerAsync(
                Session, conversation.ThreadId, conversation.IsGroup, sticker, CancellationToken.None);
            if (!Conversations.Contains(conversation)) Conversations.Add(conversation);
            AddMessage(conversation, new ZaloChatMessage
            {
                Id = sent.MessageId,
                GlobalMessageId = sent.MessageId,
                ClientMessageId = sent.ClientMessageId,
                SenderUid = Session == null ? string.Empty : Session.Uid,
                Sender = "Tôi",
                MsgType = "chat.sticker",
                StickerId = sticker.Id,
                MediaUrl = sticker.ImageUrl,
                FromMe = true,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, false);
            if (ZaloSettingsService.Current.IsSoundEnabled) ZaloAudioService.Current.PlayMessageSent();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SendVoiceAsync(ZaloConversation conversation, string fileName, IInputStream input, long totalSize)
        {
            if (conversation == null) throw new InvalidOperationException("Chưa chọn cuộc trò chuyện.");
            EnsureCanSendMessage(conversation);
            ZaloVoiceSendResult sent = await ZaloMessaging.SendVoiceAsync(
                Session, Listener, conversation.ThreadId, conversation.IsGroup, fileName, input, totalSize, CancellationToken.None);
            if (!Conversations.Contains(conversation)) Conversations.Add(conversation);
            AddMessage(conversation, new ZaloChatMessage
            {
                Id = sent.MessageId,
                GlobalMessageId = sent.MessageId,
                ClientMessageId = sent.ClientMessageId,
                SenderUid = Session == null ? string.Empty : Session.Uid,
                Sender = "Tôi",
                MsgType = "chat.voice",
                MediaUrl = sent.VoiceUrl,
                FromMe = true,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, false);
            if (ZaloSettingsService.Current.IsSoundEnabled) ZaloAudioService.Current.PlayMessageSent();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SendFileAsync(ZaloConversation conversation, string fileName, IInputStream input, long totalSize)
        {
            if (conversation == null) throw new InvalidOperationException("Chưa chọn cuộc trò chuyện.");
            EnsureCanSendMessage(conversation);
            ZaloFileSendResult sent = await ZaloMessaging.SendFileAsync(
                Session, Listener, conversation.ThreadId, conversation.IsGroup, fileName, input, totalSize, CancellationToken.None);
            if (!Conversations.Contains(conversation)) Conversations.Add(conversation);
            AddMessage(conversation, new ZaloChatMessage
            {
                Id = sent.MessageId,
                GlobalMessageId = sent.MessageId,
                ClientMessageId = sent.ClientMessageId,
                SenderUid = Session == null ? string.Empty : Session.Uid,
                Sender = "Tôi",
                MsgType = sent.MsgType,
                FileName = sent.FileName,
                MediaUrl = sent.FileUrl,
                Text = sent.MsgType == "chat.video.msg" ? "[Video]" : "[Tệp] " + sent.FileName,
                FromMe = true,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, false);
            if (ZaloSettingsService.Current.IsSoundEnabled) ZaloAudioService.Current.PlayMessageSent();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task ReactAsync(ZaloConversation conversation, ZaloChatMessage message,
            string icon, int reactionType)
        {
            await ZaloMessaging.AddReactionAsync(Session, conversation, message, icon, reactionType, CancellationToken.None);
            message.ReactionIcon = icon;
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SetPinnedAsync(ZaloConversation conversation, bool pinned)
        {
            await ZaloMessaging.SetPinnedConversationAsync(Session, conversation, pinned, CancellationToken.None);
            conversation.IsPinned = pinned;
            if (pinned) PinnedConversationKeys.Add(conversation.Key);
            else PinnedConversationKeys.Remove(conversation.Key);
            SortConversations();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SetArchivedAsync(ZaloConversation conversation, bool archived)
        {
            if (Session == null || conversation == null) throw new InvalidOperationException("Chưa có phiên Zalo.");
            await ZaloMessaging.SetArchivedConversationAsync(Session, conversation, archived, CancellationToken.None);
            if (archived) ArchivedConversationKeys.Add(conversation.Key);
            else ArchivedConversationKeys.Remove(conversation.Key);
            ZaloSettingsService.Current.SetArchivedConversationKeys(Session.Uid, ArchivedConversationKeys.ToList());
            Action statusHandler = StatusChanged;
            if (statusHandler != null) statusHandler();
            var ignored = SaveCachedConversationsAsync();
        }

        public static bool IsArchived(ZaloConversation conversation)
        {
            return conversation != null && ArchivedConversationKeys.Contains(conversation.Key);
        }

        public static async Task SetUnreadMarkAsync(ZaloConversation conversation, bool marked)
        {
            if (Session == null || conversation == null) throw new InvalidOperationException("Chưa có phiên Zalo.");
            if (marked)
                await ZaloMessaging.AddUnreadMarkAsync(Session, conversation, CancellationToken.None);
            else
                await ZaloMessaging.RemoveUnreadMarkAsync(Session, conversation, CancellationToken.None);
            conversation.IsMarkedUnread = marked;
            if (marked)
            {
                UnreadMarkedKeys.Add(conversation.Key);
            }
            else UnreadMarkedKeys.Remove(conversation.Key);
            Action statusHandler = StatusChanged;
            if (statusHandler != null) statusHandler();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SetMutedAsync(ZaloConversation conversation, bool muted)
        {
            if (Session == null || conversation == null) throw new InvalidOperationException("Chưa có phiên Zalo.");
            await ZaloAuth.SetConversationMuteAsync(Session, conversation.ThreadId, conversation.IsGroup, muted, CancellationToken.None);
            conversation.IsMuted = muted;
            if (muted) MutedConversationKeys.Add(conversation.Key);
            else MutedConversationKeys.Remove(conversation.Key);
            Action statusHandler = StatusChanged;
            if (statusHandler != null) statusHandler();
            var ignored = SaveCachedConversationsAsync();
        }

        public static bool IsHidden(ZaloConversation conversation)
        {
            return conversation != null && HiddenConversationKeys.Contains(conversation.Key);
        }

        public static async Task SetHiddenAsync(ZaloConversation conversation, bool hidden)
        {
            if (Session == null || conversation == null) throw new InvalidOperationException("Chưa có phiên Zalo.");
            await ZaloAuth.SetConversationHiddenAsync(Session, conversation.ThreadId, conversation.IsGroup,
                hidden, CancellationToken.None);
            if (hidden) HiddenConversationKeys.Add(conversation.Key);
            else HiddenConversationKeys.Remove(conversation.Key);
            Action statusHandler = StatusChanged;
            if (statusHandler != null) statusHandler();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SendTypingAsync(ZaloConversation conversation)
        {
            if (conversation == null || Session == null) return;
            DateTime now = DateTime.UtcNow;
            DateTime last;
            if (LastTypingSentUtc.TryGetValue(conversation.Key, out last)
                && now - last < TimeSpan.FromSeconds(2)) return;
            LastTypingSentUtc[conversation.Key] = now;
            try { await ZaloMessaging.SendTypingAsync(Session, conversation, CancellationToken.None); }
            catch (Exception ex) { ZaloLog.Add("Typing event failed: " + ex.Message); }
        }

        public static async Task DeleteConversationAsync(ZaloConversation conversation)
        {
            await ZaloMessaging.DeleteConversationAsync(Session, conversation, CancellationToken.None);
            Conversations.Remove(conversation);
            PinnedConversationKeys.Remove(conversation.Key);
            UnreadMarkedKeys.Remove(conversation.Key);
            conversation.IsMarkedUnread = false;
            Action statusHandler = StatusChanged;
            if (statusHandler != null) statusHandler();
            var ignored = SaveCachedConversationsAsync();
        }

        private static void SortConversations()
        {
            var sorted = Conversations.OrderByDescending(c => c.IsPinned).ThenByDescending(c => c.LastTs).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                int current = Conversations.IndexOf(sorted[i]);
                if (current >= 0 && current != i) Conversations.Move(current, i);
            }
        }

        public static async Task RecallMessageAsync(ZaloConversation conversation, ZaloChatMessage message)
        {
            await ZaloMessaging.UndoMessageAsync(Session, conversation, message, CancellationToken.None);
            MarkRecalled(conversation, message);
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task DeleteMessageForMeAsync(ZaloConversation conversation, ZaloChatMessage message)
        {
            await ZaloMessaging.DeleteMessageForMeAsync(Session, conversation, message, CancellationToken.None);
            conversation.Messages.Remove(message);
            if (!string.IsNullOrEmpty(message.Id)) conversation.SeenIds.Remove(message.Id);
            ZaloChatMessage latest = conversation.Messages.LastOrDefault();
            if (latest == null)
            {
                conversation.LastText = conversation.IsGroup ? string.Empty : "Nhấn để trò chuyện";
                conversation.LastTs = 0;
            }
            else
            {
                UpdateConversationPreviewFromMessage(conversation, latest);
            }
            var ignored = SaveCachedConversationsAsync();
        }
    }

}
