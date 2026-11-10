using System;
using System.Collections.Specialized;
using System.Linq;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using ZaloLite;

namespace ZaloUWP.Views
{
    public sealed partial class StrangerMessagesPage : Page
    {
        public StrangerMessagesPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ZaloApp.Conversations.CollectionChanged += OnConversationsChanged;
            ZaloApp.FriendsLoadChanged += OnFriendsLoadChanged;
            RefreshList();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ZaloApp.Conversations.CollectionChanged -= OnConversationsChanged;
            ZaloApp.FriendsLoadChanged -= OnFriendsLoadChanged;
            base.OnNavigatedFrom(e);
        }

        private void OnConversationsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            RefreshList();
        }

        private async void OnFriendsLoadChanged()
        {
            if (Dispatcher == null) return;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, RefreshList);
        }

        private void RefreshList()
        {
            if (StrangerList == null) return;
            var otherKeys = new System.Collections.Generic.HashSet<string>(
                ZaloUWP.Services.ZaloSettingsService.Current.GetOtherConversationKeys(ZaloApp.Session?.Uid),
                StringComparer.Ordinal);
            var strangers = ZaloApp.FriendsLoaded
                ? ZaloApp.Conversations
                    .Where(c => c != null && !c.IsGroup && !ZaloApp.IsArchived(c)
                        && !otherKeys.Contains(c.Key) && !ZaloApp.IsFriend(c.ThreadId))
                    .OrderByDescending(c => c.LastTs)
                    .ToList()
                : new System.Collections.Generic.List<ZaloConversation>();

            StrangerList.ItemsSource = strangers;
            EmptyStrangerPanel.Visibility = strangers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void StrangerList_ItemClick(object sender, ItemClickEventArgs e)
        {
            ZaloConversation conversation = e.ClickedItem as ZaloConversation;
            if (conversation != null) Frame.Navigate(typeof(ChatPage), conversation.Key);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }
    }
}
