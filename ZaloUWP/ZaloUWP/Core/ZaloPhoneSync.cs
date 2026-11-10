using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Core;

namespace ZaloLite
{
    /// <summary>
    /// Direct P/Invoke to Windows built-in winsqlite3.dll.
    /// Available out of the box on Windows 10 and Windows 10 Mobile (ARM/x86/x64).
    /// </summary>
    internal static class NativeSqlite
    {
        private const string DllName = "winsqlite3.dll";

        [DllImport(DllName, EntryPoint = "sqlite3_open", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Open([MarshalAs(UnmanagedType.LPStr)] string filename, out IntPtr db);

        [DllImport(DllName, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int PrepareV2(IntPtr db, [MarshalAs(UnmanagedType.LPStr)] string zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

        [DllImport(DllName, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Step(IntPtr pStmt);

        [DllImport(DllName, EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ColumnTextInternal(IntPtr pStmt, int iCol);

        public static string ColumnText(IntPtr pStmt, int iCol)
        {
            IntPtr ptr = ColumnTextInternal(pStmt, iCol);
            if (ptr == IntPtr.Zero) return null;
            return Marshal.PtrToStringAnsi(ptr);
        }

        [DllImport(DllName, EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
        public static extern long ColumnInt64(IntPtr pStmt, int iCol);

        [DllImport(DllName, EntryPoint = "sqlite3_column_int", CallingConvention = CallingConvention.Cdecl)]
        public static extern int ColumnInt(IntPtr pStmt, int iCol);

        [DllImport(DllName, EntryPoint = "sqlite3_column_count", CallingConvention = CallingConvention.Cdecl)]
        public static extern int ColumnCount(IntPtr pStmt);

        [DllImport(DllName, EntryPoint = "sqlite3_column_name", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ColumnNameInternal(IntPtr pStmt, int iCol);

        public static string ColumnName(IntPtr pStmt, int iCol)
        {
            IntPtr ptr = ColumnNameInternal(pStmt, iCol);
            return ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(ptr);
        }

        [DllImport(DllName, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Finalize(IntPtr pStmt);

        [DllImport(DllName, EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Close(IntPtr db);
    }

    /// <summary>
    /// Tracks the phone-history transfer UI. The legacy pull_mobile_msg request currently returns HTTP 404,
    /// and the replacement transfer protocol has not been implemented yet.
    /// </summary>
    public static class ZaloPhoneSync
    {
        private static CryptographicKey _rsaKeyPair;
        private static CoreDispatcher _dispatcher;
        private static bool _isSyncing;
        private static string _statusMessage = "Đồng bộ từ điện thoại chưa khả dụng: giao thức hiện tại cần được cập nhật.";

        public static bool IsSyncing
        {
            get { return _isSyncing; }
            private set
            {
                _isSyncing = value;
                NotifyStatus();
            }
        }

        public static string StatusMessage
        {
            get { return _statusMessage; }
            private set
            {
                _statusMessage = value;
                NotifyStatus();
            }
        }

        public static event Action<string, bool> SyncStatusChanged;

        public static void Initialize(CoreDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        private static void NotifyStatus()
        {
            if (_dispatcher == null) return;
            var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                SyncStatusChanged?.Invoke(_statusMessage, _isSyncing);
            });
        }

        /// <summary>
        /// Do not send the obsolete pull_mobile_msg request. Keep the entry point so the UI can explain
        /// why the operation is unavailable until the current transfer protocol is implemented.
        /// </summary>
        public static Task StartSyncAsync(ZaloSession session)
        {
            if (session == null)
            {
                StatusMessage = "Vui lòng đăng nhập trước khi đồng bộ.";
                return Task.FromResult(0);
            }

            StatusMessage = "Chưa thể đồng bộ từ điện thoại: API cũ trả 404, còn giao thức mới chưa được nối.";
            ZaloLog.Add("[PhoneSync] Skipped obsolete pull_mobile_msg request; current transfer protocol is not implemented.");
            return Task.FromResult(0);
        }

        /// <summary>
        /// Hooked to incoming WebSocket event frames. Catches user_confirm, syncmsg_info, transfer_error.
        /// </summary>
        public static void ProcessEvent(JsonObject obj)
        {
            if (obj == null) return;

            string act = ZaloAuth.Str(obj, "act");
            JsonObject dataObj = obj;

            if (string.IsNullOrEmpty(act) && obj.ContainsKey("data"))
            {
                var val = obj.GetNamedValue("data");
                if (val.ValueType == JsonValueType.Object)
                {
                    dataObj = val.GetObject();
                    act = ZaloAuth.Str(dataObj, "act");
                }
            }

            if (string.IsNullOrEmpty(act)) return;

            ZaloLog.Add("[PhoneSync] Event received act=" + act);

            if (act == "user_confirm" || act == "user_action")
            {
                StatusMessage = "Điện thoại đã xác nhận! Đang chuẩn bị gói tin nhắn...";
            }
            else if (act == "transfer_error")
            {
                int code = (int)ZaloAuth.Num(dataObj, "error_code");
                StatusMessage = "Đồng bộ bị từ chối hoặc điện thoại báo lỗi (" + code + ")";
                IsSyncing = false;
            }
            else if (act == "syncmsg_info")
            {
                StatusMessage = "Đã nhận thông tin gói tin nhắn, đang tải...";
                var ignored = Task.Run(() => HandleSyncMsgInfoAsync(dataObj));
            }
        }

        private static async Task HandleSyncMsgInfoAsync(JsonObject data)
        {
            try
            {
                string downloadUrl = ZaloAuth.Str(data, "url");
                string encKeyBase64 = ZaloAuth.Str(data, "encrypted_key");
                long fileSize = (long)ZaloAuth.Num(data, "file_size");

                ZaloLog.Add("[PhoneSync] syncmsg_info: size=" + fileSize + ", url=" + downloadUrl);

                if (string.IsNullOrEmpty(encKeyBase64))
                {
                    StatusMessage = "Lỗi: Không nhận được khóa giải mã từ điện thoại.";
                    IsSyncing = false;
                    return;
                }

                if (_rsaKeyPair == null)
                {
                    StatusMessage = "Lỗi: Khóa bảo mật RSA không tồn tại.";
                    IsSyncing = false;
                    return;
                }

                // 1. Decrypt transfer AES key with RSA private key (PKCS#1 v1.5 padding)
                StatusMessage = "Đang giải mã khóa truyền tải...";
                byte[] encKeyBytes = Convert.FromBase64String(encKeyBase64);
                IBuffer cipherBuf = CryptographicBuffer.CreateFromByteArray(encKeyBytes);
                IBuffer decryptedKeyBuf = CryptographicEngine.Decrypt(_rsaKeyPair, cipherBuf, null);
                byte[] aesKey = new byte[decryptedKeyBuf.Length];
                CryptographicBuffer.CopyToByteArray(decryptedKeyBuf, out aesKey);

                ZaloLog.Add("[PhoneSync] Transfer AES key decrypted (" + aesKey.Length + " bytes)");

                // 2. Download the backup file
                StatusMessage = "Đang tải dữ liệu tin nhắn (" + FormatBytes(fileSize) + ")...";
                byte[] fileBytes;
                using (var http = new System.Net.Http.HttpClient())
                {
                    http.DefaultRequestHeaders.Add("User-Agent", ZaloAuth.QrUserAgent);
                    fileBytes = await http.GetByteArrayAsync(downloadUrl);
                }

                ZaloLog.Add("[PhoneSync] Download complete: " + fileBytes.Length + " bytes");

                // 3. Decrypt fileBytes with AES-256-CBC (zero IV, no padding)
                StatusMessage = "Đang giải mã gói ZDB4.0...";
                byte[] decryptedFile;
                using (var aes = System.Security.Cryptography.Aes.Create())
                {
                    aes.Key = aesKey;
                    aes.IV = new byte[16];
                    aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                    aes.Padding = System.Security.Cryptography.PaddingMode.None;
                    using (var decryptor = aes.CreateDecryptor())
                    {
                        decryptedFile = decryptor.TransformFinalBlock(fileBytes, 0, fileBytes.Length);
                    }
                }

                ZaloLog.Add("[PhoneSync] File decrypted (" + decryptedFile.Length + " bytes)");

                // Verify ZDB4.0 magic
                if (decryptedFile.Length >= 6)
                {
                    string magic = Encoding.ASCII.GetString(decryptedFile, 0, 6);
                    ZaloLog.Add("[PhoneSync] Decrypted magic header: " + magic);
                }

                // 4. Decompress and extract SQLite databases
                StatusMessage = "Đang giải nén dữ liệu...";
                var sqliteFiles = ExtractSqliteFiles(decryptedFile);
                ZaloLog.Add("[PhoneSync] Found " + sqliteFiles.Count + " SQLite database(s) in payload");

                if (sqliteFiles.Count == 0)
                {
                    StatusMessage = "Không tìm thấy cơ sở dữ liệu tin nhắn trong gói.";
                    IsSyncing = false;
                    return;
                }

                // 5. Read messages from SQLite and import into ZaloApp
                StatusMessage = "Đang nhập tin nhắn vào danh sách...";
                int importedCount = 0;
                int conversationCount = 0;

                for (int i = 0; i < sqliteFiles.Count; i++)
                {
                    int count = await ImportSqliteDatabaseAsync(sqliteFiles[i], i);
                    if (count > 0)
                    {
                        importedCount += count;
                        conversationCount++;
                    }
                }

                // 6. Save cache to disk
                await ZaloApp.SaveCachedConversationsAsync();

                StatusMessage = "Đồng bộ thành công! Đã khôi phục " + importedCount + " tin nhắn.";
                ZaloLog.Add("[PhoneSync] Done: imported " + importedCount + " messages into " + conversationCount + " conversations.");
            }
            catch (Exception ex)
            {
                ZaloLog.Add("[PhoneSync] Error: " + ex.ToString());
                StatusMessage = "Lỗi đồng bộ: " + ex.Message;
            }
            finally
            {
                IsSyncing = false;
            }
        }

        private static List<byte[]> ExtractSqliteFiles(byte[] payload)
        {
            var results = new List<byte[]>();
            byte[] sqliteMagic = Encoding.ASCII.GetBytes("SQLite format 3\0");
            byte[] xzMagic = new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 };

            // Look for XZ stream
            int xzOffset = FindBytes(payload, xzMagic, 0);
            if (xzOffset >= 0)
            {
                try
                {
                    using (var ms = new MemoryStream(payload, xzOffset, payload.Length - xzOffset))
                    using (var xz = new SharpCompress.Compressors.Xz.XZStream(ms))
                    using (var outMs = new MemoryStream())
                    {
                        xz.CopyTo(outMs);
                        byte[] decompressed = outMs.ToArray();
                        ZaloLog.Add("[PhoneSync] XZ decompressed to " + decompressed.Length + " bytes");
                        ExtractFromBuffer(decompressed, results);
                        return results;
                    }
                }
                catch (Exception ex)
                {
                    ZaloLog.Add("[PhoneSync] XZ decompress error: " + ex.Message);
                }
            }

            // Look directly in decrypted payload
            ExtractFromBuffer(payload, results);
            return results;
        }

        private static void ExtractFromBuffer(byte[] buffer, List<byte[]> results)
        {
            byte[] sqliteMagic = Encoding.ASCII.GetBytes("SQLite format 3\0");

            // Check if buffer is directly a SQLite database
            if (buffer.Length >= 16 && StartsWith(buffer, sqliteMagic, 0))
            {
                results.Add(buffer);
                return;
            }

            // Search for multiple SQLite databases in buffer
            int pos = 0;
            while (pos < buffer.Length)
            {
                int found = FindBytes(buffer, sqliteMagic, pos);
                if (found < 0) break;

                // Parse SQLite header: page size at offset 16, page count at offset 28
                int pageSize = (buffer[found + 16] << 8) | buffer[found + 17];
                if (pageSize == 1) pageSize = 65536;
                int pageCount = (buffer[found + 28] << 24) | (buffer[found + 29] << 16) | (buffer[found + 30] << 8) | buffer[found + 31];
                long dbSize = (long)pageSize * pageCount;

                if (dbSize > 0 && found + dbSize <= buffer.Length)
                {
                    byte[] db = new byte[dbSize];
                    Array.Copy(buffer, found, db, 0, (int)dbSize);
                    results.Add(db);
                    pos = (int)(found + dbSize);
                }
                else
                {
                    // Fallback: take remaining
                    byte[] db = new byte[buffer.Length - found];
                    Array.Copy(buffer, found, db, 0, db.Length);
                    results.Add(db);
                    break;
                }
            }
        }

        private static async Task<int> ImportSqliteDatabaseAsync(byte[] dbBytes, int index)
        {
            int count = 0;
            string tempPath = null;

            try
            {
                // Write to temp file so winsqlite3 can open it
                var folder = ApplicationData.Current.TemporaryFolder;
                var file = await folder.CreateFileAsync("sync_part_" + index + ".sqlite", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, dbBytes);
                tempPath = file.Path;

                IntPtr db;
                if (NativeSqlite.Open(tempPath, out db) != 0)
                {
                    ZaloLog.Add("[PhoneSync] Could not open SQLite: " + tempPath);
                    return 0;
                }

                // Read the whole row because mobile database versions use different names for the
                // conversation/recipient column. Without it, outgoing history was assigned to the
                // first chat and most messages appeared to be missing from the opened conversation.
                string sql = "SELECT * FROM ChatContent";
                IntPtr stmt;
                if (NativeSqlite.PrepareV2(db, sql, -1, out stmt, IntPtr.Zero) == 0)
                {
                    var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < NativeSqlite.ColumnCount(stmt); i++)
                        columns[NativeSqlite.ColumnName(stmt, i)] = i;

                    int senderIndex = FindColumn(columns, "SenderId", "FromUid", "uidFrom");
                    int cliIndex = FindColumn(columns, "CliMsgId", "ClientMsgId");
                    int glbIndex = FindColumn(columns, "GlbMsgId", "MsgId", "GlobalMsgId");
                    int typeIndex = FindColumn(columns, "MsgType", "Type");
                    int timeIndex = FindColumn(columns, "TimeStamp", "Timestamp", "ts");
                    int contentIndex = FindColumn(columns, "MsgContent", "Content", "content");
                    int threadIndex = FindColumn(columns, "ThreadId", "ConversationId", "ChatId", "PeerId", "ToUid", "ReceiverId", "Grid", "GroupId");
                    int groupIndex = FindColumn(columns, "GroupId", "Grid");

                    while (NativeSqlite.Step(stmt) == 100) // SQLITE_ROW
                    {
                        string senderId = ReadColumn(stmt, senderIndex, "0");
                        long cliMsgId = ParseLong(ReadColumn(stmt, cliIndex, "0"));
                        long glbMsgId = ParseLong(ReadColumn(stmt, glbIndex, "0"));
                        int msgType = (int)ParseLong(ReadColumn(stmt, typeIndex, "0"));
                        long ts = ParseLong(ReadColumn(stmt, timeIndex, "0"));
                        string msgContent = ReadColumn(stmt, contentIndex, "");

                        string msgId = glbMsgId > 0 ? glbMsgId.ToString() : (cliMsgId > 0 ? cliMsgId.ToString() : Guid.NewGuid().ToString("N"));

                        // Extract clean text from content JSON if needed
                        string displayText = ExtractText(msgContent, msgType);

                        bool fromMe = (senderId == "0" || (ZaloApp.Session != null && senderId == ZaloApp.Session.Uid));

                        // Thread identifier: if direct chat, use senderId if not fromMe, else match
                        string threadId = ReadColumn(stmt, threadIndex, "");
                        bool isGroup = groupIndex >= 0 && ReadColumn(stmt, groupIndex, "").Length > 0;
                        if (string.IsNullOrEmpty(threadId) && !fromMe && !string.IsNullOrEmpty(senderId) && senderId != "0")
                            threadId = senderId;

                        if (_dispatcher != null)
                        {
                            await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                            {
                                ZaloConversation targetConv = null;
                                if (!string.IsNullOrEmpty(threadId))
                                {
                                    targetConv = ZaloApp.Conversations.FirstOrDefault(c => c.ThreadId == threadId);
                                    if (targetConv == null)
                                    {
                                        targetConv = new ZaloConversation
                                        {
                                            ThreadId = threadId,
                                            IsGroup = false,
                                            Name = ZaloApp.Friends.FirstOrDefault(f => f.UserId == threadId)?.Name ?? ("Người dùng " + threadId)
                                        };
                                        ZaloApp.Conversations.Add(targetConv);
                                    }
                                }
                                if (targetConv == null && !string.IsNullOrEmpty(threadId))
                                    targetConv = ZaloApp.GetOrCreate(threadId, isGroup);

                                if (targetConv != null && targetConv.SeenIds.Add(msgId))
                                {
                                    string stickerId = ExtractStickerId(msgContent);
                                    bool isSticker = msgType == 5;
                                    targetConv.Messages.Add(new ZaloChatMessage
                                    {
                                        Id = msgId,
                                        Sender = fromMe ? "Tôi" : targetConv.Name,
                                        SenderAvatarBrush = ZaloAvatar.ColorFor(fromMe ? "Tôi" : targetConv.Name),
                                        Text = isSticker && stickerId.Length > 0 ? string.Empty : displayText,
                                        MsgType = isSticker ? "5" : msgType.ToString(),
                                        StickerId = stickerId,
                                        MediaUrl = isSticker && stickerId.Length > 0
                                            ? "https://zalo-api.zadn.vn/api/emoticon/sticker/webpc?eid=" + Uri.EscapeDataString(stickerId) + "&size=130&version=4"
                                            : string.Empty,
                                        FromMe = fromMe,
                                        ShowSender = false,
                                        Ts = ts
                                    });

                                    if (ts >= targetConv.LastTs)
                                    {
                                        targetConv.LastText = displayText;
                                        targetConv.LastTs = ts;
                                    }
                                }
                            });
                        }

                        count++;
                    }
                    NativeSqlite.Finalize(stmt);
                }
                else
                {
                    ZaloLog.Add("[PhoneSync] ChatContent query prepare failed on part " + index);
                }

                NativeSqlite.Close(db);
            }
            catch (Exception ex)
            {
                ZaloLog.Add("[PhoneSync] Import part " + index + " error: " + ex.Message);
            }
            finally
            {
                try
                {
                    if (tempPath != null)
                    {
                        var file = await StorageFile.GetFileFromPathAsync(tempPath);
                        await file.DeleteAsync();
                    }
                }
                catch { }
            }

            return count;
        }

        private static int FindColumn(Dictionary<string, int> columns, params string[] names)
        {
            foreach (string name in names)
            {
                int index;
                if (columns.TryGetValue(name, out index)) return index;
            }
            return -1;
        }

        private static string ReadColumn(IntPtr stmt, int index, string fallback)
        {
            if (index < 0) return fallback;
            string value = NativeSqlite.ColumnText(stmt, index);
            if (value != null) return value;
            return NativeSqlite.ColumnInt64(stmt, index).ToString();
        }

        private static long ParseLong(string value)
        {
            long result;
            return long.TryParse(value, out result) ? result : 0;
        }

        private static string ExtractText(string content, int msgType)
        {
            if (string.IsNullOrEmpty(content))
            {
                switch (msgType)
                {
                    case 2: return "[Hình ảnh]";
                    case 3: return "[Tin nhắn thoại]";
                    case 4: return "[Video]";
                    case 5: return "[Nhãn dán]";
                    default: return "[Tin nhắn]";
                }
            }

            if (content.TrimStart().StartsWith("{"))
            {
                try
                {
                    JsonObject o = ZaloJson.ParseObject(content);
                    if (o != null)
                    {
                        if (o.ContainsKey("phone") || o.ContainsKey("phoneNumber") || o.ContainsKey("qrCodeUrl") || o.ContainsKey("contactUid"))
                        {
                            string phone = ZaloAuth.Str(o, "phone");
                            if (phone.Length == 0) phone = ZaloAuth.Str(o, "phoneNumber");
                            string name = ZaloAuth.Str(o, "displayName");
                            if (name.Length == 0) name = ZaloAuth.Str(o, "name");
                            return "[Danh thiếp] " + (name.Length > 0 ? name : phone);
                        }
                        if (o.ContainsKey("content")) return ZaloAuth.Str(o, "content");
                        if (o.ContainsKey("title")) return ZaloAuth.Str(o, "title");
                        if (o.ContainsKey("description")) return ZaloAuth.Str(o, "description");
                    }
                }
                catch { }
            }

            return content;
        }

        private static string ExtractStickerId(string content)
        {
            JsonObject value;
            if (string.IsNullOrWhiteSpace(content) || !JsonObject.TryParse(content, out value)) return string.Empty;
            string[] keys = { "stickerId", "sticker_id", "eid", "id" };
            foreach (string key in keys)
            {
                string id = ZaloAuth.Str(value, key);
                if (id.Length > 0) return id;
            }
            IJsonValue nested;
            if (value.TryGetValue("content", out nested) && nested.ValueType == JsonValueType.Object)
            {
                foreach (string key in keys)
                {
                    string id = ZaloAuth.Str(nested.GetObject(), key);
                    if (id.Length > 0) return id;
                }
            }
            return string.Empty;
        }

        private static int FindBytes(byte[] haystack, byte[] needle, int startIndex)
        {
            if (haystack == null || needle == null || needle.Length == 0) return -1;
            for (int i = startIndex; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private static bool StartsWith(byte[] array, byte[] prefix, int offset)
        {
            if (offset + prefix.Length > array.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
            {
                if (array[offset + i] != prefix[i]) return false;
            }
            return true;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
        }
    }
}
