using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace Shelf
{
    public class ReleaseInfo
    {
        public Version Version;
        public string Tag;
        public string DownloadUrl;
        public string PageUrl;
    }

    /// <summary>
    /// Self-update against the project GitHub releases. The repository is pinned
    /// here on purpose: an updater that can be pointed somewhere else by a config
    /// file is a way to make Shelf run someone elses code.
    /// </summary>
    public static class Updater
    {
        const string Owner = "rui-branco";
        const string Repo = "shelf";
        const string LatestApi =
            "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";
        const string AssetName = "shelf.exe";

        // GitHub rejects requests without one, and .NET 4 still defaults to TLS 1.0.
        const string Agent = "Shelf-Updater";
        const SecurityProtocolType Tls12 = (SecurityProtocolType)3072;

        public static Version Current
        {
            get
            {
                try { return Normalize(Assembly.GetExecutingAssembly().GetName().Version); }
                catch { return new Version(0, 0, 0, 0); }
            }
        }

        /// <summary>The running version as it is shown to a person: "1.0.0".</summary>
        public static string CurrentText
        {
            get
            {
                Version v = Current;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        /// <summary>
        /// A tag reads "v1.2" while an assembly version is always four parts, and
        /// Version treats a missing part as lower than zero. Pad both out first.
        /// </summary>
        static Version Normalize(Version v)
        {
            if (v == null) return new Version(0, 0, 0, 0);
            return new Version(v.Major, v.Minor,
                v.Build < 0 ? 0 : v.Build, v.Revision < 0 ? 0 : v.Revision);
        }

        static Version ParseTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            string s = tag.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            try { return Normalize(new Version(s)); }
            catch { return null; }
        }

        static WebClient MakeClient()
        {
            try { ServicePointManager.SecurityProtocol |= Tls12; }
            catch { }

            WebClient c = new WebClient();
            c.Headers.Add("User-Agent", Agent);
            return c;
        }

        /// <summary>Looks up the newest release, or null when there is nothing newer.</summary>
        public static ReleaseInfo Check()
        {
            string json;
            using (WebClient c = MakeClient())
            {
                c.Headers.Add("Accept", "application/vnd.github+json");
                json = c.DownloadString(LatestApi);
            }

            Dictionary<string, object> root = Json.Obj(Json.Parse(json));
            if (root == null) return null;

            if (Json.Bool(root, "draft", false) || Json.Bool(root, "prerelease", false)) return null;

            Version v = ParseTag(Json.Str(root, "tag_name"));
            if (v == null || v <= Current) return null;

            string url = AssetUrl(root);
            if (url == null) return null;   // a release with no exe is nothing to install

            ReleaseInfo r = new ReleaseInfo();
            r.Version = v;
            r.Tag = Json.Str(root, "tag_name");
            r.DownloadUrl = url;
            r.PageUrl = Json.Str(root, "html_url");
            return r;
        }

        static string AssetUrl(Dictionary<string, object> root)
        {
            object assetsObj;
            if (!root.TryGetValue("assets", out assetsObj)) return null;
            List<object> assets = Json.Arr(assetsObj);
            if (assets == null) return null;

            foreach (object o in assets)
            {
                Dictionary<string, object> a = Json.Obj(o);
                if (a == null) continue;
                if (!string.Equals(Json.Str(a, "name"), AssetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string url = Json.Str(a, "browser_download_url");
                if (url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return url;
            }
            return null;
        }

        /// <summary>
        /// Checks in the background. The callback gets the release, or null when
        /// there is nothing newer, and the error text when the lookup itself failed.
        /// </summary>
        public static void CheckAsync(Action<ReleaseInfo, string> done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                ReleaseInfo r = null;
                string err = null;
                // Offline, rate-limited, renamed repo: none of it is worth a crash.
                try { r = Check(); }
                catch (Exception ex) { err = ex.Message; }
                if (done != null) done(r, err);
            });
        }

        /// <summary>Downloads beside the installed exe and returns the temporary path.</summary>
        public static string Download(ReleaseInfo r)
        {
            if (r == null || string.IsNullOrEmpty(r.DownloadUrl))
                throw new InvalidOperationException("nothing to download");

            string exe = Application.ExecutablePath;
            string tmp = exe + ".new";
            if (File.Exists(tmp)) File.Delete(tmp);

            using (WebClient c = MakeClient())
                c.DownloadFile(r.DownloadUrl, tmp);

            // Never hand a half-written or redirected-to-HTML file to File.Move.
            FileInfo fi = new FileInfo(tmp);
            if (!fi.Exists || fi.Length < 20000)
            {
                try { File.Delete(tmp); }
                catch { }
                throw new InvalidOperationException("the download was not a program");
            }
            using (FileStream fs = File.OpenRead(tmp))
            {
                if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
                {
                    fs.Close();
                    try { File.Delete(tmp); }
                    catch { }
                    throw new InvalidOperationException("the download was not a program");
                }
            }
            return tmp;
        }

        public static void DownloadAsync(ReleaseInfo r, Action<string, string> done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string path = null;
                string err = null;
                try { path = Download(r); }
                catch (Exception ex) { err = ex.Message; }
                if (done != null) done(path, err);
            });
        }

        /// <summary>
        /// Swaps the new build in and restarts. Windows will not let a running exe
        /// be overwritten, but it will let it be renamed out of the way - so the
        /// old build steps aside and is swept up on the next start.
        /// </summary>
        public static void Apply(string newExe)
        {
            string exe = Application.ExecutablePath;
            string old = exe + ".old";

            if (File.Exists(old))
            {
                try { File.Delete(old); }
                catch { }
            }

            File.Move(exe, old);
            try
            {
                File.Move(newExe, exe);
            }
            catch
            {
                // Put the working build back rather than leaving nothing to launch.
                try { File.Move(old, exe); }
                catch { }
                throw;
            }

            // Hand the single-instance mutex over before the new build claims it,
            // otherwise the restart sees this process still holding it and quits.
            Program.ReleaseSingleInstance();
            System.Diagnostics.Process.Start(exe, "--after-update");
            Application.Exit();
        }

        /// <summary>Clears the build an update stepped aside. Called at startup.</summary>
        public static void CleanupOldBuild()
        {
            try
            {
                string old = Application.ExecutablePath + ".old";
                if (File.Exists(old)) File.Delete(old);

                string tmp = Application.ExecutablePath + ".new";
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch { }
        }
    }
}
