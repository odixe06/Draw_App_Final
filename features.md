# Tinh nang NT106 Drawing App

Danh sach cac tinh nang **dang co** trong du an (sau khi don cac tinh nang da huy). Trang thai: `ACTIVE`.

Quy uoc:
- ✅ Co luong user-facing ro rang trong client/server.
- ⚠️ Da co code nhung can demo that / cau hinh ngoai (API key, Space, ngrok) de xac nhan.

## Nhom A: Cong cu ve

| Trang thai | Tinh nang | File chinh |
| --- | --- | --- |
| ✅ | Canvas GDI+ co dinh 1920x1080, zoom/pan local | `DrawingClient/Drawing/CanvasManager.cs`, `DrawingClient/Forms/MainForm.cs` |
| ✅ | Pen ve tu do | `DrawingClient/Drawing/CanvasManager.cs` |
| ✅ | **Bút thông minh (SmartPen ✨)**: vẽ tay → khi nhả chuột tự **làm mượt mọi nét** (RDP + Catmull-Rom) và **nắn thành hình chuẩn** khi rõ ràng (đường thẳng / tròn-elip / chữ nhật / tam giác / mũi tên). Kết quả là **nét Pen thật** trên lớp pixel → **tẩy được từng phần** như nét thường. Hoàn toàn cục bộ (không AI, không mạng). | `DrawingClient/Drawing/StrokeBeautifier.cs`, `CanvasManager.cs`, `MainForm.cs` |
| ✅ | Line, Rectangle, Circle (giu `Shift` = vuong/tron deu) | `DrawingClient/Drawing/CanvasManager.cs` |
| ✅ | Eraser (xoa lop net, lo nen ben duoi) | `DrawingClient/Drawing/CanvasManager.cs` |
| ✅ | Bang mau, do day net (TrackBar) | `DrawingClient/Forms/MainForm.cs` |
| ✅ | Flood fill BFS | `DrawingClient/Drawing/FloodFill.cs` |
| ✅ | Pipette (hut mau) | `DrawingClient/Drawing/CanvasManager.cs` |
| ✅ | Text tool (luu toa do canvas, keo-tha/resize/Delete) | `DrawingClient/Drawing/TextTool.cs`, `CanvasManager.cs` |
| ✅ | Import anh; keo-tha/resize 4 goc/Delete | `DrawingClient/Forms/MainForm.cs`, `CanvasManager.cs` |
| ✅ | Sticker; keo-tha/resize/Delete | `DrawingClient/Drawing/CanvasManager.cs` |
| ✅ | Sticky note (tao/sua/keo/resize/select/Delete) | `DrawingClient/Forms/MainForm.cs` |
| ✅ | Nen mau / anh nen canvas (`SET_BACKGROUND`) | `DrawingClient/Forms/MainForm.cs`, `CanvasManager.cs` |
| ✅ | Xoa toan bo (`CLEAR_ALL`) | `CanvasManager.cs`, `DrawingServer/Network/SecureTcpServer.cs` |
| ✅ | Chuot/pan viewport (clamp khong lo nen) | `DrawingClient/Forms/MainForm.cs` |
| ✅ | Tool `Mouse`: chon/keo/resize/Delete object | `CanvasManager.cs` |

## Nhom B: Cong tac realtime

| Trang thai | Tinh nang | File chinh |
| --- | --- | --- |
| ✅ | Dong bo net ve realtime (TCP reliable, broadcast truoc, luu DB nen) | `SecureTcpServer.cs`, `StrokePersistenceQueue.cs` |
| ✅ | Cursor realtime (UDP/AES local, TCP fallback relay/ngrok) | `UdpManager.cs`, `SecureUdpServer.cs`, `SecureTcpServer.cs` |
| ✅ | Emoji reaction co ban (👍 ❤️ 😂 😮 🎉 👏), thanh chon canh chat, gui qua TCP | `DrawingClient/Forms/MainForm.cs`, `DrawingClient/UI/CursorLayer.cs`, `SecureTcpServer.cs` |
| ✅ | Chat realtime, luu `ChatHistory`, gui lai 50 tin gan nhat khi join | `MainForm.cs`, `SecureTcpServer.cs`, `DbManager.cs` |
| ✅ | Danh sach thanh vien (`ROOM_MEMBERS`), user join/leave | `MainForm.cs`, `RoomService.cs` |
| ✅ | Undo/redo theo action cua chinh user (`ActionID`/`Username`, luu `ActionStack`) | `MainForm.cs`, `CanvasManager.cs`, `SecureTcpServer.cs`, `DbManager.cs` |
| ✅ | Ve theo luot (turn-based): chu phong bat/tat, chi active user chuyen luot | `MainForm.cs`, `SecureTcpServer.cs`, `SecureUdpServer.cs`, `RoomService.cs` |
| ✅ | Sync board khi join (chunked `SYNC_BOARD`, gop pending queue) | `SecureTcpServer.cs`, `MainForm.cs` |
| ✅ | Toast thong bao noi | `DrawingClient/UI/ToastForm.cs` |

## Nhom C: Snapshot (xem lai trang thai)

