using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CatLayerUninstall
{
    internal enum RemoveMode
    {
        None,
        ProgramOnly,
        Everything
    }

    internal sealed class UninstallForm : Form
    {
        private readonly Color Back = Color.FromArgb(7, 18, 38);
        private readonly Color PanelColor = Color.FromArgb(13, 29, 55);
        private readonly Color Panel2Color = Color.FromArgb(20, 39, 69);
        private readonly Color TextColor = Color.FromArgb(235, 241, 255);
        private readonly Color Muted = Color.FromArgb(160, 177, 207);
        private readonly Color Accent = Color.FromArgb(139, 92, 246);
        private readonly Color Danger = Color.FromArgb(255, 91, 119);

        public RemoveMode Choice = RemoveMode.None;

        public UninstallForm()
        {
            Text = "CatLayer 제거";
            ClientSize = new Size(520, 305);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Back;
            ForeColor = TextColor;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Label title = NewLabel("CatLayer 제거", 28, 24, 460, 34, 16F, FontStyle.Bold, TextColor);
            Label desc = NewLabel("제거할 범위를 선택하세요.", 30, 66, 450, 24, 9.5F, FontStyle.Regular, Muted);

            Panel programPanel = NewPanel(24, 104, 472, 64);
            Label p1 = NewLabelOn(programPanel, "프로그램만 제거", 18, 10, 230, 24, 10F, FontStyle.Bold, TextColor);
            Label p2 = NewLabelOn(programPanel, "설정 · 프리셋 · 사용자 데이터는 유지합니다.", 18, 34, 315, 20, 8.5F, FontStyle.Regular, Muted);
            Button keep = NewButton(programPanel, "제거", 365, 15, 88, 34, false);
            keep.Click += delegate { Choice = RemoveMode.ProgramOnly; DialogResult = DialogResult.OK; Close(); };

            Panel allPanel = NewPanel(24, 178, 472, 64);
            Label a1 = NewLabelOn(allPanel, "완전 삭제", 18, 10, 230, 24, 10F, FontStyle.Bold, Danger);
            Label a2 = NewLabelOn(allPanel, "프로그램과 설정 · 프리셋 · 사용자 데이터를 모두 삭제합니다.", 18, 34, 330, 20, 8.5F, FontStyle.Regular, Muted);
            Button all = NewButton(allPanel, "완전 삭제", 365, 15, 88, 34, true);
            all.Click += delegate
            {
                if (MessageBox.Show(this,
                    "설정, 프리셋, 사용자 데이터까지 모두 삭제합니다.\n\n이 작업은 되돌릴 수 없습니다. 계속할까요?",
                    "CatLayer 완전 삭제",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    Choice = RemoveMode.Everything;
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };

            Button cancel = NewButton(this, "취소", 408, 258, 88, 32, false);
            cancel.Click += delegate { Choice = RemoveMode.None; DialogResult = DialogResult.Cancel; Close(); };
        }

        private Panel NewPanel(int x, int y, int w, int h)
        {
            Panel p = new Panel();
            p.SetBounds(x, y, w, h);
            p.BackColor = PanelColor;
            Controls.Add(p);
            return p;
        }

        private Label NewLabel(string text, int x, int y, int w, int h, float size, FontStyle style, Color color)
        {
            Label l = new Label();
            l.Text = text; l.SetBounds(x, y, w, h); l.ForeColor = color; l.BackColor = Color.Transparent;
            l.Font = new Font(Font.FontFamily, size, style); Controls.Add(l); return l;
        }

        private Label NewLabelOn(Control parent, string text, int x, int y, int w, int h, float size, FontStyle style, Color color)
        {
            Label l = new Label();
            l.Text = text; l.SetBounds(x, y, w, h); l.ForeColor = color; l.BackColor = Color.Transparent;
            l.Font = new Font(Font.FontFamily, size, style); parent.Controls.Add(l); return l;
        }

        private Button NewButton(Control parent, string text, int x, int y, int w, int h, bool danger)
        {
            Button b = new Button();
            b.Text = text; b.SetBounds(x, y, w, h); b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = danger ? Color.FromArgb(140, 67, 85) : Color.FromArgb(50, 73, 110);
            b.BackColor = Panel2Color; b.ForeColor = danger ? Danger : TextColor; b.Cursor = Cursors.Hand;
            parent.Controls.Add(b); return b;
        }
    }

    internal static class Program
    {
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CatLayer";
        private const string StartupRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "CatLayer";

        private static string AppVersion
        {
            get
            {
                try
                {
                    string path = Path.Combine(Application.StartupPath, "VERSION.txt");
                    if (File.Exists(path))
                    {
                        string value = File.ReadAllText(path).Trim();
                        if (!string.IsNullOrWhiteSpace(value)) return value;
                    }
                }
                catch { }
                return "unknown";
            }
        }

        private static string BuildLabel
        {
            get
            {
                try
                {
                    string path = Path.Combine(Application.StartupPath, "BUILD.txt");
                    if (File.Exists(path)) return File.ReadAllText(path).Trim();
                }
                catch { }
                return "";
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && string.Equals(args[0], "--register", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = RegisterUninstallEntry() ? 0 : 20;
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (UninstallForm form = new UninstallForm())
            {
                if (form.ShowDialog() != DialogResult.OK || form.Choice == RemoveMode.None) return;

                string appDir = Application.StartupPath;
                DirectoryInfo parent = Directory.GetParent(appDir);
                string dataRoot = parent != null ? parent.FullName : appDir;
                string installedExe = Path.Combine(appDir, "CatLayer.exe");

                CloseInstalledProgram(installedExe);
                RemoveShortcuts();
                RemoveStartupRegistry();
                RemoveUninstallRegistry();
                RemoveExplorerImageContextMenu();
                RemoveFileAssociations();

                string removePath = form.Choice == RemoveMode.Everything ? dataRoot : appDir;

                MessageBox.Show(
                    form.Choice == RemoveMode.Everything
                        ? "CatLayer와 모든 사용자 데이터를 제거합니다."
                        : "CatLayer 프로그램을 제거합니다.\n설정과 프리셋은 유지됩니다.",
                    "CatLayer 제거",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                ScheduleDirectoryRemoval(removePath);
            }
        }


        private static bool RegisterUninstallEntry()
        {
            try
            {
                string appDir = Application.StartupPath;
                string uninstaller = Application.ExecutablePath;
                string installedExe = Path.Combine(appDir, "CatLayer.exe");
                string uninstallCommand = "\"" + uninstaller + "\"";

                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    if (key == null) return false;
                    key.SetValue("DisplayName", "CatLayer v" + AppVersion + (string.IsNullOrWhiteSpace(BuildLabel) ? "" : " [" + BuildLabel + "]"), RegistryValueKind.String);
                    key.SetValue("DisplayVersion", AppVersion, RegistryValueKind.String);
                    key.SetValue("Publisher", "과출", RegistryValueKind.String);
                    key.SetValue("InstallLocation", appDir, RegistryValueKind.String);
                    key.SetValue("DisplayIcon", installedExe + ",0", RegistryValueKind.String);
                    key.SetValue("UninstallString", uninstallCommand, RegistryValueKind.String);
                    key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    key.SetValue("EstimatedSize", 2048, RegistryValueKind.DWord);
                }
                return true;
            }
            catch { return false; }
        }

        private static void CloseInstalledProgram(string installedExe)
        {
            try
            {
                string target = Path.GetFullPath(installedExe);
                Process[] processes = Process.GetProcessesByName("CatLayer");
                foreach (Process p in processes)
                {
                    try
                    {
                        string path = p.MainModule != null ? p.MainModule.FileName : "";
                        if (string.IsNullOrEmpty(path) || !string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase))
                            continue;
                        try { p.CloseMainWindow(); } catch { }
                        try
                        {
                            if (!p.WaitForExit(1200)) p.Kill();
                        }
                        catch { try { p.Kill(); } catch { } }
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
        }

        private static void RemoveShortcuts()
        {
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "CatLayer.lnk"));
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "CatLayer.lnk"));
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "CatLayer 제거.lnk"));
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Uninstall CatLayer.lnk"));
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void RemoveStartupRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRunKey, true))
                {
                    if (key != null) key.DeleteValue(StartupValueName, false);
                }
            }
            catch { }
        }

        private static void RemoveUninstallRegistry()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        }

        private static void RemoveExplorerImageContextMenu()
        {
            string[] extensions = new string[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
            foreach (string extension in extensions)
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\" + extension + @"\shell\CatLayerOverlay", false); } catch { }
            }
        }

        private static void RemoveFileAssociations()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.catlayerpreset", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CatLayer.Preset", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.catlayergroup", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CatLayer.Group", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.catlayerweb", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CatLayer.WebPackage", false); } catch { }
        }

        private static void ScheduleDirectoryRemoval(string path)
        {
            try
            {
                string tempDir = Path.GetTempPath();
                string cmd = "/c cd /d \"" + tempDir + "\" & ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + path + "\"";
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", cmd);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WorkingDirectory = tempDir;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch
            {
                MessageBox.Show("일부 파일을 자동으로 제거하지 못했습니다.\n\n" + path,
                    "CatLayer 제거", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
