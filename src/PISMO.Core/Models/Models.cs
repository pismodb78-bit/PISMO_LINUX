using System;

namespace PISMO
{
    /// <summary>Пользователь в результатах поиска / списке (admin-режим).</summary>
    public sealed class UserItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Login { get; set; } = "";
        public string Role { get; set; } = "";
    }

    /// <summary>Элемент списка диалогов в сайдбаре.</summary>
    public sealed class ConversationItem
    {
        public int PartnerId { get; set; }
        public string Name { get; set; } = "";
        public string Login { get; set; } = "";
        public string LastMessage { get; set; } = "";
        public int Unread { get; set; }
    }

    /// <summary>Одно сообщение личной переписки.</summary>
    public sealed class ChatMessage
    {
        public int Id { get; set; }
        public int SenderId { get; set; }
        public int ReceiverId { get; set; }
        public string Text { get; set; } = "";
        public byte[] ImageData { get; set; }
        public bool IsRead { get; set; }
        public DateTime CreatedAt { get; set; }
        public string SenderName { get; set; } = "";

        // ── Схема v2: ответы, правка, удаление, файлы ───────────────
        //
        // Вложения читаем НЕ вместе с перепиской: file_data и прочие BLOB'ы
        // весят мегабайты, и тянуть их на каждую отрисовку списка значит
        // ждать по несколько секунд на ровном месте. В списке держим только
        // размер и имя, а сами байты берём, когда человек нажал.
        public int ReplyToId { get; set; }
        public string ReplyToText { get; set; } = "";
        public string ReplyToSender { get; set; } = "";
        public bool IsDeleted { get; set; }
        public DateTime? EditedAt { get; set; }
        public string FileName { get; set; } = "";
        public long FileSize { get; set; }
        public bool HasAudio { get; set; }
        public bool HasVideo { get; set; }
        public bool IsPinned { get; set; }

        public bool HasImage => ImageData != null && ImageData.Length > 0;
        public bool HasFile => FileSize > 0;
        public bool IsEdited => EditedAt.HasValue;
        public bool IsMine => SenderId == UserSession.EffectiveId;
    }
}
