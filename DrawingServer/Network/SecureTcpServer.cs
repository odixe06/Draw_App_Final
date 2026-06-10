// ============================================================
// DrawingServer/Network/SecureTcpServer.cs — FIX
// Thêm WriteLock (SemaphoreSlim) vào BroadcastToRoomAsync
// và SendPacketToClientAsync để tránh race condition trên SslStream
// Đây là nguyên nhân màu nền và sticker không đồng bộ được
// ============================================================
using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using SharedLib.Packets;
using SharedLib.Payloads;
using SharedLib.Logging;
using DrawingServer.Services;
using DrawingServer.Database;

namespace DrawingServer.Network
{
    public class SecureTcpServer
    {
        private TcpListener _listener = null!;
        private X509Certificate2 _serverCertificate = null!;
        public static ConcurrentDictionary<string, ClientSession> Clients = new ConcurrentDictionary<string, ClientSession>();

        public async Task StartAsync(string pfxPath, string pfxPassword, int tcpPort = 8888)
        {
            try
            {
                _serverCertificate = new X509Certificate2(pfxPath, pfxPassword);
                _listener = new TcpListener(IPAddress.Any, tcpPort);
                _listener.Start();
                Logger.Info("TCP", $"Secure TCP Server dang chay tren port {tcpPort} (TLS 1.2)...");

                while (true)
                {
                    // XU LY BAT DONG BO: server cho client moi bang AcceptTcpClientAsync.
                    // Trong luc chua co client moi, thread se duoc tra ve ThreadPool thay vi bi block cung.
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    client.NoDelay = true;
                    Logger.Info("TCP", $"[+] Client mới: {client.Client.RemoteEndPoint}");
                    // XU LY DA LUONG: moi client duoc xu ly tren mot task rieng de phuc vu nhieu client dong thoi.
                    // Nho vay 1 client dang gui history/anh lon khong chan client khac login/join/draw.
                    _ = Task.Run(() => HandleClientAsync(client));
                }
            }
            catch (Exception ex)
            {
                Logger.Error("TCP", $"Lỗi khởi động Server: {ex.Message}");
            }
        }

