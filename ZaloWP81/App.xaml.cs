using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using ZaloLite;
using ZaloWP81.Views;

namespace ZaloWP81
{
    public sealed class App : Application
    {
        static void Main(string[] args)
        {
            Application.Start((p) => new App());
        }

        public App()
        {
            this.Suspending += OnSuspending;
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;
            }

            if (rootFrame.Content == null)
            {
                ZaloSession session = null;
                try
                {
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    session = await ZaloStore.RestoreAsync(cts.Token);
                }
                catch { }

                if (session != null)
                {
                    var task = ZaloApp.StartAsync(session);
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }
                else
                {
                    rootFrame.Navigate(typeof(LoginPage), e.Arguments);
                }
            }

            Window.Current.Activate();
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            deferral.Complete();
        }
    }
}
