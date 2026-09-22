using System;
using System.Collections.Generic;
using System.Data;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Групповые чаты: group_chats / group_members / group_messages.
    /// SQL перенесён из MainForm Windows-версии.
    ///
    /// Прочтения у групп нет и не было: в group_messages нет колонки is_read,
    /// и ПК тоже не пытается её выдумывать. «Новое» определяется по
    /// максимальному id, который клиент запомнил у себя, — см. NewMarks.
    /// </summary>
    public static class GroupService
    {
        public sealed class GroupItem
        {
            public int Id;
            public string Name = "";
            public string LastMessage = "";
            public int MemberCount;
            public int MaxMessageId;
        }

        /// <summary>Группы, в которых я состою, — свежие сверху.</summary>
        public static List<GroupItem> GetGroups(int myId)
        {
            var list = new List<GroupItem>();
            using var conn = DBHelper.OpenConnection();
            // avatar_color намеренно не читаем: цвет выводим из id, как у
            // личных аватарок, и не зависим от того, выполнена ли миграция,
            // которая эту колонку добавляет.
            const string sql = @"
                SELECT gc.id, gc.name,
                       (SELECT gm2.text FROM group_messages gm2
                        WHERE gm2.group_id = gc.id AND gm2.is_deleted = 0
                        ORDER BY gm2.created_at DESC LIMIT 1) AS last_msg,
                       (SELECT MAX(gm3.created_at) FROM group_messages gm3
                        WHERE gm3.group_id = gc.id) AS last_time,
                       (SELECT COUNT(*) FROM group_members gmem2
                        WHERE gmem2.group_id = gc.id) AS member_count,
                       (SELECT COALESCE(MAX(gm4.id),0) FROM group_messages gm4
                        WHERE gm4.group_id = gc.id AND gm4.sender_id <> @me
                          AND gm4.is_deleted = 0) AS max_foreign_id
                FROM group_chats gc
                JOIN group_members gmem ON gmem.group_id = gc.id AND gmem.user_id = @me
                ORDER BY last_time DESC, gc.name ASC";

            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@me", myId);
            var dt = new DataTable();
            new MySqlDataAdapter(cmd).Fill(dt);

            foreach (DataRow row in dt.Rows)
            {
                list.Add(new GroupItem
                {
                    Id = Convert.ToInt32(row["id"]),
                    Name = row["name"]?.ToString() ?? "группа",
                    LastMessage = row["last_msg"] == DBNull.Value
                        ? "" : Crypto.Dec(row["last_msg"].ToString()),
                    MemberCount = row["member_count"] == DBNull.Value
                        ? 0 : Convert.ToInt32(row["member_count"]),
                    MaxMessageId = row["max_foreign_id"] == DBNull.Value
                        ? 0 : Convert.ToInt32(row["max_foreign_id"]),
                });
            }
            return list;
        }

        /// <summary>Переписка группы — те же поля, что и у личной.</summary>
        public static List<ChatMessage> GetMessages(int groupId)
        {
            var list = new List<ChatMessage>();
            using var conn = DBHelper.OpenConnection();
            // Как и в личной переписке: тяжёлые вложения не выбираем, только
            // их размер.
            const string sql = @"
                SELECT m.id, m.sender_id, m.text, m.image_data, m.created_at,
                       m.reply_to_id, m.is_deleted, m.edited_at,
                       m.file_name, LENGTH(m.file_data) AS file_size,
                       LENGTH(m.audio_data) AS audio_size,
                       LENGTH(m.video_data) AS video_size,
                       u.Name, u.Surname, u.login,
                       r.text AS reply_text,
                       ru.Name AS reply_name, ru.Surname AS reply_surname, ru.login AS reply_login
                FROM group_messages m
                LEFT JOIN users u ON u.id = m.sender_id
                LEFT JOIN group_messages r ON r.id = m.reply_to_id
                LEFT JOIN users ru ON ru.id = r.sender_id
                WHERE m.group_id = @g
                ORDER BY m.created_at ASC, m.id ASC";

            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@g", groupId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var msg = new ChatMessage
                {
                    Id = reader.GetInt32("id"),
                    SenderId = reader.GetInt32("sender_id"),
                    ReceiverId = 0,
                    Text = reader["text"] == DBNull.Value ? "" : Crypto.Dec(reader["text"].ToString()),
                    CreatedAt = reader.GetDateTime("created_at"),
                    SenderName = MessageService.BuildName(
                        reader["Name"], reader["Surname"], reader["login"]),
                    ReplyToId = reader["reply_to_id"] == DBNull.Value
                        ? 0 : Convert.ToInt32(reader["reply_to_id"]),
                    IsDeleted = reader["is_deleted"] != DBNull.Value
                                && Convert.ToInt32(reader["is_deleted"]) != 0,
                    EditedAt = reader["edited_at"] == DBNull.Value
                        ? (DateTime?)null : Convert.ToDateTime(reader["edited_at"]),
                    FileName = reader["file_name"] == DBNull.Value ? "" : reader["file_name"].ToString(),
                    FileSize = reader["file_size"] == DBNull.Value
                        ? 0 : Convert.ToInt64(reader["file_size"]),
                    HasAudio = reader["audio_size"] != DBNull.Value
                               && Convert.ToInt64(reader["audio_size"]) > 0,
                    HasVideo = reader["video_size"] != DBNull.Value
                               && Convert.ToInt64(reader["video_size"]) > 0,
                    // Прочтений у групп нет — галочку не рисуем вовсе.
                    IsRead = true,
                };

                if (msg.ReplyToId > 0 && reader["reply_text"] != DBNull.Value)
                {
                    msg.ReplyToText = Crypto.Dec(reader["reply_text"].ToString());
                    msg.ReplyToSender = MessageService.BuildName(
                        reader["reply_name"], reader["reply_surname"], reader["reply_login"]);
                }

                if (msg.IsDeleted) { msg.Text = ""; }
                else
                {
                    int imgOrd = reader.GetOrdinal("image_data");
                    if (!reader.IsDBNull(imgOrd)) msg.ImageData = (byte[])reader["image_data"];
                }
                list.Add(msg);
            }
            return list;
        }

        // ── Отправка ────────────────────────────────────────────────────

        public static int Send(int groupId, int myId, string text, byte[] imageData, int replyToId = 0)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "INSERT INTO group_messages (group_id, sender_id, text, image_data, reply_to_id) " +
                "VALUES (@g, @s, @t, @img, @rep)", conn);
            cmd.Parameters.AddWithValue("@g", groupId);
            cmd.Parameters.AddWithValue("@s", myId);
            cmd.Parameters.AddWithValue("@t", Crypto.Enc(text ?? ""));
            if (imageData != null && imageData.Length > 0)
                cmd.Parameters.Add("@img", MySqlDbType.LongBlob).Value = imageData;
            else
                cmd.Parameters.AddWithValue("@img", DBNull.Value);
            cmd.Parameters.AddWithValue("@rep", replyToId > 0 ? (object)replyToId : DBNull.Value);
            cmd.ExecuteNonQuery();
            return (int)cmd.LastInsertedId;
        }

        public static int SendFile(int groupId, int myId, string caption, byte[] data, string fileName)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "INSERT INTO group_messages (group_id, sender_id, text, file_data, file_name) " +
                "VALUES (@g, @s, @t, @d, @n)", conn);
            cmd.Parameters.AddWithValue("@g", groupId);
            cmd.Parameters.AddWithValue("@s", myId);
            cmd.Parameters.AddWithValue("@t", Crypto.Enc(caption ?? ""));
            cmd.Parameters.Add("@d", MySqlDbType.LongBlob).Value = data ?? Array.Empty<byte>();
            cmd.Parameters.AddWithValue("@n", fileName ?? "file");
            cmd.ExecuteNonQuery();
            return (int)cmd.LastInsertedId;
        }

        public static bool Edit(int myId, int messageId, string newText)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "UPDATE group_messages SET text=@t, edited_at=NOW() " +
                "WHERE id=@id AND sender_id=@me AND is_deleted=0", conn);
            cmd.Parameters.AddWithValue("@t", Crypto.Enc(newText ?? ""));
            cmd.Parameters.AddWithValue("@id", messageId);
            cmd.Parameters.AddWithValue("@me", myId);
            return cmd.ExecuteNonQuery() > 0;
        }

        public static bool Delete(int myId, int messageId)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "UPDATE group_messages SET is_deleted=1, text='', image_data=NULL, " +
                "file_data=NULL, audio_data=NULL, video_data=NULL " +
                "WHERE id=@id AND sender_id=@me", conn);
            cmd.Parameters.AddWithValue("@id", messageId);
            cmd.Parameters.AddWithValue("@me", myId);
            return cmd.ExecuteNonQuery() > 0;
        }

        public static byte[] LoadFile(int messageId)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "SELECT file_data FROM group_messages WHERE id=@id", conn);
            cmd.Parameters.AddWithValue("@id", messageId);
            var o = cmd.ExecuteScalar();
            return o == null || o == DBNull.Value ? null : (byte[])o;
        }

        // ── Состав ──────────────────────────────────────────────────────

        /// <summary>Создаёт группу и сразу заводит в неё создателя админом.</summary>
        public static int Create(string name, int createdBy, IEnumerable<int> members)
        {
            using var conn = DBHelper.OpenConnection();
            int groupId;
            using (var cmd = new MySqlCommand(
                "INSERT INTO group_chats (name, created_by) VALUES (@n, @by)", conn))
            {
                cmd.Parameters.AddWithValue("@n", name);
                cmd.Parameters.AddWithValue("@by", createdBy);
                cmd.ExecuteNonQuery();
                groupId = (int)cmd.LastInsertedId;
            }

            // Создателя добавляем ОБЯЗАТЕЛЬНО и админом: группа, в которой
            // нет её создателя, не видна даже ему самому — список строится по
            // group_members.
            AddMember(conn, groupId, createdBy, admin: true);
            if (members != null)
                foreach (int uid in members)
                    if (uid != createdBy) AddMember(conn, groupId, uid, admin: false);

            return groupId;
        }

        private static void AddMember(MySqlConnection conn, int groupId, int userId, bool admin)
        {
            using var cmd = new MySqlCommand(
                "INSERT IGNORE INTO group_members (group_id, user_id, is_admin) " +
                "VALUES (@g, @u, @a)", conn);
            cmd.Parameters.AddWithValue("@g", groupId);
            cmd.Parameters.AddWithValue("@u", userId);
            cmd.Parameters.AddWithValue("@a", admin ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        public static void AddMember(int groupId, int userId)
        {
            using var conn = DBHelper.OpenConnection();
            AddMember(conn, groupId, userId, admin: false);
        }

        public static void RemoveMember(int groupId, int userId)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "DELETE FROM group_members WHERE group_id=@g AND user_id=@u", conn);
            cmd.Parameters.AddWithValue("@g", groupId);
            cmd.Parameters.AddWithValue("@u", userId);
            cmd.ExecuteNonQuery();
        }

        public sealed class Member
        {
            public int Id;
            public string Name = "";
            public bool IsAdmin;
        }

        public static List<Member> GetMembers(int groupId)
        {
            var list = new List<Member>();
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "SELECT u.id, u.Name, u.Surname, u.login, gm.is_admin " +
                "FROM group_members gm JOIN users u ON u.id = gm.user_id " +
                "WHERE gm.group_id=@g ORDER BY gm.is_admin DESC, u.Name", conn);
            cmd.Parameters.AddWithValue("@g", groupId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new Member
                {
                    Id = Convert.ToInt32(r["id"]),
                    Name = MessageService.BuildName(r["Name"], r["Surname"], r["login"]),
                    IsAdmin = r["is_admin"] != DBNull.Value && Convert.ToInt32(r["is_admin"]) != 0,
                });
            return list;
        }

        /// <summary>Кому слать событие о новом сообщении — всем, кроме себя.</summary>
        public static List<int> MemberIds(int groupId, int exceptId)
        {
            var list = new List<int>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT user_id FROM group_members WHERE group_id=@g", conn);
                cmd.Parameters.AddWithValue("@g", groupId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int uid = Convert.ToInt32(r[0]);
                    if (uid != exceptId) list.Add(uid);
                }
            }
            catch { }
            return list;
        }
    }
}
