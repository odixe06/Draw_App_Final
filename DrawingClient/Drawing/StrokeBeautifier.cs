using System;
using System.Collections.Generic;
using System.Drawing;

namespace DrawingClient.Drawing
{
    /// <summary>Loai net da duoc "lam dep" tu net ve tay tho.</summary>
    public enum BeautifiedShapeKind
    {
        Dot,        // mot cham (net qua ngan)
        Line,       // duong thang
        Arrow,      // mui ten (than thang + dau mui ten)
        Circle,     // hinh tron
        Ellipse,    // elip
        Rectangle,  // chu nhat / hinh vuong
        Triangle,   // tam giac
        Curve       // duong cong tu do da lam muot (truong hop chung)
    }

    /// <summary>Ket qua lam dep: loai net + polyline (toa do canvas) de ve ra bang cac doan Pen.</summary>
    public sealed class BeautifyResult
    {
        public BeautifiedShapeKind Kind;
        public List<PointF> Points;
        public bool Closed;
    }

    /// <summary>
    /// Lam dep net ve tay CUC BO (khong AI): bo nhieu/run tay, nhan dien hinh don gian de "nan" thanh
    /// hinh chuan (duong thang/tron/elip/chu nhat/tam giac/mui ten), con lai thi lam muot duong cong.
    /// Tat ca tra ve duoi dang polyline -> CanvasManager phat thanh cac doan Pen that (tay duoc).
    /// Thuat toan: dedup -> RDP simplify -> nhan dien hinh (nguong bao thu, khong chac thi bo qua) -> Catmull-Rom.
    /// </summary>
    public static class StrokeBeautifier
    {
        public static BeautifyResult Beautify(IList<Point> rawPoints)
        {
            if (rawPoints == null || rawPoints.Count == 0)
                return null;

            List<PointF> clean = Dedup(rawPoints, 2f);
            if (clean.Count <= 1)
            {
                PointF p = clean.Count == 1 ? clean[0] : new PointF(rawPoints[0].X, rawPoints[0].Y);
                return new BeautifyResult { Kind = BeautifiedShapeKind.Dot, Points = new List<PointF> { p, p }, Closed = false };
            }

            RectangleF box = BBox(clean);
            float diag = (float)Math.Sqrt(box.Width * box.Width + box.Height * box.Height);

            // Net qua nho -> coi nhu cham, khong lam dep (tranh chia 0 / kho nhan dien).
            if (diag < 6f)
                return new BeautifyResult { Kind = BeautifiedShapeKind.Dot, Points = clean, Closed = false };

            float pathLen = PathLength(clean);
            float gap = Dist(clean[0], clean[clean.Count - 1]);
            bool closed = gap <= Math.Max(16f, 0.22f * diag) && pathLen >= 1.5f * diag;

            float cornerEps = Clamp(0.045f * diag, 4f, 18f);
            List<PointF> corners = Rdp(clean, cornerEps);

            // 1) DUONG THANG (net mo, gan thang).
            if (!closed)
            {
                float lineDev = MaxLineDeviation(clean, clean[0], clean[clean.Count - 1]);
                if (lineDev <= Math.Max(0.035f * diag, 3.2f) && pathLen <= 1.25f * gap && gap > 4f)
                    return new BeautifyResult
                    {
                        Kind = BeautifiedShapeKind.Line,
                        Points = new List<PointF> { clean[0], clean[clean.Count - 1] },
                        Closed = false
                    };

                // 2) MUI TEN (net mo): than gan thang + dau co "moc" gap khuc o cuoi.
                if (TryDetectArrow(clean, corners, diag, out List<PointF> arrowPts))
                    return new BeautifyResult { Kind = BeautifiedShapeKind.Arrow, Points = arrowPts, Closed = false };
            }

            // 3) TRON / ELIP (net kin).
            if (closed)
            {
                PointF c = Centroid(clean);
                MeanStd(clean, c, out float meanR, out float stdR);
                float aspect = box.Height > 0.01f ? box.Width / box.Height : 1f;
                if (aspect < 1f) aspect = 1f / Math.Max(0.01f, aspect);

                if (meanR > 4f && stdR / meanR < 0.16f && aspect <= 1.35f)
                    return new BeautifyResult { Kind = BeautifiedShapeKind.Circle, Points = EllipsePolygon(c.X, c.Y, meanR, meanR, 72), Closed = true };

                float a = box.Width / 2f, b = box.Height / 2f;
                float cx = box.X + a, cy = box.Y + b;
                if (a > 4f && b > 4f && EllipseFitError(clean, cx, cy, a, b) < 0.22f)
                    return new BeautifyResult { Kind = BeautifiedShapeKind.Ellipse, Points = EllipsePolygon(cx, cy, a, b, 72), Closed = true };
            }

            // 4) CHU NHAT / TAM GIAC (net kin, dem dinh tu RDP).
            if (closed)
            {
                List<PointF> verts = PolygonVertices(corners, Math.Max(16f, 0.18f * diag));
                if (verts.Count == 4 && AllAnglesNear(verts, 90f, 22f))
                {
                    if (EdgesAxisAligned(verts, 14f))
                    {
                        // Net gan thang dung -> nan ve dung bao chu nhat (dep nhat).
                        var rect = new List<PointF>
                        {
                            new PointF(box.Left, box.Top),
                            new PointF(box.Right, box.Top),
                            new PointF(box.Right, box.Bottom),
                            new PointF(box.Left, box.Bottom),
                            new PointF(box.Left, box.Top)
                        };
                        return new BeautifyResult { Kind = BeautifiedShapeKind.Rectangle, Points = rect, Closed = true };
                    }
                    // Chu nhat xoay -> giu 4 dinh, lam thang canh.
                    verts.Add(verts[0]);
                    return new BeautifyResult { Kind = BeautifiedShapeKind.Rectangle, Points = verts, Closed = true };
                }

                if (verts.Count == 3 && AllAnglesNear(verts, 60f, 45f))
                {
                    verts.Add(verts[0]);
                    return new BeautifyResult { Kind = BeautifiedShapeKind.Triangle, Points = verts, Closed = true };
                }
            }

            // 5) LAM MUOT (truong hop chung: duong cong tu do, mo hoac kin).
            float smoothEps = Clamp(0.02f * diag, 1.5f, 5f);
            List<PointF> ctrl = Rdp(clean, smoothEps);
            List<PointF> smooth = CatmullRom(ctrl, closed);
            return new BeautifyResult { Kind = BeautifiedShapeKind.Curve, Points = smooth, Closed = closed };
        }

