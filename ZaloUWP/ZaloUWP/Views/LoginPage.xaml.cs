using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace ZaloUWP.Views
{
    public sealed partial class LoginPage : Page
    {
        private CancellationTokenSource _loginCts;

        public LoginPage()
        {
            this.InitializeComponent();
        }

        private async void RunOnUI(Action action)
        {
            try
            {
                var dispatcher = this.Dispatcher ?? Windows.ApplicationModel.Core.CoreApplication.MainView?.Dispatcher;
                if (dispatcher != null && !dispatcher.HasThreadAccess)
                {
                    await dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[LoginPage] RunOnUI action error: {ex}");
                        }
                    });
                }
                else
                {
                    action();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoginPage] RunOnUI error: {ex}");
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (ZaloLite.ZaloApp.Session != null)
            {
                RunOnUI(() =>
                {
                    var rootFrame = this.Frame ?? Window.Current?.Content as Frame;
                    rootFrame?.Navigate(typeof(MainPage));
                });
                return;
            }

            var ignored = StartQrLoginAsync();
        }

        private async Task StartQrLoginAsync()
        {
            _loginCts?.Cancel();
            _loginCts = new CancellationTokenSource();
            if (QrProgressBar != null) QrProgressBar.IsActive = true;
            if (QrStatusText != null) QrStatusText.Text = "Đang kết nối tới Zalo...";

            try
            {
                var session = await ZaloLite.ZaloAuth.LoginWithQrAsync(
                    async png =>
                    {
                        await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                        {
                            try
                            {
                                var stream = new InMemoryRandomAccessStream();
                                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                                {
                                    writer.WriteBytes(png);
                                    await writer.StoreAsync();
                                    await writer.FlushAsync();
                                    writer.DetachStream();
                                }
                                stream.Seek(0);
                                var bmp = new BitmapImage();
                                await bmp.SetSourceAsync(stream);
                                if (QrDisplayImage != null) QrDisplayImage.Source = bmp;
                                if (QrProgressBar != null) QrProgressBar.IsActive = false;
                            }
                            catch { }
                        });
                    },
                    status =>
                    {
                        var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            if (QrStatusText != null) QrStatusText.Text = status;
                        });
                    },
                    _loginCts.Token
                );

                if (QrStatusText != null) QrStatusText.Text = "Đang tải danh bạ và kết nối...";
                await ZaloLite.ZaloStore.SaveAsync(session);
                await ZaloLite.ZaloApp.StartAsync(session);

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    var rootFrame = this.Frame ?? Window.Current?.Content as Frame;
                    rootFrame?.Navigate(typeof(MainPage));
                });
            }
            catch (OperationCanceledException)
            {
                if (QrStatusText != null) QrStatusText.Text = "Đã hủy.";
            }
            catch (Exception ex)
            {
                if (QrProgressBar != null) QrProgressBar.IsActive = false;
                if (QrStatusText != null) QrStatusText.Text = "Đăng nhập thất bại: " + ex.Message;
            }
        }

        private void RefreshQr_Click(object sender, RoutedEventArgs e)
        {
            var ignored = StartQrLoginAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _loginCts?.Cancel();
        }
    }
}

