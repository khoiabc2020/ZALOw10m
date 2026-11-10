using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Media;
using ZaloLite;
using System.Threading;

namespace ZaloUWP.Views
{
    public sealed partial class AddFriendDialog : ContentDialog
    {
        public ZaloFriend FoundFriend { get; private set; }

        public AddFriendDialog()
        {
            this.InitializeComponent();
            IsPrimaryButtonEnabled = false;
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            var phone = PhoneTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(phone) || phone.Length < 9)
            {
                SetStatus("Vui lòng nhập số điện thoại hợp lệ (tối thiểu 9 số).", 209, 67, 67);
                ResultCard.Visibility = Visibility.Collapsed;
                IsPrimaryButtonEnabled = false;
                return;
            }

            SearchButton.IsEnabled = false;
            SetStatus("Đang tìm tài khoản...", 22, 119, 210);
            ResultCard.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = false;
            try
            {
                FoundFriend = await ZaloAuth.FindFriendByPhoneAsync(ZaloApp.Session, phone, CancellationToken.None);
                FoundNameTextBlock.Text = FoundFriend.Name;
                FoundPhoneTextBlock.Text = phone;
                var avatar = string.IsNullOrEmpty(FoundFriend.Avatar)
                    ? "ms-appx:///Assets/Square150x150Logo.png"
                    : FoundFriend.Avatar;
                FoundAvatarBrush.ImageSource = new BitmapImage(new Uri(avatar));

                ResultCard.Visibility = Visibility.Visible;
                SetStatus("Đã tìm thấy tài khoản Zalo.", 23, 133, 89);
                IsPrimaryButtonEnabled = true;
            }
            catch (Exception ex)
            {
                FoundFriend = null;
                SetStatus(ex.Message, 209, 67, 67);
                IsPrimaryButtonEnabled = false;
            }
            finally
            {
                SearchButton.IsEnabled = true;
            }
        }

        private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            IsPrimaryButtonEnabled = false;
            try
            {
                await ZaloAuth.SendFriendRequestAsync(ZaloApp.Session, FoundFriend.UserId,
                    "Xin chào, mình muốn kết bạn với bạn trên Zalo.", CancellationToken.None);
                SetStatus("Đã gửi lời mời kết bạn.", 23, 133, 89);
                PrimaryButtonText = "Đã gửi";
            }
            catch (Exception ex)
            {
                SetStatus("Không gửi được lời mời: " + ex.Message, 209, 67, 67);
                IsPrimaryButtonEnabled = true;
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
        }

        private void SetStatus(string message, byte red, byte green, byte blue)
        {
            StatusTextBlock.Text = message;
            StatusTextBlock.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, red, green, blue));
        }
    }
}

