using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using ZaloLite;
using ZaloUWP.Core;

namespace ZaloWP81.Views
{
    public sealed class LoginPage : Page
    {
        private CancellationTokenSource _cts;
        private Image _qrImage;
        private TextBlock _statusText;
        private ProgressRing _progressRing;
        private Button _retryButton;

        public LoginPage()
        {
            BuildUI();
        }

        private void BuildUI()
        {
            var rootGrid = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x11, 0x11, 0x11))
            };
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Header (WP8.1 Style)
            var headerStack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            var appTitle = new TextBlock
            {
                Text = "ZALO CHO WINDOWS PHONE",
                FontSize = 12,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x01, 0x67, 0xFE)),
                Margin = new Thickness(0, 0, 0, 6)
            };
            var pageTitle = new TextBlock
            {
                Text = "đăng nhập",
                FontSize = 38,
                FontWeight = Windows.UI.Text.FontWeights.Light,
                Foreground = new SolidColorBrush(Colors.White)
            };
            headerStack.Children.Add(appTitle);
            headerStack.Children.Add(pageTitle);
            Grid.SetRow(headerStack, 0);
            rootGrid.Children.Add(headerStack);

            // Content
            var contentStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var instructions = new TextBlock
            {
                Text = "Mở Zalo trên điện thoại chính và quét mã QR bên dưới để đăng nhập.",
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xAA, 0xAA, 0xAA)),
                Margin = new Thickness(0, 0, 0, 20),
                MaxWidth = 300
            };
            contentStack.Children.Add(instructions);

            var qrBorder = new Border
            {
                Width = 240,
                Height = 240,
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x01, 0x67, 0xFE)),
                BorderThickness = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };

            var qrGrid = new Grid();
            _qrImage = new Image
            {
                Width = 230,
                Height = 230,
                Stretch = Stretch.Uniform
            };
            _progressRing = new ProgressRing
            {
                Width = 48,
                Height = 48,
                IsActive = true,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x01, 0x67, 0xFE)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            qrGrid.Children.Add(_qrImage);
            qrGrid.Children.Add(_progressRing);
            qrBorder.Child = qrGrid;
            contentStack.Children.Add(qrBorder);

            _statusText = new TextBlock
            {
                Text = "Đang tải mã QR...",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xDD, 0xDD, 0xDD)),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            contentStack.Children.Add(_statusText);

            _retryButton = new Button
            {
                Content = "Lấy mã mới",
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            _retryButton.Click += (s, e) => StartQrLogin();
            contentStack.Children.Add(_retryButton);

            Grid.SetRow(contentStack, 1);
            rootGrid.Children.Add(contentStack);

            this.Content = rootGrid;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            StartQrLogin();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _cts?.Cancel();
        }

        private void StartQrLogin()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _retryButton.Visibility = Visibility.Collapsed;
            _progressRing.IsActive = true;
            _progressRing.Visibility = Visibility.Visible;
            _statusText.Text = "Đang khởi tạo mã QR...";

            Task.Run(async () =>
            {
                try
                {
                    ZaloLite.ZaloSession session = await ZaloAuth.LoginWithQrAsync(
                        onQr: async bytes =>
                        {
                            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                            {
                                try
                                {
                                    using (var stream = new InMemoryRandomAccessStream())
                                    {
                                        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                                        {
                                            writer.WriteBytes(bytes);
                                            await writer.StoreAsync();
                                        }
                                        var bmp = new BitmapImage();
                                        await bmp.SetSourceAsync(stream);
                                        _qrImage.Source = bmp;
                                        _progressRing.IsActive = false;
                                        _progressRing.Visibility = Visibility.Collapsed;
                                    }
                                }
                                catch { }
                            });
                        },
                        onStatus: async status =>
                        {
                            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                            {
                                _statusText.Text = status ?? string.Empty;
                            });
                        },
                        ct: _cts.Token
                    );

                    if (session != null)
                    {
                        await ZaloStore.SaveAsync(session);
                        await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            _statusText.Text = "Đăng nhập thành công!";
                            var task = ZaloApp.StartAsync(session);
                            Frame.Navigate(typeof(MainPage));
                        });
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        _statusText.Text = "Lỗi đăng nhập: " + ex.Message;
                        _progressRing.IsActive = false;
                        _progressRing.Visibility = Visibility.Collapsed;
                        _retryButton.Visibility = Visibility.Visible;
                    });
                }
            });
        }
    }
}
