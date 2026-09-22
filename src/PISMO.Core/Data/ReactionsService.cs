using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Реакции-эмодзи на сообщения — порт ReactionsRepository.
    /// Таблица message_reactions, scope: 0 личное, 1 групповое, 2 серверное —
    /// чтобы id из разных таблиц не путались между собой.
    /// </summary>
    public static class ReactionsService
    {
        public enum Scope { Direct = 0, Group = 1, Server = 2 }

        public sealed class Reaction
        {
            public string Emoji = "";
            public int Count;
            public bool Mine;
        }

        /// <summary>Набор, который предлагаем в меню. Тот же, что на ПК.</summary>
        public static readonly string[] Common = { "👍", "❤️", "😂", "😮", "😢", "🔥", "🎉", "👎" };

        /// <summary>
        /// Сравнение эмодзи ПОБАЙТОВО.
        ///
        /// Это не перестраховка. В обычных _ci-коллациях MySQL разные эмодзи
        /// считаются равными строками, и тумблер снимал бы чужую реакцию при
        /// попытке поставить свою. Явный COLLATE работает даже если миграция
        /// коллации на этой базе ещё не применялась.
        /// </summary>
        private const string EmojiEq =
            " AND emoji = CONVERT(@e USING utf8mb4) COLLATE utf8mb4_bin";

        /// <summary>Поставить или снять. true — после операции реакция стоит.</summary>
        public static bool Toggle(int messageId, Scope scope, int userId, string emoji)
        {
            if (messageId <= 0 || string.IsNullOrWhiteSpace(emoji)) return false;
            try
            {
                using var conn = DBHelper.OpenConnection();
                bool exists;
                using (var chk = new MySqlCommand(
                    "SELECT 1 FROM message_reactions " +
                    "WHERE message_id=@m AND scope=@s AND user_id=@u" + EmojiEq, conn))
                {
                    chk.Parameters.AddWithValue("@m", messageId);
                    chk.Parameters.AddWithValue("@s", (int)scope);
                    chk.Parameters.AddWithValue("@u", userId);
                    chk.Parameters.AddWithValue("@e", emoji);
                    exists = chk.ExecuteScalar() != null;
                }

                using (var cmd = exists
                    ? new MySqlCommand(
                        "DELETE FROM message_reactions " +
                        "WHERE message_id=@m AND scope=@s AND user_id=@u" + EmojiEq, conn)
                    : new MySqlCommand(
                        "INSERT IGNORE INTO message_reactions (message_id, scope, user_id, emoji) " +
                        "VALUES (@m, @s, @u, @e)", conn))
                {
                    cmd.Parameters.AddWithValue("@m", messageId);
                    cmd.Parameters.AddWithValue("@s", (int)scope);
                    cmd.Parameters.AddWithValue("@u", userId);
                    cmd.Parameters.AddWithValue("@e", emoji);
                    cmd.ExecuteNonQuery();
                }

                try { SignalingClient.Instance.Send("reaction", 0, messageId, ""); } catch { }
                return !exists;
            }
            catch { return false; }
        }

        /// <summary>
        /// Реакции сразу ко всей странице переписки: messageId -> список.
        /// Одним запросом, а не по одному на сообщение — иначе открытие чата
        /// это сотня обращений к серверу.
        /// </summary>
        public static Dictionary<int, List<Reaction>> ForMessages(
            IReadOnlyCollection<int> ids, Scope scope, int myId)
        {
            var map = new Dictionary<int, List<Reaction>>();
            if (ids == null || ids.Count == 0) return map;
            try
            {
                string list = string.Join(",", ids);
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT message_id, emoji, COUNT(*) AS cnt, " +
                    "       SUM(user_id = @me) AS mine " +
                    "FROM message_reactions " +
                    $"WHERE scope=@s AND message_id IN ({list}) " +
                    "GROUP BY message_id, emoji ORDER BY cnt DESC", conn);
                cmd.Parameters.AddWithValue("@s", (int)scope);
                cmd.Parameters.AddWithValue("@me", myId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int mid = Convert.ToInt32(r["message_id"]);
                    if (!map.TryGetValue(mid, out var l)) map[mid] = l = new List<Reaction>();
                    l.Add(new Reaction
                    {
                        Emoji = r["emoji"]?.ToString() ?? "",
                        Count = Convert.ToInt32(r["cnt"]),
                        // SUM() возвращает DECIMAL — читать его как целое
                        // нельзя, это уже ломало сверку закрепов.
                        Mine = Convert.ToInt64(r["mine"]) > 0,
                    });
                }
            }
            catch { }
            return map;
        }
    }
}
