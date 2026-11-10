using System;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using ZaloUWP.Models;

namespace ZaloUWP.Services
{
    public class ZaloNotificationService
    {
        private static ZaloNotificationService _current;
        public static ZaloNotificationService Current => _current ?? (_current = new ZaloNotificationService());

        public void ShowMessageToast(MessageItem msg, string conversationTitle = null)
        {
            if (msg == null) return;

            try
            {
                var title = !string.IsNullOrEmpty(msg.FromName) ? msg.FromName : (!string.IsNullOrEmpty(conversationTitle) ? conversationTitle : "Zalo");
                var text = !string.IsNullOrEmpty(msg.Text) ? msg.Text : (msg.IsSticker ? "[Nhãn dán]" : "[Hình ảnh]");

                var toastXml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastImageAndText02);
                var stringElements = toastXml.GetElementsByTagName("text");
                stringElements[0].AppendChild(toastXml.CreateTextNode(title));
                stringElements[1].AppendChild(toastXml.CreateTextNode(text));

                if (!string.IsNullOrEmpty(msg.FromAvatar))
                {
                    var imageElements = toastXml.GetElementsByTagName("image");
                    if (imageElements.Length > 0)
                    {
                        ((XmlElement)imageElements[0]).SetAttribute("src", msg.FromAvatar);
                    }
                }

                var toast = new ToastNotification(toastXml);
                toast.Tag = msg.ConversationId ?? "zalo_chat";
                toast.Group = "zalo_messages";

                ToastNotificationManager.CreateToastNotifier().Show(toast);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NotificationService] Toast error: {ex.Message}");
            }
        }

        public void UpdateBadge(int unreadCount)
        {
            try
            {
                var badgeUpdater = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
                if (unreadCount <= 0)
                {
                    badgeUpdater.Clear();
                }
                else
                {
                    var badgeXml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
                    var badgeElement = (XmlElement)badgeXml.SelectSingleNode("/badge");
                    badgeElement.SetAttribute("value", unreadCount.ToString());

                    var badgeNotification = new BadgeNotification(badgeXml);
                    badgeUpdater.Update(badgeNotification);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NotificationService] Badge error: {ex.Message}");
            }
        }
    }
}

