using System;
using System.Collections.Generic;
using Windows.Data.Json;
using Windows.ApplicationModel;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using ZaloLite;
using ZaloUWP.Services;

namespace ZaloUWP.Views
{
    public sealed partial class SettingsPage : Page
    {
        private readonly ZaloSettingsService _settings = ZaloSettingsService.Current;
        private bool _loadingPrivacySettings;
        private bool _updatingPrivacySetting;
        private bool _openLocalDataOnLoad;
        private readonly Dictionary<ToggleSwitch, bool> _privacyToggleValues = new Dictionary<ToggleSwitch, bool>();
        private readonly Dictionary<ComboBox, int> _privacyComboIndices = new Dictionary<ComboBox, int>();

        public SettingsPage()
        {
            try
            {
                this.InitializeComponent();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("SettingsPage.InitializeComponent primary attempt failed: " + ex.Message);
                try
                {
                    var fallbackUri = new Uri("ms-appx:///Views/SettingsPage.xaml");
                    Application.LoadComponent(this, fallbackUri, Windows.UI.Xaml.Controls.Primitives.ComponentResourceLocation.Application);
                }
                catch (Exception ex2)
                {
                    ZaloLog.Add("SettingsPage fallback LoadComponent failed: " + ex2.Message);
                }
            }

            if (SettingsScrollViewer != null)
            {
                SettingsScrollViewer.Loaded += SettingsScrollViewer_Loaded;
            }

            if (AboutVersionText != null)
            {
                try
                {
                    var version = Package.Current.Id.Version;
                    AboutVersionText.Text = string.Format("Phiên bản {0}.{1}.{2}.{3}",
                        version.Major, version.Minor, version.Build, version.Revision);
                }
                catch { }
            }

            PopulateWallpapers();
            LoadSettings();
            _ = LoadPrivacySettingsAsync();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _openLocalDataOnLoad = string.Equals(e.Parameter as string, "localData", StringComparison.Ordinal);
            ScrollToLocalDataIfRequested();
        }

        private void SettingsScrollViewer_Loaded(object sender, RoutedEventArgs e)
        {
            ScrollToLocalDataIfRequested();
        }

        private void ScrollToLocalDataIfRequested()
        {
            if (!_openLocalDataOnLoad || !SettingsScrollViewer.IsLoaded) return;
            try
            {
                SettingsScrollViewer.ChangeView(0, SettingsScrollViewer.ScrollableHeight, null, true);
            }
            catch (Exception ex)
            {
                // Keep Settings usable on older Mobile builds if the optional
                // ScrollViewer navigation API is unavailable there.
                ZaloLog.Add("Settings local-data auto-scroll failed: " + ex.Message);
            }
        }

        private void PopulateWallpapers()
        {
            if (WallpaperGridView == null) return;
            var list = new List<string>();
            for (int i = 1; i <= 20; i++)
            {
                var num = i.ToString("D2");
                list.Add($"ms-appx:///Assets/Backgrounds/bg_{num}.png");
            }
            WallpaperGridView.ItemsSource = list;
        }

        private void LoadSettings()
        {
            var session = ZaloApp.Session;
            if (UserNameTextBlock != null)
            {
                UserNameTextBlock.Text = !string.IsNullOrEmpty(session?.DisplayName)
                    ? session.DisplayName
                    : "Người dùng Zalo";
            }
            if (UserPhoneTextBlock != null)
            {
                UserPhoneTextBlock.Text = !string.IsNullOrEmpty(session?.PhoneNumber)
                    ? session.PhoneNumber
                    : "Chưa công khai số điện thoại";
            }
            if (UserIdTextBlock != null)
            {
                UserIdTextBlock.Text = $"UID: {session?.Uid ?? "N/A"}";
            }

            var avatarUri = !string.IsNullOrEmpty(session?.AvatarUrl)
                ? session.AvatarUrl
                : "ms-appx:///Assets/Square150x150Logo.png";
            if (UserAvatarBrush != null)
            {
                try
                {
                    UserAvatarBrush.ImageSource = new BitmapImage(new Uri(avatarUri));
                }
                catch { }
            }

            if (WallpaperGridView != null)
                WallpaperGridView.SelectedItem = _settings.SelectedWallpaper;
            if (SoundToggle != null)
                SoundToggle.IsOn = _settings.IsSoundEnabled;
            if (EnterSendToggle != null)
                EnterSendToggle.IsOn = _settings.IsEnterToSend;
            if (ThemeComboBox != null)
                ThemeComboBox.SelectedIndex = 0;
            ApplyTheme(ElementTheme.Light);

            UpdateFontSizePills(_settings.MessageFontSize);
        }

