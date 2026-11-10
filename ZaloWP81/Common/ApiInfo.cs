using System;
using System.Reflection;
using Windows.UI.ViewManagement;

namespace ZaloUWP.Common
{
    public static class ApiInfo
    {
        public static bool HasStatusBar => true;
        public static bool IsMobile => true;
        public static bool IsPhoneContractPresent => true;

        public static void ConfigureMobileStatusBar(Windows.UI.Color bg, Windows.UI.Color fg)
        {
            try
            {
                var statusBar = StatusBar.GetForCurrentView();
                if (statusBar != null)
                {
                    statusBar.BackgroundColor = bg;
                    statusBar.BackgroundOpacity = 1.0;
                    statusBar.ForegroundColor = fg;
                }
            }
            catch { }
        }
    }
}
