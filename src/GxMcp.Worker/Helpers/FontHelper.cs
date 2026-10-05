using System;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// A report control's <c>Font</c> is a <see cref="System.Drawing.Font"/>; the read path
    /// projects it with <c>Font.ToString()</c> (<c>[Font: Name=Arial, Size=12, Units=3, ...]</c>),
    /// which carries no style. This parses that projection and the
    /// <see cref="FontConverter"/> invariant form (<c>Arial, 12pt, style=Bold</c>) so the value
    /// can be written back to the SDK and compared by content instead of by string.
    /// </summary>
    public static class FontHelper
    {
        private static readonly Regex ToStringRx = new Regex(@"^\s*\[Font:\s*(?<body>.*)\]\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool IsFontAttributeName(string name)
            => string.Equals(name, "Font", StringComparison.OrdinalIgnoreCase);

        public sealed class FontSpec
        {
            public string Name;
            public float Size;
            public GraphicsUnit Unit = GraphicsUnit.Point;
            public FontStyle Style;
            public bool StyleSpecified;
            public bool SizeSpecified = true;
            public byte GdiCharSet = 1;
            public bool GdiVerticalFont;
        }

        public static bool TryParse(string raw, out FontSpec spec)
        {
            spec = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var m = ToStringRx.Match(raw);
            if (m.Success) return TryParseToString(m.Groups["body"].Value, out spec);

            try
            {
                // Not TypeDescriptor.GetConverter: inside the Worker the GeneXus SDK registers its
                // own Font converter, which does not read the invariant "Arial, 12pt" form.
                var font = new FontConverter().ConvertFromInvariantString(raw.Trim()) as Font;
                if (font == null) return false;
                // FontConverter silently substitutes a default family when the name is not installed.
                string requested = raw.Split(',')[0].Trim();
                if (!string.Equals(requested, font.Name, StringComparison.OrdinalIgnoreCase)) { font.Dispose(); return false; }
                spec = new FontSpec
                {
                    Name = font.Name,
                    Size = font.Size,
                    Unit = font.Unit,
                    Style = font.Style,
                    StyleSpecified = true,
                    // A bare family name ("Verdana") states no size; the converter's default is not a request.
                    SizeSpecified = raw.IndexOf(',') >= 0,
                    GdiCharSet = font.GdiCharSet,
                    GdiVerticalFont = font.GdiVerticalFont
                };
                font.Dispose();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryParseToString(string body, out FontSpec spec)
        {
            spec = null;
            var s = new FontSpec();
            bool hasName = false, hasSize = false;
            foreach (var part in body.Split(','))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                string key = part.Substring(0, eq).Trim();
                string val = part.Substring(eq + 1).Trim();
                switch (key.ToLowerInvariant())
                {
                    case "name": s.Name = val; hasName = val.Length > 0; break;
                    case "size":
                        if (!float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out s.Size)) return false;
                        hasSize = true;
                        break;
                    case "units":
                        if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int u)) return false;
                        s.Unit = (GraphicsUnit)u;
                        break;
                    case "style":
                        if (!Enum.TryParse(val.Replace("|", ","), true, out FontStyle st)) return false;
                        s.Style = st;
                        s.StyleSpecified = true;
                        break;
                    case "gdicharset":
                        if (byte.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte cs)) s.GdiCharSet = cs;
                        break;
                    case "gdiverticalfont":
                        bool.TryParse(val, out s.GdiVerticalFont);
                        break;
                }
            }
            if (!hasName || !hasSize || s.Size <= 0) return false;
            spec = s;
            return true;
        }

        public static Font ToFont(FontSpec spec)
            => new Font(spec.Name, spec.Size, spec.Style, spec.Unit, spec.GdiCharSet, spec.GdiVerticalFont);

        /// <summary>
        /// Same font by content. Style is compared only when both sides state one, because the
        /// <c>ToString()</c> projection the read path returns does not carry it.
        /// </summary>
        public static bool AreEquivalent(string a, string b)
        {
            if (!TryParse(a, out var x) || !TryParse(b, out var y)) return false;
            if (!string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)) return false;
            if (x.SizeSpecified && y.SizeSpecified && (x.Unit != y.Unit || Math.Abs(x.Size - y.Size) > 0.01f)) return false;
            return !(x.StyleSpecified && y.StyleSpecified) || x.Style == y.Style;
        }
    }
}
