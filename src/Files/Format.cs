using System;
using System.IO;

namespace Shelf
{
    /// <summary>Sizes, times and folder names as the stack shows them.</summary>
    public static class Format
    {
        /// <summary>
        /// A tile's target may be a folder as well as a file - File.Exists
        /// answers false for every extracted archive in the folder.
        /// </summary>
        public static bool Exists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return File.Exists(path) || Directory.Exists(path);
        }

        /// <summary>
        /// The folder's own name for the title. GetFileName comes back empty
        /// when a path carries a trailing separator, so trim first.
        /// </summary>
        public static string FolderName(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return "Folder";
            try
            {
                string name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            return "Folder";
        }

        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " bytes";
            double v = bytes / 1024.0;
            string[] units = { "KB", "MB", "GB", "TB" };
            int u = 0;
            while (v >= 1024 && u < units.Length - 1)
            {
                v /= 1024;
                u++;
            }
            return v.ToString(v < 10 ? "0.0" : "0") + " " + units[u];
        }

        public static string When(DateTime t)
        {
            DateTime today = DateTime.Today;
            string time = t.ToShortTimeString();
            if (t.Date == today) return "Today, " + time;
            if (t.Date == today.AddDays(-1)) return "Yesterday, " + time;
            if (t.Year == today.Year) return t.ToString("d MMM") + ", " + time;
            return t.ToString("d MMM yyyy");
        }
    }
}
