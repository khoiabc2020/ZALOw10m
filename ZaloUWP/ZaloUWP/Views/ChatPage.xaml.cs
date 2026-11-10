using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Collections.Specialized;
using Windows.System;
using Windows.Data.Json;
using Windows.UI;
using Windows.UI.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;
using Windows.UI.Xaml.Navigation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;
using Windows.UI.ViewManagement;
using ZaloLite;
using ZaloUWP.Common;
using ZaloUWP.Services;

namespace ZaloUWP.Views
{
    public sealed partial class ChatPage : Page
    {
        private ZaloConversation _conversation;
        private string _targetMessageId;
        private bool _isSendingText;
        private ZaloChatMessage _replyTarget;
        private bool _emojiLoaded;
        private bool _localPickerAssetsLoaded;
        private readonly List<List<string>> _emojiCategories = new List<List<string>>();
        private readonly List<EmojiAsset> _zaloEmojiAssets = new List<EmojiAsset>();
        private readonly List<BundledGifAsset> _bundledGifAssets = new List<BundledGifAsset>();
        private bool _voiceStartPending;
        private bool _chatPageActive;
        private bool _historyRefreshStarted;

        private sealed class ForwardTarget
        {
            public string Id;
            public string Name;
            public bool IsGroup;
        }

        private sealed class EmojiAsset
        {
            public string Code { get; set; }
            public string ImageUri { get; set; }
        }

        private sealed class BundledGifAsset
        {
            public string FileName { get; set; }
            public string ImageUri { get; set; }
        }
        private bool _isRecordingVoice;
        private bool _isSendingVoice;
        private bool _loadingOlderHistory;
        private readonly DispatcherTimer _messageRefreshTimer;
        private readonly DispatcherTimer _keyboardScrollTimer;
        private bool _scrollAfterMessageRefresh;
        private readonly HashSet<ZaloChatMessage> _mediaLoads = new HashSet<ZaloChatMessage>();
        private readonly HashSet<ZaloChatMessage> _playbackFallbackAttempted = new HashSet<ZaloChatMessage>();
        private readonly Dictionary<ZaloChatMessage, StorageFile> _playbackFiles = new Dictionary<ZaloChatMessage, StorageFile>();
        private MediaElement _activePlaybackPlayer;
        private readonly HashSet<string> _senderAvatarLookupsAttempted = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _phoneLookupsAttempted = new HashSet<string>(StringComparer.Ordinal);
        private bool _senderAvatarResolveRunning;
        private bool _senderAvatarResolveRequested;
        private InputPane _inputPane;
        private bool _loadingGroupPermission;
        private bool _groupPermissionLoadFailed;

        public ChatPage()
        {
            try
            {
                this.InitializeComponent();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("ChatPage.InitializeComponent error: " + ex);
                _ = new Windows.UI.Popups.MessageDialog("Lỗi khởi tạo ChatPage: " + ex.Message, "Lỗi giao diện").ShowAsync();
                throw;
            }

            _messageRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _messageRefreshTimer.Tick += MessageRefreshTimer_Tick;
            _keyboardScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _keyboardScrollTimer.Tick += KeyboardScrollTimer_Tick;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _chatPageActive = true;

            try
            {
                _inputPane = InputPane.GetForCurrentView();
                _inputPane.Showing -= InputPane_Showing;
                _inputPane.Hiding -= InputPane_Hiding;
                _inputPane.Showing += InputPane_Showing;
                _inputPane.Hiding += InputPane_Hiding;
            }
            catch { }

            try
            {
                if (e.Parameter is Tuple<string, string> target)
                {
                    _conversation = ZaloApp.Find(target.Item1);
                    if (_conversation == null && !string.IsNullOrEmpty(target.Item1))
                    {
                        bool isGroup = target.Item1.StartsWith("g:");
                        string threadId = isGroup || target.Item1.StartsWith("u:") ? target.Item1.Substring(2) : target.Item1;
                        _conversation = ZaloApp.GetOrCreate(threadId, isGroup);
                    }
                    _targetMessageId = target.Item2;
                }
                else if (e.Parameter is ZaloConversation conv)
                {
                    _conversation = conv;
                }
                else if (e.Parameter is ZaloFriend friend)
                {
                    _conversation = ZaloApp.OpenFriend(friend);
                }
                else if (e.Parameter is string key)
                {
                    _conversation = ZaloApp.Find(key);
                    if (_conversation == null && !string.IsNullOrEmpty(key))
                    {
                        bool isGroup = key.StartsWith("g:");
                        string threadId = isGroup || key.StartsWith("u:") ? key.Substring(2) : key;
                        _conversation = ZaloApp.GetOrCreate(threadId, isGroup);
                    }
                }

                if (_conversation == null)
                {
                    _chatPageActive = false;
                    await new Windows.UI.Popups.MessageDialog("Không thể tìm thấy cuộc trò chuyện: " + (e.Parameter?.ToString() ?? "null"), "Lỗi mở hội thoại").ShowAsync();
                    if (Frame.CanGoBack) Frame.GoBack();
                    return;
                }

                ZaloApp.CurrentChat = _conversation;
                _conversation.Unread = 0;
                _groupPermissionLoadFailed = false;
                _historyRefreshStarted = false;
                ZaloApp.TypingChanged += OnTypingChanged;
                ZaloApp.FriendsLoadChanged += OnFriendsLoadChanged;
                ZaloApp.StatusChanged += OnAppStatusChanged;
                _conversation.PropertyChanged += Conversation_PropertyChanged;

                UpdateHeader();
                UpdateGroupPinnedPreview();
                if (_conversation.IsGroup) _ = LoadGroupPinnedPreviewAsync();
                UpdateGroupComposerState();
                if (_conversation.IsGroup && !_conversation.GroupSettingsLoaded) _ = LoadGroupPermissionAsync();
                ApplyWallpaper();
                UpdateComposerActions();

                MessagesListView.ItemsSource = _conversation.Messages;
                _conversation.Messages.CollectionChanged += Messages_CollectionChanged;
                UpdateDateHeaders();

                // Paint the locally cached messages immediately. Network history refresh runs
                // independently so opening a chat never waits for the server response.
                ScrollToBottom();
                if (!_conversation.IsGroup) _ = RefreshFriendSubtitleAsync(_conversation.ThreadId);
                string openedConversationKey = _conversation.Key;
                string targetMessageId = _targetMessageId;
                _targetMessageId = null;
                _pendingHistoryTargetMessageId = targetMessageId;
                _pendingHistoryConversationKey = openedConversationKey;
                StartHistoryRefreshIfReady();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("ChatPage.OnNavigatedTo error: " + ex);
                await new Windows.UI.Popups.MessageDialog("Lỗi mở cuộc trò chuyện: " + ex.Message, "Lỗi điều hướng").ShowAsync();
                if (Frame.CanGoBack) Frame.GoBack();
            }
        }

        private string _pendingHistoryTargetMessageId;
        private string _pendingHistoryConversationKey;

        private void OnAppStatusChanged()
        {
            if (Dispatcher == null) return;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, StartHistoryRefreshIfReady);
        }

        private void StartHistoryRefreshIfReady()
        {
            if (!_chatPageActive || _conversation == null || ZaloApp.Session == null || _historyRefreshStarted) return;
            _historyRefreshStarted = true;
            _ = RefreshHistoryAfterOpenAsync(
                _conversation, _pendingHistoryConversationKey, _pendingHistoryTargetMessageId);
        }

