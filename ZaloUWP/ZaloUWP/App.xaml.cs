using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using ZaloUWP.Common;
using ZaloUWP.Core;
using ZaloUWP.Models;
using ZaloUWP.Views;

namespace ZaloUWP
{
    sealed partial class App : Application
    {
        public App()
        {
            this.UnhandledException += App_UnhandledException;
            this.InitializeComponent();
            this.Suspending += OnSuspending;
        }

        private async void App_UnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            try
            {
                ZaloLite.ZaloLog.Add("App_UnhandledException: " + e.Exception);
                await new MessageDialog("Lỗi ứng dụng:\n" + e.Message + "\n\nChi tiết:\n" + e.Exception?.ToString(), "Sự cố ứng dụng").ShowAsync();
            }
            catch { }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            try
            {
                Frame rootFrame = Window.Current.Content as Frame;

                if (rootFrame == null)
                {
                    rootFrame = new Frame();
                    rootFrame.NavigationFailed += OnNavigationFailed;

                    try
                    {
                        rootFrame.RequestedTheme = Services.ZaloSettingsService.Current.AppTheme;
                    }
                    catch { }

                    Window.Current.Content = rootFrame;
                }

                try
                {
                    ApiInfo.ConfigureMobileStatusBar(Windows.UI.Color.FromArgb(255, 0, 104, 255), Windows.UI.Colors.White);
                }
                catch { }

                try
                {
                    SystemNavigationManager.GetForCurrentView().BackRequested += App_BackRequested;
                }
                catch { }

                if (rootFrame.Content == null)
                {
                    rootFrame.Navigate(typeof(MainPage), e?.Arguments);
                }

                Window.Current.Activate();
                Services.ZaloSettingsService.ResetUiScale();
            }
            catch (Exception)
            {
            }
        }

        private void App_BackRequested(object sender, BackRequestedEventArgs e)
        {
            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame != null && rootFrame.CanGoBack)
            {
                e.Handled = true;
                rootFrame.GoBack();
            }
        }

        private async void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            e.Handled = true;
            try
            {
                await new MessageDialog("Không thể mở trang " + e.SourcePageType?.Name + ": " + e.Exception?.Message, "Lỗi điều hướng").ShowAsync();
            }
            catch { }
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                ZaloSession.Current?.Save();
                await ZaloLite.ZaloApp.SaveCachedConversationsAsync();
                await ZaloLite.ZaloApp.SaveCachedFriendsAsync();
            }
            catch { }
            deferral.Complete();
        }
    }
}
