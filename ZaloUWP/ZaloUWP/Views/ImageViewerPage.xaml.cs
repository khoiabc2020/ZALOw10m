using System;
using System.IO;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using ZaloLite;

namespace ZaloUWP.Views
{
    public sealed partial class ImageViewerPage : Page
    {
        private string _imageUrl;
        private StorageFile _shareImageFile;

        public ImageViewerPage()
        {
            this.InitializeComponent();
        }

        private void ImageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            MainImage.MaxWidth = Math.Max(1, e.NewSize.Width - 16);
            MainImage.MaxHeight = Math.Max(1, e.NewSize.Height - 16);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is string url && !string.IsNullOrEmpty(url))
            {
                _imageUrl = url;
                var transferManager = DataTransferManager.GetForCurrentView();
                transferManager.DataRequested -= ShareDataRequested;
                transferManager.DataRequested += ShareDataRequested;
                await LoadImageAsync(url);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            DataTransferManager.GetForCurrentView().DataRequested -= ShareDataRequested;
            if (_shareImageFile != null)
            {
                var file = _shareImageFile;
                _shareImageFile = null;
                _ = file.DeleteAsync();
            }
            base.OnNavigatedFrom(e);
        }

        private async System.Threading.Tasks.Task<byte[]> DownloadImageAsync(string url)
        {
            var session = ZaloApp.Session;
            if (session == null) throw new InvalidOperationException("No active Zalo session.");
            return await session.Http.DownloadBytesAsync(url, System.Threading.CancellationToken.None);
        }

        private async System.Threading.Tasks.Task LoadImageAsync(string url)
        {
            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;
            RetryImageButton.Visibility = Visibility.Collapsed;
            try
            {
                byte[] bytes = await DownloadImageAsync(url);
                using (var stream = new InMemoryRandomAccessStream())
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                    stream.Seek(0);
                    // The chat viewer is 480px wide on the target Lumia. Bound decoded
                    // pixels so very large camera photos do not allocate hundreds of MB.
                    var bitmap = new BitmapImage { DecodePixelWidth = 1280 };
                    await bitmap.SetSourceAsync(stream);
                    MainImage.Source = bitmap;
                }
            }
            catch (Exception)
            {
                RetryImageButton.Visibility = Visibility.Visible;
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }

        private async void RetryImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_imageUrl)) await LoadImageAsync(_imageUrl);
        }

        private void ShareButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_imageUrl)) return;
            try { DataTransferManager.ShowShareUI(); }
            catch (Exception ex)
            {
                ZaloLog.Add("Image share UI unavailable: " + ex.Message);
                _ = new MessageDialog("Không mở được bảng chia sẻ trên thiết bị này.", "Chia sẻ ảnh").ShowAsync();
            }
        }

        private async void ShareDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            DataRequestDeferral deferral = args.Request.GetDeferral();
            try
            {
                StorageFile imageFile = await GetShareImageFileAsync();
                args.Request.Data.Properties.Title = "Ảnh Zalo";
                args.Request.Data.Properties.Description = "Ảnh được chia sẻ từ Zalo.";
                args.Request.Data.SetStorageItems(new IStorageItem[] { imageFile });
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Preparing image share failed: " + ex.Message);
                args.Request.FailWithDisplayText("Không tải được ảnh để chia sẻ. Hãy thử lại.");
            }
            finally { deferral.Complete(); }
        }

        private async System.Threading.Tasks.Task<StorageFile> GetShareImageFileAsync()
        {
            if (_shareImageFile != null) return _shareImageFile;
            byte[] bytes = await DownloadImageAsync(_imageUrl);
            string extension = ImageFileExtension();
            string name = "zalo_share_" + Guid.NewGuid().ToString("N") + extension;
            _shareImageFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                name, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBytesAsync(_shareImageFile, bytes);
            return _shareImageFile;
        }

        private string ImageFileExtension()
        {
            Uri imageUri;
            string extension = Uri.TryCreate(_imageUrl, UriKind.Absolute, out imageUri)
                ? Path.GetExtension(imageUri.AbsolutePath).ToLowerInvariant() : string.Empty;
            return extension == ".png" || extension == ".webp" || extension == ".jpeg" ? extension : ".jpg";
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_imageUrl)) return;

            try
            {
                var bytes = await DownloadImageAsync(_imageUrl);
                    var picturesFolder = KnownFolders.SavedPictures;
                    string extension = ImageFileExtension();
                    var fileName = $"zalo_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";
                    var file = await picturesFolder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);
                    await FileIO.WriteBytesAsync(file, bytes);

                    var dialog = new MessageDialog("Đã lưu hình ảnh vào Bộ sưu tập!", "Thành công");
                    await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                var dialog = new MessageDialog($"Lỗi khi lưu ảnh: {ex.Message}", "Thông báo");
                await dialog.ShowAsync();
            }
        }
    }
}

