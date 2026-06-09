using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;

namespace MediaPlayerCore
{
    public class FFmpegMediaObject : IDisposable
    {
        private MediaManager _parent;
        private Process _ffmpegProcess;
        private Process _ffmpegAudioProcess;
        private string _ffmpegPath;
        private bool _disposed;
        private readonly object _disposeLock = new object();

        public IMediaGameObject CharacterObject { get; set; }
        public bool SpatialAllowed { get; set; }

        private IWavePlayer _waveOut;
        private WaveFormat _waveFormat;
        private BufferedWaveProvider _bufferedWaveProvider;
        private PanningSampleProvider _panningProvider;
        private VolumeSampleProvider _volumeProvider;

        public float Pan {
            get => _panningProvider?.Pan ?? 0;
            set {
                if (_panningProvider != null) {
                    _panningProvider.Pan = Math.Clamp(value, -1f, 1f);
                }
            }
        }

        public float Volume {
            set {
                if (_volumeProvider != null) {
                    float clampedValue = Math.Max(0f, value);
                    float scale = clampedValue;
                    if (clampedValue <= 1.0f) {
                        scale = (float)Math.Pow(clampedValue, 3);
                    } else {
                        scale = 1.0f + (clampedValue - 1.0f);
                    }
                    _volumeProvider.Volume = scale * 2.0f;
                }
            }
        }

        private int _width = 1280;
        private int _height = 720;
        private int _bytesPerPixel = 4;
        private int _frameSize;
        private Thread _readerThread;

        public event EventHandler<string> PlaybackStopped;
        public event EventHandler<MediaError> OnErrorReceived;
        public bool IsPlaying {
            get {
                try {
                    return _ffmpegProcess != null && !_ffmpegProcess.HasExited;
                } catch {
                    return false;
                }
            }
        }

        public FFmpegMediaObject(MediaManager parent, string ffmpegPath)
        {
            _parent = parent;
            _ffmpegPath = ffmpegPath;
            _frameSize = _width * _height * _bytesPerPixel;
            _parent.OnCleanupTime += _parent_OnCleanupTime;
        }

        private void _parent_OnCleanupTime(object? sender, EventArgs e)
        {
            Stop();
        }

