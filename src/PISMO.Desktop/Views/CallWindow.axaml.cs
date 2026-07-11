using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PISMO.Platform;

namespace PISMO.Views
{
    /// <summary>
    /// Окно звонка. Аудио — PCM по DataChannel (CallTransport) + ALSA. Видео камеры и
    /// демонстрация экрана — JPEG-кадры по тому же DataChannel (типы TypeVideo/TypeScreen),
    /// захват через ffmpeg. Такой путь не может нарушить аудио: при любой ошибке видео
    /// (нет камеры/ffmpeg, Wayland без x11grab) звонок продолжается голосом.
    ///
    /// Раскладка — «плитка» (WrapPanel тайлов): своя камера, камера и экран собеседника.
    /// Готово к расширению на групповые звонки (тайлы просто добавляются).
    /// </summary>
    public partial class CallWindow : Window
    {
        private readonly int _sessionId;
        private readonly bool _isCaller;
        private readonly string _peerName;

        private CallTransport _transport;
        private IAudioDevice _audio;
        private IVideoSource _camera;
        private IVideoSource _screen;

        private DispatcherTimer _answerTimer, _iceTimer, _statusTimer;
        private int _iceApplied;
        private bool _connected, _closing;

        private Tile _localTile, _remoteCamTile, _remoteScreenTile;

        public CallWindow(int sessionId, bool isCaller, string peerName)
        {
            InitializeComponent();
            _sessionId = sessionId;
            _isCaller = isCaller;
            _peerName = peerName ?? "";

            PeerName.Text = _peerName;
            AvatarLetter.Text = string.IsNullOrEmpty(_peerName) ? "?" : _peerName.Substring(0, 1).ToUpper();

            BtnMute.Click += (_, _) => ToggleMute();
            BtnCamera.Click += (_, _) => ToggleCamera();
            BtnScreen.Click += (_, _) => ToggleScreen();
            BtnHangup.Click += (_, _) => Hangup();
            Opened += (_, _) => _ = StartAsync();
            Closed += (_, _) => Cleanup();
        }

        private async Task StartAsync()
        {
            if (!PlatformServices.AudioAvailable)
            {
                SetStatus("Аудио недоступно на этой системе");
                return;
            }

            _audio = PlatformServices.CreateAudioDevice();
            _audio.StartPlayback();

            _transport = new CallTransport();
            _transport.FrameReceived += OnFrame;
            _transport.Connected += OnConnected;
            _transport.Disconnected += OnDisconnected;
            _transport.IceCandidateReady += OnLocalIce;

            try
            {
                if (_isCaller)
                {
                    SetStatus("Вызов…");
                    string offer = await _transport.CreateOfferAsync();
                    CallSignaling.SetCallerSdp(_sessionId, offer);
                    StartAnswerPolling();
                }
                else
                {
                    SetStatus("Соединение…");
                    var (offer, _) = CallSignaling.GetCallerSdp(_sessionId);
                    if (string.IsNullOrEmpty(offer)) { SetStatus("Нет приглашения"); return; }
                    string answer = await _transport.CreateAnswerAsync(offer);
                    CallSignaling.SetCalleeSdp(_sessionId, answer);
                }
                StartIcePolling();
                StartStatusPolling();
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка: " + ex.Message);
            }
        }

        // ─────────── Сигналинг ───────────
        private void OnLocalIce(string candidateJson)
        {
            try { CallSignaling.AppendIce(_sessionId, _isCaller, candidateJson); } catch { }
        }

        private void StartAnswerPolling()
        {
            _answerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _answerTimer.Tick += (_, _) =>
            {
                try
                {
                    var (status, sdp) = CallSignaling.PollAnswer(_sessionId);
                    if (status is "ended" or "rejected")
                    {
                        SetStatus(status == "rejected" ? "Отклонён" : "Завершён");
                        CloseSoon();
                        return;
                    }
                    if (!string.IsNullOrEmpty(sdp))
                    {
                        _answerTimer.Stop();
                        _transport.ApplyAnswer(sdp);
                        SetStatus("Установка соединения…");
                    }
                }
                catch { }
            };
            _answerTimer.Start();
        }

