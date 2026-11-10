using System;
using System.Linq;
using Windows.Data.Json;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using ZaloLite;
using ZaloUWP.Services;

namespace ZaloUWP.Views
{
    public sealed partial class FriendProfilePage : Page
    {
        private ZaloFriend _friend;

        public FriendProfilePage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _friend = e.Parameter as ZaloFriend;
            if (_friend == null || string.IsNullOrWhiteSpace(_friend.UserId))
            {
                ProfileFeedback.Text = "Không có thông tin người dùng để hiển thị.";
                return;
            }

            NameText.Text = _friend.Name ?? "Người dùng Zalo";
            UserIdText.Text = _friend.UserId;
            UpdateBlockButton();
            SetImage(AvatarBrush, _friend.Avatar);
            PhoneRow.Visibility = Visibility.Collapsed;
            BirthdayRow.Visibility = Visibility.Collapsed;
            BirthdayDivider.Visibility = Visibility.Collapsed;

            try
            {
                var profile = await ZaloAuth.GetFriendProfileAsync(
                    ZaloApp.Session, _friend.UserId, System.Threading.CancellationToken.None);
                string name = First(profile, "display_name", "displayName", "zalo_name", "zaloName");
                string avatar = First(profile, "avatar");
                string cover = First(profile, "cover", "bgavatar", "background");
                string status = First(profile, "status", "statusMessage");
                string phone = First(profile, "phoneNumber", "phone");
                string birthday = First(profile, "sdob", "birthday");

                if (!string.IsNullOrWhiteSpace(name)) NameText.Text = name;
                if (!string.IsNullOrWhiteSpace(avatar)) SetImage(AvatarBrush, avatar);
                if (!string.IsNullOrWhiteSpace(cover)) SetImage(CoverImage, cover);
                if (!string.IsNullOrWhiteSpace(status)) StatusText.Text = status;
                if (!string.IsNullOrWhiteSpace(phone))
                {
                    PhoneText.Text = phone;
                    PhoneRow.Visibility = Visibility.Visible;
                }
                if (!string.IsNullOrWhiteSpace(birthday))
                {
                    BirthdayText.Text = birthday;
                    BirthdayRow.Visibility = Visibility.Visible;
                    BirthdayDivider.Visibility = Visibility.Visible;
                }

                string lastAction = First(profile, "lastActionTime");
                DateTimeOffset lastSeen;
                if (long.TryParse(lastAction, out long timestamp) && timestamp > 0)
                {
                    if (timestamp < 100000000000L) timestamp *= 1000;
                    try
                    {
                        lastSeen = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
                        OnlineText.Text = lastSeen > DateTimeOffset.Now.AddMinutes(-5)
                            ? "Đang hoạt động" : "Truy cập " + lastSeen.ToLocalTime().ToString("dd/MM HH:mm");
                    }
                    catch (ArgumentOutOfRangeException) { }
                }
                ProfileFeedback.Text = "Thông tin hồ sơ được tải từ Zalo.";
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Friend profile lookup failed: " + ex.Message);
                ProfileFeedback.Text = "Chưa tải được hồ sơ mới nhất. Đang hiển thị thông tin đã có trong danh bạ.";
            }
        }

