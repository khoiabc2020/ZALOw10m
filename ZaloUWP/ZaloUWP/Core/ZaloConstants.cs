namespace ZaloUWP.Core
{
    public static class ZaloConstants
    {
        public const string WebOrigin = "https://chat.zalo.me";
        public const string WebReferer = "https://chat.zalo.me/";
        public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        // Auth Endpoints
        public const string AuthBaseUrl = "https://id.zalo.me";
        public const string QrGenerateUrl = "https://id.zalo.me/account/authen/qr/generate";
        public const string QrWaitingScanUrl = "https://id.zalo.me/account/authen/qr/waiting-scan";
        public const string QrWaitingConfirmUrl = "https://id.zalo.me/account/authen/qr/waiting-confirm";
        public const string CheckSessionUrl = "https://id.zalo.me/account/checksession?continue=https%3A%2F%2Fchat.zalo.me%2Findex.html";
        public const string QuickChartQrUrl = "https://quickchart.io/qr?text=";

        // Login & Server Info Endpoints
        public const string WpaBaseUrl = "https://wpa.chat.zalo.me";
        public const string GetServerInfoUrl = "https://wpa.chat.zalo.me/api/login/getServerInfo";
        public const string GetLoginInfoUrl = "https://wpa.chat.zalo.me/api/login/getLoginInfo";

        // REST API Endpoints
        public const string ApiFriendsList = "/api/social/friend/getfriends";
        public const string ApiFriendInvites = "/api/friend/recommendsv2/list";
        public const string ApiFriendAccept = "/api/friend/accept";
        public const string ApiFriendReject = "/api/friend/reject";
        public const string ApiFriendBlock = "/api/friend/block";
        public const string ApiFriendUnblock = "/api/friend/unblock";

        public const string ApiConversationsList = "/api/message/getconversation";
        public const string ApiMessagesList = "/api/message/list";
        public const string ApiMessagesListV2 = "/api/message/getmsgv2";
        public const string ApiSendSms = "/api/message/sms";
        public const string ApiUndoMessage = "/api/message/undo";
        public const string ApiDeleteMessage = "/api/message/delete";
        public const string ApiMessageReaction = "/api/message/reaction";
        public const string ApiMessageQuote = "/api/message/quote";
        public const string ApiMessageForward = "/api/message/mforward";

        public const string ApiGroupDetails = "/api/group/getmg-v2";
        public const string ApiGroupList = "/api/group/getlg/v4";
        public const string ApiGroupSendMsg = "/api/group/sendmsg";
        public const string ApiGroupUndoMsg = "/api/group/undomsg";
        public const string ApiGroupDeleteMsg = "/api/group/deletemsg";
        public const string ApiGroupReaction = "/api/group/reaction";
        public const string ApiGroupQuote = "/api/group/quote";
        public const string ApiGroupLeave = "/api/group/leave";

        public const string ApiPollCreate = "/api/poll/create";
        public const string ApiPollVote = "/api/poll/vote";
        public const string ApiPollDetail = "/api/poll/detail";

        public const string ApiBoardList = "/api/board/list";
        public const string ApiBoardCreateTopic = "/api/board/topic/createv2";

        public const string ApiQuickMessagesList = "/api/quickmessage/list";
        public const string ApiSetMute = "/api/social/profile/setmute";
        public const string StickerCdnUrl = "https://zalo-api.zadn.vn/api/emoticon/sticker/webpc?eid={0}&size=130&version=4";
    }
}
