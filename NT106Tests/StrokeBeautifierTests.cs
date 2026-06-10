using System;
using System.Collections.Generic;
using System.Drawing;
using DrawingClient.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NT106Tests
{
    // Kiem thu module lam dep net ve tay (StrokeBeautifier) — hinh hoc thuan, khong UI/network.
    // Cac net "tho" duoc sinh tat dinh (jitter bang sin) de tai lap duoc.
    [TestClass]
    public class StrokeBeautifierTests
    {
        private static List<Point> Lerp(Point a, Point b, int n, double jitterAmp)
        {
            var pts = new List<Point>();
            for (int i = 0; i <= n; i++)
            {
                double t = i / (double)n;
                double jx = jitterAmp * Math.Sin(t * Math.PI);          // 0 o hai dau mut
                double x = a.X + (b.X - a.X) * t;
                double y = a.Y + (b.Y - a.Y) * t + jx;
                pts.Add(new Point((int)Math.Round(x), (int)Math.Round(y)));
            }
            return pts;
        }

        private static void AppendEdge(List<Point> pts, Point a, Point b, int n, double jitterAmp)
        {
            for (int i = 0; i <= n; i++)
            {
                double t = i / (double)n;
                double j = jitterAmp * Math.Sin(t * Math.PI);
                // jitter vuong goc voi canh
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                double nx = len > 0 ? -dy / len : 0, ny = len > 0 ? dx / len : 0;
                double x = a.X + dx * t + nx * j;
                double y = a.Y + dy * t + ny * j;
                pts.Add(new Point((int)Math.Round(x), (int)Math.Round(y)));
            }
        }

        [TestMethod]
        public void Beautify_NoisyLine_ReturnsTwoPointStraightLine()
        {
            var raw = Lerp(new Point(0, 0), new Point(200, 0), 40, 2.0);
            var r = StrokeBeautifier.Beautify(raw);

            Assert.IsNotNull(r);
            Assert.AreEqual(BeautifiedShapeKind.Line, r.Kind);
            Assert.AreEqual(2, r.Points.Count);
            Assert.IsTrue(Math.Abs(r.Points[0].X - 0) <= 2 && Math.Abs(r.Points[0].Y - 0) <= 2);
            Assert.IsTrue(Math.Abs(r.Points[1].X - 200) <= 2 && Math.Abs(r.Points[1].Y - 0) <= 2);
        }

        [TestMethod]
        public void Beautify_NoisyCircle_DetectedAsCircleWithConstantRadius()
        {
            float cx = 150, cy = 150, rad = 100;
            var raw = new List<Point>();
            for (int i = 0; i < 64; i++)   // 63/64 vong -> de lai khe nho (net khong khep kin tuyet doi)
            {
                double a = 2 * Math.PI * i / 64.0;
                double jr = rad + 1.0 * Math.Sin(i * 0.9);   // run tay nho
                raw.Add(new Point((int)Math.Round(cx + jr * Math.Cos(a)), (int)Math.Round(cy + jr * Math.Sin(a))));
            }

            var r = StrokeBeautifier.Beautify(raw);

            Assert.IsNotNull(r);
            Assert.AreEqual(BeautifiedShapeKind.Circle, r.Kind);
            Assert.IsTrue(r.Closed);
            // Moi diem ket qua cach tam ~ rad.
            foreach (var p in r.Points)
            {
                double d = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
                Assert.IsTrue(Math.Abs(d - rad) < 6, $"radius lech qua nhieu: {d}");
            }
        }

        [TestMethod]
        public void Beautify_NoisyRectangle_DetectedAsRectangle()
        {
            var raw = new List<Point>();
            Point p1 = new Point(20, 20), p2 = new Point(220, 20), p3 = new Point(220, 140), p4 = new Point(20, 140);
            AppendEdge(raw, p1, p2, 24, 1.0);
            AppendEdge(raw, p2, p3, 18, 1.0);
            AppendEdge(raw, p3, p4, 24, 1.0);
            AppendEdge(raw, p4, p1, 18, 1.0);

            var r = StrokeBeautifier.Beautify(raw);

            Assert.IsNotNull(r);
            Assert.AreEqual(BeautifiedShapeKind.Rectangle, r.Kind);
            Assert.IsTrue(r.Closed);
            Assert.IsTrue(r.Points.Count == 5, "chu nhat = 4 dinh + dong");
            Assert.AreEqual(r.Points[0], r.Points[r.Points.Count - 1]); // khep kin
        }

        [TestMethod]
        public void Beautify_NoisyTriangle_DetectedAsTriangle()
        {
            var raw = new List<Point>();
            Point a = new Point(100, 20), b = new Point(20, 180), c = new Point(180, 180);
            AppendEdge(raw, a, b, 22, 1.0);
            AppendEdge(raw, b, c, 22, 1.0);
            AppendEdge(raw, c, a, 22, 1.0);

            var r = StrokeBeautifier.Beautify(raw);

            Assert.IsNotNull(r);
            Assert.AreEqual(BeautifiedShapeKind.Triangle, r.Kind);
            Assert.IsTrue(r.Closed);
            Assert.AreEqual(4, r.Points.Count); // 3 dinh + dong
        }

        [TestMethod]
        public void Beautify_OpenWavyCurve_SmoothedAsCurveNotLine()
        {
            var raw = new List<Point>();
            for (int i = 0; i <= 120; i++)
            {
                double x = i * 2.5;                 // 0..300
                double y = 50 * Math.Sin(x / 30.0);  // song hinh sin -> KHONG phai duong thang
                raw.Add(new Point((int)Math.Round(x), (int)Math.Round(150 + y)));
            }

            var r = StrokeBeautifier.Beautify(raw);

            Assert.IsNotNull(r);
            Assert.AreEqual(BeautifiedShapeKind.Curve, r.Kind);
            Assert.IsFalse(r.Closed);
            Assert.IsTrue(r.Points.Count > 2);
            // Duong cong da lam muot van bam quanh bien do song (khong bi "duoi thang").
            float maxY = float.MinValue, minY = float.MaxValue;
            foreach (var p in r.Points) { if (p.Y > maxY) maxY = p.Y; if (p.Y < minY) minY = p.Y; }
            Assert.IsTrue(maxY - minY > 40, "duong cong bi lam phang qua muc");
        }

        [TestMethod]
        public void Beautify_SinglePoint_ReturnsDot()
        {
            var r = StrokeBeautifier.Beautify(new List<Point> { new Point(50, 50) });
            Assert.IsNotNull(r);
            Assert.AreEqual(BeautifiedShapeKind.Dot, r.Kind);
            Assert.IsTrue(r.Points.Count >= 2);
        }

        [TestMethod]
        public void Beautify_TinyStroke_ReturnsDot()
        {
            var r = StrokeBeautifier.Beautify(new List<Point> { new Point(50, 50), new Point(52, 51), new Point(53, 50) });
            Assert.IsNotNull(r);
            Assert.AreEqual(BeautifiedShapeKind.Dot, r.Kind);
        }

        [TestMethod]
        public void Beautify_Null_ReturnsNull()
        {
            Assert.IsNull(StrokeBeautifier.Beautify(null));
            Assert.IsNull(StrokeBeautifier.Beautify(new List<Point>()));
        }
    }
}
