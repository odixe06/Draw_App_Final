# NT106 Drawing App

Ung dung ve cong tac thoi gian thuc bang **C# WinForms (.NET Framework 4.7.2)**: nhieu nguoi cung ve tren mot canvas, chat, luu gallery va dung AI (Hugging Face) — theo mo hinh Client–Server, co Load Balancer va PostgreSQL (Neon).

## Kien truc

| Project | Vai tro |
| --- | --- |
| `DrawingClient` | App WinForms: login, lobby, canvas, chat, gallery, AI, sticker/text/sticky note. |
| `DrawingServer` | Server TCP/TLS + UDP/AES: xu ly packet, quan ly phong, broadcast realtime, luu PostgreSQL, snapshot dinh ky. |
| `LoadBalancer` | Ingress/proxy nhieu server: route theo room-affinity, health check, relay TCP, UDP proxy local. |
| `SharedLib` | Dung chung: packet/payload, AES, env loader, logger, API config. |

```
Client(s) -> (ngrok/LAN) -> LoadBalancer -> DrawingServer-1/2 -> PostgreSQL (Neon)
hoac don gian:  Client(s) -> DrawingServer -> PostgreSQL (Neon)
```

## Tinh nang chinh

- **Ve**: pen, line, rect/circle (Shift = vuong/tron), eraser, flood fill, pipette, text, import anh, sticker, sticky note, nen mau/anh; zoom/pan local; canvas co dinh 1920x1080.
- **Cong tac**: dong bo net ve (TCP), cursor realtime (UDP + TCP fallback), chat, danh sach thanh vien, undo/redo theo user, ve theo luot, **emoji reaction**.
- **Snapshot**: server chup checkpoint board dinh ky; panel **"Xem lai"** cho phep xem lai trang thai cu (view-only) roi quay ve hien tai.
- **Bút thông minh (SmartPen)**: vẽ tay → tự **làm mượt** mọi nét và **nắn thành hình chuẩn** (đường thẳng/tròn/elip/chữ nhật/tam giác/mũi tên); kết quả là **nét vẽ thật, tẩy được** (cục bộ, không AI).
- **AI (Hugging Face)**: text-to-image, remove background, và **nhận diện nét vẽ tay → line art** (kéo chọn vùng + prompt → ảnh AI) qua Hugging Face Space.
- **Tai khoan/phong**: dang ky/dang nhap (TLS, SHA-256), tao/join phong, gallery, xuat anh.
- **Bao mat/ha tang**: TLS cho TCP, AES cho UDP, server da luong, PostgreSQL (Neon), load balancer room-affinity.

Chi tiet day du: xem [features.md](features.md). Ban do file/luong: xem [project_overview.md](project_overview.md). Boi canh phien lam viec: xem [CONTEXT.md](CONTEXT.md).

## Cau hinh (.env)

```env
DATABASE_URL=Host=<project>-pooler.<region>.aws.neon.tech; Database=neondb; Username=neondb_owner; Password=<pw>; SSL Mode=VerifyFull; Channel Binding=Require;
SERVER_CERT_PATH=server.pfx
SERVER_CERT_PASSWORD=123456
HF_TOKEN=<hugging_face_token>
HF_IMAGE_MODEL=stabilityai/stable-diffusion-xl-base-1.0
REMOVE_BG_API_KEY=<remove_bg_key>
HF_SKETCH_SPACE=                                   # de trong = Space mac dinh (tori29umai/sketch2lineart)
HF_SKETCH_API=/predict
MAX_ROOM_MEMBERS=5
```

> Connection string Neon (key-value) duoc `SharedLib/Config/PostgresConnectionString.Normalize` chuan hoa (bo `Channel Binding`, them `Timeout`). Cung chap nhan dang URI `postgresql://...`.

## Build & test

```powershell
dotnet restore .\NT106_DrawingApp.sln /p:RestorePackagesConfig=true
dotnet build   .\NT106_DrawingApp.sln -v:minimal
dotnet test    .\NT106Tests\NT106Tests.csproj -v:minimal
```

## Chay demo

```powershell
# 1 server local, khong LoadBalancer
powershell -ExecutionPolicy Bypass -File .\setup\start-local-no-lb.ps1 -StopExisting

# 2 server + LoadBalancer local
powershell -ExecutionPolicy Bypass -File .\setup\start-local-with-lb.ps1 -StopExisting
```

Goi release cho nguoi dung (build vao `setup/apps` + tao zip):

```powershell
powershell -ExecutionPolicy Bypass -File .\setup\package-release.ps1
```
