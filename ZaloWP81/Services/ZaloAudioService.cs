using System;
using Windows.Media.Playback;

namespace ZaloUWP.Services
{
    public class ZaloAudioService
    {
        private static ZaloAudioService _current;
        public static ZaloAudioService Current => _current ?? (_current = new ZaloAudioService());

        public ZaloAudioService()
        {
        }

        public void PlayMessageSent()
        {
            PlaySoundUri("ms-appx:///Assets/Sounds/like_action.m4a");
        }

        public void PlayIncomingRing()
        {
            PlaySoundUri("ms-appx:///Assets/Sounds/zalo_ringtone.ogg");
        }

        public void PlayCallWaiting()
        {
            PlaySoundUri("ms-appx:///Assets/Sounds/zalo_new_ringback.mp3");
        }

        public void PlayHangUp()
        {
            PlaySoundUri("ms-appx:///Assets/Sounds/zalo_new_hang_up.ogg");
        }

        private void PlaySoundUri(string uriString)
        {
            try
            {
                BackgroundMediaPlayer.Current.SetUriSource(new Uri(uriString));
                BackgroundMediaPlayer.Current.Play();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AudioService] Play error: " + ex.Message);
            }
        }
    }
}
