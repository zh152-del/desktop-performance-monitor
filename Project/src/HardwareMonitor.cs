using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace PerfMonitorWPF
{
    /// <summary>
    /// 硬件采集器 (Hardware Collector)
    ///
    /// 【启动性能设计】分两个阶段，绝不让 UI 线程等硬件：
    ///   阶段一 FastInit()      —— 纯 Win32 API + 本地缓存，耗时 &lt; 10ms，窗口立刻能出内容
    ///   阶段二 BeginHeavyInit()—— 后台线程做 LHM 内核驱动加载 + WMI 查询（这部分要 5~10 秒）
    ///
    /// 原则：每一项检测都有空值保护，失败输出 0，绝不抛异常。
    /// </summary>
    public static class HardwareMonitor
    {
        // ===============================================================
        // Win32 原生 API —— 用来替代慢得离谱的 WMI 查询
        // ===============================================================

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME64
        {
            public uint Low;
            public uint High;
            public ulong Value { get { return ((ulong)High << 32) | Low; } }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out FILETIME64 idleTime,
                                                  out FILETIME64 kernelTime,
                                                  out FILETIME64 userTime);

        // ===============================================================
        // 状态
        // ===============================================================
        private static Computer _computer;
        private static volatile bool _fastReady;      // 阶段一完成
        private static volatile bool _heavyReady;     // 阶段二完成（LHM 可用或已确认不可用）
        private static volatile bool _heavyStarted;   // 阶段二已在后台跑
        private static volatile bool _driverBlocked;  // 内核驱动被安全软件拦截
        private static readonly object _lock = new object();

        // 静态信息缓存 (static info cache)
        private static string _cpuName = "检测中…";
        private static int _cpuCores;
        private static string _gpuName = "检测中…";
        private static string _osName = "";
        private static string _hostName = "";
        private static long _totalMemBytes;

        // 网速计算用的上一次采样 (previous sample for speed calc)
        private static long _lastRxBytes = -1;
        private static long _lastTxBytes = -1;
        private static DateTime _lastNetTime = DateTime.MinValue;

        // CPU 占用率：GetSystemTimes 差分法（零开销，无需 PerformanceCounter）
        private static ulong _lastIdle, _lastKernel, _lastUser;
        private static bool _hasCpuBaseline;

        // 盘符 -> 物理盘索引（WMI 关联链只算一次，之后走缓存）
        private static Dictionary<string, int> _letterToDiskIndex;
        private static volatile bool _diskMapResolved;

        /// <summary>是否因安全软件拦截而降级到 WMI 模式（温度数据可能不可用）</summary>
        public static bool IsDriverBlocked { get { return _driverBlocked; } }

        // ===============================================================
        // PawnIO 检测
        //
        // LibreHardwareMonitor 从 0.9.6 起把 ring0 驱动从 WinRing0 换成了 PawnIO。
        // WinRing0 是 CVE-2020-14979 的漏洞驱动，火绒 / Microsoft Defender 都会拦
        // （本机 2026-09-21 就被火绒记了一次「漏洞驱动拦截」）。
        //
        // PawnIO 是独立的系统级驱动，需要用户自己安装一次；LHM 只负责往
        // \\.\GLOBALROOT\Device\PawnIO 里加载它自带的 ring0 模块。
        // 没装的话，处理器 / 显卡温度读不到，但其它指标（占用率、内存、磁盘、
        // 剪贴板）全部照常 —— 所以要在界面上明确告诉用户，别让人以为温度坏了。
        //
        // 判断方式和 LHM 一致：看注册表卸载项里的 DisplayVersion。
        // ===============================================================
        private static bool _pawnIoChecked;
        private static bool _pawnIoInstalled;
        private static string _pawnIoVersion = "";

        /// <summary>最低要求版本，低于这个版本 LHM 用不了</summary>
        public static readonly Version PawnIoMinVersion = new Version(2, 0, 0, 0);

        private static void CheckPawnIo()
        {
            if (_pawnIoChecked) return;
            _pawnIoChecked = true;

            const string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
            try
            {
                RegistryKey k = null;
                try { k = Registry.LocalMachine.OpenSubKey(sub); }
                catch { }

                if (k == null)
                {
                    // 64 位视图再找一次（LHM 也是这么干的）
                    try
                    {
                        using (var baseKey = RegistryKey.OpenBaseKey(
                                   RegistryHive.LocalMachine, RegistryView.Registry64))
                        {
                            if (baseKey != null) k = baseKey.OpenSubKey(sub);
                        }
                    }
                    catch { }
                }

                if (k != null)
                {
                    using (k)
                    {
                        var v = k.GetValue("DisplayVersion");
                        if (v != null)
                        {
                            _pawnIoVersion = v.ToString();
                            _pawnIoInstalled = true;
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>系统上装没装 PawnIO</summary>
        public static bool IsPawnIoInstalled
        {
            get { CheckPawnIo(); return _pawnIoInstalled; }
        }

        /// <summary>PawnIO 版本号字符串，没装时返回空串</summary>
        public static string PawnIoVersion
        {
            get { CheckPawnIo(); return _pawnIoVersion; }
        }

        /// <summary>装了但是版本太旧</summary>
        public static bool IsPawnIoTooOld
        {
            get
            {
                CheckPawnIo();
                if (!_pawnIoInstalled) return false;
                Version v;
                if (!Version.TryParse(_pawnIoVersion, out v)) return false;
                return v < PawnIoMinVersion;
            }
        }

        /// <summary>重量级初始化是否已完成（完成后才有温度数据）</summary>
        public static bool IsHeavyReady { get { return _heavyReady; } }

        /// <summary>访问硬件树时用的更新访问者 (visitor)</summary>
        private class UpdateVisitor : IVisitor
        {
            /// <summary>
            /// 本轮是否刷新 Storage（硬盘）。
            /// Storage 的 Update() 会真的往磁盘发 SMART / IOCTL 查询 —— 那是实打实的磁盘 IO。
            /// 温度是慢变量，没必要每 5 秒问一次，由下面的 StorageRefreshEvery 降频。
            /// </summary>
            public bool UpdateStorage = true;

            public void VisitComputer(IComputer computer)
            {
                if (computer == null) return;
                computer.Traverse(this);
            }
            public void VisitHardware(IHardware hardware)
            {
                if (hardware == null) return;

                bool skip = !UpdateStorage && hardware.HardwareType == HardwareType.Storage;
                if (!skip)
                {
                    try { hardware.Update(); } catch { }
                }

                foreach (var sub in hardware.SubHardware)
                {
                    if (sub != null) sub.Accept(this);
                }
            }
            public void VisitSensor(ISensor sensor) { }
            public void VisitParameter(IParameter parameter) { }
        }

        private static readonly UpdateVisitor _visitor = new UpdateVisitor();

        /// <summary>
        /// 每多少轮快照才真正去读一次硬盘 SMART。
        /// 5 秒一轮 × 12 = 60 秒。硬盘温度变化很慢，一分钟一次完全够看，
        /// 同时把「每 5 秒戳一次硬盘」这种无谓的磁盘 IO 降下来（也避免机械盘被反复唤醒）。
        /// </summary>
        private const int StorageRefreshEvery = 12;
        private static int _snapshotSeq;

        // ===============================================================
        // 通用空值保护：重试 N 次，失败返回兜底值
        // ===============================================================
        private static T Safe<T>(Func<T> fn, T fallback, int retries = 3)
        {
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    var v = fn();
                    if (v != null) return v;
                }
                catch { }
            }
            return fallback;
        }

        private static double SafeNum(Func<double?> fn, double fallback = 0, int retries = 3)
        {
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    var v = fn();
                    if (v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value))
                        return v.Value;
                }
                catch { }
            }
            return fallback;
        }

        // ===============================================================
        // 阶段一：FastInit —— 只用零成本 API，必须在 10ms 内返回
        // ===============================================================
        public static void FastInit()
        {
            if (_fastReady) return;
            lock (_lock)
            {
                if (_fastReady) return;

                _hostName = Safe(() => Environment.MachineName, "本机");
                _cpuCores = (int)SafeNum(() => (double?)Environment.ProcessorCount, 0);

                // 内存总量：GlobalMemoryStatusEx 是微秒级，WMI 是秒级
                try
                {
                    var ms = new MEMORYSTATUSEX();
                    if (GlobalMemoryStatusEx(ms)) _totalMemBytes = (long)ms.ullTotalPhys;
                }
                catch { }

                // 从上次运行的缓存里读硬件名称，秒出
                LoadCache();

                if (string.IsNullOrEmpty(_osName))
                    _osName = Environment.OSVersion.VersionString;

                // 先打一次 CPU 时间基线，第一次刷新就能有真实占用率
                SampleCpuTimes();

                _fastReady = true;
            }
        }

        // ===============================================================
        // 阶段二：BeginHeavyInit —— 后台线程做 LHM 驱动 + WMI
        // ===============================================================
        /// <summary>在后台线程做重量级初始化，完成后回调（回调在后台线程上）</summary>
        public static void BeginHeavyInit(Action onReady)
        {
            if (_heavyReady)
            {
                if (onReady != null) { try { onReady(); } catch { } }
                return;
            }
            if (_heavyStarted) return;
            _heavyStarted = true;

            var t = new Thread(delegate ()
            {
                var sw = Stopwatch.StartNew();
                try { HeavyInit(); }
                catch (Exception ex) { App.Log("HeavyInit", ex); }
                App.Trace("硬件重量级初始化完成，耗时 " + sw.ElapsedMilliseconds + "ms"
                          + (_driverBlocked ? "（驱动被拦截，走 WMI 兜底）" : ""));

                if (onReady != null) { try { onReady(); } catch { } }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;  // 别跟开机时的其它进程抢
            t.Start();
        }

        private static void HeavyInit()
        {
            // 1) LibreHardwareMonitor —— 会释放并加载内核驱动，是最慢的一步
            Computer comp = null;
            try
            {
                comp = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsStorageEnabled = true,
                    IsMotherboardEnabled = false,
                    IsNetworkEnabled = false   // 网速走 .NET 原生 API，不需要 LHM
                };
                comp.Open();
                comp.Accept(_visitor);
            }
            catch (System.Security.SecurityException)
            {
                comp = null; _driverBlocked = true;
            }
            catch (UnauthorizedAccessException)
            {
                comp = null; _driverBlocked = true;
            }
            catch (Exception)
            {
                comp = null; _driverBlocked = true;
            }

            lock (_lock) { _computer = comp; }

            // 2) 硬件名称（能从 LHM 拿就不查 WMI）
            RefreshStaticNames();

            _heavyReady = true;

            // 3) 盘符→物理盘映射（WMI 关联链很慢，放最后，不阻塞温度显示）
            ResolveDiskMap();

            // 4) 存缓存，下次启动秒出
            SaveCache();
        }

        // ===============================================================
        // 静态信息缓存文件
        // ===============================================================
        private static string CachePath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PerfMonitorWPF");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "hwcache.ini");
            }
        }

        private static void LoadCache()
        {
            try
            {
                var p = CachePath;
                if (!File.Exists(p)) return;
                foreach (var line in File.ReadAllLines(p))
                {
                    var i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var k = line.Substring(0, i).Trim();
                    var v = line.Substring(i + 1).Trim();
                    if (v.Length == 0) continue;

                    if (k == "cpu") _cpuName = v;
                    else if (k == "gpu") _gpuName = v;
                    else if (k == "os") _osName = v;
                    else if (k == "diskmap")
                    {
                        var map = new Dictionary<string, int>();
                        foreach (var pair in v.Split('|'))
                        {
                            var kv = pair.Split(':');
                            if (kv.Length != 2) continue;
                            int idx;
                            if (int.TryParse(kv[1], out idx)) map[kv[0]] = idx;
                        }
                        if (map.Count > 0)
                        {
                            _letterToDiskIndex = map;
                            _diskMapResolved = true;   // 有缓存就直接用，省掉开机时的 WMI 关联查询
                        }
                    }
                }
            }
            catch { }
        }

        private static void SaveCache()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("cpu=" + _cpuName);
                sb.AppendLine("gpu=" + _gpuName);
                sb.AppendLine("os=" + _osName);
                var map = _letterToDiskIndex;
                if (map != null && map.Count > 0)
                {
                    sb.AppendLine("diskmap=" + string.Join("|",
                        map.Select(kv => kv.Key + ":" + kv.Value).ToArray()));
                }
                File.WriteAllText(CachePath, sb.ToString());
            }
            catch { }
        }

        // ===============================================================
        // 硬件名称（重量级，后台执行）
        // ===============================================================
        private static void RefreshStaticNames()
        {
            // CPU 名称：优先 LHM，其次 WMI
            var lhmCpu = Safe(() =>
            {
                if (_computer == null) return null;
                var h = _computer.Hardware.FirstOrDefault(x => x.HardwareType == HardwareType.Cpu);
                return h == null ? null : h.Name;
            }, null, 1);

            if (!string.IsNullOrWhiteSpace(lhmCpu))
            {
                _cpuName = lhmCpu.Trim();
            }
            else
            {
                var wmiCpu = Safe(() =>
                {
                    using (var s = new ManagementObjectSearcher("root\\CIMV2", "SELECT Name FROM Win32_Processor"))
                    {
                        foreach (ManagementObject o in s.Get())
                        {
                            var n = o["Name"];
                            if (n != null) return n.ToString().Trim();
                        }
                    }
                    return null;
                }, null, 1);
                if (!string.IsNullOrWhiteSpace(wmiCpu)) _cpuName = wmiCpu;
            }

            // 线程数后缀只加一次
            if (_cpuCores > 0 && _cpuName.IndexOf(" 线程", StringComparison.Ordinal) < 0)
                _cpuName = _cpuName + "  " + _cpuCores + " 线程";

            // GPU 名称
            var lhmGpu = Safe(() =>
            {
                if (_computer == null) return null;
                var h = _computer.Hardware.FirstOrDefault(x =>
                    x.HardwareType == HardwareType.GpuNvidia ||
                    x.HardwareType == HardwareType.GpuAmd ||
                    x.HardwareType == HardwareType.GpuIntel);
                return h == null ? null : h.Name;
            }, null, 1);

            if (!string.IsNullOrWhiteSpace(lhmGpu))
            {
                _gpuName = lhmGpu.Trim();
            }
            else
            {
                var wmiGpu = Safe(() =>
                {
                    using (var s = new ManagementObjectSearcher("root\\CIMV2", "SELECT Name FROM Win32_VideoController"))
                    {
                        foreach (ManagementObject o in s.Get())
                        {
                            var n = o["Name"];
                            if (n != null) return n.ToString().Trim();
                        }
                    }
                    return null;
                }, null, 1);
                if (!string.IsNullOrWhiteSpace(wmiGpu)) _gpuName = wmiGpu;
            }

            // 系统名称
            var os = Safe(() =>
            {
                using (var s = new ManagementObjectSearcher("root\\CIMV2", "SELECT Caption FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        var v = o["Caption"];
                        if (v != null) return v.ToString().Trim();
                    }
                }
                return null;
            }, null, 1);
            if (!string.IsNullOrWhiteSpace(os)) _osName = os;
        }

        public static StaticInfo GetStatic()
        {
            FastInit();
            return new StaticInfo
            {
                CpuBrand = _cpuName,
                CpuCores = _cpuCores,
                GpuModel = _gpuName,
                OsName = _osName,
                TotalMemBytes = _totalMemBytes,
                HostName = _hostName
            };
        }

        // ===============================================================
        // 采集一次快照 (one snapshot)
        // LHM 没就绪时自动走轻量路径，不阻塞
        // ===============================================================
        public static Snapshot GetSnapshot()
        {
            FastInit();

            var s = new Snapshot { Ts = DateTime.Now };

            // 刷新硬件树（只有 LHM 就绪才做）。
            // 硬盘走降频：绝大多数轮询里跳过 Storage，避免每 5 秒戳一次磁盘 SMART。
            var comp = _computer;
            if (comp != null)
            {
                _snapshotSeq++;
                _visitor.UpdateStorage = (_snapshotSeq % StorageRefreshEvery) == 1;
                try { comp.Accept(_visitor); } catch { }
            }

            FillCpu(s);
            FillGpu(s);
            FillMemory(s);
            s.Disks = GetDisks();

            // 网络卡已经从界面上去掉了，这里不再每轮枚举网卡 / 读网卡统计。
            // 留着 GetNetwork() 方法，将来要恢复网络显示时把下面这行放开即可。
            // s.Network = GetNetwork();

            return s;
        }

        private static IHardware FindHw(params HardwareType[] types)
        {
            return Safe(() =>
            {
                var comp = _computer;
                if (comp == null) return null;
                return comp.Hardware.FirstOrDefault(h => types.Contains(h.HardwareType));
            }, null, 1);
        }

        private static IEnumerable<ISensor> AllSensors(IHardware hw)
        {
            if (hw == null) yield break;
            ISensor[] own;
            try { own = hw.Sensors ?? new ISensor[0]; } catch { yield break; }
            foreach (var s in own) if (s != null) yield return s;

            IHardware[] subs;
            try { subs = hw.SubHardware ?? new IHardware[0]; } catch { yield break; }
            foreach (var sub in subs)
            {
                foreach (var s in AllSensors(sub)) yield return s;
            }
        }

        // ---------------------------------------------------------------
        // CPU：占用率走 GetSystemTimes 差分（零开销），温度走 LHM
        // ---------------------------------------------------------------
        private static void SampleCpuTimes()
        {
            try
            {
                FILETIME64 idle, kernel, user;
                if (!GetSystemTimes(out idle, out kernel, out user)) return;
                _lastIdle = idle.Value;
                _lastKernel = kernel.Value;
                _lastUser = user.Value;
                _hasCpuBaseline = true;
            }
            catch { }
        }

        /// <summary>用两次 GetSystemTimes 的差值算整机 CPU 占用率，微秒级开销</summary>
        private static double ReadCpuLoadNative()
        {
            try
            {
                FILETIME64 idle, kernel, user;
                if (!GetSystemTimes(out idle, out kernel, out user)) return -1;

                ulong i = idle.Value, k = kernel.Value, u = user.Value;
                if (!_hasCpuBaseline)
                {
                    _lastIdle = i; _lastKernel = k; _lastUser = u;
                    _hasCpuBaseline = true;
                    return -1;
                }

                ulong dIdle = i - _lastIdle;
                ulong dKernel = k - _lastKernel;
                ulong dUser = u - _lastUser;
                _lastIdle = i; _lastKernel = k; _lastUser = u;

                // kernel 已包含 idle
                ulong total = dKernel + dUser;
                if (total == 0) return -1;
                double busy = (double)(total - dIdle) / total * 100.0;
                return Clamp(busy, 0, 100);
            }
            catch { return -1; }
        }

        private static void FillCpu(Snapshot s)
        {
            var hw = FindHw(HardwareType.Cpu);

            // 占用率：原生 API 优先（快且准），LHM 传感器作为补充
            double load = ReadCpuLoadNative();
            if (load < 0)
            {
                load = SafeNum(() =>
                {
                    var sensor = AllSensors(hw).FirstOrDefault(x =>
                        x.SensorType == SensorType.Load &&
                        x.Name != null && x.Name.IndexOf("Total", StringComparison.OrdinalIgnoreCase) >= 0);
                    return sensor == null ? (double?)null : sensor.Value;
                }, 0, 1);
            }
            s.CpuLoad = Clamp(Math.Round(load, 1), 0, 100);

            // 温度：取 Package / Core Max / 任意温度传感器最大值
            s.CpuTemp = SafeNum(() =>
            {
                var temps = AllSensors(hw)
                    .Where(x => x.SensorType == SensorType.Temperature && x.Value.HasValue)
                    .ToList();
                if (temps.Count == 0) return null;

                var pkg = temps.FirstOrDefault(x => x.Name != null &&
                    (x.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     x.Name.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0));
                if (pkg != null && pkg.Value.HasValue) return pkg.Value.Value;

                return temps.Max(x => x.Value.Value);
            }, 0, 1);
            s.CpuTemp = Math.Round(s.CpuTemp, 0);

            s.CpuThreads = _cpuCores;
        }

        private static void FillGpu(Snapshot s)
        {
            var hw = FindHw(HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel);

            s.GpuName = _gpuName;

            s.GpuLoad = Clamp(Math.Round(SafeNum(() =>
            {
                var sensor = AllSensors(hw).FirstOrDefault(x =>
                    x.SensorType == SensorType.Load &&
                    x.Name != null && x.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0);
                if (sensor == null)
                    sensor = AllSensors(hw).FirstOrDefault(x => x.SensorType == SensorType.Load);
                return sensor == null ? (double?)null : sensor.Value;
            }, 0, 1), 1), 0, 100);

            s.GpuTemp = Math.Round(SafeNum(() =>
            {
                var temps = AllSensors(hw)
                    .Where(x => x.SensorType == SensorType.Temperature && x.Value.HasValue)
                    .ToList();
                if (temps.Count == 0) return null;
                var core = temps.FirstOrDefault(x => x.Name != null &&
                    x.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0);
                if (core != null && core.Value.HasValue) return core.Value.Value;
                return temps.Max(x => x.Value.Value);
            }, 0, 1), 0);

            // 显存 (MB -> bytes)：不同驱动传感器名不同，按优先级依次尝试
            double usedMb = SafeNum(() =>
            {
                var all = AllSensors(hw)
                    .Where(x => x.SensorType == SensorType.SmallData &&
                                x.Name != null && x.Value.HasValue)
                    .ToList();
                if (all.Count == 0) return null;

                var s1 = all.FirstOrDefault(x =>
                    x.Name.IndexOf("GPU Memory Used", StringComparison.OrdinalIgnoreCase) >= 0);
                if (s1 != null) return s1.Value.Value;

                var s2 = all.FirstOrDefault(x =>
                    x.Name.IndexOf("Dedicated Memory Used", StringComparison.OrdinalIgnoreCase) >= 0);
                if (s2 != null) return s2.Value.Value;

                var s3 = all.FirstOrDefault(x =>
                    x.Name.IndexOf("Memory Used", StringComparison.OrdinalIgnoreCase) >= 0);
                return s3 == null ? (double?)null : s3.Value.Value;
            }, 0, 1);

            double totalMb = SafeNum(() =>
            {
                var sensor = AllSensors(hw).FirstOrDefault(x =>
                    x.SensorType == SensorType.SmallData &&
                    x.Name != null &&
                    x.Name.IndexOf("Memory Total", StringComparison.OrdinalIgnoreCase) >= 0);
                return sensor == null ? (double?)null : sensor.Value;
            }, 0, 1);

            s.GpuMemUsed = (long)(usedMb * 1024 * 1024);
            s.GpuMemTotal = (long)(totalMb * 1024 * 1024);
            s.GpuMemPercent = s.GpuMemTotal > 0
                ? Math.Round((double)s.GpuMemUsed / s.GpuMemTotal * 100, 1)
                : 0;
        }

        // ---------------------------------------------------------------
        // 内存：全程走 GlobalMemoryStatusEx，不碰 WMI
        // ---------------------------------------------------------------
        private static void FillMemory(Snapshot s)
        {
            try
            {
                var ms = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(ms))
                {
                    s.MemTotal = (long)ms.ullTotalPhys;
                    s.MemUsed = (long)(ms.ullTotalPhys - ms.ullAvailPhys);
                    if (_totalMemBytes <= 0) _totalMemBytes = s.MemTotal;
                }
            }
            catch { }

            if (s.MemTotal <= 0) s.MemTotal = _totalMemBytes;
            s.MemPercent = s.MemTotal > 0
                ? Clamp(Math.Round((double)s.MemUsed / s.MemTotal * 100, 1), 0, 100)
                : 0;

            // 内存温度：可选能力。取不到就是 0，卡片据此整块隐藏温度文字。
            s.MemTemp = ReadMemoryTemp();
        }

        /// <summary>
        /// 系统内存（DIMM）温度。
        /// 先问硬件库有没有 Memory 类型硬件、并且它带温度传感器 —— 普通 DDR4 台机
        /// 通常没有 SPD 温度通道，本机实测返回 0。取不到就返回 0，
        /// 绝不拿 CPU / 硬盘温度冒充，也不因为这一步失败影响其他采集。
        /// </summary>
        private static double ReadMemoryTemp()
        {
            return SafeNum(delegate
            {
                var comp = _computer;
                if (comp == null) return null;

                var all = comp.Hardware;
                if (all == null) return null;

                foreach (var hw in all)
                {
                    if (hw == null) continue;

                    string type = "";
                    try { type = hw.HardwareType.ToString(); } catch { continue; }
                    if (type != "Memory") continue;

                    foreach (ISensor se in AllSensors(hw))
                    {
                        if (se == null || se.SensorType != SensorType.Temperature) continue;
                        if (!se.Value.HasValue) continue;
                        double v = se.Value.Value;
                        if (v > 0 && v < 110) return (double?)v;
                    }
                }
                return null;
            }, 0, 1);
        }

        // ---------------------------------------------------------------
        // 磁盘：容量走 DriveInfo（毫秒级），温度走 LHM
        // ---------------------------------------------------------------
        private static List<DiskInfo> GetDisks()
        {
            var list = new List<DiskInfo>();

            // 物理盘索引 -> 温度
            var tempByIndex = GetDiskTemps();

            // 兜底：所有物理盘最高温
            double maxDiskTemp = 0;
            if (tempByIndex.Count > 0) maxDiskTemp = tempByIndex.Values.Max();

            var map = _letterToDiskIndex;

            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed) continue;
                        if (!d.IsReady) continue;

                        long size = d.TotalSize;
                        long free = d.TotalFreeSpace;
                        long used = Math.Max(0, size - free);
                        // "C:\" -> "C:"
                        var mount = d.Name.TrimEnd('\\');
                        var key = mount.TrimEnd(':').ToUpperInvariant();

                        double t = 0;
                        if (map != null && map.ContainsKey(key))
                        {
                            int idx = map[key];
                            if (tempByIndex.ContainsKey(idx)) t = tempByIndex[idx];
                            else t = maxDiskTemp;
                        }
                        else t = maxDiskTemp;

                        list.Add(new DiskInfo
                        {
                            Mount = mount,
                            Size = size,
                            Used = used,
                            Percent = size > 0 ? Math.Round((double)used / size * 100, 1) : 0,
                            Temp = t > 0 ? Math.Round(t, 0) : 0
                        });
                    }
                    catch { }
                }
            }
            catch { }

            return list;
        }

        /// <summary>
        /// 物理盘索引 -> 温度（直接读 LHM 内存里的传感器，无 IO）。
        ///
        /// 【必须按 Identifier 取索引，不能按集合下标取！】
        /// LHM 的 Storage 硬件在 computer.Hardware 里的排列顺序和物理盘序号是不一致的，
        /// 本机实测顺序是 /hdd/1、/hdd/2、/hdd/0（即 240G、Hitachi、480G），
        /// 而物理盘序号是 0=480G(C+E)、1=240G(D)、2=Hitachi(F)。
        /// 早期版本按 storages[i] 直接当下标 i 用，导致三块盘的温度整体错位
        /// （C 盘会显示成 240G 那块盘的温度）。
        ///
        /// Identifier 形如 "/hdd/3"，尾部数字就是物理盘索引，与
        /// WMI Win32_DiskDrive.Index、性能计数器实例名 "3 X: Y:" 里的 3 完全一致。
        /// </summary>
        public static Dictionary<int, double> GetDiskTemps()
        {
            var result = new Dictionary<int, double>();
            try
            {
                var comp = _computer;
                if (comp == null) return result;

                foreach (var storage in comp.Hardware)
                {
                    if (storage.HardwareType != HardwareType.Storage) continue;

                    int idx = -1;
                    try
                    {
                        string ident = storage.Identifier.ToString();
                        if (!string.IsNullOrEmpty(ident))
                        {
                            int slash = ident.LastIndexOf('/');
                            if (slash >= 0 && slash + 1 < ident.Length)
                                int.TryParse(ident.Substring(slash + 1), out idx);
                        }
                    }
                    catch { }

                    var t = AllSensors(storage)
                        .Where(x => x.SensorType == SensorType.Temperature && x.Value.HasValue)
                        .Select(x => x.Value.Value)
                        .ToList();

                    if (idx >= 0 && t.Count > 0) result[idx] = t.Max();
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 盘符 -> 物理盘索引（磁盘映射还没解析出来时返回 -1）。
        /// "C" / "C:" 都能传。
        /// </summary>
        public static int GetPhysicalDiskIndex(string letter)
        {
            var map = _letterToDiskIndex;
            if (map == null || string.IsNullOrEmpty(letter)) return -1;

            int idx;
            var key = letter.Trim().TrimEnd(':').ToUpperInvariant();
            if (map.TryGetValue(key, out idx)) return idx;
            return -1;
        }

        /// <summary>
        /// 建立 盘符 -> 物理盘索引 的映射。
        /// WMI 的 磁盘→分区→逻辑盘 关联链非常慢（每块盘两层 GetRelated），
        /// 所以只在后台算一次并写入缓存，之后启动直接复用。
        /// </summary>
        private static void ResolveDiskMap()
        {
            if (_diskMapResolved) return;   // 已有缓存

            var map = new Dictionary<string, int>();
            try
            {
                // 注意：GetRelated 需要对象带完整 __PATH，所以必须 SELECT *，
                // 只选单个字段会导致关联查询返回空集合。
                using (var dq = new ManagementObjectSearcher("root\\CIMV2",
                    "SELECT * FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject disk in dq.Get())
                    {
                        int idx;
                        try { idx = Convert.ToInt32(disk["Index"]); }
                        catch { continue; }

                        try
                        {
                            foreach (ManagementObject part in disk.GetRelated("Win32_DiskPartition"))
                            {
                                foreach (ManagementObject ld in part.GetRelated("Win32_LogicalDisk"))
                                {
                                    var id = ld["DeviceID"];
                                    if (id == null) continue;
                                    var key = id.ToString().TrimEnd(':').ToUpperInvariant();
                                    if (!map.ContainsKey(key)) map[key] = idx;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { App.Log("解析磁盘映射", ex); }

            App.Trace("磁盘映射解析完成，盘符数=" + map.Count);

            if (map.Count > 0)
            {
                _letterToDiskIndex = map;
                _diskMapResolved = true;
            }
        }

        // ---------------------------------------------------------------
        // 网络：用两次采样差值算真实速率
        // ---------------------------------------------------------------
        private static NetworkInfo GetNetwork()
        {
            var info = new NetworkInfo { Iface = "未连接", RxSec = 0, TxSec = 0, RxText = "0 B/s", TxText = "0 B/s" };

            try
            {
                var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                                n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback &&
                                n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                    .ToList();
                if (nics.Count == 0) return info;

                long totalRx = 0, totalTx = 0;
                string bestName = null;
                long bestTraffic = -1;

                foreach (var n in nics)
                {
                    try
                    {
                        var st = n.GetIPv4Statistics();
                        totalRx += st.BytesReceived;
                        totalTx += st.BytesSent;
                        var traffic = st.BytesReceived + st.BytesSent;
                        if (traffic > bestTraffic) { bestTraffic = traffic; bestName = n.Name; }
                    }
                    catch { }
                }

                info.Iface = string.IsNullOrEmpty(bestName) ? "未连接" : bestName;

                var now = DateTime.Now;
                if (_lastRxBytes >= 0 && _lastNetTime != DateTime.MinValue)
                {
                    var sec = (now - _lastNetTime).TotalSeconds;
                    if (sec > 0.2)
                    {
                        info.RxSec = (long)Math.Max(0, (totalRx - _lastRxBytes) / sec);
                        info.TxSec = (long)Math.Max(0, (totalTx - _lastTxBytes) / sec);
                    }
                }

                _lastRxBytes = totalRx;
                _lastTxBytes = totalTx;
                _lastNetTime = now;

                info.RxText = FormatBytes(info.RxSec) + "/s";
                info.TxText = FormatBytes(info.TxSec) + "/s";
            }
            catch { }

            return info;
        }

        // ---------------------------------------------------------------
        // C 盘软件占用：扫描 Program Files(x86) / Program Files 下各子目录
        // ---------------------------------------------------------------
        /// <summary>
        /// 枚举 C:\Program Files 与 C:\Program Files (x86) 下的每个子目录，
        /// 递归计算各自占用的字节数，按从大到小排序返回。
        /// 扫描较慢（递归读文件很多），由调用方在后台线程执行。
        /// </summary>
        public static List<SoftDirInfo> GetSoftwareDirectories()
        {
            var roots = new[] { @"C:\Program Files", @"C:\Program Files (x86)" };
            var list = new List<SoftDirInfo>();

            foreach (var root in roots)
            {
                try
                {
                    var di = new DirectoryInfo(root);
                    if (!di.Exists) continue;

                    foreach (var sub in di.EnumerateDirectories())
                    {
                        try
                        {
                            // 跳过符号链接 / junction（避免重复计数与死循环）
                            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            long size = DirSizeSafe(sub, 0);
                            list.Add(new SoftDirInfo { Name = sub.Name, Path = sub.FullName, SizeBytes = size });
                        }
                        catch { }
                    }
                }
                catch { }
            }

            list.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            return list;
        }

        /// <summary>递归统计目录字节数（跳过不可访问与重解析点，单文件出错不中断）</summary>
        private static long DirSizeSafe(DirectoryInfo dir, int depth)
        {
            if (depth > 12) return 0;   // 防御性上限，避免极端深目录拖垮扫描
            long total = 0;
            try
            {
                foreach (var f in dir.EnumerateFiles())
                {
                    try { total += f.Length; } catch { }
                }
                foreach (var d in dir.EnumerateDirectories())
                {
                    try
                    {
                        if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        total += DirSizeSafe(d, depth + 1);
                    }
                    catch { }
                }
            }
            catch { }
            return total;
        }

        // ---------------------------------------------------------------
        public static string FormatBytes(double bytes)
        {
            if (bytes <= 0 || double.IsNaN(bytes)) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            int i = 0;
            double v = bytes;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return Math.Round(v, i <= 1 ? 0 : 1).ToString() + " " + units[i];
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (double.IsNaN(v)) return lo;
            return v < lo ? lo : (v > hi ? hi : v);
        }

        public static void Shutdown()
        {
            lock (_lock)
            {
                try { if (_computer != null) _computer.Close(); } catch { }
                _computer = null;
                _heavyReady = false;
                _heavyStarted = false;
                _fastReady = false;
            }
        }

        // ---------------------------------------------------------------
        // 数据模型 (models)
        // ---------------------------------------------------------------
        public class StaticInfo
        {
            public string CpuBrand = "未知 CPU";
            public int CpuCores;
            public string GpuModel = "未知显卡";
            public string OsName = "";
            public long TotalMemBytes;
            public string HostName = "";
        }

        public class Snapshot
        {
            public DateTime Ts;
            public double CpuLoad;
            public double CpuTemp;
            public int CpuThreads;
            public double GpuLoad;
            public double GpuTemp;
            public string GpuName = "未知显卡";
            public long GpuMemUsed;
            public long GpuMemTotal;
            public double GpuMemPercent;
            public long MemUsed;
            public long MemTotal;
            public double MemPercent;
            /// <summary>系统内存温度（℃）；0 表示取不到，界面必须隐藏，不许显示 0 或顶替值。</summary>
            public double MemTemp;
            public List<DiskInfo> Disks = new List<DiskInfo>();
            public NetworkInfo Network = new NetworkInfo();
        }

        public class DiskInfo
        {
            public string Mount = "";
            public long Size;
            public long Used;
            public double Percent;
            public double Temp;
        }

        public class NetworkInfo
        {
            public string Iface = "";
            public long RxSec;
            public long TxSec;
            public string RxText = "0 B/s";
            public string TxText = "0 B/s";
        }

        /// <summary>C 盘软件目录占用信息</summary>
        public class SoftDirInfo
        {
            public string Name = "";      // 显示名（文件夹名）
            public string Path = "";      // 完整路径（悬停提示用）
            public long SizeBytes;        // 占用字节数
        }
    }
}