        private void StartIcePolling()
        {
            _iceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _iceTimer.Tick += (_, _) =>
            {
                try
                {
                    foreach (var c in CallSignaling.PollRemoteIce(_sessionId, _isCaller, _iceApplied))
                    {
                        _transport.AddRemoteIceCandidate(c);
                        _iceApplied++;
                    }
                }
                catch { }
            };
            _iceTimer.Start();
        }

        private void StartStatusPolling()
        {
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += (_, _) =>
            {
                try
                {
                    var s = CallSignaling.GetStatus(_sessionId);
                    if (s is "ended" or "rejected" && !_connected)
                    {
                        SetStatus(s == "rejected" ? "Отклонён" : "Завершён");
                        CloseSoon();
                    }
                }
                catch { }
            };
            _statusTimer.Start();
        }

        // ─────────── Транспорт ───────────
        private void OnConnected()
        {
            Dispatcher.UIThread.Post(() =>
            {
                _connected = true;
                _answerTimer?.Stop();
                SetStatus("В разговоре");
            });
            _audio.SamplesCaptured += OnMicSamples;
            _audio.StartCapture();
        }

        private void OnDisconnected()
        {
            Dispatcher.UIThread.Post(() =>
            {
                SetStatus("Собеседник отключился");
                CloseSoon();
            });
        }

        private void OnFrame(byte type, byte[] payload)
        {
            if (payload == null) return;
            switch (type)
            {
                case CallTransport.TypeAudio:
                    _audio?.PlaySamples(payload);
                    break;
                case CallTransport.TypeVideo:
                    Dispatcher.UIThread.Post(() => ShowFrame(ref _remoteCamTile, "Камера собеседника", payload));
                    break;
                case CallTransport.TypeScreen:
                    Dispatcher.UIThread.Post(() => ShowFrame(ref _remoteScreenTile, "Экран собеседника", payload));
                    break;
                case CallTransport.TypeScreenStop:
                    Dispatcher.UIThread.Post(() => RemoveTile(ref _remoteScreenTile));
                    break;
            }
        }

        private void OnMicSamples(byte[] frame)
        {
            try { _transport?.Send(CallTransport.TypeAudio, frame); } catch { }
        }

        // ─────────── Камера ───────────
        private void ToggleCamera()
        {
            if (_camera != null)
            {
                _camera.JpegFrameReady -= OnLocalCameraFrame;
                try { _camera.Stop(); } catch { }
                _camera = null;
                BtnCamera.Background = new SolidColorBrush(Color.Parse("#40444b"));
                RemoveTile(ref _localTile);
                return;
            }
            if (!PlatformServices.CameraAvailable)
            {
                SetStatus("Камера недоступна (нужен ffmpeg)");
                return;
            }
            _camera = PlatformServices.CreateCameraSource();
            _camera.Error += e => Dispatcher.UIThread.Post(() => SetStatus(e));
            _camera.JpegFrameReady += OnLocalCameraFrame;
            _camera.Start();
            BtnCamera.Background = new SolidColorBrush(Color.Parse("#43b581"));
        }

        private void OnLocalCameraFrame(byte[] jpeg)
        {
            try { _transport?.Send(CallTransport.TypeVideo, jpeg); } catch { }
            Dispatcher.UIThread.Post(() => ShowFrame(ref _localTile, "Вы", jpeg));
        }

