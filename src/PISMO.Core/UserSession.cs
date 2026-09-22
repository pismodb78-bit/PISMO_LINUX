namespace PISMO
{
    /// <summary>Статическое хранилище данных залогиненного пользователя.</summary>
    public static class UserSession
    {
        public static int    UserId       { get; set; } = 0;
        public static string UserName     { get; set; } = "";

        /// <summary>
        /// Логин — не то же самое, что имя. UserName это «Имя Фамилия» для
        /// показа, а упоминания в сообщениях пишутся через логин (@ivanov),
        /// и сравнивать их с отображаемым именем бессмысленно.
        /// </summary>
        public static string Login        { get; set; } = "";
        public static string Role         { get; set; } = "";   // "admin" | "teacher"

        // Для режима «войти за пользователя» (только admin)
        public static int    ImpersonatedId   { get; set; } = 0;
        public static string ImpersonatedName { get; set; } = "";

        public static bool IsImpersonating => ImpersonatedId > 0;

        /// <summary>Эффективный ID с учётом impersonation</summary>
        public static int    EffectiveId   => IsImpersonating ? ImpersonatedId   : UserId;
        public static string EffectiveName => IsImpersonating ? ImpersonatedName : UserName;

        public static void Clear()
        {
            UserId = 0;
            UserName = "";
            Login = "";
            Role = "";
            StopImpersonating();
        }

        public static void StopImpersonating()
        {
            ImpersonatedId = 0;
            ImpersonatedName = "";
        }
    }
}
