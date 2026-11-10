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
    /// Tracks the phone-history transfer UI. The legacy pull_mobile_msg request currently returns HTTP 404,
    /// and the replacement transfer protocol has not been implemented yet.
    /// </summary>
    public static class ZaloPhoneSync
    {
        private static CoreDispatcher _dispatcher;
        private static bool _isSyncing;
        private static string _statusMessage = "Đồng bộ từ điện thoại chưa khả dụng trên Windows Phone 8.1.";

        public static void Initialize(CoreDispatcher dispatcher, ZaloSession session = null)
        {
            _dispatcher = dispatcher;
        }

        public static bool IsSyncing
        {
            get { return _isSyncing; }
            private set
            {
                if (_isSyncing != value)
                {
                    _isSyncing = value;
                    OnChanged();
                }
            }
        }

        public static string StatusMessage
        {
            get { return _statusMessage; }
            private set
            {
                _statusMessage = value;
                OnChanged();
            }
        }

        public static event Action Changed;

        private static void OnChanged()
        {
            Action handler = Changed;
            if (handler == null) return;
            if (_dispatcher != null && !_dispatcher.HasThreadAccess)
            {
                var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => handler());
            }
            else
            {
                handler();
            }
        }

        public static void SetDispatcher(CoreDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public static Task StartPhoneSyncAsync(ZaloSession session)
        {
            IsSyncing = false;
            StatusMessage = "Tính năng đồng bộ từ điện thoại chưa hỗ trợ trên Windows Phone 8.1.";
            return Task.FromResult(0);
        }

        public static void ProcessEvent(JsonObject obj)
        {
        }
    }
}
