using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using ZaloLite;
using ZaloUWP.Models;

namespace ZaloUWP.Views
{
    public sealed partial class CallPage : Page
    {
        public CallPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is ZaloFriend friend)
            {
                CalleeNameTextBlock.Text = friend.Name ?? "Bạn bè Zalo";
                if (!string.IsNullOrEmpty(friend.Avatar))
                {
                    try { CalleeAvatarBrush.ImageSource = ZaloAvatar.BitmapFor(friend.Avatar); } catch { }
                }
            }
            else if (e.Parameter is ZaloConversation convLite)
            {
                CalleeNameTextBlock.Text = convLite.Name ?? "Trò chuyện";
                if (!string.IsNullOrEmpty(convLite.AvatarUrl))
                {
                    try { CalleeAvatarBrush.ImageSource = ZaloAvatar.BitmapFor(convLite.AvatarUrl); } catch { }
                }
            }
            else if (e.Parameter is ConversationItem conv)
            {
                CalleeNameTextBlock.Text = conv.Title ?? "Người dùng Zalo";
                var avatarUrl = !string.IsNullOrEmpty(conv.DisplayAvatar) ? conv.DisplayAvatar : "ms-appx:///Assets/Square150x150Logo.png";
                try
                {
                    CalleeAvatarBrush.ImageSource = ZaloAvatar.BitmapFor(avatarUrl);
                }
                catch { }
            }

        }

        private void EndCallButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }
}
