using System;
using System.Reflection;
using Windows.Foundation.Metadata;
using Windows.System.Profile;

namespace ZaloUWP.Common
{
    public static class ApiInfo
    {
        private static bool? _hasStatusBar;
        public static bool HasStatusBar => (_hasStatusBar = _hasStatusBar ?? ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar")) ?? false;

        private static bool? _isMobile;
        public static bool IsMobile => (_isMobile = _isMobile ?? (AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Mobile")) ?? false;

        private static bool? _isPhoneContractPresent;
        public static bool IsPhoneContractPresent => (_isPhoneContractPresent = _isPhoneContractPresent ?? ApiInformation.IsApiContractPresent("Windows.Phone.PhoneContract", 1)) ?? false;

        public static void ConfigureMobileStatusBar(Windows.UI.Color bg, Windows.UI.Color fg)
        {
            if (HasStatusBar)
            {
                try
                {
                    var statusBarType = Type.GetType("Windows.UI.ViewManagement.StatusBar, Windows, ContentType=WindowsRuntime");
                    if (statusBarType != null)
                    {
                        var method = statusBarType.GetTypeInfo().GetDeclaredMethod("GetForCurrentView");
                        var statusBar = method?.Invoke(null, null);
                        if (statusBar != null)
                        {
                            statusBarType.GetTypeInfo().GetDeclaredProperty("BackgroundColor")?.SetValue(statusBar, bg);
                            statusBarType.GetTypeInfo().GetDeclaredProperty("BackgroundOpacity")?.SetValue(statusBar, 1.0);
                            statusBarType.GetTypeInfo().GetDeclaredProperty("ForegroundColor")?.SetValue(statusBar, fg);
                        }
                    }
                }
                catch { }
            }
        }
    }
}

