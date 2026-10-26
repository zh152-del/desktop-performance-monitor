using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PerfMonitorWPF
{
    /// <summary>
    /// 面板设置（panel settings）。
    ///
    /// 刻意用最朴素的 key=value 文本存盘：
    ///   · 不引入任何序列化 / 第三方依赖，不会因为缺 DLL 而启动失败；
    ///   · 文件坏了也只是回落到默认值，绝不会抛到 UI 线程；
    ///   · 想让用户手改也一眼看得懂。
    ///
    /// 位置：%LOCALAPPDATA%\PerfMonitorWPF\settings.ini
    /// </summary>
    public static class AppSettings
    {
        private static string DirPath
        {
            get
            {
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PerfMonitorWPF");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        public static string FilePath { get { return Path.Combine(DirPath, "settings.ini"); } }

        // ==================== 刷新与省电 ====================
        /// <summary>刷新间隔（秒）：1 / 2 / 3 / 5 / 10</summary>
        public static int RefreshSeconds = 5;
        /// <summary>省电降频：面板长时间没人碰、且负载很低时，自动把刷新间隔拉长一倍</summary>
        public static bool LowPower = true;
        /// <summary>暂停采样</summary>
        public static bool Paused = false;

        // ==================== 启动与位置 ====================
        public static bool AutoStart = true;
        /// <summary>顺手取消 Win10/11 对自启项强加的 10 秒延迟</summary>
        public static bool FastAutoStart = true;
        /// <summary>右下 / 左下 / 右上 / 左上</summary>
        public static string Corner = "右下";
        public static int EdgeX = 16;
        public static int EdgeY = 16;

        // ==================== 显示模块 ====================
        public static bool ShowRings = true;
        public static bool ShowPartitions = true;
        public static bool ShowDiskCard = true;
        public static bool ShowClipboard = true;
        public static bool ShowButtons = true;

        // ==================== 剪贴板规则 ====================
        public static int ClipMax = 4;
        public static int ClipExpireMinutes = 15;
        /// <summary>中日韩字符（含标点）截断长度</summary>
        public static int ClipTruncCjk = 20;
        /// <summary>纯字母/数字截断长度</summary>
        public static int ClipTruncLatin = 25;
        /// <summary>记录全部超时删完后，是否连 Windows 剪贴板一起清掉（默认关，避免误清用户正在用的内容）</summary>
        public static bool ClipWipeSystem = false;

        // ==================== 单位与操作 ====================
        /// <summary>true = 1024 进制（KB=1024B），false = 1000 进制</summary>
        public static bool BinaryUnits = true;
        public static bool Fahrenheit = false;
        /// <summary>关机 / 重启前二次确认</summary>
        public static bool ConfirmDanger = true;

        // ==================================================================
        public static void Load()
        {
            try
            {
                string p = FilePath;
                if (!File.Exists(p)) return;

                string[] lines = File.ReadAllLines(p, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string ln = lines[i];
                    if (ln == null) continue;
                    ln = ln.Trim();
                    if (ln.Length == 0 || ln[0] == '#' || ln[0] == ';') continue;

                    int eq = ln.IndexOf('=');
                    if (eq <= 0) continue;

                    string k = ln.Substring(0, eq).Trim();
                    string v = ln.Substring(eq + 1).Trim();
                    Apply(k, v);
                }
                App.Trace("AppSettings.Load: 已从 " + p + " 载入");
            }
            catch (Exception ex)
            {
                App.Log("AppSettings.Load", ex);
            }
            Normalize();
        }

        private static void Apply(string k, string v)
        {
            switch (k)
            {
                case "RefreshSeconds": RefreshSeconds = ToInt(v, RefreshSeconds); break;
                case "LowPower": LowPower = ToBool(v, LowPower); break;
                case "Paused": Paused = ToBool(v, Paused); break;

                case "AutoStart": AutoStart = ToBool(v, AutoStart); break;
                case "FastAutoStart": FastAutoStart = ToBool(v, FastAutoStart); break;
                case "Corner": Corner = v; break;
                case "EdgeX": EdgeX = ToInt(v, EdgeX); break;
                case "EdgeY": EdgeY = ToInt(v, EdgeY); break;

                case "ShowRings": ShowRings = ToBool(v, ShowRings); break;
                case "ShowPartitions": ShowPartitions = ToBool(v, ShowPartitions); break;
                case "ShowDiskCard": ShowDiskCard = ToBool(v, ShowDiskCard); break;
                case "ShowClipboard": ShowClipboard = ToBool(v, ShowClipboard); break;
                case "ShowButtons": ShowButtons = ToBool(v, ShowButtons); break;

                case "ClipMax": ClipMax = ToInt(v, ClipMax); break;
                case "ClipExpireMinutes": ClipExpireMinutes = ToInt(v, ClipExpireMinutes); break;
                case "ClipTruncCjk": ClipTruncCjk = ToInt(v, ClipTruncCjk); break;
                case "ClipTruncLatin": ClipTruncLatin = ToInt(v, ClipTruncLatin); break;

                case "BinaryUnits": BinaryUnits = ToBool(v, BinaryUnits); break;
                case "Fahrenheit": Fahrenheit = ToBool(v, Fahrenheit); break;
                case "ConfirmDanger": ConfirmDanger = ToBool(v, ConfirmDanger); break;
            }
        }

        public static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("# 桌面性能助手 设置文件 —— 可以直接改，改完重启面板生效\r\n");
                sb.Append("# 生成时间 ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\r\n\r\n");

                sb.Append("# ---- 刷新与省电 ----\r\n");
                sb.Append("RefreshSeconds=").Append(RefreshSeconds).Append("\r\n");
                sb.Append("LowPower=").Append(LowPower).Append("\r\n");
                sb.Append("Paused=").Append(Paused).Append("\r\n\r\n");

                sb.Append("# ---- 启动与位置 ----\r\n");
                sb.Append("AutoStart=").Append(AutoStart).Append("\r\n");
                sb.Append("FastAutoStart=").Append(FastAutoStart).Append("\r\n");
                sb.Append("Corner=").Append(Corner).Append("\r\n");
                sb.Append("EdgeX=").Append(EdgeX).Append("\r\n");
                sb.Append("EdgeY=").Append(EdgeY).Append("\r\n\r\n");

                sb.Append("# ---- 显示模块 ----\r\n");
                sb.Append("ShowRings=").Append(ShowRings).Append("\r\n");
                sb.Append("ShowPartitions=").Append(ShowPartitions).Append("\r\n");
                sb.Append("ShowDiskCard=").Append(ShowDiskCard).Append("\r\n");
                sb.Append("ShowClipboard=").Append(ShowClipboard).Append("\r\n");
                sb.Append("ShowButtons=").Append(ShowButtons).Append("\r\n\r\n");

                sb.Append("# ---- 剪贴板规则 ----\r\n");
                sb.Append("ClipMax=").Append(ClipMax).Append("\r\n");
                sb.Append("ClipExpireMinutes=").Append(ClipExpireMinutes).Append("\r\n");
                sb.Append("ClipTruncCjk=").Append(ClipTruncCjk).Append("\r\n");
                sb.Append("ClipTruncLatin=").Append(ClipTruncLatin).Append("\r\n");
                sb.Append("ClipWipeSystem=").Append(ClipWipeSystem).Append("\r\n\r\n");

                sb.Append("# ---- 单位与操作 ----\r\n");
                sb.Append("BinaryUnits=").Append(BinaryUnits).Append("\r\n");
                sb.Append("Fahrenheit=").Append(Fahrenheit).Append("\r\n");
                sb.Append("ConfirmDanger=").Append(ConfirmDanger).Append("\r\n");

                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
                App.Trace("AppSettings.Save: 已写入 " + FilePath);
            }
            catch (Exception ex)
            {
                App.Log("AppSettings.Save", ex);
            }
        }

        /// <summary>恢复出厂默认（内存中），需要再调 Save 才落盘</summary>
        public static void ResetToDefault()
        {
            RefreshSeconds = 5;
            LowPower = true;
            Paused = false;

            AutoStart = true;
            FastAutoStart = true;
            Corner = "右下";
            EdgeX = 16;
            EdgeY = 16;

            ShowRings = true;
            ShowPartitions = true;
            ShowDiskCard = true;
            ShowClipboard = true;
            ShowButtons = true;

            ClipMax = 4;
            ClipExpireMinutes = 15;
            ClipTruncCjk = 20;
            ClipTruncLatin = 25;

            BinaryUnits = true;
            Fahrenheit = false;
            ConfirmDanger = true;
        }

        /// <summary>把可能被手改坏的值拉回合法范围</summary>
        private static void Normalize()
        {
            if (RefreshSeconds < 1) RefreshSeconds = 1;
            if (RefreshSeconds > 60) RefreshSeconds = 60;

            if (Corner != "右下" && Corner != "左下" && Corner != "右上" && Corner != "左上")
                Corner = "右下";
            if (EdgeX < 0) EdgeX = 0;
            if (EdgeX > 400) EdgeX = 400;
            if (EdgeY < 0) EdgeY = 0;
            if (EdgeY > 400) EdgeY = 400;

            // 面板只有 4 行记录位，上限绝不能超过它 ——
            // 超出的记录界面上看不见，却还会参与去重判断、占着内存
            if (ClipMax < 1) ClipMax = 1;
            if (ClipMax > 4) ClipMax = 4;
            if (ClipExpireMinutes < 1) ClipExpireMinutes = 1;
            if (ClipExpireMinutes > 720) ClipExpireMinutes = 720;
            if (ClipTruncCjk < 4) ClipTruncCjk = 4;
            if (ClipTruncCjk > 200) ClipTruncCjk = 200;
            if (ClipTruncLatin < 4) ClipTruncLatin = 4;
            if (ClipTruncLatin > 200) ClipTruncLatin = 200;
        }

        private static int ToInt(string v, int fallback)
        {
            int r;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r;
            return fallback;
        }

        private static bool ToBool(string v, bool fallback)
        {
            if (v == null) return fallback;
            string s = v.Trim().ToLowerInvariant();
            if (s == "1" || s == "true" || s == "yes" || s == "on" || s == "是" || s == "开") return true;
            if (s == "0" || s == "false" || s == "no" || s == "off" || s == "否" || s == "关") return false;
            return fallback;
        }
    }
}
