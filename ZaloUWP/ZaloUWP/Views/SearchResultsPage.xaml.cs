using System;
using System.Collections.Generic;
using System.Linq;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using ZaloLite;

namespace ZaloUWP.Views
{
    public sealed partial class SearchResultsPage : Page
    {
        private string _conversationKey;
        private string _conversationName;

        public SearchResultsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            SearchPageRequest request = e.Parameter as SearchPageRequest;
            _conversationKey = request == null ? null : request.ConversationKey;
            _conversationName = string.IsNullOrWhiteSpace(_conversationKey) ? string.Empty
                : ZaloApp.Find(_conversationKey)?.Name ?? string.Empty;
            QueryBox.PlaceholderText = string.IsNullOrWhiteSpace(_conversationName)
                ? "Tìm tin nhắn" : "Tìm trong " + _conversationName;
            QueryBox.Text = request == null ? e.Parameter as string ?? string.Empty : request.Query ?? string.Empty;
            QueryBox.Focus(FocusState.Programmatic);
            SearchMessages(QueryBox.Text);
        }

        private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchMessages(QueryBox.Text);
        }

        private void QueryBox_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                SearchMessages(QueryBox.Text);
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            SearchMessages(QueryBox.Text);
        }

        private void SearchMessages(string query)
        {
            string text = (query ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                ResultsList.ItemsSource = null;
                SearchStatusText.Text = "Nhập từ khóa để tìm trong tin nhắn đã tải xuống.";
                EmptyStatePanel.Visibility = Visibility.Visible;
                EmptyStateTitle.Text = "Tìm tin nhắn";
                EmptyStateDescription.Text = "Tìm trong các cuộc trò chuyện có lịch sử trên thiết bị này.";
                return;
            }

            string needle = Normalize(text);
            var hits = new List<SearchMessageHit>();
            IEnumerable<ZaloConversation> conversations = ZaloApp.Conversations;
            if (!string.IsNullOrWhiteSpace(_conversationKey))
                conversations = conversations.Where(conversation => conversation.Key == _conversationKey);
            foreach (ZaloConversation conversation in conversations)
            {
                foreach (ZaloChatMessage message in conversation.Messages)
                {
                    if (message == null || message.IsRecallEvent || message.IsRecalled) continue;
                    string body = message.Text ?? string.Empty;
                    if (!Normalize(body).Contains(needle)) continue;
                    hits.Add(new SearchMessageHit(conversation, message));
                }
            }

            var ordered = hits.OrderByDescending(hit => hit.Timestamp).Take(200).ToList();
            ResultsList.ItemsSource = ordered;
            EmptyStatePanel.Visibility = ordered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateTitle.Text = "Không tìm thấy tin nhắn";
            EmptyStateDescription.Text = "Kết quả chỉ gồm tin nhắn đã có trong lịch sử tải xuống. Mở thêm cuộc trò chuyện rồi thử lại để tìm trong tin cũ hơn.";
            string scopeText = string.IsNullOrWhiteSpace(_conversationName)
                ? "lịch sử hiện có trên thiết bị" : "lịch sử đã tải của " + _conversationName;
            SearchStatusText.Text = ordered.Count == 0
                ? "Tìm trong " + scopeText + "."
                : ordered.Count == 200
                    ? "200 kết quả gần nhất · tìm trong " + scopeText + "."
                    : ordered.Count + " kết quả · tìm trong " + scopeText + ".";
        }

        private static string Normalize(string value)
        {
            return ZaloFormat.NormalizeSearch(value);
        }

        private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            SearchMessageHit hit = e.ClickedItem as SearchMessageHit;
            if (hit == null || hit.Conversation == null || hit.Message == null) return;
            Frame.Navigate(typeof(ChatPage), Tuple.Create(hit.Conversation.Key, hit.Message.Id));
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }
    }

    public sealed class SearchPageRequest
    {
        public string ConversationKey { get; set; }
        public string Query { get; set; }
    }

    public sealed class SearchMessageHit
    {
        public ZaloConversation Conversation { get; private set; }
        public ZaloChatMessage Message { get; private set; }
        public long Timestamp { get { return Message == null ? 0 : Message.Ts; } }
        public string ConversationName { get { return Conversation == null ? "Trò chuyện" : Conversation.Name; } }
        public string Preview
        {
            get
            {
                if (Message == null) return string.Empty;
                string body = Message.Text ?? string.Empty;
                if (Conversation != null && Conversation.IsGroup && !string.IsNullOrWhiteSpace(Message.Sender))
                    body = Message.Sender + ": " + body;
                return body;
            }
        }
        public string TimeText { get { return Message == null ? string.Empty : ZaloFormat.Time(Message.Ts); } }

        public SearchMessageHit(ZaloConversation conversation, ZaloChatMessage message)
        {
            Conversation = conversation;
            Message = message;
        }
    }
}
