using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Shelf
{
    public class AppConfig
    {
        /// <summary>Override for the folder to watch. Empty means the system Downloads folder.</summary>
        public string FolderPath { get; set; }
        /// <summary>
        /// Ceiling on how many items the stack will hold. There is no UI for it,
        /// so it is deliberately high: the grid decides for itself how many tiles
        /// fit on the monitor, and this is only here to stop a folder with
        /// thousands of files in it from being read into memory for nothing. It
        /// used to default to 12, which quietly hid everything past the twelfth
        /// newest file with nothing to say it had.
        /// </summary>
        public int MaxItems { get; set; }
        /// <summary>Tile size in logical pixels at 100% DPI.</summary>
        public int TileSize { get; set; }
        /// <summary>Whether to launch at Windows startup.</summary>
        public bool StartWithWindows { get; set; }

        public AppConfig()
        {
            FolderPath = "";
            MaxItems = 100;
            TileSize = 70;
            StartWithWindows = false;
        }

        public static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Shelf");
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(Dir, "config.json"); }
        }

        public static AppConfig Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new AppConfig();
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                Dictionary<string, object> root = Json.Obj(Json.Parse(json));
                if (root == null) return new AppConfig();

                AppConfig cfg = new AppConfig();

                cfg.FolderPath = Json.Str(root, "FolderPath") ?? "";
                cfg.MaxItems = Json.Int(root, "MaxItems", 100);
                cfg.TileSize = Json.Int(root, "TileSize", 96);
                cfg.StartWithWindows = Json.Bool(root, "StartWithWindows", false);

                // Sanity clamps
                if (cfg.MaxItems < 1) cfg.MaxItems = 1;
                if (cfg.MaxItems > 100) cfg.MaxItems = 100;
                if (cfg.TileSize < 48) cfg.TileSize = 48;
                if (cfg.TileSize > 256) cfg.TileSize = 256;

                return cfg;
            }
            catch
            {
                // A corrupt config must never stop the app running.
                return new AppConfig();
            }
        }

        public bool Save()
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);

                Dictionary<string, object> root = new Dictionary<string, object>();
                root["FolderPath"] = FolderPath;
                root["MaxItems"] = MaxItems;
                root["TileSize"] = TileSize;
                root["StartWithWindows"] = StartWithWindows;

                File.WriteAllText(FilePath, Json.Write(root), new UTF8Encoding(false));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