        private async Task HandleClientAsync(TcpClient tcpClient)
        {
            string clientId = tcpClient.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();
            ClientSession session = new ClientSession(tcpClient);
            SslStream sslStream = new SslStream(tcpClient.GetStream(), false);

            try
            {
                // MA HOA/TLS: xac thuc TLS server bang certificate .pfx truoc khi doc/ghi packet.
                // Tu day tro di session.SecureStream la SslStream, toan bo payload TCP duoc ma hoa.
                await sslStream.AuthenticateAsServerAsync(_serverCertificate, clientCertificateRequired: false, checkCertificateRevocation: true);
                session.SecureStream = sslStream;
                Clients.TryAdd(clientId, session);

                // Gửi canvas size ngay khi kết nối
                await SendPacketToClientAsync(session, PacketHelper.Create(CommandType.CANVAS_SIZE, new CanvasSizePayload { Width = 1920, Height = 1080 }));

                while (true)
                {
                    byte[] lenBuf = new byte[4];
                    // LUONG STREAM: dung length-prefix 4 byte de phan biet ranh gioi packet tren TCP stream.
                    // TCP la stream lien tuc, nen server phai doc dung do dai truoc khi doc body packet.
                    if (!await ReadExactAsync(sslStream, lenBuf, 4)) break;

                    if (BitConverter.IsLittleEndian) Array.Reverse(lenBuf);
                    int packetLen = BitConverter.ToInt32(lenBuf, 0);
                    if (packetLen <= 0 || packetLen > 50 * 1024 * 1024) break;

                    byte[] packetBuf = new byte[packetLen];
                    if (!await ReadExactAsync(sslStream, packetBuf, packetLen)) break;

                    Packet packet = Packet.Deserialize(packetBuf);

                    switch (packet.Cmd)
                    {
                        case CommandType.HEARTBEAT:
                            await SendPacketToClientAsync(session, PacketHelper.CreateEmpty(CommandType.HEARTBEAT));
                            break;

                        case CommandType.LOGIN:
                            // AUTH FLOW - BUOC 5B (SERVER): nhan LOGIN packet, parse LoginPayload tu JSON payload.
                            var loginData = PacketHelper.GetPayload<LoginPayload>(packet);
                            if (loginData != null)
                            {
                                // AUTH FLOW - BUOC 6B: kiem tra username/password trong PostgreSQL qua DbManager.LoginAsync.
                                var dbResult = await DbManager.LoginAsync(loginData.Username, loginData.Password);
                                // AUTH FLOW - BUOC 7B: tra LOGIN_RESPONSE ve dung client dang gui request.
                                await SendPacketToClientAsync(session, PacketHelper.Create(CommandType.LOGIN_RESPONSE,
                                    new LoginResponse { IsSuccess = dbResult.IsSuccess, Message = dbResult.Message }));
                                // AUTH FLOW - BUOC 8B: neu hop le, gan username vao ClientSession de cac lenh room/chat/draw biet chu so huu.
                                if (dbResult.IsSuccess) session.Username = (loginData.Username ?? "").Trim();
                            }
                            break;

                        case CommandType.REGISTER:
                            // AUTH FLOW - BUOC 5A (SERVER): nhan REGISTER packet, parse RegisterPayload tu JSON payload.
                            var regData = PacketHelper.GetPayload<RegisterPayload>(packet);
                            if (regData != null)
                            {
                                var regResult = await DbManager.RegisterAsync(regData.Username, regData.Password);
                                await SendPacketToClientAsync(session, PacketHelper.Create(CommandType.REGISTER_RESPONSE,
                                    new RegisterResponse { IsSuccess = regResult.IsSuccess, Message = regResult.Message }));
                                Logger.Info("TCP", $"[REGISTER] '{(regData.Username ?? "").Trim()}' -> {(regResult.IsSuccess ? "success" : "failed")}: {regResult.Message}");
                            }
                            break;

                        case CommandType.CREATE_ROOM:
                            var roomData = PacketHelper.GetPayload<CreateRoomPayload>(packet);
                            var createResult = await RoomService.CreateRoomAsync(session.Username ?? "guest", roomData?.CanvasWidth ?? 1920, roomData?.CanvasHeight ?? 1080);
                            await SendPacketToClientAsync(session, PacketHelper.Create(CommandType.CREATE_ROOM_RESPONSE, new CreateRoomResponse
                            {
                                IsSuccess = createResult.Success,
                                RoomCode = createResult.RoomCode,
                                Message = createResult.Success ? "Tạo phòng thành công" : createResult.Message
                            }));
                            break;

                        case CommandType.JOIN_ROOM:
                            var joinData = PacketHelper.GetPayload<JoinRoomPayload>(packet);
                            if (joinData != null)
                            {
                                if (!string.IsNullOrEmpty(session.RoomCode) &&
                                    !string.Equals(session.RoomCode, joinData.RoomCode, StringComparison.OrdinalIgnoreCase))
                                {
                                    try { RoomService.RemoveMemberFromRoom(session.RoomCode, session.Username ?? "unknown"); } catch { }
                                    session.RoomCode = "";
                                    session.UdpEndPoint = null;
                                }

                                bool exists = await DbManager.CheckRoomExistsAsync(joinData.RoomCode);
                                if (exists)
                                {
                                    int currentMembers = RoomService.GetRoomMemberCount(joinData.RoomCode);
                                    int maxMembers = RoomService.GetDefaultMaxMembers();
                                    if (currentMembers >= maxMembers)
                                        exists = false;
                                }
                                await SendPacketToClientAsync(session, PacketHelper.Create(CommandType.JOIN_ROOM_RESPONSE,
                                    new JoinRoomResponse
                                    {
                                        IsSuccess = exists,
                                        RoomCode = joinData.RoomCode,
                                        Message = exists ? "OK" : "Lỗi",
                                        IsRoomOwner = string.Equals(RoomService.GetRoomState(joinData.RoomCode)?.OwnerId, session.Username, StringComparison.OrdinalIgnoreCase)
                                    }));

                                var joinResult = exists
                                    ? await RoomService.TryAddMemberToRoomAsync(joinData.RoomCode, session)
                                    : (false, "Room does not exist");

                                if (joinResult.Item1)
                                {
                                    session.RoomCode = joinData.RoomCode;

                                    try
                                    {
                                        var members = RoomService.GetRoomMembersInfo(joinData.RoomCode);
                                        await SendPacketToClientAsync(session, PacketHelper.Create(CommandType.ROOM_MEMBERS,
                                            new RoomMembersPayload { RoomCode = joinData.RoomCode, Members = members }));

                                        var roomState = RoomService.GetRoomState(joinData.RoomCode);
                                        if (roomState != null && roomState.IsTurnBasedEnabled)
                                        {
                                            await SendPacketToClientAsync(session, PacketHelper.Create(CommandType.SET_TURNBASED,
                                                new TurnBasedPayload
                                                {
                                                    RoomCode = joinData.RoomCode,
                                                    IsEnabled = true,
                                                    ActiveUser = roomState.ActiveDrawingUser
                                                }));
                                        }
                                            
                                        // Broadcast USER_JOIN cho các client khác trong phòng
                                        try
                                        {
                                            var recentChats = await DbManager.GetChatMessagesAsync(joinData.RoomCode, 50);
                                            foreach (var chat in recentChats.OrderBy(c => c.SentAt))
                                            {
                                                await SendPacketToClientAsync(session, PacketHelper.Create(CommandType.CHAT,
                                                    new ChatPayload
                                                    {
                                                        Username = chat.Username,
                                                        Message = chat.Message,
                                                        Timestamp = new DateTimeOffset(chat.SentAt).ToUnixTimeMilliseconds()
                                                    }));
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Logger.Warning("TCP", $"[CHAT HISTORY] Skip for '{session.Username}': {ex.Message}");
                                        }

                                        await BroadcastToRoomAsync(joinData.RoomCode, PacketHelper.Create(CommandType.USER_JOIN,
                                            new UserJoinPayload { Username = session.Username ?? "guest" }), excludeClientId: clientId);
                                        await BroadcastRoomMembersAsync(joinData.RoomCode);
                                    }
                                    catch { }

                                    var history = await DbManager.GetRoomHistoryAsync(joinData.RoomCode);
                                    Logger.Info("TCP", $"[JOIN] '{session.Username}' room={joinData.RoomCode} history={history.Count} strokes");
                                    if (history.Count > 0)
                                    {
                                        await SendHistoryToClientAsync(session, joinData.RoomCode, history);
                                        Logger.Info("TCP", $"[SYNC_BOARD] Gửi {history.Count} strokes cho '{session.Username}'");
                                    }
                                    else
                                    {
                                        Logger.Warning("TCP", $"[JOIN] Không có history cho phòng {joinData.RoomCode} — canvas sẽ trắng");
                                    }
                                    await SendActionStackToClientAsync(session, joinData.RoomCode);
                                }
                            }
                            break;

                        case CommandType.LEAVE_ROOM:
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                string leavingRoom = session.RoomCode;
                                string leavingUsername = session.Username ?? "unknown";
                                var roomStateBeforeLeave = RoomService.GetRoomState(leavingRoom);
                                bool wasActiveTurnUser = roomStateBeforeLeave != null &&
                                    roomStateBeforeLeave.IsTurnBasedEnabled &&
                                    string.Equals(roomStateBeforeLeave.ActiveDrawingUser, leavingUsername, StringComparison.OrdinalIgnoreCase);

                                try { RoomService.RemoveMemberFromRoom(session.RoomCode, leavingUsername); } catch { }
                                await BroadcastToRoomAsync(session.RoomCode,
                                    PacketHelper.Create(CommandType.USER_LEAVE, new UserLeavePayload { Username = leavingUsername }),
                                    excludeClientId: clientId);

                                string autoAdvanceMessage = string.Empty;
                                if (wasActiveTurnUser && RoomService.TryAdvanceTurnAfterMemberRemoval(leavingRoom, leavingUsername, out string nextActiveUser, out bool turnChanged, out autoAdvanceMessage) && turnChanged)
                                {
                                    var turnPacket = PacketHelper.Create(CommandType.TURN_CHANGE, new TurnBasedPayload
                                    {
                                        RoomCode = leavingRoom,
                                        Username = leavingUsername,
                                        IsEnabled = true,
                                        ActiveUser = nextActiveUser
                                    });
                                    await BroadcastToRoomAsync(leavingRoom, turnPacket, excludeClientId: clientId);
                                    PublishCrossServerEvent(leavingRoom, turnPacket, leavingUsername);
                                }

                                await BroadcastRoomMembersAsync(leavingRoom);
                                session.RoomCode = "";
                                session.UdpEndPoint = null;
                                Logger.Info("TCP", $"[LEAVE] '{session.Username}' rời phòng {leavingRoom}");
                            }
                            break;

                        case CommandType.CHAT:
                            var chatData = PacketHelper.GetPayload<ChatPayload>(packet);
                            if (chatData != null && !string.IsNullOrEmpty(session.RoomCode))
                            {
                                chatData.Username = session.Username ?? "unknown";
                                packet.Payload = Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(chatData));
                                try { await DbManager.SaveChatMessageAsync(session.RoomCode, chatData.Username, chatData.Message); } catch { }
                                Logger.Info("TCP", $"[CHAT] '{session.Username}' → room={session.RoomCode} msg='{chatData.Message}'");
                                await BroadcastToRoomAsync(session.RoomCode, packet);
                                PublishCrossServerEvent(session.RoomCode, packet, session.Username ?? "");
                            }
                            else
                            {
                                Logger.Warning("TCP", $"[CHAT] Bỏ qua — Username='{session.Username}' RoomCode='{session.RoomCode}'");
                            }
                            break;

                        // Realtime interaction over TCP fallback.
                        case CommandType.CURSOR:
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                var cursorData = PacketHelper.GetPayload<CursorPayload>(packet) ?? new CursorPayload();
                                cursorData.Username = session.Username ?? "unknown";
                                cursorData.RoomCode = session.RoomCode;
                                packet = PacketHelper.Create(CommandType.CURSOR, cursorData);
                                await BroadcastToRoomAsync(session.RoomCode, packet, excludeClientId: clientId);
                            }
                            break;

                        // Emoji reaction (ephemeral) — broadcast den ca phong, khong luu DB.
                        case CommandType.REACTION:
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                var reactionData = PacketHelper.GetPayload<ReactionPayload>(packet) ?? new ReactionPayload();
                                reactionData.Username = session.Username ?? "unknown";
                                packet = PacketHelper.Create(CommandType.REACTION, reactionData);
                                await BroadcastToRoomAsync(session.RoomCode, packet, excludeClientId: clientId);
                            }
                            break;

                        // Drawing commands are broadcast first, then saved in the background.
                        case CommandType.DRAW:
                        case CommandType.FLOOD_FILL:
                        case CommandType.TEXT:
                        case CommandType.SPRAY:
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                if (IsTurnBlocked(session)) break;

                                string roomCodeForSave = session.RoomCode;
                                string usernameForSave = session.Username ?? "unknown";
                                string actionIdForSave = Guid.NewGuid().ToString();
                                string strokeJsonForSave = "";

                                try
                                {
                                    string drawJson = Encoding.UTF8.GetString(packet.Payload);
                                    var drawObj = Newtonsoft.Json.Linq.JObject.Parse(drawJson);
                                    drawObj["Username"] = usernameForSave;
                                    if (string.IsNullOrWhiteSpace(drawObj["ToolType"]?.ToString()))
                                    {
                                        if (packet.Cmd == CommandType.FLOOD_FILL)
                                            drawObj["ToolType"] = "FloodFill";
                                        else if (packet.Cmd == CommandType.TEXT)
                                            drawObj["ToolType"] = "Text";
                                        else if (packet.Cmd == CommandType.SPRAY)
                                            drawObj["ToolType"] = "Spray";
                                        else
                                            drawObj["ToolType"] = "Pen";
                                    }
                                    string actionId = drawObj["ActionID"]?.ToString() ?? "";
                                    actionId = Guid.TryParse(actionId, out var parsedActionId) ? parsedActionId.ToString() : actionIdForSave;
                                    drawObj["ActionID"] = actionId;
                                    packet.Payload = Encoding.UTF8.GetBytes(drawObj.ToString());
                                    actionIdForSave = actionId;
                                    strokeJsonForSave = drawObj.ToString();
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warning("TCP", $"[DRAW TCP] Khong parse duoc stroke: {ex.Message}");
                                }

                                await BroadcastToRoomAsync(session.RoomCode, packet, excludeClientId: clientId);
                                if (!string.IsNullOrWhiteSpace(strokeJsonForSave))
                                    SaveStrokeFastPath(roomCodeForSave, actionIdForSave, strokeJsonForSave, usernameForSave);
                                PublishCrossServerEvent(session.RoomCode, packet, session.Username ?? "");

                            }
                            break;

