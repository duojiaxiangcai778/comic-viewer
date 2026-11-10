// Installer.cs — 文件关联接管（更新免疫的关键）
//
// 原理：
//   Windows 打开图片的链路是：
//     .png  ->  UserChoice(HKCU)  ->  ProgID(如 2345Pic.png)  ->  shell\open\command
//   其中 UserChoice 带哈希校验，不能直接改；但 ProgID 的 command 在 HKCU 下可以改写。
//   HKCU\Software\Classes 优先级高于 HKLM，所以只覆盖这一层就能接管，且：
//     - 不动 360zip 任何文件 -> 360zip 升级完全免疫
//     - 不动 UserChoice      -> 不触发 Windows 的关联保护
//     - 只写当前用户         -> 不需要管理员权限
//
// 卸载时把备份的原 command 写回去即可。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ComicGlue
{
    internal static class AssocInstaller
    {
        // 需要探测的图片扩展名（360zip 支持的格式 + 常见图片）
        static readonly string[] ProbeExts = {
            ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".jif",
            ".gif", ".bmp", ".dib", ".webp", ".tif", ".tiff", ".ico"
        };

        public static string ExePath
        {
            get { return Application.ExecutablePath; }
        }

        public static string BackupPath
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ComicViewer.backup.ini"); }
        }

        // ---------------- 注册表基础操作 ----------------

        static string ReadHkcu(string subKey)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(subKey))
                {
                    if (k == null) return null;
                    object o = k.GetValue(null);
                    return o as string;
                }
            }
            catch { return null; }
        }

        static void WriteHkcu(string subKey, string value)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(subKey))
            {
                k.SetValue(null, value, RegistryValueKind.String);
            }
        }

        static void DeleteHkcuTree(string subKey)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(subKey, false); } catch { }
        }

        static string GetUserChoiceProgId(string ext)
        {
            string key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\UserChoice";
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(key))
                {
                    if (k == null) return null;
                    object o = k.GetValue("ProgId");
                    return o as string;
                }
            }
            catch { return null; }
        }

        /// <summary>判断某个路径是不是本程序自己</summary>
        public static bool IsSelf(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            if (exePath.IndexOf("ComicViewer", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            try
            {
                return string.Equals(Path.GetFullPath(exePath),
                    Path.GetFullPath(Application.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>从命令行里抽出 exe 路径</summary>
        public static string ExtractExe(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return null;
            cmd = cmd.Trim();
            if (cmd.Length > 1 && cmd[0] == '"')
            {
                int e = cmd.IndexOf('"', 1);
                if (e > 1) return cmd.Substring(1, e - 1);
            }
            int sp = cmd.IndexOf(' ');
            return sp > 0 ? cmd.Substring(0, sp) : cmd;
        }

        // ---------------- 安装 / 卸载 / 状态 ----------------

        public static string Install()
        {
            var sb = new StringBuilder();
            var backup = Ini.ReadFlat(BackupPath);
            int ok = 0, skip = 0;

            string ourCmd = "\"" + ExePath + "\" \"%1\"";

            foreach (string ext in ProbeExts)
            {
                string progId = GetUserChoiceProgId(ext);
                if (string.IsNullOrEmpty(progId))
                {
                    skip++;
                    continue;
                }

                // 系统应用（照片等 AppX）关联无法通过 HKCU 的 ProgID 覆盖
                if (progId.StartsWith("AppX", StringComparison.OrdinalIgnoreCase) ||
                    progId.StartsWith("Applications\\", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("· 跳过 " + ext.PadRight(6) + "  ->  " + progId + "   [系统应用，需在 Windows 设置里手动改]");
                    skip++;
                    continue;
                }

                string cmdKey = @"Software\Classes\" + progId + @"\shell\open\command";
                string orig = ReadHkcu(cmdKey);

                // HKCU 没有时从 HKCR 读（只读，用于备份/还原）
                if (string.IsNullOrEmpty(orig))
                {
                    try
                    {
                        using (RegistryKey k = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
                        {
                            if (k != null) orig = k.GetValue(null) as string;
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(orig))
                {
                    sb.AppendLine("· 跳过 " + ext.PadRight(6) + "  ->  " + progId + "   [找不到原始启动命令]");
                    skip++;
                    continue;
                }

                // 备份（只在第一次记录，避免覆盖掉真正的原值）
                string bkey = "assoc:" + progId;
                if (!backup.ContainsKey(bkey)) backup[bkey] = orig;

                WriteHkcu(cmdKey, ourCmd);

                // 记录原看图器：绝不能把自己记进去
                // （重复安装时 HKCU 里已经是本程序，用它当"原看图器"会导致转发时自噬）
                string exe = ExtractExe(orig);
                if (!string.IsNullOrEmpty(exe) && !IsSelf(exe) && !backup.ContainsKey("viewer"))
                    backup["viewer"] = exe;

                sb.AppendLine("√ 接管 " + ext.PadRight(6) + "  ->  " + progId);
                ok++;
            }

            // 自愈：viewer 缺失 / 指向自己时，从备份的原始命令里恢复
            string viewerVal;
            if (!backup.TryGetValue("viewer", out viewerVal) ||
                string.IsNullOrEmpty(viewerVal) || IsSelf(viewerVal))
            {
                foreach (var kv in backup)
                {
                    if (!kv.Key.StartsWith("assoc:", StringComparison.OrdinalIgnoreCase)) continue;
                    string e = ExtractExe(kv.Value);
                    if (!string.IsNullOrEmpty(e) && !IsSelf(e))
                    {
                        backup["viewer"] = e;
                        break;
                    }
                }
            }

            Ini.WriteFlat(BackupPath, backup);

            // 记下原看图器，供转发用
            var cfg = Ini.ReadFlat(Settings.ConfigPath);
            string v;
            if (backup.TryGetValue("viewer", out v) && !string.IsNullOrEmpty(v)) cfg["original_viewer"] = v;
            cfg["exe_path"] = ExePath;
            Ini.WriteFlat(Settings.ConfigPath, cfg);

            sb.AppendLine();
            sb.AppendLine("接管成功 " + ok + " 项，跳过 " + skip + " 项。");
            sb.AppendLine("原看图器：" + (cfg.ContainsKey("original_viewer") ? cfg["original_viewer"] : "未识别"));
            sb.AppendLine("备份文件：" + BackupPath);

            // ---- IFEO 劫持 2345 看图王 ----
            // 360zip 对 webp 等它自家解码器不支持的格式，不走文件关联，
            // 而是直接 CreateProcess 拉起 2345PicViewer.exe。
            // 用 Image File Execution Options 把那个 exe 的启动重定向到本程序，
            // 这样无论 360zip 从哪儿拿到这个路径，都只能落到我们手里。
            sb.AppendLine();
            string viewerExe = cfg.ContainsKey("original_viewer") ? cfg["original_viewer"] : null;
            if (!string.IsNullOrEmpty(viewerExe) &&
                string.Equals(Path.GetFileName(viewerExe), IfeoTarget, StringComparison.OrdinalIgnoreCase))
            {
                string msg;
                if (SetIfeo(viewerExe, out msg))
                    sb.AppendLine("√ 已劫持 " + IfeoTarget + "（IFEO），360zip 直接调用也能接管");
                else
                {
                    sb.AppendLine("× IFEO 劫持失败：" + msg);
                    sb.AppendLine("  请用【管理员身份】重新运行安装。");
                }
            }
            else
            {
                sb.AppendLine("· 原看图器不是 " + IfeoTarget + "，跳过 IFEO 劫持");
            }

            if (ok > 0)
            {
                sb.AppendLine();
                sb.AppendLine("现在到 360zip 里双击压缩包内的图片就能连续翻页了。");
            }
            return sb.ToString();
        }

        // ---------------- IFEO（映像文件执行选项）----------------

        public const string IfeoTarget = "2345PicViewer.exe";

        static string IfeoKeyPath
        {
            get
            {
                return @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" + IfeoTarget;
            }
        }

        /// <summary>备份原 exe 为 .real.exe，并写入 IFEO Debugger</summary>
        static bool SetIfeo(string viewerExe, out string error)
        {
            error = null;
            try
            {
                string dir = Path.GetDirectoryName(viewerExe);
                string real = Path.Combine(dir, Path.GetFileNameWithoutExtension(viewerExe) + ".real.exe");
                if (!File.Exists(real)) File.Copy(viewerExe, real, false);

                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(IfeoKeyPath))
                {
                    if (k == null) { error = "无法写入 HKLM（需要管理员权限）"; return false; }
                    k.SetValue("Debugger", "\"" + ExePath + "\"");
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        static void RemoveIfeo()
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(IfeoKeyPath, false); }
            catch { }
        }

        static string IfeoStatus()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(IfeoKeyPath))
                {
                    if (k == null) return "(未劫持)";
                    object dbg = k.GetValue("Debugger");
                    return dbg == null ? "(无 Debugger 值)" : dbg.ToString();
                }
            }
            catch (Exception ex) { return "(读取失败: " + ex.Message + ")"; }
        }

        public static string Uninstall()
        {
            var sb = new StringBuilder();
            var backup = Ini.ReadFlat(BackupPath);
            int n = 0;

            foreach (var kv in backup)
            {
                if (!kv.Key.StartsWith("assoc:", StringComparison.OrdinalIgnoreCase)) continue;
                string progId = kv.Key.Substring(6);
                string cmdKey = @"Software\Classes\" + progId + @"\shell\open\command";
                try
                {
                    WriteHkcu(cmdKey, kv.Value);
                    sb.AppendLine("√ 还原 " + progId);
                    n++;
                }
                catch (Exception ex)
                {
                    sb.AppendLine("× 还原失败 " + progId + "：" + ex.Message);
                }
            }

            if (n == 0) sb.AppendLine("没有找到备份记录，可能从未安装过。");

            // 移除 IFEO 劫持
            RemoveIfeo();
            sb.AppendLine("√ 已移除 IFEO 劫持（若存在）");

            try { File.Delete(BackupPath); } catch { }
            sb.AppendLine();
            sb.AppendLine("已还原 " + n + " 项，图片关联回到原来的看图器。");
            return sb.ToString();
        }

        public static string Status()
        {
            var sb = new StringBuilder();
            var backup = Ini.ReadFlat(BackupPath);

            sb.AppendLine("程序路径：" + ExePath);
            sb.AppendLine("备份文件：" + (File.Exists(BackupPath) ? BackupPath : "(无)"));
            sb.AppendLine("IFEO 劫持：" + IfeoTarget + " -> " + IfeoStatus());
            sb.AppendLine();
            sb.AppendLine("当前图片关联接管状态：");

            foreach (string ext in ProbeExts)
            {
                string progId = GetUserChoiceProgId(ext);
                if (string.IsNullOrEmpty(progId)) { sb.AppendLine("  " + ext.PadRight(6) + " (无关联记录)"); continue; }

                string cmd = ReadHkcu(@"Software\Classes\" + progId + @"\shell\open\command");
                if (string.IsNullOrEmpty(cmd))
                {
                    try
                    {
                        using (RegistryKey k = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
                        {
                            if (k != null) cmd = k.GetValue(null) as string;
                        }
                    }
                    catch { }
                }

                bool mine = !string.IsNullOrEmpty(cmd) &&
                            cmd.IndexOf("ComicViewer", StringComparison.OrdinalIgnoreCase) >= 0;
                sb.AppendLine("  " + ext.PadRight(6) + (mine ? "[已接管] " : "[原样]   ") + progId);
                if (!string.IsNullOrEmpty(cmd)) sb.AppendLine("          " + cmd);
            }
            return sb.ToString();
        }

        // ---------------- 带界面的入口 ----------------

        public static void RunInstallGUI()
        {
            string r = Install();
            ShowReport("安装结果", r);
        }

        public static void RunUninstallGUI()
        {
            if (MessageBox.Show("确定要解除接管、把图片关联还给原来的看图器吗？",
                    Program.AppName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            string r = Uninstall();
            ShowReport("卸载结果", r);
        }

        public static void RunStatusGUI()
        {
            ShowReport("当前状态", Status());
        }

        static void ShowReport(string title, string body)
        {
            using (var f = new Form())
            {
                f.Text = Program.AppName + " - " + title;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.Width = 780;
                f.Height = 560;
                f.MinimumSize = new Size(520, 360);
                f.Font = new Font("Microsoft YaHei", 9.5f);

                var txt = new TextBox();
                txt.Multiline = true;
                txt.ReadOnly = true;
                txt.ScrollBars = ScrollBars.Both;
                txt.WordWrap = false;
                txt.Dock = DockStyle.Fill;
                txt.BackColor = Color.White;
                txt.Text = body;
                txt.Select(0, 0);

                var bar = new Panel();
                bar.Dock = DockStyle.Bottom;
                bar.Height = 48;

                var btn = new Button();
                btn.Text = "关闭";
                btn.Width = 96;
                btn.Height = 30;
                btn.Left = 12;
                btn.Top = 9;
                btn.Click += delegate { f.Close(); };

                bar.Controls.Add(btn);
                f.Controls.Add(txt);
                f.Controls.Add(bar);
                f.ShowDialog();
            }
        }
    }
}
