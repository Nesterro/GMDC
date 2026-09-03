using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GMDC.Helpers
{
    public static class IconGenerator
    {
        private static readonly SolidColorBrush GreenBrush = new SolidColorBrush(Color.FromRgb(59, 183, 0)); // #3bb700
        private static readonly SolidColorBrush WhiteBrush = Brushes.White;
        private static readonly SolidColorBrush BlackBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        
        static IconGenerator()
        {
            GreenBrush.Freeze();
            BlackBrush.Freeze();
        }

        public static BitmapSource CreateExcelIcon(int size)
        {
            var rt = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            double scale = size / 32.0;

            using (var dc = dv.RenderOpen())
            {
                // Background shadow/border for visibility on dark/light themes
                var borderPen = new Pen(BlackBrush, 2.5 * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var greenPen = new Pen(GreenBrush, 2.0 * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var whitePen = new Pen(WhiteBrush, 1.5 * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

                borderPen.Freeze();
                greenPen.Freeze();
                whitePen.Freeze();

                // Draw Spreadsheet body
                Rect sheetRect = new Rect(3 * scale, 8 * scale, 20 * scale, 20 * scale);
                dc.DrawRoundedRectangle(null, borderPen, sheetRect, 3 * scale, 3 * scale);
                dc.DrawRoundedRectangle(GreenBrush, null, sheetRect, 3 * scale, 3 * scale);

                // Grid lines inside Excel sheet (white lines)
                dc.DrawLine(whitePen, new Point(3 * scale, 14 * scale), new Point(23 * scale, 14 * scale));
                dc.DrawLine(whitePen, new Point(3 * scale, 21 * scale), new Point(23 * scale, 21 * scale));
                dc.DrawLine(whitePen, new Point(10 * scale, 8 * scale), new Point(10 * scale, 28 * scale));
                dc.DrawLine(whitePen, new Point(17 * scale, 8 * scale), new Point(17 * scale, 28 * scale));

                // Export Arrow (Black border for contrast, White body, Green accent)
                Point arrowStart = new Point(12 * scale, 20 * scale);
                Point arrowEnd = new Point(28 * scale, 4 * scale);
                
                // Draw Arrow shaft (with black outline)
                var arrowShaftPen = new Pen(BlackBrush, 4.0 * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(arrowShaftPen, arrowStart, arrowEnd);
                dc.DrawLine(whitePen, arrowStart, arrowEnd);

                // Draw Arrow head (with black outline)
                StreamGeometry headGeom = new StreamGeometry();
                using (StreamGeometryContext context = headGeom.Open())
                {
                    context.BeginFigure(new Point(20 * scale, 4 * scale), true, true);
                    context.LineTo(arrowEnd, true, false);
                    context.LineTo(new Point(28 * scale, 12 * scale), true, false);
                }
                headGeom.Freeze();
                
                var headBorderPen = new Pen(BlackBrush, 3.0 * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawGeometry(null, headBorderPen, headGeom);
                dc.DrawGeometry(WhiteBrush, null, headGeom);
            }

            rt.Render(dv);
            rt.Freeze();
            return rt;
        }

        public static BitmapSource CreateNamingIcon(int size)
        {
            var rt = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            double scale = size / 32.0;

            using (var dc = dv.RenderOpen())
            {
                var borderPen = new Pen(BlackBrush, 2.5 * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var whitePen = new Pen(WhiteBrush, 2.0 * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                
                borderPen.Freeze();
                whitePen.Freeze();

                // Draw Cable Tray background (vertical ladder)
                // Left & Right Rails
                dc.DrawLine(borderPen, new Point(6 * scale, 2 * scale), new Point(6 * scale, 30 * scale));
                dc.DrawLine(borderPen, new Point(18 * scale, 2 * scale), new Point(18 * scale, 30 * scale));
                dc.DrawLine(whitePen, new Point(6 * scale, 2 * scale), new Point(6 * scale, 30 * scale));
                dc.DrawLine(whitePen, new Point(18 * scale, 2 * scale), new Point(18 * scale, 30 * scale));

                // Rungs
                for (int y = 6; y <= 26; y += 6)
                {
                    dc.DrawLine(borderPen, new Point(6 * scale, y * scale), new Point(18 * scale, y * scale));
                    dc.DrawLine(whitePen, new Point(6 * scale, y * scale), new Point(18 * scale, y * scale));
                }

                // Naming Tag overlay (rounded box on bottom right)
                Rect tagRect = new Rect(14 * scale, 12 * scale, 15 * scale, 15 * scale);
                dc.DrawRoundedRectangle(null, borderPen, tagRect, 3 * scale, 3 * scale);
                dc.DrawRoundedRectangle(GreenBrush, null, tagRect, 3 * scale, 3 * scale);

                // Draw "A" letter inside the green tag
                // Left leg, Right leg, Cross bar of "A" in white
                var textPen = new Pen(WhiteBrush, 1.5 * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                textPen.Freeze();
                dc.DrawLine(textPen, new Point(21.5 * scale, 15 * scale), new Point(17.5 * scale, 24 * scale));
                dc.DrawLine(textPen, new Point(21.5 * scale, 15 * scale), new Point(25.5 * scale, 24 * scale));
                dc.DrawLine(textPen, new Point(19.0 * scale, 21 * scale), new Point(24.0 * scale, 21 * scale));
            }

            rt.Render(dv);
            rt.Freeze();
            return rt;
        }

        public static BitmapSource CreateHangerIcon(int size)
        {
            var rt = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            double scale = size / 32.0;

            using (var dc = dv.RenderOpen())
            {
                var borderPen = new Pen(BlackBrush, 2.5 * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var whitePen = new Pen(WhiteBrush, 2.0 * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                
                borderPen.Freeze();
                whitePen.Freeze();

                // 1. Draw Cable Tray profile (rectangular duct cross-section)
                Rect trayRect = new Rect(8 * scale, 6 * scale, 16 * scale, 10 * scale);
                dc.DrawRectangle(null, borderPen, trayRect);
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(50, 50, 50)), null, trayRect); // dark fill for tray

                // 2. Draw Hanger Support System
                // Horizontal support beam under the tray
                Point beamStart = new Point(4 * scale, 18 * scale);
                Point beamEnd = new Point(28 * scale, 18 * scale);
                dc.DrawLine(borderPen, beamStart, beamEnd);
                dc.DrawLine(whitePen, beamStart, beamEnd);

                // Left & Right threaded rods (vertical lines going up to ceiling)
                Point leftRodStart = new Point(6 * scale, 18 * scale);
                Point leftRodEnd = new Point(6 * scale, 2 * scale);
                dc.DrawLine(borderPen, leftRodStart, leftRodEnd);
                dc.DrawLine(whitePen, leftRodStart, leftRodEnd);

                Point rightRodStart = new Point(26 * scale, 18 * scale);
                Point rightRodEnd = new Point(26 * scale, 2 * scale);
                dc.DrawLine(borderPen, rightRodStart, rightRodEnd);
                dc.DrawLine(whitePen, rightRodStart, rightRodEnd);

                // 3. Draw Fasteners / Bolts (Green #3bb700 circles)
                // Bottom bolts under the beam
                dc.DrawEllipse(GreenBrush, null, new Point(6 * scale, 18 * scale), 2.0 * scale, 2.0 * scale);
                dc.DrawEllipse(GreenBrush, null, new Point(26 * scale, 18 * scale), 2.0 * scale, 2.0 * scale);
                
                // Top mounting bolts
                dc.DrawEllipse(GreenBrush, null, new Point(6 * scale, 4 * scale), 1.5 * scale, 1.5 * scale);
                dc.DrawEllipse(GreenBrush, null, new Point(26 * scale, 4 * scale), 1.5 * scale, 1.5 * scale);
            }

            rt.Render(dv);
            rt.Freeze();
            return rt;
        }

        public static BitmapSource CreateCableIcon(int size)
        {
            var rt = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            double sc = size / 32.0;

            using (var dc = dv.RenderOpen())
            {
                var borderPen = new Pen(BlackBrush, 2.5 * sc) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var greenPen  = new Pen(GreenBrush,  2.5 * sc) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var whitePen  = new Pen(WhiteBrush,  1.8 * sc) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                borderPen.Freeze(); greenPen.Freeze(); whitePen.Freeze();

                // Cable tray (horizontal rectangle)
                Rect tray = new Rect(2 * sc, 12 * sc, 28 * sc, 7 * sc);
                dc.DrawRoundedRectangle(
                    new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 40, 40)),
                    borderPen, tray, 2 * sc, 2 * sc);

                // Cable wire inside tray (green Bezier wave)
                StreamGeometry wire = new StreamGeometry();
                using (var ctx = wire.Open())
                {
                    ctx.BeginFigure(new Point(4 * sc, 15.5 * sc), false, false);
                    ctx.BezierTo(
                        new Point(10 * sc, 10  * sc),
                        new Point(14 * sc, 21  * sc),
                        new Point(20 * sc, 15.5 * sc), true, false);
                    ctx.BezierTo(
                        new Point(23 * sc, 12  * sc),
                        new Point(26 * sc, 15.5 * sc),
                        new Point(28 * sc, 15.5 * sc), true, false);
                }
                wire.Freeze();
                dc.DrawGeometry(null,
                    new Pen(BlackBrush, 3.5 * sc) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                    wire);
                dc.DrawGeometry(null, greenPen, wire);

                // Endpoint dots
                dc.DrawEllipse(WhiteBrush, borderPen, new Point(4  * sc, 15.5 * sc), 2.5 * sc, 2.5 * sc);
                dc.DrawEllipse(WhiteBrush, borderPen, new Point(28 * sc, 15.5 * sc), 2.5 * sc, 2.5 * sc);

                // Arrow top-right corner
                dc.DrawLine(borderPen, new Point(25 * sc, 6  * sc), new Point(30 * sc, 10 * sc));
                dc.DrawLine(whitePen,  new Point(25 * sc, 6  * sc), new Point(30 * sc, 10 * sc));
                dc.DrawLine(borderPen, new Point(30 * sc, 10 * sc), new Point(26 * sc, 10 * sc));
                dc.DrawLine(whitePen,  new Point(30 * sc, 10 * sc), new Point(26 * sc, 10 * sc));
                dc.DrawLine(borderPen, new Point(30 * sc, 10 * sc), new Point(30 * sc, 6  * sc));
                dc.DrawLine(whitePen,  new Point(30 * sc, 10 * sc), new Point(30 * sc, 6  * sc));
            }

            rt.Render(dv);
            rt.Freeze();
            return rt;
        }
    }
}