                        case CommandType.STICKER:
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                if (IsTurnBlocked(session)) break;

                                // ✅ FIX: Lưu DB với ToolType="Sticker" để client biết cách replay khi reconnect
                                try
                                {
                                    string stickerJson = Encoding.UTF8.GetString(packet.Payload);
                                    var stickerObj = Newtonsoft.Json.Linq.JObject.Parse(stickerJson);
                                    stickerObj["ToolType"] = "Sticker";
                                    string actionId = stickerObj["ActionID"]?.ToString() ?? "";
                                    actionId = string.IsNullOrWhiteSpace(actionId) ? Guid.NewGuid().ToString() : actionId;
                                    stickerObj["ActionID"] = actionId;
                                    packet.Payload = Encoding.UTF8.GetBytes(stickerObj.ToString());
                                    SaveStrokeFastPath(session.RoomCode, actionId, stickerObj.ToString(), session.Username ?? "");
                                }
                                catch { }

                                await BroadcastToRoomAsync(session.RoomCode, packet, excludeClientId: clientId);
                                PublishCrossServerEvent(session.RoomCode, packet, session.Username ?? "");
                            }
                            break;

                        case CommandType.UNDO:
                        case CommandType.REDO:
                        case CommandType.SET_TURNBASED:
                        case CommandType.TURN_CHANGE:
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                if ((packet.Cmd == CommandType.UNDO || packet.Cmd == CommandType.REDO) && IsTurnBlocked(session))
                                    break;

