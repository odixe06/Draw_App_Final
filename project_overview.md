# Tong quan du an NT106 Drawing App

Ban do file/luong de tim nhanh code cho cac phien lam viec sau. Dong bo voi [features.md](features.md) va [CONTEXT.md](CONTEXT.md).

## 1. Muc tieu & kien truc

Ung dung ve cong tac realtime WinForms (`.NET Framework 4.7.2`), 4 project:

- `DrawingClient`: app WinForms (login, lobby, canvas, chat, gallery, AI, sticker/text/sticky note, snapshot view).
- `DrawingServer`: server TCP/TLS + UDP/AES, quan ly phong, broadcast realtime, luu PostgreSQL, snapshot dinh ky.
- `LoadBalancer`: ingress TCP/UDP, route room-affinity toi backend.
- `SharedLib`: packet/payload, AES, env loader, logger, API config.

Runtime DB: **PostgreSQL tren Neon** (key-value connection string, chuan hoa boi `PostgresConnectionString.Normalize`).

## 2. Cau truc thu muc

| Thu muc/file | Vai tro |
| --- | --- |
| `DrawingClient/` | Client WinForms: `Forms/`, `Drawing/`, `Network/`, `AI/`, `UI/`. |
| `DrawingServer/` | Server: `Network/`, `Services/`, `Services/Database/` (+ `Migrations/`). |
| `LoadBalancer/` | TCP/UDP proxy, ROUTE/RELAY, room-affinity, health check. |
| `SharedLib/` | Contract dung chung: `Packets/`, `Payloads/`, `Security/`, `Config/`, `Logging/`, `AI/`. |
| `NT106Tests/` | Unit/load/security tests (MSTest). |
| `setup/` | Goi demo: scripts PowerShell, binary `setup/apps`, README/checklist. |
| `.env`, `.env.example`, `setup/.env`, `setup/.env.example` | Cau hinh runtime (`.env` that khong commit). |
| `NT106_DrawingApp.sln` | Solution chinh. |

`bin/`, `obj/`, `setup/apps`, `*/local/tmp_build` la output build, khong phai source logic.

## 3. SharedLib

| File | Noi dung chinh |
| --- | --- |
| `Packets/PacketDef.cs` | `CommandType` (enum lenh), `Packet.Serialize/Deserialize` (framing length-prefix). |
| `Packets/PacketHelper.cs` | `Create`, `GetPayload<T>`, `GetRawJson`. |
| `Payloads/AuthPayload.cs` | Login/Register payload + response. |
| `Payloads/RoomPayload.cs` | Create/Join room, members, canvas size. |
| `Payloads/DrawPayload.cs` | `DrawPayload`, `FloodFillPayload`, `ImportImagePayload`, `SetBackgroundPayload`. |
| `Payloads/InteractionPayload.cs` | `CursorPayload`, `ReactionPayload`, `ChatPayload`, `ActivityLogPayload`, `StickerPayload`, `StickyNotePayload`, `TurnBasedPayload`. |
| `Payloads/SyncPayload.cs` | `DrawAction`, `SyncBoardPayload` (chunk), `UndoPayload`, `RedoPayload`, `PlaybackRequestPayload`, `SnapshotInfo`/`SnapshotListPayload`/`SnapshotRestorePayload`/`SnapshotDataPayload` (board JSON cho thumbnail/preview). |
| `Payloads/GalleryPayload.cs` | Gallery save/response/item/public link. |
| `Payloads/AiPayload.cs` | `AiTextToImageRequest/Result`, `AiBgRemovedPayload`. |
| `Payloads/PixelArtPayload.cs` | Pixel art draw/sync. |
| `Security/AesHelper.cs`, `SecurityConfig.cs` | AES cho UDP. |
| `Config/EnvLoader.cs` | `Load/Get/GetRequired/GetInt` (.env; process env uu tien). |
| `Config/PostgresConnectionString.cs` | `Normalize` URI/key-value -> Npgsql (bo `Channel Binding`, them `Timeout`). |
| `Logging/Logger.cs` | Log console/file. |
| `AI/ApiConfig.cs` | Token/model/URL HF (text-to-image), Remove.bg, va cau hinh sketch -> line art Space (`HF_SKETCH_*`: SPACE/API/PROMPT/NEGATIVE/FIDELITY; `DefaultSketchSpace` gan san). |

