using System;
using System.Data;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Аутентификация и управление аккаунтом. Логика полностью перенесена из
    /// WinForms-форм (LoginForm / RegisterForm / ChangePasswordForm) без изменения
    /// SQL-запросов — совместима с существующей схемой bdauth.
    /// </summary>
    public static class AuthService
    {
        public sealed class Result
        {
            public bool Ok { get; init; }
            public string Error { get; init; } = "";
            public static Result Success() => new() { Ok = true };
            public static Result Fail(string msg) => new() { Ok = false, Error = msg };
        }

        /// <summary>Проверяет доступность БД (аналог LoginForm_Load).</summary>
        public static Result TestConnection()
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Fail(ex.Message);
            }
        }

        /// <summary>
        /// Вход. При успехе заполняет <see cref="UserSession"/> и возвращает Ok.
        /// SQL идентичен LoginForm.btnLogin_Click.
        /// </summary>
        public static Result Login(string login, string password)
        {
            login = (login ?? "").Trim();
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
                return Result.Fail("Заполните логин и пароль.");

            try
            {
                using var conn = DBHelper.OpenConnection();
                const string sql =
                    "SELECT id, Name, Surname, role FROM users WHERE login=@l AND password=@p";
                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@l", login);
                cmd.Parameters.AddWithValue("@p", password);

                var dt = new DataTable();
                new MySqlDataAdapter(cmd).Fill(dt);

                if (dt.Rows.Count == 0)
                    return Result.Fail("Неверный логин или пароль.");

                DataRow row = dt.Rows[0];
                UserSession.UserId = Convert.ToInt32(row["id"]);
                UserSession.UserName = $"{row["Name"]} {row["Surname"]}".Trim();
                UserSession.Role = row["role"].ToString().ToLower();
                if (string.IsNullOrWhiteSpace(UserSession.UserName))
                    UserSession.UserName = login;

                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Fail("Ошибка БД: " + ex.Message);
            }
        }

        /// <summary>Регистрация нового пользователя (роль teacher). SQL из RegisterForm.</summary>
        public static Result Register(string name, string surname, string login, string password)
        {
            name = (name ?? "").Trim();
            surname = (surname ?? "").Trim();
            login = (login ?? "").Trim();

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(surname) ||
                string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
                return Result.Fail("Заполните все поля!");
            if (login.ToLower() == "admin")
                return Result.Fail("Этот логин зарезервирован.");
            if (password.Length < 8)
                return Result.Fail("Пароль минимум 8 символов.");
            if (password == "12345678" || password == "87654321")
                return Result.Fail("Пароль слишком предсказуем!");

            try
            {
                using var conn = DBHelper.OpenConnection();
                using (var chk = new MySqlCommand("SELECT COUNT(*) FROM users WHERE login=@l", conn))
                {
                    chk.Parameters.AddWithValue("@l", login);
                    if (Convert.ToInt32(chk.ExecuteScalar()) > 0)
                        return Result.Fail("Этот логин уже занят.");
                }

                const string sql =
                    "INSERT INTO users (login, password, Name, Surname, role) " +
                    "VALUES (@l, @p, @n, @s, 'teacher')";
                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@l", login);
                cmd.Parameters.AddWithValue("@p", password);
                cmd.Parameters.AddWithValue("@n", name);
                cmd.Parameters.AddWithValue("@s", surname);
                cmd.ExecuteNonQuery();

                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Fail("Ошибка БД: " + ex.Message);
            }
        }

        /// <summary>Смена пароля текущего пользователя. SQL из ChangePasswordForm.</summary>
        public static Result ChangePassword(string oldPass, string newPass, string confirmPass)
        {
            if ((newPass ?? "").Length < 8)
                return Result.Fail("Пароль минимум 8 символов!");
            if (newPass == "12345678" || newPass == "87654321")
                return Result.Fail("Пароль слишком предсказуем!");
            if (string.IsNullOrWhiteSpace(newPass) || newPass.Trim().Length == 0)
                return Result.Fail("Пароль не может состоять из пробелов!");
            if (newPass != confirmPass)
                return Result.Fail("Пароли не совпадают!");

            try
            {
                using var conn = DBHelper.OpenConnection();
                using (var chk = new MySqlCommand(
                    "SELECT COUNT(*) FROM users WHERE id=@uid AND password=@old", conn))
                {
                    chk.Parameters.AddWithValue("@uid", UserSession.UserId);
                    chk.Parameters.AddWithValue("@old", oldPass);
                    if (Convert.ToInt32(chk.ExecuteScalar()) == 0)
                        return Result.Fail("Старый пароль неверен!");
                }

                using var upd = new MySqlCommand(
                    "UPDATE users SET password=@new WHERE id=@uid", conn);
                upd.Parameters.AddWithValue("@new", newPass);
                upd.Parameters.AddWithValue("@uid", UserSession.UserId);
                upd.ExecuteNonQuery();

                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Fail("Ошибка БД: " + ex.Message);
            }
        }
    }
}
