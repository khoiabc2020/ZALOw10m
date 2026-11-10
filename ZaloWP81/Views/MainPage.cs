using System;
using System.Linq;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;
using ZaloLite;

namespace ZaloWP81.Views
{
    public sealed class MainPage : Page
    {
        private Grid _conversationsView;
        private Grid _friendsView;
        private Grid _profileView;

        private TextBlock _tabConversationsHeader;
        private TextBlock _tabFriendsHeader;
        private TextBlock _tabProfileHeader;

        private ListView _conversationList;
        private ListView _friendList;
        private int _currentTab = 0;

        public MainPage()
        {
            BuildUI();
        }

        private void BuildUI()
        {
            var rootGrid = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x11, 0x11, 0x11))
            };
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 1. Windows Phone 8.1 Pivot Header
            var headerStack = new StackPanel { Margin = new Thickness(20, 16, 20, 12) };

            var appTitle = new TextBlock
            {
                Text = "ZALO CHO WINDOWS PHONE",
                FontSize = 11,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x01, 0x67, 0xFE)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            headerStack.Children.Add(appTitle);

            var pivotTabs = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };

            _tabConversationsHeader = CreateTabHeader("tin nhắn", true);
            _tabConversationsHeader.Tapped += (s, e) => SwitchTab(0);
            pivotTabs.Children.Add(_tabConversationsHeader);

            _tabFriendsHeader = CreateTabHeader("danh bạ", false);
            _tabFriendsHeader.Tapped += (s, e) => SwitchTab(1);
            pivotTabs.Children.Add(_tabFriendsHeader);

            _tabProfileHeader = CreateTabHeader("cá nhân", false);
            _tabProfileHeader.Tapped += (s, e) => SwitchTab(2);
            pivotTabs.Children.Add(_tabProfileHeader);

            headerStack.Children.Add(pivotTabs);
            Grid.SetRow(headerStack, 0);
            rootGrid.Children.Add(headerStack);

            // 2. Tab Views Container
            var contentGrid = new Grid();

            _conversationsView = CreateConversationsView();
            _friendsView = CreateFriendsView();
            _profileView = CreateProfileView();

            contentGrid.Children.Add(_conversationsView);
            contentGrid.Children.Add(_friendsView);
            contentGrid.Children.Add(_profileView);

            Grid.SetRow(contentGrid, 1);
            rootGrid.Children.Add(contentGrid);

            this.Content = rootGrid;
            SwitchTab(0);
        }

        private TextBlock CreateTabHeader(string text, bool active)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 32,
                FontWeight = active ? Windows.UI.Text.FontWeights.Normal : Windows.UI.Text.FontWeights.Light,
                Foreground = active ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 0, 24, 0)
            };
        }

        private void SwitchTab(int tabIndex)
        {
            _currentTab = tabIndex;
            _tabConversationsHeader.Foreground = tabIndex == 0 ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
            _tabFriendsHeader.Foreground = tabIndex == 1 ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
            _tabProfileHeader.Foreground = tabIndex == 2 ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

            _conversationsView.Visibility = tabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            _friendsView.Visibility = tabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            _profileView.Visibility = tabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

            if (tabIndex == 0) RefreshConversations();
            else if (tabIndex == 1) RefreshFriends();
            else if (tabIndex == 2) RefreshProfile();
        }

        private Grid CreateConversationsView()
        {
            var grid = new Grid();
            _conversationList = new ListView
            {
                SelectionMode = ListViewSelectionMode.None,
                Padding = new Thickness(16, 0, 16, 16)
            };
            grid.Children.Add(_conversationList);
            return grid;
        }

        private Grid CreateFriendsView()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var searchBox = new TextBox
            {
                PlaceholderText = "Tìm kiếm bạn bè...",
                Margin = new Thickness(16, 0, 16, 12),
                FontSize = 14
            };
            searchBox.TextChanged += (s, e) =>
            {
                FilterFriends(searchBox.Text);
            };
            Grid.SetRow(searchBox, 0);
            grid.Children.Add(searchBox);

            _friendList = new ListView
            {
                SelectionMode = ListViewSelectionMode.None,
                Padding = new Thickness(16, 0, 16, 16)
            };
            Grid.SetRow(_friendList, 1);
            grid.Children.Add(_friendList);

            return grid;
        }

        private Grid CreateProfileView()
        {
            var grid = new Grid { Margin = new Thickness(20, 8, 20, 20) };
            var stack = new StackPanel();

            var avatarEllipse = new Ellipse
            {
                Width = 90,
                Height = 90,
                Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0x01, 0x67, 0xFE)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 16)
            };
            stack.Children.Add(avatarEllipse);

            var nameBlock = new TextBlock
            {
                Text = ZaloApp.Session != null ? (ZaloApp.Session.DisplayName ?? "Người dùng Zalo") : "Chưa đăng nhập",
                FontSize = 22,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };
            stack.Children.Add(nameBlock);

            var phoneBlock = new TextBlock
            {
                Text = ZaloApp.Session != null ? (ZaloApp.Session.PhoneNumber ?? "") : "",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 24)
            };
            stack.Children.Add(phoneBlock);

            var logoutButton = new Button
            {
                Content = "Đăng xuất tài khoản",
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xE5, 0x53, 0x3D)),
                Foreground = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(24, 10, 24, 10),
                FontSize = 15
            };
            logoutButton.Click += async (s, e) =>
            {
                ZaloApp.Reset();
                await ZaloStore.ClearAsync();
                Frame.Navigate(typeof(LoginPage));
            };
            stack.Children.Add(logoutButton);

            grid.Children.Add(stack);
            return grid;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (ZaloApp.Session == null)
            {
                Frame.Navigate(typeof(LoginPage));
                return;
            }

            ZaloApp.SetDispatcher(Dispatcher);
            RefreshConversations();
            ZaloApp.Conversations.CollectionChanged += Conversations_CollectionChanged;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            ZaloApp.Conversations.CollectionChanged -= Conversations_CollectionChanged;
        }

        private async void Conversations_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_currentTab == 0) RefreshConversations();
            });
        }

        private void RefreshConversations()
        {
            _conversationList.Items.Clear();
            foreach (var conv in ZaloApp.Conversations)
            {
                var item = CreateConversationItem(conv);
                _conversationList.Items.Add(item);
            }
        }

        private FrameworkElement CreateConversationItem(ZaloConversation conv)
        {
            var grid = new Grid
            {
                Height = 68,
                Margin = new Thickness(0, 2, 0, 2),
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A))
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Avatar circle
            var avatarGrid = new Grid { Width = 44, Height = 44, Margin = new Thickness(8, 0, 4, 0) };
            var ellipse = new Ellipse
            {
                Width = 44,
                Height = 44,
                Fill = conv.AvatarColor ?? new SolidColorBrush(Color.FromArgb(0xFF, 0x01, 0x67, 0xFE))
            };
            var initial = new TextBlock
            {
                Text = conv.Initial ?? "?",
                FontSize = 18,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            avatarGrid.Children.Add(ellipse);
            avatarGrid.Children.Add(initial);
            Grid.SetColumn(avatarGrid, 0);
            grid.Children.Add(avatarGrid);

            // Title & Preview
            var textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };
            var nameBlock = new TextBlock
            {
                Text = conv.Name ?? "Cuộc trò chuyện",
                FontSize = 16,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var lastTextBlock = new TextBlock
            {
                Text = conv.LastText ?? string.Empty,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            };
            textStack.Children.Add(nameBlock);
            textStack.Children.Add(lastTextBlock);
            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

            // Time & Unread
            var rightStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            var timeBlock = new TextBlock
            {
                Text = conv.TimeText ?? "",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(0x77, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            rightStack.Children.Add(timeBlock);

            if (conv.Unread > 0)
            {
                var unreadBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xE5, 0x53, 0x3D)),
                    CornerRadius = new CornerRadius(9),
                    Height = 18,
                    MinWidth = 18,
                    Padding = new Thickness(4, 0, 4, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                var unreadText = new TextBlock
                {
                    Text = conv.UnreadLabel,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                unreadBorder.Child = unreadText;
                rightStack.Children.Add(unreadBorder);
            }

            Grid.SetColumn(rightStack, 2);
            grid.Children.Add(rightStack);

            grid.Tapped += (s, e) =>
            {
                Frame.Navigate(typeof(ChatPage), conv.Key);
            };

            return grid;
        }

        private void RefreshFriends()
        {
            FilterFriends(string.Empty);
        }

        private void FilterFriends(string query)
        {
            _friendList.Items.Clear();
            var friends = ZaloApp.Friends.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(query))
            {
                string norm = ZaloFormat.NormalizeSearch(query);
                friends = friends.Where(f => ZaloFormat.SearchContains(f.Name, norm));
            }

            foreach (var friend in friends)
            {
                var item = CreateFriendItem(friend);
                _friendList.Items.Add(item);
            }
        }

        private FrameworkElement CreateFriendItem(ZaloFriend friend)
        {
            var grid = new Grid
            {
                Height = 60,
                Margin = new Thickness(0, 2, 0, 2),
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A))
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var avatarGrid = new Grid { Width = 40, Height = 40, Margin = new Thickness(8, 0, 4, 0) };
            var ellipse = new Ellipse
            {
                Width = 40,
                Height = 40,
                Fill = friend.AvatarColor ?? new SolidColorBrush(Color.FromArgb(0xFF, 0x01, 0x67, 0xFE))
            };
            var initial = new TextBlock
            {
                Text = friend.Initial ?? "?",
                FontSize = 16,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            avatarGrid.Children.Add(ellipse);
            avatarGrid.Children.Add(initial);
            Grid.SetColumn(avatarGrid, 0);
            grid.Children.Add(avatarGrid);

            var nameBlock = new TextBlock
            {
                Text = friend.Name ?? "Bạn bè",
                FontSize = 16,
                FontWeight = Windows.UI.Text.FontWeights.Normal,
                Foreground = new SolidColorBrush(Colors.White),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(nameBlock, 1);
            grid.Children.Add(nameBlock);

            grid.Tapped += (s, e) =>
            {
                var conv = ZaloApp.GetOrCreate(friend.UserId, false);
                Frame.Navigate(typeof(ChatPage), conv.Key);
            };

            return grid;
        }

        private void RefreshProfile()
        {
            // Profile view refreshes dynamically
        }
    }
}
