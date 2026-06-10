// ============================================================
// DrawingClient/Network/UdpManager.cs — FIX v5
// GIẢI PHÁP ĐÚNG: Dùng 1 UdpClient duy nhất cho cả GỬI lẫn NHẬN
// - Bind 1 port cố định (ví dụ: hệ thống tự cấp khi bind port 0)
// - Gửi: dùng socket đó gửi đến server
// - Nhận: dùng cùng socket đó nhận về
// => Server broadcast về đúng port nguồn => nhận được!
// ============================================================
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharedLib.Packets;
using SharedLib.Payloads;
using SharedLib.Security;
using Newtonsoft.Json;

namespace DrawingClient.Network
{
    public class UdpManager : IDisposable
    {
        private UdpClient _socket;          // 1 socket duy nhất cho cả gửi lẫn nhận
        private CancellationTokenSource _cts;
        private Task _listenTask;
        private readonly string _serverIp;
        private readonly int _serverPort;
        private readonly IPEndPoint _serverEndpoint;
        private bool _disposed = false;

        public int LocalPort { get; private set; }

        public UdpManager(string serverIp = "127.0.0.1", int serverPort = 8889)
        {
            _serverIp = serverIp;
            _serverPort = serverPort;
            IPAddress address;
            if (!IPAddress.TryParse(serverIp, out address))
                address = Dns.GetHostAddresses(serverIp)[0];
            _serverEndpoint = new IPEndPoint(address, serverPort);
        }

        public void Start()
        {
            // Bind port 0 → hệ thống tự cấp 1 port trống
            _socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            LocalPort = ((IPEndPoint)_socket.Client.LocalEndPoint).Port;

            _cts = new CancellationTokenSource();
            // XU LY BAT DONG BO/DA LUONG: listen UDP tren task nen de client van gui TCP/UI binh thuong.
            _listenTask = Task.Run(() => ListenLoop(_cts.Token));

            Console.WriteLine($"[UdpManager] Khởi động trên port {LocalPort}");
        }

        // ── GỬI ─────────────────────────────────────────────

        private void SendPacket(CommandType cmd, object payload)
        {
            try
            {
                if (_socket == null)
                    return;

                string json = JsonConvert.SerializeObject(payload);
                byte[] payloadBytes = Encoding.UTF8.GetBytes(json);
                Packet packet = new Packet { Cmd = cmd, Payload = payloadBytes };
                // MA HOA: moi packet UDP duoc ma hoa AES truoc khi gui ra mang.
                byte[] encrypted = AesHelper.Encrypt(packet.Serialize());
                _socket.Send(encrypted, encrypted.Length, _serverEndpoint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UdpManager] Lỗi gửi: {ex.Message}");
            }
        }

        public void SendDraw(DrawPayload p) => SendPacket(CommandType.DRAW, p);
        public void SendText(DrawPayload p) => SendPacket(CommandType.TEXT, p);
        public void SendFloodFill(FloodFillPayload p) => SendPacket(CommandType.FLOOD_FILL, p);
        public void SendSetBackground(SetBackgroundPayload p) => SendPacket(CommandType.SET_BACKGROUND, p);
        public void SendSticker(StickerPayload p) => SendPacket(CommandType.STICKER, p);
        public void SendChat(ChatPayload p) => SendPacket(CommandType.CHAT, p);
        public void SendTurnBased(TurnBasedPayload p) => SendPacket(CommandType.SET_TURNBASED, p);
        public void SendTurnChange(TurnBasedPayload p) => SendPacket(CommandType.TURN_CHANGE, p);
        public void SendCursor(CursorPayload p) => SendPacket(CommandType.CURSOR, p);

        public void RegisterEndpoint(string username, string roomCode, string serverId = "")
        {
            if (string.IsNullOrWhiteSpace(username))
                return;

            SendPacket(CommandType.UDP_PING, new
            {
                Username = username,
                RoomCode = roomCode,
                ServerId = serverId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        // ── NHẬN ────────────────────────────────────────────

        private void ListenLoop(CancellationToken token)
        {
            // XU LY DA LUONG: vong lap nhan UDP chay tren background task, doc lien tuc datagram tu server.
            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    byte[] data = _socket.Receive(ref remoteEP);
                    ProcessPacket(data);
                }
                catch { break; }
            }
        }

        private void ProcessPacket(byte[] data)
        {
            try
            {
                // MA HOA: giai ma AES truoc khi deserialize Packet va day vao NetworkEvents.
                byte[] decrypted = AesHelper.Decrypt(data);
                Packet packet = Packet.Deserialize(decrypted);
                string json = Encoding.UTF8.GetString(packet.Payload);

                switch (packet.Cmd)
                {
                    case CommandType.DRAW:
                    case CommandType.TEXT:
                        NetworkEvents.RaiseDrawReceived(
                            JsonConvert.DeserializeObject<DrawPayload>(json));
                        break;
                    case CommandType.FLOOD_FILL:
                        NetworkEvents.RaiseFloodFillReceived(
                            JsonConvert.DeserializeObject<FloodFillPayload>(json));
                        break;
                    case CommandType.SET_BACKGROUND:
                        NetworkEvents.RaiseSetBackgroundReceived(
                            JsonConvert.DeserializeObject<SetBackgroundPayload>(json));
                        break;
                    case CommandType.STICKER:
                        NetworkEvents.RaiseStickerReceived(
                            JsonConvert.DeserializeObject<StickerPayload>(json));
                        break;
                    case CommandType.CHAT:
                        NetworkEvents.RaiseChatReceived(
                            JsonConvert.DeserializeObject<ChatPayload>(json));
                        break;
                    case CommandType.SET_TURNBASED:
                    case CommandType.TURN_CHANGE:
                        NetworkEvents.RaiseTurnBasedReceived(
                            JsonConvert.DeserializeObject<TurnBasedPayload>(json));
                        break;
                    case CommandType.CURSOR:
                        NetworkEvents.RaiseCursorReceived(
                            JsonConvert.DeserializeObject<CursorPayload>(json));
                        break;
                    // REACTION da chuyen sang TCP (xem ClientNetwork) — khong xu ly qua UDP nua.
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UdpManager] Lỗi xử lý packet: {ex.Message}");
            }
        }

        // ── CLEANUP ──────────────────────────────────────────

        public void Stop()
        {
            _cts?.Cancel();
            _socket?.Close();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _socket?.Dispose();
                _cts?.Dispose();
                _disposed = true;
            }
        }
    }
}
