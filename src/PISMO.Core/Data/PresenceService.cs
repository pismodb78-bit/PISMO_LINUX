using System;
using System.Collections.Generic;
using System.Text;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Статусы присутствия: в сети / бездействует / не в сети.
    /// Порт MainForm_Presence.cs — те же колонки, те же пороги, тот же расчёт.
    ///
    /// ЧТО СЧИТАЕТСЯ АКТИВНОСТЬЮ. На Windows это системный простой ввода
    /// (GetLastInputInfo): не трогал мышь — бездействует. Такого одного
    /// способа на Linux нет: XScreenSaver есть не везде и на Wayland не
    /// работает вовсе, а тащить ради этого зависимость, которой может не
    /// оказаться у человека, — плохой размен. Берём тот же признак, что и
    /// Android: окно программы активно. Признак честный и ни от чего не
    /// зависит; «сижу в другой программе» при этом считается простоем, что
    /// для статуса даже точнее.
    ///
    /// Статус ходит двумя путями сразу: через базу (heartbeat) и по сокету
    /// (сразу, но только когда изменился). База — источник правды для тех,
    /// кто подключился позже или до кого событие не дошло.
    /// </summary>
    public static class PresenceService
    {
        /// <summary>Сколько секунд без heartbeat считаем «не в сети».</summary>
        public const int SeenOfflineSec = 40;

        /// <summary>Сколько секунд без активности считаем «бездействует».</summary>
        public const int ActiveIdleSec = 90;

        /// <summary>Период heartbeat — тот же, что у ПК и телефона.</summary>
        public const int TickMs = 6000;

        /// <summary>0 — не в сети, 1 — бездействует, 2 — в сети.</summary>
        public static int StatusFrom(int seenAgo, int activeAgo)
        {
            if (seenAgo > SeenOfflineSec) return 0;
            if (activeAgo > ActiveIdleSec) return 1;
            return 2;
        }

        /// <summary>Присутствия нет в схеме — больше не долбимся.</summary>
        private static bool _columnsOk = true;

        private static bool SchemaMissing(Exception ex)
            => ex is MySqlException my && (my.Number == 1054 || my.Number == 1146);

        // ── Heartbeat ───────────────────────────────────────────────────

        /// <summary>
        /// Отмечается «я здесь» и пишет НАСТОЯЩИЙ момент последней активности.
        ///
        /// Не «был ли активен только что»: при такой записи метка ещё какое-то
        /// время после ухода подтягивается к текущему времени, и порог в 90
        /// секунд срабатывает заметно позже девяноста. GREATEST — чтобы метка
        /// не поехала назад.
        /// </summary>
        public static void Heartbeat(int idleSec)
        {
            if (!_columnsOk) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "UPDATE users SET last_seen=NOW(), " +
                    "last_active = GREATEST(COALESCE(last_active, '1970-01-02'), " +
                    "                       NOW() - INTERVAL @idle SECOND) " +
                    "WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@idle", Math.Max(0, idleSec));
                cmd.Parameters.AddWithValue("@id", UserSession.EffectiveId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { if (SchemaMissing(ex)) _columnsOk = false; }
        }

        /// <summary>При выходе — сразу «не в сети», не дожидаясь таймаута.</summary>
        public static void MarkOffline()
        {
            // Сначала по сокету: дойдёт мгновенно. Запись в базу — для тех,
            // кого сейчас нет на связи.
            try { SignalingClient.Instance.Send("presence", 0, 0, "0"); } catch { }
            if (!_columnsOk) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "UPDATE users SET last_seen = DATE_SUB(NOW(), INTERVAL 1 HOUR) WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", UserSession.EffectiveId);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        // ── Чтение чужих статусов ───────────────────────────────────────

        public sealed class Ago
        {
            public int SeenAgo = int.MaxValue;
            public int ActiveAgo = int.MaxValue;
            public int Status => StatusFrom(SeenAgo, ActiveAgo);
        }

        /// <summary>Статусы перечисленных людей. null — спросить не вышло.</summary>
        public static Dictionary<int, int> Read(IReadOnlyList<int> ids)
        {
            if (!_columnsOk || ids == null || ids.Count == 0) return null;
            try
            {
                var sb = new StringBuilder();
                for (int i = 0; i < ids.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(ids[i]);
                }

                var result = new Dictionary<int, int>();
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT id, TIMESTAMPDIFF(SECOND, last_seen, NOW()) AS seen_ago, " +
                    "TIMESTAMPDIFF(SECOND, last_active, NOW()) AS active_ago " +
                    $"FROM users WHERE id IN ({sb})", conn);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int seen = r["seen_ago"] == DBNull.Value ? int.MaxValue : Convert.ToInt32(r["seen_ago"]);
                    int act = r["active_ago"] == DBNull.Value ? int.MaxValue : Convert.ToInt32(r["active_ago"]);
                    result[Convert.ToInt32(r["id"])] = StatusFrom(seen, act);
                }
                return result;
            }
            catch (Exception ex) { if (SchemaMissing(ex)) _columnsOk = false; return null; }
        }

        /// <summary>Подробности по одному — для подписи в шапке чата.</summary>
        public static Ago ReadOne(int uid)
        {
            if (!_columnsOk || uid <= 0) return null;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT TIMESTAMPDIFF(SECOND, last_seen, NOW()) AS seen_ago, " +
                    "TIMESTAMPDIFF(SECOND, last_active, NOW()) AS active_ago FROM users WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", uid);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                return new Ago
                {
                    SeenAgo = r["seen_ago"] == DBNull.Value ? int.MaxValue : Convert.ToInt32(r["seen_ago"]),
                    ActiveAgo = r["active_ago"] == DBNull.Value ? int.MaxValue : Convert.ToInt32(r["active_ago"]),
                };
            }
            catch (Exception ex) { if (SchemaMissing(ex)) _columnsOk = false; return null; }
        }

        // ── Тексты ──────────────────────────────────────────────────────

        public static string HumanDur(int s)
        {
            if (s < 60) return "меньше минуты";
            int m = s / 60; if (m < 60) return $"{m} мин";
            int h = m / 60; if (h < 24) return $"{h} ч";
            return $"{h / 24} дн";
        }

        public static string HumanAgo(int s)
        {
            if (s < 60) return "только что";
            int m = s / 60; if (m < 60) return $"{m} мин назад";
            int h = m / 60; if (h < 24) return $"{h} ч назад";
            return $"{h / 24} дн назад";
        }

        /// <summary>Подпись статуса — та же, что в шапке чата на ПК.</summary>
        public static string Text(int status, int seenAgo, int activeAgo)
        {
            if (status == 0) return $"был(а) в сети {HumanAgo(seenAgo)}";
            if (status == 1) return $"● бездействует {HumanDur(activeAgo)}";
            return "● в сети";
        }

        // ── Рассылка своего статуса ─────────────────────────────────────

        private static int _broadcastStatus = -1;
        private static DateTime _broadcastAt = DateTime.MinValue;

        /// <summary>
        /// Сообщает свой статус остальным, когда он изменился. Плюс раз в 30
        /// секунд — иначе каждый клиент каждые шесть секунд слал бы всем одно
        /// и то же.
        /// </summary>
        public static void Announce(int idleSec)
        {
            try
            {
                // Сокета нет — и отмечать нечего: иначе запомним разосланное,
                // которого никто не получил.
                if (!SignalingClient.Instance.IsConnected) return;

                int st = idleSec > ActiveIdleSec ? 1 : 2;
                bool changed = st != _broadcastStatus;
                bool stale = (DateTime.UtcNow - _broadcastAt).TotalSeconds >= 30;
                if (!changed && !stale) return;

                _broadcastStatus = st;
                _broadcastAt = DateTime.UtcNow;
                // sessionId — статус, payload — простой в секундах: из него
                // получатель сразу строит «бездействует 5 мин».
                SignalingClient.Instance.Send("presence", 0, st, idleSec.ToString());
            }
            catch { }
        }
    }
}
