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

        public bool HasImage => ImageData != null && ImageData.Length > 0;
        public bool IsMine => SenderId == UserSession.EffectiveId;
    }
}