## 4. DrawingClient

### Forms / UI
| File | Chuc nang |
| --- | --- |
| `Program.cs` | Entry point, load env/logger, bat unhandled exception. |
| `Forms/LoginForm.cs` | Login/register, chon direct/LB, connect async. |
| `Forms/LobbyForm.cs` | Tao/join phong, reconnect owner qua LB truoc khi join. |
| `Forms/MainForm.cs` | UI chinh: toolbar (nut **SmartPen ✨** nhom "Vẽ" + **AiRegion 🪄** nhom "AI"), canvas, chat/members/logs, gallery/AI, **emoji bar**, **dialog snapshot ListView thumbnail + preview lon** (`ShowSnapshotListUI`, `RenderActionsToBitmap` offscreen, `NetworkEvents_OnSnapshotDataReceived`), `RunAiRegionAsync` (sketch→line art), event handlers realtime. |
| `Forms/GalleryForm.cs` | Hien/tai anh gallery. |
| `UI/ToastForm.cs` | Thong bao noi. |
| `UI/CursorLayer.cs` | Lop phu emoji reaction (bay len, mo dan). |

### Drawing engine
| File | Chuc nang |
| --- | --- |
| `Drawing/CanvasManager.cs` | Canvas 1920x1080, render GDI+, pen/shape/eraser/fill/pipette/text/import/sticker/background, object select/move/resize/delete, remote cursor, **cong cu `SmartPen`: bat diem net khi keo (preview), nha chuot -> `StrokeBeautifier.Beautify` -> phat thanh cac doan Pen that (`CommitSmartStroke`)**, **cong cu `AiRegion` keo-tha chon vung (marquee) -> `OnAiRegionSelected` + `RenderRegionToBitmap`**. |
| `Drawing/StrokeBeautifier.cs` | Lam dep net ve tay **cuc bo** (khong AI): dedup -> RDP simplify -> nhan dien hinh (duong thang/tron/elip/chu nhat/tam giac/mui ten, nguong bao thu) -> Catmull-Rom lam muot. Tra ve polyline -> CanvasManager ve thanh net Pen (tay duoc). |
| `Drawing/DrawingTools.cs` | Enum/constant tool. |
| `Drawing/FloodFill.cs` | BFS flood fill. |
| `Drawing/TextTool.cs` | Editor text tren viewport, commit ve toa do canvas. |
| `Drawing/UndoStack.cs` | Stack undo/redo local (bitmap). |

### Network
| File | Chuc nang |
| --- | --- |
| `Network/ClientNetwork.cs` | TCP/TLS, heartbeat, reconnect owner, `Send*` (gom `SendReaction`, `RequestSnapshotList/Restore`, `RequestSnapshotData`), `ReceiveLoop`/`ProcessPacket` (dispatch `SNAPSHOT_DATA` -> `ParseRawActions`). |
| `Network/LoadBalancerRouteClient.cs` | `ResolveAsync` goi `ROUTE`/`ROUTE room=`. |
| `Network/NetworkEvents.cs` | Event hub static, `SafeInvoke`. |
| `Network/UdpManager.cs` | UDP/AES realtime (cursor, endpoint registration). |

