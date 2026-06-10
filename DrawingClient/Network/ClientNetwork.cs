using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using SharedLib.Packets;
using SharedLib.Payloads;
using SharedLib.Logging;
using System.Net.Security;
using System.Security.Authentication;
using Newtonsoft.Json;
using SharedLib.Config;

namespace DrawingClient.Network
{
    public class ClientNetwork
    {
        private TcpClient _tcpClient;
        private Stream _stream;
        private Thread _receiveThread;
        private Thread _heartbeatThread;
        private volatile bool _running = false;
        private string _lastPassword = "";

        // Heartbeat
        private long _lastHeartbeatReceived = 0;
        private const int HEARTBEAT_INTERVAL_SEC = 15;
        private const int HEARTBEAT_TIMEOUT_SEC = 60;

        public string CurrentUsername { get; private set; }
        public string CurrentRoomCode { get; private set; }
        public string ServerIp { get; private set; } = "127.0.0.1";
        public int ServerTcpPort { get; private set; } = 8888;
        public int ServerUdpPort { get; private set; } = 8889;
        public bool PreferTcpRealtime { get; set; }
        public string AssignedServerId { get; private set; } = "";
        public bool IsConnected => _tcpClient?.Connected ?? false;
        private int ConnectTimeoutMs => Math.Max(1000, EnvLoader.GetInt("CLIENT_CONNECT_TIMEOUT_MS", 6000));

        public void SetAssignedServer(string serverIp, int tcpPort, int udpPort, string serverId = "")
        {
            ServerIp = string.IsNullOrWhiteSpace(serverIp) ? "127.0.0.1" : serverIp.Trim();
            ServerTcpPort = tcpPort > 0 ? tcpPort : 8888;
            ServerUdpPort = udpPort > 0 ? udpPort : 8889;
            AssignedServerId = serverId ?? "";
        }

        // ── CONNECT / DISCONNECT ────────────────────────────────

        public bool Connect(string ip, int port = 8888, bool useSSL = true)
        {
            try
            {
                ServerIp = string.IsNullOrWhiteSpace(ip) ? "127.0.0.1" : ip.Trim();
                ServerTcpPort = port;
                _tcpClient = new TcpClient();
                _tcpClient.NoDelay = true;
                ConnectTcpWithTimeout(_tcpClient, ServerIp, port, ConnectTimeoutMs);

                if (useSSL)
                {
                    // LUONG STREAM: moi TCP socket duoc boc bang SslStream de packet LOGIN/CHAT/DRAW di qua TLS.
                    // LUONG STREAM: boc NetworkStream bang SslStream de toan bo packet TCP sau do di qua TLS.
                    var ssl = new SslStream(_tcpClient.GetStream(), false, (s, cert, chain, err) => true);
                    AuthenticateSslWithTimeout(ssl, ConnectTimeoutMs);
                    _stream = ssl;
                    Logger.Info("ClientNetwork", "Kết nối SSL thành công.");
                }
                else
                {
                    _stream = _tcpClient.GetStream();
                    Logger.Info("ClientNetwork", "Kết nối TCP (không SSL).");
                }

                _running = true;
                _lastHeartbeatReceived = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // XU LY DA LUONG: tach thread nhan packet TCP de khong chan thread UI/gui lenh.
                // Thread nay chi doc stream va day packet vao NetworkEvents; UI se duoc marshal bang BeginInvoke o cac form.
                _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "TCP-Recv" };
                _receiveThread.Start();

                // XU LY DA LUONG: heartbeat chay tren thread rieng de phat hien mat ket noi dinh ky.
                // Neu gop vao receive loop, khi server im lang client se kho phat hien timeout dung han.
                _heartbeatThread = new Thread(HeartbeatLoop) { IsBackground = true, Name = "TCP-Heartbeat" };
                _heartbeatThread.Start();

                NetworkEvents.RaiseConnected();
                return true;
            }
            catch (Exception ex)
            {
                try { _stream?.Close(); } catch { }
                try { _tcpClient?.Close(); } catch { }
                Logger.Error("ClientNetwork", $"Lỗi kết nối: {ex.Message}");
                return false;
            }
        }

