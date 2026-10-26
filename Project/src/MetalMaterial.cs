using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PerfMonitorWPF
{
    /// <summary>
    /// 深色金属材质的微表面噪声（DarkMetal v1.0 · L4）。
    ///
    /// 规范要求：极低不透明度颗粒（≤0.03），把「完美塑料感」破掉。
    /// 说明：
    ///   · 64×64 灰度噪点，一次性生成并 Freeze，全局只留一份，不随时间变化；
    ///   · 固定随机种子 —— 每次启动颗粒图案一致。若每次刷新都重新随机，
    ///     颗粒会跳动，肉眼会读成「面板在闪」，反而更廉价；
    ///   · 三通道同值（纯灰），规范禁止彩色噪点；
    ///   · 亮度收窄在 96~160，避免纯黑白对比过强显脏；
    ///   · 整体透明度由使用方的 Rectangle.Opacity 控制，默认 0.03。
    /// </summary>
    internal static class MetalMaterial
    {
        private const int NoiseSize = 64;

        /// <summary>固定种子：保证每次启动的颗粒图案一致</summary>
        private const int NoiseSeed = 20261024;

        private static ImageBrush _noiseBrush;

        /// <summary>噪声画刷（64×64 平铺）。首次访问时生成，之后复用同一个冻结实例。</summary>
        public static ImageBrush NoiseBrush
        {
            get
            {
                if (_noiseBrush == null)
                {
                    var bmp = BuildNoise();
                    var brush = new ImageBrush(bmp);
                    brush.TileMode = TileMode.Tile;
                    brush.Stretch = Stretch.None;
                    brush.ViewportUnits = BrushMappingMode.Absolute;
                    brush.Viewport = new System.Windows.Rect(0, 0, NoiseSize, NoiseSize);
                    brush.Freeze();          // 冻结：跨线程安全、渲染更快
                    _noiseBrush = brush;
                }
                return _noiseBrush;
            }
        }

        private static BitmapSource BuildNoise()
        {
            int w = NoiseSize;
            int h = NoiseSize;
            int stride = w * 4;
            var px = new byte[stride * h];

            var rnd = new Random(NoiseSeed);

            for (int i = 0; i < w * h; i++)
            {
                byte v = (byte)rnd.Next(96, 161);     // 灰度收窄，不出现死黑/死白
                int q = i * 4;
                px[q] = v;                            // B
                px[q + 1] = v;                        // G
                px[q + 2] = v;                        // R
                px[q + 3] = 255;                      // A（透明度交给 Opacity 控制）
            }

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            bmp.Freeze();
            return bmp;
        }
    }
}
