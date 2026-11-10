// ComicViewer.exe  —  360zip 压缩包内连续看图插件
// 目标框架：.NET Framework 4.8（Windows 内置，零额外依赖）
// 编译：csc /target:winexe /codepage:65001 /r:System.IO.Compression.FileSystem.dll ComicViewer.cs
//
// 行为：
//   1) 被 360zip 调用（路径形如 %TEMP%\360zip$Pic\360$N\xx.png）时进入「漫画模式」：
//      解析同目录 config.ini 得到完整文件清单，定位源压缩包，直接按需解压显示，支持左右翻页。
//   2) 其它图片路径走「转发模式」：交给用户原来的看图器，日常看图无感。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ComicGlue
{
    internal static class Native
    {
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr hMem);
    }

    /// <summary>360zip 图片模式上下文（来自 config.ini）</summary>
    internal class PicContext
    {
        public string TempDir;                 // ...\360zip$Pic\360$N
        public List<string> Files = new List<string>();
        public int Current = 0;
        public IntPtr Handle = IntPtr.Zero;
        public string Archive;                 // 源压缩包完整路径
        public string StartFile;               // 命令行传入的文件名
    }

    internal static class Ini
    {
        public static Dictionary<string, string> ReadFlat(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return d;
            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.Unicode); }
            catch { try { lines = File.ReadAllLines(path, Encoding.Default); } catch { return d; } }
            foreach (string raw in lines)
            {
                string line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return d;
        }

        public static void WriteFlat(string path, Dictionary<string, string> d)
        {
            var sb = new StringBuilder();
            foreach (var kv in d) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
    }

    internal static class Program
    {
        public const string AppName = "360zip 连续看图";

        static readonly string[] ArchiveExts = { ".zip", ".rar", ".7z", ".cbr", ".cbz", ".cb7", ".tar", ".gz" };

        /// <summary>诊断日志（%TEMP%\ComicViewer.log）</summary>
        public static void Log(string msg)
        {
            try
            {
                string p = Path.Combine(Path.GetTempPath(), "ComicViewer.log");
                File.AppendAllText(p, DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + "  " + msg + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // ---- IFEO 模式剥离 ----
            // 360zip 对 webp 等格式会直接 CreateProcess 拉起 2345PicViewer.exe，绕过文件关联。
            // 我们用 Image File Execution Options 把那个 exe 劫持到本程序，系统调用时会变成：
            //   ComicViewer.exe "D:\D\2345pic\2345PicViewer.exe" "C:\...\360zip$Temp\360$0\001.webp"
            // 这里把第一个参数（原 exe）剥掉，还原成真实的图片路径。
            bool ifeoMode = false;
            if (args.Length >= 2 &&
                args[0].EndsWith("2345PicViewer.exe", StringComparison.OrdinalIgnoreCase))
            {
                ifeoMode = true;
                Log("IFEO 剥离: args[0]=" + args[0] + " -> 真实路径=" + args[1]);
                args = new string[] { args[1] };
            }
            else
            {
                Log("普通启动: args=" + string.Join(" | ", args));
            }

            if (args.Length == 0)
            {
                MessageBox.Show("用法：\n\n  ComicViewer.exe <图片路径>\n      由 360zip 打开压缩包内图片时自动调用\n\n" +
                                "  ComicViewer.exe --install\n      接管图片关联（启用连续看图）\n\n" +
                                "  ComicViewer.exe --uninstall\n      解除接管，还原原看图器\n\n" +
                                "  ComicViewer.exe --status\n      查看当前接管状态",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // ---- 管理命令 ----
            string a0 = args[0].Trim();
            switch (a0.ToLowerInvariant())
            {
                case "--install":
                case "-i":
                case "/install":
                    AssocInstaller.RunInstallGUI();
                    return;
                case "--uninstall":
                case "-u":
                case "/uninstall":
                    AssocInstaller.RunUninstallGUI();
                    return;
                case "--status":
                case "-s":
                case "/status":
                    AssocInstaller.RunStatusGUI();
                    return;
            }

            string path = args[0];

            // ---- 漫画模式判定（不只认目录名，也认 config.ini，兼容 360zip 未来改版）----
            string parent = Path.GetDirectoryName(path);
            // 360zip 有两条临时目录：
            //   360zip$Pic  —— 内置支持的图片格式（png/jpg/gif/bmp/tif…），带 config.ini
            //   360zip$Temp —— 不支持的格式（如 webp），只有一张图、没有 config.ini
            bool inPicDir = path.IndexOf("360zip$Pic", StringComparison.OrdinalIgnoreCase) >= 0
                         || path.IndexOf("360zip$Temp", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasCfg = !string.IsNullOrEmpty(parent) && File.Exists(Path.Combine(parent, "config.ini"));
            if (inPicDir || hasCfg)
            {
                // ---- 交给 2345 自己翻页 ----
                // 360zip 的临时目录里通常只有当前这一张图，2345 打开后当然只能翻 1/1。
                // 破解办法：把压缩包里所有图片全部铺到这个目录，2345 自己就会认出
                // 「同目录兄弟文件」并翻页（实测 83 张 webp 显示 第1/83张）。
                // 这条路线还顺带解决了 GDI+ 在 Win10 上不支持 webp 的问题 —— 解码交给 2345。
                if (ifeoMode || !IsGdiPlusImage(path))
                {
                    HandoffTo2345(path);
                    return;
                }

                PicContext ctx = BuildContext(path);
                Log("图片模式: inPicDir=" + inPicDir + " hasCfg=" + hasCfg +
                    " files=" + (ctx == null ? -1 : ctx.Files.Count) +
                    " archive=" + (ctx == null ? "null" : (ctx.Archive ?? "null")));
                if (ctx != null && ctx.Files.Count > 1 && !string.IsNullOrEmpty(ctx.Archive))
                {
                    try
                    {
                        Log("进入 ComicForm, current=" + ctx.Current);
                        Application.Run(new ComicForm(ctx));
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log("ComicForm 异常: " + ex);
                        MessageBox.Show("连续看图启动失败，已退回系统看图器。\n\n" + ex.Message,
                            AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            else
            {
                Log("非图片模式: " + path);
            }

            Forward(path);
        }

        /// <summary>GDI+ 能直接解码的格式（webp/heic 等不在其中，必须交给 2345）</summary>
        public static bool IsGdiPlusImage(string name)
        {
            string e = Path.GetExtension(name).ToLowerInvariant();
            switch (e)
            {
                case ".png": case ".jpg": case ".jpeg": case ".jpe": case ".jfif": case ".jif":
                case ".gif": case ".bmp": case ".dib": case ".rle": case ".tif": case ".tiff":
                case ".ico": case ".wmf": case ".emf":
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 把压缩包里所有图片铺到 360zip 的临时目录，然后拉起真正的 2345 打开第一张。
        /// 2345 见到同目录多张图就能连续翻页（实测 83 张 webp 正常显示 第1/83张）。
        /// </summary>
        static void HandoffTo2345(string imgPath)
        {
            string dir = Path.GetDirectoryName(imgPath);
            try
            {
                string archive = ArchiveFromRunning360zip();
                Log("Handoff: archive=" + (archive ?? "null") + " dir=" + dir);
                if (!string.IsNullOrEmpty(archive) && Directory.Exists(dir))
                {
                    int n = ExtractAllImages(archive, dir);
                    Log("Handoff: 铺图完成 " + n + " 张");
                }
            }
            catch (Exception ex)
            {
                Log("Handoff 异常: " + ex.Message);
            }

            // 启动真正的 2345（.real.exe 副本，避免再撞 IFEO）
            string real = null;
            string viewer = Settings.OriginalViewer;
            if (!string.IsNullOrEmpty(viewer))
            {
                string vd = Path.GetDirectoryName(viewer);
                if (!string.IsNullOrEmpty(vd))
                    real = Path.Combine(vd, Path.GetFileNameWithoutExtension(viewer) + ".real.exe");
            }
            if (string.IsNullOrEmpty(real) || !File.Exists(real)) real = viewer;
            if (string.IsNullOrEmpty(real) || !File.Exists(real))
            {
                Log("Handoff: 找不到原看图器");
                Forward(imgPath);
                return;
            }

            try
            {
                Log("Handoff: 启动 " + real);
                Process.Start(new ProcessStartInfo(real, "\"" + imgPath + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log("Handoff 启动失败: " + ex.Message);
                Forward(imgPath);
            }
        }

        /// <summary>把压缩包内所有图片解压到目标目录（同名同大小则跳过）</summary>
        static int ExtractAllImages(string archive, string targetDir)
        {
            int n = 0;
            string ext = Path.GetExtension(archive).ToLowerInvariant();

            if (ext == ".zip" || ext == ".cbz")
            {
                using (ZipArchive z = ZipFile.OpenRead(archive))
                {
                    foreach (ZipArchiveEntry e in z.Entries)
                    {
                        if (e.FullName.EndsWith("/")) continue;
                        string name = Path.GetFileName(e.FullName);
                        if (!IsImage(name)) continue;
                        string dest = Path.Combine(targetDir, name);
                        if (File.Exists(dest) && new FileInfo(dest).Length == e.Length) continue;
                        e.ExtractToFile(dest, true);
                        n++;
                    }
                }
                return n;
            }

            // rar / 7z：先整包解到缓存，再挑图片复制过来
            string tmp = External.ExtractAll(archive);
            if (string.IsNullOrEmpty(tmp)) return 0;
            foreach (string f in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories))
            {
                if (!IsImage(f)) continue;
                string dest = Path.Combine(targetDir, Path.GetFileName(f));
                try
                {
                    if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(f).Length) continue;
                    File.Copy(f, dest, true);
                    n++;
                }
                catch { }
            }
            return n;
        }

        static string ArchiveFromRunning360zip()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("360zip"))
                {
                    string c = ArchiveFromProcessId(p.Id);
                    if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
                }
            }
            catch { }
            return null;
        }

        /// <summary>把非漫画模式的图片交还给原来的看图器</summary>
        static void Forward(string path)
        {
            string orig = Settings.OriginalViewer;

            // IFEO 场景：2345PicViewer.exe 本体已被劫持，若直接启动它会再次落回本程序（死循环）。
            // 安装时已把原文件复制为 2345PicViewer.real.exe，这里优先走副本。
            if (!string.IsNullOrEmpty(orig) && !AssocInstaller.IsSelf(orig))
            {
                string dir = Path.GetDirectoryName(orig);
                if (!string.IsNullOrEmpty(dir))
                {
                    string real = Path.Combine(dir, Path.GetFileNameWithoutExtension(orig) + ".real.exe");
                    if (File.Exists(real)) orig = real;
                }
            }

            // 防呆：原看图器绝不能是自己，否则会自杀式递归
            if (!string.IsNullOrEmpty(orig) && File.Exists(orig) && !AssocInstaller.IsSelf(orig))
            {
                try
                {
                    var psi = new ProcessStartInfo(orig, "\"" + path + "\"");
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    return;
                }
                catch { }
            }
            // 兜底：用系统"打开方式"对话框（避免再次回到自己，用 rundll32 壳）
            try
            {
                Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,OpenAs_RunDLL \"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开图片：" + path + "\n" + ex.Message, AppName);
            }
        }

        /// <summary>从 360zip 的临时目录还原出完整浏览上下文</summary>
        static PicContext BuildContext(string imgPath)
        {
            string dir = Path.GetDirectoryName(imgPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

            var ctx = new PicContext();
            ctx.TempDir = dir;
            ctx.StartFile = Path.GetFileName(imgPath);

            string iniPath = Path.Combine(dir, "config.ini");
            if (File.Exists(iniPath))
            {
                Parse360Config(iniPath, ctx);
            }

            // config.ini 缺失或没有文件清单时，退化为扫描本目录
            if (ctx.Files.Count == 0)
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    if (IsImage(f)) ctx.Files.Add(Path.GetFileName(f));
                }
                ctx.Files.Sort(StringComparer.OrdinalIgnoreCase);
            }

            // 当前项
            int idx = ctx.Files.FindIndex(delegate(string s) { return string.Equals(s, ctx.StartFile, StringComparison.OrdinalIgnoreCase); });
            if (idx >= 0) ctx.Current = idx;
            if (ctx.Current < 0 || ctx.Current >= ctx.Files.Count) ctx.Current = 0;

            // 源压缩包
            ctx.Archive = LocateArchive(ctx);

            // ---- 兜底：清单不足时直接扫描压缩包 ----
            // 360zip 未来版本若改变临时目录布局或 config.ini 格式，只要还能定位到压缩包，
            // 就自己把压缩包里的图片全列出来，功能不会断。
            if (ctx.Files.Count <= 1 && !string.IsNullOrEmpty(ctx.Archive))
            {
                List<string> fromZip = ArchiveReader.ListImages(ctx.Archive);
                if (fromZip.Count > 1)
                {
                    ctx.Files = fromZip;
                    int k = fromZip.FindIndex(delegate(string s) { return string.Equals(s, ctx.StartFile, StringComparison.OrdinalIgnoreCase); });
                    ctx.Current = k >= 0 ? k : 0;
                }
            }

            return ctx;
        }

        static void Parse360Config(string iniPath, PicContext ctx)
        {
            string[] lines = ReadIniLines(iniPath);
            if (lines == null) return;

            string section = "";
            foreach (string raw in lines)
            {
                string line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0) continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }
                if (section == "files")
                {
                    if (IsImage(line)) ctx.Files.Add(line);
                }
                else if (section == "param")
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    if (k == "current")
                    {
                        int c;
                        if (int.TryParse(v, out c)) ctx.Current = c;
                    }
                    else if (k == "handle")
                    {
                        long h;
                        if (long.TryParse(v, out h)) ctx.Handle = new IntPtr(h);
                    }
                }
            }
        }

        /// <summary>读 ini 文本，自动识别 UTF-16 / UTF-8 / ANSI 编码</summary>
        static string[] ReadIniLines(string path)
        {
            string[] lines = null;
            try { lines = File.ReadAllLines(path, Encoding.Unicode); } catch { }
            if (lines != null && LooksLikeIni(lines)) return lines;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); } catch { }
            if (lines != null && LooksLikeIni(lines)) return lines;
            try { lines = File.ReadAllLines(path, Encoding.Default); } catch { }
            if (lines != null && LooksLikeIni(lines)) return lines;
            return lines;
        }

        static bool LooksLikeIni(string[] lines)
        {
            foreach (string raw in lines)
            {
                string l = raw.Trim().TrimStart('\uFEFF');
                if (l.Length == 0) continue;
                return l[0] == '[' || l.IndexOf('=') > 0;
            }
            return false;
        }

        /// <summary>定位源压缩包：窗口句柄对应进程命令行 → 记忆 → 用户指定</summary>
        static string LocateArchive(PicContext ctx)
        {
            // 1) 通过 handle 找到 360zip 进程，读命令行
            string fromCmd = ArchiveFromWindow(ctx.Handle);
            if (!string.IsNullOrEmpty(fromCmd) && File.Exists(fromCmd))
            {
                Settings.RememberArchive(fromCmd);
                return fromCmd;
            }

            // 2) 当前所有 360zip 进程的命令行
            foreach (Process p in Process.GetProcessesByName("360zip"))
            {
                string c = ArchiveFromProcessId(p.Id);
                if (!string.IsNullOrEmpty(c) && File.Exists(c))
                {
                    Settings.RememberArchive(c);
                    return c;
                }
            }

            // 3) 上次成功记忆（同名压缩包优先）
            string memo = Settings.LastArchive;
            if (!string.IsNullOrEmpty(memo) && File.Exists(memo)) return memo;

            // 4) 让用户手动指定
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "请选择这个压缩包（用于连续看图）";
                ofd.Filter = "压缩包|*.zip;*.rar;*.7z;*.cbr;*.cbz;*.cb7;*.tar;*.gz|所有文件|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    Settings.RememberArchive(ofd.FileName);
                    return ofd.FileName;
                }
            }
            return null;
        }

        static string ArchiveFromWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return null;
            return ArchiveFromProcessId((int)pid);
        }

        /// <summary>读进程命令行并挑出压缩包路径（WMI 优先，PEB 兜底）</summary>
        static string ArchiveFromProcessId(int pid)
        {
            string cmd = Wmi.CommandLine(pid);
            if (string.IsNullOrEmpty(cmd)) cmd = ProcessCommandLine.Get(pid);
            if (string.IsNullOrEmpty(cmd)) return null;

            List<string> argv = SplitCommandLine(cmd);
            foreach (string a in argv)
            {
                string t = a.Trim().Trim('"');
                if (t.Length < 4) continue;
                string ext = Path.GetExtension(t).ToLowerInvariant();
                if (Array.IndexOf(ArchiveExts, ext) < 0) continue;
                if (File.Exists(t)) return t;
            }
            return null;
        }

        static List<string> SplitCommandLine(string cmd)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(cmd)) return list;
            int argc;
            IntPtr p = Native.CommandLineToArgvW(cmd, out argc);
            if (p == IntPtr.Zero) return list;
            try
            {
                for (int i = 0; i < argc; i++)
                {
                    IntPtr s = Marshal.ReadIntPtr(p, i * IntPtr.Size);
                    list.Add(Marshal.PtrToStringUni(s));
                }
            }
            finally { Native.LocalFree(p); }
            return list;
        }

        public static bool IsImage(string name)
        {
            string e = Path.GetExtension(name).ToLowerInvariant();
            switch (e)
            {
                case ".png": case ".jpg": case ".jpeg": case ".jpe": case ".jfif": case ".jif":
                case ".gif": case ".bmp": case ".dib": case ".rle": case ".tif": case ".tiff":
                case ".webp": case ".ico": case ".wmf": case ".emf":
                    return true;
            }
            return false;
        }
    }

    /// <summary>用 WMI 读进程命令行（比 PEB 在 WOW64 下更稳）</summary>
    internal static class Wmi
    {
        public static string CommandLine(int pid)
        {
            try
            {
                string q = "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid.ToString();
                using (var searcher = new System.Management.ManagementObjectSearcher(q))
                {
                    foreach (System.Management.ManagementBaseObject mo in searcher.Get())
                    {
                        object o = mo["CommandLine"];
                        if (o != null)
                        {
                            string s = o.ToString();
                            if (s.Length > 0) return s;
                        }
                    }
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>极简 PEB 读命令行（WMI 不可用时的兜底）</summary>
    internal static class ProcessCommandLine
    {
        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr Reserved3;
        }

        [DllImport("ntdll.dll")]
        static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PROCESS_BASIC_INFORMATION pbi, int len, out int ret);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        const int PROCESS_QUERY_INFORMATION = 0x0400;
        const int PROCESS_VM_READ = 0x0010;
        const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public static string Get(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
            if (h == IntPtr.Zero) h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var pbi = new PROCESS_BASIC_INFORMATION();
                int ret;
                int st = NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(pbi), out ret);
                if (st != 0 || pbi.PebBaseAddress == IntPtr.Zero) return null;

                bool is64Target = IntPtr.Size == 8 && IsWow64(h) == false;

                // PEB->ProcessParameters 偏移：x86 = 0x10，x64 = 0x20
                int offParams = is64Target ? 0x20 : 0x10;
                byte[] buf = new byte[8];
                IntPtr read;
                if (!ReadProcessMemory(h, Add(pbi.PebBaseAddress, offParams), buf, (IntPtr)8, out read)) return null;
                IntPtr procParams = ReadPtr(buf, is64Target);

                // RTL_USER_PROCESS_PARAMETERS->CommandLine（UNICODE_STRING）
                // x86: 0x40 ; x64: 0x70
                int offCmd = is64Target ? 0x70 : 0x40;
                byte[] us = new byte[16];
                if (!ReadProcessMemory(h, Add(procParams, offCmd), us, (IntPtr)16, out read)) return null;

                ushort length = BitConverter.ToUInt16(us, 0);
                // UNICODE_STRING: Length(2) MaximumLength(2) Buffer(ptr)
                // x86 -> Buffer 在 +4；x64 -> Buffer 在 +8（含对齐）
                IntPtr buffer = is64Target
                    ? new IntPtr(BitConverter.ToInt64(us, 8))
                    : new IntPtr(BitConverter.ToInt32(us, 4));
                if (length == 0 || buffer == IntPtr.Zero) return null;

                byte[] str = new byte[length];
                if (!ReadProcessMemory(h, buffer, str, (IntPtr)length, out read)) return null;
                return Encoding.Unicode.GetString(str);
            }
            catch { return null; }
            finally { CloseHandle(h); }
        }

        static IntPtr Add(IntPtr p, int off)
        {
            return IntPtr.Size == 8 ? new IntPtr(p.ToInt64() + off) : new IntPtr(p.ToInt32() + off);
        }

        static IntPtr ReadPtr(byte[] b, bool is64)
        {
            if (is64) return new IntPtr(BitConverter.ToInt64(b, 0));
            return new IntPtr(BitConverter.ToInt32(b, 0));
        }

        [DllImport("kernel32.dll")]
        static extern bool IsWow64Process(IntPtr hProcess, out bool wow64);

        static bool IsWow64(IntPtr h)
        {
            bool b;
            if (IsWow64Process(h, out b)) return b;
            return false;
        }
    }

    /// <summary>配置（原看图器、压缩包记忆）</summary>
    internal static class Settings
    {
        /// <summary>
        /// 主程序安装目录。本体替换模式下 2345PicViewer.exe 是主程序的副本、位于别的目录，
        /// 必须共用同一份配置，否则副本读不到 original_viewer 会走到「找不到原看图器」。
        /// </summary>
        public const string MainDir = @"E:\360zip连续看图";

        public static string ConfigPath
        {
            get
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                string local = Path.Combine(dir, "ComicViewer.ini");
                if (File.Exists(local)) return local;

                // 副本场景（D:\D\2345pic\2345PicViewer.exe）：回落到主程序目录的配置
                string main = Path.Combine(MainDir, "ComicViewer.ini");
                if (File.Exists(main)) return main;

                return local;
            }
        }

        public static string OriginalViewer
        {
            get
            {
                var d = Ini.ReadFlat(ConfigPath);
                string v;
                if (d.TryGetValue("original_viewer", out v)) return v;
                return null;
            }
        }

        public static string LastArchive
        {
            get
            {
                var d = Ini.ReadFlat(ConfigPath);
                string v;
                if (d.TryGetValue("last_archive", out v)) return v;
                return null;
            }
        }

        public static void RememberArchive(string path)
        {
            var d = Ini.ReadFlat(ConfigPath);
            d["last_archive"] = path;
            try { Ini.WriteFlat(ConfigPath, d); } catch { }
        }
    }
}
