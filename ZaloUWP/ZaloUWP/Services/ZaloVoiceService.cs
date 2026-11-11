using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace ZaloUWP.Services
{
    public class ZaloVoiceService
    {
        private static ZaloVoiceService _current;
        public static ZaloVoiceService Current => _current ?? (_current = new ZaloVoiceService());

        private MediaCapture _mediaCapture;
        private StorageFile _recordFile;
        private bool _isRecording;

        public bool IsRecording => _isRecording;
        public string LastError { get; private set; }

        public async Task<bool> StartRecordingAsync()
        {
            if (_isRecording) return false;
            LastError = string.Empty;

            try
            {
                var settings = new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Audio,
                    MediaCategory = MediaCategory.Communications
                };

                _mediaCapture = new MediaCapture();
                await _mediaCapture.InitializeAsync(settings);

                var tempFolder = ApplicationData.Current.TemporaryFolder;
                var fileName = $"voice_{DateTime.Now:yyyyMMdd_HHmmss}.m4a";
                _recordFile = await tempFolder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);

                var profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Medium);
                await _mediaCapture.StartRecordToStorageFileAsync(profile, _recordFile);

                _isRecording = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                System.Diagnostics.Debug.WriteLine($"[VoiceService] StartRecording error: {ex.Message}");
                _isRecording = false;
                _mediaCapture?.Dispose();
                _mediaCapture = null;
                return false;
            }
        }

        public async Task<StorageFile> StopRecordingAsync()
        {
            if (!_isRecording || _mediaCapture == null) return null;

            try
            {
                await _mediaCapture.StopRecordAsync();
                _isRecording = false;
                return _recordFile;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VoiceService] StopRecording error: {ex.Message}");
                _isRecording = false;
                return null;
            }
            finally
            {
                _mediaCapture?.Dispose();
                _mediaCapture = null;
            }
        }
    }
}

