using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using Application = System.Windows.Application;

namespace PerfMonitorWPF
{
    /// <summary>
    /// 系统托盘图标 (System Tray Icon)
    /// 提供右键菜单：显示/隐藏面板、退出程序。
    /// 关闭窗口时缩到托盘而不是退出。
    /// </summary>
    public class TrayIcon : IDisposable
    {
        private NotifyIcon _notifyIcon;
        private Window _window;
        private bool _disposed;

        public TrayIcon(Window owner)
        {
            _window = owner;
            _notifyIcon = new NotifyIcon
            {
                Text = "桌面性能助手",
                Visible = true,
                BalloonTipTitle = "桌面性能助手",
                BalloonTipIcon = ToolTipIcon.Info
            };

            // 使用 exe 自带的图标（通过 ApplicationIcon 嵌入）
            try
            {
                var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
                    _notifyIcon.Icon = Icon.ExtractAssociatedIcon(exePath);
                else
                    _notifyIcon.Icon = SystemIcons.Application;
            }
            catch { try { _notifyIcon.Icon = SystemIcons.Application; } catch { } }

            // 右键菜单
            var menu = new ContextMenuStrip();
            var showItem = new ToolStripMenuItem("显示面板", null, OnShow);
            var hideItem = new ToolStripMenuItem("隐藏到托盘", null, OnHide);
            var sep = new ToolStripSeparator();
            var exitItem = new ToolStripMenuItem("退出", null, OnExit);
            menu.Items.AddRange(new ToolStripItem[] { showItem, hideItem, sep, exitItem });
            _notifyIcon.ContextMenuStrip = menu;

            // 双击左键 = 显示/隐藏
            _notifyIcon.DoubleClick += (s, e) => ToggleVisibility();

            // 关闭窗口时缩到托盘
            _window.Closing += OnWindowClosing;
        }

        private void OnShow(object sender, EventArgs e)
        {
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            _window.Show();
            _window.Activate();
        }

        private void OnHide(object sender, EventArgs e)
        {
            _window.Hide();
        }

        /// <summary>
        /// 打开设置。底部按钮可以整个隐藏，藏了之后面板上就没有入口了，
        /// 这里必须留一条回头路。
        /// </summary>
        private void OnSettings(object sender, EventArgs e)
        {
            try
            {
                if (_window != null && !_window.IsVisible) _window.Show();
                var mw = _window as MainWindow;
                if (mw != null) mw.OpenSettingsFromTray();
            }
            catch { }
        }

        private void OnExit(object sender, EventArgs e)
        {
            ExitApp();
        }

        /// <summary>
        /// 真正退出程序。
        /// 必须先把 Closing 事件摘掉：那个处理函数的作用是「关闭窗口 = 缩到托盘」，
        /// 不摘的话从设置面板点「退出」只会把自己藏起来。
        /// </summary>
        public void ExitApp()
        {
            try
            {
                if (_window != null) _window.Closing -= OnWindowClosing;
                Application.Current.Shutdown();
            }
            catch { }
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 不真正关闭，而是隐藏到托盘
            e.Cancel = true;
            HideToTray();
        }

        private void ToggleVisibility()
        {
            if (_window.Visibility == Visibility.Visible && _window.WindowState != WindowState.Minimized)
            {
                HideToTray();
            }
            else
            {
                OnShow(null, null);
            }
        }

        private void HideToTray()
        {
            _window.Hide();
            ShowBalloon("提示", "面板已隐藏到托盘，双击图标可恢复。");
        }

        /// <summary>显示气泡通知</summary>
        public void ShowBalloon(string title, string msg)
        {
            try
            {
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = msg;
                _notifyIcon.ShowBalloonTip(3000);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            if (_window != null)
            {
                _window.Closing -= OnWindowClosing;
            }
        }
    }
}
