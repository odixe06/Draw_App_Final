// ============================================================
// SharedLib/Payloads/InteractionPayload.cs
// Tất cả payload tương tác: cursor, emoji, chat, log,
// spotlight, follow, sticker, sticky note (Tuần 2-6)
// ============================================================
namespace SharedLib.Payloads
{
    // ── Tuần 2 ──────────────────────────────────────────────

    public class CursorPayload
    {
        public string Username { get; set; }
        public string RoomCode { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public long Timestamp { get; set; }
    }

    public class ReactionPayload
    {
        public string Username { get; set; }
        public string Emoji { get; set; }   // "👍", "❤️", "😂"
        public int X { get; set; }
        public int Y { get; set; }
    }

    // ── Tuần 3 ──────────────────────────────────────────────

    public class ChatPayload
    {
        public string Username { get; set; }
        public int ColorARGB { get; set; }
        public string Message { get; set; }
        public long Timestamp { get; set; }
    }

    public class ActivityLogPayload
    {
        public string Username { get; set; }
        public string Action { get; set; }  // "joined", "left", "drew", "undo", "flood_fill", etc.
        public long Timestamp { get; set; }
    }

    // ── Tuần 5 ──────────────────────────────────────────────

    /// <summary>Sticker & Shape Library — kéo thả hình dán vào canvas.</summary>
    public class StickerPayload
    {
        public string ActionID { get; set; }
        public string Username { get; set; }
        public string StickerID { get; set; }  // "heart", "star", "arrow", "emoji_happy", ...
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public float Rotation { get; set; }    // độ
        public bool IsDeleted { get; set; }
        public long Timestamp { get; set; }
    }

    /// <summary>Sticky Note / Comment — giống Figma comment.</summary>
    public class StickyNotePayload
    {
        public string NoteID { get; set; }
        public string AuthorUsername { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Text { get; set; }
        public bool IsOpen { get; set; } = true;
        public long Timestamp { get; set; }
    }

    // ── Tuần 6 ──────────────────────────────────────────────

    public class TurnBasedPayload
    {
        public string RoomCode { get; set; }
        public string Username { get; set; }
        public bool IsEnabled { get; set; }
        public string ActiveUser { get; set; }
    }

}