### AI
| File | Chuc nang |
| --- | --- |
| `AI/HuggingFaceClient.cs` | `GenerateImageAsync` text-to-image (HF routing). *(doi ten tu StabilityAiClient)* |
| `AI/RemoveBgClient.cs` | Remove.bg xoa nen. |
| `AI/SketchToImageClient.cs` | `AnalyzeSketchAsync` (tagger `process_prompt_analysis`) + `GenerateFromSketchAsync` (HF Space Gradio 4 ControlNet-lineart `sketch2lineart`). `EnsureGdiReadablePng` giai ma ben vung: PNG/JPEG/GIF/BMP/TIFF giu nguyen, WebP/khac -> ImageSharp -> PNG, khong phai anh -> nem loi ro kem trich phan hoi; `ParseSse` theo doi event error/complete + retry 3 lan. Env-configurable (`HF_SKETCH_*`). |

## 5. DrawingServer

| File | Chuc nang |
| --- | --- |
| `Program.cs` | Load env, start TCP/UDP, heartbeat, cross-server sync, **`SnapshotService.Start()`**. |
| `Network/SecureTcpServer.cs` | TCP/TLS protocol, login/room/chat/draw/gallery/AI/timeline-thay-bang-snapshot, broadcast, `SendHistoryToClientAsync` (chunk), `SaveStrokeFastPath`, turn-based; handler `REACTION`, `SNAPSHOT_LIST`, `SNAPSHOT_RESTORE`, `SNAPSHOT_DATA` (tra board JSON cho thumbnail, khong dung `SYNC_BOARD`). |
| `Network/SecureUdpServer.cs` | UDP/AES realtime, endpoint registration, TCP fallback. |
| `Network/ClientSession.cs` | Trang thai client (TCP, SslStream, username, room, UDP endpoint, WriteLock). |
| `Services/RoomService.cs` | Room state RAM, members, owner, turn-based. |
| `Services/AuthService.cs` | Session online RAM, user color, logout. |
| `Services/CrossServerSyncService.cs` | PostgreSQL LISTEN/NOTIFY fallback cross-server. |
| `Services/ServerNodeHeartbeatService.cs` | Upsert `ServerNodes`. |
| `Services/SnapshotService.cs` | **Dinh ky chup snapshot JSON board cac phong active + retention.** |
| `Services/Database/DbManager.cs` | Truy cap PostgreSQL: auth, rooms, draw history, chat, gallery, AI, action stack, pixel art, **snapshot (`SaveSnapshotAsync`/`GetSnapshotListAsync`/`GetSnapshotDataAsync`/`PruneSnapshotsAsync`/`EnsureSnapshotsTableAsync`)**. |
| `Services/Database/StrokePersistenceQueue.cs` | Queue luu `DrawHistory` nen, retry/backoff, pending theo room. |
| `Services/Database/Migrations/*.sql` | 001..006 (006 = re-add `Snapshots`). |
| `database_setup.sql` | Fresh schema (11 bang). |

## 6. LoadBalancer

| File | Chuc nang |
| --- | --- |
| `Program.cs` | Load env, doc `servers.json`/env. |
| `LoadBalancer.cs` | health check TLS, `ROUTE`/`RELAY`, room-affinity (`Rooms.owner_server_id`), proxy stream, UDP proxy. |
| `servers.json` / `servers.example.json` | Backend list. |

## 7. NT106Tests

`SecurityTests.cs` (AES/packet/logger), `EnvLoaderTests.cs`, `PostgresConnectionStringTests.cs` (gom case Neon VerifyFull + drop Channel Binding), `LoadTests.cs` (serialize/encrypt perf, anh lon, concurrent TCP — skip neu server khong chay).

## 8. Luong hoat dong chinh

