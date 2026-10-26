using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PerfMonitorWPF
{
    /// <summary>
    /// 按「卷」判断磁盘此刻是否在读写。
    ///
    /// 为什么不用 DiskPerf 的物理盘数据：物理盘计数器（PhysicalDisk，"0 C: E:"）把同一块盘上的
    /// 所有分区算在一起，C 和 E 如果在一块 SSD 上会一起亮，那就不叫「当前磁盘在运行」了。
    /// 这里改用 LogicalDisk 计数器，实例就是盘符（"C:" / "D:"），读写各自独立。
    ///
    /// 只回答一个问题：这个盘符在最近一次采样窗口内有没有动过。
    /// 计数器初始化失败（极少数精简系统缺 LogicalDisk 分类）时一律返回 false，
    /// 光点不显示，功能降级但不报错、不崩溃。
    /// </summary>
    internal static class VolumeIo
    {
        /// <summary>判定阈值：读写速率合计超过这个值就算「在运行」。16 KB/s 能滤掉计数器噪声，又不漏掉轻量读写。</summary>
        private const double BusyBps = 16384.0;

        private sealed class Slot
        {
            public PerformanceCounter Read;
            public PerformanceCounter Write;
            public bool LastBusy;      // 上次结论
            public double LastSec;     // 上次真正读计数器的时刻
        }

        private static readonly Dictionary<string, Slot> _slots = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static bool _failed;

        /// <summary>某个盘符（"C:"）此刻是否在读写。</summary>
        public static bool IsBusy(string mount)
        {
            if (_failed || string.IsNullOrEmpty(mount)) return false;

            Slot s = GetSlot(mount);
            if (s == null) return false;

            // 0.8s 内重复调用直接返回上次结论：
            // 速率计数器两次 NextValue 间隔太近会算成 0，会造成光点闪断
            double now = _clock.Elapsed.TotalSeconds;
            if (s.LastSec > 0.0 && now - s.LastSec < 0.8) return s.LastBusy;

            try
            {
                double r = s.Read.NextValue();
                double w = s.Write.NextValue();
                if (r < 0) r = 0;
                if (w < 0) w = 0;
                s.LastBusy = (r + w) > BusyBps;
                s.LastSec = now;
                return s.LastBusy;
            }
            catch
            {
                // 单个盘出错（比如盘刚被拔出）不算整体失败，下次再试
                return false;
            }
        }

        private static Slot GetSlot(string mount)
        {
            Slot s;
            if (_slots.TryGetValue(mount, out s)) return s;

            try
            {
                s = new Slot();
                s.Read = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", mount, true);
                s.Write = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", mount, true);
                // 首次读一次把计数器的基线建好，避免第一次 NextValue 恒为 0
                try { s.Read.NextValue(); s.Write.NextValue(); } catch { }
                _slots[mount] = s;
                return s;
            }
            catch
            {
                // 分类不存在或实例不存在：整类禁用，不再反复尝试
                _failed = true;
                try { App.Trace("VolumeIo: LogicalDisk 计数器不可用，磁盘运行指示灯停用"); } catch { }
                return null;
            }
        }
    }
}
