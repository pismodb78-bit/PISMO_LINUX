using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PISMO
{
    /// <summary>
    /// Клиент ws-сервера сигналинга — порт PISMO/WebSocketSignalingClient.cs.
    ///
    /// ЗАЧЕМ ОН НУЖЕН. До сих пор Linux-клиент знал только один способ узнать о
    /// новостях: спросить базу. Раз в несколько секунд, по кругу, про всё
    /// сразу. Отсюда и задержка, и лишние запросы. Через сокет то же событие
    /// приходит сразу же — новое сообщение, звонок, «печатает», прочтение,
    /// закреп, смена статуса.
    ///
    /// Сервер необязателен. Если его нет или связь оборвалась, клиент
    /// продолжает работать на опросе базы — ровно как раньше. Поэтому все
    /// ошибки здесь глушатся: сигналинг ускоряет, но ничего не решает.
    ///
    /// Протокол тот же, что у Windows и Android: {type, userId, targetUserId,
    /// sessionId, payload}. targetUserId = 0 — всем; равный своему id — только
    /// остальным своим устройствам.
    /// </summary>
    public sealed class SignalingClient
    {
        private static SignalingClient _instance;
        public static SignalingClient Instance => _instance ??= new SignalingClient();

        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private int _myUserId;
        private bool _isConnecting;
        private bool _wantConnection;
        private bool _softToken;      // после auth_error регистрируемся без токена

        public bool IsConnected => _ws?.State == WebSocketState.Open;

        /// <summary>type, senderUserId, sessionId, payload.</summary>
        public event Action<string, int, int, string> OnMessage;

        // ── Проверка живости ────────────────────────────────────────────
        //
        // «Сокет открыт» ещё не значит «доставляет». Раз в семь секунд шлём
        // ping и ждём pong. Но строгий режим включаем только ПОСЛЕ первого
        // полученного pong: старый сервер на ping не отвечает вовсе, и без
        // этой оговорки канал навсегда считался бы мёртвым.
        private DateTime _lastTrafficUtc = DateTime.MinValue;
        private bool _pongEverReceived;
        private Timer _pingTimer;
        private const int PingIntervalMs = 7000;
        private const int HealthWindowSec = 20;

        public bool IsHealthy => IsConnected
            && (!_pongEverReceived
                || (DateTime.UtcNow - _lastTrafficUtc).TotalSeconds < HealthWindowSec);

        public async Task ConnectAsync(int userId)
        {
            if (_isConnecting || IsConnected) return;
            _myUserId = userId;
            _wantConnection = true;
            _isConnecting = true;

            try
            {
                string url = WebSocketUrl();
                _ws = new ClientWebSocket();
                _cts = new CancellationTokenSource();
                await _ws.ConnectAsync(new Uri(url), _cts.Token);

                string token = _softToken ? "" : JwtAuth.Create(userId, UserSession.EffectiveName);
                var reg = JsonSerializer.Serialize(new { type = "register", userId, token });
                await SendRawAsync(reg, _cts.Token);

                _ = Task.Run(() => ReceiveLoop(_cts.Token));
                StartPing();
            }
            catch
            {
                Reconnect(5000);
            }
            finally { _isConnecting = false; }
        }

        public void Disconnect()
        {
            _wantConnection = false;
            try { _cts?.Cancel(); } catch { }
            try
            {
                if (_ws != null && _ws.State == WebSocketState.Open)
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
            try { _ws?.Dispose(); } catch { }
            _ws = null;
            try { _pingTimer?.Dispose(); _pingTimer = null; } catch { }
        }

        /// <summary>Отправка события. Нет сокета — молча ничего, как на ПК.</summary>
        public void Send(string type, int targetUserId, int sessionId, string payload)
        {
            if (!IsConnected) return;
            try
            {
                var msg = JsonSerializer.Serialize(new
                {
                    type,
                    userId = _myUserId,
                    targetUserId,
                    sessionId,
                    payload = payload ?? "",
                });
                _ = SendRawAsync(msg, CancellationToken.None);
            }
            catch { }
        }

        // ── Внутреннее ──────────────────────────────────────────────────

        private Task SendRawAsync(string json, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            return _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }

        private void StartPing()
        {
            SendPing();
            try { _pingTimer?.Dispose(); } catch { }
            _pingTimer = new Timer(_ => SendPing(), null, PingIntervalMs, PingIntervalMs);
        }

        private void SendPing()
        {
            if (!IsConnected) return;
            try { _ = SendRawAsync("{\"type\":\"ping\"}", CancellationToken.None); } catch { }
        }

        private void Reconnect(int delayMs)
        {
            if (!_wantConnection) return;
            _ = Task.Run(async () =>
            {
                await Task.Delay(delayMs);
                _isConnecting = false;
                if (_wantConnection && !IsConnected) await ConnectAsync(_myUserId);
            });
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text) continue;

                    string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        // Разбор терпимый: отсутствие ключа не должно ронять
                        // приём — иначе одно кривое сообщение съедает все
                        // следующие.
                        string type = Str(root, "type");
                        if (type.Length == 0) continue;

                        // Любой входящий трафик — доказательство, что канал жив.
                        _lastTrafficUtc = DateTime.UtcNow;
                        if (type == "pong") { _pongEverReceived = true; continue; }
                        if (type == "auth_error") { _softToken = true; continue; }

                        OnMessage?.Invoke(type, Int(root, "userId"), Int(root, "sessionId"), Str(root, "payload"));
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                _isConnecting = false;
                try { _pingTimer?.Dispose(); _pingTimer = null; } catch { }
                _lastTrafficUtc = DateTime.MinValue;
                _pongEverReceived = false;
                if (!token.IsCancellationRequested) Reconnect(3000);
            }
        }

        private static string Str(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var v)) return "";
            try { return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.ToString(); }
            catch { return ""; }
        }

        private static int Int(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var v)) return 0;
            try
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int m)) return m;
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Адрес сокета из ip.txt — ключ ws= (или websocket=). Их нет —
        /// собираем из server= и порта 8080, как делает Windows-версия.
        /// </summary>
        private static string WebSocketUrl()
        {
            string host = "localhost";
            const int port = 8080;
            try
            {
                string line = null;
                foreach (var p in new[]
                         {
                             Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ip.txt"),
                             Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "ip.txt"),
                             "ip.txt",
                         })
                {
                    if (File.Exists(p)) { line = File.ReadAllText(p).Trim(); break; }
                }
                if (string.IsNullOrWhiteSpace(line)) return $"ws://{host}:{port}/";

                foreach (var part in line.Split(';'))
                {
                    var seg = part.Trim();
                    if (seg.StartsWith("ws=", StringComparison.OrdinalIgnoreCase))
                        return seg.Substring(3);
                    if (seg.StartsWith("websocket=", StringComparison.OrdinalIgnoreCase))
                        return seg.Substring(10);
                }
                foreach (var part in line.Split(';'))
                {
                    var seg = part.Trim();
                    if (seg.StartsWith("server=", StringComparison.OrdinalIgnoreCase))
                    { host = seg.Substring(7); break; }
                }
                if (!line.Contains('=') && !line.Contains(';')) host = line.Split(':')[0].Trim();
            }
            catch { }
            return $"ws://{host}:{port}/";
        }
    }
}