                                if (packet.Cmd == CommandType.UNDO)
                                {
                                    var undoData = PacketHelper.GetPayload<UndoPayload>(packet) ?? new UndoPayload();
                                    undoData.Username = session.Username ?? "";
                                    packet = PacketHelper.Create(CommandType.UNDO, undoData);
                                    await DbManager.SaveActionStackAsync(session.RoomCode, Newtonsoft.Json.JsonConvert.SerializeObject(undoData), isUndo: true);
                                }
                                else if (packet.Cmd == CommandType.REDO)
                                {
                                    var redoData = PacketHelper.GetPayload<RedoPayload>(packet) ?? new RedoPayload();
                                    redoData.Username = session.Username ?? "";
                                    packet = PacketHelper.Create(CommandType.REDO, redoData);
                                    await DbManager.SaveActionStackAsync(session.RoomCode, Newtonsoft.Json.JsonConvert.SerializeObject(redoData), isUndo: false);
                                }

                                if (packet.Cmd == CommandType.SET_TURNBASED)
                                {
                                    var turnData = PacketHelper.GetPayload<TurnBasedPayload>(packet) ?? new TurnBasedPayload();
                                    var roomState = RoomService.GetRoomState(session.RoomCode);
                                    if (roomState == null ||
                                        !string.Equals(roomState.OwnerId, session.Username, StringComparison.OrdinalIgnoreCase))
                                    {
                                        Logger.Warning("TCP", $"[TURN_BASED] Bo qua SET_TURNBASED tu '{session.Username}': only room owner can toggle");
                                        break;
                                    }

                                    if (roomState != null)
                                    {
                                        roomState.IsTurnBasedEnabled = turnData.IsEnabled;
                                        roomState.ActiveDrawingUser = turnData.IsEnabled ? (session.Username ?? "") : "";
                                    }

                                    packet = PacketHelper.Create(CommandType.SET_TURNBASED, new TurnBasedPayload
                                    {
                                        RoomCode = session.RoomCode,
                                        Username = session.Username ?? "",
                                        IsEnabled = turnData.IsEnabled,
                                        ActiveUser = turnData.IsEnabled ? (session.Username ?? "") : ""
                                    });
                                    Logger.Info("TCP", $"[TURN_BASED] {(turnData.IsEnabled ? "ON" : "OFF")} active='{session.Username}' phòng {session.RoomCode}");
                                }
                                else if (packet.Cmd == CommandType.TURN_CHANGE)
                                {
                                    if (!RoomService.TryAdvanceTurn(session.RoomCode, session.Username ?? "", out string nextActiveUser, out string message))
                                    {
                                        Logger.Warning("TCP", $"[TURN_CHANGE] Bỏ qua từ '{session.Username}': {message}");
                                        break;
                                    }

                                    packet = PacketHelper.Create(CommandType.TURN_CHANGE, new TurnBasedPayload
                                    {
                                        RoomCode = session.RoomCode,
                                        Username = session.Username ?? "",
                                        IsEnabled = true,
                                        ActiveUser = nextActiveUser
                                    });
                                    Logger.Info("TCP", $"[TURN_CHANGE] '{session.Username}' chuyển lượt sang '{nextActiveUser}' phòng {session.RoomCode}");
                                }