        private static string First(Windows.Data.Json.JsonObject profile, params string[] keys)
        {
            foreach (string key in keys)
            {
                string value = ZaloAuth.Str(profile, key);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return string.Empty;
        }

        private static void SetImage(ImageBrush brush, string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return;
            Uri uri;
            if (Uri.TryCreate(source, UriKind.Absolute, out uri)
                && (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                try { brush.ImageSource = new BitmapImage(uri); } catch { }
            }
        }

        private static void SetImage(Image image, string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return;
            Uri uri;
            if (Uri.TryCreate(source, UriKind.Absolute, out uri)
                && (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                try { image.Source = new BitmapImage(uri); } catch { }
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private void MessageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_friend == null || string.IsNullOrWhiteSpace(_friend.UserId)) return;
            Frame.Navigate(typeof(ChatPage), _friend);
        }

        private void UpdateBlockButton()
        {
            string accountId = ZaloApp.Session == null ? null : ZaloApp.Session.Uid;
            bool blocked = ZaloSettingsService.Current.GetBlockedUserIds(accountId).Contains(_friend.UserId);
            BlockFriendButton.Content = blocked ? "Bỏ chặn người dùng" : "Chặn người dùng";
            BlockFriendButton.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF,
                blocked ? (byte)0x08 : (byte)0xB4, blocked ? (byte)0x7A : (byte)0x23, blocked ? (byte)0xF5 : (byte)0x18));
        }

        private async void LoadFriendPosts_Click(object sender, RoutedEventArgs e)
        {
            if (_friend == null || ZaloApp.Session == null) return;
            FriendPostsStatus.Text = "\u0110ang t\u1ea3i b\u00e0i vi\u1ebft...";
            try
            {
                var rawPosts = await ZaloMessaging.GetFriendBoardListAsync(ZaloApp.Session, _friend.UserId,
                    System.Threading.CancellationToken.None);
                var cards = new System.Collections.Generic.List<FriendBoardCard>();
                foreach (Windows.Data.Json.JsonObject post in rawPosts)
                {
                    string body = First(post, "content", "text", "description", "desc", "title");
                    if (string.IsNullOrWhiteSpace(body)) body = post.Stringify();
                    string timestamp = First(post, "ts", "createTime", "createdTime", "time");
                    long ts;
                    if (long.TryParse(timestamp, out ts) && ts > 0)
                    {
                        if (ts < 100000000000L) ts *= 1000;
                        try { timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().ToString("dd/MM/yyyy HH:mm"); }
                        catch { timestamp = string.Empty; }
                    }
                    cards.Add(new FriendBoardCard { Text = body, TimeText = timestamp });
                }
                FriendPostsList.ItemsSource = cards;
                FriendPostsList.Visibility = Visibility.Visible;
                FriendPostsStatus.Text = cards.Count == 0 ? "Ch\u01b0a c\u00f3 b\u00e0i \u0111\u0103ng hi\u1ec3n th\u1ecb." : cards.Count + " b\u00e0i vi\u1ebft";
            }
            catch (Exception ex)
            {
                FriendPostsStatus.Text = "Kh\u00f4ng t\u1ea3i \u0111\u01b0\u1ee3c b\u00e0i vi\u1ebft. Ch\u1ea1m \u0111\u1ec3 th\u1eed l\u1ea1i.";
                ZaloLog.Add("Friend posts load failed: " + ex.Message);
            }
        }

        private sealed class FriendBoardCard
        {
            public string Text { get; set; }
            public string TimeText { get; set; }
        }
        private async void LoadMutualGroups_Click(object sender, RoutedEventArgs e)
        {
            if (_friend == null || ZaloApp.Session == null) return;
            MutualGroupsStatus.Text = "\u0110ang t\u00ecm nh\u00f3m chung...";
            try
            {
                var groups = ZaloApp.Groups == null ? new System.Collections.Generic.List<ZaloConversation>()
                    : ZaloApp.Groups.Where(g => g != null && !string.IsNullOrWhiteSpace(g.ThreadId)).Take(60).ToList();
                if (groups.Count == 0)
                {
                    MutualGroupsStatus.Text = "Ch\u01b0a c\u00f3 d\u1eef li\u1ec7u nh\u00f3m trong phi\u00ean n\u00e0y.";
                    MutualGroupsList.Visibility = Visibility.Visible;
                    MutualGroupsList.ItemsSource = null;
                    return;
                }
                var infoMap = await ZaloMessaging.GetGroupInfoMapAsync(ZaloApp.Session,
                    groups.Select(g => g.ThreadId).ToList(), System.Threading.CancellationToken.None);
                var names = new System.Collections.Generic.List<string>();
                foreach (var group in groups)
                {
                    Windows.Data.Json.JsonObject info = null;
                    if (infoMap == null || !infoMap.TryGetValue(group.ThreadId, out info) || info == null) continue;
                    Windows.Data.Json.IJsonValue memberValue;
                    if (!info.TryGetValue("currentMems", out memberValue) || memberValue.ValueType != JsonValueType.Array) continue;
                    bool found = false;
                    foreach (Windows.Data.Json.IJsonValue item in memberValue.GetArray())
                    {
                        if (item.ValueType == JsonValueType.Object && ZaloAuth.Str(item.GetObject(), "id") == _friend.UserId) { found = true; break; }
                    }
                    if (found) names.Add(string.IsNullOrWhiteSpace(group.Name) ? ZaloAuth.Str(info, "name") : group.Name);
                }
                MutualGroupsList.ItemsSource = names;
                MutualGroupsList.Visibility = Visibility.Visible;
                MutualGroupsStatus.Text = names.Count == 0 ? "Kh\u00f4ng t\u00ecm th\u1ea5y nh\u00f3m chung trong c\u00e1c nh\u00f3m \u0111\u00e3 t\u1ea3i." : names.Count + " nh\u00f3m chung";
            }
            catch (Exception ex)
            {
                MutualGroupsStatus.Text = "Kh\u00f4ng t\u1ea3i \u0111\u01b0\u1ee3c nh\u00f3m chung. Ch\u1ea1m \u0111\u1ec3 th\u1eed l\u1ea1i.";
                ZaloLog.Add("Mutual groups load failed: " + ex.Message);
            }
        }
        private async void RemoveFriendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_friend == null || ZaloApp.Session == null) return;
            var confirm = new Windows.UI.Popups.MessageDialog("X\u00f3a ng\u01b0\u1eddi n\u00e0y kh\u1ecfi danh b\u1ea1?", "H\u1ee7y k\u1ebft b\u1ea1n");
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("H\u1ee7y k\u1ebft b\u1ea1n"));
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("H\u1ee7y"));
            confirm.DefaultCommandIndex = 1;
            if ((await confirm.ShowAsync()).Label != "H\u1ee7y k\u1ebft b\u1ea1n") return;
            try
            {
                await ZaloAuth.RemoveFriendAsync(ZaloApp.Session, _friend.UserId, System.Threading.CancellationToken.None);
                if (ZaloApp.Friends != null) ZaloApp.Friends.RemoveAll(f => f.UserId == _friend.UserId);
                await ZaloApp.SaveCachedFriendsAsync();
                if (Frame.CanGoBack) Frame.GoBack();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Remove friend failed: " + ex.Message);
                await new Windows.UI.Popups.MessageDialog("Kh\u00f4ng h\u1ee7y k\u1ebft b\u1ea1n \u0111\u01b0\u1ee3c. H\u00e3y th\u1eed l\u1ea1i sau.", "Danh b\u1ea1").ShowAsync();
            }
        }
        private async void BlockFriendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_friend == null || string.IsNullOrWhiteSpace(_friend.UserId) || ZaloApp.Session == null) return;
            string accountId = ZaloApp.Session.Uid;
            var settings = ZaloSettingsService.Current;
            var blockedIds = settings.GetBlockedUserIds(accountId);
            bool blocked = blockedIds.Contains(_friend.UserId);
            if (!blocked)
            {
                var confirm = new Windows.UI.Popups.MessageDialog(
                    "Chặn " + (_friend.Name ?? "người dùng này") + "? Người này sẽ không thể nhắn tin hoặc gọi cho bạn. Bạn có thể bỏ chặn sau.",
                    "Chặn người dùng");
                confirm.Commands.Add(new Windows.UI.Popups.UICommand("Chặn"));
                confirm.Commands.Add(new Windows.UI.Popups.UICommand("Hủy"));
                confirm.DefaultCommandIndex = 1;
                if ((await confirm.ShowAsync()).Label != "Chặn") return;
            }

            BlockFriendButton.IsEnabled = false;
            try
            {
                await ZaloAuth.SetFriendBlockedAsync(ZaloApp.Session, _friend.UserId, !blocked,
                    System.Threading.CancellationToken.None);
                if (blocked) blockedIds.RemoveAll(id => id == _friend.UserId);
                else if (!blockedIds.Contains(_friend.UserId)) blockedIds.Add(_friend.UserId);
                settings.SetBlockedUserIds(accountId, blockedIds);
                UpdateBlockButton();
                ProfileFeedback.Text = blocked ? "Đã bỏ chặn người dùng." : "Đã chặn người dùng.";
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Friend profile block update failed: " + ex.Message);
                ProfileFeedback.Text = blocked ? "Không bỏ chặn được người dùng. Hãy thử lại sau." : "Không chặn được người dùng. Hãy thử lại sau.";
            }
            finally { BlockFriendButton.IsEnabled = true; }
        }
    }
}
