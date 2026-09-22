using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Друзья и приватность личных сообщений — порт FriendsRepository.
    ///
    /// Таблица friends (user_id — кто позвал, friend_id — кого, status:
    /// 0 заявка, 1 приняты). Колонки status может не быть, если миграция на
    /// этой базе не выполнялась: тогда сам факт строки считается дружбой, а
    /// запросы строятся без неё — падать с «Unknown column» из-за не
    /// применённой миграции клиент не должен.
    /// </summary>
    public static class FriendsService
    {
        public enum Relation { None, Friend, OutgoingPending, IncomingPending }

        public sealed class UserHit
        {
            public int Id;
            public string Name = "";
            public string Login = "";
            public Relation Rel;
        }

        private static bool _probed;
        private static bool _hasStatus;

        /// <summary>Один раз выясняет, какие колонки на этой базе есть.</summary>
        private static void Probe()
        {
            if (_probed) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT COUNT(*) FROM information_schema.COLUMNS " +
                    "WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='friends' AND COLUMN_NAME='status'",
                    conn);
                _hasStatus = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                _probed = true;   // ставим только при удаче: нет связи — спросим позже
            }
            catch { }
        }

        /// <summary>Предикат «принятая дружба» для алиаса таблицы.</summary>
        private static string Accepted(string alias) => _hasStatus ? $"{alias}.status=1" : "(1=1)";

        public static bool IsFriend(int a, int b)
        {
            Probe();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT 1 FROM friends f WHERE " + Accepted("f") +
                    " AND ((f.user_id=@a AND f.friend_id=@b) OR (f.user_id=@b AND f.friend_id=@a)) LIMIT 1",
                    conn);
                cmd.Parameters.AddWithValue("@a", a);
                cmd.Parameters.AddWithValue("@b", b);
                return cmd.ExecuteScalar() != null;
            }
            catch { return false; }
        }

        public static Relation GetRelation(int me, int them)
        {
            Probe();
            if (me == them) return Relation.None;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT user_id, friend_id" + (_hasStatus ? ", status" : "") +
                    " FROM friends WHERE (user_id=@me AND friend_id=@th) OR (user_id=@th AND friend_id=@me)",
                    conn);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.Parameters.AddWithValue("@th", them);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return Relation.None;

                bool accepted = !_hasStatus || Convert.ToInt32(r["status"]) == 1;
                if (accepted) return Relation.Friend;
                return Convert.ToInt32(r["user_id"]) == me
                    ? Relation.OutgoingPending : Relation.IncomingPending;
            }
            catch { return Relation.None; }
        }

        /// <summary>Отношения сразу ко многим — для списков, одним запросом.</summary>
        public static Dictionary<int, Relation> RelationsFor(int me, IReadOnlyCollection<int> ids)
        {
            var map = new Dictionary<int, Relation>();
            if (ids == null || ids.Count == 0) return map;
            Probe();
            try
            {
                string list = string.Join(",", ids);
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT user_id, friend_id" + (_hasStatus ? ", status" : "") +
                    $" FROM friends WHERE (user_id=@me AND friend_id IN ({list})) " +
                    $"   OR (friend_id=@me AND user_id IN ({list}))", conn);
                cmd.Parameters.AddWithValue("@me", me);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int u = Convert.ToInt32(r["user_id"]);
                    int f = Convert.ToInt32(r["friend_id"]);
                    int other = u == me ? f : u;
                    bool accepted = !_hasStatus || Convert.ToInt32(r["status"]) == 1;
                    map[other] = accepted
                        ? Relation.Friend
                        : (u == me ? Relation.OutgoingPending : Relation.IncomingPending);
                }
            }
            catch { }
            return map;
        }

        /// <summary>Позвать в друзья. false — уже есть связь или не вышло.</summary>
        public static bool SendRequest(int me, int them)
        {
            if (me == them) return false;
            Probe();
            if (GetRelation(me, them) != Relation.None) return false;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    _hasStatus
                        ? "INSERT IGNORE INTO friends (user_id, friend_id, status) VALUES (@me, @th, 0)"
                        : "INSERT IGNORE INTO friends (user_id, friend_id) VALUES (@me, @th)", conn);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.Parameters.AddWithValue("@th", them);
                cmd.ExecuteNonQuery();
                // Пусть узнает сразу, а не со следующей сверкой.
                try { SignalingClient.Instance.Send("friend", them, me, ""); } catch { }
                return true;
            }
            catch { return false; }
        }

        public static void Accept(int me, int requester)
        {
            Probe();
            try
            {
                using var conn = DBHelper.OpenConnection();
                if (_hasStatus)
                {
                    using var cmd = new MySqlCommand(
                        "UPDATE friends SET status=1 WHERE user_id=@rq AND friend_id=@me", conn);
                    cmd.Parameters.AddWithValue("@rq", requester);
                    cmd.Parameters.AddWithValue("@me", me);
                    cmd.ExecuteNonQuery();
                }
                try { SignalingClient.Instance.Send("friend", requester, me, ""); } catch { }
            }
            catch { }
        }

        public static void Decline(int me, int requester) => Remove(me, requester);

        public static void Remove(int me, int them)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "DELETE FROM friends WHERE (user_id=@me AND friend_id=@th) " +
                    "OR (user_id=@th AND friend_id=@me)", conn);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.Parameters.AddWithValue("@th", them);
                cmd.ExecuteNonQuery();
                try { SignalingClient.Instance.Send("friend", them, me, ""); } catch { }
            }
            catch { }
        }

        public static List<UserHit> Friends(int me) => Query(me, Relation.Friend);
        public static List<UserHit> Incoming(int me) => Query(me, Relation.IncomingPending);
        public static List<UserHit> Outgoing(int me) => Query(me, Relation.OutgoingPending);

        public static int CountIncoming(int me)
        {
            Probe();
            if (!_hasStatus) return 0;   // без status заявок не бывает
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT COUNT(*) FROM friends WHERE friend_id=@me AND status=0", conn);
                cmd.Parameters.AddWithValue("@me", me);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch { return 0; }
        }

        private static List<UserHit> Query(int me, Relation want)
        {
            var list = new List<UserHit>();
            Probe();
            try
            {
                string where = want switch
                {
                    Relation.Friend =>
                        "(" + Accepted("f") + ") AND (f.user_id=@me OR f.friend_id=@me)",
                    Relation.IncomingPending =>
                        _hasStatus ? "f.status=0 AND f.friend_id=@me" : "1=0",
                    Relation.OutgoingPending =>
                        _hasStatus ? "f.status=0 AND f.user_id=@me" : "1=0",
                    _ => "1=0",
                };

                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT u.id, u.Name, u.Surname, u.login FROM friends f " +
                    "JOIN users u ON u.id = IF(f.user_id=@me, f.friend_id, f.user_id) " +
                    $"WHERE {where} ORDER BY u.Name", conn);
                cmd.Parameters.AddWithValue("@me", me);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new UserHit
                    {
                        Id = Convert.ToInt32(r["id"]),
                        Name = MessageService.BuildName(r["Name"], r["Surname"], r["login"]),
                        Login = r["login"]?.ToString() ?? "",
                        Rel = want,
                    });
            }
            catch { }
            return list;
        }

        /// <summary>Поиск людей по имени или логину, с отношением к каждому.</summary>
        public static List<UserHit> Search(int me, string query)
        {
            var list = new List<UserHit>();
            if (string.IsNullOrWhiteSpace(query)) return list;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT id, Name, Surname, login FROM users " +
                    "WHERE id <> @me AND (login LIKE @q OR Name LIKE @q OR Surname LIKE @q) " +
                    "ORDER BY Name LIMIT 50", conn);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.Parameters.AddWithValue("@q", "%" + query.Trim() + "%");
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new UserHit
                    {
                        Id = Convert.ToInt32(r["id"]),
                        Name = MessageService.BuildName(r["Name"], r["Surname"], r["login"]),
                        Login = r["login"]?.ToString() ?? "",
                    });
            }
            catch { return list; }

            // Отношения — одним запросом на весь список, а не по одному на
            // строку: иначе поиск на пятьдесят человек это пятьдесят
            // обращений к серверу.
            var ids = new List<int>();
            foreach (var h in list) ids.Add(h.Id);
            var rel = RelationsFor(me, ids);
            foreach (var h in list)
                if (rel.TryGetValue(h.Id, out var r2)) h.Rel = r2;
            return list;
        }

        // ── Приватность личных сообщений ────────────────────────────────

        /// <summary>0 — писать могут все, 1 — только друзья.</summary>
        public static int GetDmPrivacy(int uid)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT dm_privacy FROM user_prefs WHERE user_id=@u", conn);
                cmd.Parameters.AddWithValue("@u", uid);
                var o = cmd.ExecuteScalar();
                return o == null || o == DBNull.Value ? 0 : Convert.ToInt32(o);
            }
            catch { return 0; }
        }

        public static void SetDmPrivacy(int uid, int mode)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "INSERT INTO user_prefs (user_id, dm_privacy) VALUES (@u, @m) " +
                    "ON DUPLICATE KEY UPDATE dm_privacy=@m", conn);
                cmd.Parameters.AddWithValue("@u", uid);
                cmd.Parameters.AddWithValue("@m", mode);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        /// <summary>
        /// Можно ли писать этому человеку. Админ пишет всегда — иначе он не
        /// сможет ответить на обращение.
        /// </summary>
        public static bool CanMessage(int me, int them, bool isAdmin = false)
        {
            if (me == them || isAdmin) return true;
            if (GetDmPrivacy(them) == 0) return true;
            return IsFriend(me, them);
        }
    }
}
