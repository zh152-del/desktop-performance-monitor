using System;
using System.Windows;
using System.Windows.Media;

namespace PerfMonitorWPF
{
    /// <summary>圆环进度几何生成器 (arc geometry builder)</summary>
    public static class ArcBuilder
    {
        /// <summary>
        /// 生成一段从 12 点方向顺时针的圆弧
        /// </summary>
        /// <param name="cx">圆心 X</param>
        /// <param name="cy">圆心 Y</param>
        /// <param name="radius">半径</param>
        /// <param name="percent">0-100</param>
        public static Geometry Build(double cx, double cy, double radius, double percent)
        {
            return Build(cx, cy, radius, percent, false);
        }

        /// <summary>
        /// 生成一段从 12 点方向起画的圆弧。
        /// counterClockwise=true 时逆时针扫（卡片大环用这个方向，与光点、数字旋转方向统一）。
        /// </summary>
        public static Geometry Build(double cx, double cy, double radius, double percent, bool counterClockwise)
        {
            if (double.IsNaN(percent) || percent <= 0)
                return Geometry.Empty;

            if (percent > 100) percent = 100;

            // 99.9% 以上直接画整圆，避免起止点重合导致不显示
            if (percent >= 99.9)
            {
                var full = new EllipseGeometry(new Point(cx, cy), radius, radius);
                full.Freeze();
                return full;
            }

            double angle = percent / 100.0 * 360.0;

            // 极低占用率（<1.5°）时 ArcSegment 会退化成看不见的一个点甚至整段不显示，
            // 这里保底扫过 1.5°，配合圆头线帽就是一个小圆点，读数仍然可信
            if (angle < 1.5) angle = 1.5;

            double startRad = -Math.PI / 2;                       // 12 点方向
            double endRad = counterClockwise
                ? startRad - angle * Math.PI / 180.0
                : startRad + angle * Math.PI / 180.0;

            var start = new Point(cx + radius * Math.Cos(startRad),
                                  cy + radius * Math.Sin(startRad));
            var end = new Point(cx + radius * Math.Cos(endRad),
                                cy + radius * Math.Sin(endRad));

            var fig = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
            fig.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Size(radius, radius),
                IsLargeArc = angle > 180,
                SweepDirection = counterClockwise
                    ? SweepDirection.Counterclockwise
                    : SweepDirection.Clockwise
            });

            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            geo.Freeze();
            return geo;
        }
    }
}