- **Auth**: `LoginForm` -> `ClientNetwork.SendLogin/Register` -> `SecureTcpServer` -> `DbManager.LoginAsync` (SHA-256, auto-register) -> `LOGIN_RESPONSE` -> `LobbyForm`.
- **Room**: `LobbyForm` -> `CREATE_ROOM`/`JOIN_ROOM`; server `RoomService` + `DbManager.CreateRoomAsync` (gan `owner_server_id`); join gui members/chat/draw history (chunked `SYNC_BOARD`).
- **Ve realtime**: `CanvasManager` callback -> `ClientNetwork.Send*` (TCP) -> server broadcast truoc, `StrokePersistenceQueue` luu DB nen.
- **Emoji reaction**: emoji bar / phim 1-2-3 -> render local + `ClientNetwork.SendReaction` (TCP) -> server broadcast (`REACTION`) -> `CursorLayer.AddEmoji`.
- **Snapshot**: `SnapshotService` chup JSON board dinh ky (retention). Client bam "Xem lai (Snapshot)" -> `SNAPSHOT_LIST` -> dialog. **Thumbnail/preview**: voi moi snapshot client gui `SNAPSHOT_DATA` (tuan tu) -> server tra board JSON -> client render **offscreen** (`CanvasManager` an, `RenderActionHistory` + `RenderRegionToBitmap`) -> ImageList thumbnail + PictureBox preview lon, KHONG dung canvas chinh. Ngoai ra van co "Xem tren canvas chinh" -> `SNAPSHOT_RESTORE` -> `SYNC_BOARD` (view-only); "Ve hien tai" -> `REQUEST_PLAYBACK`.
- **Nhan dien net ve tay -> line art**: nut 🪄 `AiRegion` (nhom toolbar "AI") -> **keo-tha chon vung** -> `CanvasManager.OnAiRegionSelected` -> `MainForm.RunAiRegionAsync` -> chup vung (`RenderRegionToBitmap`) -> tagger `AnalyzeSketchAsync` nhan dien -> dialog sua mo ta -> `GenerateFromSketchAsync` (HF Space `sketch2lineart`, Gradio 4, fidelity 0.6) -> giai ma anh ben vung (`EnsureGdiReadablePng`: WebP→PNG qua ImageSharp, hoac bao loi ro rang) -> `ImportImageAndBroadcast` (sync nhu AI image, command `AI_TEXT_TO_IMAGE`).
- **AI text-to-image / remove-bg**: `HuggingFaceClient`/`RemoveBgClient` -> `ImportImageAndBroadcast` -> server `AI_TEXT_TO_IMAGE`/`AI_BG_REMOVED` persist + broadcast.

## 9. Database (Neon — 11 bang)

`Users`, `Rooms`, `DrawHistory`, `Gallery`, `ChatHistory`, `ActionStack`, `AiResults`, `PixelArtCells`, `ServerNodes`, `RoomEvents`, `Snapshots`.

Schema canonical: `DrawingServer/database_setup.sql`. Migrations 001..006. Server tu tao `Gallery` va `Snapshots` luc runtime (Ensure*TableAsync); cac bang con lai apply bang `database_setup.sql`.

## 10. Tieu chi NT106 (I/O, Database, Thread, Auth, Crypto, Load balancing)

- **I/O File**: `EnvLoader`, `Logger`, `MainForm`/`GalleryForm` (OpenFile/SaveFile, ReadAllBytes).
- **I/O Network TCP/TLS**: `ClientNetwork`, `SecureTcpServer`, `LoadBalancer` (length-prefix, proxy stream).
- **I/O Network UDP/AES**: `UdpManager`, `SecureUdpServer`.
- **I/O HTTP API**: `HuggingFaceClient`, `RemoveBgClient`, `SketchToImageClient`.
- **Database**: `DbManager` (Npgsql), `LoadBalancer` (owner lookup).
- **Da luong**: receive/heartbeat thread (client), `Task.Run` per client (server/LB), `StrokePersistenceQueue` (`BlockingCollection`), timers (cursor/snapshot dwell), `lock`/`SemaphoreSlim`.
- **Sign up/Sign in**: `LoginForm` -> `ClientNetwork` -> `SecureTcpServer` -> `DbManager.LoginAsync` (SHA-256).
- **Crypto**: TLS (TCP), AES (UDP), SHA-256 (password).
- **Load balancing**: `LoadBalancer.cs` room-affinity + health check.
