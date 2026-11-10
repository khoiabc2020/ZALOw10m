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
    public static class ZaloEmoticonMap
    {
        private static readonly Dictionary<string, string> CodeToUri = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static ZaloEmoticonMap()
        {
            try
            {
                RegisterCode(":)", "emo_00");
                RegisterCode(":-)", "emo_00");
                RegisterCode(":~", "emo_01");
                RegisterCode(":B", "emo_02");
                RegisterCode(":')", "emo_03");
                RegisterCode("8-)", "emo_04");
                RegisterCode("8)", "emo_04");
                RegisterCode(":-((", "emo_05");
                RegisterCode(":$", "emo_06");
                RegisterCode(":3", "emo_07");
                RegisterCode(":Z", "emo_08");
                RegisterCode(":((", "emo_09");
                RegisterCode("&-(", "emo_10");
                RegisterCode(":-H", "emo_11");
                RegisterCode(":P", "emo_12");
                RegisterCode(":-P", "emo_12");
                RegisterCode(":D", "emo_13");
                RegisterCode(":-D", "emo_13");
                RegisterCode(":O", "emo_14");
                RegisterCode(":-O", "emo_14");
                RegisterCode(":(", "emo_15");
                RegisterCode(":-(", "emo_15");
                RegisterCode(";)", "emo_16");
                RegisterCode(";-)", "emo_16");
                RegisterCode(";-))", "emo_16");
                RegisterCode("--B", "emo_17");
                RegisterCode(":))", "emo_18");
                RegisterCode(":)))", "emo_18");
                RegisterCode(":-))", "emo_18");
                RegisterCode(":*", "emo_19");
                RegisterCode(":-*", "emo_19");
                RegisterCode(";P", "emo_20");
                RegisterCode(";-P", "emo_20");
                RegisterCode(";-D", "emo_21");
                RegisterCode("/-showlove", "emo_22");
                RegisterCode("<3", "emo_22");
                RegisterCode(";D", "emo_23");
                RegisterCode(";O", "emo_24");
                RegisterCode(";G", "emo_25");
                RegisterCode("|-)", "emo_26");
                RegisterCode(":!", "emo_27");
                RegisterCode(":L", "emo_28");
                RegisterCode(":>", "emo_29");
                RegisterCode(":;", "emo_30");
                RegisterCode(";F", "emo_31");
                RegisterCode(":V", "emo_32");
                RegisterCode(":WIPE", "emo_33");
                RegisterCode(":-DIG", "emo_34");
                RegisterCode(":handclap", "emo_35");
                RegisterCode("B-)", "emo_36");
                RegisterCode(":-R", "emo_37");
                RegisterCode(":-<", "emo_38");
                RegisterCode(":-0", "emo_39");
                RegisterCode(":0", "emo_39");
                RegisterCode(";-S", "emo_40");
                RegisterCode(";?", "emo_41");
                RegisterCode(";-X", "emo_42");
                RegisterCode(":-F", "emo_43");
                RegisterCode("8*)", "emo_44");
                RegisterCode(";!", "emo_45");
                RegisterCode(";-!", "emo_46");
                RegisterCode(";XX", "emo_47");
                RegisterCode(":-BYE", "emo_48");
                RegisterCode(">-|", "emo_49");
                RegisterCode("P-(", "emo_50");
                RegisterCode(":--|", "emo_51");
                RegisterCode(":Q", "emo_52");
                RegisterCode("X-)", "emo_53");
                RegisterCode(";-A", "emo_55");
                RegisterCode("8*", "emo_56");
                RegisterCode(":|", "emo_57");
                RegisterCode(":-|", "emo_57");
                RegisterCode(":X", "emo_58");
                RegisterCode(":T", "emo_59");
                RegisterCode(";-/", "emo_60");
                RegisterCode(":-L", "emo_61");
                RegisterCode("$-)", "emo_62");
                RegisterCode("/-beer", "emo_63");
                RegisterCode("/-coffee", "emo_64");
                RegisterCode("/-rose", "emo_65");
                RegisterCode("/-fade", "emo_66");
                RegisterCode("/-bd", "emo_67");
                RegisterCode("/-bome", "emo_68");
                RegisterCode("/-cake", "emo_69");
                RegisterCode("/-heart", "emo_70");
                RegisterCode("/-break", "emo_71");
                RegisterCode("/-shit", "emo_72");
                RegisterCode("/-li", "emo_73");
                RegisterCode("/-flag", "emo_74");
                RegisterCode("/-strong", "emo_75");
                RegisterCode("(y)", "emo_75");
                RegisterCode("/-weak", "emo_76");
                RegisterCode("(n)", "emo_76");
                RegisterCode("/-ok", "emo_77");
                RegisterCode("(ok)", "emo_77");
                RegisterCode("/-v", "emo_78");
                RegisterCode("/-thanks", "emo_79");
                RegisterCode("/-punch", "emo_80");
                RegisterCode("/-share", "emo_81");
                RegisterCode("_()_", "emo_82");
                RegisterCode("/-no", "emo_83");
                RegisterCode("/-bad", "emo_84");
                RegisterCode("/-loveu", "emo_85");
                RegisterCode(":+", "emo_87");
                RegisterCode(";8", "emo_88");
                RegisterCode("/-jj", "emo_89");

                for (int i = 0; i < 90; i++)
                {
                    string key = "emo_" + i.ToString("D2");
                    RegisterCode(key, key);
                }
            }
            catch (Exception ex)
            {
                ZaloLog.Add("ZaloEmoticonMap init error: " + ex.Message);
            }
        }

        private static void RegisterCode(string code, string emoFileName)
        {
            CodeToUri[code] = "ms-appx:///Assets/Emoticons/Emoji/" + emoFileName + ".png";
        }

        public static string GetEmoticonUri(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                string trimmed = text.Trim();
                string uri;
                if (CodeToUri.TryGetValue(trimmed, out uri)) return uri;
            }
            catch { }
            return null;
        }
    }

}