        // ─────────────────────────── Hinh hoc co ban ───────────────────────────

        private static float Dist(PointF a, PointF b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        private static List<PointF> Dedup(IList<Point> pts, float minGap)
        {
            var outp = new List<PointF>();
            foreach (var p in pts)
            {
                PointF f = new PointF(p.X, p.Y);
                if (outp.Count == 0 || Dist(outp[outp.Count - 1], f) >= minGap)
                    outp.Add(f);
            }
            // Luon giu diem cuoi (ngay ca khi sat diem truoc) de khong cut net.
            PointF last = new PointF(pts[pts.Count - 1].X, pts[pts.Count - 1].Y);
            if (outp.Count == 0 || Dist(outp[outp.Count - 1], last) > 0.01f)
                outp.Add(last);
            return outp;
        }

        private static RectangleF BBox(List<PointF> pts)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            return new RectangleF(minX, minY, maxX - minX, maxY - minY);
        }

        private static float PathLength(List<PointF> pts)
        {
            float len = 0f;
            for (int i = 1; i < pts.Count; i++) len += Dist(pts[i - 1], pts[i]);
            return len;
        }

        private static PointF Centroid(List<PointF> pts)
        {
            double sx = 0, sy = 0;
            foreach (var p in pts) { sx += p.X; sy += p.Y; }
            return new PointF((float)(sx / pts.Count), (float)(sy / pts.Count));
        }

        private static void MeanStd(List<PointF> pts, PointF c, out float mean, out float std)
        {
            double sum = 0;
            var r = new double[pts.Count];
            for (int i = 0; i < pts.Count; i++) { r[i] = Dist(pts[i], c); sum += r[i]; }
            mean = (float)(sum / pts.Count);
            double v = 0;
            foreach (var ri in r) { double d = ri - mean; v += d * d; }
            std = (float)Math.Sqrt(v / pts.Count);
        }

        /// <summary>Khoang cach tu p toi DUONG THANG vo han qua a-b.</summary>
        private static float PerpDist(PointF p, PointF a, PointF b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float len2 = dx * dx + dy * dy;
            if (len2 < 1e-6f) return Dist(p, a);
            float num = Math.Abs(dy * p.X - dx * p.Y + b.X * a.Y - b.Y * a.X);
            return num / (float)Math.Sqrt(len2);
        }

        private static float MaxLineDeviation(List<PointF> pts, PointF a, PointF b)
        {
            float max = 0f;
            foreach (var p in pts) { float d = PerpDist(p, a, b); if (d > max) max = d; }
            return max;
        }

        // ─────────────────────────── RDP simplify ───────────────────────────

