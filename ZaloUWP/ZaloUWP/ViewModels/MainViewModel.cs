using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml.Media;
using ZaloUWP.Core;
using ZaloUWP.Database;
using ZaloUWP.Models;
using ZaloUWP.Services;

namespace ZaloUWP.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly ZaloApiClient _apiClient;
        private readonly ZaloDatabase _database;
        private readonly ZaloWebSocketClient _wsClient;
        private readonly ZaloNotificationService _notificationService;

        public ObservableCollection<ConversationItem> Conversations { get; } = new ObservableCollection<ConversationItem>();
        public ObservableCollection<FriendItem> Friends { get; } = new ObservableCollection<FriendItem>();

        private static readonly SolidColorBrush ActiveBlueBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x88, 0xFF));
        private static readonly SolidColorBrush InactiveGrayBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x75, 0x75, 0x75));

        private int _selectedTabIndex = 0;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (SetProperty(ref _selectedTabIndex, value))
                {
                    RaisePropertyChanged(nameof(Tab0Icon));
                    RaisePropertyChanged(nameof(Tab1Icon));
                    RaisePropertyChanged(nameof(Tab2Icon));
                    RaisePropertyChanged(nameof(Tab3Icon));
                    RaisePropertyChanged(nameof(Tab4Icon));
                    RaisePropertyChanged(nameof(Tab0TextColor));
                    RaisePropertyChanged(nameof(Tab1TextColor));
                    RaisePropertyChanged(nameof(Tab2TextColor));
                    RaisePropertyChanged(nameof(Tab3TextColor));
                    RaisePropertyChanged(nameof(Tab4TextColor));
                    RaisePropertyChanged(nameof(Tab0Brush));
                    RaisePropertyChanged(nameof(Tab1Brush));
                    RaisePropertyChanged(nameof(Tab2Brush));
                    RaisePropertyChanged(nameof(Tab3Brush));
                    RaisePropertyChanged(nameof(Tab4Brush));
                    RaisePropertyChanged(nameof(Tab0Weight));
                    RaisePropertyChanged(nameof(Tab1Weight));
                    RaisePropertyChanged(nameof(Tab2Weight));
                    RaisePropertyChanged(nameof(Tab3Weight));
                    RaisePropertyChanged(nameof(Tab4Weight));
                }
            }
        }

        public string Tab0Icon => SelectedTabIndex == 0 ? "ms-appx:///Assets/Icons/tab_msg_active.png" : "ms-appx:///Assets/Icons/tab_msg_inactive.png";
        public string Tab1Icon => SelectedTabIndex == 1 ? "ms-appx:///Assets/Icons/tab_contact_active.png" : "ms-appx:///Assets/Icons/tab_contact_inactive.png";
        public string Tab2Icon => SelectedTabIndex == 2 ? "ms-appx:///Assets/Icons/tab_discovery_active.png" : "ms-appx:///Assets/Icons/tab_discovery_inactive.png";
        public string Tab3Icon => SelectedTabIndex == 3 ? "ms-appx:///Assets/Icons/tab_timeline_active.png" : "ms-appx:///Assets/Icons/tab_timeline_inactive.png";
        public string Tab4Icon => SelectedTabIndex == 4 ? "ms-appx:///Assets/Icons/tab_me_active.png" : "ms-appx:///Assets/Icons/tab_me_inactive.png";

        public string Tab0TextColor => SelectedTabIndex == 0 ? "#0088FF" : "#757575";
        public string Tab1TextColor => SelectedTabIndex == 1 ? "#0088FF" : "#757575";
        public string Tab2TextColor => SelectedTabIndex == 2 ? "#0088FF" : "#757575";
        public string Tab3TextColor => SelectedTabIndex == 3 ? "#0088FF" : "#757575";
        public string Tab4TextColor => SelectedTabIndex == 4 ? "#0088FF" : "#757575";

        public SolidColorBrush Tab0Brush => SelectedTabIndex == 0 ? ActiveBlueBrush : InactiveGrayBrush;
        public SolidColorBrush Tab1Brush => SelectedTabIndex == 1 ? ActiveBlueBrush : InactiveGrayBrush;
        public SolidColorBrush Tab2Brush => SelectedTabIndex == 2 ? ActiveBlueBrush : InactiveGrayBrush;
        public SolidColorBrush Tab3Brush => SelectedTabIndex == 3 ? ActiveBlueBrush : InactiveGrayBrush;
        public SolidColorBrush Tab4Brush => SelectedTabIndex == 4 ? ActiveBlueBrush : InactiveGrayBrush;

        public FontWeight Tab0Weight => SelectedTabIndex == 0 ? FontWeights.SemiBold : FontWeights.Normal;
        public FontWeight Tab1Weight => SelectedTabIndex == 1 ? FontWeights.SemiBold : FontWeights.Normal;
        public FontWeight Tab2Weight => SelectedTabIndex == 2 ? FontWeights.SemiBold : FontWeights.Normal;
        public FontWeight Tab3Weight => SelectedTabIndex == 3 ? FontWeights.SemiBold : FontWeights.Normal;
        public FontWeight Tab4Weight => SelectedTabIndex == 4 ? FontWeights.SemiBold : FontWeights.Normal;

        private int _selectedSubTab = 0;
        public int SelectedSubTab
        {
            get => _selectedSubTab;
            set
            {
                if (SetProperty(ref _selectedSubTab, value))
                {
                    RaisePropertyChanged(nameof(IsPriorityTabSelected));
                    RaisePropertyChanged(nameof(IsOtherTabSelected));
                    ApplySearchFilter();
                }
            }
        }

        public bool IsPriorityTabSelected => _selectedSubTab == 0;
        public bool IsOtherTabSelected => _selectedSubTab == 1;

        private int _totalUnreadBadge = 0;
        public int TotalUnreadBadge
        {
            get => _totalUnreadBadge;
            set => SetProperty(ref _totalUnreadBadge, value);
        }

        private string _currentUserName = "Người dùng Zalo";
        public string CurrentUserName
        {
            get => _currentUserName;
            set => SetProperty(ref _currentUserName, value);
        }

        private string _currentUserAvatar = "ms-appx:///Assets/Square150x150Logo.png";
        public string CurrentUserAvatar
        {
            get => _currentUserAvatar;
            set => SetProperty(ref _currentUserAvatar, value);
        }

        private string _connectionStatus = "Đã kết nối";
        public string ConnectionStatus
        {
            get => _connectionStatus;
            set => SetProperty(ref _connectionStatus, value);
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (SetProperty(ref _isLoading, value))
                {
                    RaisePropertyChanged(nameof(LoadingVisibility));
                }
            }
        }
        public Windows.UI.Xaml.Visibility LoadingVisibility => IsLoading ? Windows.UI.Xaml.Visibility.Visible : Windows.UI.Xaml.Visibility.Collapsed;

        private string _searchQuery = "";
        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (SetProperty(ref _searchQuery, value))
                {
                    ApplySearchFilter();
                }
            }
        }

        private bool _isPhoneSyncBannerVisible = true;
        public bool IsPhoneSyncBannerVisible
        {
            get => _isPhoneSyncBannerVisible;
            set => SetProperty(ref _isPhoneSyncBannerVisible, value);
        }

        private string _phoneSyncStatusText = "Đồng bộ toàn bộ tin nhắn gần đây từ điện thoại của bạn sang máy này";
        public string PhoneSyncStatusText
        {
            get => _phoneSyncStatusText;
            set => SetProperty(ref _phoneSyncStatusText, value);
        }

        private List<ConversationItem> _allConversations = new List<ConversationItem>();
        private List<FriendItem> _allFriends = new List<FriendItem>();

        public ICommand RefreshCommand { get; }
        public ICommand LogoutCommand { get; }
        public ICommand SwitchTabCommand { get; }
        public ICommand SyncMessagesCommand { get; }
        public ICommand DismissSyncBannerCommand { get; }

        public event EventHandler<ConversationItem> ConversationSelected;
        public event EventHandler LogoutRequested;

        public MainViewModel()
        {
            _apiClient = ZaloApiClient.Current;
            _database = ZaloDatabase.Current;
            _wsClient = ZaloWebSocketClient.Current;
            _notificationService = ZaloNotificationService.Current;

            RefreshCommand = new RelayCommand(async () => await LoadDataAsync(forceRefresh: true));
            LogoutCommand = new RelayCommand(Logout);
            SwitchTabCommand = new RelayCommand<int>((idx) => SelectedTabIndex = idx);
            SyncMessagesCommand = new RelayCommand(async () => await SyncFromPhoneAsync());
            DismissSyncBannerCommand = new RelayCommand(() => IsPhoneSyncBannerVisible = false);

            _wsClient.ConnectionStateChanged += WsClient_ConnectionStateChanged;
            _wsClient.MessageReceived += WsClient_MessageReceived;

            UpdateUserProfile();
        }

        private void UpdateUserProfile()
        {
            if (!string.IsNullOrEmpty(ZaloSession.Current.DisplayName))
            {
                CurrentUserName = ZaloSession.Current.DisplayName;
            }
            if (!string.IsNullOrEmpty(ZaloSession.Current.AvatarUrl))
            {
                CurrentUserAvatar = ZaloSession.Current.AvatarUrl;
            }
        }

        public async Task InitializeAsync()
        {
            try
            {
                UpdateUserProfile();

                // Check real data JSON import if user placed one
                try
                {
                    await CheckAndImportRealDataAsync();
                }
                catch { }

                // 1. Instant load from local offline cache
                try
                {
                    var cachedConvs = await _database.GetConversationsAsync();
                    bool hasBrokenCache = (cachedConvs == null || cachedConvs.Count == 0 || cachedConvs.Any(c => string.IsNullOrEmpty(c.Title)));
                    if (!hasBrokenCache)
                    {
                        _allConversations = cachedConvs;
                    }
                    else
                    {
                        await CheckAndImportRealDataAsync();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainViewModel] Load cached convs error: {ex.Message}");
                    await CheckAndImportRealDataAsync();
                }
                ApplySearchFilter();
                UpdateBadgeCount();

                try
                {
                    var cachedFriends = await _database.GetFriendsAsync();
                    if (cachedFriends != null && cachedFriends.Count > 0 && !cachedFriends.Any(f => string.IsNullOrEmpty(f.DisplayName)))
                    {
                        _allFriends = cachedFriends;
                    }
                    else
                    {
                        await CheckAndImportRealDataAsync();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainViewModel] Load cached friends error: {ex.Message}");
                    _allFriends = new List<FriendItem>();
                }
                PopulateFriendsList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainViewModel] InitializeAsync FATAL: {ex.Message}");
                _allConversations = new List<ConversationItem>();
                _allFriends = new List<FriendItem>();
                ApplySearchFilter();
                PopulateFriendsList();
                UpdateBadgeCount();
            }

            // 2. Connect WebSocket and load remote real data in background without blocking UI
            if (ZaloSession.Current.IsLoggedIn)
            {
                var ignored = Task.Run(async () =>
                {
                    try
                    {
                        try
                        {
                            await LoadDataAsync(forceRefresh: true);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MainViewModel] LoadData warning (swallowed): {ex.Message}");
                        }

                        try
                        {
                            await _wsClient.ConnectAsync();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MainViewModel] WS connect warning (swallowed): {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MainViewModel] BG init FATAL (swallowed): {ex.Message}");
                    }
                });
            }
        }

        public async Task<bool> CheckAndImportRealDataAsync()
        {
            try
            {
                // Check 1: In AppData LocalFolder
                var localFile = await Windows.Storage.ApplicationData.Current.LocalFolder.TryGetItemAsync("zalo_real_data.json") as Windows.Storage.StorageFile;
                if (localFile != null)
                {
                    var text = await Windows.Storage.FileIO.ReadTextAsync(localFile);
                    return await ApplyRealDataJsonAsync(text);
                }

                // Check 2: In App package Assets
                try
                {
                    var assetUri = new Uri("ms-appx:///Assets/zalo_real_data.json");
                    var assetFile = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(assetUri);
                    if (assetFile != null)
                    {
                        var text = await Windows.Storage.FileIO.ReadTextAsync(assetFile);
                        return await ApplyRealDataJsonAsync(text);
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainViewModel] CheckAndImportRealData error: {ex.Message}");
            }
            return false;
        }

        public async Task<bool> ApplyRealDataJsonAsync(string jsonText)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jsonText)) return false;
                var obj = Newtonsoft.Json.Linq.JObject.Parse(jsonText);

                var uid = obj["userId"]?.ToString();
                var dname = obj["displayName"]?.ToString();
                var avt = obj["avatarUrl"]?.ToString();

                if (!string.IsNullOrEmpty(uid)) ZaloSession.Current.UserId = uid;
                if (!string.IsNullOrEmpty(dname)) ZaloSession.Current.DisplayName = dname;
                if (!string.IsNullOrEmpty(avt)) ZaloSession.Current.AvatarUrl = avt;
                ZaloSession.Current.Save();
                UpdateUserProfile();

                var rawFriends = obj["friends"]?.ToString();
                if (!string.IsNullOrEmpty(rawFriends))
                {
                    var fList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<FriendItem>>(rawFriends);
                    if (fList != null && fList.Count > 0)
                    {
                        _allFriends = fList;
                        await _database.SaveFriendsAsync(_allFriends);
                        PopulateFriendsList();
                    }
                }

                var rawConvs = obj["conversations"]?.ToString();
                if (!string.IsNullOrEmpty(rawConvs))
                {
                    var cList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ConversationItem>>(rawConvs);
                    if (cList != null && cList.Count > 0)
                    {
                        _allConversations = cList;
                        await _database.SaveConversationsAsync(_allConversations);
                        ApplySearchFilter();
                        UpdateBadgeCount();
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainViewModel] ApplyRealDataJson error: {ex.Message}");
                return false;
            }
        }



        public Task SyncFromPhoneAsync()
        {
            RunOnUIThread(() =>
            {
                IsPhoneSyncBannerVisible = true;
                PhoneSyncStatusText = "Chưa thể đồng bộ từ điện thoại: API cũ trả 404, còn giao thức mới chưa được nối.";
            });
            return Task.FromResult(0);
        }

        private void PopulateFriendsList()
        {
            RunOnUIThread(() =>
            {
                Friends.Clear();
                foreach (var f in _allFriends.OrderBy(x => x.DisplayName))
                {
                    Friends.Add(f);
                }
            });
        }

        public async Task LoadDataAsync(bool forceRefresh)
        {
            if (!ZaloSession.Current.IsLoggedIn) return;

            RunOnUIThread(() => IsLoading = true);
            try
            {
                // Nếu đã đăng nhập nhưng chưa có zpw_enk hoặc ServiceMap (ví dụ vừa quét QR xong hoặc mở app lại)
                // Gọi getLoginInfo + getServerInfo để lấy key giải mã và routing cluster thật
                if (string.IsNullOrEmpty(ZaloSession.Current.ZpwEnk) || ZaloSession.Current.ServiceMap.Count == 0)
                {
                    try
                    {
                        var auth = new ZaloAuthService();
                        await auth.GetLoginInfoAsync();
                        await auth.GetServerInfoAsync();
                        RunOnUIThread(UpdateUserProfile);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MainViewModel] EnsureLoginInfo warning: {ex.Message}");
                    }
                }
                else
                {
                    RunOnUIThread(UpdateUserProfile);
                }

                // Bọc từng API call riêng - nếu 1 cái fail thì cái kia vẫn chạy
                // Giống Zalo10: mỗi step có try-catch riêng
                try
                {
                    var convs = await _apiClient.GetConversationsAsync();
                    if (convs != null && convs.Count > 0)
                    {
                        _allConversations = convs;
                        await _database.SaveConversationsAsync(convs);
                        ApplySearchFilter();
                        UpdateBadgeCount();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainViewModel] LoadData conversations error (swallowed): {ex.Message}");
                }

                try
                {
                    var friends = await _apiClient.GetFriendsListAsync();
                    if (friends != null && friends.Count > 0)
                    {
                        _allFriends = friends;
                        await _database.SaveFriendsAsync(friends);
                        PopulateFriendsList();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainViewModel] LoadData friends error (swallowed): {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainViewModel] LoadDataAsync FATAL (swallowed): {ex.Message}");
            }
            finally
            {
                RunOnUIThread(() => IsLoading = false);
            }
        }

        private void UpdateBadgeCount()
        {
            RunOnUIThread(() =>
            {
                TotalUnreadBadge = _allConversations.Sum(c => c.UnreadCount);
                _notificationService.UpdateBadge(TotalUnreadBadge);
            });
        }

        private void ApplySearchFilter()
        {
            RunOnUIThread(() =>
            {
                Conversations.Clear();
                var q = SearchQuery?.Trim().ToLowerInvariant() ?? "";

                IEnumerable<ConversationItem> source = _allConversations;
                if (SelectedSubTab == 1)
                {
                    // Tab "Khác": Official Accounts (Long Châu, Zalo, Media Box...), hoặc hội thoại phụ
                    var otherList = _allConversations.Where(c => 
                        (c.Title != null && (c.Title.Contains("Zalo") || c.Title.Contains("Long Châu") || c.Title.Contains("Báo") || c.Title.Contains("Media Box") || c.Title.Contains("Verify"))) ||
                        (!c.IsGroup && !c.IsPinned)
                    ).ToList();

                    source = otherList.Count > 0 ? otherList : _allConversations;
                }
                else
                {
                    // Tab "Ưu tiên": Tất cả cuộc hội thoại (ghim, nhóm, bạn bè)
                    source = _allConversations;
                }

                var filtered = string.IsNullOrEmpty(q)
                    ? source
                    : source.Where(c => (c.Title != null && c.Title.ToLowerInvariant().Contains(q)) ||
                                                   (c.LastMessage != null && c.LastMessage.ToLowerInvariant().Contains(q)));

                foreach (var item in filtered)
                {
                    Conversations.Add(item);
                }
            });
        }

        private void WsClient_ConnectionStateChanged(object sender, bool isConnected)
        {
            RunOnUIThread(() =>
            {
                ConnectionStatus = isConnected ? "Đã kết nối" : "Ngoại tuyến";
            });
        }

        private void WsClient_MessageReceived(object sender, MessageItem msg)
        {
            RunOnUIThread(() =>
            {
                var existing = _allConversations.FirstOrDefault(c => c.Id == msg.ConversationId);
                if (existing != null)
                {
                    existing.LastMessage = msg.Text;
                    existing.LastMessageTimestamp = msg.Timestamp;
                    if (!msg.IsOutgoing)
                    {
                        existing.UnreadCount++;
                        _notificationService.ShowMessageToast(msg, existing.Title);
                    }

                    _allConversations.Remove(existing);
                    _allConversations.Insert(0, existing);
                }
                else
                {
                    var newConv = new ConversationItem
                    {
                        Id = msg.ConversationId,
                        Title = msg.FromName ?? "Tin nhắn mới",
                        AvatarUrl = msg.FromAvatar,
                        LastMessage = msg.Text,
                        LastMessageTimestamp = msg.Timestamp,
                        UnreadCount = 1,
                        IsGroup = false
                    };
                    _allConversations.Insert(0, newConv);
                    if (!msg.IsOutgoing)
                    {
                        _notificationService.ShowMessageToast(msg, newConv.Title);
                    }
                }

                ApplySearchFilter();
                UpdateBadgeCount();
            });
        }

        public void SelectConversation(ConversationItem conv)
        {
            if (conv != null)
            {
                conv.UnreadCount = 0;
                UpdateBadgeCount();
                ConversationSelected?.Invoke(this, conv);
            }
        }

        public void Logout()
        {
            try
            {
                _wsClient.Disconnect();
            }
            catch { }
            ZaloSession.Current.Clear();
            _allConversations.Clear();
            _allFriends.Clear();
            Conversations.Clear();
            Friends.Clear();
            var ignored = _database.ClearAllDataAsync();
            LogoutRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