        public void Play(string url, IMediaGameObject characterObject = null, bool spatialAllowed = false)
        {
            lock (_disposeLock)
            {
                if (_disposed) return;

                CharacterObject = characterObject;
                SpatialAllowed = spatialAllowed;

                try
                {
                    Stop();
                    _disposed = false;

                    lock (_parent.FrameLock)
                    {
                        _parent.LastFrame = new byte[_frameSize];
                        _parent.LastFrameWidth = _width;
                        _parent.LastFrameHeight = _height;
                        _parent.LastFrameCount++;
                    }

                    Task.Run(() =>
                    {
                        // Generate random local UDP port for audio
                        var udpClient = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
                        int audioPort = ((System.Net.IPEndPoint)udpClient.Client.LocalEndPoint).Port;

                        _ffmpegProcess = new Process();
                        _ffmpegProcess.StartInfo.FileName = _ffmpegPath;
                        // Single process decoding both video to stdout and audio to UDP
                        _ffmpegProcess.StartInfo.Arguments = $"-hwaccel auto -rtsp_transport tcp -use_wallclock_as_timestamps 1 -fflags nobuffer -flags low_delay -i \"{url}\" -map 0:v -vf scale={_width}:{_height} -sws_flags fast_bilinear -f rawvideo -pix_fmt bgra pipe:1 -map 0:a? -f s16le -ac 1 -ar 48000 udp://127.0.0.1:{audioPort}";
                        _ffmpegProcess.StartInfo.UseShellExecute = false;
                        _ffmpegProcess.StartInfo.RedirectStandardOutput = true;
                        _ffmpegProcess.StartInfo.RedirectStandardError = true;
                        _ffmpegProcess.StartInfo.CreateNoWindow = true;

                        _ffmpegProcess.ErrorDataReceived += (s, e) =>
                        {
                            if (e.Data != null)
                                Debug.WriteLine($"[FFmpegMediaObject] {e.Data}");
                        };

                        _waveFormat = new WaveFormat(48000, 16, 1);
                        _bufferedWaveProvider = new BufferedWaveProvider(_waveFormat);
                        _bufferedWaveProvider.BufferDuration = TimeSpan.FromSeconds(10);
                        _bufferedWaveProvider.DiscardOnBufferOverflow = true;

                        _panningProvider = new PanningSampleProvider(_bufferedWaveProvider.ToSampleProvider());
                        _panningProvider.Pan = 0;

                        _volumeProvider = new VolumeSampleProvider(_panningProvider);
                        _volumeProvider.Volume = 1.0f;

                        _waveOut = new WasapiOut(AudioClientShareMode.Shared, 50);
                        _waveOut.Init(_volumeProvider);

                        _ffmpegProcess.Start();
                        _ffmpegProcess.BeginErrorReadLine();

                        _readerThread = new Thread(ReadFrames);
                        _readerThread.IsBackground = true;
                        _readerThread.Start();

                        Task.Run(async () => {
                            try {
                                bool startedPlaying = false;
                                while (!_disposed) {
                                    var result = await udpClient.ReceiveAsync();
                                    if (result.Buffer.Length > 0 && !_disposed) {
                                        _bufferedWaveProvider.AddSamples(result.Buffer, 0, result.Buffer.Length);
                                        if (!startedPlaying && _bufferedWaveProvider.BufferedDuration.TotalMilliseconds > 50) {
                                            _waveOut.Play();
                                            startedPlaying = true;
                                        }
                                    }
                                }
                            } catch { }
                            finally {
                                try { udpClient.Dispose(); } catch { }
                            }
                        });
                    });
                }
                catch (Exception ex)
                {
                    OnErrorReceived?.Invoke(this, new MediaError() { Exception = ex });
                }
            }
        }

        private void ReadFrames()
        {
            try
            {
                using (var stream = _ffmpegProcess.StandardOutput.BaseStream)
                {
                    byte[] buffer = new byte[_frameSize];
                    while (!_disposed && !_ffmpegProcess.HasExited)
                    {
                        int bytesRead = 0;
                        while (bytesRead < _frameSize && !_disposed)
                        {
                            int read = stream.Read(buffer, bytesRead, _frameSize - bytesRead);
                            if (read == 0) break;
                            bytesRead += read;
                        }

                        if (bytesRead == _frameSize && !_disposed)
                        {
                            lock (_parent.FrameLock)
                            {
                                if (_parent.LastFrame.Length != _frameSize)
                                {
                                    _parent.LastFrame = new byte[_frameSize];
                                }
                                Buffer.BlockCopy(buffer, 0, _parent.LastFrame, 0, _frameSize);
                                _parent.LastFrameWidth = _width;
                                _parent.LastFrameHeight = _height;
                                _parent.LastFrameCount++;
                            }
                        }
                        else
                        {
                            break; // EOF or stopped
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FFmpegMediaObject] Stream read error: {ex.Message}");
            }
            finally
            {
                Stop();
            }
        }

        public void Stop()
        {
            lock (_disposeLock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            PlaybackStopped?.Invoke(this, "OK");

            try
            {
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.Kill();
                    _ffmpegProcess.Dispose();
                }
            }
            catch { }

            try {
                if (_waveOut != null) {
                    _waveOut.Stop();
                    _waveOut.Dispose();
                    _waveOut = null;
                }
            } catch { }

            lock (_parent.FrameLock)
            {
                _parent.LastFrame = Array.Empty<byte>();
                _parent.LastFrameWidth = 0;
                _parent.LastFrameHeight = 0;
                _parent.LastFrameCount++;
            }
        }

        public void Dispose()
        {
            _parent.OnCleanupTime -= _parent_OnCleanupTime;
            Stop();
            _ffmpegProcess?.Dispose();
        }
    }
}
