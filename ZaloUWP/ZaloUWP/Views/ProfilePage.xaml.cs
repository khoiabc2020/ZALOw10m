using System;
using System.Globalization;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using ZaloLite;
using ZaloUWP.Common;

namespace ZaloUWP.Views
{
    public sealed partial class ProfilePage : Page
    {
        public ProfilePage()
        {
            InitializeComponent();
            Loaded += ProfilePage_Loaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ShowProfile();
        }

        private async void ProfilePage_Loaded(object sender, RoutedEventArgs e)
        {
            var session = ZaloApp.Session;
            if (session == null) return;
            try
            {
                await ZaloAuth.LoadMyProfileAsync(session, System.Threading.CancellationToken.None);
                ShowProfile();
            }
            catch
            {
                ProfileFeedback.Text = "Không làm mới được hồ sơ. Đang hiển thị thông tin đã lưu trong phiên.";
            }
        }

        private void ShowProfile()
        {
            var session = ZaloApp.Session;
            if (session == null)
            {
                ProfileFeedback.Text = "Chưa có tài khoản đang đăng nhập.";
                return;
            }

            string name = string.IsNullOrWhiteSpace(session.DisplayName) ? "Người dùng Zalo" : session.DisplayName;
            ProfileName.Text = name;
            ProfileInitial.Text = ZaloAvatar.InitialOf(name);
            ProfileUid.Text = string.IsNullOrWhiteSpace(session.Uid) ? "Chưa có" : session.Uid;
            ProfilePhone.Text = string.IsNullOrWhiteSpace(session.PhoneNumber) ? "Chưa công khai" : session.PhoneNumber;
            ProfileStatus.Text = string.IsNullOrWhiteSpace(session.ProfileStatus) ? "Chưa đặt trạng thái" : session.ProfileStatus;
            DateTime dob;
            ProfileDobText.Text = DateTime.TryParseExact(session.ProfileDob, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out dob) ? dob.ToString("dd/MM/yyyy") : "Chưa công khai";
            ProfileGenderText.Text = session.ProfileGender == 0 ? "Nam" : session.ProfileGender == 1 ? "Nữ" : "Chưa công khai";

            if (!string.IsNullOrWhiteSpace(session.AvatarUrl))
            {
                try
                {
                    ProfileAvatar.Fill = new ImageBrush { ImageSource = new BitmapImage(new Uri(session.AvatarUrl)), Stretch = Stretch.UniformToFill };
                    ProfileInitial.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    ProfileAvatar.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                    ProfileInitial.Visibility = Visibility.Visible;
                }
            }
            else
            {
                ProfileAvatar.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                ProfileInitial.Visibility = Visibility.Visible;
            }
        }

        private async void ChangeAvatar_Click(object sender, RoutedEventArgs e)
        {
            var session = ZaloApp.Session;
            if (session == null) return;
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".bmp");
            StorageFile file = await picker.PickSingleFileAsync();
            if (file == null) return;

            ChangeAvatarButton.IsEnabled = false;
            ProfileFeedback.Text = "Đang chuẩn bị ảnh đại diện…";
            try
            {
                var properties = await file.GetBasicPropertiesAsync();
                if (properties.Size > 20UL * 1024UL * 1024UL)
                    throw new InvalidOperationException("Ảnh cần nhỏ hơn 20 MB trước khi tải lên.");

                byte[] jpegBytes;
                uint width;
                uint height;
                using (IRandomAccessStream input = await file.OpenReadAsync())
                {
                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
                    uint orientedWidth = decoder.OrientedPixelWidth;
                    uint orientedHeight = decoder.OrientedPixelHeight;
                    double scale = Math.Min(1.0, 1600.0 / Math.Max(orientedWidth, orientedHeight));
                    width = Math.Max(1U, (uint)Math.Round(orientedWidth * scale));
                    height = Math.Max(1U, (uint)Math.Round(orientedHeight * scale));
                    var transform = new BitmapTransform
                    {
                        ScaledWidth = width,
                        ScaledHeight = height,
                        InterpolationMode = BitmapInterpolationMode.Fant
                    };
                    PixelDataProvider pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8,
                        BitmapAlphaMode.Ignore, transform, ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.DoNotColorManage);
                    using (var output = new InMemoryRandomAccessStream())
                    {
                        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
                        encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, width, height,
                            96, 96, pixels.DetachPixelData());
                        await encoder.FlushAsync();
                        output.Seek(0);
                        using (var reader = new DataReader(output.GetInputStreamAt(0)))
                        {
                            await reader.LoadAsync((uint)output.Size);
                            jpegBytes = new byte[(int)output.Size];
                            reader.ReadBytes(jpegBytes);
                        }
                    }
                }

