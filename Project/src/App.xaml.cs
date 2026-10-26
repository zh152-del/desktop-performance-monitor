using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace PerfMonitorWPF
{
    public partial class App : Application
    {
        private static Mutex _singleInstance;

        /// <summary>error.log 上限。超了就把它挪成 error.log.old，新日志重新开始。</summary>
        private const long MaxLogBytes = 1024 * 1024;

        /// <summary>日志写的串行锁。主线程、采集线程、自启线程都会写日志，不加锁会互相踩。</summary>
        private static readonly object _logLock = new object();

        private static string LogPath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PerfMonitorWPF");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "error.log");
            }
        }

        /// <summary>
        /// 只保留最近半份日志：长期开着面板，日志不能无限长大。
        /// </summary>
        private static void RotateLogIfTooBig(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxLogBytes) return;

                string old = path + ".old";
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                File.Move(path, old);
            }
            catch { }
        }

        private static void AppendLog(string text)
        {
            lock (_logLock)
            {
                try
                {
                    string p = LogPath;
                    RotateLogIfTooBig(p);
                    File.AppendAllText(p, text);
                }
                catch { }
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            Trace("=== 进程启动 PID=" + System.Diagnostics.Process.GetCurrentProcess().Id + " ===");
            // 单实例保护 (single instance)
            // 用会话内(Local)互斥体，避免 Global 命名空间在非提权/服务会话下的权限问题。
            bool created = true;
            try
            {
                _singleInstance = new Mutex(true, "Local\\PerfMonitorWPF_SingleInstance", out created);
            }
            catch
            {
                // 拿不到互斥体不应阻止程序启动
                created = true;
                _singleInstance = null;
            }

            if (!created)
            {
                Log("启动中止", new Exception("已有实例在运行"));
                Shutdown();
                return;
            }

            // ============================================================
            // 设置必须在这里就加载完，不能等主窗口。
            // 因为下面那个自启线程要读 AppSettings.AutoStart / FastAutoStart，
            // 如果设置还没载入，线程读到的全是默认值 —— 用户明明关掉了开机自启，
            // 下一次启动又被悄悄写回注册表。这就是典型的启动竞态。
            // ============================================================
            try { AppSettings.Load(); } catch (Exception ex) { Log("加载设置", ex); }

            // 全局异常兜底：绝不因为一个异常导致闪退
            DispatcherUnhandledException += (s, args) =>
            {
                Log("UI线程异常", args.Exception);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                Log("非UI线程异常", args.ExceptionObject as Exception);
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                Log("Task异常", args.Exception);
                args.SetObserved();
            };

            base.OnStartup(e);

            // 轻量硬件信息（纯 Win32 API + 本地缓存），毫秒级
            try { HardwareMonitor.FastInit(); } catch { }

            var swWin = System.Diagnostics.Stopwatch.StartNew();
            Trace("OnStartup: 开始创建主窗口");
            try
            {
                var win = new MainWindow();
                MainWindow = win;
                win.Show();
                Trace("OnStartup: 主窗口已显示，耗时 " + swWin.ElapsedMilliseconds + "ms" +
                      " IsVisible=" + win.IsVisible +
                      " Left=" + win.Left + " Top=" + win.Top);
            }
            catch (Exception ex)
            {
                Log("主窗口创建失败", ex);
                MessageBox.Show("面板启动失败：" + ex.Message + "\r\n\r\n" + ex.StackTrace,
                    "桌面性能助手", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }

            // 窗口出来之后再做注册表维护，一点都不挡启动路径。
            // 【必须尊重设置里的开关】—— 以前这里无条件写 Run 项，
            // 结果用户在设置里关掉开机自启，下次启动又被写回去。
            var t = new Thread(delegate ()
            {
                try
                {
                    var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;

                    if (AppSettings.AutoStart)
                    {
                        // 每次启动都重写一遍，exe 换目录后能自愈
                        if (SystemActions.SetAutoStart(exePath))
                            Trace("自启已登记: " + exePath);
                        else
                            Trace("自启登记失败（注册表不可写？）");
                    }
                    else
                    {
                        // 关掉开关就顺手清掉遗留的 Run 项，不留垃圾
                        if (SystemActions.IsAutoStartEnabled())
                        {
                            SystemActions.RemoveAutoStart();
                            Trace("自启已按设置关闭，Run 项已清除");
                        }
                    }

                    if (AppSettings.FastAutoStart)
                    {
                        // 干掉 Win10/11 对自启项默认的约 10 秒延迟
                        if (SystemActions.DisableStartupDelay())
                            Trace("已取消系统开机启动延迟 (StartupDelayInMSec=0)");
                    }
                }
                catch (Exception ex) { Trace("自启注册失败: " + ex.Message); }
            });
            t.IsBackground = true;
            t.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { HardwareMonitor.Shutdown(); } catch { }
            // 通知主窗口清理托盘图标和定时器
            try { var mw = MainWindow as MainWindow; if (mw != null) mw.Cleanup(); } catch { }
            try { if (_singleInstance != null) _singleInstance.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }

        /// <summary>诊断日志（用于排查启动问题）</summary>
        public static void Trace(string msg)
        {
            try
            {
                AppendLog(string.Format("[{0}] {1}\r\n",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), msg));
            }
            catch { }
        }

        public static void Log(string tag, Exception ex)
        {
            try
            {
                var msg = string.Format("[{0}] {1}: {2}\r\n{3}\r\n\r\n",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    tag,
                    ex == null ? "(null)" : ex.Message,
                    ex == null ? "" : ex.StackTrace);
                AppendLog(msg);
            }
            catch { }
        }
    }
}
