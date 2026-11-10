using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Newtonsoft.Json.Linq;
using ZaloUWP.Models;

namespace ZaloUWP.Core
{
    public class ZaloWebSocketClient : IDisposable
    {
        private static ZaloWebSocketClient _current;
        public static ZaloWebSocketClient Current => _current ?? (_current = new ZaloWebSocketClient());

        private MessageWebSocket _webSocket;
        private DataWriter _dataWriter;
        private Timer _pingTimer;
        private Timer _reconnectTimer;
        private bool _isConnected;
        private bool _isConnecting;
        private int _reconnectAttempts;

        public bool IsConnected => _isConnected;

        public event EventHandler<MessageItem> MessageReceived;
        public event EventHandler<string> TypingReceived;
        public event EventHandler<bool> ConnectionStateChanged;

        public ZaloWebSocketClient()
        {
            NetworkInformation.NetworkStatusChanged += NetworkInformation_NetworkStatusChanged;
        }

        private void NetworkInformation_NetworkStatusChanged(object sender)
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile != null && profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess)
                {
                    if (!_isConnected && !_isConnecting && ZaloSession.Current.IsLoggedIn)
                    {
                        var task = ConnectAsync();
                    }
                }
            }
            catch { }
        }

        public async Task ConnectAsync(string wsUrl = null)
        {
            if (_isConnected || _isConnecting) return;

            string targetUrl = wsUrl;
            if (string.IsNullOrEmpty(targetUrl))
            {
                targetUrl = ZaloSession.Current.WebSocketUrl;
            }
            if (string.IsNullOrEmpty(targetUrl))
            {
                // Chưa có WebSocket URL chính thức từ Zalo, không cố kết nối để tránh nghẽn socket / DNS timeout
                _isConnecting = false;
                return;
            }

            _isConnecting = true;

            try
            {
                _webSocket?.Dispose();
                _webSocket = new MessageWebSocket();
                _webSocket.Control.MessageType = SocketMessageType.Utf8;

                try { _webSocket.SetRequestHeader("User-Agent", ZaloConstants.UserAgent); } catch { }
                try { _webSocket.SetRequestHeader("Origin", ZaloConstants.WebOrigin); } catch { }

                try
                {
                    var cookieHeader = ZaloSession.Current.GetCookieHeader();
                    if (!string.IsNullOrEmpty(cookieHeader))
                    {
                        _webSocket.SetRequestHeader("Cookie", cookieHeader);
                    }
                }
                catch { }

                _webSocket.MessageReceived += WebSocket_MessageReceived;
                _webSocket.Closed += WebSocket_Closed;

                var uri = new Uri(targetUrl);
                await _webSocket.ConnectAsync(uri);

                _dataWriter = new DataWriter(_webSocket.OutputStream);
                _isConnected = true;
                _isConnecting = false;
                _reconnectAttempts = 0;
                _reconnectTimer?.Dispose();

                ConnectionStateChanged?.Invoke(this, true);

                StartPing();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebSocket] Connect failed: {ex.Message}");
                _isConnected = false;
                _isConnecting = false;
                ConnectionStateChanged?.Invoke(this, false);

                if (ZaloSession.Current.IsLoggedIn)
                {
                    ScheduleAutoReconnect();
                }
            }
        }

        private void ScheduleAutoReconnect()
        {
            _reconnectTimer?.Dispose();
            _reconnectAttempts++;
            var delayMs = Math.Min(_reconnectAttempts * 3000, 30000); // 3s, 6s, 9s... max 30s

            _reconnectTimer = new Timer(async (s) =>
            {
                if (!_isConnected && !_isConnecting && ZaloSession.Current.IsLoggedIn)
                {
                    await ConnectAsync();
                }
            }, null, delayMs, Timeout.Infinite);
        }

        private void StartPing()
        {
            _pingTimer?.Dispose();
            _pingTimer = new Timer(async (s) =>
            {
                await SendPingAsync();
            }, null, 25000, 25000);
        }

        public async Task SendPingAsync()
        {
            if (!_isConnected || _dataWriter == null) return;

            try
            {
                var pingMsg = "{\"cmd\":1,\"type\":\"ping\"}";
                _dataWriter.WriteString(pingMsg);
                await _dataWriter.StoreAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebSocket] Ping error: {ex.Message}");
            }
        }

        private void WebSocket_MessageReceived(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
        {
            try
            {
                using (var dr = args.GetDataReader())
                {
                    dr.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                    var str = dr.ReadString(dr.UnconsumedBufferLength);

                    ProcessIncomingMessage(str);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebSocket] Message read error: {ex.Message}");
            }
        }

        private void ProcessIncomingMessage(string rawMessage)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rawMessage)) return;

                if (!rawMessage.Trim().StartsWith("{") && !string.IsNullOrEmpty(ZaloSession.Current.ZpwEnk))
                {
                    rawMessage = ZaloCrypto.DecryptAesCbc(rawMessage.Trim(), ZaloSession.Current.ZpwEnk);
                }

                var json = JObject.Parse(rawMessage);
                var act = json["act"]?.ToString() ?? json["event"]?.ToString() ?? json["type"]?.ToString();

                if (act == "chat.message" || act == "group.message" || json["data"]?["msgId"] != null)
                {
                    var data = json["data"] ?? json;
                    var fromUid = data["uidFrom"]?.ToString() ?? data["from_uid"]?.ToString();
                    var isOutgoing = (fromUid == ZaloSession.Current.UserId);

                    var msg = new MessageItem
                    {
                        MsgId = data["msgId"]?.ToString() ?? Guid.NewGuid().ToString("N"),
                        CliMsgId = data["cliMsgId"]?.ToString(),
                        ConversationId = data["idTo"]?.ToString() ?? data["grid"]?.ToString() ?? fromUid,
                        FromUid = fromUid,
                        FromName = data["name"]?.ToString() ?? (isOutgoing ? "Tôi" : "Bạn bè"),
                        FromAvatar = data["avatar"]?.ToString(),
                        Text = data["message"]?.ToString() ?? data["text"]?.ToString() ?? "",
                        Timestamp = data["ts"]?.Value<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        IsOutgoing = isOutgoing,
                        Status = MessageDeliveryStatus.Delivered
                    };

                    MessageReceived?.Invoke(this, msg);
                }
                else if (act == "chat.typing" || act == "typing")
                {
                    var uid = json["data"]?["fromUid"]?.ToString() ?? json["uid"]?.ToString();
                    TypingReceived?.Invoke(this, uid);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebSocket] Parse incoming error: {ex.Message}");
            }
        }

        private void WebSocket_Closed(IWebSocket sender, WebSocketClosedEventArgs args)
        {
            _isConnected = false;
            ConnectionStateChanged?.Invoke(this, false);
            _pingTimer?.Dispose();

            if (ZaloSession.Current.IsLoggedIn)
            {
                ScheduleAutoReconnect();
            }
        }

        public void Disconnect()
        {
            try
            {
                _reconnectTimer?.Dispose();
                _pingTimer?.Dispose();
                _webSocket?.Dispose();
                _dataWriter?.Dispose();
            }
            catch { }
            finally
            {
                _isConnected = false;
                _isConnecting = false;
                _webSocket = null;
                _dataWriter = null;
                ConnectionStateChanged?.Invoke(this, false);
            }
        }

        public void Dispose()
        {
            Disconnect();
            NetworkInformation.NetworkStatusChanged -= NetworkInformation_NetworkStatusChanged;
        }
    }
}
