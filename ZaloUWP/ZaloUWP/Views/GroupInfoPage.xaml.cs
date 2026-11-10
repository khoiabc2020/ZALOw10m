using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using ZaloUWP.Common;
using ZaloLite;
using ZaloUWP.Models;

namespace ZaloUWP.Views
{
    public sealed partial class GroupInfoPage : Page
    {
        private ZaloConversation _conversation;
        private string _creatorId = string.Empty;
        private HashSet<string> _adminIds = new HashSet<string>(StringComparer.Ordinal);
        private string _groupInviteLink = string.Empty;
        private List<ConversationMediaItem> _groupMediaItems = new List<ConversationMediaItem>();

        public GroupInfoPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            string key = e.Parameter as string;
            _conversation = key == null ? null : ZaloApp.Find(key);
            if (_conversation == null || !_conversation.IsGroup)
            {
                if (Frame.CanGoBack) Frame.GoBack();
                return;
            }
            GroupNameTextBlock.Text = _conversation.Name;
            var cachedAvatar = _conversation.AvatarImageBrush as Windows.UI.Xaml.Media.ImageBrush;
            GroupAvatarBrush.ImageSource = cachedAvatar == null ? null : cachedAvatar.ImageSource;
            GroupAvatarImageShape.Visibility = GroupAvatarBrush.ImageSource == null ? Visibility.Collapsed : Visibility.Visible;
            GroupAvatarFallback.Visibility = GroupAvatarBrush.ImageSource == null ? Visibility.Visible : Visibility.Collapsed;
            UpdateMuteGroupAction();
            _creatorId = string.Empty;
            _adminIds.Clear();
            MembersListView.ItemsSource = new List<UserInfo>();
            await System.Threading.Tasks.Task.WhenAll(LoadGroupInfoAsync(), LoadGroupBoardAsync(), LoadGroupRemindersAsync());
        }

        private async System.Threading.Tasks.Task LoadGroupRemindersAsync()
        {
            GroupRemindersStatusTextBlock.Text = "Đang tải nhắc hẹn…";
            GroupRemindersItemsControl.ItemsSource = null;
            try
            {
                List<ZaloGroupReminder> reminders = await ZaloMessaging.GetGroupRemindersAsync(
                    ZaloApp.Session, _conversation.ThreadId, 1, 20, CancellationToken.None);
                GroupRemindersItemsControl.ItemsSource = reminders;
                GroupRemindersStatusTextBlock.Text = reminders.Count == 0
                    ? "Chưa có nhắc hẹn trong nhóm."
                    : reminders.Count + " nhắc hẹn gần đây";
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Group reminders load failed: " + ex.Message);
                GroupRemindersStatusTextBlock.Text = "Không tải được nhắc hẹn. Hãy thử làm mới.";
            }
        }

        private async System.Threading.Tasks.Task LoadGroupBoardAsync()
        {
            GroupBoardStatusTextBlock.Text = "Đang tải bảng nhóm…";
            GroupBoardItemsControl.ItemsSource = null;
            try
            {
                List<ZaloGroupBoardEntry> entries = await ZaloMessaging.GetGroupBoardAsync(
                    ZaloApp.Session, _conversation.ThreadId, 1, 20, CancellationToken.None);
                GroupBoardItemsControl.ItemsSource = entries;
                var pinned = entries.FirstOrDefault(entry => entry != null && entry.Icon == "📌");
                _conversation.GroupPinnedPreview = pinned == null ? string.Empty : pinned.Title;
                _conversation.GroupPinnedPreviewUpdatedUtc = DateTime.UtcNow;
                GroupBoardStatusTextBlock.Text = entries.Count == 0
                    ? "Chưa có ghi chú, tin ghim hoặc bình chọn trong nhóm."
                    : entries.Count + " mục gần đây";
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Group board load failed: " + ex.Message);
                GroupBoardStatusTextBlock.Text = "Không tải được bảng nhóm. Hãy thử làm mới.";
            }
        }

