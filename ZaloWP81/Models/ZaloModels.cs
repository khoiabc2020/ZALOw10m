using System;
using System.Collections.Generic;

namespace ZaloUWP.Models
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    internal sealed class JsonPropertyAttribute : Attribute
    {
        public string PropertyName { get; set; }
        public JsonPropertyAttribute(string name) { PropertyName = name; }
    }
    public class UserInfo
    {
        [JsonProperty("uid")]
        public string UserId { get; set; }

        [JsonProperty("zalo_name")]
        public string DisplayName { get; set; }

        [JsonProperty("avatar")]
        public string AvatarUrl { get; set; }

        [JsonProperty("phone")]
        public string PhoneNumber { get; set; }

        [JsonProperty("status")]
        public string StatusMessage { get; set; }

        [JsonProperty("gender")]
        public int Gender { get; set; }

        [JsonProperty("dob")]
        public string DateOfBirth { get; set; }

        public bool IsGroupOwner { get; set; }
        public bool IsGroupDeputy { get; set; }
        public string GroupRoleText => IsGroupOwner ? "Trưởng nhóm" : IsGroupDeputy ? "Phó nhóm" : "Thành viên";

        public bool IsOnline { get; set; }

        public string DisplayAvatar => string.IsNullOrEmpty(AvatarUrl)
            ? "ms-appx:///Assets/Square150x150Logo.png"
            : AvatarUrl;
    }

    public class ConversationItem
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Title { get; set; }

        [JsonProperty("avatar")]
        public string AvatarUrl { get; set; }

        [JsonProperty("last_message")]
        public string LastMessage { get; set; }

        [JsonProperty("last_time")]
        public long LastMessageTimestamp { get; set; }

        [JsonProperty("unread_count")]
        public int UnreadCount { get; set; }

        [JsonProperty("is_group")]
        public bool IsGroup { get; set; }

        [JsonProperty("is_online")]
        public bool IsOnline { get; set; }

        [JsonProperty("is_pinned")]
        public bool IsPinned { get; set; }

        public bool IsMuted { get; set; }

        public bool HasUnreadDot { get; set; }

        public string SenderPrefix { get; set; }

        public string CustomTime { get; set; }

        public string UnreadBadgeText { get; set; }

        public string DisplayTime => !string.IsNullOrEmpty(CustomTime) ? CustomTime : FormattedTime;

        public bool HasSenderPrefix => !string.IsNullOrEmpty(SenderPrefix);

        public string DisplayBadgeText => !string.IsNullOrEmpty(UnreadBadgeText) 
            ? UnreadBadgeText 
            : (UnreadCount > 0 ? UnreadCount.ToString() : "");

        public string FormattedTime
        {
            get
            {
                if (LastMessageTimestamp <= 0) return "";
                try
                {
                    var dt = UnixTime.FromMilliseconds(LastMessageTimestamp).LocalDateTime;
                    var now = DateTime.Now;
                    if (dt.Date == now.Date)
                    {
                        return dt.ToString("HH:mm");
                    }
                    if (dt.Year == now.Year)
                    {
                        return dt.ToString("dd/MM");
                    }
                    return dt.ToString("dd/MM/yy");
                }
                catch
                {
                    return "";
                }
            }
        }

        public string DisplayAvatar => string.IsNullOrEmpty(AvatarUrl)
            ? "ms-appx:///Assets/Square150x150Logo.png"
            : AvatarUrl;

        public bool HasUnread => UnreadCount > 0;
    }

    public enum MessageType
    {
        Text = 1,
        Image = 2,
        Sticker = 3,
        Voice = 4,
        File = 5,
        Video = 6,
        System = 7
    }

    public enum MessageDeliveryStatus
    {
        Sending,
        Sent,
        Delivered,
        Read,
        Failed
    }

    public class MessageItem
    {
        [JsonProperty("msg_id")]
        public string MsgId { get; set; }

        [JsonProperty("cli_msg_id")]
        public string CliMsgId { get; set; }

        [JsonProperty("conv_id")]
        public string ConversationId { get; set; }

        [JsonProperty("from_uid")]
        public string FromUid { get; set; }

        [JsonProperty("from_name")]
        public string FromName { get; set; }

        [JsonProperty("from_avatar")]
        public string FromAvatar { get; set; }

        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("timestamp")]
        public long Timestamp { get; set; }

        [JsonProperty("is_outgoing")]
        public bool IsOutgoing { get; set; }

        [JsonProperty("msg_type")]
        public MessageType MsgType { get; set; } = MessageType.Text;

        [JsonProperty("delivery_status")]
        public MessageDeliveryStatus Status { get; set; } = MessageDeliveryStatus.Sent;

        [JsonProperty("media_url")]
        public string MediaUrl { get; set; }

        [JsonProperty("thumb_url")]
        public string ThumbUrl { get; set; }

        [JsonProperty("sticker_id")]
        public string StickerId { get; set; }

        [JsonProperty("quote_text")]
        public string QuoteText { get; set; }

        [JsonProperty("quote_sender")]
        public string QuoteSender { get; set; }

        public bool HasQuote => !string.IsNullOrEmpty(QuoteText);
        public bool HasMedia => !string.IsNullOrEmpty(MediaUrl);
        public bool IsSticker => MsgType == MessageType.Sticker && !string.IsNullOrEmpty(MediaUrl);

        public string FormattedTime
        {
            get
            {
                if (Timestamp <= 0) return "";
                try
                {
                    var dt = UnixTime.FromMilliseconds(Timestamp).LocalDateTime;
                    return dt.ToString("HH:mm");
                }
                catch
                {
                    return "";
                }
            }
        }
    }

    public class FriendItem
    {
        [JsonProperty("userId")]
        public string UserId { get; set; }

        [JsonProperty("displayName")]
        public string DisplayName { get; set; }

        [JsonProperty("avatar")]
        public string AvatarUrl { get; set; }

        [JsonProperty("phoneNumber")]
        public string PhoneNumber { get; set; }

        [JsonProperty("status")]
        public string StatusMessage { get; set; }

        public bool IsOnline { get; set; }

        public string CategoryHeader
        {
            get
            {
                if (string.IsNullOrEmpty(DisplayName)) return "#";
                char c = char.ToUpperInvariant(DisplayName[0]);
                return (c >= 'A' && c <= 'Z') ? c.ToString() : "#";
            }
        }

        public string DisplayAvatar => string.IsNullOrEmpty(AvatarUrl)
            ? "ms-appx:///Assets/Square150x150Logo.png"
            : AvatarUrl;
    }

    public class GroupItem
    {
        [JsonProperty("grid")]
        public string GroupId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("avatar")]
        public string AvatarUrl { get; set; }

        [JsonProperty("total_member")]
        public int TotalMembers { get; set; }

        [JsonProperty("admin_id")]
        public string AdminId { get; set; }

        public List<UserInfo> Members { get; set; } = new List<UserInfo>();
    }

    public class QrLoginSession
    {
        public string Code { get; set; }
        public string Token { get; set; }
        public string QrImageUrl { get; set; }
        public string Status { get; set; } = "waiting_scan";
        public DateTime ExpireTime { get; set; }
        public bool IsExpired => DateTime.Now > ExpireTime;
    }

    public class ZaloWebResponse<T>
    {
        [JsonProperty("error_code")]
        public int ErrorCode { get; set; }

        [JsonProperty("error_message")]
        public string ErrorMessage { get; set; }

        [JsonProperty("data")]
        public T Data { get; set; }

        public bool IsSuccess => ErrorCode == 0;
    }
}

