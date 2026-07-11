using System;
using System.Collections.Generic;
using System.Data;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Доступ к личным сообщениям и списку диалогов. SQL перенесён из MainForm
    /// (LoadConversations / LoadMessages / SendMessage / MarkAsRead / RefreshUnread)
    /// и работает с базовой схемой bdauth (pismo_messenger_migration.sql):
    /// таблицы users(id, Name, Surname, login, role) и
    /// messages(id, sender_id, receiver_id, text, image_data, is_read, created_at).
    ///
    /// Расширенные поля v2 (audio/video/file/reply/edit/delete/blocking и группы)
    /// подключаются на следующем этапе — см. docs/ROADMAP.md.
    /// </summary>
    public static class MessageService
    {
        /// <summary>Список диалогов для сайдбара (аналог LoadConversations).</summary>
        public static List<ConversationItem> GetConversations(int myId)
        {
            var list = new List<ConversationItem>();
            using var conn = DBHelper.OpenConnection();
            const string sql = @"
                SELECT u.id, u.Name, u.Surname, u.login,
                       MAX(m.created_at) AS last_time,
                       (SELECT m2.text FROM messages m2
                        WHERE (m2.sender_id = @me AND m2.receiver_id = u.id)
                           OR (m2.sender_id = u.id AND m2.receiver_id = @me)
                        ORDER BY m2.created_at DESC LIMIT 1) AS last_msg,
                       SUM(CASE WHEN m.sender_id=u.id
                                 AND m.receiver_id=@me
                                 AND m.is_read=0 THEN 1 ELSE 0 END) AS unread
                FROM users u
                LEFT JOIN messages m
                       ON (m.sender_id=@me AND m.receiver_id=u.id)
                       OR (m.sender_id=u.id AND m.receiver_id=@me)
                WHERE u.id <> @me
                GROUP BY u.id, u.Name, u.Surname, u.login
                ORDER BY last_time DESC, u.Name ASC";

            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@me", myId);
            var dt = new DataTable();
            new MySqlDataAdapter(cmd).Fill(dt);

            foreach (DataRow row in dt.Rows)
            {
                list.Add(new ConversationItem
                {
                    PartnerId = Convert.ToInt32(row["id"]),
                    Name = BuildName(row["Name"], row["Surname"], row["login"]),
                    Login = row["login"].ToString(),
                    LastMessage = row["last_msg"] == DBNull.Value ? "" : row["last_msg"].ToString(),
                    Unread = row["unread"] == DBNull.Value ? 0 : Convert.ToInt32(row["unread"]),
                });
            }
            return list;
        }

        /// <summary>Все пользователи (режим admin, аналог LoadAllUsersForAdmin).</summary>
        public static List<UserItem> GetAllUsers()
        {
            var list = new List<UserItem>();
            using var conn = DBHelper.OpenConnection();
            const string sql = "SELECT id, Name, Surname, login, role FROM users ORDER BY Name";
            using var cmd = new MySqlCommand(sql, conn);
            var dt = new DataTable();
            new MySqlDataAdapter(cmd).Fill(dt);

            foreach (DataRow row in dt.Rows)
            {
                list.Add(new UserItem
                {
                    Id = Convert.ToInt32(row["id"]),
                    Name = BuildName(row["Name"], row["Surname"], row["login"]),
                    Login = row["login"].ToString(),
                    Role = row["role"].ToString(),
                });
            }
            return list;
        }

        /// <summary>Переписка между myId и partnerId, по возрастанию времени.</summary>
        public static List<ChatMessage> GetMessages(int myId, int partnerId)
        {
            var list = new List<ChatMessage>();
            using var conn = DBHelper.OpenConnection();
            const string sql = @"
                SELECT m.id, m.sender_id, m.receiver_id, m.text, m.image_data,
                       m.is_read, m.created_at,
                       u.Name, u.Surname, u.login
                FROM messages m
                LEFT JOIN users u ON u.id = m.sender_id
                WHERE (m.sender_id=@me AND m.receiver_id=@th)
                   OR (m.sender_id=@th AND m.receiver_id=@me)
                ORDER BY m.created_at ASC, m.id ASC";

            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@me", myId);
            cmd.Parameters.AddWithValue("@th", partnerId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var msg = new ChatMessage
                {
                    Id = reader.GetInt32("id"),
                    SenderId = reader.GetInt32("sender_id"),
                    ReceiverId = reader.GetInt32("receiver_id"),
                    Text = reader["text"] == DBNull.Value ? "" : reader["text"].ToString(),
                    IsRead = !reader.IsDBNull(reader.GetOrdinal("is_read")) && reader.GetBoolean("is_read"),
                    CreatedAt = reader.GetDateTime("created_at"),
                    SenderName = BuildName(reader["Name"], reader["Surname"], reader["login"]),
                };
                int imgOrd = reader.GetOrdinal("image_data");
                if (!reader.IsDBNull(imgOrd))
                    msg.ImageData = (byte[])reader["image_data"];
                list.Add(msg);
            }
            return list;
        }

        /// <summary>Отправка личного сообщения (текст и/или изображение). Аналог SendMessage.</summary>
        public static void SendMessage(int myId, int partnerId, string text, byte[] imageData)
        {
            using var conn = DBHelper.OpenConnection();
            const string sql =
                "INSERT INTO messages (sender_id, receiver_id, text, image_data) " +
                "VALUES (@s, @r, @t, @img)";
            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@s", myId);
            cmd.Parameters.AddWithValue("@r", partnerId);
            cmd.Parameters.AddWithValue("@t", text ?? "");
            if (imageData != null && imageData.Length > 0)
                cmd.Parameters.Add("@img", MySqlDbType.LongBlob).Value = imageData;
            else
                cmd.Parameters.AddWithValue("@img", DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        /// <summary>Помечает входящие от partnerId как прочитанные. Аналог MarkAsRead.</summary>
        public static void MarkAsRead(int myId, int partnerId)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "UPDATE messages SET is_read=1 WHERE sender_id=@s AND receiver_id=@r AND is_read=0", conn);
            cmd.Parameters.AddWithValue("@s", partnerId);
            cmd.Parameters.AddWithValue("@r", myId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>Счётчик непрочитанных по каждому отправителю (аналог RefreshUnreadAndNotify).</summary>
        public static Dictionary<int, int> GetUnreadBySender(int myId)
        {
            var map = new Dictionary<int, int>();
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "SELECT sender_id, COUNT(*) AS cnt FROM messages " +
                "WHERE receiver_id=@me AND is_read=0 GROUP BY sender_id", conn);
            cmd.Parameters.AddWithValue("@me", myId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                map[reader.GetInt32("sender_id")] = reader.GetInt32("cnt");
            return map;
        }

        /// <summary>Общее число сообщений (для лёгкого поллинга, аналог GetMsgCount).</summary>
        public static int GetTotalMessageCount(int myId)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "SELECT COUNT(*) FROM messages WHERE sender_id=@me OR receiver_id=@me", conn);
            cmd.Parameters.AddWithValue("@me", myId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        /// <summary>Собирает отображаемое имя из Name+Surname с откатом на login.</summary>
        public static string BuildName(object name, object surname, object login)
        {
            string n = name == DBNull.Value ? "" : name?.ToString() ?? "";
            string s = surname == DBNull.Value ? "" : surname?.ToString() ?? "";
            string full = $"{n} {s}".Trim();
            if (!string.IsNullOrWhiteSpace(full)) return full;
            return login == DBNull.Value ? "" : login?.ToString() ?? "";
        }
    }
}