        private void UpdateFontSizePills(double selectedSize)
        {
            var activeBg = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["ZaloBlueBrush"];
            var inactiveBg = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9));
            var activeFg = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White);
            var inactiveFg = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x29, 0x3B));

            SetPillStyle(FontSmallBtn, Math.Abs(selectedSize - 13.5) < 0.5, activeBg, inactiveBg, activeFg, inactiveFg);
            SetPillStyle(FontMediumBtn, Math.Abs(selectedSize - 15.5) < 0.5, activeBg, inactiveBg, activeFg, inactiveFg);
            SetPillStyle(FontLargeBtn, Math.Abs(selectedSize - 17.5) < 0.5, activeBg, inactiveBg, activeFg, inactiveFg);
            SetPillStyle(FontXLargeBtn, Math.Abs(selectedSize - 19.5) < 0.5, activeBg, inactiveBg, activeFg, inactiveFg);

            if (PreviewMessageText != null)
                PreviewMessageText.FontSize = selectedSize;
        }

        private void SetPillStyle(Button btn, bool isSelected, Windows.UI.Xaml.Media.Brush activeBg, Windows.UI.Xaml.Media.Brush inactiveBg, Windows.UI.Xaml.Media.Brush activeFg, Windows.UI.Xaml.Media.Brush inactiveFg)
        {
            if (btn == null) return;
            btn.Background = isSelected ? activeBg : inactiveBg;
            btn.Foreground = isSelected ? activeFg : inactiveFg;
            btn.FontWeight = isSelected ? Windows.UI.Text.FontWeights.SemiBold : Windows.UI.Text.FontWeights.Normal;
        }

        private void FontSizePill_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && double.TryParse(tagStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double size))
            {
                _settings.MessageFontSize = size;
                UpdateFontSizePills(size);
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private void WallpaperGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (WallpaperGridView.SelectedItem is string selectedBg)
            {
                _settings.SelectedWallpaper = selectedBg;
            }
        }

        private void SoundToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _settings.IsSoundEnabled = SoundToggle.IsOn;
        }

        private void EnterSendToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _settings.IsEnterToSend = EnterSendToggle.IsOn;
        }

        private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeComboBox.SelectedIndex >= 0)
            {
                ApplyTheme((ElementTheme)ThemeComboBox.SelectedIndex);
            }
        }

        private void ApplyTheme(ElementTheme theme)
        {
            // All current pages use light surfaces. Keep them consistent until dark resources exist.
            _settings.AppTheme = ElementTheme.Light;
            if (Window.Current.Content is FrameworkElement root)
                root.RequestedTheme = ElementTheme.Light;
        }

        private async System.Threading.Tasks.Task LoadPrivacySettingsAsync()
        {
            _loadingPrivacySettings = true;
            PrivacyStatusText.Text = "Đang tải cài đặt tài khoản…";
            try
            {
                JsonObject settings = await ZaloAuth.GetAccountSettingsAsync(
                    ZaloApp.Session, System.Threading.CancellationToken.None);
                BindToggleSetting(OnlineStatusToggle, settings, "show_online_status");
                BindToggleSetting(SeenStatusToggle, settings, "display_seen_status");
                BindToggleSetting(AddByPhoneToggle, settings, "add_friend_via_phone");
                BindToggleSetting(AddByQrToggle, settings, "add_friend_via_qr");
                BindToggleSetting(AddByGroupToggle, settings, "add_friend_via_group");
                BindToggleSetting(AddByContactToggle, settings, "add_friend_via_contact");
                BindToggleSetting(RecommendProfileToggle, settings, "display_on_recommend_friend");
                BindComboSetting(BirthdayVisibilityCombo, settings, "view_birthday", value => value >= 0 && value <= 2 ? value : -1);
                BindComboSetting(ReceiveMessagesCombo, settings, "receive_message", value => value == 1 ? 0 : value == 2 ? 1 : -1);
                BindComboSetting(AcceptCallsCombo, settings, "accept_stranger_call", value => value == 2 ? 0 : value == 4 ? 1 : value == 3 ? 2 : -1);
                PrivacyStatusText.Text = "Các thay đổi được lưu trực tiếp vào tài khoản Zalo.";
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Privacy settings load failed: " + ex.Message);
                PrivacyStatusText.Text = "Không tải được cài đặt tài khoản. Các lựa chọn đã bị khóa để tránh ghi đè giá trị chưa biết.";
            }
            finally
            {
                _loadingPrivacySettings = false;
            }
        }

        private static bool TryGetSetting(JsonObject settings, string key, out int value)
        {
            value = 0;
            IJsonValue item;
            if (settings == null || !settings.TryGetValue(key, out item)) return false;
            if (item.ValueType == JsonValueType.Number) value = (int)item.GetNumber();
            else if (item.ValueType == JsonValueType.Boolean) value = item.GetBoolean() ? 1 : 0;
            else if (item.ValueType == JsonValueType.String && !int.TryParse(item.GetString(), out value)) return false;
            else if (item.ValueType != JsonValueType.String) return false;
            return true;
        }

        private void BindToggleSetting(ToggleSwitch toggle, JsonObject settings, string key)
        {
            int value;
            bool available = TryGetSetting(settings, key, out value);
            toggle.IsOn = available && value != 0;
            toggle.IsEnabled = available;
            _privacyToggleValues[toggle] = toggle.IsOn;
        }

        private void BindComboSetting(ComboBox combo, JsonObject settings, string key, Func<int, int> toIndex)
        {
            int value;
            bool available = TryGetSetting(settings, key, out value);
            int index = available ? toIndex(value) : -1;
            combo.SelectedIndex = index;
            combo.IsEnabled = index >= 0;
            _privacyComboIndices[combo] = index;
        }

        private async void PrivacyToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingPrivacySettings || _updatingPrivacySetting) return;
            var toggle = sender as ToggleSwitch;
            if (toggle == null || toggle.Tag == null) return;
            bool previous = _privacyToggleValues.ContainsKey(toggle) && _privacyToggleValues[toggle];
            bool requested = toggle.IsOn;
            await SavePrivacySettingAsync(toggle, toggle.Tag.ToString(), requested ? 1 : 0,
                () => toggle.IsOn = previous,
                () => _privacyToggleValues[toggle] = requested);
        }

        private async void PrivacyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingPrivacySettings || _updatingPrivacySetting) return;
            var combo = sender as ComboBox;
            if (combo == null || combo.Tag == null || combo.SelectedIndex < 0) return;
            string key = combo.Tag.ToString();
            int value = combo.SelectedIndex;
            if (key == "receive_message") value = combo.SelectedIndex == 0 ? 1 : 2;
            else if (key == "accept_stranger_call") value = combo.SelectedIndex == 0 ? 2 : combo.SelectedIndex == 1 ? 4 : 3;
            int requestedIndex = combo.SelectedIndex;
            int previousIndex = _privacyComboIndices.ContainsKey(combo) ? _privacyComboIndices[combo] : -1;
            await SavePrivacySettingAsync(combo, key, value,
                () => combo.SelectedIndex = previousIndex,
                () => _privacyComboIndices[combo] = requestedIndex);
        }

        private async System.Threading.Tasks.Task SavePrivacySettingAsync(DependencyObject control, string key, int value,
            Action rollback, Action commit)
        {
            if (_updatingPrivacySetting) return;
            _updatingPrivacySetting = true;
            var ui = control as Control;
            bool wasEnabled = ui == null || ui.IsEnabled;
            if (ui != null) ui.IsEnabled = false;
            PrivacyStatusText.Text = "Đang lưu cài đặt…";
            try
            {
                await ZaloAuth.SetAccountSettingAsync(ZaloApp.Session, key, value,
                    System.Threading.CancellationToken.None);
                commit();
                PrivacyStatusText.Text = "Đã lưu cài đặt.";
            }
            catch (Exception ex)
            {
                _loadingPrivacySettings = true;
                rollback();
                _loadingPrivacySettings = false;
                PrivacyStatusText.Text = "Không lưu được cài đặt. Đã khôi phục lựa chọn trước đó.";
                ZaloLog.Add("Privacy setting update failed: " + ex.Message);
            }
            finally
            {
                if (ui != null) ui.IsEnabled = wasEnabled;
                _updatingPrivacySetting = false;
            }
        }

        private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            var confirm = new MessageDialog("Xóa danh sách hội thoại và tin nhắn đã lưu trên thiết bị?", "Xóa bộ nhớ đệm");
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Xóa", command => { }));
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Hủy"));
            confirm.DefaultCommandIndex = 1;
            confirm.CancelCommandIndex = 1;
            var result = await confirm.ShowAsync();
            if (result.Label != "Xóa") return;

            await ZaloApp.ClearLocalCacheAsync();
            await new MessageDialog("Đã xóa dữ liệu hội thoại lưu trên thiết bị.", "Hoàn tất").ShowAsync();
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            ZaloApp.Reset();
            await ZaloStore.ClearAsync();
            Frame.Navigate(typeof(LoginPage));
        }
    }
}

