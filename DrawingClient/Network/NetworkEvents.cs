// ============================================================
// DrawingClient/Network/NetworkEvents.cs
// Tuần 2→8 — Tất cả events từ network → UI
// Person A subscribe events này trong MainForm/LobbyForm
// ============================================================
using System;
using System.Collections.Generic;
using SharedLib.Payloads;
using SharedLib.Logging;

namespace DrawingClient.Network
{
    /// <summary>
    /// Static event hub: network layer raise, UI layer subscribe.
    /// Tất cả events đều có thể được gọi từ background thread
    /// → Person A phải dùng this.Invoke() khi cập nhật UI.
    /// </summary>
    public static class NetworkEvents
    {
        // ── AUTH ────────────────────────────────────────────────
        public static event Action<LoginResponse> OnLoginResponse;
        public static event Action<RegisterResponse> OnRegisterResponse;

        // ── ROOM ────────────────────────────────────────────────
        public static event Action<CreateRoomResponse> OnCreateRoomResponse;
        public static event Action<JoinRoomResponse> OnJoinRoomResponse;
        public static event Action<RoomMembersPayload> OnRoomMembersReceived;
        public static event Action<UserJoinPayload> OnUserJoined;
        public static event Action<UserLeavePayload> OnUserLeft;
        public static event Action<CanvasSizePayload> OnCanvasSizeReceived;

        // ── DRAWING (UDP) ───────────────────────────────────────
        public static event Action<DrawPayload> OnDrawReceived;
        public static event Action<FloodFillPayload> OnFloodFillReceived;
        public static event Action<ImportImagePayload> OnImportImageReceived;
        public static event Action<SetBackgroundPayload> OnSetBackgroundReceived;
        public static event Action OnClearAllReceived;

        // ── SYNC / UNDO ──────────────────────────────────────────
        public static event Action<SyncBoardPayload> OnSyncBoardReceived;
        public static event Action<UndoPayload> OnUndoReceived;
        public static event Action<RedoPayload> OnRedoReceived;
        public static event Action<SnapshotListPayload> OnSnapshotListReceived;
        // Board JSON cua mot snapshot (de render thumbnail/preview offscreen): (snapshotId, actions)
        public static event Action<int, List<DrawAction>> OnSnapshotDataReceived;

        // ── INTERACTION (UDP) ───────────────────────────────────
        public static event Action<CursorPayload> OnCursorReceived;
        public static event Action<ReactionPayload> OnReactionReceived;

        // ── CHAT / ACTIVITY ─────────────────────────────────────
        public static event Action<ChatPayload> OnChatReceived;
        public static event Action<ActivityLogPayload> OnActivityLogReceived;

        // ── GALLERY ──────────────────────────────────────────────
        public static event Action<GalleryResponsePayload> OnGalleryReceived;
        public static event Action<SaveGalleryResponse> OnSaveGalleryResponse;
        public static event Action<PublicGalleryLinkPayload> OnPublicLinkReceived;

        // ── AI FEATURES (Tuần 5-6) ──────────────────────────────
        public static event Action<AiTextToImageResultPayload> OnAiTextToImageResult;
        public static event Action<AiBgRemovedPayload> OnAiBgRemovedResult;

        // ── ADVANCED FEATURES (Tuần 5-6) ────────────────────────
        public static event Action<StickerPayload> OnStickerReceived;
        public static event Action<TurnBasedPayload> OnTurnBasedReceived;
        public static event Action<StickyNotePayload> OnStickyNoteReceived;

        // ── PIXEL ART / EXPORT ─────────────────────
        public static event Action<PixelArtDrawPayload> OnPixelArtDrawReceived;
        public static event Action<PixelArtSyncPayload> OnPixelArtSyncReceived;

        // ── CONNECTION ──────────────────────────────────────────
        public static event Action OnDisconnected;
        public static event Action OnConnected;

        // ── RAISE METHODS ────────────────────────────────────────
        private static void SafeInvoke<T>(Action<T> handler, T payload, string eventName)
        {
            if (handler == null) return;
            foreach (Action<T> subscriber in handler.GetInvocationList())
            {
                try { subscriber(payload); }
                catch (Exception ex) { Logger.Exception($"NetworkEvents.{eventName}", ex); }
            }
        }

        private static void SafeInvoke(Action handler, string eventName)
        {
            if (handler == null) return;
            foreach (Action subscriber in handler.GetInvocationList())
            {
                try { subscriber(); }
                catch (Exception ex) { Logger.Exception($"NetworkEvents.{eventName}", ex); }
            }
        }

        private static void SafeInvoke<T1, T2>(Action<T1, T2> handler, T1 a, T2 b, string eventName)
        {
            if (handler == null) return;
            foreach (Action<T1, T2> subscriber in handler.GetInvocationList())
            {
                try { subscriber(a, b); }
                catch (Exception ex) { Logger.Exception($"NetworkEvents.{eventName}", ex); }
            }
        }

