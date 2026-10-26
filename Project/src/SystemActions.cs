using System;
using System.Diagnostics;
using Microsoft.Win32;
using System.Windows;

namespace PerfMonitorWPF
{
    /// <summary>系统操作 (System Actions)：此电脑 / 电源 / 剪贴板 / 开机自启</summary>
    public static class SystemActions
    {
        private const string AutoStartKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AutoStartName = "PerfMonitorWPF";

        /// <summary>打开「此电脑」(This PC)，而不是某个盘符</summary>
        public static bool OpenThisPC()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "shell:MyComputerFolder",
                    UseShellExecute = true
                });
                return true;
            }
            catch { }

            try
            {
                Process.Start("explorer.exe", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
                return true;
            }
            catch { return false; }
        }

        /// <summary>关机 (Shutdown)</summary>
        public static bool Shutdown()
        {
            return RunShutdown("/s /t 0 /f");
        }

        /// <summary>重启 (Restart)</summary>
        public static bool Restart()
        {
            return RunShutdown("/r /t 0 /f");
        }

        private static bool RunShutdown(string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);
                return true;
            }
            catch { return false; }
        }

        /// <summary>读取剪贴板文本 (read clipboard text)，失败返回空串</summary>
        public static string ReadClipboard()
        {
            string result = "";
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        result = Clipboard.GetText() ?? "";
                        break;
                    }
                    if (Clipboard.ContainsFileDropList())
                    {
                        var files = Clipboard.GetFileDropList();
                        var arr = new string[files.Count];
                        files.CopyTo(arr, 0);
                        result = "[文件] " + string.Join("; ", arr);
                        break;
                    }
                    if (Clipboard.ContainsImage())
                    {
                        result = "[图片内容]";
                        break;
                    }
                    break; // 剪贴板为空
                }
                catch
                {
                    System.Threading.Thread.Sleep(60);
                }
            }
            return result ?? "";
        }

        /// <summary>写入剪贴板文本 (write clipboard text)，成功返回 true</summary>
        public static bool WriteClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    // 第二个参数 true = 保留到剪贴板，程序退出后内容依然可用
                    Clipboard.SetDataObject(text, true);
                    return true;
                }
                catch { System.Threading.Thread.Sleep(60); }
            }
            return false;
        }

        /// <summary>清空剪贴板 (clear clipboard)</summary>
        public static bool ClearClipboard()
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Clipboard.Clear();
                    return true;
                }
                catch { System.Threading.Thread.Sleep(60); }
            }
            return false;
        }

        // ==================== 开机自启（注册表方式）====================

        /// <summary>设置开机自启（写入注册表 Run 键，不经过 VBS，避免被安全软件拦截）</summary>
        public static bool SetAutoStart(string exePath)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(AutoStartKey, true))
                {
                    if (key != null)
                    {
                        key.SetValue(AutoStartName, "\"" + exePath + "\"");
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>取消开机自启</summary>
        public static bool RemoveAutoStart()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(AutoStartKey, true))
                {
                    if (key != null && key.GetValue(AutoStartName) != null)
                    {
                        key.DeleteValue(AutoStartName);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 取消 Windows 对开机自启项强加的延迟。
        ///
        /// Win10/11 默认会让 HKCU\Run 里的程序在桌面加载完后再等一段时间（通常 10 秒）才启动，
        /// 这就是"明明开机了，面板过好久才冒出来"的系统层原因。
        /// 把 Explorer\Serialize\StartupDelayInMSec 设成 0 即可跟随桌面立即启动。
        /// 只影响当前用户，不动任何系统服务。
        /// </summary>
        public static bool DisableStartupDelay()
        {
            try
            {
                const string path = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize";
                using (var key = Registry.CurrentUser.CreateSubKey(path))
                {
                    if (key == null) return false;
                    var cur = key.GetValue("StartupDelayInMSec");
                    if (cur is int && (int)cur == 0) return true;   // 已经是 0，不重复写
                    key.SetValue("StartupDelayInMSec", 0, RegistryValueKind.DWord);
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>是否已设置开机自启</summary>
        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(AutoStartKey, false))
                {
                    return key != null && key.GetValue(AutoStartName) != null;
                }
            }
            catch { return false; }
        }
    }
}
