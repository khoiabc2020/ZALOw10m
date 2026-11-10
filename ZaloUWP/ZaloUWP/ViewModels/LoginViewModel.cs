using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ZaloUWP.Core;
using ZaloUWP.Models;

namespace ZaloUWP.ViewModels
{
    public class LoginViewModel : ViewModelBase
    {
        private readonly ZaloAuthService _authService;
        private QrLoginSession _currentQrSession;
        private CancellationTokenSource _pollCts;

        private string _qrImageUrl;
        public string QrImageUrl
        {
            get => _qrImageUrl;
            set => SetProperty(ref _qrImageUrl, value);
        }

        private string _statusMessage = "Đang khởi tạo...";
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        private string _manualToken = "";
        public string ManualToken
        {
            get => _manualToken;
            set => SetProperty(ref _manualToken, value);
        }

        private string _manualZpsId = "";
        public string ManualZpsId
        {
            get => _manualZpsId;
            set => SetProperty(ref _manualZpsId, value);
        }

        public ICommand RefreshQrCommand { get; }
        public ICommand LoginWithTokenCommand { get; }

        public event EventHandler LoginSucceeded;

        public LoginViewModel()
        {
            _authService = new ZaloAuthService();
            RefreshQrCommand = new RelayCommand(async () => await StartQrFlowAsync());
            LoginWithTokenCommand = new RelayCommand(async () => await LoginWithTokenAsync());
        }

        public async Task StartQrFlowAsync()
        {
            IsLoading = true;
            StatusMessage = "Đang tạo mã QR...";
            _pollCts?.Cancel();

            try
            {
                _currentQrSession = await _authService.GenerateQrAsync();
                if (_currentQrSession != null)
                {
                    QrImageUrl = _currentQrSession.QrImageUrl;
                    StatusMessage = "Mở Zalo trên điện thoại để quét mã QR";
                    StartPollingQrStatus();
                }
                else
                {
                    StatusMessage = "Không thể tạo mã QR, vui lòng bấm làm mới.";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi tạo mã QR: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void StartPollingQrStatus()
        {
            _pollCts?.Cancel();
            _pollCts = new CancellationTokenSource();
            var token = _pollCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested && _currentQrSession != null)
                {
                    try
                    {
                        var status = await _authService.CheckQrStatusAsync(_currentQrSession.Code);
                        if (token.IsCancellationRequested) break;

                        if (status == "waiting_confirm")
                        {
                            RunOnUIThread(() => StatusMessage = "Đã quét thành công! Đang chờ bạn bấm [Xác nhận đăng nhập] trên điện thoại...");
                        }
                        else if (status == "success")
                        {
                            RunOnUIThread(() =>
                            {
                                StatusMessage = "Đăng nhập thành công! Đang mở Zalo...";
                                LoginSucceeded?.Invoke(this, EventArgs.Empty);
                            });
                            break;
                        }
                        else if (status == "declined")
                        {
                            RunOnUIThread(() => StatusMessage = "Bạn đã từ chối yêu cầu đăng nhập trên điện thoại.");
                            break;
                        }
                        else if (status == "expired")
                        {
                            RunOnUIThread(() => StatusMessage = "Mã QR đã hết hạn. Vui lòng bấm làm mới.");
                            break;
                        }
                        else if (status == "waiting_scan")
                        {
                            // Tiếp tục chờ quét
                        }

                        // Chờ 1 giây trước khi gửi poll tiếp theo (tránh flood socket và xung đột)
                        await Task.Delay(1000, token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LoginViewModel] Poll loop error: {ex.Message}");
                        try
                        {
                            await Task.Delay(2000, token);
                        }
                        catch { break; }
                    }
                }
            }, token);
        }

        public async Task LoginWithTokenAsync()
        {
            if (string.IsNullOrWhiteSpace(ManualToken))
            {
                StatusMessage = "Vui lòng nhập zpw_sek!";
                return;
            }

            IsLoading = true;
            StatusMessage = "Đang đăng nhập bằng Token...";
            try
            {
                var success = await _authService.LoginWithSessionTokenAsync(ManualToken, ManualZpsId);
                if (success || ZaloSession.Current.IsLoggedIn)
                {
                    StatusMessage = "Đăng nhập thành công!";
                    LoginSucceeded?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    StatusMessage = "Token không hợp lệ hoặc đã hết hạn!";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task HandleCookiesFromWebViewAsync(string rawCookies)
        {
            IsLoading = true;
            StatusMessage = "Đang thiết lập phiên đăng nhập...";
            try
            {
                var success = await _authService.ParseAndApplyCookiesAsync(rawCookies);
                if (success)
                {
                    StatusMessage = "Đăng nhập thành công! Đang mở Zalo...";
                    LoginSucceeded?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    StatusMessage = "Chưa nhận diện được phiên đăng nhập, vui lòng thử lại.";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi đăng nhập: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void Cleanup()
        {
            _pollCts?.Cancel();
        }
    }
}