| Trang thai | Tinh nang | File chinh |
| --- | --- | --- |
| ✅ | Snapshot dinh ky o server (checkpoint JSON board, co retention) | `DrawingServer/Services/SnapshotService.cs`, `DbManager.cs` |
| ✅ | Xem lai trang thai cu (view-only time-travel): panel liet ke snapshot, chon de xem, nut "Ve hien tai" | `MainForm.cs` (`ShowSnapshotListUI`), `SecureTcpServer.cs` (`SNAPSHOT_LIST`/`SNAPSHOT_RESTORE`) |
| ✅ | **Thumbnail + xem truoc snapshot (render client-side)**: dialog `ListView` LargeIcon co thumbnail tung snapshot + o preview lon; bam de xem ro trang thai canvas luc do (khong dung canvas chinh) | `MainForm.cs` (`RenderActionsToBitmap` offscreen, `NetworkEvents_OnSnapshotDataReceived`), `ClientNetwork.cs` (`RequestSnapshotData`), `SecureTcpServer.cs` (`SNAPSHOT_DATA`), `SyncPayload.cs` (`SnapshotDataPayload`) |

> Ghi chu: snapshot luu dang **JSON action-checkpoint**, dung cho xem lai trang thai cu va lam checkpoint dinh ky. Thumbnail/preview duoc **render client-side offscreen** (mot `CanvasManager` an replay JSON -> bitmap, qua `SNAPSHOT_DATA`); server van headless nen KHONG render raster (`SnapshotInfo.ThumbnailBase64` bo trong). Fast-join bang raster snapshot van la huong nang cap tiep theo.

## Nhom D: AI (Hugging Face)

| Trang thai | Tinh nang | File chinh |
| --- | --- | --- |
| ✅ | Text-to-image (Hugging Face routing) | `DrawingClient/AI/HuggingFaceClient.cs`, `SharedLib/AI/ApiConfig.cs`, `MainForm.cs` |
| ✅ | Remove background (Remove.bg) | `DrawingClient/AI/RemoveBgClient.cs`, `MainForm.cs` |
| ✅ | **Nhan dien net ve tay → line art (ControlNet-lineart)**: nut 🪄 `AiRegion` **nhom toolbar "AI"** → **keo-tha chon vung** quanh hinh → tagger (`process_prompt_analysis`) nhan dien noi dung → dialog cho sua mo ta → Space `tori29umai/sketch2lineart` (Gradio 4, fidelity 0.6) → WebP→PNG (ImageSharp) → chen ket qua **dang object anh AI**. Chay san voi Space mac dinh; doi qua `HF_SKETCH_SPACE`. ZeroGPU co the cold-start/busy. **(Khac SmartPen: cho ra ANH AI, khong phai net tay duoc.)** | `DrawingClient/AI/SketchToImageClient.cs`, `CanvasManager.cs`, `MainForm.cs`, `ApiConfig.cs` |

> Ghi chu (2026-06-10): co **2 cong cu rieng cho net ve tay**: **SmartPen (✨, Nhom A)** lam dep **cuc bo** → net Pen **tay duoc**; va **AiRegion (🪄, Nhom D)** dung **AI** → **anh line art** (object). Giai quyet 2 nhu cau khac nhau, song song.

## Nhom E: Nguoi dung, phong, luu tru

| Trang thai | Tinh nang | File chinh |
| --- | --- | --- |
| ✅ | Dang ky/dang nhap (TCP/TLS, SHA-256) | `LoginForm.cs`, `DbManager.cs` |
| ✅ | Tao/join phong (ma 6 so, gioi han thanh vien) | `LobbyForm.cs`, `RoomService.cs`, `SecureTcpServer.cs` |
| ✅ | Gallery: luu/xem/tai anh (public token) | `GalleryForm.cs`, `DbManager.cs` |
| ✅ | Xuat anh PNG/JPEG (khong watermark) | `MainForm.cs`, `CanvasManager.cs` |
| ⚠️ | Pixel art (payload + server storage co; UI grid 64x64 chua hoan thien) | `PixelArtPayload.cs`, `DbManager.cs` |

## Nhom F: Bao mat & ha tang

| Trang thai | Tinh nang | File chinh |
| --- | --- | --- |
| ✅ | TCP/TLS (SslStream, length-prefix framing, heartbeat) | `ClientNetwork.cs`, `SecureTcpServer.cs` |
| ✅ | UDP/AES cho tin hieu tam thoi | `UdpManager.cs`, `SecureUdpServer.cs`, `AesHelper.cs` |
| ✅ | Server da luong (task/stream lock moi client) | `SecureTcpServer.cs`, `ClientSession.cs` |
| ✅ | PostgreSQL (Neon) — 11 bang active | `DbManager.cs`, `database_setup.sql`, `Migrations/*.sql` |
| ✅ | Load balancer room-affinity (ROUTE/RELAY, health check TLS) | `LoadBalancer/LoadBalancer.cs` |
| ⚠️ | Cross-server sync fallback (LISTEN/NOTIFY) | `CrossServerSyncService.cs` |
| ⚠️ | Demo Internet ngrok / Tailscale | `setup/*.ps1` |

## Da go khoi du an (khong con code)

GIF export, snapshot tu dong cu (da lam lai dang moi o tren), khoa vung ve (claim area), follow user, spotlight, sticky note reply, laser, AI prompt-suggest (Gemini), nhan dien giong noi (voice-to-draw), cac file mang plaintext legacy (`Server.cs`/`UdpServer.cs`/`ClientHandler.cs`/`SecureTcpClient.cs`/`SecureUdpSender.cs`/`SecureUdpReceiver.cs`), `Form1.cs`, `DrawService.cs`, `Class1.cs`.
