using System;
using System.Collections.Generic;
using System.Text.Json;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Сигналинг звонков через БД (таблица <c>call_sessions</c>). Протокол полностью
    /// совместим с Windows-версией (CallForm / MainForm_MessageActions):
    ///
    ///   Звонящий:   INSERT (status='ringing') → пишет caller_sdp → ждёт callee_sdp/status='active'
    ///   Принимающий: видит 'ringing' по callee_id → читает caller_sdp → пишет callee_sdp/status='active'
    ///   Оба:        дописывают ICE в caller_ice / callee_ice (JSON-массив строк-кандидатов),
    ///               опрашивают колонку собеседника.
    /// </summary>
    public static class CallSignaling
    {
        public sealed class Incoming
        {
            public int SessionId;
            public int CallerId;
            public string CallerName = "";
            public bool HasVideo;
        }

        /// <summary>Ищет уже активный звонок с этим собеседником (обратный заход).</summary>
        public static int FindActiveSession(int myId, int peerId)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "SELECT id FROM call_sessions WHERE " +
                "((caller_id=@me AND callee_id=@peer) OR (caller_id=@peer AND callee_id=@me)) " +
                "AND status IN ('ringing','active') ORDER BY id DESC LIMIT 1", conn);
            cmd.Parameters.AddWithValue("@me", myId);
            cmd.Parameters.AddWithValue("@peer", peerId);
            var obj = cmd.ExecuteScalar();
            return obj != null && obj != DBNull.Value ? Convert.ToInt32(obj) : -1;
        }

        /// <summary>Создаёт новую сессию звонка. Возвращает id.</summary>
        public static int CreateCall(int callerId, int calleeId, bool hasVideo)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "INSERT INTO call_sessions (caller_id, callee_id, group_id, status, has_video) " +
                "VALUES (@c, @e, NULL, 'ringing', @v)", conn);
            cmd.Parameters.AddWithValue("@c", callerId);
            cmd.Parameters.AddWithValue("@e", calleeId);
            cmd.Parameters.AddWithValue("@v", hasVideo ? 1 : 0);
            cmd.ExecuteNonQuery();
            return (int)cmd.LastInsertedId;
        }

        public static void SetCallerSdp(int sessionId, string sdp)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "UPDATE call_sessions SET caller_sdp=@sdp, status='ringing' WHERE id=@id", conn);
            cmd.Parameters.AddWithValue("@sdp", sdp);
            cmd.Parameters.AddWithValue("@id", sessionId);
            cmd.ExecuteNonQuery();
        }

        public static (string sdp, bool hasVideo) GetCallerSdp(int sessionId)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "SELECT caller_sdp, has_video FROM call_sessions WHERE id=@id", conn);
            cmd.Parameters.AddWithValue("@id", sessionId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                string sdp = r["caller_sdp"] == DBNull.Value ? null : r["caller_sdp"].ToString();
                bool hv = r["has_video"] != DBNull.Value && Convert.ToBoolean(r["has_video"]);
                return (sdp, hv);
            }
            return (null, false);
        }

        public static void SetCalleeSdp(int sessionId, string sdp)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "UPDATE call_sessions SET callee_sdp=@sdp, status='active' WHERE id=@id", conn);
            cmd.Parameters.AddWithValue("@sdp", sdp);
            cmd.Parameters.AddWithValue("@id", sessionId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>Опрос статуса и answer-SDP (для звонящего).</summary>
        public static (string status, string calleeSdp) PollAnswer(int sessionId)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "SELECT status, callee_sdp FROM call_sessions WHERE id=@id", conn);
            cmd.Parameters.AddWithValue("@id", sessionId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                string status = r["status"]?.ToString() ?? "";
                string sdp = r["callee_sdp"] == DBNull.Value ? null : r["callee_sdp"].ToString();
                return (status, sdp);
            }
            return ("ended", null);
        }

        public static string GetStatus(int sessionId)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand("SELECT status FROM call_sessions WHERE id=@id", conn);
            cmd.Parameters.AddWithValue("@id", sessionId);
            var obj = cmd.ExecuteScalar();
            return obj?.ToString() ?? "ended";
        }

        /// <summary>Дописывает свой ICE-кандидат в caller_ice/callee_ice (JSON-массив строк).</summary>
        public static void AppendIce(int sessionId, bool isCaller, string candidateJson)
        {
            string column = isCaller ? "caller_ice" : "callee_ice";
            using var conn = DBHelper.OpenConnection();

            string existing;
            using (var read = new MySqlCommand($"SELECT {column} FROM call_sessions WHERE id=@id", conn))
            {
                read.Parameters.AddWithValue("@id", sessionId);
                var obj = read.ExecuteScalar();
                existing = obj == null || obj == DBNull.Value ? null : obj.ToString();
            }

            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                try { list = JsonSerializer.Deserialize<List<string>>(existing) ?? new(); }
                catch { list = new(); }
            }
            list.Add(candidateJson);

            using var upd = new MySqlCommand($"UPDATE call_sessions SET {column}=@v WHERE id=@id", conn);
            upd.Parameters.AddWithValue("@v", JsonSerializer.Serialize(list));
            upd.Parameters.AddWithValue("@id", sessionId);
            upd.ExecuteNonQuery();
        }

        /// <summary>
        /// Возвращает ICE-кандидатов собеседника, накопленных сверх уже применённых
        /// (<paramref name="alreadyApplied"/>). Кандидат — JSON-строка для CallTransport.AddRemoteIceCandidate.
        /// </summary>
        public static List<string> PollRemoteIce(int sessionId, bool isCaller, int alreadyApplied)
        {
            // звонящий читает ICE принимающего и наоборот
            string column = isCaller ? "callee_ice" : "caller_ice";
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand($"SELECT {column} FROM call_sessions WHERE id=@id", conn);
            cmd.Parameters.AddWithValue("@id", sessionId);
            var obj = cmd.ExecuteScalar();
            if (obj == null || obj == DBNull.Value) return new List<string>();

            List<string> all;
            try { all = JsonSerializer.Deserialize<List<string>>(obj.ToString()) ?? new(); }
            catch { return new List<string>(); }

            if (alreadyApplied >= all.Count) return new List<string>();
            return all.GetRange(alreadyApplied, all.Count - alreadyApplied);
        }

        /// <summary>Входящие звонки для пользователя (status='ringing', я — callee).</summary>
        public static List<Incoming> CheckIncoming(int myId, int lastCheckedId)
        {
            var result = new List<Incoming>();
            using var conn = DBHelper.OpenConnection();
            const string sql = @"
                SELECT cs.id, cs.caller_id, cs.has_video,
                       TRIM(CONCAT(u.Name,' ',u.Surname)) AS caller_name, u.login
                FROM call_sessions cs
                JOIN users u ON u.id = cs.caller_id
                WHERE cs.callee_id = @me
                  AND cs.status = 'ringing'
                  AND cs.caller_id != @me
                  AND cs.id > @last";
            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@me", myId);
            cmd.Parameters.AddWithValue("@last", lastCheckedId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string name = r["caller_name"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(name)) name = r["login"]?.ToString();
                result.Add(new Incoming
                {
                    SessionId = Convert.ToInt32(r["id"]),
                    CallerId = Convert.ToInt32(r["caller_id"]),
                    HasVideo = r["has_video"] != DBNull.Value && Convert.ToBoolean(r["has_video"]),
                    CallerName = name ?? "",
                });
            }
            return result;
        }

        public static void EndCall(int sessionId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "UPDATE call_sessions SET status='ended', ended_at=NOW() " +
                    "WHERE id=@id AND status IN ('ringing','active')", conn);
                cmd.Parameters.AddWithValue("@id", sessionId);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        public static void RejectCall(int sessionId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "UPDATE call_sessions SET status='rejected', ended_at=NOW() WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", sessionId);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }
}
