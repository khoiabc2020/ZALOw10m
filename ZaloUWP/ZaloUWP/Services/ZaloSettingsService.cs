using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.UI.Xaml;

namespace ZaloUWP.Services
{
    public class ZaloSettingsService
    {
        private static ZaloSettingsService _current;
        public static ZaloSettingsService Current => _current ?? (_current = new ZaloSettingsService());

        private readonly IPropertySet _settings;

        public ZaloSettingsService()
        {
            _settings = ApplicationData.Current.LocalSettings.Values;
        }

        public string SelectedWallpaper
        {
            get => _settings.ContainsKey("SelectedWallpaper") ? (string)_settings["SelectedWallpaper"] : "ms-appx:///Assets/Backgrounds/bg_01.png";
            set => _settings["SelectedWallpaper"] = value;
        }

        public string GetConversationWallpaper(string accountId, string conversationKey)
        {
            if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(conversationKey)) return string.Empty;
            string key = "ConversationWallpaper." + accountId + "." + conversationKey;
            return _settings.ContainsKey(key) ? _settings[key] as string ?? string.Empty : string.Empty;
        }

        public void SetConversationWallpaper(string accountId, string conversationKey, string wallpaper)
        {
            if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(conversationKey)) return;
            string key = "ConversationWallpaper." + accountId + "." + conversationKey;
            if (string.IsNullOrWhiteSpace(wallpaper)) _settings.Remove(key);
            else _settings[key] = wallpaper;
        }

        public bool IsSoundEnabled
        {
            get => _settings.ContainsKey("IsSoundEnabled") ? (bool)_settings["IsSoundEnabled"] : true;
            set => _settings["IsSoundEnabled"] = value;
        }

        public bool IsEnterToSend
        {
            get => _settings.ContainsKey("IsEnterToSend") ? (bool)_settings["IsEnterToSend"] : true;
            set => _settings["IsEnterToSend"] = value;
        }

        public ElementTheme AppTheme
        {
            get
            {
                if (_settings.ContainsKey("AppTheme"))
                {
                    var val = (int)_settings["AppTheme"];
                    return (ElementTheme)val;
                }
                return ElementTheme.Light;
            }
            set => _settings["AppTheme"] = (int)value;
        }

        public List<string> GetQuickContactIds(string accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return new List<string>();
            string key = "QuickContactIds." + accountId;
            try
            {
                return _settings.ContainsKey(key)
                    ? JsonConvert.DeserializeObject<List<string>>((string)_settings[key]) ?? new List<string>()
                    : new List<string>();
            }
            catch { return new List<string>(); }
        }

        public void SetQuickContactIds(string accountId, List<string> userIds)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return;
            _settings["QuickContactIds." + accountId] = JsonConvert.SerializeObject(userIds ?? new List<string>());
        }

        public List<string> GetBlockedUserIds(string accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return new List<string>();
            string key = "BlockedUserIds." + accountId;
            try
            {
                return _settings.ContainsKey(key)
                    ? JsonConvert.DeserializeObject<List<string>>((string)_settings[key]) ?? new List<string>()
                    : new List<string>();
            }
            catch { return new List<string>(); }
        }

        public void SetBlockedUserIds(string accountId, List<string> userIds)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return;
            _settings["BlockedUserIds." + accountId] = JsonConvert.SerializeObject(userIds ?? new List<string>());
        }

        public List<string> GetOtherConversationKeys(string accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return new List<string>();
            string key = "OtherConversationKeys." + accountId;
            try
            {
                return _settings.ContainsKey(key)
                    ? JsonConvert.DeserializeObject<List<string>>((string)_settings[key]) ?? new List<string>()
                    : new List<string>();
            }
            catch { return new List<string>(); }
        }

        public void SetOtherConversationKeys(string accountId, List<string> conversationKeys)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return;
            _settings["OtherConversationKeys." + accountId] = JsonConvert.SerializeObject(conversationKeys ?? new List<string>());
        }

        public List<string> GetArchivedConversationKeys(string accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return new List<string>();
            string key = "ArchivedConversationKeys." + accountId;
            try
            {
                return _settings.ContainsKey(key)
                    ? JsonConvert.DeserializeObject<List<string>>((string)_settings[key]) ?? new List<string>()
                    : new List<string>();
            }
            catch { return new List<string>(); }
        }

        public void SetArchivedConversationKeys(string accountId, List<string> conversationKeys)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return;
            _settings["ArchivedConversationKeys." + accountId] = JsonConvert.SerializeObject(conversationKeys ?? new List<string>());
        }
    }
}

