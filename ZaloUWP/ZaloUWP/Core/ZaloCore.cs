// Stand-alone Zalo client, stage 1: sign in with a QR code, keep the session, and prove it works
// by loading the friend list. It talks to Zalo the way Zalo Web does, following the open-source
// zca-js library (MIT licence, https://github.com/RFS-ADRENO/zca-js).
//
// This is an UNOFFICIAL client. Zalo may restrict accounts that use it; use a spare account.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace ZaloLite
{
    /// <summary>A running log shown on screen so problems can be copied and reported. Never holds secrets.</summary>
    public static class ZaloLog
    {
        private static readonly StringBuilder Buffer = new StringBuilder();
        public static event Action Changed;

        public static string Text { get { return Buffer.ToString(); } }

        public static void Add(string line)
        {
            Buffer.AppendLine(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            if (Buffer.Length > 20000) Buffer.Remove(0, Buffer.Length - 15000);
            Action handler = Changed;
            if (handler != null) handler();
        }
    }

    public static class ZaloCrypto
    {
        public static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static string Md5Hex(string text)
        {
            using (MD5 md5 = MD5.Create())
                return ToHex(md5.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        // AES-CBC, PKCS7 padding and an all-zero IV, exactly like Zalo Web.
        private static byte[] AesCbc(byte[] key, byte[] data, bool encrypt)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.IV = new byte[16];
                using (ICryptoTransform transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor())
                    return transform.TransformFinalBlock(data, 0, data.Length);
            }
        }

        /// <summary>Encrypts with a key given as a plain 32-character string (login-time parameters).</summary>
        public static string EncodeWithTextKey(string textKey, string message, bool hex, bool upper)
        {
            byte[] cipher = AesCbc(Encoding.UTF8.GetBytes(textKey), Encoding.UTF8.GetBytes(message), true);
            string result = hex ? ToHex(cipher) : Convert.ToBase64String(cipher);
            return upper ? result.ToUpperInvariant() : result;
        }

        /// <summary>Decrypts a login response whose key is a plain 32-character string.</summary>
        public static string DecodeWithTextKey(string textKey, string data)
        {
            byte[] cipher = Convert.FromBase64String(Uri.UnescapeDataString(data));
            return Encoding.UTF8.GetString(AesCbc(Encoding.UTF8.GetBytes(textKey), cipher, false));
        }

        /// <summary>Encrypts request parameters with the session key (base64).</summary>
        public static string Encode(string secretKeyBase64, string text)
        {
            byte[] cipher = AesCbc(Convert.FromBase64String(secretKeyBase64), Encoding.UTF8.GetBytes(text), true);
            return Convert.ToBase64String(cipher);
        }

        /// <summary>Decrypts a response body with the session key (base64).</summary>
        public static string Decode(string secretKeyBase64, string data)
        {
            byte[] cipher = Convert.FromBase64String(Uri.UnescapeDataString(data));
            return Encoding.UTF8.GetString(AesCbc(Convert.FromBase64String(secretKeyBase64), cipher, false));
        }

        /// <summary>MD5 signature over the sorted parameter values.</summary>
        public static string SignKey(string type, SortedDictionary<string, string> parameters)
        {
            var sb = new StringBuilder("zsecure" + type);
            foreach (KeyValuePair<string, string> pair in parameters) sb.Append(pair.Value);
            return Md5Hex(sb.ToString());
        }
    }

    /// <summary>Builds the zcid / zcid_ext / key used to encrypt the login request.</summary>
    public sealed class ZaloParamsEncryptor
    {
        private static readonly Random Rng = new Random();

        public string Zcid { get; private set; }
        public string ZcidExt { get; private set; }
        public string EncryptKey { get; private set; }
        public const string EncVersion = "v2";

        public ZaloParamsEncryptor(int type, string imei, long firstLaunchTime)
        {
            Zcid = ZaloCrypto.EncodeWithTextKey("3FC4F0D2AB50057BCE0D90D9187A22B1",
                type + "," + imei + "," + firstLaunchTime, true, true);
            ZcidExt = RandomHex();

            string md5Upper = ZaloCrypto.Md5Hex(ZcidExt).ToUpperInvariant();
            string md5Even = Pick(md5Upper, 0);
            string zcidEven = Pick(Zcid, 0);
            char[] zcidOdd = Pick(Zcid, 1).ToCharArray();
            Array.Reverse(zcidOdd);

            EncryptKey = md5Even.Substring(0, 8) + zcidEven.Substring(0, 12) + new string(zcidOdd, 0, 12);
        }

        // every second character, starting at index 0 (even) or 1 (odd)
        private static string Pick(string text, int start)
        {
            var sb = new StringBuilder();
            for (int i = start; i < text.Length; i += 2) sb.Append(text[i]);
            return sb.ToString();
        }

        private static string RandomHex()
        {
            int length = Rng.Next(6, 13);
            var sb = new StringBuilder();
            for (int i = 0; i < length; i++) sb.Append(Rng.Next(16).ToString("x"));
            return sb.ToString();
        }
    }

    public sealed class ZaloSavedCookie
    {
        public string Name, Value, Domain, Path;
        public long ExpiresMs; // 0 = session cookie
        public bool Secure, HttpOnly;
    }

    public sealed class ZaloResponse
    {
        public int Status;
        public string Body = string.Empty;
    }

    /// <summary>HTTP with a browser-like identity. Cookies are kept by Windows' own cookie manager.</summary>
    public sealed class ZaloHttp
    {
        // Hosts whose cookies we keep or read; the cookie manager returns domain cookies for sub-hosts too.
        private static readonly string[] CookieHosts =
        {
            "zalo.me", "id.zalo.me", "chat.zalo.me", "wpa.chat.zalo.me", "jr.chat.zalo.me"
        };

        private readonly HttpBaseProtocolFilter _filter;
        private readonly HttpClient _client;
        public string UserAgent { get; private set; }

        public ZaloHttp(string userAgent)
        {
            UserAgent = userAgent;
            _filter = new HttpBaseProtocolFilter { AllowAutoRedirect = false, AutomaticDecompression = true };
            _filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.MostRecent;
            _filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
            _client = new HttpClient(_filter);
        }

        public async Task<ZaloResponse> SendAsync(HttpMethod method, string url,
            IDictionary<string, string> headers, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
        {
            string currentUrl = url;
            HttpMethod currentMethod = method;
            IEnumerable<KeyValuePair<string, string>> currentForm = form;
            var currentHeaders = headers == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(headers);

            for (int hop = 0; hop < 10; hop++)
            {
                using (var request = new HttpRequestMessage(currentMethod, new Uri(currentUrl)))
                {
                    request.Headers.UserAgent.Clear();
                    request.Headers.UserAgent.TryParseAdd(UserAgent);
                    SetHeader(request, "Accept", "application/json, text/plain, */*");
                    SetHeader(request, "Accept-Language", "en-US,en;q=0.9");
                    SetHeader(request, "Origin", "https://chat.zalo.me");
                    SetHeader(request, "Referer", "https://chat.zalo.me/");
                    foreach (KeyValuePair<string, string> h in currentHeaders) SetHeader(request, h.Key, h.Value);
                    if (currentForm != null) request.Content = new HttpFormUrlEncodedContent(currentForm);

                    using (HttpResponseMessage response =
                        await _client.SendRequestAsync(request, HttpCompletionOption.ResponseContentRead).AsTask(ct))
                    {
                        int status = (int)response.StatusCode;
                        string location;
                        if (status >= 300 && status < 400 && response.Headers.TryGetValue("Location", out location))
                        {
                            currentUrl = new Uri(new Uri(currentUrl), location).ToString();
                            currentMethod = HttpMethod.Get;
                            currentForm = null;
                            currentHeaders["Referer"] = "https://id.zalo.me/";
                            continue;
                        }

                        string body = response.Content == null
                            ? string.Empty
                            : await response.Content.ReadAsStringAsync().AsTask(ct);
                        return new ZaloResponse { Status = status, Body = body };
                    }
                }
            }
            throw new InvalidOperationException("Too many redirects.");
        }

        /// <summary>Downloads binary media through the same cookie manager and user agent as API calls.</summary>
        public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct)
        {
            string currentUrl = url;
            for (int hop = 0; hop < 10; hop++)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(currentUrl)))
                {
                    request.Headers.UserAgent.Clear();
                    request.Headers.UserAgent.TryParseAdd(UserAgent);
                    SetHeader(request, "Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
                    SetHeader(request, "Accept-Language", "en-US,en;q=0.9");
                    SetHeader(request, "Referer", "https://chat.zalo.me/");
                    using (HttpResponseMessage response = await _client.SendRequestAsync(request,
                        HttpCompletionOption.ResponseContentRead).AsTask(ct))
                    {
                        int status = (int)response.StatusCode;
                        string location;
                        if (status >= 300 && status < 400 && response.Headers.TryGetValue("Location", out location))
                        {
                            currentUrl = new Uri(new Uri(currentUrl), location).ToString();
                            continue;
                        }
                        if (!response.IsSuccessStatusCode || response.Content == null)
                            throw new InvalidOperationException("Media request failed (HTTP " + status + ").");
                        var buffer = await response.Content.ReadAsBufferAsync().AsTask(ct);
                        if (buffer == null || buffer.Length == 0 || buffer.Length > 25 * 1024 * 1024)
                            throw new InvalidOperationException("Media response is empty or too large.");
                        var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
                        var bytes = new byte[(int)buffer.Length];
                        reader.ReadBytes(bytes);
                        reader.Dispose();
                        return bytes;
                    }
                }
            }
            throw new InvalidOperationException("Too many media redirects.");
        }

        /// <summary>Streams authenticated media directly to storage and enforces the size limit per chunk.</summary>
        public async Task<ulong> DownloadToFileAsync(string url, StorageFile destination, ulong maxBytes, CancellationToken ct)
        {
            if (destination == null) throw new ArgumentNullException("destination");
            string currentUrl = url;
            for (int hop = 0; hop < 10; hop++)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(currentUrl)))
                {
                    request.Headers.UserAgent.Clear();
                    request.Headers.UserAgent.TryParseAdd(UserAgent);
                    SetHeader(request, "Accept", "*/*");
                    SetHeader(request, "Accept-Language", "en-US,en;q=0.9");
                    SetHeader(request, "Referer", "https://chat.zalo.me/");
                    using (HttpResponseMessage response = await _client.SendRequestAsync(request,
                        HttpCompletionOption.ResponseHeadersRead).AsTask(ct))
                    {
                        int status = (int)response.StatusCode;
                        string location;
                        if (status >= 300 && status < 400 && response.Headers.TryGetValue("Location", out location))
                        {
                            currentUrl = new Uri(new Uri(currentUrl), location).ToString();
                            continue;
                        }
                        if (!response.IsSuccessStatusCode || response.Content == null)
                            throw new InvalidOperationException("Media request failed (HTTP " + status + ").");

                        ulong total = 0;
                        using (IInputStream input = await response.Content.ReadAsInputStreamAsync().AsTask(ct))
                        using (IRandomAccessStream output = await destination.OpenAsync(FileAccessMode.ReadWrite).AsTask(ct))
                        {
                            output.Size = 0;
                            output.Seek(0);
                            IBuffer chunkBuffer = new Windows.Storage.Streams.Buffer(64 * 1024);
                            while (true)
                            {
                                IBuffer chunk = await input.ReadAsync(chunkBuffer, chunkBuffer.Capacity,
                                    InputStreamOptions.Partial).AsTask(ct);
                                if (chunk.Length == 0) break;
                                total += chunk.Length;
                                if (total > maxBytes)
                                    throw new InvalidOperationException("Tệp vượt giới hạn tải " + (maxBytes / (1024 * 1024)) + " MB.");
                                await output.WriteAsync(chunk).AsTask(ct);
                            }
                            if (total == 0) throw new InvalidOperationException("Media response is empty.");
                            await output.FlushAsync().AsTask(ct);
                        }
                        return total;
                    }
                }
            }
            throw new InvalidOperationException("Too many media redirects.");
        }

        public async Task<ZaloResponse> SendMultipartAsync(string url, string fileName, byte[] bytes, CancellationToken ct,
            string fieldName = "chunkContent", string contentType = "application/octet-stream")
        {
            string boundary = "----ZaloUWP" + Guid.NewGuid().ToString("N");
            using (var body = new System.IO.MemoryStream())
            {
                byte[] head = Encoding.UTF8.GetBytes("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + fieldName.Replace("\"", "") + "\"; filename=\"" + fileName.Replace("\"", "") + "\"\r\nContent-Type: " + contentType + "\r\n\r\n");
                byte[] tail = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");
                body.Write(head, 0, head.Length);
                body.Write(bytes, 0, bytes.Length);
                body.Write(tail, 0, tail.Length);
                using (var writer = new Windows.Storage.Streams.DataWriter())
                {
                    writer.WriteBytes(body.ToArray());
                    using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url)))
                    {
                        request.Headers.UserAgent.Clear();
                        request.Headers.UserAgent.TryParseAdd(UserAgent);
                        SetHeader(request, "Accept", "application/json, text/plain, */*");
                        SetHeader(request, "Accept-Language", "en-US,en;q=0.9");
                        SetHeader(request, "Origin", "https://chat.zalo.me");
                        SetHeader(request, "Referer", "https://chat.zalo.me/");
                        var content = new HttpBufferContent(writer.DetachBuffer());
                        content.Headers.ContentType = new Windows.Web.Http.Headers.HttpMediaTypeHeaderValue("multipart/form-data; boundary=" + boundary);
                        request.Content = content;
                        using (HttpResponseMessage response = await _client.SendRequestAsync(request, HttpCompletionOption.ResponseContentRead).AsTask(ct))
                        {
                            string responseBody = response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync().AsTask(ct);
                            return new ZaloResponse { Status = (int)response.StatusCode, Body = responseBody };
                        }
                    }
                }
            }
        }

        public async Task<ZaloResponse> UploadProfileImageAsync(string url, byte[] jpegBytes, CancellationToken ct)
        {
            if (jpegBytes == null || jpegBytes.Length == 0) throw new InvalidOperationException("Ảnh đại diện bị rỗng.");
            string boundary = "----ZaloUwpAvatar" + Guid.NewGuid().ToString("N");
            using (var body = new System.IO.MemoryStream())
            {
                byte[] head = Encoding.UTF8.GetBytes("--" + boundary + "\r\nContent-Disposition: form-data; name=\"fileContent\"; filename=\"blob\"\r\nContent-Type: image/jpeg\r\n\r\n");
                byte[] tail = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");
                body.Write(head, 0, head.Length);
                body.Write(jpegBytes, 0, jpegBytes.Length);
                body.Write(tail, 0, tail.Length);
                using (var writer = new Windows.Storage.Streams.DataWriter())
                using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url)))
                {
                    writer.WriteBytes(body.ToArray());
                    request.Headers.UserAgent.Clear();
                    request.Headers.UserAgent.TryParseAdd(UserAgent);
                    SetHeader(request, "Accept", "application/json, text/plain, */*");
                    SetHeader(request, "Accept-Language", "vi-VN,vi;q=0.9,en-US;q=0.7,en;q=0.6");
                    SetHeader(request, "Origin", "https://chat.zalo.me");
                    SetHeader(request, "Referer", "https://chat.zalo.me/");
                    var content = new HttpBufferContent(writer.DetachBuffer());
                    content.Headers.ContentType = new Windows.Web.Http.Headers.HttpMediaTypeHeaderValue("multipart/form-data; boundary=" + boundary);
                    request.Content = content;
                    using (HttpResponseMessage response = await _client.SendRequestAsync(request,
                        HttpCompletionOption.ResponseContentRead).AsTask(ct))
                    {
                        string responseBody = response.Content == null ? string.Empty
                            : await response.Content.ReadAsStringAsync().AsTask(ct);
                        return new ZaloResponse { Status = (int)response.StatusCode, Body = responseBody };
                    }
                }
            }
        }

        private static void SetHeader(HttpRequestMessage request, string name, string value)
        {
            try
            {
                request.Headers.Remove(name);
                request.Headers.TryAppendWithoutValidation(name, value);
            }
            catch (Exception)
            {
                // some headers are managed by Windows; a missing one is not fatal
            }
        }

        /// <summary>The Cookie header value for chat.zalo.me (used for the WebSocket if Windows does not add it).</summary>
        public string CookieHeader()
        {
            return string.Join("; ", _filter.CookieManager.GetCookies(new Uri("https://chat.zalo.me"))
                .Select(c => c.Name + "=" + c.Value));
        }

        public void ClearCookies()
        {
            foreach (HttpCookie cookie in AllCookies()) _filter.CookieManager.DeleteCookie(cookie);
        }

        private List<HttpCookie> AllCookies()
        {
            var result = new List<HttpCookie>();
            var seen = new HashSet<string>();
            foreach (string host in CookieHosts)
            {
                foreach (HttpCookie c in _filter.CookieManager.GetCookies(new Uri("https://" + host)))
                    if (seen.Add(c.Name + "|" + c.Domain + "|" + c.Path)) result.Add(c);
            }
            return result;
        }

        public List<ZaloSavedCookie> ExportCookies()
        {
            return AllCookies().Select(c => new ZaloSavedCookie
            {
                Name = c.Name,
                Value = c.Value,
                Domain = c.Domain,
                Path = c.Path,
                Secure = c.Secure,
                HttpOnly = c.HttpOnly,
                ExpiresMs = c.Expires.HasValue ? c.Expires.Value.ToUnixTimeMilliseconds() : 0
            }).ToList();
        }

        public void ImportCookies(IEnumerable<ZaloSavedCookie> cookies)
        {
            foreach (ZaloSavedCookie saved in cookies)
            {
                var cookie = new HttpCookie(saved.Name, saved.Domain, saved.Path)
                {
                    Value = saved.Value,
                    Secure = saved.Secure,
                    HttpOnly = saved.HttpOnly
                };
                if (saved.ExpiresMs > 0) cookie.Expires = DateTimeOffset.FromUnixTimeMilliseconds(saved.ExpiresMs);
                _filter.CookieManager.SetCookie(cookie);
            }
        }
    }

    public sealed class ZaloSession
    {
        public ZaloHttp Http;
        public string Imei;
        public string UserAgent;
        public string SecretKey;
        public string Uid;
        public string DisplayName;
        public string AvatarUrl;
        public string PhoneNumber;
        public string ProfileStatus;
        public string ProfileDob;
        public int ProfileGender = -1;
        public bool HasBusinessCategory;
        public JsonObject ServiceMap;
        public JsonObject ExtraVer;
        public List<string> WsUrls = new List<string>();
        public JsonObject Settings;

        public string ServiceUrl(string name)
        {
            return ServiceMap.GetNamedArray(name)[0].GetString();
        }
    }

    public static class ZaloAuth
    {
        public const int ApiType = 30;
        public const int ApiVersion = 671;
        public const string QrUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0";

        private const string AcceptLanguage = "vi-VN,vi;q=0.9,fr-FR;q=0.8,fr;q=0.7,en-US;q=0.6,en;q=0.5";

        // ------------------------------------------------------------------ helpers

        public static string Str(JsonObject o, string key)
        {
            IJsonValue v;
            if (o == null || !o.TryGetValue(key, out v)) return string.Empty;
            switch (v.ValueType)
            {
                case JsonValueType.String: return v.GetString();
                case JsonValueType.Number: return v.GetNumber().ToString("F0");
                case JsonValueType.Boolean: return v.GetBoolean() ? "true" : "false";
                default: return string.Empty;
            }
        }

        public static double Num(JsonObject o, string key)
        {
            IJsonValue v;
            return o != null && o.TryGetValue(key, out v) && v.ValueType == JsonValueType.Number ? v.GetNumber() : 0;
        }

        private static string BuildUrl(string baseUrl, IEnumerable<KeyValuePair<string, string>> query)
        {
            var sb = new StringBuilder(baseUrl);
            char sep = baseUrl.Contains("?") ? '&' : '?';
            foreach (KeyValuePair<string, string> pair in query)
            {
                sb.Append(sep).Append(Uri.EscapeDataString(pair.Key)).Append('=').Append(Uri.EscapeDataString(pair.Value));
                sep = '&';
            }
            return sb.ToString();
        }

        /// <summary>Adds the zpw_ver / zpw_type parameters every Zalo API call carries.</summary>
        public static string ApiUrl(string baseUrl, params KeyValuePair<string, string>[] query)
        {
            var all = new List<KeyValuePair<string, string>>(query);
            all.Add(new KeyValuePair<string, string>("zpw_ver", ApiVersion.ToString()));
            all.Add(new KeyValuePair<string, string>("zpw_type", ApiType.ToString()));
            return BuildUrl(baseUrl, all);
        }

        private static string PathOf(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u) ? u.Host + u.AbsolutePath : "?";
        }

        private static JsonObject ParseObject(ZaloResponse response, string what)
        {
            JsonObject parsed;
            string body = response == null ? string.Empty : response.Body ?? string.Empty;
            if (!JsonObject.TryParse(body, out parsed))
            {
                int status = response == null ? 0 : response.Status;
                // Gateways often return an HTML error page (for example nginx 404).
                // Never pass that markup into user-facing dialogs or status banners.
                string detail = status == 404
                    ? "API không tồn tại hoặc máy chủ đã thay đổi đường dẫn (HTTP 404)."
                    : status >= 500
                        ? "Máy chủ Zalo đang lỗi (HTTP " + status + ")."
                        : status > 0
                            ? "Máy chủ trả về dữ liệu không hợp lệ (HTTP " + status + ")."
                            : "Máy chủ trả về dữ liệu không hợp lệ.";
                throw new InvalidOperationException(what + ": " + detail);
            }
            return parsed;
        }

        private static string NewImei(string userAgent)
        {
            return Guid.NewGuid().ToString() + "-" + ZaloCrypto.Md5Hex(userAgent);
        }

        // ------------------------------------------------------------------ QR sign-in

        private static Dictionary<string, string> QrHeaders(string referer)
        {
            return new Dictionary<string, string>
            {
                { "Accept", "*/*" },
                { "Accept-Language", AcceptLanguage },
                { "Priority", "u=1, i" },
                { "sec-ch-ua", "\"Chromium\";v=\"130\", \"Google Chrome\";v=\"130\", \"Not?A_Brand\";v=\"99\"" },
                { "sec-ch-ua-mobile", "?0" },
                { "sec-ch-ua-platform", "\"Windows\"" },
                { "sec-fetch-dest", "empty" },
                { "sec-fetch-mode", "cors" },
                { "sec-fetch-site", "same-origin" },
                { "Referer", referer },
                { "Referrer-Policy", "strict-origin-when-cross-origin" }
            };
        }

        private static async Task<JsonObject> QrPostAsync(ZaloHttp http, string url, string referer,
            List<KeyValuePair<string, string>> form, CancellationToken ct)
        {
            ZaloResponse r = await http.SendAsync(HttpMethod.Post, url, QrHeaders(referer), form, ct);
            ZaloLog.Add("POST " + PathOf(url) + " -> " + r.Status);
            return ParseObject(r, PathOf(url));
        }

        private static List<KeyValuePair<string, string>> Form(params string[] pairs)
        {
            var list = new List<KeyValuePair<string, string>>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                list.Add(new KeyValuePair<string, string>(pairs[i], pairs[i + 1]));
            return list;
        }

        /// <summary>
        /// Signs in by showing a QR code to scan with the Zalo app on another phone.
        /// onQr receives the PNG bytes of each QR code (a new one appears if it expires).
        /// </summary>
        public static async Task<ZaloSession> LoginWithQrAsync(Action<byte[]> onQr, Action<string> onStatus, CancellationToken ct)
        {
            var http = new ZaloHttp(QrUserAgent);
            http.ClearCookies();

            const string accountPage = "https://id.zalo.me/account?continue=https%3A%2F%2Fzalo.me%2Fpc";
            const string accountPageChat = "https://id.zalo.me/account?continue=https%3A%2F%2Fchat.zalo.me%2F";

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                onStatus("Đang kết nối tới Zalo...");

                // 1. the login page tells us which version of the login script is current
                var pageHeaders = new Dictionary<string, string>
                {
                    { "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8" },
                    { "Accept-Language", AcceptLanguage },
                    { "sec-fetch-dest", "document" },
                    { "sec-fetch-mode", "navigate" },
                    { "sec-fetch-site", "same-site" },
                    { "Upgrade-Insecure-Requests", "1" },
                    { "Referer", "https://chat.zalo.me/" }
                };
                ZaloResponse page = await http.SendAsync(HttpMethod.Get,
                    "https://id.zalo.me/account?continue=https%3A%2F%2Fchat.zalo.me%2F", pageHeaders, null, ct);
                ZaloLog.Add("GET id.zalo.me/account -> " + page.Status);

                Match m = Regex.Match(page.Body, @"https://stc-zlogin\.zdn\.vn/main-([\d.]+)\.js");
                if (!m.Success) throw new InvalidOperationException("Could not read the Zalo login version (the login page may have changed).");
                string version = m.Groups[1].Value;
                ZaloLog.Add("Login script version " + version);

                // 2. two warm-up calls the browser makes (answers are not needed)
                await QrPostAsync(http, "https://id.zalo.me/account/logininfo", accountPage,
                    Form("continue", "https://zalo.me/pc", "v", version), ct);
                await QrPostAsync(http, "https://id.zalo.me/account/verify-client", accountPage,
                    Form("type", "device", "continue", "https://zalo.me/pc", "v", version), ct);

                // 3. ask for a QR code
                JsonObject gen = await QrPostAsync(http, "https://id.zalo.me/account/authen/qr/generate", accountPage,
                    Form("continue", "https://zalo.me/pc", "v", version), ct);
                IJsonValue dataValue;
                if (!gen.TryGetValue("data", out dataValue) || dataValue.ValueType != JsonValueType.Object)
                    throw new InvalidOperationException("Zalo did not return a QR code: " + Str(gen, "error_message"));
                JsonObject qr = dataValue.GetObject();
                string code = Str(qr, "code");
                string image = Str(qr, "image");
                const string prefix = "data:image/png;base64,";
                if (image.StartsWith(prefix)) image = image.Substring(prefix.Length);
                onQr(Convert.FromBase64String(image));
                onStatus("Hãy quét mã QR bằng app Zalo trên một điện thoại khác.");

                // 4. wait (up to 100 seconds) for the scan and the confirmation
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    linked.CancelAfter(TimeSpan.FromSeconds(100));
                    try
                    {
                        JsonObject scan;
                        do
                        {
                            scan = await QrPostAsync(http, "https://id.zalo.me/account/authen/qr/waiting-scan", accountPageChat,
                                Form("code", code, "continue", "https://chat.zalo.me/", "v", version), linked.Token);
                        } while (Num(scan, "error_code") == 8);

                        IJsonValue scanData;
                        if (!scan.TryGetValue("data", out scanData) || scanData.ValueType != JsonValueType.Object)
                            throw new InvalidOperationException("Could not read the scan result: " + Str(scan, "error_message"));
                        onStatus("Đã quét bởi " + Str(scanData.GetObject(), "display_name") + ". Hãy bấm xác nhận trên điện thoại đó.");

                        JsonObject confirm;
                        do
                        {
                            confirm = await QrPostAsync(http, "https://id.zalo.me/account/authen/qr/waiting-confirm", accountPageChat,
                                Form("code", code, "gToken", "", "gAction", "CONFIRM_QR",
                                     "continue", "https://chat.zalo.me/", "v", version), linked.Token);
                        } while (Num(confirm, "error_code") == 8);

                        double confirmCode = Num(confirm, "error_code");
                        if (confirmCode == -13) throw new InvalidOperationException("Bạn đã từ chối đăng nhập trên điện thoại.");
                        if (confirmCode != 0) throw new InvalidOperationException("Login failed: " + Str(confirm, "error_message") + " (" + confirmCode + ")");
                    }
                    catch (OperationCanceledException)
                    {
                        if (ct.IsCancellationRequested) throw;
                        ZaloLog.Add("QR code expired, making a new one.");
                        onStatus("Mã QR đã hết hạn, đang tạo mã mới...");
                        continue; // new QR
                    }
                }

                // 5. finish the browser sign-in, then check we are really logged in
                var sessionHeaders = new Dictionary<string, string>
                {
                    { "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8" },
                    { "Accept-Language", AcceptLanguage },
                    { "sec-fetch-dest", "document" },
                    { "sec-fetch-mode", "navigate" },
                    { "sec-fetch-site", "same-origin" },
                    { "Upgrade-Insecure-Requests", "1" },
                    { "Referer", accountPageChat }
                };
                ZaloResponse check = await http.SendAsync(HttpMethod.Get,
                    "https://id.zalo.me/account/checksession?continue=https%3A%2F%2Fchat.zalo.me%2Findex.html",
                    sessionHeaders, null, ct);
                ZaloLog.Add("GET checksession -> " + check.Status);

                var infoHeaders = QrHeaders("https://chat.zalo.me/");
                infoHeaders["sec-fetch-site"] = "same-site";
                ZaloResponse info = await http.SendAsync(HttpMethod.Get, "https://jr.chat.zalo.me/jr/userinfo", infoHeaders, null, ct);
                ZaloLog.Add("GET jr/userinfo -> " + info.Status);
                JsonObject infoObj = ParseObject(info, "userinfo");
                IJsonValue infoData;
                if (!infoObj.TryGetValue("data", out infoData) || infoData.ValueType != JsonValueType.Object
                    || Str(infoData.GetObject(), "logged") != "true")
                    throw new InvalidOperationException("Zalo did not confirm the sign-in.");

                onStatus("Đã đăng nhập, đang khởi tạo phiên...");
                return await LoginWithSavedCookiesAsync(http, NewImei(QrUserAgent), QrUserAgent, ct);
            }
        }

        // ------------------------------------------------------------------ session from cookies

        public static async Task<ZaloSession> LoginWithSavedCookiesAsync(ZaloHttp http, string imei, string userAgent, CancellationToken ct)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // getLoginInfo: the request parameters are encrypted with a key made from the IMEI
            string plain = "{\"computer_name\":\"Web\",\"imei\":\"" + imei + "\",\"language\":\"vi\",\"ts\":" + now + "}";
            var encryptor = new ZaloParamsEncryptor(ApiType, imei, now);
            string encrypted = ZaloCrypto.EncodeWithTextKey(encryptor.EncryptKey, plain, false, false);

            var signed = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                { "zcid", encryptor.Zcid },
                { "zcid_ext", encryptor.ZcidExt },
                { "enc_ver", ZaloParamsEncryptor.EncVersion },
                { "params", encrypted },
                { "type", ApiType.ToString() },
                { "client_version", ApiVersion.ToString() }
            };
            string sign = ZaloCrypto.SignKey("getlogininfo", signed);

            var query = signed.ToList();
            query.Add(new KeyValuePair<string, string>("signkey", sign));
            query.Add(new KeyValuePair<string, string>("nretry", "0"));
            string loginUrl = ApiUrl("https://wpa.chat.zalo.me/api/login/getLoginInfo", query.ToArray());

            ZaloResponse loginResponse = await http.SendAsync(HttpMethod.Get, loginUrl, null, null, ct);
            ZaloLog.Add("GET getLoginInfo -> " + loginResponse.Status);
            JsonObject loginOuter = ParseObject(loginResponse, "getLoginInfo");
            if (Num(loginOuter, "error_code") != 0)
                throw new InvalidOperationException("Login was refused: " + Str(loginOuter, "error_message") + " (" + Num(loginOuter, "error_code") + ")");

            string decrypted = ZaloCrypto.DecodeWithTextKey(encryptor.EncryptKey, Str(loginOuter, "data"));
            JsonObject loginInner;
            if (!JsonObject.TryParse(decrypted, out loginInner))
                throw new InvalidOperationException("Could not read the login answer.");
            IJsonValue infoValue;
            if (!loginInner.TryGetValue("data", out infoValue) || infoValue.ValueType != JsonValueType.Object)
                throw new InvalidOperationException("The saved login is no longer valid (" + Str(loginInner, "error_message") + ").");
            JsonObject loginInfo = infoValue.GetObject();

            var session = new ZaloSession
            {
                Http = http,
                Imei = imei,
                UserAgent = userAgent,
                SecretKey = Str(loginInfo, "zpw_enk"),
                Uid = Str(loginInfo, "uid"),
                ServiceMap = loginInfo.GetNamedObject("zpw_service_map_v3")
            };
            foreach (IJsonValue ws in loginInfo.GetNamedArray("zpw_ws", new JsonArray())) session.WsUrls.Add(ws.GetString());
            if (session.SecretKey.Length == 0) throw new InvalidOperationException("The login answer has no session key.");

            // getServerInfo: socket settings (kept for the message connection in the next stage)
            var infoParams = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                { "imei", imei },
                { "type", ApiType.ToString() },
                { "client_version", ApiVersion.ToString() },
                { "computer_name", "Web" }
            };
            var serverQuery = infoParams.ToList();
            serverQuery.Add(new KeyValuePair<string, string>("signkey", ZaloCrypto.SignKey("getserverinfo", infoParams)));
            ZaloResponse serverResponse = await http.SendAsync(HttpMethod.Get,
                BuildUrl("https://wpa.chat.zalo.me/api/login/getServerInfo", serverQuery), null, null, ct);
            ZaloLog.Add("GET getServerInfo -> " + serverResponse.Status);
            JsonObject serverOuter = ParseObject(serverResponse, "getServerInfo");
            IJsonValue serverData;
            if (serverOuter.TryGetValue("data", out serverData) && serverData.ValueType == JsonValueType.Object)
            {
                JsonObject s = serverData.GetObject();
                IJsonValue extraVer;
                if (s.TryGetValue("extra_ver", out extraVer) && extraVer.ValueType == JsonValueType.Object)
                    session.ExtraVer = extraVer.GetObject();
                IJsonValue settings;
                if (s.TryGetValue("setttings", out settings) || s.TryGetValue("settings", out settings))
                    if (settings.ValueType == JsonValueType.Object) session.Settings = settings.GetObject();
            }

            ZaloLog.Add("Session ready for account " + session.Uid);
            return session;
        }

        // ------------------------------------------------------------------ calling the API

        /// <summary>Calls an API whose answer is encrypted with the session key and returns the "data" part.</summary>
        public static async Task<IJsonValue> CallAsync(ZaloSession session, HttpMethod method, string url,
            List<KeyValuePair<string, string>> form, CancellationToken ct)
        {
            ZaloResponse r = await session.Http.SendAsync(method, url, null, form, ct);
            return ResolveResponse(session, url, method.Method, r);
        }

        public static async Task<IJsonValue> CallMultipartAsync(ZaloSession session, string url, string fileName, byte[] bytes, CancellationToken ct,
            string fieldName = "chunkContent", string contentType = "application/octet-stream")
        {
            ZaloResponse r = await session.Http.SendMultipartAsync(url, fileName, bytes, ct, fieldName, contentType);
            return ResolveResponse(session, url, "POST", r);
        }

        private static IJsonValue ResolveResponse(ZaloSession session, string url, string method, ZaloResponse r)
        {
            ZaloLog.Add(method + " " + PathOf(url) + " -> " + r.Status);
            JsonObject outer = ParseObject(r, PathOf(url));
            if (Num(outer, "error_code") != 0)
                throw new InvalidOperationException(Str(outer, "error_message") + " (" + Num(outer, "error_code") + ")");

            string decrypted = ZaloCrypto.Decode(session.SecretKey, Str(outer, "data"));
            JsonObject inner = ZaloJson.ParseObject(decrypted);
            if (inner == null) throw new InvalidOperationException("Could not read the answer.");
            if (Num(inner, "error_code") != 0)
                throw new InvalidOperationException(Str(inner, "error_message") + " (" + Num(inner, "error_code") + ")");

            IJsonValue data;
            return inner.TryGetValue("data", out data) ? data : JsonValue.CreateNullValue();
        }

        /// <summary>Loads the friend list. In this stage it only proves that encrypted requests work.</summary>
        public static async Task<List<ZaloFriend>> GetFriendsAsync(ZaloSession session, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            string plain = "{\"incInvalid\":1,\"page\":1,\"count\":20000,\"avatar_size\":120,\"actiontime\":0,\"imei\":\""
                           + session.Imei + "\"}";
            string url = ApiUrl(session.ServiceUrl("profile") + "/api/social/friend/getfriends",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, plain)));

            IJsonValue data = await CallAsync(session, HttpMethod.Get, url, null, ct);
            var friends = new List<ZaloFriend>();
            if (data.ValueType == JsonValueType.Array)
            {
                foreach (IJsonValue item in data.GetArray())
                {
                    if (item.ValueType != JsonValueType.Object) continue;
                    JsonObject o = item.GetObject();
                    string name = Str(o, "displayName");
                    if (name.Length == 0) name = Str(o, "zaloName");
                    friends.Add(new ZaloFriend { UserId = Str(o, "userId"), Name = name, OriginalName = name, Avatar = Str(o, "avatar") });
                }
            }
            try
            {
                Dictionary<string, string> aliases = await GetFriendAliasesAsync(session, ct);
                foreach (ZaloFriend friend in friends)
                {
                    string alias;
                    if (aliases.TryGetValue(friend.UserId ?? string.Empty, out alias))
                    {
                        friend.Alias = alias;
                        friend.Name = alias;
                    }
                }
            }
            catch (Exception ex) { ZaloLog.Add("Friend aliases unavailable: " + ex.Message); }
            return friends;
        }

        public static async Task<Dictionary<string, string>> GetFriendAliasesAsync(ZaloSession session, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int page = 1; page <= 200; page++)
            {
                var payload = new JsonObject();
                payload["page"] = JsonValue.CreateNumberValue(page);
                payload["count"] = JsonValue.CreateNumberValue(100);
                payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);
                string url = ApiUrl(session.ServiceUrl("alias") + "/api/alias/list",
                    new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify())));
                IJsonValue result = await CallAsync(session, HttpMethod.Get, url, null, ct);
                JsonObject root = result != null && result.ValueType == JsonValueType.Object ? result.GetObject() : null;
                IJsonValue dataValue;
                if (root != null && root.TryGetValue("data", out dataValue))
                    root = dataValue.ValueType == JsonValueType.Object ? dataValue.GetObject()
                        : dataValue.ValueType == JsonValueType.String ? ZaloJson.ParseObject(dataValue.GetString()) : root;
                IJsonValue items;
                if (root == null || !root.TryGetValue("items", out items) || items.ValueType != JsonValueType.Array) break;
                foreach (IJsonValue item in items.GetArray())
                {
                    if (item.ValueType != JsonValueType.Object) continue;
                    string id = Str(item.GetObject(), "userId");
                    string alias = Str(item.GetObject(), "alias");
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(alias)) aliases[id] = alias;
                }
                if (items.GetArray().Count < 100) break;
            }
            return aliases;
        }

        public static async Task SetFriendAliasAsync(ZaloSession session, string friendId, string alias, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(friendId)) throw new ArgumentException("Thiếu mã bạn bè.", "friendId");
            string trimmed = (alias ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > 40) throw new ArgumentException("Biệt danh phải có từ 1 đến 40 ký tự.", "alias");
            var payload = new JsonObject();
            payload["friendId"] = JsonValue.CreateStringValue(friendId);
            payload["alias"] = JsonValue.CreateStringValue(trimmed);
            payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);
            string url = ApiUrl(session.ServiceUrl("alias") + "/api/alias/update",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify())));
            await CallAsync(session, HttpMethod.Get, url, null, ct);
        }

        public static async Task RemoveFriendAliasAsync(ZaloSession session, string friendId, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(friendId)) throw new ArgumentException("Thiếu mã bạn bè.", "friendId");
            var payload = new JsonObject();
            payload["friendId"] = JsonValue.CreateStringValue(friendId);
            string url = ApiUrl(session.ServiceUrl("alias") + "/api/alias/remove",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify())));
            await CallAsync(session, HttpMethod.Get, url, null, ct);
        }

        public static async Task<HashSet<string>> GetHiddenConversationKeysAsync(ZaloSession session, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            var payload = new JsonObject();
            payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);
            string url = ApiUrl(session.ServiceUrl("conversation") + "/api/hiddenconvers/get-all",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify())));
            IJsonValue result = await CallAsync(session, HttpMethod.Get, url, null, ct);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            JsonObject data = result != null && result.ValueType == JsonValueType.Object ? result.GetObject() : null;
            IJsonValue threadsValue;
            if (data != null && data.TryGetValue("threads", out threadsValue) && threadsValue.ValueType == JsonValueType.Array)
            {
                foreach (IJsonValue value in threadsValue.GetArray())
                {
                    if (value.ValueType != JsonValueType.Object) continue;
                    JsonObject thread = value.GetObject();
                    string threadId = Str(thread, "thread_id");
                    if (threadId.Length == 0) continue;
                    keys.Add((Num(thread, "is_group") == 1 ? "g:" : "u:") + threadId);
                }
            }
            return keys;
        }

        public static async Task SetConversationHiddenAsync(ZaloSession session, string threadId, bool isGroup,
            bool hidden, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(threadId)) throw new ArgumentException("Thiếu mã cuộc trò chuyện.", "threadId");
            var threads = new JsonArray();
            var thread = new JsonObject();
            thread["thread_id"] = JsonValue.CreateStringValue(threadId);
            thread["is_group"] = JsonValue.CreateNumberValue(isGroup ? 1 : 0);
            threads.Add(thread);

            var payload = new JsonObject();
            payload[hidden ? "add_threads" : "del_threads"] = JsonValue.CreateStringValue(threads.Stringify());
            payload[hidden ? "del_threads" : "add_threads"] = JsonValue.CreateStringValue("[]");
            payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);
            string url = ApiUrl(session.ServiceUrl("conversation") + "/api/hiddenconvers/add-remove");
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify()))
            };
            await CallAsync(session, HttpMethod.Post, url, form, ct);
        }

        public static async Task RemoveFriendAsync(ZaloSession session, string friendId, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(friendId)) throw new ArgumentException("Thiếu mã bạn bè.", "friendId");
            var payload = new JsonObject();
            payload["fid"] = JsonValue.CreateStringValue(friendId);
            payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);
            string url = ApiUrl(session.ServiceUrl("friend") + "/api/friend/remove");
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify()))
            };
            await CallAsync(session, HttpMethod.Post, url, form, ct);
        }

        public static async Task SetFriendBlockedAsync(ZaloSession session, string friendId, bool blocked, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(friendId)) throw new ArgumentException("Thiếu mã bạn bè.", "friendId");
            var payload = new JsonObject();
            payload["fid"] = JsonValue.CreateStringValue(friendId);
            payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);
            string route = blocked ? ZaloUWP.Core.ZaloConstants.ApiFriendBlock : ZaloUWP.Core.ZaloConstants.ApiFriendUnblock;
            string url = ApiUrl(session.ServiceUrl("friend") + route);
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify()))
            };
            await CallAsync(session, HttpMethod.Post, url, form, ct);
        }

        /// <summary>Returns only the friend IDs the server currently reports as online.</summary>
        public static async Task<HashSet<string>> GetFriendOnlineIdsAsync(ZaloSession session, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            var payload = new JsonObject();
            payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);
            string url = ApiUrl(session.ServiceUrl("profile") + "/api/social/friend/onlines",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify())));
            IJsonValue result = await CallAsync(session, HttpMethod.Get, url, null, ct);

            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (result == null || result.ValueType != JsonValueType.Object) return ids;
            IJsonValue onlines;
            if (!result.GetObject().TryGetValue("onlines", out onlines) || onlines.ValueType != JsonValueType.Array) return ids;
            foreach (IJsonValue item in onlines.GetArray())
            {
                if (item.ValueType != JsonValueType.Object) continue;
                string uid = Str(item.GetObject(), "userId");
                if (!string.IsNullOrWhiteSpace(uid)) ids.Add(uid);
            }
            return ids;
        }

        /// <summary>Gets a friend's last activity only when that friend allows it to be shown.</summary>
        public static async Task<DateTimeOffset?> GetFriendLastOnlineAsync(ZaloSession session, string uid, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(uid)) return null;

            var payload = new JsonObject();
            payload["uid"] = JsonValue.CreateStringValue(uid);
            payload["conv_type"] = JsonValue.CreateNumberValue(1);
            payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);
            string url = ApiUrl(session.ServiceUrl("profile") + "/api/social/profile/lastOnline",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify())));
            IJsonValue result = await CallAsync(session, HttpMethod.Get, url, null, ct);
            if (result == null || result.ValueType != JsonValueType.Object) return null;

            JsonObject data = result.GetObject();
            IJsonValue settingsValue;
            if (!data.TryGetValue("settings", out settingsValue) || settingsValue.ValueType != JsonValueType.Object)
                return null;
            JsonObject settings = settingsValue.GetObject();
            IJsonValue visibleValue;
            if (!settings.TryGetValue("show_online_status", out visibleValue)
                || visibleValue.ValueType != JsonValueType.Boolean || !visibleValue.GetBoolean())
                return null;

            double timestamp = Num(data, "lastOnline");
            if (timestamp <= 0) return null;
            // Zalo endpoints have used both Unix seconds and Unix milliseconds.
            if (timestamp < 100000000000d) timestamp *= 1000d;
            try { return DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        public static async Task LoadMyProfileAsync(ZaloSession session, CancellationToken ct)
        {
            if (session == null) return;
            string url = ApiUrl(session.ServiceUrl("profile") + "/api/social/profile/me-v2");
            IJsonValue result = await CallAsync(session, HttpMethod.Get, url, null, ct);
            if (result == null || result.ValueType != JsonValueType.Object) return;

            JsonObject profile = result.GetObject();
            IJsonValue nested;
            if (profile.TryGetValue("profile", out nested) && nested.ValueType == JsonValueType.Object)
                profile = nested.GetObject();

            session.HasBusinessCategory = false;
            IJsonValue bizValue;
            if (profile.TryGetValue("biz", out bizValue))
            {
                JsonObject biz = bizValue.ValueType == JsonValueType.Object ? bizValue.GetObject() : null;
                if (biz == null && bizValue.ValueType == JsonValueType.String)
                    JsonObject.TryParse(bizValue.GetString(), out biz);
                session.HasBusinessCategory = biz != null && biz.ContainsKey("cate");
            }

            session.DisplayName = Str(profile, "displayName");
            if (session.DisplayName.Length == 0) session.DisplayName = Str(profile, "display_name");
            if (session.DisplayName.Length == 0) session.DisplayName = Str(profile, "zaloName");
            if (session.DisplayName.Length == 0) session.DisplayName = Str(profile, "zalo_name");
            if (session.DisplayName.Length == 0) session.DisplayName = Str(profile, "name");
            if (session.DisplayName.Length == 0) session.DisplayName = Str(profile, "username");
            session.AvatarUrl = Str(profile, "avatar");
            session.PhoneNumber = Str(profile, "phoneNumber");
            if (session.PhoneNumber.Length == 0) session.PhoneNumber = Str(profile, "phone");
            session.ProfileStatus = Str(profile, "status");
            session.ProfileDob = NormalizeProfileDob(Str(profile, "sdob"));
            if (session.ProfileDob.Length == 0)
            {
                string dobValue = Str(profile, "dob");
                session.ProfileDob = NormalizeProfileDob(dobValue);
            }
            double gender = Num(profile, "gender");
            session.ProfileGender = gender == 0 || gender == 1 ? (int)gender : -1;
        }

        private static string NormalizeProfileDob(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            DateTime date;
            string[] formats = { "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy", "MM/dd/yyyy" };
            if (DateTime.TryParseExact(value.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out date))
                return date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            long numeric;
            if (long.TryParse(value, out numeric))
            {
                try
                {
                    if (numeric > 100000000000L) numeric /= 1000;
                    string digits = numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (digits.Length == 8 && DateTime.TryParseExact(digits, "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date))
                        return date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    if (numeric >= 100000000L && numeric <= 4102444800L)
                    {
                        date = DateTimeOffset.FromUnixTimeSeconds(numeric).UtcDateTime;
                        if (date.Year >= 1900 && date.Date <= DateTime.UtcNow.Date)
                            return date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                catch { }
            }
            return string.Empty;
        }

        public static async Task UpdateMyProfileAsync(ZaloSession session, string name, string dob, int gender, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (session.HasBusinessCategory)
                throw new InvalidOperationException("Chưa hỗ trợ sửa hồ sơ tài khoản doanh nghiệp vì cần giữ nguyên danh mục biz.cate.");
            string trimmedName = (name ?? string.Empty).Trim();
            if (trimmedName.Length == 0 || trimmedName.Length > 40)
                throw new ArgumentException("Tên phải có từ 1 đến 40 ký tự.", "name");
            DateTime parsedDob;
            if (!DateTime.TryParseExact(dob ?? string.Empty, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out parsedDob)
                || parsedDob.Date > DateTime.Today || parsedDob.Year < 1900)
                throw new ArgumentException("Ngày sinh phải có dạng YYYY-MM-DD và không ở tương lai.", "dob");
            if (gender != 0 && gender != 1) throw new ArgumentOutOfRangeException("gender");

            var profile = new JsonObject();
            profile["name"] = JsonValue.CreateStringValue(trimmedName);
            profile["dob"] = JsonValue.CreateStringValue(parsedDob.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            profile["gender"] = JsonValue.CreateNumberValue(gender);
            var payload = new JsonObject();
            payload["profile"] = JsonValue.CreateStringValue(profile.Stringify());
            payload["biz"] = JsonValue.CreateStringValue("{}");
            payload["language"] = JsonValue.CreateStringValue("vi");

            string url = ApiUrl(session.ServiceUrl("profile") + "/api/social/profile/update");
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify()))
            };
            await CallAsync(session, HttpMethod.Post, url, form, ct);
        }

        public static async Task UpdateMyAvatarAsync(ZaloSession session, byte[] jpegBytes, uint width, uint height, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (jpegBytes == null || jpegBytes.Length == 0 || jpegBytes.Length > 12 * 1024 * 1024)
                throw new ArgumentException("Ảnh đại diện phải nhỏ hơn 12 MB.", "jpegBytes");
            if (width == 0 || height == 0) throw new ArgumentException("Kích thước ảnh không hợp lệ.");

            var metaData = new JsonObject();
            var origin = new JsonObject();
            origin["width"] = JsonValue.CreateNumberValue(width);
            origin["height"] = JsonValue.CreateNumberValue(height);
            metaData["origin"] = origin;
            var processed = new JsonObject();
            processed["width"] = JsonValue.CreateNumberValue(width);
            processed["height"] = JsonValue.CreateNumberValue(height);
            processed["size"] = JsonValue.CreateNumberValue(jpegBytes.Length);
            metaData["processed"] = processed;

            var payload = new JsonObject();
            payload["avatarSize"] = JsonValue.CreateNumberValue(120);
            payload["clientId"] = JsonValue.CreateStringValue(session.Uid + DateTime.Now.ToString("HH:mm dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture));
            payload["language"] = JsonValue.CreateStringValue("vi");
            payload["metaData"] = JsonValue.CreateStringValue(metaData.Stringify());
            string url = ApiUrl(session.ServiceUrl("file") + "/api/profile/upavatar",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify())));
            ZaloResponse response = await session.Http.UploadProfileImageAsync(url, jpegBytes, ct);
            ResolveResponse(session, url, HttpMethod.Post.Method, response);
        }

        public static async Task<JsonObject> GetFriendProfileAsync(ZaloSession session, string userId, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("Thiếu mã người dùng.", "userId");

            var payload = new JsonObject();
            payload["phonebook_version"] = JsonValue.CreateNumberValue(Num(session.ExtraVer, "phonebook"));
            var friendVersions = new JsonArray();
            friendVersions.Add(JsonValue.CreateStringValue(userId + "_0"));
            payload["friend_pversion_map"] = friendVersions;
            payload["avatar_size"] = JsonValue.CreateNumberValue(120);
            payload["language"] = JsonValue.CreateStringValue("vi");
            payload["show_online_status"] = JsonValue.CreateNumberValue(1);
            payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);

            string encrypted = ZaloCrypto.Encode(session.SecretKey, payload.Stringify());
            string url = ApiUrl(session.ServiceUrl("profile") + "/api/social/friend/getprofiles/v2");
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", encrypted)
            };
            IJsonValue result = await CallAsync(session, HttpMethod.Post, url, form, ct);
            if (result == null || result.ValueType != JsonValueType.Object)
                throw new InvalidOperationException("Máy chủ không trả về hồ sơ người dùng.");

            JsonObject response = result.GetObject();
            IJsonValue profilesValue;
            if (response.TryGetValue("changed_profiles", out profilesValue)
                && profilesValue.ValueType == JsonValueType.Object)
            {
                JsonObject profiles = profilesValue.GetObject();
                JsonObject profile;
                if (profiles.TryGetValue(userId, out var byId) && byId.ValueType == JsonValueType.Object)
                    profile = byId.GetObject();
                else if (profiles.TryGetValue(userId + "_0", out var byVersion) && byVersion.ValueType == JsonValueType.Object)
                    profile = byVersion.GetObject();
                else
                    profile = profiles.Values.FirstOrDefault(v => v.ValueType == JsonValueType.Object
                        && (Str(v.GetObject(), "uid") == userId || Str(v.GetObject(), "userId") == userId))?.GetObject();
                if (profile != null) return profile;
            }

            throw new InvalidOperationException("Hồ sơ này chưa có thông tin hiển thị hoặc chưa được máy chủ trả về.");
        }

        public static async Task<Dictionary<string, ZaloFriend>> GetGroupMemberProfilesAsync(
            ZaloSession session, IEnumerable<string> memberIds, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            var ids = (memberIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id) && id != "0" && id != session.Uid)
                .Distinct(StringComparer.Ordinal).Take(100).ToList();
            var profiles = new Dictionary<string, ZaloFriend>(StringComparer.Ordinal);
            if (ids.Count == 0) return profiles;

            var versions = new JsonArray();
            foreach (string id in ids)
                versions.Add(JsonValue.CreateStringValue(id.EndsWith("_0", StringComparison.Ordinal) ? id : id + "_0"));
            var payload = new JsonObject();
            payload["friend_pversion_map"] = versions;
            string url = ApiUrl(session.ServiceUrl("profile") + "/api/social/group/members",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify())));
            IJsonValue response = await CallAsync(session, HttpMethod.Get, url, null, ct);
            JsonObject root = response != null && response.ValueType == JsonValueType.Object ? response.GetObject() : null;
            IJsonValue profilesValue;
            if (root != null && root.TryGetValue("profiles", out profilesValue) && profilesValue.ValueType == JsonValueType.Object)
            {
                foreach (var pair in profilesValue.GetObject())
                {
                    if (pair.Value.ValueType != JsonValueType.Object) continue;
                    JsonObject profile = pair.Value.GetObject();
                    string id = Str(profile, "id");
                    if (id.Length == 0) id = Str(profile, "uid");
                    if (id.Length == 0) id = Str(profile, "userId");
                    if (id.Length == 0) id = pair.Key.EndsWith("_0", StringComparison.Ordinal)
                        ? pair.Key.Substring(0, pair.Key.Length - 2) : pair.Key;
                    string name = Str(profile, "displayName");
                    if (name.Length == 0) name = Str(profile, "zaloName");
                    profiles[id] = new ZaloFriend { UserId = id, Name = name, OriginalName = name, Avatar = Str(profile, "avatar") };
                }
            }
            return profiles;
        }

        public static async Task UpdateMyStatusAsync(ZaloSession session, string status, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            var payload = new JsonObject();
            payload["status"] = JsonValue.CreateStringValue(status ?? string.Empty);
            string encrypted = ZaloCrypto.Encode(session.SecretKey, payload.Stringify());
            string url = ApiUrl(session.ServiceUrl("profile") + "/api/social/profile/status",
                new KeyValuePair<string, string>("params", encrypted));
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", encrypted)
            };
            await CallAsync(session, HttpMethod.Post, url, form, ct);
        }

        public static async Task<JsonObject> GetAccountSettingsAsync(ZaloSession session, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            string encrypted = ZaloCrypto.Encode(session.SecretKey, "{}");
            string url = ApiUrl("https://wpa.chat.zalo.me/api/setting/me",
                new KeyValuePair<string, string>("params", encrypted));
            IJsonValue result = await CallAsync(session, HttpMethod.Get, url, null, ct);
            JsonObject settings = result != null && result.ValueType == JsonValueType.Object ? result.GetObject() : null;
            if (settings == null) throw new InvalidOperationException("Máy chủ không trả về cài đặt tài khoản.");
            IJsonValue nested;
            if (settings.TryGetValue("settings", out nested) && nested.ValueType == JsonValueType.Object)
                settings = nested.GetObject();
            session.Settings = settings;
            return settings;
        }

        public static async Task SetAccountSettingAsync(ZaloSession session, string key, int value, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            string[] allowed =
            {
                "view_birthday", "show_online_status", "display_seen_status", "receive_message",
                "accept_stranger_call", "add_friend_via_phone", "add_friend_via_qr", "add_friend_via_group",
                "add_friend_via_contact", "display_on_recommend_friend"
            };
            if (!allowed.Contains(key)) throw new ArgumentException("Thiết lập quyền riêng tư không được hỗ trợ.", "key");
            var payload = new JsonObject();
            payload[key] = JsonValue.CreateNumberValue(value);
            string encrypted = ZaloCrypto.Encode(session.SecretKey, payload.Stringify());
            string url = ApiUrl("https://wpa.chat.zalo.me/api/setting/update",
                new KeyValuePair<string, string>("params", encrypted));
            await CallAsync(session, HttpMethod.Get, url, null, ct);
            if (session.Settings == null) session.Settings = new JsonObject();
            session.Settings[key] = JsonValue.CreateNumberValue(value);
        }

        public static async Task<HashSet<string>> GetMutedConversationKeysAsync(ZaloSession session, CancellationToken ct)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (session == null) return keys;
            var payload = new JsonObject();
            payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);
            string url = ApiUrl(session.ServiceUrl("profile") + "/api/social/profile/getmute",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify())));
            IJsonValue result = await CallAsync(session, HttpMethod.Get, url, null, ct);
            JsonObject root = result != null && result.ValueType == JsonValueType.Object ? result.GetObject() : null;
            IJsonValue dataValue;
            if (root != null && root.TryGetValue("data", out dataValue))
            {
                if (dataValue.ValueType == JsonValueType.Object) root = dataValue.GetObject();
                else if (dataValue.ValueType == JsonValueType.String)
                {
                    JsonObject parsed = ZaloJson.ParseObject(dataValue.GetString());
                    if (parsed != null) root = parsed;
                }
            }
            AddMutedConversationKeys(root, "chatEntries", "u:", keys);
            AddMutedConversationKeys(root, "groupChatEntries", "g:", keys);
            return keys;
        }

        private static void AddMutedConversationKeys(JsonObject root, string field, string prefix, HashSet<string> keys)
        {
            IJsonValue entriesValue;
            if (root == null || !root.TryGetValue(field, out entriesValue) || entriesValue.ValueType != JsonValueType.Array) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (IJsonValue entryValue in entriesValue.GetArray())
            {
                if (entryValue.ValueType != JsonValueType.Object) continue;
                JsonObject entry = entryValue.GetObject();
                string id = Str(entry, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                double duration = Num(entry, "duration");
                double start = Num(entry, "startTime");
                if (duration < 0 || duration > 0 && start + duration > now) keys.Add(prefix + id);
            }
        }

        public static async Task SetConversationMuteAsync(ZaloSession session, string threadId, bool isGroup, bool muted, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(threadId)) throw new ArgumentException("Thiếu mã cuộc trò chuyện.", "threadId");
            var payload = new JsonObject();
            payload["toid"] = JsonValue.CreateStringValue(threadId);
            payload["duration"] = JsonValue.CreateNumberValue(-1);
            payload["action"] = JsonValue.CreateNumberValue(muted ? 1 : 3);
            payload["startTime"] = JsonValue.CreateNumberValue(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            payload["muteType"] = JsonValue.CreateNumberValue(isGroup ? 2 : 1);
            payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);
            string encrypted = ZaloCrypto.Encode(session.SecretKey, payload.Stringify());
            string url = ApiUrl(session.ServiceUrl("profile") + "/api/social/profile/setmute");
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", encrypted)
            };
            await CallAsync(session, HttpMethod.Post, url, form, ct);
        }

        public static async Task<ZaloFriend> FindFriendByPhoneAsync(ZaloSession session, string phone, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(phone)) throw new ArgumentException("Vui lòng nhập số điện thoại.", "phone");

            string normalizedPhone = phone.Trim().Replace(" ", "").Replace("-", "");
            if (normalizedPhone.StartsWith("0")) normalizedPhone = "84" + normalizedPhone.Substring(1);

            var payload = new JsonObject();
            payload["phone"] = JsonValue.CreateStringValue(normalizedPhone);
            payload["avatar_size"] = JsonValue.CreateNumberValue(120);
            payload["language"] = JsonValue.CreateStringValue("vi");
            payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);
            payload["reqSrc"] = JsonValue.CreateNumberValue(40);

            string encrypted = ZaloCrypto.Encode(session.SecretKey, payload.Stringify());
            string url = ApiUrl(session.ServiceUrl("friend") + "/api/friend/profile/get",
                new KeyValuePair<string, string>("params", encrypted));
            IJsonValue result = await CallAsync(session, HttpMethod.Get, url, null, ct);
            if (result == null || result.ValueType != JsonValueType.Object)
                throw new InvalidOperationException("Không tìm thấy tài khoản Zalo phù hợp.");

            JsonObject user = result.GetObject();
            string userId = Str(user, "uid");
            if (userId.Length == 0) userId = Str(user, "userId");
            if (userId.Length == 0) throw new InvalidOperationException("Máy chủ không trả về mã người dùng.");

            string name = Str(user, "display_name");
            if (name.Length == 0) name = Str(user, "displayName");
            if (name.Length == 0) name = Str(user, "zalo_name");
            if (name.Length == 0) name = Str(user, "zaloName");

            return new ZaloFriend { UserId = userId, Name = name, Avatar = Str(user, "avatar") };
        }

        public static async Task<ZaloFriend> FindFriendByUsernameAsync(ZaloSession session, string username, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Vui lòng nhập tên người dùng Zalo.", "username");

            var payload = new JsonObject();
            payload["user_name"] = JsonValue.CreateStringValue(username.Trim().TrimStart('@'));
            payload["avatar_size"] = JsonValue.CreateNumberValue(120);
            string encrypted = ZaloCrypto.Encode(session.SecretKey, payload.Stringify());
            string url = ApiUrl(session.ServiceUrl("friend") + "/api/friend/search/by-user-name",
                new KeyValuePair<string, string>("params", encrypted));
            IJsonValue result = await CallAsync(session, HttpMethod.Get, url, null, ct);
            if (result == null || result.ValueType != JsonValueType.Object)
                throw new InvalidOperationException("Không tìm thấy người dùng Zalo phù hợp.");

            JsonObject user = result.GetObject();
            IJsonValue nested;
            if (user.TryGetValue("user", out nested) && nested.ValueType == JsonValueType.Object)
                user = nested.GetObject();
            string userId = Str(user, "uid");
            if (userId.Length == 0) userId = Str(user, "userId");
            if (userId.Length == 0) throw new InvalidOperationException("Không tìm thấy người dùng Zalo phù hợp.");

            string name = Str(user, "display_name");
            if (name.Length == 0) name = Str(user, "displayName");
            if (name.Length == 0) name = Str(user, "zalo_name");
            if (name.Length == 0) name = Str(user, "zaloName");
            if (name.Length == 0) name = username.Trim().TrimStart('@');
            return new ZaloFriend { UserId = userId, Name = name, Avatar = Str(user, "avatar"), Subtitle = "Kết quả tìm kiếm trực tuyến" };
        }

        public static async Task SendFriendRequestAsync(ZaloSession session, string userId, string message, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("Thiếu mã người dùng.", "userId");

            var payload = new JsonObject();
            payload["toid"] = JsonValue.CreateStringValue(userId);
            payload["msg"] = JsonValue.CreateStringValue(message ?? string.Empty);
            payload["reqsrc"] = JsonValue.CreateNumberValue(30);
            payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);
            payload["language"] = JsonValue.CreateStringValue("vi");
            var srcParams = new JsonObject();
            srcParams["uidTo"] = JsonValue.CreateStringValue(userId);
            payload["srcParams"] = JsonValue.CreateStringValue(srcParams.Stringify());

            string encrypted = ZaloCrypto.Encode(session.SecretKey, payload.Stringify());
            string url = ApiUrl(session.ServiceUrl("friend") + "/api/friend/sendreq");
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", encrypted)
            };
            await CallAsync(session, HttpMethod.Post, url, form, ct);
        }

        public static async Task<List<ZaloFriendRequest>> GetFriendRequestsAsync(ZaloSession session, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            var payload = new JsonObject();
            payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);
            string url = ApiUrl(session.ServiceUrl("friend") + "/api/friend/recommendsv2/list",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify())));
            IJsonValue result = await CallAsync(session, HttpMethod.Get, url, null, ct);
            JsonObject root = result != null && result.ValueType == JsonValueType.Object ? result.GetObject() : null;
            IJsonValue itemsValue = null;
            if (root != null && !root.TryGetValue("recommItems", out itemsValue))
            {
                IJsonValue nested;
                if (root.TryGetValue("data", out nested) && nested.ValueType == JsonValueType.Object)
                    nested.GetObject().TryGetValue("recommItems", out itemsValue);
            }
            if (root == null || itemsValue == null || itemsValue.ValueType != JsonValueType.Array)
                return new List<ZaloFriendRequest>();

            var requests = new List<ZaloFriendRequest>();
            foreach (IJsonValue itemValue in itemsValue.GetArray())
            {
                if (itemValue.ValueType != JsonValueType.Object) continue;
                JsonObject item = itemValue.GetObject();
                IJsonValue infoValue;
                JsonObject info = item.TryGetValue("dataInfo", out infoValue) && infoValue.ValueType == JsonValueType.Object
                    ? infoValue.GetObject() : item;
                int requestType = (int)Num(info, "recommType");
                if (requestType != 2) continue;
                string id = Str(info, "userId");
                if (string.IsNullOrWhiteSpace(id)) continue;
                string name = Str(info, "displayName");
                if (string.IsNullOrWhiteSpace(name)) name = Str(info, "zaloName");
                if (string.IsNullOrWhiteSpace(name)) name = Str(info, "userName");
                if (string.IsNullOrWhiteSpace(name)) name = "Người dùng Zalo";
                requests.Add(new ZaloFriendRequest
                {
                    UserId = id,
                    Name = name,
                    Avatar = Str(info, "avatar"),
                    Status = Str(info, "status"),
                    Message = Str(info, "message")
                });
            }
            return requests;
        }

        public static async Task<List<ZaloFriend>> GetRecommendedFriendsAsync(ZaloSession session, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            var payload = new JsonObject();
            payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);
            string url = ApiUrl(session.ServiceUrl("friend") + "/api/friend/recommendsv2/list",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify())));
            IJsonValue result = await CallAsync(session, HttpMethod.Get, url, null, ct);
            JsonObject root = result != null && result.ValueType == JsonValueType.Object ? result.GetObject() : null;
            IJsonValue itemsValue = null;
            if (root != null && !root.TryGetValue("recommItems", out itemsValue))
            {
                IJsonValue nested;
                if (root.TryGetValue("data", out nested) && nested.ValueType == JsonValueType.Object)
                    nested.GetObject().TryGetValue("recommItems", out itemsValue);
            }
            if (itemsValue == null || itemsValue.ValueType != JsonValueType.Array)
                return new List<ZaloFriend>();

            var friends = new List<ZaloFriend>();
            foreach (IJsonValue itemValue in itemsValue.GetArray())
            {
                if (itemValue.ValueType != JsonValueType.Object) continue;
                JsonObject item = itemValue.GetObject();
                IJsonValue infoValue;
                JsonObject info = item.TryGetValue("dataInfo", out infoValue) && infoValue.ValueType == JsonValueType.Object
                    ? infoValue.GetObject() : item;
                if ((int)Num(info, "recommType") != 1) continue;
                string id = Str(info, "userId");
                if (string.IsNullOrWhiteSpace(id) || id == session.Uid) continue;
                string name = Str(info, "displayName");
                if (string.IsNullOrWhiteSpace(name)) name = Str(info, "zaloName");
                if (string.IsNullOrWhiteSpace(name)) name = "Người dùng Zalo";
                friends.Add(new ZaloFriend
                {
                    UserId = id,
                    Name = name,
                    OriginalName = name,
                    Avatar = Str(info, "avatar"),
                    Subtitle = Str(info, "status")
                });
            }
            return friends;
        }

        public static async Task AcceptFriendRequestAsync(ZaloSession session, string userId, CancellationToken ct)
        {
            await RespondToFriendRequestAsync(session, userId, true, ct);
        }

        public static async Task RejectFriendRequestAsync(ZaloSession session, string userId, CancellationToken ct)
        {
            await RespondToFriendRequestAsync(session, userId, false, ct);
        }

        public static async Task<List<ZaloFriendRequest>> GetSentFriendRequestsAsync(ZaloSession session, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            var payload = new JsonObject();
            payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);
            string url = ApiUrl(session.ServiceUrl("friend") + "/api/friend/requested/list",
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify())));
            IJsonValue result;
            try { result = await CallAsync(session, HttpMethod.Get, url, null, ct); }
            catch (Exception ex)
            {
                // zca-js documents API error 112 as the empty outgoing-request state.
                if ((ex.Message ?? string.Empty).IndexOf("112", StringComparison.Ordinal) >= 0)
                    return new List<ZaloFriendRequest>();
                throw;
            }
            JsonObject root = result != null && result.ValueType == JsonValueType.Object ? result.GetObject() : null;
            IJsonValue dataValue;
            if (root != null && root.TryGetValue("data", out dataValue))
                root = dataValue.ValueType == JsonValueType.Object ? dataValue.GetObject()
                    : dataValue.ValueType == JsonValueType.String ? ZaloJson.ParseObject(dataValue.GetString()) : root;
            var requests = new List<ZaloFriendRequest>();
            if (root == null) return requests;
            foreach (KeyValuePair<string, IJsonValue> pair in root)
            {
                if (pair.Value.ValueType != JsonValueType.Object) continue;
                JsonObject info = pair.Value.GetObject();
                string userId = Str(info, "userId");
                if (string.IsNullOrWhiteSpace(userId)) userId = pair.Key;
                if (string.IsNullOrWhiteSpace(userId)) continue;
                string name = Str(info, "displayName");
                if (string.IsNullOrWhiteSpace(name)) name = Str(info, "zaloName");
                if (string.IsNullOrWhiteSpace(name)) name = userId;
                string message = string.Empty;
                IJsonValue requestInfoValue;
                if (info.TryGetValue("fReqInfo", out requestInfoValue) && requestInfoValue.ValueType == JsonValueType.Object)
                    message = Str(requestInfoValue.GetObject(), "message");
                requests.Add(new ZaloFriendRequest
                {
                    UserId = userId,
                    Name = name,
                    Avatar = Str(info, "avatar"),
                    Message = message
                });
            }
            return requests;
        }

        public static async Task UndoFriendRequestAsync(ZaloSession session, string userId, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("Thiếu mã người dùng.", "userId");
            var payload = new JsonObject();
            payload["fid"] = JsonValue.CreateStringValue(userId);
            string url = ApiUrl(session.ServiceUrl("friend") + "/api/friend/undo");
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify()))
            };
            await CallAsync(session, HttpMethod.Post, url, form, ct);
        }

        private static async Task RespondToFriendRequestAsync(ZaloSession session, string userId, bool accept, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("Thiếu mã người dùng.", "userId");
            var payload = new JsonObject();
            payload["fid"] = JsonValue.CreateStringValue(userId);
            if (accept) payload["language"] = JsonValue.CreateStringValue("vi");
            string route = accept ? "/api/friend/accept" : "/api/friend/reject";
            string url = ApiUrl(session.ServiceUrl("friend") + route);
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("params", ZaloCrypto.Encode(session.SecretKey, payload.Stringify()))
            };
            await CallAsync(session, HttpMethod.Post, url, form, ct);
        }

        public static async Task<string> CreateGroupAsync(ZaloSession session, string name, IList<string> memberIds, CancellationToken ct)
        {
            if (session == null) throw new InvalidOperationException("Vui lòng đăng nhập trước.");
            if (memberIds == null || memberIds.Count == 0) throw new ArgumentException("Chọn ít nhất một người bạn.", "memberIds");

            var members = new JsonArray();
            var memberTypes = new JsonArray();
            foreach (string id in memberIds)
            {
                members.Add(JsonValue.CreateStringValue(id));
                memberTypes.Add(JsonValue.CreateNumberValue(-1));
            }

            var payload = new JsonObject();
            payload["clientId"] = JsonValue.CreateNumberValue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            payload["gname"] = JsonValue.CreateStringValue(string.IsNullOrWhiteSpace(name) ? "Nhóm mới" : name.Trim());
            payload["gdesc"] = JsonValue.CreateNullValue();
            payload["members"] = members;
            payload["membersTypes"] = memberTypes;
            payload["nameChanged"] = JsonValue.CreateNumberValue(1);
            payload["createLink"] = JsonValue.CreateNumberValue(1);
            payload["clientLang"] = JsonValue.CreateStringValue("vi");
            payload["imei"] = JsonValue.CreateStringValue(session.Imei ?? string.Empty);
            payload["zsource"] = JsonValue.CreateNumberValue(601);

            string encrypted = ZaloCrypto.Encode(session.SecretKey, payload.Stringify());
            string url = ApiUrl(session.ServiceUrl("group") + "/api/group/create/v2",
                new KeyValuePair<string, string>("params", encrypted));
            IJsonValue result = await CallAsync(session, HttpMethod.Post, url, null, ct);
            if (result == null || result.ValueType != JsonValueType.Object)
                throw new InvalidOperationException("Máy chủ chưa xác nhận tạo nhóm.");

            string groupId = Str(result.GetObject(), "groupId");
            if (groupId.Length == 0) groupId = Str(result.GetObject(), "grid");
            if (groupId.Length == 0) throw new InvalidOperationException("Máy chủ không trả về mã nhóm.");
            return groupId;
        }
    }

    /// <summary>Keeps the signed-in session on the phone (app-private storage) so QR is needed only once.</summary>
    public static class ZaloStore
    {
        private const string FileName = "zalo-session.json";

        public static async Task<bool> HasSessionAsync()
        {
            return await ApplicationData.Current.LocalFolder.TryGetItemAsync(FileName) != null;
        }

        public static async Task SaveAsync(ZaloSession session)
        {
            var cookies = new JsonArray();
            foreach (ZaloSavedCookie c in session.Http.ExportCookies())
            {
                var o = new JsonObject();
                o["n"] = JsonValue.CreateStringValue(c.Name ?? string.Empty);
                o["v"] = JsonValue.CreateStringValue(c.Value ?? string.Empty);
                o["d"] = JsonValue.CreateStringValue(c.Domain ?? string.Empty);
                o["p"] = JsonValue.CreateStringValue(c.Path ?? "/");
                o["e"] = JsonValue.CreateNumberValue(c.ExpiresMs);
                o["s"] = JsonValue.CreateBooleanValue(c.Secure);
                o["h"] = JsonValue.CreateBooleanValue(c.HttpOnly);
                cookies.Add(o);
            }

            var root = new JsonObject();
            root["imei"] = JsonValue.CreateStringValue(session.Imei);
            root["ua"] = JsonValue.CreateStringValue(session.UserAgent);
            root["uid"] = JsonValue.CreateStringValue(session.Uid ?? string.Empty);
            root["displayName"] = JsonValue.CreateStringValue(session.DisplayName ?? string.Empty);
            root["avatarUrl"] = JsonValue.CreateStringValue(session.AvatarUrl ?? string.Empty);
            root["cookies"] = cookies;

            StorageFile file = await ApplicationData.Current.LocalFolder
                .CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, root.Stringify());
        }

        public static async Task<ZaloSession> RestoreAsync(CancellationToken ct)
        {
            IStorageItem item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(FileName);
            if (item == null) return null;

            string text = await FileIO.ReadTextAsync((StorageFile)item);
            JsonObject root;
            if (!JsonObject.TryParse(text, out root)) return null;

            string imei = root.GetNamedString("imei", "");
            string ua = root.GetNamedString("ua", "");
            if (imei.Length == 0 || ua.Length == 0) return null;

            var saved = new List<ZaloSavedCookie>();
            foreach (IJsonValue v in root.GetNamedArray("cookies", new JsonArray()))
            {
                JsonObject o = v.GetObject();
                saved.Add(new ZaloSavedCookie
                {
                    Name = o.GetNamedString("n", ""),
                    Value = o.GetNamedString("v", ""),
                    Domain = o.GetNamedString("d", ""),
                    Path = o.GetNamedString("p", "/"),
                    ExpiresMs = (long)o.GetNamedNumber("e", 0),
                    Secure = o.GetNamedBoolean("s", false),
                    HttpOnly = o.GetNamedBoolean("h", false)
                });
            }

            var http = new ZaloHttp(ua);
            http.ClearCookies();
            http.ImportCookies(saved);
            return await ZaloAuth.LoginWithSavedCookiesAsync(http, imei, ua, ct);
        }

        public static async Task<string> GetCachedUserIdAsync()
        {
            try
            {
                IStorageItem item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(FileName);
                if (item == null) return string.Empty;
                JsonObject root;
                string text = await FileIO.ReadTextAsync((StorageFile)item);
                return JsonObject.TryParse(text, out root) ? root.GetNamedString("uid", string.Empty) : string.Empty;
            }
            catch { return string.Empty; }
        }

        public static async Task ClearAsync()
        {
            IStorageItem item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(FileName);
            if (item != null) await item.DeleteAsync();
        }
    }
}
