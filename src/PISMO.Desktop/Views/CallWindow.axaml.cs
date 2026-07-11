using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PISMO.Platform;

namespace PISMO.Views
{
    /// <summary>
    /// Окно аудиозвонка. Ведёт SIPSorcery-транспорт (CallTransport, DataChannel),
    /// сигналинг через БД (CallSignaling) и платформенное аудио (IAudioDevice).
    /// Аудио идёт бинарными кадрами PCM 16кГц/моно по DataChannel — 1:1 совместимо
    /// с Windows-версией, поэтому Linux ↔ Windows звонки взаимодействуют.
    ///
    /// Видео/демонстрация экрана — следующий этап (см. docs/ROADMAP.md); окно
    /// сознательно audio-only, чтобы голосовые звонки заработали надёжно.
    /// </summary>
    public partial class CallWindow : Window
    {
        private readonly int _sessionId;
        private readonly bool _isCaller;
        private readonly string _peerName;

        private CallTransport _transport;
        private IAudioDevice _audio;
        private DispatcherTimer _answerTimer;
        private DispatcherTimer _iceTimer;
        private DispatcherTimer _statusTimer;
        private int _iceApplied;
        private bool _connected;
        private bool _closing;

        public CallWindow(int sessionId, bool isCaller, string peerName)
        {
            InitializeComponent();
            _sessionId = sessionId;
            _isCaller = isCaller;
            _peerName = peerName ?? "";

            PeerName.Text = _peerName;
            AvatarLetter.Text = string.IsNullOrEmpty(_peerName) ? "?" : _peerName.Substring(0, 1).ToUpper();

            BtnMute.Click += (_, _) => ToggleMute();
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
                    if (string.IsNullOrEmpty(offer))
                    {
                        SetStatus("Не удалось получить приглашение");
                        return;
                    }
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
                    var fresh = CallSignaling.PollRemoteIce(_sessionId, _isCaller, _iceApplied);
                    foreach (var c in fresh)
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
            if (type == CallTransport.TypeAudio && payload != null && payload.Length > 0)
                _audio?.PlaySamples(payload);
        }

        private void OnMicSamples(byte[] frame)
        {
            try { _transport?.Send(CallTransport.TypeAudio, frame); } catch { }
        }

        // ─────────── UI ───────────
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
            try { if (_audio != null) _audio.SamplesCaptured -= OnMicSamples; } catch { }
            try { _audio?.Dispose(); } catch { }
            try { _transport?.Dispose(); } catch { }
        }
    }
}