                ProfileFeedback.Text = "Đang tải ảnh lên Zalo…";
                await ZaloAuth.UpdateMyAvatarAsync(session, jpegBytes, width, height,
                    System.Threading.CancellationToken.None);
                await ZaloAuth.LoadMyProfileAsync(session, System.Threading.CancellationToken.None);
                ShowProfile();
                ProfileFeedback.Text = "Đã cập nhật ảnh đại diện.";
            }
            catch (Exception ex)
            {
                ZaloLog.Add("Profile avatar update failed: " + ex.Message);
                ProfileFeedback.Text = "Không đổi được ảnh đại diện. Hãy thử ảnh JPG hoặc PNG khác.";
            }
            finally
            {
                ChangeAvatarButton.IsEnabled = true;
            }
        }

        private async void EditProfile_Click(object sender, RoutedEventArgs e)
        {
            var session = ZaloApp.Session;
            if (session == null) return;
            if (session.HasBusinessCategory)
            {
                ProfileFeedback.Text = "Tài khoản doanh nghiệp cần gửi lại danh mục biz.cate để tránh làm mất cấu hình. Tạm thời không bật sửa hồ sơ.";
                return;
            }

            var nameInput = new TextBox
            {
                Header = "Tên hiển thị",
                Text = session.DisplayName ?? string.Empty,
                MaxLength = 40,
                PlaceholderText = "Tên của bạn"
            };
            var dobInput = new TextBox
            {
                Header = "Ngày sinh (YYYY-MM-DD)",
                Text = session.ProfileDob ?? string.Empty,
                PlaceholderText = "Ví dụ: 1995-08-24"
            };
            var genderInput = new ComboBox { Header = "Giới tính", HorizontalAlignment = HorizontalAlignment.Stretch };
            genderInput.Items.Add(new ComboBoxItem { Content = "Nam" });
            genderInput.Items.Add(new ComboBoxItem { Content = "Nữ" });
            if (session.ProfileGender == 0 || session.ProfileGender == 1) genderInput.SelectedIndex = session.ProfileGender;

            var content = new StackPanel { Width = 320 };
            content.Children.Add(nameInput);
            content.Children.Add(dobInput);
            content.Children.Add(genderInput);
            StackPanelSpacingCompat.Apply(content, 10);
            var dialog = new ContentDialog
            {
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Title = "Chỉnh sửa hồ sơ",
                Content = content,
                PrimaryButtonText = "Lưu",
                CloseButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            string name = (nameInput.Text ?? string.Empty).Trim();
            string dobText = (dobInput.Text ?? string.Empty).Trim();
            DateTime parsedDob;
            if (name.Length == 0 || name.Length > 40)
            {
                ProfileFeedback.Text = "Tên hiển thị cần có từ 1 đến 40 ký tự.";
                return;
            }
            if (!DateTime.TryParseExact(dobText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out parsedDob) || parsedDob.Date > DateTime.Today || parsedDob.Year < 1900)
            {
                ProfileFeedback.Text = "Ngày sinh cần đúng dạng YYYY-MM-DD và không ở tương lai.";
                return;
            }
            if (genderInput.SelectedIndex < 0)
            {
                ProfileFeedback.Text = "Hãy chọn giới tính trước khi lưu.";
                return;
            }

            ProfileFeedback.Text = "Đang cập nhật hồ sơ…";
            try
            {
                await ZaloAuth.UpdateMyProfileAsync(session, name, parsedDob.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    genderInput.SelectedIndex, System.Threading.CancellationToken.None);
                session.DisplayName = name;
                session.ProfileDob = parsedDob.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                session.ProfileGender = genderInput.SelectedIndex;
                ShowProfile();
                ProfileFeedback.Text = "Đã cập nhật hồ sơ.";
            }
            catch (Exception ex)
            {
                ProfileFeedback.Text = "Không cập nhật được hồ sơ: " + ex.Message;
            }
        }

        private async void EditStatus_Click(object sender, RoutedEventArgs e)
        {
            var session = ZaloApp.Session;
            if (session == null) return;
            var input = new TextBox
            {
                Text = session.ProfileStatus ?? string.Empty,
                PlaceholderText = "Nhập trạng thái của bạn",
                MaxLength = 250,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 72
            };
            var dialog = new ContentDialog
            {
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37)),
                RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Windows.UI.Colors.White),
                Title = "Cập nhật trạng thái",
                Content = input,
                PrimaryButtonText = "Lưu",
                SecondaryButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            try
            {
                await ZaloAuth.UpdateMyStatusAsync(session, input.Text.Trim(), System.Threading.CancellationToken.None);
                session.ProfileStatus = input.Text.Trim();
                ProfileStatus.Text = string.IsNullOrWhiteSpace(session.ProfileStatus) ? "Chưa đặt trạng thái" : session.ProfileStatus;
                ProfileFeedback.Text = "Đã cập nhật trạng thái.";
            }
            catch (Exception ex)
            {
                ProfileFeedback.Text = "Không cập nhật được trạng thái: " + ex.Message;
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }
    }
}
