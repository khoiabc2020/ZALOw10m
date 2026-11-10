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
    public static class ZaloApp
    {
        private static CoreDispatcher _dispatcher;

        public static void SetDispatcher(CoreDispatcher dispatcher)
        {
            if (dispatcher != null)
            {
                _dispatcher = dispatcher;
                ZaloPhoneSync.Initialize(dispatcher);
            }
        }
        private static readonly SemaphoreSlim ConversationCacheWriteLock = new SemaphoreSlim(1, 1);
        private static readonly Dictionary<string, ZaloConversation> ByKey = new Dictionary<string, ZaloConversation>();
        private static readonly HashSet<string> PendingGroups = new HashSet<string>();
        private static HashSet<string> PinnedConversationKeys = new HashSet<string>(StringComparer.Ordinal);
        private static HashSet<string> ArchivedConversationKeys = new HashSet<string>(StringComparer.Ordinal);
        private static HashSet<string> UnreadMarkedKeys = new HashSet<string>(StringComparer.Ordinal);
        private static HashSet<string> MutedConversationKeys = new HashSet<string>(StringComparer.Ordinal);
        private static HashSet<string> HiddenConversationKeys = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, DateTime> LastTypingSentUtc = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly HashSet<string> LoadingHistory = new HashSet<string>();
        private static readonly HashSet<string> LoadedHistory = new HashSet<string>();
        private static readonly object StickerUrlLock = new object();
        private static readonly HashSet<string> LoadingStickerUrls = new HashSet<string>();
        private static readonly Dictionary<string, string> StickerUrlCache = new Dictionary<string, string>();
        private static readonly object LinkPreviewLock = new object();
        private static readonly Dictionary<string, JsonObject> LinkPreviewCache = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LoadingLinkPreviews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _friendNames = new Dictionary<string, string>();
        private static Dictionary<string, string> _friendAvatars = new Dictionary<string, string>();
        private static string _cachedDataUid;
        private static bool _batchScheduled;
        private static bool _resolvingGroups;
        private static bool _resumeHooked;

        public static ZaloSession Session { get; private set; }
        public static ZaloListener Listener { get; private set; }
        public static ZaloConversation CurrentChat { get; set; }
        public static string ConnectionStatus { get; private set; }
        public static List<ZaloFriend> Friends { get; set; }
        public static bool FriendsLoaded { get; private set; }
        public static ObservableCollection<ZaloConversation> Conversations { get; private set; }
        public static ObservableCollection<ZaloConversation> Groups { get; private set; }
        public static bool GroupsLoaded { get; private set; }
        public static bool GroupsLoadFailed { get; private set; }
        public static bool IsRestoringCache { get; private set; }

        public static bool IsFriendOnline(string userId)
        {
            return !string.IsNullOrEmpty(userId) && Friends != null
                && Friends.Any(friend => friend.UserId == userId && friend.IsOnline);
        }

        public static bool IsFriend(string userId)
        {
            return !string.IsNullOrWhiteSpace(userId) && FriendsLoaded && Friends != null
                && Friends.Any(friend => string.Equals(friend.UserId, userId, StringComparison.Ordinal));
        }

        public static void UpdateConversationPreviewFromMessage(ZaloConversation conversation, ZaloChatMessage message)
        {
            if (conversation == null || message == null) return;
            if (message.Ts > 0 && (conversation.LastTs <= 0 || message.Ts >= conversation.LastTs))
            {
                conversation.LastTs = message.Ts;
            }

            string preview = message.Text;
            if (string.IsNullOrWhiteSpace(preview) || preview.Contains("dlfl.vn") || preview.Contains("zadn.vn/api/emoticon"))
            {
                preview = message.HasMedia || message.MsgType == "chat.photo" ? "[Ảnh]"
                    : message.IsSticker || message.MsgType == "5" || message.MsgType == "chat.sticker" ? "[Nhãn dán]"
                    : message.HasVoice ? "[Tin nhắn thoại]"
                    : message.HasVideo ? "[Video]"
                    : message.HasFile ? "[Tệp] " + (message.FileName ?? string.Empty)
                    : message.HasLocation ? "[Vị trí]"
                    : message.HasContactCard ? (string.IsNullOrEmpty(message.ContactName) ? "[Danh thiếp]" : "[Danh thiếp] " + message.ContactName)
                    : string.Empty;
            }
            else
            {
                preview = ZaloFormat.SanitizePreviewText(preview);
            }

            conversation.LastText = conversation.IsGroup && !message.FromMe && !message.IsSystemEvent
                ? message.Sender + ": " + preview
                : preview;
        }

        public static event Action StatusChanged;
        public static event Action GroupsLoadChanged;
        public static event Action FriendsLoadChanged;
        public static event Action CacheRestored;
        public static event Action<ZaloConversation> TypingChanged;

        static ZaloApp()
        {
            Conversations = new ObservableCollection<ZaloConversation>();
            Groups = new ObservableCollection<ZaloConversation>();
            Friends = new List<ZaloFriend>();
            FriendsLoaded = false;
            ConnectionStatus = string.Empty;
        }

        private const string LegacyCacheFile = "zalo-conversations.json";

        private static string CacheFileFor(ZaloSession session)
        {
            // Keep cached conversations separated between accounts on a shared device.
            return CacheFileForUid(session == null ? string.Empty : session.Uid);
        }

        private static string CacheFileForUid(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return null;
            return "zalo-conversations-" + ZaloCrypto.Md5Hex(uid).Substring(0, 16) + ".json";
        }

        private static string FriendCacheFileFor(ZaloSession session)
        {
            return FriendCacheFileForUid(session == null ? string.Empty : session.Uid);
        }

        private static string FriendCacheFileForUid(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return null;
            return "zalo-friends-" + ZaloCrypto.Md5Hex(uid).Substring(0, 16) + ".json";
        }

        public static async Task LoadCachedFriendsAsync(ZaloSession session)
        {
            await LoadCachedFriendsAsync(session == null ? string.Empty : session.Uid);
        }

        private static async Task LoadCachedFriendsAsync(string userId)
        {
            try
            {
                string cacheFile = FriendCacheFileForUid(userId);
                if (cacheFile == null) return;
                IStorageItem item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(cacheFile);
                StorageFile file = item as StorageFile;
                if (file == null) return;

                string json = await FileIO.ReadTextAsync(file);
                JsonArray root;
                if (!JsonArray.TryParse(json, out root)) return;
                FriendsLoaded = true;
                var cached = new List<ZaloFriend>();
                foreach (IJsonValue value in root)
                {
                    if (value.ValueType != JsonValueType.Object) continue;
                    JsonObject friend = value.GetObject();
                    string uid = ZaloAuth.Str(friend, "userId");
                    if (string.IsNullOrWhiteSpace(uid)) continue;
                    cached.Add(new ZaloFriend
                    {
                        UserId = uid,
                        Name = ZaloAuth.Str(friend, "name"),
                        OriginalName = ZaloAuth.Str(friend, "originalName"),
                        Alias = ZaloAuth.Str(friend, "alias"),
                        Avatar = ZaloAuth.Str(friend, "avatar"),
                        Subtitle = ZaloAuth.Str(friend, "subtitle"),
                        IsOnline = false
                    });
                }
                if (cached.Count == 0) return;
                Friends = cached;
                UpdateFriendLookupCaches();
                ZaloLog.Add("Restored cached friends: " + Friends.Count);
                if (FriendsLoadChanged != null) FriendsLoadChanged();
            }
            catch (Exception ex) { ZaloLog.Add("Friend cache load failed: " + ex.Message); }
        }

        public static async Task SaveCachedFriendsAsync()
        {
            try
            {
                string cacheFile = FriendCacheFileFor(Session);
                if (cacheFile == null || Friends == null || Friends.Count == 0) return;
                var root = new JsonArray();
                foreach (ZaloFriend friend in Friends)
                {
                    if (friend == null || string.IsNullOrWhiteSpace(friend.UserId)) continue;
                    var value = new JsonObject();
                    value["userId"] = JsonValue.CreateStringValue(friend.UserId);
                    value["name"] = JsonValue.CreateStringValue(friend.Name ?? string.Empty);
                    value["originalName"] = JsonValue.CreateStringValue(friend.OriginalName ?? string.Empty);
                    value["alias"] = JsonValue.CreateStringValue(friend.Alias ?? string.Empty);
                    value["avatar"] = JsonValue.CreateStringValue(friend.Avatar ?? string.Empty);
                    value["subtitle"] = JsonValue.CreateStringValue(friend.Subtitle ?? string.Empty);
                    root.Add(value);
                }
                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    cacheFile, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, root.Stringify());
            }
            catch (Exception ex) { ZaloLog.Add("Friend cache save failed: " + ex.Message); }
        }

        private static void UpdateFriendLookupCaches()
        {
            _friendNames = (Friends ?? new List<ZaloFriend>())
                .Where(friend => !string.IsNullOrWhiteSpace(friend.UserId))
                .GroupBy(friend => friend.UserId)
                .ToDictionary(group => group.Key, group => group.First().Name);
            _friendAvatars = (Friends ?? new List<ZaloFriend>())
                .Where(friend => !string.IsNullOrWhiteSpace(friend.UserId) && !string.IsNullOrWhiteSpace(friend.Avatar))
                .GroupBy(friend => friend.UserId)
                .ToDictionary(group => group.Key, group => group.First().Avatar);
        }

        private static async Task DeleteLegacyCacheAsync()
        {
            try
            {
                IStorageItem legacy = await Windows.Storage.ApplicationData.Current.LocalFolder.TryGetItemAsync(LegacyCacheFile);
                if (legacy != null) await legacy.DeleteAsync();
            }
            catch { }
        }

        public static async Task LoadCachedConversationsAsync()
        {
            await LoadCachedConversationsAsync(Session == null ? string.Empty : Session.Uid);
        }

        private static async Task LoadCachedConversationsAsync(string userId)
        {
            try
            {
                string cacheFile = CacheFileForUid(userId);
                if (cacheFile == null) return;
                var item = await Windows.Storage.ApplicationData.Current.LocalFolder.TryGetItemAsync(cacheFile);
                if (item is Windows.Storage.StorageFile file)
                {
                    string json = await Windows.Storage.FileIO.ReadTextAsync(file);
                    JsonArray root;
                    if (JsonArray.TryParse(json, out root))
                    {
                        foreach (var itemVal in root)
                        {
                            try
                            {
                                if (itemVal.ValueType != JsonValueType.Object) continue;
                                var o = itemVal.GetObject();
                                string threadId = ZaloAuth.Str(o, "threadId");
                                bool isGroup = o.GetNamedBoolean("isGroup", false);
                                if (string.IsNullOrEmpty(threadId)) continue;

                                var conv = GetOrCreate(threadId, isGroup);
                                string name = ZaloAuth.Str(o, "name");
                                string avatar = ZaloAuth.Str(o, "avatar");
                                if (!string.IsNullOrEmpty(name)) conv.Name = name;
                                if (!string.IsNullOrEmpty(avatar)) conv.AvatarUrl = avatar;
                                conv.LastText = ZaloAuth.Str(o, "lastText");
                                conv.LastTs = (long)ZaloAuth.Num(o, "lastTs");
                                conv.Unread = (int)ZaloAuth.Num(o, "unread");
                                conv.IsMarkedUnread = o.GetNamedBoolean("isMarkedUnread", false);
                                conv.IsMuted = o.GetNamedBoolean("isMuted", conv.IsMuted);
                                conv.GroupPostingRestricted = o.GetNamedBoolean("groupPostingRestricted", false);
                                conv.GroupSettingsLoaded = o.GetNamedBoolean("groupSettingsLoaded", false);

                                IJsonValue msgsVal;
                                if (o.TryGetValue("msgs", out msgsVal) && msgsVal.ValueType == JsonValueType.Array)
                                {
                                    foreach (var mVal in msgsVal.GetArray())
                                    {
                                        try
                                        {
                                            if (mVal.ValueType != JsonValueType.Object) continue;
                                            var mo = mVal.GetObject();
                                            string msgId = ZaloAuth.Str(mo, "id");
                                            if (!string.IsNullOrEmpty(msgId) && conv.SeenIds.Add(msgId))
                                            {
                                                var cachedMessage = new ZaloChatMessage
                                                {
                                                    Id = msgId,
                                                    GlobalMessageId = ZaloAuth.Str(mo, "globalMessageId"),
                                                    ClientMessageId = ZaloAuth.Str(mo, "clientMsgId"),
                                                    SenderUid = ZaloAuth.Str(mo, "senderUid"),
                                                    RecipientUid = ZaloAuth.Str(mo, "recipientUid"),
                                                    Sender = ZaloAuth.Str(mo, "sender"),
                                                    Status = (int)ZaloAuth.Num(mo, "status"),
                                                    At = (int)ZaloAuth.Num(mo, "at"),
                                                    Command = (int)ZaloAuth.Num(mo, "command"),
                                                    SeenSent = mo.GetNamedBoolean("seenSent", false),
                                                    ReceiptState = (int)ZaloAuth.Num(mo, "receiptState"),
                                                    DeliveryAvatarUrl = ZaloAuth.Str(mo, "deliveryAvatarUrl"),
                                                    SenderAvatarUrl = ZaloAuth.Str(mo, "senderAvatarUrl"),
                                                    Text = ZaloAuth.Str(mo, "text"),
                                                    ReplyContent = ZaloAuth.Str(mo, "replyContent"),
                                                    QuoteMessage = ZaloAuth.Str(mo, "quoteMessage"),
                                                    QuoteAttachment = ZaloAuth.Str(mo, "quoteAttachment"),
                                                    ReplyBlocked = mo.GetNamedBoolean("replyBlocked", false),
                                                    QuotedSender = ZaloAuth.Str(mo, "quotedSender"),
                                                    QuotedText = ZaloAuth.Str(mo, "quotedText"),
                                                    ReactionIcon = ZaloAuth.Str(mo, "reactionIcon"),
                                                    IsRecalled = mo.GetNamedBoolean("isRecalled", false),
                                                    MsgType = ZaloAuth.Str(mo, "msgType"),
                                                    StickerId = ZaloAuth.Str(mo, "stickerId"),
                                                    FileName = ZaloAuth.Str(mo, "fileName"),
                                                    MediaUrl = ZaloAuth.Str(mo, "mediaUrl"),
                                                    MediaFullUrl = ZaloAuth.Str(mo, "mediaFullUrl"),
                                                    FromMe = mo.GetNamedBoolean("fromMe", false),
                                                    ShowSender = mo.GetNamedBoolean("showSender", false),
                                                    Ts = (long)ZaloAuth.Num(mo, "ts")
                                                };
                                                if (mo.GetNamedBoolean("hasLocation", false))
                                                    cachedMessage.SetLocation(ZaloAuth.Num(mo, "locationLatitude"),
                                                        ZaloAuth.Num(mo, "locationLongitude"), ZaloAuth.Str(mo, "locationLabel"));
                                                string cachedLinkUrl = ZaloAuth.Str(mo, "linkPreviewUrl");
                                                if (cachedLinkUrl.Length > 0)
                                                    cachedMessage.SetLinkPreview(ZaloAuth.Str(mo, "linkPreviewTitle"),
                                                        ZaloAuth.Str(mo, "linkPreviewDescription"), cachedLinkUrl,
                                                        ZaloAuth.Str(mo, "linkPreviewImageUrl"));
                                                long cachedPollId = (long)ZaloAuth.Num(mo, "pollId");
                                                if (cachedPollId > 0) cachedMessage.SetPoll(cachedPollId, ZaloAuth.Str(mo, "pollQuestion"));
                                                string cachedContactUid = ZaloAuth.Str(mo, "contactUid");
                                                if (cachedContactUid.Length > 0)
                                                    cachedMessage.SetContactCard(cachedContactUid, ZaloAuth.Str(mo, "contactName"),
                                                        ZaloAuth.Str(mo, "contactAvatarUrl"), ZaloAuth.Str(mo, "contactQrUrl"),
                                                        ZaloAuth.Str(mo, "contactPhone"));
                                                else if (!cachedMessage.HasContactCard && !string.IsNullOrEmpty(cachedMessage.Text) && cachedMessage.Text.TrimStart().StartsWith("{") && (cachedMessage.Text.Contains("\"phone\"") || cachedMessage.Text.Contains("\"qrCodeUrl\"")))
                                                {
                                                    JsonObject cardObj = ZaloJson.ParseObject(cachedMessage.Text);
                                                    if (cardObj != null)
                                                    {
                                                        string cardPhone = ZaloAuth.Str(cardObj, "phone");
                                                        if (cardPhone.Length == 0) cardPhone = ZaloAuth.Str(cardObj, "phoneNumber");
                                                        string cardQr = ZaloAuth.Str(cardObj, "qrCodeUrl");
                                                        string cardName = ZaloAuth.Str(cardObj, "displayName");
                                                        if (cardName.Length == 0) cardName = ZaloAuth.Str(cardObj, "name");
                                                        if (cardName.Length == 0) cardName = cardPhone;
                                                        string cardUid = ZaloAuth.Str(cardObj, "contactUid");
                                                        if (cardUid.Length == 0) cardUid = cardPhone;
                                                        cachedMessage.SetContactCard(cardUid, cardName, ZaloAuth.Str(cardObj, "avatar"), cardQr, cardPhone);
                                                    }
                                                }
                                                conv.Messages.Add(cachedMessage);
                                            }
                                        }
                                        catch (Exception exMsg)
                                        {
                                            ZaloLog.Add("Cached msg error: " + exMsg.Message);
                                        }
                                    }
                                }

                                if ((string.IsNullOrWhiteSpace(conv.LastText) || conv.LastTs <= 0) && conv.Messages.Count > 0)
                                {
                                    var latest = conv.Messages.OrderBy(m => m.Ts).LastOrDefault();
                                    if (latest != null) UpdateConversationPreviewFromMessage(conv, latest);
                                }
                            }
                            catch (Exception exConv)
                            {
                                ZaloLog.Add("Cached conv error: " + exConv.Message);
                            }
                        }

                        foreach (var c in Conversations)
                        {
                            if (string.IsNullOrWhiteSpace(c.LastText))
                            {
                                if (c.Messages.Count > 0)
                                {
                                    var latest = c.Messages.OrderBy(m => m.Ts).LastOrDefault();
                                    if (latest != null) UpdateConversationPreviewFromMessage(c, latest);
                                }
                                else if (!c.IsGroup)
                                {
                                    c.LastText = "Nhấn để trò chuyện";
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Load cache error: " + ex.Message);
            }
        }

        public static async Task SaveCachedConversationsAsync()
        {
            await ConversationCacheWriteLock.WaitAsync();
            try { await SaveCachedConversationsCoreAsync(); }
            finally { ConversationCacheWriteLock.Release(); }
        }

        private static async Task SaveCachedConversationsCoreAsync()
        {
            try
            {
                string cacheFile = CacheFileFor(Session);
                if (cacheFile == null) return;
                var arr = new JsonArray();
                var list = Conversations.ToList();
                foreach (var conv in list)
                {
                    var o = new JsonObject();
                    o["key"] = JsonValue.CreateStringValue(conv.Key ?? string.Empty);
                    o["threadId"] = JsonValue.CreateStringValue(conv.ThreadId ?? string.Empty);
                    o["isGroup"] = JsonValue.CreateBooleanValue(conv.IsGroup);
                    o["name"] = JsonValue.CreateStringValue(conv.Name ?? string.Empty);
                    o["avatar"] = JsonValue.CreateStringValue(conv.AvatarUrl ?? string.Empty);
                    o["lastText"] = JsonValue.CreateStringValue(conv.LastText ?? string.Empty);
                    o["lastTs"] = JsonValue.CreateNumberValue(conv.LastTs);
                    o["unread"] = JsonValue.CreateNumberValue(conv.Unread);
                    o["isMarkedUnread"] = JsonValue.CreateBooleanValue(conv.IsMarkedUnread);
                    o["isMuted"] = JsonValue.CreateBooleanValue(conv.IsMuted);
                    o["groupPostingRestricted"] = JsonValue.CreateBooleanValue(conv.GroupPostingRestricted);
                    o["groupSettingsLoaded"] = JsonValue.CreateBooleanValue(conv.GroupSettingsLoaded);

                    var msgs = new JsonArray();
                    int startIdx = Math.Max(0, conv.Messages.Count - 30);
                    for (int i = startIdx; i < conv.Messages.Count; i++)
                    {
                        var m = conv.Messages[i];
                        var mo = new JsonObject();
                        mo["id"] = JsonValue.CreateStringValue(m.Id ?? string.Empty);
                        mo["globalMessageId"] = JsonValue.CreateStringValue(m.GlobalMessageId ?? string.Empty);
                        mo["clientMsgId"] = JsonValue.CreateStringValue(m.ClientMessageId ?? string.Empty);
                        mo["senderUid"] = JsonValue.CreateStringValue(m.SenderUid ?? string.Empty);
                        mo["recipientUid"] = JsonValue.CreateStringValue(m.RecipientUid ?? string.Empty);
                        mo["sender"] = JsonValue.CreateStringValue(m.Sender ?? string.Empty);
                        mo["senderAvatarUrl"] = JsonValue.CreateStringValue(m.SenderAvatarUrl ?? string.Empty);
                        mo["status"] = JsonValue.CreateNumberValue(m.Status);
                        mo["at"] = JsonValue.CreateNumberValue(m.At);
                        mo["command"] = JsonValue.CreateNumberValue(m.Command);
                        mo["seenSent"] = JsonValue.CreateBooleanValue(m.SeenSent);
                        mo["receiptState"] = JsonValue.CreateNumberValue(m.ReceiptState);
                        mo["deliveryAvatarUrl"] = JsonValue.CreateStringValue(m.DeliveryAvatarUrl ?? string.Empty);
                        mo["text"] = JsonValue.CreateStringValue(m.Text ?? string.Empty);
                        mo["replyContent"] = JsonValue.CreateStringValue(m.ReplyContent ?? string.Empty);
                        mo["quoteMessage"] = JsonValue.CreateStringValue(m.QuoteMessage ?? string.Empty);
                        mo["quoteAttachment"] = JsonValue.CreateStringValue(m.QuoteAttachment ?? string.Empty);
                        mo["replyBlocked"] = JsonValue.CreateBooleanValue(m.ReplyBlocked);
                        mo["quotedSender"] = JsonValue.CreateStringValue(m.QuotedSender ?? string.Empty);
                        mo["quotedText"] = JsonValue.CreateStringValue(m.QuotedText ?? string.Empty);
                        mo["reactionIcon"] = JsonValue.CreateStringValue(m.ReactionIcon ?? string.Empty);
                        mo["isRecalled"] = JsonValue.CreateBooleanValue(m.IsRecalled);
                        mo["msgType"] = JsonValue.CreateStringValue(m.MsgType ?? string.Empty);
                        mo["stickerId"] = JsonValue.CreateStringValue(m.StickerId ?? string.Empty);
                        mo["fileName"] = JsonValue.CreateStringValue(m.FileName ?? string.Empty);
                        mo["mediaUrl"] = JsonValue.CreateStringValue(m.MediaUrl ?? string.Empty);
                        mo["mediaFullUrl"] = JsonValue.CreateStringValue(m.MediaFullUrl ?? string.Empty);
                        mo["hasLocation"] = JsonValue.CreateBooleanValue(m.HasLocation);
                        if (m.HasLocation)
                        {
                            mo["locationLatitude"] = JsonValue.CreateNumberValue(m.LocationLatitude.Value);
                            mo["locationLongitude"] = JsonValue.CreateNumberValue(m.LocationLongitude.Value);
                            mo["locationLabel"] = JsonValue.CreateStringValue(m.LocationLabel ?? string.Empty);
                        }
                        mo["pollId"] = JsonValue.CreateNumberValue(m.PollId);
                        mo["pollQuestion"] = JsonValue.CreateStringValue(m.PollQuestion ?? string.Empty);
                        mo["linkPreviewTitle"] = JsonValue.CreateStringValue(m.LinkPreviewTitle);
                        mo["linkPreviewDescription"] = JsonValue.CreateStringValue(m.LinkPreviewDescription);
                        mo["linkPreviewUrl"] = JsonValue.CreateStringValue(m.LinkPreviewUrl);
                        mo["linkPreviewImageUrl"] = JsonValue.CreateStringValue(m.LinkPreviewImageUrl);
                        mo["contactUid"] = JsonValue.CreateStringValue(m.ContactUid);
                        mo["contactName"] = JsonValue.CreateStringValue(m.ContactName);
                        mo["contactAvatarUrl"] = JsonValue.CreateStringValue(m.ContactAvatarUrl);
                        mo["contactQrUrl"] = JsonValue.CreateStringValue(m.ContactQrUrl);
                        mo["contactPhone"] = JsonValue.CreateStringValue(m.ContactPhone);
                        mo["fromMe"] = JsonValue.CreateBooleanValue(m.FromMe);
                        mo["showSender"] = JsonValue.CreateBooleanValue(m.ShowSender);
                        mo["ts"] = JsonValue.CreateNumberValue(m.Ts);
                        msgs.Add(mo);
                    }
                    o["msgs"] = msgs;
                    arr.Add(o);
                }

                var file = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFileAsync(
                    cacheFile, Windows.Storage.CreationCollisionOption.ReplaceExisting);
                await Windows.Storage.FileIO.WriteTextAsync(file, arr.Stringify());
            }
            catch { }
        }

        public static async Task ClearLocalCacheAsync()
        {
            _cachedDataUid = null;
            foreach (ZaloConversation conversation in Conversations)
            {
                conversation.Messages.Clear();
                conversation.SeenIds.Clear();
                conversation.LastText = string.Empty;
                conversation.LastTs = 0;
                conversation.Unread = 0;
            }
            Conversations.Clear();
            Groups.Clear();
            ByKey.Clear();
            PendingGroups.Clear();
            LoadingHistory.Clear();
            LoadedHistory.Clear();
            GroupsLoaded = false;
            GroupsLoadFailed = false;

            string cacheFile = CacheFileFor(Session);
            IStorageItem item = cacheFile == null ? null
                : await Windows.Storage.ApplicationData.Current.LocalFolder.TryGetItemAsync(cacheFile);
            if (item != null) await item.DeleteAsync();
            if (Session != null) _ = LoadGroupsAsync(Session);
        }

        public static async Task PreloadOfflineCacheAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)
                || string.Equals(_cachedDataUid, userId, StringComparison.Ordinal)) return;

            IsRestoringCache = true;
            try
            {
                // A different cached account must not see the previous account's in-memory rows.
                Conversations.Clear();
                Groups.Clear();
                ByKey.Clear();
                PendingGroups.Clear();
                LoadingHistory.Clear();
                LoadedHistory.Clear();
                Friends = new List<ZaloFriend>();
                FriendsLoaded = false;
                _friendNames.Clear();
                _friendAvatars.Clear();

                await LoadCachedConversationsAsync(userId);
                await LoadCachedFriendsAsync(userId);
                ArchivedConversationKeys = new HashSet<string>(
                    ZaloSettingsService.Current.GetArchivedConversationKeys(userId), StringComparer.Ordinal);
                _cachedDataUid = userId;
            }
            finally
            {
                IsRestoringCache = false;
                if (CacheRestored != null) CacheRestored();
            }
        }

        public static async Task StartAsync(ZaloSession session)
        {
            if (ReferenceEquals(Session, session) && Listener != null) return;

            Session = session;
            bool cacheAlreadyLoaded = session != null
                && string.Equals(_cachedDataUid, session.Uid, StringComparison.Ordinal);
            if (!cacheAlreadyLoaded)
            {
                Conversations.Clear();
                Groups.Clear();
                ByKey.Clear();
                PendingGroups.Clear();
                LoadingHistory.Clear();
                LoadedHistory.Clear();
                FriendsLoaded = false;
                Friends = new List<ZaloFriend>();
                _friendNames.Clear();
                _friendAvatars.Clear();
            }
            if (_dispatcher == null)
            {
                try
                {
                    _dispatcher = Window.Current?.Dispatcher ?? Windows.ApplicationModel.Core.CoreApplication.MainView?.CoreWindow?.Dispatcher;
                }
                catch { }
            }
            if (_dispatcher != null)
            {
                ZaloPhoneSync.Initialize(_dispatcher);
            }

            // Render local data before making any network request. The old unscoped cache is
            // cleanup only and must never hold the first screen behind storage/network work.
            if (!cacheAlreadyLoaded)
            {
                IsRestoringCache = true;
                try
                {
                    await LoadCachedConversationsAsync();
                    await LoadCachedFriendsAsync(session);
                    _cachedDataUid = session == null ? null : session.Uid;
                }
                catch { }
                finally
                {
                    IsRestoringCache = false;
                    if (CacheRestored != null) CacheRestored();
                }
            }

            ArchivedConversationKeys = new HashSet<string>(ZaloSettingsService.Current.GetArchivedConversationKeys(session?.Uid), StringComparer.Ordinal);

            Listener = new ZaloListener(session);
            Listener.MessageReceived += OnIncoming;
            Listener.TypingReceived += OnTyping;
            Listener.GroupEventReceived += OnGroupEvent;
            Listener.ReceiptReceived += OnReceiptReceived;
            Listener.ReactionReceived += OnReactionReceived;
            Listener.StatusChanged += OnStatus;

            if (!_resumeHooked)
            {
                _resumeHooked = true;
                Application.Current.Resuming += (s, e) =>
                {
                    ZaloListener l = Listener;
                    if (l != null) { var ignored = l.RestartAsync(); }
                };
            }

            ConnectionStatus = "Đang kết nối…";
            if (StatusChanged != null) StatusChanged();

            // Start the socket and refresh server-only state after the cached screen is visible.
            ZaloListener listener = Listener;
            _ = StartListenerInBackgroundAsync(listener);
            _ = RefreshStartupDataAsync(session);
            _ = LoadGroupsAsync(session);

            // Old account-agnostic data is no longer used; remove it in the background.
            _ = DeleteLegacyCacheAsync();
        }

        private static async Task StartListenerInBackgroundAsync(ZaloListener listener)
        {
            try
            {
                await listener.StartAsync();
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Listener startup failed: " + ex.Message);
            }
        }

        private static async Task RefreshStartupDataAsync(ZaloSession session)
        {
            try
            {
                HashSet<string> pinned = await ZaloMessaging.GetPinnedConversationKeysAsync(session, CancellationToken.None);
                if (pinned != null)
                {
                    PinnedConversationKeys = pinned;
                    foreach (ZaloConversation conversation in Conversations)
                        conversation.IsPinned = pinned.Contains(conversation.Key);
                    if (StatusChanged != null) StatusChanged();
                }
            }
            catch (Exception ex) { ZaloLog.Add("Pinned conversations unavailable: " + ex.Message); }

            try
            {
                HashSet<string> archived = await ZaloMessaging.GetArchivedConversationKeysAsync(session, CancellationToken.None);
                if (archived != null)
                {
                    ArchivedConversationKeys = archived;
                    ZaloSettingsService.Current.SetArchivedConversationKeys(session.Uid, archived.ToList());
                }
            }
            catch (Exception ex) { ZaloLog.Add("Archived conversations unavailable: " + ex.Message); }

            try
            {
                UnreadMarkedKeys = await ZaloMessaging.GetUnreadMarkKeysAsync(session, CancellationToken.None);
                foreach (ZaloConversation conversation in Conversations)
                    conversation.IsMarkedUnread = UnreadMarkedKeys.Contains(conversation.Key);
            }
            catch (Exception ex) { ZaloLog.Add("Unread marks unavailable: " + ex.Message); }

            try
            {
                MutedConversationKeys = await ZaloAuth.GetMutedConversationKeysAsync(session, CancellationToken.None);
                foreach (ZaloConversation conversation in Conversations)
                    conversation.IsMuted = MutedConversationKeys.Contains(conversation.Key);
            }
            catch (Exception ex) { ZaloLog.Add("Conversation mute state unavailable: " + ex.Message); }

            try
            {
                HiddenConversationKeys = await ZaloAuth.GetHiddenConversationKeysAsync(session, CancellationToken.None);
            }
            catch (Exception ex)
            {
                HiddenConversationKeys = new HashSet<string>(StringComparer.Ordinal);
                ZaloLog.Add("Hidden conversation state unavailable: " + ex.Message);
            }

            try
            {
                List<ZaloFriend> refreshedFriends = await ZaloAuth.GetFriendsAsync(session, CancellationToken.None);
                if (refreshedFriends != null)
                {
                    Friends = refreshedFriends;
                    FriendsLoaded = true;
                    UpdateFriendLookupCaches();
                    if (FriendsLoadChanged != null) FriendsLoadChanged();
                    await SaveCachedFriendsAsync();
                }
                ZaloLog.Add("Friends loaded: " + (Friends == null ? 0 : Friends.Count));
            }
            catch (Exception ex) { ZaloLog.Add("Friend list refresh failed; using account cache when available: " + ex.Message); }

            try
            {
                HashSet<string> onlineIds = await ZaloAuth.GetFriendOnlineIdsAsync(session, CancellationToken.None);
                foreach (ZaloFriend friend in Friends) friend.IsOnline = onlineIds.Contains(friend.UserId ?? string.Empty);
            }
            catch (Exception ex) { ZaloLog.Add("Friend presence unavailable: " + ex.Message); }

            try { await ZaloAuth.LoadMyProfileAsync(session, CancellationToken.None); }
            catch (Exception ex) { ZaloLog.Add("Profile load failed: " + ex.Message); }

            // The profile request runs after cached startup. Notify the shell so its name and
            // avatar can replace the temporary account-ID fallback without blocking launch.
            if (_dispatcher != null)
            {
                try
                {
                    await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        if (!ReferenceEquals(Session, session)) return;
                        Action handler = StatusChanged;
                        if (handler != null) handler();
                    });
                }
                catch { }
            }
        }

        public static void Reset()
        {
            _cachedDataUid = null;
            string cacheFile = CacheFileFor(Session);
            if (Listener != null) Listener.Stop();
            if (Session != null) Session.Http.ClearCookies();
            Listener = null;
            Session = null;
            CurrentChat = null;
            ByKey.Clear();
            PendingGroups.Clear();
            LoadingHistory.Clear();
            LoadedHistory.Clear();
            Conversations.Clear();
            Groups.Clear();
            GroupsLoaded = false;
            GroupsLoadFailed = false;
            NotifyGroupsLoadChanged();
            PinnedConversationKeys = new HashSet<string>(StringComparer.Ordinal);
            ArchivedConversationKeys = new HashSet<string>(StringComparer.Ordinal);
            Friends = new List<ZaloFriend>();
            _friendNames = new Dictionary<string, string>();
            _friendAvatars = new Dictionary<string, string>();
            LastTypingSentUtc.Clear();
            UnreadMarkedKeys = new HashSet<string>(StringComparer.Ordinal);
            MutedConversationKeys = new HashSet<string>(StringComparer.Ordinal);
            HiddenConversationKeys = new HashSet<string>(StringComparer.Ordinal);
            ConnectionStatus = string.Empty;
            if (cacheFile != null) try
            {
                var ignored = Windows.Storage.ApplicationData.Current.LocalFolder.TryGetItemAsync(cacheFile)
                    .ContinueWith(t => (t.Result as Windows.Storage.StorageFile)?.DeleteAsync());
            }
            catch { }
        }

        private static void OnStatus(string status)
        {
            var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ConnectionStatus = status;
                Action handler = StatusChanged;
                if (handler != null) handler();
            });
        }

        private static void OnIncoming(ZaloIncoming incoming)
        {
            var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try { Handle(incoming); }
                catch (Exception ex) { ZaloLog.Add("Could not show a message: " + ex.Message); }
            });
        }

        private static void OnTyping(ZaloTyping typing)
        {
            if (typing == null || string.IsNullOrWhiteSpace(typing.ThreadId) || _dispatcher == null) return;
            string key = (typing.IsGroup ? "g:" : "u:") + typing.ThreadId;
            var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                ZaloConversation conversation = Find(key);
                if (conversation == null) return;
                int revision = ++conversation.TypingRevision;
                conversation.IsTyping = true;
                Action<ZaloConversation> handler = TypingChanged;
                if (handler != null) handler(conversation);
                await Task.Delay(2600);
                if (conversation.TypingRevision != revision) return;
                conversation.IsTyping = false;
                Action<ZaloConversation> updatedHandler = TypingChanged;
                if (updatedHandler != null) updatedHandler(conversation);
            });
        }

        private static void OnReceiptReceived(JsonObject payload, bool isGroup)
        {
            if (payload == null || _dispatcher == null) return;
            var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                bool changed = false;
                JsonArray delivered = ArrayValue(payload, "delivereds");
                if (delivered != null)
                {
                    foreach (IJsonValue value in delivered)
                    {
                        if (value.ValueType != JsonValueType.Object) continue;
                        JsonObject receipt = value.GetObject();
                        string threadId = isGroup ? ZaloAuth.Str(receipt, "groupId") : FirstStringValue(receipt, "deliveredUids");
                        if (threadId.Length == 0) threadId = ZaloAuth.Str(receipt, "idTo");
                        if (threadId.Length == 0) continue;
                        ZaloConversation conversation = Find((isGroup ? "g:" : "u:") + threadId);
                        if (conversation == null) continue;

                        string[] seenUids = StringArrayValue(receipt, "seenUids");
                        string[] deliveredUids = StringArrayValue(receipt, "deliveredUids");
                        if (isGroup && Session != null)
                        {
                            seenUids = seenUids.Where(uid => uid != Session.Uid).ToArray();
                            deliveredUids = deliveredUids.Where(uid => uid != Session.Uid).ToArray();
                        }
                        int state = (!isGroup && ZaloAuth.Num(receipt, "seen") > 0) || seenUids.Length > 0 ? 2
                            : deliveredUids.Length > 0 ? 1 : 0;
                        string avatarUid = isGroup
                            ? (seenUids.FirstOrDefault() ?? deliveredUids.FirstOrDefault() ?? string.Empty)
                            : threadId;
                        changed |= ApplyReceiptToMessage(conversation, receipt, state, AvatarForUser(avatarUid));
                    }
                }

                JsonArray seen = ArrayValue(payload, isGroup ? "groupSeens" : "seens");
                if (seen != null)
                {
                    foreach (IJsonValue value in seen)
                    {
                        if (value.ValueType != JsonValueType.Object) continue;
                        JsonObject receipt = value.GetObject();
                        string threadId = isGroup ? ZaloAuth.Str(receipt, "groupId") : ZaloAuth.Str(receipt, "idTo");
                        if (threadId.Length == 0) continue;
                        ZaloConversation conversation = Find((isGroup ? "g:" : "u:") + threadId);
                        if (conversation == null) continue;
                        string avatarUid = isGroup
                            ? (StringArrayValue(receipt, "seenUids").FirstOrDefault() ?? string.Empty)
                            : threadId;
                        if (isGroup && Session != null && avatarUid == Session.Uid)
                            avatarUid = StringArrayValue(receipt, "seenUids").FirstOrDefault(uid => uid != Session.Uid) ?? string.Empty;
                        changed |= ApplyReceiptToMessage(conversation, receipt, 2, AvatarForUser(avatarUid));
                    }
                }

                if (changed)
                {
                    if (StatusChanged != null) StatusChanged();
                    _ = SaveCachedConversationsAsync();
                }
            });
        }

        private static void OnReactionReceived(JsonObject payload, bool ignoredGroupFlag)
        {
            if (payload == null || _dispatcher == null) return;
            IJsonValue userValues, groupValues;
            JsonArray userReactions = payload.TryGetValue("reacts", out userValues) && userValues.ValueType == JsonValueType.Array ? userValues.GetArray() : null;
            JsonArray groupReactions = payload.TryGetValue("reactGroups", out groupValues) && groupValues.ValueType == JsonValueType.Array ? groupValues.GetArray() : null;
            if ((userReactions == null || userReactions.Count == 0) && (groupReactions == null || groupReactions.Count == 0)) return;
            var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                bool changed = ApplyReactionArray(userReactions, false);
                changed |= ApplyReactionArray(groupReactions, true);
                if (changed) _ = SaveCachedConversationsAsync();
            });
        }

        private static bool ApplyReactionArray(JsonArray reactions, bool isGroup)
        {
            bool changed = false;
            if (reactions == null) return changed;
            foreach (IJsonValue value in reactions)
            {
                if (value.ValueType != JsonValueType.Object) continue;
                JsonObject reaction = value.GetObject();
                IJsonValue contentValue;
                JsonObject content = reaction.TryGetValue("content", out contentValue) && contentValue.ValueType == JsonValueType.Object
                    ? contentValue.GetObject() : contentValue != null && contentValue.ValueType == JsonValueType.String
                        ? ZaloJson.ParseObject(contentValue.GetString()) : null;
                if (content == null) continue;
                string sender = ZaloAuth.Str(reaction, "uidFrom");
                string threadId = isGroup || sender == "0" ? ZaloAuth.Str(reaction, "idTo") : sender;
                if (string.IsNullOrEmpty(threadId) || threadId == "0") continue;
                ZaloConversation conversation = Find((isGroup ? "g:" : "u:") + threadId);
                if (conversation == null) continue;
                string icon = ZaloAuth.Str(content, "rIcon");
                IJsonValue targetValue;
                JsonArray targets = content.TryGetValue("rMsg", out targetValue) && targetValue.ValueType == JsonValueType.Array
                    ? targetValue.GetArray() : null;
                if (targets == null && targetValue != null && targetValue.ValueType == JsonValueType.String)
                    JsonArray.TryParse(targetValue.GetString(), out targets);
                if (targets == null || icon.Length == 0) continue;
                foreach (IJsonValue targetItem in targets)
                {
                    if (targetItem.ValueType != JsonValueType.Object) continue;
                    JsonObject target = targetItem.GetObject();
                    string globalId = ZaloAuth.Str(target, "gMsgID"), clientId = ZaloAuth.Str(target, "cMsgID");
                    ZaloChatMessage message = conversation.Messages.FirstOrDefault(m =>
                        (!string.IsNullOrEmpty(globalId) && m.GlobalMessageId == globalId)
                        || (!string.IsNullOrEmpty(clientId) && m.ClientMessageId == clientId));
                    if (message == null) continue;
                    message.ReactionIcon = icon;
                    changed = true;
                }
            }
            return changed;
        }
        private static bool ApplyReceiptToMessage(ZaloConversation conversation, JsonObject receipt, int state, string avatarUrl)
        {
            string messageId = ZaloAuth.Str(receipt, "msgId");
            string realMessageId = ZaloAuth.Str(receipt, "realMsgId");
            if (messageId.Length == 0 && realMessageId.Length == 0) return false;
            ZaloChatMessage message = conversation.Messages.FirstOrDefault(item => item.FromMe
                && ((!string.IsNullOrEmpty(messageId) && (item.GlobalMessageId == messageId || item.Id == messageId))
                    || (!string.IsNullOrEmpty(realMessageId) && (item.GlobalMessageId == realMessageId || item.Id == realMessageId))));
            if (message == null) return false;
            int previousState = message.ReceiptState;
            string previousAvatar = message.DeliveryAvatarUrl;
            message.SetDeliveryReceipt(state, avatarUrl);
            return previousState != message.ReceiptState || previousAvatar != message.DeliveryAvatarUrl;
        }

        private static string AvatarForUser(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return string.Empty;
            string avatar;
            if (_friendAvatars.TryGetValue(userId, out avatar)) return avatar ?? string.Empty;
            ZaloFriend friend = Friends == null ? null : Friends.FirstOrDefault(item => item.UserId == userId);
            if (friend != null && !string.IsNullOrWhiteSpace(friend.Avatar)) return friend.Avatar;
            ZaloConversation conversation = Find("u:" + userId);
            return conversation == null ? string.Empty : conversation.AvatarUrl ?? string.Empty;
        }

        private static JsonArray ArrayValue(JsonObject source, string key)
        {
            IJsonValue value;
            return source != null && source.TryGetValue(key, out value) && value.ValueType == JsonValueType.Array
                ? value.GetArray() : null;
        }

        private static string[] StringArrayValue(JsonObject source, string key)
        {
            JsonArray array = ArrayValue(source, key);
            if (array == null) return new string[0];
            return array.Where(item => item.ValueType == JsonValueType.String)
                .Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
        }

        private static string FirstStringValue(JsonObject source, string key)
        {
            return StringArrayValue(source, key).FirstOrDefault() ?? string.Empty;
        }

        private static void OnGroupEvent(ZaloGroupEvent groupEvent)
        {
            if (groupEvent == null || string.IsNullOrWhiteSpace(groupEvent.GroupId) || _dispatcher == null) return;
            var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    string member = string.IsNullOrWhiteSpace(groupEvent.MemberNames) ? "Thành viên" : groupEvent.MemberNames;
                    string text;
                    switch (groupEvent.Act)
                    {
                        case "join": text = member + " đã tham gia nhóm"; break;
                        case "leave": text = member + " đã rời nhóm"; break;
                        case "remove_member": text = member + " đã bị xóa khỏi nhóm"; break;
                        case "block_member": text = member + " đã bị chặn khỏi nhóm"; break;
                        case "add_admin": text = member + " được bổ nhiệm làm phó nhóm"; break;
                        case "remove_admin": text = member + " không còn là phó nhóm"; break;
                        case "update": text = "Thông tin nhóm đã được cập nhật"; break;
                        case "update_setting": text = "Cài đặt nhóm đã được cập nhật"; break;
                        case "update_avatar": text = "Ảnh đại diện nhóm đã được thay đổi"; break;
                        case "new_link": text = "Liên kết mời tham gia nhóm đã được tạo"; break;
                        default: return;
                    }

                    ZaloConversation conversation = GetOrCreate(groupEvent.GroupId, true);
                    if (!string.IsNullOrWhiteSpace(groupEvent.GroupName) && groupEvent.Act == "update")
                        conversation.Name = groupEvent.GroupName;
                    string id = "group-event-" + (string.IsNullOrWhiteSpace(groupEvent.EventId)
                        ? groupEvent.Timestamp + "-" + groupEvent.Act + "-" + groupEvent.MemberNames
                        : groupEvent.EventId);
                    var message = new ZaloChatMessage
                    {
                        Id = id,
                        GlobalMessageId = id,
                        Sender = string.Empty,
                        Text = text,
                        MsgType = "system.group_event",
                        Ts = groupEvent.Timestamp
                    };
                    AddMessage(conversation, message, false);
                }
                catch (Exception ex) { ZaloLog.Add("Could not show group event: " + ex.Message); }
            });
        }

        public static ZaloConversation Find(string key)
        {
            ZaloConversation conversation;
            return key != null && ByKey.TryGetValue(key, out conversation) ? conversation : null;
        }

        public static ZaloConversation GetOrCreate(string threadId, bool isGroup, bool includeInConversationList = true)
        {
            string key = (isGroup ? "g:" : "u:") + threadId;
            ZaloConversation conversation;
            if (ByKey.TryGetValue(key, out conversation))
            {
                if (includeInConversationList && !Conversations.Contains(conversation)) Conversations.Add(conversation);
                if (isGroup && !Groups.Contains(conversation)) Groups.Add(conversation);
                return conversation;
            }

            string friendName;
            conversation = new ZaloConversation
            {
                Key = key,
                ThreadId = threadId,
                IsGroup = isGroup,
                IsPinned = PinnedConversationKeys.Contains(key),
                IsMarkedUnread = UnreadMarkedKeys.Contains(key),
                IsMuted = MutedConversationKeys.Contains(key),
                Name = isGroup
                    ? "Nhóm " + (threadId.Length > 4 ? threadId.Substring(threadId.Length - 4) : threadId)
                    : (_friendNames.TryGetValue(threadId, out friendName) ? friendName : threadId)
            };
            string avatar;
            if (!isGroup && _friendAvatars.TryGetValue(threadId, out avatar)) conversation.AvatarUrl = avatar;
            ByKey[key] = conversation;
            if (includeInConversationList) Conversations.Add(conversation);
            if (isGroup) Groups.Add(conversation);
            if (isGroup) PendingGroups.Add(threadId);
            return conversation;
        }

        private static async Task LoadGroupsAsync(ZaloSession session)
        {
            GroupsLoaded = false;
            GroupsLoadFailed = false;
            NotifyGroupsLoadChanged();
            try
            {
                List<string> ids;
                try { ids = await ZaloMessaging.GetGroupIdsAsync(session, CancellationToken.None); }
                catch (Exception ex)
                {
                    ids = new List<string>();
                    ZaloLog.Add("Group directory unavailable; using cached conversation groups: " + ex.Message);
                }
                foreach (ZaloConversation cachedGroup in Conversations.Where(item => item.IsGroup))
                    if (!ids.Contains(cachedGroup.ThreadId)) ids.Add(cachedGroup.ThreadId);
                Dictionary<string, JsonObject> details = ids.Count == 0
                    ? new Dictionary<string, JsonObject>(StringComparer.Ordinal)
                    : await ZaloMessaging.GetGroupInfoMapAsync(session, ids, CancellationToken.None);

                await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    var currentIds = new HashSet<string>(ids, StringComparer.Ordinal);
                    foreach (ZaloConversation stale in Groups.Where(g => !currentIds.Contains(g.ThreadId)).ToList())
                        Groups.Remove(stale);
                    foreach (string id in ids)
                    {
                        ZaloConversation group = GetOrCreate(id, true, false);
                        JsonObject info;
                        if (!details.TryGetValue(id, out info)) continue;
                        string name = ZaloAuth.Str(info, "name");
                        string avatar = ZaloAuth.Str(info, "fullAvt");
                        if (string.IsNullOrWhiteSpace(avatar)) avatar = ZaloAuth.Str(info, "avt");
                        if (string.IsNullOrWhiteSpace(avatar)) avatar = ZaloAuth.Str(info, "avatarUrl");
                        if (string.IsNullOrWhiteSpace(avatar)) avatar = ZaloAuth.Str(info, "avatar");
                        if (!string.IsNullOrWhiteSpace(name)) group.Name = name;
                        if (!string.IsNullOrWhiteSpace(avatar)) group.AvatarUrl = avatar;
                        int memberCount = (int)ZaloAuth.Num(info, "totalMember");
                        if (memberCount > 0) group.GroupMemberCount = memberCount;
                        UpdateGroupMessagePermission(group, info, session.Uid);
                    }
                    GroupsLoaded = true;
                    GroupsLoadFailed = false;
                    NotifyGroupsLoadChanged();
                });
                CoreDispatcher dispatcher = _dispatcher;
                if (dispatcher != null)
                    _ = dispatcher.RunAsync(CoreDispatcherPriority.Low, () => { var ignored = SaveCachedConversationsAsync(); });
                ZaloLog.Add("Groups loaded: " + ids.Count);
            }
            catch (Exception ex)
            {
                await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    GroupsLoaded = true;
                    GroupsLoadFailed = true;
                    NotifyGroupsLoadChanged();
                });
                ZaloLog.Add("Group list failed: " + ex.Message);
            }
        }

        private static void UpdateGroupMessagePermission(ZaloConversation group, JsonObject info, string userId)
        {
            if (group == null || info == null) return;
            IJsonValue settingValue;
            if (!info.TryGetValue("setting", out settingValue) || settingValue.ValueType != JsonValueType.Object)
                return;

            JsonObject setting = settingValue.GetObject();
            bool locked = ZaloAuth.Num(setting, "lockSendMsg") == 1;
            bool isAdmin = !string.IsNullOrWhiteSpace(userId)
                && string.Equals(ZaloAuth.Str(info, "creatorId"), userId, StringComparison.Ordinal);
            IJsonValue adminValue;
            if (!isAdmin && info.TryGetValue("adminIds", out adminValue) && adminValue.ValueType == JsonValueType.Array)
                isAdmin = adminValue.GetArray().Any(value => value.ValueType == JsonValueType.String
                    && string.Equals(value.GetString(), userId, StringComparison.Ordinal));
            group.UpdateGroupMessagePermission(true, locked, isAdmin);
        }

        private static void NotifyGroupsLoadChanged()
        {
            Action handler = GroupsLoadChanged;
            if (handler != null) handler();
        }

        public static async Task LoadHistoryAsync(ZaloConversation conversation, CancellationToken ct)
        {
            if (conversation == null || Session == null || conversation.ThreadId == Session.Uid) return;
            string key = conversation.Key;
            if (string.IsNullOrEmpty(key) || LoadedHistory.Contains(key) || !LoadingHistory.Add(key)) return;

            try
            {
                List<ZaloChatMessage> history = await ZaloMessaging.GetHistoryAsync(
                    Session, conversation.ThreadId, conversation.IsGroup, 50, ct);
                await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    conversation.HistoryLoadedCount = history.Count;
                    conversation.HasMoreHistory = conversation.IsGroup
                        ? history.Count >= 50
                        : history.Count > 0 || conversation.Messages.Count > 0;
                    if (history.Count > 0 && !Conversations.Contains(conversation))
                        Conversations.Add(conversation);
                    foreach (ZaloChatMessage recall in history.Where(m => m.IsRecallEvent))
                    {
                        ZaloChatMessage target = conversation.Messages.Concat(history.Where(m => !m.IsRecallEvent))
                            .FirstOrDefault(m => (!string.IsNullOrEmpty(recall.GlobalMessageId) && m.GlobalMessageId == recall.GlobalMessageId)
                                || (!string.IsNullOrEmpty(recall.ClientMessageId) && m.ClientMessageId == recall.ClientMessageId));
                        if (target != null) MarkRecalled(conversation, target);
                    }
                    foreach (ZaloChatMessage message in history.Where(m => !m.IsRecallEvent)) AddMessage(conversation, message, false);
                    if (conversation.Messages.Count > 0 && (string.IsNullOrWhiteSpace(conversation.LastText) || conversation.LastTs <= 0))
                    {
                        var latest = conversation.Messages.OrderBy(m => m.Ts).LastOrDefault();
                        if (latest != null) UpdateConversationPreviewFromMessage(conversation, latest);
                    }
                    LoadedHistory.Add(key);
                });
            }
            catch (Exception ex)
            {
                ZaloLog.Add("History load failed: " + ex.Message);
            }
            finally
            {
                LoadingHistory.Remove(key);
            }
        }

        public static async Task LoadOlderGroupHistoryAsync(ZaloConversation conversation, CancellationToken ct)
        {
            if (conversation == null || !conversation.IsGroup || Session == null || !conversation.HasMoreHistory) return;
            string key = conversation.Key;
            if (string.IsNullOrEmpty(key) || !LoadingHistory.Add(key)) return;
            int targetCount = Math.Min(500, Math.Max(50, conversation.HistoryLoadedCount) + 50);
            try
            {
                List<ZaloChatMessage> history = await ZaloMessaging.GetHistoryAsync(
                    Session, conversation.ThreadId, true, targetCount, ct);
                await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    foreach (ZaloChatMessage recall in history.Where(m => m.IsRecallEvent))
                    {
                        ZaloChatMessage target = conversation.Messages.Concat(history.Where(m => !m.IsRecallEvent))
                            .FirstOrDefault(m => (!string.IsNullOrEmpty(recall.GlobalMessageId) && m.GlobalMessageId == recall.GlobalMessageId)
                                || (!string.IsNullOrEmpty(recall.ClientMessageId) && m.ClientMessageId == recall.ClientMessageId));
                        if (target != null) MarkRecalled(conversation, target);
                    }
                    foreach (ZaloChatMessage message in history.Where(m => !m.IsRecallEvent)) AddMessage(conversation, message, false);
                    conversation.HistoryLoadedCount = targetCount;
                    conversation.HasMoreHistory = targetCount < 500 && history.Count >= targetCount;
                });
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Older group history load failed: " + ex.Message);
                throw;
            }
            finally
            {
                LoadingHistory.Remove(key);
            }
        }

        public static async Task<int> LoadOlderDirectHistoryAsync(ZaloConversation conversation, CancellationToken ct)
        {
            if (conversation == null || conversation.IsGroup || Session == null || !conversation.HasMoreHistory) return 0;
            if (Listener == null) throw new InvalidOperationException("Kết nối tin nhắn chưa sẵn sàng.");
            string key = conversation.Key;
            if (string.IsNullOrEmpty(key) || !LoadingHistory.Add(key)) return 0;
            try
            {
                // The protocol's cmd=510 cursor covers personal messages across conversations,
                // so continue from the oldest known personal message rather than this chat alone.
                ZaloChatMessage oldest = Conversations
                    .Where(item => !item.IsGroup)
                    .SelectMany(item => item.Messages)
                    .Where(message => !string.IsNullOrWhiteSpace(message.Id))
                    .OrderBy(message => message.Ts)
                    .FirstOrDefault();
                if (oldest == null) return 0;

                int received = await Listener.RequestOlderMessagesAsync(false, oldest.Id, ct);
                await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    foreach (ZaloConversation directConversation in Conversations.Where(item => !item.IsGroup))
                    {
                        directConversation.HistoryLoadedCount += received;
                        directConversation.HasMoreHistory = received > 0;
                    }
                });
                return received;
            }
            finally
            {
                LoadingHistory.Remove(key);
            }
        }

        public static async Task MarkConversationSeenAsync(ZaloConversation conversation)
        {
            if (conversation == null || Session == null) return;
            conversation.Unread = 0;
            bool changed = false;
            if (conversation.IsMarkedUnread)
            {
                try
                {
                    await ZaloMessaging.RemoveUnreadMarkAsync(Session, conversation, CancellationToken.None);
                    conversation.IsMarkedUnread = false;
                    UnreadMarkedKeys.Remove(conversation.Key);
                    changed = true;
                }
                catch (Exception ex) { ZaloLog.Add("Remove unread mark failed: " + ex.Message); }
            }
            Action statusHandler = StatusChanged;
            if (statusHandler != null) statusHandler();
            List<ZaloChatMessage> pending = conversation.Messages
                .Where(m => !m.FromMe && !m.SeenSent && m.CanSendSeen)
                .OrderBy(m => m.Ts).Take(50).ToList();
            if (pending.Count == 0)
            {
                if (changed || conversation.Unread == 0) await SaveCachedConversationsAsync();
                return;
            }
            foreach (ZaloChatMessage message in pending) message.SeenSent = true;
            try
            {
                await ZaloMessaging.MarkMessagesSeenAsync(Session, conversation, pending, CancellationToken.None);
                await SaveCachedConversationsAsync();
            }
            catch (Exception ex)
            {
                foreach (ZaloChatMessage message in pending) message.SeenSent = false;
                ZaloLog.Add("Seen event failed: " + ex.Message);
            }
        }

        private static void Handle(ZaloIncoming incoming)
        {
            JsonObject m = incoming.Msg;

            IJsonValue content;
            if (m.TryGetValue("content", out content) && content.ValueType == JsonValueType.Object
                && content.GetObject().ContainsKey("deleteMsg"))
            {
                HandleUndo(incoming, content.GetObject());
                return;
            }

            string uidFrom = ZaloAuth.Str(m, "uidFrom");
            string idTo = ZaloAuth.Str(m, "idTo");
            bool isSelf = uidFrom == "0" || (Session != null && !string.IsNullOrEmpty(Session.Uid) && uidFrom == Session.Uid);
            string threadId = incoming.IsGroup ? idTo : (isSelf ? idTo : uidFrom);
            if (threadId.Length == 0 || threadId == "0") return;

            long ts;
            if (!long.TryParse(ZaloAuth.Str(m, "ts"), out ts) || ts <= 0) ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            string senderName = isSelf ? "Tôi" : ZaloAuth.Str(m, "dName");
            if (senderName.Length == 0) senderName = "Zalo";

            ZaloConversation conversation = GetOrCreate(threadId, incoming.IsGroup);
            if (!incoming.IsGroup && !isSelf && !_friendNames.ContainsKey(threadId) && senderName != "Zalo")
                conversation.Name = senderName;

            var chatMessage = new ZaloChatMessage
            {
                Id = ZaloAuth.Str(m, "msgId"),
                GlobalMessageId = ZaloAuth.Str(m, "msgId"),
                ClientMessageId = ZaloAuth.Str(m, "cliMsgId"),
                SenderUid = uidFrom == "0" ? Session?.Uid : uidFrom,
                RecipientUid = idTo == "0" ? Session?.Uid : idTo,
                Sender = senderName,
                SenderAvatarBrush = ZaloAvatar.ColorFor(senderName),
                SenderAvatarUrl = !incoming.IsGroup && !isSelf ? conversation.AvatarUrl : string.Empty,
                Text = ZaloFormat.Describe(m),
                ReplyContent = ZaloFormat.PlainContent(m),
                MsgType = ZaloAuth.Str(m, "msgType"),
                StickerId = ZaloFormat.StickerId(m),
                FileName = ZaloFormat.FileName(m),
                MediaUrl = ZaloFormat.MediaUrl(m),
                MediaFullUrl = ZaloFormat.MediaFullUrl(m),
                FromMe = isSelf,
                ShowSender = incoming.IsGroup && !isSelf,
                Status = m.ContainsKey("st") ? 0 : -1,
                At = m.ContainsKey("at") ? 0 : -1,
                Command = m.ContainsKey("cmd") ? 0 : -1,
                Ts = ts
            };
            ZaloFormat.ReadQuote(m, chatMessage);
            ZaloFormat.ReadReplyPayload(m, chatMessage, incoming.IsGroup);
            ZaloFormat.ReadLinkPreview(m, chatMessage);
            ZaloFormat.ReadLocation(m, chatMessage);
            ZaloFormat.ReadPoll(m, chatMessage);
            ZaloFormat.ReadRecommendedLinkPreview(m, chatMessage);
            ZaloFormat.ReadContactCard(m, chatMessage);
            AddMessage(conversation, chatMessage, !incoming.IsOld && !isSelf);
            if (!incoming.IsOld) _ = ResolveLinkPreviewAsync(chatMessage);
            if (ReferenceEquals(conversation, CurrentChat) && !isSelf)
                _ = MarkConversationSeenAsync(conversation);
        }

        private static void HandleUndo(ZaloIncoming incoming, JsonObject undo)
        {
            JsonObject source = incoming == null ? null : incoming.Msg;
            if (source == null || undo == null) return;
            string fromUid = ZaloAuth.Str(source, "uidFrom");
            string toId = ZaloAuth.Str(source, "idTo");
            bool isSelf = fromUid == "0" || (Session != null && fromUid == Session.Uid);
            string threadId = incoming.IsGroup ? toId : (isSelf ? toId : fromUid);
            if (string.IsNullOrWhiteSpace(threadId) || threadId == "0") return;

            ZaloConversation conversation = Find((incoming.IsGroup ? "g:" : "u:") + threadId);
            if (conversation == null) return;
            string globalId = ZaloAuth.Str(undo, "globalMsgId");
            string clientId = ZaloAuth.Str(undo, "cliMsgId");
            ZaloChatMessage message = conversation.Messages.FirstOrDefault(m =>
                (!string.IsNullOrEmpty(globalId) && m.GlobalMessageId == globalId)
                || (!string.IsNullOrEmpty(clientId) && m.ClientMessageId == clientId));
            if (message == null) return;
            MarkRecalled(conversation, message);
            var ignored = SaveCachedConversationsAsync();
        }

        private static void MarkRecalled(ZaloConversation conversation, ZaloChatMessage message)
        {
            message.IsRecalled = true;
            message.Text = "Tin nhắn đã được thu hồi";
            message.MsgType = "chat.recalled";
            message.MediaUrl = string.Empty;
            message.FileName = string.Empty;
            message.StickerId = string.Empty;
            message.ReactionIcon = string.Empty;
            if (conversation.Messages.Count > 0 && ReferenceEquals(conversation.Messages[conversation.Messages.Count - 1], message))
                conversation.LastText = conversation.IsGroup && !message.FromMe
                    ? message.Sender + ": " + message.Text : message.Text;
        }

        private static void AddMessage(ZaloConversation conversation, ZaloChatMessage message, bool countUnread)
        {
            if (!string.IsNullOrEmpty(message.Id) && !conversation.SeenIds.Add(message.Id))
            {
                // History may enrich a lightweight message restored from disk. Preserve the
                // existing row/scroll position while replacing old generic contact placeholders
                // with the link/contact payload now available from the server.
                ZaloChatMessage existing = conversation.Messages.FirstOrDefault(item => item.Id == message.Id);
                if (existing != null)
                {
                    if (message.HasLinkPreview && !existing.HasLinkPreview)
                        existing.SetLinkPreview(message.LinkPreviewTitle, message.LinkPreviewDescription,
                            message.LinkPreviewUrl, message.LinkPreviewImageUrl);
                    if (message.HasContactCard && !existing.HasContactCard)
                        existing.SetContactCard(message.ContactUid, message.ContactName, message.ContactAvatarUrl,
                            message.ContactQrUrl, message.ContactPhone);
                    if (existing.Text == "[Danh thiếp]" && message.Text != "[Danh thiếp]")
                        existing.Text = message.Text;
                }
                if (string.IsNullOrWhiteSpace(conversation.LastText) || conversation.LastTs <= 0 || message.Ts >= conversation.LastTs)
                {
                    UpdateConversationPreviewFromMessage(conversation, message);
                }
                return;
            }

            int index = conversation.Messages.Count;
            while (index > 0 && conversation.Messages[index - 1].Ts > message.Ts) index--;
            conversation.Messages.Insert(index, message);

            if (message.Ts >= conversation.LastTs || string.IsNullOrWhiteSpace(conversation.LastText))
            {
                UpdateConversationPreviewFromMessage(conversation, message);
            }
            if (countUnread && !ReferenceEquals(conversation, CurrentChat)) conversation.Unread++;
            if (countUnread && !ReferenceEquals(conversation, CurrentChat) && !conversation.IsMuted && ZaloSettingsService.Current.IsSoundEnabled)
                ZaloAudioService.Current.PlayMessageSent();

            if (countUnread)
            {
                Action statusHandler = StatusChanged;
                if (statusHandler != null) statusHandler();
            }

            if (message.IsSticker && !string.IsNullOrEmpty(message.StickerId))
                _ = ResolveStickerUrlAsync(message);

            ScheduleBatchWork();
        }

        private static async Task ResolveStickerUrlAsync(ZaloChatMessage message)
        {
            string stickerId = message.StickerId;
            string cachedUrl;
            lock (StickerUrlLock)
            {
                if (StickerUrlCache.TryGetValue(stickerId, out cachedUrl))
                {
                    message.MediaUrl = cachedUrl;
                    return;
                }
                if (!LoadingStickerUrls.Add(stickerId)) return;
            }

            try
            {
                string imageUrl = await ZaloMessaging.GetStickerImageUrlAsync(Session, stickerId, CancellationToken.None);
                Uri uri;
                if (Uri.TryCreate(imageUrl, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                {
                    lock (StickerUrlLock) StickerUrlCache[stickerId] = imageUrl;
                    await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        foreach (ZaloConversation conversation in Conversations)
                            foreach (ZaloChatMessage sticker in conversation.Messages.Where(m => m.StickerId == stickerId))
                                sticker.MediaUrl = imageUrl;
                        message.MediaUrl = imageUrl;
                    });
                }
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Sticker image lookup failed: " + ex.Message);
            }
            finally
            {
                lock (StickerUrlLock) LoadingStickerUrls.Remove(stickerId);
            }
        }

        // Sorting the list and looking up group names is done once after a burst of messages.
        private static async void ScheduleBatchWork()
        {
            if (_batchScheduled) return;
            _batchScheduled = true;
            await Task.Delay(400);
            _batchScheduled = false;

            SortConversations();

            await ResolveGroupNamesAsync();
            var ignored = SaveCachedConversationsAsync();
        }

        private static async Task ResolveGroupNamesAsync()
        {
            if (_resolvingGroups || PendingGroups.Count == 0 || Session == null) return;
            _resolvingGroups = true;
            try
            {
                var ids = PendingGroups.ToList();
                PendingGroups.Clear();
                Dictionary<string, JsonObject> details = await ZaloMessaging.GetGroupInfoMapAsync(Session, ids, CancellationToken.None);
                foreach (KeyValuePair<string, JsonObject> pair in details)
                {
                    ZaloConversation conversation = Find("g:" + pair.Key);
                    if (conversation == null) continue;
                    string name = ZaloAuth.Str(pair.Value, "name");
                    string avatar = ZaloAuth.Str(pair.Value, "avt");
                    if (string.IsNullOrWhiteSpace(avatar)) avatar = ZaloAuth.Str(pair.Value, "fullAvt");
                    if (string.IsNullOrWhiteSpace(avatar)) avatar = ZaloAuth.Str(pair.Value, "avatar");
                    if (!string.IsNullOrWhiteSpace(name)) conversation.Name = name;
                    if (!string.IsNullOrWhiteSpace(avatar)) conversation.AvatarUrl = avatar;
                }
                CoreDispatcher dispatcher = _dispatcher;
                if (dispatcher != null)
                    _ = dispatcher.RunAsync(CoreDispatcherPriority.Low, () => { var ignored = SaveCachedConversationsAsync(); });
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Group names failed: " + ex.Message);
            }
            finally
            {
                _resolvingGroups = false;
            }
        }

        public static ZaloConversation OpenFriend(ZaloFriend friend)
        {
            ZaloConversation conversation = GetOrCreate(friend.UserId, false, false);
            if (friend.Name.Length > 0) conversation.Name = friend.Name;
            return conversation;
        }

        public static void ResolveRecentLinkPreviews(ZaloConversation conversation)
        {
            if (conversation == null) return;
            foreach (ZaloChatMessage message in conversation.Messages.Reverse()
                .Where(m => (!m.HasLinkPreview && ZaloFormat.FirstHttpUrl(m.Text).Length > 0)
                    || (m.MsgType == "chat.recommended" && m.HasLinkPreview && m.LinkPreviewImage == null)).Take(5))
                _ = ResolveLinkPreviewAsync(message);
        }

        private static async Task ResolveLinkPreviewAsync(ZaloChatMessage message)
        {
            if (message == null || Session == null || message.HasVoice || message.HasVideo || message.HasFile
                || message.HasMedia || message.MsgType == "chat.gif" || message.MsgType == "chat.photo"
                || message.MsgType == "chat.sticker" || message.MsgType == "5") return;
            string link = message.HasLinkPreview ? message.LinkPreviewUrl : ZaloFormat.FirstHttpUrl(message.Text);
            if (link.Length == 0) return;
            if (link.Contains("dlfl.vn") || link.Contains("zadn.vn")) return;
            if (message.HasLinkPreview && message.LinkPreviewImage != null
                && message.LinkPreviewTitle != "Cộng đồng Zalo" && message.LinkPreviewTitle != "Liên kết được chia sẻ") return;
            JsonObject preview;
            bool cached;
            lock (LinkPreviewLock)
            {
                cached = LinkPreviewCache.TryGetValue(link, out preview);
                if (!cached && !LoadingLinkPreviews.Add(link)) return;
            }

            if (cached)
            {
                await ApplyLinkPreviewAsync(message, preview);
                return;
            }

            try
            {
                preview = await ZaloMessaging.ParseLinkAsync(Session, link, CancellationToken.None);
                if (preview == null) return;
                string title = ZaloAuth.Str(preview, "title");
                string description = ZaloAuth.Str(preview, "desc");
                string href = ZaloAuth.Str(preview, "href");
                string thumb = ZaloAuth.Str(preview, "thumb");
                if (title.Length == 0 && href.Length == 0) return;
                var normalized = new JsonObject();
                normalized["title"] = JsonValue.CreateStringValue(title);
                normalized["desc"] = JsonValue.CreateStringValue(description);
                normalized["href"] = JsonValue.CreateStringValue(href.Length == 0 ? link : href);
                normalized["thumb"] = JsonValue.CreateStringValue(thumb);
                lock (LinkPreviewLock) LinkPreviewCache[link] = normalized;
                await ApplyLinkPreviewAsync(message, normalized);
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Link preview failed: " + ex.Message);
            }
            finally
            {
                lock (LinkPreviewLock) LoadingLinkPreviews.Remove(link);
            }
        }

        private static async Task ApplyLinkPreviewAsync(ZaloChatMessage message, JsonObject preview)
        {
            string title = ZaloAuth.Str(preview, "title");
            string description = ZaloAuth.Str(preview, "desc");
            string href = ZaloAuth.Str(preview, "href");
            string thumb = ZaloAuth.Str(preview, "thumb");
            Action apply = () => message.SetLinkPreview(title, description, href, thumb);
            CoreDispatcher dispatcher = _dispatcher;
            if (dispatcher != null && !dispatcher.HasThreadAccess)
                await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => apply());
            else apply();
        }

        private static void EnsureCanSendMessage(ZaloConversation conversation)
        {
            if (conversation == null || !conversation.IsGroup) return;
            if (!conversation.GroupSettingsLoaded)
                throw new InvalidOperationException("Chưa xác minh được quyền gửi tin nhắn của nhóm. Hãy mở lại nhóm và thử lại.");
            if (conversation.GroupPostingRestricted)
                throw new InvalidOperationException("Chỉ trưởng và phó nhóm được gửi tin nhắn trong nhóm.");
        }

        public static async Task SendAsync(ZaloConversation conversation, string text, ZaloChatMessage quote = null)
        {
            EnsureCanSendMessage(conversation);
            long localTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string localId = "pending_" + localTs;
            var outgoing = new ZaloChatMessage
            {
                Id = localId,
                GlobalMessageId = localId,
                ClientMessageId = localTs.ToString(),
                SenderUid = Session?.Uid,
                Sender = "Tôi",
                Text = text,
                ReplyContent = text,
                QuoteMessage = text,
                QuotedSender = quote == null ? string.Empty : quote.Sender,
                QuotedText = quote == null ? string.Empty : quote.Text,
                FromMe = true,
                Ts = localTs
            };
            if (!Conversations.Contains(conversation)) Conversations.Add(conversation);
            AddMessage(conversation, outgoing, false);

            try
            {
                ZaloMessaging.ZaloSentText sent = await ZaloMessaging.SendTextAsync(
                    Session, conversation.ThreadId, conversation.IsGroup, text, quote, CancellationToken.None);
                outgoing.Id = sent.MessageId;
                outgoing.GlobalMessageId = sent.MessageId;
                outgoing.ClientMessageId = sent.ClientMessageId;
                _ = ResolveLinkPreviewAsync(outgoing);
                if (ZaloSettingsService.Current.IsSoundEnabled) ZaloAudioService.Current.PlayMessageSent();
                var ignored = SaveCachedConversationsAsync();
            }
            catch (Exception)
            {
                throw;
            }
        }

        public static async Task SendContactCardAsync(ZaloConversation conversation, ZaloFriend contact)
        {
            if (conversation == null || contact == null) throw new InvalidOperationException("Chưa chọn cuộc trò chuyện hoặc liên hệ.");
            EnsureCanSendMessage(conversation);
            ZaloContactCardSendResult sent = await ZaloMessaging.SendContactCardAsync(
                Session, conversation, contact, CancellationToken.None);
            if (!Conversations.Contains(conversation)) Conversations.Add(conversation);
            var message = new ZaloChatMessage
            {
                Id = sent.MessageId,
                GlobalMessageId = sent.MessageId,
                ClientMessageId = sent.ClientMessageId,
                SenderUid = Session == null ? string.Empty : Session.Uid,
                Sender = "Tôi",
                MsgType = "chat.recommended",
                Text = "[Danh thiếp] " + contact.Name,
                FromMe = true,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            message.SetContactCard(contact.UserId, contact.Name, contact.Avatar, sent.QrCodeUrl, string.Empty);
            AddMessage(conversation, message, false);
            if (ZaloSettingsService.Current.IsSoundEnabled) ZaloAudioService.Current.PlayMessageSent();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SendImageAsync(ZaloConversation conversation, string fileName, IInputStream input,
            long totalSize, uint width, uint height, string caption)
        {
            EnsureCanSendMessage(conversation);
            JsonObject result = await ZaloMessaging.SendImageAsync(Session, conversation.ThreadId, conversation.IsGroup,
                fileName, input, totalSize, width, height, caption, CancellationToken.None);
            if (!Conversations.Contains(conversation)) Conversations.Add(conversation);
            AddMessage(conversation, new ZaloChatMessage
            {
                Id = ZaloAuth.Str(result, "msgId"),
                Sender = "Tôi",
                MsgType = "chat.photo",
                MediaUrl = ZaloAuth.Str(result, "normalUrl"),
                MediaFullUrl = string.IsNullOrWhiteSpace(ZaloAuth.Str(result, "hdUrl"))
                    ? ZaloAuth.Str(result, "normalUrl") : ZaloAuth.Str(result, "hdUrl"),
                Text = caption ?? string.Empty,
                FromMe = true,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, false);
            if (ZaloSettingsService.Current.IsSoundEnabled) ZaloAudioService.Current.PlayMessageSent();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SendGifAsync(ZaloConversation conversation, string fileName, IInputStream input,
            long totalSize, uint width, uint height, string previewUrl)
        {
            EnsureCanSendMessage(conversation);
            JsonObject result = await ZaloMessaging.SendGifAsync(Session, conversation.ThreadId, conversation.IsGroup,
                fileName, input, totalSize, width, height, CancellationToken.None);
            if (!Conversations.Contains(conversation)) Conversations.Add(conversation);
            string mediaUrl = string.IsNullOrWhiteSpace(previewUrl) ? ZaloAuth.Str(result, "previewUrl") : previewUrl;
            if (string.IsNullOrWhiteSpace(mediaUrl)) mediaUrl = "ms-appx:///Assets/Emoticons/" + fileName;
            AddMessage(conversation, new ZaloChatMessage
            {
                Id = ZaloAuth.Str(result, "msgId"),
                GlobalMessageId = ZaloAuth.Str(result, "msgId"),
                SenderUid = Session == null ? string.Empty : Session.Uid,
                Sender = "Tôi",
                MsgType = "chat.gif",
                Text = string.Empty,
                MediaUrl = mediaUrl,
                FromMe = true,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, false);
            if (ZaloSettingsService.Current.IsSoundEnabled) ZaloAudioService.Current.PlayMessageSent();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SendStickerAsync(ZaloConversation conversation, ZaloSticker sticker)
        {
            if (conversation == null || sticker == null) throw new InvalidOperationException("Chưa chọn cuộc trò chuyện hoặc nhãn dán.");
            EnsureCanSendMessage(conversation);
            ZaloStickerSendResult sent = await ZaloMessaging.SendStickerAsync(
                Session, conversation.ThreadId, conversation.IsGroup, sticker, CancellationToken.None);
            if (!Conversations.Contains(conversation)) Conversations.Add(conversation);
            AddMessage(conversation, new ZaloChatMessage
            {
                Id = sent.MessageId,
                GlobalMessageId = sent.MessageId,
                ClientMessageId = sent.ClientMessageId,
                SenderUid = Session == null ? string.Empty : Session.Uid,
                Sender = "Tôi",
                MsgType = "chat.sticker",
                StickerId = sticker.Id,
                MediaUrl = sticker.ImageUrl,
                FromMe = true,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, false);
            if (ZaloSettingsService.Current.IsSoundEnabled) ZaloAudioService.Current.PlayMessageSent();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SendVoiceAsync(ZaloConversation conversation, string fileName, IInputStream input, long totalSize)
        {
            if (conversation == null) throw new InvalidOperationException("Chưa chọn cuộc trò chuyện.");
            EnsureCanSendMessage(conversation);
            ZaloVoiceSendResult sent = await ZaloMessaging.SendVoiceAsync(
                Session, Listener, conversation.ThreadId, conversation.IsGroup, fileName, input, totalSize, CancellationToken.None);
            if (!Conversations.Contains(conversation)) Conversations.Add(conversation);
            AddMessage(conversation, new ZaloChatMessage
            {
                Id = sent.MessageId,
                GlobalMessageId = sent.MessageId,
                ClientMessageId = sent.ClientMessageId,
                SenderUid = Session == null ? string.Empty : Session.Uid,
                Sender = "Tôi",
                MsgType = "chat.voice",
                MediaUrl = sent.VoiceUrl,
                FromMe = true,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, false);
            if (ZaloSettingsService.Current.IsSoundEnabled) ZaloAudioService.Current.PlayMessageSent();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SendFileAsync(ZaloConversation conversation, string fileName, IInputStream input, long totalSize)
        {
            if (conversation == null) throw new InvalidOperationException("Chưa chọn cuộc trò chuyện.");
            EnsureCanSendMessage(conversation);
            ZaloFileSendResult sent = await ZaloMessaging.SendFileAsync(
                Session, Listener, conversation.ThreadId, conversation.IsGroup, fileName, input, totalSize, CancellationToken.None);
            if (!Conversations.Contains(conversation)) Conversations.Add(conversation);
            AddMessage(conversation, new ZaloChatMessage
            {
                Id = sent.MessageId,
                GlobalMessageId = sent.MessageId,
                ClientMessageId = sent.ClientMessageId,
                SenderUid = Session == null ? string.Empty : Session.Uid,
                Sender = "Tôi",
                MsgType = sent.MsgType,
                FileName = sent.FileName,
                MediaUrl = sent.FileUrl,
                Text = sent.MsgType == "chat.video.msg" ? "[Video]" : "[Tệp] " + sent.FileName,
                FromMe = true,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, false);
            if (ZaloSettingsService.Current.IsSoundEnabled) ZaloAudioService.Current.PlayMessageSent();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task ReactAsync(ZaloConversation conversation, ZaloChatMessage message,
            string icon, int reactionType)
        {
            await ZaloMessaging.AddReactionAsync(Session, conversation, message, icon, reactionType, CancellationToken.None);
            message.ReactionIcon = icon;
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SetPinnedAsync(ZaloConversation conversation, bool pinned)
        {
            await ZaloMessaging.SetPinnedConversationAsync(Session, conversation, pinned, CancellationToken.None);
            conversation.IsPinned = pinned;
            if (pinned) PinnedConversationKeys.Add(conversation.Key);
            else PinnedConversationKeys.Remove(conversation.Key);
            SortConversations();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SetArchivedAsync(ZaloConversation conversation, bool archived)
        {
            if (Session == null || conversation == null) throw new InvalidOperationException("Chưa có phiên Zalo.");
            await ZaloMessaging.SetArchivedConversationAsync(Session, conversation, archived, CancellationToken.None);
            if (archived) ArchivedConversationKeys.Add(conversation.Key);
            else ArchivedConversationKeys.Remove(conversation.Key);
            ZaloSettingsService.Current.SetArchivedConversationKeys(Session.Uid, ArchivedConversationKeys.ToList());
            Action statusHandler = StatusChanged;
            if (statusHandler != null) statusHandler();
            var ignored = SaveCachedConversationsAsync();
        }

        public static bool IsArchived(ZaloConversation conversation)
        {
            return conversation != null && ArchivedConversationKeys.Contains(conversation.Key);
        }

        public static async Task SetUnreadMarkAsync(ZaloConversation conversation, bool marked)
        {
            if (Session == null || conversation == null) throw new InvalidOperationException("Chưa có phiên Zalo.");
            if (marked)
                await ZaloMessaging.AddUnreadMarkAsync(Session, conversation, CancellationToken.None);
            else
                await ZaloMessaging.RemoveUnreadMarkAsync(Session, conversation, CancellationToken.None);
            conversation.IsMarkedUnread = marked;
            if (marked)
            {
                UnreadMarkedKeys.Add(conversation.Key);
            }
            else UnreadMarkedKeys.Remove(conversation.Key);
            Action statusHandler = StatusChanged;
            if (statusHandler != null) statusHandler();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SetMutedAsync(ZaloConversation conversation, bool muted)
        {
            if (Session == null || conversation == null) throw new InvalidOperationException("Chưa có phiên Zalo.");
            await ZaloAuth.SetConversationMuteAsync(Session, conversation.ThreadId, conversation.IsGroup, muted, CancellationToken.None);
            conversation.IsMuted = muted;
            if (muted) MutedConversationKeys.Add(conversation.Key);
            else MutedConversationKeys.Remove(conversation.Key);
            Action statusHandler = StatusChanged;
            if (statusHandler != null) statusHandler();
            var ignored = SaveCachedConversationsAsync();
        }

        public static bool IsHidden(ZaloConversation conversation)
        {
            return conversation != null && HiddenConversationKeys.Contains(conversation.Key);
        }

        public static async Task SetHiddenAsync(ZaloConversation conversation, bool hidden)
        {
            if (Session == null || conversation == null) throw new InvalidOperationException("Chưa có phiên Zalo.");
            await ZaloAuth.SetConversationHiddenAsync(Session, conversation.ThreadId, conversation.IsGroup,
                hidden, CancellationToken.None);
            if (hidden) HiddenConversationKeys.Add(conversation.Key);
            else HiddenConversationKeys.Remove(conversation.Key);
            Action statusHandler = StatusChanged;
            if (statusHandler != null) statusHandler();
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task SendTypingAsync(ZaloConversation conversation)
        {
            if (conversation == null || Session == null) return;
            DateTime now = DateTime.UtcNow;
            DateTime last;
            if (LastTypingSentUtc.TryGetValue(conversation.Key, out last)
                && now - last < TimeSpan.FromSeconds(2)) return;
            LastTypingSentUtc[conversation.Key] = now;
            try { await ZaloMessaging.SendTypingAsync(Session, conversation, CancellationToken.None); }
            catch (Exception ex) { ZaloLog.Add("Typing event failed: " + ex.Message); }
        }

        public static async Task DeleteConversationAsync(ZaloConversation conversation)
        {
            await ZaloMessaging.DeleteConversationAsync(Session, conversation, CancellationToken.None);
            Conversations.Remove(conversation);
            PinnedConversationKeys.Remove(conversation.Key);
            UnreadMarkedKeys.Remove(conversation.Key);
            conversation.IsMarkedUnread = false;
            Action statusHandler = StatusChanged;
            if (statusHandler != null) statusHandler();
            var ignored = SaveCachedConversationsAsync();
        }

        private static void SortConversations()
        {
            var sorted = Conversations.OrderByDescending(c => c.IsPinned).ThenByDescending(c => c.LastTs).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                int current = Conversations.IndexOf(sorted[i]);
                if (current >= 0 && current != i) Conversations.Move(current, i);
            }
        }

        public static async Task RecallMessageAsync(ZaloConversation conversation, ZaloChatMessage message)
        {
            await ZaloMessaging.UndoMessageAsync(Session, conversation, message, CancellationToken.None);
            MarkRecalled(conversation, message);
            var ignored = SaveCachedConversationsAsync();
        }

        public static async Task DeleteMessageForMeAsync(ZaloConversation conversation, ZaloChatMessage message)
        {
            await ZaloMessaging.DeleteMessageForMeAsync(Session, conversation, message, CancellationToken.None);
            conversation.Messages.Remove(message);
            if (!string.IsNullOrEmpty(message.Id)) conversation.SeenIds.Remove(message.Id);
            ZaloChatMessage latest = conversation.Messages.LastOrDefault();
            if (latest == null)
            {
                conversation.LastText = conversation.IsGroup ? string.Empty : "Nhấn để trò chuyện";
                conversation.LastTs = 0;
            }
            else
            {
                UpdateConversationPreviewFromMessage(conversation, latest);
            }
            var ignored = SaveCachedConversationsAsync();
        }
    }

}
