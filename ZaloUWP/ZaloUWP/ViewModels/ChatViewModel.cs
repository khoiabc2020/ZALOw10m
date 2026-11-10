using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ZaloUWP.Core;
using ZaloUWP.Database;
using ZaloUWP.Models;

namespace ZaloUWP.ViewModels
{
    public class ChatViewModel : ViewModelBase
    {
        private readonly ZaloApiClient _apiClient;
        private readonly ZaloDatabase _database;
        private readonly ZaloWebSocketClient _wsClient;

        private ConversationItem _currentConversation;
        public ConversationItem CurrentConversation
        {
            get => _currentConversation;
            set => SetProperty(ref _currentConversation, value);
        }

        public ObservableCollection<MessageItem> Messages { get; } = new ObservableCollection<MessageItem>();

        private string _draftText = "";
        public string DraftText
        {
            get => _draftText;
            set => SetProperty(ref _draftText, value);
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        private string _statusSubtitle = "";
        public string StatusSubtitle
        {
            get => _statusSubtitle;
            set => SetProperty(ref _statusSubtitle, value);
        }

        private Timer _typingTimer;

        public ICommand SendMessageCommand { get; }
        public ICommand RefreshMessagesCommand { get; }

        public event EventHandler MessageSent;

        public ChatViewModel()
        {
            _apiClient = ZaloApiClient.Current;
            _database = ZaloDatabase.Current;
            _wsClient = ZaloWebSocketClient.Current;

            SendMessageCommand = new RelayCommand(async () => await SendCurrentMessageAsync());
            RefreshMessagesCommand = new RelayCommand(async () => await LoadMessagesAsync(forceRefresh: true));

            _wsClient.MessageReceived += WsClient_MessageReceived;
            _wsClient.TypingReceived += WsClient_TypingReceived;
        }

        public async Task SetConversationAsync(ConversationItem conversation)
        {
            CurrentConversation = conversation;
            StatusSubtitle = conversation.IsOnline ? "Đang hoạt động" : (conversation.IsGroup ? "Nhóm trò chuyện" : "Ngoại tuyến");

            Messages.Clear();

            // 1. Instant load from local cache
            var cached = await _database.GetMessagesAsync(conversation.Id);
            if (cached.Count > 0)
            {
                foreach (var m in cached)
                {
                    Messages.Add(m);
                }
            }
            // 2. Refresh from server if session exists
            if (ZaloSession.Current.IsLoggedIn)
            {
                await LoadMessagesAsync(forceRefresh: false);
            }
        }

        public async Task LoadMessagesAsync(bool forceRefresh)
        {
            if (CurrentConversation == null) return;

            IsLoading = true;
            try
            {
                var serverMsgs = await _apiClient.GetMessagesAsync(CurrentConversation.Id, CurrentConversation.IsGroup);
                if (serverMsgs.Count > 0)
                {
                    Messages.Clear();
                    foreach (var m in serverMsgs)
                    {
                        Messages.Add(m);
                    }
                    await _database.SaveMessagesAsync(CurrentConversation.Id, serverMsgs);
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task SendCurrentMessageAsync()
        {
            var text = DraftText?.Trim();
            if (string.IsNullOrEmpty(text) || CurrentConversation == null)
                return;

            DraftText = "";

            var nowMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var localMsg = new MessageItem
            {
                MsgId = Guid.NewGuid().ToString("N"),
                ConversationId = CurrentConversation.Id,
                IsOutgoing = true,
                Text = text,
                Timestamp = nowMs,
                Status = MessageDeliveryStatus.Sent
            };

            Messages.Add(localMsg);
            CurrentConversation.LastMessage = text;
            CurrentConversation.LastMessageTimestamp = nowMs;
            MessageSent?.Invoke(this, EventArgs.Empty);

            await _database.SaveMessagesAsync(CurrentConversation.Id, Messages.ToList());

            if (ZaloSession.Current.IsLoggedIn)
            {
                var sentMsg = await _apiClient.SendMessageAsync(CurrentConversation.Id, CurrentConversation.IsGroup, text);
                if (sentMsg != null)
                {
                    localMsg.Status = MessageDeliveryStatus.Delivered;
                }
            }
        }

        public async Task SendStickerAsync(string stickerId)
        {
            if (CurrentConversation == null || string.IsNullOrEmpty(stickerId)) return;

            var nowMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var localMsg = new MessageItem
            {
                MsgId = Guid.NewGuid().ToString("N"),
                ConversationId = CurrentConversation.Id,
                IsOutgoing = true,
                Text = "[Sticker]",
                Timestamp = nowMs,
                MsgType = MessageType.Sticker,
                StickerId = stickerId,
                MediaUrl = string.Format(ZaloConstants.StickerCdnUrl, stickerId),
                Status = MessageDeliveryStatus.Sent
            };

            Messages.Add(localMsg);
            CurrentConversation.LastMessage = "[Sticker]";
            CurrentConversation.LastMessageTimestamp = nowMs;
            MessageSent?.Invoke(this, EventArgs.Empty);

            await _database.SaveMessagesAsync(CurrentConversation.Id, Messages.ToList());
        }

        public void SendTyping()
        {
        }

        public void Cleanup()
        {
            _typingTimer?.Dispose();
        }

        private void WsClient_MessageReceived(object sender, MessageItem msg)
        {
            if (CurrentConversation != null && msg.ConversationId == CurrentConversation.Id)
            {
                RunOnUIThread(() =>
                {
                    Messages.Add(msg);
                    MessageSent?.Invoke(this, EventArgs.Empty);
                });
            }
        }

        private void WsClient_TypingReceived(object sender, string convId)
        {
            if (CurrentConversation != null && convId == CurrentConversation.Id)
            {
                RunOnUIThread(() =>
                {
                    StatusSubtitle = "Đang soạn tin...";
                    Task.Delay(3000).ContinueWith(_ =>
                    {
                        RunOnUIThread(() =>
                        {
                            if (CurrentConversation != null)
                            {
                                StatusSubtitle = CurrentConversation.IsOnline ? "Đang hoạt động" : "Ngoại tuyến";
                            }
                        });
                    });
                });
            }
        }
    }
}