                                await BroadcastToRoomAsync(session.RoomCode, packet);
                                PublishCrossServerEvent(session.RoomCode, packet, session.Username ?? "");
                            }
                            break;

                        case CommandType.CLEAR_ALL:
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                if (IsTurnBlocked(session)) break;

                                // Xóa lịch sử vẽ trong DB để người join sau không nhận history cũ
                                try
                                {
                                    StrokePersistenceQueue.ClearRoom(session.RoomCode);
                                    await DbManager.ClearRoomHistoryAsync(session.RoomCode);
                                    await DbManager.ClearActionStackAsync(session.RoomCode);
                                }
                                catch { }
                                // Broadcast cho tất cả NGOẠI TRỪ người gửi (họ đã clear local rồi)
                                await BroadcastToRoomAsync(session.RoomCode, packet, excludeClientId: clientId);
                                Logger.Info("TCP", $"[CLEAR_ALL] '{session.Username}' xóa canvas phòng {session.RoomCode}");
                            }
                            break;

                        case CommandType.CANVAS_SIZE:
                            // ✅ Client thay đổi kích thước canvas → broadcast cho cả phòng
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                await BroadcastToRoomAsync(session.RoomCode, packet, excludeClientId: clientId);
                                Logger.Info("TCP", $"[CANVAS_SIZE] Broadcast từ '{session.Username}' phòng {session.RoomCode}");
                            }
                            break;

                        case CommandType.SET_BACKGROUND:
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                if (IsTurnBlocked(session)) break;

                                // ✅ FIX: Lưu màu nền với ToolType="SetBackground" — khi reconnect canvas sẽ đúng màu nền
                                try
                                {
                                    string bgJson = Encoding.UTF8.GetString(packet.Payload);
                                    var bgObj = Newtonsoft.Json.Linq.JObject.Parse(bgJson);
                                    bgObj["ToolType"] = "SetBackground";
                                    string actionId = bgObj["ActionID"]?.ToString() ?? "";
                                    actionId = string.IsNullOrWhiteSpace(actionId) ? Guid.NewGuid().ToString() : actionId;
                                    bgObj["ActionID"] = actionId;
                                    packet.Payload = Encoding.UTF8.GetBytes(bgObj.ToString());
                                    SaveStrokeFastPath(session.RoomCode, actionId, bgObj.ToString(), session.Username ?? "");
                                }
                                catch { }
                                await BroadcastToRoomAsync(session.RoomCode, packet, excludeClientId: clientId);
                                PublishCrossServerEvent(session.RoomCode, packet, session.Username ?? "");
                            }
                            break;

                        case CommandType.IMPORT_IMAGE:
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                if (IsTurnBlocked(session)) break;

                                // ✅ FIX: Lưu ảnh import với ToolType="ImportImage" — khi reconnect ảnh vẫn còn trên canvas
                                try
                                {
                                    string imgJson = Encoding.UTF8.GetString(packet.Payload);
                                    var imgObj = Newtonsoft.Json.Linq.JObject.Parse(imgJson);
                                    imgObj["ToolType"] = "ImportImage";
                                    string actionId = imgObj["ActionID"]?.ToString() ?? "";
                                    actionId = Guid.TryParse(actionId, out var parsedActionId) ? parsedActionId.ToString() : Guid.NewGuid().ToString();
                                    imgObj["ActionID"] = actionId;
                                    packet.Payload = Encoding.UTF8.GetBytes(imgObj.ToString());
                                    SaveStrokeFastPath(session.RoomCode, actionId, imgObj.ToString(), session.Username ?? "");
                                }
                                catch { }
                                await BroadcastToRoomAsync(session.RoomCode, packet, excludeClientId: clientId);
                                PublishCrossServerEvent(session.RoomCode, packet, session.Username ?? "");
                            }
                            break;

                        case CommandType.STICKY_NOTE:
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                await BroadcastToRoomAsync(session.RoomCode, packet, excludeClientId: clientId);
                                PublishCrossServerEvent(session.RoomCode, packet, session.Username ?? "");
                            }
                            break;

                        case CommandType.AI_TEXT_TO_IMAGE:
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                var aiTtiData = PacketHelper.GetPayload<SharedLib.Payloads.AiTextToImageResultPayload>(packet);
                                if (aiTtiData != null)
                                {
                                    PersistAiImageInBackground(
                                        session.RoomCode,
                                        "text_to_image",
                                        aiTtiData.Prompt ?? "",
                                        aiTtiData.ImageData ?? "",
                                        aiTtiData.ActionID,
                                        session.Username ?? "",
                                        aiTtiData.X,
                                        aiTtiData.Y,
                                        aiTtiData.Width,
                                        aiTtiData.Height,
                                        provider: "huggingface",
                                        model: SharedLib.AI.ApiConfig.HuggingFaceImageModel);
                                }
                                await BroadcastToRoomAsync(session.RoomCode, packet, excludeClientId: clientId);
                                PublishCrossServerEvent(session.RoomCode, packet, session.Username ?? "");
                            }
                            break;

                        case CommandType.AI_BG_REMOVED:
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                var aiBgData = PacketHelper.GetPayload<SharedLib.Payloads.AiBgRemovedPayload>(packet);
                                if (aiBgData != null)
                                {
                                    PersistAiImageInBackground(
                                        session.RoomCode,
                                        "bg_removed",
                                        "",
                                        aiBgData.ImageData ?? "",
                                        aiBgData.ActionID,
                                        session.Username ?? "",
                                        aiBgData.X,
                                        aiBgData.Y,
                                        aiBgData.Width,
                                        aiBgData.Height,
                                        provider: "remove.bg",
                                        model: "");
                                }
                                await BroadcastToRoomAsync(session.RoomCode, packet, excludeClientId: clientId);
                                PublishCrossServerEvent(session.RoomCode, packet, session.Username ?? "");
                            }
                            break;

                        case CommandType.SAVE_TO_GALLERY:
                            var galleryData = PacketHelper.GetPayload<SaveGalleryPayload>(packet);
                            if (galleryData != null && !string.IsNullOrEmpty(session.RoomCode))
                            {
                                var (saved, galleryId, publicToken) = await DbManager.SaveGalleryItemAsync(
                                    session.RoomCode, galleryData.Filename, galleryData.ImageData, session.Username ?? "unknown");

                                // Tạo public URL từ token (format: /gallery/<token>)
                                string publicUrl = saved ? $"/gallery/{publicToken}" : "";

                                await SendPacketToClientAsync(session, PacketHelper.Create(CommandType.SAVE_TO_GALLERY,
                                    new SaveGalleryResponse
                                    {
                                        IsSuccess  = saved,
                                        Message    = saved ? "Đã lưu vào Gallery" : "Lỗi lưu Gallery",
                                        GalleryUrl = publicUrl
                                    }));

                                // Nếu lưu thành công, broadcast PUBLIC_GALLERY_LINK cho cả phòng
                                // để ai cũng thấy link chia sẻ mới
                                if (saved)
                                {
                                    await BroadcastToRoomAsync(session.RoomCode,
                                        PacketHelper.Create(CommandType.PUBLIC_GALLERY_LINK,
                                            new PublicGalleryLinkPayload
                                            {
                                                GalleryItemID = galleryId,
                                                PublicToken   = publicToken,
                                                PublicUrl     = publicUrl
                                            }));
                                    Logger.Info("TCP", $"[GALLERY] '{session.Username}' lưu '{galleryData.Filename}' → {publicUrl}");
                                }
                            }
                            break;

                        case CommandType.GET_GALLERY:
                            // Client xin danh sách gallery của phòng (mở tab Gallery)
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                var galleryItems = await DbManager.GetGalleryItemsAsync(session.RoomCode);
                                var galleryResp  = new GalleryResponsePayload
                                {
                                    RoomCode = session.RoomCode,
                                    Items    = galleryItems.Select(g => new GalleryItem
                                    {
                                        ID            = g.Id,
                                        Filename      = g.Filename,
                                        ThumbnailData = g.ImageData,   // client tự resize thumbnail
                                        SavedBy       = g.CreatedBy,
                                        SavedAt       = new DateTimeOffset(g.CreatedAt).ToUnixTimeMilliseconds(),
                                        PublicLink    = string.IsNullOrEmpty(g.PublicToken) ? "" : $"/gallery/{g.PublicToken}"
                                    }).ToList()
                                };
                                await SendPacketToClientAsync(session,
                                    PacketHelper.Create(CommandType.GALLERY_RESPONSE, galleryResp));
                                Logger.Info("TCP", $"[GALLERY] Gửi {galleryItems.Count} ảnh cho '{session.Username}'");
                            }
                            break;

                        case CommandType.PUBLIC_GALLERY_LINK:
                            // Client xin lấy nội dung 1 ảnh theo public token (xem ảnh public)
                            var linkReq = PacketHelper.GetPayload<PublicGalleryLinkPayload>(packet);
                            if (linkReq != null && !string.IsNullOrEmpty(linkReq.PublicToken))
                            {
                                var item = await DbManager.GetPublicGalleryItemAsync(linkReq.PublicToken);
                                if (item.HasValue)
                                {
                                    await SendPacketToClientAsync(session,
                                        PacketHelper.Create(CommandType.PUBLIC_GALLERY_LINK,
                                            new PublicGalleryLinkPayload
                                            {
                                                GalleryItemID = item.Value.Id,
                                                PublicToken   = linkReq.PublicToken,
                                                PublicUrl     = $"/gallery/{linkReq.PublicToken}"
                                            }));
                                }
                                else
                                {
                                    await SendPacketToClientAsync(session,
                                        PacketHelper.Create(CommandType.PUBLIC_GALLERY_LINK,
                                            new PublicGalleryLinkPayload { PublicToken = "", PublicUrl = "" }));
                                }
                            }
                            break;

                        // ── PIXEL ART (TCP) ─────────────────────────────────────────
                        case CommandType.PIXEL_ART_SYNC:
                            // Client gửi PIXEL_ART_SYNC để xin toàn bộ board hiện tại
                            // (thường gọi ngay sau khi join phòng và chuyển sang chế độ Pixel Art)
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                var cells = await DbManager.GetPixelBoardAsync(session.RoomCode);
                                var syncPayload = new SharedLib.Payloads.PixelArtSyncPayload
                                {
                                    RoomCode = session.RoomCode,
                                    GridSize = 32,  // mặc định 32×32; client có thể override
                                    Cells    = cells.Select(c => new SharedLib.Payloads.PixelCell
                                    {
                                        Row      = c.Row,
                                        Col      = c.Col,
                                        ColorARGB = c.ColorArgb
                                    }).ToList()
                                };
                                await SendPacketToClientAsync(session,
                                    PacketHelper.Create(CommandType.PIXEL_ART_SYNC, syncPayload));
                                Logger.Info("TCP", $"[PIXEL_ART] Gửi {cells.Count} ô cho '{session.Username}' (phòng {session.RoomCode})");
                            }
                            break;

                        case CommandType.REQUEST_PLAYBACK:
                            // Client yêu cầu phát lại toàn bộ lịch sử vẽ của phòng
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                var pbHistory = await DbManager.GetRoomHistoryAsync(session.RoomCode);
                                if (pbHistory.Count > 0)
                                {
                                    // Gửi lại dưới dạng SYNC_BOARD để client replay (tái sử dụng handler có sẵn)
                                    string pbJson = "[" + string.Join(",", pbHistory) + "]";
                                    await SendPacketToClientAsync(session,
                                        new Packet { Cmd = CommandType.SYNC_BOARD, Payload = Encoding.UTF8.GetBytes(pbJson) });
                                    Logger.Info("TCP", $"[PLAYBACK] Gửi {pbHistory.Count} stroke cho '{session.Username}' phòng {session.RoomCode}");
                                }
                                else
                                {
                                    Logger.Info("TCP", $"[PLAYBACK] Không có history cho phòng {session.RoomCode}");
                                }
                            }
                            break;

                        case CommandType.SNAPSHOT_LIST:
                            // Client mo panel "Xem lai" -> liet ke cac snapshot (checkpoint) cua phong
                            if (!string.IsNullOrEmpty(session.RoomCode))
                            {
                                var snaps = await DbManager.GetSnapshotListAsync(session.RoomCode);
                                var listPayload = new SharedLib.Payloads.SnapshotListPayload { RoomCode = session.RoomCode };
                                foreach (var s in snaps)
                                {
                                    listPayload.Snapshots.Add(new SharedLib.Payloads.SnapshotInfo
                                    {
                                        SnapshotID = s.Id,
                                        Timestamp = new DateTimeOffset(s.TakenAt.ToUniversalTime()).ToUnixTimeMilliseconds(),
                                        ThumbnailBase64 = s.Thumbnail ?? ""
                                    });
                                }
                                await SendPacketToClientAsync(session,
                                    PacketHelper.Create(CommandType.SNAPSHOT_LIST, listPayload));
                                Logger.Info("TCP", $"[SNAPSHOT] Gui {listPayload.Snapshots.Count} snapshot cho '{session.Username}' phong {session.RoomCode}");
                            }
                            break;

                        case CommandType.SNAPSHOT_RESTORE:
                            // Client chon mot snapshot -> xem lai trang thai cu (view-only, chi gui cho client nay)
                            var restoreReq = PacketHelper.GetPayload<SharedLib.Payloads.SnapshotRestorePayload>(packet);
                            if (restoreReq != null && !string.IsNullOrEmpty(session.RoomCode))
                            {
                                string snapJson = await DbManager.GetSnapshotDataAsync(restoreReq.SnapshotID);
                                if (!string.IsNullOrWhiteSpace(snapJson))
                                {
                                    // Tai su dung SYNC_BOARD: client se render trang thai snapshot trong che do xem lai
                                    await SendPacketToClientAsync(session,
                                        new Packet { Cmd = CommandType.SYNC_BOARD, Payload = Encoding.UTF8.GetBytes(snapJson) });
                                    Logger.Info("TCP", $"[SNAPSHOT] Restore snapshot {restoreReq.SnapshotID} (view-only) cho '{session.Username}'");
                                }
                            }
                            break;

                        case CommandType.SNAPSHOT_DATA:
                            // Client xin board JSON cua mot snapshot de render thumbnail/preview offscreen
                            // (KHONG dung SYNC_BOARD nen khong dung canvas chinh cua client).
                            var dataReq = PacketHelper.GetPayload<SharedLib.Payloads.SnapshotDataPayload>(packet);
                            if (dataReq != null && !string.IsNullOrEmpty(session.RoomCode))
                            {
                                string boardJson = await DbManager.GetSnapshotDataAsync(dataReq.SnapshotID);
                                await SendPacketToClientAsync(session, PacketHelper.Create(CommandType.SNAPSHOT_DATA,
                                    new SharedLib.Payloads.SnapshotDataPayload
                                    {
                                        RoomCode = session.RoomCode,
                                        SnapshotID = dataReq.SnapshotID,
                                        BoardJson = boardJson ?? ""
                                    }));
                            }
                            break;

                        default:
                            Logger.Warning("TCP", $"Bỏ qua lệnh chưa hỗ trợ: {packet.Cmd}");
                            break;
                    }
                }
            }
            catch (Exception ex) { Logger.Warning("TCP", $"Client disconnect: {ex.Message}"); }
            finally
            {
                if (!string.IsNullOrEmpty(session.RoomCode))
                {
                    try { 
                        RoomService.RemoveMemberFromRoom(session.RoomCode, session.Username ?? "unknown");
                        _ = BroadcastToRoomAsync(session.RoomCode, PacketHelper.Create(CommandType.USER_LEAVE, new UserLeavePayload { Username = session.Username ?? "unknown" }), excludeClientId: clientId);
                        _ = BroadcastRoomMembersAsync(session.RoomCode);
                    } catch { }
                }
                try { AuthService.LogoutUser(session.Username ?? "unknown"); } catch { }
                Clients.TryRemove(clientId, out _);
                tcpClient.Close();
                Logger.Info("TCP", $"[-] Client {clientId} đã thoát.");
            }
        }

        // ✅ Gửi packet đến 1 client — dùng WriteLock để tránh race condition
        private async Task SendPacketToClientAsync(ClientSession client, Packet packet)
        {
            if (client?.SecureStream == null || client.IsDisconnected) return;
            byte[] data = packet.Serialize();
            byte[] lenBytes = BitConverter.GetBytes(data.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);

            // LUONG STREAM: khoa ghi tren tung session de nhieu broadcast khong ghi xen ke len cung SslStream.
            await client.WriteLock.WaitAsync();
            try
            {
                // Ghi length truoc, payload sau de client doc lai dung packet boundary.
                await client.SecureStream.WriteAsync(lenBytes, 0, 4);
                await client.SecureStream.WriteAsync(data, 0, data.Length);
                await client.SecureStream.FlushAsync();
            }
            catch (Exception ex)
            {
                client.IsDisconnected = true;
                Logger.Warning("TCP", $"Loi gui den {client.Username}: {ex.Message}");
            }
            finally
            {
                client.WriteLock.Release();
            }
        }

        public static async Task BroadcastPacketToRoomStaticAsync(string roomCode, Packet packet)
        {
            if (string.IsNullOrWhiteSpace(roomCode) || packet == null)
                return;

            foreach (var kvp in Clients)
            {
                ClientSession client = kvp.Value;
                if (client.IsDisconnected)
                    continue;
                if (!string.Equals(client.RoomCode, roomCode, StringComparison.OrdinalIgnoreCase))
                    continue;
                await SendPacketToClientStaticAsync(client, packet);
            }
        }

        public static async Task<int> BroadcastPacketToRoomWithoutUdpEndpointStaticAsync(
            string roomCode,
            Packet packet,
            string excludeUsername = "")
        {
            if (string.IsNullOrWhiteSpace(roomCode) || packet == null)
                return 0;

            int count = 0;
            foreach (var kvp in Clients)
            {
                ClientSession client = kvp.Value;
                if (client.IsDisconnected)
                    continue;
                if (!string.Equals(client.RoomCode, roomCode, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (client.UdpEndPoint != null)
                    continue;
                if (!string.IsNullOrWhiteSpace(excludeUsername) &&
                    string.Equals(client.Username, excludeUsername, StringComparison.OrdinalIgnoreCase))
                    continue;

                await SendPacketToClientStaticAsync(client, packet);
                count++;
            }

            return count;
        }

        private static async Task SendPacketToClientStaticAsync(ClientSession client, Packet packet)
        {
            if (client?.SecureStream == null || client.IsDisconnected)
                return;

            byte[] data = packet.Serialize();
            byte[] lenBytes = BitConverter.GetBytes(data.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);

            // LUONG STREAM: static broadcast cung dung WriteLock de khong ghi xen ke packet tren session nay.
            await client.WriteLock.WaitAsync();
            try
            {
                // Cu ly nay giu nguyen protocol length-prefix giua server va client.
                await client.SecureStream.WriteAsync(lenBytes, 0, 4);
                await client.SecureStream.WriteAsync(data, 0, data.Length);
                await client.SecureStream.FlushAsync();
            }
            catch { client.IsDisconnected = true; }
            finally
            {
                client.WriteLock.Release();
            }
        }

        private static void PublishCrossServerEvent(string roomCode, Packet packet, string username)
        {
            if (string.IsNullOrWhiteSpace(roomCode) || packet == null)
                return;
            if (packet.Cmd == CommandType.DRAW ||
                packet.Cmd == CommandType.FLOOD_FILL ||
                packet.Cmd == CommandType.TEXT ||
                packet.Cmd == CommandType.SPRAY)
                return;
            _ = CrossServerSyncService.PublishEventAsync(roomCode, packet, username ?? "");
        }

        // ✅ Broadcast đến tất cả client trong phòng — mỗi client dùng WriteLock riêng
        private static void SaveStrokeFastPath(string roomCode, string actionId, string strokeJson, string username)
        {
            try
            {
                StrokePersistenceQueue.Enqueue(roomCode, actionId, strokeJson, username ?? "");
            }
            catch (Exception ex)
            {
                Logger.Warning("TCP", $"[DRAW TCP] Khong enqueue duoc stroke: {ex.Message}");
            }
        }

        private static void SaveAiImageStroke(
            string roomCode,
            string actionId,
            string username,
            string imageData,
            int x,
            int y,
            int width,
            int height)
        {
            if (string.IsNullOrWhiteSpace(roomCode) || string.IsNullOrWhiteSpace(imageData))
                return;

            string safeActionId = Guid.TryParse(actionId, out Guid parsedId)
                ? parsedId.ToString()
                : Guid.NewGuid().ToString();

            var imageObj = new Newtonsoft.Json.Linq.JObject
            {
                ["ActionID"] = safeActionId,
                ["Username"] = username ?? "",
                ["ToolType"] = "ImportImage",
                ["X"] = x,
                ["Y"] = y,
                ["Width"] = Math.Max(1, width),
                ["Height"] = Math.Max(1, height),
                ["ImageData"] = imageData,
                ["Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            SaveStrokeFastPath(roomCode, safeActionId, imageObj.ToString(), username ?? "");
        }

        private static void PersistAiImageInBackground(
            string roomCode,
            string aiType,
            string prompt,
            string imageData,
            string actionId,
            string username,
            int x,
            int y,
            int width,
            int height,
            string provider,
            string model)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await DbManager.SaveAiResultAsync(
                        roomCode,
                        aiType,
                        prompt ?? "",
                        imageData ?? "",
                        username ?? "",
                        provider: provider ?? "",
                        model: model ?? "");

                    SaveAiImageStroke(
                        roomCode,
                        actionId,
                        username ?? "",
                        imageData ?? "",
                        x,
                        y,
                        width,
                        height);
                }
                catch (Exception ex)
                {
                    Logger.Warning("TCP", $"[AI] Luu ket qua {aiType} that bai: {ex.Message}");
                }
            });
        }

        private async Task SendActionStackToClientAsync(ClientSession session, string roomCode)
        {
            var actionStack = await DbManager.GetActionStackEntriesAsync(roomCode);
            foreach (var entry in actionStack)
            {
                if (string.IsNullOrWhiteSpace(entry.ActionJson))
                    continue;

                if (entry.IsUndo)
                {
                    var undo = Newtonsoft.Json.JsonConvert.DeserializeObject<UndoPayload>(entry.ActionJson);
                    if (undo != null && !string.IsNullOrWhiteSpace(undo.ActionID))
                        await SendPacketToClientAsync(session, PacketHelper.Create(CommandType.UNDO, undo));
                }
                else
                {
                    var redo = Newtonsoft.Json.JsonConvert.DeserializeObject<RedoPayload>(entry.ActionJson);
                    if (redo != null && !string.IsNullOrWhiteSpace(redo.ActionID))
                        await SendPacketToClientAsync(session, PacketHelper.Create(CommandType.REDO, redo));
                }
            }
        }

        private async Task SendHistoryToClientAsync(ClientSession session, string roomCode, List<string> history)
        {
            const int MaxChunkBytes = 512 * 1024;
            var chunks = new List<List<string>>();
            var current = new List<string>();
            int currentBytes = 0;

            foreach (string rawAction in history)
            {
                string safeAction = string.IsNullOrWhiteSpace(rawAction) ? "{}" : rawAction;
                int actionBytes = Encoding.UTF8.GetByteCount(safeAction) + 2;
                if (current.Count > 0 && currentBytes + actionBytes > MaxChunkBytes)
                {
                    chunks.Add(current);
                    current = new List<string>();
                    currentBytes = 0;
                }

                current.Add(safeAction);
                currentBytes += actionBytes;
            }

            if (current.Count > 0)
                chunks.Add(current);

            int totalChunks = Math.Max(1, chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                var payload = new SyncBoardPayload
                {
                    RoomCode = roomCode,
                    RawActions = chunks[i],
                    IsChunked = totalChunks > 1,
                    ChunkIndex = i,
                    TotalChunks = totalChunks,
                    IsFinalChunk = i == chunks.Count - 1
                };

                await SendPacketToClientAsync(session, PacketHelper.Create(CommandType.SYNC_BOARD, payload));
            }
        }

        private async Task BroadcastToRoomAsync(string roomCode, Packet packet, string excludeClientId = "")
        {
            int count = 0;
            foreach (var kvp in Clients)
            {
                ClientSession client = kvp.Value;
                if (client.IsDisconnected)
                    continue;
                if (client.RoomCode != roomCode) continue;
                if (!string.IsNullOrEmpty(excludeClientId) && kvp.Key == excludeClientId) continue;
                await SendPacketToClientAsync(client, packet);
                count++;
            }
            if (packet.Cmd == CommandType.CHAT)
            {
                Logger.Info("TCP", $"[CHAT BROADCAST] Gửi đến {count} client trong phòng {roomCode}");
                // Debug: log tất cả client và RoomCode của họ
                foreach (var kvp in Clients)
                    Logger.Info("TCP", $"  → Client '{kvp.Value.Username}' RoomCode='{kvp.Value.RoomCode}' excluded={kvp.Key == excludeClientId}");
            }
        }

        private Task BroadcastRoomMembersAsync(string roomCode)
        {
            if (string.IsNullOrWhiteSpace(roomCode))
                return Task.CompletedTask;

            var members = RoomService.GetRoomMembersInfo(roomCode);
            var payload = new RoomMembersPayload
            {
                RoomCode = roomCode,
                Members = members
            };

            return BroadcastToRoomAsync(roomCode, PacketHelper.Create(CommandType.ROOM_MEMBERS, payload));
        }

        private async Task<bool> ReadExactAsync(SslStream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buffer, total, count - total);
                if (read == 0) return false;
                total += read;
            }
            return true;
        }

        private bool IsTurnBlocked(ClientSession session)
        {
            if (session == null || string.IsNullOrEmpty(session.RoomCode))
                return false;

            var roomState = RoomService.GetRoomState(session.RoomCode);
            if (roomState == null || !roomState.IsTurnBasedEnabled)
                return false;

            bool blocked = !string.Equals(roomState.ActiveDrawingUser, session.Username, StringComparison.OrdinalIgnoreCase);
            if (blocked)
                Logger.Info("TCP", $"[TURN_BASED] Chặn thao tác từ '{session.Username}', lượt hiện tại='{roomState.ActiveDrawingUser}'");
            return blocked;
        }
    }
}