        private static List<PointF> Rdp(List<PointF> pts, float eps)
        {
            if (pts.Count < 3) return new List<PointF>(pts);
            bool[] keep = new bool[pts.Count];
            keep[0] = true; keep[pts.Count - 1] = true;
            RdpRec(pts, 0, pts.Count - 1, eps, keep);
            var outp = new List<PointF>();
            for (int i = 0; i < pts.Count; i++) if (keep[i]) outp.Add(pts[i]);
            return outp;
        }

        private static void RdpRec(List<PointF> pts, int i0, int i1, float eps, bool[] keep)
        {
            if (i1 <= i0 + 1) return;
            float maxD = -1f; int idx = -1;
            for (int i = i0 + 1; i < i1; i++)
            {
                float d = PerpDist(pts[i], pts[i0], pts[i1]);
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (maxD > eps && idx > 0)
            {
                keep[idx] = true;
                RdpRec(pts, i0, idx, eps, keep);
                RdpRec(pts, idx, i1, eps, keep);
            }
        }

        // ─────────────────────────── Da giac / goc ───────────────────────────

        /// <summary>Tu danh sach diem RDP (gom 2 dau mut) -> danh sach DINH cua da giac kin (gop dinh dau/cuoi neu trung).</summary>
        private static List<PointF> PolygonVertices(List<PointF> corners, float mergeDist)
        {
            var v = new List<PointF>(corners);
            while (v.Count > 1 && Dist(v[0], v[v.Count - 1]) <= mergeDist)
                v.RemoveAt(v.Count - 1);
            return v;
        }

        /// <summary>Goc trong tai moi dinh (cyclic) co nam quanh "target" do (+- tol) khong.</summary>
        private static bool AllAnglesNear(List<PointF> verts, float target, float tol)
        {
            int n = verts.Count;
            for (int i = 0; i < n; i++)
            {
                PointF prev = verts[(i - 1 + n) % n];
                PointF cur = verts[i];
                PointF next = verts[(i + 1) % n];
                float ang = AngleDeg(prev, cur, next);
                if (Math.Abs(ang - target) > tol) return false;
            }
            return true;
        }

        /// <summary>Goc (do) giua hai vector (prev-cur) va (next-cur).</summary>
        private static float AngleDeg(PointF prev, PointF cur, PointF next)
        {
            float ax = prev.X - cur.X, ay = prev.Y - cur.Y;
            float bx = next.X - cur.X, by = next.Y - cur.Y;
            float la = (float)Math.Sqrt(ax * ax + ay * ay);
            float lb = (float)Math.Sqrt(bx * bx + by * by);
            if (la < 1e-4f || lb < 1e-4f) return 0f;
            float cos = (ax * bx + ay * by) / (la * lb);
            cos = Clamp(cos, -1f, 1f);
            return (float)(Math.Acos(cos) * 180.0 / Math.PI);
        }

        private static bool EdgesAxisAligned(List<PointF> verts, float tolDeg)
        {
            int n = verts.Count;
            for (int i = 0; i < n; i++)
            {
                PointF a = verts[i], b = verts[(i + 1) % n];
                float ang = (float)(Math.Atan2(b.Y - a.Y, b.X - a.X) * 180.0 / Math.PI);
                ang = ((ang % 90f) + 90f) % 90f;            // khoang cach toi boi cua 90 do
                float toAxis = Math.Min(ang, 90f - ang);
                if (toAxis > tolDeg) return false;
            }
            return true;
        }

        // ─────────────────────────── Mui ten ───────────────────────────

        /// <summary>
        /// Phat hien mui ten (bao thu): than gan thang chiem phan lon do dai, dau co mot "moc" gap khuc
        /// ngan o cuoi. Neu dung -> tra ve polyline [start, tip, barb1, tip, barb2].
        /// </summary>
        private static bool TryDetectArrow(List<PointF> clean, List<PointF> corners, float diag, out List<PointF> arrowPts)
        {
            arrowPts = null;
            // Can it nhat 1 goc gap o gan cuoi: RDP cho >= 3 diem (start ... elbow, end).
            if (corners.Count < 3 || corners.Count > 5) return false;

            PointF start = corners[0];
            PointF tip = corners[corners.Count - 2];   // "khuyu" = dau mui ten
            PointF tail = corners[corners.Count - 1];   // duoi cua moc

            float shaft = Dist(start, tip);
            float barb = Dist(tip, tail);
            if (shaft < 0.45f * diag) return false;              // than phai du dai
            if (barb < 0.08f * diag || barb > 0.45f * shaft) return false; // moc ngan, hop ly

            // Than (start..tip) phai gan thang.
            int tipIdx = NearestIndex(clean, tip);
            float shaftDev = MaxLineDeviation(clean.GetRange(0, Math.Max(2, tipIdx + 1)), start, tip);
            if (shaftDev > Math.Max(0.05f * diag, 4f)) return false;

            // Moc phai gap khuc ro (goc giua than va moc khac xa 180 do).
            float turn = AngleDeg(start, tip, tail);
            if (turn < 25f || turn > 150f) return false;

            // Tao mui ten chuan tai tip, theo huong than (start->tip), do dai moc dua tren barb.
            float dirx = tip.X - start.X, diry = tip.Y - start.Y;
            float dl = (float)Math.Sqrt(dirx * dirx + diry * diry);
            if (dl < 1e-3f) return false;
            dirx /= dl; diry /= dl;
            float headLen = Clamp(Math.Max(barb, 0.16f * shaft), 8f, 0.4f * shaft);
            const double spread = 28.0 * Math.PI / 180.0; // nua goc mo cua dau mui ten
            // Hai canh moc huong nguoc lai than, lech +-spread.
            PointF b1 = RotateBack(tip, dirx, diry, headLen, spread);
            PointF b2 = RotateBack(tip, dirx, diry, headLen, -spread);
            arrowPts = new List<PointF> { start, tip, b1, tip, b2 };
            return true;
        }

        private static PointF RotateBack(PointF tip, float dirx, float diry, float len, double spread)
        {
            // Huong nguoc than = (-dir), xoay +-spread.
            double cos = Math.Cos(spread), sin = Math.Sin(spread);
            float bx = (float)(-dirx * cos - -diry * sin);
            float by = (float)(-dirx * sin + -diry * cos);
            return new PointF(tip.X + bx * len, tip.Y + by * len);
        }

        private static int NearestIndex(List<PointF> pts, PointF target)
        {
            int best = 0; float bd = float.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                float d = Dist(pts[i], target);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        // ─────────────────────────── Tron / Elip ───────────────────────────

        private static float EllipseFitError(List<PointF> pts, float cx, float cy, float a, float b)
        {
            double sum = 0;
            foreach (var p in pts)
            {
                float nx = (p.X - cx) / a, ny = (p.Y - cy) / b;
                sum += Math.Abs(nx * nx + ny * ny - 1.0);
            }
            return (float)(sum / pts.Count);
        }

        private static List<PointF> EllipsePolygon(float cx, float cy, float a, float b, int segments)
        {
            var pts = new List<PointF>(segments + 1);
            for (int i = 0; i <= segments; i++)
            {
                double t = 2.0 * Math.PI * i / segments;
                pts.Add(new PointF(cx + a * (float)Math.Cos(t), cy + b * (float)Math.Sin(t)));
            }
            return pts;
        }

        // ─────────────────────────── Catmull-Rom ───────────────────────────

        private static List<PointF> CatmullRom(List<PointF> ctrl, bool closed)
        {
            int n = ctrl.Count;
            if (n < 3) return new List<PointF>(ctrl);

            var pts = new List<PointF>();
            int segCount = closed ? n : n - 1;
            for (int s = 0; s < segCount; s++)
            {
                PointF p0 = Ctrl(ctrl, s - 1, closed);
                PointF p1 = Ctrl(ctrl, s, closed);
                PointF p2 = Ctrl(ctrl, s + 1, closed);
                PointF p3 = Ctrl(ctrl, s + 2, closed);
                int sub = (int)Clamp((float)Math.Round(Dist(p1, p2) / 4f), 2f, 24f);
                for (int j = 0; j < sub; j++)
                {
                    float t = j / (float)sub;
                    pts.Add(CatmullRomPoint(p0, p1, p2, p3, t));
                }
            }
            pts.Add(closed ? pts[0] : ctrl[n - 1]);
            return pts;
        }

        private static PointF Ctrl(List<PointF> ctrl, int i, bool closed)
        {
            int n = ctrl.Count;
            if (closed) return ctrl[((i % n) + n) % n];
            if (i < 0) i = 0;
            if (i > n - 1) i = n - 1;
            return ctrl[i];
        }

        private static PointF CatmullRomPoint(PointF p0, PointF p1, PointF p2, PointF p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            float x = 0.5f * (2f * p1.X + (-p0.X + p2.X) * t + (2f * p0.X - 5f * p1.X + 4f * p2.X - p3.X) * t2 + (-p0.X + 3f * p1.X - 3f * p2.X + p3.X) * t3);
            float y = 0.5f * (2f * p1.Y + (-p0.Y + p2.Y) * t + (2f * p0.Y - 5f * p1.Y + 4f * p2.Y - p3.Y) * t2 + (-p0.Y + 3f * p1.Y - 3f * p2.Y + p3.Y) * t3);
            return new PointF(x, y);
        }
    }
}