        public static void RaiseLoginResponse(LoginResponse p) => SafeInvoke(OnLoginResponse, p, nameof(OnLoginResponse));
        public static void RaiseRegisterResponse(RegisterResponse p) => SafeInvoke(OnRegisterResponse, p, nameof(OnRegisterResponse));
        public static void RaiseCreateRoomResponse(CreateRoomResponse p) => SafeInvoke(OnCreateRoomResponse, p, nameof(OnCreateRoomResponse));
        public static void RaiseJoinRoomResponse(JoinRoomResponse p) => SafeInvoke(OnJoinRoomResponse, p, nameof(OnJoinRoomResponse));
        public static void RaiseRoomMembersReceived(RoomMembersPayload p) => SafeInvoke(OnRoomMembersReceived, p, nameof(OnRoomMembersReceived));
        public static void RaiseUserJoined(UserJoinPayload p) => SafeInvoke(OnUserJoined, p, nameof(OnUserJoined));
        public static void RaiseUserLeft(UserLeavePayload p) => SafeInvoke(OnUserLeft, p, nameof(OnUserLeft));
        public static void RaiseCanvasSizeReceived(CanvasSizePayload p) => SafeInvoke(OnCanvasSizeReceived, p, nameof(OnCanvasSizeReceived));
        public static void RaiseDrawReceived(DrawPayload p) => SafeInvoke(OnDrawReceived, p, nameof(OnDrawReceived));
        public static void RaiseFloodFillReceived(FloodFillPayload p) => SafeInvoke(OnFloodFillReceived, p, nameof(OnFloodFillReceived));
        public static void RaiseImportImageReceived(ImportImagePayload p) => SafeInvoke(OnImportImageReceived, p, nameof(OnImportImageReceived));
        public static void RaiseSetBackgroundReceived(SetBackgroundPayload p) => SafeInvoke(OnSetBackgroundReceived, p, nameof(OnSetBackgroundReceived));
        public static void RaiseClearAll() => SafeInvoke(OnClearAllReceived, nameof(OnClearAllReceived));
        public static void RaiseSyncBoardReceived(SyncBoardPayload p) => SafeInvoke(OnSyncBoardReceived, p, nameof(OnSyncBoardReceived));
        public static void RaiseUndoReceived(UndoPayload p) => SafeInvoke(OnUndoReceived, p, nameof(OnUndoReceived));
        public static void RaiseRedoReceived(RedoPayload p) => SafeInvoke(OnRedoReceived, p, nameof(OnRedoReceived));
        public static void RaiseSnapshotListReceived(SnapshotListPayload p) => SafeInvoke(OnSnapshotListReceived, p, nameof(OnSnapshotListReceived));
        public static void RaiseSnapshotDataReceived(int snapshotId, List<DrawAction> actions) => SafeInvoke(OnSnapshotDataReceived, snapshotId, actions, nameof(OnSnapshotDataReceived));
        public static void RaiseCursorReceived(CursorPayload p) => SafeInvoke(OnCursorReceived, p, nameof(OnCursorReceived));
        public static void RaiseReactionReceived(ReactionPayload p) => SafeInvoke(OnReactionReceived, p, nameof(OnReactionReceived));
        public static void RaiseChatReceived(ChatPayload p) => SafeInvoke(OnChatReceived, p, nameof(OnChatReceived));
        public static void RaiseActivityLogReceived(ActivityLogPayload p) => SafeInvoke(OnActivityLogReceived, p, nameof(OnActivityLogReceived));
        public static void RaiseGalleryReceived(GalleryResponsePayload p) => SafeInvoke(OnGalleryReceived, p, nameof(OnGalleryReceived));
        public static void RaiseSaveGalleryResponse(SaveGalleryResponse p) => SafeInvoke(OnSaveGalleryResponse, p, nameof(OnSaveGalleryResponse));
        public static void RaisePublicLinkReceived(PublicGalleryLinkPayload p) => SafeInvoke(OnPublicLinkReceived, p, nameof(OnPublicLinkReceived));
        public static void RaiseAiTextToImageResult(AiTextToImageResultPayload p) => SafeInvoke(OnAiTextToImageResult, p, nameof(OnAiTextToImageResult));
        public static void RaiseAiBgRemovedResult(AiBgRemovedPayload p) => SafeInvoke(OnAiBgRemovedResult, p, nameof(OnAiBgRemovedResult));
        public static void RaiseStickerReceived(StickerPayload p) => SafeInvoke(OnStickerReceived, p, nameof(OnStickerReceived));
        public static void RaiseTurnBasedReceived(TurnBasedPayload p) => SafeInvoke(OnTurnBasedReceived, p, nameof(OnTurnBasedReceived));
        public static void RaiseStickyNoteReceived(StickyNotePayload p) => SafeInvoke(OnStickyNoteReceived, p, nameof(OnStickyNoteReceived));
        public static void RaisePixelArtDrawReceived(PixelArtDrawPayload p) => SafeInvoke(OnPixelArtDrawReceived, p, nameof(OnPixelArtDrawReceived));
        public static void RaisePixelArtSyncReceived(PixelArtSyncPayload p) => SafeInvoke(OnPixelArtSyncReceived, p, nameof(OnPixelArtSyncReceived));
        public static void RaiseDisconnected() => SafeInvoke(OnDisconnected, nameof(OnDisconnected));
        public static void RaiseConnected() => SafeInvoke(OnConnected, nameof(OnConnected));
    }
}
