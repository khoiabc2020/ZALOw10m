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
    public static class ZaloJson
    {
        // Message and user ids are integers longer than 15 digits. Read as numbers they would lose
        // their last digits, so such numbers are turned into strings before parsing.
        public static string QuoteBigNumbers(string json)
        {
            var sb = new StringBuilder(json.Length + 16);
            bool inString = false;
            int i = 0;
            while (i < json.Length)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length)
                    {
                        sb.Append(json[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (c == '"') inString = false;
                    i++;
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                    sb.Append(c);
                    i++;
                    continue;
                }
                if (c == '-' || (c >= '0' && c <= '9'))
                {
                    int j = i;
                    while (j < json.Length && "+-.eE0123456789".IndexOf(json[j]) >= 0) j++;
                    string token = json.Substring(i, j - i);
                    if (IsBigInteger(token)) sb.Append('"').Append(token).Append('"');
                    else sb.Append(token);
                    i = j;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static bool IsBigInteger(string token)
        {
            int start = token.StartsWith("-") ? 1 : 0;
            if (token.Length - start < 16) return false;
            for (int k = start; k < token.Length; k++)
                if (token[k] < '0' || token[k] > '9') return false;
            return true;
        }

        public static JsonObject ParseObject(string text)
        {
            JsonObject result;
            return JsonObject.TryParse(QuoteBigNumbers(text), out result) ? result : null;
        }
    }


    // ===================================================================== AES-GCM (decrypt only)


}
