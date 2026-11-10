using System;
using System.Linq;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using ZaloLite;

namespace ZaloWP81.Views
{
    public sealed class ChatPage : Page
    {
        private ZaloConversation _conversation;
        private ListView _messageList;
        private TextBox _inputBox;
        private Button _sendButton;
        private TextBlock _titleBlock;
        private TextBlock _subtitleBlock;

        public ChatPage()
        {
            BuildUI();
        }

        private void BuildUI()
        {
            var rootGrid = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x12, 0x12, 0x12))
            };
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 1. Header
            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x01, 0x67, 0xFE)),
                Height = 60,
                Padding = new Thickness(8, 0, 16, 0)
            };
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var backBtn = new Button
            {
                Content = "←",
                FontSize = 22,
                Foreground = new SolidColorBrush(Colors.White),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Width = 48,
                Height = 48,
                VerticalAlignment = VerticalAlignment.Center
            };
            backBtn.Click += (s, e) =>
            {
                if (Frame.CanGoBack) Frame.GoBack();
                else Frame.Navigate(typeof(MainPage));
            };
            Grid.SetColumn(backBtn, 0);
            headerGrid.Children.Add(backBtn);

            var titleStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            _titleBlock = new TextBlock
            {
                Text = "Trò chuyện",
                FontSize = 18,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White)
            };
            _subtitleBlock = new TextBlock
            {
                Text = "Đang hoạt động",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
            };
            titleStack.Children.Add(_titleBlock);
            titleStack.Children.Add(_subtitleBlock);
            Grid.SetColumn(titleStack, 1);
            headerGrid.Children.Add(titleStack);

            header.Child = headerGrid;
            Grid.SetRow(header, 0);
            rootGrid.Children.Add(header);

            // 2. Message List
            _messageList = new ListView
            {
                Padding = new Thickness(12, 12, 12, 12),
                SelectionMode = ListViewSelectionMode.None
            };
            Grid.SetRow(_messageList, 1);
            rootGrid.Children.Add(_messageList);

            // 3. Input Bar
            var inputBar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E)),
                Padding = new Thickness(10, 8, 10, 8)
            };
            var inputGrid = new Grid();
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _inputBox = new TextBox
            {
                PlaceholderText = "Nhập tin nhắn...",
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 15
            };
            _inputBox.KeyDown += (s, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    SendMessage();
                    e.Handled = true;
                }
            };
            Grid.SetColumn(_inputBox, 0);
            inputGrid.Children.Add(_inputBox);

            _sendButton = new Button
            {
                Content = "Gửi",
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x01, 0x67, 0xFE)),
                Foreground = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(16, 6, 16, 6),
                FontSize = 15
            };
            _sendButton.Click += (s, e) => SendMessage();
            Grid.SetColumn(_sendButton, 1);
            inputGrid.Children.Add(_sendButton);

            inputBar.Child = inputGrid;
            Grid.SetRow(inputBar, 2);
            rootGrid.Children.Add(inputBar);

            this.Content = rootGrid;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            string conversationKey = e.Parameter as string;
            if (string.IsNullOrEmpty(conversationKey) && ZaloApp.Conversations.Count > 0)
            {
                _conversation = ZaloApp.Conversations[0];
            }
            else
            {
                _conversation = ZaloApp.Conversations.FirstOrDefault(c => c.Key == conversationKey);
            }

            if (_conversation != null)
            {
                _titleBlock.Text = _conversation.Name ?? "Trò chuyện";
                _subtitleBlock.Text = _conversation.IsGroup ? "Nhóm trò chuyện" : "Đang hoạt động";
                ZaloApp.CurrentChat = _conversation;

                RenderMessages();
                _conversation.Messages.CollectionChanged += Messages_CollectionChanged;
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            if (_conversation != null)
            {
                _conversation.Messages.CollectionChanged -= Messages_CollectionChanged;
                ZaloApp.CurrentChat = null;
            }
        }

        private async void Messages_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                RenderMessages();
            });
        }

        private void RenderMessages()
        {
            if (_conversation == null) return;
            _messageList.Items.Clear();

            foreach (var m in _conversation.Messages)
            {
                var bubble = CreateMessageBubble(m);
                _messageList.Items.Add(bubble);
            }

            if (_messageList.Items.Count > 0)
            {
                _messageList.ScrollIntoView(_messageList.Items.Last());
            }
        }

        private FrameworkElement CreateMessageBubble(ZaloChatMessage msg)
        {
            bool isMe = msg.FromMe;

            var container = new Grid
            {
                Margin = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = isMe ? HorizontalAlignment.Right : HorizontalAlignment.Left
            };

            var border = new Border
            {
                Background = isMe ? new SolidColorBrush(Color.FromArgb(0xFF, 0x01, 0x67, 0xFE))
                                  : new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 8, 12, 8),
                MaxWidth = 280
            };

            var stack = new StackPanel();

            if (!isMe && !string.IsNullOrEmpty(msg.Sender) && _conversation.IsGroup)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = msg.Sender,
                    FontSize = 11,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x7C, 0xFF)),
                    Margin = new Thickness(0, 0, 0, 2)
                });
            }

            stack.Children.Add(new TextBlock
            {
                Text = msg.Text ?? string.Empty,
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Colors.White)
            });

            stack.Children.Add(new TextBlock
            {
                Text = msg.TimeText ?? string.Empty,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0)
            });

            border.Child = stack;
            container.Children.Add(border);
            return container;
        }

        private void SendMessage()
        {
            string text = _inputBox.Text?.Trim();
            if (string.IsNullOrEmpty(text) || _conversation == null) return;

            _inputBox.Text = string.Empty;
            var task = ZaloApp.SendAsync(_conversation, text);
        }
    }
}
