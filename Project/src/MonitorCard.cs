using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PerfMonitorWPF
{
    /// <summary>卡片类型：决定大环取哪个利用率、要不要画左下角三圈容量。</summary>
    public enum MonitorKind { Cpu, Gpu, Memory }

    /// <summary>
    /// 顶部硬件监控卡片（纯矢量自绘，无任何内嵌 TextBlock / Canvas）。
    ///
    /// ==== 统一的卡片局部几何模型（所有坐标由实际宽高 + 下列常量算出） ====
    ///   顶部区   y ∈ [_pad, _pad + _headerH]：左温度（14~16 DIP 半粗），右类别
    ///   绘图区   y ∈ [_zoneTop, _h - _pad]
    ///   主环     圆心 _bigC，外半径 _bigR，环带 _band（两个同心圆界定，中心镂空）
    ///   三圈     共同圆心 _smallC，半径 _r1 > _r2 > _r3，与大环外沿适度交叠
    ///   数位     外圈→左、内圈→中、中圈→右；锚点由「所属圆的半径」反解，互不共用中心
    ///   单位     三圈整体下沿，居中于 _smallC.X
    ///   分割线   两组圆形的横向中线 = 180°（9 点方向）；光点每经过一次触发数位过渡
    ///
    /// ==== 数据 / 布局 / 动画三层分离 ====
    ///   SetLoad / SetTemp / SetCapacity 只写字段并标脏；EnsureLayout 只在尺寸或单位变化时跑；
    ///   Tick(dt) 只推进动画，不读传感器、不重建布局、不新建 Brush 或 Geometry。
    /// </summary>
    public class MonitorCard : FrameworkElement
    {
        // ================= 可调阈值（三者互相独立，禁止互相复用） =================
        /// <summary>大环配色分界：利用率 ≤55 绿，>55 红（55 本身归绿）。</summary>
        public const double LoadColorSplit = 55.0;
        /// <summary>单位切换：显示值 &lt; 999 MB 用 MB，≥ 999 MB 用 GB。</summary>
        public const double UnitSwitchMb = 999.0;
        /// <summary>动画分支：已用容量 ≤200 MB 走低占用特殊路径（光点向内圈推进）。</summary>
        public const double LowUsagePathMb = 200.0;

        // ================= 几何常量 =================
        // 以下两个比例是用 Microsoft YaHei Bold 实测 0~9 的墨迹包围盒得到的最大值：
        //   墨迹高 / 字号 = 0.755~0.775，取 0.78；墨迹宽 / 字号 = 0.335("1")~0.595("4")，取 0.60。
        // 取最大值而不是平均值，保证 0~9 任意一个数字都不会顶出所属圆圈。
        private const double InkW = 0.60;
        private const double InkH = 0.78;
        private const double InkMargin = 1.5;      // 墨迹边界到圆弧内缘的安全距
        private const double FontCapRatio = 1.35;  // 三个数位的视觉权重上限（最大/最小）
        private const double CenterOrbitK = 0.18;  // 内圈数位的动画轨道半径 = 系数 × r3
        private const double SideArcDeg = 42.0;    // 外 / 中圈数位沿弧运动的行程（度）
        private const double CenterArcDeg = 78.0;  // 内圈数位沿弧运动的行程（度）

        // ================= 动画常量 =================
        // 一圈耗时：0% 约 4.0s，100% 约 2.25s。分割线每圈只会被跨过一次，所以这个下限
        // 直接决定「数位动画最长能等多久」——取 4s 是为了不超过 5s 的数据刷新间隔。
        private const double MinDotDegPerSec = 90.0;
        private const double MaxDotDegPerSec = 160.0;
        private const double MaxSmallDegPerSec = 190.0;
        private const double SpeedTauSec = 0.9;
        private const double DigitAnimSec = 0.42;   // 单个数字沿弧过渡时长
        private const double StallGuardSec = 8.0;   // 极端情况下防止数位永久落后于真值
        private const double MinBigTravelDeg = 200.0;

        private enum DotPhase
        {
            BigOrbit,      // 大环圆周
            TransferOut,   // 大环 → 三圈外圈
            OuterOrbit,    // 三圈外圈
            SpiralIn,      // 沿引导位向内推进（穿过中圈到内圈）
            InnerOrbit,    // 三圈内圈
            SpiralOut,     // 内圈向外回到外圈
            TransferBack   // 三圈外圈 → 大环
        }

        /// <summary>单个数位的状态：已显示值 / 目标值 / 沿弧过渡进度。</summary>
        private class DigitSlot
        {
            public int Shown;
            public int Target;
            public int From;
            public bool Animating;
            public double T;
            public bool Pending;
            public double PendingAge;
        }

        // ================= 外部数据 =================
        private MonitorKind _kind = MonitorKind.Cpu;
        private bool _loadValid;
        private double _load;
        private string _tempText;
        private Brush _tempBrush;
        private bool _capValid;
        private bool _useGb;
        private string _unit = "";
        private bool _lowUsagePath;

        private readonly DigitSlot[] _digits = new DigitSlot[3];

        // ================= 布局 =================
        private bool _layoutValid;
        private double _w, _h;
        private double _pad, _headerH, _zoneTop, _zoneH, _tempFont, _labelFont;
        private Point _bigC;
        private double _bigR, _band, _bigInnerR;
        private Point _smallC;
        private double _r1, _r2, _r3;
        private Typeface _faceBold, _faceSemi, _faceNorm;
        // 三个数位各自的几何：字号 / 墨迹宽高 / 锚点 / 动画轨道（圆心 + 半径 + 锚点角）
        private readonly double[] _dFont = new double[3];
        private readonly double[] _dW = new double[3];
        private readonly double[] _dH = new double[3];
        private readonly Point[] _dOrbitC = new Point[3];
        private readonly double[] _dOrbitR = new double[3];
        private readonly double[] _dAngle = new double[3];
        private readonly FormattedText[][] _digitFt = new FormattedText[3][];
        private FormattedText _unitFt, _naFt, _labelFt, _bigFt, _tempFt;
        private string _bigFtKey = "";
        private string _tempFtKey = "";
        private double _ppi = 1.0;

        // ================= 画刷 / 画笔 =================
        private Brush _brText, _brSecond, _brTrack, _brGreen, _brRed, _brRing, _brDot, _brHalo;
        private Brush _brHaloSource;
        private Pen _penRing1, _penRing2, _penRing3;
        private Pen _penTrackBig, _penArc;
        private double _penKeyBand = -1;
        private Brush _penKeyBrush;

        // ================= 动画状态 =================
        private double _bigA = 270.0;        // 大环角度（度；0=右，屏幕顺时针为正，光点逆时针走=递减）
        private double _bigTravel;
        private double _speed = MinDotDegPerSec;
        private DotPhase _phase = DotPhase.BigOrbit;
        private DotPhase _prevPhase = DotPhase.BigOrbit;
        private double _phaseT, _phaseDur;
        private double _smallA;
        private double _radTravel, _radTravelNeed;
        private Point _bezP0, _bezC1, _bezC2, _bezP3;
        private Point _dot;
        private double _prevAng = double.NaN;
        private bool _started;

        // ==================================================================
        public MonitorCard()
        {
            SnapsToDevicePixels = true;
            for (int i = 0; i < 3; i++) _digits[i] = new DigitSlot();
            _dAngle[0] = 180.0;
            _dAngle[1] = 90.0;
            _dAngle[2] = 0.0;
        }

        /// <summary>卡片类型（XAML 里写 Kind="Cpu" / "Gpu" / "Memory"）。</summary>
        public MonitorKind Kind
        {
            get { return _kind; }
            set
            {
                if (_kind == value) return;
                _kind = value;
                _layoutValid = false;
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        // ==================================================================
        // 数据入口（只写字段，绝不动画、不重排）
        // ==================================================================
        /// <summary>大环：硬件实时利用率 0-100（不是容量，也不是容量占用率）。</summary>
        public void SetLoad(double percent)
        {
            if (double.IsNaN(percent)) { _loadValid = false; InvalidateVisual(); return; }
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            if (_loadValid && Math.Abs(percent - _load) < 0.01) return;
            _loadValid = true;
            _load = percent;
            _bigFtKey = "";
            InvalidateVisual();
        }

        /// <summary>左上角温度。null / 空串 = 没有真实读数，整项隐藏，不做假读数。</summary>
        public void SetTemp(string text, Brush brush)
        {
            if (text != _tempText) { _tempText = text; _tempFtKey = ""; InvalidateVisual(); }
            if (brush != null && !ReferenceEquals(brush, _tempBrush)) { _tempBrush = brush; InvalidateVisual(); }
        }

        /// <summary>左下三圈：已用容量 / 总容量（字节）。CPU 卡片不需要调用。</summary>
        public void SetCapacity(long usedBytes, long totalBytes)
        {
            if (!(totalBytes > 0 && usedBytes >= 0 && usedBytes <= totalBytes * 2))
            {
                if (_capValid) { _capValid = false; InvalidateVisual(); }
                return;
            }
            bool first = !_capValid;
            _capValid = true;

            long mb = AppSettings.BinaryUnits ? 1048576L : 1000000L;
            double mbVal = (double)usedBytes / (double)mb;

            // 两个阈值各管一件事，互不影响：
            //   999 MB → 只决定显示单位；200 MB → 只决定光点动画是否向内圈推进
            bool useGb = mbVal >= UnitSwitchMb;
            _lowUsagePath = mbVal <= LowUsagePathMb;

            int d0, d1, d2;
            if (!useGb)
            {
                int v = (int)Math.Floor(mbVal);
                if (v < 0) v = 0;
                if (v > 998) v = 998;                 // 999 起已经归 GB 管
                d0 = v / 100;
                d1 = (v / 10) % 10;
                d2 = v % 10;
            }
            else
            {
                long gb = mb * (AppSettings.BinaryUnits ? 1024L : 1000L);
                // +1e-4 个「百分位」抵消浮点误差：1.01 GB 直接 floor 会算成 100（显示 1.00）
                int h = (int)Math.Floor((double)usedBytes / (double)gb * 100.0 + 0.0001);
                if (h < 0) h = 0;
                if (h > 999) h = 999;
                d0 = h / 100;
                d1 = (h / 10) % 10;
                d2 = h % 10;
            }

            string unit = useGb ? "GB" : "MB";
            bool relayout = (_useGb != useGb) || (_unit != unit);
            _useGb = useGb;
            _unit = unit;

            // 圈 → 数位槽永远固定为「外圈 → 内圈 → 中圈」，与单位无关：
            //   槽0 = 外圈 = 最高位（MB 的百位 / GB 的整数位）
            //   槽1 = 内圈 = 中间位（MB 的十位 / GB 的十分位）
            //   槽2 = 中圈 = 最低位（MB 的个位 / GB 的百分位）
            SetDigitTarget(0, d0);
            SetDigitTarget(1, d1);
            SetDigitTarget(2, d2);

            if (first)
            {
                // 首次拿到真实容量：直接落到真值，不空转、不假装过渡
                for (int i = 0; i < 3; i++)
                {
                    DigitSlot d = _digits[i];
                    d.Shown = d.Target;
                    d.From = d.Target;
                    d.Animating = false;
                    d.Pending = false;
                    d.T = 1.0;
                }
            }

            if (relayout) { _layoutValid = false; InvalidateVisual(); }
        }

        // ==================================================================
        // 每帧推进（dt = 真实秒数，由 Stopwatch 提供）
        // ==================================================================
        public void Tick(double dt)
        {
            if (dt <= 0 || double.IsNaN(dt)) return;
            if (dt > 0.25) dt = 0.25;

            if (!_layoutValid) EnsureLayout();
            if (!_started) { _started = true; _bigA = 330.0; _prevAng = _bigA; }

            // 光点速度跟真实利用率走，再平滑，避免数据抖动导致忽快忽停
            double target = _loadValid
                ? MinDotDegPerSec + (MaxDotDegPerSec - MinDotDegPerSec) * (_load / 100.0)
                : MinDotDegPerSec;
            double k = 1.0 - Math.Exp(-dt / SpeedTauSec);
            _speed += (target - _speed) * k;

            TickDigits(dt);

            if (_kind == MonitorKind.Cpu)
            {
                // 处理器没有三圈，只走主环；光点照常绕行，到 9 点照常触发（无数位可动）
                _bigA = Norm360(_bigA - _speed * dt);
                _bigTravel += _speed * dt;
                CheckSplitLine();
                InvalidateVisual();
                return;
            }

            switch (_phase)
            {
                case DotPhase.BigOrbit: StepBigOrbit(dt); break;
                case DotPhase.TransferOut: StepTransfer(dt, true); break;
                case DotPhase.OuterOrbit: StepOuter(dt); break;
                case DotPhase.SpiralIn: StepSpiral(dt, true); break;
                case DotPhase.InnerOrbit: StepInner(dt); break;
                case DotPhase.SpiralOut: StepSpiral(dt, false); break;
                case DotPhase.TransferBack: StepTransfer(dt, false); break;
            }
            // 三圈角度每帧归一：否则连续跑几小时后角度累积到百万级，
            // StepInner 里「补到 70° 以上」的 while 会空转上万次
            _smallA = Norm360(_smallA);
            CheckSplitLine();
            InvalidateVisual();
        }

        // ---------------- 相位 ----------------

        private void StepBigOrbit(double dt)
        {
            double step = _speed * dt;
            double toExit = Norm360(_bigA - 180.0);        // 逆时针到 9 点还差多少度

            if (_bigTravel >= MinBigTravelDeg && toExit <= step)
            {
                _bigA = 180.0;
                _bigTravel = 0;
                // 光点此刻正压在分割线上（横向中线 9 点方向）——这就是一次到达事件。
                // 必须在这里显式触发：下一帧相位已切成转场，没有圆周角度，交叉检测会被跳过。
                TriggerDigits();
                BeginTransferOut();
                return;
            }
            _bigA = Norm360(_bigA - step);
            _bigTravel += step;
        }

        private void BeginTransferOut()
        {
            _bezP0 = PointOn(_bigC, _bigR - _band / 2, 180.0);
            double ang = AngleOf(_smallC, _bigC);           // 外圈上朝向大环的那一点
            _bezP3 = PointOn(_smallC, _r1, ang);

            // 起点切线 = 大环逆时针切线，终点切线 = 小圈顺时针切线（两圆啮合方向），
            // 接缝处切线连续，光点不会「一进三圈就掉头」
            Vector v0 = CcwTangent(180.0);
            Vector v3 = CwTangent(ang);
            double kk = Math.Max(4.0, Dist(_bezP0, _bezP3) * 0.55);
            _bezC1 = new Point(_bezP0.X + v0.X * kk, _bezP0.Y + v0.Y * kk);
            _bezC2 = new Point(_bezP3.X - v3.X * kk, _bezP3.Y - v3.Y * kk);

            _phase = DotPhase.TransferOut;
            _phaseT = 0;
            _phaseDur = Clamp(Dist(_bezP0, _bezP3) / Math.Max(8.0, LinearSpeed()) * 1.1, 0.35, 0.9);
            _smallA = Norm360(ang);
            _dot = _bezP0;
        }

        private void BeginTransferBack()
        {
            _bezP0 = PointOn(_smallC, _r1, _smallA);
            _bezP3 = PointOn(_bigC, _bigR - _band / 2, 180.0);
            Vector v0 = CwTangent(_smallA);
            Vector v3 = CcwTangent(180.0);
            double kk = Math.Max(4.0, Dist(_bezP0, _bezP3) * 0.55);
            _bezC1 = new Point(_bezP0.X + v0.X * kk, _bezP0.Y + v0.Y * kk);
            _bezC2 = new Point(_bezP3.X - v3.X * kk, _bezP3.Y - v3.Y * kk);

            _phase = DotPhase.TransferBack;
            _phaseT = 0;
            _phaseDur = Clamp(Dist(_bezP0, _bezP3) / Math.Max(8.0, LinearSpeed()) * 1.1, 0.35, 0.9);
        }

        private void StepTransfer(double dt, bool outward)
        {
            _phaseT += dt;
            double t = _phaseT / _phaseDur;
            if (t > 1) t = 1;
            double e = t * t * (3 - 2 * t);                 // smoothstep
            _dot = Bez(_bezP0, _bezC1, _bezC2, _bezP3, e);   // 两个方向都是 P0 → P3

            if (t < 1) return;

            if (outward)
            {
                _phase = DotPhase.OuterOrbit;
                _smallA = Norm360(AngleOf(_smallC, _bezP3));
            }
            else
            {
                _phase = DotPhase.BigOrbit;
                _bigA = 180.0;
                _bigTravel = 0;
                _dot = _bezP3;
            }
        }

        private void StepOuter(double dt)
        {
            _smallA += SmallStep(dt);                       // 三圈顺时针（与大环反向啮合）
            _dot = PointOn(_smallC, _r1, _smallA);

            if (_smallA >= 180.0)                           // 到达引导位（三圈 9 点 = 分割线）
            {
                _smallA = 180.0;
                _dot = PointOn(_smallC, _r1, _smallA);
                TriggerDigits();                            // 同样是「到达分割线」事件
                if (_lowUsagePath && _capValid)
                {
                    _phase = DotPhase.SpiralIn;
                    _radTravel = 0;
                    _radTravelNeed = 150.0;             // 由外到内走 150°
                }
                else
                {
                    BeginTransferBack();
                }
            }
        }

        private void StepSpiral(double dt, bool inward)
        {
            double step = SmallStep(dt);
            _smallA += step;
            _radTravel += step;

            double t = _radTravelNeed <= 0 ? 1.0 : _radTravel / _radTravelNeed;
            if (t > 1) t = 1;
            double e = t * t * (3 - 2 * t);
            double r = inward
                ? _r1 + (_r3 - _r1) * e                    // 外 → 内
                : _r3 + (_r1 - _r3) * e;                   // 内 → 外
            _dot = PointOn(_smallC, r, _smallA);

            if (t < 1) return;

            if (inward)
            {
                _phase = DotPhase.InnerOrbit;
                _radTravelNeed = 330.0;                    // 内圈再绕 330°
                _radTravel = 0;
            }
            else
            {
                BeginTransferBack();
            }
        }

        private void StepInner(double dt)
        {
            double step = SmallStep(dt);
            _smallA += step;
            _radTravel += step;
            _dot = PointOn(_smallC, _r3, _smallA);

            if (_radTravel >= _radTravelNeed)
            {
                _phase = DotPhase.SpiralOut;
                _radTravel = 0;
                // 回外圈的同时转到「朝向大环」的角度，离开方向才自然
                double target = AngleOf(_smallC, _bigC) - 90.0;
                double delta = target - _smallA;
                while (delta < 70.0) delta += 360.0;
                _radTravelNeed = delta;
            }
        }

        // ---------------- 分割线触发 ----------------

        /// <summary>当前光点在各自圆周上的角度；转场阶段没有圆周角度，返回 NaN。</summary>
        private double CurrentAngle()
        {
            switch (_phase)
            {
                case DotPhase.BigOrbit: return _bigA;
                case DotPhase.OuterOrbit:
                case DotPhase.SpiralIn:
                case DotPhase.InnerOrbit:
                case DotPhase.SpiralOut: return Norm360(_smallA);
                default: return double.NaN;
            }
        }

        /// <summary>
        /// 分割线 = 两组圆形的横向中线（180°，9 点方向）。光点每跨过这条线一次，
        /// 就检查三个数位的最新目标值：变了的才播放沿弧过渡，没变的一动不动。
        /// 这是数位动画唯一的触发源（不是定时器周期性改字）。
        /// </summary>
        private void CheckSplitLine()
        {
            if (_phase != _prevPhase) { _prevPhase = _phase; _prevAng = double.NaN; }

            double ang = CurrentAngle();
            if (double.IsNaN(ang)) { _prevAng = double.NaN; return; }
            if (double.IsNaN(_prevAng)) { _prevAng = ang; return; }

            double sweep = ang - _prevAng;
            if (sweep < -180.0) sweep += 360.0;
            else if (sweep > 180.0) sweep -= 360.0;

            if (Math.Abs(sweep) > 0.0001)
            {
                double toLine = 180.0 - _prevAng;
                if (toLine < -180.0) toLine += 360.0;
                else if (toLine > 180.0) toLine -= 360.0;
                if (toLine / sweep >= 0.0 && Math.Abs(toLine) <= Math.Abs(sweep)) TriggerDigits();
            }
            _prevAng = ang;
        }

        /// <summary>光点到达分割线：只放行真正变过的数位，正在过渡的不打断。</summary>
        private void TriggerDigits()
        {
            for (int i = 0; i < 3; i++)
            {
                DigitSlot d = _digits[i];
                if (d.Animating) continue;          // 已经在动的数位不重启动画
                if (d.Pending || d.Target != d.Shown) StartDigitAnim(d);
            }
        }

        // ---------------- 数字 ----------------

        private void SetDigitTarget(int slot, int value)
        {
            DigitSlot d = _digits[slot];
            if ((d.Animating || d.Pending) && d.Target == value) return;
            if (!d.Animating && !d.Pending && d.Shown == value) return;

            d.Target = value;
            if (d.Pending) return;          // 已经排着队，改目标即可，等分割线统一放行

            d.Pending = true;
            d.PendingAge = 0;
        }

        private void StartDigitAnim(DigitSlot d)
        {
            d.Pending = false;
            d.PendingAge = 0;
            if (d.Shown == d.Target) { d.Animating = false; return; }

            // 动画中途来了新数据：以最新真值为准，从当前视觉位置继续，不排队播旧数字
            d.From = d.Animating ? (d.T < 0.5 ? d.From : d.Target) : d.Shown;
            if (d.From == d.Target) { d.Shown = d.Target; d.Animating = false; return; }
            d.T = 0;
            d.Animating = true;
        }

        private void TickDigits(double dt)
        {
            for (int i = 0; i < 3; i++)
            {
                DigitSlot d = _digits[i];
                if (d.Animating)
                {
                    d.T += dt / DigitAnimSec;
                    if (d.T >= 1)
                    {
                        d.T = 1;
                        d.Animating = false;
                        d.Shown = d.Target;
                    }
                }
                else if (d.Pending)
                {
                    d.PendingAge += dt;
                    // 兜底：光点因故长期没经过分割线（例如刚显隐切换），也不能永久停在旧值
                    if (d.PendingAge >= StallGuardSec) StartDigitAnim(d);
                }
            }
        }

        // ==================================================================
        // 布局（尺寸 / 单位 / DPI 变化时才跑）
        // ==================================================================
        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsInfinity(availableSize.Width) ? 96.0 : availableSize.Width;
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
            if (_w < 24 || _h < 24) { _layoutValid = false; return; }

            try { _ppi = VisualTreeHelper.GetDpi(this).PixelsPerDip; }
            catch { _ppi = 1.0; }

            var family = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
            _faceBold = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            _faceSemi = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            _faceNorm = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            _brText = BrushOf("TextPrimary", "#FFFFFF");
            _brSecond = BrushOf("TextSecond", "#8B96A6");
            _brTrack = BrushOf("RingTrack", "#26FFFFFF");
            _brGreen = BrushOf("RingGreen", "#63BE8C");
            _brRed = BrushOf("DangerRed", "#D97C85");
            _brRing = _brGreen;
            _brDot = BrushOf("DotWhite", "#F5F7FA");       // 光点固定白色，不继承状态色

            _pad = Math.Round(Math.Max(1.5, _w * 0.02));

            // ---------- 顶部：温度是重要数据（14~16 DIP 半粗），类别略小 ----------
            _tempFont = Clamp(Math.Round(_h * 0.145, 1), 14.0, 16.0);
            _labelFont = Math.Max(9.5, _tempFont * 0.72);
            _headerH = Math.Ceiling(_tempFont * 1.34);
            _zoneTop = _pad + _headerH + 1.0;
            _zoneH = _h - _zoneTop - _pad;
            if (_zoneH < 24.0) _zoneH = 24.0;
            double contentW = Math.Max(24.0, _w - _pad * 2);

            _labelFt = MakeText(KindLabel(), _labelFont, _brSecond, _faceNorm);

            if (_kind == MonitorKind.Cpu)
            {
                _bigR = Math.Max(10.0, Math.Min(_zoneH / 2.0, contentW / 2.0) * 0.96);
                _bigC = new Point(_w / 2.0, _zoneTop + _zoneH / 2.0);
                _smallC = _bigC;
                _r1 = _r2 = _r3 = 0.0;
                _penRing1 = null; _penRing2 = null; _penRing3 = null;
            }
            else
            {
                LayoutRings(contentW);
            }

            _band = Math.Max(3.0, _bigR * 0.235);
            _bigInnerR = Math.Max(6.0, _bigR - _band);

            _bigFtKey = "";
            _tempFtKey = "";
            _penKeyBand = -1;          // 尺寸变了，大环画笔必须重建
            _layoutValid = true;
        }

        // ------------------------------------------------------------------
        // 三圈 + 主环 + 数位锚点：一套几何解，不是手填坐标
        // ------------------------------------------------------------------
        private void LayoutRings(double contentW)
        {
            // ---- 1) 三圈半径：横向 / 纵向双预算下取「不缩小的最大」 ----
            _r1 = Math.Min(_zoneH * 0.33, contentW * 0.29);
            _r2 = _r1 * 0.80;
            _r3 = _r1 * 0.575;

            double gap = Math.Max(1.6, _r1 * 0.075);
            double m = InkMargin;
            // 内圈数位的锚点就是内圈圆心：0.5*f*sqrt(InkW²+InkH²) ≤ r3 - m
            double fcLimit = (_r3 - m) / (0.5 * Math.Sqrt(InkW * InkW + InkH * InkH));

            // ---- 2) 三个数位字号：各自圆内解几何最大，再按视觉权重上限收敛 ----
            double fc = fcLimit, fl = 0, fr = 0;
            for (int it = 0; it < 40; it++)
            {
                double a = gap + 0.5 * InkW * fc;
                fl = SideFont(_r1, a, m);
                fr = SideFont(_r2, a, m);
                double fmin = Math.Min(fc, Math.Min(fl, fr));
                double next = Math.Min(fcLimit, fmin * FontCapRatio);
                double damped = fc * 0.45 + next * 0.55;
                if (Math.Abs(damped - fc) < 0.005) { fc = damped; break; }
                fc = damped;
            }
            {
                double a = gap + 0.5 * InkW * fc;
                fl = SideFont(_r1, a, m);
                fr = SideFont(_r2, a, m);
                double cap = Math.Min(fc, Math.Min(fl, fr)) * FontCapRatio;
                _dFont[0] = Math.Min(fl, cap);
                _dFont[1] = Math.Min(fc, cap);
                _dFont[2] = Math.Min(fr, cap);
            }
            // 硬性后置校验：任何一个数位越过所属圆（含迭代误差），整体等比回缩，
            // 保证「文字边界框一定在圈内」这条不依赖运气
            for (int guard = 0; guard < 60; guard++)
            {
                if (FitOk(gap, m)) break;
                for (int i = 0; i < 3; i++) _dFont[i] *= 0.994;
            }

            for (int i = 0; i < 3; i++)
            {
                _dW[i] = InkW * _dFont[i];
                _dH[i] = InkH * _dFont[i];
            }
            double rho0 = gap + 0.5 * (_dW[1] + _dW[0]);    // 外圈数字（左）的锚点半径
            double rho2 = gap + 0.5 * (_dW[1] + _dW[2]);    // 中圈数字（右）的锚点半径
            double needClear = Corner(rho2, _dW[2], _dH[2]) + 1.5;

            // ---- 3) 单位：三圈整体下沿，先量宽度（主环要让开它） ----
            double uf = Math.Max(9.0, _tempFont * 0.66);
            _unitFt = MakeText(_unit, uf, _brSecond, _faceNorm);
            double unitRight = _pad + _r1 + (_unitFt != null ? _unitFt.Width * 0.5 : 0.0) + 1.5;

            // ---- 4) 主环半径：与三圈横向挨紧（允许 0~6px 交叠），纵向正好用满绘图区 ----
            //     从小到大扫，取「仍然满足约束的最大值」：卡片变宽时主环会自动长大，
            //     保证两组圆始终是衔接关系，不会因为留白变大而各走各的。
            double best = -1.0;
            for (double cand = _r1 * 0.78; cand <= _r1 * 1.40; cand += 0.2)
            {
                double s = _r1 + cand;
                double dy = _zoneH - s;
                double dx = contentW - s;
                bool ok = dy >= 0.0 && dx > 2.0;
                if (ok)
                {
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    double overlap = s - dist;                     // >0 = 两圆相交
                    double innerR = cand - Math.Max(3.0, cand * 0.235);
                    ok = overlap <= 6.0                            // 交叠过多会压到数位
                         && overlap >= -2.0                        // 之间不能拉出明显空隙
                         && dist - innerR >= needClear             // 大环内缘不得切到中圈数字
                         && (_w - _pad - 2.0 * cand) >= unitRight; // 大环左沿不得压住单位文字
                }
                if (ok) { best = cand; continue; }
                if (best > 0.0) break;
            }
            if (best < 0.0) best = Math.Min(_r1 * 0.95, _zoneH * 0.36);
            _bigR = Math.Max(10.0, best);

            // ---- 5) 圆心：大环贴右下角；三圈贴左，纵向由「纵向预算正好用满」反推 ----
            _bigC = new Point(_w - _pad - _bigR, _zoneTop + _zoneH - _bigR);
            double dyReal = _zoneH - (_bigR + _r1);
            if (dyReal < 0.0) dyReal = 0.0;
            _smallC = new Point(_pad + _r1, _bigC.Y - dyReal);

            // ---- 6) 每个数位自己的锚点与动画轨道（互不共用中心点） ----
            _dOrbitC[0] = _smallC;
            _dOrbitR[0] = rho0;
            _dAngle[0] = 180.0;                                  // 外圈：左

            _dOrbitR[1] = Math.Max(1.2, CenterOrbitK * _r3);
            _dOrbitC[1] = new Point(_smallC.X, _smallC.Y - _dOrbitR[1]);
            _dAngle[1] = 90.0;                                   // 内圈：锚点落在圆心（轨道在锚点上方）

            _dOrbitC[2] = _smallC;
            _dOrbitR[2] = rho2;
            _dAngle[2] = 0.0;                                    // 中圈：右

            // ---- 7) 三圈笔：外→内逐级变淡变细，保证三条轮廓都看得清 ----
            _penRing1 = MakePen(0x4D, Math.Max(1.5, _r1 * 0.062));
            _penRing2 = MakePen(0x3D, Math.Max(1.4, _r1 * 0.058));
            _penRing3 = MakePen(0x33, Math.Max(1.3, _r1 * 0.054));

            // ---- 8) 数位字形缓存（10 个数字 × 3 个字号，布局期建一次） ----
            for (int i = 0; i < 3; i++)
            {
                if (_digitFt[i] == null) _digitFt[i] = new FormattedText[10];
                for (int v = 0; v < 10; v++)
                    _digitFt[i][v] = MakeText(v.ToString(CultureInfo.InvariantCulture),
                                              _dFont[i], _brText, _faceBold);
            }
            _naFt = MakeText("--", _dFont[1], _brSecond, _faceBold);
        }

        /// <summary>
        /// 某个半径 r 的圆内、锚点距圆心 (gap + 0.5·InkW·fc) 时，数位字号能取多大。
        /// 由「墨迹框最远角 ≤ r - m」列方程：(A + InkW·f)² + (0.5·InkH·f)² = (r-m)²，
        /// 解一元二次方程正根；系数从 InkW / InkH 推导，不另设一套魔数。
        /// </summary>
        private static double SideFont(double r, double a, double m)
        {
            double R = r - m;
            if (R <= 0.0) return 6.0;
            double p = InkW * InkW + 0.25 * InkH * InkH;
            double q = 2.0 * InkW * a;
            double c = a * a - R * R;
            double disc = q * q - 4.0 * p * c;
            if (disc <= 0.0) return 6.0;
            double f = (-q + Math.Sqrt(disc)) / (2.0 * p);
            return f > 4.0 ? f : 4.0;
        }

        /// <summary>数位墨迹框最远角到圆心的距离。</summary>
        private static double Corner(double rho, double w, double h)
        {
            double x = rho + w * 0.5;
            double y = h * 0.5;
            return Math.Sqrt(x * x + y * y);
        }

        private bool FitOk(double gap, double m)
        {
            double rho0 = gap + 0.5 * (InkW * _dFont[1] + InkW * _dFont[0]);
            double rho2 = gap + 0.5 * (InkW * _dFont[1] + InkW * _dFont[2]);
            if (Corner(rho0, InkW * _dFont[0], InkH * _dFont[0]) > _r1 - m) return false;
            if (Corner(0.0, InkW * _dFont[1], InkH * _dFont[1]) > _r3 - m) return false;
            if (Corner(rho2, InkW * _dFont[2], InkH * _dFont[2]) > _r2 - m) return false;
            return true;
        }

        private Pen MakePen(byte alpha, double thickness)
        {
            var b = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
            b.Freeze();
            var p = new Pen(b, thickness);
            p.Freeze();
            return p;
        }

        private string KindLabel()
        {
            if (_kind == MonitorKind.Cpu) return "处理器";
            if (_kind == MonitorKind.Gpu) return "显卡";
            return "内存";
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

        // ==================================================================
        // 绘制
        // ==================================================================
        protected override void OnRender(DrawingContext dc)
        {
            if (!_layoutValid) EnsureLayout();
            if (!_layoutValid) return;

            _brRing = (_loadValid && _load > LoadColorSplit) ? _brRed : _brGreen;

            DrawBigRing(dc);
            if (_kind != MonitorKind.Cpu && _r1 > 0.0)
            {
                DrawSmallRings(dc);
                DrawCapacityDigits(dc);
            }
            DrawHeader(dc);
            DrawDot(dc);
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

            if (_labelFt != null)
            {
                // 类别与温度同一顶部行、光学居中对齐（不是贴着顶端）
                double y = _pad + Math.Max(0.0, ((_tempFt != null ? _tempFt.Height : _headerH) - _labelFt.Height) * 0.5);
                dc.DrawText(_labelFt, new Point(_w - _pad - _labelFt.Width, y));
            }
        }

        private void DrawBigRing(DrawingContext dc)
        {
            double rr = _bigR - _band / 2;

            if (_penTrackBig == null || _penKeyBand != _band || !ReferenceEquals(_penKeyBrush, _brRing))
            {
                var t = new Pen(_brTrack, _band);
                t.Freeze();
                _penTrackBig = t;

                var a = new Pen(_brRing, _band);
                a.StartLineCap = PenLineCap.Round;
                a.EndLineCap = PenLineCap.Round;
                a.Freeze();
                _penArc = a;

                _penKeyBand = _band;
                _penKeyBrush = _brRing;
            }

            dc.DrawEllipse(null, _penTrackBig, _bigC, rr, rr);

            if (_loadValid && _load > 0.0)
            {
                Geometry geo = ArcBuilder.Build(_bigC.X, _bigC.Y, rr, _load, true);
                if (geo != null && geo != Geometry.Empty) dc.DrawGeometry(null, _penArc, geo);
            }

            // 中心只显示整数，不带百分号
            string txt = _loadValid
                ? Math.Round(_load, 0).ToString("0", CultureInfo.InvariantCulture)
                : "--";
            if (_bigFt == null || _bigFtKey != txt)
            {
                _bigFt = FitText(txt, _bigInnerR * 2.0 * 0.94, _bigInnerR * 2.0 * 0.92, _faceBold);
                _bigFtKey = txt;
            }
            dc.DrawText(_bigFt, new Point(_bigC.X - _bigFt.Width / 2, _bigC.Y - _bigFt.Height / 2));
        }

        /// <summary>按可用宽高把文字放到最大（10pt 探测后反推字号，1~3 位数都能贴边）。</summary>
        private FormattedText FitText(string s, double maxW, double maxH, Typeface face)
        {
            var probe = MakeText(s, 10.0, _brText, face);
            double fw = probe.Width > 0.01 ? 10.0 * maxW / probe.Width : 10.0;
            double fh = probe.Height > 0.01 ? 10.0 * maxH / probe.Height : 10.0;
            double f = Math.Min(fw, fh);
            if (f < 6.0) f = 6.0;
            if (f > 46.0) f = 46.0;
            return MakeText(s, f, _brText, face);
        }

        private void DrawSmallRings(DrawingContext dc)
        {
            if (_penRing1 != null) dc.DrawEllipse(null, _penRing1, _smallC, _r1, _r1);
            if (_penRing2 != null) dc.DrawEllipse(null, _penRing2, _smallC, _r2, _r2);
            if (_penRing3 != null) dc.DrawEllipse(null, _penRing3, _smallC, _r3, _r3);
        }

        private void DrawCapacityDigits(DrawingContext dc)
        {
            if (!_capValid)
            {
                if (_naFt != null)
                    dc.DrawText(_naFt, new Point(_smallC.X - _naFt.Width / 2, _smallC.Y - _naFt.Height / 2));
                return;
            }

            for (int i = 0; i < 3; i++) DrawOneDigit(dc, i);

            if (_unitFt != null && _unit != null && _unit.Length > 0)
            {
                dc.DrawText(_unitFt, new Point(_smallC.X - _unitFt.Width / 2,
                                               _smallC.Y + _r1 + 1.5));
            }
        }

        /// <summary>
        /// 画一个数位。过渡时：旧数字沿所属圆的圆周逆时针滑出，新数字从顺时针一侧沿同一
        /// 方向滑入并停在锚点；两者都只做圆周平移，不做自身旋转，字形始终正向可读。
        /// </summary>
        private void DrawOneDigit(DrawingContext dc, int slot)
        {
            DigitSlot d = _digits[slot];
            FormattedText[] set = _digitFt[slot];
            if (set == null) return;

            Point orbit = _dOrbitC[slot];
            double rho = _dOrbitR[slot];
            double a0 = _dAngle[slot];
            double span = slot == 1 ? CenterArcDeg : SideArcDeg;
            double inkH = _dH[slot];

            if (d.Animating && d.T < 1.0)
            {
                double e = d.T * d.T * (3.0 - 2.0 * d.T);       // smoothstep

                double oa = 1.0 - e * 1.35;
                if (oa < 0.0) oa = 0.0;
                DrawGlyph(dc, set[ClampDigit(d.From)], PointOn(orbit, rho, a0 - span * e), oa, inkH);

                double ia = (e - 0.18) / 0.82;
                if (ia < 0.0) ia = 0.0;
                if (ia > 1.0) ia = 1.0;
                DrawGlyph(dc, set[ClampDigit(d.Target)], PointOn(orbit, rho, a0 + span * (1.0 - e)), ia, inkH);
            }
            else
            {
                DrawGlyph(dc, set[ClampDigit(d.Shown)], PointOn(orbit, rho, a0), 1.0, inkH);
            }
        }

        /// <summary>
        /// 把数字的「墨迹中心」（不是行框中心）精确压到 center：行框比墨迹高得多，
        /// 若按 Height/2 居中，字形会整体下沉约 0.05×字号，紧贴边界的那个数位就会越线。
        /// </summary>
        private void DrawGlyph(DrawingContext dc, FormattedText ft, Point center, double opacity, double inkH)
        {
            if (ft == null || opacity <= 0.004) return;
            bool pushOp = opacity < 0.996;
            if (pushOp) dc.PushOpacity(opacity);
            dc.DrawText(ft, new Point(center.X - ft.Width * 0.5,
                                      center.Y - ft.Baseline + inkH * 0.5));
            if (pushOp) dc.Pop();
        }

        private static int ClampDigit(int v)
        {
            if (v < 0) return 0;
            if (v > 9) return 9;
            return v;
        }

        private void DrawDot(DrawingContext dc)
        {
            Point p = CurrentDot();
            double r = Math.Max(1.8, _band * 0.30);
            double halo = r * 2.6;

            if (_brHalo == null || !ReferenceEquals(_brHaloSource, _brDot))
            {
                _brHaloSource = _brDot;
                var sc = _brDot as SolidColorBrush;
                Color c = sc != null ? sc.Color : Color.FromRgb(0xF5, 0xF7, 0xFA);
                var rg = new RadialGradientBrush(Color.FromArgb(64, c.R, c.G, c.B),
                                                 Color.FromArgb(0, c.R, c.G, c.B));
                rg.Freeze();
                _brHalo = rg;
            }

            dc.DrawEllipse(_brHalo, null, p, halo, halo);   // 非常克制的白色柔光
            dc.DrawEllipse(_brDot, null, p, r, r);          // 纯白实心点
        }

        private Point CurrentDot()
        {
            // 只有纯在大环上绕行时才由角度反推；其余相位（含两条转场）的位置
            // 都由状态机逐帧写进 _dot，直接用，避免转场时被大环角度拽回去
            if (_phase == DotPhase.BigOrbit)
                return PointOn(_bigC, _bigR - _band / 2, _bigA);
            return _dot;
        }

        // ==================================================================
        // 几何 / 工具
        // ==================================================================
        private static Point PointOn(Point c, double r, double deg)
        {
            double rad = deg * Math.PI / 180.0;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }

        /// <summary>逆时针切线方向（屏幕坐标，y 向下）。</summary>
        private static Vector CcwTangent(double deg)
        {
            double rad = deg * Math.PI / 180.0;
            return new Vector(Math.Sin(rad), -Math.Cos(rad));
        }

        /// <summary>顺时针切线方向（屏幕坐标，y 向下）。</summary>
        private static Vector CwTangent(double deg)
        {
            double rad = deg * Math.PI / 180.0;
            return new Vector(-Math.Sin(rad), Math.Cos(rad));
        }

        private static double AngleOf(Point from, Point to)
        {
            return Math.Atan2(to.Y - from.Y, to.X - from.X) * 180.0 / Math.PI;
        }

        private static double Dist(Point a, Point b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double Norm360(double deg)
        {
            deg = deg % 360.0;
            if (deg < 0.0) deg += 360.0;
            return deg;
        }

        private static Point Bez(Point p0, Point c1, Point c2, Point p3, double t)
        {
            double u = 1.0 - t;
            double a = u * u * u, b = 3.0 * u * u * t, c = 3.0 * u * t * t, d = t * t * t;
            return new Point(a * p0.X + b * c1.X + c * c2.X + d * p3.X,
                             a * p0.Y + b * c1.Y + c * c2.Y + d * p3.Y);
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        /// <summary>大环上的线速度（像素/秒），用来折算三圈的角速度。</summary>
        private double LinearSpeed()
        {
            return _speed * Math.PI / 180.0 * Math.Max(6.0, _bigR);
        }

        private double SmallStep(double dt)
        {
            double degPerSec = LinearSpeed() / Math.Max(4.0, _r1) * 180.0 / Math.PI;
            if (degPerSec > MaxSmallDegPerSec) degPerSec = MaxSmallDegPerSec;
            if (degPerSec < MinDotDegPerSec) degPerSec = MinDotDegPerSec;
            return degPerSec * dt;
        }
    }
}