        // ─────────── Экран ───────────
        private void ToggleScreen()
        {
            if (_screen != null)
            {
                _screen.JpegFrameReady -= OnLocalScreenFrame;
                try { _screen.Stop(); } catch { }
                _screen = null;
                BtnScreen.Background = new SolidColorBrush(Color.Parse("#40444b"));
                try { _transport?.Send(CallTransport.TypeScreenStop, Array.Empty<byte>()); } catch { }
                return;
            }
            if (!PlatformServices.ScreenShareAvailable)
            {
                SetStatus("Демонстрация экрана недоступна (нужен ffmpeg, X11)");
                return;
            }
            _screen = PlatformServices.CreateScreenSource();
            _screen.Error += e => Dispatcher.UIThread.Post(() => SetStatus(e));
            _screen.JpegFrameReady += OnLocalScreenFrame;
            _screen.Start();
            BtnScreen.Background = new SolidColorBrush(Color.Parse("#43b581"));
        }

        private void OnLocalScreenFrame(byte[] jpeg)
        {
            try { _transport?.Send(CallTransport.TypeScreen, jpeg); } catch { }
        }

        // ─────────── Тайлы ───────────
        private sealed class Tile
        {
            public Border Root;
            public Image Image;
            public Bitmap Current;
        }

        private void ShowFrame(ref Tile tile, string caption, byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length < 4) return;
            tile ??= CreateTile(caption);

            try
            {
                Bitmap bmp;
                using (var ms = new MemoryStream(jpeg)) bmp = new Bitmap(ms);
                var old = tile.Current;
                tile.Image.Source = bmp;
                tile.Current = bmp;
                old?.Dispose();
            }
            catch { /* битый кадр — пропускаем */ }
        }

        private Tile CreateTile(string caption)
        {
            var img = new Image { Stretch = Stretch.Uniform, Width = 320, Height = 200 };
            var cap = new TextBlock
            {
                Text = caption, Foreground = new SolidColorBrush(Color.Parse("#b9bbbe")),
                FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
            };
            var root = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#202225")),
                CornerRadius = new Avalonia.CornerRadius(8),
                Padding = new Avalonia.Thickness(6),
                Margin = new Avalonia.Thickness(6),
                Child = new StackPanel { Children = { img, cap } },
            };
            var tile = new Tile { Root = root, Image = img };
            TilesHost.Children.Add(root);
            UpdatePlaceholder();
            return tile;
        }

        private void RemoveTile(ref Tile tile)
        {
            if (tile == null) return;
            try { TilesHost.Children.Remove(tile.Root); } catch { }
            try { tile.Current?.Dispose(); } catch { }
            tile = null;
            UpdatePlaceholder();
        }

        private void UpdatePlaceholder()
            => AudioPlaceholder.IsVisible = TilesHost.Children.Count == 0;

        // ─────────── UI / завершение ───────────
        private void ToggleMute()
        {
            if (_audio == null) return;
            _audio.Muted = !_audio.Muted;
            BtnMute.Content = _audio.Muted ? "🔇" : "🎤";
            BtnMute.Background = new SolidColorBrush(
                _audio.Muted ? Color.Parse("#f04747") : Color.Parse("#40444b"));
        }

        private void Hangup()
        {
            try { _transport?.SendHangup(); } catch { }
            try { CallSignaling.EndCall(_sessionId); } catch { }
            CloseSoon();
        }

        private void SetStatus(string s) => StatusLabel.Text = s;

        private void CloseSoon()
        {
            if (_closing) return;
            _closing = true;
            Dispatcher.UIThread.Post(() => { try { Close(); } catch { } });
        }

        private void Cleanup()
        {
            _answerTimer?.Stop();
            _iceTimer?.Stop();
            _statusTimer?.Stop();
            try { CallSignaling.EndCall(_sessionId); } catch { }
            try { if (_camera != null) { _camera.JpegFrameReady -= OnLocalCameraFrame; _camera.Stop(); } } catch { }
            try { if (_screen != null) { _screen.JpegFrameReady -= OnLocalScreenFrame; _screen.Stop(); } } catch { }
            try { if (_audio != null) _audio.SamplesCaptured -= OnMicSamples; } catch { }
            try { _audio?.Dispose(); } catch { }
            try { _transport?.Dispose(); } catch { }
        }
    }
}
