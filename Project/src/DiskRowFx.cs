using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace PerfMonitorWPF
{
    /// <summary>
    /// 磁盘分区卡片上的两处运行指示（叠在单张卡片内部，只服务这一种卡片）：
    ///   1) 白色光点沿卡片圆角边框巡回一圈——磁盘在读写时才出现，停就淡出消失；
    ///   2) 盘符文字底下（后面）一层呼吸光，颜色跟着进度条的状态色走。
    ///
    /// 摆放方式：放在卡片内容 Grid 的第一个子元素，`Grid.ColumnSpan="3"`，
    /// `Margin="-9,-1,-9,-1"`（抵消 Padding 8 与边框 1），于是本元素的坐标原点正好落在
    /// 卡片外框左上角往左/往上各 1px 处，卡片边框中线 = (1, 0.5) → (w-1, h-0.5)。
    /// 因为它是第一个子元素，呼吸光在文字下方；光点走的是边框，内容区碰不到。
    /// </summary>
    public class DiskRowFx : FrameworkElement
    {
        private const double CycleSeconds = 9.0;     // 光点绕卡片一圈的时长
        private const double FadeSec = 0.16;         // 运行 → 停止 的淡出时长
        private const double BreathPeriod = 3.8;     // 呼吸灯周期，与内存卡一致
        private const double MinSpeed = 30.0;
        private const double MaxSpeed = 220.0;

        private bool _busy;
        private Brush _accent;
        private double _glowX = 16.0;                // 盘符文字中心（见类注释的坐标换算）

        private Point[] _path;
        private double _len;
        private double _prog;
        private double _alpha;
        private double _t;

        private Brush _brDot, _brDotHalo, _brGlow, _brGlowSrc;

        public DiskRowFx()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
        }

        /// <summary>磁盘此刻是否在运行（读或写都算）。由 MainWindow 每轮采集后刷新。</summary>
        public bool Busy
        {
            get { return _busy; }
            set
            {
                if (_busy == value) return;
                _busy = value;
                if (!value) { /* 淡出由 Tick 处理，这里不动 */ }
            }
        }

        /// <summary>呼吸灯颜色，跟随该分区进度条的状态色。</summary>
        public Brush Accent
        {
            get { return _accent; }
            set { if (!ReferenceEquals(_accent, value)) { _accent = value; InvalidateVisual(); } }
        }

        /// <summary>盘符中心在卡片内的 X（默认 8 边距 + 30 列宽的一半 + 1px 偏移）。</summary>
        public double GlowX
        {
            get { return _glowX; }
            set { _glowX = value; }
        }

        // ==================================================================
        public void Tick(double dt)
        {
            if (dt <= 0 || double.IsNaN(dt)) return;
            if (dt > 0.25) dt = 0.25;

            _t += dt;
            if (_t > 3600.0) _t -= 3600.0;

            // 运行/停止：淡入淡出，避免光点硬弹
            double target = _busy ? 1.0 : 0.0;
            if (_alpha != target)
            {
                double step = dt / FadeSec;
                _alpha = _alpha < target ? Math.Min(target, _alpha + step)
                                         : Math.Max(target, _alpha - step);
                if (_alpha <= 0.0) _prog = 0.0;      // 停下后回到起点，下次从头跑
                InvalidateVisual();
            }
            if (_alpha <= 0.001) return;

            if (_path == null || _len < 1.0) return;
            double speed = Clamp(_len / CycleSeconds, MinSpeed, MaxSpeed);
            _prog += speed * dt / _len;
            while (_prog >= 1.0) _prog -= 1.0;

            InvalidateVisual();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            BuildPath();
        }

        // ==================================================================
        protected override void OnRender(DrawingContext dc)
        {
            if (_alpha <= 0.001) return;
            if (_path == null || _path.Length < 8) return;

            EnsureBrushes();

            // ① 盘符底下的呼吸光（本元素是第一个子元素，所以它在文字下面）
            double phase = 0.5 + 0.5 * Math.Sin(_t * 2.0 * Math.PI / BreathPeriod);
            double body = 0.45 + 0.55 * phase;
            if (_brGlow != null)
            {
                dc.PushOpacity(_alpha * body);
                dc.DrawEllipse(_brGlow, null, new Point(_glowX, ActualHeight * 0.5), 12.0, 12.0);
                dc.Pop();
            }

            // ② 白色光点沿边框巡回
            Point p = PointAt(_path, _prog);
            dc.PushOpacity(_alpha);
            dc.DrawEllipse(_brDotHalo, null, p, 5.2, 5.2);
            dc.DrawEllipse(_brDot, null, p, 2.0, 2.0);
            dc.Pop();
        }

        // ==================================================================
        private void EnsureBrushes()
        {
            if (_brDot == null)
            {
                _brDot = BrushOf("DotWhite", "#F5F7FA");
                var sc = _brDot as SolidColorBrush;
                Color c = sc != null ? sc.Color : Color.FromRgb(0xF5, 0xF7, 0xFA);
                var halo = new RadialGradientBrush(Color.FromArgb(70, c.R, c.G, c.B),
                                                   Color.FromArgb(0, c.R, c.G, c.B));
                halo.Freeze();
                _brDotHalo = halo;
            }

            if (_brGlow == null || !ReferenceEquals(_brGlowSrc, _accent))
            {
                _brGlowSrc = _accent;
                var sc = (_accent ?? _brDot) as SolidColorBrush;
                Color c = sc != null ? sc.Color : Color.FromRgb(0x55, 0xC6, 0xD4);
                var g = new RadialGradientBrush();
                g.GradientStops.Add(new GradientStop(Color.FromArgb(120, c.R, c.G, c.B), 0.0));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(52, c.R, c.G, c.B), 0.55));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1.0));
                g.Freeze();
                _brGlow = g;
            }
        }

        private Brush BrushOf(string key, string fallback)
        {
            try
            {
                var b = TryFindResource(key) as Brush;
                if (b != null) return b;
            }
            catch { }
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(fallback);
                var s = new SolidColorBrush(c);
                s.Freeze();
                return s;
            }
            catch { }
            return Brushes.White;
        }

        /// <summary>把卡片圆角边框中线采样成等距闭合路径（逆时针），查表 O(1)。</summary>
        private void BuildPath()
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 12.0 || h < 12.0) { _path = null; _len = 0; return; }

            // 卡片边框中线（见类注释的坐标换算）
            double L = 1.0, T = 0.5, R = w - 1.0, B = h - 0.5;
            double r = 8.0;
            r = Math.Min(r, Math.Min((R - L) * 0.5 - 0.5, (B - T) * 0.5 - 0.5));
            if (r < 1.0) r = 1.0;

            List<Point> p = new List<Point>(64);
            p.Add(new Point((L + R) * 0.5, T));                 // 上边中点起跑
            p.Add(new Point(L + r, T));
            AddArc(p, new Point(L + r, T + r), r, 270.0);       // 左上角
            p.Add(new Point(L, B - r));
            AddArc(p, new Point(L + r, B - r), r, 180.0);       // 左下角
            p.Add(new Point(R - r, B));
            AddArc(p, new Point(R - r, B - r), r, 90.0);        // 右下角
            p.Add(new Point(R, T + r));
            AddArc(p, new Point(R - r, T + r), r, 0.0);         // 右上角
            p.Add(new Point((L + R) * 0.5, T));                 // 闭合

            int n = Math.Max(160, Math.Min(900, (int)Math.Round(2 * ((R - L) + (B - T)))));
            _path = Resample(p, n, out _len);
            InvalidateVisual();
        }

        /// <summary>补一个 90° 圆弧（逆时针 = 屏幕角度递减），起点已由调用方 Add。</summary>
        private static void AddArc(List<Point> pts, Point c, double r, double startDeg)
        {
            const int steps = 8;
            for (int i = 1; i <= steps; i++)
            {
                double a = (startDeg - 90.0 * i / steps) * Math.PI / 180.0;
                pts.Add(new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)));
            }
        }

        private static Point[] Resample(List<Point> pts, int n, out double total)
        {
            int m = pts.Count;
            total = 0.0;
            if (m < 4) return null;

            double[] seg = new double[m - 1];
            for (int i = 0; i < m - 1; i++)
            {
                double dx = pts[i + 1].X - pts[i].X, dy = pts[i + 1].Y - pts[i].Y;
                seg[i] = Math.Sqrt(dx * dx + dy * dy);
                total += seg[i];
            }
            if (total < 1.0) return null;

            Point[] outPts = new Point[n];
            double step = total / n;
            int si = 0;
            double acc = 0.0;
            for (int k = 0; k < n; k++)
            {
                double want = k * step;
                while (si < m - 2 && acc + seg[si] < want) { acc += seg[si]; si++; }
                double t = seg[si] > 0.0001 ? (want - acc) / seg[si] : 0.0;
                if (t < 0.0) t = 0.0;
                if (t > 1.0) t = 1.0;
                outPts[k] = new Point(pts[si].X + (pts[si + 1].X - pts[si].X) * t,
                                      pts[si].Y + (pts[si + 1].Y - pts[si].Y) * t);
            }
            return outPts;
        }

        private static Point PointAt(Point[] path, double prog)
        {
            int n = path.Length;
            double x = prog * n;
            int i0 = (int)Math.Floor(x);
            if (i0 < 0) i0 = 0;
            if (i0 >= n) i0 = n - 1;
            int i1 = i0 + 1; if (i1 >= n) i1 = 0;
            double t = x - i0;
            if (t < 0.0) t = 0.0;
            if (t > 1.0) t = 1.0;
            return new Point(path[i0].X + (path[i1].X - path[i0].X) * t,
                             path[i0].Y + (path[i1].Y - path[i0].Y) * t);
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
