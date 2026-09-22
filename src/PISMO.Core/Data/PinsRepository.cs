using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Закреплённые сообщения — порт PISMO/PinsRepository.cs.
    /// Таблица pinned_messages (message_id, scope): scope 0 — личная
    /// переписка, 1 — группа. Закреп общий для чата: его видят обе стороны.
    /// </summary>
    public static class PinsRepository
    {
        public sealed class PinnedItem
        {
            public int MessageId;
            public string Sender = "";
            public string Text = "";      // уже расшифрованный
        }

        /// <summary>
        /// Почему последняя операция не удалась, или null.
        ///
        /// Здесь всё завёрнуто в catch и возвращает false — закреп не то, ради
        /// чего стоит ронять окно. Но «не удалось» и «открепил» выглядели бы
        /// снаружи одинаково: нажал, ничего не произошло, и понять, в чём
        /// дело — нет прав, нет таблицы, нет связи, — нельзя ни по чему.
        /// </summary>
        public static string LastError;

        public static bool IsPinned(int messageId, int scope)
        {
            if (messageId <= 0) return false;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT 1 FROM pinned_messages WHERE message_id=@m AND scope=@s", conn);
                cmd.Parameters.AddWithValue("@m", messageId);
                cmd.Parameters.AddWithValue("@s", scope);
                return cmd.ExecuteScalar() != null;
            }
            catch { return false; }
        }

        /// <summary>Закрепить/открепить. Возвращает итоговое состояние.</summary>
        public static bool Toggle(int messageId, int scope, int byUserId)
        {
            LastError = null;
            if (messageId <= 0) return false;
            try
            {
                bool nowPinned;
                using (var conn = DBHelper.OpenConnection())
                {
                    using var chk = new MySqlCommand(
                        "SELECT 1 FROM pinned_messages WHERE message_id=@m AND scope=@s", conn);
                    chk.Parameters.AddWithValue("@m", messageId);
                    chk.Parameters.AddWithValue("@s", scope);
                    bool was = chk.ExecuteScalar() != null;

                    using var cmd = was
                        ? new MySqlCommand(
                            "DELETE FROM pinned_messages WHERE message_id=@m AND scope=@s", conn)
                        : new MySqlCommand(
                            "INSERT IGNORE INTO pinned_messages (message_id, scope, pinned_by) " +
                            "VALUES (@m, @s, @by)", conn);
                    cmd.Parameters.AddWithValue("@m", messageId);
                    cmd.Parameters.AddWithValue("@s", scope);
                    if (!was) cmd.Parameters.AddWithValue("@by", byUserId);
                    cmd.ExecuteNonQuery();
                    nowPinned = !was;
                }

                Announce(messageId);
                return nowPinned;
            }
            catch (Exception ex) { LastError = ex.Message; return false; }
        }

        /// <summary>
        /// Сказать остальным, что закрепы изменились.
        ///
        /// Широковещательно: закреп сообщения виден обеим сторонам переписки, и
        /// перечитать его должен каждый, у кого этот чат открыт. (В отличие от
        /// закреплённых ЧАТОВ — те личные, и событие о них уходит только своим
        /// же устройствам.)
        /// </summary>
        private static void Announce(int messageId)
        {
            try { SignalingClient.Instance.Send("pin", 0, messageId, ""); } catch { }
        }

        /// <summary>Закреплённые id этого чата — для пометки в переписке.</summary>
        public static HashSet<int> PinnedIds(int scope, int myId, int chatId)
        {
            var set = new HashSet<int>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(ScopedSql(
                    "SELECT p.message_id", scope), conn);
                cmd.Parameters.AddWithValue("@me", myId);
                cmd.Parameters.AddWithValue("@chat", chatId);
                cmd.Parameters.AddWithValue("@scope", scope);
                using var r = cmd.ExecuteReader();
                while (r.Read()) set.Add(Convert.ToInt32(r[0]));
            }
            catch { }
            return set;
        }

        /// <summary>Список закреплённых с текстом — для отдельного окна.</summary>
        public static List<PinnedItem> List(int scope, int myId, int chatId)
        {
            var list = new List<PinnedItem>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(ScopedSql(
                    "SELECT p.message_id, t.text, u.Name, u.Surname, u.login", scope,
                    joinSender: true), conn);
                cmd.Parameters.AddWithValue("@me", myId);
                cmd.Parameters.AddWithValue("@chat", chatId);
                cmd.Parameters.AddWithValue("@scope", scope);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new PinnedItem
                    {
                        MessageId = Convert.ToInt32(r[0]),
                        Text = r[1] == DBNull.Value ? "" : Crypto.Dec(r[1].ToString()),
                        Sender = MessageService.BuildName(r[2], r[3], r[4]),
                    });
            }
            catch { }
            return list;
        }

        /// <summary>
        /// Отпечаток закрепов ОТКРЫТОГО чата — чтобы опрос замечал правку, до
        /// которой событие не дошло (клиент мог быть не на связи).
        ///
        /// Считается по одному чату, а не по всей таблице: общий отпечаток
        /// менялся от любого закрепа любого человека в любой переписке, и
        /// каждый, у кого открыт хоть какой-то чат, получал перезагрузку — из-за
        /// события, которое его не касается.
        ///
        /// CAST обязателен: SUM() от целой колонки MySQL возвращает DECIMAL, и
        /// читать его как целое — исключение.
        /// </summary>
        public static string Fingerprint(int scope, int myId, int chatId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(ScopedSql(
                    "SELECT COUNT(*), CAST(COALESCE(SUM(p.message_id),0) AS SIGNED)", scope), conn);
                cmd.Parameters.AddWithValue("@me", myId);
                cmd.Parameters.AddWithValue("@chat", chatId);
                cmd.Parameters.AddWithValue("@scope", scope);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return "";
                return Convert.ToInt64(r.GetValue(0)) + ":" + Convert.ToInt64(r.GetValue(1));
            }
            catch { return ""; }
        }

        /// <summary>
        /// Общая часть запросов: чем чат опознаётся в своей таблице. У группы
        /// это одна колонка, у переписки — пара отправитель/получатель в обе
        /// стороны.
        /// </summary>
        private static string ScopedSql(string head, int scope, bool joinSender = false)
        {
            string table = scope == 1 ? "group_messages" : "messages";
            string sender = joinSender ? " LEFT JOIN users u ON u.id = t.sender_id" : "";
            string where = scope == 1
                ? "t.group_id=@chat"
                : "((t.sender_id=@me AND t.receiver_id=@chat) OR (t.sender_id=@chat AND t.receiver_id=@me))";
            return $"{head} FROM pinned_messages p JOIN {table} t ON t.id = p.message_id{sender} " +
                   $"WHERE p.scope=@scope AND {where}";
        }
    }
}
