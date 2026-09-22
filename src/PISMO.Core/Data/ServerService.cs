using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Серверы как в Discord: servers / server_channels / server_members /
    /// server_roles / server_messages / voice_presence.
    /// SQL перенесён из ServersForm Windows-версии.
    ///
    /// Права здесь намеренно проверяются в САМИХ запросах, а не только в
    /// интерфейсе. Спрятанная кнопка — это удобство, а не защита: у каждого
    /// клиента прямой доступ к базе, и «нельзя» должно стоять там же, где
    /// происходит действие.
    /// </summary>
    public static class ServerService
    {
        public sealed class ServerItem
        {
            public int Id;
            public string Name = "";
            public int OwnerId;
            public bool IsOwner;
            public int MemberCount;
        }

        public sealed class ChannelItem
        {
            public int Id;
            public string Name = "";
            public string Type = "text";     // text | voice
            public int Position;
            public bool IsVoice => string.Equals(Type, "voice", StringComparison.OrdinalIgnoreCase);
        }

        public sealed class MemberItem
        {
            public int Id;
            public string Name = "";
            public string RoleName = "";
            public bool CanManage;
            public bool CanKick;
            public bool CanBan;
        }

        public sealed class VoiceUser
        {
            public int UserId;
            public string Name = "";
            public bool Streaming;
            public bool MicMuted;
            public bool Deafened;
        }

        /// <summary>Сколько секунд запись в voice_presence считается живой.</summary>
        private const int VoiceFreshSec = 20;

        // ── Серверы ─────────────────────────────────────────────────────

        public static List<ServerItem> MyServers(int myId)
        {
            var list = new List<ServerItem>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT s.id, s.name, s.owner_id, " +
                    "  (SELECT COUNT(*) FROM server_members m2 WHERE m2.server_id = s.id) AS cnt " +
                    "FROM servers s " +
                    "JOIN server_members m ON m.server_id = s.id AND m.user_id = @me " +
                    "ORDER BY s.name", conn);
                cmd.Parameters.AddWithValue("@me", myId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int owner = Convert.ToInt32(r["owner_id"]);
                    list.Add(new ServerItem
                    {
                        Id = Convert.ToInt32(r["id"]),
                        Name = r["name"]?.ToString() ?? "сервер",
                        OwnerId = owner,
                        IsOwner = owner == myId,
                        MemberCount = r["cnt"] == DBNull.Value ? 0 : Convert.ToInt32(r["cnt"]),
                    });
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// Создаёт сервер, заводит владельца участником и делает один
        /// текстовый и один голосовой канал.
        ///
        /// Пустой сервер — это тупик: зайти в него можно, а написать некуда,
        /// и человек первым делом идёт искать, чем его наполнить.
        /// </summary>
        public static int Create(string name, int ownerId)
        {
            using var conn = DBHelper.OpenConnection();
            int serverId;
            using (var cmd = new MySqlCommand(
                "INSERT INTO servers (name, owner_id) VALUES (@n, @o)", conn))
            {
                cmd.Parameters.AddWithValue("@n", name);
                cmd.Parameters.AddWithValue("@o", ownerId);
                cmd.ExecuteNonQuery();
                serverId = (int)cmd.LastInsertedId;
            }

            using (var cmd = new MySqlCommand(
                "INSERT IGNORE INTO server_members (server_id, user_id) VALUES (@s, @u)", conn))
            {
                cmd.Parameters.AddWithValue("@s", serverId);
                cmd.Parameters.AddWithValue("@u", ownerId);
                cmd.ExecuteNonQuery();
            }

            AddChannel(conn, serverId, "общий", "text", 0);
            AddChannel(conn, serverId, "голосовой", "voice", 1);
            return serverId;
        }

        /// <summary>
        /// Вступить в сервер по номеру. false — сервера нет или вход закрыт
        /// баном. Бан проверяется здесь же: убрать проверку из интерфейса
        /// значило бы, что забаненный входит обратно первым же запросом.
        /// </summary>
        public static bool Join(int serverId, int userId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using (var chk = new MySqlCommand("SELECT 1 FROM servers WHERE id=@s", conn))
                {
                    chk.Parameters.AddWithValue("@s", serverId);
                    if (chk.ExecuteScalar() == null) return false;
                }
                using (var ban = new MySqlCommand(
                    "SELECT 1 FROM server_bans WHERE server_id=@s AND user_id=@u", conn))
                {
                    ban.Parameters.AddWithValue("@s", serverId);
                    ban.Parameters.AddWithValue("@u", userId);
                    if (ban.ExecuteScalar() != null) return false;
                }
                using var cmd = new MySqlCommand(
                    "INSERT IGNORE INTO server_members (server_id, user_id) VALUES (@s, @u)", conn);
                cmd.Parameters.AddWithValue("@s", serverId);
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.ExecuteNonQuery();
                return true;
            }
            catch { return false; }
        }

        public static void Leave(int serverId, int userId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                // Владельца не выпускаем: сервер без владельца остаётся без
                // того, кто может им управлять, и чинить это некому.
                using var cmd = new MySqlCommand(
                    "DELETE FROM server_members WHERE server_id=@s AND user_id=@u " +
                    "AND @u <> (SELECT owner_id FROM servers WHERE id=@s)", conn);
                cmd.Parameters.AddWithValue("@s", serverId);
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        // ── Каналы ──────────────────────────────────────────────────────

        public static List<ChannelItem> Channels(int serverId)
        {
            var list = new List<ChannelItem>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT id, name, type, position FROM server_channels " +
                    "WHERE server_id=@s ORDER BY type DESC, position, id", conn);
                cmd.Parameters.AddWithValue("@s", serverId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new ChannelItem
                    {
                        Id = Convert.ToInt32(r["id"]),
                        Name = r["name"]?.ToString() ?? "канал",
                        Type = r["type"]?.ToString() ?? "text",
                        Position = r["position"] == DBNull.Value ? 0 : Convert.ToInt32(r["position"]),
                    });
            }
            catch { }
            return list;
        }

        private static void AddChannel(MySqlConnection conn, int serverId, string name, string type, int pos)
        {
            using var cmd = new MySqlCommand(
                "INSERT INTO server_channels (server_id, name, type, position) " +
                "VALUES (@s, @n, @t, @p)", conn);
            cmd.Parameters.AddWithValue("@s", serverId);
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@t", type);
            cmd.Parameters.AddWithValue("@p", pos);
            cmd.ExecuteNonQuery();
        }

        /// <summary>Создаёт канал, если у человека есть на это право.</summary>
        public static bool CreateChannel(int serverId, int byUserId, string name, bool voice)
        {
            try
            {
                if (!CanManage(serverId, byUserId)) return false;
                using var conn = DBHelper.OpenConnection();
                AddChannel(conn, serverId, name, voice ? "voice" : "text", 100);
                return true;
            }
            catch { return false; }
        }

        public static bool DeleteChannel(int serverId, int byUserId, int channelId)
        {
            try
            {
                if (!CanManage(serverId, byUserId)) return false;
                using var conn = DBHelper.OpenConnection();
                using (var m = new MySqlCommand(
                    "DELETE FROM server_messages WHERE channel_id=@c", conn))
                {
                    m.Parameters.AddWithValue("@c", channelId);
                    m.ExecuteNonQuery();
                }
                using var cmd = new MySqlCommand(
                    "DELETE FROM server_channels WHERE id=@c AND server_id=@s", conn);
                cmd.Parameters.AddWithValue("@c", channelId);
                cmd.Parameters.AddWithValue("@s", serverId);
                return cmd.ExecuteNonQuery() > 0;
            }
            catch { return false; }
        }

        // ── Права ───────────────────────────────────────────────────────

        /// <summary>
        /// Может ли человек управлять сервером: владелец либо роль с
        /// can_manage. Спрашивается перед каждым таким действием.
        /// </summary>
        public static bool CanManage(int serverId, int userId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT 1 FROM servers s " +
                    "LEFT JOIN server_members m ON m.server_id = s.id AND m.user_id = @u " +
                    "LEFT JOIN server_roles r ON r.id = m.role_id " +
                    "WHERE s.id = @s AND (s.owner_id = @u OR r.can_manage = 1)", conn);
                cmd.Parameters.AddWithValue("@s", serverId);
                cmd.Parameters.AddWithValue("@u", userId);
                return cmd.ExecuteScalar() != null;
            }
            catch { return false; }
        }

        public static List<MemberItem> Members(int serverId)
        {
            var list = new List<MemberItem>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT u.id, u.Name, u.Surname, u.login, " +
                    "       r.name AS role_name, r.can_manage, r.can_kick, r.can_ban, " +
                    "       s.owner_id " +
                    "FROM server_members m " +
                    "JOIN users u ON u.id = m.user_id " +
                    "JOIN servers s ON s.id = m.server_id " +
                    "LEFT JOIN server_roles r ON r.id = m.role_id " +
                    "WHERE m.server_id=@s ORDER BY u.Name", conn);
                cmd.Parameters.AddWithValue("@s", serverId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int uid = Convert.ToInt32(r["id"]);
                    bool owner = Convert.ToInt32(r["owner_id"]) == uid;
                    list.Add(new MemberItem
                    {
                        Id = uid,
                        Name = MessageService.BuildName(r["Name"], r["Surname"], r["login"]),
                        RoleName = owner ? "владелец" : (r["role_name"]?.ToString() ?? ""),
                        CanManage = owner || Flag(r["can_manage"]),
                        CanKick = owner || Flag(r["can_kick"]),
                        CanBan = owner || Flag(r["can_ban"]),
                    });
                }
            }
            catch { }
            return list;
        }

        private static bool Flag(object v) =>
            v != null && v != DBNull.Value && Convert.ToInt32(v) != 0;

        public static bool Kick(int serverId, int byUserId, int targetId, bool ban)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                // Право спрашиваем у базы, а не у интерфейса, и владельца
                // тронуть нельзя никому.
                using (var chk = new MySqlCommand(
                    "SELECT 1 FROM servers s " +
                    "LEFT JOIN server_members m ON m.server_id = s.id AND m.user_id = @by " +
                    "LEFT JOIN server_roles r ON r.id = m.role_id " +
                    $"WHERE s.id=@s AND s.owner_id <> @t AND (s.owner_id = @by OR r.{(ban ? "can_ban" : "can_kick")} = 1)",
                    conn))
                {
                    chk.Parameters.AddWithValue("@s", serverId);
                    chk.Parameters.AddWithValue("@by", byUserId);
                    chk.Parameters.AddWithValue("@t", targetId);
                    if (chk.ExecuteScalar() == null) return false;
                }

                if (ban)
                {
                    using var b = new MySqlCommand(
                        "INSERT IGNORE INTO server_bans (server_id, user_id) VALUES (@s, @t)", conn);
                    b.Parameters.AddWithValue("@s", serverId);
                    b.Parameters.AddWithValue("@t", targetId);
                    b.ExecuteNonQuery();
                }

                using var cmd = new MySqlCommand(
                    "DELETE FROM server_members WHERE server_id=@s AND user_id=@t", conn);
                cmd.Parameters.AddWithValue("@s", serverId);
                cmd.Parameters.AddWithValue("@t", targetId);
                cmd.ExecuteNonQuery();
                return true;
            }
            catch { return false; }
        }

        // ── Сообщения канала ────────────────────────────────────────────

        public static List<ChatMessage> Messages(int channelId)
        {
            var list = new List<ChatMessage>();
            using var conn = DBHelper.OpenConnection();
            const string sql = @"
                SELECT m.id, m.sender_id, m.text, m.image_data, m.created_at,
                       m.reply_to_id, m.is_deleted, m.edited_at,
                       m.file_name, LENGTH(m.file_data) AS file_size,
                       u.Name, u.Surname, u.login,
                       r.text AS reply_text,
                       ru.Name AS reply_name, ru.Surname AS reply_surname, ru.login AS reply_login
                FROM server_messages m
                LEFT JOIN users u ON u.id = m.sender_id
                LEFT JOIN server_messages r ON r.id = m.reply_to_id
                LEFT JOIN users ru ON ru.id = r.sender_id
                WHERE m.channel_id = @c
                ORDER BY m.created_at ASC, m.id ASC";

            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@c", channelId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var msg = new ChatMessage
                {
                    Id = reader.GetInt32("id"),
                    SenderId = reader.GetInt32("sender_id"),
                    Text = reader["text"] == DBNull.Value ? "" : Crypto.Dec(reader["text"].ToString()),
                    CreatedAt = reader.GetDateTime("created_at"),
                    SenderName = MessageService.BuildName(
                        reader["Name"], reader["Surname"], reader["login"]),
                    ReplyToId = reader["reply_to_id"] == DBNull.Value
                        ? 0 : Convert.ToInt32(reader["reply_to_id"]),
                    IsDeleted = Flag(reader["is_deleted"]),
                    EditedAt = reader["edited_at"] == DBNull.Value
                        ? (DateTime?)null : Convert.ToDateTime(reader["edited_at"]),
                    FileName = reader["file_name"] == DBNull.Value ? "" : reader["file_name"].ToString(),
                    FileSize = reader["file_size"] == DBNull.Value
                        ? 0 : Convert.ToInt64(reader["file_size"]),
                    IsRead = true,   // прочтений в каналах нет
                };

                if (msg.ReplyToId > 0 && reader["reply_text"] != DBNull.Value)
                {
                    msg.ReplyToText = Crypto.Dec(reader["reply_text"].ToString());
                    msg.ReplyToSender = MessageService.BuildName(
                        reader["reply_name"], reader["reply_surname"], reader["reply_login"]);
                }

                if (msg.IsDeleted) msg.Text = "";
                else
                {
                    int imgOrd = reader.GetOrdinal("image_data");
                    if (!reader.IsDBNull(imgOrd)) msg.ImageData = (byte[])reader["image_data"];
                }
                list.Add(msg);
            }
            return list;
        }

        public static int Send(int channelId, int myId, string text, byte[] imageData, int replyToId = 0)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "INSERT INTO server_messages (channel_id, sender_id, text, image_data, reply_to_id) " +
                "VALUES (@c, @s, @t, @img, @rep)", conn);
            cmd.Parameters.AddWithValue("@c", channelId);
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

        public static int SendFile(int channelId, int myId, string caption, byte[] data, string fileName)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "INSERT INTO server_messages (channel_id, sender_id, text, file_data, file_name) " +
                "VALUES (@c, @s, @t, @d, @n)", conn);
            cmd.Parameters.AddWithValue("@c", channelId);
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
                "UPDATE server_messages SET text=@t, edited_at=NOW() " +
                "WHERE id=@id AND sender_id=@me AND is_deleted=0", conn);
            cmd.Parameters.AddWithValue("@t", Crypto.Enc(newText ?? ""));
            cmd.Parameters.AddWithValue("@id", messageId);
            cmd.Parameters.AddWithValue("@me", myId);
            return cmd.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Удаление. Своё — всегда; чужое — если есть право управлять
        /// сервером, которому принадлежит канал. Оба условия стоят в самом
        /// UPDATE: модератор не должен полагаться на то, что клиент показал
        /// ему нужную кнопку.
        /// </summary>
        public static bool Delete(int myId, int messageId)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "UPDATE server_messages m " +
                "JOIN server_channels c ON c.id = m.channel_id " +
                "JOIN servers s ON s.id = c.server_id " +
                "LEFT JOIN server_members mem ON mem.server_id = s.id AND mem.user_id = @me " +
                "LEFT JOIN server_roles r ON r.id = mem.role_id " +
                "SET m.is_deleted=1, m.text='', m.image_data=NULL, m.file_data=NULL, " +
                "    m.audio_data=NULL, m.video_data=NULL " +
                "WHERE m.id=@id AND (m.sender_id=@me OR s.owner_id=@me OR r.can_manage=1)", conn);
            cmd.Parameters.AddWithValue("@id", messageId);
            cmd.Parameters.AddWithValue("@me", myId);
            return cmd.ExecuteNonQuery() > 0;
        }

        public static byte[] LoadFile(int messageId)
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "SELECT file_data FROM server_messages WHERE id=@id", conn);
            cmd.Parameters.AddWithValue("@id", messageId);
            var o = cmd.ExecuteScalar();
            return o == null || o == DBNull.Value ? null : (byte[])o;
        }

        /// <summary>Максимальный id по всем каналам сервера — дешёвый детект нового.</summary>
        public static int MaxMessageId(int serverId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT COALESCE(MAX(m.id),0) FROM server_messages m " +
                    "JOIN server_channels c ON c.id = m.channel_id WHERE c.server_id=@s", conn);
                cmd.Parameters.AddWithValue("@s", serverId);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch { return 0; }
        }

        // ── Голосовые каналы ────────────────────────────────────────────

        /// <summary>
        /// Отмечается в голосовом канале. Запись живёт, пока её обновляют:
        /// приложение может закрыться крестиком или потерять сеть, и тогда
        /// «выйти» за него некому — отсюда проверка свежести, а не факта
        /// наличия строки.
        /// </summary>
        public static void VoiceHeartbeat(int channelId, int userId,
            bool streaming = false, bool micMuted = false, bool deafened = false)
        {
            if (channelId <= 0) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "INSERT INTO voice_presence (channel_id,user_id,joined_at,last_seen,streaming,mic_muted,deafened) " +
                    "VALUES (@c,@u,NOW(),NOW(),@st,@mm,@df) " +
                    "ON DUPLICATE KEY UPDATE last_seen=NOW(), streaming=@st, mic_muted=@mm, deafened=@df", conn);
                cmd.Parameters.AddWithValue("@c", channelId);
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.Parameters.AddWithValue("@st", streaming ? 1 : 0);
                cmd.Parameters.AddWithValue("@mm", micMuted ? 1 : 0);
                cmd.Parameters.AddWithValue("@df", deafened ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        public static void VoiceLeave(int channelId, int userId)
        {
            if (channelId <= 0) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "DELETE FROM voice_presence WHERE channel_id=@c AND user_id=@u", conn);
                cmd.Parameters.AddWithValue("@c", channelId);
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        /// <summary>Кто сейчас «в эфире» в каналах сервера: channelId -> люди.</summary>
        public static Dictionary<int, List<VoiceUser>> VoiceForServer(int serverId)
        {
            var map = new Dictionary<int, List<VoiceUser>>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT vp.channel_id, vp.user_id, vp.streaming, vp.mic_muted, vp.deafened, " +
                    "       u.Name, u.Surname, u.login " +
                    "FROM voice_presence vp " +
                    "JOIN server_channels sc ON sc.id = vp.channel_id " +
                    "JOIN users u ON u.id = vp.user_id " +
                    $"WHERE sc.server_id=@s AND vp.last_seen > (NOW() - INTERVAL {VoiceFreshSec} SECOND)",
                    conn);
                cmd.Parameters.AddWithValue("@s", serverId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int ch = Convert.ToInt32(r["channel_id"]);
                    if (!map.TryGetValue(ch, out var people))
                        map[ch] = people = new List<VoiceUser>();
                    people.Add(new VoiceUser
                    {
                        UserId = Convert.ToInt32(r["user_id"]),
                        Name = MessageService.BuildName(r["Name"], r["Surname"], r["login"]),
                        Streaming = Flag(r["streaming"]),
                        MicMuted = Flag(r["mic_muted"]),
                        Deafened = Flag(r["deafened"]),
                    });
                }
            }
            catch { }
            return map;
        }
    }
}
