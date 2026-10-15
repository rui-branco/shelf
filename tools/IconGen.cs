using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Shelf
{
    /// <summary>
    /// Renders Logo.Draw into assets/shelf.ico and docs/logo.png. build.ps1
    /// compiles this together with src/Logo.cs alone, so the exe icon and the
    /// README logo come from the same drawing as the taskbar pill instead of
    /// from a port of it.
    ///
    /// Usage: IconGen.exe out.ico out.png
    /// </summary>
    static class IconGen
    {
        // Every size Windows picks between for small and large icons at 100 to
        // 200 percent scaling, plus Explorer's big views, so it only ever has
        // to scale down from a close frame rather than from 256.
        static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

        static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: IconGen.exe <out.ico> <out.png>");
                return 2;
            }

            try
            {
                string icoPath = Path.GetFullPath(args[0]);
                string pngPath = Path.GetFullPath(args[1]);

                List<byte[]> blobs = new List<byte[]>();
                foreach (int size in Sizes) blobs.Add(RenderPng(size));

                WriteIco(icoPath, blobs);
                File.WriteAllBytes(pngPath, RenderPng(256));

                Console.WriteLine("ICO written: " + icoPath + " (" + new FileInfo(icoPath).Length + " bytes, "
                    + Sizes.Length + " sizes: " + string.Join(", ", Sizes) + "), PNG written: "
                    + pngPath + " (" + new FileInfo(pngPath).Length + " bytes)");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("IconGen failed: " + ex.Message);
                return 1;
            }
        }

        static byte[] RenderPng(int size)
        {
            using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    // No margin: the mark already keeps its own padding inside the box.
                    Logo.Draw(g, new RectangleF(0, 0, size, size));
                }

                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        /// <summary>
        /// An ICO is a directory of entries followed by the images. Each image is
        /// stored as PNG, which Windows has accepted since Vista and which keeps
        /// the 256px frame small with its alpha intact.
        /// </summary>
        static void WriteIco(string path, List<byte[]> blobs)
        {
            using (FileStream fs = File.Create(path))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write((ushort)0);                // reserved
                bw.Write((ushort)1);                // type: icon
                bw.Write((ushort)blobs.Count);

                int offset = 6 + 16 * blobs.Count;
                for (int i = 0; i < blobs.Count; i++)
                {
                    byte dim = (byte)(Sizes[i] < 256 ? Sizes[i] : 0);   // 0 means 256 in the ICO directory
                    bw.Write(dim);
                    bw.Write(dim);
                    bw.Write((byte)0);              // palette
                    bw.Write((byte)0);              // reserved
                    bw.Write((ushort)1);            // colour planes
                    bw.Write((ushort)32);           // bits per pixel
                    bw.Write((uint)blobs[i].Length);
                    bw.Write((uint)offset);
                    offset += blobs[i].Length;
                }
                foreach (byte[] data in blobs) bw.Write(data);
            }
        }
    }
}
