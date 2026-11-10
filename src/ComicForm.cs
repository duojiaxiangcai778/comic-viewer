// ComicForm.cs — 连续看图窗口
// 编译：csc /target:winexe /codepage:65001 /r:System.IO.Compression.FileSystem.dll ComicViewer.cs ComicForm.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Forms;

namespace ComicGlue
{
    /// <summary>从压缩包里按需取图的统一入口（zip 原生 / rar·7z 走外部解压器）</summary>
    internal class ArchiveReader : IDisposable
    {
        string _archive;
        ZipArchive _zip;
        Dictionary<string, ZipArchiveEntry> _map =
            new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        string _extractDir;      // 非 zip 格式时的落地缓存目录
        bool _needExternal;

        public ArchiveReader(string archive)
        {
            _archive = archive;
            if (string.Equals(Path.GetExtension(archive), ".zip", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(archive), ".cbz", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _zip = ZipFile.OpenRead(archive);
                }
                catch
                {
                    _zip = null;
                    _needExternal = true;
                }
            }
            else
            {
                _needExternal = true;
            }

            if (_zip != null)
            {
                foreach (ZipArchiveEntry e in _zip.Entries)
                {
                    if (e.FullName.EndsWith("/")) continue;
                    string key = Path.GetFileName(e.FullName);
                    if (!_map.ContainsKey(key)) _map[key] = e;
                }
            }
        }

        public bool IsZip { get { return _zip != null; } }

        /// <summary>直接列出压缩包内所有图片（不依赖 360zip 的 config.ini，用于兼容未来版本）</summary>
        public static List<string> ListImages(string archive)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(archive) || !File.Exists(archive)) return list;
            string ext = Path.GetExtension(archive).ToLowerInvariant();
            try
            {
                if (ext == ".zip" || ext == ".cbz")
                {
                    using (ZipArchive z = ZipFile.OpenRead(archive))
                    {
                        foreach (ZipArchiveEntry e in z.Entries)
                        {
                            if (e.FullName.EndsWith("/")) continue;
                            if (Program.IsImage(e.FullName)) list.Add(Path.GetFileName(e.FullName));
                        }
                    }
                }
                else
                {
                    string dir = External.ExtractAll(archive);
                    if (dir != null)
                    {
                        foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                            if (Program.IsImage(f)) list.Add(Path.GetFileName(f));
                    }
                }
            }
            catch { }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        public byte[] Read(string fileName)
        {
            if (_zip != null)
            {
                ZipArchiveEntry e;
                if (_map.TryGetValue(Path.GetFileName(fileName), out e))
                {
                    using (Stream s = e.Open())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
                return null;
            }

            // 外部解压：一次性解到缓存目录
            if (_extractDir == null) _extractDir = External.ExtractAll(_archive);
            if (_extractDir == null) return null;
            string p = Path.Combine(_extractDir, Path.GetFileName(fileName));
            if (!File.Exists(p))
            {
                // 可能带子目录，递归找
                string[] hits = Directory.GetFiles(_extractDir, Path.GetFileName(fileName), SearchOption.AllDirectories);
                if (hits.Length > 0) p = hits[0];
                else return null;
            }
            return File.ReadAllBytes(p);
        }

        public void Dispose()
        {
            if (_zip != null) { try { _zip.Dispose(); } catch { } _zip = null; }
            if (_extractDir != null)
            {
                try { Directory.Delete(_extractDir, true); } catch { }
                _extractDir = null;
            }
        }
    }

    /// <summary>外部解压器（7-Zip / WinRAR）</summary>
    internal static class External
    {
        static readonly string[] SevenZipCandidates = {
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
            @"D:\Program Files\7-Zip\7z.exe",
            @"D:\D\7-Zip\7z.exe",
            @"E:\Program Files\7-Zip\7z.exe",
            @"E:\工具\7-Zip\7z.exe",
        };

        static readonly string[] UnrarCandidates = {
            @"C:\Program Files\WinRAR\UnRAR.exe",
            @"C:\Program Files (x86)\WinRAR\UnRAR.exe",
            @"D:\Program Files\WinRAR\UnRAR.exe",
            @"D:\D\WinRAR\UnRAR.exe",
            @"E:\工具\WinRAR\UnRAR.exe",
        };

