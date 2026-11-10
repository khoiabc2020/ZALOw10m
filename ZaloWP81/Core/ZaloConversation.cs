using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Networking.Sockets;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Web.Http;
using ZaloUWP.Services;

namespace ZaloLite
{
    public sealed class ZaloFriend
    {
        private Brush _color;
        private Brush _image;
        private bool _imageMade;

        // These must be properties (not fields) so the lists on screen can show them.
        public string UserId { get; set; }
        public string PhoneNumber { get; set; }
        public string Name { get; set; }
        public string OriginalName { get; set; }
        public string Alias { get; set; }
        public string Avatar { get; set; }
        public string Subtitle { get; set; }
        public bool IsOnline { get; set; }
        public Visibility OnlineDotVisibility { get { return IsOnline ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility SubtitleVisibility { get { return string.IsNullOrWhiteSpace(Subtitle) ? Visibility.Collapsed : Visibility.Visible; } }
        public string SectionHeader { get; set; }
        public bool ShowSectionHeader { get; set; }
        public Visibility SectionHeaderVisibility { get { return ShowSectionHeader ? Visibility.Visible : Visibility.Collapsed; } }

        public string Initial { get { return ZaloAvatar.InitialOf(Name); } }

        public Brush AvatarColor
        {
            get
            {
                if (_color == null) _color = ZaloAvatar.ColorFor(Name);
                return _color;
            }
        }

        public Brush AvatarImageBrush
        {
            get
            {
                if (!_imageMade)
                {
                    _image = ZaloAvatar.ImageFor(Avatar);
                    _imageMade = true;
                }
                return _image;
            }
        }
    }


    public sealed class ZaloFriendRequest
    {
        public string UserId { get; set; }
        public string PhoneNumber { get; set; }
        public string Name { get; set; }
        public string Avatar { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
    }

    public abstract class ZaloObservable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }


    public sealed class ZaloGroupBoardEntry
    {
        public string Icon { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string TimeText { get; set; }
        public long PollId { get; set; }
        public string TopicId { get; set; }
        public Visibility ContentActionVisibility
        {
            get { return string.IsNullOrWhiteSpace(Title) || Title.Length <= 180 ? Visibility.Collapsed : Visibility.Visible; }
        }
        public Visibility PollActionVisibility { get { return PollId > 0 ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility NoteActionVisibility { get { return !string.IsNullOrWhiteSpace(TopicId) ? Visibility.Visible : Visibility.Collapsed; } }
    }

    public sealed class ZaloGroupReminder
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string TimeText { get; set; }
        public long StartTime { get; set; }
        public int RepeatMode { get; set; }
        public string RepeatText { get; set; }
        public string ResponseText { get; set; }
        public bool CanManage { get; set; }
        public Visibility ManageVisibility { get { return CanManage ? Visibility.Visible : Visibility.Collapsed; } }
    }

    public sealed class ZaloGroupLinkInfo
    {
        public bool Enabled { get; set; }
        public string Link { get; set; }
        public long Expiration { get; set; }
    }

    public sealed class ZaloPollDetail
    {
        public long PollId { get; set; }
        public string Question { get; set; }
        public string Creator { get; set; }
        public bool Joined { get; set; }
        public bool Closed { get; set; }
        public bool AllowMultiple { get; set; }
        public int VoteCount { get; set; }
        public List<ZaloPollOption> Options { get; private set; } = new List<ZaloPollOption>();
    }

    public sealed class ZaloPollOption
    {
        public long OptionId { get; set; }
        public string Content { get; set; }
        public int Votes { get; set; }
        public bool Voted { get; set; }
    }

    public sealed class ZaloChatMessage : ZaloObservable
    {
        private static readonly Brush IncomingBubbleBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        private static readonly Brush OutgoingBubbleBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xD8, 0xF1, 0xFF));
        private static readonly Brush ReceiptSeenBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x16, 0x88, 0xF8));
        private static readonly Brush ReceiptPendingBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x82, 0x90, 0x9E));
        private static readonly Brush MediaBubbleBrush = new SolidColorBrush(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
        private static readonly Brush MessageBorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xD8, 0xE0, 0xE8));
        private string _text;
        private string _reactionIcon;
        private string _mediaUrl;
        private string _mediaFullUrl;
        private Windows.UI.Xaml.Media.Imaging.BitmapImage _mediaImage;
        private bool _mediaLoadFailed;
        private bool _playbackFailed;
        private bool _playbackFailedVideo;
        private bool _isRecalled;
        private Brush _senderAvatarBrush;
        private Brush _senderAvatarImageBrush;
        private string _senderAvatarUrl;
        private int _receiptState;
        private string _deliveryAvatarUrl;
        private Brush _deliveryAvatarBrush;
        public string Id { get; set; }
        public string GlobalMessageId { get; set; }
        public string ClientMessageId { get; set; }
        public string SenderUid { get; set; }
        public string RecipientUid { get; set; }
        public string Sender { get; set; }
        public int Status { get; set; }
        public int At { get; set; }
        public int Command { get; set; }
        public bool SeenSent { get; set; }
        public int ReceiptState
        {
            get { return _receiptState; }
            set
            {
                int normalized = Math.Max(0, Math.Min(2, value));
                if (_receiptState == normalized) return;
                _receiptState = normalized;
                Raise("ReceiptState");
                Raise("DeliveryText");
                Raise("DeliveryForeground");
                Raise("DeliveryAvatarVisibility");
                Raise("DeliveryVisibility");
            }
        }
        public string DeliveryAvatarUrl
        {
            get { return _deliveryAvatarUrl ?? string.Empty; }
            set
            {
                if (_deliveryAvatarUrl == value) return;
                _deliveryAvatarUrl = value;
                _deliveryAvatarBrush = null;
                Raise("DeliveryAvatarUrl");
                Raise("DeliveryAvatarBrush");
                Raise("DeliveryAvatarVisibility");
            }
        }
        public Brush DeliveryAvatarBrush
        {
            get
            {
                if (_deliveryAvatarBrush == null && !string.IsNullOrWhiteSpace(DeliveryAvatarUrl))
                    _deliveryAvatarBrush = ZaloAvatar.ImageFor(DeliveryAvatarUrl);
                return _deliveryAvatarBrush;
            }
        }
        public string ReplyContent { get; set; }
        public string QuoteMessage { get; set; }
        public string QuoteAttachment { get; set; }
        public bool ReplyBlocked { get; set; }
        public string QuotedSender { get; set; }
        public string QuotedText { get; set; }
        private string _linkPreviewTitle;
        private string _linkPreviewDescription;
        private string _linkPreviewUrl;
        private string _linkPreviewImageUrl;
        private Windows.UI.Xaml.Media.Imaging.BitmapImage _linkPreviewImage;
        private double? _locationLatitude;
        private double? _locationLongitude;
        private string _locationLabel;
        private long _pollId;
        private string _pollQuestion;
        private string _contactUid;
        private string _contactName;
        private string _contactAvatarUrl;
        private string _contactQrUrl;
        private string _contactPhone;
        private Windows.UI.Xaml.Media.Imaging.BitmapImage _contactAvatarImage;
        private Brush _contactAvatarBrush;
        private string _phonePreviewNumber;
        private string _phonePreviewUid;
        private string _phonePreviewName;
        private string _phonePreviewAvatarUrl;
        private Windows.UI.Xaml.Media.Imaging.BitmapImage _phonePreviewAvatar;
        private bool _phonePreviewFriendRequestSent;
        private bool _phonePreviewAlreadyFriend;
        private static readonly Regex VietnamesePhoneRegex = new Regex(@"(?<!\d)(?:\+?84|0)(?:[ .-]*\d){8,10}(?!\d)");
        public string LinkPreviewTitle { get { return _linkPreviewTitle ?? string.Empty; } }
        public string LinkPreviewDescription { get { return _linkPreviewDescription ?? string.Empty; } }
        public string LinkPreviewUrl { get { return _linkPreviewUrl ?? string.Empty; } }
        public string LinkPreviewImageUrl { get { return _linkPreviewImageUrl ?? string.Empty; } }
        public Uri LinkPreviewUri
        {
            get
            {
                Uri uri;
                return Uri.TryCreate(LinkPreviewUrl, UriKind.Absolute, out uri)
                    && (uri.Scheme == "http" || uri.Scheme == "https") ? uri : null;
            }
        }
        public Windows.UI.Xaml.Media.Imaging.BitmapImage LinkPreviewImage
        {
            get
            {
                if (_linkPreviewImage == null && !string.IsNullOrEmpty(_linkPreviewImageUrl))
                {
                    try
                    {
                        Uri imageUri;
                        if (Uri.TryCreate(_linkPreviewImageUrl, UriKind.Absolute, out imageUri)
                            && (imageUri.Scheme == "http" || imageUri.Scheme == "https"))
                        {
                            _linkPreviewImage = new Windows.UI.Xaml.Media.Imaging.BitmapImage(imageUri);
                        }
                    }
                    catch { }
                }
                return _linkPreviewImage;
            }
        }
        public bool HasLinkPreview { get { return LinkPreviewTitle.Length > 0 || LinkPreviewUrl.Length > 0; } }
        public bool HasPhotoAttachment { get { return MsgType == "chat.photo" || MsgType == "chat.gif"
            || MsgType == "chat.doodle" || MsgType == "chat.sticker" || MsgType == "5"; } }
        private bool IsDuplicatePhotoLink
        {
            get
            {
                if (!HasPhotoAttachment && !HasMedia) return false;
                if (string.Equals(LinkPreviewUrl, MediaUrl, StringComparison.OrdinalIgnoreCase)) return true;
                Uri uri;
                if (!Uri.TryCreate(LinkPreviewUrl, UriKind.Absolute, out uri)) return false;
                string host = uri.Host ?? string.Empty;
                return host.EndsWith(".zdn.vn", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".zaloapp.com", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".dlfl.vn", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".zadn.vn", StringComparison.OrdinalIgnoreCase);
            }
        }
        public Visibility LinkPreviewVisibility { get { return HasLinkPreview && !HasVoice && !HasVideo && !HasFile && !IsDuplicatePhotoLink && !IsSticker && !HasMedia && !HasPhotoAttachment
            ? Visibility.Visible : Visibility.Collapsed; } }
        public bool HasLocation { get { return _locationLatitude.HasValue && _locationLongitude.HasValue; } }
        public double? LocationLatitude { get { return _locationLatitude; } }
        public double? LocationLongitude { get { return _locationLongitude; } }
        public string LocationLabel { get { return string.IsNullOrWhiteSpace(_locationLabel) ? "Vị trí được chia sẻ" : _locationLabel; } }
        public string LocationCoordinates
        {
            get { return !HasLocation ? string.Empty : _locationLatitude.Value.ToString("0.######", CultureInfo.InvariantCulture)
                + ", " + _locationLongitude.Value.ToString("0.######", CultureInfo.InvariantCulture); }
        }
        public Uri LocationUri
        {
            get
            {
                if (!HasLocation) return null;
                return new Uri("https://www.google.com/maps/search/?api=1&query="
                    + Uri.EscapeDataString(LocationCoordinates.Replace(" ", string.Empty)));
            }
        }
        public Visibility LocationVisibility { get { return HasLocation ? Visibility.Visible : Visibility.Collapsed; } }
        public long PollId { get { return _pollId; } }
        public string PollQuestion { get { return string.IsNullOrWhiteSpace(_pollQuestion) ? "Bình chọn trong nhóm" : _pollQuestion; } }
        public Visibility PollVisibility { get { return PollId > 0 ? Visibility.Visible : Visibility.Collapsed; } }
        public string ContactUid { get { return _contactUid ?? string.Empty; } }
        public string ContactName { get { return _contactName ?? string.Empty; } }
        public string ContactPhone { get { return _contactPhone ?? string.Empty; } }
        public string ContactAvatarUrl { get { return _contactAvatarUrl ?? string.Empty; } }
        public string ContactQrUrl { get { return _contactQrUrl ?? string.Empty; } }
        public Windows.UI.Xaml.Media.Imaging.BitmapImage ContactAvatarImage
        {
            get
            {
                if (_contactAvatarImage == null && !string.IsNullOrEmpty(_contactAvatarUrl))
                {
                    try
                    {
                        Uri avatarUri;
                        if (Uri.TryCreate(_contactAvatarUrl, UriKind.Absolute, out avatarUri)
                            && (avatarUri.Scheme == "http" || avatarUri.Scheme == "https"))
                        {
                            _contactAvatarImage = new Windows.UI.Xaml.Media.Imaging.BitmapImage(avatarUri);
                        }
                    }
                    catch { }
                }
                return _contactAvatarImage;
            }
        }
        public Brush ContactAvatarBrush
        {
            get
            {
                if (_contactAvatarBrush == null)
                {
                    try
                    {
                        if (ContactAvatarImage != null)
                        {
                            _contactAvatarBrush = new ImageBrush { ImageSource = ContactAvatarImage, Stretch = Stretch.UniformToFill };
                        }
                        else
                        {
                            _contactAvatarBrush = ZaloAvatar.ColorFor(_contactName);
                        }
                    }
                    catch
                    {
                        _contactAvatarBrush = ZaloAvatar.ColorFor(_contactName);
                    }
                }
                return _contactAvatarBrush;
            }
        }
        public bool HasContactCard { get { return !string.IsNullOrWhiteSpace(ContactUid); } }
        public Visibility ContactCardVisibility { get { return HasContactCard ? Visibility.Visible : Visibility.Collapsed; } }
        public string DetectedPhoneNumber
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Text) || HasContactCard || HasLinkPreview) return string.Empty;
                Match match = VietnamesePhoneRegex.Match(Text);
                return match.Success ? Regex.Replace(match.Value, @"[^0-9+]", string.Empty) : string.Empty;
            }
        }
        public bool HasPhonePreview { get { return !string.IsNullOrWhiteSpace(_phonePreviewUid); } }
        public Visibility PhonePreviewVisibility { get { return HasPhonePreview ? Visibility.Visible : Visibility.Collapsed; } }
        public string PhonePreviewNumber { get { return _phonePreviewNumber ?? DetectedPhoneNumber; } }
        public string PhonePreviewUid { get { return _phonePreviewUid ?? string.Empty; } }
        public string PhonePreviewName { get { return _phonePreviewName ?? string.Empty; } }
        public string PhonePreviewAvatarUrl { get { return _phonePreviewAvatarUrl ?? string.Empty; } }
        public Windows.UI.Xaml.Media.Imaging.BitmapImage PhonePreviewAvatar
        {
            get
            {
                if (_phonePreviewAvatar == null && !string.IsNullOrEmpty(_phonePreviewAvatarUrl))
                {
                    try
                    {
                        Uri avatarUri;
                        if (Uri.TryCreate(_phonePreviewAvatarUrl, UriKind.Absolute, out avatarUri)
                            && (avatarUri.Scheme == "http" || avatarUri.Scheme == "https"))
                        {
                            _phonePreviewAvatar = new Windows.UI.Xaml.Media.Imaging.BitmapImage(avatarUri);
                        }
                    }
                    catch { }
                }
                return _phonePreviewAvatar;
            }
        }
        public string PhonePreviewFriendAction
        {
            get { return _phonePreviewAlreadyFriend ? "Đã là bạn" : _phonePreviewFriendRequestSent ? "Đã gửi lời mời" : "Kết bạn"; }
        }
        public void SetPhonePreview(ZaloFriend friend, string phone)
        {
            if (friend == null || string.IsNullOrWhiteSpace(friend.UserId)) return;
            _phonePreviewUid = friend.UserId;
            _phonePreviewName = string.IsNullOrWhiteSpace(friend.Name) ? friend.UserId : friend.Name;
            _phonePreviewAvatarUrl = friend.Avatar ?? string.Empty;
            _phonePreviewNumber = phone ?? DetectedPhoneNumber;
            _phonePreviewAvatar = null;
            Raise("PhonePreviewVisibility");
            Raise("PhonePreviewNumber");
            Raise("PhonePreviewUid");
            Raise("PhonePreviewName");
            Raise("PhonePreviewAvatarUrl");
            Raise("PhonePreviewAvatar");
            Raise("PhonePreviewFriendAction");
        }
        public void MarkPhoneFriendRequestSent()
        {
            _phonePreviewFriendRequestSent = true;
            Raise("PhonePreviewFriendAction");
        }
        public void MarkPhonePreviewAlreadyFriend()
        {
            _phonePreviewAlreadyFriend = true;
            Raise("PhonePreviewFriendAction");
        }
        public void SetContactCard(string uid, string name, string avatarUrl, string qrUrl, string phone)
        {
            _contactUid = uid;
            _contactName = name;
            _contactAvatarUrl = avatarUrl;
            _contactQrUrl = qrUrl;
            _contactPhone = phone;
            _contactAvatarImage = null;
            _contactAvatarBrush = null;
            Raise("ContactUid");
            Raise("ContactName");
            Raise("ContactPhone");
            Raise("ContactAvatarImage");
            Raise("ContactAvatarBrush");
            Raise("HasContactCard");
            Raise("ContactCardVisibility");
            Text = "[Danh thiếp] " + (_contactName ?? _contactUid);
            Raise("HasText");
        }
        public void SetPoll(long pollId, string question)
        {
            if (pollId <= 0) return;
            _pollId = pollId;
            _pollQuestion = question;
            Raise("PollId");
            Raise("PollQuestion");
            Raise("PollVisibility");
            Raise("HasText");
        }
        public void SetLocation(double latitude, double longitude, string label)
        {
            if (latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180) return;
            CoreDispatcher dispatcher = Windows.ApplicationModel.Core.CoreApplication.MainView?.CoreWindow?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasThreadAccess)
            {
                var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                    () => SetLocationCore(latitude, longitude, label));
                return;
            }
            SetLocationCore(latitude, longitude, label);
        }
        private void SetLocationCore(double latitude, double longitude, string label)
        {
            _locationLatitude = latitude;
            _locationLongitude = longitude;
            _locationLabel = label;
            Raise("HasLocation");
            Raise("LocationLabel");
            Raise("LocationCoordinates");
            Raise("LocationUri");
            Raise("LocationVisibility");
            Raise("HasText");
        }
        public async Task OpenLocationAsync()
        {
            Uri target = LocationUri;
            if (target != null) await Windows.System.Launcher.LaunchUriAsync(target);
        }
        public void SetLinkPreview(string title, string description, string href, string imageUrl)
        {
            CoreDispatcher dispatcher = Windows.ApplicationModel.Core.CoreApplication.MainView?.CoreWindow?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasThreadAccess)
            {
                var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                    () => SetLinkPreviewCore(title, description, href, imageUrl));
                return;
            }
            SetLinkPreviewCore(title, description, href, imageUrl);
        }

        private void SetLinkPreviewCore(string title, string description, string href, string imageUrl)
        {
            _linkPreviewTitle = title;
            _linkPreviewDescription = description;
            _linkPreviewUrl = href;
            _linkPreviewImageUrl = imageUrl;
            _linkPreviewImage = null;
            Raise("LinkPreviewTitle");
            Raise("LinkPreviewDescription");
            Raise("LinkPreviewUrl");
            Raise("LinkPreviewUri");
            Raise("LinkPreviewImage");
            Raise("HasLinkPreview");
            Raise("LinkPreviewVisibility");
        }
        public bool CanReply { get { return !ReplyBlocked && !IsRecalled && !string.IsNullOrEmpty(GlobalMessageId)
            && !string.IsNullOrEmpty(ClientMessageId) && !string.IsNullOrEmpty(SenderUid)
            && (!string.IsNullOrEmpty(ReplyContent) || !string.IsNullOrEmpty(QuoteMessage)
                || !string.IsNullOrEmpty(QuoteAttachment)); } }
        public bool CanSendSeen { get { return !string.IsNullOrEmpty(GlobalMessageId) && !string.IsNullOrEmpty(ClientMessageId)
            && !string.IsNullOrEmpty(SenderUid) && !string.IsNullOrEmpty(RecipientUid); } }
        public bool CanReact
        {
            get
            {
                long globalId, clientId;
                return !IsRecalled && long.TryParse(GlobalMessageId, out globalId) && long.TryParse(ClientMessageId, out clientId);
            }
        }
        public bool IsRecalled
        {
            get { return _isRecalled; }
            set
            {
                if (_isRecalled == value) return;
                _isRecalled = value;
                Raise("IsRecalled");
                Raise("CanRecall");
                Raise("CanReply");
                Raise("DeliveryText");
                Raise("DeliveryVisibility");
                Raise("DeliveryAvatarVisibility");
            }
        }
        public bool IsRecallEvent { get; set; }
        public bool CanRecall
        {
            get
            {
                long globalId, clientId;
                return FromMe && !IsRecalled && long.TryParse(GlobalMessageId, out globalId)
                    && long.TryParse(ClientMessageId, out clientId);
            }
        }
        public bool CanDeleteForMe
        {
            get
            {
                long globalId, clientId;
                return long.TryParse(GlobalMessageId, out globalId) && long.TryParse(ClientMessageId, out clientId)
                    && !string.IsNullOrWhiteSpace(SenderUid);
            }
        }
        public string ReactionIcon
        {
            get { return _reactionIcon ?? string.Empty; }
            set
            {
                if (_reactionIcon == value) return;
                _reactionIcon = value;
                Raise("ReactionIcon");
                Raise("ReactionDisplayIcon");
                Raise("ReactionVisibility");
            }
        }
        public string ReactionDisplayIcon
        {
            get
            {
                switch (ReactionIcon)
                {
                    case "/-strong": return "\U0001F44D";
                    case "/-heart": return "\u2764\uFE0F";
                    case ":>": return "\U0001F602";
                    case ":o": return "\U0001F62E";
                    case ":-((":
                    case ":-(": return "\U0001F62D";
                    case ":-h": return "\U0001F621";
                    default: return string.IsNullOrEmpty(ReactionIcon) || ReactionIcon.StartsWith("/", StringComparison.Ordinal)
                        ? string.Empty : ReactionIcon;
                }
            }
        }
        public Visibility ReactionVisibility { get { return string.IsNullOrEmpty(ReactionDisplayIcon) ? Visibility.Collapsed : Visibility.Visible; } }
        public double MediaMaxWidth { get { return IsSticker || MsgType == "chat.gif" ? 200 : 260; } }
        public double MediaMaxHeight { get { return IsSticker || MsgType == "chat.gif" ? 230 : 390; } }
        public Visibility QuotedVisibility { get { return string.IsNullOrEmpty(QuotedText) ? Visibility.Collapsed : Visibility.Visible; } }
        public Brush SenderAvatarBrush
        {
            get
            {
                if (_senderAvatarBrush == null) _senderAvatarBrush = ZaloAvatar.ColorFor(Sender);
                return _senderAvatarBrush;
            }
            set { _senderAvatarBrush = value; }
        }
        public string SenderInitial { get { return ZaloAvatar.InitialOf(Sender); } }
        public Brush SenderAvatarImageBrush
        {
            get
            {
                if (_senderAvatarImageBrush == null && !string.IsNullOrEmpty(_senderAvatarUrl))
                {
                    try
                    {
                        _senderAvatarImageBrush = ZaloAvatar.ImageFor(_senderAvatarUrl);
                    }
                    catch { }
                }
                return _senderAvatarImageBrush;
            }
        }
        public string SenderAvatarUrl
        {
            get { return _senderAvatarUrl ?? string.Empty; }
            set
            {
                if (string.Equals(_senderAvatarUrl, value, StringComparison.Ordinal)) return;
                _senderAvatarUrl = value;
                _senderAvatarImageBrush = null;
                Raise("SenderAvatarUrl");
                Raise("SenderAvatarImageBrush");
            }
        }
        private string _emoticonUriString;
        private Windows.UI.Xaml.Media.Imaging.BitmapImage _emoticonImage;
        public Windows.UI.Xaml.Media.Imaging.BitmapImage EmoticonImage
        {
            get
            {
                if (_emoticonImage == null && !string.IsNullOrEmpty(_emoticonUriString))
                {
                    try
                    {
                        _emoticonImage = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(_emoticonUriString));
                    }
                    catch { }
                }
                return _emoticonImage;
            }
        }
        public bool HasEmoticon { get { return !string.IsNullOrEmpty(_emoticonUriString); } }
        public Visibility EmoticonVisibility { get { return HasEmoticon ? Visibility.Visible : Visibility.Collapsed; } }

        private void UpdateEmoticon()
        {
            _emoticonUriString = ZaloEmoticonMap.GetEmoticonUri(_text);
            _emoticonImage = null;
            Raise("EmoticonImage");
            Raise("HasEmoticon");
            Raise("EmoticonVisibility");
            Raise("HasText");
            Raise("Bubble");
            Raise("BubblePadding");
            Raise("BubbleBorderBrush");
            Raise("BubbleBorderThickness");
        }

        public string Text
        {
            get { return _text; }
            set
            {
                if (_text == value) return;
                _text = value;
                UpdateEmoticon();
                Raise("Text");
                Raise("HasText");
                Raise("DetectedPhoneNumber");
                Raise("Bubble");
                Raise("BubblePadding");
                Raise("BubbleBorderBrush");
                Raise("BubbleBorderThickness");
            }
        }
        public string MsgType { get; set; }
        public string StickerId { get; set; }
        public string FileName { get; set; }
        public string FileDisplayName { get { return string.IsNullOrWhiteSpace(FileName) ? "Tệp đính kèm" : FileName; } }
        public string MediaUrl
        {
            get { return _mediaUrl; }
            set
            {
                if (_mediaUrl == value) return;
                _mediaUrl = value;
                _mediaImage = null;
                _mediaLoadFailed = false;
                _playbackFailed = false;
                Raise("MediaUrl");
                Raise("MediaImage");
                Raise("HasHdPhoto");
                Raise("MediaVisibility");
                Raise("MediaRetryVisibility");
                Raise("MediaUri");
                Raise("HasVoice");
                Raise("HasMedia");
                Raise("LinkPreviewVisibility");
                Raise("HasVideo");
                Raise("VideoPlayerVisibility");
                Raise("VoicePlayerVisibility");
                Raise("PlaybackRetryVisibility");
                Raise("HasFile");
                Raise("HasText");
                Raise("Bubble");
                Raise("BubblePadding");
                Raise("BubbleBorderBrush");
                Raise("BubbleBorderThickness");
            }
        }
        public string MediaFullUrl
        {
            get { return string.IsNullOrWhiteSpace(_mediaFullUrl) ? _mediaUrl : _mediaFullUrl; }
            set
            {
                if (_mediaFullUrl == value) return;
                _mediaFullUrl = value;
                Raise("MediaFullUrl");
                Raise("HasHdPhoto");
            }
        }
        public bool HasHdPhoto
        {
            get { return MsgType == "chat.photo" && !string.IsNullOrWhiteSpace(_mediaFullUrl)
                && !string.Equals(_mediaFullUrl, _mediaUrl, StringComparison.OrdinalIgnoreCase); }
        }
        public string ViewerUrl { get { return string.IsNullOrWhiteSpace(MediaFullUrl) ? MediaUrl : MediaFullUrl; } }
        public Windows.UI.Xaml.Media.Imaging.BitmapImage MediaImage
        {
            get
            {
                if (_mediaImage == null && !string.IsNullOrWhiteSpace(_mediaUrl))
                {
                    Uri uri;
                    bool imageMessage = MsgType == "chat.photo" || MsgType == "chat.gif" || MsgType == "chat.doodle"
                        || MsgType == "chat.sticker" || MsgType == "5";
                    if (imageMessage && Uri.TryCreate(_mediaUrl, UriKind.Absolute, out uri)
                        && (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "ms-appx"))
                    {
                        _mediaImage = CreateChatImage(uri);
                    }
                }
                return _mediaImage;
            }
        }
        public Visibility MediaVisibility { get { return HasMedia && !_mediaLoadFailed ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility MediaRetryVisibility { get { return _mediaLoadFailed ? Visibility.Visible : Visibility.Collapsed; } }
        public string MediaRetryText
        {
            get
            {
                if (IsSticker) return "Không tải được nhãn dán · Thử lại";
                return MsgType == "chat.doodle" ? "Không tải được bản vẽ · Thử lại" : "Không tải được ảnh · Thử lại";
            }
        }
        public Uri MediaUri
        {
            get
            {
                Uri uri;
                return Uri.TryCreate(MediaUrl, UriKind.Absolute, out uri)
                    && (uri.Scheme == "http" || uri.Scheme == "https" || (MsgType == "chat.gif" && uri.Scheme == "ms-appx"))
                    ? uri : null;
            }
        }
        private static Windows.UI.Xaml.Media.Imaging.BitmapImage CreateChatImage(Uri uri)
        {
            if (uri == null) return null;
            try
            {
                var bitmap = new Windows.UI.Xaml.Media.Imaging.BitmapImage();
                bool isGif = uri.AbsoluteUri.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
                if (!isGif && (uri.Scheme == "http" || uri.Scheme == "https"))
                    bitmap.DecodePixelWidth = 520;
                bitmap.UriSource = uri;
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
        public bool HasVoice { get { return (MsgType == "chat.voice" || MsgType == "31") && MediaUri != null; } }
        public bool HasVideo { get { return (MsgType == "chat.video.msg" || MsgType == "44") && MediaUri != null; } }
        public Visibility VideoPlayerVisibility { get { return HasVideo && !_playbackFailed ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility VoicePlayerVisibility { get { return HasVoice && !_playbackFailed ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility PlaybackRetryVisibility { get { return _playbackFailed ? Visibility.Visible : Visibility.Collapsed; } }
        public string PlaybackFailureText { get { return _playbackFailedVideo ? "Video này chưa phát được trên thiết bị." : "Tin nhắn thoại chưa phát được."; } }
        public void SetPlaybackFailure(bool video)
        {
            _playbackFailed = true;
            _playbackFailedVideo = video;
            Raise("VideoPlayerVisibility");
            Raise("VoicePlayerVisibility");
            Raise("PlaybackRetryVisibility");
            Raise("PlaybackFailureText");
            Raise("HasText");
            Raise("Bubble");
            Raise("BubblePadding");
            Raise("BubbleBorderBrush");
            Raise("BubbleBorderThickness");
        }
        public void ResetPlaybackFailure()
        {
            if (!_playbackFailed) return;
            _playbackFailed = false;
            Raise("VideoPlayerVisibility");
            Raise("VoicePlayerVisibility");
            Raise("PlaybackRetryVisibility");
        }
        public bool HasFile { get { return (MsgType == "share.file" || MsgType == "46") && MediaUri != null; } }
        public bool IsSticker { get { return MsgType == "chat.sticker" || MsgType == "5"; } }
        public bool HasMedia
        {
            get
            {
                bool imageMessage = MsgType == "chat.photo" || MsgType == "chat.gif" || MsgType == "chat.doodle"
                    || MsgType == "chat.sticker" || MsgType == "5";
                return imageMessage && !string.IsNullOrWhiteSpace(_mediaUrl);
            }
        }
        public void MarkMediaLoadFailed()
        {
            _mediaLoadFailed = true;
            Raise("MediaVisibility");
            Raise("MediaRetryVisibility");
            Raise("Bubble");
            Raise("BubblePadding");
            Raise("BubbleBorderBrush");
            Raise("BubbleBorderThickness");
        }
        public void SetDownloadedMediaImage(Windows.UI.Xaml.Media.Imaging.BitmapImage image)
        {
            if (image == null) return;
            _mediaImage = image;
            _mediaLoadFailed = false;
            Raise("MediaImage");
            Raise("MediaVisibility");
            Raise("MediaRetryVisibility");
            Raise("HasMedia");
            Raise("LinkPreviewVisibility");
            Raise("Bubble");
            Raise("BubblePadding");
            Raise("BubbleBorderBrush");
            Raise("BubbleBorderThickness");
        }
        public void RetryMediaLoad()
        {
            if (string.IsNullOrWhiteSpace(_mediaUrl)) return;
            Uri uri;
            bool imageMessage = MsgType == "chat.photo" || MsgType == "chat.gif" || MsgType == "chat.doodle"
                || MsgType == "chat.sticker" || MsgType == "5";
            _mediaImage = imageMessage && Uri.TryCreate(_mediaUrl, UriKind.Absolute, out uri)
                && (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "ms-appx") ? CreateChatImage(uri) : null;
            _mediaLoadFailed = false;
            Raise("MediaImage");
            Raise("MediaVisibility");
            Raise("MediaRetryVisibility");
            Raise("HasMedia");
            Raise("LinkPreviewVisibility");
            Raise("Bubble");
            Raise("BubblePadding");
            Raise("BubbleBorderBrush");
            Raise("BubbleBorderThickness");
        }
        public bool HasText
        {
            get
            {
                bool imagePlaceholder = (MsgType == "chat.photo" || MsgType == "chat.gif" || MsgType == "chat.doodle" || MsgType == "chat.sticker" || MsgType == "5")
                    && (Text == "[\u1EA2nh]" || Text == "[\u1EA2nh \u0111\u1ED9ng]" || Text == "[Nh\u00E3n d\u00E1n]" || Text == "[B\u1EA3n v\u1EBD]");
                bool voicePlaceholder = HasVoice && Text == "[Tin nh\u1EAFn tho\u1EA1i]";
                return !string.IsNullOrWhiteSpace(Text) && !HasVoice && !HasVideo && !HasFile && !HasLocation && PollId <= 0
                    && !HasContactCard && !HasEmoticon && !imagePlaceholder && !voicePlaceholder;
            }
        }
        public bool FromMe { get; set; }
        public bool IsSystemEvent { get { return MsgType == "system.group_event"; } }
        public Visibility RegularMessageVisibility { get { return IsSystemEvent ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility SystemEventVisibility { get { return IsSystemEvent ? Visibility.Visible : Visibility.Collapsed; } }
        public long Ts { get; set; }

        public bool ShowSender { get; set; }
        public Visibility MessageAvatarVisibility { get { return !FromMe && !IsSystemEvent ? Visibility.Visible : Visibility.Collapsed; } }
        public int MessageColumn { get { return FromMe ? 0 : 1; } }
        public int MessageColumnSpan { get { return FromMe ? 2 : 1; } }
        private bool _showDateHeader;
        private string _dateHeaderText = string.Empty;
        public bool ShowDateHeader
        {
            get { return _showDateHeader; }
            set { if (_showDateHeader != value) { _showDateHeader = value; Raise("ShowDateHeader"); Raise("DateHeaderVisibility"); } }
        }
        public string DateHeaderText
        {
            get { return _dateHeaderText; }
            set { if (_dateHeaderText != value) { _dateHeaderText = value ?? string.Empty; Raise("DateHeaderText"); } }
        }
        public Visibility DateHeaderVisibility { get { return ShowDateHeader ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility SenderVisibility { get { return ShowSender ? Visibility.Visible : Visibility.Collapsed; } }
        public string TimeText { get { return ZaloFormat.Time(Ts); } }
        public string DeliveryText
        {
            get
            {
                if (!FromMe || IsRecalled || (string.IsNullOrWhiteSpace(GlobalMessageId)
                    && string.IsNullOrWhiteSpace(ClientMessageId))) return string.Empty;
                return ReceiptState >= 2 ? "✓✓  Đã xem" : ReceiptState == 1 ? "✓✓  Đã nhận" : "✓  Đã gửi";
            }
        }
        public Brush DeliveryForeground { get { return ReceiptState >= 2
            ? ReceiptSeenBrush : ReceiptPendingBrush; } }
        public Visibility DeliveryAvatarVisibility { get { return ReceiptState >= 2 && DeliveryAvatarBrush != null
            ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility DeliveryVisibility
        {
            get { return string.IsNullOrEmpty(DeliveryText) && DeliveryAvatarVisibility == Visibility.Collapsed
                ? Visibility.Collapsed : Visibility.Visible; }
        }

        public void SetDeliveryReceipt(int state, string avatarUrl)
        {
            ReceiptState = Math.Max(ReceiptState, state);
            if (!string.IsNullOrWhiteSpace(avatarUrl)) DeliveryAvatarUrl = avatarUrl;
        }

        public HorizontalAlignment Align
        {
            get { return IsSystemEvent ? HorizontalAlignment.Center : FromMe ? HorizontalAlignment.Right : HorizontalAlignment.Left; }
        }

        public Brush Bubble
        {
            get
            {
                if (HasEmoticon) return MediaBubbleBrush;
                if (HasMedia && !HasText && !_mediaLoadFailed) return MediaBubbleBrush;
                return FromMe ? OutgoingBubbleBrush : IncomingBubbleBrush;
            }
        }
        public Thickness BubblePadding
        {
            get
            {
                if (HasEmoticon) return new Thickness(2);
                return HasMedia && !HasText && !_mediaLoadFailed ? new Thickness(0) : new Thickness(11, 8, 11, 8);
            }
        }
        public Brush BubbleBorderBrush
        {
            get
            {
                if (HasEmoticon) return MediaBubbleBrush;
                return HasMedia && !HasText && !_mediaLoadFailed ? MediaBubbleBrush : MessageBorderBrush;
            }
        }
        public Thickness BubbleBorderThickness
        {
            get
            {
                if (HasEmoticon) return new Thickness(0);
                return HasMedia && !HasText && !_mediaLoadFailed ? new Thickness(0) : new Thickness(0.35);
            }
        }
    }


    public sealed class ZaloConversation : ZaloObservable
    {
        private string _name = string.Empty;
        private string _lastText = string.Empty;
        private long _lastTs;
        private int _unread;
        private string _avatarUrl;
        private bool _isPinned;
        private bool _isMarkedUnread;
        private bool _isMuted;
        private bool _friendRequestSent;
        private bool _isTyping;
        private bool _hasMoreHistory;
        private int _groupMemberCount;
        private bool _groupSettingsLoaded;
        private bool _groupPostingRestricted;
        private string _groupPinnedPreview = string.Empty;
        private DateTime _groupPinnedPreviewUpdatedUtc = DateTime.MinValue;
        private int _typingRevision;
        private Brush _colorBrush;
        private Brush _imageBrush;
        private bool _imageMade;

        public string Key { get; set; }
        public string ThreadId { get; set; }
        public bool IsGroup { get; set; }
        public bool FriendRequestSent
        {
            get { return _friendRequestSent; }
            set { if (_friendRequestSent != value) { _friendRequestSent = value; Raise("FriendRequestSent"); } }
        }
        public int GroupMemberCount
        {
            get { return _groupMemberCount; }
            set { if (_groupMemberCount != value) { _groupMemberCount = Math.Max(0, value); Raise("GroupMemberCount"); } }
        }
        public bool GroupSettingsLoaded
        {
            get { return _groupSettingsLoaded; }
            set { if (_groupSettingsLoaded != value) { _groupSettingsLoaded = value; Raise("GroupSettingsLoaded"); } }
        }
        public bool GroupPostingRestricted
        {
            get { return _groupPostingRestricted; }
            set { if (_groupPostingRestricted != value) { _groupPostingRestricted = value; Raise("GroupPostingRestricted"); } }
        }
        public void UpdateGroupMessagePermission(bool settingsLoaded, bool lockSendMessage, bool currentUserIsAdmin)
        {
            GroupPostingRestricted = settingsLoaded && lockSendMessage && !currentUserIsAdmin;
            GroupSettingsLoaded = settingsLoaded;
        }
        public string GroupPinnedPreview
        {
            get { return _groupPinnedPreview; }
            set { if (_groupPinnedPreview != value) { _groupPinnedPreview = value ?? string.Empty; Raise("GroupPinnedPreview"); } }
        }
        public DateTime GroupPinnedPreviewUpdatedUtc
        {
            get { return _groupPinnedPreviewUpdatedUtc; }
            set { _groupPinnedPreviewUpdatedUtc = value; }
        }
        public int HistoryLoadedCount { get; set; }
        public bool HasMoreHistory
        {
            get { return _hasMoreHistory; }
            set { if (_hasMoreHistory != value) { _hasMoreHistory = value; Raise("HasMoreHistory"); } }
        }
        public bool IsPinned
        {
            get { return _isPinned; }
            set { if (_isPinned != value) { _isPinned = value; Raise("IsPinned"); Raise("PinnedVisibility"); } }
        }
        public bool IsMuted
        {
            get { return _isMuted; }
            set { if (_isMuted != value) { _isMuted = value; Raise("IsMuted"); Raise("MutedVisibility"); } }
        }
        public bool CanDeleteFromInbox { get { return Messages.Any(m => m.CanDeleteForMe); } }
        public Visibility PinnedVisibility { get { return IsPinned ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility MutedVisibility { get { return IsMuted ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility GroupVisibility { get { return IsGroup ? Visibility.Visible : Visibility.Collapsed; } }
        public bool IsMarkedUnread
        {
            get { return _isMarkedUnread; }
            set
            {
                if (_isMarkedUnread != value)
                {
                    _isMarkedUnread = value;
                    Raise("IsMarkedUnread");
                    Raise("UnreadVisibility");
                    Raise("UnreadLabel");
                }
            }
        }
        public bool IsTyping
        {
            get { return _isTyping; }
            set { if (_isTyping != value) { _isTyping = value; Raise("IsTyping"); } }
        }
        public int TypingRevision { get { return _typingRevision; } set { _typingRevision = value; } }
        public ObservableCollection<ZaloChatMessage> Messages { get; private set; }
        public HashSet<string> SeenIds { get; private set; }

        public ZaloConversation()
        {
            Messages = new ObservableCollection<ZaloChatMessage>();
            SeenIds = new HashSet<string>();
        }

        public string Name
        {
            get { return _name; }
            set { if (_name != value) { _name = value; _colorBrush = null; Raise("Name"); Raise("Initial"); Raise("AvatarColor"); } }
        }

        public string LastText
        {
            get { return _lastText; }
            set
            {
                string clean = ZaloFormat.SanitizePreviewText(value);
                if (_lastText != clean) { _lastText = clean; Raise("LastText"); }
            }
        }

        public long LastTs
        {
            get { return _lastTs; }
            set { if (_lastTs != value) { _lastTs = value; Raise("LastTs"); Raise("TimeText"); } }
        }

        public int Unread
        {
            get { return _unread; }
            set
            {
                if (_unread != value)
                {
                    _unread = value;
                    Raise("Unread");
                    Raise("UnreadVisibility");
                    Raise("UnreadLabel");
                }
            }
        }

        public string AvatarUrl
        {
            get { return _avatarUrl; }
            set
            {
                if (_avatarUrl == value) return;
                _avatarUrl = value;
                _imageBrush = null;
                _imageMade = false;
                Raise("AvatarImageBrush");
            }
        }

        public string Initial { get { return ZaloAvatar.InitialOf(_name); } }

        public Brush AvatarColor
        {
            get
            {
                if (_colorBrush == null) _colorBrush = ZaloAvatar.ColorFor(_name);
                return _colorBrush;
            }
        }

        public Brush AvatarImageBrush
        {
            get
            {
                if (!_imageMade)
                {
                    _imageBrush = ZaloAvatar.ImageFor(_avatarUrl);
                    _imageMade = true;
                }
                return _imageBrush;
            }
        }

        public string TimeText { get { return ZaloFormat.Time(_lastTs); } }
        public string UnreadLabel
        {
            get { return _unread > 99 ? "99+" : _unread > 0 ? _unread.ToString() : IsMarkedUnread ? "1" : string.Empty; }
        }
        public Visibility UnreadVisibility { get { return _unread > 0 || IsMarkedUnread ? Visibility.Visible : Visibility.Collapsed; } }
    }

}
