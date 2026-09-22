using System;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Профиль: аватар, баннер, «о себе», ссылки. Колонки users.avatar_data /
    /// banner_data / about / social_links.
    ///
    /// Картинки читаются ОТДЕЛЬНО от текста. Аватар нужен в списке на каждой
    /// строке, а баннер — только когда профиль открыт: тянуть их вместе
    /// значило бы качать по мегабайту на человека ради подписи под именем.
    /// </summary>
    public static class ProfileService
    {
        public sealed class Profile
        {
            public int Id;
            public string Name = "";
            public string Login = "";
            public string Role = "";
            public string About = "";
            public string Social = "";
        }

        public static Profile Load(int uid)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT id, Name, Surname, login, role, about, social_links " +
                    "FROM users WHERE id=@u", conn);
                cmd.Parameters.AddWithValue("@u", uid);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                return new Profile
                {
                    Id = Convert.ToInt32(r["id"]),
                    Name = MessageService.BuildName(r["Name"], r["Surname"], r["login"]),
                    Login = r["login"]?.ToString() ?? "",
                    Role = r["role"]?.ToString() ?? "",
                    About = r["about"] == DBNull.Value ? "" : r["about"].ToString(),
                    Social = r["social_links"] == DBNull.Value ? "" : r["social_links"].ToString(),
                };
            }
            catch { return null; }
        }

        public static void SaveAbout(int uid, string about, string social)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "UPDATE users SET about=@a, social_links=@s WHERE id=@u", conn);
                cmd.Parameters.AddWithValue("@a", about ?? "");
                cmd.Parameters.AddWithValue("@s", social ?? "");
                cmd.Parameters.AddWithValue("@u", uid);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        public static byte[] Avatar(int uid) => Blob(uid, "avatar_data");
        public static byte[] Banner(int uid) => Blob(uid, "banner_data");

        private static byte[] Blob(int uid, string column)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                // Имя колонки подставляется, а не приходит извне — здесь его
                // задают только два вызова выше.
                using var cmd = new MySqlCommand($"SELECT {column} FROM users WHERE id=@u", conn);
                cmd.Parameters.AddWithValue("@u", uid);
                var o = cmd.ExecuteScalar();
                return o == null || o == DBNull.Value ? null : (byte[])o;
            }
            catch { return null; }
        }

        public static void SetAvatar(int uid, byte[] data) => SetBlob(uid, "avatar_data", data);
        public static void SetBanner(int uid, byte[] data) => SetBlob(uid, "banner_data", data);

        private static void SetBlob(int uid, string column, byte[] data)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand($"UPDATE users SET {column}=@d WHERE id=@u", conn);
                if (data != null && data.Length > 0)
                    cmd.Parameters.Add("@d", MySqlDbType.LongBlob).Value = data;
                else
                    cmd.Parameters.AddWithValue("@d", DBNull.Value);
                cmd.Parameters.AddWithValue("@u", uid);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }
}