        private async System.Threading.Tasks.Task LoadGroupInfoAsync()
        {
            GroupMemberCountTextBlock.Text = "Đang tải thông tin nhóm…";
            try
            {
                JsonObject info = await ZaloMessaging.GetGroupInfoAsync(ZaloApp.Session, _conversation.ThreadId, CancellationToken.None);
                if (info == null) throw new InvalidOperationException("Zalo chưa trả thông tin nhóm này.");

                string name = ZaloAuth.Str(info, "name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _conversation.Name = name;
                    GroupNameTextBlock.Text = name;
                }
                string avatar = ZaloAuth.Str(info, "fullAvt");
                if (avatar.Length == 0) avatar = ZaloAuth.Str(info, "avt");
                if (!string.IsNullOrWhiteSpace(avatar)) _conversation.AvatarUrl = avatar;
                try
                {
                    if (Uri.IsWellFormedUriString(avatar, UriKind.Absolute))
                    {
                        GroupAvatarBrush.ImageSource = new BitmapImage(new Uri(avatar));
                        GroupAvatarImageShape.Visibility = Visibility.Visible;
                        GroupAvatarFallback.Visibility = Visibility.Collapsed;
                    }
                    else if (GroupAvatarBrush.ImageSource == null)
                    {
                        GroupAvatarImageShape.Visibility = Visibility.Collapsed;
                        GroupAvatarFallback.Visibility = Visibility.Visible;
                    }
                }
                catch
                {
                    if (GroupAvatarBrush.ImageSource == null)
                    {
                        GroupAvatarImageShape.Visibility = Visibility.Collapsed;
                        GroupAvatarFallback.Visibility = Visibility.Visible;
                    }
                }

                _creatorId = ZaloAuth.Str(info, "creatorId");
                _adminIds.Clear();
                IJsonValue adminValue;
                if (info.TryGetValue("adminIds", out adminValue) && adminValue.ValueType == JsonValueType.Array)
                    foreach (IJsonValue admin in adminValue.GetArray())
                    {
                        string adminId = admin.ValueType == JsonValueType.String ? admin.GetString() : string.Empty;
                        if (adminId.Length > 0) _adminIds.Add(adminId);
                    }
                bool canManageGroup = CanManageGroup();
                GroupInviteLinkPanel.Visibility = canManageGroup ? Visibility.Visible : Visibility.Collapsed;
                ChangeGroupNameButton.Visibility = canManageGroup ? Visibility.Visible : Visibility.Collapsed;
                IJsonValue settingsValue;
                if (info.TryGetValue("setting", out settingsValue) && settingsValue.ValueType == JsonValueType.Object)
                {
                    bool lockSendMsg = ZaloAuth.Num(settingsValue.GetObject(), "lockSendMsg") == 1;
                    _conversation.UpdateGroupMessagePermission(true, lockSendMsg, canManageGroup);
                    _ = ZaloApp.SaveCachedConversationsAsync();
                }

                var members = new List<UserInfo>();
                IJsonValue memValue;
                if (info.TryGetValue("currentMems", out memValue) && memValue.ValueType == JsonValueType.Array)
                {
                    foreach (IJsonValue value in memValue.GetArray())
                    {
                        if (value.ValueType != JsonValueType.Object) continue;
                        JsonObject member = value.GetObject();
                        string id = ZaloAuth.Str(member, "id");
                        string memberName = ZaloAuth.Str(member, "dName");
                        if (memberName.Length == 0) memberName = ZaloAuth.Str(member, "zaloName");
                        if (memberName.Length == 0) memberName = id;
                        members.Add(new UserInfo
                        {
                            UserId = id,
                            DisplayName = memberName,
                            AvatarUrl = ZaloAuth.Str(member, "avatar"),
                            IsGroupOwner = id == _creatorId,
                            IsGroupDeputy = id != _creatorId && _adminIds.Contains(id)
                        });
                    }
                }
                MembersListView.ItemsSource = members;
                int total = (int)ZaloAuth.Num(info, "totalMember");
                if (total < members.Count) total = members.Count;
                _conversation.GroupMemberCount = total;
                GroupMemberCountTextBlock.Text = total + " thành viên";
                GroupMembersAvailabilityTextBlock.Text = members.Count == 0
                    ? "Zalo chưa cung cấp danh sách thành viên cho phiên này."
                    : members.Count < total ? "Đã tải " + members.Count + " thành viên." : string.Empty;
                GroupMembersAvailabilityTextBlock.Visibility = GroupMembersAvailabilityTextBlock.Text.Length == 0
                    ? Visibility.Collapsed : Visibility.Visible;
                BuildGroupMediaItems();
                if (canManageGroup) await LoadGroupLinkAsync();
            }
            catch (Exception ex)
            {
                GroupMemberCountTextBlock.Text = "Không tải được thông tin nhóm";
                ZaloLog.Add("Group info failed: " + ex.Message);
                await new MessageDialog("Không tải được thông tin nhóm. " + ex.Message, "Thông tin nhóm").ShowAsync();
            }
        }

        private bool CanManageGroup()
        {
            string uid = ZaloApp.Session == null ? string.Empty : ZaloApp.Session.Uid;
            return uid.Length > 0 && (uid == _creatorId || _adminIds.Contains(uid));
        }

        private void UpdateMuteGroupAction()
        {
            if (_conversation == null) return;
            MuteGroupActionTextBlock.Text = _conversation.IsMuted ? "Bật thông báo" : "Tắt thông báo";
            MuteGroupIconTextBlock.Text = _conversation.IsMuted ? "\uE768" : "\uE7ED";
        }

