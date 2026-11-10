using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using ZaloUWP.Common;
using ZaloLite;
using ZaloUWP.Core;
using ZaloUWP.Services;

namespace ZaloUWP.Views
{
    public sealed partial class MainPage : Page
    {
        private static readonly Dictionary<string, BitmapImage> NavIconCache = new Dictionary<string, BitmapImage>(StringComparer.Ordinal);
        private readonly SolidColorBrush ActiveBlueBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x88, 0xFF));
        private readonly SolidColorBrush InactiveGrayBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x75, 0x75, 0x75));
        private readonly SolidColorBrush TextDarkBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x11, 0x18, 0x27));
        private int _selectedTabIndex = 0;
        private ZaloFriend _remoteSearchResult;
        private string _remoteSearchQuery = string.Empty;
        private bool _showOtherConversations;
        private System.Collections.Generic.List<ZaloFriendRequest> _friendRequests = new System.Collections.Generic.List<ZaloFriendRequest>();
        private System.Collections.Generic.List<ZaloFriendRequest> _sentFriendRequests = new System.Collections.Generic.List<ZaloFriendRequest>();
        private System.Collections.Generic.List<ZaloFriend> _recommendedFriends = new System.Collections.Generic.List<ZaloFriend>();
        private bool _recommendationsLoaded;
        private bool _recommendationsLoading;
        private bool _conversationListDirty = true;
        private DispatcherTimer _conversationSearchTimer;
        private string _pendingConversationQuery = string.Empty;
        private bool _sessionBootstrapPending;
        private string _lastConversationQuery;
        private bool _lastOtherConversationView;
        private string _lastFriendFilter;
        private System.Collections.Generic.List<ZaloFriend> _lastFriendSource;
        private ZaloFriend _lastRenderedRemoteSearchResult;
        private System.Collections.Generic.List<ZaloFriend> _sortedFriendSource;
        private System.Collections.Generic.List<ZaloFriend> _sortedFriends;
        private string _profileUid;
        private string _profileDisplayName;
        private string _profileAvatarUrl;
        private string _profileStatus;

        public MainPage()
        {
            this.InitializeComponent();
            NavigationCacheMode = Windows.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
            _conversationSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _conversationSearchTimer.Tick += ConversationSearchTimer_Tick;
        }

        private void ConversationSearchTimer_Tick(object sender, object e)
        {
            _conversationSearchTimer.Stop();
            FilterConversations(_pendingConversationQuery);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            bool isReturning = e.NavigationMode == NavigationMode.Back;
            if (!isReturning) Frame.BackStack.Clear();
            _sessionBootstrapPending = ZaloApp.Session == null;

            _conversationListDirty = true;
            FilterConversations(SearchBox != null ? SearchBox.Text : string.Empty);

            if (!isReturning)
            {
                ShowFriends(SearchBox.Text);
                UpdateQuickContacts();
                UpdateGroupList();
            }

            ZaloApp.Conversations.CollectionChanged += OnConversationsChanged;
            ZaloApp.Groups.CollectionChanged += OnGroupsChanged;
            ZaloApp.GroupsLoadChanged += OnGroupsLoadChanged;
            ZaloApp.FriendsLoadChanged += OnFriendsLoadChanged;
            ZaloApp.CacheRestored += OnCacheRestored;
            ZaloApp.StatusChanged += OnStatusChanged;
            ZaloPhoneSync.SyncStatusChanged += OnSyncStatusChanged;

            SyncBannerSubtitle.Text = ZaloPhoneSync.StatusMessage;
            SyncBannerButton.IsEnabled = false;
            SyncBannerButton.Content = "Chưa hỗ trợ";

            if (!isReturning) UpdateConversationsEmptyState();
            UpdateUnreadBadges();
            if (!isReturning)
            {
                UpdateUserProfile();
                SelectTab(0);
                RefreshFriendRequestsAsync();
                RefreshSentFriendRequestsAsync();
            }

            if (ZaloApp.Session != null)
            {
                if ((ZaloApp.Friends == null || ZaloApp.Friends.Count == 0))
                {
                    _ = RefreshFriendsAsync();
                }
            }
            else
            {
                _ = InitializeSessionAsync();
            }
        }

        private async Task InitializeSessionAsync()
        {
            try
            {
                if (await ZaloStore.HasSessionAsync())
                {
                    string cachedUid = await ZaloStore.GetCachedUserIdAsync();
                    if (!string.IsNullOrWhiteSpace(cachedUid))
                    {
                        await ZaloApp.PreloadOfflineCacheAsync(cachedUid);
                        ShowFriends(SearchBox?.Text);
                        UpdateQuickContacts();
                        _sessionBootstrapPending = ZaloApp.Conversations.Count == 0
                            && (ZaloApp.Friends == null || ZaloApp.Friends.Count == 0);
                        FilterConversations(SearchBox?.Text);
                    }

                    var session = await ZaloStore.RestoreAsync(CancellationToken.None);
                    if (session != null)
                    {
                        // Keep the authenticated account identity locally so the next cold start
                        // can show its offline cache before cookie validation completes.
                        if (!string.Equals(cachedUid, session.Uid, StringComparison.Ordinal))
                            await ZaloStore.SaveAsync(session);
                        // StartAsync restores both local lists before launching network refreshes.
                        await ZaloApp.StartAsync(session);
                        _sessionBootstrapPending = false;
                        FilterConversations(SearchBox?.Text);
                        ShowFriends(SearchBox?.Text);
                        UpdateUserProfile();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainPage] Session restore error: {ex.Message}");
            }

            // If no valid session, navigate to LoginPage
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (ZaloApp.Session == null)
                {
                    Frame?.Navigate(typeof(LoginPage));
                }
            });
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            // The page stays cached while another page is active. Any conversation changes
            // made there must be reflected when the user returns.
            _conversationListDirty = true;
            _conversationSearchTimer?.Stop();
            ZaloApp.Conversations.CollectionChanged -= OnConversationsChanged;
            ZaloApp.Groups.CollectionChanged -= OnGroupsChanged;
            ZaloApp.GroupsLoadChanged -= OnGroupsLoadChanged;
            ZaloApp.FriendsLoadChanged -= OnFriendsLoadChanged;
            ZaloApp.CacheRestored -= OnCacheRestored;
            ZaloApp.StatusChanged -= OnStatusChanged;
            ZaloPhoneSync.SyncStatusChanged -= OnSyncStatusChanged;
        }

        private void RunOnUI(DispatchedHandler action)
        {
            if (Dispatcher == null) return;
            if (Dispatcher.HasThreadAccess)
            {
                try { action(); } catch { }
            }
            else
            {
                var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    try { action(); } catch { }
                });
            }
        }

        private void OnSyncStatusChanged(string message, bool isSyncing)
        {
            RunOnUI(() =>
            {
                if (SyncBannerSubtitle != null) SyncBannerSubtitle.Text = message ?? string.Empty;
                if (SyncBannerButton != null)
                {
                    SyncBannerButton.IsEnabled = false;
                    SyncBannerButton.Content = "Chưa hỗ trợ";
                }
            });
        }

        private async void SyncPhone_Click(object sender, RoutedEventArgs e)
        {
            if (ZaloPhoneSync.IsSyncing) return;
            await ZaloPhoneSync.StartSyncAsync(ZaloApp.Session);
        }

        private void OnStatusChanged()
        {
            RunOnUI(() =>
            {
                // Profile fields arrive asynchronously after the cached shell is shown.
                UpdateUserProfile();

                // Bound row properties update themselves. Only rebuild the filtered source when a
                // text search is active, since that search also scans cached message bodies.
                if (_selectedTabIndex == 0 && SearchBox != null && !string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    _conversationListDirty = true;
                    FilterConversations(SearchBox.Text);
                }
                UpdateConversationStatus();
                UpdateUnreadBadges();
            });
        }

        private void OnConversationsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            RunOnUI(() =>
            {
                _conversationListDirty = true;
                if (ZaloApp.IsRestoringCache) return;
                UpdateConversationsEmptyState();
                UpdateUnreadBadges();
                UpdateGroupList();
                if (_selectedTabIndex == 0 && SearchBox != null)
                    FilterConversations(SearchBox.Text);
            });
        }

        private void OnGroupsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            RunOnUI(() =>
            {
                if (ZaloApp.IsRestoringCache) return;
                UpdateGroupList();
            });
        }

        private void OnCacheRestored()
        {
            RunOnUI(() =>
            {
                _conversationListDirty = true;
                if (_selectedTabIndex == 0 && SearchBox != null)
                    FilterConversations(SearchBox.Text);
                UpdateGroupList();
                UpdateUnreadBadges();
            });
        }

        private void OnGroupsLoadChanged()
        {
            RunOnUI(() =>
            {
                UpdateGroupList();
            });
        }

        private void OnFriendsLoadChanged()
        {
            RunOnUI(() =>
            {
                _conversationListDirty = true;
                if (_selectedTabIndex == 0 && SearchBox != null)
                    FilterConversations(SearchBox.Text);
            });
        }

        private void UpdateConversationsEmptyState()
        {
            if (SearchBox != null && _selectedTabIndex == 0)
                FilterConversations(SearchBox.Text);
            UpdateConversationStatus();
        }

        private void UpdateConversationStatus()
        {
            if (ConversationStatusText == null) return;
            string status = ZaloApp.ConnectionStatus ?? string.Empty;
            if (string.IsNullOrWhiteSpace(status))
                status = ZaloApp.Session == null ? "" : "Đang kết nối để tải tin nhắn gần đây…";
            else if (status == "Đã kết nối")
                status = "Đã kết nối. Đang tải tin nhắn gần đây…";
            ConversationStatusText.Text = status;
        }

        private void UpdateUnreadBadges()
        {
            try
            {
                int totalUnread = ZaloApp.Conversations.Sum(c => c.Unread + (c.IsMarkedUnread && c.Unread == 0 ? 1 : 0));
                if (totalUnread > 0)
                {
                    Tab0BadgeText.Text = totalUnread > 99 ? "99+" : totalUnread.ToString();
                    Tab0BadgeBorder.Visibility = Visibility.Visible;
                }
                else
                {
                    Tab0BadgeBorder.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        private void UpdateUserProfile()
        {
            try
            {
                var session = ZaloApp.Session;
                string uid = session?.Uid ?? string.Empty;
                string displayName = "Người dùng Zalo";
                string avatarUrl = null;

                if (session != null)
                {
                    if (!string.IsNullOrEmpty(session.DisplayName)) displayName = session.DisplayName;
                    else if (!string.IsNullOrEmpty(session.Uid)) displayName = "Zalo ID: " + session.Uid;
                    avatarUrl = session.AvatarUrl;
                }

                if (!string.Equals(_profileUid, uid, StringComparison.Ordinal))
                {
                    _profileUid = uid;
                    _profileDisplayName = null;
                    _profileAvatarUrl = null;
                    _profileStatus = null;
                }

                if (!string.Equals(_profileDisplayName, displayName, StringComparison.Ordinal))
                {
                    _profileDisplayName = displayName;
                    ProfileNameText.Text = displayName;
                    ProfileInitial.Text = ZaloAvatar.InitialOf(displayName);
                    ProfileAvatarColor.Fill = ZaloAvatar.ColorFor(displayName);
                }

                string status = string.IsNullOrWhiteSpace(session?.ProfileStatus)
                    ? "Trạng thái..." : session.ProfileStatus;
                if (!string.Equals(_profileStatus, status, StringComparison.Ordinal))
                {
                    _profileStatus = status;
                    ProfileStatusPreview.Text = status;
                }

                if (!string.Equals(_profileAvatarUrl, avatarUrl, StringComparison.Ordinal))
                {
                    _profileAvatarUrl = avatarUrl;
                    ProfileAvatarImage.Fill = ZaloAvatar.ImageFor(avatarUrl);
                }
            }
            catch { }
        }

        private void ShowFriends(string filter, bool forceRefresh = true)
        {
            string text = (filter ?? string.Empty).Trim();
            if (!forceRefresh
                && string.Equals(_lastFriendFilter, text, StringComparison.Ordinal)
                && ReferenceEquals(_lastFriendSource, ZaloApp.Friends)
                && ReferenceEquals(_lastRenderedRemoteSearchResult, _remoteSearchResult))
                return;
            string needle = ZaloFormat.NormalizeSearch(text);
            if (!ReferenceEquals(_sortedFriendSource, ZaloApp.Friends) || _sortedFriends == null)
            {
                _sortedFriendSource = ZaloApp.Friends;
                _sortedFriends = (ZaloApp.Friends ?? new System.Collections.Generic.List<ZaloFriend>())
                    .OrderBy(f => f.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            var allFriends = _sortedFriends;
            var list = text.Length == 0
                ? allFriends.ToList()
                : allFriends.Where(f => ZaloFormat.SearchContains(f.Name, needle)
                                     || ZaloFormat.SearchContains(f.OriginalName, needle)
                                     || ZaloFormat.SearchContains(f.UserId, needle)).ToList();
            bool addedRemote = _remoteSearchResult != null && string.Equals(_remoteSearchQuery, text, StringComparison.OrdinalIgnoreCase)
                && !list.Any(f => f.UserId == _remoteSearchResult.UserId);
            if (addedRemote) list.Add(_remoteSearchResult);

            if (addedRemote)
                list = list.OrderBy(f => f.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase).ToList();
            string lastSection = null;
            foreach (ZaloFriend friend in list)
            {
                string name = (friend.Name ?? string.Empty).Trim();
                string normalizedInitial = name.Length == 0 ? string.Empty : ZaloFormat.NormalizeSearch(name.Substring(0, 1));
                string section = normalizedInitial.Length > 0 && char.IsLetter(normalizedInitial[0])
                    ? normalizedInitial[0].ToString() : "#";
                friend.SectionHeader = section;
                friend.ShowSectionHeader = !string.Equals(lastSection, section, StringComparison.Ordinal);
                lastSection = section;
            }

            FriendList.ItemsSource = list;
            _lastFriendFilter = text;
            _lastFriendSource = ZaloApp.Friends;
            _lastRenderedRemoteSearchResult = _remoteSearchResult;
            FriendCountText.Text = text.Length == 0 ? $"TẤT CẢ BẠN BÈ ({list.Count})"
                : (list.Count == 0 ? "KHÔNG TÌM THẤY BẠN BÈ" : $"KẾT QUẢ TÌM KIẾM ({list.Count})");
            EmptyFriendsPanel.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (text.Length > 0)
            {
                EmptyFriendsTitle.Text = "Không tìm thấy bạn bè";
                EmptyFriendsDescription.Text = "Thử tìm bằng tên khác hoặc xóa nội dung tìm kiếm.";
            }
            else
            {
                EmptyFriendsTitle.Text = "Chưa có bạn bè để hiển thị";
                EmptyFriendsDescription.Text = "Danh bạ chưa tải được hoặc tài khoản chưa có bạn bè. Chạm làm mới để thử lại.";
            }
        }

        private void UpdateQuickContacts()
        {
            var ids = ZaloSettingsService.Current.GetQuickContactIds(ZaloApp.Session?.Uid);
            var friends = (ZaloApp.Friends ?? new System.Collections.Generic.List<ZaloFriend>())
                .Where(f => ids.Contains(f.UserId)).ToList();
            QuickContactList.ItemsSource = friends;
            QuickContactCountText.Text = $"Liên hệ nhanh ({friends.Count}/10)";
            QuickContactList.Visibility = friends.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            QuickContactEmptyText.Visibility = friends.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void QuickContactAdd_Click(object sender, RoutedEventArgs e)
        {
            var friends = (ZaloApp.Friends ?? new System.Collections.Generic.List<ZaloFriend>())
                .Where(f => !string.IsNullOrWhiteSpace(f.UserId))
                .OrderBy(f => f.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (friends.Count == 0)
            {
                await new Windows.UI.Popups.MessageDialog("Danh bạ chưa tải xong hoặc hiện chưa có bạn bè.", "Liên hệ nhanh").ShowAsync();
                return;
            }

            var picker = new ListView
            {
                ItemsSource = friends,
                DisplayMemberPath = "Name",
                SelectionMode = ListViewSelectionMode.Multiple,
                MaxHeight = 360
            };
            var accountId = ZaloApp.Session?.Uid;
            var selectedIds = ZaloSettingsService.Current.GetQuickContactIds(accountId);
            foreach (var friend in friends.Where(f => selectedIds.Contains(f.UserId)))
                picker.SelectedItems.Add(friend);

            var dialog = new ContentDialog
            {
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Title = "Chọn liên hệ nhanh (tối đa 10)",
                Content = picker,
                PrimaryButtonText = "Lưu",
                SecondaryButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (picker.SelectedItems.Count > 10)
            {
                await new Windows.UI.Popups.MessageDialog("Bạn chỉ có thể ghim tối đa 10 liên hệ nhanh.", "Liên hệ nhanh").ShowAsync();
                return;
            }

            ZaloSettingsService.Current.SetQuickContactIds(accountId, picker.SelectedItems
                .OfType<ZaloFriend>().Select(f => f.UserId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList());
            UpdateQuickContacts();
        }

        private async void RefreshFriendRequestsAsync()
        {
            if (ZaloApp.Session == null) return;
            try
            {
                _friendRequests = await ZaloAuth.GetFriendRequestsAsync(ZaloApp.Session, CancellationToken.None);
                FriendRequestsTitle.Text = _friendRequests.Count == 0
                    ? "Lời mời kết bạn" : "Lời mời kết bạn (" + _friendRequests.Count + ")";
                FriendRequestsSubtitle.Text = _friendRequests.Count == 0
                    ? "Hiện chưa có lời mời mới" : "Xem và phản hồi lời mời mới";
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Friend requests unavailable: " + ex.Message);
                FriendRequestsSubtitle.Text = "Không tải được lời mời. Chạm để thử lại.";
            }
        }

        private async void RefreshSentFriendRequestsAsync()
        {
            if (ZaloApp.Session == null) return;
            try
            {
                _sentFriendRequests = await ZaloAuth.GetSentFriendRequestsAsync(ZaloApp.Session, CancellationToken.None);
                UpdateSentFriendRequestsSummary();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Sent friend requests unavailable: " + ex.Message);
                SentFriendRequestsSubtitle.Text = "Không tải được lời mời đã gửi. Chạm để thử lại.";
            }
        }

        private void UpdateSentFriendRequestsSummary()
        {
            SentFriendRequestsTitle.Text = _sentFriendRequests.Count == 0
                ? "Lời mời đã gửi" : "Lời mời đã gửi (" + _sentFriendRequests.Count + ")";
            SentFriendRequestsSubtitle.Text = _sentFriendRequests.Count == 0
                ? "Không có lời mời đang chờ" : "Chạm để xem hoặc thu hồi lời mời";
        }

        private async void SentFriendRequests_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            await OpenSentFriendRequestsAsync();
        }

        private async void SentFriendRequestsMenu_Click(object sender, RoutedEventArgs e)
        {
            await OpenSentFriendRequestsAsync();
        }

        private async System.Threading.Tasks.Task OpenSentFriendRequestsAsync()
        {
            if (ZaloApp.Session == null) return;
            await RefreshSentFriendRequestsForDialogAsync();
            await ShowSentFriendRequestsDialogAsync();
        }

        private async System.Threading.Tasks.Task RefreshSentFriendRequestsForDialogAsync()
        {
            try
            {
                _sentFriendRequests = await ZaloAuth.GetSentFriendRequestsAsync(ZaloApp.Session, CancellationToken.None);
                UpdateSentFriendRequestsSummary();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Sent friend request refresh failed: " + ex.Message);
                SentFriendRequestsSubtitle.Text = "Không làm mới được danh sách. Hãy thử lại.";
            }
        }

        private async System.Threading.Tasks.Task ShowSentFriendRequestsDialogAsync()
        {
            var rows = new StackPanel();
            if (_sentFriendRequests.Count == 0)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = "Bạn không có lời mời kết bạn nào đang chờ.",
                    Foreground = InactiveGrayBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 10, 4, 10)
                });
            }
            foreach (ZaloFriendRequest request in _sentFriendRequests.ToList())
            {
                var row = new Grid { Padding = new Thickness(10, 8, 10, 8), Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xF7, 0xF9, 0xFC)) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                details.Children.Add(new TextBlock { Text = request.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = TextDarkBrush, TextTrimming = TextTrimming.CharacterEllipsis });
                if (!string.IsNullOrWhiteSpace(request.Message))
                    details.Children.Add(new TextBlock { Text = request.Message, FontSize = 11.5, Foreground = InactiveGrayBrush, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(details, 0);
                row.Children.Add(details);
                var cancel = new Button { Content = "Thu hồi", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0), UseSystemFocusVisuals = false };
                var status = new TextBlock { FontSize = 11, Foreground = InactiveGrayBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
                cancel.Click += async (s, e) => await UndoSentFriendRequestAsync(request, status, cancel);
                Grid.SetColumn(cancel, 1);
                row.Children.Add(cancel);
                Grid.SetColumn(status, 1);
                rows.Children.Add(new StackPanel { Children = { row, status } });
            }
            StackPanelSpacingCompat.Apply(rows, 6);
            var dialog = new ContentDialog
            {
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Title = "Lời mời đã gửi",
                Content = new ScrollViewer { Content = rows, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = "Đóng",
                DefaultButton = ContentDialogButton.Primary
            };
            await dialog.ShowAsync();
        }

        private async System.Threading.Tasks.Task UndoSentFriendRequestAsync(ZaloFriendRequest request, TextBlock status, Button cancel)
        {
            cancel.IsEnabled = false;
            status.Visibility = Visibility.Visible;
            status.Text = "Đang thu hồi…";
            try
            {
                await ZaloAuth.UndoFriendRequestAsync(ZaloApp.Session, request.UserId, CancellationToken.None);
                _sentFriendRequests.Remove(request);
                UpdateSentFriendRequestsSummary();
                status.Text = "Đã thu hồi lời mời.";
                cancel.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Undo sent friend request failed: " + ex.Message);
                status.Text = "Không thu hồi được. Hãy thử lại.";
                cancel.IsEnabled = true;
            }
        }

        private async void FriendRequests_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            await OpenFriendRequestsAsync();
        }

        private async void FriendRequestsMenu_Click(object sender, RoutedEventArgs e)
        {
            await OpenFriendRequestsAsync();
        }

        private async System.Threading.Tasks.Task OpenFriendRequestsAsync()
        {
            if (ZaloApp.Session == null) return;
            if (_friendRequests == null || _friendRequests.Count == 0)
                await LoadFriendRequestsForDialogAsync();
            await ShowFriendRequestsDialogAsync();
        }

        private async System.Threading.Tasks.Task LoadFriendRequestsForDialogAsync()
        {
            try
            {
                _friendRequests = await ZaloAuth.GetFriendRequestsAsync(ZaloApp.Session, CancellationToken.None);
                FriendRequestsTitle.Text = _friendRequests.Count == 0
                    ? "Lời mời kết bạn" : "Lời mời kết bạn (" + _friendRequests.Count + ")";
                FriendRequestsSubtitle.Text = _friendRequests.Count == 0
                    ? "Hiện chưa có lời mời mới" : "Xem và phản hồi lời mời mới";
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Friend requests refresh failed: " + ex.Message);
                FriendRequestsSubtitle.Text = "Không tải được lời mời. Hãy thử lại sau.";
            }
        }

        private async System.Threading.Tasks.Task ShowFriendRequestsDialogAsync()
        {
            var rows = new StackPanel();
            if (_friendRequests.Count == 0)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = "Bạn chưa có lời mời kết bạn mới.",
                    Foreground = InactiveGrayBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 10, 4, 10)
                });
            }
            foreach (ZaloFriendRequest request in _friendRequests.ToList())
            {
                var row = new StackPanel { Padding = new Thickness(10, 8, 10, 10), Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xF7, 0xF9, 0xFC)) };
                var profile = new Grid { Height = 48 };
                profile.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                profile.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var avatar = new Border
                {
                    Width = 42,
                    Height = 42,
                    CornerRadius = new CornerRadius(21),
                    Background = ZaloAvatar.ImageFor(request.Avatar) ?? ZaloAvatar.ColorFor(request.Name),
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (ZaloAvatar.ImageFor(request.Avatar) == null)
                    avatar.Child = new TextBlock
                    {
                        Text = ZaloAvatar.InitialOf(request.Name),
                        Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                Grid.SetColumn(avatar, 0);
                profile.Children.Add(avatar);
                var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                details.Children.Add(new TextBlock { Text = request.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = TextDarkBrush, TextTrimming = TextTrimming.CharacterEllipsis });
                string note = !string.IsNullOrWhiteSpace(request.Message) ? request.Message : request.Status;
                if (!string.IsNullOrWhiteSpace(note))
                    details.Children.Add(new TextBlock { Text = note, FontSize = 11.5, Foreground = InactiveGrayBrush, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(details, 1);
                profile.Children.Add(details);
                row.Children.Add(profile);

                var status = new TextBlock { FontSize = 11, Foreground = InactiveGrayBrush, Margin = new Thickness(0, 4, 0, 2) };
                row.Children.Add(status);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var reject = new Button { Content = "Từ chối", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0), UseSystemFocusVisuals = false };
                var accept = new Button { Content = "Đồng ý", Padding = new Thickness(12, 4, 12, 4), Background = ActiveBlueBrush, Foreground = new SolidColorBrush(Windows.UI.Colors.White), UseSystemFocusVisuals = false };
                accept.Click += async (s, e) => await RespondToFriendRequestAsync(request, true, status, accept, reject);
                reject.Click += async (s, e) => await RespondToFriendRequestAsync(request, false, status, accept, reject);
                actions.Children.Add(reject);
                actions.Children.Add(accept);
                row.Children.Add(actions);
                rows.Children.Add(new Border { Child = row, BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xE6, 0xEB, 0xF1)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) });
            }

            StackPanelSpacingCompat.Apply(rows, 6);

            var dialog = new ContentDialog
            {
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Title = "Lời mời kết bạn",
                Content = new ScrollViewer { Content = rows, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = "Đóng",
                DefaultButton = ContentDialogButton.Primary
            };
            await dialog.ShowAsync();
        }

        private async System.Threading.Tasks.Task RespondToFriendRequestAsync(ZaloFriendRequest request, bool accept,
            TextBlock status, Button acceptButton, Button rejectButton)
        {
            acceptButton.IsEnabled = false;
            rejectButton.IsEnabled = false;
            status.Text = accept ? "Đang chấp nhận…" : "Đang từ chối…";
            try
            {
                if (accept)
                    await ZaloAuth.AcceptFriendRequestAsync(ZaloApp.Session, request.UserId, CancellationToken.None);
                else
                    await ZaloAuth.RejectFriendRequestAsync(ZaloApp.Session, request.UserId, CancellationToken.None);
                _friendRequests.Remove(request);
                FriendRequestsTitle.Text = _friendRequests.Count == 0
                    ? "Lời mời kết bạn" : "Lời mời kết bạn (" + _friendRequests.Count + ")";
                FriendRequestsSubtitle.Text = _friendRequests.Count == 0
                    ? "Hiện chưa có lời mời mới" : "Xem và phản hồi lời mời mới";
                status.Text = accept ? "Đã chấp nhận lời mời" : "Đã từ chối lời mời";
                if (accept) _ = RefreshFriendsAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Friend request response failed: " + ex.Message);
                status.Text = "Không thực hiện được. Hãy thử lại.";
                acceptButton.IsEnabled = true;
                rejectButton.IsEnabled = true;
            }
        }

        private void UpdateGroupList()
        {
            string query = SearchBox == null ? string.Empty : SearchBox.Text.Trim();
            string needle = ZaloFormat.NormalizeSearch(query);
            var groups = string.IsNullOrEmpty(query)
                ? ZaloApp.Groups.ToList()
                : ZaloApp.Groups.Where(g => ZaloFormat.SearchContains(g.Name, needle)
                                          || ZaloFormat.SearchContains(g.LastText, needle)).ToList();
            GroupList.ItemsSource = groups;
            EmptyGroupText.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (groups.Count == 0)
            {
                EmptyGroupText.Text = query.Length > 0 ? "Không tìm thấy nhóm phù hợp."
                    : (ZaloApp.GroupsLoadFailed
                    ? "Không tải được nhóm. Hãy thử lại sau."
                    : (ZaloApp.GroupsLoaded ? "Bạn chưa tham gia nhóm nào." : "Đang tải danh sách nhóm..."));
            }
        }

        // ==================== SUBTABS: TAB 0 (TIN NHẮN) ====================

        private void SubTabPriority_Click(object sender, RoutedEventArgs e)
        {
            _showOtherConversations = false;
            PriorityGrid.Visibility = Visibility.Visible;
            PriorityIndicator.Visibility = Visibility.Visible;
            OtherIndicator.Visibility = Visibility.Collapsed;
            PriorityTabText.FontWeight = FontWeights.SemiBold;
            PriorityTabText.Foreground = TextDarkBrush;
            OtherTabText.FontWeight = FontWeights.Normal;
            OtherTabText.Foreground = InactiveGrayBrush;
            FilterConversations(SearchBox.Text);
        }

        private void SubTabOther_Click(object sender, RoutedEventArgs e)
        {
            _showOtherConversations = true;
            PriorityGrid.Visibility = Visibility.Visible;
            PriorityIndicator.Visibility = Visibility.Collapsed;
            OtherIndicator.Visibility = Visibility.Visible;
            PriorityTabText.FontWeight = FontWeights.Normal;
            PriorityTabText.Foreground = InactiveGrayBrush;
            OtherTabText.FontWeight = FontWeights.SemiBold;
            OtherTabText.Foreground = TextDarkBrush;
            FilterConversations(SearchBox.Text);
        }

        // ==================== CONTACT FILTERS ====================
        private void SubTabAll_Click(object sender, RoutedEventArgs e)
        {
            FriendList.Visibility = Visibility.Visible;
            ContactQuickActions.Visibility = Visibility.Collapsed;
            GroupsGrid.Visibility = Visibility.Collapsed;
            OAGrid.Visibility = Visibility.Collapsed;
            SetContactChipVisuals(0);
            ShowFriends(SearchBox.Text);
        }

        private void SubTabFriends_Click(object sender, RoutedEventArgs e)
        {
            FriendList.Visibility = Visibility.Visible;
            ContactQuickActions.Visibility = Visibility.Collapsed;
            GroupsGrid.Visibility = Visibility.Collapsed;
            OAGrid.Visibility = Visibility.Collapsed;
            SetContactChipVisuals(1);
            ShowFriends(SearchBox.Text);
        }

        private void SubTabGroups_Click(object sender, RoutedEventArgs e)
        {
            FriendList.Visibility = Visibility.Collapsed;
            GroupsGrid.Visibility = Visibility.Visible;
            OAGrid.Visibility = Visibility.Collapsed;
            SetContactChipVisuals(-1);
            UpdateGroupList();
        }

        private void SubTabOA_Click(object sender, RoutedEventArgs e)
        {
            FriendList.Visibility = Visibility.Collapsed;
            GroupsGrid.Visibility = Visibility.Collapsed;
            OAGrid.Visibility = Visibility.Visible;
            SetContactChipVisuals(2);
        }

        private void ContactGroupsHeader_Click(object sender, RoutedEventArgs e)
        {
            SubTabGroups_Click(sender, e);
        }

        private void SetContactChipVisuals(int selected)
        {
            Border[] chips = { AllChip, FriendsChip, OAChip };
            TextBlock[] labels = { AllTabText, FriendsTabText, OATabText };
            for (int i = 0; i < chips.Length; i++)
            {
                bool active = i == selected;
                chips[i].Background = active
                    ? new SolidColorBrush(Color.FromArgb(0xFF, 0xE5, 0xE7, 0xEB))
                    : new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
                chips[i].BorderBrush = active
                    ? new SolidColorBrush(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF))
                    : new SolidColorBrush(Color.FromArgb(0xFF, 0xDD, 0xDD, 0xDD));
                chips[i].BorderThickness = active ? new Thickness(0) : new Thickness(1);
                labels[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                labels[i].Foreground = active ? TextDarkBrush : InactiveGrayBrush;
            }
        }

        private void AddFriendQuick_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            AddFriend_Click(sender, new RoutedEventArgs());
        }

        private void GroupsQuick_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            SubTabGroups_Click(sender, new RoutedEventArgs());
        }

        private void CreateGroupQuick_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            CreateGroup_Click(sender, new RoutedEventArgs());
        }

        // ==================== SEARCH & CLICKS ====================

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = SearchBox.Text.Trim();
            if (!string.Equals(query, _remoteSearchQuery, StringComparison.OrdinalIgnoreCase))
            {
                _remoteSearchResult = null;
                _remoteSearchQuery = string.Empty;
            }
            if (_selectedTabIndex == 0)
            {
                _pendingConversationQuery = query;
                _conversationSearchTimer.Stop();
                _conversationSearchTimer.Start();
            }
            else if (_selectedTabIndex == 1)
            {
                if (GroupsGrid.Visibility == Visibility.Visible) UpdateGroupList();
                else if (FriendList.Visibility == Visibility.Visible) ShowFriends(query);
            }
        }

        private void FilterConversations(string query)
        {
            string text = (query ?? string.Empty).Trim();
            UpdateStrangerInboxHeader(text);
            if (!_conversationListDirty
                && string.Equals(_lastConversationQuery, text, StringComparison.Ordinal)
                && _lastOtherConversationView == _showOtherConversations)
                return;
            string needle = ZaloFormat.NormalizeSearch(text);
            var otherKeys = new System.Collections.Generic.HashSet<string>(
                ZaloSettingsService.Current.GetOtherConversationKeys(ZaloApp.Session?.Uid),
                StringComparer.Ordinal);
            var matches = ZaloApp.Conversations.Where(c =>
            {
                bool archived = ZaloApp.IsArchived(c);
                if (text.Length == 0 && !archived && ZaloApp.IsHidden(c)) return false;
                // Keep the Android-style two-tab layout. Archived threads remain
                // reachable in Khác so removing the extra tab never hides data.
                bool isOther = archived || otherKeys.Contains(c.Key);
                if (isOther != _showOtherConversations) return false;
                if (text.Length == 0 && !_showOtherConversations && IsStrangerConversation(c)) return false;
                return text.Length == 0
                    || ZaloFormat.SearchContains(c.Name, needle)
                    || ZaloFormat.SearchContains(c.LastText, needle)
                    || c.Messages.Any(m => ZaloFormat.SearchContains(m.Text, needle));
            })
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.LastTs)
            .ToList();
            ConversationList.ItemsSource = matches;
            _lastConversationQuery = text;
            _lastOtherConversationView = _showOtherConversations;
            _conversationListDirty = false;
            bool searching = text.Length > 0;
            Visibility showEmptyPanel = matches.Count == 0 && !searching
                && StrangerInboxHeader.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            EmptyConversationPanel.Visibility = showEmptyPanel;
            NoConversationSearchPanel.Visibility = matches.Count == 0 && searching
                ? Visibility.Visible : Visibility.Collapsed;
            if (!searching)
            {
                if (_sessionBootstrapPending)
                {
                    EmptyConversationTitle.Text = "Đang mở hộp thư…";
                    EmptyConversationDescription.Text = "Đang khôi phục tin nhắn và danh bạ đã lưu trên máy.";
                    EmptyConversationContactsButton.Visibility = Visibility.Collapsed;
                    ConversationStatusText.Visibility = Visibility.Collapsed;
                    return;
                }
                ConversationStatusText.Visibility = Visibility.Visible;
                EmptyConversationTitle.Text = _showOtherConversations
                    ? "Chưa có cuộc trò chuyện trong mục Khác"
                    : "Chưa có cuộc trò chuyện nào";
                EmptyConversationDescription.Text = _showOtherConversations
                    ? "Nhấn giữ một cuộc trò chuyện rồi chọn Chuyển sang Khác để lưu tại đây."
                    : "Chọn bạn bè trong Danh bạ để bắt đầu trò chuyện";
                EmptyConversationContactsButton.Visibility = _showOtherConversations ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private static bool IsStrangerConversation(ZaloConversation conversation)
        {
            return conversation != null && !conversation.IsGroup && ZaloApp.FriendsLoaded
                && !ZaloApp.IsFriend(conversation.ThreadId);
        }

        private void UpdateStrangerInboxHeader(string query)
        {
            if (StrangerInboxHeader == null) return;
            if (_selectedTabIndex != 0 || _showOtherConversations
                || !string.IsNullOrWhiteSpace(query) || !ZaloApp.FriendsLoaded)
            {
                StrangerInboxHeader.Visibility = Visibility.Collapsed;
                return;
            }

            var otherKeys = new System.Collections.Generic.HashSet<string>(
                ZaloSettingsService.Current.GetOtherConversationKeys(ZaloApp.Session?.Uid), StringComparer.Ordinal);
            int count = ZaloApp.Conversations.Count(c => !ZaloApp.IsArchived(c)
                && !otherKeys.Contains(c.Key) && IsStrangerConversation(c));
            StrangerInboxHeader.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            StrangerInboxCountText.Text = count > 99 ? "99+" : count.ToString();
        }

        private void OpenStrangerInbox_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            e.Handled = true;
            Frame.Navigate(typeof(StrangerMessagesPage));
        }

        private async void SearchBox_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return;
            if (_selectedTabIndex == 0)
            {
                string messageQuery = SearchBox.Text.Trim();
                if (messageQuery.Length == 0) return;
                e.Handled = true;
                Frame.Navigate(typeof(SearchResultsPage), messageQuery);
                return;
            }
            if (_selectedTabIndex != 1 || FriendList.Visibility != Visibility.Visible) return;
            e.Handled = true;

            string query = SearchBox.Text.Trim();
            if (query.Length == 0 || ZaloApp.Session == null) return;

            _remoteSearchQuery = query;
            _remoteSearchResult = null;
            FriendCountText.Text = "ĐANG TÌM TRÊN ZALO...";
            try
            {
                const string phoneChars = "+- ()0123456789";
                bool phoneQuery = query.All(c => phoneChars.IndexOf(c) >= 0) && query.Any(char.IsDigit);
                ZaloFriend result;
                if (phoneQuery)
                {
                    string digits = new string(query.Where(char.IsDigit).ToArray());
                    if (digits.Length < 9)
                    {
                        FriendCountText.Text = "Nhập đủ số điện thoại để tìm kiếm.";
                        return;
                    }
                    result = await ZaloAuth.FindFriendByPhoneAsync(ZaloApp.Session, query, CancellationToken.None);
                }
                else if (!query.Any(char.IsWhiteSpace))
                {
                    result = await ZaloAuth.FindFriendByUsernameAsync(ZaloApp.Session, query, CancellationToken.None);
                }
                else
                {
                    FriendCountText.Text = "Tìm tên trong bạn bè; tìm trực tuyến bằng số điện thoại hoặc tên người dùng.";
                    return;
                }

                if (!string.Equals(SearchBox.Text.Trim(), query, StringComparison.OrdinalIgnoreCase)
                    || _selectedTabIndex != 1 || FriendList.Visibility != Visibility.Visible) return;
                _remoteSearchResult = result;
                ShowFriends(query);
            }
            catch
            {
                FriendCountText.Text = "Không tìm thấy hoặc không kết nối được. Kiểm tra số điện thoại/tên người dùng.";
            }
        }

        private async void ConversationList_ItemClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                if (e.ClickedItem is ZaloConversation conv)
                {
                    bool navigated = Frame.Navigate(typeof(ChatPage), conv);
                    if (!navigated)
                    {
                        await new Windows.UI.Popups.MessageDialog("Không thể chuyển sang giao diện cuộc trò chuyện.", "Lỗi điều hướng").ShowAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                ZaloLog.Add("ConversationList_ItemClick error: " + ex);
                await new Windows.UI.Popups.MessageDialog("Lỗi mở cuộc trò chuyện: " + ex.Message, "Lỗi điều hướng").ShowAsync();
            }
        }

        private void Conversation_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e)
        {
            ShowConversationActions(sender as FrameworkElement);
            e.Handled = true;
        }

        private void Conversation_RightTapped(object sender, Windows.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            ShowConversationActions(sender as FrameworkElement);
            e.Handled = true;
        }

        private void ShowConversationActions(FrameworkElement target)
        {
            ZaloConversation conversation = target == null ? null : target.DataContext as ZaloConversation;
            if (conversation == null) return;
            var menu = new MenuFlyout();
            var unread = new MenuFlyoutItem { Text = conversation.IsMarkedUnread ? "Bỏ dấu chưa đọc" : "Đánh dấu chưa đọc" };
            unread.Click += async (s, e) => await SetConversationUnreadMarkAsync(conversation, !conversation.IsMarkedUnread);
            menu.Items.Add(unread);
            var mute = new MenuFlyoutItem { Text = conversation.IsMuted ? "Bật thông báo" : "Tắt thông báo" };
            mute.Click += async (s, e) => await SetConversationMutedAsync(conversation, !conversation.IsMuted);
            menu.Items.Add(mute);
            var pin = new MenuFlyoutItem { Text = conversation.IsPinned ? "Bỏ ghim" : "Ghim lên đầu" };
            pin.Click += async (s, e) =>
            {
                try { await ZaloApp.SetPinnedAsync(conversation, !conversation.IsPinned); }
                catch (Exception ex)
                {
                    ZaloLog.Add("Pin conversation failed: " + ex.Message);
                    await new Windows.UI.Popups.MessageDialog("Không cập nhật được trạng thái ghim. Hãy thử lại sau.", "Ghim cuộc trò chuyện").ShowAsync();
                }
            };
            menu.Items.Add(pin);
            var archive = new MenuFlyoutItem { Text = ZaloApp.IsArchived(conversation) ? "Bỏ lưu trữ" : "Lưu trữ" };
            archive.Click += async (s, e) =>
            {
                try
                {
                    await ZaloApp.SetArchivedAsync(conversation, !ZaloApp.IsArchived(conversation));
                    _conversationListDirty = true;
                    FilterConversations(SearchBox.Text);
                }
                catch (Exception ex)
                {
                    ZaloLog.Add("Archive conversation failed: " + ex.Message);
                    await new Windows.UI.Popups.MessageDialog("Không cập nhật được trạng thái lưu trữ. Hãy thử lại sau.", "Lưu trữ trò chuyện").ShowAsync();
                }
            };
            menu.Items.Add(archive);
            var moveCategory = new MenuFlyoutItem
            {
                Text = _showOtherConversations ? "Chuyển sang Ưu tiên" : "Chuyển sang Khác"
            };
            moveCategory.Click += (s, e) => MoveConversationCategory(conversation, !_showOtherConversations);
            menu.Items.Add(moveCategory);
            if (conversation.CanDeleteFromInbox)
            {
                var delete = new MenuFlyoutItem { Text = "Xóa cuộc trò chuyện" };
                delete.Click += async (s, e) => await DeleteConversationFromInboxAsync(conversation);
                menu.Items.Add(delete);
            }
            menu.ShowAt(target);
        }

        private void MoveConversationCategory(ZaloConversation conversation, bool moveToOther)
        {
            if (conversation == null || string.IsNullOrWhiteSpace(conversation.Key) || ZaloApp.Session == null) return;
            var keys = ZaloSettingsService.Current.GetOtherConversationKeys(ZaloApp.Session.Uid);
            keys.RemoveAll(key => string.Equals(key, conversation.Key, StringComparison.Ordinal));
            if (moveToOther) keys.Add(conversation.Key);
            ZaloSettingsService.Current.SetOtherConversationKeys(ZaloApp.Session.Uid, keys);
            _conversationListDirty = true;
            FilterConversations(SearchBox.Text);
        }

        private async System.Threading.Tasks.Task SetConversationUnreadMarkAsync(ZaloConversation conversation, bool marked)
        {
            try { await ZaloApp.SetUnreadMarkAsync(conversation, marked); }
            catch (Exception ex)
            {
                ZaloLog.Add("Unread mark update failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không cập nhật được dấu chưa đọc. Hãy thử lại sau.", "Cuộc trò chuyện").ShowAsync();
            }
        }

        private async System.Threading.Tasks.Task SetConversationMutedAsync(ZaloConversation conversation, bool muted)
        {
            try { await ZaloApp.SetMutedAsync(conversation, muted); }
            catch (Exception ex)
            {
                ZaloLog.Add("Conversation mute update failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không cập nhật được thông báo cho cuộc trò chuyện này. Hãy thử lại sau.", "Thông báo cuộc trò chuyện").ShowAsync();
            }
        }

        private async System.Threading.Tasks.Task DeleteConversationFromInboxAsync(ZaloConversation conversation)
        {
            var confirm = new Windows.UI.Popups.MessageDialog(
                "Xóa cuộc trò chuyện này khỏi hộp thư của bạn? Tin nhắn nhóm không bị rời khỏi nhóm.",
                "Xóa cuộc trò chuyện");
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Xóa"));
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Hủy"));
            confirm.DefaultCommandIndex = 1;
            if ((await confirm.ShowAsync()).Label != "Xóa") return;
            try { await ZaloApp.DeleteConversationAsync(conversation); }
            catch (Exception ex)
            {
                ZaloLog.Add("Delete conversation failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không xóa được cuộc trò chuyện. Hãy thử lại sau.", "Xóa cuộc trò chuyện").ShowAsync();
            }
        }

        private async void FriendList_ItemClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                if (e.ClickedItem is ZaloFriend friend)
                {
                    var conv = ZaloApp.OpenFriend(friend);
                    bool navigated = Frame.Navigate(typeof(ChatPage), conv);
                    if (!navigated)
                    {
                        await new Windows.UI.Popups.MessageDialog("Không thể chuyển sang giao diện cuộc trò chuyện.", "Lỗi điều hướng").ShowAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                ZaloLog.Add("FriendList_ItemClick error: " + ex);
                await new Windows.UI.Popups.MessageDialog("Lỗi mở cuộc trò chuyện với bạn bè: " + ex.Message, "Lỗi điều hướng").ShowAsync();
            }
        }

        private async void QuickContact_ItemTapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                var row = sender as FrameworkElement;
                var friend = row == null ? null : row.DataContext as ZaloFriend;
                if (friend == null) return;
                var conv = ZaloApp.OpenFriend(friend);
                bool navigated = Frame.Navigate(typeof(ChatPage), conv);
                if (!navigated)
                {
                    await new Windows.UI.Popups.MessageDialog("Không thể chuyển sang giao diện cuộc trò chuyện.", "Lỗi điều hướng").ShowAsync();
                }
                e.Handled = true;
            }
            catch (Exception ex)
            {
                ZaloLog.Add("QuickContact_ItemTapped error: " + ex);
                await new Windows.UI.Popups.MessageDialog("Lỗi mở cuộc trò chuyện nhanh: " + ex.Message, "Lỗi điều hướng").ShowAsync();
            }
        }

        private void Friend_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e)
        {
            e.Handled = true;
            ShowFriendActions(sender as FrameworkElement);
        }

        private void Friend_RightTapped(object sender, Windows.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            ShowFriendActions(sender as FrameworkElement);
        }

        private void ShowFriendActions(FrameworkElement target)
        {
            ZaloFriend friend = target == null ? null : target.DataContext as ZaloFriend;
            if (friend == null || ZaloApp.Friends == null
                || !ZaloApp.Friends.Any(f => f.UserId == friend.UserId)) return;
            var menu = new MenuFlyout();
            var profile = new MenuFlyoutItem { Text = "Xem trang cá nhân" };
            profile.Click += (s, e) => Frame.Navigate(typeof(FriendProfilePage), friend);
            menu.Items.Add(profile);
            var change = new MenuFlyoutItem { Text = "Đổi biệt danh" };
            change.Click += async (s, e) => await ChangeFriendAliasAsync(friend);
            menu.Items.Add(change);
            if (!string.IsNullOrWhiteSpace(friend.Alias))
            {
                var remove = new MenuFlyoutItem { Text = "Xóa biệt danh" };
                remove.Click += async (s, e) => await RemoveFriendAliasAsync(friend);
                menu.Items.Add(remove);
            }
            string accountId = ZaloApp.Session == null ? null : ZaloApp.Session.Uid;
            List<string> blockedIds = ZaloSettingsService.Current.GetBlockedUserIds(accountId);
            bool isBlocked = blockedIds.Contains(friend.UserId);
            var block = new MenuFlyoutItem { Text = isBlocked ? "Bỏ chặn người dùng" : "Chặn người dùng" };
            block.Click += async (s, e) => await SetFriendBlockedAsync(friend, !isBlocked);
            menu.Items.Add(block);
            var delete = new MenuFlyoutItem { Text = "Xóa bạn khỏi danh bạ" };
            delete.Click += async (s, e) => await RemoveFriendAsync(friend);
            menu.Items.Add(delete);
            menu.ShowAt(target);
        }

        private async System.Threading.Tasks.Task SetFriendBlockedAsync(ZaloFriend friend, bool blocked)
        {
            if (friend == null) return;
            if (blocked)
            {
                var confirm = new Windows.UI.Popups.MessageDialog(
                    "Chặn " + friend.Name + "? Người này sẽ không thể nhắn tin hoặc gọi cho bạn. Bạn có thể bỏ chặn sau.",
                    "Chặn người dùng");
                confirm.Commands.Add(new Windows.UI.Popups.UICommand("Chặn"));
                confirm.Commands.Add(new Windows.UI.Popups.UICommand("Hủy"));
                confirm.DefaultCommandIndex = 1;
                if ((await confirm.ShowAsync()).Label != "Chặn") return;
            }

            try
            {
                await ZaloAuth.SetFriendBlockedAsync(ZaloApp.Session, friend.UserId, blocked, CancellationToken.None);
                string accountId = ZaloApp.Session == null ? null : ZaloApp.Session.Uid;
                List<string> ids = ZaloSettingsService.Current.GetBlockedUserIds(accountId);
                if (blocked && !ids.Contains(friend.UserId)) ids.Add(friend.UserId);
                else if (!blocked) ids.RemoveAll(id => id == friend.UserId);
                ZaloSettingsService.Current.SetBlockedUserIds(accountId, ids);
                await new Windows.UI.Popups.MessageDialog(
                    blocked ? "Đã chặn người dùng." : "Đã bỏ chặn người dùng.", "Danh bạ").ShowAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add((blocked ? "Block" : "Unblock") + " friend failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog(
                    blocked ? "Không chặn được người dùng. Hãy thử lại sau." : "Không bỏ chặn được người dùng. Hãy thử lại sau.",
                    "Danh bạ").ShowAsync();
            }
        }

        private async System.Threading.Tasks.Task RemoveFriendAsync(ZaloFriend friend)
        {
            var confirm = new Windows.UI.Popups.MessageDialog(
                "Xóa " + friend.Name + " khỏi danh bạ bạn bè? Cuộc trò chuyện hiện có vẫn được giữ.",
                "Xóa bạn bè");
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Xóa bạn"));
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Hủy"));
            confirm.DefaultCommandIndex = 1;
            if ((await confirm.ShowAsync()).Label != "Xóa bạn") return;

            try
            {
                await ZaloAuth.RemoveFriendAsync(ZaloApp.Session, friend.UserId, CancellationToken.None);
                ZaloApp.Friends.RemoveAll(f => f.UserId == friend.UserId);
                string accountId = ZaloApp.Session == null ? null : ZaloApp.Session.Uid;
                var quickIds = ZaloSettingsService.Current.GetQuickContactIds(accountId);
                quickIds.RemoveAll(id => id == friend.UserId);
                ZaloSettingsService.Current.SetQuickContactIds(accountId, quickIds);
                if (_remoteSearchResult != null && _remoteSearchResult.UserId == friend.UserId)
                {
                    _remoteSearchResult = null;
                    _remoteSearchQuery = string.Empty;
                }
                ShowFriends(SearchBox.Text);
                UpdateQuickContacts();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Remove friend failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không xóa được bạn khỏi danh bạ. " + ex.Message, "Xóa bạn bè").ShowAsync();
            }
        }

        private async System.Threading.Tasks.Task ChangeFriendAliasAsync(ZaloFriend friend)
        {
            var input = new TextBox
            {
                Text = friend.Alias ?? string.Empty,
                PlaceholderText = friend.OriginalName ?? friend.Name,
                MaxLength = 40
            };
            var dialog = new ContentDialog
            {
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Title = "Đổi biệt danh bạn bè",
                Content = input,
                PrimaryButtonText = "Lưu",
                SecondaryButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            string alias = (input.Text ?? string.Empty).Trim();
            if (alias.Length == 0)
            {
                await new Windows.UI.Popups.MessageDialog("Biệt danh không được để trống. Chọn Xóa biệt danh để khôi phục tên gốc.", "Biệt danh").ShowAsync();
                return;
            }
            try
            {
                await ZaloAuth.SetFriendAliasAsync(ZaloApp.Session, friend.UserId, alias, CancellationToken.None);
                friend.Alias = alias;
                friend.Name = alias;
                var conversation = ZaloApp.Find("u:" + friend.UserId);
                if (conversation != null) conversation.Name = alias;
                ShowFriends(SearchBox.Text);
                UpdateQuickContacts();
                await ZaloApp.SaveCachedConversationsAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Friend alias update failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không đổi được biệt danh. " + ex.Message, "Biệt danh").ShowAsync();
            }
        }

        private async System.Threading.Tasks.Task RemoveFriendAliasAsync(ZaloFriend friend)
        {
            try
            {
                await ZaloAuth.RemoveFriendAliasAsync(ZaloApp.Session, friend.UserId, CancellationToken.None);
                friend.Alias = string.Empty;
                friend.Name = friend.OriginalName ?? friend.Name;
                var conversation = ZaloApp.Find("u:" + friend.UserId);
                if (conversation != null) conversation.Name = friend.Name;
                ShowFriends(SearchBox.Text);
                UpdateQuickContacts();
                await ZaloApp.SaveCachedConversationsAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Friend alias removal failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không xóa được biệt danh. " + ex.Message, "Biệt danh").ShowAsync();
            }
        }

        private void GoToContacts_Click(object sender, RoutedEventArgs e)
        {
            SelectTab(1);
        }

        private async void CallFriend_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Windows.UI.Popups.MessageDialog("Gọi thoại Zalo chưa được nối với dịch vụ gọi trên Windows 10 Mobile.", "Gọi thoại");
            await dialog.ShowAsync();
        }

        private async void VideoCallFriend_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Windows.UI.Popups.MessageDialog("Gọi video Zalo chưa được nối với dịch vụ gọi trên Windows 10 Mobile.", "Gọi video");
            await dialog.ShowAsync();
        }

        private async void OpenCloud_Click(object sender, RoutedEventArgs e)
        {
            await new Windows.UI.Popups.MessageDialog(
                "Cloud cá nhân chưa khả dụng vì ứng dụng chưa có endpoint tải lên và liệt kê tệp được xác nhận. Bạn vẫn có thể gửi ảnh, video và tệp trong cuộc trò chuyện.",
                "Cloud của tôi").ShowAsync();
        }

        private async void RefreshRecommendations_Click(object sender, RoutedEventArgs e)
        {
            _recommendationsLoaded = false;
            await RefreshFriendRecommendationsAsync();
        }

        private async System.Threading.Tasks.Task RefreshFriendRecommendationsAsync()
        {
            if (_recommendationsLoading || ZaloApp.Session == null) return;
            _recommendationsLoading = true;
            RecommendationsStatusText.Text = "Đang tải gợi ý kết bạn…";
            try
            {
                var suggested = await ZaloAuth.GetRecommendedFriendsAsync(ZaloApp.Session, CancellationToken.None);
                var existingIds = new System.Collections.Generic.HashSet<string>(
                    (ZaloApp.Friends ?? new System.Collections.Generic.List<ZaloFriend>())
                        .Select(friend => friend.UserId).Where(id => !string.IsNullOrWhiteSpace(id)),
                    System.StringComparer.Ordinal);
                foreach (ZaloFriendRequest request in _sentFriendRequests)
                    if (!string.IsNullOrWhiteSpace(request.UserId)) existingIds.Add(request.UserId);
                _recommendedFriends = suggested.Where(friend => !existingIds.Contains(friend.UserId))
                    .GroupBy(friend => friend.UserId).Select(group => group.First()).Take(20).ToList();
                RecommendedFriendsList.ItemsSource = _recommendedFriends;
                RecommendedFriendsList.Visibility = _recommendedFriends.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                RecommendationsStatusText.Text = _recommendedFriends.Count == 0
                    ? "Chưa có gợi ý mới. Bạn có thể tìm bạn bằng số điện thoại hoặc Zalo ID."
                    : "Gợi ý bạn bè dành cho tài khoản của bạn";
                _recommendationsLoaded = true;
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Friend recommendations unavailable: " + ex.Message);
                RecommendedFriendsList.Visibility = Visibility.Collapsed;
                RecommendationsStatusText.Text = "Không tải được gợi ý. Chạm Làm mới để thử lại.";
            }
            finally
            {
                _recommendationsLoading = false;
            }
        }

        private async void RecommendedFriendAdd_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            string userId = button == null ? string.Empty : button.Tag as string;
            ZaloFriend friend = _recommendedFriends.FirstOrDefault(item => item.UserId == userId);
            if (friend == null || ZaloApp.Session == null) return;
            var noteInput = new TextBox
            {
                Header = "Lời nhắn",
                Text = "Xin chào, mình kết bạn nhé!",
                MaxLength = 200,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 64
            };
            var dialog = new ContentDialog
            {
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Title = "Kết bạn với " + friend.Name,
                Content = noteInput,
                PrimaryButtonText = "Gửi lời mời",
                CloseButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            button.IsEnabled = false;
            try
            {
                await ZaloAuth.SendFriendRequestAsync(ZaloApp.Session, friend.UserId, noteInput.Text.Trim(), CancellationToken.None);
                _recommendedFriends.Remove(friend);
                RecommendedFriendsList.ItemsSource = null;
                RecommendedFriendsList.ItemsSource = _recommendedFriends;
                RecommendedFriendsList.Visibility = _recommendedFriends.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                RecommendationsStatusText.Text = _recommendedFriends.Count > 0
                    ? "Đã gửi lời mời. Đây là những gợi ý khác dành cho bạn."
                    : "Đã gửi lời mời kết bạn.";
                _sentFriendRequests.Add(new ZaloFriendRequest { UserId = friend.UserId, Name = friend.Name, Avatar = friend.Avatar });
                UpdateSentFriendRequestsSummary();
            }
            catch (Exception ex)
            {
                button.IsEnabled = true;
                ZaloLog.Add("Recommended friend request failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Không gửi được lời mời kết bạn: " + ex.Message, "Kết bạn").ShowAsync();
            }
        }

        private async void QrScan_Click(object sender, RoutedEventArgs e)
        {
            await new Windows.UI.Popups.MessageDialog(
                "Ứng dụng chưa có bộ giải mã QR từ camera. Nút này không mở màn đăng nhập để tránh làm gián đoạn phiên Zalo hiện tại.",
                "Quét mã QR chưa khả dụng").ShowAsync();
        }

        private async void CreateGroup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var friendItems = (ZaloApp.Friends ?? new System.Collections.Generic.List<ZaloFriend>())
                    .Select(f => new ZaloUWP.Models.FriendItem { UserId = f.UserId, DisplayName = f.Name, AvatarUrl = f.Avatar })
                    .ToList();
                var dialog = new CreateGroupDialog(friendItems);
                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary && !string.IsNullOrEmpty(dialog.CreatedGroupId))
                {
                    var conv = ZaloApp.GetOrCreate(dialog.CreatedGroupId, true);
                    conv.Name = dialog.CreatedGroupName;
                    Frame.Navigate(typeof(ChatPage), conv.Key);
                }
            }
            catch (Exception ex)
            {
                var dialog = new Windows.UI.Popups.MessageDialog("Không tạo được nhóm: " + ex.Message, "Tạo nhóm");
                await dialog.ShowAsync();
            }
        }

        private async void AddFriend_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new AddFriendDialog();
                await dialog.ShowAsync();
            }
            catch { }
        }

        private async System.Threading.Tasks.Task<bool> RefreshFriendsAsync()
        {
            try
            {
                if (ZaloApp.Session == null) return false;
                ZaloApp.Friends = await ZaloAuth.GetFriendsAsync(ZaloApp.Session, CancellationToken.None);
                await ZaloApp.SaveCachedFriendsAsync();
                await LoadFriendPresenceAsync();
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    ShowFriends(SearchBox.Text);
                    UpdateQuickContacts();
                });
                return true;
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Friend list refresh failed: " + ex.Message);
                return false;
            }
        }

        private async void RefreshContacts_Click(object sender, RoutedEventArgs e)
        {
            if (await RefreshFriendsAsync()) return;
            await new Windows.UI.Popups.MessageDialog(
                "Không làm mới được danh bạ. Danh sách đang hiển thị vẫn được giữ lại.", "Danh bạ").ShowAsync();
        }

        private async void RefreshList_Click(object sender, RoutedEventArgs e)
        {
            if (await RefreshFriendsAsync()) return;
            await new Windows.UI.Popups.MessageDialog(
                "Không làm mới được danh bạ. Danh sách đang hiển thị vẫn được giữ lại.", "Danh bạ").ShowAsync();
        }

        private async System.Threading.Tasks.Task LoadFriendPresenceAsync()
        {
            try
            {
                var onlineIds = await ZaloAuth.GetFriendOnlineIdsAsync(ZaloApp.Session, CancellationToken.None);
                foreach (ZaloFriend friend in ZaloApp.Friends)
                    friend.IsOnline = onlineIds.Contains(friend.UserId ?? string.Empty);
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Friend presence unavailable: " + ex.Message);
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SettingsPage));
        }

        private void OpenLocalData_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SettingsPage), "localData");
        }

        private void Profile_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(ProfilePage));
        }

        private async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ZaloStore.ClearAsync();
                ZaloApp.Reset();
                Frame.Navigate(typeof(LoginPage));
            }
            catch { }
        }

        // ==================== BOTTOM NAVIGATION BAR ====================

        public void SelectTab(int index)
        {
            _selectedTabIndex = index;

            if (Tab0Content != null) Tab0Content.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (Tab1Content != null) Tab1Content.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (Tab2Content != null) Tab2Content.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (Tab3Content != null) Tab3Content.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
            if (Tab4Content != null) Tab4Content.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;

            UpdateNavTabVisuals(index);

            bool contactsSelected = index == 1;
            bool profileSelected = index == 4;
            if (QrScanHeaderButton != null) QrScanHeaderButton.Visibility = Visibility.Collapsed;
            if (PlusHeaderButton != null) PlusHeaderButton.Visibility = contactsSelected || profileSelected ? Visibility.Collapsed : Visibility.Visible;
            if (ProfileSettingsHeaderButton != null) ProfileSettingsHeaderButton.Visibility = profileSelected ? Visibility.Visible : Visibility.Collapsed;
            if (ContactGroupsHeaderButton != null) ContactGroupsHeaderButton.Visibility = contactsSelected ? Visibility.Visible : Visibility.Collapsed;
            if (ContactRequestsHeaderButton != null) ContactRequestsHeaderButton.Visibility = contactsSelected ? Visibility.Visible : Visibility.Collapsed;

            if (SearchBox == null) return;
            if (index == 0)
            {
                _conversationListDirty = true;
                FilterConversations(SearchBox.Text);
            }
            else if (index == 1)
            {
                if (GroupsGrid != null && GroupsGrid.Visibility == Visibility.Visible) UpdateGroupList();
                else if (FriendList != null && FriendList.Visibility == Visibility.Visible) ShowFriends(SearchBox.Text, false);
            }
            else if (index == 2 && !_recommendationsLoaded)
                _ = RefreshFriendRecommendationsAsync();
        }

        private void TabMessages_Click(object sender, RoutedEventArgs e)
        {
            SelectTab(0);
        }

        private void TabContacts_Click(object sender, RoutedEventArgs e)
        {
            SelectTab(1);
        }

        private void TabDiscovery_Click(object sender, RoutedEventArgs e)
        {
            SelectTab(2);
        }

        private void TabTimeline_Click(object sender, RoutedEventArgs e)
        {
            SelectTab(3);
        }

        private void TabMe_Click(object sender, RoutedEventArgs e)
        {
            SelectTab(4);
        }

        private void UpdateNavTabVisuals(int index)
        {
            Tab0Icon.Source = GetNavIcon(index == 0 ? "icn_tab_message_o.png" : "tab_msg_inactive.png", index == 0);
            Tab1Icon.Source = GetNavIcon(index == 1 ? "icn_tab_contact_o.png" : "tab_contact_inactive.png", index == 1);
            Tab2Icon.Source = GetNavIcon(index == 2 ? "icn_tab_more_o.png" : "tab_discovery_inactive.png", index == 2);
            Tab3Icon.Source = GetNavIcon(index == 3 ? "icn_tab_social_o.png" : "tab_timeline_inactive.png", index == 3);
            Tab4Icon.Source = GetNavIcon(index == 4 ? "icn_tab_me_o.png" : "tab_me_inactive.png", index == 4);

            Tab0Text.Foreground = index == 0 ? ActiveBlueBrush : InactiveGrayBrush;
            Tab1Text.Foreground = index == 1 ? ActiveBlueBrush : InactiveGrayBrush;
            Tab2Text.Foreground = index == 2 ? ActiveBlueBrush : InactiveGrayBrush;
            Tab3Text.Foreground = index == 3 ? ActiveBlueBrush : InactiveGrayBrush;
            Tab4Text.Foreground = index == 4 ? ActiveBlueBrush : InactiveGrayBrush;

            Tab0Text.FontWeight = index == 0 ? FontWeights.SemiBold : FontWeights.Normal;
            Tab1Text.FontWeight = index == 1 ? FontWeights.SemiBold : FontWeights.Normal;
            Tab2Text.FontWeight = index == 2 ? FontWeights.SemiBold : FontWeights.Normal;
            Tab3Text.FontWeight = index == 3 ? FontWeights.SemiBold : FontWeights.Normal;
            Tab4Text.FontWeight = index == 4 ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private static BitmapImage GetNavIcon(string fileName, bool androidAsset)
        {
            string uri = androidAsset
                ? "ms-appx:///Assets/Icons/Android/" + fileName
                : "ms-appx:///Assets/Icons/" + fileName;
            BitmapImage image;
            if (!NavIconCache.TryGetValue(uri, out image))
            {
                image = new BitmapImage(new Uri(uri));
                NavIconCache.Add(uri, image);
            }
            return image;
        }
    }
}