        public static string FindSevenZip()
        {
            foreach (string c in SevenZipCandidates) if (File.Exists(c)) return c;
            return null;
        }

        public static string FindUnrar()
        {
            foreach (string c in UnrarCandidates) if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>把整包解压到临时目录，返回目录路径</summary>
        public static string ExtractAll(string archive)
        {
            string h = Hash.Path(archive);
            string dir = Path.Combine(Path.GetTempPath(), "ComicGlue", h);
            try
            {
                if (Directory.Exists(dir) && Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length > 0)
                    return dir;
                Directory.CreateDirectory(dir);
            }
            catch { return null; }

            string ext = Path.GetExtension(archive).ToLowerInvariant();

            string sz = FindSevenZip();
            if (sz != null)
            {
                int code = Run(sz, "x \"" + archive + "\" -o\"" + dir + "\" -y -aos");
                if (code == 0) return dir;
            }

            string ur = FindUnrar();
            if (ur != null && (ext == ".rar" || ext == ".cbr"))
            {
                int code = Run(ur, "x -y \"" + archive + "\" \"" + dir + "\\\"");
                if (code == 0) return dir;
            }

            try { Directory.Delete(dir, true); } catch { }
            MessageBox.Show(
                "这个压缩包不是 zip 格式，需要 7-Zip 或 WinRAR 帮忙解包。\n\n" +
                "请安装 7-Zip（推荐）或 WinRAR 后重试。",
                Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        static int Run(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(120000);
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch { return -1; }
        }
    }

    internal static class Hash
    {
        public static string Path(string s)
        {
            uint h = 2166136261;
            foreach (char c in s.ToLowerInvariant()) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }
    }

    /// <summary>连续看图主窗口</summary>
    internal class ComicForm : Form
    {
        PicContext _ctx;
        ArchiveReader _reader;
        int _index;
        Image _cur;
        string _err;

        float _zoom = 0f;          // 0 = 适合窗口；>0 = 倍率
        PointF _offset = PointF.Empty;
        bool _dragging;
        Point _dragStart;
        PointF _dragOffsetStart;

        bool _showInfo = true;
        DateTime _infoUntil = DateTime.MaxValue;

        const int InfoBarHeight = 28;

        public ComicForm(PicContext ctx)
        {
            _ctx = ctx;
            _index = ctx.Current;
            _reader = new ArchiveReader(ctx.Archive);

            Text = Program.AppName + " - " + Path.GetFileName(ctx.Archive);
            BackColor = Color.Black;
            DoubleBuffered = true;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(480, 360);
            Width = 1100;
            Height = 820;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            LoadCurrent();
        }

        // ---------------- 图片加载 ----------------

        void LoadCurrent()
        {
            if (_cur != null) { _cur.Dispose(); _cur = null; }
            _err = null;
            _zoom = 0f;
            _offset = PointF.Empty;

            string name = _ctx.Files[_index];
            try
            {
                byte[] bytes = _reader.Read(name);
                if (bytes == null)
                {
                    _err = "压缩包里找不到：" + name;
                }
                else
                {
                    using (var ms = new MemoryStream(bytes))
                    {
                        // 复制一份，避免 Image 持有 ms 生命周期
                        using (Image tmp = Image.FromStream(ms))
                        {
                            _cur = new Bitmap(tmp);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _err = "解码失败：" + name + "\n" + ex.Message;
            }

            UpdateTitle();
            Invalidate();
        }

        void UpdateTitle()
        {
            this.Text = string.Format("{0}   [{1}/{2}]   {3}",
                Path.GetFileName(_ctx.Archive), _index + 1, _ctx.Files.Count, _ctx.Files[_index]);
        }

        // ---------------- 导航 ----------------

        void Go(int delta)
        {
            int n = _index + delta;
            if (n < 0) n = _ctx.Files.Count - 1;
            if (n >= _ctx.Files.Count) n = 0;
            _index = n;
            LoadCurrent();
        }

        void GoTo(int n)
        {
            if (n < 0) n = 0;
            if (n >= _ctx.Files.Count) n = _ctx.Files.Count - 1;
            if (n == _index) return;
            _index = n;
            LoadCurrent();
        }

        // ---------------- 绘制 ----------------

        RectangleF ImageRect()
        {
            if (_cur == null) return RectangleF.Empty;
            float w = _cur.Width, h = _cur.Height;
            float availW = ClientSize.Width;
            float availH = ClientSize.Height;

            float scale;
            if (_zoom <= 0f)
            {
                scale = Math.Min(availW / w, availH / h);
                if (scale > 4f) scale = 4f;   // 小图适度放大，避免糊成马赛克
            }
            else scale = _zoom;

            float dw = w * scale, dh = h * scale;
            float x = (availW - dw) / 2f + _offset.X;
            float y = (availH - dh) / 2f + _offset.Y;
            return new RectangleF(x, y, dw, dh);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            if (_cur != null)
            {
                g.DrawImage(_cur, ImageRect());
            }
            else if (_err != null)
            {
                using (var f = new Font("Microsoft YaHei", 12f))
                using (var b = new SolidBrush(Color.FromArgb(230, 230, 230)))
                {
                    var sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(_err, f, b, new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), sf);
                }
            }

            if (_showInfo)
            {
                using (var b = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                    g.FillRectangle(b, 0, 0, ClientSize.Width, InfoBarHeight);

                using (var f = new Font("Microsoft YaHei", 9.5f))
                using (var b = new SolidBrush(Color.FromArgb(240, 240, 240)))
                {
                    string left = string.Format("{0} / {1}", _index + 1, _ctx.Files.Count);
                    string right = _ctx.Files[_index];
                    g.DrawString(left, f, b, 10, 6);
                    var sf = new StringFormat();
                    sf.Alignment = StringAlignment.Far;
                    g.DrawString(right, f, b, new RectangleF(0, 6, ClientSize.Width - 12, 20), sf);
                }
            }
            base.OnPaint(e);
        }

        // ---------------- 输入 ----------------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Left:
                case Keys.Up:
                case Keys.PageUp:
                case Keys.Back:
                    Go(-1); return true;
                case Keys.Right:
                case Keys.Down:
                case Keys.PageDown:
                case Keys.Space:
                    Go(1); return true;
                case Keys.Home: GoTo(0); return true;
                case Keys.End: GoTo(_ctx.Files.Count - 1); return true;
                case Keys.Escape:
                    if (FormBorderStyle == FormBorderStyle.None) { ToggleFullscreen(); return true; }
                    Close(); return true;
                case Keys.F11: ToggleFullscreen(); return true;
                case Keys.D0:
                case Keys.NumPad0:
                    _zoom = 0f; _offset = PointF.Empty; Invalidate(); return true;
                case Keys.Oemplus:
                case Keys.Add:
                    ZoomBy(1.15f); return true;
                case Keys.OemMinus:
                case Keys.Subtract:
                    ZoomBy(1f / 1.15f); return true;
                case Keys.I: _showInfo = !_showInfo; Invalidate(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void ZoomBy(float factor)
        {
            if (_cur == null) return;
            float cur = _zoom <= 0f ? ImageRect().Width / _cur.Width : _zoom;
            cur *= factor;
            if (cur < 0.05f) cur = 0.05f;
            if (cur > 16f) cur = 16f;
            _zoom = cur;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == Keys.Control) ZoomBy(e.Delta > 0 ? 1.12f : 1f / 1.12f);
            else Go(e.Delta > 0 ? -1 : 1);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _dragStart = e.Location;
                _dragOffsetStart = _offset;
                Cursor = Cursors.SizeAll;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                _offset = new PointF(
                    _dragOffsetStart.X + (e.X - _dragStart.X),
                    _dragOffsetStart.Y + (e.Y - _dragStart.Y));
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_dragging) { _dragging = false; Cursor = Cursors.Default; }
            base.OnMouseUp(e);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            ToggleFullscreen();
            base.OnDoubleClick(e);
        }

        void ToggleFullscreen()
        {
            if (FormBorderStyle == FormBorderStyle.None)
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = FormWindowState.Normal;
                TopMost = false;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Maximized;
                TopMost = true;
            }
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_cur != null) { _cur.Dispose(); _cur = null; }
                if (_reader != null) { _reader.Dispose(); _reader = null; }
            }
            base.Dispose(disposing);
        }
    }
}
