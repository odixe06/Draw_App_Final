// ============================================================
// SharedLib/Packets/PacketDef.cs
// Dinh nghia toan bo CommandType cho giao thuc NT106 Drawing App.
// ============================================================
using System;

namespace SharedLib.Packets
{
    /// <summary>
    /// Toan bo command types cho giao thuc NT106 Drawing App.
    /// TCP: Auth, Room, Sync, Undo, Chat, Gallery, Security, AI, Snapshot
    /// UDP: Draw, Cursor, Reaction, PixelArt
    /// </summary>
    public enum CommandType : byte
    {
        // -- AUTH (TCP) ------------------------------------------
        LOGIN = 0x01,
        REGISTER = 0x02,
        LOGIN_RESPONSE = 0x03,
        REGISTER_RESPONSE = 0x04,

        // -- ROOM MANAGEMENT (TCP) -------------------------------
        CREATE_ROOM = 0x10,
        CREATE_ROOM_RESPONSE = 0x11,
        JOIN_ROOM = 0x12,
        JOIN_ROOM_RESPONSE = 0x13,
        LEAVE_ROOM = 0x14,

        // -- ROOM INFO (TCP broadcast) ---------------------------
        ROOM_MEMBERS = 0x20,
        USER_JOIN = 0x21,
        USER_LEAVE = 0x22,

        // -- DRAWING (TCP/UDP) -----------------------------------
        DRAW = 0x30,
        FLOOD_FILL = 0x31,
        TEXT = 0x32,
        SPRAY = 0x33,
        IMPORT_IMAGE = 0x34,
        SET_BACKGROUND = 0x35,
        CLEAR_ALL = 0x36,     // broadcast xoa canvas

        // -- SYNC (TCP) ------------------------------------------
        SYNC_BOARD = 0x40,
        CANVAS_SIZE = 0x41,

        // -- UNDO / REDO (TCP) -----------------------------------
        UNDO = 0x50,
        REDO = 0x51,

        // -- INTERACTION (TCP/UDP) -------------------------------
        CHAT = 0x60,     // TCP
        REACTION = 0x61,     // UDP/TCP emoji reaction
        CURSOR = 0x62,     // UDP/TCP real-time
        LASER = 0x63,     // legacy disabled (server bo qua)
        ACTIVITY_LOG = 0x64,     // TCP
        UDP_PING = 0x65,     // UDP endpoint registration

        // -- FEATURES (TCP) --------------------------------------
        SET_TURNBASED = 0x80,
        TURN_CHANGE = 0x81,
        REQUEST_PLAYBACK = 0x82,

        // -- GALLERY (TCP) ---------------------------------------
        SAVE_TO_GALLERY = 0x90,
        GET_GALLERY = 0x91,
        GALLERY_RESPONSE = 0x92,
        PUBLIC_GALLERY_LINK = 0x93,

        // -- AI FEATURES (TCP) -----------------------------------
        AI_TEXT_TO_IMAGE = 0xA0,
        AI_BG_REMOVED = 0xA1,

        // -- ADVANCED FEATURES (TCP/UDP) -------------------------
        STICKER = 0xB0,     // Sticker & Shape Library
        STICKY_NOTE = 0xB3,     // Sticky note/comment
        SNAPSHOT_LIST = 0xB9,     // Snapshot: liet ke checkpoint cua phong
        SNAPSHOT_RESTORE = 0xBA,     // Snapshot: xem lai mot checkpoint (view-only)
        SNAPSHOT_DATA = 0xBB,     // Snapshot: lay board JSON cua mot checkpoint de render thumbnail/preview (khong dung canvas chinh)

        // -- PIXEL ART (TCP/UDP) --------------------------------
        PIXEL_ART_DRAW = 0xC3,     // UDP
        PIXEL_ART_SYNC = 0xC4,     // TCP

        // -- SYSTEM ----------------------------------------------
        HEARTBEAT = 0xF0,
        DISCONNECT = 0xFF
    }

    /// <summary>
    /// Cau truc packet: [Header=0xFF(1B)] [Cmd(1B)] [Length(4B, big-endian)] [Payload(N bytes, UTF-8 JSON)]
    /// </summary>
    public class Packet
    {
        public const byte HEADER_BYTE = 0xFF;

        public byte Header { get; set; } = HEADER_BYTE;
        public CommandType Cmd { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>Chuyen Packet thanh byte[] de gui qua socket.</summary>
        public byte[] Serialize()
        {
            int payloadLen = Payload?.Length ?? 0;
            // Header(1) + Cmd(1) + Length(4) + Payload
            byte[] result = new byte[6 + payloadLen];
            result[0] = HEADER_BYTE;
            result[1] = (byte)Cmd;
            // Length big-endian
            result[2] = (byte)((payloadLen >> 24) & 0xFF);
            result[3] = (byte)((payloadLen >> 16) & 0xFF);
            result[4] = (byte)((payloadLen >> 8) & 0xFF);
            result[5] = (byte)(payloadLen & 0xFF);
            if (payloadLen > 0)
                Buffer.BlockCopy(Payload, 0, result, 6, payloadLen);
            return result;
        }

        /// <summary>Phan tich byte[] nhan tu socket thanh Packet.</summary>
        public static Packet Deserialize(byte[] data)
        {
            if (data == null || data.Length < 6)
                throw new ArgumentException("Du lieu packet qua ngan.");
            if (data[0] != HEADER_BYTE)
                throw new ArgumentException($"Header khong hop le: 0x{data[0]:X2}");

            int payloadLen = (data[2] << 24) | (data[3] << 16) | (data[4] << 8) | data[5];
            if (data.Length < 6 + payloadLen)
                throw new ArgumentException("Payload bi cat ngan.");

            byte[] payload = new byte[payloadLen];
            if (payloadLen > 0)
                Buffer.BlockCopy(data, 6, payload, 0, payloadLen);

            return new Packet
            {
                Header = data[0],
                Cmd = (CommandType)data[1],
                Payload = payload
            };
        }

        public override string ToString()
            => $"Packet[{Cmd}] {Payload?.Length ?? 0} bytes";
    }
}
