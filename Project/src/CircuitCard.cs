using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PerfMonitorWPF
{
    /// <summary>电路卡类型：决定方框里放什么、圆环里放什么。处理器卡仍用 MonitorCard。</summary>
    public enum CircuitKind { Gpu, Memory }

    /// <summary>
    /// 带单位状态灯与白色光点巡回的卡片（显卡 / 内存两张共用，靠 Kind 显式区分）。
    ///
    /// ==== 静态结构（按手绘图） ====
    ///   顶部：左温度 / 右类别名
    ///   左侧中上：数据方框 —— 不写「内存」这类文字，直接放真实容量数字
    ///   右上 GB 方框 + 右侧状态灯；左下 MB 方框 + 左侧状态灯（灯色 = 当前生效单位）
    ///   中偏右下：圆环 —— 不写「占用」这类文字：
    ///       Gpu    → 环内是真实利用率整数 + 环带上的占用弧
    ///       Memory → 环内没有任何数字，整块做成淡淡的呼吸光（因为「已用内存」已经
    ///                在方框里说完了，圆环再放百分比是同一件事）
    ///
    /// ==== 动态 ====
    ///   一颗白色光点沿闭路巡回：有效灯 → 连接线 → 圆环整圈 → 连接线 → 有效单位方框整圈
    ///   → 连接线 → 数据方框整圈 → 连接线 → 回到有效灯；红灯那一侧永不进入。
    /// </summary>
    public class CircuitCard : FrameworkElement
    {
        // ================= 常量 =================
        private const double SwitchToGb = 1.0;
        private const int PathSamples = 1500;
        private const double CycleSeconds = 10.5;
        private const double HandoverFadeSec = 0.14;
        private const double DotSmoothTau = 0.045;
        private const double MinSpeed = 24.0;
        private const double MaxSpeed = 78.0;
        private const double BreathPeriod = 3.8;      // 呼吸灯周期（秒）
        /// <summary>配色分界，与处理器卡 MonitorCard.LoadColorSplit 完全一致：≤55 绿，&gt;55 红。</summary>
        private const double LoadColorSplit = 55.0;

        // ================= 数据 =================
        private CircuitKind _kind = CircuitKind.Memory;
        private string _tempText;
        private Brush _tempBrush;
        private bool _loadValid;
        private double _load;
        private bool _capValid;
        private string _capText = "";
        private bool _useGb;

        // ================= 布局 =================
        private bool _layoutValid;
        private double _w, _h, _pad, _headerH, _zoneTop;
        private double _tempFont, _nameFont;
        private Rect _boxData, _boxGb, _boxMb;
        private Point _lampGbC, _lampMbC;
        private double _lampR;
        private Point _ringC;
        private double _ringOuter, _ringR, _ringBand, _ringHole;
        private Typeface _faceBold, _faceSemi, _faceNorm;
        private double _ppi = 1.0;
        private double _capFont, _loadFont;

        // 文本缓存
        private FormattedText _capFt, _loadFt, _nameFt, _tempFt;
        private FormattedText _gbFt, _mbFt;
        private string _capFtKey = "", _loadFtKey = "", _tempFtKey = "";

        // ================= 画刷 / 画笔 =================
        // 大圆、大方框、占用弧、呼吸光全部只跟「状态色」走：≤55 绿、>55 红，
        // 与处理器卡同一套色，不再使用独立的强调色，避免一张卡里出现四五种颜色。
        private Brush _brText, _brSecond, _brGreen, _brRed, _brLampOn, _brLampOff, _brDot;
        private Brush _brGlowOn, _brGlowOff, _brDotGlow;
        private Pen _penWire, _penBox;
        private Pen _penMemG, _penMemR;        // 大方框描边
        private Pen _penRingG, _penRingR;      // 大圆双线
        private Pen _penArcG, _penArcR;        // 占用弧
        private Brush _brBreathG, _brBreathR;  // 呼吸光
        private Brush _brRingFill;
        private Geometry _clipGeo;

        // ================= 路径与动画 =================
        private Point[] _pathGb, _pathMb;
        private double _lenGb, _lenMb;
        private double _prog;
        private double _fade = 1.0;
        private Point _dotPos;
        private bool _dotInit;
        private bool _useGbPath;
        private double _t;                     // 呼吸灯相位
        private bool _hot;                     // 当前是否 >55（决定大圆/大方框的红绿）

        // ==================================================================
        public CircuitCard()
        {
            SnapsToDevicePixels = true;
        }

        /// <summary>卡片类型（XAML 里写 Kind="Gpu" / "Memory"）。</summary>
        public CircuitKind Kind
        {
            get { return _kind; }
            set
            {
                if (_kind == value) return;
                _kind = value;
                _layoutValid = false;
                InvalidateVisual();
            }
        }

        private bool ShowsLoad { get { return _kind == CircuitKind.Gpu; } }

        // ==================================================================
        // 数据入口（只写字段，不动画、不重排）
        // ==================================================================
        /// <summary>左上角温度。null / 空串 = 没有真实读数，整项隐藏。</summary>
        public void SetTemp(string text, Brush brush)
        {
            if (text != _tempText) { _tempText = text; _tempFtKey = ""; InvalidateVisual(); }
            if (brush != null && !ReferenceEquals(brush, _tempBrush)) { _tempBrush = brush; InvalidateVisual(); }
        }

        /// <summary>圆环：真实利用率（显卡用；内存卡不显示利用率，收到也不使用）。</summary>
        public void SetLoad(double percent)
        {
            if (double.IsNaN(percent)) { _loadValid = false; InvalidateVisual(); return; }
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            if (_loadValid && Math.Abs(percent - _load) < 0.01) return;
            _loadValid = true;
            _load = percent;
            _loadFtKey = "";
            InvalidateVisual();
        }

        /// <summary>
        /// 数据方框：已用 / 总容量（字节）。单位、数字格式、灯色、光点路径全部只由这里决定。
        /// </summary>
        public void SetCapacity(long usedBytes, long totalBytes)
        {
            if (!(totalBytes > 0 && usedBytes >= 0 && usedBytes <= totalBytes * 2))
            {
                if (_capValid) { _capValid = false; InvalidateVisual(); }
                return;
            }
            _capValid = true;

            double gb = AppSettings.BinaryUnits ? 1073741824.0 : 1000000000.0;
            double mb = AppSettings.BinaryUnits ? 1048576.0 : 1000000.0;

            bool useGb = (double)usedBytes >= gb * SwitchToGb;
            string txt;
            if (useGb)
            {
                txt = ((double)usedBytes / gb).ToString("0.00", CultureInfo.InvariantCulture);
            }
            else
            {
                long v = (long)Math.Floor((double)usedBytes / mb);
                if (v < 0) v = 0;
                if (v > 999999) v = 999999;
                txt = v.ToString(CultureInfo.InvariantCulture);
            }

            if (txt == _capText && useGb == _useGb && _capFt != null) return;
            _capText = txt;
            _useGb = useGb;
            _capFt = null;
            InvalidateVisual();
        }

        // ==================================================================
        // 每帧推进
        // ==================================================================
        public void Tick(double dt)
        {
            if (dt <= 0 || double.IsNaN(dt)) return;
            if (dt > 0.25) dt = 0.25;

            _t += dt;
            if (_t > 3600.0) _t -= 3600.0;

            if (!_layoutValid) EnsureLayout();
            if (!_layoutValid) return;

            Point[] path = _useGb ? _pathGb : _pathMb;
            double len = _useGb ? _lenGb : _lenMb;
            if (path == null || path.Length < 8 || len <= 0.5) return;

            if (_useGbPath != _useGb)
            {
                _useGbPath = _useGb;
                _prog = 0.0;
                _dotInit = false;
                _fade = 0.0;
            }
            if (_fade < 1.0)
            {
                _fade += dt / HandoverFadeSec;
                if (_fade > 1.0) _fade = 1.0;
            }

            double speed = Clamp(len / CycleSeconds, MinSpeed, MaxSpeed);
            _prog += speed * dt / len;
            while (_prog >= 1.0) _prog -= 1.0;
            if (_prog < 0.0) _prog = 0.0;

            Point target = DotAt(path, _prog);
            if (!_dotInit) { _dotPos = target; _dotInit = true; }
            else
            {
                double k = 1.0 - Math.Exp(-dt / DotSmoothTau);
                _dotPos = new Point(_dotPos.X + (target.X - _dotPos.X) * k,
                                    _dotPos.Y + (target.Y - _dotPos.Y) * k);
            }
            InvalidateVisual();
        }

        // ==================================================================
        // 布局
        // ==================================================================
        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsInfinity(availableSize.Width) ? 106.0 : availableSize.Width;
            double h = double.IsInfinity(availableSize.Height) ? 106.0 : availableSize.Height;
            return new Size(w, h);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            _layoutValid = false;
        }

        private void EnsureLayout()
        {
            _w = ActualWidth;
            _h = ActualHeight;
            if (_w < 40 || _h < 40) { _layoutValid = false; return; }

            try { _ppi = VisualTreeHelper.GetDpi(this).PixelsPerDip; }
            catch { _ppi = 1.0; }

            var family = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
            _faceBold = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            _faceSemi = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            _faceNorm = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            _brText = BrushOf("TextPrimary", "#FFFFFF");
            _brSecond = BrushOf("TextSecond", "#8B96A6");
            // 状态色与处理器卡同源：同一个资源 key、同一个 55 分界
            _brGreen = BrushOf("RingGreen", "#63BE8C");
            _brRed = BrushOf("DangerRed", "#D97C85");
            _brLampOn = BrushOf("MemLampOn", "#FFC93A");
            _brLampOff = BrushOf("MemLampOff", "#E0524D");
            _brDot = BrushOf("DotWhite", "#F5F7FA");
            _brRingFill = BrushOf("MemBoxFill", "#12FFFFFF");

            _pad = Math.Round(Math.Max(1.5, _w * 0.02));

            var clip = new RectangleGeometry(new Rect(0, 0, _w, _h), 8, 8);
            clip.Freeze();
            _clipGeo = clip;

            _tempFont = Clamp(Math.Round(_h * 0.15, 1), 14.5, 17.0);
            _nameFont = Math.Max(9.5, _tempFont * 0.64);
            _headerH = Math.Ceiling(_tempFont * 1.34);
            _zoneTop = _pad + _headerH + 1.0;

            double contentW = _w - _pad * 2.0;
            double zoneH = _h - _zoneTop - _pad;
            if (zoneH < 30.0) zoneH = 30.0;

            // ---- 圆环 ----
            _ringOuter = Math.Min(zoneH * 0.325, contentW * 0.27);
            _ringBand = Math.Max(2.2, _ringOuter * 0.065);
            _ringR = _ringOuter - _ringBand * 0.55;
            _ringHole = Math.Max(6.0, _ringR - _ringBand * 0.5);
            _ringC = new Point(_w - _pad - _ringOuter, _h - _pad - _ringOuter - 0.5);

            // ---- 方框 ----
            double boxW = contentW * 0.40;
            double boxH = zoneH * 0.285;
            double chipW = contentW * 0.225;
            double chipH = zoneH * 0.196;
            _lampR = Math.Max(2.6, Math.Min(chipH * 0.21, 3.4));

            double boxX = _pad + 1.0;
            double boxY = _zoneTop + 2.5;
            _boxData = new Rect(boxX, boxY, boxW, boxH);

            double gbCx = _ringC.X;
            _boxGb = new Rect(gbCx - chipW / 2.0, boxY, chipW, chipH);

            double mbCx = boxX + boxW / 2.0;
            _boxMb = new Rect(mbCx - chipW / 2.0, _ringC.Y - chipH / 2.0, chipW, chipH);

            _lampGbC = new Point(_boxGb.Right + 2.2 + _lampR, _boxGb.Y + _boxGb.Height / 2.0);
            _lampMbC = new Point(_boxMb.Left - 2.2 - _lampR, _boxMb.Y + _boxMb.Height / 2.0);

            // ---- 字号：都按「最宽的那种字串」反推后定死，换数值时字形不跳 ----
            _nameFt = MakeText(_kind == CircuitKind.Gpu ? "显卡" : "内存", _nameFont, _brSecond, _faceNorm);

            // 方框字号：按 5 个字符（如 12.40）能放进方框反推；更长的串在绘制时再单独缩
            var probeCap = MakeText("00.00", 10.0, _brText, _faceBold);
            double capMaxW = Math.Max(8.0, _boxData.Width - 6.0);
            double capMaxH = Math.Max(8.0, _boxData.Height - 5.0) / 0.78;
            double capFit = probeCap.Width > 0.01 ? 10.0 * capMaxW / probeCap.Width : 13.0;
            _capFont = Clamp(Math.Min(capFit, capMaxH), 9.0, 17.0);

            // 圆环字号：按 3 位整数（利用率最大 100）能放进镂空区反推
            var probeLoad = MakeText("100", 10.0, _brText, _faceBold);
            double loadW = _ringHole * 2.0 * 0.92;
            _loadFont = Clamp(probeLoad.Width > 0.01 ? 10.0 * loadW / probeLoad.Width : 22.0, 10.0, 30.0);

            _gbFt = MakeText("GB", FitFont("GB", _boxGb.Width - 6.0, 13.0, _faceBold), _brText, _faceBold);
            _mbFt = MakeText("MB", FitFont("MB", _boxMb.Width - 6.0, 13.0, _faceBold), _brText, _faceBold);

            _capFt = null; _capFtKey = "";
            _loadFt = null; _loadFtKey = "";
            _tempFtKey = "";

            // ---- 画笔：状态色各备一套，绘制时按占用率切换，不逐帧新建 ----
            _penWire = MakePen(_brSecond, 1.0);
            _penBox = MakePen(_brSecond, 1.2);
            _penMemG = MakePen(_brGreen, 1.3);
            _penMemR = MakePen(_brRed, 1.3);
            _penRingG = MakePen(_brGreen, 1.2);
            _penRingR = MakePen(_brRed, 1.2);
            _penArcG = MakeArcPen(_brGreen);
            _penArcR = MakeArcPen(_brRed);
            _brGlowOn = MakeGlow(_brLampOn, 90);
            _brGlowOff = MakeGlow(_brLampOff, 70);
            _brDotGlow = MakeGlow(_brDot, 60);
            _brBreathG = MakeBreath(_brGreen);
            _brBreathR = MakeBreath(_brRed);

            // ---- 巡回路径 ----
            _pathGb = Resample(BuildGbPath(), PathSamples, out _lenGb);
            _pathMb = Resample(BuildMbPath(), PathSamples, out _lenMb);

            _dotInit = false;
            _layoutValid = true;
        }

        // ------------------------------------------------------------------
        // 闭合路径
        // ------------------------------------------------------------------
        private List<Point> BuildGbPath()
        {
            List<Point> p = new List<Point>(64);
            double portY = _boxGb.Y + _boxGb.Height / 2.0;
            double gbx = _boxGb.X + _boxGb.Width / 2.0;

            p.Add(_lampGbC);
            p.Add(new Point(_boxGb.Right, portY));

            p.Add(new Point(_boxGb.Right, _boxGb.Bottom));
            p.Add(new Point(gbx, _boxGb.Bottom));
            p.Add(PointOn(_ringC, _ringR, 270.0));
            AddArc(p, _ringC, _ringR, 270.0, -360.0);

            p.Add(new Point(gbx, _boxGb.Bottom));
            p.Add(new Point(_boxGb.Right, _boxGb.Bottom));
            p.Add(new Point(_boxGb.Right, _boxGb.Top));
            p.Add(new Point(_boxGb.Left, _boxGb.Top));
            p.Add(new Point(_boxGb.Left, _boxGb.Bottom));
            p.Add(new Point(gbx, _boxGb.Bottom));

            p.Add(new Point(_boxGb.Left, _boxGb.Bottom));
            p.Add(new Point(_boxGb.Left, portY));
            p.Add(new Point(_boxData.Right, portY));

            p.Add(new Point(_boxData.Right, _boxData.Top));
            p.Add(new Point(_boxData.Left, _boxData.Top));
            p.Add(new Point(_boxData.Left, _boxData.Bottom));
            p.Add(new Point(_boxData.Right, _boxData.Bottom));
            p.Add(new Point(_boxData.Right, portY));

            p.Add(new Point(_boxGb.Left, portY));
            p.Add(new Point(_boxGb.Left, _boxGb.Top));
            p.Add(new Point(_boxGb.Right, _boxGb.Top));
            p.Add(new Point(_boxGb.Right, portY));
            p.Add(_lampGbC);
            return p;
        }

        private List<Point> BuildMbPath()
        {
            List<Point> p = new List<Point>(64);
            double cy = _boxMb.Y + _boxMb.Height / 2.0;
            double cx = _boxMb.X + _boxMb.Width / 2.0;
            double dataCx = _boxData.X + _boxData.Width / 2.0;

            p.Add(_lampMbC);
            p.Add(new Point(_boxMb.Left, cy));

            p.Add(new Point(_boxMb.Left, _boxMb.Bottom));
            p.Add(new Point(_boxMb.Right, _boxMb.Bottom));
            p.Add(new Point(_boxMb.Right, cy));
            p.Add(PointOn(_ringC, _ringR, 180.0));
            AddArc(p, _ringC, _ringR, 180.0, -360.0);

            p.Add(new Point(_boxMb.Right, cy));
            p.Add(new Point(_boxMb.Right, _boxMb.Top));
            p.Add(new Point(_boxMb.Left, _boxMb.Top));
            p.Add(new Point(_boxMb.Left, _boxMb.Bottom));
            p.Add(new Point(_boxMb.Right, _boxMb.Bottom));
            p.Add(new Point(_boxMb.Right, cy));

            p.Add(new Point(_boxMb.Right, _boxMb.Top));
            p.Add(new Point(cx, _boxMb.Top));
            p.Add(new Point(dataCx, _boxData.Bottom));

            p.Add(new Point(_boxData.Right, _boxData.Bottom));
            p.Add(new Point(_boxData.Right, _boxData.Top));
            p.Add(new Point(_boxData.Left, _boxData.Top));
            p.Add(new Point(_boxData.Left, _boxData.Bottom));
            p.Add(new Point(dataCx, _boxData.Bottom));

            p.Add(new Point(cx, _boxMb.Top));
            p.Add(new Point(_boxMb.Left, _boxMb.Top));
            p.Add(new Point(_boxMb.Left, cy));
            p.Add(_lampMbC);
            return p;
        }

        private static void AddArc(List<Point> pts, Point c, double r, double startDeg, double sweepDeg)
        {
            int n = Math.Max(10, (int)Math.Round(Math.Abs(sweepDeg) / 5.0));
            for (int i = 1; i <= n; i++)
                pts.Add(PointOn(c, r, startDeg + sweepDeg * i / n));
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

        private static Point DotAt(Point[] path, double prog)
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

        // ==================================================================
        // 绘制
        // ==================================================================
        protected override void OnRender(DrawingContext dc)
        {
            if (!_layoutValid) EnsureLayout();
            if (!_layoutValid) return;

            if (_clipGeo != null) dc.PushClip(_clipGeo);
            // 状态色：与处理器卡同一个判定（≤55 绿，>55 红）；读不到利用率时按绿处理
            _hot = _loadValid && _load > LoadColorSplit;
            DrawWires(dc);
            DrawBoxes(dc);
            DrawRing(dc);
            DrawLamps(dc);
            DrawHeader(dc);
            DrawDot(dc);
            if (_clipGeo != null) dc.Pop();
        }

        private void DrawWires(DrawingContext dc)
        {
            double portY = _boxGb.Y + _boxGb.Height / 2.0;
            double gbx = _boxGb.X + _boxGb.Width / 2.0;
            double dataCx = _boxData.X + _boxData.Width / 2.0;

            dc.DrawLine(_penWire, new Point(_boxData.Right, portY), new Point(_boxGb.Left, portY));
            dc.DrawLine(_penWire, new Point(gbx, _boxGb.Bottom),
                                  new Point(gbx, _ringC.Y - _ringOuter));
            dc.DrawLine(_penWire, new Point(dataCx, _boxData.Bottom), new Point(dataCx, _boxMb.Top));
            dc.DrawLine(_penWire, new Point(_boxMb.Right, _boxMb.Y + _boxMb.Height / 2.0),
                                  new Point(_ringC.X - _ringOuter, _boxMb.Y + _boxMb.Height / 2.0));
            dc.DrawLine(_penWire, new Point(_boxGb.Right, portY),
                                  new Point(_lampGbC.X - _lampR, portY));
            dc.DrawLine(_penWire, new Point(_boxMb.Left, _boxMb.Y + _boxMb.Height / 2.0),
                                  new Point(_lampMbC.X + _lampR, _boxMb.Y + _boxMb.Height / 2.0));
        }

        /// <summary>
        /// 数据方框：只放真实容量数字（不再写「内存」「显存」这类文字）。
        /// 容量取不到时显示占位符，绝不编造数字。
        /// </summary>
        private void DrawBoxes(DrawingContext dc)
        {
            double r = Math.Max(2.5, _boxGb.Height * 0.24);

            dc.DrawRoundedRectangle(null, _hot ? _penMemR : _penMemG, _boxData, r, r);

            string txt = _capValid ? _capText : "--";
            if (_capFt == null || _capFtKey != txt)
            {
                var ft = MakeText(txt, _capFont, _brText, _faceBold);
                double avail = _boxData.Width - 6.0;
                // 只有真的放不下（例如 127.99 这种 6 字符）才针对这一串单独缩，
                // 常态数值字号固定，方框里的数字不会忽大忽小
                if (ft.Width > avail && ft.Width > 0.01)
                    ft = MakeText(txt, _capFont * avail / ft.Width, _brText, _faceBold);
                _capFt = ft;
                _capFtKey = txt;
            }
            DrawInkCentered(dc, _capFt, _capFont * 0.78,
                            _boxData.X + _boxData.Width / 2.0,
                            _boxData.Y + _boxData.Height / 2.0);

            dc.DrawRoundedRectangle(_brRingFill, _penBox, _boxGb, r, r);
            dc.DrawRoundedRectangle(_brRingFill, _penBox, _boxMb, r, r);
            if (_gbFt != null)
                dc.DrawText(_gbFt, new Point(_boxGb.X + (_boxGb.Width - _gbFt.Width) / 2.0,
                                             _boxGb.Y + (_boxGb.Height - _gbFt.Height) / 2.0));
            if (_mbFt != null)
                dc.DrawText(_mbFt, new Point(_boxMb.X + (_boxMb.Width - _mbFt.Width) / 2.0,
                                             _boxMb.Y + (_boxMb.Height - _mbFt.Height) / 2.0));
        }

        /// <summary>
        /// 圆环：不再写「占用」两个字。
        ///   显卡：环内是真实利用率整数，环带上是随利用率增长的占用弧。
        ///   内存：环内整块是淡淡的呼吸光（已用容量已经在方框里说完了）。
        /// </summary>
        private void DrawRing(DrawingContext dc)
        {
            double half = _ringBand * 0.5;
            Pen ringPen = _hot ? _penRingR : _penRingG;
            dc.DrawEllipse(_brRingFill, null, _ringC, _ringR + half, _ringR + half);
            dc.DrawEllipse(null, ringPen, _ringC, _ringR - half, _ringR - half);
            dc.DrawEllipse(null, ringPen, _ringC, _ringR + half, _ringR + half);

            if (ShowsLoad)
            {
                if (_loadValid && _load > 0.0)
                {
                    Geometry arc = ArcBuilder.Build(_ringC.X, _ringC.Y, _ringR, _load, true);
                    if (arc != null && arc != Geometry.Empty)
                        dc.DrawGeometry(null, _hot ? _penArcR : _penArcG, arc);
                }

                string txt = _loadValid
                    ? Math.Round(_load, 0).ToString("0", CultureInfo.InvariantCulture)
                    : "--";
                if (_loadFt == null || _loadFtKey != txt)
                {
                    _loadFt = MakeText(txt, _loadFont, _brText, _faceBold);
                    _loadFtKey = txt;
                }
                DrawInkCentered(dc, _loadFt, _loadFont * 0.78, _ringC.X, _ringC.Y);
            }
            else
            {
                // 呼吸灯：整块填满圆环内部，亮度随时间缓慢起伏（0.55 ~ 1.0）；
                // 颜色跟状态色走，所以内存吃紧时整块会转红，一眼能看出来
                double phase = 0.5 + 0.5 * Math.Sin(_t * 2.0 * Math.PI / BreathPeriod);
                dc.PushOpacity(0.55 + 0.45 * phase);
                dc.DrawEllipse(_hot ? _brBreathR : _brBreathG, null, _ringC, _ringHole, _ringHole);
                dc.Pop();
            }
        }

        private void DrawLamps(DrawingContext dc)
        {
            if (!_capValid)
            {
                // 容量读不到：两个灯都压成中性灰，不假装"当前是某个单位"
                dc.DrawEllipse(_brSecond, null, _lampGbC, _lampR, _lampR);
                dc.DrawEllipse(_brSecond, null, _lampMbC, _lampR, _lampR);
                return;
            }
            DrawOneLamp(dc, _lampGbC, _useGb);
            DrawOneLamp(dc, _lampMbC, !_useGb);
        }

        private void DrawOneLamp(DrawingContext dc, Point c, bool on)
        {
            Brush glow = on ? _brGlowOn : _brGlowOff;
            dc.DrawEllipse(glow, null, c, _lampR * 2.6, _lampR * 2.6);
            dc.DrawEllipse(on ? _brLampOn : _brLampOff, null, c, _lampR, _lampR);
        }

        private void DrawHeader(DrawingContext dc)
        {
            if (_tempText != null && _tempText.Length > 0)
            {
                string key = _tempText + "|" + _tempFont.ToString("0.0", CultureInfo.InvariantCulture);
                if (_tempFt == null || key != _tempFtKey)
                {
                    _tempFt = MakeText(_tempText, _tempFont, _tempBrush ?? _brText, _faceSemi);
                    _tempFtKey = key;
                }
                dc.DrawText(_tempFt, new Point(_pad, _pad));
            }

            if (_nameFt != null)
            {
                double y = _pad + Math.Max(0.0, ((_tempFt != null ? _tempFt.Height : _headerH) - _nameFt.Height) * 0.5);
                dc.DrawText(_nameFt, new Point(_w - _pad - _nameFt.Width, y));
            }
        }

        private void DrawDot(DrawingContext dc)
        {
            if (!_dotInit || _fade <= 0.004) return;
            double r = Math.Max(1.9, _ringBand * 0.95);
            bool push = _fade < 0.996;
            if (push) dc.PushOpacity(_fade);
            dc.DrawEllipse(_brDotGlow, null, _dotPos, r * 2.4, r * 2.4);
            dc.DrawEllipse(_brDot, null, _dotPos, r, r);
            if (push) dc.Pop();
        }

        private static void DrawInkCentered(DrawingContext dc, FormattedText ft, double inkH, double cx, double cy)
        {
            if (ft == null) return;
            dc.DrawText(ft, new Point(cx - ft.Width * 0.5, cy - ft.Baseline + inkH * 0.5));
        }

        // ==================================================================
        // 工具
        // ==================================================================
        private Brush MakeGlow(Brush src, byte alpha)
        {
            var sc = src as SolidColorBrush;
            Color c = sc != null ? sc.Color : Colors.White;
            var rg = new RadialGradientBrush(Color.FromArgb(alpha, c.R, c.G, c.B),
                                             Color.FromArgb(0, c.R, c.G, c.B));
            rg.Freeze();
            return rg;
        }

        /// <summary>呼吸灯底图：中心偏亮、边缘淡出的柔光，配 PushOpacity 做明暗起伏。</summary>
        private Brush MakeBreath(Brush src)
        {
            var sc = src as SolidColorBrush;
            Color c = sc != null ? sc.Color : Colors.White;
            var rg = new RadialGradientBrush();
            rg.GradientOrigin = new Point(0.5, 0.44);      // 光源略偏上，像从上方透进来
            rg.GradientStops.Add(new GradientStop(Color.FromArgb(96, c.R, c.G, c.B), 0.0));
            rg.GradientStops.Add(new GradientStop(Color.FromArgb(58, c.R, c.G, c.B), 0.60));
            rg.GradientStops.Add(new GradientStop(Color.FromArgb(14, c.R, c.G, c.B), 1.0));
            rg.Freeze();
            return rg;
        }

        private Pen MakePen(Brush brush, double thickness)
        {
            var p = new Pen(brush, thickness);
            p.Freeze();
            return p;
        }

        /// <summary>占用弧的笔：略粗于环带、圆头，压在两条环线之间。</summary>
        private Pen MakeArcPen(Brush brush)
        {
            var p = new Pen(brush, Math.Max(2.4, _ringBand * 0.95));
            p.StartLineCap = PenLineCap.Round;
            p.EndLineCap = PenLineCap.Round;
            p.Freeze();
            return p;
        }

        private double FitFont(string s, double maxW, double cap, Typeface face)
        {
            var probe = MakeText(s, 10.0, _brText, face);
            double f = probe.Width > 0.01 ? 10.0 * maxW / probe.Width : cap;
            if (f > cap) f = cap;
            if (f < 6.0) f = 6.0;
            return f;
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
                if (fallback != null && fallback.Length > 0 && fallback[0] == '#')
                {
                    var c = (Color)ColorConverter.ConvertFromString(fallback);
                    var s = new SolidColorBrush(c);
                    s.Freeze();
                    return s;
                }
            }
            catch { }
            return Brushes.White;
        }

        private FormattedText MakeText(string s, double size, Brush brush, Typeface face)
        {
            return new FormattedText(s == null ? "" : s,
                                     CultureInfo.CurrentUICulture,
                                     FlowDirection.LeftToRight,
                                     face, size, brush ?? Brushes.White, _ppi);
        }

        private static Point PointOn(Point c, double r, double deg)
        {
            double rad = deg * Math.PI / 180.0;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
