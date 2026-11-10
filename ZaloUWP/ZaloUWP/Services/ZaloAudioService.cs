using System;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ZaloUWP.Services
{
    public class ZaloAudioService
    {
        private static ZaloAudioService _current;
        public static ZaloAudioService Current => _current ?? (_current = new ZaloAudioService());

        private readonly MediaPlayer _mediaPlayer;

        public ZaloAudioService()
        {
            _mediaPlayer = new MediaPlayer();
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
                var source = MediaSource.CreateFromUri(new Uri(uriString));
                _mediaPlayer.Source = source;
                _mediaPlayer.Play();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioService] Play error: {ex.Message}");
            }
        }
    }
}

