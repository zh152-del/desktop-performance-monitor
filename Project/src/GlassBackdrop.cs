using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PerfMonitorWPF
{
    /// <summary>
    /// 静态毛玻璃背景（glass backdrop）
    ///
    /// 为什么不用 Win10 原生 Acrylic：
    ///   原生方案是 SetWindowCompositionAttribute + WCA_ACCENT_POLICY
    ///   （参考 riverar/sample-win32-acrylicblur、BlurWindow、FluentWPF，
    ///    三者的根实现完全一致），但它只对【非 layered】窗口生效。
    ///   本项目的窗口是 WPF AllowsTransparency=True 的无边框透明窗，
    ///   HWND 带 WS_EX_LAYERED，DWM 不会在它后面画背景模糊 ——
    ///   这是上述三个项目共同踩过的坑（社区大量 issue 证实）。
    ///   强行去掉 AllowsTransparency 又会毁掉圆角 / 透明 / 置底 / 观察模式，
    ///   属于「为了视觉效果破坏功能」，本规格明令禁止。
    ///
    /// 所以采用规格允许的回退方案：
    ///   启动后（以及窗口移动后）把窗口身后的桌面【一次性】截下来，
    ///   缩到 1/8 做一次 3x3 均值模糊，再双线性放大铺满窗口，
    ///   上面压一层半透明深色渐变。之后任何数据刷新都不再重算背景。
    ///
    /// 硬性保证：
    ///   - 没有定时器、没有后台循环、没有 RenderTargetBitmap 持续截屏
    ///   - CPU/GPU/内存数据刷新与背景完全解耦
    ///   - 截屏失败时返回 null，调用方退回不透明深色，绝不出黑块
    /// </summary>
    public static class GlassBackdrop
    {
        // ---- GDI ----
        private const int SRCCOPY = 0x00CC0020;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
            IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);

        /// <summary>模糊降采样倍数：1/8 已经足够柔，再小会糊掉壁纸细节变成一团色块</summary>
        private const int Downscale = 8;

        /// <summary>
        /// 底图压暗系数。玻璃图上还要压一层 91~95% 的近黑 tint，
        /// 不预先压暗的话，白色壁纸会把浅色文字的对比度吃掉。
        /// 压暗 45% 后：纯白桌面透出来约 115 灰，再被 tint 吃掉九成，
        /// 最终面板最亮也只有约 30 灰（近纯黑），桌面明暗变化仍能极淡地感觉到。
        /// </summary>
        private const double Darken = 0.45;

        /// <summary>
        /// 截取窗口身后的桌面区域，返回一张模糊后的小位图（调用方拉伸铺满即可）。
        /// 失败返回 null。
        /// </summary>
        /// <param name="hideWindow">
        /// 截屏前是否临时隐藏窗口。GetDC(NULL) 取到的是合成后的整屏，
        /// 不隐藏就会把面板自己拍进背景，移动面板后能看到明显的重影。
        /// 开机首次截屏时窗口还没渲染出来，不存在自拍，传 false。
        /// </param>
        public static BitmapSource Capture(Window win, bool hideWindow)
        {
            if (win == null) return null;

            bool hid = false;
            try
            {
                if (!win.IsVisible) return null;
                if (win.WindowState == WindowState.Minimized) return null;

                // 窗口矩形（DIP）→ 物理像素（GDI 用的是设备像素）
                // 注意：这些量要在隐藏窗口【之前】取，隐藏后不依赖可视树
                var src = PresentationSource.FromVisual(win);
                if (src == null) return null;
                Matrix toDevice = src.CompositionTarget.TransformToDevice;

                double wDip = win.ActualWidth;
                double hDip = win.ActualHeight;
                if (wDip < 8 || hDip < 8) return null;

                int wPx = (int)Math.Ceiling(wDip * toDevice.M11);
                int hPx = (int)Math.Ceiling(hDip * toDevice.M22);
                if (wPx < 8 || hPx < 8) return null;

                // Left/Top 就是窗口左上角的物理像素坐标（WPF 已按 DPI 换算好）
                int xPx = (int)Math.Round(win.Left * toDevice.M11);
                int yPx = (int)Math.Round(win.Top * toDevice.M22);

                int sw = Math.Max(2, wPx / Downscale);
                int sh = Math.Max(2, hPx / Downscale);

                if (hideWindow)
                {
                    win.Visibility = Visibility.Hidden;
                    hid = true;
                }

                IntPtr hdcScreen = GetDC(IntPtr.Zero);
                if (hdcScreen == IntPtr.Zero) return null;

                IntPtr hdcMem = IntPtr.Zero;
                IntPtr hBmp = IntPtr.Zero;
                IntPtr hOld = IntPtr.Zero;
                Bitmap small = null;

                try
                {
                    hdcMem = CreateCompatibleDC(hdcScreen);
                    if (hdcMem == IntPtr.Zero) return null;

                    hBmp = CreateCompatibleBitmap(hdcScreen, sw, sh);
                    if (hBmp == IntPtr.Zero) return null;

                    hOld = SelectObject(hdcMem, hBmp);

                    // 直接从屏幕 DC 拉伸到 1/8 小图：GDI 的拉伸采样本身就是一层廉价模糊
                    if (!StretchBlt(hdcMem, 0, 0, sw, sh, hdcScreen, xPx, yPx, wPx, hPx, SRCCOPY))
                        return null;

                    small = Image.FromHbitmap(hBmp);
                    BoxBlur3x3(small);

                    IntPtr hbmpSource = small.GetHbitmap();
                    try
                    {
                        BitmapSource frame = Imaging.CreateBitmapSourceFromHBitmap(
                            hbmpSource, IntPtr.Zero, Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        frame.Freeze();          // 冻结后可跨线程访问，也省一份包装
                        return frame;
                    }
                    finally
                    {
                        DeleteObject(hbmpSource);
                    }
                }
                finally
                {
                    if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero) SelectObject(hdcMem, hOld);
                    if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                    if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
                    if (hdcScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcScreen);
                    if (small != null) small.Dispose();
                }
            }
            catch
            {
                return null;      // 任何失败都让调用方走不透明回退
            }
            finally
            {
                if (hid)
                {
                    try { win.Visibility = Visibility.Visible; }
                    catch { }
                }
            }
        }

        /// <summary>3x3 均值模糊。小图只有几千像素，一次遍历，开销可忽略。</summary>
        private static void BoxBlur3x3(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            if (w < 3 || h < 3) return;

            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                int stride = data.Stride;
                IntPtr scan0 = data.Scan0;
                byte[] src = new byte[stride * h];
                Marshal.Copy(scan0, src, 0, src.Length);

                byte[] dst = new byte[stride * h];

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int r = 0, g = 0, b = 0, n = 0;

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int yy = y + dy;
                            if (yy < 0 || yy >= h) continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = x + dx;
                                if (xx < 0 || xx >= w) continue;
                                int p = yy * stride + xx * 4;
                                b += src[p];
                                g += src[p + 1];
                                r += src[p + 2];
                                n++;
                            }
                        }

                        int q = y * stride + x * 4;
                        // 模糊的同时压暗（一次遍历搞定，不额外增加开销）
                        dst[q] = (byte)(b * Darken / n);
                        dst[q + 1] = (byte)(g * Darken / n);
                        dst[q + 2] = (byte)(r * Darken / n);
                        dst[q + 3] = 255;
                    }
                }

                Marshal.Copy(dst, 0, scan0, dst.Length);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
    }
}
