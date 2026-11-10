using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Windows.Data.Json;
using Windows.System;
using Windows.UI;

using Windows.UI.Xaml.Media;

using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using ZaloLite;

namespace ZaloUWP.Views
{
    public sealed partial class ConversationOptionsPage : Page
    {
        private ZaloConversation _conversation;
        private bool _loadingHidden;
        private bool _updatingHidden;
        private List<ConversationMediaItem> _mediaItems = new List<ConversationMediaItem>();

        public ConversationOptionsPage()
        {
            InitializeComponent();
            OptionsScrollViewer.Loaded += OptionsScrollViewer_Loaded;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _conversation = e.Parameter as ZaloConversation;
            if (_conversation == null && e.Parameter is string key) _conversation = ZaloApp.Find(key);
            if (_conversation == null || _conversation.IsGroup)
            {
                if (Frame.CanGoBack) Frame.GoBack();
                return;
            }

            var avatarBrush = _conversation.AvatarImageBrush as Windows.UI.Xaml.Media.ImageBrush;
            AvatarBrush.ImageSource = avatarBrush == null ? null : avatarBrush.ImageSource;
            AvatarColor.Fill = _conversation.AvatarColor;
            AvatarInitial.Text = _conversation.Initial;
            NameText.Text = _conversation.Name ?? "Trò chuyện";
            SubtitleText.Text = ZaloApp.IsFriend(_conversation.ThreadId) ? "" : "Người lạ";
            UpdateMuteAction();
            UpdateAliasText();
            BuildMediaItems();
            OptionsScrollViewer.ChangeView(0, 0, null, true);
            await LoadHiddenStateAsync();
        }

        private void OptionsScrollViewer_Loaded(object sender, RoutedEventArgs e)
        {
            OptionsScrollViewer.ChangeView(0, 0, null, true);
        }

        private void UpdateMuteAction()
        {
            if (_conversation == null) return;
            MuteText.Text = _conversation.IsMuted ? "Bật thông báo" : "Tắt thông báo";
            MuteIcon.Text = _conversation.IsMuted ? "\uE768" : "\uE7ED";
        }

        private void UpdateAliasText()
        {
            ZaloFriend friend = FindFriend();
            AliasStatus.Text = friend != null && !string.IsNullOrWhiteSpace(friend.Alias)
                ? friend.Alias : "";
        }

        private ZaloFriend FindFriend()
        {
            return ZaloApp.Friends == null ? null
                : ZaloApp.Friends.FirstOrDefault(item => item.UserId == _conversation?.ThreadId);
        }

        private void BuildMediaItems()
        {
            _mediaItems = _conversation.Messages
                .Where(message => message != null && !message.HasVoice && !message.IsSticker
                    && (message.HasMedia || message.HasLinkPreview))
                .OrderByDescending(message => message.Ts)
                .Take(40)
                .Select(message =>
                {
                    bool link = message.HasLinkPreview;
                    string url = link ? message.LinkPreviewUrl : message.MediaFullUrl;
                    if (string.IsNullOrWhiteSpace(url)) url = message.MediaUrl;
                    string kind = link ? "Liên kết" : message.HasVideo ? "Video"
                        : message.HasFile ? "Tệp" : message.IsSticker ? "Nhãn dán" : "Ảnh";
                    string title = link ? (message.LinkPreviewTitle.Length > 0 ? message.LinkPreviewTitle : url)
                        : message.HasFile ? (message.FileName ?? "Tệp") : kind;
                    return new ConversationMediaItem
                    {
                        Title = title,
                        Subtitle = kind + " · " + ZaloFormat.Time(message.Ts),
                        Url = url,
                        IsImage = !link && !message.HasVideo && !message.HasFile && !message.IsSticker
                    };
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Url))
                .ToList();

            MediaCountText.Text = _mediaItems.Count == 0 ? "" : _mediaItems.Count + "+";
            MediaSummaryText.Text = _mediaItems.Count == 0
                ? "Nội dung đã tải trong cuộc trò chuyện sẽ xuất hiện tại đây."
                : "" + _mediaItems.Count + " mục gần đây đã tải · chạm để xem";
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private void SearchMessages_Click(object sender, RoutedEventArgs e)
        {
            if (_conversation != null)
                Frame.Navigate(typeof(SearchResultsPage), new SearchPageRequest
                {
                    ConversationKey = _conversation.Key,
                    Query = string.Empty
                });
        }

        private void Profile_Click(object sender, RoutedEventArgs e)
        {
            ZaloFriend friend = FindFriend() ?? new ZaloFriend
            {
                UserId = _conversation.ThreadId,
                Name = _conversation.Name,
                OriginalName = _conversation.Name,
                Avatar = _conversation.AvatarUrl
            };
            Frame.Navigate(typeof(FriendProfilePage), friend);
        }

        private async void Mute_Click(object sender, RoutedEventArgs e)
        {
            MuteButton.IsEnabled = false;
            try
            {
                await ZaloApp.SetMutedAsync(_conversation, !_conversation.IsMuted);
                UpdateMuteAction();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Conversation mute update failed: " + ex.Message);
                await new MessageDialog("Không đổi được cài đặt thông báo. Hãy thử lại sau.", "Thông báo").ShowAsync();
            }
            finally { MuteButton.IsEnabled = true; }
        }

        private async void Alias_Click(object sender, RoutedEventArgs e)
        {
            ZaloFriend friend = FindFriend();
            if (friend == null)
            {
                await new MessageDialog("Chỉ có thể đặt tên gợi nhớ cho người trong danh bạ.", "Tên gợi nhớ").ShowAsync();
                return;
            }

            var input = new TextBox
            {
                Text = friend.Alias ?? string.Empty,
                PlaceholderText = friend.OriginalName ?? friend.Name,
                MaxLength = 40
            };
            var dialog = new ContentDialog
            {
                Title = "Đổi tên gợi nhớ",
                Content = input,
                PrimaryButtonText = "Lưu",
                SecondaryButtonText = string.IsNullOrWhiteSpace(friend.Alias) ? "Hủy" : "Xóa tên",
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37))
            };
            ContentDialogResult result = await dialog.ShowAsync();
            try
            {
                if (result == ContentDialogResult.Primary)
                {
                    string alias = (input.Text ?? string.Empty).Trim();
                    if (alias.Length == 0) throw new InvalidOperationException("Nhập tên gợi nhớ hoặc chọn Xóa tên.");
                    await ZaloAuth.SetFriendAliasAsync(ZaloApp.Session, friend.UserId, alias, CancellationToken.None);
                    friend.Alias = alias;
                    friend.Name = alias;
                }
                else if (result == ContentDialogResult.Secondary && !string.IsNullOrWhiteSpace(friend.Alias))
                {
                    await ZaloAuth.RemoveFriendAliasAsync(ZaloApp.Session, friend.UserId, CancellationToken.None);
                    friend.Alias = string.Empty;
                    friend.Name = friend.OriginalName ?? friend.Name;
                }
                else return;

                _conversation.Name = friend.Name;
                NameText.Text = friend.Name;
                UpdateAliasText();
                await ZaloApp.SaveCachedConversationsAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Conversation alias update failed: " + ex.Message);
                await new MessageDialog("Không đổi được tên gợi nhớ. " + ex.Message, "Tên gợi nhớ").ShowAsync();
            }
        }