        private async System.Threading.Tasks.Task RefreshHistoryAfterOpenAsync(
            ZaloConversation conversation, string conversationKey, string targetMessageId)
        {
            try
            {
                await ZaloApp.LoadHistoryAsync(conversation, System.Threading.CancellationToken.None);
                if (!_chatPageActive || !ReferenceEquals(_conversation, conversation)
                    || !string.Equals(conversation.Key, conversationKey, StringComparison.Ordinal)) return;

                _ = ResolveSenderAvatarsAsync();
                ZaloApp.ResolveRecentLinkPreviews(conversation);
                _ = ResolvePhonePreviewsAsync(conversation);
                UpdateOlderHistoryButton();
                _ = ZaloApp.MarkConversationSeenAsync(conversation);
                ZaloChatMessage targetMessage = string.IsNullOrEmpty(targetMessageId)
                    ? null : conversation.Messages.FirstOrDefault(m => m.Id == targetMessageId);
                if (targetMessage != null) MessagesListView.ScrollIntoView(targetMessage);
                else ScrollToBottom();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Background chat history refresh failed: " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task RefreshFriendSubtitleAsync(string userId)
        {
            try
            {
                if (!ZaloApp.FriendsLoaded)
                {
                    UpdateHeader();
                    return;
                }
                if (_conversation != null && _conversation.IsTyping)
                {
                    UpdateHeader();
                    return;
                }
                if (IsCurrentChatStranger())
                {
                    UpdateHeader();
                    return;
                }
                if (ZaloApp.IsFriendOnline(userId))
                {
                    SubtitleTextBlock.Text = "Đang hoạt động";
                    SubtitleTextBlock.Visibility = Visibility.Visible;
                    return;
                }

                DateTimeOffset? lastOnline = await ZaloAuth.GetFriendLastOnlineAsync(
                    ZaloApp.Session, userId, System.Threading.CancellationToken.None);
                if (_conversation == null || _conversation.IsGroup || _conversation.ThreadId != userId) return;
                if (IsCurrentChatStranger())
                {
                    UpdateHeader();
                    return;
                }
                if (_conversation.IsTyping)
                {
                    UpdateHeader();
                    return;
                }

                // Presence may have refreshed while the last-seen request was in flight.
                if (ZaloApp.IsFriendOnline(userId))
                {
                    SubtitleTextBlock.Text = "Đang hoạt động";
                    SubtitleTextBlock.Visibility = Visibility.Visible;
                }
                else if (lastOnline.HasValue)
                {
                    SubtitleTextBlock.Text = FormatLastOnline(lastOnline.Value);
                    SubtitleTextBlock.Visibility = Visibility.Visible;
                }
                else
                {
                    SubtitleTextBlock.Text = string.Empty;
                    SubtitleTextBlock.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Last online unavailable: " + ex.Message);
                if (_conversation != null && _conversation.ThreadId == userId)
                {
                    UpdateHeader();
                }
            }
        }

        private bool IsCurrentChatStranger()
        {
            return _conversation != null && !_conversation.IsGroup && ZaloApp.FriendsLoaded
                && !ZaloApp.IsFriend(_conversation.ThreadId);
        }

        private static string FormatLastOnline(DateTimeOffset timestamp)
        {
            TimeSpan age = DateTimeOffset.Now - timestamp.ToLocalTime();
            if (age < TimeSpan.Zero) return string.Empty;
            if (age.TotalMinutes < 1) return "Truy cập vừa xong";
            if (age.TotalHours < 1) return "Truy cập " + Math.Max(1, (int)age.TotalMinutes) + " phút trước";
            if (age.TotalDays < 1) return "Truy cập " + Math.Max(1, (int)age.TotalHours) + " giờ trước";
            if (age.TotalDays < 7) return "Truy cập " + Math.Max(1, (int)age.TotalDays) + " ngày trước";
            return "Truy cập " + timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _chatPageActive = false;
            if (_isRecordingVoice || ZaloVoiceService.Current.IsRecording)
                _ = FinishVoiceRecordingAsync(false);
            StopActivePlayback();
            if (_inputPane != null)
            {
                _inputPane.Showing -= InputPane_Showing;
                _inputPane.Hiding -= InputPane_Hiding;
                _inputPane = null;
            }
            if (_conversation != null)
            {
                _conversation.PropertyChanged -= Conversation_PropertyChanged;
                _conversation.Messages.CollectionChanged -= Messages_CollectionChanged;
                _conversation.Unread = 0;
            }
            _messageRefreshTimer.Stop();
            _keyboardScrollTimer.Stop();
            _scrollAfterMessageRefresh = false;
            _ = CleanupPlaybackFilesAsync();
            ZaloApp.TypingChanged -= OnTypingChanged;
            ZaloApp.FriendsLoadChanged -= OnFriendsLoadChanged;
            ZaloApp.StatusChanged -= OnAppStatusChanged;
            ZaloApp.CurrentChat = null;
        }

        private void InputPane_Showing(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            // Reserve the keyboard-covered viewport at the bottom. This keeps the chat header
            // fixed while the message list shrinks above the keyboard instead of panning the page.
            double keyboardHeight = Math.Max(0, args.OccludedRect.Height);
            ChatRoot.Margin = new Thickness(0, 0, 0, keyboardHeight);
            QueueScrollAboveKeyboard();
            args.EnsuredFocusedElementInView = true;
        }

        private void MessageInputBox_GotFocus(object sender, RoutedEventArgs e)
        {
            QueueScrollAboveKeyboard();
        }

        private void QueueScrollAboveKeyboard()
        {
            if (!_chatPageActive || _conversation == null || _conversation.Messages.Count == 0) return;
            _keyboardScrollTimer.Stop();
            _keyboardScrollTimer.Start();
        }

        private void KeyboardScrollTimer_Tick(object sender, object e)
        {
            _keyboardScrollTimer.Stop();
            if (!_chatPageActive || _conversation == null || _conversation.Messages.Count == 0) return;
            ChatRoot.UpdateLayout();
            MessagesListView.UpdateLayout();
            ScrollToBottom();
        }

        private void InputPane_Hiding(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            ChatRoot.Margin = new Thickness(0);
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ChatRoot.UpdateLayout();
                MessagesListView.UpdateLayout();
            });
        }
        private void OnTypingChanged(ZaloConversation conversation)
        {
            if (ReferenceEquals(conversation, _conversation)) UpdateHeader();
        }

        private async void OnFriendsLoadChanged()
        {
            if (Dispatcher == null) return;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                UpdateHeader();
                if (_conversation != null && !_conversation.IsGroup && ZaloApp.IsFriend(_conversation.ThreadId))
                    _ = RefreshFriendSubtitleAsync(_conversation.ThreadId);
            });
        }

        private void Conversation_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "GroupSettingsLoaded" || e.PropertyName == "GroupPostingRestricted")
                UpdateGroupComposerState();
        }

        private void UpdateGroupComposerState()
        {
            if (ComposerBar == null || GroupPostingNotice == null) return;
            bool isGroup = _conversation != null && _conversation.IsGroup;
            bool permissionsKnown = isGroup && _conversation.GroupSettingsLoaded;
            bool restricted = permissionsKnown && _conversation.GroupPostingRestricted;
            bool waiting = isGroup && !permissionsKnown && !_groupPermissionLoadFailed;
            bool showNotice = waiting || restricted || (isGroup && _groupPermissionLoadFailed);

            ComposerBar.Visibility = showNotice ? Visibility.Collapsed : Visibility.Visible;
            GroupPostingNotice.Visibility = showNotice ? Visibility.Visible : Visibility.Collapsed;
            MessageInputBox.IsEnabled = !restricted && !waiting && !_groupPermissionLoadFailed;
            if (!showNotice) return;

            if (restricted)
            {
                GroupPostingNoticeText.Text = "Chỉ trưởng và phó nhóm được gửi tin nhắn trong nhóm.";
                GroupPostingNoticeAction.Content = "Thông tin";
                GroupPostingNoticeAction.Visibility = Visibility.Visible;
            }
            else if (_groupPermissionLoadFailed)
            {
                GroupPostingNoticeText.Text = "Không tải được quyền gửi tin nhắn của nhóm.";
                GroupPostingNoticeAction.Content = "Thử lại";
                GroupPostingNoticeAction.Visibility = Visibility.Visible;
            }
            else
            {
                GroupPostingNoticeText.Text = "Đang kiểm tra quyền gửi tin nhắn nhóm…";
                GroupPostingNoticeAction.Visibility = Visibility.Collapsed;
            }

            if (restricted || _groupPermissionLoadFailed)
            {
                if (_inputPane != null) _inputPane.TryHide();
            }
        }

        private async System.Threading.Tasks.Task LoadGroupPermissionAsync()
        {
            if (_loadingGroupPermission || _conversation == null || !_conversation.IsGroup) return;
            if (ZaloApp.Session == null)
            {
                _groupPermissionLoadFailed = true;
                UpdateGroupComposerState();
                return;
            }

            _loadingGroupPermission = true;
            _groupPermissionLoadFailed = false;
            UpdateGroupComposerState();
            ZaloConversation conversation = _conversation;
            try
            {
                JsonObject info = await ZaloMessaging.GetGroupInfoAsync(
                    ZaloApp.Session, conversation.ThreadId, System.Threading.CancellationToken.None);
                if (info == null) throw new InvalidOperationException("Không nhận được cài đặt nhóm.");
                IJsonValue settingsValue;
                if (!info.TryGetValue("setting", out settingsValue) || settingsValue.ValueType != JsonValueType.Object)
                    throw new InvalidOperationException("Máy chủ không gửi quyền nhắn tin của nhóm.");

                string ownId = ZaloApp.Session.Uid ?? string.Empty;
                bool isAdmin = string.Equals(ZaloAuth.Str(info, "creatorId"), ownId, StringComparison.Ordinal);
                IJsonValue adminValue;
                if (!isAdmin && info.TryGetValue("adminIds", out adminValue) && adminValue.ValueType == JsonValueType.Array)
                    isAdmin = adminValue.GetArray().Any(value => value.ValueType == JsonValueType.String
                        && string.Equals(value.GetString(), ownId, StringComparison.Ordinal));
                bool locked = ZaloAuth.Num(settingsValue.GetObject(), "lockSendMsg") == 1;
                if (!ReferenceEquals(_conversation, conversation)) return;

                conversation.UpdateGroupMessagePermission(true, locked, isAdmin);
                int memberCount = (int)ZaloAuth.Num(info, "totalMember");
                if (memberCount > 0) conversation.GroupMemberCount = memberCount;
                _ = ZaloApp.SaveCachedConversationsAsync();
                _groupPermissionLoadFailed = false;
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Group message permission lookup failed: " + ex.Message);
                if (ReferenceEquals(_conversation, conversation)) _groupPermissionLoadFailed = true;
            }
            finally
            {
                _loadingGroupPermission = false;
                UpdateGroupComposerState();
            }
        }

        private void GroupPostingNotice_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (_conversation != null && _conversation.GroupSettingsLoaded && _conversation.GroupPostingRestricted)
                Frame.Navigate(typeof(GroupInfoPage), _conversation.Key);
        }

        private void GroupPostingNoticeAction_Click(object sender, RoutedEventArgs e)
        {
            if (_groupPermissionLoadFailed)
            {
                _ = LoadGroupPermissionAsync();
                return;
            }
            if (_conversation != null && _conversation.GroupPostingRestricted)
                Frame.Navigate(typeof(GroupInfoPage), _conversation.Key);
        }

        private void UpdateHeader()
        {
            if (_conversation == null) return;
            TitleTextBlock.Text = _conversation.Name ?? "Trò chuyện";
            RecipientInitial.Text = _conversation.Initial ?? "?";
            RecipientColor.Fill = _conversation.AvatarColor;
            RecipientImage.Fill = _conversation.AvatarImageBrush;
            VoiceCallHeaderButton.Visibility = Visibility.Collapsed;
            VideoCallHeaderButton.Visibility = Visibility.Collapsed;
            GroupInfoHeaderButton.Visibility = _conversation.IsGroup ? Visibility.Visible : Visibility.Collapsed;
            GroupMoreHeaderButton.Visibility = _conversation.IsGroup ? Visibility.Visible : Visibility.Collapsed;
            InfoHeaderButton.Visibility = _conversation.IsGroup ? Visibility.Collapsed : Visibility.Visible;
            bool isStranger = !_conversation.IsGroup && ZaloApp.FriendsLoaded
                && !ZaloApp.IsFriend(_conversation.ThreadId);
            StrangerBanner.Visibility = isStranger ? Visibility.Visible : Visibility.Collapsed;
            StrangerActionText.Text = _conversation.FriendRequestSent ? "Đã gửi lời mời kết bạn" : "Kết bạn";
            StrangerBanner.IsHitTestVisible = isStranger && !_conversation.FriendRequestSent;
            if (_conversation.IsTyping)
            {
                SubtitleTextBlock.Text = _conversation.IsGroup ? "Có người đang nhập…" : "Đang nhập…";
                SubtitleTextBlock.Visibility = Visibility.Visible;
                return;
            }
            bool showSubtitle = _conversation.IsGroup || ZaloApp.IsFriendOnline(_conversation.ThreadId);
            SubtitleTextBlock.Text = _conversation.IsGroup
                ? (_conversation.GroupMemberCount > 0
                    ? "Nhóm · " + _conversation.GroupMemberCount + " thành viên"
                    : "Nhóm trò chuyện")
                : (isStranger ? "Người lạ" : (showSubtitle ? "Đang hoạt động" : string.Empty));
            SubtitleTextBlock.Visibility = showSubtitle ? Visibility.Visible : Visibility.Collapsed;
            if (isStranger) SubtitleTextBlock.Visibility = Visibility.Visible;
        }

        private async void AddStranger_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (_conversation == null || _conversation.IsGroup || _conversation.FriendRequestSent) return;
            if (ZaloApp.Session == null)
            {
                await new Windows.UI.Popups.MessageDialog("Vui lòng đăng nhập lại rồi thử kết bạn.", "Kết bạn").ShowAsync();
                return;
            }

            StrangerBanner.IsHitTestVisible = false;
            StrangerActionText.Text = "Đang gửi lời mời…";
            try
            {
                await ZaloAuth.SendFriendRequestAsync(ZaloApp.Session, _conversation.ThreadId,
                    "Xin chào, mình muốn kết bạn với bạn trên Zalo.", System.Threading.CancellationToken.None);
                _conversation.FriendRequestSent = true;
                UpdateHeader();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Stranger friend request failed: " + ex.Message);
                StrangerActionText.Text = "Kết bạn";
                StrangerBanner.IsHitTestVisible = true;
                await new Windows.UI.Popups.MessageDialog("Không gửi được lời mời kết bạn. Hãy thử lại sau.", "Kết bạn").ShowAsync();
            }
        }

        private void UpdateGroupPinnedPreview()
        {
            bool hasPreview = _conversation != null && _conversation.IsGroup
                && !string.IsNullOrWhiteSpace(_conversation.GroupPinnedPreview);
            GroupPinnedPreviewText.Text = hasPreview ? _conversation.GroupPinnedPreview : string.Empty;
            GroupPinnedPreviewPanel.Visibility = hasPreview ? Visibility.Visible : Visibility.Collapsed;
        }

        private async System.Threading.Tasks.Task LoadGroupPinnedPreviewAsync()
        {
            if (_conversation == null || !_conversation.IsGroup || ZaloApp.Session == null) return;
            if (DateTime.UtcNow - _conversation.GroupPinnedPreviewUpdatedUtc < TimeSpan.FromMinutes(5))
            {
                UpdateGroupPinnedPreview();
                return;
            }

            try
            {
                var entries = await ZaloMessaging.GetGroupBoardAsync(
                    ZaloApp.Session, _conversation.ThreadId, 1, 10, System.Threading.CancellationToken.None);
                var pinned = entries.FirstOrDefault(item => item != null && item.Icon == "📌");
                _conversation.GroupPinnedPreview = pinned == null ? string.Empty : pinned.Title;
                _conversation.GroupPinnedPreviewUpdatedUtc = DateTime.UtcNow;
                UpdateGroupPinnedPreview();
            }
            catch (Exception ex)
            {
                // Keep the message list responsive; pin preview is secondary and can be retried later.
                ZaloLog.Add("Group pinned preview unavailable: " + ex.Message);
            }
        }

        private void GroupPinnedPreview_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (_conversation != null && _conversation.IsGroup)
                Frame.Navigate(typeof(GroupInfoPage), _conversation.Key);
        }

        private void ApplyWallpaper()
        {
            try
            {
                var image = new BitmapImage { DecodePixelWidth = 480, DecodePixelHeight = 854 };
                image.UriSource = new Uri(ZaloSettingsService.Current.SelectedWallpaper);
                MessagesListView.Background = new ImageBrush
                {
                    ImageSource = image,
                    Stretch = Stretch.UniformToFill,
                    Opacity = 0.20
                };
            }
            catch
            {
                MessagesListView.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE4, 0xEB, 0xF3));
            }
        }

        private void Messages_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // History is inserted one row at a time. Coalesce the binding and scroll work so a
            // 50-message page does not rescan the entire conversation 50 times on the UI thread.
            if (!_loadingOlderHistory && e.Action == NotifyCollectionChangedAction.Add
                && e.NewStartingIndex >= 0 && _conversation != null
                && e.NewStartingIndex >= _conversation.Messages.Count - e.NewItems.Count)
                _scrollAfterMessageRefresh = true;
            _messageRefreshTimer.Stop();
            _messageRefreshTimer.Start();
            if (_conversation != null && _conversation.IsGroup)
            {
                _senderAvatarResolveRequested = true;
                _ = ResolveSenderAvatarsAsync();
            }
        }

        private async System.Threading.Tasks.Task ResolveSenderAvatarsAsync()
        {
            if (_senderAvatarResolveRunning)
            {
                _senderAvatarResolveRequested = true;
                return;
            }
            _senderAvatarResolveRunning = true;
            try
            {
                do
                {
                    _senderAvatarResolveRequested = false;
                    if (_conversation == null || ZaloApp.Session == null) return;
                    var incoming = _conversation.Messages
                        .Where(message => !message.FromMe && !string.IsNullOrWhiteSpace(message.SenderUid))
                        .ToList();
                    if (incoming.Count == 0) return;

                    if (!_conversation.IsGroup)
                    {
                        string avatar = _conversation.AvatarUrl;
                        if (!string.IsNullOrWhiteSpace(avatar))
                            foreach (ZaloChatMessage message in incoming)
                                if (string.IsNullOrWhiteSpace(message.SenderAvatarUrl)) message.SenderAvatarUrl = avatar;
                        continue;
                    }

                    var friendAvatars = (ZaloApp.Friends ?? new List<ZaloFriend>())
                        .Where(friend => !string.IsNullOrWhiteSpace(friend.UserId) && !string.IsNullOrWhiteSpace(friend.Avatar))
                        .GroupBy(friend => friend.UserId, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.First().Avatar, StringComparer.Ordinal);
                    foreach (ZaloChatMessage message in incoming)
                    {
                        if (!string.IsNullOrWhiteSpace(message.SenderAvatarUrl)) continue;
                        string avatar;
                        if (friendAvatars.TryGetValue(message.SenderUid, out avatar)) message.SenderAvatarUrl = avatar;
                    }

                    var missingIds = incoming.Where(message => string.IsNullOrWhiteSpace(message.SenderAvatarUrl))
                        .Select(message => message.SenderUid).Distinct(StringComparer.Ordinal)
                        .Where(id => !_senderAvatarLookupsAttempted.Contains(id)).Take(100).ToList();
                    if (missingIds.Count == 0) continue;

                    foreach (string id in missingIds) _senderAvatarLookupsAttempted.Add(id);
                    var profiles = await ZaloAuth.GetGroupMemberProfilesAsync(
                        ZaloApp.Session, missingIds, System.Threading.CancellationToken.None);
                    foreach (ZaloChatMessage message in incoming)
                    {
                        if (!string.IsNullOrWhiteSpace(message.SenderAvatarUrl)) continue;
                        ZaloFriend profile;
                        if (profiles.TryGetValue(message.SenderUid, out profile)
                            && !string.IsNullOrWhiteSpace(profile.Avatar)) message.SenderAvatarUrl = profile.Avatar;
                    }
                    if (profiles.Count > 0) await ZaloApp.SaveCachedConversationsAsync();
                }
                while (_senderAvatarResolveRequested);
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Group sender avatars unavailable: " + ex.Message);
            }
            finally
            {
                _senderAvatarResolveRunning = false;
                if (_senderAvatarResolveRequested) _ = ResolveSenderAvatarsAsync();
            }
        }

        private async System.Threading.Tasks.Task ResolvePhonePreviewsAsync(ZaloConversation conversation)
        {
            if (conversation == null || ZaloApp.Session == null) return;
            var numbers = conversation.Messages.Reverse()
                .Select(message => message.DetectedPhoneNumber)
                .Where(number => !string.IsNullOrWhiteSpace(number))
                .Distinct(StringComparer.Ordinal)
                .Where(number => !_phoneLookupsAttempted.Contains(number))
                .Take(5)
                .ToList();

            foreach (string number in numbers)
            {
                _phoneLookupsAttempted.Add(number);
                try
                {
                    ZaloFriend friend = await ZaloAuth.FindFriendByPhoneAsync(
                        ZaloApp.Session, number, System.Threading.CancellationToken.None);
                    if (!_chatPageActive || !ReferenceEquals(_conversation, conversation)) return;
                    foreach (ZaloChatMessage message in conversation.Messages)
                        if (string.Equals(message.DetectedPhoneNumber, number, StringComparison.Ordinal))
                        {
                            message.SetPhonePreview(friend, number);
                            if ((ZaloApp.Friends ?? new List<ZaloFriend>()).Any(item =>
                                string.Equals(item.UserId, friend.UserId, StringComparison.Ordinal)))
                                message.MarkPhonePreviewAlreadyFriend();
                        }
                }
                catch (Exception ex)
                {
                    // A phone number may not belong to a Zalo account; keep the chat usable.
                    ZaloLog.Add("Phone preview unavailable: " + ex.Message);
                }
            }
        }

        private async void PhoneCardAddFriend_Click(object sender, RoutedEventArgs e)
        {
            ZaloChatMessage message = (sender as FrameworkElement)?.DataContext as ZaloChatMessage;
            if (message == null || string.IsNullOrWhiteSpace(message.PhonePreviewUid)) return;
            bool alreadyFriend = (ZaloApp.Friends ?? new List<ZaloFriend>())
                .Any(friend => string.Equals(friend.UserId, message.PhonePreviewUid, StringComparison.Ordinal));
            if (alreadyFriend || message.PhonePreviewFriendAction == "Đã gửi lời mời") return;
            var button = sender as Button;
            if (button != null) button.IsEnabled = false;
            try
            {
                await ZaloAuth.SendFriendRequestAsync(ZaloApp.Session, message.PhonePreviewUid,
                    "Xin chào, mình muốn kết bạn với bạn trên Zalo.", System.Threading.CancellationToken.None);
                message.MarkPhoneFriendRequestSent();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Phone card friend request failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không gửi được lời mời kết bạn. Hãy thử lại sau.", "Kết bạn").ShowAsync();
            }
            finally
            {
                if (button != null) button.IsEnabled = true;
            }
        }

        private void PhoneCardMessage_Click(object sender, RoutedEventArgs e)
        {
            ZaloChatMessage message = (sender as FrameworkElement)?.DataContext as ZaloChatMessage;
            if (message == null || string.IsNullOrWhiteSpace(message.PhonePreviewUid)) return;
            var friend = new ZaloFriend
            {
                UserId = message.PhonePreviewUid,
                Name = message.PhonePreviewName,
                Avatar = message.PhonePreviewAvatarUrl
            };
            var conversation = ZaloApp.OpenFriend(friend);
            Frame.Navigate(typeof(ChatPage), conversation.Key);
        }

        private async void PhoneNumberCopy_Click(object sender, RoutedEventArgs e)
        {
            ZaloChatMessage message = (sender as FrameworkElement)?.DataContext as ZaloChatMessage;
            if (message == null || string.IsNullOrWhiteSpace(message.PhonePreviewNumber)) return;
            var data = new DataPackage();
            data.SetText(message.PhonePreviewNumber);
            Clipboard.SetContent(data);
            await new Windows.UI.Popups.MessageDialog("Đã sao chép số điện thoại.").ShowAsync();
        }

        private void MessageRefreshTimer_Tick(object sender, object e)
        {
            _messageRefreshTimer.Stop();
            if (_conversation == null) return;
            UpdateDateHeaders();
            UpdateHeader();
            if (_scrollAfterMessageRefresh && _conversation.Messages.Count > 0)
                MessagesListView.ScrollIntoView(_conversation.Messages[_conversation.Messages.Count - 1]);
            _scrollAfterMessageRefresh = false;
        }

        private void UpdateDateHeaders()
        {
            if (_conversation == null) return;
            DateTime? previousDate = null;
            DateTime today = DateTime.Now.Date;
            foreach (ZaloChatMessage message in _conversation.Messages)
            {
                DateTime messageDate;
                try
                {
                    messageDate = DateTimeOffset.FromUnixTimeMilliseconds(message.Ts).ToLocalTime().Date;
                }
                catch
                {
                    message.ShowDateHeader = false;
                    message.DateHeaderText = string.Empty;
                    continue;
                }

                bool showHeader = !previousDate.HasValue || previousDate.Value != messageDate;
                message.ShowDateHeader = showHeader;
                if (showHeader)
                {
                    if (messageDate == today) message.DateHeaderText = "Hôm nay";
                    else if (messageDate == today.AddDays(-1)) message.DateHeaderText = "Hôm qua";
                    else message.DateHeaderText = messageDate.ToString("dd/MM/yyyy");
                }
                previousDate = messageDate;
            }
        }

        private void UpdateOlderHistoryButton()
        {
            LoadOlderMessagesButton.Visibility = _conversation != null && _conversation.HasMoreHistory
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void LoadOlderMessages_Click(object sender, RoutedEventArgs e)
        {
            if (_conversation == null || _loadingOlderHistory) return;
            var anchor = _conversation.Messages.FirstOrDefault();
            _loadingOlderHistory = true;
            _scrollAfterMessageRefresh = false;
            LoadOlderMessagesButton.IsEnabled = false;
            LoadOlderMessagesButton.Content = "Đang tải…";
            try
            {
                if (_conversation.IsGroup)
                {
                    await ZaloApp.LoadOlderGroupHistoryAsync(_conversation, System.Threading.CancellationToken.None);
                }
                else
                {
                    int received = await ZaloApp.LoadOlderDirectHistoryAsync(_conversation, System.Threading.CancellationToken.None);
                    if (received == 0)
                    {
                        var dialog = new Windows.UI.Popups.MessageDialog("Máy chủ không còn trả về tin nhắn cũ hơn.", "Lịch sử trò chuyện");
                        await dialog.ShowAsync();
                    }
                }
                UpdateOlderHistoryButton();
                if (anchor != null) MessagesListView.ScrollIntoView(anchor);
            }
            catch
            {
                var dialog = new Windows.UI.Popups.MessageDialog("Không tải được tin cũ. Hãy thử lại sau.", "Lịch sử trò chuyện");
                await dialog.ShowAsync();
            }
            finally
            {
                _loadingOlderHistory = false;
                LoadOlderMessagesButton.IsEnabled = true;
                LoadOlderMessagesButton.Content = "Tải tin nhắn cũ hơn";
                UpdateOlderHistoryButton();
            }
        }

        private async void MessageImage_Failed(object sender, ExceptionRoutedEventArgs e)
        {
            var image = sender as Image;
            var message = image == null ? null : image.DataContext as ZaloChatMessage;
            if (message == null) return;
            if (!_mediaLoads.Add(message)) return;
            try
            {
                var session = ZaloApp.Session;
                if (session == null || message.MediaUri == null) throw new InvalidOperationException("No active session or media URL.");
                byte[] bytes = await session.Http.DownloadBytesAsync(message.MediaUri.ToString(), System.Threading.CancellationToken.None);
                using (var stream = new InMemoryRandomAccessStream())
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                    stream.Seek(0);
                    var bitmap = new BitmapImage { DecodePixelWidth = 520 };
                    await bitmap.SetSourceAsync(stream);
                    EnableAnimatedBitmap(bitmap);
                    message.SetDownloadedMediaImage(bitmap);
                }
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Chat image download failed: " + ex.Message);
                message.MarkMediaLoadFailed();
            }
            finally
            {
                _mediaLoads.Remove(message);
            }
        }

        private void MessageImage_Opened(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            var bitmap = image == null ? null : image.Source as BitmapImage;
            EnableAnimatedBitmap(bitmap);
        }

        private static void EnableAnimatedBitmap(BitmapImage bitmap)
        {
            // BitmapImage auto-plays animated GIFs by default on supported Windows versions.
            // Avoid linking newer optional playback APIs into the ARM NativeAOT closure.
        }

        private void RetryMessageImage_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as FrameworkElement;
            var message = button == null ? null : button.DataContext as ZaloChatMessage;
            if (message == null) return;
            message.RetryMediaLoad();
        }

        private void MessageImage_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var image = sender as Image;
            var message = image == null ? null : image.DataContext as ZaloChatMessage;
            if (message == null || string.IsNullOrWhiteSpace(message.MediaUrl)) return;
            Frame.Navigate(typeof(ImageViewerPage), message.ViewerUrl);
        }

        private async void FileMessage_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as FrameworkElement;
            var message = button == null ? null : button.DataContext as ZaloChatMessage;
            if (message == null || message.MediaUri == null || ZaloApp.Session == null) return;
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                SuggestedFileName = string.IsNullOrWhiteSpace(message.FileName)
                    ? "Zalo_attachment" : System.IO.Path.GetFileName(message.FileName)
            };
            string extension = System.IO.Path.GetExtension(picker.SuggestedFileName ?? string.Empty).ToLowerInvariant();
            if (extension.Length < 2 || extension.Length > 12
                || extension.Skip(1).Any(c => !char.IsLetterOrDigit(c))) extension = ".bin";
            picker.FileTypeChoices.Add("Tệp đính kèm", new List<string> { extension });
            StorageFile destination = await picker.PickSaveFileAsync();
            if (destination == null) return;

            Button actionButton = button as Button;
            if (actionButton != null) actionButton.IsEnabled = false;
            try
            {
                await ZaloApp.Session.Http.DownloadToFileAsync(message.MediaUri.ToString(), destination,
                    25UL * 1024UL * 1024UL, System.Threading.CancellationToken.None);
                bool opened = await Launcher.LaunchFileAsync(destination);
                if (!opened)
                    await new Windows.UI.Popups.MessageDialog(
                        "Đã tải tệp về " + destination.Name + ", nhưng thiết bị chưa có ứng dụng để mở định dạng này.",
                        "Tệp đã tải").ShowAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Attachment download failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog(
                    "Không tải được tệp. Hãy kiểm tra kết nối rồi thử lại. " + ex.Message,
                    "Tải tệp đính kèm").ShowAsync();
            }
            finally { if (actionButton != null) actionButton.IsEnabled = true; }
        }

        private async void LinkPreview_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var message = element == null ? null : element.DataContext as ZaloChatMessage;
            Uri target = message == null ? null : message.LinkPreviewUri;
            if (target == null) return;
            try { await Launcher.LaunchUriAsync(target); }
            catch (Exception ex) { ZaloLog.Add("Link launch failed: " + ex.Message); }
        }

        private async void LocationCard_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var message = element == null ? null : element.DataContext as ZaloChatMessage;
            if (message == null || !message.HasLocation) return;
            try { await message.OpenLocationAsync(); }
            catch (Exception ex) { ZaloLog.Add("Location launch failed: " + ex.Message); }
        }

        private void ContactCard_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var message = element == null ? null : element.DataContext as ZaloChatMessage;
            if (message == null || string.IsNullOrWhiteSpace(message.ContactUid)) return;
            Frame.Navigate(typeof(FriendProfilePage), new ZaloFriend
            {
                UserId = message.ContactUid,
                Name = message.ContactName,
                Avatar = message.ContactAvatarUrl
            });
        }

        private async void PollCard_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var message = element == null ? null : element.DataContext as ZaloChatMessage;
            if (message == null || message.PollId <= 0 || ZaloApp.Session == null) return;
            var button = sender as Button;
            if (button != null) button.IsEnabled = false;
            try
            {
                ZaloPollDetail poll = await ZaloMessaging.GetPollDetailAsync(
                    ZaloApp.Session, message.PollId, System.Threading.CancellationToken.None);
                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text = poll.VoteCount + " lượt bình chọn" + (poll.AllowMultiple ? " · được chọn nhiều đáp án" : string.Empty),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x75, 0x85, 0x96)),
                    TextWrapping = TextWrapping.Wrap
                });
                var choices = new List<FrameworkElement>();
                int totalOptionVotes = poll.Options.Sum(option => Math.Max(0, option.Votes));
                foreach (ZaloPollOption option in poll.Options)
                {
                    double votePercent = totalOptionVotes <= 0 ? 0 : (100.0 * Math.Max(0, option.Votes) / totalOptionVotes);
                    var optionContent = new StackPanel();
                    optionContent.Children.Add(new TextBlock
                    {
                        Text = option.Content ?? "Lựa chọn",
                        FontSize = 15,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                        TextWrapping = TextWrapping.Wrap
                    });
                    optionContent.Children.Add(new TextBlock
                    {
                        Text = option.Votes + " phiếu",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x75, 0x85, 0x96)),
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                    optionContent.Children.Add(new ProgressBar
                    {
                        Minimum = 0,
                        Maximum = 100,
                        Value = votePercent,
                        Height = 5,
                        Margin = new Thickness(0, 6, 0, 0),
                        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x18, 0xA0, 0xD9)),
                        Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0xEF, 0xF5))
                    });
                    FrameworkElement control = poll.AllowMultiple
                        ? (FrameworkElement)new CheckBox { Content = optionContent, IsChecked = option.Voted, Tag = option.OptionId, IsEnabled = !poll.Closed, Margin = new Thickness(0, 4, 0, 4) }
                        : new RadioButton { Content = optionContent, IsChecked = option.Voted, Tag = option.OptionId,
                            Margin = new Thickness(0, 4, 0, 4),
                            GroupName = "ZaloChatPoll_" + poll.PollId, IsEnabled = !poll.Closed };
                    choices.Add(control);
                    content.Children.Add(control);
                }
                if (poll.Closed)
                    content.Children.Add(new TextBlock { Text = "Bình chọn này đã đóng.", FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xB4, 0x23, 0x18)) });

                StackPanelSpacingCompat.Apply(content, 8);

                var dialog = new ContentDialog
                {
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                    Title = poll.Question,
                    Content = new ScrollViewer { Content = content, MaxHeight = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                    PrimaryButtonText = poll.Closed ? "Đóng" : "Gửi bình chọn",
                    SecondaryButtonText = poll.Closed ? null : "Hủy",
                    DefaultButton = ContentDialogButton.Primary,
                    FullSizeDesired = true,
                };
                ContentDialogResult result = await dialog.ShowAsync();
                if (poll.Closed || result != ContentDialogResult.Primary) return;

                var selected = new List<long>();
                foreach (FrameworkElement choice in choices)
                {
                    bool isChecked = choice is CheckBox ? ((CheckBox)choice).IsChecked == true
                        : choice is RadioButton && ((RadioButton)choice).IsChecked == true;
                    long optionId;
                    if (isChecked && choice.Tag != null && long.TryParse(choice.Tag.ToString(), out optionId)) selected.Add(optionId);
                }
                if (selected.Count == 0)
                {
                    await new Windows.UI.Popups.MessageDialog("Hãy chọn ít nhất một phương án.", "Bình chọn").ShowAsync();
                    return;
                }
                await ZaloMessaging.VotePollAsync(ZaloApp.Session, poll.PollId, selected,
                    System.Threading.CancellationToken.None);
                await new Windows.UI.Popups.MessageDialog("Đã gửi bình chọn.", "Bình chọn").ShowAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Chat poll failed: " + ex.Message);
                // Keep the server/API reason visible so an unsupported or expired poll
                // is distinguishable from a rendering or network failure.
                string reason = (ex.Message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (reason.Length > 240) reason = reason.Substring(0, 240) + "…";
                string text = "Không tải hoặc gửi được bình chọn. Hãy thử lại sau.";
                if (!string.IsNullOrWhiteSpace(reason)) text += "\n\nChi tiết: " + reason;
                await new Windows.UI.Popups.MessageDialog(text, "Bình chọn").ShowAsync();
            }
            finally { if (button != null) button.IsEnabled = true; }
        }

        private void PlayVideo_Click(object sender, RoutedEventArgs e)
        {
            StartPlayback(sender as FrameworkElement, true);
        }

        private void PlayVoice_Click(object sender, RoutedEventArgs e)
        {
            StartPlayback(sender as FrameworkElement, false);
        }

        private void StartPlayback(FrameworkElement source, bool video)
        {
            ZaloChatMessage message = source == null ? null : source.DataContext as ZaloChatMessage;
            if (message == null || message.MediaUri == null) return;
            var container = MessagesListView.ContainerFromItem(message);
            ContentControl host = FindPlaybackHost(container, video ? "video" : "voice");
            if (host == null) return;

            StopActivePlayback();
            if (!video)
            {
                StartVoicePlayback(host, message);
                return;
            }

            var player = new MediaElement
            {
                DataContext = message,
                AreTransportControlsEnabled = true,
                AutoPlay = false,
                Width = 240,
                Height = video ? 150 : 48,
                MaxWidth = 260
            };
            player.MediaFailed += VideoMedia_Failed;
            host.Content = player;
            _activePlaybackPlayer = player;
            player.Source = message.MediaUri;
        }

        private void StartVoicePlayback(ContentControl host, ZaloChatMessage message)
        {
            var player = new MediaElement
            {
                DataContext = message,
                AreTransportControlsEnabled = false,
                AutoPlay = false,
                Width = 1,
                Height = 1,
                Opacity = 0,
                IsHitTestVisible = false
            };
            var playGlyph = new TextBlock
            {
                Text = "\uE768",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 17,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            playGlyph.Text = "\uE768";

            var playTarget = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(20),
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x08, 0x7F, 0xF5)),
                Child = playGlyph,
                IsTapEnabled = true
            };
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(playTarget, "Phát hoặc tạm dừng tin nhắn thoại");
            var cardGrid = new Grid { Width = 224, MinHeight = 46 };
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(playTarget, 0);
            cardGrid.Children.Add(playTarget);

            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) };
            var waveform = new StackPanel { Orientation = Orientation.Horizontal, Height = 22, VerticalAlignment = VerticalAlignment.Center };
            int[] heights = { 8, 15, 11, 19, 13, 7, 17, 10, 20, 12, 8, 16, 10, 18, 7, 13 };
            foreach (int height in heights)
            {
                waveform.Children.Add(new Border
                {
                    Width = 3,
                    Height = height,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x8C, 0xA6, 0xBC)),
                    Margin = new Thickness(1, 0, 1, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            var stateText = new TextBlock
            {
                Text = "Tin nhắn thoại",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x77, 0x84, 0x93)),
                Margin = new Thickness(1, 1, 0, 0)
            };
            details.Children.Add(waveform);
            details.Children.Add(stateText);
            Grid.SetColumn(details, 1);
            cardGrid.Children.Add(details);

            var card = new Border
            {
                Background = new SolidColorBrush(message.FromMe
                    ? Color.FromArgb(0xFF, 0xE2, 0xF3, 0xFF)
                    : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xDD, 0xE6, 0xEF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(9, 7, 9, 7),
                Child = cardGrid
            };
            var playbackRoot = new Grid { Width = 244, Height = 60 };
            playbackRoot.Children.Add(card);
            playbackRoot.Children.Add(player);
            host.Content = playbackRoot;
            _activePlaybackPlayer = player;

            Action updateState = () =>
            {
                bool playing = player.CurrentState == MediaElementState.Playing;
                playGlyph.Text = playing ? "\uE769" : "\uE768";
                stateText.Text = playing ? "Đang phát tin nhắn thoại" : "Tin nhắn thoại";
            };
            playTarget.Tapped += (sender, args) =>
            {
                if (player.CurrentState == MediaElementState.Playing) player.Pause();
                else player.Play();
                updateState();
            };
            player.CurrentStateChanged += (sender, args) => updateState();
            player.MediaOpened += (sender, args) =>
            {
                player.Play();
                updateState();
            };
            player.MediaEnded += (sender, args) =>
            {
                try { player.Position = TimeSpan.Zero; } catch { }
                updateState();
            };
            player.MediaFailed += VoiceMedia_Failed;
            player.Source = message.MediaUri;
        }

        private void StopActivePlayback()
        {
            if (_activePlaybackPlayer == null) return;
            try { _activePlaybackPlayer.Stop(); _activePlaybackPlayer.Source = null; } catch { }
            _activePlaybackPlayer = null;
        }

        private ContentControl FindPlaybackHost(Windows.UI.Xaml.DependencyObject parent, string kind)
        {
            if (parent == null) return null;
            int children = Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < children; i++)
            {
                var child = Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                var host = child as ContentControl;
                if (host != null && string.Equals(host.Tag as string, kind, StringComparison.Ordinal)) return host;
                var nested = FindPlaybackHost(child, kind);
                if (nested != null) return nested;
            }
            return null;
        }

        private async void VideoMedia_Failed(object sender, ExceptionRoutedEventArgs e)
        {
            await RecoverPlayableMediaAsync(sender as MediaElement, true);
        }

        private async void VoiceMedia_Failed(object sender, ExceptionRoutedEventArgs e)
        {
            await RecoverPlayableMediaAsync(sender as MediaElement, false);
        }

        private void RetryPlayback_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as FrameworkElement;
            var message = button == null ? null : button.DataContext as ZaloChatMessage;
            if (message == null) return;
            _playbackFallbackAttempted.Remove(message);
            message.ResetPlaybackFailure();
            var container = MessagesListView.ContainerFromItem(message);
            var player = FindPlaybackElement(container, message, message.HasVideo);
            if (player != null)
            {
                player.Source = null;
                StorageFile oldFile;
                if (_playbackFiles.TryGetValue(message, out oldFile))
                {
                    _playbackFiles.Remove(message);
                    _ = oldFile.DeleteAsync();
                }
                player.Source = message.MediaUri;
            }
        }

        private MediaElement FindPlaybackElement(Windows.UI.Xaml.DependencyObject parent, ZaloChatMessage message, bool video)
        {
            if (parent == null) return null;
            int children = Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < children; i++)
            {
                var child = Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                var media = child as MediaElement;
                if (media != null && ReferenceEquals(media.DataContext, message)
                    && (video ? media.Height > 80 : media.Height > 0 && media.Height <= 80)) return media;
                var nested = FindPlaybackElement(child, message, video);
                if (nested != null) return nested;
            }
            return null;
        }

        private async System.Threading.Tasks.Task RecoverPlayableMediaAsync(MediaElement media, bool video)
        {
            ZaloChatMessage message = media == null ? null : media.DataContext as ZaloChatMessage;
            if (message == null) return;
            if (!_playbackFallbackAttempted.Add(message))
            {
                message.SetPlaybackFailure(video);
                return;
            }

            StorageFile localFile = null;
            try
            {
                ZaloSession session = ZaloApp.Session;
                string source = message.MediaUrl;
                if (session == null || string.IsNullOrWhiteSpace(source))
                    throw new InvalidOperationException("Thiếu phiên đăng nhập hoặc URL media.");

                string extension = video ? ".mp4" : ".m4a";
                Uri sourceUri;
                if (Uri.TryCreate(source, UriKind.Absolute, out sourceUri))
                {
                    string candidate = System.IO.Path.GetExtension(sourceUri.AbsolutePath).ToLowerInvariant();
                    string[] allowed = video
                        ? new[] { ".mp4", ".3gp", ".mov", ".webm" }
                        : new[] { ".m4a", ".mp3", ".aac", ".wav", ".ogg", ".amr" };
                    if (allowed.Contains(candidate)) extension = candidate;
                }

                string fileName = "zalo_playback_" + Guid.NewGuid().ToString("N") + extension;
                localFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                    fileName, CreationCollisionOption.ReplaceExisting);
                await session.Http.DownloadToFileAsync(source, localFile, 25UL * 1024UL * 1024UL,
                    System.Threading.CancellationToken.None);
                _playbackFiles[message] = localFile;
                media.Source = new Uri("ms-appdata:///temp/" + fileName);
            }
            catch (Exception ex)
            {
                ZaloLog.Add((video ? "Video" : "Voice") + " playback recovery failed: " + ex.Message);
                if (localFile != null) try { await localFile.DeleteAsync(); } catch { }
                message.SetPlaybackFailure(video);
            }
        }

        private async System.Threading.Tasks.Task CleanupPlaybackFilesAsync()
        {
            // Let Frame.GoBack paint the cached conversation list before walking the
            // chat visual tree or deleting temporary media files.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => { });
            foreach (var pair in _playbackFiles.ToList())
            {
                var container = MessagesListView.ContainerFromItem(pair.Key);
                StopPlaybackElements(container, pair.Key);
                try { await pair.Value.DeleteAsync(); } catch { }
            }
            _playbackFiles.Clear();
        }

        private void StopPlaybackElements(Windows.UI.Xaml.DependencyObject parent, ZaloChatMessage message)
        {
            if (parent == null) return;
            int children = Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < children; i++)
            {
                var child = Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                var media = child as MediaElement;
                if (media != null && ReferenceEquals(media.DataContext, message))
                {
                    try { media.Stop(); media.Source = null; } catch { }
                }
                else StopPlaybackElements(child, message);
            }
        }

        private void Message_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e)
        {
            ShowMessageActions(sender as FrameworkElement);
            e.Handled = true;
        }

        private async void Message_DoubleTapped(object sender, Windows.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            FrameworkElement bubble = sender as FrameworkElement;
            ZaloChatMessage message = bubble == null ? null : bubble.DataContext as ZaloChatMessage;
            if (message == null || !message.CanReact || _conversation == null) return;

            // A double tap on nested media/action controls keeps its normal meaning (play/open).
            Windows.UI.Xaml.DependencyObject source = e.OriginalSource as Windows.UI.Xaml.DependencyObject;
            while (source != null && !ReferenceEquals(source, bubble))
            {
                if (source is Windows.UI.Xaml.Controls.Primitives.ButtonBase
                    || source is Image || source is MediaElement || source is Slider)
                    return;
                source = Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
            }

            e.Handled = true;
            await SendReactionAsync(message, "/-heart", 5);
        }

        private void Message_RightTapped(object sender, Windows.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            ShowMessageActions(sender as FrameworkElement);
            e.Handled = true;
        }

        private void ShowMessageActions(FrameworkElement target)
        {
            ZaloChatMessage message = target == null ? null : target.DataContext as ZaloChatMessage;
            if (message == null) return;
            var flyout = new Flyout { Placement = Windows.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
            var shell = new Border { Width = 354, MaxWidth = 390, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)), CornerRadius = new CornerRadius(18), Padding = new Thickness(12), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 230, 234, 240)), BorderThickness = new Thickness(1) };
            var panel = new StackPanel();
            var preview = new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 245, 247, 250)), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8) };
            preview.Child = new TextBlock { Text = string.IsNullOrWhiteSpace(message.Text) ? "Tin nhắn" : message.Text, FontSize = 15, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 34, 41, 53)), TextWrapping = TextWrapping.Wrap, MaxHeight = 72, TextTrimming = TextTrimming.CharacterEllipsis };
            panel.Children.Add(preview);
            if (message.CanReact)
            {
                var reactions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                panel.Children.Add(new Border { Background = new SolidColorBrush(Windows.UI.Colors.White), CornerRadius = new CornerRadius(16), Padding = new Thickness(3), Margin = new Thickness(0, 0, 0, 8), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 238, 240, 243)), BorderThickness = new Thickness(1), Child = reactions });
                string[,] choices = { { "❤️", "/-heart", "5" }, { "👍", "/-strong", "3" }, { "😆", ":>", "0" }, { "😮", ":o", "32" }, { "😭", ":-((", "2" }, { "😡", ":-h", "20" } };
                for (int i = 0; i < choices.GetLength(0); i++)
                {
                    string emoji = choices[i, 0], icon = choices[i, 1]; int type = int.Parse(choices[i, 2]);
                    var button = new Button { Content = emoji, Width = 49, Height = 46, Padding = new Thickness(0), FontSize = 24, Background = new SolidColorBrush(Windows.UI.Colors.Transparent), BorderThickness = new Thickness(0), UseSystemFocusVisuals = false };
                    button.Click += async (s, e) => { flyout.Hide(); await SendReactionAsync(message, icon, type); };
                    reactions.Children.Add(button);
                }
            }
            var actions = new Grid();
            for (int i = 0; i < 4; i++) actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Action<string, string, Action> addAction = (glyph, label, invoke) =>
            {
                int index = actions.Children.Count, row = index / 4;
                while (actions.RowDefinitions.Count <= row) actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                stack.Children.Add(new TextBlock { Text = glyph, FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 21, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 121, 217)), HorizontalAlignment = HorizontalAlignment.Center });
                stack.Children.Add(new TextBlock { Text = label, FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 54, 61, 70)) });
                var button = new Button { Content = stack, Background = new SolidColorBrush(Windows.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(2, 8, 2, 8), UseSystemFocusVisuals = false };
                Grid.SetColumn(button, index % 4); Grid.SetRow(button, row); button.Click += (s, e) => { flyout.Hide(); invoke(); }; actions.Children.Add(button);
            };
            if (message.CanReply) addAction("\uE8CA", "Trả lời", () => BeginReply(message));
            if (message.HasText)
            {
                addAction("\uE8C8", "Sao chép", () => { var package = new DataPackage(); package.SetText(message.Text ?? string.Empty); Clipboard.SetContent(package); });
                addAction("\uE72A", "Chuyển tiếp", () => { var ignored = ForwardMessageAsync(message); });
            }
            if (message.CanRecall) addAction("\uE777", "Thu hồi", () => { var ignored = RecallMessageAsync(message); });
            if (message.CanDeleteForMe) addAction("\uE74D", "Xóa ở tôi", () => { var ignored = DeleteMessageForMeAsync(message); });
            panel.Children.Add(actions); shell.Child = panel; flyout.Content = shell; flyout.ShowAt(target);
        }
        private async System.Threading.Tasks.Task ForwardMessageAsync(ZaloChatMessage message)
        {
            var targets = new List<ForwardTarget>();
            if (ZaloApp.Friends != null)
                targets.AddRange(ZaloApp.Friends.Where(f => f != null && !string.IsNullOrWhiteSpace(f.UserId))
                    .Select(f => new ForwardTarget { Id = f.UserId, Name = f.Name ?? f.UserId, IsGroup = false }));
            if (ZaloApp.Groups != null)
                targets.AddRange(ZaloApp.Groups.Where(g => g != null && !string.IsNullOrWhiteSpace(g.ThreadId))
                    .Select(g => new ForwardTarget { Id = g.ThreadId, Name = g.Name ?? g.ThreadId, IsGroup = true }));

            targets = targets.Where(t => !(t.IsGroup == _conversation.IsGroup && t.Id == _conversation.ThreadId))
                .GroupBy(t => (t.IsGroup ? "g:" : "u:") + t.Id).Select(g => g.First())
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (targets.Count == 0)
            {
                await new Windows.UI.Popups.MessageDialog("Chưa có danh bạ hoặc nhóm để chuyển tiếp.", "Chuyển tiếp").ShowAsync();
                return;
            }

            var search = new TextBox { PlaceholderText = "Tìm bạn bè hoặc nhóm", Margin = new Thickness(0, 0, 0, 8) };
            var list = new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = 380 };
            var checkBoxes = new List<CheckBox>();
            foreach (ForwardTarget target in targets)
            {
                var check = new CheckBox { Content = target.Name + (target.IsGroup ? "  ·  Nhóm" : ""), Tag = target, Margin = new Thickness(4, 2, 4, 2) };
                checkBoxes.Add(check);
                list.Items.Add(check);
            }
            search.TextChanged += (s, e) =>
            {
                string query = (search.Text ?? string.Empty).Trim();
                foreach (CheckBox check in checkBoxes)
                {
                    ForwardTarget target = check.Tag as ForwardTarget;
                    check.Visibility = query.Length == 0 || target.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0
                        || target.Id.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0
                        ? Visibility.Visible : Visibility.Collapsed;
                }
            };

            var content = new StackPanel { Width = 360 };
            content.Children.Add(search);
            content.Children.Add(list);
            var dialog = new ContentDialog
            {
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                RequestedTheme = ElementTheme.Light,
                Title = "Chuyển tiếp tin nhắn",
                Content = content,
                PrimaryButtonText = "Chuyển tiếp",
                CloseButtonText = "Hủy"
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var selected = checkBoxes.Where(c => c.IsChecked == true).Select(c => c.Tag as ForwardTarget).Where(t => t != null).ToList();
            if (selected.Count == 0)
            {
                await new Windows.UI.Popups.MessageDialog("Hãy chọn ít nhất một người nhận.", "Chuyển tiếp").ShowAsync();
                return;
            }

            try
            {
                var conversations = selected.Select(t => t.IsGroup
                    ? ZaloApp.Groups.FirstOrDefault(g => g.ThreadId == t.Id)
                    : ZaloApp.OpenFriend(ZaloApp.Friends.First(f => f.UserId == t.Id)))
                    .Where(c => c != null).ToList();
                ZaloMessaging.ZaloForwardResult result = await ZaloMessaging.ForwardTextAsync(
                    ZaloApp.Session, message, conversations, System.Threading.CancellationToken.None);
                string status = result.Succeeded + " người nhận" + (result.Failed > 0 ? "; " + result.Failed + " lượt gửi thất bại." : ".");
                await new Windows.UI.Popups.MessageDialog(status, "Đã chuyển tiếp").ShowAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Message forward failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không chuyển tiếp được tin nhắn. Hãy thử lại sau.", "Chuyển tiếp").ShowAsync();
            }
        }

        private async System.Threading.Tasks.Task RecallMessageAsync(ZaloChatMessage message)
        {
            var confirm = new Windows.UI.Popups.MessageDialog("Thu hồi tin nhắn này cho mọi người?", "Thu hồi tin nhắn");
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Thu hồi"));
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Hủy"));
            confirm.DefaultCommandIndex = 1;
            if ((await confirm.ShowAsync()).Label != "Thu hồi") return;
            try
            {
                await ZaloApp.RecallMessageAsync(_conversation, message);
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Message recall failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không thu hồi được tin nhắn. Hãy thử lại sau.", "Thu hồi tin nhắn").ShowAsync();
            }
        }

        private async System.Threading.Tasks.Task DeleteMessageForMeAsync(ZaloChatMessage message)
        {
            var confirm = new Windows.UI.Popups.MessageDialog("Xóa tin nhắn này ở phía bạn?", "Xóa tin nhắn");
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Xóa"));
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Hủy"));
            confirm.DefaultCommandIndex = 1;
            if ((await confirm.ShowAsync()).Label != "Xóa") return;
            try
            {
                await ZaloApp.DeleteMessageForMeAsync(_conversation, message);
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Message delete failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không xóa được tin nhắn. Hãy thử lại sau.", "Xóa tin nhắn").ShowAsync();
            }
        }

        private async System.Threading.Tasks.Task SendReactionAsync(ZaloChatMessage message, string icon, int reactionType)
        {
            try
            {
                await ZaloApp.ReactAsync(_conversation, message, icon, reactionType);
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Reaction send failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không gửi được cảm xúc. Hãy thử lại sau.", "Cảm xúc").ShowAsync();
            }
        }

        private void BeginReply(ZaloChatMessage message)
        {
            _replyTarget = message;
            ReplySenderText.Text = string.IsNullOrWhiteSpace(message.Sender) ? "Trả lời tin nhắn" : message.Sender;
            ReplyBodyText.Text = message.Text;
            ReplyPreviewPanel.Visibility = Visibility.Visible;
            MessageInputBox.Focus(Windows.UI.Xaml.FocusState.Programmatic);
        }

        private void CancelReply_Click(object sender, RoutedEventArgs e)
        {
            ClearReply();
        }

        private void ClearReply()
        {
            _replyTarget = null;
            ReplyPreviewPanel.Visibility = Visibility.Collapsed;
            ReplySenderText.Text = string.Empty;
            ReplyBodyText.Text = string.Empty;
        }

        private async void ScrollToBottom()
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (_conversation != null && _conversation.Messages.Count > 0)
                {
                    MessagesListView.ScrollIntoView(_conversation.Messages[_conversation.Messages.Count - 1]);
                }
            });
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private void MessageInputBox_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter && ZaloSettingsService.Current.IsEnterToSend)
            {
                e.Handled = true;
                SendButton_Click(sender, e);
            }
        }

        private void MessageInputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateComposerActions();
            if (_conversation == null || string.IsNullOrWhiteSpace(MessageInputBox.Text)) return;
            _ = ZaloApp.SendTypingAsync(_conversation);
        }

        private void UpdateComposerActions()
        {
            if (MessageInputBox == null || VoiceButton == null || SendButton == null) return;
            bool hasText = !string.IsNullOrWhiteSpace(MessageInputBox.Text);
            VoiceButton.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
            SendButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            string text = MessageInputBox.Text.Trim();
            if (string.IsNullOrEmpty(text) || _conversation == null || _isSendingText) return;

            var reply = _replyTarget;
            MessageInputBox.Text = string.Empty;
            ClearReply();
            MessageInputBox.Focus(FocusState.Programmatic);

            _isSendingText = true;
            try
            {
                await ZaloApp.SendAsync(_conversation, text, reply);
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Send error: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog(
                    "Tin nhắn chưa gửi được:\n" + ex.Message,
                    "Lỗi gửi tin nhắn").ShowAsync();
            }
            finally
            {
                _isSendingText = false;
                MessageInputBox.Focus(FocusState.Programmatic);
            }
        }

        private void StickerToggle_Click(object sender, RoutedEventArgs e)
        {
            if (PickerPanel.Visibility == Visibility.Visible)
            {
                PickerPanel.Visibility = Visibility.Collapsed;
                return;
            }
            PickerPanel.Visibility = Visibility.Visible;
            ShowEmojiPicker();
            if (!_emojiLoaded) _ = LoadEmojiPickerAsync();
        }

        private void ShowEmojiPicker()
        {
            EmojiGridView.Visibility = Visibility.Visible;
            ZaloEmoticonGridView.Visibility = Visibility.Collapsed;
            LocalGifGridView.Visibility = Visibility.Collapsed;
            StickerGridView.Visibility = Visibility.Collapsed;
            EmojiCategoryScroll.Visibility = Visibility.Visible;
            StickerSearchBox.Visibility = Visibility.Collapsed;
            StickerSearchButton.Visibility = Visibility.Collapsed;
            PickerStatusText.Visibility = Visibility.Collapsed;
            EmojiModeButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, 234, 244, 255));
            EmojiModeButton.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, 8, 127, 245));
            StickerModeButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Colors.Transparent);
            StickerModeButton.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, 102, 112, 133));
        }

        private void EmojiMode_Click(object sender, RoutedEventArgs e)
        {
            ShowEmojiPicker();
        }

        private void StickerMode_Click(object sender, RoutedEventArgs e)
        {
            EmojiGridView.Visibility = Visibility.Collapsed;
            ZaloEmoticonGridView.Visibility = Visibility.Collapsed;
            LocalGifGridView.Visibility = Visibility.Collapsed;
            StickerGridView.Visibility = Visibility.Visible;
            EmojiCategoryScroll.Visibility = Visibility.Collapsed;
            StickerSearchBox.Visibility = Visibility.Visible;
            StickerSearchButton.Visibility = Visibility.Visible;
            StickerModeButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, 234, 244, 255));
            StickerModeButton.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, 8, 127, 245));
            EmojiModeButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Colors.Transparent);
            EmojiModeButton.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, 102, 112, 133));
            PickerStatusText.Text = "Nhập từ khóa rồi nhấn tìm";
            PickerStatusText.Visibility = StickerGridView.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void EmojiCategory_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null || button.Tag == null) return;
            int index;
            if (!int.TryParse(button.Tag.ToString(), out index)) return;
            SetEmojiCategory(index);
        }

        private void SetEmojiCategory(int index)
        {
            if (index < 0 || index >= _emojiCategories.Count) return;
            EmojiGridView.ItemsSource = _emojiCategories[index];
            var buttons = EmojiCategoryScroll.Content as StackPanel;
            if (buttons == null) return;
            foreach (var child in buttons.Children)
            {
                var categoryButton = child as Button;
                if (categoryButton == null) continue;
                int buttonIndex;
                bool selected = int.TryParse(categoryButton.Tag?.ToString(), out buttonIndex) && buttonIndex == index;
                categoryButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(selected
                    ? Color.FromArgb(255, 234, 244, 255) : Colors.Transparent);
                categoryButton.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(selected
                    ? Color.FromArgb(255, 8, 127, 245) : Color.FromArgb(255, 102, 112, 133));
            }
        }

        private static readonly string[][] BuiltinEmojiCategories = new string[][]
        {
            // Category 0: Smileys & Emotions
            new string[] {
                "😀", "😃", "😄", "😁", "😆", "😅", "😂", "🤣", "😊", "😇", "🙂", "🙃", "😉", "😌", "😍", "🥰",
                "😘", "😗", "😙", "😚", "😋", "😛", "😜", "🤪", "😝", "🤑", "🤗", "🤭", "🤫", "🤔", "🤐", "🤨",
                "😐", "😑", "😶", "😏", "😒", "🙄", "😬", "😮", "😯", "😲", "😳", "🥺", "😦", "😧", "😨", "😰",
                "😥", "😢", "😭", "😱", "😖", "😣", "😞", "😓", "😩", "😫", "🥱", "😤", "😡", "😠", "🤬", "😈",
                "👿", "💀", "💩", "🤡", "👻", "👽", "🤖", "😺", "😸", "😹", "😻", "😼", "😽", "🙀", "😿", "😾"
            },
            // Category 1: Gestures & People
            new string[] {
                "👋", "🤚", "🖐", "✋", "🖖", "👌", "🤌", "🤏", "✌", "🤞", "🤟", "🤘", "🤙", "👈", "👉", "👆",
                "🖕", "👇", "☝", "👍", "👎", "✊", "👊", "🤛", "🤜", "👏", "🙌", "👐", "🤲", "🤝", "🙏", "✍",
                "💅", "🤳", "💪", "🦵", "🦶", "👂", "👃", "🧠", "👀", "👁", "👅", "👄", "👶", "🧒", "👦", "👧",
                "🧑", "👱", "👨", "🧔", "👩", "🧓", "👴", "👵", "🙍", "🙎", "🙅", "🙆", "💁", "🙋", "🧏", "🙇"
            },
            // Category 2: Animals & Nature
            new string[] {
                "🐶", "🐱", "🐭", "🐹", "🐰", "🦊", "🐻", "🐼", "🐨", "🐯", "🦁", "🐮", "🐷", "🐸", "🐵", "🐔",
                "🐧", "🐦", "🐤", "🦆", "🦅", "🦉", "🦇", "🐺", "🐗", "🐴", "🦄", "🐝", "🐛", "🦋", "🐌", "🐞",
                "🐜", "🦟", "🐢", "🐍", "🦎", "🐙", "🦑", "🦐", "🦞", "🦀", "🐡", "🐠", "🐟", "🐬", "🐳", "🦈",
                "🐊", "🐅", "🐆", "🦓", "🦍", "🦧", "🐘", "🦛", "🦏", "🐪", "🐫", "🦒", "🦘", "🐃", "🐂", "🐄",
                "🌸", "💮", "🏵️", "🌹", "🥀", "🌺", "🌻", "🌼", "🌷", "🌱", "🪴", "🌲", "🌳", "🌴", "🌵", "🌾"
            },
            // Category 3: Food & Drinks
            new string[] {
                "🍏", "🍎", "🍐", "🍊", "🍋", "🍌", "🍉", "🍇", "🍓", "🫐", "🍈", "🍒", "🍑", "🥭", "🍍", "🥥",
                "🥝", "🍅", "🥑", "🥦", "🥬", "🥒", "🌶️", "🫑", "🌽", "🥕", "🫒", "🧄", "🧅", "🥔", "🍠", "🥐",
                "🥯", "🍞", "🥖", "🥨", "🧀", "🥚", "🍳", "🧈", "🥞", "🧇", "🥓", "🥩", "🍗", "🍖", "🦴", "🌭",
                "🍔", "🍟", "🍕", "🥪", "🥙", "🌮", "🌯", "🫔", "🥗", "🥘", "🫕", "🍲", "🍜", "🍝", "🍣", "🍱",
                "🍦", "🍧", "🍨", "🍩", "🍪", "🎂", "🍰", "🧁", "🍫", "🍬", "🍭", "🍮", "🍯", "☕", "🫖", "🍵"
            },
            // Category 4: Activities & Sports
            new string[] {
                "⚽", "🏀", "🏈", "⚾", "🥎", "🎾", "🏐", "🏉", "🥏", "🎱", "🪀", "🏓", "🏸", "🏒", "🏑", "🥍",
                "🏏", "🪃", "🥅", "⛳", "🪁", "🏹", "🎣", "🤿", "🥊", "🥋", "🎽", "🛹", "🛼", "🛷", "⛸️", "🥌",
                "🎿", "⛷️", "🏂", "🪂", "🏋️", "🤼", "🤸", "🧗", "🏇", "🚴", "🚵", "🏆", "🥇", "🥈", "🥉", "🏅"
            },
            // Category 5: Travel & Objects
            new string[] {
                "🚗", "🚕", "🚙", "🚌", "🚎", "🏎️", "🚓", "🚑", "🚒", "🚐", "🛻", "🚚", "🚛", "🚜", "🛵", "🏍️",
                "🛺", "🚲", "🛴", "🚨", "🚥", "🚦", "⚓", "⛵", "🚤", "🛳️", "⛴️", "🚢", "✈️", "🛫", "🛬", "🪂",
                "💺", "🚁", "🚟", "🚠", "🚡", "🛰️", "🚀", "🛸", "⏰", "📱", "💻", "⌨️", "🖥️", "🖨️", "📷", "📸"
            },
            // Category 6: Hearts & Symbols
            new string[] {
                "❤️", "🧡", "💛", "💚", "💙", "💜", "🖤", "🤍", "🤎", "💔", "❣️", "💕", "💞", "💓", "💗", "💖",
                "💘", "💝", "💟", "☮️", "✝️", "☪️", "🕉️", "☸️", "✡️", "🔯", "🕎", "☯️", "☦️", "🛐", "⛎", "♈",
                "💯", "💢", "💬", "💭", "💤", "💥", "💫", "💦", "💨", "🕳️", "💣", "🛑", "✨", "⭐", "🌟", "⚡",
                "🔥", "🌈", "☀️", "🌤️", "⛅", "🌥️", "☁️", "🌦️", "🌧️", "⛈️", "🌩️", "🌨️", "🎉", "🎊", "🎈", "🎁"
            },
            // Category 7: Flags
            new string[] {
                "🇻🇳", "🇺🇸", "🇯🇵", "🇰🇷", "🇨🇳", "🇬🇧", "🇫🇷", "🇩🇪", "🇷🇺", "🇮🇹", "🇪🇸", "🇨🇦", "🇦🇺", "🇧🇷", "🇮🇳", "🇹🇭",
                "🇸🇬", "🇲🇾", "🇮🇩", "🇵🇭", "🇱🇦", "🇰🇭", "🇲🇲", "🚩", "🏁", "🎌", "🏴", "🏳️"
            }
        };

        private async System.Threading.Tasks.Task LoadEmojiPickerAsync()
        {
            try
            {
                if (_emojiCategories.Count == 0)
                {
                    foreach (var cat in BuiltinEmojiCategories)
                    {
                        _emojiCategories.Add(new List<string>(cat));
                    }
                }

                if (_zaloEmojiAssets.Count == 0)
                {
                    for (int i = 0; i < 90; i++)
                    {
                        _zaloEmojiAssets.Add(new EmojiAsset
                        {
                            Code = "emo_" + i.ToString("D2"),
                            ImageUri = "ms-appx:///Assets/Emoticons/Emoji/emo_" + i.ToString("D2") + ".png"
                        });
                    }
                }

                if (_bundledGifAssets.Count == 0)
                {
                    for (int i = 1; i <= 30; i++)
                    {
                        string name = "sticker_" + i.ToString("D2") + ".gif";
                        _bundledGifAssets.Add(new BundledGifAsset
                        {
                            FileName = name,
                            ImageUri = "ms-appx:///Assets/Emoticons/" + name
                        });
                    }
                }

                SetEmojiCategory(0);
                ZaloEmoticonGridView.ItemsSource = _zaloEmojiAssets;
                LocalGifGridView.ItemsSource = _bundledGifAssets;
                _localPickerAssetsLoaded = true;
                _emojiLoaded = true;

                // Load custom Zalo character codes asynchronously if available
                try
                {
                    var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/Emoticons/emoticon_data.json"));
                    JsonObject data = JsonObject.Parse(await FileIO.ReadTextAsync(file));
                    JsonArray zaloCodes = data.GetNamedArray("zalo_characters", new JsonArray());
                    for (int i = 0; i < zaloCodes.Count && i < _zaloEmojiAssets.Count; i++)
                    {
                        if (zaloCodes[i].ValueType == JsonValueType.String)
                        {
                            _zaloEmojiAssets[i].Code = zaloCodes[i].GetString();
                        }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Emoji load error: " + ex.Message);
            }
        }

        private void EmojiGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            string emoji = e.ClickedItem as string;
            if (!string.IsNullOrEmpty(emoji))
            {
                int sel = MessageInputBox.SelectionStart;
                string current = MessageInputBox.Text ?? string.Empty;
                if (sel >= 0 && sel <= current.Length)
                {
                    MessageInputBox.Text = current.Insert(sel, emoji);
                    MessageInputBox.SelectionStart = sel + emoji.Length;
                }
                else
                {
                    MessageInputBox.Text += emoji;
                    MessageInputBox.SelectionStart = MessageInputBox.Text.Length;
                }
                MessageInputBox.Focus(Windows.UI.Xaml.FocusState.Programmatic);
            }
        }

        private void ZaloEmojiMode_Click(object sender, RoutedEventArgs e)
        {
            EmojiGridView.Visibility = Visibility.Collapsed;
            ZaloEmoticonGridView.Visibility = Visibility.Visible;
            LocalGifGridView.Visibility = Visibility.Collapsed;
            StickerGridView.Visibility = Visibility.Collapsed;
            EmojiCategoryScroll.Visibility = Visibility.Collapsed;
            StickerSearchBox.Visibility = Visibility.Collapsed;
            StickerSearchButton.Visibility = Visibility.Collapsed;
            PickerStatusText.Visibility = Visibility.Collapsed;
            if (!_localPickerAssetsLoaded && !_emojiLoaded) _ = LoadEmojiPickerAsync();
            ZaloEmojiModeButton.Background = new SolidColorBrush(Color.FromArgb(255, 234, 244, 255));
            ZaloEmojiModeButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 8, 127, 245));
            EmojiModeButton.Background = new SolidColorBrush(Colors.Transparent);
            EmojiModeButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 102, 112, 133));
            StickerModeButton.Background = new SolidColorBrush(Colors.Transparent);
            StickerModeButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 102, 112, 133));
            GifModeButton.Background = new SolidColorBrush(Colors.Transparent);
            GifModeButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 102, 112, 133));
        }

        private void ZaloEmoticonGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as EmojiAsset;
            if (item == null || string.IsNullOrEmpty(item.Code)) return;
            int sel = MessageInputBox.SelectionStart;
            string current = MessageInputBox.Text ?? string.Empty;
            if (sel >= 0 && sel <= current.Length)
            {
                MessageInputBox.Text = current.Insert(sel, item.Code);
                MessageInputBox.SelectionStart = sel + item.Code.Length;
            }
            else
            {
                MessageInputBox.Text += item.Code;
                MessageInputBox.SelectionStart = MessageInputBox.Text.Length;
            }
            MessageInputBox.Focus(FocusState.Programmatic);
        }

        private void GifMode_Click(object sender, RoutedEventArgs e)
        {
            EmojiGridView.Visibility = Visibility.Collapsed;
            ZaloEmoticonGridView.Visibility = Visibility.Collapsed;
            LocalGifGridView.Visibility = Visibility.Visible;
            StickerGridView.Visibility = Visibility.Collapsed;
            EmojiCategoryScroll.Visibility = Visibility.Collapsed;
            StickerSearchBox.Visibility = Visibility.Collapsed;
            StickerSearchButton.Visibility = Visibility.Collapsed;
            PickerStatusText.Visibility = Visibility.Collapsed;
            if (!_localPickerAssetsLoaded && !_emojiLoaded) _ = LoadEmojiPickerAsync();
            GifModeButton.Background = new SolidColorBrush(Color.FromArgb(255, 234, 244, 255));
            GifModeButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 8, 127, 245));
            EmojiModeButton.Background = new SolidColorBrush(Colors.Transparent);
            EmojiModeButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 102, 112, 133));
            StickerModeButton.Background = new SolidColorBrush(Colors.Transparent);
            StickerModeButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 102, 112, 133));
            ZaloEmojiModeButton.Background = new SolidColorBrush(Colors.Transparent);
            ZaloEmojiModeButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 102, 112, 133));
        }

        private async void LocalGifGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as BundledGifAsset;
            if (item == null || _conversation == null) return;
            LocalGifGridView.IsEnabled = false;
            try
            {
                StorageFile file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(item.ImageUri));
                var properties = await file.GetBasicPropertiesAsync();
                using (IRandomAccessStream stream = await file.OpenReadAsync())
                {
                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                    stream.Seek(0);
                    await ZaloApp.SendGifAsync(_conversation, item.FileName, stream, (long)properties.Size,
                        decoder.PixelWidth, decoder.PixelHeight, item.ImageUri);
                }
                PickerPanel.Visibility = Visibility.Collapsed;
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Bundled GIF send failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không gửi được ảnh động. " + ex.Message, "Gửi GIF").ShowAsync();
            }
            finally
            {
                LocalGifGridView.IsEnabled = true;
            }
        }

        private void StickerSearchButton_Click(object sender, RoutedEventArgs e)
        {
            SearchStickersAsync();
        }

        private void StickerSearchBox_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                SearchStickersAsync();
            }
        }

        private async void SearchStickersAsync()
        {
            string query = StickerSearchBox.Text.Trim();
            if (query.Length < 2)
            {
                StickerGridView.ItemsSource = null;
                PickerStatusText.Text = "Nhập ít nhất 2 ký tự để tìm";
                PickerStatusText.Visibility = Visibility.Visible;
                return;
            }

            PickerStatusText.Text = "Đang tìm nhãn dán...";
            PickerStatusText.Visibility = Visibility.Visible;
            try
            {
                var stickers = await ZaloMessaging.SearchStickersAsync(ZaloApp.Session, query, System.Threading.CancellationToken.None);
                if (!string.Equals(query, StickerSearchBox.Text.Trim(), StringComparison.Ordinal)) return;
                StickerGridView.ItemsSource = stickers;
                PickerStatusText.Text = stickers.Count == 0 ? "Không tìm thấy nhãn dán" : string.Empty;
                PickerStatusText.Visibility = stickers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                PickerStatusText.Text = "Không tải được nhãn dán. Thử lại sau.";
                PickerStatusText.Visibility = Visibility.Visible;
                ZaloLog.Add("Sticker search failed: " + ex.Message);
            }
        }

        private async void StickerGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is ZaloSticker sticker) || _conversation == null) return;
            StickerGridView.IsEnabled = false;
            try
            {
                await ZaloApp.SendStickerAsync(_conversation, sticker);
                PickerPanel.Visibility = Visibility.Collapsed;
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Sticker send failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không gửi được nhãn dán. " + ex.Message, "Gửi nhãn dán").ShowAsync();
            }
            finally
            {
                StickerGridView.IsEnabled = true;
            }
        }

        private async void PhotoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_conversation == null) return;
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".gif");

            StorageFile file = await picker.PickSingleFileAsync();
            if (file == null) return;

            PhotoButton.IsEnabled = false;
            string caption = MessageInputBox.Text.Trim();
            bool isVideo = string.Equals(System.IO.Path.GetExtension(file.Name), ".mp4", StringComparison.OrdinalIgnoreCase);
            try
            {
                var properties = await file.GetBasicPropertiesAsync();
                if (properties.Size > 20UL * 1024UL * 1024UL)
                    throw new InvalidOperationException((isVideo ? "Video" : "Ảnh") + " vượt giới hạn 20 MB.");

                if (isVideo)
                {
                    using (IRandomAccessStream videoStream = await file.OpenReadAsync())
                        await ZaloApp.SendFileAsync(_conversation, file.Name, videoStream, (long)properties.Size);
                    ScrollToBottom();
                    return; // Keep the caption in the composer; this upload route does not send video captions.
                }

                using (IRandomAccessStream imageStream = await file.OpenReadAsync())
                {
                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(imageStream);
                    uint width = decoder.PixelWidth;
                    uint height = decoder.PixelHeight;
                    imageStream.Seek(0);
                    await ZaloApp.SendImageAsync(_conversation, file.Name, imageStream, (long)properties.Size,
                        width, height, caption);
                }
                MessageInputBox.Text = string.Empty;
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                ZaloLog.Add((isVideo ? "Video" : "Image") + " send error: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không gửi được " + (isVideo ? "video" : "ảnh")
                    + ". " + ex.Message, "Lỗi gửi " + (isVideo ? "video" : "ảnh")).ShowAsync();
            }
            finally
            {
                PhotoButton.IsEnabled = true;
            }
        }

        private async void FileButton_Click(object sender, RoutedEventArgs e)
        {
            await PickAndSendFileAsync();
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new MenuFlyout();
            var sendFile = new MenuFlyoutItem { Text = "Gửi tệp" };
            sendFile.Click += async (menuSender, args) => await PickAndSendFileAsync();
            menu.Items.Add(sendFile);
            var sendGif = new MenuFlyoutItem { Text = "Gửi ảnh GIF" };
            sendGif.Click += async (menuSender, args) => await PickAndSendGifAsync();
            menu.Items.Add(sendGif);
            var sendContact = new MenuFlyoutItem { Text = "Chia sẻ danh thiếp" };
            sendContact.Click += async (menuSender, args) => await ShareContactCardAsync();
            menu.Items.Add(sendContact);
            var quickMessages = new MenuFlyoutItem { Text = "Tin nhắn nhanh" };
            quickMessages.Click += async (menuSender, args) => await ShowQuickMessagesAsync();
            menu.Items.Add(quickMessages);
            menu.ShowAt(MoreButton);
        }

        private async System.Threading.Tasks.Task PickAndSendGifAsync()
        {
            if (_conversation == null) return;
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add(".gif");

            StorageFile file = await picker.PickSingleFileAsync();
            if (file == null) return;

            string caption = MessageInputBox.Text.Trim();
            try
            {
                var properties = await file.GetBasicPropertiesAsync();
                if (properties.Size > 20UL * 1024UL * 1024UL)
                    throw new InvalidOperationException("Ảnh GIF vượt giới hạn 20 MB.");

                using (IRandomAccessStream imageStream = await file.OpenReadAsync())
                {
                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(imageStream);
                    uint width = decoder.PixelWidth;
                    uint height = decoder.PixelHeight;
                    imageStream.Seek(0);
                    await ZaloApp.SendImageAsync(_conversation, file.Name, imageStream, (long)properties.Size,
                        width, height, caption);
                }
                MessageInputBox.Text = string.Empty;
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("GIF send error: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không gửi được ảnh GIF. " + ex.Message, "Lỗi gửi GIF").ShowAsync();
            }
        }

        private async System.Threading.Tasks.Task ShareContactCardAsync()
        {
            List<ZaloFriend> contacts = (ZaloApp.Friends ?? new List<ZaloFriend>())
                .Where(friend => friend != null && !string.IsNullOrWhiteSpace(friend.UserId))
                .OrderBy(friend => friend.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (contacts.Count == 0)
            {
                await new Windows.UI.Popups.MessageDialog("Danh bạ chưa tải xong hoặc chưa có liên hệ để chia sẻ.", "Danh thiếp").ShowAsync();
                return;
            }

            var search = new TextBox { PlaceholderText = "Tìm trong danh bạ", Margin = new Thickness(0, 0, 0, 8) };
            var list = new ListView { Height = 300, SelectionMode = ListViewSelectionMode.Single };
            var dialog = new ContentDialog
            {
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                RequestedTheme = ElementTheme.Light,
                Title = "Chia sẻ danh thiếp",
                Content = new StackPanel { Children = { search, list } },
                PrimaryButtonText = "Gửi danh thiếp",
                SecondaryButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false
            };
            Action refresh = () =>
            {
                string query = ZaloFormat.NormalizeSearch(search.Text);
                list.Items.Clear();
                foreach (ZaloFriend friend in contacts.Where(item => query.Length == 0 || ZaloFormat.SearchContains(item.Name, query)))
                {
                    var avatar = new Grid { Width = 42, Height = 42, Margin = new Thickness(0, 0, 10, 0) };
                    avatar.Children.Add(new Ellipse { Fill = friend.AvatarColor });
                    if (friend.AvatarImageBrush != null) avatar.Children.Add(new Ellipse { Fill = friend.AvatarImageBrush });
                    var name = new TextBlock
                    {
                        Text = friend.Name,
                        FontSize = 14,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x17, 0x21, 0x2B)),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 54, VerticalAlignment = VerticalAlignment.Center };
                    row.Children.Add(avatar);
                    row.Children.Add(name);
                    list.Items.Add(new ListViewItem { Content = row, Tag = friend, Height = 58 });
                }
                dialog.IsPrimaryButtonEnabled = false;
            };
            search.TextChanged += (s, args) => refresh();
            list.SelectionChanged += (s, args) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is ListViewItem;
            refresh();

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var selected = list.SelectedItem as ListViewItem;
            var contact = selected == null ? null : selected.Tag as ZaloFriend;
            if (contact == null) return;
            try
            {
                await ZaloApp.SendContactCardAsync(_conversation, contact);
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Send contact card failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không gửi được danh thiếp. " + ex.Message, "Danh thiếp").ShowAsync();
            }
        }

        private async System.Threading.Tasks.Task ShowQuickMessagesAsync()
        {
            if (ZaloApp.Session == null || _conversation == null) return;
            List<ZaloQuickMessage> quickMessages;
            try
            {
                quickMessages = await ZaloMessaging.GetQuickMessagesAsync(
                    ZaloApp.Session, System.Threading.CancellationToken.None);
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Quick messages load failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không tải được tin nhắn nhanh. " + ex.Message, "Tin nhắn nhanh").ShowAsync();
                return;
            }

            var textMessages = quickMessages.Where(item => item.Type == 0 && !string.IsNullOrWhiteSpace(item.Title))
                .OrderBy(item => item.Keyword, StringComparer.CurrentCultureIgnoreCase).ToList();
            var search = new TextBox { PlaceholderText = "Tìm từ khóa hoặc nội dung", Margin = new Thickness(0, 0, 0, 8) };
            var list = new ListView { Height = 280, SelectionMode = ListViewSelectionMode.Single };
            var empty = new TextBlock
            {
                Text = "Chưa có tin nhắn nhanh. Chọn Tạo mẫu để lưu nội dung thường dùng.",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x75, 0x85, 0x96)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 8, 2, 8),
                Visibility = Visibility.Collapsed
            };
            var content = new StackPanel();
            content.Children.Add(search);
            content.Children.Add(empty);
            content.Children.Add(list);
            var dialog = new ContentDialog
            {
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                RequestedTheme = ElementTheme.Light,
                Title = "Tin nhắn nhanh",
                Content = content,
                PrimaryButtonText = "Chèn vào tin nhắn",
                SecondaryButtonText = "Tạo mẫu",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false
            };

            ZaloQuickMessage pendingEdit = null;
            ZaloQuickMessage pendingDelete = null;
            Action refresh = () =>
            {
                string query = ZaloFormat.NormalizeSearch(search.Text);
                list.Items.Clear();
                foreach (ZaloQuickMessage item in textMessages.Where(value => query.Length == 0
                    || ZaloFormat.SearchContains(value.Keyword, query) || ZaloFormat.SearchContains(value.Title, query)))
                {
                    var row = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
                    row.Children.Add(new TextBlock
                    {
                        Text = item.Keyword,
                        FontSize = 11,
                        FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x08, 0x7D, 0xE0))
                    });
                    row.Children.Add(new TextBlock
                    {
                        Text = item.Title,
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x23, 0x2D, 0x38)),
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 2
                    });
                    StackPanelSpacingCompat.Apply(row, 3);
                    var listItem = new ListViewItem { Content = row, Tag = item };
                    var actions = new MenuFlyout();
                    var editAction = new MenuFlyoutItem { Text = "Sửa" };
                    editAction.Click += (sender, args) => { pendingEdit = item; dialog.Hide(); };
                    var deleteAction = new MenuFlyoutItem { Text = "Xóa" };
                    deleteAction.Click += (sender, args) => { pendingDelete = item; dialog.Hide(); };
                    actions.Items.Add(editAction);
                    actions.Items.Add(deleteAction);
                    listItem.ContextFlyout = actions;
                    list.Items.Add(listItem);
                }
                empty.Visibility = textMessages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                list.Visibility = textMessages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = false;
            };
            search.TextChanged += (s, args) => refresh();
            list.SelectionChanged += (s, args) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is ListViewItem;
            refresh();

            ContentDialogResult result = await dialog.ShowAsync();
            if (pendingEdit != null)
            {
                await EditQuickMessageAsync(pendingEdit);
                await ShowQuickMessagesAsync();
                return;
            }
            if (pendingDelete != null)
            {
                await DeleteQuickMessageAsync(pendingDelete);
                await ShowQuickMessagesAsync();
                return;
            }
            if (result == ContentDialogResult.Primary)
            {
                var selected = list.SelectedItem as ListViewItem;
                var quickMessage = selected == null ? null : selected.Tag as ZaloQuickMessage;
                if (quickMessage == null) return;
                MessageInputBox.Text = quickMessage.Title;
                MessageInputBox.Focus(Windows.UI.Xaml.FocusState.Programmatic);
                return;
            }
            if (result != ContentDialogResult.Secondary) return;

            var keywordInput = new TextBox { PlaceholderText = "Từ khóa", MaxLength = 40 };
            var textInput = new TextBox
            {
                PlaceholderText = "Nội dung tin nhắn",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 112,
                MaxLength = 2000
            };
            var addContent = new StackPanel();
            addContent.Children.Add(keywordInput);
            addContent.Children.Add(textInput);
            var addDialog = new ContentDialog
            {
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                RequestedTheme = ElementTheme.Light,
                Title = "Tạo tin nhắn nhanh",
                Content = addContent,
                PrimaryButtonText = "Lưu",
                SecondaryButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await addDialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                await ZaloMessaging.AddQuickMessageAsync(ZaloApp.Session, keywordInput.Text, textInput.Text,
                    System.Threading.CancellationToken.None);
                await new Windows.UI.Popups.MessageDialog("Đã lưu tin nhắn nhanh.", "Tin nhắn nhanh").ShowAsync();
                await ShowQuickMessagesAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Quick message create failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không lưu được tin nhắn nhanh. " + ex.Message, "Tin nhắn nhanh").ShowAsync();
            }
        }

        private async System.Threading.Tasks.Task EditQuickMessageAsync(ZaloQuickMessage item)
        {
            var keyword = new TextBox { Text = item.Keyword ?? string.Empty, PlaceholderText = "Từ khóa", MaxLength = 40 };
            var title = new TextBox
            {
                Text = item.Title ?? string.Empty,
                PlaceholderText = "Nội dung tin nhắn",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 112,
                MaxLength = 2000
            };
            var content = new StackPanel();
            content.Children.Add(keyword);
            content.Children.Add(title);
            var dialog = new ContentDialog
            {
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                RequestedTheme = ElementTheme.Light,
                Title = "Sửa tin nhắn nhanh",
                Content = content,
                PrimaryButtonText = "Lưu",
                SecondaryButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                await ZaloMessaging.UpdateQuickMessageAsync(ZaloApp.Session, item.Id, keyword.Text, title.Text,
                    System.Threading.CancellationToken.None);
                item.Keyword = keyword.Text.Trim();
                item.Title = title.Text.Trim();
                await new Windows.UI.Popups.MessageDialog("Đã cập nhật tin nhắn nhanh.", "Tin nhắn nhanh").ShowAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Quick message update failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không cập nhật được tin nhắn nhanh. " + ex.Message, "Tin nhắn nhanh").ShowAsync();
            }
        }

        private async System.Threading.Tasks.Task DeleteQuickMessageAsync(ZaloQuickMessage item)
        {
            var confirm = new Windows.UI.Popups.MessageDialog("Xóa mẫu ‘" + (item.Keyword ?? string.Empty) + "’?", "Tin nhắn nhanh");
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Xóa"));
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Hủy"));
            confirm.DefaultCommandIndex = 1;
            if ((await confirm.ShowAsync()).Label != "Xóa") return;
            try
            {
                await ZaloMessaging.DeleteQuickMessageAsync(ZaloApp.Session, item.Id,
                    System.Threading.CancellationToken.None);
                await new Windows.UI.Popups.MessageDialog("Đã xóa tin nhắn nhanh.", "Tin nhắn nhanh").ShowAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Quick message delete failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không xóa được tin nhắn nhanh. " + ex.Message, "Tin nhắn nhanh").ShowAsync();
            }
        }

        private async System.Threading.Tasks.Task PickAndSendFileAsync()
        {
            if (_conversation == null || ZaloApp.Session == null) return;
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");
            StorageFile file = await picker.PickSingleFileAsync();
            if (file == null) return;

            FileButton.IsEnabled = false;
            try
            {
                string extension = System.IO.Path.GetExtension(file.Name).TrimStart('.').ToLowerInvariant();
                var shareFile = ZaloApp.Session.Settings.GetNamedObject("features").GetNamedObject("sharefile");
                Windows.Data.Json.JsonArray restricted;
                if (shareFile.TryGetValue("restricted_ext_file", out var restrictedValue)
                    && restrictedValue.ValueType == Windows.Data.Json.JsonValueType.Array)
                {
                    restricted = restrictedValue.GetArray();
                    if (restricted.Any(v => v.ValueType == Windows.Data.Json.JsonValueType.String
                        && string.Equals(v.GetString().TrimStart('.'), extension, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Loại tệp này bị Zalo chặn.");
                }

                var properties = await file.GetBasicPropertiesAsync();
                if (properties.Size == 0) throw new InvalidOperationException("Tệp đang trống.");
                if (properties.Size > 20UL * 1024UL * 1024UL)
                    throw new InvalidOperationException("Tệp vượt giới hạn 20 MB của phiên bản này.");
                using (IRandomAccessStream input = await file.OpenReadAsync())
                    await ZaloApp.SendFileAsync(_conversation, file.Name, input, (long)properties.Size);
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("File send error: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không gửi được tệp. " + ex.Message, "Gửi tệp").ShowAsync();
            }
            finally
            {
                FileButton.IsEnabled = true;
            }
        }

        private async void VoiceButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecordingVoice)
            {
                await FinishVoiceRecordingAsync(true);
                return;
            }
            if (_voiceStartPending || _isSendingVoice || _conversation == null) return;

            StopActivePlayback();
            _voiceStartPending = true;
            RecordingStatusPanel.Visibility = Visibility.Visible;
            RecordingStatusText.Text = "Đang mở microphone…";
            CancelVoiceRecordingButton.Visibility = Visibility.Collapsed;
            SendVoiceRecordingButton.Visibility = Visibility.Collapsed;
            VoiceButton.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xD9, 0x2D, 0x20));
            bool started = await ZaloVoiceService.Current.StartRecordingAsync();
            _voiceStartPending = false;
            if (!started)
            {
                RecordingStatusPanel.Visibility = Visibility.Collapsed;
                VoiceButton.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x07, 0x86, 0xF9));
                string details = ZaloVoiceService.Current.LastError;
                if (!_chatPageActive) return;
                await new Windows.UI.Popups.MessageDialog(
                    "Không mở được microphone. Kiểm tra quyền microphone trong cài đặt Windows rồi thử lại."
                    + (string.IsNullOrWhiteSpace(details) ? string.Empty : "\n\nChi tiết: " + details),
                    "Ghi âm tin nhắn").ShowAsync();
                return;
            }

            _isRecordingVoice = true;
            if (!_chatPageActive)
            {
                await FinishVoiceRecordingAsync(false);
                return;
            }
            RecordingStatusText.Text = "Đang ghi âm · chạm Gửi khi xong";
            RecordingStatusPanel.Visibility = Visibility.Visible;
            CancelVoiceRecordingButton.Visibility = Visibility.Visible;
            SendVoiceRecordingButton.Visibility = Visibility.Visible;
            VoiceButton.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xD9, 0x2D, 0x20));
        }

        private async void CancelVoiceRecording_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecordingVoice && !_voiceStartPending) await FinishVoiceRecordingAsync(false);
        }

        private async void SendVoiceRecording_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecordingVoice && !_voiceStartPending) await FinishVoiceRecordingAsync(true);
        }

        private async System.Threading.Tasks.Task FinishVoiceRecordingAsync(bool send)
        {
            if (!_isRecordingVoice) return;
            _isRecordingVoice = false;
            _isSendingVoice = send;
            VoiceButton.IsEnabled = false;
            StorageFile recordedFile = null;
            RecordingStatusPanel.Visibility = Visibility.Visible;
            RecordingStatusText.Text = send ? "Đang tải và gửi tin nhắn thoại..." : "Đã hủy ghi âm";
            try
            {
                recordedFile = await ZaloVoiceService.Current.StopRecordingAsync();
                if (recordedFile == null) throw new InvalidOperationException("Không lưu được bản ghi âm.");
                if (!send) return;

                var properties = await recordedFile.GetBasicPropertiesAsync();
                if (properties.Size < 1024) throw new InvalidOperationException("Bản ghi quá ngắn, hãy giữ nút lâu hơn.");
                if (properties.Size > 20UL * 1024UL * 1024UL) throw new InvalidOperationException("Bản ghi vượt giới hạn 20 MB.");
                using (IRandomAccessStream input = await recordedFile.OpenReadAsync())
                    await ZaloApp.SendVoiceAsync(_conversation, recordedFile.Name, input, (long)properties.Size);
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Voice send error: " + ex.Message);
                if (send)
                    await new Windows.UI.Popups.MessageDialog("Không gửi được tin nhắn thoại. " + ex.Message, "Gửi tin nhắn thoại").ShowAsync();
            }
            finally
            {
                try
                {
                    if (ZaloVoiceService.Current.IsRecording)
                    {
                        StorageFile pending = await ZaloVoiceService.Current.StopRecordingAsync();
                        if (recordedFile == null) recordedFile = pending;
                    }
                    if (recordedFile != null) await recordedFile.DeleteAsync();
                }
                catch { }
                RecordingStatusPanel.Visibility = Visibility.Collapsed;
                VoiceButton.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x07, 0x86, 0xF9));
                VoiceButton.IsEnabled = true;
                _isSendingVoice = false;
                CancelVoiceRecordingButton.Visibility = Visibility.Collapsed;
                SendVoiceRecordingButton.Visibility = Visibility.Collapsed;
            }
        }

        private async void CallButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Windows.UI.Popups.MessageDialog("Gọi thoại Zalo chưa được nối với dịch vụ gọi trên Windows 10 Mobile.", "Chưa hỗ trợ");
            await dialog.ShowAsync();
        }

        private async void VideoCallButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Windows.UI.Popups.MessageDialog("Gọi video Zalo chưa được nối với dịch vụ gọi trên Windows 10 Mobile.", "Chưa hỗ trợ");
            await dialog.ShowAsync();
        }

        private void InfoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_conversation == null) return;
            if (_conversation.IsGroup)
            {
                Frame.Navigate(typeof(GroupInfoPage), _conversation.Key);
                return;
            }

            Frame.Navigate(typeof(ConversationOptionsPage), _conversation.Key);
        }

        private void GroupMoreHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            if (_conversation == null || !_conversation.IsGroup) return;
            Frame.Navigate(typeof(GroupInfoPage), _conversation.Key);
        }
    }
}
