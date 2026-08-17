using System.Drawing;
using System.Drawing.Drawing2D;
using VideoOS.Platform.UI.Controls;
using Media = System.Windows.Media;

namespace TimeProfileEditor
{
    /// <summary>
    /// Builds the plugin icon in code rather than embedding an asset, so the deployed plugin stays
    /// a single DLL plus plugin.def with no resource to lose track of at deploy time.
    /// </summary>
    internal static class IconFactory
    {
        // A clock face drawn as one filled path in a 24x24 box. The ring is an outer circle with an
        // inner circle punched out by the even-odd rule, and the two hands are kept from overlapping
        // so that same rule does not punch a hole where they would cross.
        private const string ClockGeometry =
            "M 2,12 A 10,10 0 1 1 22,12 A 10,10 0 1 1 2,12 Z " +
            "M 3.7,12 A 8.3,8.3 0 1 0 20.3,12 A 8.3,8.3 0 1 0 3.7,12 Z " +
            "M 11.15,5.8 H 12.85 V 12.85 H 11.15 Z " +
            "M 12.85,11.15 H 17.2 V 12.85 H 12.85 Z";

        /// <summary>
        /// The current MIP icon type: vector, and with AutoColor the Smart Client tints it to match
        /// the active theme instead of the plugin guessing at a foreground colour.
        /// </summary>
        public static VideoOSIconSourceBase CreateIconSource()
        {
            var geometry = Media.Geometry.Parse(ClockGeometry);
            geometry.Freeze();

            return new VideoOSIconPathSource
            {
                AutoColor = true,
                Path = new System.Windows.Shapes.Path
                {
                    Data = geometry,
                    Fill = Media.Brushes.White,
                    Width = 24,
                    Height = 24,
                    Stretch = Media.Stretch.Uniform
                }
            };
        }

        /// <summary>
        /// Bitmap fallback for <see cref="VideoOS.Platform.PluginDefinition.Icon"/>, which still
        /// takes a System.Drawing image.
        /// </summary>
        public static Bitmap CreateClockIcon(int size)
        {
            var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                var inset = size * 0.09f;
                var box = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);

                using (var pen = new Pen(Color.FromArgb(235, 235, 235), size * 0.085f))
                    g.DrawEllipse(pen, box);

                var centre = new PointF(size / 2f, size / 2f);
                using (var pen = new Pen(Color.FromArgb(235, 235, 235), size * 0.085f)
                       { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, centre, new PointF(centre.X, centre.Y - size * 0.26f));
                    g.DrawLine(pen, centre, new PointF(centre.X + size * 0.20f, centre.Y));
                }
            }

            return bitmap;
        }
    }
}
