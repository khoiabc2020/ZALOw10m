using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI.Xaml.Controls;
using System.Threading;
using ZaloLite;
using ZaloUWP.Models;

namespace ZaloUWP.Views
{
    public sealed partial class CreateGroupDialog : ContentDialog
    {
        public string CreatedGroupId { get; private set; }
        public string CreatedGroupName { get; private set; }

        public CreateGroupDialog(IEnumerable<FriendItem> friends)
        {
            this.InitializeComponent();
            FriendsListView.ItemsSource = friends;
        }

        private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var name = GroupNameTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                ErrorTextBlock.Text = "Vui lòng nhập tên nhóm!";
                args.Cancel = true;
                return;
            }

            var selected = FriendsListView.SelectedItems.Cast<FriendItem>().ToList();
            if (selected.Count == 0)
            {
                ErrorTextBlock.Text = "Vui lòng chọn ít nhất 1 thành viên!";
                args.Cancel = true;
                return;
            }

            args.Cancel = true;
            var deferral = args.GetDeferral();
            IsPrimaryButtonEnabled = false;
            ErrorTextBlock.Text = "Đang tạo nhóm trên Zalo...";
            try
            {
                CreatedGroupId = await ZaloAuth.CreateGroupAsync(ZaloApp.Session, name,
                    selected.Select(f => f.UserId).ToList(), CancellationToken.None);
                CreatedGroupName = name;
                ErrorTextBlock.Text = "Đã tạo nhóm.";
                args.Cancel = false;
            }
            catch (Exception ex)
            {
                ErrorTextBlock.Text = "Không tạo được nhóm: " + ex.Message;
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
    }
}