        private async void Wallpaper_Click(object sender, RoutedEventArgs e)
        {
            var grid = new GridView
            {
                Height = 280,
                SelectionMode = ListViewSelectionMode.Single,
                IsItemClickEnabled = false,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            for (int i = 1; i <= 20; i++)
            {
                string uri = "ms-appx:///Assets/Backgrounds/bg_" + i.ToString("D2") + ".png";
                var image = new Image
                {
                    Width = 74,
                    Height = 92,
                    Stretch = Windows.UI.Xaml.Media.Stretch.UniformToFill,
                    Source = new BitmapImage(new Uri(uri))
                };
                var border = new Border
                {
                    Width = 78,
                    Height = 96,
                    Margin = new Thickness(3),
                    BorderBrush = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xDD, 0xE3, 0xE9)),
                    BorderThickness = new Thickness(1),
                    Child = image,
                    Tag = uri
                };
                grid.Items.Add(border);
            }
            var dialog = new ContentDialog
            {
                Title = "Đổi hình nền cuộc trò chuyện",
                Content = grid,
                PrimaryButtonText = "Áp dụng",
                SecondaryButtonText = "Dùng hình nền chung",
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37))
            };
            ContentDialogResult result = await dialog.ShowAsync();
            string settingKey = ZaloApp.Session == null ? string.Empty : ZaloApp.Session.Uid;
            if (result == ContentDialogResult.Primary)
            {
                Border selected = grid.SelectedItem as Border;
                if (selected == null) return;
                ZaloUWP.Services.ZaloSettingsService.Current.SetConversationWallpaper(
                    settingKey, _conversation.Key, selected.Tag as string);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                ZaloUWP.Services.ZaloSettingsService.Current.SetConversationWallpaper(settingKey, _conversation.Key, string.Empty);
            }
        }

        private async void Media_Click(object sender, RoutedEventArgs e)
        {
            var content = new StackPanel();
            if (_mediaItems.Count == 0)
                content.Children.Add(new TextBlock { Text = "Chưa có ảnh, tệp hoặc liên kết trong phần lịch sử đã tải.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 12, 4, 12) });
            ConversationMediaItem selected = null;
            foreach (ConversationMediaItem item in _mediaItems.Take(12))
            {
                var button = new Button
                {
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = item.Title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock { Text = item.Subtitle, FontSize = 11, Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x7D, 0x87, 0x91)) }
                        }
                    },
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 2, 0, 2),
                    UseSystemFocusVisuals = false
                };
                button.Click += (s, args) => { selected = item; };
                content.Children.Add(button);
            }
            var dialog = new ContentDialog
            {
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                Title = "Ảnh, file, link",
                Content = new ScrollViewer { Content = content, MaxHeight = 470, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = "Đóng",
                DefaultButton = ContentDialogButton.Primary,
            };
            await dialog.ShowAsync();
            if (selected != null) await OpenMediaAsync(selected);
        }

        private async System.Threading.Tasks.Task OpenMediaAsync(ConversationMediaItem item)
        {
            Uri uri;
            if (item.IsImage && Uri.TryCreate(item.Url, UriKind.Absolute, out uri))
                Frame.Navigate(typeof(ImageViewerPage), item.Url);
            else if (Uri.TryCreate(item.Url, UriKind.Absolute, out uri))
                await Launcher.LaunchUriAsync(uri);
        }

        private async void CommonGroups_Click(object sender, RoutedEventArgs e)
        {
            if (_conversation == null || ZaloApp.Session == null) return;
            CommonGroupsButton.IsEnabled = false;
            CommonGroupsStatus.Text = "Đang tìm nhóm chung…";
            CommonGroupsList.Visibility = Visibility.Collapsed;
            try
            {
                List<string> ids = await ZaloMessaging.GetGroupIdsAsync(ZaloApp.Session, CancellationToken.None);
                Dictionary<string, JsonObject> details = await ZaloMessaging.GetGroupInfoMapAsync(ZaloApp.Session, ids, CancellationToken.None);
                var matches = new List<CommonGroupItem>();
                foreach (KeyValuePair<string, JsonObject> pair in details)
                {
                    JsonObject info = pair.Value;
                    bool isMember = HasMember(info, _conversation.ThreadId);
                    if (!isMember) continue;
                    string name = ZaloAuth.Str(info, "name");
                    if (name.Length == 0) name = "Nhóm " + pair.Key;
                    matches.Add(new CommonGroupItem { ThreadId = pair.Key, Name = name });
                }
                CommonGroupsList.ItemsSource = matches.OrderBy(item => item.Name).ToList();
                CommonGroupsList.Visibility = matches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                CommonGroupsStatus.Text = matches.Count == 0 ? "Không tìm thấy nhóm chung." : matches.Count + " nhóm chung";
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Common groups lookup failed: " + ex.Message);
                CommonGroupsStatus.Text = "Không tải được nhóm chung. Chạm để thử lại.";
            }
            finally { CommonGroupsButton.IsEnabled = true; }
        }

        private static bool HasMember(JsonObject info, string userId)
        {
            IJsonValue value;
            if (info.TryGetValue("memberIds", out value) && value.ValueType == JsonValueType.Array)
                foreach (IJsonValue member in value.GetArray())
                    if (member.ValueType == JsonValueType.String && member.GetString() == userId) return true;
            if (info.TryGetValue("currentMems", out value) && value.ValueType == JsonValueType.Array)
                foreach (IJsonValue member in value.GetArray())
                    if (member.ValueType == JsonValueType.Object && ZaloAuth.Str(member.GetObject(), "id") == userId) return true;
            return false;
        }

        private void CommonGroupItem_Click(object sender, RoutedEventArgs e)
        {
            string threadId = (sender as Button)?.Tag as string;
            if (string.IsNullOrWhiteSpace(threadId)) return;
            Frame.Navigate(typeof(ChatPage), "g:" + threadId);
        }

        private async System.Threading.Tasks.Task LoadHiddenStateAsync()
        {
            _loadingHidden = true;
            HiddenToggle.IsEnabled = false;
            HiddenStatus.Text = "Đang tải trạng thái ẩn…";
            try
            {
                var keys = await ZaloAuth.GetHiddenConversationKeysAsync(ZaloApp.Session, CancellationToken.None);
                _updatingHidden = true;
                HiddenToggle.IsOn = keys.Contains(_conversation.Key);
                HiddenStatus.Text = "Ẩn cuộc trò chuyện khỏi danh sách chính; có thể tìm lại bằng ô tìm kiếm.";
                HiddenToggle.IsEnabled = true;
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Hidden conversation state load failed: " + ex.Message);
                HiddenStatus.Text = "Không tải được trạng thái ẩn. Thử lại khi có mạng.";
            }
            finally
            {
                _loadingHidden = false;
                _updatingHidden = false;
            }
        }

        private async void HiddenToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingHidden || _updatingHidden || _conversation == null) return;
            bool requested = HiddenToggle.IsOn;
            HiddenToggle.IsEnabled = false;
            HiddenStatus.Text = requested ? "Đang ẩn trò chuyện…" : "Đang hiện lại trò chuyện…";
            try
            {
                await ZaloApp.SetHiddenAsync(_conversation, requested);
                HiddenStatus.Text = requested ? "Đã ẩn. Dùng tìm kiếm để mở lại." : "Đã hiện lại trong danh sách.";
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Hidden conversation update failed: " + ex.Message);
                _updatingHidden = true;
                HiddenToggle.IsOn = !requested;
                _updatingHidden = false;
                HiddenStatus.Text = "Không đổi được trạng thái ẩn. " + ex.Message;
            }
            finally { HiddenToggle.IsEnabled = true; }
        }
    }

    public sealed class ConversationMediaItem
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Url { get; set; }
        public bool IsImage { get; set; }
    }

    public sealed class CommonGroupItem
    {
        public string ThreadId { get; set; }
        public string Name { get; set; }
    }
}
