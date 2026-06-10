// ============================================================
// SharedLib/Payloads/SyncPayload.cs
// ============================================================
using System;
using System.Collections.Generic;

namespace SharedLib.Payloads
{
    /// <summary>Đại diện một hành động vẽ đã được lưu (dùng cho sync & playback).</summary>
    public class DrawAction
    {
        public string ActionID { get; set; }
        public string Username { get; set; }
        public string ToolType { get; set; }
        public int X1 { get; set; }
        public int Y1 { get; set; }
        public int X2 { get; set; }
        public int Y2 { get; set; }
        public int ColorARGB { get; set; }
        public int Thickness { get; set; }
        public string Text { get; set; }
        public string FontName { get; set; }
        public int FontSize { get; set; }
        public string ImageData { get; set; }  // base64, dùng cho ImportImage
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
        public bool IsDeleted { get; set; }
        public long Timestamp { get; set; }
        public bool IsAiGenerated { get; set; } = false;
    }

    public class SyncBoardPayload
    {
        public string RoomCode { get; set; }
        public List<DrawAction> Actions { get; set; } = new List<DrawAction>();
        public List<string> RawActions { get; set; } = new List<string>();
        public bool IsChunked { get; set; }
        public int ChunkIndex { get; set; }
        public int TotalChunks { get; set; }
        public bool IsFinalChunk { get; set; }
    }

    public class UndoPayload
    {
        public string ActionID { get; set; }
        public string Username { get; set; }
    }

    public class RedoPayload
    {
        public string ActionID { get; set; }
        public string Username { get; set; }
    }

    public class PlaybackRequestPayload
    {
        public string RoomCode { get; set; }
    }

    // Snapshot: checkpoint dinh ky cua board (JSON), dung cho fast-join va xem lai trang thai cu (view-only).
    public class SnapshotInfo
    {
        public int SnapshotID { get; set; }
        public long Timestamp { get; set; }       // Unix ms cua taken_at
        public string ThumbnailBase64 { get; set; }
    }

    public class SnapshotListPayload
    {
        public string RoomCode { get; set; }
        public List<SnapshotInfo> Snapshots { get; set; } = new List<SnapshotInfo>();
    }

    public class SnapshotRestorePayload
    {
        public string RoomCode { get; set; }
        public int SnapshotID { get; set; }
    }

    // SNAPSHOT_DATA: request gui {RoomCode, SnapshotID}; response server tra ve them BoardJson
    // (mang JSON cac DrawAction) de client render thumbnail/preview offscreen, khong dung canvas chinh.
    public class SnapshotDataPayload
    {
        public string RoomCode { get; set; }
        public int SnapshotID { get; set; }
        public string BoardJson { get; set; }
    }
}
