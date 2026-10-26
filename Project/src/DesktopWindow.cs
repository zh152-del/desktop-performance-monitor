using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PerfMonitorWPF
{
    /// <summary>
    /// 桌面级窗口 (desktop-level window)
    ///
    /// 目标：面板常驻桌面右下角，永远不遮挡任何其它程序窗口，
    /// 同时按钮仍然可以正常点击。
    ///
    /// 做法：
    ///   1. 去掉系统菜单 / 最小化 / 最大化，禁止拖动（XAML 里已无拖动逻辑）
    ///   2. 设为 ToolWindow：不出现在任务栏和 Alt+Tab
    ///   3. 每次被激活或有窗口置前时，主动把自己压回 Z 序最底层
    ///
    /// 不使用 SetParent 挂到 WorkerW —— 那会让 WPF 变成子窗口，
    /// 导致坐标错乱、鼠标事件失效、多显示器异常。
    /// </summary>
    public static class DesktopWindow
    {
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;

        private const int WS_SYSMENU = 0x00080000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;

        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        // 窗口消息
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_ACTIVATE = 0x0006;
        private const int WM_SETFOCUS = 0x0007;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        /// <summary>把窗口固定到桌面层 (pin to desktop layer)</summary>
        public static void PinToDesktop(Window win)
        {
            if (win == null) return;

            IntPtr hwnd;
            try { hwnd = new WindowInteropHelper(win).Handle; }
            catch { return; }
            if (hwnd == IntPtr.Zero) return;

            // 1) 去掉系统菜单 / 最小化 / 最大化按钮
            try
            {
                int style = GetWindowLong(hwnd, GWL_STYLE);
                style &= ~WS_SYSMENU;
                style &= ~WS_MINIMIZEBOX;
                style &= ~WS_MAXIMIZEBOX;
                SetWindowLong(hwnd, GWL_STYLE, style);
            }
            catch { }

            // 2) 工具窗口：不出现在任务栏 / Alt+Tab（按钮仍可点击）
            try
            {
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                ex |= WS_EX_TOOLWINDOW;
                SetWindowLong(hwnd, GWL_EXSTYLE, ex);
            }
            catch { }

            // 3) 压到 Z 序最底
            SinkToBottom(hwnd);

            // 4) 挂钩：任何试图把本窗口提到前面的行为，都改回最底层
            try
            {
                var src = HwndSource.FromHwnd(hwnd);
                if (src != null) src.AddHook(WndProc);
            }
            catch { }
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                if (msg == WM_WINDOWPOSCHANGING)
                {
                    var wp = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
                    // 强制始终插在最底层
                    wp.hwndInsertAfter = HWND_BOTTOM;
                    wp.flags |= SWP_NOACTIVATE;
                    Marshal.StructureToPtr(wp, lParam, false);
                }
                else if (msg == WM_ACTIVATE || msg == WM_SETFOCUS)
                {
                    SinkToBottom(hwnd);
                }
            }
            catch { }

            return IntPtr.Zero;
        }

        private static void SinkToBottom(IntPtr hwnd)
        {
            try
            {
                SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
        }
    }
}
