using System;
using System.Drawing;
using System.IO;
using System.Text;

namespace Shelf
{
    /// <summary>Fits file names into the stack's two-line tile labels.</summary>
    public static class Labels
    {
        public static readonly StringFormat Format = MakeLabelFormat();

        static StringFormat MakeLabelFormat()
        {
            StringFormat f = new StringFormat();
            f.Alignment = StringAlignment.Center;
            f.LineAlignment = StringAlignment.Near;
            f.Trimming = StringTrimming.None;
            // Only whole lines: a third line peeking out under the second is
            // worse than no third line at all.
            f.FormatFlags = StringFormatFlags.LineLimit;
            return f;
        }

        /// <summary>
        /// Fits a file name into two lines. Names that do not fit lose their
        /// middle, not their end: "quarterly-rep...final.pdf" still tells you
        /// what it is and what kind of file, where cutting the end leaves a
        /// row of identical prefixes that differ only in what was cut.
        /// </summary>
        public static string Fit(Graphics g, string name, Font font, SizeF box)
        {
            string whole = Breakable(name);
            if (Fits(g, whole, font, box)) return whole;

            string ext = Path.GetExtension(name);
            int tail = ext.Length > 0 && ext.Length <= 6 ? ext.Length + 4 : 6;
            tail = Math.Min(tail, name.Length / 2);

            // Never start the kept end on punctuation: an ellipsis followed by
            // a dot reads as a typo, not as a cut.
            while (tail > 1 && IsSeparator(name[name.Length - tail])) tail--;
            string end = name.Substring(name.Length - tail);

            int lo = 1, hi = name.Length - tail - 1, best = 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (Fits(g, Breakable(name.Substring(0, mid) + Ellipsis + end), font, box))
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return Breakable(name.Substring(0, best) + Ellipsis + end);
        }

        static readonly string Ellipsis = ((char)0x2026).ToString();
        const char ZeroWidthSpace = (char)0x200B;

        static bool IsSeparator(char c)
        {
            return c == '.' || c == '-' || c == '_' || c == ' ';
        }

        /// <summary>
        /// GDI+ wraps only at spaces and hyphens, and otherwise breaks wherever
        /// the line runs out, so "IMG_6805.png" a pixel too wide came out as
        /// "IMG_6805.pn" over a lone "g". Zero-width spaces after underscores
        /// and before the extension give it somewhere sensible to break.
        /// </summary>
        static string Breakable(string s)
        {
            int dot = s.LastIndexOf('.');
            StringBuilder sb = new StringBuilder(s.Length + 4);
            for (int i = 0; i < s.Length; i++)
            {
                if (i == dot && i > 0) sb.Append(ZeroWidthSpace);
                sb.Append(s[i]);
                if (s[i] == '_') sb.Append(ZeroWidthSpace);
            }
            return sb.ToString();
        }

        static bool Fits(Graphics g, string text, Font font, SizeF box)
        {
            int chars, lines;
            g.MeasureString(text, font, box, Format, out chars, out lines);
            return chars >= text.Length;
        }
    }
}
