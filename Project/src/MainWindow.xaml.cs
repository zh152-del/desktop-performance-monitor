using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PerfMonitorWPF
{
    public partial class MainWindow : Window
    {
        // ==================== 面板尺寸（与 XAML 保持一致） ====================
        private const double PanelWidth = 360;
        private const double PanelMaxHeight = 540;
        /// <summary>算出来的高度离上限这么近时就直接撑满，保证满配布局正好 540</summary>
        private const double SnapToMaxGap = 16;
        /// <summary>窗口圆角：与 rootBorder.CornerRadius 保持一致</summary>
        private const double CornerRadius = 16;
        private const double BodyTopPad = 12;
        private const double ButtonsRowH = 30;
        private const double ButtonsTopPad = 8;
        private const double ButtonsBottomPad = 12;
        private const double BorderPad = 0;       // 根容器不画描边，没有边框占位

        // ==================== 定时器 ====================
        private DispatcherTimer _timer;           // 采集
        private DispatcherTimer _houseTimer;      // 每分钟：剪贴板超时清理
        private TrayIcon _tray;
        private Action _confirmAction;
        private volatile bool _polling;
        private int _tickCount;
        private double _lastCpuLoad = 50;
        private DateTime _lastInteraction = DateTime.Now;

        // ==================== 剪贴板历史 ====================
        private class ClipItem
        {
            public string Text = "";     // 完整内容（复制时写回剪贴板用）
            public string Short = "";    // 截断后的显示串
            public DateTime Time;
        }

        private readonly List<ClipItem> _clips = new List<ClipItem>();
        private bool _clipEverDetected;                 // 开机以来是否检测过（决定「未检测」还是「已检测」）
        private readonly TextBlock[] _clipTexts = new TextBlock[4];
        private readonly Button[] _clipButtons = new Button[4];
        private DispatcherTimer _clipMsgTimer;          // 临时提示看完自动回到「已检测」

        // ==================== 分区行控件缓存 ====================
        private readonly TextBlock[] _partNames = new TextBlock[4];
        private readonly TextBlock[] _partInfos = new TextBlock[4];
        private readonly ColumnDefinition[] _partFill = new ColumnDefinition[4];
        private readonly ColumnDefinition[] _partRest = new ColumnDefinition[4];
        private readonly Border[] _partBars = new Border[4];
        private readonly DiskRowFx[] _partFx = new DiskRowFx[4];

        // ==================== 毛玻璃背景 ====================
        // 静态背景：启动后 / 窗口移动后一次性截取桌面模糊化，
        // 数据刷新（5 秒定时器）绝不触发重算，没有后台循环。
        private DispatcherTimer _glassTimer;

        // ==================== 深色金属材质（DarkMetal v1.0）====================
        // 规范第 5 节：120ms 一次（约 8 次/秒，上限 10 次/秒），26 秒一个循环；
        // 只改三光源的 TranslateTransform，不触发布局、不参与数据刷新。
        private DispatcherTimer _metalTimer;
        private DateTime _metalStart;
        private const double MetalDriftPeriodSeconds = 26.0;
        private const int MetalDriftIntervalMs = 120;

        // ==================== 卡片光点动画 ====================
        // 30fps 只推进三张卡片的自绘动画：不读传感器、不触发布局、不重算几何，
        // 传感器采集仍然是原来的 5 秒周期（采集与动画彻底分离）。
        private DispatcherTimer _cardTimer;
        private readonly System.Diagnostics.Stopwatch _cardClock = new System.Diagnostics.Stopwatch();
        private const int CardFrameMs = 33;

        /// <summary>
        /// 是否用「截取桌面 + 模糊」当面板底图。
        ///
        /// 默认 false，两个原因：
        /// 1) 参考实现 ProcessPanel V3.0（深色金属规范落地版）根本不截屏，金属层直接叠在实时桌面上；
        /// 2) 截屏在本机会拍到黑帧（自身窗口未完全隐藏时截到的是自己的黑面），
        ///    那张黑图盖在整块面板上，金属层再正确也是纯黑——这是"面板离谱地黑"的根因。
        ///
        /// 面板要透，靠的是 L1 基底那 80% 的 alpha，不需要截图参与。
        /// </summary>
        /// <remarks>用 static readonly 而不是 const：const 会让下面的分支被判定为"不可达代码"，编译时报警告。</remarks>
        private static readonly bool UseCapturedBackdrop = false;

        // 阈值画刷缓存（避免每次刷新都 FindResource）
        private Brush _brAccent;
        private Brush _brWarn;
        private Brush _brDanger;
        private Brush _brSecond;

        // ==================== 物理磁盘卡控件缓存 ====================
        private readonly TextBlock[] _physTitle = new TextBlock[3];
        private readonly TextBlock[] _physWrite = new TextBlock[3];
        private readonly TextBlock[] _physRead = new TextBlock[3];
        private readonly TextBlock[] _physAct = new TextBlock[3];

        // ==================================================================
        public MainWindow()
        {
            InitializeComponent();

            Loaded += OnLoaded;
            SourceInitialized += OnSourceInitialized;

            CacheControls();
        }

        /// <summary>
        /// 把所有 XAML 里 x:Name 命名的控件收进数组，避免后面写一长串 if/switch。
        /// 之所以用数组而不是生成控件，是因为布局已经在 XAML 里按 375x540 精确摆好，
        /// 代码只负责往里填数字。
        /// </summary>
        private void CacheControls()
        {
            _partNames[0] = txtPartName0; _partNames[1] = txtPartName1;
            _partNames[2] = txtPartName2; _partNames[3] = txtPartName3;

            _partInfos[0] = txtPartInfo0; _partInfos[1] = txtPartInfo1;
            _partInfos[2] = txtPartInfo2; _partInfos[3] = txtPartInfo3;

            _partFill[0] = colPart0; _partFill[1] = colPart1;
            _partFill[2] = colPart2; _partFill[3] = colPart3;

            _partRest[0] = colPart0b; _partRest[1] = colPart1b;
            _partRest[2] = colPart2b; _partRest[3] = colPart3b;

            _partBars[0] = barPart0; _partBars[1] = barPart1;
            _partBars[2] = barPart2; _partBars[3] = barPart3;

            _partFx[0] = fxPart0; _partFx[1] = fxPart1;
            _partFx[2] = fxPart2; _partFx[3] = fxPart3;

            _brAccent = (Brush)FindResource("AccentCyan");
            _brWarn = (Brush)FindResource("WarnOrange");
            _brDanger = (Brush)FindResource("DangerRed");
            _brSecond = (Brush)FindResource("TextSecond");

            _physTitle[0] = txtPhys0; _physTitle[1] = txtPhys1; _physTitle[2] = txtPhys2;
            _physWrite[0] = valWrite0; _physWrite[1] = valWrite1; _physWrite[2] = valWrite2;
            _physRead[0] = valRead0; _physRead[1] = valRead1; _physRead[2] = valRead2;
            _physAct[0] = valAct0; _physAct[1] = valAct1; _physAct[2] = valAct2;

            _clipTexts[0] = txtClip0; _clipTexts[1] = txtClip1;
            _clipTexts[2] = txtClip2; _clipTexts[3] = txtClip3;
            _clipButtons[0] = btnCopy0; _clipButtons[1] = btnCopy1;
            _clipButtons[2] = btnCopy2; _clipButtons[3] = btnCopy3;
        }

        // ==================================================================
        // 初始化
        // ==================================================================
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 设置在 App.OnStartup 里已经加载过一次（自启线程要用），这里再读一次是无害的幂等兜底
            SafeRun(delegate { AppSettings.Load(); });

            SafeRun(BindButtons);

            // 从托盘重新显示时补采一次（隐藏期间是停采的）
            IsVisibleChanged += OnVisibleChanged;

            // 毛玻璃：窗口移动 / 尺寸变化后防抖重截一次背景（与数据刷新完全解耦）
            LocationChanged += delegate { RestartGlassTimer(); };
            SizeChanged += delegate
            {
                UpdateRoundedClip();          // 高度会随模块开关变化，裁剪必须跟着更新
                RestartGlassTimer();
            };

            SafeRun(delegate { PanelFeedbackHooks(); });
            SafeRun(ApplyModuleVisibility);
            SafeRun(delegate { PositionPanel(); });
            SafeRun(LoadStaticInfo);
            SafeRun(delegate { RenderClipboard(); });
            SafeRun(StartTimers);
            SafeRun(FitPanelHeight);
            SafeRun(UpdateRoundedClip);
            SafeRun(InitMetal);
            SafeRun(StartCardAnimation);
            SafeRun(PollAsync);
            SafeRun(delegate { _tray = new TrayIcon(this); });

            // 高度定下来之后再截玻璃背景，否则窗口变高时截到的是旧尺寸身后的画面。
            // 首次不隐藏窗口：这一刻窗口还没渲染到屏幕，不存在把自己拍进背景的问题。
            SafeRun(delegate { RefreshGlass(false); });

            App.Trace("OnLoaded 完成，耗时 " + sw.ElapsedMilliseconds + "ms");

            // 硬件驱动加载要 5~10 秒，丢后台，就绪后回填一次（温度这时才有）
            HardwareMonitor.BeginHeavyInit(delegate
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        SafeRun(LoadStaticInfo);
                        SafeRun(PollAsync);
                    }));
                }
                catch { }
            });
        }

        /// <summary>
        /// 观察模式：exe 同目录存在 observe.flag 时不钉在桌面，
        /// 改成左上角独立浮动窗口，方便和原桌面面板并排对比。
        /// </summary>
        private static bool ObserveMode
        {
            get
            {
                try
                {
                    var loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    var dir = System.IO.Path.GetDirectoryName(loc) ?? "";
                    return System.IO.File.Exists(System.IO.Path.Combine(dir, "observe.flag"));
                }
                catch { return false; }
            }
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            if (ObserveMode)
            {
                SafeRun(delegate { Left = 16; Top = 16; });
            }
            else
            {
                SafeRun(delegate { DesktopWindow.PinToDesktop(this); });
            }
        }

        /// <summary>面板吸附（设置里可改角 + 边距）</summary>
        private void PositionPanel()
        {
            var wa = SystemParameters.WorkArea;
            double w = PanelWidth;
            double h = Height > 0 ? Height : PanelMaxHeight;
            double x, y;

            switch (AppSettings.Corner)
            {
                case "左下":
                    x = wa.Left + AppSettings.EdgeX;
                    y = wa.Bottom - h - AppSettings.EdgeY;
                    break;
                case "右上":
                    x = wa.Right - w - AppSettings.EdgeX;
                    y = wa.Top + AppSettings.EdgeY;
                    break;
                case "左上":
                    x = wa.Left + AppSettings.EdgeX;
                    y = wa.Top + AppSettings.EdgeY;
                    break;
                default:                       // 右下
                    x = wa.Right - w - AppSettings.EdgeX;
                    y = wa.Bottom - h - AppSettings.EdgeY;
                    break;
            }
            Left = x;
            Top = y;
        }

        private void LoadStaticInfo()
        {
            var info = HardwareMonitor.GetStatic();
            txtCpuName.Text = info.CpuBrand;
            txtGpuName.Text = info.GpuModel;

            // 温度读不到时，界面上必须说清楚**为什么** ——
            // 否则用户只会以为「温度功能坏了」，而实际原因通常是没装 PawnIO。
            // 按可能性排序：没装 PawnIO > PawnIO 太旧 > 装了但驱动仍被拦。
            string hint = null;
            string tip = null;

            if (!HardwareMonitor.IsPawnIoInstalled)
            {
                hint = "需装 PawnIO 才能读温度";
                tip = "读处理器 / 显卡温度需要 ring0 驱动。\n" +
                      "旧版的 WinRing0 是 CVE-2020-14979 漏洞驱动，会被火绒 / Defender 拦；\n" +
                      "本版已改用 PawnIO（官方数字签名，不在漏洞驱动黑名单）。\n\n" +
                      "请安装一次 PawnIO：https://pawnio.eu/\n" +
                      "装完不用重启，重新打开面板即可。\n\n" +
                      "其它指标（占用率、内存、磁盘、剪贴板）不受影响，不装也能用。";
            }
            else if (HardwareMonitor.IsPawnIoTooOld)
            {
                hint = "PawnIO 版本过旧";
                tip = "当前装的是 PawnIO " + HardwareMonitor.PawnIoVersion +
                      "，本版要求 2.0 以上。\n" +
                      "请到 https://pawnio.eu/ 下载最新版覆盖安装。";
            }
            else if (HardwareMonitor.IsDriverBlocked)
            {
                hint = "驱动被拦截，温度不可用";
                tip = "PawnIO 已装（" + HardwareMonitor.PawnIoVersion + "），" +
                      "但硬件驱动没能加载成功，\n" +
                      "通常是安全软件 / 反作弊 / 内核隔离拦下来的。\n\n" +
                      "处理器和显卡温度会显示成 --°C，其它指标不受影响。";
            }

            if (hint != null) txtCpuName.Text = info.CpuBrand + "（" + hint + "）";
            txtCpuName.ToolTip = tip;
        }

        private void StartTimers()
        {
            RebuildPollTimer();

            // 每分钟做一次剪贴板超时清理。
            // 注意：这里【不】主动读剪贴板 —— 用户明确要求低功耗、不做激进访读，
            // 剪贴板只在点「检测」或「刷新」时才读一次。
            _houseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _houseTimer.Tick += delegate { SafeRun(ClipboardHousekeeping); };
            _houseTimer.Start();
        }

        private void RebuildPollTimer()
        {
            if (_timer != null) { _timer.Stop(); _timer = null; }

            int sec = AppSettings.RefreshSeconds;
            if (sec < 1) sec = 1;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(sec) };
            _timer.Tick += delegate { SafeRun(OnPollTick); };
            _timer.Start();
            App.Trace("刷新定时器已设为 " + sec + " 秒");
        }

        private void OnPollTick()
        {
            if (AppSettings.Paused) return;

            // 隐藏到托盘时完全停采：看不见的界面没必要每几秒去问一遍硬件和性能计数器。
            // 重新显示时由 OnVisibleChanged 立刻补一次，不会有空窗期。
            if (!IsVisible) return;

            _tickCount++;

            // 省电降频：5 分钟没人碰面板、且处理器不忙 —— 隔一次采一次
            if (AppSettings.LowPower)
            {
                bool idle = (DateTime.Now - _lastInteraction).TotalMinutes >= 5;
                if (idle && _lastCpuLoad < 20 && (_tickCount % 2) != 0) return;
            }

            PollAsync();
        }

        /// <summary>从托盘重新显示时立刻补采一次，避免看到旧数据</summary>
        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // 隐藏到托盘时材质漂移必须停（规范第 5 节：非显示状态立即停止计时器）
            if (!(bool)e.NewValue)
            {
                SafeRun(StopMetalDrift);
                SafeRun(StopCardAnimation);
                return;
            }

            _lastInteraction = DateTime.Now;
            _tickCount = 0;
            SafeRun(PollAsync);
            SafeRun(StartMetalDrift);
            SafeRun(StartCardAnimation);
        }

        // ==================================================================
        // 采集（后台线程）→ 渲染（UI 线程）
        // ==================================================================
        private void PollAsync()
        {
            if (_polling) return;
            _polling = true;

            var t = new System.Threading.Thread(delegate ()
            {
                HardwareMonitor.Snapshot snap = null;
                List<DiskPerf.PhysDisk> phys = null;

                try { snap = HardwareMonitor.GetSnapshot(); }
                catch (Exception ex) { App.Log("采集快照", ex); }

                try { phys = DiskPerf.ReadAll(); }
                catch (Exception ex) { App.Log("采集物理磁盘", ex); }

                var rSnap = snap;
                var rPhys = phys;

                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        try { ApplySnapshot(rSnap, rPhys); }
                        catch (Exception ex) { App.Log("渲染快照", ex); }
                        finally { _polling = false; }
                    }));
                }
                catch { _polling = false; }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void ApplySnapshot(HardwareMonitor.Snapshot s, List<DiskPerf.PhysDisk> phys)
        {
            if (s != null)
            {
                ApplyRings(s);
                ApplyPartitions(s.Disks);
            }
            if (phys != null) ApplyPhysDisks(phys);
        }

        // ---------------- 三张硬件卡片 ----------------
        private void ApplyRings(HardwareMonitor.Snapshot s)
        {
            // 处理器 / 显卡：大环 = 真实利用率，三圈 = 显存已用容量
            cardCpu.SetLoad(s.CpuLoad);
            cardCpu.SetTemp(DiskPerf.FormatTemp(s.CpuTemp, AppSettings.Fahrenheit, "--°C"),
                            TempBrush(s.CpuTemp));

            cardGpu.SetLoad(s.GpuLoad);
            cardGpu.SetTemp(DiskPerf.FormatTemp(s.GpuTemp, AppSettings.Fahrenheit, "--°C"),
                            TempBrush(s.GpuTemp));
            cardGpu.SetCapacity(s.GpuMemUsed, s.GpuMemTotal);

            // 内存卡：方框显示已用内存（单位由容量决定），圆环是呼吸光
            // SetLoad 一起喂进去，卡片自己不显示利用率就不会用，接口保持三张卡一致
            cardMem.SetLoad(s.MemPercent);
            // 内存温度：现有硬件库取不到就整块隐藏，绝不拿 0 或别的硬件温度顶替
            if (s.MemTemp > 0)
                cardMem.SetTemp(DiskPerf.FormatTemp(s.MemTemp, AppSettings.Fahrenheit, ""),
                                TempBrush(s.MemTemp));
            else
                cardMem.SetTemp(null, null);
            cardMem.SetCapacity(s.MemUsed, s.MemTotal);

            _lastCpuLoad = s.CpuLoad;
        }

        // ---------------- 四个分区 ----------------
        private void ApplyPartitions(List<HardwareMonitor.DiskInfo> disks)
        {
            string[] want = { "C:", "D:", "E:", "F:" };

            for (int i = 0; i < 4; i++)
            {
                HardwareMonitor.DiskInfo d = FindDisk(disks, want[i]);
                if (d == null)
                {
                    _partNames[i].Text = want[i];
                    _partInfos[i].Text = "未挂载";
                    SetPartFill(i, 0);
                    SetPartRun(i, false);
                    continue;
                }

                _partNames[i].Text = d.Mount;
                _partInfos[i].Text =
                    Math.Round(d.Percent, 0).ToString("0", CultureInfo.InvariantCulture) + "%  " +
                    DiskPerf.FormatTemp(d.Temp, AppSettings.Fahrenheit, "--");
                SetPartFill(i, d.Percent);

                // 这一块盘此刻有没有读写（读或写都算「运行」）——按卷统计，不是按物理盘
                SetPartRun(i, VolumeIo.IsBusy(want[i]));
            }
        }

        /// <summary>
        /// 刷新单张分区卡的运行指示：在跑就出现光点 + 呼吸灯，停了就淡出。
        /// 呼吸灯颜色直接跟随进度条的状态色（青 / 橙 / 红），与卡片本身的配色一致。
        /// </summary>
        private void SetPartRun(int i, bool busy)
        {
            DiskRowFx fx = _partFx[i];
            if (fx == null) return;
            if (_partBars[i] != null) fx.Accent = _partBars[i].Background;
            fx.Busy = busy;
        }

        private static HardwareMonitor.DiskInfo FindDisk(List<HardwareMonitor.DiskInfo> disks, string mount)
        {
            if (disks == null) return null;
            for (int i = 0; i < disks.Count; i++)
            {
                if (string.Equals(disks[i].Mount, mount, StringComparison.OrdinalIgnoreCase))
                    return disks[i];
            }
            return null;
        }

        private void SetPartFill(int i, double percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            _partFill[i].Width = new GridLength(percent, GridUnitType.Star);
            _partRest[i].Width = new GridLength(100 - percent, GridUnitType.Star);

            // 正常态统一青蓝，达到阈值才切换橙 / 红
            if (_partBars[i] != null)
                _partBars[i].Background = LoadBrush(percent);
        }

        /// <summary>占用率阈值配色：≥90 红 / ≥75 橙 / 其余用传入的基色（磁盘条用青）</summary>
        private Brush LoadBrush(double percent, Brush baseBrush)
        {
            if (percent >= 90) return _brDanger;
            if (percent >= 75) return _brWarn;
            return baseBrush ?? _brAccent;
        }

        private Brush LoadBrush(double percent)
        {
            return LoadBrush(percent, null);
        }

        /// <summary>温度配色：≥85°C 红 / ≥70°C 橙 / 其余次级色。温度无效时文字由 FormatTemp 给 "--°C"。</summary>
        private Brush TempBrush(double temp)
        {
            if (temp >= 85) return _brDanger;
            if (temp >= 70) return _brWarn;
            return _brSecond;
        }

        // ==================================================================
        // 毛玻璃背景（静态：只在启动和窗口移动后截一次，数据刷新不触发）
        // ==================================================================

        /// <summary>
        /// 根容器的圆角裁剪。
        ///
        /// 为什么必须有：WPF 的 Border 只会把自己的背景和描边画成圆角，
        /// 【不会】把子元素裁到圆角里。上一版在圆角 Border 里放了铺满全窗的
        /// 玻璃图和 tint，方角把圆角区域整个盖住，窗口就变成直角矩形了。
        ///
        /// 这里用 RectangleGeometry（带圆角半径）当 Clip：
        ///   - 圆角外真正透明，桌面直接透出来（AllowsTransparency=True 的分层窗口）
        ///   - 玻璃图 / tint / 内容 / 浮层全部被裁进圆角，四角不会再露出方角
        ///   - 圆角外的区域不参与命中测试，点角落不会误触面板
        /// 高度会随设置里的模块开关变化，所以每次 SizeChanged 都要重算。
        /// </summary>
        private void UpdateRoundedClip()
        {
            try
            {
                double w = rootBorder.ActualWidth;
                double h = rootBorder.ActualHeight;
                if (w < 8 || h < 8) return;

                var clip = new RectangleGeometry(new Rect(0, 0, w, h), CornerRadius, CornerRadius);
                clip.Freeze();
                rootBorder.Clip = clip;
            }
            catch { }
        }

        private void RestartGlassTimer()
        {
            try
            {
                if (_glassTimer == null)
                {
                    _glassTimer = new DispatcherTimer();
                    _glassTimer.Interval = TimeSpan.FromMilliseconds(400);
                    _glassTimer.Tick += delegate
                    {
                        _glassTimer.Stop();
                        SafeRun(RefreshGlass);
                    };
                }
                _glassTimer.Stop();
                _glassTimer.Start();
            }
            catch { }
        }

        /// <summary>截取窗口身后的桌面并模糊，作为玻璃底图。失败则隐藏玻璃层，退回不透明深色。</summary>
        private void RefreshGlass()
        {
            RefreshGlass(true);
        }

        /// <param name="hideWindow">
        /// 截屏前是否临时隐藏窗口（防止把面板自己拍进背景造成重影）。
        /// 开机首次截屏时窗口还没渲染，不存在自拍，传 false，免得加载期隐藏窗口。
        /// </param>
        private void RefreshGlass(bool hideWindow)
        {
            // 默认不截屏：黑帧会盖死整块面板（详见 UseCapturedBackdrop 注释）
            if (!UseCapturedBackdrop)
            {
                imgGlass.Visibility = Visibility.Collapsed;
                if (_glassTimer != null) _glassTimer.Stop();
                return;
            }

            try
            {
                var frame = GlassBackdrop.Capture(this, hideWindow);
                if (frame != null)
                {
                    imgGlass.Source = frame;
                    imgGlass.Visibility = Visibility.Visible;
                }
                else
                {
                    imgGlass.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                imgGlass.Visibility = Visibility.Collapsed;
            }
        }

        // ==================================================================
        // 深色金属材质（DarkMetal v1.0）
        // ==================================================================

        /// <summary>
        /// 规范 7.2「必须做的一步：圆角裁剪」。
        /// 反射光斑是椭圆/矩形子元素，WPF 的 Border.CornerRadius 只圆角自己，
        /// 不会裁剪子元素；不显式设 Clip，四角就会漏出方形亮斑。
        /// 半径 15 = 外层 16 圆角内缩 1px，与外缘倒角同心。
        /// </summary>
        private void MetalClip_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                var host = sender as Grid;
                if (host == null) return;

                double w = host.ActualWidth;
                double h = host.ActualHeight;
                if (w <= 0 || h <= 0) return;

                var clip = new RectangleGeometry(new Rect(0, 0, w, h), 15, 15);
                clip.Freeze();
                host.Clip = clip;
            }
            catch { }
        }

        /// <summary>接上材质：L4 噪声填充 + 初始裁剪 + 启动低频漂移</summary>
        private void InitMetal()
        {
            try
            {
                if (NoiseLayer != null) NoiseLayer.Fill = MetalMaterial.NoiseBrush;
                MetalClip_SizeChanged(MetalClip, null);      // 布局完成后补一次裁剪
                StartMetalDrift();
            }
            catch (Exception ex) { App.Log("金属材质", ex); }
        }

        /// <summary>
        /// 规范第 5 节的动态反射：材质要「活着」，但不能耗性能。
        /// 120ms 一次（约 8 次/秒）；26 秒一个完整循环；
        /// 位移 7px / 5px（规范区间 5~9px）；
        /// 三光源倍率 暖 ×1.0、冷 ×−1.4、灯条 ×1.8 —— 反向漂移制造视差；
        /// Y 轴取 0.7 倍频率，避免 X/Y 同步看起来像整体平移。
        /// 只改 TranslateTransform，不参与布局测量，不会引起重排。
        /// </summary>
        private void TickMetalDrift()
        {
            if (!IsVisible) return;              // 隐藏到托盘后不再空转

            double t = (DateTime.Now - _metalStart).TotalSeconds;
            double w = 2.0 * Math.PI / MetalDriftPeriodSeconds;

            double dx = Math.Sin(t * w) * 7.0;
            double dy = Math.Cos(t * w * 0.7) * 5.0;

            if (WarmOffset != null) { WarmOffset.X = dx; WarmOffset.Y = dy; }
            if (CoolOffset != null) { CoolOffset.X = -dx * 1.4; CoolOffset.Y = -dy * 1.2; }
            if (StripOffset != null) { StripOffset.X = dx * 1.8; StripOffset.Y = dy * 0.4; }
        }

        private void StartMetalDrift()
        {
            try
            {
                if (_metalTimer == null)
                {
                    _metalTimer = new DispatcherTimer();
                    _metalTimer.Interval = TimeSpan.FromMilliseconds(MetalDriftIntervalMs);
                    _metalTimer.Tick += delegate { SafeRun(TickMetalDrift); };
                }
                _metalStart = DateTime.Now;
                _metalTimer.Start();
            }
            catch { }
        }

        /// <summary>规范要求：窗口非显示状态 / 关闭时立即停止计时器</summary>
        private void StopMetalDrift()
        {
            try { if (_metalTimer != null) _metalTimer.Stop(); } catch { }
        }

        // ==================================================================
        // 三张卡片的光点 / 数字动画驱动
        // 只推进动画，不读传感器；隐藏到托盘时整体停掉，恢复时重新计时。
        // ==================================================================
        private void StartCardAnimation()
        {
            try
            {
                if (_cardTimer == null)
                {
                    _cardTimer = new DispatcherTimer();
                    _cardTimer.Interval = TimeSpan.FromMilliseconds(CardFrameMs);
                    _cardTimer.Tick += delegate { SafeRun(TickCards); };
                }
                _cardClock.Restart();          // 重新计时，避免隐藏期间攒出一个巨大的 dt
                _cardTimer.Start();
            }
            catch { }
        }

        private void StopCardAnimation()
        {
            try { if (_cardTimer != null) _cardTimer.Stop(); } catch { }
            try { _cardClock.Stop(); } catch { }
        }

        private void TickCards()
        {
            if (!IsVisible) return;
            double dt = _cardClock.Elapsed.TotalSeconds;
            _cardClock.Restart();
            if (dt <= 0) return;

            if (cardCpu != null) cardCpu.Tick(dt);
            if (cardGpu != null) cardGpu.Tick(dt);
            if (cardMem != null) cardMem.Tick(dt);

            // 磁盘分区卡片的运行指示（光点 / 呼吸灯）复用同一个 30fps 时钟，不另开定时器
            for (int i = 0; i < 4; i++)
                if (_partFx[i] != null) _partFx[i].Tick(dt);
        }

        // ---------------- 三块物理磁盘 ----------------
        private void ApplyPhysDisks(List<DiskPerf.PhysDisk> list)
        {
            bool binary = AppSettings.BinaryUnits;

            for (int i = 0; i < 3; i++)
            {
                DiskPerf.PhysDisk p = FindPhys(list, i);
                if (p == null)
                {
                    _physTitle[i].Text = "磁盘" + i.ToString(CultureInfo.InvariantCulture);
                    _physWrite[i].Text = "--";
                    _physRead[i].Text = "--";
                    _physAct[i].Text = "--";
                    continue;
                }

                _physTitle[i].Text = p.Title;      // 磁盘0【C E】
                _physWrite[i].Text = DiskPerf.FormatSpeed(p.WriteBps, binary);
                _physRead[i].Text = DiskPerf.FormatSpeed(p.ReadBps, binary);
                _physAct[i].Text =
                    Math.Round(p.Activity, 0).ToString("0", CultureInfo.InvariantCulture) + "%";
            }
        }

        private static DiskPerf.PhysDisk FindPhys(List<DiskPerf.PhysDisk> list, int index)
        {
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Index == index) return list[i];
            }
            return null;
        }

        // ==================================================================
        // 剪贴板：4 条历史，最新在顶区，下面 3 条为历史
        // ==================================================================
        /// <summary>点「检测」/「刷新」时读一次剪贴板</summary>
        private void DetectClipboard()
        {
            _clipEverDetected = true;
            _lastInteraction = DateTime.Now;

            string raw = SystemActions.ReadClipboard();

            if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0)
            {
                SetClipState("剪贴板为空");
                RenderClipboard();
                return;
            }

            // 与已有记录重复 -> 不操作。
            // 比较前统一换行符：同一段文字从不同程序复制出来，\r\n 和 \n 会不一样，
            // 但对用户来说就是同一份内容，不该重复占两条。
            string norm = NormalizeNewlines(raw);
            for (int i = 0; i < _clips.Count; i++)
            {
                if (string.Equals(NormalizeNewlines(_clips[i].Text), norm, StringComparison.Ordinal))
                {
                    SetClipState("内容已存在");
                    RenderClipboard();
                    return;
                }
            }

            var it = new ClipItem();
            it.Text = raw;
            it.Short = Shorten(raw);
            it.Time = DateTime.Now;

            // 最新插到第一条，其余整体下移一位
            _clips.Insert(0, it);

            // 超过上限，删掉末尾（也就是第 5 条）
            while (_clips.Count > AppSettings.ClipMax)
                _clips.RemoveAt(_clips.Count - 1);

            SetClipState(null);          // 回到基础状态「已检测」
            RenderClipboard();
        }

        /// <summary>把某条记录写回系统剪贴板</summary>
        private void CopyClip(int index)
        {
            if (index < 0 || index >= _clips.Count) return;
            _lastInteraction = DateTime.Now;

            if (SystemActions.WriteClipboard(_clips[index].Text))
            {
                _clips[index].Time = DateTime.Now;      // 重新计时
                SetClipState("已复制第 " + (index + 1).ToString(CultureInfo.InvariantCulture) + " 条");
            }
            else
            {
                SetClipState("复制失败");
            }
            RenderClipboard();
        }

        /// <summary>
        /// 状态行文字。基础状态只有两种：开机后没检测过 =「未检测」，检测过 =「已检测」。
        /// 临时提示（已复制 / 剪贴板为空 …）会短暂覆盖它，2.5 秒后自动回到基础状态。
        /// </summary>
        private void SetClipState(string transient)
        {
            if (!string.IsNullOrEmpty(transient))
            {
                txtClipState.Text = transient;
                if (_clipMsgTimer == null)
                {
                    _clipMsgTimer = new DispatcherTimer();
                    _clipMsgTimer.Interval = TimeSpan.FromSeconds(2.5);
                    _clipMsgTimer.Tick += ClipMsgTick;
                }
                _clipMsgTimer.Stop();      // 重新计时：新提示顶掉旧提示
                _clipMsgTimer.Start();
                return;
            }

            if (_clipMsgTimer != null) _clipMsgTimer.Stop();
            txtClipState.Text = _clipEverDetected ? "已检测" : "未检测";
        }

        private void ClipMsgTick(object sender, EventArgs e)
        {
            if (_clipMsgTimer != null) _clipMsgTimer.Stop();
            txtClipState.Text = _clipEverDetected ? "已检测" : "未检测";
        }

        /// <summary>每分钟跑一次：删掉超时的记录</summary>
        private void ClipboardHousekeeping()
        {
            int before = _clips.Count;
            ExpireClips();

            // 可选的「连系统剪贴板一起清掉」，默认关闭（设置里能开）
            if (AppSettings.ClipWipeSystem && before > 0 && _clips.Count == 0)
                SystemActions.ClearClipboard();

            if (before != _clips.Count) RenderClipboard();
        }

        private void ExpireClips()
        {
            if (_clips.Count == 0) return;
            DateTime limit = DateTime.Now.AddMinutes(-Math.Abs(AppSettings.ClipExpireMinutes));

            // 从末尾往前删：越靠后的记录越老，也就越先超时
            for (int i = _clips.Count - 1; i >= 0; i--)
            {
                if (_clips[i].Time < limit) _clips.RemoveAt(i);
            }
        }

        private void RenderClipboard()
        {
            ExpireClips();

            // 4 条记录一视同仁：都占固定一行、都有一个「复制」按钮。
            // 行本身始终占位（面板尺寸固定，不做重排），没内容就显示占位符、按钮置灰。
            for (int i = 0; i < 4; i++)
            {
                if (i < _clips.Count)
                {
                    _clipTexts[i].Text = _clips[i].Short;
                    // 行内是截断显示的，悬停给全文，不静默丢信息
                    _clipTexts[i].ToolTip = _clips[i].Text;
                    _clipButtons[i].IsEnabled = true;
                }
                else
                {
                    _clipTexts[i].Text = "—";
                    _clipTexts[i].ToolTip = null;
                    _clipButtons[i].IsEnabled = false;
                }
            }

            // 记录少了就删掉超时的（上面 ExpireClips 已做），这里只负责把状态文字拉回基础态
            if (_clipMsgTimer == null || !_clipMsgTimer.IsEnabled)
                txtClipState.Text = _clipEverDetected ? "已检测" : "未检测";
        }

        /// <summary>统一换行符，用于「内容是否重复」的判断</summary>
        private static string NormalizeNewlines(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        /// <summary>纯文本化 + 截断：中日韩 20 字，纯字母数字 25 字</summary>
        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            // 先把换行 / 制表 / 连续空格压成单空格 —— 面板是单行显示，不能有换行
            var sb = new StringBuilder(s.Length);
            bool lastSpace = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\r' || c == '\n' || c == '\t' || c == ' ')
                {
                    if (!lastSpace) { sb.Append(' '); lastSpace = true; }
                }
                else
                {
                    sb.Append(c);
                    lastSpace = false;
                }
            }
            string flat = sb.ToString().Trim();

            bool hasCjk = false;
            for (int i = 0; i < flat.Length; i++)
            {
                if (IsCjk(flat[i])) { hasCjk = true; break; }
            }

            int limit = hasCjk ? AppSettings.ClipTruncCjk : AppSettings.ClipTruncLatin;
            if (limit < 1) limit = 1;

            if (flat.Length <= limit) return flat;
            return flat.Substring(0, limit) + "…";
        }

        private static bool IsCjk(char c)
        {
            return (c >= 0x3000 && c <= 0x303F)      // 中日韩标点
                || (c >= 0x4E00 && c <= 0x9FFF)      // 中日韩统一表意文字
                || (c >= 0xF900 && c <= 0xFAFF)      // 兼容表意文字
                || (c >= 0xFF00 && c <= 0xFFEF);     // 全角字符
        }

        // ==================================================================
        // 按钮
        // ==================================================================
        private void BindButtons()
        {
            btnThisPC.Click += delegate { SafeRun(delegate { if (!SystemActions.OpenThisPC()) {} }); };

            btnRefresh.Click += delegate { SafeRun(RefreshAll); };

            btnSettings.Click += delegate { SafeRun(OpenSettings); };
            btnSettingsClose.Click += delegate { SafeRun(CloseSettings); };

            btnClipDetect.Click += delegate { SafeRun(DetectClipboard); };
            for (int i = 0; i < 4; i++)
            {
                int idx = i;                      // 闭包捕获：每条记录一个按钮
                _clipButtons[i].Click += delegate { SafeRun(delegate { CopyClip(idx); }); };
            }

            btnPower.Click += delegate { SafeRun(delegate { ovPowerMask.Visibility = Visibility.Visible; }); };
            btnPowerCancel.Click += delegate { ovPowerMask.Visibility = Visibility.Collapsed; };

            ovPowerMask.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                // 点遮罩空白处关闭菜单
                if (Equals(e.OriginalSource, ovPowerMask))
                    ovPowerMask.Visibility = Visibility.Collapsed;
            };

            btnPowerShutdown.Click += delegate
            {
                SafeRun(delegate
                {
                    ovPowerMask.Visibility = Visibility.Collapsed;
                    ShowConfirm("确认关机", "系统将立即关机，请先保存正在编辑的内容。",
                        delegate { SystemActions.Shutdown(); });
                });
            };

            btnPowerRestart.Click += delegate
            {
                SafeRun(delegate
                {
                    ovPowerMask.Visibility = Visibility.Collapsed;
                    ShowConfirm("确认重启", "系统将立即重启，请先保存正在编辑的内容。",
                        delegate { SystemActions.Restart(); });
                });
            };

            btnConfirmCancel.Click += delegate
            {
                ovConfirmMask.Visibility = Visibility.Collapsed;
                _confirmAction = null;
            };

            btnConfirmOk.Click += delegate
            {
                SafeRun(delegate
                {
                    ovConfirmMask.Visibility = Visibility.Collapsed;
                    var act = _confirmAction;
                    _confirmAction = null;
                    if (act != null) act();
                });
            };

            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            _lastInteraction = DateTime.Now;
            if (e.Key != Key.Escape) return;

            if (ovConfirmMask.Visibility == Visibility.Visible)
            {
                ovConfirmMask.Visibility = Visibility.Collapsed;
                _confirmAction = null;
                e.Handled = true;
            }
            else if (ovPowerMask.Visibility == Visibility.Visible)
            {
                ovPowerMask.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            else if (ovSettings.Visibility == Visibility.Visible)
            {
                CloseSettings();
                e.Handled = true;
            }
        }

        /// <summary>刷新按钮：把面板上所有数据重新采一遍（含剪贴板）</summary>
        private void RefreshAll()
        {
            _lastInteraction = DateTime.Now;
            _tickCount = 0;
            PollAsync();
            DetectClipboard();
            txtSettingsFoot.Text = "已刷新 · " + DateTime.Now.ToString("HH:mm:ss");
        }

        private void ShowConfirm(string title, string text, Action onOk)
        {
            if (!AppSettings.ConfirmDanger)
            {
                if (onOk != null) onOk();
                return;
            }
            txtConfirmTitle.Text = title;
            txtConfirmText.Text = text;
            _confirmAction = onOk;
            ovConfirmMask.Visibility = Visibility.Visible;
        }

        /// <summary>面板上的鼠标 / 键盘活动，用于省电降频判断</summary>
        private void PanelFeedbackHooks()
        {
            PreviewMouseMove += delegate { _lastInteraction = DateTime.Now; };
            PreviewMouseDown += delegate { _lastInteraction = DateTime.Now; };
            MouseWheel += delegate { _lastInteraction = DateTime.Now; };
        }

        // ==================================================================
        // 模块显示开关
        // ==================================================================
        private void ApplyModuleVisibility()
        {
            pnlRings.Visibility = AppSettings.ShowRings ? Visibility.Visible : Visibility.Collapsed;
            pnlPartitions.Visibility = AppSettings.ShowPartitions ? Visibility.Visible : Visibility.Collapsed;
            pnlClipboard.Visibility = AppSettings.ShowClipboard ? Visibility.Visible : Visibility.Collapsed;
            pnlDiskCard.Visibility = AppSettings.ShowDiskCard ? Visibility.Visible : Visibility.Collapsed;
            pnlButtons.Visibility = AppSettings.ShowButtons ? Visibility.Visible : Visibility.Collapsed;

            // 分隔线跟着相邻模块一起走，避免出现孤零零一条线
            pnlHeader.Visibility = Visibility.Visible;

            SafeRun(FitPanelHeight);
        }

        /// <summary>面板高度按实际内容收紧（模块关掉之后不留一大块空底）</summary>
        private void FitPanelHeight()
        {
            try
            {
                UpdateLayout();

                // 用 DesiredSize 而不是 ActualHeight：主体所在的网格行是 *，
                // ActualHeight 会被拉伸到整行高度，量出来永远等于窗口高度。
                double content = pnlBody.DesiredSize.Height;
                if (content <= 0) return;

                // 底部按钮整块被隐藏时不能再把它的高度算进去，否则底部会空出一大块
                double btnBlock = pnlButtons.Visibility == Visibility.Visible
                                ? (ButtonsTopPad + ButtonsRowH + ButtonsBottomPad)
                                : 0;

                double h = BodyTopPad + content + btnBlock + BorderPad + 4;
                if (h < 200) return;                    // 还没布局好，别乱改

                // 高度必须取整。分层窗口（AllowsTransparency=True）的位图尺寸是整数像素，
                // 窗口高度带小数时两者差出零点几像素，边缘被重采样就会出现一条
                // 浅色细边 —— 这是「白边」最可疑的技术来源之一。
                h = Math.Round(h);

                if (h > PanelMaxHeight)
                {
                    // 内容比面板还高 = 又超预算了，记一笔方便排查（底部会被裁）
                    if (Math.Abs(Height - PanelMaxHeight) >= 0.5)
                        App.Trace("警告：面板内容 " + h + "px 超过上限 " + PanelMaxHeight + "px");
                    h = PanelMaxHeight;
                }
                else if (h > PanelMaxHeight - SnapToMaxGap)
                {
                    h = PanelMaxHeight;                 // 差一点点就撑满，别在底部留一条缝
                }

                if (Math.Abs(Height - h) < 0.5) return;

                Height = h;
                if (!ObserveMode) PositionPanel();
            }
            catch { }
        }

        // ==================================================================
        // 设置面板
        // ==================================================================
        private void OpenSettings()
        {
            _lastInteraction = DateTime.Now;
            BuildSettingsRows();
            ovSettings.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 托盘菜单的「打开设置」入口。
        /// 存在的意义：底部按钮可以整个隐藏，藏了之后面板上就没有回到设置的按钮了，
        /// 只能靠托盘兜底，避免把自己关在门外。
        /// </summary>
        internal void OpenSettingsFromTray()
        {
            SafeRun(delegate
            {
                if (!IsVisible) Show();
                OpenSettings();
            });
        }

        private void CloseSettings()
        {
            ovSettings.Visibility = Visibility.Collapsed;
            ApplyModuleVisibility();
            RebuildPollTimer();
            AppSettings.Save();
        }

        private void BuildSettingsRows()
        {
            // 重建前记住滚动位置。每改一个开关都重建整个列表，
            // 不记住的话滚到下面点一下就被弹回顶部，很难用。
            double offset = 0;
            try { offset = scvSettings.VerticalOffset; } catch { }

            pnlSettingsRows.Children.Clear();

            // ---------- 1 刷新与省电 ----------
            AddGroupTitle("刷新与省电");
            AddSegRowInt("刷新间隔", new int[] { 1, 2, 3, 5, 10 }, "s",
                AppSettings.RefreshSeconds, delegate (int v) { AppSettings.RefreshSeconds = v; });
            AddSwitchRow("省电降频", "5 分钟没人碰面板且处理器不忙时，自动隔次采集",
                AppSettings.LowPower, delegate (bool v) { AppSettings.LowPower = v; });
            AddSwitchRow("暂停采样", "临时停掉采集，只保留界面",
                AppSettings.Paused, delegate (bool v) { AppSettings.Paused = v; });

            // ---------- 2 启动与位置 ----------
            AddGroupTitle("启动与位置");
            AddSwitchRow("开机自启", "写入当前用户 Run 项，不动系统服务",
                AppSettings.AutoStart, delegate (bool v) { AppSettings.AutoStart = v; ApplyAutoStart(v); });
            AddSwitchRow("取消系统启动延迟", "干掉 Win10/11 对自启项强加的约 10 秒等待",
                AppSettings.FastAutoStart, delegate (bool v) { AppSettings.FastAutoStart = v; ApplyFastStart(v); });
            AddSegRowText("面板位置", new string[] { "右下", "左下", "右上", "左上" },
                AppSettings.Corner, delegate (string v) { AppSettings.Corner = v; PositionPanel(); });
            AddSegRowInt("屏幕边距", new int[] { 8, 16, 24, 32 }, "px",
                AppSettings.EdgeX, delegate (int v) { AppSettings.EdgeX = v; AppSettings.EdgeY = v; PositionPanel(); });

            // ---------- 3 显示模块 ----------
            AddGroupTitle("显示模块");
            AddSwitchRow("三圆环", "处理器 / 显卡 / 内存",
                AppSettings.ShowRings, delegate (bool v) { AppSettings.ShowRings = v; });
            AddSwitchRow("分区进度条", "本机固定 C / D / E / F 四行",
                AppSettings.ShowPartitions, delegate (bool v) { AppSettings.ShowPartitions = v; });
            AddSwitchRow("物理磁盘卡", "本机固定 3 块盘的读 / 写 / 活动时间",
                AppSettings.ShowDiskCard, delegate (bool v) { AppSettings.ShowDiskCard = v; });
            AddSwitchRow("剪贴板历史", "最新一条在顶区，下面 3 条历史",
                AppSettings.ShowClipboard, delegate (bool v) { AppSettings.ShowClipboard = v; });
            AddSwitchRow("底部按钮", "此电脑 / 刷新 / 设置 / 电源",
                AppSettings.ShowButtons, delegate (bool v) { AppSettings.ShowButtons = v; });

            // ---------- 4 剪贴板规则 ----------
            AddGroupTitle("剪贴板规则");
            // 【上限只能到 4】面板上就 4 行记录。以前允许选到 6，
            // 多出来的 2 条看不见却还在参与去重判断，属于隐性 bug。
            AddSegRowInt("记录条数上限", new int[] { 2, 3, 4 }, "条",
                AppSettings.ClipMax, delegate (int v)
                {
                    AppSettings.ClipMax = v;
                    // 调小之后立刻把多出来的记录裁掉，别让它们留在内存里
                    while (_clips.Count > AppSettings.ClipMax)
                        _clips.RemoveAt(_clips.Count - 1);
                    SafeRun(RenderClipboard);
                });
            AddSegRowInt("超时自动删除", new int[] { 5, 15, 30, 60 }, "分",
                AppSettings.ClipExpireMinutes, delegate (int v) { AppSettings.ClipExpireMinutes = v; });
            AddSegRowInt("中文截断长度", new int[] { 10, 20, 30, 50 }, "字",
                AppSettings.ClipTruncCjk, delegate (int v) { AppSettings.ClipTruncCjk = v; SafeRun(RenderClipboard); });
            AddSegRowInt("字母截断长度", new int[] { 10, 25, 40, 60 }, "字",
                AppSettings.ClipTruncLatin, delegate (int v) { AppSettings.ClipTruncLatin = v; SafeRun(RenderClipboard); });
            AddSwitchRow("同时清空系统剪贴板", "记录全部超时后，连 Windows 剪贴板一起清掉（默认关闭，避免误清）",
                AppSettings.ClipWipeSystem, delegate (bool v) { AppSettings.ClipWipeSystem = v; });

            // ---------- 5 单位与操作 ----------
            AddGroupTitle("单位与操作");
            AddSegRowText("容量单位进制", new string[] { "1024", "1000" },
                AppSettings.BinaryUnits ? "1024" : "1000",
                delegate (string v) { AppSettings.BinaryUnits = (v == "1024"); SafeRun(RefreshAll); });
            AddSegRowText("温度单位", new string[] { "°C", "°F" },
                AppSettings.Fahrenheit ? "°F" : "°C",
                delegate (string v) { AppSettings.Fahrenheit = (v == "°F"); SafeRun(RefreshAll); });
            AddSwitchRow("危险操作二次确认", "关机 / 重启前弹确认框",
                AppSettings.ConfirmDanger, delegate (bool v) { AppSettings.ConfirmDanger = v; });

            AddDivider();
            AddActionRow("恢复全部默认设置", delegate
            {
                AppSettings.ResetToDefault();
                ApplyAutoStart(true);
                SafeRun(delegate { ApplyModuleVisibility(); PositionPanel(); RebuildPollTimer(); RefreshAll(); });
                BuildSettingsRows();
            }, false);

            AddActionRow("打开程序所在目录", delegate { OpenPath(ExeDir()); }, false);
            AddActionRow("打开运行日志", delegate
            {
                string p = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PerfMonitorWPF", "error.log");
                OpenPath(p);
            }, false);
            AddActionRow("打开设置文件", delegate { OpenPath(AppSettings.FilePath); }, false);
            AddActionRow("立即刷新全部数据", delegate { RefreshAll(); }, false);
            AddActionRow("退出面板", delegate
            {
                // 不能直接 Close()：托盘的 Closing 处理会把「关闭」变成「隐藏到托盘」，
                // 必须走 TrayIcon.ExitApp() 才是真的退出。
                if (_tray != null) _tray.ExitApp();
                else System.Windows.Application.Current.Shutdown();
            }, true);

            txtSettingsFoot.Text = "改动即时生效并自动保存 · Esc 关闭 · 配置文件 "
                + AppSettings.FilePath;

            // 还原滚动位置（列表刚重建完，需要先跑一次布局才知道滚动范围）
            if (offset > 0)
            {
                try
                {
                    scvSettings.UpdateLayout();
                    scvSettings.ScrollToVerticalOffset(offset);
                }
                catch { }
            }
        }

        private static string ExeDir()
        {
            try
            {
                var loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
                return System.IO.Path.GetDirectoryName(loc) ?? "";
            }
            catch { return ""; }
        }

        private static void OpenPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = path;
                psi.UseShellExecute = true;
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex) { App.Log("打开路径 " + path, ex); }
        }

        private void ApplyAutoStart(bool on)
        {
            try
            {
                if (on) SystemActions.SetAutoStart(System.Reflection.Assembly.GetExecutingAssembly().Location);
                else SystemActions.RemoveAutoStart();
            }
            catch (Exception ex) { App.Log("设置开机自启", ex); }
        }

        private void ApplyFastStart(bool on)
        {
            try { if (on) SystemActions.DisableStartupDelay(); }
            catch (Exception ex) { App.Log("取消启动延迟", ex); }
        }

        // ---------------- 设置行构造 ----------------
        private Grid NewSettingRow(string label)
        {
            var row = new Grid();
            row.Height = 26;
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var t = new TextBlock();
            t.Text = label;
            t.FontSize = 11;
            t.Foreground = (Brush)FindResource("TextSecond");
            t.VerticalAlignment = VerticalAlignment.Center;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(t, 0);
            row.Children.Add(t);

            return row;
        }

        private void AddGroupTitle(string title)
        {
            var t = new TextBlock();
            t.Text = title;
            t.FontSize = 11;
            t.FontWeight = FontWeights.Bold;
            t.Foreground = (Brush)FindResource("AccentCyan");
            t.Margin = new Thickness(0, 10, 0, 4);
            pnlSettingsRows.Children.Add(t);

            AddDivider();
        }

        private void AddDivider()
        {
            var b = new Border();
            b.Height = 1;
            b.Background = (Brush)FindResource("DividerBrush");
            b.Margin = new Thickness(0, 2, 0, 2);
            pnlSettingsRows.Children.Add(b);
        }

        private void AddSwitchRow(string label, string hint, bool current, Action<bool> set)
        {
            var row = NewSettingRow(label);
            var sw = new ToggleButton();
            sw.Style = (Style)FindResource("SwitchBox");
            sw.IsChecked = current;
            sw.VerticalAlignment = VerticalAlignment.Center;
            if (!string.IsNullOrEmpty(hint)) sw.ToolTip = hint;
            sw.Checked += delegate { AppSettings.Save(); set(true); SafeRun(FitPanelHeight); };
            sw.Unchecked += delegate { AppSettings.Save(); set(false); SafeRun(FitPanelHeight); };
            Grid.SetColumn(sw, 1);
            row.Children.Add(sw);
            pnlSettingsRows.Children.Add(row);
            AddDivider();
        }

        private void AddSegRowInt(string label, int[] options, string suffix, int current, Action<int> set)
        {
            var row = NewSettingRow(label);
            var host = new StackPanel();
            host.Orientation = Orientation.Horizontal;
            host.VerticalAlignment = VerticalAlignment.Center;
            var style = (Style)FindResource("SegBtn");

            for (int i = 0; i < options.Length; i++)
            {
                int val = options[i];
                var b = new ToggleButton();
                b.Style = style;
                b.Content = val.ToString(CultureInfo.InvariantCulture) + suffix;
                b.IsChecked = (val == current);
                b.Click += delegate
                {
                    set(val);
                    AppSettings.Save();
                    BuildSettingsRows();
                    SafeRun(FitPanelHeight);
                };
                host.Children.Add(b);
            }

            Grid.SetColumn(host, 1);
            row.Children.Add(host);
            pnlSettingsRows.Children.Add(row);
            AddDivider();
        }

        private void AddSegRowText(string label, string[] options, string current, Action<string> set)
        {
            var row = NewSettingRow(label);
            var host = new StackPanel();
            host.Orientation = Orientation.Horizontal;
            host.VerticalAlignment = VerticalAlignment.Center;
            var style = (Style)FindResource("SegBtn");

            for (int i = 0; i < options.Length; i++)
            {
                string val = options[i];
                var b = new ToggleButton();
                b.Style = style;
                b.Content = val;
                b.IsChecked = (val == current);
                b.Click += delegate
                {
                    set(val);
                    AppSettings.Save();
                    BuildSettingsRows();
                    SafeRun(ApplyModuleVisibility);
                };
                host.Children.Add(b);
            }

            Grid.SetColumn(host, 1);
            row.Children.Add(host);
            pnlSettingsRows.Children.Add(row);
            AddDivider();
        }

        private void AddActionRow(string label, Action act, bool danger)
        {
            var b = new Button();
            b.Style = (Style)FindResource(danger ? "RowBtnDanger" : "RowBtn");
            b.Content = label;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            b.Margin = new Thickness(0, 1, 0, 1);
            b.Click += delegate { SafeRun(act); };
            pnlSettingsRows.Children.Add(b);
        }

        // ==================================================================
        // 工具
        // ==================================================================
        private void SafeRun(Action act)
        {
            try { act(); }
            catch (Exception ex) { App.Log("MainWindow", ex); }
        }

        /// <summary>退出时清理资源（托盘图标、定时器、性能计数器）</summary>
        internal void Cleanup()
        {
            try { if (_timer != null) { _timer.Stop(); _timer = null; } } catch { }
            try { if (_houseTimer != null) { _houseTimer.Stop(); _houseTimer = null; } } catch { }
            try { if (_clipMsgTimer != null) { _clipMsgTimer.Stop(); _clipMsgTimer = null; } } catch { }
            try { if (_glassTimer != null) { _glassTimer.Stop(); _glassTimer = null; } } catch { }
            try { if (_metalTimer != null) { _metalTimer.Stop(); _metalTimer = null; } } catch { }
            try { if (_cardTimer != null) { _cardTimer.Stop(); _cardTimer = null; } } catch { }
            try { _cardClock.Stop(); } catch { }
            try { DiskPerf.Shutdown(); } catch { }
            try { if (_tray != null) { _tray.Dispose(); _tray = null; } } catch { }
        }
    }
}
