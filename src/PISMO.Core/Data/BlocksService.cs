using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Блокировки: таблица user_blocks (blocker_id, blocked_id).
    /// Порт того, что на ПК живёт в MainForm_MessageActions.
    ///
    /// Блокировка ДВУСТОРОННЯЯ по последствиям: заблокировавший не получает
    /// сообщений, но и заблокированный не может написать. Если бы работало
    /// только первое, человек писал бы в пустоту и ждал ответа, которого не
    /// будет, — это хуже прямого «нельзя».
    /// </summary>
    public static class BlocksService
    {
        /// <summary>Я заблокировал его?</summary>
        public static bool IBlocked(int me, int them) => Exists(me, them);

        /// <summary>Он заблокировал меня?</summary>
        public static bool BlockedMe(int me, int them) => Exists(them, me);

        private static bool Exists(int blocker, int blocked)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT 1 FROM user_blocks WHERE blocker_id=@b AND blocked_id=@t", conn);
                cmd.Parameters.AddWithValue("@b", blocker);
                cmd.Parameters.AddWithValue("@t", blocked);
                return cmd.ExecuteScalar() != null;
            }
            catch { return false; }
        }

        /// <summary>Обе стороны сразу — одним запросом вместо двух.</summary>
        public static (bool iBlocked, bool blockedMe) State(int me, int them)
        {
            bool mine = false, theirs = false;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT blocker_id FROM user_blocks " +
                    "WHERE (blocker_id=@me AND blocked_id=@th) OR (blocker_id=@th AND blocked_id=@me)",
                    conn);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.Parameters.AddWithValue("@th", them);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (Convert.ToInt32(r[0]) == me) mine = true;
                    else theirs = true;
                }
            }
            catch { }
            return (mine, theirs);
        }

        public static void Block(int me, int them)
        {
            if (me == them) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "INSERT IGNORE INTO user_blocks (blocker_id, blocked_id) VALUES (@b, @t)", conn);
                cmd.Parameters.AddWithValue("@b", me);
                cmd.Parameters.AddWithValue("@t", them);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        public static void Unblock(int me, int them)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "DELETE FROM user_blocks WHERE blocker_id=@b AND blocked_id=@t", conn);
                cmd.Parameters.AddWithValue("@b", me);
                cmd.Parameters.AddWithValue("@t", them);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        /// <summary>Все, кого я заблокировал — для списка в настройках.</summary>
        public static List<int> MyBlocked(int me)
        {
            var list = new List<int>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT blocked_id FROM user_blocks WHERE blocker_id=@b", conn);
                cmd.Parameters.AddWithValue("@b", me);
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add(Convert.ToInt32(r[0]));
            }
            catch { }
            return list;
        }
    }
}
