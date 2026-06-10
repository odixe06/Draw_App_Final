using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using SharedLib.Payloads;

namespace DrawingClient.Drawing
{
    public class CanvasManager
    {
        private enum InteractiveObjectKind
        {
            None,
            Sticker,
            Image,
            Text
        }

        private enum ResizeHandleKind
        {
            None,
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }

        private sealed class CanvasImageObject
        {
            public string ActionID;
            public string Username;
            public Rectangle Bounds;
            public string ImageData;
            public Bitmap Bitmap;
            public long Timestamp;
        }

        private sealed class CanvasTextObject
        {
            public string ActionID;
            public string Username;
            public string Text;
            public int X;
            public int Y;
            public int ColorARGB;
            public string FontName;
            public int FontSize;
            public long Timestamp;
        }

        public static readonly Size DefaultCanvasSize = new Size(1920, 1080);

        private readonly PictureBox canvas;
        private Bitmap drawingSurface;
        private Graphics graphics;
        private Image backgroundImage;
        private float viewOffsetX;
        private float viewOffsetY;
        private Point previousPoint;
        private Point currentPoint;
        private bool isDrawing;
        private bool isPanning;
        private Point lastPanPoint;
        private readonly Dictionary<string, Point> remoteCursors = new Dictionary<string, Point>();
        private readonly object cursorLock = new object();
        private readonly List<StickerPayload> stickers = new List<StickerPayload>();
        private readonly Dictionary<string, int> stickerIndexById = new Dictionary<string, int>();
        private readonly HashSet<string> stickerIds = new HashSet<string>();
        private readonly object stickerLock = new object();
        private readonly List<CanvasImageObject> imageObjects = new List<CanvasImageObject>();
        private readonly Dictionary<string, int> imageIndexById = new Dictionary<string, int>();
        private readonly object imageLock = new object();
        private readonly List<CanvasTextObject> textObjects = new List<CanvasTextObject>();
        private readonly Dictionary<string, int> textIndexById = new Dictionary<string, int>();
        private readonly object textLock = new object();
        private InteractiveObjectKind activeObjectKind = InteractiveObjectKind.None;
        private string activeObjectId;
        private bool isManipulatingObject;
        private bool isResizingObject;
        private ResizeHandleKind activeResizeHandle = ResizeHandleKind.None;
        private Point manipulationStartPoint;
        private Rectangle manipulationStartBounds;
        private int manipulationStartFontSize;
        private float minimumZoomFactor = 0.2f;
        private string activeDrawingActionId;

        // Theo doi vung net ve tay gan day cua nguoi dung local (toa do canvas), phuc vu nhan dien hinh ve.
        private Rectangle recentSketchBounds = Rectangle.Empty;
        private bool hasRecentSketch;

        // Cong cu SmartPen (But thong minh): bat diem net ve tay trong khi keo,
        // nha chuot -> lam dep (lam muot/nan hinh) thanh net Pen that (tay duoc).
        private readonly List<Point> smartStrokePoints = new List<Point>();

        // Cong cu AiRegion: keo-tha chon mot vung chu nhat (toa do canvas) de gui AI ve lai thanh line art (object anh).
        private bool isSelectingAiRegion;
        private Point aiRegionStart;
        private Point aiRegionCurrent;
        /// <summary>Bao MainForm da chon xong mot vung (toa do canvas) bang cong cu AiRegion.</summary>
        public Action<Rectangle> OnAiRegionSelected;

        public ToolType CurrentTool { get; set; } = ToolType.Pen;
        public Color CurrentColor { get; set; } = Color.Black;
        public Color BackgroundColor { get; set; } = Color.White;
        public int PenWidth { get; set; } = 2;
        public float ZoomFactor { get; set; } = 1.0f;
        public Size CanvasSize => drawingSurface?.Size ?? Size.Empty;

        // ✅ Turn-based: false = bị chặn vẽ
        public bool IsDrawingEnabled { get; set; } = true;

        public UndoStack UndoHistory { get; private set; } = new UndoStack();
        private readonly TextTool textTool;
        public Action<Color> OnColorPicked;
        public Action<DrawPayload> OnNetworkDrawAction;
        public Action<FloodFillPayload> OnNetworkFloodFillAction;
        public Action<DrawPayload> OnNetworkTextAction;
        public Action<ImportImagePayload> OnNetworkImportImageAction;
        public Action<StickerPayload> OnNetworkStickerAction;
        public bool HasSelectedObject => activeObjectKind != InteractiveObjectKind.None && !string.IsNullOrWhiteSpace(activeObjectId);

        // ── Nhan dien hinh ve tay: theo doi vung va trich bitmap vung do ───────────
        public bool HasRecentSketch => hasRecentSketch;
        public Rectangle RecentSketchBounds => recentSketchBounds;

        public void ResetRecentSketch()
        {
            hasRecentSketch = false;
            recentSketchBounds = Rectangle.Empty;
        }

        public bool IsPointInRecentSketch(Point canvasPoint)
        {
            if (!hasRecentSketch) return false;
            return Rectangle.Inflate(recentSketchBounds, 24, 24).Contains(canvasPoint);
        }

        private void ExpandRecentSketch(Point a, Point b)
        {
            int minX = Math.Min(a.X, b.X), minY = Math.Min(a.Y, b.Y);
            int maxX = Math.Max(a.X, b.X), maxY = Math.Max(a.Y, b.Y);
            Rectangle seg = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
            recentSketchBounds = hasRecentSketch ? Rectangle.Union(recentSketchBounds, seg) : seg;
            hasRecentSketch = true;
        }

        private static Rectangle NormalizeRect(Point a, Point b)
        {
            int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        /// <summary>Render mot vung cua canvas (da gop nen + net ve + object) ra bitmap doc lap.</summary>
        public Bitmap RenderRegionToBitmap(Rectangle canvasRect)
        {
            if (drawingSurface == null) return null;
            Rectangle clamp = Rectangle.Intersect(canvasRect, new Rectangle(0, 0, drawingSurface.Width, drawingSurface.Height));
            if (clamp.Width <= 1 || clamp.Height <= 1) return null;
            using (Bitmap full = RenderToBitmap())
                return full.Clone(clamp, full.PixelFormat);
        }

        public CanvasManager(PictureBox pictureBox)
        {
            canvas = pictureBox;
            ResizeCanvas(DefaultCanvasSize.Width, DefaultCanvasSize.Height);

            textTool = new TextTool(canvas, DrawTextOnCanvas);

            canvas.MouseDown += Canvas_MouseDown;
            canvas.MouseMove += Canvas_MouseMove;
            canvas.MouseUp += Canvas_MouseUp;
            canvas.Paint += Canvas_Paint;
            canvas.Resize += (s, e) => FitToViewport();
        }

        public Point ScreenToCanvas(Point screenPoint)
        {
            return new Point(
                (int)((screenPoint.X - viewOffsetX) / ZoomFactor),
                (int)((screenPoint.Y - viewOffsetY) / ZoomFactor));
        }

        public void FitToViewport()
        {
            if (drawingSurface == null || canvas.ClientSize.Width <= 0 || canvas.ClientSize.Height <= 0)
                return;

            float scaleX = canvas.ClientSize.Width / (float)drawingSurface.Width;
            float scaleY = canvas.ClientSize.Height / (float)drawingSurface.Height;
            // Max zoom-out should still cover the whole visible area so every visible point is drawable.
            minimumZoomFactor = Math.Max(0.01f, Math.Max(scaleX, scaleY));
            ZoomFactor = Math.Max(minimumZoomFactor, ZoomFactor);

            ClampViewportOffsets();
            canvas.Invalidate();
        }

        public void PanBy(float deltaX, float deltaY)
        {
            viewOffsetX += deltaX;
            viewOffsetY += deltaY;
            ClampViewportOffsets();
            canvas.Invalidate();
        }

        public void ZoomAt(Point screenPoint, float delta)
        {
            float nextZoom = Math.Max(minimumZoomFactor, Math.Min(4f, ZoomFactor + delta));
            if (Math.Abs(nextZoom - ZoomFactor) < 0.0001f)
                return;

            PointF canvasPoint = new PointF(
                (screenPoint.X - viewOffsetX) / ZoomFactor,
                (screenPoint.Y - viewOffsetY) / ZoomFactor);

            ZoomFactor = nextZoom;
            viewOffsetX = screenPoint.X - canvasPoint.X * ZoomFactor;
            viewOffsetY = screenPoint.Y - canvasPoint.Y * ZoomFactor;
            ClampViewportOffsets();
            canvas.Invalidate();
        }

        private void ClampViewportOffsets()
        {
            if (drawingSurface == null || canvas.ClientSize.Width <= 0 || canvas.ClientSize.Height <= 0)
                return;

            float scaledWidth = drawingSurface.Width * ZoomFactor;
            float scaledHeight = drawingSurface.Height * ZoomFactor;

            if (scaledWidth <= canvas.ClientSize.Width)
            {
                viewOffsetX = (canvas.ClientSize.Width - scaledWidth) / 2f;
            }
            else
            {
                float minX = canvas.ClientSize.Width - scaledWidth;
                if (viewOffsetX < minX) viewOffsetX = minX;
                if (viewOffsetX > 0f) viewOffsetX = 0f;
            }

            if (scaledHeight <= canvas.ClientSize.Height)
            {
                viewOffsetY = (canvas.ClientSize.Height - scaledHeight) / 2f;
            }
            else
            {
                float minY = canvas.ClientSize.Height - scaledHeight;
                if (viewOffsetY < minY) viewOffsetY = minY;
                if (viewOffsetY > 0f) viewOffsetY = 0f;
            }
        }

        public void ResizeCanvas(int width, int height)
        {
            Bitmap newSurface = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(newSurface))
            {
                g.Clear(Color.Transparent);
                if (drawingSurface != null)
                {
                    g.DrawImage(drawingSurface, 0, 0);
                    drawingSurface.Dispose();
                }
            }
            drawingSurface = newSurface;
            graphics = Graphics.FromImage(drawingSurface);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            FitToViewport();
        }

