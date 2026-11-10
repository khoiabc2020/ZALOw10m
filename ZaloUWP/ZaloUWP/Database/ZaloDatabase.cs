using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Newtonsoft.Json;
using ZaloUWP.Models;

namespace ZaloUWP.Database
{
    public class ZaloDatabase
    {
        private static ZaloDatabase _current;
        public static ZaloDatabase Current => _current ?? (_current = new ZaloDatabase());

        private readonly StorageFolder _localFolder;

        public ZaloDatabase()
        {
            _localFolder = ApplicationData.Current.LocalFolder;
        }

        public async Task SaveConversationsAsync(List<ConversationItem> items)
        {
            try
            {
                var file = await _localFolder.CreateFileAsync("conversations_cache.json", CreationCollisionOption.ReplaceExisting);
                var json = JsonConvert.SerializeObject(items, Formatting.Indented);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloDatabase] SaveConversations error: {ex.Message}");
            }
        }

        public async Task<List<ConversationItem>> GetConversationsAsync()
        {
            try
            {
                var file = await _localFolder.TryGetItemAsync("conversations_cache.json") as StorageFile;
                if (file != null)
                {
                    var json = await FileIO.ReadTextAsync(file);
                    return JsonConvert.DeserializeObject<List<ConversationItem>>(json) ?? new List<ConversationItem>();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloDatabase] GetConversations error: {ex.Message}");
            }
            return new List<ConversationItem>();
        }

        public async Task SaveMessagesAsync(string conversationId, List<MessageItem> messages)
        {
            if (string.IsNullOrEmpty(conversationId)) return;
            try
            {
                var safeId = string.Join("_", conversationId.Split(Path.GetInvalidFileNameChars()));
                var fileName = $"messages_{safeId}.json";
                var file = await _localFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                var json = JsonConvert.SerializeObject(messages, Formatting.Indented);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloDatabase] SaveMessages error: {ex.Message}");
            }
        }

        public async Task<List<MessageItem>> GetMessagesAsync(string conversationId)
        {
            if (string.IsNullOrEmpty(conversationId)) return new List<MessageItem>();
            try
            {
                var safeId = string.Join("_", conversationId.Split(Path.GetInvalidFileNameChars()));
                var fileName = $"messages_{safeId}.json";
                var file = await _localFolder.TryGetItemAsync(fileName) as StorageFile;
                if (file != null)
                {
                    var json = await FileIO.ReadTextAsync(file);
                    return JsonConvert.DeserializeObject<List<MessageItem>>(json) ?? new List<MessageItem>();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloDatabase] GetMessages error: {ex.Message}");
            }
            return new List<MessageItem>();
        }

        public async Task SaveFriendsAsync(List<FriendItem> items)
        {
            try
            {
                var file = await _localFolder.CreateFileAsync("friends_cache.json", CreationCollisionOption.ReplaceExisting);
                var json = JsonConvert.SerializeObject(items, Formatting.Indented);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloDatabase] SaveFriends error: {ex.Message}");
            }
        }

        public async Task<List<FriendItem>> GetFriendsAsync()
        {
            try
            {
                var file = await _localFolder.TryGetItemAsync("friends_cache.json") as StorageFile;
                if (file != null)
                {
                    var json = await FileIO.ReadTextAsync(file);
                    return JsonConvert.DeserializeObject<List<FriendItem>>(json) ?? new List<FriendItem>();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloDatabase] GetFriends error: {ex.Message}");
            }
            return new List<FriendItem>();
        }

        public async Task ClearAllDataAsync()
        {
            try
            {
                var files = await _localFolder.GetFilesAsync();
                foreach (var file in files)
                {
                    if (file.Name.EndsWith(".json"))
                    {
                        await file.DeleteAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ZaloDatabase] ClearAll error: {ex.Message}");
            }
        }
    }
}

