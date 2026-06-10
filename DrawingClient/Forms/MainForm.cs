using DrawingClient.AI;
using DrawingClient.Drawing;
using DrawingClient.Network;
using DrawingClient.UI;
using SharedLib.AI;
using SharedLib.Packets;
using SharedLib.Payloads;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DrawingClient.Forms
{
    public class MainForm : Form
    {
        private readonly ClientNetwork _network;
        private readonly string _roomCode;
        private bool _isRoomOwner;
        private UdpManager _udpManager;

        private DoubleBufferedPictureBox canvas;
        private Panel toolPanel;
        private Panel userPanel;
        private Panel rightSidebarHost;
        private Panel rightSidebarHandle;
        private CursorLayer cursorLayer;
        private Button btnColorPicker;
        private Button btnBackColor;
        private Button btnBackImage;
        private Button btnClearAll;
        private Button btnTurnMode;
        private Button btnToggleSidebar;
        private TrackBar tbPenWidth;
        private Label lblPenWidth;
        private ToolTip colorToolTip;
        private ColorDialog colorDialog;
        private ListBox lstMembers;
        private RichTextBox rtbChat;
        private ListBox lstLogs;
        private TextBox txtChatInput;
        private readonly HashSet<string> locallyShownChat = new HashSet<string>();
        private readonly Queue<string> locallyShownChatOrder = new Queue<string>();
        private readonly HashSet<string> displayedChat = new HashSet<string>();
        private readonly Queue<string> displayedChatOrder = new Queue<string>();

        private const float ZoomStep = 0.2f;
        private const float ZoomMin = 0.2f;
        private const float ZoomMax = 4f;

        private CanvasManager canvasManager;
        private StickerPickerControl stickerPicker;
        private TurnPanelControl turnPanel;
        private PlaybackPanelControl playbackPanel;
        private readonly Dictionary<string, StickyNoteControl> noteControls = new Dictionary<string, StickyNoteControl>();
        private string selectedStickyNoteId;
        private string selectedStickerId;
        private bool isPlacingSticker;
        private bool isStickyNoteMode;
        private readonly object realtimePointerLock = new object();
        private readonly object remoteCursorLock = new object();
        private readonly Dictionary<string, CursorPayload> pendingRemoteCursors = new Dictionary<string, CursorPayload>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> remoteCursorTimestamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private CursorPayload pendingCursorPayload;
        private bool hasPendingCursor;
        private System.Threading.Timer realtimePointerTimer;
        private System.Windows.Forms.Timer remoteCursorRenderTimer;
        private int isFlushingRealtimePointers;
        private const int RealtimePointerFlushIntervalMs = 12;
        private const int RemoteCursorRenderIntervalMs = 15;
        private readonly List<DrawAction> actionHistory = new List<DrawAction>();
        private readonly HashSet<string> undoneActionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> ownRedoActionIds = new List<string>();
        private bool isReceivingSyncChunks;
        private readonly Dictionary<ToolType, Button> toolButtons = new Dictionary<ToolType, Button>();
        private ToolType selectedToolType = ToolType.Pen;
        private bool isRightSidebarCollapsed;
        private Form stickerPickerPopup;

        public MainForm(ClientNetwork network, string roomCode, bool isRoomOwner = false)
        {
            _network = network;
            _roomCode = roomCode;
            _isRoomOwner = isRoomOwner;

            InitializeUI();
            canvasManager = new CanvasManager(canvas);
            ConfigureCanvasManager();
            SubscribeNetworkEvents();
            SetupUdp();

            this.FormClosed += MainForm_FormClosed;
        }

        public void RegisterUdpEndpoint()
        {
            _ = RegisterUdpEndpointBurstAsync();
        }

        private async Task RegisterUdpEndpointBurstAsync()
        {
            for (int i = 0; i < 5; i++)
            {
                _udpManager?.RegisterEndpoint(_network?.CurrentUsername, _roomCode, _network?.AssignedServerId);
                await Task.Delay(250);
            }
        }

        public void SetRoomOwner(bool isRoomOwner)
        {
            _isRoomOwner = isRoomOwner;
            if (btnTurnMode != null)
                btnTurnMode.Visible = _isRoomOwner;

            if (turnPanel != null)
                turnPanel.SetState(turnPanel.IsEnabled, turnPanel.ActiveUser, CanAdvanceCurrentTurn(turnPanel.IsEnabled, turnPanel.ActiveUser));
        }

        private void ConfigureCanvasManager()
        {
            canvasManager.OnColorPicked = (color) =>
            {
                btnColorPicker.BackColor = color;
                colorToolTip?.SetToolTip(btnColorPicker, $"Mã màu: #{color.R:X2}{color.G:X2}{color.B:X2}");
                ToastForm.ShowToast(this, "Đã hút màu");
            };

            // Cong cu AiRegion: chon xong vung -> nhan dien + ve lai thanh line art (object anh AI).
            canvasManager.OnAiRegionSelected = async (region) => await RunAiRegionAsync(region);

            canvasManager.OnNetworkDrawAction = payload =>
            {
                if (payload == null)
                    return;
                payload.Username = _network?.CurrentUsername;
                RecordAction(ToDrawAction(payload), true);
                _network?.SendDrawRealtime(payload);
            };
            canvasManager.OnNetworkFloodFillAction = payload =>
            {
                if (payload == null)
                    return;
                payload.Username = _network.CurrentUsername;
                RecordAction(ToDrawAction(payload), true);
                _network?.SendFloodFillRealtime(payload);
            };

            canvasManager.OnNetworkTextAction = payload =>
            {
                if (payload == null)
                    return;
                payload.Username = _network.CurrentUsername;
                RecordAction(ToDrawAction(payload), true);
                _network?.SendTextRealtime(payload);
            };

            canvasManager.OnNetworkImportImageAction = payload =>
            {
                if (payload == null)
                    return;

                payload.Username = _network.CurrentUsername;
                RecordAction(ToDrawAction(payload), true);
                _network?.Send(CommandType.IMPORT_IMAGE, payload);
            };

            canvasManager.OnNetworkStickerAction = payload =>
            {
                if (payload == null)
                    return;

                payload.Username = _network.CurrentUsername;
                RecordAction(ToDrawAction(payload), true);
                _network?.SendSticker(payload);
            };
        }

        private void SetupUdp()
        {
            try
            {
                if (_network?.PreferTcpRealtime == true)
                {
                    AppendLog("UDP bo qua trong che do LB relay; cursor dung TCP fallback latest-state.");
                    return;
                }

                string serverIp = string.IsNullOrWhiteSpace(_network?.ServerIp) ? "127.0.0.1" : _network.ServerIp;
                _udpManager = new UdpManager(serverIp, _network?.ServerUdpPort ?? 8889);
                _udpManager.Start();
                AppendLog($"UDP sẵn sàng trên port {_udpManager.LocalPort}, server {serverIp}:{_network?.ServerUdpPort ?? 8889}");
            }
            catch (Exception ex)
            {
                AppendLog($"UDP chưa sẵn sàng: {ex.Message}");
            }
        }



        private void SubscribeNetworkEvents()
        {
            NetworkEvents.OnCursorReceived += NetworkEvents_OnCursorReceived;
            NetworkEvents.OnRoomMembersReceived += NetworkEvents_OnRoomMembersReceived;
            NetworkEvents.OnUserJoined += NetworkEvents_OnUserJoined;
            NetworkEvents.OnUserLeft += NetworkEvents_OnUserLeft;
            NetworkEvents.OnDrawReceived += NetworkEvents_OnDrawReceived;
            NetworkEvents.OnSyncBoardReceived += NetworkEvents_OnSyncBoardReceived;
            NetworkEvents.OnFloodFillReceived += NetworkEvents_OnFloodFillReceived;
            NetworkEvents.OnImportImageReceived += NetworkEvents_OnImportImageReceived;
            NetworkEvents.OnSetBackgroundReceived += NetworkEvents_OnSetBackgroundReceived;
            NetworkEvents.OnClearAllReceived += NetworkEvents_OnClearAllReceived;
            NetworkEvents.OnReactionReceived += NetworkEvents_OnReactionReceived;
            NetworkEvents.OnChatReceived += NetworkEvents_OnChatReceived;
            NetworkEvents.OnActivityLogReceived += NetworkEvents_OnActivityLogReceived;
            NetworkEvents.OnUndoReceived += NetworkEvents_OnUndoReceived;
            NetworkEvents.OnRedoReceived += NetworkEvents_OnRedoReceived;
            NetworkEvents.OnSnapshotListReceived += NetworkEvents_OnSnapshotListReceived;
            NetworkEvents.OnSnapshotDataReceived += NetworkEvents_OnSnapshotDataReceived;
            NetworkEvents.OnStickerReceived += NetworkEvents_OnStickerReceived;
            NetworkEvents.OnStickyNoteReceived += NetworkEvents_OnStickyNoteReceived;
            NetworkEvents.OnTurnBasedReceived += NetworkEvents_OnTurnBasedReceived;
            NetworkEvents.OnSaveGalleryResponse += NetworkEvents_OnSaveGalleryResponse;
            NetworkEvents.OnAiTextToImageResult += NetworkEvents_OnAiTextToImageResult;
            NetworkEvents.OnAiBgRemovedResult += NetworkEvents_OnAiBgRemovedResult;
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            NetworkEvents.OnCursorReceived -= NetworkEvents_OnCursorReceived;
            NetworkEvents.OnRoomMembersReceived -= NetworkEvents_OnRoomMembersReceived;
            NetworkEvents.OnUserJoined -= NetworkEvents_OnUserJoined;
            NetworkEvents.OnUserLeft -= NetworkEvents_OnUserLeft;
            NetworkEvents.OnDrawReceived -= NetworkEvents_OnDrawReceived;
            NetworkEvents.OnSyncBoardReceived -= NetworkEvents_OnSyncBoardReceived;
            NetworkEvents.OnFloodFillReceived -= NetworkEvents_OnFloodFillReceived;
            NetworkEvents.OnImportImageReceived -= NetworkEvents_OnImportImageReceived;
            NetworkEvents.OnSetBackgroundReceived -= NetworkEvents_OnSetBackgroundReceived;
            NetworkEvents.OnClearAllReceived -= NetworkEvents_OnClearAllReceived;
            NetworkEvents.OnReactionReceived -= NetworkEvents_OnReactionReceived;
            NetworkEvents.OnChatReceived -= NetworkEvents_OnChatReceived;
            NetworkEvents.OnActivityLogReceived -= NetworkEvents_OnActivityLogReceived;
            NetworkEvents.OnUndoReceived -= NetworkEvents_OnUndoReceived;
            NetworkEvents.OnRedoReceived -= NetworkEvents_OnRedoReceived;
            NetworkEvents.OnSnapshotListReceived -= NetworkEvents_OnSnapshotListReceived;
            NetworkEvents.OnSnapshotDataReceived -= NetworkEvents_OnSnapshotDataReceived;
            NetworkEvents.OnStickerReceived -= NetworkEvents_OnStickerReceived;
            NetworkEvents.OnStickyNoteReceived -= NetworkEvents_OnStickyNoteReceived;
            NetworkEvents.OnTurnBasedReceived -= NetworkEvents_OnTurnBasedReceived;
            NetworkEvents.OnSaveGalleryResponse -= NetworkEvents_OnSaveGalleryResponse;
            NetworkEvents.OnAiTextToImageResult -= NetworkEvents_OnAiTextToImageResult;
            NetworkEvents.OnAiBgRemovedResult -= NetworkEvents_OnAiBgRemovedResult;
            StopRealtimePointerTimer();
            StopRemoteCursorRenderTimer();
            _udpManager?.Stop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopRealtimePointerTimer();
                StopRemoteCursorRenderTimer();
                _udpManager?.Stop();
            }

            base.Dispose(disposing);
        }

        private void StopRealtimePointerTimer()
        {
            var timer = realtimePointerTimer;
            realtimePointerTimer = null;
            if (timer == null)
                return;

            try { timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            timer.Dispose();
        }

        private void StopRemoteCursorRenderTimer()
        {
            var timer = remoteCursorRenderTimer;
            remoteCursorRenderTimer = null;
            if (timer == null)
                return;

            timer.Stop();
            timer.Dispose();
        }

        private void InitializeUI()
        {
            this.Text = string.IsNullOrWhiteSpace(_roomCode) ? "Draw Together" : $"Draw Together - Room {_roomCode}";
            this.Size = new Size(1360, 840);
            this.MinimumSize = new Size(1024, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Segoe UI", 9F);

            toolPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 112,
                BackColor = Color.FromArgb(245, 246, 248),
                AutoScroll = false,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(4)
            };
            rightSidebarHost = new Panel
            {
                Dock = DockStyle.Right,
                Width = 318,
                BackColor = Color.FromArgb(238, 241, 245),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(0)
            };
            userPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(245, 246, 248),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(6)
            };
            rightSidebarHandle = new Panel
            {
                Dock = DockStyle.Right,
                Width = 24,
                BackColor = Color.FromArgb(227, 232, 238)
            };
            canvas = new DoubleBufferedPictureBox { Dock = DockStyle.Fill, BackColor = Color.White };

            btnToggleSidebar = new Button
            {
                Dock = DockStyle.Fill,
                Text = "◀",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(227, 232, 238),
                ForeColor = Color.FromArgb(45, 52, 64),
                Font = new Font("Segoe UI Symbol", 11F, FontStyle.Bold),
                TabStop = false
            };
            btnToggleSidebar.FlatAppearance.BorderSize = 0;
            btnToggleSidebar.Click += (s, e) => ToggleRightSidebar();
            rightSidebarHandle.Controls.Add(btnToggleSidebar);

            colorDialog = new ColorDialog();
            BuildToolPanel();
            BuildUserPanel();

            this.Controls.Add(canvas);
            this.Controls.Add(rightSidebarHost);
            this.Controls.Add(toolPanel);
            rightSidebarHost.Controls.Add(userPanel);
            rightSidebarHost.Controls.Add(rightSidebarHandle);
            this.KeyPreview = true;
            this.KeyDown += MainForm_KeyDown;
            this.KeyUp += MainForm_KeyUp;

            canvas.TabStop = true;
            canvas.MouseEnter += (s, e) => canvas.Focus();
            canvas.MouseWheel += Canvas_MouseWheel;

            canvas.MouseMove += Canvas_MouseMove_SendCursor;
            canvas.MouseDown += Canvas_MouseDown_Custom;
            canvas.MouseMove += Canvas_MouseMove_Custom;
            canvas.MouseUp += Canvas_MouseUp_Custom;
            canvas.Paint += Canvas_Paint_Custom;

            // XU LY DA LUONG/BAT DONG BO: System.Threading.Timer chay tren ThreadPool.
            // Timer chi gui trang thai cursor moi nhat theo chu ky, tranh gui qua nhieu packet khi MouseMove lien tuc.
            realtimePointerTimer = new System.Threading.Timer(_ => FlushRealtimePointerState(), null, 0, RealtimePointerFlushIntervalMs);
            remoteCursorRenderTimer = new System.Windows.Forms.Timer { Interval = RemoteCursorRenderIntervalMs };
            remoteCursorRenderTimer.Tick += (s, e) => FlushRemoteCursorState();
            remoteCursorRenderTimer.Start();
            cursorLayer = new CursorLayer(canvas);

            this.Shown += (s, e) => canvasManager?.FitToViewport();
            UpdateToolSelectionVisuals(selectedToolType);
        }

        private void BuildToolPanel()
        {
            colorToolTip = new ToolTip();

            toolPanel.Controls.Clear();

            var root = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.Transparent,
                AutoScroll = false
            };
            toolPanel.Controls.Add(root);

            Button btnUndo = CreatePaintActionButton("↶", "Hoàn tác (Ctrl+Z)", 28);
            btnUndo.Click += (s, e) =>
            {
                if (!EnsureCanDraw()) return;
                UndoOwnLastAction();
            };

            Button btnRedo = CreatePaintActionButton("↷", "Làm lại (Ctrl+Y)", 28);
            btnRedo.Click += (s, e) =>
            {
                if (!EnsureCanDraw()) return;
                RedoOwnLastAction();
            };

            Button btnImport = CreatePaintActionButton("📥", "Nhập ảnh", 28);
            btnImport.Click += BtnImport_Click;

            Button btnExport = CreatePaintActionButton("📤", "Xuất ảnh", 28);
            btnExport.Click += BtnExport_Click;

            Button btnGallery = CreatePaintActionButton("🖼", "Mở gallery", 28);
            btnGallery.Click += (s, e) => new GalleryForm(_network).Show(this);

            Button btnSaveGallery = CreatePaintActionButton("★", "Lưu vào gallery", 28);
            btnSaveGallery.Click += (s, e) => SaveCurrentCanvasToGallery();

            Button btnAiTextToDrawing = CreatePaintActionButton("✨", "AI text-to-image", 28, Color.Honeydew);
            btnAiTextToDrawing.Click += BtnAiTextToDrawing_Click;

            Button btnAiRemoveBg = CreatePaintActionButton("✂", "AI remove background", 28, Color.Honeydew);
            btnAiRemoveBg.Click += BtnAiRemoveBackground_Click;

            Button btnToggleChat = CreatePaintActionButton("🗂", "Ẩn/hiện khung chat bên phải", 28);
            btnToggleChat.Click += (s, e) => ToggleRightSidebar();

            Button btnLeaveRoom = CreatePaintActionButton("↩", "Rời phòng", 28, Color.MistyRose);
            btnLeaveRoom.Click += (s, e) =>
            {
                _network?.SendLeaveRoom();
                var lobby = new LobbyForm(_network, _network.CurrentUsername);
                lobby.FormClosed += (fs, fe) => this.Show();
                lobby.Show();
                this.Hide();
            };

            toolButtons.Clear();
            foreach (ToolType toolType in Enum.GetValues(typeof(ToolType)))
            {
                string glyph = GetToolGlyph(toolType);
                string tooltip = GetToolTooltip(toolType);
                Button toolButton = CreatePaintToolButton(glyph, tooltip);
                toolButton.Tag = toolType;
                toolButton.Click += (s, e) => SelectToolFromToolbar((ToolType)((Button)s).Tag);
                toolButtons[toolType] = toolButton;
            }

            btnColorPicker = CreatePaintActionButton("■", "Màu nét", 28);
            btnColorPicker.Click += (s, e) =>
            {
                if (colorDialog.ShowDialog() == DialogResult.OK)
                {
                    canvasManager.CurrentColor = colorDialog.Color;
                    btnColorPicker.BackColor = colorDialog.Color;
                    colorToolTip.SetToolTip(btnColorPicker, $"Mã màu: #{colorDialog.Color.R:X2}{colorDialog.Color.G:X2}{colorDialog.Color.B:X2}");
                }
            };

            btnBackColor = CreatePaintActionButton("▣", "Màu nền", 28);
            btnBackColor.Click += (s, e) =>
            {
                if (colorDialog.ShowDialog() == DialogResult.OK)
                {
                    canvasManager.ChangeBackgroundColor(colorDialog.Color);
                    var payload = new SetBackgroundPayload
                    {
                        ActionID = Guid.NewGuid().ToString(),
                        RoomCode = _roomCode,
                        Username = _network.CurrentUsername,
                        ColorARGB = colorDialog.Color.ToArgb(),
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    RecordAction(ToDrawAction(payload), true);
                    _network?.Send(CommandType.SET_BACKGROUND, payload);
                    if (_network?.PreferTcpRealtime != true)
                        _udpManager?.SendSetBackground(payload);
                    colorToolTip.SetToolTip(btnBackColor, $"Mã màu: #{colorDialog.Color.R:X2}{colorDialog.Color.G:X2}{colorDialog.Color.B:X2}");
                }
            };

            btnBackImage = CreatePaintActionButton("🖼", "Ảnh nền", 28);
            btnBackImage.Click += BtnBackImage_Click;

            tbPenWidth = new TrackBar
            {
                Minimum = 1,
                Maximum = 30,
                Value = 2,
                TickStyle = TickStyle.None,
                Width = 82,
                Height = 24,
                Margin = new Padding(0)
            };
            lblPenWidth = new Label
            {
                Width = 58,
                Height = 24,
                Text = $"{tbPenWidth.Value}px",
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            };
            tbPenWidth.Scroll += (s, e) =>
            {
                canvasManager.PenWidth = tbPenWidth.Value;
                lblPenWidth.Text = $"{tbPenWidth.Value}px";
            };

            Button btnZoomIn = CreatePaintActionButton("＋", "Zoom lớn hơn", 28);
            btnZoomIn.Click += (s, e) => AdjustCanvasZoom(ZoomStep);
            Button btnZoomOut = CreatePaintActionButton("－", "Zoom nhỏ hơn", 28);
            btnZoomOut.Click += (s, e) => AdjustCanvasZoom(-ZoomStep);

            btnClearAll = CreatePaintActionButton("✖", "Xóa toàn bộ", 28, Color.MistyRose);
            btnClearAll.Click += (s, e) =>
            {
                if (!EnsureCanDraw()) return;
                canvasManager.ClearAll();
                actionHistory.Clear();
                undoneActionIds.Clear();
                ownRedoActionIds.Clear();
                _network?.SendEmpty(CommandType.CLEAR_ALL);
            };

            stickerPicker = new StickerPickerControl { Size = new Size(220, 95) };
            stickerPicker.StickerSelected += id =>
            {
                selectedStickerId = id;
                isPlacingSticker = true;
                isStickyNoteMode = false;
                stickerPickerPopup?.Hide();
                ToastForm.ShowToast(this, "Đã chọn sticker, kéo thả trên canvas để đặt");
            };

            Button btnStickerLibrary = CreatePaintActionButton("🏷", "Mở thư viện sticker", 28);
            btnStickerLibrary.Click += (s, e) => ShowStickerPickerPopup(btnStickerLibrary);

            Button btnStickerMode = CreatePaintActionButton("📌", "Bật/tắt chế độ đặt sticker", 28);
            btnStickerMode.Click += (s, e) =>
            {
                isPlacingSticker = !isPlacingSticker;
                isStickyNoteMode = false;
                ToastForm.ShowToast(this, isPlacingSticker ? "Kéo thả trên canvas để đặt kích cỡ sticker" : "Tắt đặt sticker");
            };

            Button btnStickyNote = CreatePaintActionButton("📝", "Thêm ghi chú", 28);
            btnStickyNote.Click += (s, e) =>
            {
                isStickyNoteMode = !isStickyNoteMode;
                isPlacingSticker = false;
                ToastForm.ShowToast(this, isStickyNoteMode ? "Click canvas để tạo ghi chú" : "Tắt tạo ghi chú");
            };

            btnTurnMode = CreatePaintActionButton("⏱", "Bật/tắt vẽ theo lượt", 28);
            btnTurnMode.Visible = _isRoomOwner;
            btnTurnMode.Click += (s, e) =>
            {
                if (!_isRoomOwner)
                {
                    ToastForm.ShowToast(this, "Chỉ chủ phòng mới được bật/tắt vẽ theo lượt");
                    return;
                }

                bool enable = !turnPanel.IsEnabled;
                var payload = new TurnBasedPayload
                {
                    RoomCode = _roomCode,
                    Username = _network.CurrentUsername,
                    IsEnabled = enable,
                    ActiveUser = enable ? _network.CurrentUsername : string.Empty
                };
                ApplyTurnBasedState(payload);
                _network?.Send(CommandType.SET_TURNBASED, payload);
                _udpManager?.SendTurnBased(payload);
            };

            FlowLayoutPanel row1Left;
            FlowLayoutPanel row1Right;
            FlowLayoutPanel row2Left;
            FlowLayoutPanel row2Right;
            FlowLayoutPanel row3Left;
            FlowLayoutPanel row3Right;

            Panel row1 = CreateToolbarCompactRow(out row1Left, out row1Right);
            Panel row2 = CreateToolbarCompactRow(out row2Left, out row2Right);
            Panel row3 = CreateToolbarCompactRow(out row3Left, out row3Right);

            root.Controls.Add(row3);
            root.Controls.Add(row2);
            root.Controls.Add(row1);

            row1Left.Controls.Add(CreateToolbarLabel("Vẽ"));
            AddWrapControl(row1Left, toolButtons[ToolType.Pen]);
            AddWrapControl(row1Left, toolButtons[ToolType.SmartPen]);
            AddWrapControl(row1Left, toolButtons[ToolType.Mouse]);
            AddWrapControl(row1Left, toolButtons[ToolType.Line]);
            AddWrapControl(row1Left, toolButtons[ToolType.Rectangle]);
            AddWrapControl(row1Left, toolButtons[ToolType.Circle]);
            AddWrapControl(row1Left, toolButtons[ToolType.Eraser]);
            AddWrapControl(row1Left, toolButtons[ToolType.FloodFill]);
            AddWrapControl(row1Left, toolButtons[ToolType.Text]);
            AddWrapControl(row1Left, toolButtons[ToolType.Pipette]);

            row1Left.Controls.Add(CreateToolbarLabel("Điều hướng"));
            AddWrapControl(row1Left, btnUndo);
            AddWrapControl(row1Left, btnRedo);
            AddWrapControl(row1Left, btnToggleChat);
            AddWrapControl(row1Left, btnLeaveRoom);

            row2Left.Controls.Add(CreateToolbarLabel("Nền"));
            AddWrapControl(row2Left, btnColorPicker);
            AddWrapControl(row2Left, btnBackColor);
            AddWrapControl(row2Left, btnBackImage);
            row2Left.Controls.Add(CreateToolbarLabel("Nét"));
            row2Left.Controls.Add(tbPenWidth);
            row2Left.Controls.Add(lblPenWidth);
            AddWrapControl(row2Left, btnClearAll);

            row2Left.Controls.Add(CreateToolbarLabel("Canvas"));
            AddWrapControl(row2Left, btnZoomOut);
            AddWrapControl(row2Left, btnZoomIn);

            row3Left.Controls.Add(CreateToolbarLabel("Tệp/Lưu"));
            AddWrapControl(row3Left, btnImport);
            AddWrapControl(row3Left, btnExport);
            AddWrapControl(row3Left, btnGallery);
            AddWrapControl(row3Left, btnSaveGallery);

            row3Left.Controls.Add(CreateToolbarLabel("AI"));
            AddWrapControl(row3Left, btnAiTextToDrawing);
            AddWrapControl(row3Left, btnAiRemoveBg);
            AddWrapControl(row3Left, toolButtons[ToolType.AiRegion]);
            row3Left.Controls.Add(CreateToolbarLabel("Sticker"));
            AddWrapControl(row3Left, btnStickerLibrary);
            AddWrapControl(row3Left, btnStickerMode);
            AddWrapControl(row3Left, btnStickyNote);
            AddWrapControl(row3Left, btnTurnMode);

            NormalizeToolPanelControls();
            UpdateToolSelectionVisuals(selectedToolType);
        }

        private Panel CreateToolbarCompactRow(out FlowLayoutPanel leftPanel, out FlowLayoutPanel rightPanel)
        {
            var row = new Panel
            {
                Dock = DockStyle.Top,
                Height = 34,
                Margin = new Padding(0),
                Padding = new Padding(2, 1, 2, 1),
                BackColor = Color.FromArgb(242, 242, 242)
            };

            rightPanel = CreateToolbarCompactFlow(true);
            rightPanel.Dock = DockStyle.Right;

            leftPanel = CreateToolbarCompactFlow(false);
            leftPanel.Dock = DockStyle.Fill;

            row.Controls.Add(leftPanel);
            row.Controls.Add(rightPanel);
            return row;
        }

        private FlowLayoutPanel CreateToolbarCompactFlow(bool autoSize)
        {
            return new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = false,
                AutoSize = autoSize,
                AutoSizeMode = autoSize ? AutoSizeMode.GrowAndShrink : AutoSizeMode.GrowOnly,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.Transparent
            };
        }

        private Label CreateToolHeader(string text)
        {
            return new Label
            {
                Text = text,
                Height = 24,
                ForeColor = Color.FromArgb(45, 52, 64),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private int PlaceToolHeader(Control control, int y)
        {
            control.Location = new Point(12, y);
            control.Size = new Size(226, 24);
            return y + 28;
        }

        private int PlaceToolControl(Control control, int y, int height = 32)
        {
            control.Location = new Point(12, y);
            control.Size = new Size(226, height);
            return y + height + 7;
        }

        private int PlaceToolPair(Control left, Control right, int y, int leftWidth, int rightWidth, int height = 32)
        {
            left.Location = new Point(12, y);
            left.Size = new Size(leftWidth, height);
            right.Location = new Point(12 + leftWidth + 6, y);
            right.Size = new Size(rightWidth, height);
            return y + height + 7;
        }

        private FlowLayoutPanel CreateToolbarWrapRow()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoScroll = false,
                AutoSize = false,
                Margin = new Padding(0),
                Padding = new Padding(2, 1, 2, 1),
                BackColor = Color.FromArgb(242, 242, 242)
            };
        }

        private Button CreatePaintActionButton(string text, string tooltip, int width, Color? backColor = null)
        {
            var button = new Button
            {
                Text = text,
                Width = width,
                Height = 24,
                FlatStyle = FlatStyle.Flat,
                BackColor = backColor ?? Color.White,
                ForeColor = Color.FromArgb(35, 35, 35),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
                Margin = new Padding(0)
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(196, 196, 196);
            button.FlatAppearance.BorderSize = 1;
            colorToolTip?.SetToolTip(button, tooltip);
            return button;
        }

        private Button CreatePaintToolButton(string icon, string tooltip)
        {
            var button = new Button
            {
                Text = icon,
                Width = 30,
                Height = 24,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(30, 30, 30),
                Font = new Font("Segoe UI Symbol", 9F, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(0)
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(190, 190, 190);
            button.FlatAppearance.BorderSize = 1;
            colorToolTip?.SetToolTip(button, tooltip);
            return button;
        }

        private Label CreateToolbarLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Height = 24,
                Margin = new Padding(4, 5, 4, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(35, 35, 35),
                Font = new Font("Segoe UI", 8.25F, FontStyle.Bold)
            };
        }

        private void AddWrapControl(FlowLayoutPanel panel, Control control)
        {
            if (panel == null || control == null)
                return;

            panel.Controls.Add(control);
            control.Margin = new Padding(2, 4, 2, 0);
        }

        private void AddHostedControl(ToolStrip strip, Control control)
        {
            if (strip == null || control == null)
                return;

            var host = new ToolStripControlHost(control)
            {
                AutoSize = false,
                Width = control.Width,
                Height = Math.Max(24, control.Height),
                Margin = new Padding(1, 0, 1, 0)
            };
            strip.Items.Add(host);
        }

        private void ShowStickerPickerPopup(Control anchor)
        {
            if (anchor == null)
                return;

            if (stickerPickerPopup == null || stickerPickerPopup.IsDisposed)
            {
                stickerPickerPopup = new Form
                {
                    FormBorderStyle = FormBorderStyle.FixedToolWindow,
                    StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = false,
                    TopMost = true,
                    BackColor = Color.White,
                    ClientSize = new Size(230, 105),
                    Text = "Sticker"
                };

                stickerPicker.Dock = DockStyle.Fill;
                stickerPickerPopup.Controls.Add(stickerPicker);
                stickerPickerPopup.Deactivate += (s, e) =>
                {
                    if (stickerPickerPopup != null && !stickerPickerPopup.IsDisposed)
                        stickerPickerPopup.Hide();
                };
            }

            Point screen = anchor.PointToScreen(new Point(0, anchor.Height + 2));
            stickerPickerPopup.Location = screen;
            stickerPickerPopup.Show();
            stickerPickerPopup.BringToFront();
        }

        private Button CreateSymbolButton(string text, string tooltip, int width, int height, Color? backColor = null)
        {
            var button = new Button
            {
                Text = text,
                Width = width,
                Height = height,
                Margin = new Padding(4, 4, 4, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = backColor ?? Color.White,
                ForeColor = Color.FromArgb(45, 52, 64),
                Font = new Font("Segoe UI Symbol", 10F, FontStyle.Regular)
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(205, 210, 216);
            button.FlatAppearance.BorderSize = 1;
            colorToolTip?.SetToolTip(button, tooltip);
            return button;
        }

        private void AddToolbarGroup(FlowLayoutPanel root, string title, params Control[] controls)
        {
            AddToolbarGroup(root, title, (IEnumerable<Control>)controls);
        }

        private void AddToolbarGroup(FlowLayoutPanel root, string title, IEnumerable<Control> controls)
        {
            var group = new Panel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.FromArgb(250, 251, 252),
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(4),
                Padding = new Padding(6)
            };

            var header = new Label
            {
                Text = title,
                AutoSize = false,
                Width = 220,
                Height = 18,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(65, 73, 84),
                Margin = new Padding(0, 0, 0, 4),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var flow = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.Transparent
            };

            foreach (Control control in controls)
            {
                if (control == null)
                    continue;
                flow.Controls.Add(control);
            }

            group.Controls.Add(flow);
            group.Controls.Add(header);
            flow.Location = new Point(6, 24);
            header.Location = new Point(6, 6);
            group.Size = new Size(Math.Max(220, flow.PreferredSize.Width + 12), flow.PreferredSize.Height + 34);
            root.Controls.Add(group);
        }

        private void NormalizeToolPanelControls()
        {
            foreach (Control control in toolPanel.Controls)
            {
                control.Margin = new Padding(0);
            }
        }

        private void SelectToolFromToolbar(ToolType toolType)
        {
            selectedToolType = toolType;
            if (canvasManager != null)
                canvasManager.CurrentTool = toolType;
            if (canvas != null)
                canvas.Cursor = (toolType == ToolType.SmartPen || toolType == ToolType.AiRegion) ? Cursors.Cross : Cursors.Default;
            UpdateToolSelectionVisuals(toolType);
        }

        private void UpdateToolSelectionVisuals(ToolType selectedTool)
        {
            foreach (var entry in toolButtons)
            {
                bool isSelected = entry.Key == selectedTool;
                entry.Value.BackColor = isSelected ? Color.FromArgb(208, 230, 255) : Color.White;
                entry.Value.FlatAppearance.BorderColor = isSelected ? Color.FromArgb(70, 120, 180) : Color.FromArgb(205, 210, 216);
                entry.Value.Font = new Font("Segoe UI", isSelected ? 8.5F : 8.25F, isSelected ? FontStyle.Bold : FontStyle.Regular);
            }
        }

        private string GetToolGlyph(ToolType toolType)
        {
            switch (toolType)
            {
                case ToolType.Pen:
                    return "✏";
                case ToolType.Mouse:
                    return "🖱";
                case ToolType.Line:
                    return "／";
                case ToolType.Rectangle:
                    return "▭";
                case ToolType.Circle:
                    return "◯";
                case ToolType.Eraser:
                    return "⌫";
                case ToolType.FloodFill:
                    return "▨";
                case ToolType.Text:
                    return "A";
                case ToolType.Pipette:
                    return "🧪";
                case ToolType.SmartPen:
                    return "✨";
                case ToolType.AiRegion:
                    return "🪄";
                default:
                    return toolType.ToString();
            }
        }

        private string GetToolTooltip(ToolType toolType)
        {
            switch (toolType)
            {
                case ToolType.Pen:
                    return "Bút vẽ";
                case ToolType.Mouse:
                    return "Chọn / kéo / pan";
                case ToolType.Line:
                    return "Vẽ đường thẳng";
                case ToolType.Rectangle:
                    return "Vẽ hình chữ nhật";
                case ToolType.Circle:
                    return "Vẽ hình tròn";
                case ToolType.Eraser:
                    return "Tẩy";
                case ToolType.FloodFill:
                    return "Tô màu";
                case ToolType.Text:
                    return "Chèn chữ";
                case ToolType.Pipette:
                    return "Hút màu";
                case ToolType.SmartPen:
                    return "Bút thông minh: vẽ tay → tự làm mượt & nắn nét (tẩy được như nét thường)";
                case ToolType.AiRegion:
                    return "AI: kéo-thả chọn vùng → vẽ lại thành line art (ảnh AI)";
                default:
                    return toolType.ToString();
            }
        }

        private void ToggleRightSidebar()
        {
            isRightSidebarCollapsed = !isRightSidebarCollapsed;
            userPanel.Visible = !isRightSidebarCollapsed;
            rightSidebarHost.Width = isRightSidebarCollapsed ? 24 : 318;
            btnToggleSidebar.Text = isRightSidebarCollapsed ? "▶" : "◀";
            btnToggleSidebar.AccessibleDescription = isRightSidebarCollapsed ? "Hiện thanh bên phải" : "Ẩn thanh bên phải";
            canvasManager?.FitToViewport();
            canvas.Invalidate();
        }

        private void AdjustCanvasZoom(float delta, Point? pivot = null)
        {
            if (canvasManager == null)
                return;

            Point zoomPivot = pivot ?? new Point(canvas.ClientSize.Width / 2, canvas.ClientSize.Height / 2);
            canvasManager.ZoomAt(zoomPivot, delta);
        }

        private void BuildUserPanel()
        {
            turnPanel = new TurnPanelControl { Dock = DockStyle.Top };
            turnPanel.NextTurnRequested += HandleNextTurnRequested;
            playbackPanel = new PlaybackPanelControl { Dock = DockStyle.Top };

            // FIX LỖI: Dùng hàm Send tổng quát
            playbackPanel.RequestPlayback += () => _network?.Send(CommandType.REQUEST_PLAYBACK, new PlaybackRequestPayload { RoomCode = _roomCode });
            playbackPanel.RequestSnapshots += () => _network?.RequestSnapshotList();

            TabControl tabs = new TabControl { Dock = DockStyle.Fill };
            TabPage tabMembers = new TabPage("Members");
            TabPage tabChat = new TabPage("Chat");
            TabPage tabLogs = new TabPage("Nhật ký");

            lstMembers = new ListBox { Dock = DockStyle.Fill };
            tabMembers.Controls.Add(lstMembers);

            rtbChat = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                WordWrap = true,
                Multiline = true,
                DetectUrls = false,
                BorderStyle = BorderStyle.FixedSingle,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                BackColor = Color.White,
                ForeColor = Color.Black,
                Font = new Font("Segoe UI", 9F)
            };
            Panel chatBottom = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            txtChatInput = new TextBox { Dock = DockStyle.Fill };
            Button btnSendChat = new Button { Text = "Gửi", Dock = DockStyle.Right, Width = 65 };
            btnSendChat.Click += (s, e) => SendChatMessage();
            txtChatInput.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SendChatMessage();
                }
            };

            chatBottom.Controls.Add(txtChatInput);
            chatBottom.Controls.Add(btnSendChat);

            // Thanh tha cam xuc (emoji reaction) hien thi ngay tren khung chat.
            FlowLayoutPanel emojiBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 34,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(2),
                BackColor = Color.WhiteSmoke
            };
            foreach (string emo in new[] { "👍", "❤️", "😂", "😮", "🎉", "👏" })
            {
                string captured = emo;
                var btnEmoji = new Button
                {
                    Text = emo,
                    Width = 40,
                    Height = 28,
                    Font = new Font("Segoe UI Emoji", 11F),
                    Margin = new Padding(2, 2, 0, 0),
                    FlatStyle = FlatStyle.Standard,
                    TabStop = false
                };
                btnEmoji.Click += (s, e) => SendReactionFromPicker(captured);
                emojiBar.Controls.Add(btnEmoji);
            }

            tabChat.Controls.Add(chatBottom);
            tabChat.Controls.Add(emojiBar);
            tabChat.Controls.Add(rtbChat);

            lstLogs = new ListBox { Dock = DockStyle.Fill };
            tabLogs.Controls.Add(lstLogs);

            tabs.TabPages.Add(tabMembers);
            tabs.TabPages.Add(tabChat);
            tabs.TabPages.Add(tabLogs);

            userPanel.Controls.Add(tabs);
            userPanel.Controls.Add(playbackPanel);
            userPanel.Controls.Add(turnPanel);
        }

        private void Canvas_MouseMove_SendCursor(object sender, MouseEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_network?.CurrentUsername))
                return;

            Point canvasPoint = canvasManager?.ScreenToCanvas(e.Location) ?? e.Location;
            QueueRealtimeCursor(new CursorPayload
            {
                Username = _network.CurrentUsername,
                RoomCode = _roomCode,
                X = canvasPoint.X,
                Y = canvasPoint.Y,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        private void QueueRealtimeCursor(CursorPayload payload)
        {
            if (payload == null)
                return;

            lock (realtimePointerLock)
            {
                pendingCursorPayload = payload;
                hasPendingCursor = true;
            }
        }

        private void FlushRealtimePointerState()
        {
            if (Interlocked.Exchange(ref isFlushingRealtimePointers, 1) == 1)
                return;

            try
            {
                CursorPayload cursor = null;

                lock (realtimePointerLock)
                {
                    if (hasPendingCursor)
                    {
                        cursor = pendingCursorPayload;
                        hasPendingCursor = false;
                    }
                }

                if (cursor != null)
                    SendCursorRealtime(cursor);
            }
            finally
            {
                Interlocked.Exchange(ref isFlushingRealtimePointers, 0);
            }
        }

        private void SendCursorRealtime(CursorPayload payload)
        {
            if (payload == null)
                return;
            if (string.IsNullOrWhiteSpace(payload.Username))
                payload.Username = _network?.CurrentUsername;
            if (string.IsNullOrWhiteSpace(payload.Username))
                return;
            if (string.IsNullOrWhiteSpace(payload.RoomCode))
                payload.RoomCode = _roomCode;
            if (payload.Timestamp <= 0)
                payload.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (_udpManager != null && _network?.PreferTcpRealtime != true)
                _udpManager.SendCursor(payload);
            else
                _network?.SendCursorRealtime(payload);
        }

        private void Canvas_MouseDown_Custom(object sender, MouseEventArgs e)
        {
            canvas.Focus();
            SelectStickyNote(null);

            if (e.Button != MouseButtons.Left) return;

            if (isPlacingSticker || pendingImportImage != null)
            {
                if (!EnsureCanDraw()) return;
                dragStartPoint = e.Location;
            }
            else if (isStickyNoteMode)
            {
                string noteId = Guid.NewGuid().ToString();
                var note = new StickyNoteControl { NoteId = noteId, Author = _network?.CurrentUsername, Location = e.Location };
                note.NoteChanged += StickyNoteChanged;
                note.NoteSelected += StickyNoteSelected;
                canvas.Controls.Add(note);
                note.BringToFront();
                noteControls[noteId] = note;
                SelectStickyNote(note);

                _network?.SendStickyNote(new StickyNotePayload
                {
                    NoteID = noteId,
                    AuthorUsername = _network?.CurrentUsername,
                    X = e.X,
                    Y = e.Y,
                    Width = note.Width,
                    Height = note.Height,
                    Text = note.NoteText,
                    IsOpen = true,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });

                isStickyNoteMode = false;
            }
        }

        private void Canvas_MouseMove_Custom(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && (isPlacingSticker || pendingImportImage != null))
            {
                canvas.Invalidate();
            }
        }

        private void Canvas_MouseUp_Custom(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && isPlacingSticker)
            {
                if (!EnsureCanDraw()) return;

                Point start = canvasManager.ScreenToCanvas(dragStartPoint);
                Point end = canvasManager.ScreenToCanvas(e.Location);
                int width = Math.Max(24, Math.Abs(end.X - start.X));
                int height = Math.Max(24, Math.Abs(end.Y - start.Y));
                int x = Math.Min(start.X, end.X);
                int y = Math.Min(start.Y, end.Y);

                var payload = new StickerPayload
                {
                    ActionID = Guid.NewGuid().ToString(),
                    Username = _network?.CurrentUsername,
                    StickerID = string.IsNullOrWhiteSpace(selectedStickerId) ? "star" : selectedStickerId,
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height,
                    Rotation = 0,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                canvasManager.AddSticker(payload);
                _network?.SendSticker(payload);
                if (_network?.PreferTcpRealtime != true)
                    _udpManager?.SendSticker(payload);

                isPlacingSticker = false;
                canvas.Invalidate();
            }
            else if (e.Button == MouseButtons.Left && pendingImportImage != null)
            {
                if (!EnsureCanDraw()) return;

                Point start = canvasManager.ScreenToCanvas(dragStartPoint);
                Point end = canvasManager.ScreenToCanvas(e.Location);
                int width = Math.Max(50, Math.Abs(end.X - start.X));
                int height = Math.Max(50, Math.Abs(end.Y - start.Y));
                int x = Math.Min(start.X, end.X);
                int y = Math.Min(start.Y, end.Y);
                Rectangle target = new Rectangle(x, y, width, height);

                ImportImageAndBroadcast(pendingImportImage, target, pendingImportAiType, pendingImportPrompt);

                pendingImportImage.Dispose();
                pendingImportImage = null;
                pendingImportAiType = null;
                pendingImportPrompt = null;
                canvas.Invalidate();
            }
        }

        private void Canvas_Paint_Custom(object sender, PaintEventArgs e)
        {
            if (Control.MouseButtons == MouseButtons.Left && (isPlacingSticker || pendingImportImage != null))
            {
                Point endPoint = canvas.PointToClient(Cursor.Position);
                int x = Math.Min(dragStartPoint.X, endPoint.X);
                int y = Math.Min(dragStartPoint.Y, endPoint.Y);
                int w = Math.Abs(endPoint.X - dragStartPoint.X);
                int h = Math.Abs(endPoint.Y - dragStartPoint.Y);

                using (Pen p = new Pen(Color.Red, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                {
                    e.Graphics.DrawRectangle(p, x, y, w, h);
                }
            }
        }

        private void StickyNoteChanged(StickyNoteControl note)
        {
            _network?.SendStickyNote(new StickyNotePayload
            {
                NoteID = note.NoteId,
                AuthorUsername = _network.CurrentUsername,
                X = note.Left,
                Y = note.Top,
                Width = note.Width,
                Height = note.Height,
                Text = note.NoteText,
                IsOpen = true,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        private void StickyNoteSelected(StickyNoteControl note)
        {
            SelectStickyNote(note);
        }

        private void SelectStickyNote(StickyNoteControl note)
        {
            selectedStickyNoteId = note?.NoteId;
            foreach (var pair in noteControls)
            {
                bool isSelected = note != null && string.Equals(pair.Key, selectedStickyNoteId, StringComparison.OrdinalIgnoreCase);
                pair.Value.SetSelected(isSelected);
            }
        }

        private bool DeleteSelectedStickyNote()
        {
            if (string.IsNullOrWhiteSpace(selectedStickyNoteId))
                return false;

            if (!noteControls.TryGetValue(selectedStickyNoteId, out var note))
            {
                selectedStickyNoteId = null;
                return false;
            }

            string noteId = selectedStickyNoteId;
            selectedStickyNoteId = null;
            noteControls.Remove(noteId);
            canvas.Controls.Remove(note);
            note.Dispose();

            _network?.SendStickyNote(new StickyNotePayload
            {
                NoteID = noteId,
                AuthorUsername = _network.CurrentUsername,
                IsOpen = false,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            return true;
        }

        private Image pendingImportImage;
        private string pendingImportAiType;
        private string pendingImportPrompt;
        private Point dragStartPoint;

        private void BtnBackImage_Click(object sender, EventArgs e)
        {
            if (!EnsureCanDraw()) return;

            // I/O FILE NHAP TU MAY: mo hop thoai cho nguoi dung chon file anh nen tren o dia.
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp";
                if (openFileDialog.ShowDialog() != DialogResult.OK)
                    return;

                // I/O FILE -> DU LIEU: Image.FromFile nap anh tu duong dan local vao bo nho.
                using (Image image = Image.FromFile(openFileDialog.FileName))
                {
                    // I/O DU LIEU -> NETWORK/DB: nen/rescale anh nen roi encode base64 de gui qua packet SET_BACKGROUND.
                    string imageData = EncodeBackgroundImageForNetwork(image);
                    canvasManager.ChangeBackgroundImage(image);

                    var payload = new SetBackgroundPayload
                    {
                        ActionID = Guid.NewGuid().ToString(),
                        RoomCode = _roomCode,
                        Username = _network.CurrentUsername,
                        ColorARGB = canvasManager.BackgroundColor.ToArgb(),
                        ImageData = imageData,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };

                    RecordAction(ToDrawAction(payload), true);
                    _network?.Send(CommandType.SET_BACKGROUND, payload);
                    ToastForm.ShowToast(this, "Da dat anh nen canvas");
                }
            }

        }

        private void BtnImport_Click(object sender, EventArgs e)
        {
            if (!EnsureCanDraw()) return;

            // I/O FILE NHAP TU MAY: nguoi dung chon anh local de import vao canvas.
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp";
                if (openFileDialog.ShowDialog() != DialogResult.OK)
                    return;

                if (pendingImportImage != null)
                    pendingImportImage.Dispose();

                // I/O FILE -> DU LIEU: doc file anh thanh Image trong RAM; chua gui network cho den khi user keo dat kich thuoc.
                pendingImportImage = Image.FromFile(openFileDialog.FileName);
                pendingImportAiType = null;
                pendingImportPrompt = null;
                isStickyNoteMode = false;
                isPlacingSticker = false;
                ToastForm.ShowToast(this, "Kéo thả chuột trên Canvas để chọn kích cỡ ảnh");
            }
        }

        private void BtnExport_Click(object sender, EventArgs e)
        {
            // I/O FILE XUAT RA MAY: SaveFileDialog lay duong dan dich de ghi PNG/JPEG ra o dia.
            using (SaveFileDialog saveFileDialog = new SaveFileDialog())
            {
                saveFileDialog.Filter = "PNG Image|*.png|JPEG Image|*.jpg";
                saveFileDialog.FileName = $"canvas_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                if (saveFileDialog.ShowDialog() != DialogResult.OK)
                    return;

                // I/O DU LIEU -> FILE: CanvasManager render bitmap hien tai va Save vao file local.
                canvasManager.ExportImage(saveFileDialog.FileName);
                ToastForm.ShowToast(this, "Đã xuất ảnh.");
            }
        }

        private async void BtnAiTextToDrawing_Click(object sender, EventArgs e)
        {
            if (!EnsureCanDraw()) return;
            if (!ApiConfig.IsHuggingFaceConfigured())
            {
                MessageBox.Show("Chua cau hinh HF_TOKEN trong .env.", "Thieu API key");
                return;
            }

            string prompt = ShowPromptDialog("Text-to-Drawing", "Nhập prompt để AI tạo ảnh:");
            if (string.IsNullOrWhiteSpace(prompt))
                return;

            // XU LY BAT DONG BO: chay request AI bang async de UI khong bi khoa trong luc cho HTTP API.
            await RunButtonTaskAsync(sender as Button, "AI đang tạo ảnh...", async () =>
            {
                byte[] imageBytes = await HuggingFaceClient.GenerateImageAsync(prompt.Trim());
                if (imageBytes == null || imageBytes.Length == 0)
                    throw new InvalidOperationException("Hugging Face khong tra ve anh.");

                using (Image aiImage = CreateImageFromBytes(imageBytes))
                {
                    Rectangle target = BuildCenteredImageTarget(aiImage.Size);
                    ImportImageAndBroadcast(aiImage, target, "text_to_image", prompt.Trim());
                }

                ToastForm.ShowToast(this, "Đã thêm ảnh AI vào canvas");
            });
        }

        private async void BtnAiRemoveBackground_Click(object sender, EventArgs e)
        {
            if (!EnsureCanDraw()) return;
            if (!ApiConfig.IsRemoveBgConfigured())
            {
                MessageBox.Show("Chua cau hinh REMOVE_BG_API_KEY trong .env.", "Thieu API key");
                return;
            }

            if (canvasManager.TryGetSelectedImagePayload(out var selectedImage))
            {
                await RemoveBackgroundFromCanvasImageAsync(sender as Button, selectedImage);
                return;
            }

            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp";
                if (openFileDialog.ShowDialog() != DialogResult.OK)
                    return;

                // I/O FILE -> DU LIEU: doc raw bytes tu file local de gui len Remove.bg.
                byte[] inputBytes = File.ReadAllBytes(openFileDialog.FileName);
                // XU LY BAT DONG BO: goi Remove.bg trong async task, sau do convert bytes tra ve thanh Image.
                await RunButtonTaskAsync(sender as Button, "AI đang xóa nền...", async () =>
                {
                    byte[] resultBytes = await RemoveBgClient.RemoveBackgroundAsync(inputBytes);
                    if (resultBytes == null || resultBytes.Length == 0)
                        throw new InvalidOperationException("remove.bg không trả về ảnh.");

                    using (Image resultImage = CreateImageFromBytes(resultBytes))
                    {
                        Rectangle target = BuildCenteredImageTarget(resultImage.Size);
                        ImportImageAndBroadcast(resultImage, target, "bg_removed", "");
                    }

                    ToastForm.ShowToast(this, "Đã thêm ảnh đã xóa nền vào canvas");
                });
            }
        }

        private async Task RemoveBackgroundFromCanvasImageAsync(Button button, ImportImagePayload selectedImage)
        {
            if (selectedImage == null || string.IsNullOrWhiteSpace(selectedImage.ImageData))
                return;

            // I/O DU LIEU -> BYTES: anh dang chon da nam tren canvas dang base64, decode ve bytes de gui Remove.bg.
            byte[] inputBytes = Convert.FromBase64String(selectedImage.ImageData);
            await RunButtonTaskAsync(button, "AI đang xóa nền...", async () =>
            {
                byte[] resultBytes = await RemoveBgClient.RemoveBackgroundAsync(inputBytes);
                if (resultBytes == null || resultBytes.Length == 0)
                    throw new InvalidOperationException("remove.bg không trả về ảnh.");

                using (Image resultImage = CreateImageFromBytes(resultBytes))
                {
                    var target = new Rectangle(selectedImage.X, selectedImage.Y, selectedImage.Width, selectedImage.Height);
                    ImportImageAndBroadcast(resultImage, target, "bg_removed", "", selectedImage.ActionID);
                }

                ToastForm.ShowToast(this, "Đã xóa nền ảnh đang chọn");
            });
        }

        private async Task RunButtonTaskAsync(Button button, string busyText, Func<Task> action)
        {
            string oldText = button?.Text;
            try
            {
                if (button != null)
                {
                    button.Enabled = false;
                    button.Text = busyText;
                }
                Cursor = Cursors.WaitCursor;
                // XU LY BAT DONG BO: await action() nhuong UI thread trong luc tac vu network/AI dang chay.
                await action();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không thể xử lý AI: " + ex.Message, "AI");
            }
            finally
            {
                Cursor = Cursors.Default;
                if (button != null)
                {
                    button.Text = oldText;
                    button.Enabled = true;
                }
            }
        }

        private string ShowPromptDialog(string title, string labelText, string defaultText = "")
        {
            using (Form dialog = new Form())
            using (Label label = new Label())
            using (TextBox textBox = new TextBox())
            using (Button okButton = new Button())
            using (Button cancelButton = new Button())
            {
                dialog.Text = title;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ClientSize = new Size(420, 145);

                label.Text = labelText;
                label.Location = new Point(12, 12);
                label.Size = new Size(396, 22);

                textBox.Location = new Point(12, 40);
                textBox.Size = new Size(396, 24);
                textBox.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
                textBox.Text = defaultText ?? string.Empty;
                textBox.SelectionStart = textBox.Text.Length;

                okButton.Text = "Tạo ảnh";
                okButton.DialogResult = DialogResult.OK;
                okButton.Location = new Point(232, 94);
                okButton.Size = new Size(84, 30);

                cancelButton.Text = "Hủy";
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.Location = new Point(324, 94);
                cancelButton.Size = new Size(84, 30);

                dialog.Controls.AddRange(new Control[] { label, textBox, okButton, cancelButton });
                dialog.AcceptButton = okButton;
                dialog.CancelButton = cancelButton;

                return dialog.ShowDialog(this) == DialogResult.OK ? textBox.Text.Trim() : null;
            }
        }

        private Image CreateImageFromBytes(byte[] bytes)
        {
            // I/O DU LIEU -> IMAGE: MemoryStream boc mang byte thanh stream de Image.FromStream doc duoc.
            using (MemoryStream ms = new MemoryStream(bytes))
            using (Image image = Image.FromStream(ms))
            {
                // Tao Bitmap moi de tach khoi MemoryStream, tranh anh bi loi sau khi stream dispose.
                return new Bitmap(image);
            }
        }

        private Rectangle BuildCenteredImageTarget(Size imageSize)
        {
            Size canvasSize = canvasManager.CanvasSize;
            if (canvasSize.Width <= 0 || canvasSize.Height <= 0)
                return new Rectangle(0, 0, Math.Max(50, imageSize.Width), Math.Max(50, imageSize.Height));

            float scale = Math.Min(1f, Math.Min(canvasSize.Width / (float)Math.Max(1, imageSize.Width), canvasSize.Height / (float)Math.Max(1, imageSize.Height)));
            int width = Math.Max(50, (int)(imageSize.Width * scale));
            int height = Math.Max(50, (int)(imageSize.Height * scale));
            int x = Math.Max(0, (canvasSize.Width - width) / 2);
            int y = Math.Max(0, (canvasSize.Height - height) / 2);
            return new Rectangle(x, y, width, height);
        }

        private void ImportImageAndBroadcast(Image image, Rectangle target, string aiType = null, string prompt = null, string actionIdOverride = null)
        {
            string actionId = string.IsNullOrWhiteSpace(actionIdOverride) ? Guid.NewGuid().ToString() : actionIdOverride;
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            canvasManager.ImportImage(image, target, actionId, _network?.CurrentUsername, timestamp);

            // I/O DU LIEU -> NETWORK/DB: chuyen Image trong RAM thanh PNG bytes roi base64 de gui packet.
            using (MemoryStream ms = new MemoryStream())
            {
                image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                string imageData = Convert.ToBase64String(ms.ToArray());
                RecordAction(ToDrawAction(new ImportImagePayload
                {
                    ActionID = actionId,
                    Username = _network?.CurrentUsername,
                    X = target.X,
                    Y = target.Y,
                    Width = target.Width,
                    Height = target.Height,
                    ImageData = imageData,
                    Timestamp = timestamp
                }), true);

                if (string.Equals(aiType, "text_to_image", StringComparison.OrdinalIgnoreCase))
                {
                    _network?.Send(CommandType.AI_TEXT_TO_IMAGE, new AiTextToImageResultPayload
                    {
                        ActionID = actionId,
                        RequesterUsername = _network?.CurrentUsername,
                        Prompt = prompt ?? "",
                        X = target.X,
                        Y = target.Y,
                        Width = target.Width,
                        Height = target.Height,
                        ImageData = imageData,
                        Timestamp = timestamp
                    });
                }
                else if (string.Equals(aiType, "bg_removed", StringComparison.OrdinalIgnoreCase))
                {
                    _network?.Send(CommandType.AI_BG_REMOVED, new AiBgRemovedPayload
                    {
                        ActionID = actionId,
                        RequesterUsername = _network?.CurrentUsername,
                        X = target.X,
                        Y = target.Y,
                        Width = target.Width,
                        Height = target.Height,
                        ImageData = imageData,
                        Timestamp = timestamp
                    });
                }
                else
                {
                    _network?.Send(CommandType.IMPORT_IMAGE, new ImportImagePayload
                    {
                        ActionID = actionId,
                        Username = _network?.CurrentUsername,
                        X = target.X,
                        Y = target.Y,
                        Width = target.Width,
                        Height = target.Height,
                        ImageData = imageData,
                        Timestamp = timestamp
                    });
                }
            }

            canvas.Invalidate();
        }

        private string EncodeBackgroundImageForNetwork(Image image)
        {
            Size canvasSize = canvasManager.CanvasSize;
            int width = canvasSize.Width > 0 ? canvasSize.Width : 1920;
            int height = canvasSize.Height > 0 ? canvasSize.Height : 1080;

            using (Bitmap bitmap = new Bitmap(width, height))
            using (Graphics g = Graphics.FromImage(bitmap))
            using (MemoryStream ms = new MemoryStream())
            {
                // I/O DU LIEU -> NETWORK/DB: ve anh nen vao bitmap dung kich thuoc canvas,
                // nen JPEG vao MemoryStream, roi base64 hoa de luu/replay qua DrawHistory.
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.Clear(canvasManager.BackgroundColor);
                g.DrawImage(image, new Rectangle(0, 0, width, height));

                var jpegCodec = GetJpegCodec();
                if (jpegCodec != null)
                {
                    using (var encoderParameters = new System.Drawing.Imaging.EncoderParameters(1))
                    {
                        encoderParameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                            System.Drawing.Imaging.Encoder.Quality,
                            85L);
                        bitmap.Save(ms, jpegCodec, encoderParameters);
                    }
                }
                else
                {
                    bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                }

                return Convert.ToBase64String(ms.ToArray());
            }
        }

        private static System.Drawing.Imaging.ImageCodecInfo GetJpegCodec()
        {
            foreach (var codec in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
            {
                if (string.Equals(codec.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
                    return codec;
            }

            return null;
        }

        private void SendChatMessage()
        {
            string message = txtChatInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(message)) return;

            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var payload = new ChatPayload
            {
                Username = _network.CurrentUsername,
                Message = message,
                Timestamp = timestamp
            };
            RememberLocalChat(payload);
            AppendChatMessage(payload);
            _network?.Send(CommandType.CHAT, payload);
            _udpManager?.SendChat(payload);
            txtChatInput.Clear();
        }

        private void SaveCurrentCanvasToGallery()
        {
            try
            {
                string filename = $"canvas_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string imageData = canvasManager.ExportPngBase64();
                string thumbnailData = canvasManager.ExportPngBase64(240, 160);
                _network?.SendSaveGallery(filename, imageData, thumbnailData);
                ToastForm.ShowToast(this, "Đang lưu vào Gallery...");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không thể lưu Gallery: " + ex.Message);
            }
        }

        private bool EnsureCanDraw()
        {
            if (canvasManager == null || canvasManager.IsDrawingEnabled)
                return true;

            ToastForm.ShowToast(this, "Chưa tới lượt vẽ của bạn");
            return false;
        }

        private string BuildChatKey(ChatPayload payload)
        {
            if (payload == null) return string.Empty;
            return $"{payload.Username}|{payload.Timestamp}|{payload.Message}";
        }

        private void RememberLocalChat(ChatPayload payload)
        {
            string key = BuildChatKey(payload);
            if (string.IsNullOrEmpty(key)) return;
            locallyShownChat.Add(key);
            locallyShownChatOrder.Enqueue(key);
            while (locallyShownChatOrder.Count > 50)
                locallyShownChat.Remove(locallyShownChatOrder.Dequeue());
        }

        private void AppendChatMessage(ChatPayload payload)
        {
            if (payload == null || rtbChat == null)
                return;

            string key = BuildChatKey(payload);
            if (!string.IsNullOrEmpty(key) && !displayedChat.Add(key))
                return;
            if (!string.IsNullOrEmpty(key))
            {
                displayedChatOrder.Enqueue(key);
                while (displayedChatOrder.Count > 120)
                    displayedChat.Remove(displayedChatOrder.Dequeue());
            }

            long ts = payload.Timestamp > 0 ? payload.Timestamp : DateTimeOffset.Now.ToUnixTimeMilliseconds();
            DateTimeOffset time = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime();
            string chatLine = $"[{time:HH:mm}] {payload.Username}: {payload.Message}{Environment.NewLine}";
            rtbChat.AppendText(chatLine);
            rtbChat.SelectionStart = rtbChat.TextLength;
            rtbChat.ScrollToCaret();
        }

        private void AppendLog(string message)
        {
            if (lstLogs == null)
                return;

            lstLogs.Items.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            lstLogs.TopIndex = lstLogs.Items.Count - 1;
        }

        private void RecordAction(DrawAction action, bool isOwnAction)
        {
            if (action == null || string.IsNullOrWhiteSpace(action.ActionID))
                return;

            actionHistory.Add(action);
            if (isOwnAction)
            {
                undoneActionIds.Remove(action.ActionID);
                ownRedoActionIds.Clear();
            }
        }

        private static DrawAction ToDrawAction(DrawPayload payload)
        {
            if (payload == null)
                return null;

            return new DrawAction
            {
                ActionID = payload.ActionID,
                Username = payload.Username,
                ToolType = payload.ToolType,
                X1 = payload.X1,
                Y1 = payload.Y1,
                X2 = payload.X2,
                Y2 = payload.Y2,
                ColorARGB = payload.ColorARGB,
                Thickness = payload.Thickness,
                Text = payload.Text,
                FontName = payload.FontName,
                FontSize = payload.FontSize,
                IsDeleted = payload.IsDeleted,
                Timestamp = payload.Timestamp
            };
        }

        private static DrawAction ToDrawAction(FloodFillPayload payload)
        {
            if (payload == null)
                return null;

            return new DrawAction
            {
                ActionID = payload.ActionID,
                Username = payload.Username,
                ToolType = "FloodFill",
                X1 = payload.X,
                Y1 = payload.Y,
                ColorARGB = payload.ColorARGB,
                Timestamp = payload.Timestamp
            };
        }

        private static DrawAction ToDrawAction(SetBackgroundPayload payload)
        {
            if (payload == null)
                return null;

            return new DrawAction
            {
                ActionID = payload.ActionID,
                Username = payload.Username,
                ToolType = "SetBackground",
                ColorARGB = payload.ColorARGB,
                ImageData = payload.ImageData,
                Timestamp = payload.Timestamp
            };
        }

        private static DrawAction ToDrawAction(ImportImagePayload payload)
        {
            if (payload == null)
                return null;

            return new DrawAction
            {
                ActionID = payload.ActionID,
                Username = payload.Username,
                ToolType = "ImportImage",
                X1 = payload.X,
                Y1 = payload.Y,
                ImageWidth = payload.Width,
                ImageHeight = payload.Height,
                ImageData = payload.ImageData,
                IsDeleted = payload.IsDeleted,
                Timestamp = payload.Timestamp
            };
        }

        private static DrawAction ToDrawAction(StickerPayload payload)
        {
            if (payload == null)
                return null;

            return new DrawAction
            {
                ActionID = payload.ActionID,
                Username = payload.Username,
                ToolType = "Sticker",
                Text = payload.StickerID,
                X1 = payload.X,
                Y1 = payload.Y,
                ImageWidth = payload.Width,
                ImageHeight = payload.Height,
                IsDeleted = payload.IsDeleted,
                Timestamp = payload.Timestamp
            };
        }

        private List<DrawAction> GetVisibleActions()
        {
            var visible = new List<DrawAction>();
            foreach (var action in actionHistory)
            {
                if (action == null || string.IsNullOrWhiteSpace(action.ActionID))
                    continue;
                if (!undoneActionIds.Contains(action.ActionID))
                    visible.Add(action);
            }
            return visible;
        }

        private void RenderVisibleHistory()
        {
            canvasManager.RenderActionHistory(GetVisibleActions());
        }

        private string GetLastUndoableOwnActionId()
        {
            string currentUsername = _network?.CurrentUsername ?? "";
            var seenActionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = actionHistory.Count - 1; i >= 0; i--)
            {
                var action = actionHistory[i];
                if (action == null || string.IsNullOrWhiteSpace(action.ActionID))
                    continue;
                if (!string.Equals(action.Username ?? "", currentUsername, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (seenActionIds.Contains(action.ActionID))
                    continue;
                seenActionIds.Add(action.ActionID);
                if (!undoneActionIds.Contains(action.ActionID))
                    return action.ActionID;
            }
            return null;
        }

        private void UndoOwnLastAction()
        {
            string actionId = GetLastUndoableOwnActionId();
            if (string.IsNullOrWhiteSpace(actionId))
                return;

            undoneActionIds.Add(actionId);
            ownRedoActionIds.Add(actionId);
            RenderVisibleHistory();
            _network?.SendUndo(actionId);
        }

        private void RedoOwnLastAction()
        {
            while (ownRedoActionIds.Count > 0)
            {
                int index = ownRedoActionIds.Count - 1;
                string actionId = ownRedoActionIds[index];
                ownRedoActionIds.RemoveAt(index);
                if (string.IsNullOrWhiteSpace(actionId) || !undoneActionIds.Contains(actionId))
                    continue;

                undoneActionIds.Remove(actionId);
                RenderVisibleHistory();
                _network?.SendRedo(actionId);
                return;
            }
        }

        private void ApplyUndoFromNetwork(UndoPayload payload)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.ActionID))
                return;
            if (!ActionBelongsToUser(payload.ActionID, payload.Username))
                return;

            bool changed = undoneActionIds.Add(payload.ActionID);
            if (changed && string.Equals(payload.Username ?? "", _network?.CurrentUsername ?? "", StringComparison.OrdinalIgnoreCase))
                ownRedoActionIds.Add(payload.ActionID);

            if (changed)
                RenderVisibleHistory();
        }

        private void ApplyRedoFromNetwork(RedoPayload payload)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.ActionID))
                return;
            if (!ActionBelongsToUser(payload.ActionID, payload.Username))
                return;

            if (undoneActionIds.Remove(payload.ActionID))
                RenderVisibleHistory();
        }

        private bool ActionBelongsToUser(string actionId, string username)
        {
            if (string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(username))
                return false;

            foreach (var action in actionHistory)
            {
                if (action == null)
                    continue;
                if (string.Equals(action.ActionID, actionId, StringComparison.OrdinalIgnoreCase))
                    return string.Equals(action.Username ?? "", username, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private bool IsCurrentUser(string username)
        {
            return !string.IsNullOrWhiteSpace(username) &&
                string.Equals(username, _network?.CurrentUsername ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private void NetworkEvents_OnCursorReceived(CursorPayload payload)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.Username))
                return;
            if (IsCurrentUser(payload.Username))
                return;

            long timestamp = payload.Timestamp > 0
                ? payload.Timestamp
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (remoteCursorLock)
            {
                if (remoteCursorTimestamps.TryGetValue(payload.Username, out long latest) && timestamp < latest)
                    return;

                payload.Timestamp = timestamp;
                remoteCursorTimestamps[payload.Username] = timestamp;
                pendingRemoteCursors[payload.Username] = payload;
            }
        }

        private void FlushRemoteCursorState()
        {
            Dictionary<string, CursorPayload> snapshot = null;
            lock (remoteCursorLock)
            {
                if (pendingRemoteCursors.Count == 0)
                    return;

                snapshot = new Dictionary<string, CursorPayload>(pendingRemoteCursors, StringComparer.OrdinalIgnoreCase);
                pendingRemoteCursors.Clear();
            }

            foreach (var item in snapshot.Values)
            {
                canvasManager.UpdateRemoteCursor(item.Username, new Point(item.X, item.Y));
                cursorLayer?.UpdateCursor(item);
            }
        }

        private void NetworkEvents_OnRoomMembersReceived(RoomMembersPayload payload)
        {
            if (payload?.Members == null)
                return;

            UIInvoke(() =>
            {
                if (lstMembers == null)
                    return;

                lstMembers.BeginUpdate();
                try
                {
                    lstMembers.Items.Clear();
                    foreach (var member in payload.Members)
                    {
                        string name = string.IsNullOrWhiteSpace(member.Username) ? "unknown" : member.Username;
                        string role = member.IsSpectator ? "observer" : "editor";
                        string state = member.IsOnline ? "online" : "offline";
                        string color = member.ColorARGB == 0 ? "n/a" : $"#{member.ColorARGB & 0x00FFFFFF:X6}";
                        lstMembers.Items.Add($"{name} | {role} | {state} | {color}");
                    }
                }
                finally
                {
                    lstMembers.EndUpdate();
                }
            });
        }

        private void NetworkEvents_OnUserJoined(UserJoinPayload payload)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.Username))
                return;
            UIInvoke(() =>
            {
                ToastForm.ShowToast(this, $"{payload.Username} đã tham gia phòng");
                AppendLog($"{payload.Username} đã tham gia phòng.");
            });
        }

        private void NetworkEvents_OnUserLeft(UserLeavePayload payload)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.Username))
                return;
            UIInvoke(() =>
            {
                lock (remoteCursorLock)
                {
                    pendingRemoteCursors.Remove(payload.Username);
                    remoteCursorTimestamps.Remove(payload.Username);
                }
                cursorLayer?.RemoveCursor(payload.Username);
                canvasManager.RemoveRemoteCursor(payload.Username);
                ToastForm.ShowToast(this, $"{payload.Username} đã rời phòng");
                AppendLog($"{payload.Username} đã rời phòng.");
            });
        }

        private void NetworkEvents_OnDrawReceived(DrawPayload payload) => UIInvoke(() =>
        {
            if (isViewingSnapshot) return; // dang xem lai snapshot (view-only) -> bo qua cap nhat live
            RecordAction(ToDrawAction(payload), false);
            canvasManager.ApplyRemoteDraw(payload);
        });
        private void NetworkEvents_OnSyncBoardReceived(SyncBoardPayload payload)
        {
            if (payload?.Actions == null)
                return;
            if (payload.IsChunked)
            {
                UIInvoke(() =>
                {
                    if (payload.ChunkIndex == 0 || !isReceivingSyncChunks)
                    {
                        actionHistory.Clear();
                        undoneActionIds.Clear();
                        ownRedoActionIds.Clear();
                        isReceivingSyncChunks = true;
                    }

                    actionHistory.AddRange(payload.Actions);
                    AppendLog($"Dang dong bo bang ve: chunk {payload.ChunkIndex + 1}/{Math.Max(1, payload.TotalChunks)}");

                    if (!payload.IsFinalChunk)
                        return;

                    isReceivingSyncChunks = false;
                    RenderVisibleHistory();
                    AppendLog($"Dong bo {actionHistory.Count} hanh dong tu phong");
                });
                return;
            }
            if (payload.Actions.Count >= 0)
            {
                isReceivingSyncChunks = false;
                actionHistory.Clear();
                actionHistory.AddRange(payload.Actions);
                undoneActionIds.Clear();
                ownRedoActionIds.Clear();
                UIInvoke(() =>
                {
                    RenderVisibleHistory();
                    AppendLog($"Dong bo {payload.Actions.Count} hanh dong tu phong");
                });
                return;
            }
            UIInvoke(() =>
            {
                // ✅ FIX: Clear canvas trước khi replay để tránh bị chồng nét khi reconnect
                canvasManager.ClearAll();

                foreach (var action in payload.Actions)
                {
                    string tool = action.ToolType ?? "";

                    if (tool.Equals("FloodFill", StringComparison.OrdinalIgnoreCase))
                    {
                        canvasManager.ApplyRemoteFloodFill(new SharedLib.Payloads.FloodFillPayload
                        {
                            X = action.X1,
                            Y = action.Y1,
                            ColorARGB = action.ColorARGB
                        });
                    }
                    else if (tool.Equals("SetBackground", StringComparison.OrdinalIgnoreCase))
                    {
                        // ✅ FIX: Replay màu nền khi reconnect
                        canvasManager.ApplyRemoteSetBackground(new SharedLib.Payloads.SetBackgroundPayload
                        {
                            ColorARGB = action.ColorARGB,
                            ImageData = action.ImageData
                        });
                    }
                    else if (tool.Equals("Sticker", StringComparison.OrdinalIgnoreCase))
                    {
                        // ✅ FIX: Replay sticker khi reconnect
                        canvasManager.AddSticker(new SharedLib.Payloads.StickerPayload
                        {
                            ActionID = action.ActionID,
                            Username  = action.Username,
                            StickerID = action.Text,   // StickerID được map vào field Text khi lưu
                            X         = action.X1,
                            Y         = action.Y1,
                            Width     = action.ImageWidth  > 0 ? action.ImageWidth  : 64,
                            Height    = action.ImageHeight > 0 ? action.ImageHeight : 64,
                            IsDeleted = action.IsDeleted,
                            Timestamp = action.Timestamp,
                        });
                    }
                    else if (tool.Equals("ImportImage", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(action.ImageData))
                    {
                        canvasManager.ApplyRemoteImportImage(new SharedLib.Payloads.ImportImagePayload
                        {
                            ActionID = action.ActionID,
                            Username = action.Username,
                            X = action.X1,
                            Y = action.Y1,
                            Width = action.ImageWidth > 0 ? action.ImageWidth : 400,
                            Height = action.ImageHeight > 0 ? action.ImageHeight : 300,
                            ImageData = action.ImageData,
                            IsDeleted = action.IsDeleted,
                            Timestamp = action.Timestamp
                        });
                    }
                    else
                    {
                        canvasManager.ApplyDrawAction(action);
                    }
                }
                AppendLog($"Đồng bộ {payload.Actions.Count} hành động từ phòng");
            });
        }
        private void NetworkEvents_OnFloodFillReceived(FloodFillPayload payload) => UIInvoke(() =>
        {
            if (isViewingSnapshot) return;
            RecordAction(ToDrawAction(payload), false);
            canvasManager.ApplyRemoteFloodFill(payload);
        });
        private void NetworkEvents_OnImportImageReceived(ImportImagePayload payload) => UIInvoke(() =>
        {
            if (isViewingSnapshot) return;
            RecordAction(ToDrawAction(payload), false);
            canvasManager.ApplyRemoteImportImage(payload);
        });
        private void NetworkEvents_OnAiTextToImageResult(AiTextToImageResultPayload payload)
        {
            if (payload == null || isViewingSnapshot)
                return;

            UIInvoke(() =>
            {
                var importPayload = new ImportImagePayload
                {
                    ActionID = payload.ActionID,
                    Username = payload.RequesterUsername,
                    X = payload.X,
                    Y = payload.Y,
                    Width = payload.Width,
                    Height = payload.Height,
                    ImageData = payload.ImageData,
                    Timestamp = payload.Timestamp
                };
                RecordAction(ToDrawAction(importPayload), false);
                canvasManager.ApplyRemoteImportImage(importPayload);
                AppendLog($"AI text-to-image: {payload.RequesterUsername}");
            });
        }

        private void NetworkEvents_OnAiBgRemovedResult(AiBgRemovedPayload payload)
        {
            if (payload == null || isViewingSnapshot)
                return;

            UIInvoke(() =>
            {
                var importPayload = new ImportImagePayload
                {
                    ActionID = payload.ActionID,
                    Username = payload.RequesterUsername,
                    X = payload.X,
                    Y = payload.Y,
                    Width = payload.Width,
                    Height = payload.Height,
                    ImageData = payload.ImageData,
                    Timestamp = payload.Timestamp
                };
                RecordAction(ToDrawAction(importPayload), false);
                canvasManager.ApplyRemoteImportImage(importPayload);
                AppendLog($"AI remove.bg: {payload.RequesterUsername}");
            });
        }

        private void NetworkEvents_OnSetBackgroundReceived(SetBackgroundPayload payload) => UIInvoke(() =>
        {
            if (isViewingSnapshot) return;
            RecordAction(ToDrawAction(payload), false);
            canvasManager.ApplyRemoteSetBackground(payload);
        });
        private void NetworkEvents_OnClearAllReceived() => UIInvoke(() =>
        {
            if (isViewingSnapshot) return;
            actionHistory.Clear();
            undoneActionIds.Clear();
            ownRedoActionIds.Clear();
            canvasManager.ApplyRemoteClearAll();
        });
        private void NetworkEvents_OnUndoReceived(UndoPayload payload) => UIInvoke(() => { if (!isViewingSnapshot) ApplyUndoFromNetwork(payload); });
        private void NetworkEvents_OnRedoReceived(RedoPayload payload) => UIInvoke(() => { if (!isViewingSnapshot) ApplyRedoFromNetwork(payload); });

        private void NetworkEvents_OnReactionReceived(ReactionPayload payload)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.Emoji))
                return;
            UIInvoke(() => cursorLayer?.AddEmoji(payload.Emoji, new Point(payload.X, payload.Y)));
        }

        // Tha emoji: render local ngay + gui qua TCP (tin cay o moi che do: direct/LAN/relay/ngrok).
        // Toa do gui di la toa do CANVAS (world) de moi client thay dung vi tri du zoom/pan khac nhau.
        private void EmitReaction(string emoji, Point clientPoint)
        {
            if (string.IsNullOrWhiteSpace(_network?.CurrentUsername))
                return;
            Point canvasPoint = canvasManager?.ScreenToCanvas(clientPoint) ?? clientPoint;
            cursorLayer?.AddEmoji(emoji, canvasPoint);
            _network?.SendReaction(new ReactionPayload
            {
                Username = _network.CurrentUsername,
                Emoji = emoji,
                X = canvasPoint.X,
                Y = canvasPoint.Y
            });
        }

        private void SendReactionFromPicker(string emoji)
        {
            // Tha o giua-duoi vung canvas dang nhin, emoji se bay len roi mo dan.
            Point pos = new Point(Math.Max(20, canvas.ClientSize.Width / 2),
                                  Math.Max(40, canvas.ClientSize.Height * 3 / 4));
            EmitReaction(emoji, pos);
        }

        // ── NHAN DIEN NET VE TAY -> line art: cong cu AiRegion keo-tha chon vung -> tagger nhan dien -> ve lai dep hon ──

        private async Task RunAiRegionAsync(Rectangle region)
        {
            if (!EnsureCanDraw()) return;
            if (canvasManager == null) return;

            if (!ApiConfig.IsSketchToImageConfigured())
            {
                MessageBox.Show(
                    "Chưa cấu hình HF_SKETCH_SPACE trong .env (URL Hugging Face Space chạy ControlNet-lineart).",
                    "Thiếu cấu hình AI");
                return;
            }

            if (region.Width < 8 || region.Height < 8)
            {
                ToastForm.ShowToast(this, "Vùng chọn quá nhỏ");
                return;
            }

            // Chup vung da chon (noi rong nhe de khong cat sat net).
            Bitmap sketchBmp = canvasManager.RenderRegionToBitmap(Rectangle.Inflate(region, 8, 8));
            if (sketchBmp == null)
                return;

            byte[] sketchPng;
            using (var ms = new MemoryStream())
            {
                sketchBmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                sketchPng = ms.ToArray();
            }
            sketchBmp.Dispose();

            // 1) Tagger "nhan dien" noi dung vung ve (tuy chon: loi -> chuoi rong).
            string suggested = "";
            Cursor = Cursors.WaitCursor;
            try { suggested = await SketchToImageClient.AnalyzeSketchAsync(sketchPng); }
            catch { /* tagger tuy chon, bo qua */ }
            finally { Cursor = Cursors.Default; }

            // 2) Cho nguoi dung xac nhan/sua mo ta truoc khi tao.
            string prompt = ShowPromptDialog("Vẽ lại vùng chọn thành line art",
                "AI nhận diện (sửa/bổ sung nếu cần, để trống cũng được):", suggested ?? "");
            if (prompt == null) return; // huy
            prompt = prompt.Trim();

            // 3) Goi model ve lai (fidelity thap -> AI ve lai cho dep thay vi chi trace net tho).
            Rectangle target = region;
            await RunButtonTaskAsync(null, "AI đang vẽ lại thành line art...", async () =>
            {
                byte[] resultBytes = await SketchToImageClient.GenerateFromSketchAsync(sketchPng, prompt);
                if (resultBytes == null || resultBytes.Length == 0)
                    throw new InvalidOperationException("Space không trả về ảnh line art.");

                using (Image aiImage = CreateImageFromBytes(resultBytes))
                {
                    Rectangle place = NormalizeSketchTarget(target, aiImage.Size);
                    ImportImageAndBroadcast(aiImage, place, "text_to_image", prompt);
                }

                ToastForm.ShowToast(this, "Đã vẽ lại vùng chọn thành line art");
            });
        }

        private Rectangle NormalizeSketchTarget(Rectangle sketch, Size imageSize)
        {
            Size canvasSize = canvasManager.CanvasSize;
            int srcW = imageSize.Width > 0 ? imageSize.Width : Math.Max(1, sketch.Width);
            int srcH = imageSize.Height > 0 ? imageSize.Height : Math.Max(1, sketch.Height);
            float ar = srcW / (float)srcH;
            if (ar <= 0) ar = 1f;

            // Bat dau tu chieu rong vung sketch (toi thieu 96), giu ti le anh; thu nho deu de vua canvas.
            int w = Math.Max(96, sketch.Width);
            int h = Math.Max(1, (int)Math.Round(w / ar));
            if (canvasSize.Width > 0 && w > canvasSize.Width) { w = canvasSize.Width; h = (int)Math.Round(w / ar); }
            if (canvasSize.Height > 0 && h > canvasSize.Height) { h = canvasSize.Height; w = (int)Math.Round(h * ar); }

            int x = sketch.X, y = sketch.Y;
            if (canvasSize.Width > 0) x = Math.Max(0, Math.Min(x, canvasSize.Width - w));
            if (canvasSize.Height > 0) y = Math.Max(0, Math.Min(y, canvasSize.Height - h));
            return new Rectangle(x, y, Math.Max(48, w), Math.Max(48, h));
        }

        private void NetworkEvents_OnChatReceived(ChatPayload payload)
        {
            if (payload == null)
                return;
            UIInvoke(() =>
            {
                string key = BuildChatKey(payload);
                if (!string.IsNullOrEmpty(key) && locallyShownChat.Remove(key))
                    return;

                AppendChatMessage(payload);
            });
        }

        private void NetworkEvents_OnActivityLogReceived(ActivityLogPayload payload)
        {
            if (payload == null)
                return;
            UIInvoke(() => AppendLog($"{payload.Username}: {payload.Action}"));
        }

        private void NetworkEvents_OnSnapshotListReceived(SnapshotListPayload payload)
        {
            if (payload?.Snapshots == null)
                return;
            UIInvoke(() => ShowSnapshotListUI(payload.Snapshots));
        }

        // ── SNAPSHOT / XEM LAI (view-only time-travel) ─────────────────────────
        private Form snapshotListForm;
        private ListView snapshotListView;
        private ImageList snapshotImageList;
        private PictureBox snapshotPreview;
        private Label snapshotPreviewLabel;
        private List<SnapshotInfo> lastSnapshots = new List<SnapshotInfo>();
        private bool isViewingSnapshot;

        // Render snapshot offscreen -> thumbnail/preview (KHONG dung canvas chinh).
        private PictureBox offscreenCanvasHost;
        private CanvasManager offscreenCanvas;
        private readonly Dictionary<int, Bitmap> snapshotPreviewCache = new Dictionary<int, Bitmap>();
        private readonly Queue<int> snapshotFetchQueue = new Queue<int>();
        private bool snapshotFetchInFlight;

        private void ShowSnapshotListUI(List<SnapshotInfo> snapshots)
        {
            lastSnapshots = snapshots ?? new List<SnapshotInfo>();

            if (snapshotListForm == null || snapshotListForm.IsDisposed)
            {
                snapshotListForm = new Form
                {
                    Text = "Xem lại trạng thái phòng (chỉ xem)",
                    StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.Sizable,
                    ClientSize = new Size(840, 520),
                    MinimumSize = new Size(620, 380),
                    MinimizeBox = false,
                    MaximizeBox = true
                };

                snapshotImageList = new ImageList { ImageSize = new Size(160, 90), ColorDepth = ColorDepth.Depth32Bit };
                snapshotListView = new ListView
                {
                    Dock = DockStyle.Left,
                    Width = 200,
                    View = View.LargeIcon,
                    LargeImageList = snapshotImageList,
                    MultiSelect = false,
                    HideSelection = false
                };
                snapshotListView.SelectedIndexChanged += (s, e) => ShowSelectedSnapshotPreview();
                snapshotListView.DoubleClick += (s, e) => ViewSelectedSnapshot();

                var previewPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Gainsboro, Padding = new Padding(6) };
                snapshotPreviewLabel = new Label { Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft };
                snapshotPreview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
                previewPanel.Controls.Add(snapshotPreview);
                previewPanel.Controls.Add(snapshotPreviewLabel);

                var pnl = new Panel { Dock = DockStyle.Bottom, Height = 40 };
                var btnView = new Button { Text = "Xem trên canvas chính", Width = 160, Height = 28, Location = new Point(8, 6) };
                var btnLive = new Button { Text = "Về hiện tại", Width = 110, Height = 28, Location = new Point(176, 6) };
                btnView.Click += (s, e) => ViewSelectedSnapshot();
                btnLive.Click += (s, e) => ReturnToLiveBoard();
                pnl.Controls.Add(btnView);
                pnl.Controls.Add(btnLive);

                // Them theo thu tu Fill -> Left -> Bottom de docking chia vung dung.
                snapshotListForm.Controls.Add(previewPanel);
                snapshotListForm.Controls.Add(snapshotListView);
                snapshotListForm.Controls.Add(pnl);
                snapshotListForm.FormClosing += (s, e) =>
                {
                    e.Cancel = true;
                    snapshotListForm.Hide();
                    if (isViewingSnapshot) ReturnToLiveBoard();
                };
            }

            snapshotFetchQueue.Clear();
            snapshotFetchInFlight = false;
            snapshotListView.Items.Clear();
            foreach (var snap in lastSnapshots)
            {
                DateTime local = DateTimeOffset.FromUnixTimeMilliseconds(snap.Timestamp).LocalDateTime;
                var item = new ListViewItem($"#{snap.SnapshotID}  {local:HH:mm:ss dd/MM}") { Tag = snap };
                if (snapshotImageList.Images.ContainsKey(snap.SnapshotID.ToString()))
                    item.ImageKey = snap.SnapshotID.ToString();
                snapshotListView.Items.Add(item);
                snapshotFetchQueue.Enqueue(snap.SnapshotID); // tai du lieu de render thumbnail
            }

            snapshotPreview.Image = null;
            snapshotPreviewLabel.Text = lastSnapshots.Count > 0
                ? "Chọn một snapshot để xem trạng thái canvas lúc đó."
                : "(Chưa có snapshot nào)";
            if (snapshotListView.Items.Count > 0)
                snapshotListView.Items[0].Selected = true;

            if (!snapshotListForm.Visible)
                snapshotListForm.Show(this);
            snapshotListForm.BringToFront();

            PumpSnapshotFetch();
        }

        // Gui lan luot tung yeu cau SNAPSHOT_DATA (1 cai dang bay) de khong nghen server.
        private void PumpSnapshotFetch()
        {
            if (snapshotFetchInFlight) return;
            while (snapshotFetchQueue.Count > 0)
            {
                int id = snapshotFetchQueue.Dequeue();
                if (snapshotPreviewCache.ContainsKey(id))
                {
                    UpdateSnapshotThumbnail(id, snapshotPreviewCache[id]);
                    continue;
                }
                snapshotFetchInFlight = true;
                _network?.RequestSnapshotData(id);
                return;
            }
        }

        private void NetworkEvents_OnSnapshotDataReceived(int snapshotId, List<DrawAction> actions)
        {
            UIInvoke(() =>
            {
                try
                {
                    if (!snapshotPreviewCache.ContainsKey(snapshotId))
                    {
                        Bitmap preview = RenderActionsToBitmap(actions);
                        if (preview != null)
                        {
                            snapshotPreviewCache[snapshotId] = preview;
                            UpdateSnapshotThumbnail(snapshotId, preview);
                        }
                    }

                    var sel = GetSelectedSnapshot();
                    if (sel != null && sel.SnapshotID == snapshotId)
                        ShowSelectedSnapshotPreview();
                }
                catch { /* render loi 1 snapshot khong duoc lam ket dialog */ }
                finally
                {
                    snapshotFetchInFlight = false;
                    PumpSnapshotFetch();
                }
            });
        }

        // Render mang DrawAction ra bitmap bang mot CanvasManager an (offscreen), khong dung canvas chinh.
        private Bitmap RenderActionsToBitmap(List<DrawAction> actions)
        {
            if (offscreenCanvas == null)
            {
                offscreenCanvasHost = new PictureBox
                {
                    Width = CanvasManager.DefaultCanvasSize.Width,
                    Height = CanvasManager.DefaultCanvasSize.Height,
                    Visible = false
                };
                offscreenCanvas = new CanvasManager(offscreenCanvasHost);
            }

            offscreenCanvas.RenderActionHistory(actions ?? new List<DrawAction>());
            Size sz = offscreenCanvas.CanvasSize;
            if (sz.Width <= 0 || sz.Height <= 0)
                return null;
            using (Bitmap full = offscreenCanvas.RenderRegionToBitmap(new Rectangle(0, 0, sz.Width, sz.Height)))
            {
                if (full == null) return null;
                return ScaleToFit(full, 960, 540); // cache kich thuoc vua phai cho preview
            }
        }

        private void UpdateSnapshotThumbnail(int snapshotId, Bitmap preview)
        {
            if (snapshotImageList == null || snapshotListView == null || preview == null) return;
            string key = snapshotId.ToString();
            // Noi dung snapshot bat bien -> chi them thumbnail mot lan (tranh xao tron index ImageList).
            if (!snapshotImageList.Images.ContainsKey(key))
            {
                using (Bitmap thumb = FitOntoCanvas(preview, snapshotImageList.ImageSize))
                    snapshotImageList.Images.Add(key, thumb); // ImageList sao chep -> co the dispose thumb
            }
            foreach (ListViewItem it in snapshotListView.Items)
            {
                if (it.Tag is SnapshotInfo s && s.SnapshotID == snapshotId)
                {
                    it.ImageKey = key;
                    break;
                }
            }
        }

        private SnapshotInfo GetSelectedSnapshot()
        {
            if (snapshotListView == null || snapshotListView.SelectedItems.Count == 0) return null;
            return snapshotListView.SelectedItems[0].Tag as SnapshotInfo;
        }

        private void ShowSelectedSnapshotPreview()
        {
            var snap = GetSelectedSnapshot();
            if (snap == null) return;
            DateTime local = DateTimeOffset.FromUnixTimeMilliseconds(snap.Timestamp).LocalDateTime;
            if (snapshotPreviewCache.TryGetValue(snap.SnapshotID, out var bmp) && bmp != null)
            {
                snapshotPreview.Image = bmp;
                snapshotPreviewLabel.Text = $"Snapshot #{snap.SnapshotID} — {local:HH:mm:ss dd/MM/yyyy}";
            }
            else
            {
                snapshotPreview.Image = null;
                snapshotPreviewLabel.Text = $"Snapshot #{snap.SnapshotID} — đang tải xem trước...";
                if (!snapshotFetchQueue.Contains(snap.SnapshotID))
                    snapshotFetchQueue.Enqueue(snap.SnapshotID);
                PumpSnapshotFetch();
            }
        }

        // Thu nho giu ti le, tra ve bitmap moi.
        private static Bitmap ScaleToFit(Bitmap src, int maxW, int maxH)
        {
            float scale = Math.Min(1f, Math.Min(maxW / (float)src.Width, maxH / (float)src.Height));
            int w = Math.Max(1, (int)(src.Width * scale));
            int h = Math.Max(1, (int)(src.Height * scale));
            var dst = new Bitmap(w, h);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return dst;
        }

        // Ve src (giu ti le) vao giua mot khung trang dung kich thuoc box (cho icon ListView dong deu).
        private static Bitmap FitOntoCanvas(Bitmap src, Size box)
        {
            var dst = new Bitmap(box.Width, box.Height);
            using (var g = Graphics.FromImage(dst))
            {
                g.Clear(Color.White);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                float scale = Math.Min(box.Width / (float)src.Width, box.Height / (float)src.Height);
                int w = Math.Max(1, (int)(src.Width * scale));
                int h = Math.Max(1, (int)(src.Height * scale));
                g.DrawImage(src, new Rectangle((box.Width - w) / 2, (box.Height - h) / 2, w, h));
            }
            return dst;
        }

        private void ViewSelectedSnapshot()
        {
            var snap = GetSelectedSnapshot();
            if (snap == null) return;
            isViewingSnapshot = true;
            _network?.RequestSnapshotRestore(snap.SnapshotID);
            AppendLog($"Đang xem lại snapshot #{snap.SnapshotID} (chỉ xem) trên canvas chính. Bấm 'Về hiện tại' để quay lại.");
        }

        private void ReturnToLiveBoard()
        {
            isViewingSnapshot = false;
            _network?.Send(CommandType.REQUEST_PLAYBACK, new PlaybackRequestPayload { RoomCode = _roomCode });
            AppendLog("Đã quay lại trạng thái hiện tại của phòng.");
        }

        private void NetworkEvents_OnStickerReceived(StickerPayload payload)
        {
            if (payload == null || isViewingSnapshot)
                return;
            UIInvoke(() =>
            {
                RecordAction(ToDrawAction(payload), false);
                canvasManager.AddSticker(payload);
            });
        }

        private void NetworkEvents_OnStickyNoteReceived(StickyNotePayload payload)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.NoteID) || isViewingSnapshot)
                return;

            UIInvoke(() =>
            {
                if (!payload.IsOpen)
                {
                    if (noteControls.TryGetValue(payload.NoteID, out var deletedNote))
                    {
                        noteControls.Remove(payload.NoteID);
                        if (selectedStickyNoteId == payload.NoteID)
                            selectedStickyNoteId = null;

                        canvas.Controls.Remove(deletedNote);
                        deletedNote.Dispose();
                    }

                    return;
                }

                if (!noteControls.TryGetValue(payload.NoteID, out var note))
                {
                    note = new StickyNoteControl { NoteId = payload.NoteID, Author = payload.AuthorUsername };
                    note.NoteChanged += StickyNoteChanged;
                    note.NoteSelected += StickyNoteSelected;
                    canvas.Controls.Add(note);
                    noteControls[payload.NoteID] = note;
                }

                note.Location = new Point(payload.X, payload.Y);
                if (payload.Width > 0 && payload.Height > 0)
                    note.Size = new Size(payload.Width, payload.Height);
                note.NoteText = payload.Text ?? string.Empty;
                note.BringToFront();
            });
        }

        private void NetworkEvents_OnTurnBasedReceived(TurnBasedPayload payload)
        {
            if (payload == null)
                return;

            UIInvoke(() => ApplyTurnBasedState(payload));
        }

        private void ApplyTurnBasedState(TurnBasedPayload payload)
        {
            bool isMyTurn = !payload.IsEnabled ||
                string.Equals(payload.ActiveUser, _network.CurrentUsername, StringComparison.OrdinalIgnoreCase);

            canvasManager.IsDrawingEnabled = isMyTurn;
            turnPanel.SetState(payload.IsEnabled, payload.ActiveUser, CanAdvanceCurrentTurn(payload.IsEnabled, payload.ActiveUser));
            AppendLog(payload.IsEnabled
                ? $"Vẽ theo lượt: bật, lượt của {payload.ActiveUser}"
                : "Vẽ theo lượt: tắt");
            ToastForm.ShowToast(this, payload.IsEnabled
                ? (isMyTurn ? "Bạn đang có quyền vẽ" : $"Đang chờ lượt của {payload.ActiveUser}")
                : "Đã tắt vẽ theo lượt");
        }

        private void HandleNextTurnRequested()
        {
            if (!CanAdvanceCurrentTurn(turnPanel.IsEnabled, turnPanel.ActiveUser))
            {
                ToastForm.ShowToast(this, "Chỉ người đang giữ lượt mới được chuyển lượt");
                return;
            }

            if (!turnPanel.IsEnabled)
            {
                ToastForm.ShowToast(this, "Hãy bật vẽ theo lượt trước");
                return;
            }

            var payload = new TurnBasedPayload
            {
                RoomCode = _roomCode,
                Username = _network.CurrentUsername,
                IsEnabled = true,
                ActiveUser = turnPanel.ActiveUser
            };

            _network?.Send(CommandType.TURN_CHANGE, payload);
            _udpManager?.SendTurnChange(payload);
        }

        private bool CanAdvanceCurrentTurn(bool enabled, string activeUser)
        {
            return enabled &&
                string.Equals(activeUser, _network?.CurrentUsername, StringComparison.OrdinalIgnoreCase);
        }

        private void NetworkEvents_OnSaveGalleryResponse(SaveGalleryResponse payload)
        {
            if (payload == null)
                return;
            UIInvoke(() => ToastForm.ShowToast(this, payload.Message ?? (payload.IsSuccess ? "Đã lưu Gallery" : "Lỗi lưu Gallery")));
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Shift) canvas.Cursor = Cursors.Cross;

            if (e.Control && (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add))
            {
                AdjustCanvasZoom(ZoomStep);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.Control && (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract))
            {
                AdjustCanvasZoom(-ZoomStep);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            // Phim tat tha emoji (chi khi khong dang go chat/RichTextBox). Gui qua TCP cho tin cay.
            if (!(ActiveControl is TextBoxBase))
            {
                if (e.KeyCode == Keys.D1) EmitReaction("👍", canvas.PointToClient(Cursor.Position));
                else if (e.KeyCode == Keys.D2) EmitReaction("❤️", canvas.PointToClient(Cursor.Position));
                else if (e.KeyCode == Keys.D3) EmitReaction("😂", canvas.PointToClient(Cursor.Position));
            }
        }

        private void MainForm_KeyUp(object sender, KeyEventArgs e)
        {
            if (!e.Shift)
                canvas.Cursor = Cursors.Default;
        }

        private void Canvas_MouseWheel(object sender, MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) != Keys.Control)
                return;

            AdjustCanvasZoom(e.Delta > 0 ? ZoomStep : -ZoomStep, e.Location);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Delete)
            {
                bool isTextInputFocused = ActiveControl is TextBoxBase || (ActiveControl != null && ActiveControl.GetType().Name.IndexOf("TextBox", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!isTextInputFocused && EnsureCanDraw())
                {
                    if (DeleteSelectedStickyNote())
                    {
                        ToastForm.ShowToast(this, "Da xoa sticky note duoc chon");
                        return true;
                    }

                    if (canvas != null && canvas.Focused && canvasManager != null && canvasManager.DeleteSelectedObject())
                    {
                        ToastForm.ShowToast(this, "Da xoa doi tuong duoc chon");
                        return true;
                    }
                }
            }

            if (keyData == (Keys.Control | Keys.Z))
            {
                if (EnsureCanDraw())
                {
                    UndoOwnLastAction();
                }
                return true;
            }
            if (keyData == (Keys.Control | Keys.Y))
            {
                if (EnsureCanDraw())
                {
                    RedoOwnLastAction();
                }
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void UIInvoke(Action action)
        {
            if (this.IsHandleCreated && this.InvokeRequired)
            {
                // XU LY DA LUONG: packet den tu network thread, nen phai marshal ve UI thread truoc khi cham control WinForms.
                this.BeginInvoke(action);
                return;
            }
            action();
        }

        public class DoubleBufferedPictureBox : PictureBox
        {
            public DoubleBufferedPictureBox()
            {
                this.DoubleBuffered = true;
            }
        }

        private class StickerPickerControl : Panel
        {
            public event Action<string> StickerSelected;

            public StickerPickerControl()
            {
                var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(4), WrapContents = true };
                Add(flow, "❤️", "heart");
                Add(flow, "⭐", "star");
                Add(flow, "🔥", "fire");
                Add(flow, "💡", "idea");
                Add(flow, "✅", "check");
                Controls.Add(flow);
                BorderStyle = BorderStyle.FixedSingle;
                BackColor = Color.WhiteSmoke;
            }

            private void Add(FlowLayoutPanel flow, string glyph, string id)
            {
                var btn = new Button { Text = glyph, Tag = id, Width = 34, Height = 30, Font = new Font("Segoe UI Emoji", 11f) };
                btn.Click += (s, e) => StickerSelected?.Invoke((string)btn.Tag);
                flow.Controls.Add(btn);
            }
        }

        private class StickyNoteControl : Panel
        {
            private bool dragging;
            private bool resizing;
            private Point dragOffset;
            private Point resizeStartPoint;
            private Size resizeStartSize;
            private readonly Label header;
            private readonly TextBox txt;
            private readonly Panel resizeGrip;
            public string NoteId { get; set; }
            public string Author { get; set; }
            public string NoteText { get => txt.Text; set => txt.Text = value; }
            public event Action<StickyNoteControl> NoteChanged;
            public event Action<StickyNoteControl> NoteSelected;

            public StickyNoteControl()
            {
                Size = new Size(170, 120);
                BackColor = Color.FromArgb(255, 255, 220);
                BorderStyle = BorderStyle.FixedSingle;

                header = new Label { Dock = DockStyle.Top, Height = 20, Text = "Ghi chú", BackColor = Color.Khaki, Padding = new Padding(4, 0, 0, 0) };
                txt = new TextBox { Dock = DockStyle.Fill, Multiline = true, BorderStyle = BorderStyle.None, BackColor = BackColor };
                resizeGrip = new Panel
                {
                    Size = new Size(12, 12),
                    BackColor = Color.Goldenrod,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                    Cursor = Cursors.SizeNWSE
                };

                header.MouseDown += DragStart;
                header.MouseMove += DragMove;
                header.MouseUp += DragEnd;
                MouseDown += DragStart;
                MouseMove += DragMove;
                MouseUp += DragEnd;
                txt.Leave += (s, e) => NoteChanged?.Invoke(this);
                txt.Enter += (s, e) => NoteSelected?.Invoke(this);
                txt.MouseDown += (s, e) => NoteSelected?.Invoke(this);
                resizeGrip.MouseDown += ResizeStart;
                resizeGrip.MouseMove += ResizeMove;
                resizeGrip.MouseUp += ResizeEnd;

                Resize += (s, e) => PositionResizeGrip();

                Controls.Add(txt);
                Controls.Add(header);
                Controls.Add(resizeGrip);
                PositionResizeGrip();
            }

            private void PositionResizeGrip()
            {
                resizeGrip.Location = new Point(Math.Max(0, Width - resizeGrip.Width - 1), Math.Max(0, Height - resizeGrip.Height - 1));
                resizeGrip.BringToFront();
            }

            private void DragStart(object sender, MouseEventArgs e)
            {
                if (resizing)
                    return;

                if (e.Button != MouseButtons.Left) return;
                NoteSelected?.Invoke(this);
                dragging = true;
                dragOffset = e.Location;
                BringToFront();
            }

            private void DragMove(object sender, MouseEventArgs e)
            {
                if (!dragging) return;
                var p = Location;
                p.Offset(e.X - dragOffset.X, e.Y - dragOffset.Y);
                Location = p;
            }

            private void DragEnd(object sender, MouseEventArgs e)
            {
                if (!dragging) return;
                dragging = false;
                NoteChanged?.Invoke(this);
            }

            private void ResizeStart(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left)
                    return;

                NoteSelected?.Invoke(this);
                resizing = true;
                dragging = false;
                resizeStartPoint = PointToScreen(e.Location);
                resizeStartSize = Size;
                BringToFront();
            }

            private void ResizeMove(object sender, MouseEventArgs e)
            {
                if (!resizing)
                    return;

                Point current = PointToScreen(e.Location);
                int width = Math.Max(120, resizeStartSize.Width + (current.X - resizeStartPoint.X));
                int height = Math.Max(80, resizeStartSize.Height + (current.Y - resizeStartPoint.Y));
                Size = new Size(width, height);
                PositionResizeGrip();
            }

            private void ResizeEnd(object sender, MouseEventArgs e)
            {
                if (!resizing)
                    return;

                resizing = false;
                NoteChanged?.Invoke(this);
            }

            public void SetSelected(bool selected)
            {
                BorderStyle = selected ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
                header.BackColor = selected ? Color.Gold : Color.Khaki;
            }
        }

        private class TurnPanelControl : Panel
        {
            private readonly Label lbl;
            private readonly Button btnNextTurn;
            public bool IsEnabled { get; private set; }
            public string ActiveUser { get; private set; } = string.Empty;
            public event Action NextTurnRequested;

            public TurnPanelControl()
            {
                Height = 76;
                BackColor = Color.AliceBlue;
                BorderStyle = BorderStyle.FixedSingle;
                lbl = new Label { Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0), Text = "Turn-based: OFF" };
                btnNextTurn = new Button { Dock = DockStyle.Bottom, Height = 30, Text = "Lượt kế tiếp" };
                btnNextTurn.Click += (s, e) => NextTurnRequested?.Invoke();
                Controls.Add(btnNextTurn);
                Controls.Add(lbl);
            }

            public void SetState(bool enabled, string activeUser, bool canAdvanceTurn)
            {
                IsEnabled = enabled;
                ActiveUser = activeUser ?? string.Empty;
                lbl.Text = enabled ? $"Turn-based: ON | Lượt: {ActiveUser}" : "Turn-based: OFF";
                btnNextTurn.Visible = enabled && canAdvanceTurn;
                btnNextTurn.Enabled = enabled && canAdvanceTurn;
            }
        }

        private class PlaybackPanelControl : Panel
        {
            public event Action RequestPlayback;
            public event Action RequestSnapshots;

            public PlaybackPanelControl()
            {
                Height = 44;
                BackColor = Color.Honeydew;
                BorderStyle = BorderStyle.FixedSingle;
                var btn = new Button { Text = "Phát lại", Width = 92, Height = 28, Location = new Point(8, 8) };
                btn.Click += (s, e) => RequestPlayback?.Invoke();
                var btnSnap = new Button { Text = "Xem lại (Snapshot)", Width = 150, Height = 28, Location = new Point(106, 8) };
                btnSnap.Click += (s, e) => RequestSnapshots?.Invoke();
                Controls.Add(btn);
                Controls.Add(btnSnap);
            }
        }
    } 
}