        public bool ConnectRelay(string lbHost, int lbPort, string serverId)
        {
            try
            {
                ServerIp = string.IsNullOrWhiteSpace(lbHost) ? "127.0.0.1" : lbHost.Trim();
                ServerTcpPort = lbPort;
                AssignedServerId = serverId ?? "";

                _tcpClient = new TcpClient();
                _tcpClient.NoDelay = true;
                ConnectTcpWithTimeout(_tcpClient, ServerIp, lbPort, ConnectTimeoutMs);

                var rawStream = _tcpClient.GetStream();
                if (!string.IsNullOrWhiteSpace(serverId))
                {
                    // LUONG STREAM: relay preface duoc gui truoc TLS de LoadBalancer biet backend ma client muon den.
                    // Day la du lieu dieu huong ban dau, khong phai packet business cua ung dung.
                    byte[] preface = Encoding.ASCII.GetBytes($"RELAY server={serverId.Trim()}\n");
                    rawStream.Write(preface, 0, preface.Length);
                    rawStream.Flush();
                }

                // LUONG STREAM: sau preface, stream duoc nang cap len TLS de bao ve packet TCP.
                // Tu thoi diem nay tro di, moi lenh auth/room/chat/deu chay tren SslStream.
                var ssl = new SslStream(rawStream, false, (s, cert, chain, err) => true);
                AuthenticateSslWithTimeout(ssl, ConnectTimeoutMs);
                _stream = ssl;
                Logger.Info("ClientNetwork", $"Kết nối LB relay thành công. target={serverId}");

                _running = true;
                _lastHeartbeatReceived = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // XU LY DA LUONG: relay mode van dung thread rieng de doc packet tu stream TLS.
                _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "TCP-Recv" };
                _receiveThread.Start();

                // XU LY DA LUONG: heartbeat doc lap voi receive loop, tranh treo khi server khong phan hoi.
                _heartbeatThread = new Thread(HeartbeatLoop) { IsBackground = true, Name = "TCP-Heartbeat" };
                _heartbeatThread.Start();

                NetworkEvents.RaiseConnected();
                return true;
            }
            catch (Exception ex)
            {
                try { _stream?.Close(); } catch { }
                try { _tcpClient?.Close(); } catch { }
                Logger.Error("ClientNetwork", $"Lỗi kết nối relay: {ex.Message}");
                return false;
            }
        }

        private static void ConnectTcpWithTimeout(TcpClient tcp, string host, int port, int timeoutMs)
        {
            // XU LY BAT DONG BO: dung ConnectAsync ket hop Wait(timeout) de gioi han thoi gian ket noi.
            // Ham Connect() duoc LoginForm goi ben trong Task.Run, nen doan Wait nay khong lam dong bang UI.
            Task connectTask = tcp.ConnectAsync(host, port);
            if (!connectTask.Wait(timeoutMs) || !tcp.Connected)
                throw new TimeoutException($"Timeout connecting to {host}:{port}.");

            if (connectTask.IsFaulted && connectTask.Exception != null)
                throw connectTask.Exception.GetBaseException();
        }

        private static void AuthenticateSslWithTimeout(SslStream ssl, int timeoutMs)
        {
            // MA HOA/TLS + BAT DONG BO: bat tay TLS 1.2 voi server va co timeout de tranh treo vo han.
            // Sau khi thanh cong, _stream tro thanh SslStream nen toan bo packet TCP duoc ma hoa TLS.
            Task authTask = ssl.AuthenticateAsClientAsync("DrawingServer", null, SslProtocols.Tls12, false);
            if (!authTask.Wait(timeoutMs) || !ssl.IsAuthenticated)
                throw new TimeoutException("Timeout during TLS handshake.");

            if (authTask.IsFaulted && authTask.Exception != null)
                throw authTask.Exception.GetBaseException();
        }

        public async System.Threading.Tasks.Task<bool> ReconnectToRoomOwnerViaLoadBalancerAsync(string roomCode)
        {
            if (string.IsNullOrWhiteSpace(roomCode) ||
                string.IsNullOrWhiteSpace(CurrentUsername) ||
                string.IsNullOrWhiteSpace(_lastPassword))
            {
                return true;
            }

            bool useLoadBalancer = EnvLoader.Get("USE_LOAD_BALANCER_ROUTING", "1") != "0";
            string lbMode = EnvLoader.Get("LOAD_BALANCER_CLIENT_MODE", "relay").Trim().ToLowerInvariant();
            if (!useLoadBalancer || lbMode != "relay")
                return true;

            string lbHost = ServerIp;
            int lbPort = ServerTcpPort > 0 ? ServerTcpPort : EnvLoader.GetInt("LOAD_BALANCER_PORT", 9000);

            ServerRouteInfo route;
            try
            {
                // XU LY BAT DONG BO: hoi LB de lay owner server cua room truoc khi join phong.
                route = await LoadBalancerRouteClient.ResolveAsync(lbHost, lbPort, 2500, roomCode);
            }
            catch (Exception ex)
            {
                Logger.Warning("ClientNetwork", $"Không resolve được room route {roomCode}: {ex.Message}");
                return false;
            }

            if (string.IsNullOrWhiteSpace(route.ServerId) ||
                string.Equals(route.ServerId, AssignedServerId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var loginWait = new System.Threading.Tasks.TaskCompletionSource<bool>();
            Action<LoginResponse> loginHandler = null;
            loginHandler = response =>
            {
                NetworkEvents.OnLoginResponse -= loginHandler;
                loginWait.TrySetResult(response != null && response.IsSuccess);
            };

            Disconnect();
            bool useLbUdpProxy = EnvLoader.Get("LOAD_BALANCER_UDP_PROXY", "0") == "1";
            int lbUdpPort = EnvLoader.GetInt("LOAD_BALANCER_UDP_PORT", 9001);
            PreferTcpRealtime = !useLbUdpProxy;
            SetAssignedServer(lbHost, lbPort, lbUdpPort, route.ServerId);

            if (!ConnectRelay(lbHost, lbPort, route.ServerId))
            {
                NetworkEvents.OnLoginResponse -= loginHandler;
                return false;
            }

            NetworkEvents.OnLoginResponse += loginHandler;
            SendLogin(CurrentUsername, _lastPassword);

            var completed = await System.Threading.Tasks.Task.WhenAny(loginWait.Task, System.Threading.Tasks.Task.Delay(3500));
            if (completed != loginWait.Task)
            {
                NetworkEvents.OnLoginResponse -= loginHandler;
                return false;
            }

            return loginWait.Task.Result;
        }

        private void HeartbeatLoop()
        {
            // XU LY DA LUONG: vong lap nay chay tren TCP-Heartbeat thread nen khong chan receive loop.
            while (_running)
            {
                try
                {
                    for (int i = 0; i < HEARTBEAT_INTERVAL_SEC * 10; i++)
                    {
                        if (!_running) return;
                        Thread.Sleep(100);
                    }

                    if (_running && IsConnected)
                    {
                        SendEmpty(CommandType.HEARTBEAT);
                    }

                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    long timeSinceLastHb = (now - _lastHeartbeatReceived) / 1000;

                    if (timeSinceLastHb > HEARTBEAT_TIMEOUT_SEC)
                    {
                        Logger.Warning("Heartbeat", "Timeout Server. Reconnecting...");
                        _running = false;
                        NetworkEvents.RaiseDisconnected();
                        return;
                    }
                }
                catch
                {
                    if (_running) { _running = false; NetworkEvents.RaiseDisconnected(); }
                }
            }
        }

        public void Disconnect()
        {
            _running = false;
            try { SendEmpty(CommandType.DISCONNECT); } catch { }
            _stream?.Close();
            _tcpClient?.Close();
            NetworkEvents.RaiseDisconnected();
        }

        // ── SEND ────────────────────────────────────────────────

        public void Send(Packet packet)
        {
            if (_stream == null || !IsConnected) return;
            try
            {
                byte[] data = packet.Serialize();
                byte[] lenBytes = BitConverter.GetBytes(data.Length);
                if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                // LUONG STREAM: ghi length-prefix truoc, sau do ghi payload de ben nhan doc dung packet boundary.
                // XU LY DA LUONG: khoa stream de nhieu thread khong ghi xen ke lam vo packet.
                lock (_stream)
                {
                    if (_stream == null || !_running) return;
                    _stream.Write(lenBytes, 0, 4);
                    _stream.Write(data, 0, data.Length);
                    _stream.Flush();
                }
            }
            catch
            {
                if (_running) { _running = false; NetworkEvents.RaiseDisconnected(); }
            }
        }

        public void Send(CommandType cmd, object payload) => Send(PacketHelper.Create(cmd, payload));
        public void SendEmpty(CommandType cmd) => Send(PacketHelper.CreateEmpty(cmd));

        // ── GỬI TÍN HIỆU CÁC TÍNH NĂNG ──────────────────────────

        public void SendLogin(string username, string password)
        {
            // AUTH FLOW - BUOC 3B: luu username/password hien tai de sau nay co the reconnect room owner qua LB.
            CurrentUsername = username;
            _lastPassword = password ?? "";
            // AUTH FLOW - BUOC 4B: tao LOGIN packet va gui qua stream TCP/TLS da ket noi.
            Send(CommandType.LOGIN, new LoginPayload { Username = username, Password = password });
        }
        public void SendRegister(string username, string password)
        {
            // AUTH FLOW - BUOC 4A: tao REGISTER packet tu username/password va gui qua stream TCP/TLS.
            Send(CommandType.REGISTER, new RegisterPayload { Username = username, Password = password });
        }
        public void SendCreateRoom(int canvasWidth = 1920, int canvasHeight = 1080) => Send(CommandType.CREATE_ROOM, new CreateRoomPayload { CanvasWidth = canvasWidth, CanvasHeight = canvasHeight });
        public void SendJoinRoom(string roomCode, bool isSpectator = false)
        {
            CurrentRoomCode = roomCode;
            Send(CommandType.JOIN_ROOM, new JoinRoomPayload { RoomCode = roomCode, IsSpectator = isSpectator });
        }
        public void SendLeaveRoom() { SendEmpty(CommandType.LEAVE_ROOM); CurrentRoomCode = null; }

        public void SendSyncBoard() => Send(CommandType.SYNC_BOARD, new { RoomCode = CurrentRoomCode });
        public void SendUndo(string actionId) => Send(CommandType.UNDO, new UndoPayload { ActionID = actionId, Username = CurrentUsername });
        public void SendRedo(string actionId) => Send(CommandType.REDO, new RedoPayload { ActionID = actionId, Username = CurrentUsername });
        public void SendChat(string message, int colorArgb = 0) => Send(CommandType.CHAT, new ChatPayload { Username = CurrentUsername, ColorARGB = colorArgb, Message = message, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        public void SendCursorRealtime(CursorPayload payload) => Send(CommandType.CURSOR, payload);
        public void SendDrawRealtime(DrawPayload payload) => Send(CommandType.DRAW, payload);
        public void SendFloodFillRealtime(FloodFillPayload payload) => Send(CommandType.FLOOD_FILL, payload);
        public void SendTextRealtime(DrawPayload payload) => Send(CommandType.TEXT, payload);
        public void SendSticker(StickerPayload payload) => Send(CommandType.STICKER, payload);
        public void SendStickyNote(StickyNotePayload payload) => Send(CommandType.STICKY_NOTE, payload);
        public void SendReaction(ReactionPayload payload) => Send(CommandType.REACTION, payload);
        public void SendTurnChange(TurnBasedPayload payload) => Send(CommandType.TURN_CHANGE, payload);
        public void RequestSnapshotList() => Send(CommandType.SNAPSHOT_LIST, new SnapshotListPayload { RoomCode = CurrentRoomCode });
        public void RequestSnapshotRestore(int snapshotId) => Send(CommandType.SNAPSHOT_RESTORE, new SnapshotRestorePayload { RoomCode = CurrentRoomCode, SnapshotID = snapshotId });
        public void RequestSnapshotData(int snapshotId) => Send(CommandType.SNAPSHOT_DATA, new SnapshotDataPayload { RoomCode = CurrentRoomCode, SnapshotID = snapshotId });
        public void SendGetGallery() => Send(CommandType.GET_GALLERY, new GetGalleryPayload { RoomCode = CurrentRoomCode });
        public void SendSaveGallery(string filename, string imageData, string thumbnailData) => Send(CommandType.SAVE_TO_GALLERY, new SaveGalleryPayload { RoomCode = CurrentRoomCode, Username = CurrentUsername, Filename = filename, ImageData = imageData, ThumbnailData = thumbnailData });

        // ── RECEIVE LOOP BỌC THÉP ───────────────────────────────

        private void ReceiveLoop()
        {
            // LUONG STREAM: ReceiveLoop doc lien tuc tu SslStream theo kieu length-prefix 4 byte + payload.
            // Khi nhan xong packet, no goi ProcessPacket de phat event sang UI/network layer.
            // XU LY DA LUONG: thread nay chi doc stream, khong cham vao UI thread.
            byte[] lenBuf = new byte[4];
            while (_running)
            {
                try
                {
                    ReadExact(lenBuf, 4);
                    if (BitConverter.IsLittleEndian) Array.Reverse(lenBuf);

                    int packetLen = BitConverter.ToInt32(lenBuf, 0);
                    if (packetLen <= 0 || packetLen > 50 * 1024 * 1024) break; // Chặn rác tránh tràn RAM

                    byte[] packetBuf = new byte[packetLen];
                    ReadExact(packetBuf, packetLen);

                    var packet = Packet.Deserialize(packetBuf);
                    ProcessPacket(packet);
                }
                catch (IOException) when (!_running) { break; }
                catch (Exception) { if (_running) NetworkEvents.RaiseDisconnected(); break; }
            }
        }

        private void ReadExact(byte[] buffer, int count)
        {
            // LUONG STREAM: doc dung so byte yeu cau, co the lap nhieu lan neu TCP cap du lieu theo tung doan nho.
            int total = 0;
            while (total < count)
            {
                int read = _stream.Read(buffer, total, count - total);
                if (read == 0) throw new IOException("Server đóng kết nối.");
                total += read;
            }
        }

        private void ProcessPacket(Packet p)
        {
            if (p.Cmd != CommandType.HEARTBEAT)
                _lastHeartbeatReceived = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // BỌC TRY-CATCH ĐỂ 1 LỆNH LỖI KHÔNG LÀM CHẾT TOÀN BỘ APP
            try
            {
                switch (p.Cmd)
                {
                    case CommandType.HEARTBEAT:
                        _lastHeartbeatReceived = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        break;

                    case CommandType.LOGIN_RESPONSE:
                        NetworkEvents.RaiseLoginResponse(PacketHelper.GetPayload<LoginResponse>(p));
                        break;
                    case CommandType.REGISTER_RESPONSE:
                        NetworkEvents.RaiseRegisterResponse(PacketHelper.GetPayload<RegisterResponse>(p));
                        break;
                    case CommandType.CREATE_ROOM_RESPONSE:
                        NetworkEvents.RaiseCreateRoomResponse(PacketHelper.GetPayload<CreateRoomResponse>(p));
                        break;
                    case CommandType.JOIN_ROOM_RESPONSE:
                        var jr = PacketHelper.GetPayload<JoinRoomResponse>(p);
                        if (jr.IsSuccess) CurrentRoomCode = jr.RoomCode;
                        NetworkEvents.RaiseJoinRoomResponse(jr);
                        break;
                    case CommandType.ROOM_MEMBERS:
                        NetworkEvents.RaiseRoomMembersReceived(PacketHelper.GetPayload<RoomMembersPayload>(p));
                        break;
                    case CommandType.USER_JOIN:
                        NetworkEvents.RaiseUserJoined(PacketHelper.GetPayload<UserJoinPayload>(p));
                        break;
                    case CommandType.USER_LEAVE:
                        NetworkEvents.RaiseUserLeft(PacketHelper.GetPayload<UserLeavePayload>(p));
                        break;
                    case CommandType.CANVAS_SIZE:
                        NetworkEvents.RaiseCanvasSizeReceived(PacketHelper.GetPayload<CanvasSizePayload>(p));
                        break;

                    // FIX LỖI ĐỒNG BỘ MẠNH NHẤT TẠI ĐÂY (SYNC_BOARD)
                    case CommandType.SYNC_BOARD:
                        NetworkEvents.RaiseSyncBoardReceived(ParseSyncBoardPacket(p));
                        break;

                    case CommandType.UNDO:
                        NetworkEvents.RaiseUndoReceived(PacketHelper.GetPayload<UndoPayload>(p));
                        break;
                    case CommandType.REDO:
                        NetworkEvents.RaiseRedoReceived(PacketHelper.GetPayload<RedoPayload>(p));
                        break;
                    case CommandType.DRAW:
                    case CommandType.TEXT:
                    case CommandType.SPRAY:
                        NetworkEvents.RaiseDrawReceived(PacketHelper.GetPayload<DrawPayload>(p));
                        break;
                    case CommandType.FLOOD_FILL:
                        NetworkEvents.RaiseFloodFillReceived(PacketHelper.GetPayload<FloodFillPayload>(p));
                        break;
                    case CommandType.SET_BACKGROUND:
                        NetworkEvents.RaiseSetBackgroundReceived(PacketHelper.GetPayload<SetBackgroundPayload>(p));
                        break;
                    case CommandType.CLEAR_ALL:
                        NetworkEvents.RaiseClearAll();
                        break;
                    case CommandType.CHAT:
                        NetworkEvents.RaiseChatReceived(PacketHelper.GetPayload<ChatPayload>(p));
                        break;
                    case CommandType.CURSOR:
                        NetworkEvents.RaiseCursorReceived(PacketHelper.GetPayload<CursorPayload>(p));
                        break;
                    case CommandType.REACTION:
                        NetworkEvents.RaiseReactionReceived(PacketHelper.GetPayload<ReactionPayload>(p));
                        break;
                    case CommandType.STICKER:
                        NetworkEvents.RaiseStickerReceived(PacketHelper.GetPayload<StickerPayload>(p));
                        break;
                    case CommandType.STICKY_NOTE:
                        NetworkEvents.RaiseStickyNoteReceived(PacketHelper.GetPayload<StickyNotePayload>(p));
                        break;
                    case CommandType.SNAPSHOT_LIST:
                        NetworkEvents.RaiseSnapshotListReceived(PacketHelper.GetPayload<SnapshotListPayload>(p));
                        break;
                    case CommandType.SNAPSHOT_DATA:
                    {
                        var snapData = PacketHelper.GetPayload<SnapshotDataPayload>(p);
                        if (snapData != null)
                            NetworkEvents.RaiseSnapshotDataReceived(snapData.SnapshotID, ParseRawActions(snapData.BoardJson ?? "[]"));
                        break;
                    }
                    case CommandType.SET_TURNBASED:
                    case CommandType.TURN_CHANGE:
                        NetworkEvents.RaiseTurnBasedReceived(PacketHelper.GetPayload<TurnBasedPayload>(p));
                        break;
                    case CommandType.SAVE_TO_GALLERY:
                        NetworkEvents.RaiseSaveGalleryResponse(PacketHelper.GetPayload<SaveGalleryResponse>(p));
                        break;
                    case CommandType.GALLERY_RESPONSE:
                        NetworkEvents.RaiseGalleryReceived(PacketHelper.GetPayload<GalleryResponsePayload>(p));
                        break;
                    case CommandType.IMPORT_IMAGE:
                        NetworkEvents.RaiseImportImageReceived(PacketHelper.GetPayload<ImportImagePayload>(p));
                        break;
                    case CommandType.AI_TEXT_TO_IMAGE:
                        NetworkEvents.RaiseAiTextToImageResult(PacketHelper.GetPayload<AiTextToImageResultPayload>(p));
                        break;
                    case CommandType.AI_BG_REMOVED:
                        NetworkEvents.RaiseAiBgRemovedResult(PacketHelper.GetPayload<AiBgRemovedPayload>(p));
                        break;
                    case CommandType.DISCONNECT:
                        _running = false;
                        NetworkEvents.RaiseDisconnected();
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("ClientNetwork", $"Lỗi bỏ qua khi xử lý lệnh {p.Cmd}: {ex.Message}");
            }
        }

        private SyncBoardPayload ParseSyncBoardPacket(Packet packet)
        {
            string jsonSync = Encoding.UTF8.GetString(packet.Payload ?? Array.Empty<byte>());
            if (jsonSync.TrimStart().StartsWith("["))
            {
                return new SyncBoardPayload { Actions = ParseRawActions(jsonSync) };
            }

            var payload = PacketHelper.GetPayload<SyncBoardPayload>(packet) ?? new SyncBoardPayload();
            if (payload.RawActions != null && payload.RawActions.Count > 0)
            {
                payload.Actions = new List<DrawAction>();
                foreach (string rawAction in payload.RawActions)
                {
                    var action = ParseRawAction(rawAction);
                    if (action != null)
                        payload.Actions.Add(action);
                }
            }

            return payload;
        }

        private List<DrawAction> ParseRawActions(string jsonArray)
        {
            var actions = new List<DrawAction>();
            try
            {
                var rawItems = JsonConvert.DeserializeObject<List<Newtonsoft.Json.Linq.JObject>>(jsonArray);
                if (rawItems != null)
                {
                    foreach (var item in rawItems)
                    {
                        var action = ParseRawAction(item);
                        if (action != null)
                            actions.Add(action);
                    }
                }
            }
            catch
            {
                var fallbackActions = JsonConvert.DeserializeObject<List<DrawAction>>(jsonArray);
                if (fallbackActions != null)
                    actions.AddRange(fallbackActions);
            }

            return actions;
        }

        private DrawAction ParseRawAction(string rawAction)
        {
            if (string.IsNullOrWhiteSpace(rawAction))
                return null;

            try
            {
                return ParseRawAction(Newtonsoft.Json.Linq.JObject.Parse(rawAction));
            }
            catch
            {
                return null;
            }
        }

        private DrawAction ParseRawAction(Newtonsoft.Json.Linq.JObject item)
        {
            if (item == null)
                return null;

            string toolType = item["ToolType"]?.ToString() ?? item["toolType"]?.ToString() ?? "";
            if (toolType.Equals("SetBackground", StringComparison.OrdinalIgnoreCase))
            {
                return new DrawAction
                {
                    ActionID = item["ActionID"]?.ToString() ?? item["actionId"]?.ToString() ?? "",
                    Username = item["Username"]?.ToString() ?? item["username"]?.ToString() ?? "",
                    ToolType = "SetBackground",
                    ColorARGB = item["ColorARGB"]?.ToObject<int>() ?? 0,
                    ImageData = item["ImageData"]?.ToString() ?? "",
                    Timestamp = item["Timestamp"]?.ToObject<long>() ?? 0
                };
            }

            if (toolType.Equals("ImportImage", StringComparison.OrdinalIgnoreCase))
            {
                return new DrawAction
                {
                    ActionID = item["ActionID"]?.ToString() ?? item["actionId"]?.ToString() ?? "",
                    Username = item["Username"]?.ToString() ?? item["username"]?.ToString() ?? "",
                    ToolType = "ImportImage",
                    X1 = item["X"]?.ToObject<int>() ?? item["X1"]?.ToObject<int>() ?? 0,
                    Y1 = item["Y"]?.ToObject<int>() ?? item["Y1"]?.ToObject<int>() ?? 0,
                    ImageWidth = item["Width"]?.ToObject<int>() ?? 400,
                    ImageHeight = item["Height"]?.ToObject<int>() ?? 300,
                    ImageData = item["ImageData"]?.ToString() ?? "",
                    IsDeleted = item["IsDeleted"]?.ToObject<bool>() ?? false,
                    Timestamp = item["Timestamp"]?.ToObject<long>() ?? 0
                };
            }

            if (toolType.Equals("Sticker", StringComparison.OrdinalIgnoreCase))
            {
                return new DrawAction
                {
                    ActionID = item["ActionID"]?.ToString() ?? item["actionId"]?.ToString() ?? "",
                    Username = item["Username"]?.ToString() ?? item["username"]?.ToString() ?? "",
                    ToolType = "Sticker",
                    Text = item["StickerID"]?.ToString() ?? item["Text"]?.ToString() ?? "",
                    X1 = item["X"]?.ToObject<int>() ?? item["X1"]?.ToObject<int>() ?? 0,
                    Y1 = item["Y"]?.ToObject<int>() ?? item["Y1"]?.ToObject<int>() ?? 0,
                    ImageWidth = item["Width"]?.ToObject<int>() ?? item["ImageWidth"]?.ToObject<int>() ?? 64,
                    ImageHeight = item["Height"]?.ToObject<int>() ?? item["ImageHeight"]?.ToObject<int>() ?? 64,
                    IsDeleted = item["IsDeleted"]?.ToObject<bool>() ?? false,
                    Timestamp = item["Timestamp"]?.ToObject<long>() ?? 0
                };
            }

            if (toolType.Equals("FloodFill", StringComparison.OrdinalIgnoreCase))
            {
                return new DrawAction
                {
                    ActionID = item["ActionID"]?.ToString() ?? item["actionId"]?.ToString() ?? "",
                    Username = item["Username"]?.ToString() ?? item["username"]?.ToString() ?? "",
                    ToolType = "FloodFill",
                    X1 = item["X"]?.ToObject<int>() ?? item["X1"]?.ToObject<int>() ?? 0,
                    Y1 = item["Y"]?.ToObject<int>() ?? item["Y1"]?.ToObject<int>() ?? 0,
                    ColorARGB = item["ColorARGB"]?.ToObject<int>() ?? 0,
                    Timestamp = item["Timestamp"]?.ToObject<long>() ?? 0
                };
            }

            var dp = item.ToObject<DrawPayload>();
            if (dp == null)
                return null;

            return new DrawAction
            {
                ActionID = dp.ActionID,
                Username = dp.Username,
                ToolType = dp.ToolType,
                X1 = dp.X1,
                Y1 = dp.Y1,
                X2 = dp.X2,
                Y2 = dp.Y2,
                ColorARGB = dp.ColorARGB,
                Thickness = dp.Thickness,
                Text = dp.Text,
                FontName = dp.FontName,
                FontSize = dp.FontSize,
                IsDeleted = dp.IsDeleted,
                Timestamp = dp.Timestamp
            };
        }
    }
}