        private void BuildGroupMediaItems()
        {
            _groupMediaItems = _conversation.Messages
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
                    return new ConversationMediaItem
                    {
                        Title = link ? (message.LinkPreviewTitle.Length > 0 ? message.LinkPreviewTitle : url)
                            : message.HasFile ? (message.FileName ?? "Tệp") : kind,
                        Subtitle = kind + " · " + ZaloFormat.Time(message.Ts),
                        Url = url,
                        IsImage = !link && !message.HasVideo && !message.HasFile && !message.IsSticker
                    };
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Url))
                .ToList();
            GroupMediaCountTextBlock.Text = _groupMediaItems.Count == 0 ? string.Empty : _groupMediaItems.Count + "+";
            GroupMediaSummaryTextBlock.Text = _groupMediaItems.Count == 0
                ? "Nội dung đã tải trong nhóm sẽ xuất hiện tại đây."
                : _groupMediaItems.Count + " mục gần đây đã tải · chạm để xem";
        }

        private async void GroupMedia_Click(object sender, RoutedEventArgs e)
        {
            var content = new StackPanel();
            if (_groupMediaItems.Count == 0)
                content.Children.Add(new TextBlock
                {
                    Text = "Chưa có ảnh, tệp hoặc liên kết trong phần lịch sử nhóm đã tải.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 12, 4, 12)
                });
            ConversationMediaItem selected = null;
            foreach (ConversationMediaItem item in _groupMediaItems.Take(12))
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
                button.Click += (s, args) => selected = item;
                content.Children.Add(button);
            }
            await new ContentDialog
            {
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                Title = "Ảnh, file, link",
                Content = new ScrollViewer { Content = content, MaxHeight = 470, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = "Đóng",
                DefaultButton = ContentDialogButton.Primary,
            }.ShowAsync();
            if (selected == null) return;
            Uri uri;
            if (selected.IsImage && Uri.TryCreate(selected.Url, UriKind.Absolute, out uri))
                Frame.Navigate(typeof(ImageViewerPage), selected.Url);
            else if (Uri.TryCreate(selected.Url, UriKind.Absolute, out uri))
                await Windows.System.Launcher.LaunchUriAsync(uri);
        }

        private void SearchGroupMessages_Click(object sender, RoutedEventArgs e)
        {
            if (_conversation == null) return;
            Frame.Navigate(typeof(SearchResultsPage), new SearchPageRequest
            {
                ConversationKey = _conversation.Key,
                Query = string.Empty
            });
        }

        private async void GroupInvite_Click(object sender, RoutedEventArgs e)
        {
            if (!CanManageGroup())
            {
                await new MessageDialog("Chỉ trưởng nhóm hoặc phó nhóm mới có thể quản lý link mời.", "Link mời").ShowAsync();
                return;
            }

            GroupInviteLinkPanel.Visibility = Visibility.Visible;
            GeneralTransform transform = GroupInviteLinkPanel.TransformToVisual(GroupInfoScrollViewer);
            Point position = transform.TransformPoint(new Point(0, 0));
            GroupInfoScrollViewer.ChangeView(null, GroupInfoScrollViewer.VerticalOffset + position.Y, null, false);
        }

        private async void GroupWallpaper_Click(object sender, RoutedEventArgs e)
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
                grid.Items.Add(new Border
                {
                    Width = 78,
                    Height = 96,
                    Margin = new Thickness(3),
                    BorderBrush = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xDD, 0xE3, 0xE9)),
                    BorderThickness = new Thickness(1),
                    Child = image,
                    Tag = uri
                });
            }

            var dialog = new ContentDialog
            {
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                Title = "Đổi hình nền cuộc trò chuyện",
                Content = grid,
                PrimaryButtonText = "Áp dụng",
                SecondaryButtonText = "Dùng hình nền chung",
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = ElementTheme.Light
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
                ZaloUWP.Services.ZaloSettingsService.Current.SetConversationWallpaper(
                    settingKey, _conversation.Key, string.Empty);
            }
        }

        private async void MuteGroup_Click(object sender, RoutedEventArgs e)
        {
            if (_conversation == null) return;
            MuteGroupButton.IsEnabled = false;
            try
            {
                await ZaloApp.SetMutedAsync(_conversation, !_conversation.IsMuted);
                UpdateMuteGroupAction();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Group mute update failed: " + ex.Message);
                await new MessageDialog("Không đổi được cài đặt thông báo cho nhóm. Hãy thử lại sau.", "Thông báo nhóm").ShowAsync();
            }
            finally
            {
                MuteGroupButton.IsEnabled = true;
            }
        }

        private async System.Threading.Tasks.Task LoadGroupLinkAsync()
        {
            _groupInviteLink = string.Empty;
            GroupLinkStatusTextBlock.Text = "Đang tải link mời…";
            GroupLinkTextBlock.Text = string.Empty;
            CreateGroupLinkButton.Visibility = Visibility.Collapsed;
            CopyGroupLinkButton.Visibility = Visibility.Collapsed;
            DisableGroupLinkButton.Visibility = Visibility.Collapsed;
            try
            {
                ZaloGroupLinkInfo info = await ZaloMessaging.GetGroupLinkInfoAsync(
                    ZaloApp.Session, _conversation.ThreadId, CancellationToken.None);
                if (!info.Enabled)
                {
                    GroupLinkStatusTextBlock.Text = "Link mời đang tắt.";
                    CreateGroupLinkButton.Visibility = Visibility.Visible;
                    return;
                }
                if (string.IsNullOrWhiteSpace(info.Link))
                {
                    GroupLinkStatusTextBlock.Text = "Link đang bật nhưng máy chủ chưa trả URL. Hãy làm mới thông tin nhóm.";
                    DisableGroupLinkButton.Visibility = Visibility.Visible;
                    return;
                }
                _groupInviteLink = info.Link;
                GroupLinkTextBlock.Text = info.Link;
                string expiration = FormatLinkExpiration(info.Expiration);
                GroupLinkStatusTextBlock.Text = expiration.Length == 0 ? "Link mời đang hoạt động." : "Link mời đang hoạt động · Hết hạn " + expiration;
                CopyGroupLinkButton.Visibility = Visibility.Visible;
                DisableGroupLinkButton.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Group link load failed: " + ex.Message);
                GroupLinkStatusTextBlock.Text = "Không tải được link mời. Hãy thử làm mới.";
            }
        }

        private static string FormatLinkExpiration(long timestamp)
        {
            if (timestamp <= 0) return string.Empty;
            try
            {
                if (timestamp < 100000000000L) timestamp *= 1000;
                return DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            }
            catch { return string.Empty; }
        }

        private async void CreateGroupLink_Click(object sender, RoutedEventArgs e)
        {
            if (!CanManageGroup()) return;
            CreateGroupLinkButton.IsEnabled = false;
            try
            {
                ZaloGroupLinkInfo result = await ZaloMessaging.EnableGroupLinkAsync(
                    ZaloApp.Session, _conversation.ThreadId, CancellationToken.None);
                if (!result.Enabled || string.IsNullOrWhiteSpace(result.Link))
                    throw new InvalidOperationException("Zalo chưa xác nhận link mời đã bật.");
                await LoadGroupLinkAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Group link creation failed: " + ex.Message);
                GroupLinkStatusTextBlock.Text = "Không tạo được link mời. Hãy kiểm tra quyền quản trị.";
            }
            finally { CreateGroupLinkButton.IsEnabled = true; }
        }

        private void CopyGroupLink_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_groupInviteLink)) return;
            var data = new DataPackage();
            data.SetText(_groupInviteLink);
            Clipboard.SetContent(data);
            GroupLinkStatusTextBlock.Text = "Đã sao chép link mời.";
        }

        private async void DisableGroupLink_Click(object sender, RoutedEventArgs e)
        {
            if (!CanManageGroup()) return;
            if (!await ConfirmMemberActionAsync("Vô hiệu hóa link mời hiện tại? Người khác sẽ không thể dùng link này để vào nhóm.", "Vô hiệu hóa link nhóm")) return;
            try
            {
                await ZaloMessaging.DisableGroupLinkAsync(ZaloApp.Session, _conversation.ThreadId, CancellationToken.None);
                await LoadGroupLinkAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Group link disable failed: " + ex.Message);
                await new MessageDialog("Không vô hiệu hóa được link mời. Hãy thử lại sau.", "Link mời nhóm").ShowAsync();
            }
        }

        private void Member_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e)
        {
            ShowMemberActions(sender as FrameworkElement);
            e.Handled = true;
        }

        private void Member_RightTapped(object sender, Windows.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            ShowMemberActions(sender as FrameworkElement);
            e.Handled = true;
        }

        private void ShowMemberActions(FrameworkElement target)
        {
            var member = target == null ? null : target.DataContext as UserInfo;
            string ownUid = ZaloApp.Session == null ? string.Empty : ZaloApp.Session.Uid;
            bool isOwner = ownUid.Length > 0 && ownUid == _creatorId;
            bool canManage = isOwner || (ownUid.Length > 0 && _adminIds.Contains(ownUid));
            if (member == null || !canManage || member.UserId == ownUid || member.IsGroupOwner) return;

            var menu = new MenuFlyout();
            var deputy = new MenuFlyoutItem
            {
                Text = member.IsGroupDeputy ? "Gỡ quyền phó nhóm" : "Bổ nhiệm phó nhóm"
            };
            deputy.Click += async (s, e) => await SetDeputyAsync(member, !member.IsGroupDeputy);
            menu.Items.Add(deputy);

            if (isOwner)
            {
                var owner = new MenuFlyoutItem { Text = "Chuyển quyền trưởng nhóm" };
                owner.Click += async (s, e) => await ChangeOwnerAsync(member);
                menu.Items.Add(owner);
            }

            var remove = new MenuFlyoutItem { Text = "Xóa khỏi nhóm" };
            remove.Click += async (s, e) => await RemoveMemberAsync(member);
            menu.Items.Add(remove);
            menu.ShowAt(target);
        }

        private async System.Threading.Tasks.Task SetDeputyAsync(UserInfo member, bool deputy)
        {
            string action = deputy ? "bổ nhiệm làm phó nhóm" : "gỡ quyền phó nhóm";
            if (!await ConfirmMemberActionAsync("Bạn có chắc muốn " + action + " cho " + member.DisplayName + "?", "Quyền quản trị nhóm")) return;
            await RunMemberActionAsync("Group deputy update failed", async () =>
            {
                await ZaloMessaging.SetGroupDeputyAsync(ZaloApp.Session, _conversation.ThreadId, member.UserId, deputy, CancellationToken.None);
            });
        }

        private async System.Threading.Tasks.Task ChangeOwnerAsync(UserInfo member)
        {
            if (!await ConfirmMemberActionAsync("Chuyển quyền trưởng nhóm cho " + member.DisplayName + "? Sau khi chuyển, quyền quản trị của bạn có thể thay đổi.", "Chuyển quyền trưởng nhóm")) return;
            await RunMemberActionAsync("Group owner change failed", async () =>
            {
                await ZaloMessaging.ChangeGroupOwnerAsync(ZaloApp.Session, _conversation.ThreadId, member.UserId, CancellationToken.None);
            });
        }

        private async System.Threading.Tasks.Task RemoveMemberAsync(UserInfo member)
        {
            if (!await ConfirmMemberActionAsync("Xóa " + member.DisplayName + " khỏi nhóm?", "Xóa thành viên")) return;
            await RunMemberActionAsync("Group member removal failed", async () =>
            {
                await ZaloMessaging.RemoveGroupMemberAsync(ZaloApp.Session, _conversation.ThreadId, member.UserId, CancellationToken.None);
            });
        }

        private async System.Threading.Tasks.Task<bool> ConfirmMemberActionAsync(string message, string title)
        {
            var dialog = new MessageDialog(message, title);
            dialog.Commands.Add(new UICommand("Xác nhận"));
            dialog.Commands.Add(new UICommand("Hủy"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;
            return (await dialog.ShowAsync()).Label == "Xác nhận";
        }

        private async System.Threading.Tasks.Task RunMemberActionAsync(string logMessage, Func<System.Threading.Tasks.Task> action)
        {
            try
            {
                await action();
                await LoadGroupInfoAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add(logMessage + ": " + ex.Message);
                await new MessageDialog("Không thực hiện được thao tác với thành viên. Hãy tải lại nhóm và kiểm tra quyền quản trị.", "Quản lý nhóm").ShowAsync();
            }
        }

        private async void AddMember_Click(object sender, RoutedEventArgs e)
        {
            var friends = (ZaloApp.Friends ?? new List<ZaloFriend>())
                .Where(f => !string.IsNullOrWhiteSpace(f.UserId))
                .ToList();
            if (friends.Count == 0)
            {
                await new MessageDialog("Danh bạ chưa có bạn để mời vào nhóm.", "Thêm thành viên").ShowAsync();
                return;
            }

            var picker = new ComboBox
            {
                ItemsSource = friends,
                DisplayMemberPath = "Name",
                PlaceholderText = "Chọn bạn bè",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 44,
                FontSize = 16
            };
            var inviteContent = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
            inviteContent.Children.Add(new TextBlock
            {
                Text = "Chọn một người trong danh bạ để mời vào nhóm.",
                FontSize = 13,
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x70, 0x77, 0x7F)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            inviteContent.Children.Add(picker);
            var dialog = new ContentDialog
            {
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                Title = "Mời bạn vào nhóm",
                Content = inviteContent,
                PrimaryButtonText = "Mời",
                SecondaryButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = ElementTheme.Light
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var selected = picker.SelectedItem as ZaloFriend;
            if (selected == null)
            {
                await new MessageDialog("Hãy chọn một người bạn.", "Thêm thành viên").ShowAsync();
                return;
            }

            try
            {
                await ZaloMessaging.InviteGroupMembersAsync(ZaloApp.Session, _conversation.ThreadId,
                    new List<string> { selected.UserId }, CancellationToken.None);
                await LoadGroupInfoAsync();
            }
            catch (Exception ex)
            {
                await new MessageDialog("Không mời được thành viên. " + ex.Message, "Thêm thành viên").ShowAsync();
            }
        }

        private async void RefreshGroup_Click(object sender, RoutedEventArgs e)
        {
            await System.Threading.Tasks.Task.WhenAll(LoadGroupInfoAsync(), LoadGroupBoardAsync(), LoadGroupRemindersAsync());
        }

        private async void RefreshGroupBoard_Click(object sender, RoutedEventArgs e)
        {
            await LoadGroupBoardAsync();
        }

        private async void RefreshGroupReminders_Click(object sender, RoutedEventArgs e)
        {
            await LoadGroupRemindersAsync();
        }

        private async void CreateGroupReminder_Click(object sender, RoutedEventArgs e)
        {
            if (_conversation == null || ZaloApp.Session == null) return;
            DateTimeOffset initial = DateTimeOffset.Now.AddHours(1);
            var title = new TextBox { PlaceholderText = "Nội dung nhắc hẹn", MaxLength = 200 };
            var date = new DatePicker { Date = initial.Date };
            var time = new TimePicker { Time = initial.TimeOfDay, ClockIdentifier = "24HourClock" };
            var repeat = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Lặp lại" };
            repeat.Items.Add(new ComboBoxItem { Content = "Không lặp", Tag = "0" });
            repeat.Items.Add(new ComboBoxItem { Content = "Hằng ngày", Tag = "1" });
            repeat.Items.Add(new ComboBoxItem { Content = "Hằng tuần", Tag = "2" });
            repeat.Items.Add(new ComboBoxItem { Content = "Hằng tháng", Tag = "3" });
            repeat.SelectedIndex = 0;
            var form = new StackPanel();
            form.Children.Add(new TextBlock { Text = "Nội dung", FontWeight = Windows.UI.Text.FontWeights.SemiBold, FontSize = 12 });
            form.Children.Add(title);
            form.Children.Add(new TextBlock { Text = "Ngày", FontWeight = Windows.UI.Text.FontWeights.SemiBold, FontSize = 12 });
            form.Children.Add(date);
            form.Children.Add(new TextBlock { Text = "Giờ", FontWeight = Windows.UI.Text.FontWeights.SemiBold, FontSize = 12 });
            form.Children.Add(time);
            form.Children.Add(new TextBlock { Text = "Lặp lại", FontWeight = Windows.UI.Text.FontWeights.SemiBold, FontSize = 12 });
            form.Children.Add(repeat);
            StackPanelSpacingCompat.Apply(form, 8);
            var dialog = new ContentDialog
            {
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                Title = "Tạo nhắc hẹn nhóm",
                Content = new ScrollViewer { Content = form, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = "Tạo nhắc hẹn",
                SecondaryButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (string.IsNullOrWhiteSpace(title.Text))
            {
                await new MessageDialog("Nhập nội dung nhắc hẹn.", "Nhắc hẹn nhóm").ShowAsync();
                return;
            }
            DateTimeOffset reminderTime = date.Date.Date.Add(time.Time);
            int repeatMode;
            ComboBoxItem selectedRepeat = repeat.SelectedItem as ComboBoxItem;
            if (selectedRepeat == null || !int.TryParse(selectedRepeat.Tag.ToString(), out repeatMode)) repeatMode = 0;
            try
            {
                await ZaloMessaging.CreateGroupReminderAsync(ZaloApp.Session, _conversation.ThreadId,
                    title.Text, reminderTime.ToUnixTimeMilliseconds(), repeatMode, CancellationToken.None);
                await LoadGroupRemindersAsync();
                await new MessageDialog("Đã gửi yêu cầu tạo nhắc hẹn.", "Nhắc hẹn nhóm").ShowAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Create group reminder failed: " + ex.Message);
                await new MessageDialog("Không tạo được nhắc hẹn. Hãy kiểm tra thời gian và thử lại.", "Nhắc hẹn nhóm").ShowAsync();
            }
        }

        private async void EditGroupReminder_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var reminder = button == null ? null : button.DataContext as ZaloGroupReminder;
            if (_conversation == null || reminder == null || !reminder.CanManage || string.IsNullOrWhiteSpace(reminder.Id)) return;
            DateTimeOffset initial;
            try { initial = DateTimeOffset.FromUnixTimeMilliseconds(reminder.StartTime).ToLocalTime(); }
            catch { initial = DateTimeOffset.Now.AddHours(1); }
            var title = new TextBox { Text = reminder.Title ?? string.Empty, MaxLength = 200 };
            var date = new DatePicker { Date = initial.Date };
            var time = new TimePicker { Time = initial.TimeOfDay, ClockIdentifier = "24HourClock" };
            var repeat = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            repeat.Items.Add(new ComboBoxItem { Content = "Không lặp", Tag = "0" });
            repeat.Items.Add(new ComboBoxItem { Content = "Hằng ngày", Tag = "1" });
            repeat.Items.Add(new ComboBoxItem { Content = "Hằng tuần", Tag = "2" });
            repeat.Items.Add(new ComboBoxItem { Content = "Hằng tháng", Tag = "3" });
            repeat.SelectedIndex = Math.Max(0, Math.Min(3, reminder.RepeatMode));
            var form = new StackPanel();
            form.Children.Add(title);
            form.Children.Add(new TextBlock { Text = "Ngày", FontSize = 12, FontWeight = Windows.UI.Text.FontWeights.SemiBold });
            form.Children.Add(date);
            form.Children.Add(new TextBlock { Text = "Giờ", FontSize = 12, FontWeight = Windows.UI.Text.FontWeights.SemiBold });
            form.Children.Add(time);
            form.Children.Add(new TextBlock { Text = "Lặp lại", FontSize = 12, FontWeight = Windows.UI.Text.FontWeights.SemiBold });
            form.Children.Add(repeat);
            StackPanelSpacingCompat.Apply(form, 8);
            var dialog = new ContentDialog
            {
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                Title = "Sửa nhắc hẹn nhóm",
                Content = form,
                PrimaryButtonText = "Lưu",
                SecondaryButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(title.Text)) return;
            DateTimeOffset reminderTime = date.Date.Date.Add(time.Time);
            int repeatMode;
            ComboBoxItem selected = repeat.SelectedItem as ComboBoxItem;
            if (selected == null || !int.TryParse(selected.Tag.ToString(), out repeatMode)) repeatMode = 0;
            try
            {
                await ZaloMessaging.UpdateGroupReminderAsync(ZaloApp.Session, _conversation.ThreadId,
                    reminder.Id, title.Text, reminderTime.ToUnixTimeMilliseconds(), repeatMode, CancellationToken.None);
                await LoadGroupRemindersAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Edit group reminder failed: " + ex.Message);
                await new MessageDialog("Không sửa được nhắc hẹn. Hãy kiểm tra thời gian và thử lại.", "Nhắc hẹn nhóm").ShowAsync();
            }
        }

        private async void DeleteGroupReminder_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var reminder = button == null ? null : button.DataContext as ZaloGroupReminder;
            if (_conversation == null || reminder == null || !reminder.CanManage || string.IsNullOrWhiteSpace(reminder.Id)) return;
            var confirm = new MessageDialog("Xóa nhắc hẹn ‘" + reminder.Title + "’ khỏi nhóm?", "Xóa nhắc hẹn");
            confirm.Commands.Add(new UICommand("Xóa"));
            confirm.Commands.Add(new UICommand("Hủy"));
            confirm.DefaultCommandIndex = 1;
            confirm.CancelCommandIndex = 1;
            if ((await confirm.ShowAsync()).Label != "Xóa") return;
            try
            {
                await ZaloMessaging.RemoveGroupReminderAsync(ZaloApp.Session, _conversation.ThreadId,
                    reminder.Id, CancellationToken.None);
                await LoadGroupRemindersAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Delete group reminder failed: " + ex.Message);
                await new MessageDialog("Không xóa được nhắc hẹn. Hãy thử lại sau.", "Nhắc hẹn nhóm").ShowAsync();
            }
        }

        private async void CreateGroupPoll_Click(object sender, RoutedEventArgs e)
        {
            if (_conversation == null || ZaloApp.Session == null) return;
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = "Câu hỏi", FontSize = 12, FontWeight = Windows.UI.Text.FontWeights.SemiBold });
            var question = new TextBox { PlaceholderText = "Nhập câu hỏi bình chọn", MaxLength = 200 };
            content.Children.Add(question);
            content.Children.Add(new TextBlock { Text = "Các lựa chọn (ít nhất 2)", FontSize = 12, FontWeight = Windows.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
            var optionPanel = new StackPanel();
            var optionBoxes = new List<TextBox>();
            var addOption = new Button { Content = "+ Thêm lựa chọn", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 5, 10, 5), FontSize = 12, UseSystemFocusVisuals = false };
            Action addOptionBox = () =>
            {
                var box = new TextBox { PlaceholderText = "Lựa chọn " + (optionBoxes.Count + 1), MaxLength = 100, Margin = new Thickness(0, 0, 0, 6) };
                optionBoxes.Add(box);
                optionPanel.Children.Add(box);
                addOption.IsEnabled = optionBoxes.Count < 10;
            };
            addOptionBox();
            addOptionBox();
            addOption.Click += (s, args) => addOptionBox();
            content.Children.Add(optionPanel);
            content.Children.Add(addOption);
            var multiChoice = new CheckBox { Content = "Cho phép chọn nhiều phương án" };
            var anonymous = new CheckBox { Content = "Ẩn danh người bình chọn" };
            content.Children.Add(multiChoice);
            content.Children.Add(anonymous);
            StackPanelSpacingCompat.Apply(content, 8);

            var dialog = new ContentDialog
            {
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                Title = "Tạo bình chọn",
                Content = new ScrollViewer { Content = content, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = "Tạo",
                SecondaryButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            List<string> options = optionBoxes.Select(box => (box.Text ?? string.Empty).Trim())
                .Where(value => value.Length > 0).ToList();
            if (string.IsNullOrWhiteSpace(question.Text) || options.Count < 2)
            {
                await new MessageDialog("Nhập câu hỏi và ít nhất hai lựa chọn.", "Tạo bình chọn").ShowAsync();
                return;
            }

            try
            {
                await ZaloMessaging.CreatePollAsync(ZaloApp.Session, _conversation.ThreadId, question.Text,
                    options, multiChoice.IsChecked == true, anonymous.IsChecked == true, CancellationToken.None);
                await LoadGroupBoardAsync();
                await new MessageDialog("Đã gửi yêu cầu tạo bình chọn cho nhóm.", "Bình chọn").ShowAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Create poll failed: " + ex.Message);
                await new MessageDialog("Không tạo được bình chọn. Hãy thử lại sau.", "Bình chọn").ShowAsync();
            }
        }

        private async void CreateGroupNote_Click(object sender, RoutedEventArgs e)
        {
            if (_conversation == null || ZaloApp.Session == null) return;
            var noteText = new TextBox
            {
                PlaceholderText = "Nhập nội dung ghi chú cho nhóm",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MaxLength = 500
            };
            var pin = new CheckBox { Content = "Ghim ghi chú lên đầu bảng nhóm" };
            var content = new StackPanel();
            content.Children.Add(noteText);
            content.Children.Add(pin);
            StackPanelSpacingCompat.Apply(content, 8);
            var dialog = new ContentDialog
            {
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                Title = "Tạo ghi chú nhóm",
                Content = content,
                PrimaryButtonText = "Tạo ghi chú",
                SecondaryButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (string.IsNullOrWhiteSpace(noteText.Text))
            {
                await new MessageDialog("Nhập nội dung ghi chú.", "Ghi chú nhóm").ShowAsync();
                return;
            }
            try
            {
                await ZaloMessaging.CreateGroupNoteAsync(ZaloApp.Session, _conversation.ThreadId,
                    noteText.Text, pin.IsChecked == true, CancellationToken.None);
                await LoadGroupBoardAsync();
                await new MessageDialog("Đã gửi yêu cầu tạo ghi chú nhóm.", "Ghi chú nhóm").ShowAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Create group note failed: " + ex.Message);
                await new MessageDialog("Không tạo được ghi chú nhóm. Hãy thử lại sau.", "Ghi chú nhóm").ShowAsync();
            }
        }

        private async void EditGroupNote_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var entry = button == null ? null : button.DataContext as ZaloGroupBoardEntry;
            if (_conversation == null || entry == null || string.IsNullOrWhiteSpace(entry.TopicId)) return;
            var noteText = new TextBox
            {
                Text = entry.Title ?? string.Empty,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MaxLength = 500
            };
            var dialog = new ContentDialog
            {
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                Title = "Sửa ghi chú nhóm",
                Content = noteText,
                PrimaryButtonText = "Lưu",
                SecondaryButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (string.IsNullOrWhiteSpace(noteText.Text))
            {
                await new MessageDialog("Nội dung ghi chú không được để trống.", "Sửa ghi chú").ShowAsync();
                return;
            }
            try
            {
                await ZaloMessaging.UpdateGroupNoteAsync(ZaloApp.Session, _conversation.ThreadId,
                    entry.TopicId, noteText.Text, CancellationToken.None);
                await LoadGroupBoardAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Edit group note failed: " + ex.Message);
                await new MessageDialog("Không sửa được ghi chú. Hãy thử lại sau.", "Sửa ghi chú").ShowAsync();
            }
        }

        private async void ViewGroupBoardEntry_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            ZaloGroupBoardEntry entry = button == null ? null : button.Tag as ZaloGroupBoardEntry;
            if (entry == null) return;
            var content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = entry.Title ?? string.Empty,
                    FontSize = 15,
                    Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x24, 0x28, 0x2C)),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                },
                MaxHeight = 440,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            await new ContentDialog
            {
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                Title = string.IsNullOrWhiteSpace(entry.Subtitle) ? "Nội dung nhóm" : entry.Subtitle,
                Content = content,
                PrimaryButtonText = "Đóng",
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = ElementTheme.Light
            }.ShowAsync();
        }

        private async void GroupPoll_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var entry = button == null ? null : button.DataContext as ZaloGroupBoardEntry;
            if (entry == null || entry.PollId <= 0 || ZaloApp.Session == null) return;
            try
            {
                ZaloPollDetail poll = await ZaloMessaging.GetPollDetailAsync(
                    ZaloApp.Session, entry.PollId, CancellationToken.None);
                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text = poll.VoteCount + " lượt bình chọn" + (poll.AllowMultiple ? " · được chọn nhiều đáp án" : string.Empty),
                    FontSize = 12,
                    Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x75, 0x85, 0x96)),
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
                        Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                        TextWrapping = TextWrapping.Wrap
                    });
                    optionContent.Children.Add(new TextBlock
                    {
                        Text = option.Votes + " phiếu",
                        FontSize = 12,
                        Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x75, 0x85, 0x96)),
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                    optionContent.Children.Add(new ProgressBar
                    {
                        Minimum = 0,
                        Maximum = 100,
                        Value = votePercent,
                        Height = 5,
                        Margin = new Thickness(0, 6, 0, 0),
                        Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x18, 0xA0, 0xD9)),
                        Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE8, 0xEF, 0xF5))
                    });
                    FrameworkElement control;
                    if (poll.AllowMultiple)
                    {
                        control = new CheckBox { Content = optionContent, IsChecked = option.Voted, Tag = option.OptionId, IsEnabled = !poll.Closed, Margin = new Thickness(0, 4, 0, 4) };
                    }
                    else
                    {
                        control = new RadioButton { Content = optionContent, IsChecked = option.Voted, Tag = option.OptionId,
                            Margin = new Thickness(0, 4, 0, 4),
                            GroupName = "ZaloPoll_" + poll.PollId, IsEnabled = !poll.Closed };
                    }
                    choices.Add(control);
                    content.Children.Add(control);
                }
                if (poll.Closed)
                    content.Children.Add(new TextBlock { Text = "Bình chọn này đã đóng.", FontSize = 12, Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xB4, 0x23, 0x18)) });

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
                    if (isChecked)
                    {
                        long optionId;
                        if (choice.Tag != null && long.TryParse(choice.Tag.ToString(), out optionId)) selected.Add(optionId);
                    }
                }
                if (!poll.AllowMultiple && selected.Count == 0)
                {
                    await new MessageDialog("Hãy chọn một phương án.", "Bình chọn").ShowAsync();
                    return;
                }
                await ZaloMessaging.VotePollAsync(ZaloApp.Session, poll.PollId, selected, CancellationToken.None);
                await LoadGroupBoardAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Poll action failed: " + ex.Message);
                await new MessageDialog("Không tải hoặc gửi được bình chọn. Hãy thử lại sau.", "Bình chọn").ShowAsync();
            }
        }

        private async void ChangeGroupName_Click(object sender, RoutedEventArgs e)
        {
            if (_conversation == null) return;
            var input = new TextBox
            {
                Text = _conversation.Name ?? string.Empty,
                PlaceholderText = "Tên nhóm",
                MaxLength = 50
            };
            var dialog = new ContentDialog
            {
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                Title = "Đổi tên nhóm",
                Content = input,
                PrimaryButtonText = "Lưu",
                SecondaryButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            string newName = (input.Text ?? string.Empty).Trim();
            if (newName.Length == 0)
            {
                await new MessageDialog("Tên nhóm không được để trống.", "Đổi tên nhóm").ShowAsync();
                return;
            }
            if (string.Equals(newName, _conversation.Name, StringComparison.Ordinal)) return;

            try
            {
                await ZaloMessaging.ChangeGroupNameAsync(ZaloApp.Session, _conversation.ThreadId, newName, CancellationToken.None);
                _conversation.Name = newName;
                GroupNameTextBlock.Text = newName;
                await ZaloApp.SaveCachedConversationsAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Group name update failed: " + ex.Message);
                await new MessageDialog("Không đổi được tên nhóm. Có thể bạn không có quyền quản lý nhóm. " + ex.Message, "Đổi tên nhóm").ShowAsync();
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private async void LeaveGroup_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new MessageDialog("Bạn sẽ không còn nhận tin nhắn mới từ nhóm này.", "Rời nhóm");
            dialog.Commands.Add(new UICommand("Rời nhóm", async _ =>
            {
                try
                {
                    await ZaloMessaging.LeaveGroupAsync(ZaloApp.Session, _conversation.ThreadId, CancellationToken.None);
                    ZaloApp.Groups.Remove(_conversation);
                    ZaloApp.Conversations.Remove(_conversation);
                    if (Frame.CanGoBack) Frame.GoBack();
                }
                catch (Exception ex)
                {
                    await new MessageDialog("Không rời được nhóm. " + ex.Message, "Rời nhóm").ShowAsync();
                }
            }));
            dialog.Commands.Add(new UICommand("Hủy"));
            dialog.DefaultCommandIndex = 0;
            dialog.CancelCommandIndex = 1;
            await dialog.ShowAsync();
        }
    }
}