        public void ApplyRemoteText(DrawPayload payload)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.Text))
                return;

            string actionId = string.IsNullOrWhiteSpace(payload.ActionID) ? Guid.NewGuid().ToString() : payload.ActionID;
            if (payload.IsDeleted)
            {
                lock (textLock)
                {
                    if (textIndexById.TryGetValue(actionId, out int deleteIndex) && deleteIndex >= 0 && deleteIndex < textObjects.Count)
                    {
                        textObjects.RemoveAt(deleteIndex);
                        RebuildTextIndexMap();
                    }
                }

                canvas.Invalidate();
                return;
            }

            var textObj = new CanvasTextObject
            {
                ActionID = actionId,
                Username = payload.Username ?? string.Empty,
                Text = payload.Text,
                X = payload.X1,
                Y = payload.Y1,
                ColorARGB = payload.ColorARGB,
                FontName = string.IsNullOrWhiteSpace(payload.FontName) ? "Arial" : payload.FontName,
                FontSize = payload.FontSize > 0 ? payload.FontSize : 14,
                Timestamp = payload.Timestamp
            };

            lock (textLock)
            {
                if (textIndexById.TryGetValue(actionId, out int index))
                {
                    textObjects[index] = textObj;
                }
                else
                {
                    textIndexById[actionId] = textObjects.Count;
                    textObjects.Add(textObj);
                }
            }

            canvas.Invalidate();
        }

        public void ApplyRemoteFloodFill(FloodFillPayload payload)
        {
            if (payload == null) return;
            if (payload.X < 0 || payload.Y < 0 || payload.X >= drawingSurface.Width || payload.Y >= drawingSurface.Height) return;
            FloodFillHelper.Apply(drawingSurface, new Point(payload.X, payload.Y), Color.FromArgb(payload.ColorARGB));
            canvas.Invalidate();
        }

        public void ApplyDrawAction(DrawAction action)
        {
            if (action == null) return;

            string tool = action.ToolType ?? string.Empty;
            if (tool.Equals("SetBackground", StringComparison.OrdinalIgnoreCase))
            {
                ApplyRemoteSetBackground(new SetBackgroundPayload
                {
                    ActionID = action.ActionID,
                    Username = action.Username,
                    ColorARGB = action.ColorARGB,
                    ImageData = action.ImageData,
                    Timestamp = action.Timestamp
                });
                return;
            }

            if (tool.Equals("Sticker", StringComparison.OrdinalIgnoreCase))
            {
                AddSticker(new StickerPayload
                {
                    ActionID = action.ActionID,
                    Username = action.Username,
                    StickerID = action.Text,
                    X = action.X1,
                    Y = action.Y1,
                    Width = action.ImageWidth > 0 ? action.ImageWidth : 64,
                    Height = action.ImageHeight > 0 ? action.ImageHeight : 64,
                    IsDeleted = action.IsDeleted,
                    Timestamp = action.Timestamp
                });
                return;
            }

            if (tool.Equals("ImportImage", StringComparison.OrdinalIgnoreCase))
            {
                ApplyRemoteImportImage(new ImportImagePayload
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
                return;
            }

            if (tool.Equals("FloodFill", StringComparison.OrdinalIgnoreCase))
            {
                ApplyRemoteFloodFill(new FloodFillPayload
                {
                    ActionID = action.ActionID,
                    Username = action.Username,
                    X = action.X1,
                    Y = action.Y1,
                    ColorARGB = action.ColorARGB,
                    Timestamp = action.Timestamp
                });
                return;
            }

            if (tool.Equals("Text", StringComparison.OrdinalIgnoreCase))
            {
                ApplyRemoteText(new DrawPayload
                {
                    ActionID = action.ActionID,
                    Username = action.Username,
                    X1 = action.X1,
                    Y1 = action.Y1,
                    Text = action.Text,
                    FontName = action.FontName,
                    FontSize = action.FontSize,
                    ColorARGB = action.ColorARGB,
                    IsDeleted = action.IsDeleted,
                    Timestamp = action.Timestamp
                });
                return;
            }

            ApplyRemoteDraw(new DrawPayload
            {
                ActionID = action.ActionID,
                Username = action.Username,
                ToolType = tool,
                X1 = action.X1,
                Y1 = action.Y1,
                X2 = action.X2,
                Y2 = action.Y2,
                ColorARGB = action.ColorARGB,
                Thickness = action.Thickness,
                IsDeleted = action.IsDeleted,
                Timestamp = action.Timestamp
            });
        }

        public void RenderActionHistory(IEnumerable<DrawAction> actions)
        {
            ApplyRemoteClearAll();
            if (actions == null)
                return;

            foreach (var action in actions)
                ApplyDrawAction(action);
        }

        public void ApplyRemoteImportImage(ImportImagePayload payload)
        {
            if (payload == null) return;
            try
            {
                string actionId = string.IsNullOrWhiteSpace(payload.ActionID) ? Guid.NewGuid().ToString() : payload.ActionID;
                if (payload.IsDeleted)
                {
                    lock (imageLock)
                    {
                        if (imageIndexById.TryGetValue(actionId, out int deleteIndex) && deleteIndex >= 0 && deleteIndex < imageObjects.Count)
                        {
                            imageObjects[deleteIndex].Bitmap?.Dispose();
                            imageObjects.RemoveAt(deleteIndex);
                            RebuildImageIndexMap();
                        }
                    }

                    canvas.Invalidate();
                    return;
                }

                if (string.IsNullOrWhiteSpace(payload.ImageData))
                    return;

                // I/O DU LIEU -> IMAGE: packet import image mang base64, decode thanh bytes roi tao Bitmap de render.
                byte[] bytes = Convert.FromBase64String(payload.ImageData);
                using (var ms = new MemoryStream(bytes))
                using (var img = new Bitmap(Image.FromStream(ms)))
                {
                    var obj = new CanvasImageObject
                    {
                        ActionID = actionId,
                        Username = payload.Username ?? string.Empty,
                        Bounds = new Rectangle(payload.X, payload.Y, Math.Max(50, payload.Width), Math.Max(50, payload.Height)),
                        ImageData = payload.ImageData,
                        Bitmap = new Bitmap(img),
                        Timestamp = payload.Timestamp
                    };

                    lock (imageLock)
                    {
                        if (imageIndexById.TryGetValue(actionId, out int index))
                        {
                            imageObjects[index].Bitmap?.Dispose();
                            imageObjects[index] = obj;
                        }
                        else
                        {
                            imageIndexById[actionId] = imageObjects.Count;
                            imageObjects.Add(obj);
                        }
                    }
                }
                canvas.Invalidate();
            }
            catch { }
        }

        public void ApplyRemoteSetBackground(SetBackgroundPayload payload)
        {
            if (payload == null) return;
            BackgroundColor = Color.FromArgb(payload.ColorARGB);
            SetBackgroundImageFromBase64(payload.ImageData);
            canvas.Invalidate();
        }

        public void ApplyRemoteClearAll()
        {
            try
            {
                graphics.Clear(Color.Transparent);
                ClearStickers();
                ClearTextObjects();
                ClearImageObjects();
                BackgroundColor = Color.White;
                ClearBackgroundImage();
                ResetRecentSketch();
                canvas.Invalidate();
            }
            catch { }
        }

        public void AddSticker(StickerPayload payload)
        {
            if (payload == null) return;
            lock (stickerLock)
            {
                string actionId = string.IsNullOrWhiteSpace(payload.ActionID) ? Guid.NewGuid().ToString() : payload.ActionID;
                payload.ActionID = actionId;
                if (payload.IsDeleted)
                {
                    if (stickerIndexById.TryGetValue(actionId, out int deleteIndex) && deleteIndex >= 0 && deleteIndex < stickers.Count)
                    {
                        stickers.RemoveAt(deleteIndex);
                        RebuildStickerIndexMap();
                    }

                    if (activeObjectKind == InteractiveObjectKind.Sticker && string.Equals(activeObjectId, actionId, StringComparison.OrdinalIgnoreCase))
                    {
                        activeObjectKind = InteractiveObjectKind.None;
                        activeObjectId = null;
                    }

                    canvas.Invalidate();
                    return;
                }

                if (stickerIndexById.TryGetValue(actionId, out int index))
                {
                    stickers[index] = payload;
                }
                else
                {
                    stickerIds.Add(actionId);
                    stickerIndexById[actionId] = stickers.Count;
                    stickers.Add(payload);
                }
            }
            canvas.Invalidate();
        }

        public void ClearStickers()
        {
            lock (stickerLock)
            {
                stickers.Clear();
                stickerIds.Clear();
                stickerIndexById.Clear();
            }
            canvas.Invalidate();
        }

        private void RebuildStickerIndexMap()
        {
            stickerIndexById.Clear();
            stickerIds.Clear();
            for (int i = 0; i < stickers.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(stickers[i].ActionID))
                {
                    stickerIndexById[stickers[i].ActionID] = i;
                    stickerIds.Add(stickers[i].ActionID);
                }
            }
        }

        private void ClearTextObjects()
        {
            lock (textLock)
            {
                textObjects.Clear();
                textIndexById.Clear();
            }
        }

        private void RebuildTextIndexMap()
        {
            textIndexById.Clear();
            for (int i = 0; i < textObjects.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(textObjects[i].ActionID))
                    textIndexById[textObjects[i].ActionID] = i;
            }
        }

        private void ClearImageObjects()
        {
            lock (imageLock)
            {
                foreach (var img in imageObjects)
                    img.Bitmap?.Dispose();

                imageObjects.Clear();
                imageIndexById.Clear();
            }
        }

        private void RebuildImageIndexMap()
        {
            imageIndexById.Clear();
            for (int i = 0; i < imageObjects.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(imageObjects[i].ActionID))
                    imageIndexById[imageObjects[i].ActionID] = i;
            }
        }

        // BỌC TRY-CATCH CHO UNDO, REDO VÀ CLEAR ALL
        public void Undo()
        {
            try
            {
                if (UndoHistory != null && UndoHistory.CanUndo)
                {
                    Bitmap current = drawingSurface;
                    Bitmap previous = UndoHistory.Undo(current);
                    if (previous != null)
                    {
                        drawingSurface = previous;
                        graphics = Graphics.FromImage(drawingSurface);
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        canvas.Invalidate();
                    }
                }
            }
            catch { }
        }

        public void Redo()
        {
            try
            {
                if (UndoHistory != null && UndoHistory.CanRedo)
                {
                    Bitmap current = drawingSurface;
                    Bitmap next = UndoHistory.Redo(current);
                    if (next != null)
                    {
                        drawingSurface = next;
                        graphics = Graphics.FromImage(drawingSurface);
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        canvas.Invalidate();
                    }
                }
            }
            catch { }
        }

        public void ClearAll()
        {
            try { UndoHistory?.Push(drawingSurface); } catch { }
            try
            {
                graphics.Clear(Color.Transparent);
                ClearStickers();
                ClearTextObjects();
                ClearImageObjects();
                BackgroundColor = Color.White;
                ClearBackgroundImage();
                ResetRecentSketch();
                canvas.Invalidate();
            }
            catch { }
        }

        public void ImportImage(Image image, Rectangle targetRect, string actionId = null, string username = null, long timestamp = 0)
        {
            if (image == null) return;
            try { UndoHistory?.Push(drawingSurface); } catch { }
            string effectiveActionId = string.IsNullOrWhiteSpace(actionId) ? Guid.NewGuid().ToString() : actionId;
            string imageData;

            // I/O DU LIEU -> NETWORK/DB: anh import trong RAM duoc ma hoa thanh PNG base64 de sync/replay.
            using (var ms = new MemoryStream())
            {
                image.Save(ms, ImageFormat.Png);
                imageData = Convert.ToBase64String(ms.ToArray());
            }

            ApplyRemoteImportImage(new ImportImagePayload
            {
                ActionID = effectiveActionId,
                Username = username,
                X = targetRect.X,
                Y = targetRect.Y,
                Width = targetRect.Width,
                Height = targetRect.Height,
                ImageData = imageData,
                Timestamp = timestamp > 0 ? timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            canvas.Invalidate();
        }

        public void ExportImage(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || drawingSurface == null) return;

            // I/O DU LIEU -> FILE: render toan bo canvas/object layer thanh Bitmap roi ghi ra file local.
            using (Bitmap exportBmp = RenderToBitmap())
                exportBmp.Save(filePath);
        }

        public string ExportPngBase64(int maxWidth = 0, int maxHeight = 0)
        {
            // I/O DU LIEU -> NETWORK/DB: render canvas thanh PNG trong MemoryStream roi base64 hoa de gui/luu gallery.
            using (Bitmap exportBmp = RenderToBitmap())
            using (Bitmap output = ResizeForExport(exportBmp, maxWidth, maxHeight))
            using (MemoryStream ms = new MemoryStream())
            {
                output.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        private Bitmap RenderToBitmap()
        {
            Bitmap exportBmp = new Bitmap(drawingSurface.Width, drawingSurface.Height);
            using (Graphics g = Graphics.FromImage(exportBmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(BackgroundColor);
                DrawBackgroundImage(g);
                g.DrawImage(drawingSurface, 0, 0);
                lock (imageLock)
                {
                    foreach (var imageObj in imageObjects)
                        g.DrawImage(imageObj.Bitmap, imageObj.Bounds);
                }
                lock (textLock)
                {
                    foreach (var textObj in textObjects)
                        DrawTextObject(g, textObj);
                }
                lock (stickerLock)
                {
                    foreach (var sticker in stickers)
                        DrawSticker(g, sticker);
                }
            }
            return exportBmp;
        }

        private static Bitmap ResizeForExport(Bitmap source, int maxWidth, int maxHeight)
        {
            if (maxWidth <= 0 || maxHeight <= 0 || (source.Width <= maxWidth && source.Height <= maxHeight))
                return new Bitmap(source);

            float scale = Math.Min(maxWidth / (float)source.Width, maxHeight / (float)source.Height);
            int width = Math.Max(1, (int)(source.Width * scale));
            int height = Math.Max(1, (int)(source.Height * scale));
            Bitmap resized = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(source, new Rectangle(0, 0, width, height));
            }
            return resized;
        }

        public void ApplyRemoteDraw(DrawPayload payload)
        {
            if (payload == null) return;
            using (Pen pen = new Pen(Color.FromArgb(payload.ColorARGB), payload.Thickness > 0 ? payload.Thickness : 2))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap   = LineCap.Round;

                Point p1 = new Point(payload.X1, payload.Y1);
                Point p2 = new Point(payload.X2, payload.Y2);

                switch (payload.ToolType?.ToLower())
                {
                    case "rectangle":
                        var rRect = BuildRectangle(p1, p2);
                        graphics.DrawRectangle(pen, rRect);
                        break;

                    case "circle":
                        var cRect = BuildRectangle(p1, p2);
                        graphics.DrawEllipse(pen, cRect);
                        break;

                    case "line":
                        graphics.DrawLine(pen, p1, p2);
                        break;

                    case "eraser":
                        using (Pen eraserPen = new Pen(Color.Transparent, payload.Thickness > 0 ? payload.Thickness * 3 : 12))
                        {
                            eraserPen.StartCap = LineCap.Round;
                            eraserPen.EndCap   = LineCap.Round;
                            CompositingMode oldMode = graphics.CompositingMode;
                            graphics.CompositingMode = CompositingMode.SourceCopy;
                            graphics.DrawLine(eraserPen, p1, p2);
                            graphics.CompositingMode = oldMode;
                        }
                        break;

                    default: // Pen, Spray, v.v.
                        graphics.DrawLine(pen, p1, p2);
                        break;
                }
            }
            canvas.Invalidate();
        }

        public void UpdateRemoteCursor(string username, Point point)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            lock (cursorLock) { remoteCursors[username] = point; }
            canvas.Invalidate();
        }

        public void RemoveRemoteCursor(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            lock (cursorLock)
            {
                if (remoteCursors.ContainsKey(username)) remoteCursors.Remove(username);
            }
            canvas.Invalidate();
        }

        private void Canvas_Paint(object sender, PaintEventArgs e)
        {
            if (drawingSurface != null)
            {
                e.Graphics.Transform = new Matrix(ZoomFactor, 0, 0, ZoomFactor, viewOffsetX, viewOffsetY);
                using (var bgBrush = new SolidBrush(BackgroundColor))
                {
                    e.Graphics.FillRectangle(bgBrush, new Rectangle(0, 0, drawingSurface.Width, drawingSurface.Height));
                }
                DrawBackgroundImage(e.Graphics);
                e.Graphics.DrawImage(drawingSurface, Point.Empty);
                lock (imageLock)
                {
                    foreach (var imageObj in imageObjects)
                        e.Graphics.DrawImage(imageObj.Bitmap, imageObj.Bounds);
                }

                lock (textLock)
                {
                    foreach (var textObj in textObjects)
                        DrawTextObject(e.Graphics, textObj);
                }

                if (isDrawing && (CurrentTool == ToolType.Line || CurrentTool == ToolType.Rectangle || CurrentTool == ToolType.Circle))
                {
                    using (Pen previewPen = new Pen(Color.FromArgb(160, CurrentColor), PenWidth))
                    {
                        previewPen.DashStyle = DashStyle.Dash;
                        DrawShape(e.Graphics, previewPen, previousPoint, GetShapeEndPoint(previousPoint, currentPoint, CurrentTool), CurrentTool);
                    }
                }

                if (isDrawing && CurrentTool == ToolType.SmartPen && smartStrokePoints.Count > 1)
                {
                    // Preview net tho (mo nhe) trong khi keo; nha chuot se thay bang net da lam dep.
                    using (Pen previewPen = new Pen(Color.FromArgb(150, CurrentColor), PenWidth))
                    {
                        previewPen.StartCap = LineCap.Round;
                        previewPen.EndCap = LineCap.Round;
                        previewPen.LineJoin = LineJoin.Round;
                        e.Graphics.DrawLines(previewPen, smartStrokePoints.ToArray());
                    }
                }

                if (isSelectingAiRegion && CurrentTool == ToolType.AiRegion)
                {
                    Rectangle sel = NormalizeRect(aiRegionStart, aiRegionCurrent);
                    using (Brush fill = new SolidBrush(Color.FromArgb(40, Color.DodgerBlue)))
                    using (Pen marquee = new Pen(Color.FromArgb(220, Color.DodgerBlue), 1.5f / Math.Max(0.01f, ZoomFactor)) { DashStyle = DashStyle.Dash })
                    {
                        e.Graphics.FillRectangle(fill, sel);
                        e.Graphics.DrawRectangle(marquee, sel);
                    }
                }

                lock (cursorLock)
                {
                    foreach (var cursor in remoteCursors)
                    {
                        using (Brush b = new SolidBrush(Color.FromArgb(180, Color.MediumPurple)))
                        using (Font f = new Font("Arial", 8, FontStyle.Bold))
                        using (Brush t = new SolidBrush(Color.Black))
                        {
                            e.Graphics.FillEllipse(b, cursor.Value.X - 4, cursor.Value.Y - 4, 8, 8);
                            e.Graphics.DrawString(cursor.Key, f, t, cursor.Value.X + 8, cursor.Value.Y - 6);
                        }
                    }
                }

                lock (stickerLock)
                {
                    foreach (var sticker in stickers)
                    {
                        DrawSticker(e.Graphics, sticker);
                    }
                }

                DrawActiveObjectSelection(e.Graphics);
            }
        }

        private static void DrawTextObject(Graphics g, CanvasTextObject textObj)
        {
            using (Font font = new Font(string.IsNullOrWhiteSpace(textObj.FontName) ? "Arial" : textObj.FontName, Math.Max(8, textObj.FontSize)))
            using (Brush brush = new SolidBrush(Color.FromArgb(textObj.ColorARGB)))
            {
                g.DrawString(textObj.Text ?? string.Empty, font, brush, textObj.X, textObj.Y);
            }
        }

        private static void DrawSticker(Graphics g, StickerPayload sticker)
        {
            Rectangle rect = new Rectangle(sticker.X, sticker.Y, Math.Max(24, sticker.Width), Math.Max(24, sticker.Height));
            string glyph = GetStickerGlyph(sticker.StickerID);

            using (Font font = new Font("Segoe UI Emoji", Math.Max(12, rect.Height - 6), FontStyle.Regular, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                if (Math.Abs(sticker.Rotation) > 0.01f)
                {
                    var state = g.Save();
                    g.TranslateTransform(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
                    g.RotateTransform(sticker.Rotation);
                    g.DrawString(glyph, font, Brushes.Black, new RectangleF(-rect.Width / 2f, -rect.Height / 2f, rect.Width, rect.Height), sf);
                    g.Restore(state);
                }
                else
                {
                    g.DrawString(glyph, font, Brushes.Black, rect, sf);
                }
            }
        }

        private static string GetStickerGlyph(string stickerId)
        {
            switch (stickerId)
            {
                case "heart": return "❤️";
                case "star": return "⭐";
                case "fire": return "🔥";
                case "idea": return "💡";
                case "check": return "✅";
                default: return "📌";
            }
        }

        private void DrawTextOnCanvas(string text, Point location, Color color)
        {
            if (!IsDrawingEnabled) return;

            try { UndoHistory?.Push(drawingSurface); } catch { }
            var payload = new DrawPayload
            {
                ActionID = Guid.NewGuid().ToString(),
                ToolType = ToolType.Text.ToString(),
                X1 = location.X,
                Y1 = location.Y,
                Text = text,
                FontName = "Arial",
                FontSize = 14,
                ColorARGB = color.ToArgb(),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            ApplyRemoteText(payload);
            OnNetworkTextAction?.Invoke(payload);
            canvas.Invalidate();
        }

        private void Canvas_MouseDown(object sender, MouseEventArgs e)
        {
            // ✅ Turn-based: chặn vẽ nếu không phải lượt của mình
            if (!IsDrawingEnabled && e.Button == MouseButtons.Left)
                return;

            if (CurrentTool == ToolType.Mouse)
            {
                if (e.Button == MouseButtons.Left)
                {
                    Point actualPoint = ScreenToCanvas(e.Location);
                    if (!TryStartObjectManipulation(actualPoint))
                    {
                        isPanning = true;
                        lastPanPoint = e.Location;
                        canvas.Cursor = Cursors.Hand;
                    }
                }
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                Point actualPoint = ScreenToCanvas(e.Location);

                if (CurrentTool == ToolType.AiRegion)
                {
                    isSelectingAiRegion = true;
                    aiRegionStart = actualPoint;
                    aiRegionCurrent = actualPoint;
                    canvas.Invalidate();
                    return;
                }

                if (CurrentTool == ToolType.Pipette)
                {
                    if (actualPoint.X >= 0 && actualPoint.X < drawingSurface.Width && actualPoint.Y >= 0 && actualPoint.Y < drawingSurface.Height)
                    {
                        Color pickedColor = drawingSurface.GetPixel(actualPoint.X, actualPoint.Y);
                        CurrentColor = pickedColor;
                        OnColorPicked?.Invoke(pickedColor);
                    }
                    return;
                }

                if (CurrentTool == ToolType.FloodFill)
                {
                    try { UndoHistory?.Push(drawingSurface); } catch { }
                    FloodFillHelper.Apply(drawingSurface, actualPoint, CurrentColor);
                    OnNetworkFloodFillAction?.Invoke(new FloodFillPayload
                    {
                        ActionID = Guid.NewGuid().ToString(),
                        X = actualPoint.X,
                        Y = actualPoint.Y,
                        ColorARGB = CurrentColor.ToArgb(),
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                    canvas.Invalidate();
                    return;
                }

                if (CurrentTool == ToolType.Text)
                {
                    textTool.StartTyping(actualPoint, e.Location, CurrentColor);
                    return;
                }

                isDrawing = true;
                previousPoint = actualPoint;
                currentPoint = actualPoint;
                activeDrawingActionId = Guid.NewGuid().ToString();
                try { UndoHistory?.Push(drawingSurface); } catch { }

                if (CurrentTool == ToolType.SmartPen)
                {
                    smartStrokePoints.Clear();
                    smartStrokePoints.Add(actualPoint);
                }
            }

        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (CurrentTool == ToolType.Mouse)
            {
                Point mousePoint = ScreenToCanvas(e.Location);

                if (isManipulatingObject)
                {
                    UpdateObjectManipulation(mousePoint);
                    return;
                }

                if (!isPanning)
                {
                    UpdateMouseToolCursor(mousePoint);
                }

                if (isPanning && e.Button == MouseButtons.Left)
                {
                    PanBy(e.Location.X - lastPanPoint.X, e.Location.Y - lastPanPoint.Y);
                    lastPanPoint = e.Location;
                }
                return;
            }

            Point actualPoint = ScreenToCanvas(e.Location);
            currentPoint = actualPoint;

            if (isSelectingAiRegion && CurrentTool == ToolType.AiRegion)
            {
                aiRegionCurrent = actualPoint;
                canvas.Invalidate();
                return;
            }

            if (isDrawing && CurrentTool == ToolType.SmartPen)
            {
                // Chi bat diem + ve preview (Canvas_Paint); KHONG ve thang/broadcast net tho.
                if (smartStrokePoints.Count == 0 || Distance(smartStrokePoints[smartStrokePoints.Count - 1], actualPoint) >= 1.2f)
                    smartStrokePoints.Add(actualPoint);
                previousPoint = actualPoint;
                canvas.Invalidate();
                return;
            }

            if (isDrawing && (CurrentTool == ToolType.Pen || CurrentTool == ToolType.Eraser))
            {
                Color penColor = CurrentTool == ToolType.Eraser ? BackgroundColor : CurrentColor;
                using (Pen pen = new Pen(CurrentTool == ToolType.Eraser ? Color.Transparent : penColor, CurrentTool == ToolType.Eraser ? PenWidth * 3 : PenWidth))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    CompositingMode oldMode = graphics.CompositingMode;
                    if (CurrentTool == ToolType.Eraser)
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.DrawLine(pen, previousPoint, actualPoint);
                    graphics.CompositingMode = oldMode;
                }

                SendNetworkDrawAction(previousPoint, actualPoint, penColor, PenWidth, CurrentTool);
                if (CurrentTool == ToolType.Pen)
                    ExpandRecentSketch(previousPoint, actualPoint);
                previousPoint = actualPoint;
                canvas.Invalidate();
            }
            else if (isDrawing && (CurrentTool == ToolType.Line || CurrentTool == ToolType.Rectangle || CurrentTool == ToolType.Circle))
            {
                canvas.Invalidate();
            }

        }

        private void UpdateMouseToolCursor(Point canvasPoint)
        {
            if (TryGetResizeHandleAtPoint(activeObjectKind, activeObjectId, canvasPoint, out var handleKind, out _))
            {
                canvas.Cursor = (handleKind == ResizeHandleKind.TopRight || handleKind == ResizeHandleKind.BottomLeft)
                    ? Cursors.SizeNESW
                    : Cursors.SizeNWSE;
                return;
            }

            if (TryHitTestObject(canvasPoint, out _, out _))
            {
                canvas.Cursor = Cursors.SizeAll;
                return;
            }

            canvas.Cursor = Cursors.Default;
        }

        private void Canvas_MouseUp(object sender, MouseEventArgs e)
        {
            if (CurrentTool == ToolType.Mouse)
            {
                if (isManipulatingObject)
                {
                    CommitObjectManipulation();
                    isManipulatingObject = false;
                    isResizingObject = false;
                    activeResizeHandle = ResizeHandleKind.None;
                    return;
                }

                if (e.Button == MouseButtons.Left && isPanning)
                {
                    isPanning = false;
                    canvas.Cursor = Cursors.Default;
                }
                return;
            }

            if (CurrentTool == ToolType.AiRegion)
            {
                if (isSelectingAiRegion)
                {
                    isSelectingAiRegion = false;
                    Rectangle sel = NormalizeRect(aiRegionStart, aiRegionCurrent);
                    canvas.Invalidate();
                    if (sel.Width >= 8 && sel.Height >= 8)
                        OnAiRegionSelected?.Invoke(sel);
                }
                return;
            }

            if (isDrawing && CurrentTool == ToolType.SmartPen)
            {
                isDrawing = false;
                var raw = new List<Point>(smartStrokePoints);
                smartStrokePoints.Clear();
                CommitSmartStroke(raw);
                activeDrawingActionId = null;
                canvas.Invalidate();
                return;
            }

            if (isDrawing && (CurrentTool == ToolType.Line || CurrentTool == ToolType.Rectangle || CurrentTool == ToolType.Circle))
            {
                Point finalPoint = GetShapeEndPoint(previousPoint, currentPoint, CurrentTool);
                using (Pen pen = new Pen(CurrentColor, PenWidth))
                {
                    DrawShape(graphics, pen, previousPoint, finalPoint, CurrentTool);
                }
                SendNetworkDrawAction(previousPoint, finalPoint, CurrentColor, PenWidth, CurrentTool);
                ExpandRecentSketch(previousPoint, finalPoint);
                canvas.Invalidate();
            }

            isDrawing = false;
            activeDrawingActionId = null;

        }

        private bool TryStartObjectManipulation(Point canvasPoint)
        {
            if (TryGetResizeHandleAtPoint(activeObjectKind, activeObjectId, canvasPoint, out var handleKind, out _))
            {
                isManipulatingObject = true;
                isResizingObject = true;
                activeResizeHandle = handleKind;
                manipulationStartPoint = canvasPoint;
                CaptureManipulationStartState();
                canvas.Cursor = (activeResizeHandle == ResizeHandleKind.TopRight || activeResizeHandle == ResizeHandleKind.BottomLeft)
                    ? Cursors.SizeNESW
                    : Cursors.SizeNWSE;
                return true;
            }

            if (TryHitTestObject(canvasPoint, out var kind, out var objectId))
            {
                activeObjectKind = kind;
                activeObjectId = objectId;
                isManipulatingObject = true;
                isResizingObject = false;
                activeResizeHandle = ResizeHandleKind.None;
                manipulationStartPoint = canvasPoint;
                CaptureManipulationStartState();
                canvas.Cursor = Cursors.SizeAll;
                canvas.Invalidate();
                return true;
            }

            activeObjectKind = InteractiveObjectKind.None;
            activeObjectId = null;
            activeResizeHandle = ResizeHandleKind.None;
            canvas.Invalidate();
            return false;
        }

        private void CaptureManipulationStartState()
        {
            manipulationStartBounds = Rectangle.Empty;
            manipulationStartFontSize = 14;

            if (activeObjectKind == InteractiveObjectKind.Sticker)
            {
                lock (stickerLock)
                {
                    if (TryGetStickerById(activeObjectId, out var sticker))
                    {
                        manipulationStartBounds = new Rectangle(sticker.X, sticker.Y, Math.Max(24, sticker.Width), Math.Max(24, sticker.Height));
                    }
                }
            }
            else if (activeObjectKind == InteractiveObjectKind.Image)
            {
                lock (imageLock)
                {
                    if (TryGetImageById(activeObjectId, out var imageObj))
                    {
                        manipulationStartBounds = imageObj.Bounds;
                    }
                }
            }
            else if (activeObjectKind == InteractiveObjectKind.Text)
            {
                lock (textLock)
                {
                    if (TryGetTextById(activeObjectId, out var textObj))
                    {
                        manipulationStartBounds = MeasureTextBounds(textObj);
                        manipulationStartFontSize = Math.Max(8, textObj.FontSize);
                    }
                }
            }
        }

        private void UpdateObjectManipulation(Point canvasPoint)
        {
            int dx = canvasPoint.X - manipulationStartPoint.X;
            int dy = canvasPoint.Y - manipulationStartPoint.Y;

            if (activeObjectKind == InteractiveObjectKind.Sticker)
            {
                lock (stickerLock)
                {
                    if (!TryGetStickerById(activeObjectId, out var sticker))
                        return;

                    if (isResizingObject)
                    {
                        Rectangle resized = ComputeResizedBounds(manipulationStartBounds, dx, dy, activeResizeHandle, 24, 24);
                        sticker.X = resized.X;
                        sticker.Y = resized.Y;
                        sticker.Width = resized.Width;
                        sticker.Height = resized.Height;
                    }
                    else
                    {
                        sticker.X = manipulationStartBounds.X + dx;
                        sticker.Y = manipulationStartBounds.Y + dy;
                    }
                }
            }
            else if (activeObjectKind == InteractiveObjectKind.Image)
            {
                lock (imageLock)
                {
                    if (!TryGetImageById(activeObjectId, out var imageObj))
                        return;

                    if (isResizingObject)
                    {
                        imageObj.Bounds = ComputeResizedBounds(manipulationStartBounds, dx, dy, activeResizeHandle, 50, 50);
                    }
                    else
                    {
                        imageObj.Bounds = new Rectangle(
                            manipulationStartBounds.X + dx,
                            manipulationStartBounds.Y + dy,
                            manipulationStartBounds.Width,
                            manipulationStartBounds.Height);
                    }
                }
            }
            else if (activeObjectKind == InteractiveObjectKind.Text)
            {
                lock (textLock)
                {
                    if (!TryGetTextById(activeObjectId, out var textObj))
                        return;

                    if (isResizingObject)
                    {
                        int sizeDelta = ComputeTextResizeDelta(dx, dy, activeResizeHandle);
                        int nextSize = manipulationStartFontSize + sizeDelta;
                        textObj.FontSize = Math.Max(8, Math.Min(96, nextSize));
                    }
                    else
                    {
                        textObj.X = manipulationStartBounds.X + dx;
                        textObj.Y = manipulationStartBounds.Y + dy;
                    }
                }
            }

            canvas.Invalidate();
        }

        private void CommitObjectManipulation()
        {
            canvas.Cursor = Cursors.Default;

            if (activeObjectKind == InteractiveObjectKind.Sticker)
            {
                StickerPayload payload = null;
                lock (stickerLock)
                {
                    if (TryGetStickerById(activeObjectId, out var sticker))
                    {
                        payload = new StickerPayload
                        {
                            ActionID = sticker.ActionID,
                            Username = sticker.Username,
                            StickerID = sticker.StickerID,
                            X = sticker.X,
                            Y = sticker.Y,
                            Width = sticker.Width,
                            Height = sticker.Height,
                            Rotation = sticker.Rotation,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                    }
                }

                if (payload != null)
                    OnNetworkStickerAction?.Invoke(payload);
            }
            else if (activeObjectKind == InteractiveObjectKind.Image)
            {
                ImportImagePayload payload = null;
                lock (imageLock)
                {
                    if (TryGetImageById(activeObjectId, out var imageObj))
                    {
                        payload = new ImportImagePayload
                        {
                            ActionID = imageObj.ActionID,
                            Username = imageObj.Username,
                            X = imageObj.Bounds.X,
                            Y = imageObj.Bounds.Y,
                            Width = imageObj.Bounds.Width,
                            Height = imageObj.Bounds.Height,
                            ImageData = imageObj.ImageData,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                    }
                }

                if (payload != null)
                    OnNetworkImportImageAction?.Invoke(payload);
            }
            else if (activeObjectKind == InteractiveObjectKind.Text)
            {
                DrawPayload payload = null;
                lock (textLock)
                {
                    if (TryGetTextById(activeObjectId, out var textObj))
                    {
                        payload = new DrawPayload
                        {
                            ActionID = textObj.ActionID,
                            Username = textObj.Username,
                            ToolType = ToolType.Text.ToString(),
                            X1 = textObj.X,
                            Y1 = textObj.Y,
                            Text = textObj.Text,
                            FontName = textObj.FontName,
                            FontSize = textObj.FontSize,
                            ColorARGB = textObj.ColorARGB,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                    }
                }

                if (payload != null)
                    OnNetworkTextAction?.Invoke(payload);
            }
        }

        public bool DeleteSelectedObject()
        {
            if (!HasSelectedObject)
                return false;

            string deletingId = activeObjectId;
            InteractiveObjectKind deletingKind = activeObjectKind;

            if (deletingKind == InteractiveObjectKind.Sticker)
            {
                StickerPayload deletePayload = null;
                lock (stickerLock)
                {
                    if (!TryGetStickerById(deletingId, out var sticker))
                        return false;

                    deletePayload = new StickerPayload
                    {
                        ActionID = sticker.ActionID,
                        Username = sticker.Username,
                        StickerID = sticker.StickerID,
                        X = sticker.X,
                        Y = sticker.Y,
                        Width = sticker.Width,
                        Height = sticker.Height,
                        Rotation = sticker.Rotation,
                        IsDeleted = true,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                }

                AddSticker(deletePayload);
                OnNetworkStickerAction?.Invoke(deletePayload);
            }
            else if (deletingKind == InteractiveObjectKind.Image)
            {
                ImportImagePayload deletePayload = null;
                lock (imageLock)
                {
                    if (!TryGetImageById(deletingId, out var imageObj))
                        return false;

                    deletePayload = new ImportImagePayload
                    {
                        ActionID = imageObj.ActionID,
                        Username = imageObj.Username,
                        X = imageObj.Bounds.X,
                        Y = imageObj.Bounds.Y,
                        Width = imageObj.Bounds.Width,
                        Height = imageObj.Bounds.Height,
                        ImageData = imageObj.ImageData,
                        IsDeleted = true,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                }

                ApplyRemoteImportImage(deletePayload);
                OnNetworkImportImageAction?.Invoke(deletePayload);
            }
            else if (deletingKind == InteractiveObjectKind.Text)
            {
                DrawPayload deletePayload = null;
                lock (textLock)
                {
                    if (!TryGetTextById(deletingId, out var textObj))
                        return false;

                    deletePayload = new DrawPayload
                    {
                        ActionID = textObj.ActionID,
                        Username = textObj.Username,
                        ToolType = ToolType.Text.ToString(),
                        X1 = textObj.X,
                        Y1 = textObj.Y,
                        Text = textObj.Text,
                        FontName = textObj.FontName,
                        FontSize = textObj.FontSize,
                        ColorARGB = textObj.ColorARGB,
                        IsDeleted = true,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                }

                ApplyRemoteText(deletePayload);
                OnNetworkTextAction?.Invoke(deletePayload);
            }
            else
            {
                return false;
            }

            activeObjectKind = InteractiveObjectKind.None;
            activeObjectId = null;
            isManipulatingObject = false;
            isResizingObject = false;
            activeResizeHandle = ResizeHandleKind.None;
            canvas.Cursor = Cursors.Default;
            canvas.Invalidate();
            return true;
        }

        public bool TryGetSelectedImagePayload(out ImportImagePayload payload)
        {
            payload = null;
            if (activeObjectKind != InteractiveObjectKind.Image || string.IsNullOrWhiteSpace(activeObjectId))
                return false;

            lock (imageLock)
            {
                if (!TryGetImageById(activeObjectId, out var imageObj))
                    return false;

                payload = new ImportImagePayload
                {
                    ActionID = imageObj.ActionID,
                    Username = imageObj.Username,
                    X = imageObj.Bounds.X,
                    Y = imageObj.Bounds.Y,
                    Width = imageObj.Bounds.Width,
                    Height = imageObj.Bounds.Height,
                    ImageData = imageObj.ImageData,
                    Timestamp = imageObj.Timestamp
                };
                return true;
            }
        }

        private bool TryHitTestObject(Point canvasPoint, out InteractiveObjectKind kind, out string objectId)
        {
            lock (stickerLock)
            {
                for (int i = stickers.Count - 1; i >= 0; i--)
                {
                    var s = stickers[i];
                    Rectangle r = new Rectangle(s.X, s.Y, Math.Max(24, s.Width), Math.Max(24, s.Height));
                    if (r.Contains(canvasPoint))
                    {
                        kind = InteractiveObjectKind.Sticker;
                        objectId = s.ActionID;
                        return true;
                    }
                }
            }

            lock (imageLock)
            {
                for (int i = imageObjects.Count - 1; i >= 0; i--)
                {
                    var img = imageObjects[i];
                    if (img.Bounds.Contains(canvasPoint))
                    {
                        kind = InteractiveObjectKind.Image;
                        objectId = img.ActionID;
                        return true;
                    }
                }
            }

            lock (textLock)
            {
                for (int i = textObjects.Count - 1; i >= 0; i--)
                {
                    var txt = textObjects[i];
                    if (MeasureTextBounds(txt).Contains(canvasPoint))
                    {
                        kind = InteractiveObjectKind.Text;
                        objectId = txt.ActionID;
                        return true;
                    }
                }
            }

            kind = InteractiveObjectKind.None;
            objectId = null;
            return false;
        }

        private bool TryGetResizeHandleAtPoint(InteractiveObjectKind kind, string objectId, Point canvasPoint, out ResizeHandleKind handleKind, out Rectangle handleRect)
        {
            handleKind = ResizeHandleKind.None;
            handleRect = Rectangle.Empty;

            if (!TryGetObjectBounds(kind, objectId, out Rectangle bounds) || bounds == Rectangle.Empty)
                return false;

            int handleSize = Math.Max(10, (int)(14f / Math.Max(ZoomFactor, 0.01f)));

            var handles = new (ResizeHandleKind Kind, Rectangle Rect)[]
            {
                (ResizeHandleKind.TopLeft, new Rectangle(bounds.Left - handleSize / 2, bounds.Top - handleSize / 2, handleSize, handleSize)),
                (ResizeHandleKind.TopRight, new Rectangle(bounds.Right - handleSize / 2, bounds.Top - handleSize / 2, handleSize, handleSize)),
                (ResizeHandleKind.BottomLeft, new Rectangle(bounds.Left - handleSize / 2, bounds.Bottom - handleSize / 2, handleSize, handleSize)),
                (ResizeHandleKind.BottomRight, new Rectangle(bounds.Right - handleSize / 2, bounds.Bottom - handleSize / 2, handleSize, handleSize)),
            };

            foreach (var h in handles)
            {
                if (h.Rect.Contains(canvasPoint))
                {
                    handleKind = h.Kind;
                    handleRect = h.Rect;
                    return true;
                }
            }

            return false;
        }

        private bool TryGetObjectBounds(InteractiveObjectKind kind, string objectId, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            if (kind == InteractiveObjectKind.None || string.IsNullOrWhiteSpace(objectId))
                return false;

            if (kind == InteractiveObjectKind.Sticker)
            {
                lock (stickerLock)
                {
                    if (TryGetStickerById(objectId, out var sticker))
                    {
                        bounds = new Rectangle(sticker.X, sticker.Y, Math.Max(24, sticker.Width), Math.Max(24, sticker.Height));
                        return true;
                    }
                }
            }
            else if (kind == InteractiveObjectKind.Image)
            {
                lock (imageLock)
                {
                    if (TryGetImageById(objectId, out var imageObj))
                    {
                        bounds = imageObj.Bounds;
                        return true;
                    }
                }
            }
            else if (kind == InteractiveObjectKind.Text)
            {
                lock (textLock)
                {
                    if (TryGetTextById(objectId, out var textObj))
                    {
                        bounds = MeasureTextBounds(textObj);
                        return true;
                    }
                }
            }

            return false;
        }

        private void DrawActiveObjectSelection(Graphics g)
        {
            if (activeObjectKind == InteractiveObjectKind.None || string.IsNullOrWhiteSpace(activeObjectId))
                return;

            if (!TryGetObjectBounds(activeObjectKind, activeObjectId, out Rectangle bounds) || bounds == Rectangle.Empty)
                return;

            using (var pen = new Pen(Color.DeepSkyBlue, 1.5f))
            {
                pen.DashStyle = DashStyle.Dash;
                g.DrawRectangle(pen, bounds);
            }

            int handleSize = Math.Max(10, (int)(14f / Math.Max(ZoomFactor, 0.01f)));
            var handles = new Rectangle[]
            {
                new Rectangle(bounds.Left - handleSize / 2, bounds.Top - handleSize / 2, handleSize, handleSize),
                new Rectangle(bounds.Right - handleSize / 2, bounds.Top - handleSize / 2, handleSize, handleSize),
                new Rectangle(bounds.Left - handleSize / 2, bounds.Bottom - handleSize / 2, handleSize, handleSize),
                new Rectangle(bounds.Right - handleSize / 2, bounds.Bottom - handleSize / 2, handleSize, handleSize),
            };

            using (var fill = new SolidBrush(Color.White))
            using (var border = new Pen(Color.DeepSkyBlue, 1.25f))
            {
                foreach (var handle in handles)
                {
                    g.FillRectangle(fill, handle);
                    g.DrawRectangle(border, handle);
                }
            }
        }

        private static Rectangle ComputeResizedBounds(Rectangle startBounds, int dx, int dy, ResizeHandleKind handleKind, int minWidth, int minHeight)
        {
            int left = startBounds.Left;
            int top = startBounds.Top;
            int right = startBounds.Right;
            int bottom = startBounds.Bottom;

            switch (handleKind)
            {
                case ResizeHandleKind.TopLeft:
                    left += dx;
                    top += dy;
                    break;
                case ResizeHandleKind.TopRight:
                    right += dx;
                    top += dy;
                    break;
                case ResizeHandleKind.BottomLeft:
                    left += dx;
                    bottom += dy;
                    break;
                case ResizeHandleKind.BottomRight:
                    right += dx;
                    bottom += dy;
                    break;
            }

            if (right - left < minWidth)
            {
                if (handleKind == ResizeHandleKind.TopLeft || handleKind == ResizeHandleKind.BottomLeft)
                    left = right - minWidth;
                else
                    right = left + minWidth;
            }

            if (bottom - top < minHeight)
            {
                if (handleKind == ResizeHandleKind.TopLeft || handleKind == ResizeHandleKind.TopRight)
                    top = bottom - minHeight;
                else
                    bottom = top + minHeight;
            }

            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        private static int ComputeTextResizeDelta(int dx, int dy, ResizeHandleKind handleKind)
        {
            int horizontal = (handleKind == ResizeHandleKind.TopLeft || handleKind == ResizeHandleKind.BottomLeft) ? -dx : dx;
            int vertical = (handleKind == ResizeHandleKind.TopLeft || handleKind == ResizeHandleKind.TopRight) ? -dy : dy;
            return (horizontal + vertical) / 12;
        }

        private Rectangle MeasureTextBounds(CanvasTextObject textObj)
        {
            string text = textObj.Text ?? string.Empty;
            int measuredWidth = 48;
            int measuredHeight = 24;

            using (Font font = new Font(string.IsNullOrWhiteSpace(textObj.FontName) ? "Arial" : textObj.FontName, Math.Max(8, textObj.FontSize)))
            {
                Size size = TextRenderer.MeasureText(text + " ", font, new Size(1000, 1000), TextFormatFlags.Left | TextFormatFlags.NoPadding);
                measuredWidth = Math.Max(48, size.Width);
                measuredHeight = Math.Max(24, size.Height);
            }

            return new Rectangle(textObj.X, textObj.Y, measuredWidth, measuredHeight);
        }

        private bool TryGetStickerById(string actionId, out StickerPayload sticker)
        {
            sticker = null;
            if (string.IsNullOrWhiteSpace(actionId))
                return false;

            if (!stickerIndexById.TryGetValue(actionId, out int index) || index < 0 || index >= stickers.Count)
                return false;

            sticker = stickers[index];
            return true;
        }

        private bool TryGetImageById(string actionId, out CanvasImageObject imageObj)
        {
            imageObj = null;
            if (string.IsNullOrWhiteSpace(actionId))
                return false;

            if (!imageIndexById.TryGetValue(actionId, out int index) || index < 0 || index >= imageObjects.Count)
                return false;

            imageObj = imageObjects[index];
            return true;
        }

        private bool TryGetTextById(string actionId, out CanvasTextObject textObj)
        {
            textObj = null;
            if (string.IsNullOrWhiteSpace(actionId))
                return false;

            if (!textIndexById.TryGetValue(actionId, out int index) || index < 0 || index >= textObjects.Count)
                return false;

            textObj = textObjects[index];
            return true;
        }

        public void ChangeBackgroundColor(Color color)
        {
            BackgroundColor = color;
            ClearBackgroundImage();
            canvas.Invalidate();
        }

        public void ChangeBackgroundImage(Image image)
        {
            if (image == null) return;
            ClearBackgroundImage();
            backgroundImage = new Bitmap(image);
            canvas.Invalidate();
        }

        private void SetBackgroundImageFromBase64(string imageData)
        {
            ClearBackgroundImage();
            if (string.IsNullOrWhiteSpace(imageData))
                return;

            try
            {
                // I/O DU LIEU -> IMAGE: background replay tu DrawHistory dang la base64, decode ve Bitmap local.
                byte[] bytes = Convert.FromBase64String(imageData);
                using (var ms = new MemoryStream(bytes))
                using (var img = Image.FromStream(ms))
                {
                    backgroundImage = new Bitmap(img);
                }
            }
            catch { }
        }

        private void ClearBackgroundImage()
        {
            if (backgroundImage == null)
                return;

            backgroundImage.Dispose();
            backgroundImage = null;
        }

        private void DrawBackgroundImage(Graphics g)
        {
            if (backgroundImage == null || drawingSurface == null)
                return;

            g.DrawImage(backgroundImage, new Rectangle(0, 0, drawingSurface.Width, drawingSurface.Height));
        }

        private static Rectangle BuildRectangle(Point p1, Point p2)
        {
            int x = Math.Min(p1.X, p2.X);
            int y = Math.Min(p1.Y, p2.Y);
            int w = Math.Abs(p2.X - p1.X);
            int h = Math.Abs(p2.Y - p1.Y);
            return new Rectangle(x, y, w, h);
        }

        private static Point ConstrainToSquare(Point origin, Point point)
        {
            int dx = point.X - origin.X;
            int dy = point.Y - origin.Y;
            int size = Math.Min(Math.Abs(dx), Math.Abs(dy));
            if (size == 0)
                size = Math.Max(Math.Abs(dx), Math.Abs(dy));

            return new Point(
                origin.X + Math.Sign(dx) * size,
                origin.Y + Math.Sign(dy) * size);
        }

        private static Point GetShapeEndPoint(Point origin, Point point, ToolType tool)
        {
            if ((Control.ModifierKeys & Keys.Shift) != Keys.Shift)
                return point;

            if (tool == ToolType.Rectangle || tool == ToolType.Circle)
                return ConstrainToSquare(origin, point);

            return point;
        }

        private static float Distance(Point a, Point b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        // SmartPen: net tho da bat -> lam dep -> ve len drawingSurface + broadcast nhu cac doan Pen that.
        // Net ket qua nam tren lop pixel (giong but thuong) nen co the TAY duoc tung phan.
        private void CommitSmartStroke(List<Point> raw)
        {
            if (raw == null || raw.Count == 0) return;

            BeautifyResult result = StrokeBeautifier.Beautify(raw);
            List<PointF> pts = result?.Points;
            if (pts == null || pts.Count < 2)
            {
                // Du phong: ve lai net tho neu khong lam dep duoc.
                pts = new List<PointF>();
                foreach (var p in raw) pts.Add(new PointF(p.X, p.Y));
            }
            if (pts.Count < 2) return;

            using (Pen pen = new Pen(CurrentColor, PenWidth))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                for (int i = 1; i < pts.Count; i++)
                {
                    Point p1 = Point.Round(pts[i - 1]);
                    Point p2 = Point.Round(pts[i]);
                    if (p1 == p2) p2.X += 1; // cham: ve doan 1px de round-cap thanh dot
                    graphics.DrawLine(pen, p1, p2);
                    SendNetworkDrawAction(p1, p2, CurrentColor, PenWidth, ToolType.Pen);
                }
            }
        }

        private void SendNetworkDrawAction(Point p1, Point p2, Color color, int width, ToolType tool)
        {
            OnNetworkDrawAction?.Invoke(new DrawPayload
            {
                ActionID = string.IsNullOrWhiteSpace(activeDrawingActionId) ? Guid.NewGuid().ToString() : activeDrawingActionId,
                ToolType = tool.ToString(),
                X1 = p1.X,
                Y1 = p1.Y,
                X2 = p2.X,
                Y2 = p2.Y,
                ColorARGB = color.ToArgb(),
                Thickness = width,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        private static void DrawShape(Graphics g, Pen pen, Point p1, Point p2, ToolType tool)
        {
            switch (tool)
            {
                case ToolType.Line:
                    g.DrawLine(pen, p1, p2);
                    break;
                case ToolType.Rectangle:
                    g.DrawRectangle(pen, BuildRectangle(p1, p2));
                    break;
                case ToolType.Circle:
                    g.DrawEllipse(pen, BuildRectangle(p1, p2));
                    break;
            }
        }
    }
}
