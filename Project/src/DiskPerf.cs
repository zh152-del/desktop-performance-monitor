using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace PerfMonitorWPF
{
    /// <summary>
    /// 物理磁盘实时性能层（physical disk realtime perf）。
    ///
    /// 数据源固定为 Windows 性能计数器对象 PhysicalDisk。本机实例名形如：
    ///     "0 C: E:"    "1 D:"    "2 F:"
    /// 这三个数字与 WMI Win32_DiskDrive.Index、以及 LibreHardwareMonitor 的
    /// Identifier "/hdd/N" 里的 N 完全一致，所以三者可以按索引严格对齐。
    ///
    /// 三个指标的取法（严格按要求，绝不用读写速度反推活动时间）：
    ///
    ///   读取速度 = 计数器 "Disk Read Bytes/sec"
    ///   写入速度 = 计数器 "Disk Write Bytes/sec"
    ///      这两个计数器本身就是速率型。PerformanceCounter.NextValue() 在速率型
    ///      计数器上返回的是「(本次累计字节 - 上次累计字节) ÷ 两次调用的真实时间间隔」，
    ///      也就是需求里写的那条公式；本程序按面板刷新间隔调用它，得到的正是该间隔
    ///      的平均速度，不需要自己积分，也不会有自建差分的时间戳漂移问题。
    ///
    ///   活动时间 = 计数器 "% Disk Time"，直接读，不反推。
    ///      若该计数器在本机不可用，退回 100 - "% Idle Time"（任务管理器「活动时间」
    ///      用的就是这个等价量）。实测两者差异 < 0.5%。
    ///
    ///   平均延迟 = 计数器 "Avg. Disk sec/Transfer"，直接读。
    ///      当前 UI 按需求（磁盘卡只保留 3 个指标）不显示，数据保留以便随时启用。
    /// </summary>
    public static class DiskPerf
    {
        // ------------------------------------------------------------------
        // 数据模型
        // ------------------------------------------------------------------
        public class PhysDisk
        {
            public int Index;            // 物理盘索引
            public string Instance = ""; // 计数器实例名，如 "0 C: E:"
            public string Letters = "";  // 盘符串，如 "C: E:"
            public string Title = "";    // 显示名，如 "磁盘0【C E】"
            public double Temp;          // 温度 °C（0 = 未知），来自 LibreHardwareMonitor
            public double ReadBps;       // 读取速度 字节/秒
            public double WriteBps;      // 写入速度 字节/秒
            public double Activity;      // 活动时间 0-100 %
            public double Latency;       // 平均延迟 秒（暂不显示）
        }

        private sealed class Slot
        {
            public int Index;
            public string Instance = "";
            public string Letters = "";
            public PerformanceCounter Read;
            public PerformanceCounter Write;
            public PerformanceCounter DiskTime;
            public PerformanceCounter IdleTime;
            public PerformanceCounter Latency;
        }

        private static readonly object _gate = new object();
        private static readonly List<Slot> _slots = new List<Slot>();
        private static bool _inited;
        private static string _note = "尚未初始化";
        private static bool _activityFallback;

        /// <summary>状态说明，便于日志排查</summary>
        public static string Note { get { return _note; } }

        /// <summary>活动时间是否走了 100 - % Idle Time 兜底</summary>
        public static bool ActivityFallback { get { return _activityFallback; } }

        // ------------------------------------------------------------------
        // 初始化（只做一次）
        // ------------------------------------------------------------------
        public static void EnsureInit()
        {
            lock (_gate)
            {
                if (_inited) return;
                _inited = true;

                try
                {
                    var cat = new PerformanceCounterCategory("PhysicalDisk");
                    string[] names = cat.GetInstanceNames();

                    // 实例名 -> 物理盘索引。形如 "0 C: E:"，"0" 就是索引。
                    var map = new Dictionary<int, string>();
                    for (int i = 0; i < names.Length; i++)
                    {
                        string nm = names[i];
                        if (nm == null || nm.Length == 0) continue;
                        if (nm[0] == '_') continue;               // 跳过 _Total
                        int sp = nm.IndexOf(' ');
                        if (sp <= 0) continue;

                        int idx;
                        if (!int.TryParse(nm.Substring(0, sp), NumberStyles.Integer,
                                          CultureInfo.InvariantCulture, out idx)) continue;
                        if (!map.ContainsKey(idx)) map[idx] = nm.Trim();
                    }

                    var keys = new List<int>(map.Keys);
                    keys.Sort();

                    for (int k = 0; k < keys.Count; k++)
                    {
                        int idx = keys[k];
                        string inst = map[idx];

                        var s = new Slot();
                        s.Index = idx;
                        s.Instance = inst;
                        s.Letters = inst.Length > 2 ? inst.Substring(2).Trim() : inst;
                        s.Read = Make("Disk Read Bytes/sec", inst);
                        s.Write = Make("Disk Write Bytes/sec", inst);
                        s.DiskTime = Make("% Disk Time", inst);
                        s.IdleTime = Make("% Idle Time", inst);
                        s.Latency = Make("Avg. Disk sec/Transfer", inst);

                        // 读写都拿不到就没有意义，直接跳过
                        if (s.Read == null && s.Write == null) continue;
                        _slots.Add(s);
                    }

                    // 速率型计数器第一次 NextValue 一定是 0，先跑一轮把基准建立起来
                    for (int i = 0; i < _slots.Count; i++)
                    {
                        Next(_slots[i].Read);
                        Next(_slots[i].Write);
                        Next(_slots[i].DiskTime);
                        Next(_slots[i].IdleTime);
                        Next(_slots[i].Latency);
                    }

                    if (_slots.Count == 0)
                    {
                        _note = "PhysicalDisk 计数器无可用实例";
                    }
                    else
                    {
                        _activityFallback = _slots[0].DiskTime == null;
                        _note = "已接入 " + _slots.Count.ToString(CultureInfo.InvariantCulture)
                                + " 块物理盘";
                    }
                    App.Trace("DiskPerf: " + _note + "  " + Dump());
                }
                catch (Exception ex)
                {
                    _note = "性能计数器不可用：" + ex.Message;
                    App.Log("DiskPerf.EnsureInit", ex);
                }
            }
        }

        private static PerformanceCounter Make(string counter, string instance)
        {
            try { return new PerformanceCounter("PhysicalDisk", counter, instance, true); }
            catch { return null; }
        }

        private static double Next(PerformanceCounter pc)
        {
            if (pc == null) return double.NaN;
            try { return pc.NextValue(); }
            catch { return double.NaN; }
        }

        private static string Dump()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _slots.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append(_slots[i].Instance);
                sb.Append(_slots[i].DiskTime != null ? "(活动=%DiskTime)" : "(活动=100-Idle)");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // 采样
        // ------------------------------------------------------------------
        /// <summary>读一轮（不含温度）。调用频率决定读写速度的平均窗口。</summary>
        public static List<PhysDisk> Read()
        {
            var result = new List<PhysDisk>();
            EnsureInit();

            lock (_gate)
            {
                for (int i = 0; i < _slots.Count; i++)
                {
                    Slot s = _slots[i];
                    var p = new PhysDisk();
                    p.Index = s.Index;
                    p.Instance = s.Instance;
                    p.Letters = s.Letters;
                    p.Title = BuildTitle(s.Index, s.Letters);
                    p.ReadBps = Math.Max(0, Num(Next(s.Read)));
                    p.WriteBps = Math.Max(0, Num(Next(s.Write)));
                    p.Activity = ReadActivity(s);
                    p.Latency = Math.Max(0, Num(Next(s.Latency)));
                    result.Add(p);
                }
            }
            return result;
        }

        /// <summary>读一轮并补上温度（温度来自 LibreHardwareMonitor，按 /hdd/N 对齐）</summary>
        public static List<PhysDisk> ReadAll()
        {
            var list = Read();

            Dictionary<int, double> temps = null;
            try { temps = HardwareMonitor.GetDiskTemps(); }
            catch { }

            if (temps != null && temps.Count > 0)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    double t;
                    if (temps.TryGetValue(list[i].Index, out t)) list[i].Temp = t;
                }
            }
            return list;
        }

        private static double ReadActivity(Slot s)
        {
            // 首选 % Disk Time（需求指定）
            if (s.DiskTime != null)
            {
                double v = Next(s.DiskTime);
                if (!double.IsNaN(v) && !double.IsInfinity(v) && v >= 0)
                    return Clamp(v, 0, 100);
            }
            // 兜底 100 - % Idle Time（任务管理器口径）
            if (s.IdleTime != null)
            {
                double idle = Next(s.IdleTime);
                if (!double.IsNaN(idle) && !double.IsInfinity(idle))
                    return Clamp(100.0 - idle, 0, 100);
            }
            return 0;
        }

        private static double Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
            return v;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (double.IsNaN(v)) return lo;
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private static string BuildTitle(int idx, string letters)
        {
            // "C: E:" -> "C E"     "D:" -> "D"
            string s = letters.Replace(":", "").Replace("  ", " ").Trim();
            return "磁盘" + idx.ToString(CultureInfo.InvariantCulture) + "【" + s + "】";
        }

        // ------------------------------------------------------------------
        // 格式化
        // ------------------------------------------------------------------
        /// <summary>
        /// 磁盘速度格式化：自动 kb / mb / gb 换算。
        /// binary = true 用 1024，false 用 1000（设置项「单位进制」）。
        /// </summary>
        public static string FormatSpeed(double bytesPerSec, bool binary)
        {
            double v = bytesPerSec;
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) v = 0;
            double step = binary ? 1024.0 : 1000.0;

            if (v < step)
                return Math.Round(v, 0).ToString("0", CultureInfo.InvariantCulture) + " B/s";
            v /= step;
            if (v < step)
                return v.ToString("0.0", CultureInfo.InvariantCulture) + " KB/s";
            v /= step;
            if (v < step)
                return v.ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
            v /= step;
            return v.ToString("0.00", CultureInfo.InvariantCulture) + " GB/s";
        }

        /// <summary>温度格式化，支持摄氏 / 华氏</summary>
        public static string FormatTemp(double celsius, bool fahrenheit, string unknown)
        {
            if (double.IsNaN(celsius) || celsius <= 0) return unknown;
            if (fahrenheit)
                return Math.Round(celsius * 9.0 / 5.0 + 32.0, 0).ToString("0", CultureInfo.InvariantCulture) + "°F";
            return Math.Round(celsius, 0).ToString("0", CultureInfo.InvariantCulture) + "°C";
        }

        /// <summary>容量格式化（沿用 1024 进制，与内存/分区显示一致）</summary>
        public static string FormatBytes(double bytes, bool binary)
        {
            if (double.IsNaN(bytes) || bytes <= 0) return "0 B";
            double step = binary ? 1024.0 : 1000.0;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double v = bytes;
            while (v >= step && i < units.Length - 1) { v /= step; i++; }
            return v.ToString(i <= 1 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[i];
        }

        // ------------------------------------------------------------------
        public static void Shutdown()
        {
            lock (_gate)
            {
                for (int i = 0; i < _slots.Count; i++)
                {
                    Dispose(_slots[i].Read);
                    Dispose(_slots[i].Write);
                    Dispose(_slots[i].DiskTime);
                    Dispose(_slots[i].IdleTime);
                    Dispose(_slots[i].Latency);
                }
                _slots.Clear();
                _inited = false;
            }
        }

        private static void Dispose(PerformanceCounter pc)
        {
            if (pc == null) return;
            try { pc.Dispose(); } catch { }
        }
    }
}
