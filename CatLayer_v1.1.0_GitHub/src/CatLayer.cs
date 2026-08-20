using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Media;
using System.Net;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Thread = System.Threading.Thread;
using Mutex = System.Threading.Mutex;
using EventWaitHandle = System.Threading.EventWaitHandle;
using EventResetMode = System.Threading.EventResetMode;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CatLayer
{
    internal static class AppInfo
    {
        public static readonly string Version = LoadVersion();

        private static string LoadVersion()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VERSION.txt");
                if (File.Exists(path))
                {
                    string value = File.ReadAllText(path, Encoding.ASCII).Trim();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
            catch { }
            return "unknown";
        }
    }
    internal static class WebOverlayEnvironment
    {
        private static readonly object Sync = new object();
        private static Task<CoreWebView2Environment> sharedEnvironment;

        public static Task<CoreWebView2Environment> GetAsync()
        {
            lock (Sync)
            {
                if (sharedEnvironment == null)
                {
                    string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatLayer", "WebData");
                    Directory.CreateDirectory(dataDir);
                    CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions();
                    sharedEnvironment = CoreWebView2Environment.CreateAsync(null, dataDir, options);
                }
                return sharedEnvironment;
            }
        }

        public static void WarmUp()
        {
            try
            {
                Task<CoreWebView2Environment> ignored = GetAsync();
            }
            catch { }
        }
    }

    internal sealed class GitHubReleaseInfo
    {
        public string Version;
        public string Tag;
        public string Name;
        public string Body;
        public string HtmlUrl;
        public string ZipName;
        public string ZipUrl;
        public string ShaUrl;
    }

    internal static class GitHubUpdateService
    {
        public const string Owner = "Cats0911";
        public const string Repository = "CatLayer";
        private const string LatestReleaseApi = "https://api.github.com/repos/Cats0911/CatLayer/releases/latest";

        private static string DownloadText(string url)
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            using (WebClient wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.UserAgent] = "CatLayer/" + AppInfo.Version;
                wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                wc.Headers["X-GitHub-Api-Version"] = "2026-03-10";
                return wc.DownloadString(url);
            }
        }

        private static string JsonValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            Match m = Regex.Match(json, "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*\\\"(?<v>(?:\\\\.|[^\\\"])*)\\\"", RegexOptions.Singleline);
            return m.Success ? JsonUnescape(m.Groups["v"].Value) : "";
        }

        private static string JsonUnescape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try
            {
                return Regex.Replace(value, "\\\\(u[0-9a-fA-F]{4}|[\\\\\\\"/bfnrt])", delegate(Match m)
                {
                    string x = m.Groups[1].Value;
                    if (x.StartsWith("u", StringComparison.OrdinalIgnoreCase))
                    {
                        int code;
                        if (int.TryParse(x.Substring(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out code)) return ((char)code).ToString();
                    }
                    switch (x)
                    {
                        case "\\": return "\\";
                        case "\"": return "\"";
                        case "/": return "/";
                        case "b": return "\b";
                        case "f": return "\f";
                        case "n": return "\n";
                        case "r": return "\r";
                        case "t": return "\t";
                    }
                    return m.Value;
                });
            }
            catch { return value; }
        }

        private static string NormalizeVersion(string value)
        {
            value = (value ?? "").Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);
            int dash = value.IndexOf('-');
            if (dash >= 0) value = value.Substring(0, dash);
            return value.Trim();
        }

        public static int CompareVersions(string left, string right)
        {
            Version a, b;
            if (Version.TryParse(NormalizeVersion(left), out a) && Version.TryParse(NormalizeVersion(right), out b)) return a.CompareTo(b);
            return string.Compare(NormalizeVersion(left), NormalizeVersion(right), StringComparison.OrdinalIgnoreCase);
        }

        public static GitHubReleaseInfo GetLatestStableRelease()
        {
            string json;
            try { json = DownloadText(LatestReleaseApi); }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) return null;
                throw;
            }

            string tag = JsonValue(json, "tag_name");
            if (string.IsNullOrWhiteSpace(tag)) return null;
            GitHubReleaseInfo info = new GitHubReleaseInfo();
            info.Tag = tag;
            info.Version = NormalizeVersion(tag);
            info.Name = JsonValue(json, "name");
            info.Body = JsonValue(json, "body");
            info.HtmlUrl = JsonValue(json, "html_url");

            MatchCollection assets = Regex.Matches(json,
                "\\\"name\\\"\\s*:\\s*\\\"(?<name>[^\\\"]+)\\\"(?:(?!\\\"name\\\"\\s*:).){0,3000}?\\\"browser_download_url\\\"\\s*:\\s*\\\"(?<url>[^\\\"]+)\\\"",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match m in assets)
            {
                string name = JsonUnescape(m.Groups["name"].Value);
                string url = JsonUnescape(m.Groups["url"].Value);
                if (string.Equals(name, "SHA256.txt", StringComparison.OrdinalIgnoreCase)) info.ShaUrl = url;
                bool preferred = Regex.IsMatch(name, @"^CatLayer_v?" + Regex.Escape(info.Version) + @"_update\.zip$", RegexOptions.IgnoreCase);
                bool fallback = Regex.IsMatch(name, @"^CatLayer_v?" + Regex.Escape(info.Version) + @"\.zip$", RegexOptions.IgnoreCase);
                if (preferred || (fallback && string.IsNullOrEmpty(info.ZipUrl)))
                {
                    info.ZipName = name;
                    info.ZipUrl = url;
                    if (preferred) { /* preferred asset wins */ }
                }
            }
            return info;
        }

        public static void DownloadFile(string url, string destination)
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            using (WebClient wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "CatLayer/" + AppInfo.Version;
                wc.Headers[HttpRequestHeader.Accept] = "application/octet-stream";
                wc.DownloadFile(url, destination);
            }
        }

        public static string ComputeSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static string ReadExpectedSha(string shaPath, string zipName)
        {
            foreach (string raw in File.ReadAllLines(shaPath))
            {
                string line = (raw ?? "").Trim();
                Match m = Regex.Match(line, @"^(?<hash>[0-9a-fA-F]{64})(?:\s+\*?(?<name>.+))?$");
                if (!m.Success) continue;
                string listedName = m.Groups["name"].Value.Trim();
                if (listedName.Length == 0 || string.Equals(Path.GetFileName(listedName), zipName, StringComparison.OrdinalIgnoreCase))
                    return m.Groups["hash"].Value.ToLowerInvariant();
            }
            return "";
        }
    }

    // CatLayer 1.1 development line. Runtime version is read from VERSION.txt.
    internal enum ItemType { Image = 0, Text = 1, Timer = 2, ObsProgram = 3, Web = 5 }
    internal enum TimerMode { OneShot = 0, Repeat = 1, Stopwatch = 2 }
    internal enum ImageScaleMode { Fit = 0, Fill = 1, Stretch = 2 }
    internal enum EditorMode { Fixed = 0, Normal = 1, WebControl = 2 }

    internal sealed class DarkNumberBox : UserControl
    {
        private readonly TextBox box = new TextBox();
        private readonly Button up = new Button();
        private readonly Button down = new Button();
        private decimal value;
        private decimal minimum = 0;
        private decimal maximum = 100;
        private bool internalChange;
        public event EventHandler ValueChanged;

        public DarkNumberBox()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.FromArgb(18, 37, 66);
            ForeColor = Color.FromArgb(236, 240, 248);
            box.BorderStyle = BorderStyle.None;
            box.BackColor = BackColor;
            box.ForeColor = ForeColor;
            box.TextAlign = HorizontalAlignment.Center;
            box.Text = "0";
            box.TextChanged += delegate { ParseText(false); };
            box.Leave += delegate { ParseText(true); };
            box.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { ParseText(true); e.SuppressKeyPress = true; } };
            Controls.Add(box);
            SetupStepButton(up, "+", 1);
            SetupStepButton(down, "-", -1);
            Controls.Add(up); Controls.Add(down);
            Height = 28;
            Resize += delegate { LayoutChildren(); };
            Paint += delegate(object sender, PaintEventArgs e) { using (Pen pen = new Pen(Color.FromArgb(47, 67, 102))) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); };
            LayoutChildren();
        }

        private void SetupStepButton(Button b, string text, int delta)
        {
            b.Text = text; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
            b.BackColor = Color.FromArgb(25, 47, 82); b.ForeColor = ForeColor; b.TabStop = false;
            b.Click += delegate { Value = Value + delta; };
        }

        private void LayoutChildren()
        {
            int bw = Math.Max(18, Math.Min(24, Width / 4));
            box.SetBounds(7, Math.Max(2, (Height - box.PreferredHeight) / 2), Math.Max(20, Width - bw - 12), box.PreferredHeight);
            up.SetBounds(Width - bw, 1, bw - 1, Math.Max(12, Height / 2 - 1));
            down.SetBounds(Width - bw, Math.Max(12, Height / 2), bw - 1, Math.Max(12, Height - Height / 2 - 1));
        }

        private void ParseText(bool normalize)
        {
            if (internalChange) return;
            decimal parsed;
            if (decimal.TryParse(box.Text, out parsed))
            {
                if (parsed < minimum) parsed = minimum;
                if (parsed > maximum) parsed = maximum;
                if (parsed != value)
                {
                    value = parsed;
                    EventHandler h = ValueChanged; if (h != null) h(this, EventArgs.Empty);
                }
                if (normalize) UpdateText();
            }
            else if (normalize) UpdateText();
        }

        private void UpdateText()
        {
            internalChange = true; box.Text = value.ToString("0"); internalChange = false;
        }

        public decimal Value
        {
            get { return value; }
            set
            {
                decimal v = value; if (v < minimum) v = minimum; if (v > maximum) v = maximum;
                if (this.value == v) { UpdateText(); return; }
                this.value = v; UpdateText(); EventHandler h = ValueChanged; if (h != null) h(this, EventArgs.Empty);
            }
        }
        public decimal Minimum { get { return minimum; } set { minimum = value; if (this.value < minimum) Value = minimum; } }
        public decimal Maximum { get { return maximum; } set { maximum = value; if (this.value > maximum) Value = maximum; } }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); box.Enabled = Enabled; up.Enabled = Enabled; down.Enabled = Enabled; Invalidate(); }
    }

    internal sealed class DarkCheckBox : CheckBox
    {
        public DarkCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            BackColor = Color.Transparent;
            ForeColor = Color.FromArgb(236, 240, 248);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Color.FromArgb(13,29,55) : Parent.BackColor);
            int boxSize = Math.Min(16, Math.Max(12, Height - 8));
            Rectangle r = new Rectangle(1, (Height - boxSize) / 2, boxSize, boxSize);
            Color border = Enabled ? Color.FromArgb(70, 88, 126) : Color.FromArgb(46, 57, 79);
            using (Pen p = new Pen(border)) e.Graphics.DrawRectangle(p, r);
            if (Checked)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(126, 82, 245))) e.Graphics.FillRectangle(b, r.X + 1, r.Y + 1, r.Width - 1, r.Height - 1);
                using (Pen p = new Pen(Color.White, 2)) { e.Graphics.DrawLine(p, r.X + 4, r.Y + 8, r.X + 7, r.Y + 11); e.Graphics.DrawLine(p, r.X + 7, r.Y + 11, r.X + 13, r.Y + 5); }
            }
            Rectangle tr = new Rectangle(r.Right + 8, 0, Math.Max(1, Width - r.Right - 8), Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, tr, Enabled ? ForeColor : Color.FromArgb(100,110,130), TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    internal static class AudioAlert
    {
        private static readonly object Sync = new object();
        private static readonly SoundPlayer Player = new SoundPlayer();
        private const string MciAlias = "CatLayerAlarm";
        private static string mciPath = "";

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciSendString(string command, StringBuilder returnValue, int returnLength, IntPtr callback);

        public static string DefaultAlarmPath
        {
            get
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "cute_alarm.wav");
            }
        }

        public static void Play(string customPath)
        {
            string path = !string.IsNullOrEmpty(customPath) && File.Exists(customPath)
                ? customPath
                : DefaultAlarmPath;
            try
            {
                if (!File.Exists(path))
                {
                    SystemSounds.Asterisk.Play();
                    return;
                }

                lock (Sync)
                {
                    string ext = Path.GetExtension(path);
                    if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
                    {
                        CloseMci();
                        Player.Stop();
                        Player.SoundLocation = path;
                        Player.Play();
                        return;
                    }

                    Player.Stop();
                    if (!string.Equals(mciPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        CloseMci();
                        int openResult = mciSendString("open \"" + path + "\" alias " + MciAlias, null, 0, IntPtr.Zero);
                        if (openResult != 0)
                        {
                            SystemSounds.Asterisk.Play();
                            return;
                        }
                        mciPath = path;
                    }

                    mciSendString("seek " + MciAlias + " to start", null, 0, IntPtr.Zero);
                    int playResult = mciSendString("play " + MciAlias, null, 0, IntPtr.Zero);
                    if (playResult != 0) SystemSounds.Asterisk.Play();
                }
            }
            catch
            {
                try { SystemSounds.Asterisk.Play(); } catch { }
            }
        }

        private static void CloseMci()
        {
            try
            {
                if (!string.IsNullOrEmpty(mciPath))
                    mciSendString("close " + MciAlias, null, 0, IntPtr.Zero);
            }
            catch { }
            mciPath = "";
        }
    }

    internal static class UiPrompt
    {
        public static int? AskOpacity(IWin32Window owner, int current)
        {
            using (Form f = new Form())
            using (NumericUpDown n = new NumericUpDown())
            using (Button ok = new Button())
            using (Button cancel = new Button())
            using (Label label = new Label())
            {
                f.Text = "투명도";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(255, 118);
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.TopMost = true;

                label.Text = "투명도 (0 = 안 보임, 100 = 완전 표시)";
                label.AutoSize = true;
                label.Location = new Point(12, 12);
                f.Controls.Add(label);

                n.Minimum = 0; n.Maximum = 100; n.Value = Math.Max(0, Math.Min(100, current));
                n.SetBounds(15, 38, 225, 24);
                f.Controls.Add(n);

                ok.Text = "확인"; ok.DialogResult = DialogResult.OK; ok.SetBounds(58, 76, 80, 28);
                cancel.Text = "취소"; cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(148, 76, 80, 28);
                f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;

                return f.ShowDialog(owner) == DialogResult.OK ? (int?)n.Value : null;
            }
        }

        public static string AskText(IWin32Window owner, string title, string labelText, string initial)
        {
            using (Form f = new Form())
            using (TextBox box = new TextBox())
            using (Button ok = new Button())
            using (Button cancel = new Button())
            using (Label label = new Label())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(330, 126);
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = false;

                label.Text = labelText; label.AutoSize = true; label.Location = new Point(12, 12); f.Controls.Add(label);
                box.Text = initial ?? ""; box.SetBounds(15, 38, 300, 24); f.Controls.Add(box);
                ok.Text = "확인"; ok.DialogResult = DialogResult.OK; ok.SetBounds(145, 82, 80, 28);
                cancel.Text = "취소"; cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(235, 82, 80, 28);
                f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.Shown += delegate { box.Focus(); box.SelectAll(); };
                return f.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
            }
        }
    }

    internal static class Native
    {
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;

        public const long WS_CHILD = 0x40000000L;
        public const long WS_POPUP = unchecked((long)0x80000000L);
        public const long WS_CAPTION = 0x00C00000L;
        public const long WS_THICKFRAME = 0x00040000L;
        public const long WS_SYSMENU = 0x00080000L;
        public const long WS_MINIMIZEBOX = 0x00020000L;
        public const long WS_MAXIMIZEBOX = 0x00010000L;
        public const long WS_BORDER = 0x00800000L;
        public const long WS_DLGFRAME = 0x00400000L;
        public const long WS_CLIPCHILDREN = 0x02000000L;
        public const long WS_CLIPSIBLINGS = 0x04000000L;

        public const long WS_EX_TRANSPARENT = 0x00000020L;
        public const long WS_EX_LAYERED = 0x00080000L;
        public const long WS_EX_TOOLWINDOW = 0x00000080L;
        public const long WS_EX_TOPMOST = 0x00000008L;
        public const long WS_EX_NOACTIVATE = 0x08000000L;
        public const long WS_EX_APPWINDOW = 0x00040000L;

        public const int WM_HOTKEY = 0x0312;
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int WM_CLOSE = 0x0010;
        public const int MA_NOACTIVATE = 3;
        public const int HTTRANSPARENT = -1;
        public const int HTCLIENT = 1;
        public const int MOD_ALT = 0x0001;
        public const int MOD_CONTROL = 0x0002;
        public const int MOD_SHIFT = 0x0004;
        public const int MOD_WIN = 0x0008;
        public const int MOD_NOREPEAT = 0x4000;
        public const int VK_F1 = 0x70;
        public const int VK_F7 = 0x76;
        public const int VK_F8 = 0x77;
        public const int VK_F9 = 0x78;
        public const int VK_F10 = 0x79;

        public const int ULW_ALPHA = 0x00000002;
        public const uint LWA_COLORKEY = 0x00000001;
        public const uint LWA_ALPHA = 0x00000002;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;

        public const uint DWM_TNP_RECTDESTINATION = 0x00000001;
        public const uint DWM_TNP_RECTSOURCE = 0x00000002;
        public const uint DWM_TNP_OPACITY = 0x00000004;
        public const uint DWM_TNP_VISIBLE = 0x00000008;
        public const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; public POINT(int X, int Y) { x = X; y = Y; } }
        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; public SIZE(int X, int Y) { cx = X; cy = Y; } }
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_THUMBNAIL_PROPERTIES
        {
            public uint dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            public int fVisible;
            public int fSourceClientAreaOnly;
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)] public static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)] public static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)] public static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)] public static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool EnableWindow(IntPtr hWnd, bool enable);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte alpha, uint flags);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);
        [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);
        [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);
        [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(IntPtr hThumbnail, out SIZE pSize);

        public static long GetLong(IntPtr hwnd, int index)
        {
            if (IntPtr.Size == 8) return unchecked((uint)GetWindowLongPtr64(hwnd, index).ToInt64());
            return unchecked((uint)GetWindowLong32(hwnd, index));
        }
        public static void SetLong(IntPtr hwnd, int index, long value)
        {
            int low32 = unchecked((int)(uint)value);
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, index, new IntPtr(low32));
            else SetWindowLong32(hwnd, index, low32);
        }
        public static long GetStyle(IntPtr hwnd) { return GetLong(hwnd, GWL_STYLE); }
        public static long GetExStyle(IntPtr hwnd) { return GetLong(hwnd, GWL_EXSTYLE); }
        public static void SetStyle(IntPtr hwnd, long value) { SetLong(hwnd, GWL_STYLE, value); }
        public static void SetExStyle(IntPtr hwnd, long value) { SetLong(hwnd, GWL_EXSTYLE, value); }
    }

    internal static class ObsBridge
    {
        private static readonly string BaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatLayer");
        private static readonly string LegacyBaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightOverlay");
        public static readonly string CommandPath = Path.Combine(BaseDir, "obs_bridge_command.txt");
        public static readonly string StatusPath = Path.Combine(BaseDir, "obs_bridge_status.txt");
        private static readonly string LegacyCommandPath = Path.Combine(LegacyBaseDir, "obs_bridge_command.txt");
        private static readonly string LegacyStatusPath = Path.Combine(LegacyBaseDir, "obs_bridge_status.txt");

        private static bool IsStatusAlive(string path)
        {
            try
            {
                return File.Exists(path) && (DateTime.Now - File.GetLastWriteTime(path)).TotalSeconds <= 4.0;
            }
            catch { return false; }
        }

        public static string RequestOpenProgram()
        {
            try
            {
                Directory.CreateDirectory(BaseDir);
                string token = Guid.NewGuid().ToString("N");
                string target = IsStatusAlive(StatusPath) ? CommandPath : (IsStatusAlive(LegacyStatusPath) ? LegacyCommandPath : CommandPath);
                string dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(target, "OPEN_PROGRAM|" + token, new UTF8Encoding(false));
                return token;
            }
            catch { return ""; }
        }

        public static bool IsAlive()
        {
            return IsStatusAlive(StatusPath) || IsStatusAlive(LegacyStatusPath);
        }

        public static string ReadStatus()
        {
            try
            {
                string path = IsStatusAlive(StatusPath) ? StatusPath : LegacyStatusPath;
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : "";
            }
            catch { return ""; }
        }
    }

    internal sealed class OverlaySelectionFrameForm : Form
    {
        private static readonly Color KeyColor = Color.FromArgb(2, 3, 4);
        private const int Outside = 6;
        private bool locked;
        private bool interactive;
        private OverlayItemForm interactiveTarget;

        public OverlaySelectionFrameForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = KeyColor;
            TransparencyKey = KeyColor;
            DoubleBuffered = true;
            MouseDown += FrameMouseDown;
            MouseMove += FrameMouseMove;
            MouseUp += FrameMouseUp;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= unchecked((int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE));
                if (!interactive) cp.ExStyle |= unchecked((int)Native.WS_EX_TRANSPARENT);
                return cp;
            }
        }

        private bool IsResizeGrip(Point p)
        {
            int cornerX = ClientSize.Width - Outside;
            int cornerY = ClientSize.Height - Outside;
            return Math.Abs(p.X - cornerX) <= 9 && Math.Abs(p.Y - cornerY) <= 9;
        }

        private bool IsInteractiveBorder(Point p)
        {
            if (IsResizeGrip(p)) return true;
            int innerLeft = Outside - 3;
            int innerTop = Outside - 3;
            int innerRight = ClientSize.Width - Outside + 3;
            int innerBottom = ClientSize.Height - Outside + 3;
            bool nearOuter = p.X <= Outside + 3 || p.Y <= Outside + 3 || p.X >= ClientSize.Width - Outside - 3 || p.Y >= ClientSize.Height - Outside - 3;
            bool inBand = p.X >= innerLeft && p.X <= innerRight && p.Y >= innerTop && p.Y <= innerBottom;
            return nearOuter && inBand;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCHITTEST)
            {
                if (!interactive)
                {
                    m.Result = new IntPtr(Native.HTTRANSPARENT);
                    return;
                }
                int packed = unchecked((int)m.LParam.ToInt64());
                int sx = (short)(packed & 0xFFFF);
                int sy = (short)((packed >> 16) & 0xFFFF);
                Point client = PointToClient(new Point(sx, sy));
                if (!IsInteractiveBorder(client))
                {
                    m.Result = new IntPtr(Native.HTTRANSPARENT);
                    return;
                }
                m.Result = new IntPtr(1); // HTCLIENT
                return;
            }
            if (m.Msg == Native.WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(Native.MA_NOACTIVATE);
                return;
            }
            base.WndProc(ref m);
        }

        private void ApplyInteractionStyle(bool enable)
        {
            interactive = enable;
            if (!IsHandleCreated) return;
            long ex = Native.GetExStyle(Handle);
            if (enable) ex &= ~Native.WS_EX_TRANSPARENT;
            else ex |= Native.WS_EX_TRANSPARENT;
            ex |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
            Native.SetExStyle(Handle, ex);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        }

        public void SyncToOverlay(Rectangle overlayBounds, bool showFrame, bool isLocked, OverlayItemForm target, bool allowInteractiveBorder)
        {
            if (IsDisposed) return;
            locked = isLocked;
            interactiveTarget = allowInteractiveBorder ? target : null;
            ApplyInteractionStyle(allowInteractiveBorder && !isLocked && target != null);
            if (!showFrame || overlayBounds.Width <= 0 || overlayBounds.Height <= 0)
            {
                if (Visible) Hide();
                return;
            }

            Rectangle frameBounds = Rectangle.Inflate(overlayBounds, Outside, Outside);
            if (Bounds != frameBounds) Bounds = frameBounds;
            if (!Visible) Show();
            Invalidate();
            BringFrameToFront();
        }

        public void BringFrameToFront()
        {
            if (!Visible || !IsHandleCreated || IsDisposed) return;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        private void FrameMouseDown(object sender, MouseEventArgs e)
        {
            if (!interactive || interactiveTarget == null || e.Button != MouseButtons.Left) return;
            Capture = true;
            interactiveTarget.BeginWebFrameDrag(IsResizeGrip(e.Location));
        }

        private void FrameMouseMove(object sender, MouseEventArgs e)
        {
            if (!interactive || interactiveTarget == null) return;
            if (Capture && e.Button == MouseButtons.Left)
                interactiveTarget.ContinueWebFrameDrag();
            else
                Cursor = IsResizeGrip(e.Location) ? Cursors.SizeNWSE : (IsInteractiveBorder(e.Location) ? Cursors.SizeAll : Cursors.Default);
        }

        private void FrameMouseUp(object sender, MouseEventArgs e)
        {
            if (!interactive || interactiveTarget == null || e.Button != MouseButtons.Left) return;
            interactiveTarget.EndWebFrameDrag();
            Capture = false;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(KeyColor);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(KeyColor);
            Color edge = locked ? Color.Gold : Color.FromArgb(235, 80, 200, 255);

            using (Pen p = new Pen(edge, 2f))
            {
                Rectangle r = new Rectangle(Outside - 1, Outside - 1,
                    Math.Max(1, ClientSize.Width - (Outside * 2) + 2),
                    Math.Max(1, ClientSize.Height - (Outside * 2) + 2));
                e.Graphics.DrawRectangle(p, r);
            }

            if (!locked)
            {
                const int grip = 10;
                int cornerX = ClientSize.Width - Outside;
                int cornerY = ClientSize.Height - Outside;
                Rectangle gripRect = new Rectangle(cornerX - grip / 2, cornerY - grip / 2, grip, grip);
                using (Brush b = new SolidBrush(edge)) e.Graphics.FillRectangle(b, gripRect);
                using (Pen outline = new Pen(Color.FromArgb(220, 20, 28, 45), 1f)) e.Graphics.DrawRectangle(outline, gripRect);
            }
        }
    }

    internal sealed class OverlayItemForm : Form
    {
        private readonly MainForm owner;
        public readonly ItemType Type;
        public string Data { get; private set; }
        public readonly int DurationSeconds;
        public int OpacityPercent { get; private set; }
        public TimerMode TimerKind { get; private set; }
        public string AlarmPath { get; private set; }
        public string CustomName { get; private set; }
        public bool IsOverlayVisible { get { return userVisible; } }
        public bool Locked { get; private set; }
        public bool PreserveAspect { get; private set; }
        public ImageScaleMode ScaleMode { get; private set; }
        public int RotationDegrees { get; private set; }
        public bool FlipHorizontal { get; private set; }
        public bool FlipVertical { get; private set; }
        public int GroupId { get; private set; }
        public int CropLeft { get; private set; }
        public int CropTop { get; private set; }
        public int CropRight { get; private set; }
        public int CropBottom { get; private set; }
        public int WebZoomPercent { get; private set; }
        public string WebCustomCss { get; private set; }
        public bool SupportsTransform { get { return Type == ItemType.Image || Type == ItemType.Text || Type == ItemType.Timer; } }
        public Size NativeImageSize { get { return image == null ? Size.Empty : new Size(image.Width, image.Height); } }

        public static Image LoadRasterImageFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try { return Image.FromFile(path); }
            catch
            {
                // System.Drawing on .NET Framework cannot decode native WebP. Try Windows Imaging Component
                // as a fallback. This also allows files whose extension is .webp but whose payload is JPEG/PNG.
                try
                {
                    Uri uri = new Uri(Path.GetFullPath(path), UriKind.Absolute);
                    System.Windows.Media.Imaging.BitmapDecoder decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                        uri,
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    if (decoder == null || decoder.Frames == null || decoder.Frames.Count == 0) return null;
                    System.Windows.Media.Imaging.BitmapSource source = decoder.Frames[0];
                    System.Windows.Media.Imaging.FormatConvertedBitmap converted = new System.Windows.Media.Imaging.FormatConvertedBitmap();
                    converted.BeginInit();
                    converted.Source = source;
                    converted.DestinationFormat = System.Windows.Media.PixelFormats.Bgra32;
                    converted.EndInit();
                    converted.Freeze();
                    int w = converted.PixelWidth, h = converted.PixelHeight;
                    if (w <= 0 || h <= 0) return null;
                    int stride = checked(w * 4);
                    byte[] pixels = new byte[checked(stride * h)];
                    converted.CopyPixels(pixels, stride, 0);
                    Bitmap bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    BitmapData data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        for (int y = 0; y < h; y++)
                            Marshal.Copy(pixels, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), stride);
                    }
                    finally { bitmap.UnlockBits(data); }
                    return bitmap;
                }
                catch { return null; }
            }
        }

        private Rectangle GetUnrotatedVisualRectLocal(Size hostSize)
        {
            int hostW = Math.Max(1, hostSize.Width);
            int hostH = Math.Max(1, hostSize.Height);
            Rectangle full = new Rectangle(0, 0, hostW, hostH);

            if (Type == ItemType.Image && image != null && image.Width > 0 && image.Height > 0)
            {
                int srcX = Math.Max(0, Math.Min(image.Width - 1, (int)Math.Round(image.Width * (CropLeft / 10000.0))));
                int srcY = Math.Max(0, Math.Min(image.Height - 1, (int)Math.Round(image.Height * (CropTop / 10000.0))));
                int srcRight = Math.Max(srcX + 1, Math.Min(image.Width, image.Width - (int)Math.Round(image.Width * (CropRight / 10000.0))));
                int srcBottom = Math.Max(srcY + 1, Math.Min(image.Height, image.Height - (int)Math.Round(image.Height * (CropBottom / 10000.0))));
                int srcW = Math.Max(1, srcRight - srcX);
                int srcH = Math.Max(1, srcBottom - srcY);

                // Fill and Stretch visibly occupy the complete host rectangle because Fill is clipped to it.
                if (ScaleMode == ImageScaleMode.Fill || ScaleMode == ImageScaleMode.Stretch) return full;

                double scale = Math.Min(hostW / (double)srcW, hostH / (double)srcH);
                int drawW = Math.Max(1, (int)Math.Round(srcW * scale));
                int drawH = Math.Max(1, (int)Math.Round(srcH * scale));
                return new Rectangle((hostW - drawW) / 2, (hostH - drawH) / 2, drawW, drawH);
            }

            if (Type == ItemType.ObsProgram)
            {
                int srcW = ObsRenderWidth, srcH = ObsRenderHeight;
                Native.SIZE sourceSize;
                try
                {
                    if (dwmThumbnail != IntPtr.Zero && Native.DwmQueryThumbnailSourceSize(dwmThumbnail, out sourceSize) == 0 && sourceSize.cx > 0 && sourceSize.cy > 0)
                    {
                        srcW = sourceSize.cx;
                        srcH = sourceSize.cy;
                    }
                }
                catch { }
                double scale = Math.Min(hostW / (double)Math.Max(1, srcW), hostH / (double)Math.Max(1, srcH));
                int drawW = Math.Max(1, (int)Math.Round(srcW * scale));
                int drawH = Math.Max(1, (int)Math.Round(srcH * scale));
                return new Rectangle((hostW - drawW) / 2, (hostH - drawH) / 2, drawW, drawH);
            }

            return full;
        }

        private Rectangle ApplyRotationToVisualRect(Rectangle localRect, Size hostSize)
        {
            if (!SupportsTransform || RotationDegrees == 0) return localRect;
            double radians = RotationDegrees * Math.PI / 180.0;
            double cos = Math.Cos(radians), sin = Math.Sin(radians);
            double cx = hostSize.Width / 2.0, cy = hostSize.Height / 2.0;
            PointF[] points = new PointF[]
            {
                new PointF(localRect.Left, localRect.Top), new PointF(localRect.Right, localRect.Top),
                new PointF(localRect.Right, localRect.Bottom), new PointF(localRect.Left, localRect.Bottom)
            };
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (PointF point in points)
            {
                double dx = point.X - cx, dy = point.Y - cy;
                double x = cx + dx * cos - dy * sin;
                double y = cy + dx * sin + dy * cos;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
            Rectangle rotated = Rectangle.FromLTRB((int)Math.Floor(minX), (int)Math.Floor(minY), (int)Math.Ceiling(maxX), (int)Math.Ceiling(maxY));
            return Rectangle.Intersect(new Rectangle(0, 0, Math.Max(1, hostSize.Width), Math.Max(1, hostSize.Height)), rotated);
        }

        public Rectangle GetVisualContentBounds(Rectangle hostBounds)
        {
            if (hostBounds.Width <= 0 || hostBounds.Height <= 0) return hostBounds;
            Rectangle local = GetUnrotatedVisualRectLocal(hostBounds.Size);
            local = ApplyRotationToVisualRect(local, hostBounds.Size);
            if (local.Width <= 0 || local.Height <= 0) return hostBounds;
            local.Offset(hostBounds.Left, hostBounds.Top);
            return local;
        }

        public void NormalizeFitBoundsToVisualContent()
        {
            // When aspect lock is enabled, a Fit image does not need an invisible letterbox container.
            // Shrinking the host to the already-visible pixels keeps the on-screen image unchanged while
            // making Bounds, snapping, resize hit testing and the edit frame refer to the same rectangle.
            if (Type != ItemType.Image || image == null || ScaleMode != ImageScaleMode.Fit || !PreserveAspect || RotationDegrees != 0) return;
            Rectangle visual = GetVisualContentBounds(Bounds);
            if (visual.Width < MinimumSize.Width || visual.Height < MinimumSize.Height) return;
            if (Math.Abs(visual.Left - Bounds.Left) <= 1 && Math.Abs(visual.Top - Bounds.Top) <= 1 &&
                Math.Abs(visual.Width - Bounds.Width) <= 1 && Math.Abs(visual.Height - Bounds.Height) <= 1) return;
            Bounds = visual;
        }

        private Image image;
        private bool imageAnimated;
        private bool gifAnimatorActive;
        private EventHandler gifAnimatorHandler;
        private readonly Timer gifTimer = new Timer();
        private readonly Timer renderTimer = new Timer();
        private long timerStartTicks;
        private bool timerCompleted;
        private long lastRepeatCycle;
        // Timer text is second-based. Avoid rebuilding the layered window ten times per second
        // when the visible text has not changed.
        private string lastRenderedTimerText = null;
        private bool editMode = true;
        private bool userVisible = true;
        private bool overlayVisible = true;
        private bool dragging;
        private Point dragStartMouse;
        private Rectangle dragStartBounds;
        private readonly Dictionary<OverlayItemForm, Rectangle> dragGroupStartBounds = new Dictionary<OverlayItemForm, Rectangle>();
        private int dragStartCropLeft;
        private int dragStartCropTop;
        private int dragStartCropRight;
        private int dragStartCropBottom;
        private bool cropDragging;
        private DragMode dragMode = DragMode.None;
        private bool mouseRotating;
        private bool mouseRotateMoved;
        private Point mouseRotateStartCursor;
        private Point mouseRotateCenterScreen;
        private double mouseRotateStartAngle;
        private readonly Dictionary<OverlayItemForm, int> mouseRotateStartDegrees = new Dictionary<OverlayItemForm, int>();
        private readonly OverlaySelectionFrameForm selectionFrame = new OverlaySelectionFrameForm();

        private WebView2 webView;
        private Label webStatusLabel;
        private bool webInitialized;
        private bool webInitializing;
        private bool webInitialHostRefreshDone;
        private int webResizeRefreshVersion;
        // For local CatLayer widgets only. Remote pages never get a native API.
        private string webResizeHost = "";

        private readonly Timer obsTimer = new Timer();
        private IntPtr obsWindow = IntPtr.Zero;
        private IntPtr dwmThumbnail = IntPtr.Zero;
        private long originalObsStyle;
        private long originalObsExStyle;
        private Native.RECT originalObsRect;
        private bool originalObsRectValid;
        private bool obsAttached;
        private bool obsOpenedByBridge;
        private bool bridgeRequestPending;
        private bool obsProjectorRequested;
        private int obsSearchAttempts;
        private readonly HashSet<IntPtr> obsWindowsBeforeRequest = new HashSet<IntPtr>();
        private string obsStatus = "OBS Program 연결 대기 중...";

        private const int ObsRenderWidth = 1920;
        private const int ObsRenderHeight = 1080;
        private const int MaxObsSearchAttempts = 12;
        private const int ObsSearchIntervalMs = 500;
        private const int ObsParkingX = -20000;
        private const int ObsParkingY = -20000;
        private static readonly Color ObsTransparentKey = Color.FromArgb(1, 2, 3);
        private static readonly Color WebTransparentKey = Color.FromArgb(4, 5, 6);

        private enum DragMode { None, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }
        private const int Grip = 10;

        protected override bool ShowWithoutActivation { get { return !editMode; } }

        public OverlayItemForm(MainForm app, ItemType type, string data, int seconds, int initialOpacityPercent, TimerMode timerMode, string alarmPath, string customName)
        {
            owner = app;
            Type = type;
            Data = data ?? "";
            CustomName = customName ?? "";
            TimerKind = timerMode;
            DurationSeconds = (type == ItemType.Timer && timerMode != TimerMode.Stopwatch) ? Math.Max(1, seconds) : Math.Max(0, seconds);
            AlarmPath = alarmPath ?? "";
            OpacityPercent = Math.Max(0, Math.Min(100, initialOpacityPercent));
            PreserveAspect = true;
            ScaleMode = ImageScaleMode.Fit;
            RotationDegrees = 0;
            FlipHorizontal = false;
            FlipVertical = false;
            GroupId = 0;
            CropLeft = CropTop = CropRight = CropBottom = 0;
            WebZoomPercent = 100;
            WebCustomCss = "";

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(100, 60);
            BackColor = Type == ItemType.ObsProgram ? ObsTransparentKey : (Type == ItemType.Web ? WebTransparentKey : Color.FromArgb(18, 18, 18));
            if (Type == ItemType.Web)
            {
                TransparencyKey = WebTransparentKey;
            }
            DoubleBuffered = Type != ItemType.ObsProgram && Type != ItemType.Web;

            if (Type == ItemType.Image)
            {
                try
                {
                    Image src = LoadRasterImageFile(Data);
                    if (src == null) throw new InvalidDataException("이미지 파일을 디코딩하지 못했습니다.");
                    if (ImageAnimator.CanAnimate(src))
                    {
                        image = src;
                        imageAnimated = true;
                        gifAnimatorHandler = delegate(object sender, EventArgs e) { };
                        try { ImageAnimator.Animate(image, gifAnimatorHandler); gifAnimatorActive = true; } catch { gifAnimatorActive = false; }
                        gifTimer.Interval = 33;
                        gifTimer.Tick += delegate
                        {
                            try
                            {
                                if (overlayVisible && image != null)
                                {
                                    ImageAnimator.UpdateFrames(image);
                                    RenderLayered();
                                }
                            }
                            catch { }
                        };
                        gifTimer.Start();
                    }
                    else
                    {
                        using (src) image = new Bitmap(src);
                    }
                }
                catch { image = null; }
            }
            if (Type == ItemType.Web)
            {
                InitializeWebControls();
            }
            if (Type == ItemType.Timer)
            {
                timerStartTicks = Stopwatch.GetTimestamp();
                timerCompleted = false;
                lastRepeatCycle = 0;
                renderTimer.Interval = 100;
                renderTimer.Tick += delegate
                {
                    // Keep the 100 ms clock check so alarms remain responsive, but only repaint
                    // the layered window when the second-based display actually changes.
                    string timerText = GetTimerText();
                    if (overlayVisible && !string.Equals(timerText, lastRenderedTimerText, StringComparison.Ordinal))
                    {
                        lastRenderedTimerText = timerText;
                        RenderLayered();
                    }
                };
                renderTimer.Start();
            }
            if (Type == ItemType.ObsProgram)
            {
                obsTimer.Interval = ObsSearchIntervalMs;
                obsTimer.Tick += delegate { PollObsProgram(); };
            }

            MouseDown += OnMouseDownOverlay;
            MouseMove += OnMouseMoveOverlay;
            MouseUp += OnMouseUpOverlay;
            MouseWheel += OnMouseWheelOverlay;
            MouseDoubleClick += delegate(object sender, MouseEventArgs e)
            {
                if (Type == ItemType.Web && editMode && e.Button == MouseButtons.Left)
                {
                    owner.EnterWebControlFromDoubleClick(this);
                    return;
                }
                if (Type == ItemType.Timer && editMode) RestartTimer();
            };

            // Accept CatLayer-supported files directly on overlay windows as well as the main window.
            // This is especially important for .html/.htm/.catlayerweb drops because users naturally
            // drop a widget onto the desktop overlay area instead of the CatLayer control window.
            try
            {
                AllowDrop = true;
                DragEnter += delegate(object sender, DragEventArgs e) { owner.HandleExternalOverlayDragEnter(e); };
                DragOver += delegate(object sender, DragEventArgs e) { owner.HandleExternalOverlayDragEnter(e); };
                DragDrop += delegate(object sender, DragEventArgs e) { owner.HandleExternalOverlayDragDrop(e); };
            }
            catch { }

            ContextMenuStrip menu = new ContextMenuStrip();
            if (Type == ItemType.ObsProgram)
            {
                ToolStripMenuItem reconnect = new ToolStripMenuItem("OBS 재연결");
                reconnect.Click += delegate { ReconnectObsProgram(); };
                menu.Items.Add(reconnect);
                menu.Items.Add(new ToolStripSeparator());
            }

            if (Type == ItemType.Web)
            {
                ToolStripMenuItem changeUrl = new ToolStripMenuItem("웹 주소 변경...");
                changeUrl.Click += delegate { owner.ChangeWebUrlInteractive(this); };
                ToolStripMenuItem reload = new ToolStripMenuItem("새로고침");
                reload.Click += delegate { ReloadWeb(); };
                ToolStripMenuItem webOpacity = new ToolStripMenuItem("전체 투명도...");
                webOpacity.Click += delegate
                {
                    int? value = UiPrompt.AskOpacity(this, OpacityPercent);
                    if (value.HasValue && value.Value != OpacityPercent)
                    {
                        owner.CaptureUndo("웹 전체 투명도 변경");
                        SetOpacityPercent(value.Value, true);
                    }
                };
                ToolStripMenuItem customCss = new ToolStripMenuItem("커스텀 CSS...");
                customCss.Click += delegate { owner.EditWebCustomCssInteractive(this); };
                ToolStripMenuItem exportWeb = new ToolStripMenuItem("CatLayerWeb 파일로 저장...");
                exportWeb.Enabled = (Data ?? "").StartsWith("catlayer-local://", StringComparison.OrdinalIgnoreCase);
                exportWeb.Click += delegate { owner.ExportWebPackageInteractive(this); };
                menu.Items.Add(changeUrl);
                menu.Items.Add(reload);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(webOpacity);
                menu.Items.Add(customCss);
                menu.Items.Add(exportWeb);
                menu.Items.Add(new ToolStripSeparator());
            }

            if (Type == ItemType.Timer && TimerKind != TimerMode.Stopwatch)
            {
                ToolStripMenuItem alarmMenu = new ToolStripMenuItem("알람 소리");
                ToolStripMenuItem chooseAlarm = new ToolStripMenuItem("파일에서 선택...");
                chooseAlarm.Click += delegate { owner.SelectTimerAlarmFile(this); };
                ToolStripMenuItem defaultAlarm = new ToolStripMenuItem("기본 귀여운 소리");
                defaultAlarm.Click += delegate { owner.UseDefaultTimerAlarm(this); };
                alarmMenu.DropDownItems.Add(chooseAlarm);
                alarmMenu.DropDownItems.Add(defaultAlarm);
                menu.Items.Add(alarmMenu);
            }

            ToolStripMenuItem lockItem = new ToolStripMenuItem("위치/크기 잠금");
            lockItem.CheckOnClick = true;
            lockItem.Checked = Locked;
            lockItem.Click += delegate { owner.CaptureUndo("위치/크기 잠금 변경"); SetLocked(lockItem.Checked, true); };
            menu.Items.Add(lockItem);

            if (Type == ItemType.Image)
            {
                ToolStripMenuItem keepAspect = new ToolStripMenuItem("비율 유지");
                keepAspect.CheckOnClick = true;
                keepAspect.Checked = PreserveAspect;
                keepAspect.Click += delegate { owner.CaptureUndo("이미지 비율 유지 변경"); SetPreserveAspect(keepAspect.Checked, true); };
                menu.Items.Add(keepAspect);

                ToolStripMenuItem scaleMenu = new ToolStripMenuItem("표시 방식");
                ToolStripMenuItem fitMode = new ToolStripMenuItem("맞춤");
                ToolStripMenuItem fillMode = new ToolStripMenuItem("채우기");
                ToolStripMenuItem stretchMode = new ToolStripMenuItem("늘리기");
                fitMode.Click += delegate { owner.CaptureUndo("이미지 표시 방식 변경"); SetImageScaleMode(ImageScaleMode.Fit, true); };
                fillMode.Click += delegate { owner.CaptureUndo("이미지 표시 방식 변경"); SetImageScaleMode(ImageScaleMode.Fill, true); };
                stretchMode.Click += delegate { owner.CaptureUndo("이미지 표시 방식 변경"); SetImageScaleMode(ImageScaleMode.Stretch, true); };
                scaleMenu.DropDownOpening += delegate
                {
                    fitMode.Checked = ScaleMode == ImageScaleMode.Fit;
                    fillMode.Checked = ScaleMode == ImageScaleMode.Fill;
                    stretchMode.Checked = ScaleMode == ImageScaleMode.Stretch;
                };
                scaleMenu.DropDownItems.Add(fitMode);
                scaleMenu.DropDownItems.Add(fillMode);
                scaleMenu.DropDownItems.Add(stretchMode);
                menu.Items.Add(scaleMenu);

                menu.Items.Add(new ToolStripSeparator());

                ToolStripMenuItem flipImageHorizontal = new ToolStripMenuItem("좌우 반전");
                flipImageHorizontal.Click += delegate { owner.FlipOverlay(this, true); };
                menu.Items.Add(flipImageHorizontal);

                ToolStripMenuItem flipImageVertical = new ToolStripMenuItem("상하 반전");
                flipImageVertical.Click += delegate { owner.FlipOverlay(this, false); };
                menu.Items.Add(flipImageVertical);

                ToolStripMenuItem resetImageRotation = new ToolStripMenuItem("각도 초기화 (반전 유지)");
                resetImageRotation.Click += delegate { owner.ResetOverlayRotation(this); };
                menu.Items.Add(resetImageRotation);

                ToolStripMenuItem resetImageTransform = new ToolStripMenuItem("회전/반전 전체 초기화");
                resetImageTransform.Click += delegate { owner.ResetOverlayTransform(this, true); };
                menu.Items.Add(resetImageTransform);

                menu.Items.Add(new ToolStripSeparator());

                ToolStripMenuItem replaceImage = new ToolStripMenuItem("이미지 교체...");
                replaceImage.Click += delegate { owner.ReplaceImageOverlay(this); };
                menu.Items.Add(replaceImage);

                ToolStripMenuItem imageSizeMenu = new ToolStripMenuItem("원본 크기로 복원");
                ToolStripMenuItem imageSize50 = new ToolStripMenuItem("50%");
                ToolStripMenuItem imageSize100 = new ToolStripMenuItem("100%");
                ToolStripMenuItem imageSize200 = new ToolStripMenuItem("200%");
                imageSize50.Click += delegate { owner.ResizeImageOverlayToScale(this, 0.5); };
                imageSize100.Click += delegate { owner.ResizeImageOverlayToScale(this, 1.0); };
                imageSize200.Click += delegate { owner.ResizeImageOverlayToScale(this, 2.0); };
                imageSizeMenu.DropDownItems.Add(imageSize50);
                imageSizeMenu.DropDownItems.Add(imageSize100);
                imageSizeMenu.DropDownItems.Add(imageSize200);
                menu.Items.Add(imageSizeMenu);

                menu.Opening += delegate
                {
                    flipImageHorizontal.Checked = FlipHorizontal;
                    flipImageVertical.Checked = FlipVertical;
                };
            }

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem groupMenu = new ToolStripMenuItem("그룹");
            ToolStripMenuItem makeOverlayGroup = new ToolStripMenuItem("선택 항목 그룹 만들기");
            makeOverlayGroup.Click += delegate { owner.GroupSelectedOverlays(); };
            ToolStripMenuItem saveOverlayGroup = new ToolStripMenuItem("그룹 파일로 저장...");
            saveOverlayGroup.Click += delegate { owner.SaveSelectedGroupInteractive(); };
            ToolStripMenuItem loadOverlayGroup = new ToolStripMenuItem("그룹 파일 불러오기...");
            loadOverlayGroup.Click += delegate { owner.LoadGroupInteractive(); };
            ToolStripMenuItem breakOverlayGroup = new ToolStripMenuItem("그룹 해제");
            breakOverlayGroup.Click += delegate { owner.UngroupSelectedOverlays(); };
            groupMenu.DropDownItems.Add(makeOverlayGroup);
            groupMenu.DropDownItems.Add(saveOverlayGroup);
            groupMenu.DropDownItems.Add(loadOverlayGroup);
            groupMenu.DropDownItems.Add(new ToolStripSeparator());
            groupMenu.DropDownItems.Add(breakOverlayGroup);
            menu.Items.Add(groupMenu);

            ToolStripMenuItem centerOnMonitor = new ToolStripMenuItem("현재 모니터 중앙으로 이동");
            centerOnMonitor.Click += delegate { owner.MoveOverlayToCurrentMonitorCenter(this); };
            menu.Items.Add(centerOnMonitor);

            ToolStripMenuItem priority = new ToolStripMenuItem("우선도");
            ToolStripMenuItem front = new ToolStripMenuItem("맨 앞으로");
            front.Click += delegate { owner.MoveItemToFront(this); };
            ToolStripMenuItem forward = new ToolStripMenuItem("한 단계 앞으로");
            forward.Click += delegate { owner.MoveItemForward(this); };
            ToolStripMenuItem backward = new ToolStripMenuItem("한 단계 뒤로");
            backward.Click += delegate { owner.MoveItemBackward(this); };
            ToolStripMenuItem back = new ToolStripMenuItem("맨 뒤로");
            back.Click += delegate { owner.MoveItemToBack(this); };
            priority.DropDownItems.Add(front);
            priority.DropDownItems.Add(forward);
            priority.DropDownItems.Add(backward);
            priority.DropDownItems.Add(back);
            menu.Items.Add(priority);

            if (Type != ItemType.Web)
            {
                ToolStripMenuItem opacityMenu = new ToolStripMenuItem("투명도...");
                opacityMenu.Click += delegate
                {
                    int? value = UiPrompt.AskOpacity(this, OpacityPercent);
                    if (value.HasValue && value.Value != OpacityPercent) { owner.CaptureUndo("투명도 변경"); SetOpacityPercent(value.Value, true); }
                };
                menu.Items.Add(opacityMenu);
            }
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem del = new ToolStripMenuItem("오버레이 삭제");
            del.Click += delegate { owner.DeleteItem(this); };
            menu.Items.Add(del);
            menu.Closed += delegate { owner.ReapplyZOrder(); };
            ContextMenuStrip = menu;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= unchecked((int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST));
                if (Type != ItemType.ObsProgram && Type != ItemType.Web) cp.ExStyle |= unchecked((int)Native.WS_EX_LAYERED);
                bool webInteractive = Type == ItemType.Web && owner != null && owner.IsWebInteractionEnabled(this);
                if (!editMode && !webInteractive) cp.ExStyle |= unchecked((int)(Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE));
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            SetEditMode(owner.EditMode);
            if (Type == ItemType.ObsProgram) StartObsProgram();
            else if (Type == ItemType.Web) StartWebOverlay();
            else RenderLayered();
        }

        protected override void WndProc(ref Message m)
        {
            if (Type == ItemType.ObsProgram && m.Msg == 0x0014) // WM_ERASEBKGND
            {
                m.Result = new IntPtr(1);
                return;
            }
            bool webInteractive = Type == ItemType.Web && owner.IsWebInteractionEnabled(this);
            if (!editMode && !webInteractive && m.Msg == Native.WM_NCHITTEST)
            {
                m.Result = new IntPtr(Native.HTTRANSPARENT);
                return;
            }
            if (!editMode && !webInteractive && m.Msg == Native.WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(Native.MA_NOACTIVATE);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Type == ItemType.ObsProgram) return;
            base.OnPaintBackground(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Type != ItemType.ObsProgram)
            {
                base.OnPaint(e);
                return;
            }

            e.Graphics.Clear(ObsTransparentKey);
            if (!obsAttached)
            {
                using (Brush b = new SolidBrush(Color.Gainsboro))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    string bridge = ObsBridge.IsAlive() ? "OBS Bridge: READY" : "OBS Bridge 미연결\nOBS > 도구 > 스크립트에서 CatLayer_OBS_Bridge.lua 추가";
                    e.Graphics.DrawString(obsStatus + "\n\n" + bridge + "\n\n수동 대체: OBS Program 창 프로젝터를 열어도 자동 탐지됩니다.", Font, b, ClientRectangle, sf);
                }
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (!IsHandleCreated) return;
            if (Type == ItemType.ObsProgram) { UpdateObsChildRect(); Invalidate(); }
            else if (Type == ItemType.Web) ScheduleWebResizeTransparencyRefresh();
            else RenderLayered();
            UpdateSelectionFrame();
        }

        private async void ScheduleWebResizeTransparencyRefresh()
        {
            if (Type != ItemType.Web || webView == null || !webInitialized) return;
            int version = ++webResizeRefreshVersion;
            try
            {
                // During mouse resizing OnSizeChanged can fire many times. Wait until the
                // resize settles, then refresh only the final WebView2 composition frame.
                await Task.Delay(120);
                if (version != webResizeRefreshVersion || IsDisposed || webView == null || webView.IsDisposed || !overlayVisible) return;

                try { webView.DefaultBackgroundColor = Color.Transparent; } catch { }
                await ApplyWebAppearanceAsync();
                if (version != webResizeRefreshVersion || IsDisposed || !overlayVisible || !IsHandleCreated) return;

                // Windowed WebView2 can keep an opaque/black child surface after its host is
                // resized. The same OFF/ON host composition that users found fixes it manually
                // is reproduced once after resizing has stopped.
                Native.ShowWindow(Handle, Native.SW_HIDE);
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
                if (webView != null && !webView.IsDisposed)
                {
                    webView.Visible = true;
                    webView.Enabled = owner.IsWebInteractionEnabled(this);
                    webView.BringToFront();
                }
            }
            catch { }
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (Type == ItemType.ObsProgram) UpdateObsChildRect();
            UpdateSelectionFrame();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            UpdateSelectionFrame();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { renderTimer.Stop(); renderTimer.Dispose(); } catch { }
                try { gifTimer.Stop(); gifTimer.Dispose(); } catch { }
                try { obsTimer.Stop(); obsTimer.Dispose(); } catch { }
                try { if (gifAnimatorActive && image != null && gifAnimatorHandler != null) ImageAnimator.StopAnimate(image, gifAnimatorHandler); } catch { }
                gifAnimatorActive = false;
                try { if (image != null) image.Dispose(); } catch { }
                try { if (Type == ItemType.ObsProgram) DetachObsWindow(obsOpenedByBridge); } catch { }
                try { if (webView != null) { webView.Dispose(); webView = null; } } catch { }
                try { if (selectionFrame != null && !selectionFrame.IsDisposed) selectionFrame.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        public void SetEditMode(bool value)
        {
            editMode = value;
            if (!IsHandleCreated) return;

            long ex = Native.GetExStyle(Handle);
            bool webInteractive = Type == ItemType.Web && owner.IsWebInteractionEnabled(this);
            if (value || webInteractive)
                ex &= ~(Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE);
            else
                ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE;

            if (Type != ItemType.ObsProgram && Type != ItemType.Web)
                ex |= Native.WS_EX_LAYERED;
            else if (Type == ItemType.ObsProgram && obsAttached)
                ex |= Native.WS_EX_LAYERED;
            else
                ex &= ~Native.WS_EX_LAYERED;

            Native.SetExStyle(Handle, ex);
            if (Type == ItemType.ObsProgram && obsAttached) ApplyObsDestinationTransparency();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

            if (Type == ItemType.ObsProgram)
            {
                UpdateObsChildRect();
                Invalidate();
            }
            else if (Type == ItemType.Web)
            {
                try { if (webView != null) webView.Enabled = owner.IsWebInteractionEnabled(this); } catch { }
            }
            else RenderLayered();
            UpdateSelectionFrame();
        }

        private void UpdateSelectionFrame()
        {
            if (selectionFrame == null || selectionFrame.IsDisposed) return;
            bool showFrame = IsHandleCreated && !IsDisposed && Visible && overlayVisible && editMode &&
                owner.ShouldShowSelectionVisuals && owner.IsOverlaySelected(this);
            Rectangle visualBounds = GetVisualContentBounds(Bounds);
            selectionFrame.SyncToOverlay(visualBounds, showFrame, Locked, this, Type == ItemType.Web && editMode);
        }

        public void RefreshSelectionVisual()
        {
            if (IsDisposed) return;
            UpdateSelectionFrame();
        }

        public void BringSelectionFrameToFront()
        {
            if (selectionFrame != null && !selectionFrame.IsDisposed) selectionFrame.BringFrameToFront();
        }

        private static uint ToColorRef(Color color)
        {
            return (uint)(color.R | (color.G << 8) | (color.B << 16));
        }

        private void ApplyObsDestinationTransparency()
        {
            if (Type != ItemType.ObsProgram || !IsHandleCreated || !obsAttached) return;
            long ex = Native.GetExStyle(Handle) | Native.WS_EX_LAYERED;
            Native.SetExStyle(Handle, ex);
            byte alpha = (byte)Math.Round(OpacityPercent * 255.0 / 100.0);
            Native.SetLayeredWindowAttributes(Handle, ToColorRef(ObsTransparentKey), alpha, Native.LWA_COLORKEY | Native.LWA_ALPHA);
        }

        public void SetOverlayVisible(bool visible)
        {
            userVisible = visible;
            RefreshEffectiveVisibility();
        }

        public void RefreshEffectiveVisibility()
        {
            ApplyOverlayVisible(userVisible && !owner.AllHidden);
        }

        private void ApplyOverlayVisible(bool visible)
        {
            overlayVisible = visible;
            UpdateRenderActivity();
            if (Type == ItemType.ObsProgram)
            {
                // Keep the source projector alive off-screen at 1920x1080.
                // Only hide/show the DWM destination overlay.
                if (visible)
                {
                    Show();
                    UpdateObsChildRect();
                }
                else Hide();
            }
            else
            {
                if (visible) Show(); else Hide();
                if (visible && Type != ItemType.Web) RenderLayered();
            }
            UpdateSelectionFrame();
        }

        private void UpdateRenderActivity()
        {
            if (Type == ItemType.Image && imageAnimated)
            {
                if (overlayVisible)
                {
                    if (!gifAnimatorActive && image != null && gifAnimatorHandler != null)
                    {
                        try { ImageAnimator.Animate(image, gifAnimatorHandler); gifAnimatorActive = true; } catch { }
                    }
                    if (!gifTimer.Enabled) gifTimer.Start();
                }
                else
                {
                    gifTimer.Stop();
                    if (gifAnimatorActive && image != null && gifAnimatorHandler != null)
                    {
                        try { ImageAnimator.StopAnimate(image, gifAnimatorHandler); } catch { }
                        gifAnimatorActive = false;
                    }
                }
            }

            if (Type == ItemType.Timer && TimerKind == TimerMode.Stopwatch)
            {
                if (overlayVisible)
                {
                    if (!renderTimer.Enabled) renderTimer.Start();
                }
                else renderTimer.Stop();
            }
        }

        public void SetCustomName(string value, bool save)
        {
            CustomName = (value ?? "").Trim();
            if (CustomName.Length > 60) CustomName = CustomName.Substring(0, 60);
            if (save) owner.SaveConfig();
        }

        public void SetOpacityPercent(int value, bool save)
        {
            OpacityPercent = Math.Max(0, Math.Min(100, value));
            if (Type == ItemType.Web)
            {
                ApplyWebAppearance();
                if (save) owner.SaveConfig();
                return;
            }
            if (Type == ItemType.ObsProgram)
            {
                if (IsHandleCreated && obsAttached) ApplyObsDestinationTransparency();
                UpdateObsChildRect();
                Invalidate();
            }
            else RenderLayered();
            if (save) owner.SaveConfig();
        }

        public void SetTimerAlarmPath(string path, bool save)
        {
            if (Type != ItemType.Timer) return;
            AlarmPath = path ?? "";
            if (save) owner.SaveConfig();
        }

        public void SetLocked(bool value, bool save)
        {
            Locked = value;
            if (save) owner.SaveConfig();
            UpdateSelectionFrame();
        }

        public void SetPreserveAspect(bool value, bool save)
        {
            if (Type != ItemType.Image) return;
            PreserveAspect = value;
            if (save && value) NormalizeFitBoundsToVisualContent();
            if (save) owner.SaveConfig();
            RenderLayered();
        }

        public void SetImageScaleMode(ImageScaleMode value, bool save)
        {
            if (Type != ItemType.Image) return;
            ScaleMode = value;
            if (save && value == ImageScaleMode.Fit) NormalizeFitBoundsToVisualContent();
            if (save) owner.SaveConfig();
            RenderLayered();
        }

        public void SetCrop(int left, int top, int right, int bottom, bool save)
        {
            if (Type != ItemType.Image) return;
            left = Math.Max(0, Math.Min(9500, left));
            top = Math.Max(0, Math.Min(9500, top));
            right = Math.Max(0, Math.Min(9500, right));
            bottom = Math.Max(0, Math.Min(9500, bottom));
            if (left + right > 9500) right = Math.Max(0, 9500 - left);
            if (top + bottom > 9500) bottom = Math.Max(0, 9500 - top);
            CropLeft = left; CropTop = top; CropRight = right; CropBottom = bottom;
            if (save) NormalizeFitBoundsToVisualContent();
            RenderLayered();
            if (save) owner.SaveConfig();
        }

        public void SetTransform(int rotationDegrees, bool flipHorizontal, bool flipVertical, bool save)
        {
            if (!SupportsTransform) return;
            int normalized = rotationDegrees % 360;
            if (normalized > 180) normalized -= 360;
            if (normalized < -180) normalized += 360;
            RotationDegrees = normalized;
            FlipHorizontal = flipHorizontal;
            FlipVertical = flipVertical;
            RenderLayered();
            if (save) owner.SaveConfig();
        }

        public void SetGroupId(int groupId, bool save)
        {
            GroupId = Math.Max(0, groupId);
            if (save) owner.SaveConfig();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // When the embedded browser owns focus, let normal web shortcuts (typing,
            // Ctrl+C/V, scrolling, forms, etc.) reach WebView2. Global CatLayer hotkeys
            // such as F8/F9 are registered at the OS level and still work.
            if (Type == ItemType.Web && webView != null && webView.ContainsFocus)
                return base.ProcessCmdKey(ref msg, keyData);
            if (owner.HandlePasteShortcut(keyData)) return true;
            if (owner.HandleDetailShortcut(this, keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void RestartTimer()
        {
            timerStartTicks = Stopwatch.GetTimestamp();
            timerCompleted = false;
            lastRepeatCycle = 0;
            lastRenderedTimerText = null;
            if (!renderTimer.Enabled) renderTimer.Start();
            RenderLayered();
        }

        private string GetTimerText()
        {
            double elapsed = (Stopwatch.GetTimestamp() - timerStartTicks) / (double)Stopwatch.Frequency;

            if (TimerKind == TimerMode.Stopwatch)
                return FormatCountdown((long)Math.Floor(Math.Max(0.0, elapsed)));

            if (TimerKind == TimerMode.Repeat)
            {
                double duration = Math.Max(1, DurationSeconds);
                long cycle = (long)Math.Floor(elapsed / duration);
                if (cycle > lastRepeatCycle)
                {
                    lastRepeatCycle = cycle;
                    AudioAlert.Play(AlarmPath);
                }

                double position = elapsed % duration;
                long remaining = (long)Math.Ceiling(duration - position);
                if (remaining <= 0 || remaining > DurationSeconds) remaining = DurationSeconds;
                return FormatCountdown(remaining);
            }

            if (elapsed >= DurationSeconds)
            {
                if (!timerCompleted)
                {
                    timerCompleted = true;
                    AudioAlert.Play(AlarmPath);
                    renderTimer.Stop();
                }
                return FormatCountdown(0);
            }

            return FormatCountdown((long)Math.Ceiling(DurationSeconds - elapsed));
        }

        private static string FormatCountdown(long sec)
        {
            if (sec < 0) sec = 0;
            long h = sec / 3600, m = (sec % 3600) / 60, s = sec % 60;
            return h > 0 ? h.ToString("00") + ":" + m.ToString("00") + ":" + s.ToString("00") : m.ToString("00") + ":" + s.ToString("00");
        }

        private void RenderLayered()
        {
            if (!overlayVisible || !IsHandleCreated || Type == ItemType.ObsProgram || Type == ItemType.Web || Width <= 0 || Height <= 0) return;
            using (Bitmap bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                RectangleF area = new RectangleF(0, 0, Math.Max(1, Width), Math.Max(1, Height));

                if (SupportsTransform && (RotationDegrees != 0 || FlipHorizontal || FlipVertical))
                {
                    g.TranslateTransform(Width / 2f, Height / 2f);
                    if (RotationDegrees != 0) g.RotateTransform(RotationDegrees);
                    if (FlipHorizontal || FlipVertical) g.ScaleTransform(FlipHorizontal ? -1f : 1f, FlipVertical ? -1f : 1f);
                    g.TranslateTransform(-Width / 2f, -Height / 2f);
                }

                if (Type == ItemType.Image)
                {
                    if (image != null && image.Width > 0 && image.Height > 0)
                    {
                        float alpha = OpacityPercent / 100f;
                        using (ImageAttributes attrs = new ImageAttributes())
                        {
                            ColorMatrix cm = new ColorMatrix();
                            cm.Matrix33 = alpha;
                            attrs.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

                            int srcX = Math.Max(0, Math.Min(image.Width - 1, (int)Math.Round(image.Width * (CropLeft / 10000.0))));
                            int srcY = Math.Max(0, Math.Min(image.Height - 1, (int)Math.Round(image.Height * (CropTop / 10000.0))));
                            int srcRight = Math.Max(srcX + 1, Math.Min(image.Width, image.Width - (int)Math.Round(image.Width * (CropRight / 10000.0))));
                            int srcBottom = Math.Max(srcY + 1, Math.Min(image.Height, image.Height - (int)Math.Round(image.Height * (CropBottom / 10000.0))));
                            Rectangle srcRect = new Rectangle(srcX, srcY, Math.Max(1, srcRight - srcX), Math.Max(1, srcBottom - srcY));

                            Rectangle dest;
                            if (ScaleMode == ImageScaleMode.Stretch)
                            {
                                dest = Rectangle.Round(area);
                            }
                            else
                            {
                                float scale = (ScaleMode == ImageScaleMode.Fill)
                                    ? Math.Max(area.Width / srcRect.Width, area.Height / srcRect.Height)
                                    : Math.Min(area.Width / srcRect.Width, area.Height / srcRect.Height);
                                float w = srcRect.Width * scale, h = srcRect.Height * scale;
                                float x = area.X + (area.Width - w) / 2f, y = area.Y + (area.Height - h) / 2f;
                                dest = new Rectangle((int)Math.Round(x), (int)Math.Round(y), Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
                            }

                            Region oldClip = null;
                            try
                            {
                                if (ScaleMode == ImageScaleMode.Fill)
                                {
                                    oldClip = g.Clip;
                                    g.SetClip(Rectangle.Round(area));
                                }
                                g.DrawImage(image, dest, srcRect.X, srcRect.Y, srcRect.Width, srcRect.Height, GraphicsUnit.Pixel, attrs);
                            }
                            finally
                            {
                                if (ScaleMode == ImageScaleMode.Fill)
                                {
                                    try { g.ResetClip(); } catch { }
                                    try { if (oldClip != null) g.Clip = oldClip; } catch { }
                                }
                            }
                        }
                    }
                }
                else
                {
                    string text = Data;
                    if (Type == ItemType.Timer)
                    {
                        text = GetTimerText();
                        lastRenderedTimerText = text;
                    }

                    // Render the complete text glyph first, then apply opacity once.
                    // This prevents the eight outline passes from accumulating alpha
                    // and leaving a dark/opaque outline at low opacity.
                    using (Bitmap textLayer = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
                    using (Graphics tg = Graphics.FromImage(textLayer))
                    {
                        tg.Clear(Color.Transparent);
                        tg.SmoothingMode = SmoothingMode.AntiAlias;
                        tg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                        float fontSize = Math.Max(14f, Math.Min(160f, Height * 0.42f));
                        using (Font f = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (StringFormat sf = new StringFormat())
                        using (Brush shadow = new SolidBrush(Color.FromArgb(220, 0, 0, 0)))
                        using (Brush fg = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                        {
                            sf.Alignment = StringAlignment.Center;
                            sf.LineAlignment = StringAlignment.Center;
                            sf.Trimming = StringTrimming.EllipsisCharacter;

                            float o = Math.Max(1f, fontSize * 0.035f);
                            PointF[] offs =
                            {
                                new PointF(-o,0), new PointF(o,0), new PointF(0,-o), new PointF(0,o),
                                new PointF(-o,-o), new PointF(o,-o), new PointF(-o,o), new PointF(o,o)
                            };
                            foreach (PointF off in offs)
                            {
                                RectangleF r = area;
                                r.X += off.X;
                                r.Y += off.Y;
                                tg.DrawString(text, f, shadow, r, sf);
                            }
                            tg.DrawString(text, f, fg, area, sf);
                        }

                        float alpha = OpacityPercent / 100f;
                        using (ImageAttributes attrs = new ImageAttributes())
                        {
                            ColorMatrix cm = new ColorMatrix();
                            cm.Matrix33 = alpha;
                            attrs.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                            g.DrawImage(textLayer, new Rectangle(0, 0, Width, Height), 0, 0, Width, Height, GraphicsUnit.Pixel, attrs);
                        }
                    }
                }

                g.ResetTransform();
                g.ResetClip();
                ApplyBitmap(bmp);
            }
        }

        private void ApplyBitmap(Bitmap bitmap)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr mem = Native.CreateCompatibleDC(screen);
            IntPtr hBitmap = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(mem, hBitmap);
                Native.POINT src = new Native.POINT(0, 0);
                Native.POINT dst = new Native.POINT(Left, Top);
                Native.SIZE size = new Native.SIZE(bitmap.Width, bitmap.Height);
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = Native.AC_SRC_OVER; blend.SourceConstantAlpha = 255; blend.AlphaFormat = Native.AC_SRC_ALPHA;
                Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                if (old != IntPtr.Zero) Native.SelectObject(mem, old);
                if (hBitmap != IntPtr.Zero) Native.DeleteObject(hBitmap);
                Native.DeleteDC(mem);
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private void InitializeWebControls()
        {
            webStatusLabel = new Label();
            webStatusLabel.Dock = DockStyle.Fill;
            webStatusLabel.BackColor = WebTransparentKey;
            webStatusLabel.ForeColor = Color.Gainsboro;
            webStatusLabel.TextAlign = ContentAlignment.MiddleCenter;
            webStatusLabel.Text = "웹 오버레이 준비 중...";
            Controls.Add(webStatusLabel);

            webView = new WebView2();
            // Set this before CoreWebView2 creation. Windowed WebView2 otherwise paints its
            // default background before CatLayer can make the document transparent.
            try { webView.DefaultBackgroundColor = Color.Transparent; } catch { }
            webView.Dock = DockStyle.Fill;
            // WebView2.AllowDrop is read-only in the SDK version used by CatLayer.
            // External CatLayer file drops are handled by the owning overlay/main window.
            webView.Visible = false;
            webView.Enabled = owner.IsWebInteractionEnabled(this);
            webView.TabStop = true;
            webView.Enter += delegate { if (editMode) owner.SelectOverlayForEditing(this); };
            webView.GotFocus += delegate { if (editMode) owner.SelectOverlayForEditing(this); };
            webView.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape && owner.IsSingleWebControl(this))
                {
                    owner.ExitSingleWebControl(true);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            webView.LostFocus += delegate
            {
                if (!owner.IsSingleWebControl(this)) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate { owner.ExitSingleWebControlIfFocusLeft(this); });
                }
                catch { }
            };
            Controls.Add(webView);
            webView.BringToFront();
        }

        public void FocusWebContent()
        {
            if (Type != ItemType.Web) return;
            try
            {
                if (webView != null && !webView.IsDisposed)
                {
                    webView.Enabled = true;
                    webView.Focus();
                }
            }
            catch { }
        }

        private static bool TryParseLocalWebData(string data, out string rootFolder, out string relativeEntry, out string hostName)
        {
            rootFolder = ""; relativeEntry = ""; hostName = "";
            string value = (data ?? "").Trim();
            const string prefix = "catlayer-local://";
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = value.Substring(prefix.Length);
            int slash = rest.IndexOf('/');
            string id = slash >= 0 ? rest.Substring(0, slash) : rest;
            string rel = slash >= 0 ? rest.Substring(slash + 1) : "index.html";
            if (!Regex.IsMatch(id, @"^[a-fA-F0-9]{32}$")) return false;
            if (string.IsNullOrWhiteSpace(rel)) rel = "index.html";
            rel = rel.Replace('\\', '/').TrimStart('/');
            if (rel.Contains("../") || rel.Contains("..\\")) return false;
            rootFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatLayer", "WebFiles", id);
            relativeEntry = rel;
            hostName = "catlayer-" + id.ToLowerInvariant() + ".local";
            return Directory.Exists(rootFolder) && File.Exists(Path.Combine(rootFolder, rel.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static bool TryNormalizeWebUrl(string input, out string normalized)
        {
            normalized = "";
            string value = (input ?? "").Trim();
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (value.StartsWith("catlayer-local://", StringComparison.OrdinalIgnoreCase))
            {
                string root, rel, host;
                if (!TryParseLocalWebData(value, out root, out rel, out host)) return false;
                normalized = value;
                return true;
            }
            if (!Regex.IsMatch(value, @"^[a-zA-Z][a-zA-Z0-9+.-]*://")) value = "https://" + value;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
            normalized = uri.AbsoluteUri;
            return true;
        }

        private static string LocalWebSecurityScript()
        {
            return @"(function(){
'use strict';
var bad='object,embed,applet,frame,frameset';
function clean(root){
  try{
    if(root.querySelectorAll){
      root.querySelectorAll(bad).forEach(function(n){n.remove();});
      root.querySelectorAll('meta[http-equiv]').forEach(function(n){if((n.getAttribute('http-equiv')||'').toLowerCase()==='refresh')n.remove();});
    }
  }catch(e){}
}
function start(){
  clean(document);
  try{new MutationObserver(function(ms){ms.forEach(function(m){m.addedNodes.forEach(function(n){if(n&&n.nodeType===1){if(n.matches&&n.matches(bad))n.remove();else clean(n);}});});}).observe(document.documentElement,{childList:true,subtree:true});}catch(e){}
}
if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',start,{once:true});else start();
})();";
        }

        private static string LocalWidgetBridgeScript()
        {
            // Intentionally expose only resize(width, height). No filesystem, process, config,
            // host object, arbitrary command channel, or callback into CatLayer is provided.
            return @"(function(){
'use strict';
try{
  if(!window.chrome||!window.chrome.webview)return;
  var api={resize:function(w,h){
    w=Math.round(Number(w));h=Math.round(Number(h));
    if(!isFinite(w)||!isFinite(h))return false;
    window.chrome.webview.postMessage('CATLAYER_RESIZE|'+w+'|'+h);
    return true;
  }};
  try{Object.freeze(api);}catch(e){}
  try{Object.defineProperty(window,'catlayer',{value:api,writable:false,configurable:false});}
  catch(e){window.catlayer=api;}
}catch(e){}
})();";
        }

        private static string WebEscapeBridgeScript()
        {
            // Escape only. This does not expose a native object or any privileged operation.
            return @"(function(){
'use strict';
try{
  if(!window.chrome||!window.chrome.webview)return;
  document.addEventListener('keydown',function(e){
    try{
      if(e && (e.key==='Escape'||e.key==='Esc'||e.keyCode===27)){
        window.chrome.webview.postMessage('CATLAYER_ESCAPE');
      }
    }catch(x){}
  },true);
}catch(e){}
})();";
        }

        private void HandleLocalWebMessage(string message)
        {
            if (Type != ItemType.Web || string.IsNullOrWhiteSpace(message)) return;
            const string prefix = "CATLAYER_RESIZE|";
            if (!message.StartsWith(prefix, StringComparison.Ordinal)) return;
            string[] parts = message.Substring(prefix.Length).Split('|');
            if (parts.Length != 2) return;
            int width, height;
            if (!int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height)) return;

            // Hard bounds prevent a widget from creating unusably tiny or absurdly large windows.
            width = Math.Max(100, Math.Min(3840, width));
            height = Math.Max(60, Math.Min(2160, height));
            Rectangle next = owner.NormalizeBounds(new Rectangle(Left, Top, width, height));
            if (Bounds == next) return;
            Bounds = next;
            UpdateSelectionFrame();
            owner.SaveConfig();
        }

        private async void StartWebOverlay()
        {
            if (Type != ItemType.Web || webView == null || webInitialized || webInitializing) return;
            webInitializing = true;
            try
            {
                if (webStatusLabel != null)
                {
                    webStatusLabel.Text = "웹 오버레이 시작 중...";
                    webStatusLabel.Visible = true;
                }
                CoreWebView2Environment environment = await WebOverlayEnvironment.GetAsync();
                if (IsDisposed || webView == null) return;
                await webView.EnsureCoreWebView2Async(environment);
                if (IsDisposed || webView == null || webView.CoreWebView2 == null) return;

                CoreWebView2Settings settings = webView.CoreWebView2.Settings;
                settings.IsStatusBarEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.AreDefaultContextMenusEnabled = true;
                settings.AreBrowserAcceleratorKeysEnabled = true;
                settings.IsZoomControlEnabled = true;
                settings.IsScriptEnabled = true;
                settings.AreHostObjectsAllowed = false;
                // WebMessage is enabled only as a transport for the tightly-scoped local-widget
                // resize command below. Messages are ignored unless their source host is the
                // current CatLayer virtual local host. No HostObject/native object is exposed.
                settings.IsWebMessageEnabled = true;
                webView.CoreWebView2.WebMessageReceived += delegate(object sender, CoreWebView2WebMessageReceivedEventArgs e)
                {
                    try
                    {
                        string message = e.TryGetWebMessageAsString();
                        if (string.Equals(message, "CATLAYER_ESCAPE", StringComparison.Ordinal))
                        {
                            if (!IsDisposed && IsHandleCreated)
                            {
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    if (!IsDisposed) owner.ExitWebInteractionFromEscape(this);
                                });
                            }
                            return;
                        }

                        if (string.IsNullOrWhiteSpace(webResizeHost)) return;
                        Uri sourceUri;
                        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out sourceUri)) return;
                        if (!string.Equals(sourceUri.Host, webResizeHost, StringComparison.OrdinalIgnoreCase)) return;
                        HandleLocalWebMessage(message);
                    }
                    catch { }
                };
                await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(WebEscapeBridgeScript());

                webView.CoreWebView2.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs e)
                {
                    try { e.State = CoreWebView2PermissionState.Deny; } catch { }
                };
                webView.CoreWebView2.DownloadStarting += delegate(object sender, CoreWebView2DownloadStartingEventArgs e)
                {
                    try { e.Cancel = true; owner.ReportStatus("웹 다운로드 차단됨"); } catch { }
                };
                webView.CoreWebView2.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
                {
                    try
                    {
                        Uri u;
                        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out u)) { e.Cancel = true; return; }
                        bool allowed = string.Equals(u.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(u.Scheme, "about", StringComparison.OrdinalIgnoreCase);
                        if (!allowed) { e.Cancel = true; owner.ReportStatus("웹에서 허용되지 않은 주소 형식을 차단했습니다."); }
                    }
                    catch { try { e.Cancel = true; } catch { } }
                };

                webView.CoreWebView2.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e)
                {
                    try
                    {
                        string target;
                        if (TryNormalizeWebUrl(e.Uri, out target))
                        {
                            webView.CoreWebView2.Navigate(target);
                            e.Handled = true;
                        }
                    }
                    catch { }
                };
                webView.NavigationCompleted += async delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
                {
                    if (!e.IsSuccess && webStatusLabel != null)
                    {
                        webStatusLabel.Text = "웹페이지를 불러오지 못했습니다.\n\n주소를 확인하거나 새로고침해 주세요.";
                        webStatusLabel.Visible = true;
                        webStatusLabel.BringToFront();
                    }
                    else
                    {
                        // Keep the child HWND hidden until the transparent background and
                        // CatLayer appearance CSS are fully applied. Previously the WebView2
                        // became visible first, so its first frame kept the dark background
                        // until the user manually toggled overlay visibility.
                        try { if (webView != null) webView.Visible = false; } catch { }
                        await ApplyWebAppearanceAsync();
                        if (webStatusLabel != null) webStatusLabel.Visible = false;
                        try
                        {
                            if (webView != null && !webView.IsDisposed)
                            {
                                webView.Visible = overlayVisible;
                                webView.Enabled = owner.IsWebInteractionEnabled(this);
                                if (overlayVisible) webView.BringToFront();
                            }

                            // On windowed WebView2 the very first composed frame can retain the
                            // host background even though DefaultBackgroundColor and page CSS are
                            // already transparent. A real overlay visibility OFF/ON refresh fixes
                            // that frame. Reproduce the same HWND refresh once, without changing
                            // the user's Visible setting or activating the overlay.
                            if (!webInitialHostRefreshDone && overlayVisible && IsHandleCreated)
                            {
                                webInitialHostRefreshDone = true;
                                await Task.Delay(40);
                                if (!IsDisposed && overlayVisible && IsHandleCreated)
                                {
                                    Native.ShowWindow(Handle, Native.SW_HIDE);
                                    Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                                    Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
                                    if (webView != null && !webView.IsDisposed)
                                    {
                                        webView.Visible = true;
                                        webView.BringToFront();
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                };

                string normalized;
                if (!TryNormalizeWebUrl(Data, out normalized)) normalized = "https://www.google.com/";
                Data = normalized;
                string navigateUrl = normalized;
                string localRoot, localEntry, localHost;
                if (TryParseLocalWebData(normalized, out localRoot, out localEntry, out localHost))
                {
                    webResizeHost = localHost;
                    webView.CoreWebView2.SetVirtualHostNameToFolderMapping(localHost, localRoot, CoreWebView2HostResourceAccessKind.DenyCors);
                    await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(LocalWebSecurityScript());
                    await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(LocalWidgetBridgeScript());
                    navigateUrl = "https://" + localHost + "/" + localEntry.Replace(" ", "%20");
                }
                else webResizeHost = "";
                webInitialized = true;
                // Do not show the child WebView2 before NavigationCompleted. The first
                // visible frame must already have a transparent controller background.
                webView.Visible = false;
                webView.Enabled = owner.IsWebInteractionEnabled(this);
                webView.Source = new Uri(navigateUrl);
                webView.ZoomFactorChanged -= OnWebZoomFactorChanged;
                webView.ZoomFactorChanged += OnWebZoomFactorChanged;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "WebView2 initialize");
                if (webStatusLabel != null)
                {
                    webStatusLabel.Text = "웹 오버레이를 시작하지 못했습니다.\n\nMicrosoft Edge WebView2 Runtime이 설치되어 있는지 확인해 주세요.\n\n" + ex.Message;
                    webStatusLabel.Visible = true;
                    webStatusLabel.BringToFront();
                }
            }
            finally { webInitializing = false; }
        }

        private static string EscapeJavaScriptSingleQuoted(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private async Task ApplyWebAppearanceAsync()
        {
            if (Type != ItemType.Web || webView == null || webView.CoreWebView2 == null) return;
            try
            {
                // WebView2 only supports fully opaque or fully transparent default backgrounds.
                // Keep the controller background fully transparent and apply CatLayer's 0-100%
                // opacity to the entire document instead of Form.Opacity (which does not reliably
                // affect the windowed WebView2 child HWND).
                try { webView.DefaultBackgroundColor = Color.Transparent; } catch { }
                webView.ZoomFactor = Math.Max(0.25, Math.Min(5.0, WebZoomPercent / 100.0));
                double opacity = Math.Max(0.0, Math.Min(1.0, OpacityPercent / 100.0));
                string opacityText = opacity.ToString(System.Globalization.CultureInfo.InvariantCulture);
                // Preserve the page's authored background. CatLayer only keeps the WebView2
                // controller itself transparent so HTML/CSS can opt into transparency naturally.
                // Opacity is applied to the whole document without rewriting its background.
                string css =
                    "html{opacity:" + opacityText + " !important;}" +
                    (WebCustomCss ?? "");
                string js =
                    "(function(){" +
                    "try{var de=document.documentElement;if(de){de.style.setProperty('opacity','" + opacityText + "','important');}}catch(_e){}" +
                    "var id='catlayer-custom-css';var st=document.getElementById(id);" +
                    "if(!st){st=document.createElement('style');st.id=id;(document.head||document.documentElement).appendChild(st);}" +
                    "st.textContent='" + EscapeJavaScriptSingleQuoted(css) + "';" +
                    "})();";
                await webView.CoreWebView2.ExecuteScriptAsync(js);
            }
            catch (Exception ex) { CrashLog.Write(ex, "ApplyWebAppearance"); }
        }

        private async void ApplyWebAppearance()
        {
            await ApplyWebAppearanceAsync();
        }

        public void SetWebCustomCss(string css, bool save)
        {
            if (Type != ItemType.Web) return;
            WebCustomCss = css ?? "";
            ApplyWebAppearance();
            if (save) owner.SaveConfig();
        }

        public void SetWebZoomPercent(int value, bool save)
        {
            if (Type != ItemType.Web) return;
            WebZoomPercent = Math.Max(25, Math.Min(500, value));
            ApplyWebAppearance();
            if (save) owner.SaveConfig();
        }

        private void OnWebZoomFactorChanged(object sender, EventArgs e)
        {
            if (Type != ItemType.Web || webView == null || !webInitialized) return;
            int value = Math.Max(25, Math.Min(500, (int)Math.Round(webView.ZoomFactor * 100.0)));
            if (value == WebZoomPercent) return;
            WebZoomPercent = value;
            owner.SaveConfig();
            if (owner.IsWebInteractionEnabled(this)) owner.ReportStatus("웹 확대/축소: " + WebZoomPercent.ToString() + "%");
        }

        private async void NavigateToWebData(string normalized)
        {
            if (webView == null || webView.CoreWebView2 == null) return;
            try
            {
                string navigateUrl = normalized;
                string localRoot, localEntry, localHost;
                if (TryParseLocalWebData(normalized, out localRoot, out localEntry, out localHost))
                {
                    webView.CoreWebView2.SetVirtualHostNameToFolderMapping(localHost, localRoot, CoreWebView2HostResourceAccessKind.DenyCors);
                    await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(LocalWebSecurityScript());
                    navigateUrl = "https://" + localHost + "/" + localEntry.Replace(" ", "%20");
                }
                webView.CoreWebView2.Navigate(navigateUrl);
            }
            catch (Exception ex) { CrashLog.Write(ex, "NavigateToWebData"); }
        }

        public bool SetWebUrl(string value, bool save)
        {
            if (Type != ItemType.Web) return false;
            string normalized;
            if (!TryNormalizeWebUrl(value, out normalized)) return false;
            Data = normalized;
            if (!webInitialized) StartWebOverlay();
            else
            {
                try { NavigateToWebData(normalized); }
                catch { return false; }
            }
            if (save) owner.SaveConfig();
            return true;
        }

        public void ReloadWeb()
        {
            if (Type != ItemType.Web) return;
            try
            {
                if (webView != null && webView.CoreWebView2 != null) webView.CoreWebView2.Reload();
                else StartWebOverlay();
            }
            catch { }
        }

        public void GoBackWeb()
        {
            if (Type != ItemType.Web) return;
            try { if (webView != null && webView.CoreWebView2 != null && webView.CoreWebView2.CanGoBack) webView.CoreWebView2.GoBack(); } catch { }
        }

        public void GoForwardWeb()
        {
            if (Type != ItemType.Web) return;
            try { if (webView != null && webView.CoreWebView2 != null && webView.CoreWebView2.CanGoForward) webView.CoreWebView2.GoForward(); } catch { }
        }

        public bool CanWebGoBack
        {
            get { try { return Type == ItemType.Web && webView != null && webView.CoreWebView2 != null && webView.CoreWebView2.CanGoBack; } catch { return false; } }
        }

        public bool CanWebGoForward
        {
            get { try { return Type == ItemType.Web && webView != null && webView.CoreWebView2 != null && webView.CoreWebView2.CanGoForward; } catch { return false; } }
        }

        public void BeginWebFrameDrag(bool resize)
        {
            if (Type != ItemType.Web || !editMode || Locked) return;
            owner.SelectOverlayForEditing(this);
            owner.CaptureUndo(resize ? "웹 오버레이 크기 변경" : "웹 오버레이 이동");
            dragging = true;
            dragMode = resize ? DragMode.BottomRight : DragMode.Move;
            cropDragging = false;
            dragStartMouse = Cursor.Position;
            dragStartBounds = Bounds;
            dragGroupStartBounds.Clear();
            if (!resize && GroupId > 0)
            {
                foreach (OverlayItemForm member in owner.GetGroupMembers(this))
                    if (!member.Locked) dragGroupStartBounds[member] = member.Bounds;
            }
        }

        public void ContinueWebFrameDrag()
        {
            if (Type != ItemType.Web || !dragging) return;
            OnMouseMoveOverlay(this, new MouseEventArgs(MouseButtons.Left, 0, Width / 2, Height / 2, 0));
        }

        public void EndWebFrameDrag()
        {
            if (Type != ItemType.Web || !dragging) return;
            OnMouseUpOverlay(this, new MouseEventArgs(MouseButtons.Left, 0, Width / 2, Height / 2, 0));
        }

        private void StartObsProgram()
        {
            obsSearchAttempts = 0;
            obsProjectorRequested = false;
            bridgeRequestPending = false;
            obsWindowsBeforeRequest.Clear();
            obsStatus = "OBS Program 프로젝터 탐색 중... (0/" + MaxObsSearchAttempts + ")";
            Invalidate();

            obsTimer.Stop();
            obsTimer.Interval = ObsSearchIntervalMs;

            IntPtr existing = FindExistingObsProjector(false);
            if (existing != IntPtr.Zero)
            {
                AttachObsWindow(existing, false);
                return;
            }

            if (ObsBridge.IsAlive()) RequestObsProjector();
            obsTimer.Start();
        }

        private void ReconnectObsProgram()
        {
            DetachObsWindow(false);
            StartObsProgram();
        }

        private void RequestObsProjector()
        {
            if (obsProjectorRequested) return;
            obsProjectorRequested = true;
            obsWindowsBeforeRequest.Clear();
            foreach (IntPtr h in EnumerateObsWindows()) obsWindowsBeforeRequest.Add(h);
            string token = ObsBridge.RequestOpenProgram();
            bridgeRequestPending = token.Length > 0;
            obsStatus = bridgeRequestPending
                ? "OBS Bridge에 1080p Program 프로젝터 생성 요청..."
                : "OBS Bridge 요청 실패 - 수동 Program 프로젝터를 탐색합니다.";
            Invalidate();
        }

        private void StopObsSearch(string reason)
        {
            obsTimer.Stop();
            bridgeRequestPending = false;
            obsStatus = reason + "\n우클릭 > Reconnect OBS Program 으로 다시 탐색할 수 있습니다.";
            Invalidate();
        }

        private void PollObsProgram()
        {
            if (Type != ItemType.ObsProgram || !IsHandleCreated) return;

            if (obsAttached)
            {
                if (obsWindow != IntPtr.Zero && Native.IsWindow(obsWindow))
                {
                    UpdateObsChildRect();
                    return;
                }

                UnregisterObsThumbnail();
                obsAttached = false;
                obsWindow = IntPtr.Zero;
                obsOpenedByBridge = false;
                obsSearchAttempts = 0;
                obsProjectorRequested = false;
                bridgeRequestPending = false;
                obsTimer.Interval = ObsSearchIntervalMs;
                obsWindowsBeforeRequest.Clear();
                obsStatus = "OBS projector가 닫혔습니다. 제한된 재탐색을 시작합니다.";
            }

            if (obsSearchAttempts >= MaxObsSearchAttempts)
            {
                StopObsSearch("OBS 탐색 중지: " + MaxObsSearchAttempts + "회 동안 projector를 찾지 못했습니다.");
                return;
            }

            obsSearchAttempts++;

            if (!obsProjectorRequested && ObsBridge.IsAlive())
                RequestObsProjector();

            IntPtr candidate = IntPtr.Zero;
            bool newFromBridge = false;
            if (bridgeRequestPending)
            {
                candidate = FindNewObsWindowAfterRequest();
                newFromBridge = candidate != IntPtr.Zero;
            }
            if (candidate == IntPtr.Zero) candidate = FindExistingObsProjector(true);

            if (candidate != IntPtr.Zero)
            {
                obsTimer.Stop();
                AttachObsWindow(candidate, newFromBridge);
                bridgeRequestPending = false;
                return;
            }

            string bridgeText = ObsBridge.IsAlive() ? "OBS Bridge READY" : "OBS Bridge 미연결";
            obsStatus = "OBS Program 탐색 중... " + obsSearchAttempts + "/" + MaxObsSearchAttempts +
                        "\n" + bridgeText + " / 자동 탐색은 약 " +
                        ((MaxObsSearchAttempts * ObsSearchIntervalMs) / 1000.0).ToString("0.#") + "초 후 멈춥니다.";
            Invalidate();
        }

        private HashSet<uint> GetObsProcessIds()
        {
            HashSet<uint> ids = new HashSet<uint>();
            string[] names = new string[] { "obs64", "obs32", "obs" };
            foreach (string name in names)
            {
                Process[] ps = null;
                try
                {
                    ps = Process.GetProcessesByName(name);
                    foreach (Process p in ps)
                    {
                        try { ids.Add((uint)p.Id); } catch { }
                    }
                }
                catch { }
                finally
                {
                    if (ps != null) foreach (Process p in ps) try { p.Dispose(); } catch { }
                }
            }
            return ids;
        }

        private List<IntPtr> EnumerateObsWindows()
        {
            List<IntPtr> result = new List<IntPtr>();
            HashSet<uint> obsPids = GetObsProcessIds();
            if (obsPids.Count == 0) return result;

            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr lp)
            {
                if (!Native.IsWindow(hwnd)) return true;
                uint pid; Native.GetWindowThreadProcessId(hwnd, out pid);
                if (obsPids.Contains(pid)) result.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private IntPtr FindNewObsWindowAfterRequest()
        {
            IntPtr best = IntPtr.Zero;
            int bestScore = int.MinValue;
            foreach (IntPtr hwnd in EnumerateObsWindows())
            {
                if (obsWindowsBeforeRequest.Contains(hwnd)) continue;
                int score = ScoreObsProjectorWindow(hwnd, true);
                if (score > bestScore) { bestScore = score; best = hwnd; }
            }
            return bestScore >= 5000 ? best : IntPtr.Zero;
        }

        private IntPtr FindExistingObsProjector(bool strict)
        {
            IntPtr best = IntPtr.Zero;
            int bestScore = int.MinValue;
            foreach (IntPtr hwnd in EnumerateObsWindows())
            {
                int score = ScoreObsProjectorWindow(hwnd, false);
                if (score > bestScore) { bestScore = score; best = hwnd; }
            }
            int need = strict ? 2400 : 3000;
            return bestScore >= need ? best : IntPtr.Zero;
        }

        private int ScoreObsProjectorWindow(IntPtr hwnd, bool newlyCreated)
        {
            if (hwnd == Handle || hwnd == owner.Handle || !Native.IsWindowVisible(hwnd)) return int.MinValue;

            StringBuilder ts = new StringBuilder(1024);
            Native.GetWindowText(hwnd, ts, ts.Capacity);
            string title = ts.ToString().Trim();
            StringBuilder cs = new StringBuilder(256);
            Native.GetClassName(hwnd, cs, cs.Capacity);
            string cls = cs.ToString();

            int score = newlyCreated ? 6000 : 0;
            string low = title.ToLowerInvariant();
            if (ContainsAny(low, "projector", "프로젝터")) score += 5000;
            if (ContainsAny(low, "program", "프로그램")) score += 2500;
            if (ContainsAny(low, "studio program")) score += 1200;
            if (cls.IndexOf("Qt", StringComparison.OrdinalIgnoreCase) >= 0) score += 250;

            if (ContainsAny(low, "audio mixer", "오디오 믹서", "settings", "설정", "scripts", "스크립트", "filters", "필터", "properties", "속성")) score -= 8000;
            if (ContainsAny(low, "obs 32", "obs studio") && ContainsAny(low, "profile", "프로파일", "scene", "장면")) score -= 9000;

            Native.RECT r;
            if (Native.GetWindowRect(hwnd, out r))
            {
                int w = Math.Abs(r.right - r.left), h = Math.Abs(r.bottom - r.top);
                if (w >= 300 && h >= 150) score += 400;
                if (w < 120 || h < 80) score -= 2000;
            }
            return score;
        }

        private static bool ContainsAny(string text, params string[] values)
        {
            if (text == null) return false;
            foreach (string value in values)
                if (text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void UnregisterObsThumbnail()
        {
            if (dwmThumbnail == IntPtr.Zero) return;
            try { Native.DwmUnregisterThumbnail(dwmThumbnail); } catch { }
            dwmThumbnail = IntPtr.Zero;
        }

        private void AttachObsWindow(IntPtr hwnd, bool openedByBridge)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;
            DetachObsWindow(false);

            obsWindow = hwnd;
            originalObsStyle = Native.GetStyle(hwnd);
            originalObsExStyle = Native.GetExStyle(hwnd);
            originalObsRectValid = Native.GetWindowRect(hwnd, out originalObsRect);
            obsOpenedByBridge = openedByBridge;

            try
            {
                // Keep the OBS projector as a TOP-LEVEL source window because DwmRegisterThumbnail
                // requires both source and destination to be top-level windows.
                long style = originalObsStyle;
                style &= ~(Native.WS_CHILD | Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_SYSMENU |
                           Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX | Native.WS_BORDER | Native.WS_DLGFRAME);
                style |= Native.WS_POPUP | Native.WS_CLIPCHILDREN | Native.WS_CLIPSIBLINGS;
                Native.SetStyle(hwnd, style);

                long ex = originalObsExStyle;
                ex &= ~(Native.WS_EX_APPWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT);
                ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                Native.SetExStyle(hwnd, ex);

                // OBS continues rendering a full 1920x1080 projector.  Park it off-screen, but do
                // not hide/minimize it; the DWM thumbnail stays a live GPU-composited connection.
                Native.EnableWindow(hwnd, false);
                Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, ObsParkingX, ObsParkingY, ObsRenderWidth, ObsRenderHeight,
                    Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
                Native.ShowWindow(hwnd, Native.SW_SHOWNOACTIVATE);

                // DWM registration is most reliable with a normal top-level destination.
                // Register first, then turn the destination into a layered/color-key window.
                long destinationEx = Native.GetExStyle(Handle);
                if ((destinationEx & Native.WS_EX_LAYERED) != 0)
                {
                    Native.SetExStyle(Handle, destinationEx & ~Native.WS_EX_LAYERED);
                    Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
                }

                int hr = Native.DwmRegisterThumbnail(Handle, hwnd, out dwmThumbnail);
                if (hr != 0 || dwmThumbnail == IntPtr.Zero)
                    throw new InvalidOperationException("DwmRegisterThumbnail failed: 0x" + hr.ToString("X8"));

                obsAttached = true;
                obsStatus = "OBS Program connected: 1920x1080 source / DWM GPU scale";
                ApplyObsDestinationTransparency();
                UpdateObsChildRect();
                obsTimer.Interval = 2000; // cheap health check only; no window scan while attached
                obsTimer.Start();
                Invalidate();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "AttachObsWindow/DWM");
                obsStatus = "OBS projector 연결 실패: " + ex.Message;
                obsAttached = false;
                UnregisterObsThumbnail();
                RestoreObsWindow(hwnd);
                obsWindow = IntPtr.Zero;
                Invalidate();
            }
        }

        private void UpdateObsChildRect()
        {
            if (!obsAttached || obsWindow == IntPtr.Zero || !Native.IsWindow(obsWindow) || dwmThumbnail == IntPtr.Zero) return;

            int inset = 0;
            int availW = Math.Max(1, ClientSize.Width - inset * 2);
            int availH = Math.Max(1, ClientSize.Height - inset * 2);

            int srcW = ObsRenderWidth, srcH = ObsRenderHeight;
            Native.SIZE sourceSize;
            if (Native.DwmQueryThumbnailSourceSize(dwmThumbnail, out sourceSize) == 0 && sourceSize.cx > 0 && sourceSize.cy > 0)
            {
                srcW = sourceSize.cx;
                srcH = sourceSize.cy;
            }

            double scale = Math.Min(availW / (double)srcW, availH / (double)srcH);
            int drawW = Math.Max(1, (int)Math.Round(srcW * scale));
            int drawH = Math.Max(1, (int)Math.Round(srcH * scale));
            int x = inset + (availW - drawW) / 2;
            int y = inset + (availH - drawH) / 2;

            Native.DWM_THUMBNAIL_PROPERTIES props = new Native.DWM_THUMBNAIL_PROPERTIES();
            props.dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY;
            props.rcDestination.left = x;
            props.rcDestination.top = y;
            props.rcDestination.right = x + drawW;
            props.rcDestination.bottom = y + drawH;
            // Keep the thumbnail itself opaque. The destination layered window applies
            // alpha to both the OBS image and its host background, avoiding black fade-in.
            props.opacity = (byte)255;
            props.fVisible = overlayVisible ? 1 : 0;
            props.fSourceClientAreaOnly = 1;
            int hr = Native.DwmUpdateThumbnailProperties(dwmThumbnail, ref props);
            if (hr != 0)
                obsStatus = "DWM thumbnail update 실패: 0x" + hr.ToString("X8");
        }

        private void RestoreObsWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;
            try
            {
                Native.EnableWindow(hwnd, true);
                Native.SetStyle(hwnd, originalObsStyle);
                Native.SetExStyle(hwnd, originalObsExStyle);
                if (originalObsRectValid)
                {
                    int w = Math.Max(100, originalObsRect.right - originalObsRect.left);
                    int h = Math.Max(60, originalObsRect.bottom - originalObsRect.top);
                    Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, originalObsRect.left, originalObsRect.top, w, h,
                        Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
                }
                else Native.ShowWindow(hwnd, Native.SW_SHOWNOACTIVATE);
            }
            catch { }
        }

        private void DetachObsWindow(bool closeIfBridgeCreated)
        {
            IntPtr hwnd = obsWindow;
            bool bridgeCreated = obsOpenedByBridge;
            obsTimer.Stop();
            UnregisterObsThumbnail();
            obsAttached = false;
            obsWindow = IntPtr.Zero;
            obsOpenedByBridge = false;
            bridgeRequestPending = false;
            obsProjectorRequested = false;

            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;
            RestoreObsWindow(hwnd);
            if (closeIfBridgeCreated && bridgeCreated)
            {
                try { Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { }
            }
        }

        private DragMode ModeAt(Point p)
        {
            Rectangle visual = GetVisualContentBounds(Bounds);
            visual.Offset(-Left, -Top);
            if (visual.Width <= 0 || visual.Height <= 0) visual = ClientRectangle;
            bool l = Math.Abs(p.X - visual.Left) < Grip;
            bool r = Math.Abs(p.X - visual.Right) < Grip;
            bool t = Math.Abs(p.Y - visual.Top) < Grip;
            bool b = Math.Abs(p.Y - visual.Bottom) < Grip;
            bool insideBand = p.X >= visual.Left - Grip && p.X <= visual.Right + Grip && p.Y >= visual.Top - Grip && p.Y <= visual.Bottom + Grip;
            if (!insideBand) return DragMode.Move;
            if (t && l) return DragMode.TopLeft; if (t && r) return DragMode.TopRight; if (b && l) return DragMode.BottomLeft; if (b && r) return DragMode.BottomRight;
            if (l) return DragMode.Left; if (r) return DragMode.Right; if (t) return DragMode.Top; if (b) return DragMode.Bottom; return DragMode.Move;
        }
        private Cursor CursorFor(DragMode m)
        {
            if (m == DragMode.Left || m == DragMode.Right) return Cursors.SizeWE;
            if (m == DragMode.Top || m == DragMode.Bottom) return Cursors.SizeNS;
            if (m == DragMode.TopLeft || m == DragMode.BottomRight) return Cursors.SizeNWSE;
            if (m == DragMode.TopRight || m == DragMode.BottomLeft) return Cursors.SizeNESW;
            return Cursors.SizeAll;
        }
        private static double AngleFromCenter(Point center, Point cursor)
        {
            return Math.Atan2(cursor.Y - center.Y, cursor.X - center.X) * 180.0 / Math.PI;
        }

        private static double NormalizeAngleDelta(double delta)
        {
            while (delta > 180.0) delta -= 360.0;
            while (delta < -180.0) delta += 360.0;
            return delta;
        }

        private void OnMouseWheelOverlay(object sender, MouseEventArgs e)
        {
            if (!editMode || e.Delta == 0) return;
            bool ctrlHeld = (Control.ModifierKeys & Keys.Control) == Keys.Control;
            if (!ctrlHeld) return;
            bool shiftHeld = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
            owner.ScaleOverlayByMouseWheel(this, e.Delta, !shiftHeld);
            HandledMouseEventArgs handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
        }

        private void OnMouseDownOverlay(object sender, MouseEventArgs e)
        {
            if (owner.HasSingleWebControl && !owner.IsSingleWebControl(this)) owner.ExitSingleWebControl(false);
            if (!editMode || e.Button != MouseButtons.Left) return;

            bool ctrlHeld = (Control.ModifierKeys & Keys.Control) == Keys.Control;
            bool shiftHeld = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
            DragMode hitMode = ModeAt(e.Location);
            // Shift+click on the body toggles multi-selection. Shift on a resize edge is reserved
            // for OBS-style image cropping.
            if (shiftHeld && !ctrlHeld && hitMode == DragMode.Move)
            {
                owner.ToggleOverlaySelectionForEditing(this);
                return;
            }

            bool ctrlRotate = ctrlHeld;
            if (ctrlRotate)
            {
                owner.PrepareOverlayForMouseRotation(this);
                mouseRotateStartDegrees.Clear();
                foreach (OverlayItemForm target in owner.GetMouseRotationTargets(this))
                    if (target.SupportsTransform) mouseRotateStartDegrees[target] = target.RotationDegrees;

                if (mouseRotateStartDegrees.Count == 0)
                {
                    owner.ReportStatus("OBS 화면은 DWM 방식이라 마우스 회전을 지원하지 않습니다.");
                    return;
                }

                mouseRotating = true;
                mouseRotateMoved = false;
                mouseRotateStartCursor = Cursor.Position;
                mouseRotateCenterScreen = new Point(Bounds.Left + Bounds.Width / 2, Bounds.Top + Bounds.Height / 2);
                mouseRotateStartAngle = AngleFromCenter(mouseRotateCenterScreen, mouseRotateStartCursor);
                Capture = true;
                Cursor = Cursors.Cross;
                return;
            }

            owner.SelectOverlayForEditing(this);
            if (Locked) return;
            owner.CaptureUndo("오버레이 위치/크기 변경");
            dragging = true; dragMode = hitMode; cropDragging = Type == ItemType.Image && hitMode != DragMode.Move && shiftHeld; dragStartMouse = Cursor.Position; dragStartBounds = Bounds; Capture = true;
            dragStartCropLeft = CropLeft; dragStartCropTop = CropTop; dragStartCropRight = CropRight; dragStartCropBottom = CropBottom;
            dragGroupStartBounds.Clear();
            if (dragMode == DragMode.Move && GroupId > 0)
            {
                foreach (OverlayItemForm member in owner.GetGroupMembers(this))
                    if (!member.Locked) dragGroupStartBounds[member] = member.Bounds;
            }
        }
        private void OnMouseMoveOverlay(object sender, MouseEventArgs e)
        {
            if (!editMode) return;

            if (mouseRotating)
            {
                Point nowRotate = Cursor.Position;
                int mdx = nowRotate.X - mouseRotateStartCursor.X;
                int mdy = nowRotate.Y - mouseRotateStartCursor.Y;
                if (!mouseRotateMoved && (mdx * mdx + mdy * mdy) < 9) return;

                if (!mouseRotateMoved)
                {
                    owner.CaptureUndo("마우스 자유 회전");
                    mouseRotateMoved = true;
                }

                double nowAngle = AngleFromCenter(mouseRotateCenterScreen, nowRotate);
                int delta = (int)Math.Round(NormalizeAngleDelta(nowAngle - mouseRotateStartAngle));
                int sourceStartDegrees;
                if (mouseRotateStartDegrees.TryGetValue(this, out sourceStartDegrees))
                {
                    int proposed = sourceStartDegrees + delta;
                    int snapped = owner.SnapMouseRotation(proposed);
                    delta += snapped - proposed;
                }
                foreach (KeyValuePair<OverlayItemForm, int> pair in mouseRotateStartDegrees)
                {
                    OverlayItemForm target = pair.Key;
                    if (target == null || target.IsDisposed || !target.SupportsTransform) continue;
                    target.SetTransform(pair.Value + delta, target.FlipHorizontal, target.FlipVertical, false);
                }
                owner.RefreshMouseTransformPreview();
                return;
            }

            if (Locked) { Cursor = Cursors.No; return; }
            if (!dragging)
            {
                bool ctrlRotate = (Control.ModifierKeys & Keys.Control) == Keys.Control && SupportsTransform;
                Cursor = ctrlRotate ? Cursors.Cross : CursorFor(ModeAt(e.Location));
                return;
            }
            Point now = Cursor.Position; int dx = now.X - dragStartMouse.X, dy = now.Y - dragStartMouse.Y;
            Rectangle r = dragStartBounds; int minW = 100, minH = 60;
            bool cropResize = cropDragging;
            if (dragMode == DragMode.Move)
            {
                r.X += dx; r.Y += dy;
                if ((Control.ModifierKeys & Keys.Shift) != Keys.Shift) r = owner.ApplyPlacementSnap(this, r);
                dx = r.X - dragStartBounds.X; dy = r.Y - dragStartBounds.Y;
            }
            else
            {
                if (dragMode == DragMode.Left || dragMode == DragMode.TopLeft || dragMode == DragMode.BottomLeft) { r.X += dx; r.Width -= dx; }
                if (dragMode == DragMode.Right || dragMode == DragMode.TopRight || dragMode == DragMode.BottomRight) r.Width += dx;
                if (dragMode == DragMode.Top || dragMode == DragMode.TopLeft || dragMode == DragMode.TopRight) { r.Y += dy; r.Height -= dy; }
                if (dragMode == DragMode.Bottom || dragMode == DragMode.BottomLeft || dragMode == DragMode.BottomRight) r.Height += dy;
                if (r.Width < minW) { if (dragMode == DragMode.Left || dragMode == DragMode.TopLeft || dragMode == DragMode.BottomLeft) r.X -= minW - r.Width; r.Width = minW; }
                if (r.Height < minH) { if (dragMode == DragMode.Top || dragMode == DragMode.TopLeft || dragMode == DragMode.TopRight) r.Y -= minH - r.Height; r.Height = minH; }

                if (cropResize && dragStartBounds.Width > 0 && dragStartBounds.Height > 0)
                {
                    int spanX = Math.Max(500, 10000 - dragStartCropLeft - dragStartCropRight);
                    int spanY = Math.Max(500, 10000 - dragStartCropTop - dragStartCropBottom);
                    int cropL = dragStartCropLeft, cropT = dragStartCropTop, cropR = dragStartCropRight, cropB = dragStartCropBottom;
                    if (dragMode == DragMode.Left || dragMode == DragMode.TopLeft || dragMode == DragMode.BottomLeft)
                        cropL += (int)Math.Round((r.Left - dragStartBounds.Left) * spanX / (double)Math.Max(1, dragStartBounds.Width));
                    if (dragMode == DragMode.Right || dragMode == DragMode.TopRight || dragMode == DragMode.BottomRight)
                        cropR += (int)Math.Round((dragStartBounds.Right - r.Right) * spanX / (double)Math.Max(1, dragStartBounds.Width));
                    if (dragMode == DragMode.Top || dragMode == DragMode.TopLeft || dragMode == DragMode.TopRight)
                        cropT += (int)Math.Round((r.Top - dragStartBounds.Top) * spanY / (double)Math.Max(1, dragStartBounds.Height));
                    if (dragMode == DragMode.Bottom || dragMode == DragMode.BottomLeft || dragMode == DragMode.BottomRight)
                        cropB += (int)Math.Round((dragStartBounds.Bottom - r.Bottom) * spanY / (double)Math.Max(1, dragStartBounds.Height));
                    SetCrop(cropL, cropT, cropR, cropB, false);
                }
                else if (Type == ItemType.Image && PreserveAspect && dragStartBounds.Height > 0)
                {
                    double aspect = dragStartBounds.Width / (double)dragStartBounds.Height;
                    if (dragMode == DragMode.Left || dragMode == DragMode.Right)
                    {
                        int newH = Math.Max(minH, (int)Math.Round(r.Width / aspect));
                        if (dragMode == DragMode.TopLeft || dragMode == DragMode.TopRight) r.Y = dragStartBounds.Bottom - newH;
                        r.Height = newH;
                    }
                    else if (dragMode == DragMode.Top || dragMode == DragMode.Bottom)
                    {
                        int newW = Math.Max(minW, (int)Math.Round(r.Height * aspect));
                        r.Width = newW;
                    }
                    else
                    {
                        int newH = Math.Max(minH, (int)Math.Round(r.Width / aspect));
                        if (dragMode == DragMode.TopLeft || dragMode == DragMode.TopRight) r.Y = dragStartBounds.Bottom - newH;
                        if (dragMode == DragMode.TopLeft || dragMode == DragMode.BottomLeft) r.X = dragStartBounds.Right - r.Width;
                        r.Height = newH;
                    }
                }
            }
            if (dragMode == DragMode.Move && dragGroupStartBounds.Count > 1)
            {
                foreach (KeyValuePair<OverlayItemForm, Rectangle> pair in dragGroupStartBounds)
                {
                    Rectangle gr = pair.Value;
                    gr.X += dx; gr.Y += dy;
                    pair.Key.Bounds = owner.NormalizeBounds(gr);
                }
            }
            else Bounds = owner.NormalizeBounds(r);
        }
        private void OnMouseUpOverlay(object sender, MouseEventArgs e)
        {
            if (mouseRotating)
            {
                mouseRotating = false;
                Capture = false;
                Cursor = Cursors.SizeAll;
                if (mouseRotateMoved)
                {
                    owner.ReapplyZOrder();
                    owner.SaveConfig();
                    owner.RefreshMouseTransformPreview();
                }
                mouseRotateMoved = false;
                mouseRotateStartDegrees.Clear();
                return;
            }

            if (!dragging) return; dragging = false; cropDragging = false; dragMode = DragMode.None; dragGroupStartBounds.Clear(); Capture = false; NormalizeFitBoundsToVisualContent(); UpdateSelectionFrame(); owner.ReapplyZOrder(); owner.SaveConfig();
        }

    }


    internal sealed class ScreenRegionCaptureForm : Form
    {
        private readonly Bitmap desktopImage;
        private Point dragStart;
        private Point dragCurrent;
        private bool dragging;
        private bool disposed;

        public Bitmap CapturedImage { get; private set; }
        public Rectangle CapturedScreenBounds { get; private set; }

        public ScreenRegionCaptureForm()
        {
            Rectangle virtualBounds = SystemInformation.VirtualScreen;
            if (virtualBounds.Width < 1 || virtualBounds.Height < 1) throw new InvalidOperationException("캡처할 화면을 찾지 못했습니다.");

            desktopImage = new Bitmap(virtualBounds.Width, virtualBounds.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(desktopImage))
                g.CopyFromScreen(virtualBounds.Left, virtualBounds.Top, 0, 0, virtualBounds.Size, CopyPixelOperation.SourceCopy);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = virtualBounds;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            BackColor = Color.Black;

            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    DialogResult = DialogResult.Cancel;
                    Close();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            MouseDown += OnCaptureMouseDown;
            MouseMove += OnCaptureMouseMove;
            MouseUp += OnCaptureMouseUp;
        }

        private static Rectangle NormalizeSelection(Point a, Point b)
        {
            int left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
            int right = Math.Max(a.X, b.X), bottom = Math.Max(a.Y, b.Y);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        private void OnCaptureMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragStart = e.Location;
            dragCurrent = e.Location;
            dragging = true;
            Capture = true;
            Invalidate();
        }

        private void OnCaptureMouseMove(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            dragCurrent = e.Location;
            Invalidate();
        }

        private void OnCaptureMouseUp(object sender, MouseEventArgs e)
        {
            if (!dragging || e.Button != MouseButtons.Left) return;
            dragCurrent = e.Location;
            dragging = false;
            Capture = false;
            Rectangle local = Rectangle.Intersect(NormalizeSelection(dragStart, dragCurrent), ClientRectangle);
            if (local.Width < 8 || local.Height < 8)
            {
                Invalidate();
                return;
            }

            CapturedImage = desktopImage.Clone(local, PixelFormat.Format32bppArgb);
            CapturedScreenBounds = new Rectangle(Bounds.Left + local.Left, Bounds.Top + local.Top, local.Width, local.Height);
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.DrawImageUnscaled(desktopImage, 0, 0);
            using (SolidBrush shade = new SolidBrush(Color.FromArgb(105, 0, 0, 0)))
                e.Graphics.FillRectangle(shade, ClientRectangle);

            using (Font guideFont = new Font("Segoe UI", 11F, FontStyle.Bold))
            {
                string guide = "원하는 영역을 마우스로 드래그하세요   ·   ESC 취소";
                SizeF guideSize = e.Graphics.MeasureString(guide, guideFont);
                float gx = Math.Max(8F, (ClientSize.Width - guideSize.Width) / 2F);
                RectangleF guideBox = new RectangleF(gx - 12F, 12F, guideSize.Width + 24F, guideSize.Height + 12F);
                using (SolidBrush guideBg = new SolidBrush(Color.FromArgb(205, 0, 0, 0))) e.Graphics.FillRectangle(guideBg, guideBox);
                using (SolidBrush guideFg = new SolidBrush(Color.White)) e.Graphics.DrawString(guide, guideFont, guideFg, gx, 18F);
            }

            if (!dragging) return;
            Rectangle selection = Rectangle.Intersect(NormalizeSelection(dragStart, dragCurrent), ClientRectangle);
            if (selection.Width < 1 || selection.Height < 1) return;

            GraphicsState state = e.Graphics.Save();
            e.Graphics.SetClip(selection);
            e.Graphics.DrawImageUnscaled(desktopImage, 0, 0);
            e.Graphics.Restore(state);
            using (Pen border = new Pen(Color.White, 2F)) e.Graphics.DrawRectangle(border, selection.X, selection.Y, Math.Max(0, selection.Width - 1), Math.Max(0, selection.Height - 1));

            string sizeText = selection.Width.ToString() + " x " + selection.Height.ToString();
            using (Font font = new Font("Segoe UI", 9F, FontStyle.Bold))
            {
                SizeF measured = e.Graphics.MeasureString(sizeText, font);
                int boxX = selection.X;
                int boxY = selection.Y - (int)Math.Ceiling(measured.Height) - 8;
                if (boxY < 4) boxY = selection.Y + 6;
                RectangleF box = new RectangleF(boxX, boxY, measured.Width + 12, measured.Height + 4);
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(190, 0, 0, 0))) e.Graphics.FillRectangle(bg, box);
                using (SolidBrush fg = new SolidBrush(Color.White)) e.Graphics.DrawString(sizeText, font, fg, box.X + 6, box.Y + 2);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                if (desktopImage != null) desktopImage.Dispose();
                if (CapturedImage != null) { CapturedImage.Dispose(); CapturedImage = null; }
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class MainForm : Form, IMessageFilter
    {
        private readonly TextBox input = new TextBox();
        private readonly Button editButton = new Button();
        private readonly Button hideButton = new Button();
        private readonly Button hotkeyEditButton = new Button();
        private readonly Button hotkeyHideButton = new Button();
        private readonly Button hotkeyDetailButton = new Button();
        private readonly Label obsBridgeLabel = new Label();
        private readonly Label statusLabel = new Label();
        private readonly Label versionLabel = new Label();
        private readonly ListView overlayList = new ListView();
        private readonly TrackBar opacityTrack = new TrackBar();
        private readonly Label opacityValueLabel = new Label();
        private readonly DarkNumberBox priorityNumber = new DarkNumberBox();
        private readonly DarkCheckBox clickThroughBox = new DarkCheckBox();
        private readonly DarkCheckBox visibleBox = new DarkCheckBox();
        private readonly DarkCheckBox lockedBox = new DarkCheckBox();
        private readonly DarkNumberBox posX = new DarkNumberBox();
        private readonly DarkNumberBox posY = new DarkNumberBox();
        private readonly DarkNumberBox sizeW = new DarkNumberBox();
        private readonly DarkNumberBox sizeH = new DarkNumberBox();
        private readonly Label selectedNameLabel = new Label();
        private readonly Label timerModeLabel = new Label();
        private readonly Label alarmNameLabel = new Label();
        private readonly Button priorityUpButton = new Button();
        private readonly Button priorityDownButton = new Button();
        private readonly Button timerAlarmButton = new Button();
        private readonly Button timerDefaultAlarmButton = new Button();
        private readonly Button timerPreviewButton = new Button();
        private readonly Button menuButton = new Button();
        private bool updateCheckRunning;
        private bool updateStartRequested;
        // Startup update prompts stay disabled until Menu > 업데이트 확인... is used.
        private bool suppressAutomaticUpdatePrompt;

        private enum UpdatePromptChoice { Update, Later, Never }
        private readonly PictureBox bannerBox = new PictureBox();
        private readonly PictureBox mascotBox = new PictureBox();
        private readonly Button trayButton = new Button();
        private readonly PictureBox starsBox = new PictureBox();
        private readonly Panel timerSettingsPanel = new Panel();
        private readonly Panel detailSettingsPanel = new Panel();
        private Panel leftPanel;
        private Panel centerPanel;
        private Panel rightPanel;
        private readonly DarkNumberBox rotationNumber = new DarkNumberBox();
        private readonly Button flipHorizontalButton = new Button();
        private readonly Button flipVerticalButton = new Button();
        private readonly Button resetRotationButton = new Button();
        private readonly Button resetTransformButton = new Button();
        private readonly Button groupButton = new Button();
        private readonly Button ungroupButton = new Button();
        private readonly Label groupStatusLabel = new Label();
        private readonly Timer bridgeUiTimer = new Timer();
        private readonly Timer foregroundUiTimer = new Timer();
        private readonly uint currentProcessId = unchecked((uint)Process.GetCurrentProcess().Id);
        private bool catLayerOwnsForeground = true;
        private string lastBridgeStatusText = null;
        private string lastSavedConfigSignature = null;
        private bool syncingMainUi;
        private bool opacityUndoCaptured;
        private bool mainUiReady;
        private readonly List<OverlayItemForm> items = new List<OverlayItemForm>();
        private readonly List<OverlayItemForm> overlayListDragSelection = new List<OverlayItemForm>();
        private readonly NotifyIcon trayIcon = new NotifyIcon();
        private bool trayHintShown;
        private int hotkeyEditVk = Native.VK_F8;
        private int hotkeyHideVk = Native.VK_F9;
        private int hotkeyDetailVk = Native.VK_F10;
        private int hotkeyCaptureVk = Native.VK_F1;
        private int hotkeyEditMods = 0;
        private int hotkeyHideMods = 0;
        private int hotkeyDetailMods = 0;
        private int hotkeyCaptureMods = 0;
        // Edit-action shortcuts are local to CatLayer edit windows, so ordinary Q/E/H/V do not steal keys system-wide.
        private int hotkeyGroupVk = (int)Keys.G;
        private int hotkeyGroupMods = Native.MOD_CONTROL;
        private int hotkeyUngroupVk = (int)Keys.G;
        private int hotkeyUngroupMods = Native.MOD_CONTROL | Native.MOD_SHIFT;
        private int hotkeyRotateMinus1Vk = (int)Keys.Q;
        private int hotkeyRotateMinus1Mods = 0;
        private int hotkeyRotatePlus1Vk = (int)Keys.E;
        private int hotkeyRotatePlus1Mods = 0;
        private int hotkeyRotateMinus10Vk = (int)Keys.Q;
        private int hotkeyRotateMinus10Mods = Native.MOD_SHIFT;
        private int hotkeyRotatePlus10Vk = (int)Keys.E;
        private int hotkeyRotatePlus10Mods = Native.MOD_SHIFT;
        private int hotkeyFlipHorizontalVk = (int)Keys.H;
        private int hotkeyFlipHorizontalMods = 0;
        private int hotkeyFlipVerticalVk = (int)Keys.V;
        private int hotkeyFlipVerticalMods = 0;
        private int hotkeyResetRotationVk = (int)Keys.R;
        private int hotkeyResetRotationMods = 0;
        private int hotkeyResetTransformVk = (int)Keys.R;
        private int hotkeyResetTransformMods = Native.MOD_SHIFT;
        private int zoomStepPercent = 10;
        private int rotationSnapDegrees = 5;
        private int placementSnapPixels = 8;
        private const int PresetHotkeyIdBase = 1000;
        private sealed class PresetHotkeyBinding
        {
            public string FileName = "";
            public int Mods;
            public int Vk;
        }
        private readonly List<PresetHotkeyBinding> presetHotkeys = new List<PresetHotkeyBinding>();
        private readonly Dictionary<int, PresetHotkeyBinding> registeredPresetHotkeys = new Dictionary<int, PresetHotkeyBinding>();
        private string currentPresetName = "";
        private readonly string configPath;
        private readonly string baseDataDir;
        private readonly string assetsDir;
        private readonly string soundsDir;
        private readonly string presetsDir;
        private readonly string groupsDir;
        private readonly string webFilesDir;
        private readonly string undoDir;
        private readonly List<UndoState> undoStates = new List<UndoState>();
        private const int MaxUndoStates = 10;
        private readonly Size mainBaseClientSize = new Size(1180, 760);
        private readonly Dictionary<Control, Rectangle> mainBaseBounds = new Dictionary<Control, Rectangle>();
        private readonly Dictionary<Control, float> mainBaseFonts = new Dictionary<Control, float>();
        private bool mainLayoutCaptured;
        private bool applyingMainLayout;
        private bool legacyDataMigrated;
        private bool configRecoveredFromBackup;
        public bool EditMode { get; private set; }
        public bool DetailEditMode { get; private set; }
        public bool WebControlMode { get; private set; }
        private OverlayItemForm singleWebControlOverlay;
        public bool HasSingleWebControl { get { return singleWebControlOverlay != null && !singleWebControlOverlay.IsDisposed; } }
        public bool CurrentEditorModeIsSingleWeb { get { return HasSingleWebControl && !WebControlMode; } }
        public EditorMode CurrentEditorMode { get { return WebControlMode ? EditorMode.WebControl : (EditMode ? EditorMode.Normal : EditorMode.Fixed); } }
        public bool AllHidden { get { return hidden; } }
        private bool hidden;
        private EditorMode modeBeforeWebControl = EditorMode.Normal;
        private int nextGroupId = 1;

        private sealed class UndoState
        {
            public string Path;
            public string Reason;
            public bool EditMode;
            public bool DetailEditMode;
            public bool Hidden;
            public int HotkeyEditVk;
            public int HotkeyHideVk;
            public int HotkeyDetailVk;
            public int HotkeyCaptureVk;
            public int HotkeyEditMods;
            public int HotkeyHideMods;
            public int HotkeyDetailMods;
            public int HotkeyCaptureMods;
            public int HotkeyGroupVk; public int HotkeyGroupMods;
            public int HotkeyUngroupVk; public int HotkeyUngroupMods;
            public int HotkeyRotateMinus1Vk; public int HotkeyRotateMinus1Mods;
            public int HotkeyRotatePlus1Vk; public int HotkeyRotatePlus1Mods;
            public int HotkeyRotateMinus10Vk; public int HotkeyRotateMinus10Mods;
            public int HotkeyRotatePlus10Vk; public int HotkeyRotatePlus10Mods;
            public int HotkeyFlipHorizontalVk; public int HotkeyFlipHorizontalMods;
            public int HotkeyFlipVerticalVk; public int HotkeyFlipVerticalMods;
            public int HotkeyResetRotationVk; public int HotkeyResetRotationMods;
            public int HotkeyResetTransformVk; public int HotkeyResetTransformMods;
            public int ZoomStepPercent;
            public int RotationSnapDegrees;
            public int PlacementSnapPixels;
            public List<PresetHotkeyBinding> PresetHotkeys;
            public string CurrentPresetName;
            public Size MainClientSize;
        }

        private static bool MigrateLegacyDataIfNeeded(string newRoot)
        {
            try
            {
                string legacyRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightOverlay");
                if (!Directory.Exists(legacyRoot) || string.Equals(Path.GetFullPath(legacyRoot), Path.GetFullPath(newRoot), StringComparison.OrdinalIgnoreCase)) return false;

                bool copied = false;
                Directory.CreateDirectory(newRoot);
                string[] files = { "config.txt", "config.txt.bak" };
                foreach (string name in files)
                {
                    string source = Path.Combine(legacyRoot, name);
                    string target = Path.Combine(newRoot, name);
                    if (File.Exists(source) && !File.Exists(target))
                    {
                        File.Copy(source, target, false);
                        copied = true;
                    }
                }

                string[] folders = { "Assets", "Sounds", "Presets", "Groups" };
                foreach (string folder in folders)
                {
                    string source = Path.Combine(legacyRoot, folder);
                    string target = Path.Combine(newRoot, folder);
                    if (Directory.Exists(source)) copied |= CopyDirectoryMissing(source, target);
                }
                return copied;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "MigrateLegacyData");
                return false;
            }
        }

        private static bool CopyDirectoryMissing(string source, string target)
        {
            bool copied = false;
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                string destination = Path.Combine(target, Path.GetFileName(file));
                if (!File.Exists(destination))
                {
                    File.Copy(file, destination, false);
                    copied = true;
                }
            }
            foreach (string directory in Directory.GetDirectories(source))
            {
                string destination = Path.Combine(target, Path.GetFileName(directory));
                copied |= CopyDirectoryMissing(directory, destination);
            }
            return copied;
        }

        public MainForm()
        {
            Text = "CatLayer v" + AppInfo.Version;
            ClientSize = mainBaseClientSize;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimumSize = new Size(900, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = UiBack;
            ForeColor = UiText;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            EditMode = true;
            DetailEditMode = true;
            DoubleBuffered = true;

            baseDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatLayer");
            legacyDataMigrated = MigrateLegacyDataIfNeeded(baseDataDir);
            assetsDir = Path.Combine(baseDataDir, "Assets");
            soundsDir = Path.Combine(baseDataDir, "Sounds");
            presetsDir = Path.Combine(baseDataDir, "Presets");
            groupsDir = Path.Combine(baseDataDir, "Groups");
            webFilesDir = Path.Combine(baseDataDir, "WebFiles");
            undoDir = Path.Combine(baseDataDir, "Undo");
            Directory.CreateDirectory(baseDataDir);
            Directory.CreateDirectory(assetsDir);
            Directory.CreateDirectory(soundsDir);
            Directory.CreateDirectory(presetsDir);
            Directory.CreateDirectory(groupsDir);
            Directory.CreateDirectory(webFilesDir);
            Directory.CreateDirectory(undoDir);
            try { foreach (string oldUndo in Directory.GetFiles(undoDir, "*.lopreset")) File.Delete(oldUndo); } catch { }
            configPath = Path.Combine(baseDataDir, "config.txt");

            BuildMainUi();
            EnableImageDropRecursive(this);
            Application.AddMessageFilter(this);

            bridgeUiTimer.Interval = 1000;
            bridgeUiTimer.Tick += delegate { UpdateBridgeStatus(); };
            bridgeUiTimer.Start();

            foregroundUiTimer.Interval = 200; // selection frame foreground polling; 5 Hz is responsive enough and cheaper
            foregroundUiTimer.Tick += delegate { UpdateForegroundSelectionState(); };
            foregroundUiTimer.Start();

            trayIcon.Text = "CatLayer";
            try { trayIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ContextMenuStrip trayMenu = new ContextMenuStrip();
            ToolStripMenuItem trayShow = new ToolStripMenuItem("열기"); trayShow.Click += delegate { ShowFromTray(); };
            ToolStripMenuItem trayEdit = new ToolStripMenuItem("편집 모드 전환"); trayEdit.Click += delegate { ToggleEdit(); };
            ToolStripMenuItem trayHide = new ToolStripMenuItem("전체 표시/숨김"); trayHide.Click += delegate { ToggleHidden(); };
            ToolStripMenuItem trayUndo = new ToolStripMenuItem("실행 취소"); trayUndo.Click += delegate { UndoLastAction(); };
            ToolStripMenuItem trayExit = new ToolStripMenuItem("종료"); trayExit.Click += delegate { trayIcon.Visible = false; Close(); };
            trayMenu.Items.Add(trayShow); trayMenu.Items.Add(trayEdit); trayMenu.Items.Add(trayHide); trayMenu.Items.Add(trayUndo); trayMenu.Items.Add(new ToolStripSeparator()); trayMenu.Items.Add(trayExit);
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.DoubleClick += delegate { ShowFromTray(); };
            try { SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged; } catch { }

            CaptureMainLayout();
            mainUiReady = true;
            Load += delegate
            {
                LoadConfig(); SetEditorMode(EditMode ? EditorMode.Normal : EditorMode.Fixed, false); MigrateLegacyStartupIfNeeded(); EnsureOverlaysOnScreen(false); ApplyHotkeys(); UpdateButtons(); UpdateBridgeStatus(); RefreshMainUi(); ScaleMainLayout(); TryEnableDarkTitleBar();
                if (configRecoveredFromBackup) SetStatus("config.txt 손상 감지: 백업에서 자동 복구됨");
                else if (legacyDataMigrated) SetStatus("LightOverlay 사용자 데이터를 CatLayer로 이전했습니다.");
            };
            Shown += delegate
            {
                // Prepare the shared WebView2 environment after CatLayer is already visible.
                // This moves the expensive first-time environment setup away from the moment
                // the user adds the first web overlay, without delaying CatLayer startup UI.
                try { WebOverlayEnvironment.WarmUp(); } catch { }
                BeginStartupUpdateCheck();
            };
            Resize += delegate { if (WindowState != FormWindowState.Minimized) ScaleMainLayout(); };
            ResizeEnd += delegate { if (WindowState == FormWindowState.Normal) { ScaleMainLayout(); SaveConfig(); } };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                SaveConfig();
                try { SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { }
                UnregisterAllHotkeys();
                bridgeUiTimer.Stop(); foregroundUiTimer.Stop(); trayIcon.Visible = false;
                try { if (bannerBox.Image != null) bannerBox.Image.Dispose(); } catch { }
                try { if (mascotBox.Image != null) mascotBox.Image.Dispose(); } catch { }
                try { if (starsBox.Image != null) starsBox.Image.Dispose(); } catch { }
                foreach (OverlayItemForm f in new List<OverlayItemForm>(items)) f.Dispose();
                try { foreach (UndoState state in undoStates) if (!string.IsNullOrEmpty(state.Path) && File.Exists(state.Path)) File.Delete(state.Path); } catch { }
            };
        }

        private static readonly Color UiBack = Color.FromArgb(7, 18, 38);
        private static readonly Color UiPanel = Color.FromArgb(13, 29, 55);
        private static readonly Color UiPanel2 = Color.FromArgb(20, 39, 69);
        private static readonly Color UiBorder = Color.FromArgb(42, 59, 88);
        private static readonly Color UiAccent = Color.FromArgb(145, 83, 255);
        private static readonly Color UiAccentSoft = Color.FromArgb(56, 43, 101);
        private static readonly Color UiText = Color.FromArgb(236, 240, 248);
        private static readonly Color UiMuted = Color.FromArgb(151, 162, 184);
        private static readonly Color UiDanger = Color.FromArgb(255, 93, 98);

        private void BuildMainUi()
        {
            Controls.Clear();

            // Keep the mascot asset for the installer, but do not show it in the main window.
            mascotBox.Image = null;
            mascotBox.Visible = false;

            Label appTitle = NewLabel("CatLayer", 20, 17, 220, 28, 16F, FontStyle.Bold, UiText);
            versionLabel.Text = "v" + AppInfo.Version;
            versionLabel.SetBounds(158, 19, 70, 22);
            versionLabel.ForeColor = UiMuted;
            versionLabel.BackColor = UiPanel2;
            versionLabel.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(versionLabel);
            starsBox.SetBounds(250, 16, 96, 24);
            starsBox.BackColor = Color.Transparent;
            starsBox.SizeMode = PictureBoxSizeMode.Zoom;
            starsBox.Image = LoadBundledImage("ui_stars.png");
            if (starsBox.Image != null) Controls.Add(starsBox);

            menuButton.Text = "메뉴";
            menuButton.SetBounds(1088, 14, 68, 30);
            StyleButton(menuButton, false);
            Controls.Add(menuButton);
            ContextMenuStrip appMenu = new ContextMenuStrip();
            ToolStripMenuItem obsHelp = new ToolStripMenuItem("OBS 연결 도움말"); obsHelp.Click += delegate { ShowBridgeHelp(); };
            ToolStripMenuItem shortcuts = new ToolStripMenuItem("바로가기 만들기"); shortcuts.Click += delegate { InstallShortcuts(); };
            ToolStripMenuItem importPreset = new ToolStripMenuItem("외부 프리셋 가져오기"); importPreset.Click += delegate { ImportPresetInteractive(); };
            ToolStripMenuItem openPresetFolder = new ToolStripMenuItem("프리셋 파일 위치 열기"); openPresetFolder.Click += delegate { OpenPresetFolder(); };
            ToolStripMenuItem updateItem = new ToolStripMenuItem("업데이트 확인...");
            updateItem.Click += delegate
            {
                if (suppressAutomaticUpdatePrompt)
                {
                    suppressAutomaticUpdatePrompt = false;
                    SaveConfig();
                }
                CheckForUpdates(true);
            };
            ToolStripMenuItem settingsItem = new ToolStripMenuItem("설정");
            ToolStripMenuItem hotkeySettingsItem = new ToolStripMenuItem("사용자 지정 단축키..."); hotkeySettingsItem.Click += delegate { ShowHotkeySettings(); };
            ToolStripMenuItem presetHotkeySettingsItem = new ToolStripMenuItem("프리셋 단축키..."); presetHotkeySettingsItem.Click += delegate { ShowPresetHotkeySettings(); };
            ToolStripMenuItem zoomSettingsItem = new ToolStripMenuItem("확대/축소 비율..."); zoomSettingsItem.Click += delegate { ShowZoomSettings(); };
            ToolStripMenuItem rotationSnapSettingsItem = new ToolStripMenuItem("회전 자석 감도..."); rotationSnapSettingsItem.Click += delegate { ShowRotationSnapSettings(); };
            ToolStripMenuItem placementSnapSettingsItem = new ToolStripMenuItem("배치 자석 감도..."); placementSnapSettingsItem.Click += delegate { ShowPlacementSnapSettings(); };
            settingsItem.DropDownItems.Add(hotkeySettingsItem);
            settingsItem.DropDownItems.Add(presetHotkeySettingsItem);
            settingsItem.DropDownItems.Add(zoomSettingsItem);
            settingsItem.DropDownItems.Add(rotationSnapSettingsItem);
            settingsItem.DropDownItems.Add(placementSnapSettingsItem);
            ToolStripMenuItem startupItem = new ToolStripMenuItem("컴퓨터 시작 시 실행");
            startupItem.CheckOnClick = false;
            startupItem.Click += delegate { SetStartupEnabled(!IsStartupEnabled()); startupItem.Checked = IsStartupEnabled(); };
            ToolStripMenuItem uninstallItem = new ToolStripMenuItem("CatLayer 제거...");
            uninstallItem.Click += delegate { RunInstalledUninstaller(); };
            appMenu.Items.Add(obsHelp);
            appMenu.Items.Add(shortcuts);
            appMenu.Items.Add(importPreset);
            appMenu.Items.Add(openPresetFolder);
            appMenu.Items.Add(new ToolStripSeparator());
            appMenu.Items.Add(updateItem);
            appMenu.Items.Add(settingsItem);
            appMenu.Items.Add(startupItem);
            appMenu.Items.Add(uninstallItem);
            appMenu.Opening += delegate
            {
                startupItem.Checked = IsStartupEnabled();
                uninstallItem.Enabled = true;
            };
            menuButton.Click += delegate { appMenu.Show(menuButton, new Point(0, menuButton.Height)); };

            leftPanel = NewPanel(18, 60, 275, 620);
            centerPanel = NewPanel(306, 60, 450, 620);
            rightPanel = NewPanel(769, 60, 393, 620);
            Panel left = leftPanel;
            Panel center = centerPanel;
            Panel right = rightPanel;

            // Left: add overlays
            NewLabelOn(left, "오버레이 추가", 18, 14, 220, 28, 13F, FontStyle.Bold, UiText);
            Button addImage = NewPanelButton(left, "이미지 / GIF", 18, 52, 155, 50); SetButtonIcon(addImage, "icon_image.png"); addImage.Click += delegate { AddImage(); };
            Button captureImage = NewPanelButton(left, "영역 캡처", 181, 52, 76, 50); SetButtonIcon(captureImage, "icon_capture.png"); captureImage.Click += delegate { AddScreenRegionCapture(); };
            Button addText = NewPanelButton(left, "텍스트", 18, 110, 239, 50); SetButtonIcon(addText, "icon_text.png"); addText.Click += delegate { AddTextInteractive(); };
            Button addTimer = NewPanelButton(left, "타이머", 18, 168, 239, 50); SetButtonIcon(addTimer, "icon_timer.png");
            ContextMenuStrip timerMenu = new ContextMenuStrip();
            ToolStripMenuItem oneShot = new ToolStripMenuItem("1회성 타이머"); oneShot.Click += delegate { AddTimerInteractive(TimerMode.OneShot); };
            ToolStripMenuItem repeat = new ToolStripMenuItem("반복 타이머"); repeat.Click += delegate { AddTimerInteractive(TimerMode.Repeat); };
            ToolStripMenuItem stopwatch = new ToolStripMenuItem("타임스톱"); stopwatch.Click += delegate { AddTimerInteractive(TimerMode.Stopwatch); };
            timerMenu.Items.Add(oneShot); timerMenu.Items.Add(repeat); timerMenu.Items.Add(stopwatch);
            addTimer.Click += delegate { timerMenu.Show(addTimer, new Point(0, addTimer.Height)); };
            Button addObs = NewPanelButton(left, "OBS 화면", 18, 226, 155, 50); SetButtonIcon(addObs, "icon_obs.png"); addObs.Click += delegate { AddObsProgram(); };
            Button addWeb = NewPanelButton(left, "웹", 181, 226, 76, 50); SetButtonIcon(addWeb, "icon_web.png");
            addWeb.ImageAlign = ContentAlignment.MiddleLeft; addWeb.TextAlign = ContentAlignment.MiddleCenter;
            addWeb.TextImageRelation = TextImageRelation.ImageBeforeText; addWeb.Padding = new Padding(6, 0, 4, 0);
            addWeb.Click += delegate { AddWebInteractive(); };

            NewSeparator(left, 18, 292, 239);
            NewLabelOn(left, "프리셋", 18, 306, 220, 26, 12F, FontStyle.Bold, UiText);
            Button savePreset = NewPanelButton(left, "프리셋 저장", 18, 340, 239, 38); SetButtonIcon(savePreset, "icon_save.png"); savePreset.Click += delegate { SavePresetInteractive(); };
            Button loadPreset = NewPanelButton(left, "프리셋 불러오기", 18, 384, 239, 38); SetButtonIcon(loadPreset, "icon_load.png"); loadPreset.Click += delegate { LoadPresetInteractive(); };
            Button deletePreset = NewPanelButton(left, "프리셋 삭제", 18, 428, 239, 38, true); SetButtonIcon(deletePreset, "icon_trash.png"); deletePreset.Click += delegate { DeletePresetInteractive(); };

            NewSeparator(left, 18, 482, 239);
            NewLabelOn(left, "전체 제어", 18, 496, 220, 26, 12F, FontStyle.Bold, UiText);
            Button clearAll = NewPanelButton(left, "전체 삭제", 18, 530, 112, 34, true); clearAll.Click += delegate { ClearAllInteractive(); };
            Button undo = NewPanelButton(left, "실행 취소", 145, 530, 112, 34); undo.Click += delegate { UndoLastAction(); };
            Button reset = NewPanelButton(left, "설정 초기화", 18, 572, 112, 34); reset.Click += delegate { ResetSettingsInteractive(); };
            editButton.SetBounds(145, 572, 112, 34); StyleButton(editButton, false); editButton.Click += delegate { CycleEditorMode(); }; left.Controls.Add(editButton);

            // Center: overlay list
            NewLabelOn(center, "목록", 18, 14, 260, 30, 13F, FontStyle.Bold, UiText);
            priorityUpButton.Text = "↑"; priorityUpButton.SetBounds(360, 13, 32, 30); StyleButton(priorityUpButton, false); center.Controls.Add(priorityUpButton);
            priorityDownButton.Text = "↓"; priorityDownButton.SetBounds(398, 13, 32, 30); StyleButton(priorityDownButton, false); center.Controls.Add(priorityDownButton);
            priorityUpButton.Click += delegate { MoveSelectedPriority(-1); };
            priorityDownButton.Click += delegate { MoveSelectedPriority(1); };

            overlayList.SetBounds(18, 56, 404, 518);
            overlayList.View = View.Details;
            overlayList.FullRowSelect = true;
            overlayList.HideSelection = false;
            overlayList.MultiSelect = true;
            overlayList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            overlayList.BackColor = UiPanel;
            overlayList.ForeColor = UiText;
            overlayList.BorderStyle = BorderStyle.None;
            overlayList.OwnerDraw = true;
            try { typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(overlayList, true, null); } catch { }
            overlayList.DrawColumnHeader += OverlayListDrawColumnHeader;
            overlayList.DrawItem += OverlayListDrawItem;
            overlayList.DrawSubItem += OverlayListDrawSubItem;
            overlayList.Columns.Add("항목", 220, HorizontalAlignment.Left);
            overlayList.Columns.Add("표시", 95, HorizontalAlignment.Center);
            overlayList.Columns.Add("순서", 70, HorizontalAlignment.Center);
            overlayList.Resize += delegate { ResizeOverlayColumns(); };
            overlayList.HandleCreated += delegate { ResizeOverlayColumns(); };
            overlayList.SelectedIndexChanged += delegate { RefreshPropertyEditor(); if (!syncingMainUi) RefreshOverlaySelectionVisuals(); };
            overlayList.ItemDrag += OverlayListItemDrag;
            overlayList.DragEnter += OverlayListReorderDragEnter;
            overlayList.DragOver += OverlayListReorderDragOver;
            overlayList.DragDrop += OverlayListReorderDragDrop;
            overlayList.DragLeave += delegate { overlayList.InsertionMark.Index = -1; };
            overlayList.MouseUp += OverlayListMouseUp;
            overlayList.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete)
                {
                    DeleteSelectedOverlays();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            ContextMenuStrip overlayListMenu = new ContextMenuStrip();
            ToolStripMenuItem renameOverlay = new ToolStripMenuItem("이름 변경");
            renameOverlay.Click += delegate { RenameSelectedOverlay(); };
            ToolStripMenuItem copyOverlay = new ToolStripMenuItem("복사");
            copyOverlay.Click += delegate { DuplicateSelectedOverlays(); };
            ToolStripMenuItem webToolsMenu = new ToolStripMenuItem("웹 도구");
            ToolStripMenuItem webChangeAddress = new ToolStripMenuItem("주소 변경..."); webChangeAddress.Click += delegate { OverlayItemForm f = SelectedOverlay; if (f != null) ChangeWebUrlInteractive(f); };
            ToolStripMenuItem webReload = new ToolStripMenuItem("새로고침"); webReload.Click += delegate { OverlayItemForm f = SelectedOverlay; if (f != null) f.ReloadWeb(); };
            ToolStripMenuItem webBack = new ToolStripMenuItem("뒤로"); webBack.Click += delegate { OverlayItemForm f = SelectedOverlay; if (f != null) f.GoBackWeb(); };
            ToolStripMenuItem webForward = new ToolStripMenuItem("앞으로"); webForward.Click += delegate { OverlayItemForm f = SelectedOverlay; if (f != null) f.GoForwardWeb(); };
            ToolStripMenuItem webCss = new ToolStripMenuItem("커스텀 CSS..."); webCss.Click += delegate { OverlayItemForm f = SelectedOverlay; if (f != null) EditWebCustomCssInteractive(f); };
            ToolStripMenuItem webOpacityMenu = new ToolStripMenuItem("전체 투명도..."); webOpacityMenu.Click += delegate { OverlayItemForm f = SelectedOverlay; if (f != null) { int? v = UiPrompt.AskOpacity(this, f.OpacityPercent); if (v.HasValue) { CaptureUndo("웹 전체 투명도 변경"); f.SetOpacityPercent(v.Value, true); } } };
            webToolsMenu.DropDownItems.Add(webChangeAddress); webToolsMenu.DropDownItems.Add(webReload); webToolsMenu.DropDownItems.Add(new ToolStripSeparator()); webToolsMenu.DropDownItems.Add(webBack); webToolsMenu.DropDownItems.Add(webForward); webToolsMenu.DropDownItems.Add(new ToolStripSeparator()); webToolsMenu.DropDownItems.Add(webOpacityMenu); webToolsMenu.DropDownItems.Add(webCss);
            ToolStripMenuItem makeGroup = new ToolStripMenuItem("선택 항목 그룹 만들기");
            makeGroup.Click += delegate { GroupSelectedOverlays(); };
            ToolStripMenuItem saveGroupFile = new ToolStripMenuItem("그룹 파일로 저장...");
            saveGroupFile.Click += delegate { SaveSelectedGroupInteractive(); };
            ToolStripMenuItem loadGroupFromMenu = new ToolStripMenuItem("그룹 파일 불러오기...");
            loadGroupFromMenu.Click += delegate { LoadGroupInteractive(); };
            ToolStripMenuItem breakGroup = new ToolStripMenuItem("그룹 해제");
            breakGroup.Click += delegate { UngroupSelectedOverlays(); };
            ToolStripMenuItem alignMenu = new ToolStripMenuItem("정렬");
            ToolStripMenuItem alignLeft = new ToolStripMenuItem("왼쪽 맞춤"); alignLeft.Click += delegate { AlignSelectedOverlays("left"); };
            ToolStripMenuItem alignCenter = new ToolStripMenuItem("가로 중앙 맞춤"); alignCenter.Click += delegate { AlignSelectedOverlays("centerx"); };
            ToolStripMenuItem alignRight = new ToolStripMenuItem("오른쪽 맞춤"); alignRight.Click += delegate { AlignSelectedOverlays("right"); };
            ToolStripMenuItem alignTop = new ToolStripMenuItem("위쪽 맞춤"); alignTop.Click += delegate { AlignSelectedOverlays("top"); };
            ToolStripMenuItem alignMiddle = new ToolStripMenuItem("세로 중앙 맞춤"); alignMiddle.Click += delegate { AlignSelectedOverlays("centery"); };
            ToolStripMenuItem alignBottom = new ToolStripMenuItem("아래쪽 맞춤"); alignBottom.Click += delegate { AlignSelectedOverlays("bottom"); };
            alignMenu.DropDownItems.Add(alignLeft); alignMenu.DropDownItems.Add(alignCenter); alignMenu.DropDownItems.Add(alignRight);
            alignMenu.DropDownItems.Add(new ToolStripSeparator());
            alignMenu.DropDownItems.Add(alignTop); alignMenu.DropDownItems.Add(alignMiddle); alignMenu.DropDownItems.Add(alignBottom);
            ToolStripMenuItem distributeMenu = new ToolStripMenuItem("간격 동일");
            ToolStripMenuItem distributeX = new ToolStripMenuItem("가로 간격 동일"); distributeX.Click += delegate { DistributeSelectedOverlays(true); };
            ToolStripMenuItem distributeY = new ToolStripMenuItem("세로 간격 동일"); distributeY.Click += delegate { DistributeSelectedOverlays(false); };
            distributeMenu.DropDownItems.Add(distributeX); distributeMenu.DropDownItems.Add(distributeY);
            ToolStripMenuItem deleteOverlay = new ToolStripMenuItem("선택 항목 삭제   Del");
            deleteOverlay.Click += delegate { DeleteSelectedOverlays(); };
            overlayListMenu.Items.Add(renameOverlay);
            overlayListMenu.Items.Add(copyOverlay);
            overlayListMenu.Items.Add(webToolsMenu);
            overlayListMenu.Items.Add(deleteOverlay);
            overlayListMenu.Items.Add(new ToolStripSeparator());
            overlayListMenu.Items.Add(alignMenu);
            overlayListMenu.Items.Add(distributeMenu);
            overlayListMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem listGroupMenu = new ToolStripMenuItem("그룹");
            listGroupMenu.DropDownItems.Add(makeGroup);
            listGroupMenu.DropDownItems.Add(saveGroupFile);
            listGroupMenu.DropDownItems.Add(loadGroupFromMenu);
            listGroupMenu.DropDownItems.Add(new ToolStripSeparator());
            listGroupMenu.DropDownItems.Add(breakGroup);
            overlayListMenu.Items.Add(listGroupMenu);
            overlayListMenu.Opening += delegate
            {
                OverlayItemForm selectedWeb = SelectedOverlay;
                bool isWeb = selectedWeb != null && selectedWeb.Type == ItemType.Web && overlayList.SelectedItems.Count == 1;
                webToolsMenu.Visible = isWeb;
                webBack.Enabled = isWeb && selectedWeb.CanWebGoBack;
                webForward.Enabled = isWeb && selectedWeb.CanWebGoForward;
            };
            overlayList.ContextMenuStrip = overlayListMenu;
            center.Controls.Add(overlayList);
            ResizeOverlayColumns();
            NewLabelOn(center, "목록 드래그 또는 ↑ ↓ 버튼으로 순서 변경", 18, 584, 390, 22, 9F, FontStyle.Regular, UiMuted);

            // Right: selected overlay properties
            NewLabelOn(right, "선택된 오버레이 속성", 18, 14, 300, 28, 13F, FontStyle.Bold, UiText);
            selectedNameLabel.SetBounds(18, 46, 340, 30); selectedNameLabel.ForeColor = UiAccent; selectedNameLabel.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold); selectedNameLabel.AutoEllipsis = true; right.Controls.Add(selectedNameLabel);
            NewSeparator(right, 18, 82, 357);

            NewLabelOn(right, "투명도", 18, 94, 100, 24, 10F, FontStyle.Bold, UiText);
            opacityTrack.AutoSize = false; opacityTrack.SetBounds(18, 122, 270, 34); opacityTrack.Minimum = 0; opacityTrack.Maximum = 100; opacityTrack.TickStyle = TickStyle.None; opacityTrack.BackColor = UiPanel; right.Controls.Add(opacityTrack);
            opacityValueLabel.SetBounds(300, 122, 58, 30); opacityValueLabel.ForeColor = UiText; opacityValueLabel.BackColor = UiPanel2; opacityValueLabel.TextAlign = ContentAlignment.MiddleCenter; right.Controls.Add(opacityValueLabel);
            opacityTrack.MouseDown += delegate { if (!syncingMainUi && SelectedOverlay != null) { CaptureUndo("투명도 변경"); opacityUndoCaptured = true; } };
            opacityTrack.Scroll += delegate { ApplyOpacityFromUi(false); };
            opacityTrack.MouseUp += delegate { if (opacityUndoCaptured) { opacityUndoCaptured = false; ApplyOpacityFromUi(true); } };

            NewLabelOn(right, "레이어 순서", 18, 164, 210, 24, 10F, FontStyle.Bold, UiText);
            priorityNumber.SetBounds(275, 160, 82, 28); priorityNumber.Minimum = 1; priorityNumber.Maximum = 999; right.Controls.Add(priorityNumber);
            priorityNumber.ValueChanged += delegate { ApplyPriorityFromUi(); };
            NewLabelOn(right, "숫자가 낮을수록 앞에 표시됩니다.", 18, 192, 300, 22, 9F, FontStyle.Regular, UiMuted);

            clickThroughBox.Text = "클릭 통과 (전체)"; clickThroughBox.SetBounds(18, 222, 200, 28); right.Controls.Add(clickThroughBox);
            clickThroughBox.CheckedChanged += delegate { if (!syncingMainUi) SetClickThroughFromUi(clickThroughBox.Checked); };
            NewLabelOn(right, "편집 모드가 꺼지면 모든 오버레이가 클릭을 통과합니다.", 18, 250, 350, 22, 8.5F, FontStyle.Regular, UiMuted);

            NewSeparator(right, 18, 280, 357);
            NewLabelOn(right, "위치 / 크기", 18, 294, 150, 24, 10F, FontStyle.Bold, UiText);
            NewLabelOn(right, "X", 18, 328, 20, 24, 9F, FontStyle.Regular, UiMuted); SetupNumber(posX, right, 42, 324, 95, -50000, 50000);
            NewLabelOn(right, "Y", 154, 328, 20, 24, 9F, FontStyle.Regular, UiMuted); SetupNumber(posY, right, 178, 324, 95, -50000, 50000);
            NewLabelOn(right, "W", 18, 366, 20, 24, 9F, FontStyle.Regular, UiMuted); SetupNumber(sizeW, right, 42, 362, 95, 100, 10000);
            NewLabelOn(right, "H", 154, 366, 20, 24, 9F, FontStyle.Regular, UiMuted); SetupNumber(sizeH, right, 178, 362, 95, 60, 10000);
            Button applyBounds = NewPanelButton(right, "적용", 288, 324, 69, 66); applyBounds.Click += delegate { ApplyBoundsFromUi(); };

            visibleBox.Text = "보이기"; visibleBox.SetBounds(18, 402, 110, 28); right.Controls.Add(visibleBox);
            visibleBox.CheckedChanged += delegate { ApplyVisibleFromUi(); };
            lockedBox.Text = "위치/크기 잠금"; lockedBox.SetBounds(145, 402, 180, 28); right.Controls.Add(lockedBox);
            lockedBox.CheckedChanged += delegate { ApplyLockedFromUi(); };

            timerSettingsPanel.SetBounds(18, 442, 357, 154);
            timerSettingsPanel.BackColor = UiPanel;
            timerSettingsPanel.BorderStyle = BorderStyle.None;
            right.Controls.Add(timerSettingsPanel);
            NewLabelOn(timerSettingsPanel, "타이머 설정", 14, 10, 180, 24, 10F, FontStyle.Bold, UiAccent);
            timerModeLabel.SetBounds(14, 42, 325, 24); timerModeLabel.ForeColor = Color.FromArgb(210, 218, 235); timerSettingsPanel.Controls.Add(timerModeLabel);
            NewLabelOn(timerSettingsPanel, "알람 사운드", 14, 72, 110, 22, 9F, FontStyle.Bold, UiText);
            alarmNameLabel.SetBounds(14, 98, 150, 32); alarmNameLabel.BackColor = UiPanel2; alarmNameLabel.ForeColor = UiText; alarmNameLabel.TextAlign = ContentAlignment.MiddleLeft; alarmNameLabel.Padding = new Padding(8,0,0,0); timerSettingsPanel.Controls.Add(alarmNameLabel);
            timerAlarmButton.Text = "선택"; timerAlarmButton.SetBounds(170, 98, 52, 32); StyleButton(timerAlarmButton, false); timerSettingsPanel.Controls.Add(timerAlarmButton); timerAlarmButton.Click += delegate { OverlayItemForm f = SelectedOverlay; if (f != null) SelectTimerAlarmFile(f); };
            timerDefaultAlarmButton.Text = "기본"; timerDefaultAlarmButton.SetBounds(227, 98, 52, 32); StyleButton(timerDefaultAlarmButton, false); timerSettingsPanel.Controls.Add(timerDefaultAlarmButton); timerDefaultAlarmButton.Click += delegate { OverlayItemForm f = SelectedOverlay; if (f != null) UseDefaultTimerAlarm(f); };
            timerPreviewButton.Text = "재생"; timerPreviewButton.SetBounds(284, 98, 55, 32); StyleButton(timerPreviewButton, false); timerSettingsPanel.Controls.Add(timerPreviewButton); timerPreviewButton.Click += delegate { OverlayItemForm f = SelectedOverlay; if (f != null && f.Type == ItemType.Timer && f.TimerKind != TimerMode.Stopwatch) AudioAlert.Play(f.AlarmPath); };
            timerSettingsPanel.Visible = false;

            detailSettingsPanel.SetBounds(18, 442, 357, 154);
            detailSettingsPanel.BackColor = UiPanel;
            detailSettingsPanel.BorderStyle = BorderStyle.None;
            right.Controls.Add(detailSettingsPanel);
            NewLabelOn(detailSettingsPanel, "편집 도구", 14, 6, 110, 24, 10F, FontStyle.Bold, UiAccent);
            NewLabelOn(detailSettingsPanel, "회전", 14, 38, 45, 24, 9F, FontStyle.Bold, UiText);
            rotationNumber.SetBounds(64, 34, 92, 28); rotationNumber.Minimum = -180; rotationNumber.Maximum = 180; detailSettingsPanel.Controls.Add(rotationNumber);
            NewLabelOn(detailSettingsPanel, "°", 160, 38, 18, 24, 9F, FontStyle.Regular, UiMuted);
            rotationNumber.ValueChanged += delegate { ApplyRotationFromUi(); };
            flipHorizontalButton.Text = "좌우 반전"; flipHorizontalButton.SetBounds(188, 34, 72, 28); StyleButton(flipHorizontalButton, false); detailSettingsPanel.Controls.Add(flipHorizontalButton); flipHorizontalButton.Click += delegate { FlipSelection(true); };
            flipVerticalButton.Text = "상하 반전"; flipVerticalButton.SetBounds(266, 34, 72, 28); StyleButton(flipVerticalButton, false); detailSettingsPanel.Controls.Add(flipVerticalButton); flipVerticalButton.Click += delegate { FlipSelection(false); };
            groupStatusLabel.SetBounds(14, 70, 324, 20); groupStatusLabel.ForeColor = UiMuted; groupStatusLabel.Font = new Font(Font.FontFamily, 8.5F); detailSettingsPanel.Controls.Add(groupStatusLabel);
            groupButton.Text = "그룹 만들기"; groupButton.SetBounds(14, 96, 100, 30); StyleButton(groupButton, false); detailSettingsPanel.Controls.Add(groupButton); groupButton.Click += delegate { GroupSelectedOverlays(); };
            ungroupButton.Text = "그룹 해제"; ungroupButton.SetBounds(120, 96, 100, 30); StyleButton(ungroupButton, false); detailSettingsPanel.Controls.Add(ungroupButton); ungroupButton.Click += delegate { UngroupSelectedOverlays(); };
            resetRotationButton.Text = "각도 0"; resetRotationButton.SetBounds(226, 96, 52, 30); StyleButton(resetRotationButton, false); detailSettingsPanel.Controls.Add(resetRotationButton); resetRotationButton.Click += delegate { ResetSelectionTransform(false); };
            resetTransformButton.Text = "전체 0"; resetTransformButton.SetBounds(284, 96, 54, 30); StyleButton(resetTransformButton, false); detailSettingsPanel.Controls.Add(resetTransformButton); resetTransformButton.Click += delegate { ResetSelectionTransform(true); };
            NewLabelOn(detailSettingsPanel, "Shift+크기 조절 = 이미지 자르기  |  Ctrl+드래그 = 회전", 14, 130, 330, 18, 8F, FontStyle.Regular, UiMuted);
            detailSettingsPanel.Visible = false;

            // Bottom hotkeys and status
            Panel hotkeys = NewPanel(18, 694, 710, 48);
            NewLabelOn(hotkeys, "단축키", 14, 12, 82, 24, 10F, FontStyle.Bold, UiText);
            hotkeyEditButton.SetBounds(96, 8, 112, 32); StyleButton(hotkeyEditButton, false); hotkeyEditButton.Text = "편집  F8"; hotkeyEditButton.Click += delegate { ShowHotkeySettings(); }; hotkeys.Controls.Add(hotkeyEditButton);
            hotkeyHideButton.SetBounds(214, 8, 112, 32); StyleButton(hotkeyHideButton, false); hotkeyHideButton.Text = "숨김  F9"; hotkeyHideButton.Click += delegate { ShowHotkeySettings(); }; hotkeys.Controls.Add(hotkeyHideButton);
            hotkeyDetailButton.SetBounds(332, 8, 112, 32); StyleButton(hotkeyDetailButton, false); hotkeyDetailButton.Text = "웹  F10"; hotkeyDetailButton.Click += delegate { ToggleWebControlMode(); }; hotkeys.Controls.Add(hotkeyDetailButton);
            hideButton.SetBounds(450, 8, 112, 32); StyleButton(hideButton, false); hideButton.Click += delegate { ToggleHidden(); }; hotkeys.Controls.Add(hideButton);
            hideButton.Text = "전체 숨김";
            trayButton.SetBounds(568, 8, 126, 32); StyleButton(trayButton, false); trayButton.Text = "백그라운드"; trayButton.Click += delegate { HideToTray(); }; hotkeys.Controls.Add(trayButton);

            Panel status = NewPanel(741, 694, 421, 48);
            obsBridgeLabel.SetBounds(14, 8, 155, 16); obsBridgeLabel.ForeColor = UiMuted; obsBridgeLabel.Font = new Font(Font.FontFamily, 8F); status.Controls.Add(obsBridgeLabel);
            statusLabel.SetBounds(14, 24, 390, 18); statusLabel.ForeColor = UiMuted; statusLabel.Font = new Font(Font.FontFamily, 8F); status.Controls.Add(statusLabel);
            statusLabel.Text = "준비";
        }

        private void SetButtonIcon(Button button, string fileName)
        {
            try
            {
                Image img = LoadBundledImage(fileName);
                if (img == null) return;
                bool compact = button.Width <= 90;
                int iconSize = compact ? (string.Equals(fileName, "icon_web.png", StringComparison.OrdinalIgnoreCase) ? 20 : 16) : 18;
                button.Image = new Bitmap(img, new Size(iconSize, iconSize));
                img.Dispose();
                if (compact)
                {
                    button.ImageAlign = ContentAlignment.TopCenter;
                    button.TextAlign = ContentAlignment.BottomCenter;
                    button.TextImageRelation = TextImageRelation.ImageAboveText;
                    button.Padding = new Padding(2, 3, 2, 3);
                }
                else
                {
                    button.ImageAlign = ContentAlignment.MiddleLeft;
                    button.TextAlign = ContentAlignment.MiddleLeft;
                    button.TextImageRelation = TextImageRelation.ImageBeforeText;
                    button.Padding = new Padding(12, 0, 12, 0);
                }
            }
            catch { }
        }

        private void ResizeOverlayColumns()
        {
            if (overlayList == null || overlayList.Columns.Count < 3 || overlayList.ClientSize.Width <= 40) return;

            // ClientSize is already the drawable list area. Subtracting a scrollbar width here
            // leaves an unpainted header strip on systems where no vertical scrollbar is present.
            int w = Math.Max(120, overlayList.ClientSize.Width);
            int c2 = Math.Max(58, (int)Math.Round(w * 0.20));
            int c3 = Math.Max(52, (int)Math.Round(w * 0.15));
            int c1 = w - c2 - c3;

            if (c1 < 80)
            {
                c1 = 80;
                int remain = Math.Max(2, w - c1);
                c2 = Math.Max(1, (int)Math.Round(remain * 0.56));
                c3 = Math.Max(1, remain - c2);
            }

            // Make the three columns end exactly at the right edge so the native header has
            // no white remainder. The last assignment absorbs any rounding difference.
            int c3Final = Math.Max(1, w - c1 - c2);
            if (overlayList.Columns[0].Width == c1 && overlayList.Columns[1].Width == c2 && overlayList.Columns[2].Width == c3Final) return;
            overlayList.Columns[0].Width = c1;
            overlayList.Columns[1].Width = c2;
            overlayList.Columns[2].Width = c3Final;
            overlayList.Invalidate();
        }

        private Image LoadBundledImage(string fileName)
        {
            try
            {
                string p1 = Path.Combine(Application.StartupPath, "assets", fileName);
                if (File.Exists(p1)) return Image.FromFile(p1);
            }
            catch { }
            return null;
        }

        private Panel NewPanel(int x, int y, int w, int h)
        {
            Panel p = new Panel(); p.SetBounds(x, y, w, h); p.BackColor = UiPanel; p.BorderStyle = BorderStyle.None; Controls.Add(p); return p;
        }

        private Panel NewChildPanel(Control parent, int x, int y, int w, int h)
        {
            Panel p = new Panel(); p.SetBounds(x, y, w, h); p.BackColor = UiPanel; p.BorderStyle = BorderStyle.None; parent.Controls.Add(p); return p;
        }

        private Label NewLabel(string text, int x, int y, int w, int h, float size, FontStyle style, Color color)
        {
            Label l = new Label(); l.Text = text; l.SetBounds(x, y, w, h); l.ForeColor = color; l.BackColor = Color.Transparent; l.Font = new Font(Font.FontFamily, size, style); Controls.Add(l); return l;
        }

        private Label NewLabelOn(Control parent, string text, int x, int y, int w, int h, float size, FontStyle style, Color color)
        {
            Label l = new Label(); l.Text = text; l.SetBounds(x, y, w, h); l.ForeColor = color; l.BackColor = Color.Transparent; l.Font = new Font(Font.FontFamily, size, style); parent.Controls.Add(l); return l;
        }

        private void NewSeparator(Control parent, int x, int y, int w)
        {
            Panel line = new Panel(); line.SetBounds(x, y, w, 1); line.BackColor = UiBorder; parent.Controls.Add(line);
        }

        private Button NewPanelButton(Control parent, string text, int x, int y, int w, int h)
        {
            return NewPanelButton(parent, text, x, y, w, h, false);
        }

        private Button NewPanelButton(Control parent, string text, int x, int y, int w, int h, bool danger)
        {
            Button b = new Button(); b.Text = text; b.SetBounds(x, y, w, h); StyleButton(b, danger); parent.Controls.Add(b); return b;
        }

        private void StyleButton(Button b, bool danger)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = danger ? Color.FromArgb(120, 75, 82) : UiBorder;
            b.BackColor = UiPanel2;
            b.ForeColor = danger ? UiDanger : UiText;
            b.Cursor = Cursors.Hand;
        }

        private void SetupNumber(DarkNumberBox n, Control parent, int x, int y, int w, decimal min, decimal max)
        {
            n.SetBounds(x, y, w, 28); n.Minimum = min; n.Maximum = max; parent.Controls.Add(n);
        }

        private OverlayItemForm SelectedOverlay
        {
            get
            {
                if (overlayList.SelectedItems.Count == 0) return null;
                return overlayList.SelectedItems[0].Tag as OverlayItemForm;
            }
        }

        private List<OverlayItemForm> SelectedOverlays
        {
            get
            {
                int count = overlayList.SelectedItems.Count;
                List<OverlayItemForm> result = new List<OverlayItemForm>(count);
                HashSet<OverlayItemForm> seen = new HashSet<OverlayItemForm>();
                foreach (ListViewItem row in overlayList.SelectedItems)
                {
                    OverlayItemForm f = row.Tag as OverlayItemForm;
                    if (f != null && seen.Add(f)) result.Add(f);
                }
                return result;
            }
        }

        public bool ShouldShowSelectionVisuals { get { return EditMode && catLayerOwnsForeground; } }

        public bool IsOverlaySelected(OverlayItemForm source)
        {
            if (source == null || overlayList == null || overlayList.IsDisposed) return false;
            foreach (ListViewItem row in overlayList.SelectedItems)
                if (object.ReferenceEquals(row.Tag as OverlayItemForm, source)) return true;
            return false;
        }

        private void UpdateForegroundSelectionState()
        {
            bool ownsForeground = false;
            try
            {
                IntPtr hwnd = Native.GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    uint pid;
                    Native.GetWindowThreadProcessId(hwnd, out pid);
                    ownsForeground = pid == currentProcessId;
                }
            }
            catch { ownsForeground = false; }
            if (catLayerOwnsForeground == ownsForeground) return;
            catLayerOwnsForeground = ownsForeground;
            if (!ownsForeground && WebControlMode)
            {
                SetEditorMode(EditorMode.Normal, false);
                SetStatus("편집 모드");
            }
            RefreshOverlaySelectionVisuals();
        }

        private void RefreshOverlaySelectionVisuals()
        {
            foreach (OverlayItemForm f in items)
                if (f != null && !f.IsDisposed) f.RefreshSelectionVisual();
        }

        public List<OverlayItemForm> GetGroupMembers(OverlayItemForm source)
        {
            List<OverlayItemForm> result = new List<OverlayItemForm>(Math.Min(items.Count, 8));
            if (source == null) return result;
            if (source.GroupId <= 0) { result.Add(source); return result; }
            foreach (OverlayItemForm f in items)
                if (f != null && !f.IsDisposed && f.GroupId == source.GroupId) result.Add(f);
            if (result.Count == 0) result.Add(source);
            return result;
        }

        public void SelectOverlayForEditing(OverlayItemForm source)
        {
            if (source == null || overlayList == null || overlayList.IsDisposed) return;
            HashSet<OverlayItemForm> wanted = new HashSet<OverlayItemForm>(GetGroupMembers(source));
            foreach (ListViewItem row in overlayList.Items)
            {
                OverlayItemForm f = row.Tag as OverlayItemForm;
                row.Selected = f != null && wanted.Contains(f);
            }
            RefreshPropertyEditor();
        }

        public void ToggleOverlaySelectionForEditing(OverlayItemForm source)
        {
            if (source == null || overlayList == null || overlayList.IsDisposed) return;
            HashSet<OverlayItemForm> targets = new HashSet<OverlayItemForm>(GetGroupMembers(source));
            bool allSelected = true;
            foreach (OverlayItemForm target in targets)
            {
                bool selected = false;
                foreach (ListViewItem row in overlayList.SelectedItems)
                {
                    if (object.ReferenceEquals(row.Tag as OverlayItemForm, target)) { selected = true; break; }
                }
                if (!selected) { allSelected = false; break; }
            }
            foreach (ListViewItem row in overlayList.Items)
            {
                OverlayItemForm item = row.Tag as OverlayItemForm;
                if (item != null && targets.Contains(item)) row.Selected = !allSelected;
            }
            RefreshPropertyEditor();
            SetStatus(SelectedOverlays.Count.ToString() + "개 항목 선택됨  |  그룹화 단축키로 바로 묶을 수 있습니다.");
        }

        public void PrepareOverlayForMouseRotation(OverlayItemForm source)
        {
            if (source == null || overlayList == null || overlayList.IsDisposed) return;
            bool alreadySelected = false;
            foreach (ListViewItem row in overlayList.SelectedItems)
            {
                if (object.ReferenceEquals(row.Tag as OverlayItemForm, source)) { alreadySelected = true; break; }
            }
            if (!alreadySelected) SelectOverlayForEditing(source);
            else RefreshPropertyEditor();
        }

        public List<OverlayItemForm> GetMouseRotationTargets(OverlayItemForm source)
        {
            List<OverlayItemForm> selected = SelectedOverlays;
            if (source != null && selected.Contains(source) && selected.Count > 0) return selected;
            return GetGroupMembers(source);
        }

        public void RefreshMouseTransformPreview()
        {
            if (!mainUiReady || IsDisposed) return;
            RefreshPropertyEditor();
        }

        public void ReportStatus(string text)
        {
            SetStatus(text);
        }

        private string DisplayName(OverlayItemForm f)
        {
            if (f == null) return "선택 없음";
            if (!string.IsNullOrWhiteSpace(f.CustomName)) return f.CustomName;
            return DefaultDisplayName(f);
        }

        private string DefaultDisplayName(OverlayItemForm f)
        {
            if (f == null) return "선택 없음";
            if (f.Type == ItemType.Image)
            {
                string ext = Path.GetExtension(f.Data) ?? "";
                return string.Equals(ext, ".gif", StringComparison.OrdinalIgnoreCase) ? "GIF" : "이미지";
            }
            if (f.Type == ItemType.Text) return string.IsNullOrWhiteSpace(f.Data) ? "텍스트" : ShortText(f.Data, 10);
            if (f.Type == ItemType.Timer)
            {
                if (f.TimerKind == TimerMode.OneShot) return "1회 타이머";
                if (f.TimerKind == TimerMode.Repeat) return "반복 타이머";
                return "스톱워치";
            }
            if (f.Type == ItemType.Web)
            {
                try { Uri u = new Uri(f.Data); return "웹 · " + u.Host; } catch { return "웹"; }
            }
            return "OBS";
        }

        private static string ShortText(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "텍스트";
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private void RefreshMainUi()
        {
            if (!mainUiReady || overlayList == null || overlayList.IsDisposed) return;
            syncingMainUi = true;
            try
            {
                HashSet<OverlayItemForm> selected = new HashSet<OverlayItemForm>(SelectedOverlays);
                overlayList.BeginUpdate();
                overlayList.Items.Clear();
                for (int priority = 1; priority <= items.Count; priority++)
                {
                    OverlayItemForm f = items[items.Count - priority];
                    ListViewItem row = new ListViewItem(DisplayName(f));
                    row.Tag = f;
                    row.SubItems.Add(f.IsOverlayVisible ? "켜짐" : "꺼짐");
                    row.SubItems.Add(priority.ToString());
                    if (selected.Contains(f)) row.Selected = true;
                    overlayList.Items.Add(row);
                }
                overlayList.EndUpdate();
                ResizeOverlayColumns();
                if (overlayList.SelectedItems.Count == 0 && overlayList.Items.Count > 0) overlayList.Items[0].Selected = true;
            }
            finally { syncingMainUi = false; }
            RefreshPropertyEditor();
            RefreshOverlaySelectionVisuals();
        }

        private void OverlayListDrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            Rectangle r = e.Bounds;
            if (e.ColumnIndex == overlayList.Columns.Count - 1)
                r = new Rectangle(e.Bounds.X, e.Bounds.Y, Math.Max(1, overlayList.ClientSize.Width - e.Bounds.X), e.Bounds.Height);
            using (SolidBrush b = new SolidBrush(UiPanel2)) e.Graphics.FillRectangle(b, r);
            using (Pen p = new Pen(UiBorder)) e.Graphics.DrawLine(p, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            if (e.Header.TextAlign == HorizontalAlignment.Center) flags |= TextFormatFlags.HorizontalCenter;
            else flags |= TextFormatFlags.Left | TextFormatFlags.LeftAndRightPadding;
            TextRenderer.DrawText(e.Graphics, e.Header.Text, overlayList.Font, r, UiText, flags);
        }

        private void DrawOverlayListRow(Graphics graphics, ListViewItem item, int y, int height)
        {
            if (graphics == null || item == null || height <= 0) return;

            Rectangle row = new Rectangle(0, y, Math.Max(1, overlayList.ClientSize.Width), height);
            Color back = item.Selected ? Color.FromArgb(34, 62, 112) : UiPanel;
            using (SolidBrush b = new SolidBrush(back)) graphics.FillRectangle(b, row);

            int x = 0;
            for (int i = 0; i < overlayList.Columns.Count; i++)
            {
                int width = overlayList.Columns[i].Width;
                Rectangle cell = new Rectangle(x, y, width, height);
                string text = i < item.SubItems.Count ? item.SubItems[i].Text : "";
                TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
                if (overlayList.Columns[i].TextAlign == HorizontalAlignment.Center) flags |= TextFormatFlags.HorizontalCenter;
                else flags |= TextFormatFlags.Left | TextFormatFlags.LeftAndRightPadding;
                TextRenderer.DrawText(graphics, text, overlayList.Font, cell, UiText, flags);
                x += width;
            }

            using (Pen p = new Pen(Color.FromArgb(22, 34, 64))) graphics.DrawLine(p, row.Left, row.Bottom - 1, row.Right, row.Bottom - 1);
        }

        private void OverlayListDrawItem(object sender, DrawListViewItemEventArgs e)
        {
            // Some ListView repaint paths redraw the item without reliably repainting every
            // subitem. Paint the full row here so "표시"/"순서" cannot disappear.
            DrawOverlayListRow(e.Graphics, e.Item, e.Bounds.Y, e.Bounds.Height);
        }

        private void OverlayListDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            // Subitem events may be clipped to one cell; drawing the same row is safe because
            // GDI+ respects that clip and keeps every cell consistent with DrawItem.
            DrawOverlayListRow(e.Graphics, e.Item, e.Bounds.Y, e.Bounds.Height);
        }

        private void RefreshPropertyEditor()
        {
            if (!mainUiReady) return;
            syncingMainUi = true;
            try
            {
                OverlayItemForm f = SelectedOverlay;
                bool has = f != null;
                bool isTimer = has && f.Type == ItemType.Timer;
                bool isWeb = has && f.Type == ItemType.Web;
                List<OverlayItemForm> selectedItems = SelectedOverlays;
                selectedNameLabel.Text = has ? DisplayName(f) + (f.GroupId > 0 ? "  ·  그룹 " + f.GroupId.ToString() : "") : "선택 없음";
                opacityTrack.Enabled = has; priorityNumber.Enabled = has; visibleBox.Enabled = has; lockedBox.Enabled = has;
                posX.Enabled = has; posY.Enabled = has; sizeW.Enabled = has; sizeH.Enabled = has;
                detailSettingsPanel.Visible = has && !isTimer;
                timerSettingsPanel.Visible = isTimer;
                OverlayItemForm transformItem = null;
                foreach (OverlayItemForm selectedItem in selectedItems) if (selectedItem.SupportsTransform) { transformItem = selectedItem; break; }
                bool transformSupported = transformItem != null;
                rotationNumber.Enabled = transformSupported;
                flipHorizontalButton.Enabled = transformSupported;
                flipVerticalButton.Enabled = transformSupported;
                resetRotationButton.Enabled = transformSupported;
                resetTransformButton.Enabled = transformSupported;
                groupButton.Enabled = selectedItems.Count >= 2;
                bool hasGroup = false; foreach (OverlayItemForm selectedItem in selectedItems) if (selectedItem.GroupId > 0) { hasGroup = true; break; }
                ungroupButton.Enabled = hasGroup;
                groupStatusLabel.Text = !has ? "선택 없음" : (selectedItems.Count > 1 ? selectedItems.Count.ToString() + "개 항목 선택됨" : (f.GroupId > 0 ? "현재 그룹: " + f.GroupId.ToString() : "그룹 없음"));
                timerAlarmButton.Enabled = isTimer && f.TimerKind != TimerMode.Stopwatch;
                timerDefaultAlarmButton.Enabled = timerAlarmButton.Enabled;
                timerPreviewButton.Enabled = timerAlarmButton.Enabled;
                if (!has)
                {
                    opacityTrack.Value = 100; opacityValueLabel.Text = "--"; priorityNumber.Value = 1;
                    visibleBox.Checked = false; lockedBox.Checked = false; rotationNumber.Value = 0;
                    timerModeLabel.Text = "타이머를 선택하면 설정이 표시됩니다."; alarmNameLabel.Text = "-";
                    return;
                }
                opacityTrack.Value = Math.Max(0, Math.Min(100, f.OpacityPercent)); opacityValueLabel.Text = f.OpacityPercent + "%";
                int priority = items.Count - items.IndexOf(f); priorityNumber.Maximum = Math.Max(1, items.Count); priorityNumber.Value = Math.Max(1, priority);
                visibleBox.Checked = f.IsOverlayVisible;
                lockedBox.Checked = f.Locked;
                rotationNumber.Value = transformItem != null ? transformItem.RotationDegrees : 0;
                Rectangle r = f.Bounds;
                SetNumericSafe(posX, r.X); SetNumericSafe(posY, r.Y); SetNumericSafe(sizeW, r.Width); SetNumericSafe(sizeH, r.Height);
                clickThroughBox.Checked = !EditMode;
                if (isTimer)
                {
                    timerModeLabel.Text = f.TimerKind == TimerMode.OneShot ? "현재 모드: 1회성 타이머" :
                        (f.TimerKind == TimerMode.Repeat ? "현재 모드: 반복 타이머" : "현재 모드: 타임스톱");
                    alarmNameLabel.Text = f.TimerKind == TimerMode.Stopwatch ? "알람 없음" : (string.IsNullOrEmpty(f.AlarmPath) ? "기본 알람음" : Path.GetFileName(f.AlarmPath));
                }
                else
                {
                    timerModeLabel.Text = "타이머를 선택하면 설정이 표시됩니다.";
                    alarmNameLabel.Text = "-";
                }
            }
            finally { syncingMainUi = false; }
        }

        private static void SetNumericSafe(DarkNumberBox n, int value)
        {
            decimal v = value;
            if (v < n.Minimum) v = n.Minimum;
            if (v > n.Maximum) v = n.Maximum;
            if (n.Value != v) n.Value = v;
        }

        private void OverlayListItemDrag(object sender, ItemDragEventArgs e)
        {
            ListViewItem row = e.Item as ListViewItem;
            if (row == null || row.Tag as OverlayItemForm == null) return;
            if (!row.Selected)
            {
                foreach (ListViewItem item in overlayList.Items) item.Selected = false;
                row.Selected = true;
            }
            overlayListDragSelection.Clear();
            foreach (ListViewItem item in overlayList.Items)
            {
                OverlayItemForm f = item.Tag as OverlayItemForm;
                if (item.Selected && f != null) overlayListDragSelection.Add(f);
            }
            if (overlayListDragSelection.Count == 0) return;
            try { overlayList.DoDragDrop(row, DragDropEffects.Move); }
            finally { overlayListDragSelection.Clear(); overlayList.InsertionMark.Index = -1; }
        }

        private bool IsOverlayListInternalDrag(DragEventArgs e)
        {
            if (e == null || e.Data == null || !e.Data.GetDataPresent(typeof(ListViewItem))) return false;
            ListViewItem row = e.Data.GetData(typeof(ListViewItem)) as ListViewItem;
            return row != null && object.ReferenceEquals(row.ListView, overlayList);
        }

        private void OverlayListReorderDragEnter(object sender, DragEventArgs e)
        {
            if (IsOverlayListInternalDrag(e)) e.Effect = DragDropEffects.Move;
        }

        private void OverlayListReorderDragOver(object sender, DragEventArgs e)
        {
            if (!IsOverlayListInternalDrag(e)) return;
            e.Effect = DragDropEffects.Move;
            Point point = overlayList.PointToClient(new Point(e.X, e.Y));
            ListViewItem target = overlayList.GetItemAt(Math.Max(1, point.X), Math.Max(1, point.Y));
            if (target == null)
            {
                if (overlayList.Items.Count > 0)
                {
                    overlayList.InsertionMark.Index = overlayList.Items.Count - 1;
                    overlayList.InsertionMark.AppearsAfterItem = true;
                }
            }
            else
            {
                overlayList.InsertionMark.Index = target.Index;
                overlayList.InsertionMark.AppearsAfterItem = point.Y > target.Bounds.Top + target.Bounds.Height / 2;
                if (point.Y < 24 && target.Index > 0) overlayList.EnsureVisible(target.Index - 1);
                else if (point.Y > overlayList.ClientSize.Height - 24 && target.Index < overlayList.Items.Count - 1) overlayList.EnsureVisible(target.Index + 1);
            }
        }

        private void OverlayListReorderDragDrop(object sender, DragEventArgs e)
        {
            if (!IsOverlayListInternalDrag(e)) return;
            e.Effect = DragDropEffects.Move;
            int markIndex = overlayList.InsertionMark.Index;
            bool after = overlayList.InsertionMark.AppearsAfterItem;
            overlayList.InsertionMark.Index = -1;
            if (overlayListDragSelection.Count == 0 || overlayList.Items.Count < 2) return;

            List<OverlayItemForm> currentRows = new List<OverlayItemForm>();
            foreach (ListViewItem row in overlayList.Items)
            {
                OverlayItemForm f = row.Tag as OverlayItemForm;
                if (f != null) currentRows.Add(f);
            }
            if (currentRows.Count != items.Count) return;

            HashSet<OverlayItemForm> movingSet = new HashSet<OverlayItemForm>(overlayListDragSelection);
            List<OverlayItemForm> moving = new List<OverlayItemForm>();
            List<int> movingIndexes = new List<int>();
            for (int i = 0; i < currentRows.Count; i++)
            {
                if (movingSet.Contains(currentRows[i])) { moving.Add(currentRows[i]); movingIndexes.Add(i); }
            }
            if (moving.Count == 0) return;

            int insertionSlot = markIndex < 0 ? currentRows.Count : markIndex + (after ? 1 : 0);
            insertionSlot = Math.Max(0, Math.Min(currentRows.Count, insertionSlot));
            int removedBeforeSlot = 0;
            foreach (int index in movingIndexes) if (index < insertionSlot) removedBeforeSlot++;
            int adjustedSlot = insertionSlot - removedBeforeSlot;

            List<OverlayItemForm> reordered = new List<OverlayItemForm>();
            foreach (OverlayItemForm f in currentRows) if (!movingSet.Contains(f)) reordered.Add(f);
            adjustedSlot = Math.Max(0, Math.Min(reordered.Count, adjustedSlot));
            reordered.InsertRange(adjustedSlot, moving);

            bool changed = reordered.Count == currentRows.Count;
            if (changed)
            {
                changed = false;
                for (int i = 0; i < reordered.Count; i++)
                {
                    if (!object.ReferenceEquals(reordered[i], currentRows[i])) { changed = true; break; }
                }
            }
            if (!changed) return;

            CaptureUndo(moving.Count == 1 ? "오버레이 순서 드래그 변경" : "선택 오버레이 순서 드래그 변경");
            items.Clear();
            for (int i = reordered.Count - 1; i >= 0; i--) items.Add(reordered[i]);
            ApplyZOrder();
            SaveConfig();
            RefreshMainUi();
            foreach (ListViewItem row in overlayList.Items) row.Selected = movingSet.Contains(row.Tag as OverlayItemForm);
            RefreshPropertyEditor();
            RefreshOverlaySelectionVisuals();
            SetStatus(moving.Count == 1 ? "오버레이 순서 변경 완료  |  Ctrl+Z로 복구 가능" : moving.Count.ToString() + "개 오버레이 순서 변경 완료  |  Ctrl+Z로 복구 가능");
        }

        private void OverlayListMouseUp(object sender, MouseEventArgs e)
        {
            ListViewHitTestInfo hit = overlayList.HitTest(e.Location);
            if (hit.Item == null) return;
            hit.Item.Selected = true;
            if (e.Button == MouseButtons.Left && hit.SubItem != null && hit.Item.SubItems.IndexOf(hit.SubItem) == 1)
            {
                OverlayItemForm f = hit.Item.Tag as OverlayItemForm;
                if (f == null) return;
                CaptureUndo("오버레이 표시 변경");
                f.SetOverlayVisible(!f.IsOverlayVisible);
                SaveConfig();
            }
        }

        private void RenameSelectedOverlay()
        {
            OverlayItemForm f = SelectedOverlay;
            if (f == null) return;
            string initial = string.IsNullOrWhiteSpace(f.CustomName) ? DefaultDisplayName(f) : f.CustomName;
            string name = UiPrompt.AskText(this, "오버레이 이름 변경", "새 이름 (빈칸이면 기본 이름 사용)", initial);
            if (name == null) return;
            CaptureUndo("오버레이 이름 변경");
            f.SetCustomName(name, false);
            SaveConfig();
            SetStatus(string.IsNullOrWhiteSpace(f.CustomName) ? "오버레이 이름을 기본값으로 되돌렸습니다." : "오버레이 이름 변경: " + f.CustomName);
        }

        private void DuplicateSelectedOverlays()
        {
            List<OverlayItemForm> selected = SelectedOverlays;
            if (selected.Count == 0) return;
            HashSet<OverlayItemForm> selectedSet = new HashSet<OverlayItemForm>(selected);
            List<OverlayItemForm> ordered = new List<OverlayItemForm>();
            foreach (OverlayItemForm f in items) if (selectedSet.Contains(f) && f.Type != ItemType.ObsProgram) ordered.Add(f);
            int skippedObs = selected.Count - ordered.Count;
            if (ordered.Count == 0) { SetStatus("OBS 화면은 한 개만 사용할 수 있어 복사하지 않습니다."); return; }
            CaptureUndo(ordered.Count == 1 ? "오버레이 복사" : "선택 오버레이 복사");
            Dictionary<int, int> counts = new Dictionary<int, int>();
            foreach (OverlayItemForm source in ordered)
            {
                if (source.GroupId <= 0) continue;
                int count; counts.TryGetValue(source.GroupId, out count); counts[source.GroupId] = count + 1;
            }
            Dictionary<int, int> groupMap = new Dictionary<int, int>();
            foreach (KeyValuePair<int, int> pair in counts) if (pair.Value >= 2) groupMap[pair.Key] = nextGroupId++;
            List<OverlayItemForm> copies = new List<OverlayItemForm>();
            foreach (OverlayItemForm source in ordered)
            {
                Rectangle bounds = source.Bounds; bounds.Offset(22, 22); bounds = NormalizeBounds(bounds);
                int newGroupId = 0; if (source.GroupId > 0) groupMap.TryGetValue(source.GroupId, out newGroupId);
                CreateItem(source.Type, source.Data, source.DurationSeconds, bounds, source.OpacityPercent, source.TimerKind, source.AlarmPath, source.Locked, source.PreserveAspect, source.ScaleMode, source.IsOverlayVisible, DisplayName(source) + " 복사", source.RotationDegrees, source.FlipHorizontal, source.FlipVertical, newGroupId, source.CropLeft, source.CropTop, source.CropRight, source.CropBottom, source.WebZoomPercent, source.WebCustomCss);
                copies.Add(items[items.Count - 1]);
            }
            SaveConfig();
            RefreshMainUi();
            foreach (ListViewItem row in overlayList.Items) row.Selected = copies.Contains(row.Tag as OverlayItemForm);
            RefreshPropertyEditor(); RefreshOverlaySelectionVisuals();
            SetStatus(copies.Count.ToString() + "개 오버레이 복사 완료" + (skippedObs > 0 ? "  |  OBS " + skippedObs.ToString() + "개 제외" : "") + "  |  Ctrl+Z로 복구 가능");
        }

        private void DeleteSelectedOverlays()
        {
            List<OverlayItemForm> selected = SelectedOverlays;
            if (selected.Count == 0) return;
            CaptureUndo(selected.Count == 1 ? "오버레이 삭제" : "선택 오버레이 삭제");
            List<string> oldAssets = new List<string>();
            List<string> oldSounds = new List<string>();
            foreach (OverlayItemForm f in selected)
            {
                if (!items.Contains(f)) continue;
                if (f.Type == ItemType.Image && !string.IsNullOrEmpty(f.Data)) oldAssets.Add(f.Data);
                if (f.Type == ItemType.Timer && !string.IsNullOrEmpty(f.AlarmPath)) oldSounds.Add(f.AlarmPath);
                items.Remove(f);
                try { f.Dispose(); } catch { }
            }
            foreach (string path in oldAssets) TryDeleteUnusedManagedAsset(path);
            foreach (string path in oldSounds) TryDeleteUnusedManagedSound(path);
            ApplyZOrder();
            SaveConfig();
            SetStatus(selected.Count == 1 ? "오버레이 삭제 완료  |  Ctrl+Z로 복구 가능" : selected.Count.ToString() + "개 오버레이 삭제 완료  |  Ctrl+Z로 복구 가능");
        }

        private List<OverlayItemForm> GetMovableSelectedOverlays(int minimumRequired)
        {
            List<OverlayItemForm> movable = new List<OverlayItemForm>();
            foreach (OverlayItemForm f in SelectedOverlays)
            {
                if (f != null && !f.IsDisposed && items.Contains(f) && !f.Locked) movable.Add(f);
            }
            if (movable.Count < minimumRequired)
            {
                SetStatus(minimumRequired <= 1 ? "변경할 오버레이를 선택하세요." : minimumRequired.ToString() + "개 이상의 잠기지 않은 오버레이를 선택하세요.");
                return null;
            }
            return movable;
        }

        private void AlignSelectedOverlays(string mode)
        {
            List<OverlayItemForm> selected = GetMovableSelectedOverlays(2);
            if (selected == null) return;
            int minLeft = int.MaxValue, minTop = int.MaxValue, maxRight = int.MinValue, maxBottom = int.MinValue;
            foreach (OverlayItemForm f in selected)
            {
                Rectangle b = f.Bounds;
                if (b.Left < minLeft) minLeft = b.Left;
                if (b.Top < minTop) minTop = b.Top;
                if (b.Right > maxRight) maxRight = b.Right;
                if (b.Bottom > maxBottom) maxBottom = b.Bottom;
            }
            double centerX = (minLeft + maxRight) / 2.0;
            double centerY = (minTop + maxBottom) / 2.0;
            CaptureUndo("오버레이 정렬");
            foreach (OverlayItemForm f in selected)
            {
                Rectangle b = f.Bounds;
                switch (mode)
                {
                    case "left": b.X = minLeft; break;
                    case "centerx": b.X = (int)Math.Round(centerX - b.Width / 2.0); break;
                    case "right": b.X = maxRight - b.Width; break;
                    case "top": b.Y = minTop; break;
                    case "centery": b.Y = (int)Math.Round(centerY - b.Height / 2.0); break;
                    case "bottom": b.Y = maxBottom - b.Height; break;
                    default: continue;
                }
                f.Bounds = NormalizeBounds(b);
            }
            SaveConfig();
            RefreshMainUi();
            RefreshPropertyEditor();
            RefreshOverlaySelectionVisuals();
            SetStatus("오버레이 정렬 완료  |  Ctrl+Z로 복구 가능");
        }

        private void DistributeSelectedOverlays(bool horizontal)
        {
            List<OverlayItemForm> selected = GetMovableSelectedOverlays(3);
            if (selected == null) return;
            selected.Sort(delegate(OverlayItemForm a, OverlayItemForm b)
            {
                Rectangle ra = a.Bounds, rb = b.Bounds;
                int cmp = horizontal ? ra.Left.CompareTo(rb.Left) : ra.Top.CompareTo(rb.Top);
                if (cmp != 0) return cmp;
                return horizontal ? ra.Top.CompareTo(rb.Top) : ra.Left.CompareTo(rb.Left);
            });
            CaptureUndo(horizontal ? "오버레이 가로 간격 동일" : "오버레이 세로 간격 동일");
            if (horizontal)
            {
                int left = selected[0].Bounds.Left;
                int right = selected[selected.Count - 1].Bounds.Right;
                int total = 0; foreach (OverlayItemForm f in selected) total += f.Bounds.Width;
                double gap = (right - left - total) / (double)Math.Max(1, selected.Count - 1);
                double cursor = left;
                foreach (OverlayItemForm f in selected)
                {
                    Rectangle b = f.Bounds;
                    b.X = (int)Math.Round(cursor);
                    f.Bounds = NormalizeBounds(b);
                    cursor += b.Width + gap;
                }
            }
            else
            {
                int top = selected[0].Bounds.Top;
                int bottom = selected[selected.Count - 1].Bounds.Bottom;
                int total = 0; foreach (OverlayItemForm f in selected) total += f.Bounds.Height;
                double gap = (bottom - top - total) / (double)Math.Max(1, selected.Count - 1);
                double cursor = top;
                foreach (OverlayItemForm f in selected)
                {
                    Rectangle b = f.Bounds;
                    b.Y = (int)Math.Round(cursor);
                    f.Bounds = NormalizeBounds(b);
                    cursor += b.Height + gap;
                }
            }
            SaveConfig();
            RefreshMainUi();
            RefreshPropertyEditor();
            RefreshOverlaySelectionVisuals();
            SetStatus((horizontal ? "가로" : "세로") + " 간격 동일 완료  |  Ctrl+Z로 복구 가능");
        }

        public void MoveOverlayToCurrentMonitorCenter(OverlayItemForm target)
        {
            if (target == null || target.IsDisposed || !items.Contains(target) || target.Locked) { SetStatus("잠긴 오버레이는 이동할 수 없습니다."); return; }
            Screen screen = Screen.FromRectangle(target.Bounds);
            Rectangle area = screen.WorkingArea;
            Rectangle next = target.Bounds;
            next.X = area.Left + Math.Max(0, (area.Width - next.Width) / 2);
            next.Y = area.Top + Math.Max(0, (area.Height - next.Height) / 2);
            CaptureUndo("오버레이 중앙 이동");
            target.Bounds = NormalizeBounds(next);
            SaveConfig();
            RefreshMainUi();
            RefreshPropertyEditor();
            RefreshOverlaySelectionVisuals();
            SetStatus("현재 모니터 중앙으로 이동 완료");
        }

        public void ResizeImageOverlayToScale(OverlayItemForm target, double scale)
        {
            if (target == null || target.IsDisposed || !items.Contains(target) || target.Type != ItemType.Image) return;
            if (target.Locked) { SetStatus("잠긴 오버레이는 크기를 변경할 수 없습니다."); return; }
            Size src = target.NativeImageSize;
            if (src.Width <= 0 || src.Height <= 0) { SetStatus("원본 이미지 크기를 읽지 못했습니다."); return; }
            Rectangle b = target.Bounds;
            b.Width = Math.Max(100, (int)Math.Round(src.Width * scale));
            b.Height = Math.Max(60, (int)Math.Round(src.Height * scale));
            CaptureUndo(scale == 1.0 ? "원본 크기 복원" : "이미지 크기 비율 복원");
            target.Bounds = NormalizeBounds(b);
            SaveConfig();
            RefreshMainUi();
            RefreshPropertyEditor();
            RefreshOverlaySelectionVisuals();
            SetStatus(scale == 1.0 ? "원본 크기로 복원 완료" : ((int)Math.Round(scale * 100)).ToString() + "% 크기로 복원 완료");
        }

        public void ReplaceImageOverlay(OverlayItemForm target)
        {
            if (target == null || target.IsDisposed || !items.Contains(target) || target.Type != ItemType.Image) return;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string managed = ImportImageAsset(d.FileName);
                if (string.IsNullOrEmpty(managed)) return;
                int oldIndex = items.IndexOf(target);
                if (oldIndex < 0) return;
                string oldData = target.Data;
                string newName = string.IsNullOrWhiteSpace(target.CustomName) ? SuggestedImageNameFromPath(d.FileName) : target.CustomName;
                CaptureUndo("이미지 교체");
                CreateItem(ItemType.Image, managed, 0, target.Bounds, target.OpacityPercent, target.TimerKind, target.AlarmPath, target.Locked, target.PreserveAspect, target.ScaleMode, target.IsOverlayVisible, newName, target.RotationDegrees, target.FlipHorizontal, target.FlipVertical, target.GroupId, target.CropLeft, target.CropTop, target.CropRight, target.CropBottom);
                OverlayItemForm created = items[items.Count - 1];
                items.RemoveAt(items.Count - 1);
                items.Insert(oldIndex, created);
                items.Remove(target);
                try { target.Dispose(); } catch { }
                TryDeleteUnusedManagedAsset(oldData);
                ApplyZOrder();
                SaveConfig();
                RefreshMainUi();
                SelectOverlayForEditing(created);
                SetStatus("이미지 교체 완료  |  위치와 설정 유지");
            }
        }

        private void ApplyOpacityFromUi(bool save)
        {
            if (syncingMainUi) return;
            OverlayItemForm f = SelectedOverlay; if (f == null) return;
            f.SetOpacityPercent(opacityTrack.Value, false);
            opacityValueLabel.Text = opacityTrack.Value + "%";
            if (save) SaveConfig();
        }

        private void ApplyPriorityFromUi()
        {
            if (syncingMainUi) return;
            OverlayItemForm f = SelectedOverlay; if (f == null) return;
            int wanted = (int)priorityNumber.Value;
            int current = items.Count - items.IndexOf(f);
            if (wanted == current) return;
            CaptureUndo("오버레이 우선도 변경");
            SetItemPriorityNoUndo(f, wanted);
            SaveConfig();
        }

        private void SetItemPriorityNoUndo(OverlayItemForm f, int priority)
        {
            int old = items.IndexOf(f); if (old < 0) return;
            priority = Math.Max(1, Math.Min(items.Count, priority));
            int target = items.Count - priority;
            items.RemoveAt(old);
            if (target > items.Count) target = items.Count;
            items.Insert(target, f);
            ApplyZOrder();
        }

        private void MoveSelectedPriority(int delta)
        {
            OverlayItemForm f = SelectedOverlay; if (f == null) return;
            int current = items.Count - items.IndexOf(f);
            int wanted = Math.Max(1, Math.Min(items.Count, current + delta));
            if (wanted == current) return;
            CaptureUndo("오버레이 우선도 변경");
            SetItemPriorityNoUndo(f, wanted);
            SaveConfig();
        }

        private void SetClickThroughFromUi(bool clickThrough)
        {
            SetEditorMode(clickThrough ? EditorMode.Fixed : EditorMode.Normal, true);
        }

        private void ApplyBoundsFromUi()
        {
            if (syncingMainUi) return;
            OverlayItemForm f = SelectedOverlay; if (f == null || f.Locked) { SetStatus("잠긴 오버레이는 위치/크기를 변경할 수 없습니다."); return; }
            CaptureUndo("오버레이 위치/크기 변경");
            Rectangle r = new Rectangle((int)posX.Value, (int)posY.Value, (int)sizeW.Value, (int)sizeH.Value);
            f.Bounds = NormalizeBounds(r);
            SaveConfig();
        }

        private void ApplyVisibleFromUi()
        {
            if (syncingMainUi) return;
            List<OverlayItemForm> targets = SelectedOverlays; if (targets.Count == 0) return;
            CaptureUndo("오버레이 표시 변경");
            foreach (OverlayItemForm f in targets) f.SetOverlayVisible(visibleBox.Checked);
            SaveConfig();
        }

        private void ApplyLockedFromUi()
        {
            if (syncingMainUi) return;
            List<OverlayItemForm> targets = SelectedOverlays; if (targets.Count == 0) return;
            CaptureUndo("위치/크기 잠금 변경");
            foreach (OverlayItemForm f in targets) f.SetLocked(lockedBox.Checked, false);
            SaveConfig();
        }

        private void ApplyRotationFromUi()
        {
            if (syncingMainUi) return;
            List<OverlayItemForm> targets = SelectedOverlays;
            if (targets.Count == 0) return;
            bool any = false;
            foreach (OverlayItemForm f in targets) if (f.SupportsTransform && f.RotationDegrees != (int)rotationNumber.Value) { any = true; break; }
            if (!any) return;
            CaptureUndo("오버레이 회전 변경");
            foreach (OverlayItemForm f in targets)
                if (f.SupportsTransform) f.SetTransform((int)rotationNumber.Value, f.FlipHorizontal, f.FlipVertical, false);
            SaveConfig();
        }

        private void RotateSelectionBy(int delta)
        {
            List<OverlayItemForm> targets = SelectedOverlays;
            if (targets.Count == 0) return;
            bool any = false; foreach (OverlayItemForm f in targets) if (f.SupportsTransform) { any = true; break; }
            if (!any) { SetStatus("OBS 화면은 DWM 방식이라 회전/반전을 지원하지 않습니다."); return; }
            CaptureUndo("오버레이 회전 변경");
            foreach (OverlayItemForm f in targets)
                if (f.SupportsTransform) f.SetTransform(f.RotationDegrees + delta, f.FlipHorizontal, f.FlipVertical, false);
            SaveConfig();
        }

        private void FlipSelection(bool horizontal)
        {
            List<OverlayItemForm> targets = SelectedOverlays;
            if (targets.Count == 0) return;
            bool any = false; foreach (OverlayItemForm f in targets) if (f.SupportsTransform) { any = true; break; }
            if (!any) { SetStatus("OBS 화면은 DWM 방식이라 회전/반전을 지원하지 않습니다."); return; }
            CaptureUndo(horizontal ? "좌우 반전" : "상하 반전");
            foreach (OverlayItemForm f in targets)
            {
                if (!f.SupportsTransform) continue;
                f.SetTransform(f.RotationDegrees, horizontal ? !f.FlipHorizontal : f.FlipHorizontal,
                    horizontal ? f.FlipVertical : !f.FlipVertical, false);
            }
            SaveConfig();
        }

        public void FlipOverlay(OverlayItemForm target, bool horizontal)
        {
            if (target == null || target.IsDisposed || !items.Contains(target) || !target.SupportsTransform) return;
            CaptureUndo(horizontal ? "좌우 반전" : "상하 반전");
            target.SetTransform(target.RotationDegrees,
                horizontal ? !target.FlipHorizontal : target.FlipHorizontal,
                horizontal ? target.FlipVertical : !target.FlipVertical, false);
            SaveConfig(); RefreshPropertyEditor();
            SetStatus(horizontal ? "좌우 반전 완료" : "상하 반전 완료");
        }

        public void ResetOverlayRotation(OverlayItemForm target)
        {
            ResetOverlayTransform(target, false);
        }

        public void ResetOverlayTransform(OverlayItemForm target, bool resetFlips)
        {
            if (target == null || target.IsDisposed || !items.Contains(target) || !target.SupportsTransform) return;
            bool anyChange = target.RotationDegrees != 0 || (resetFlips && (target.FlipHorizontal || target.FlipVertical));
            if (!anyChange)
            {
                SetStatus(resetFlips ? "회전/반전이 이미 초기 상태입니다." : "회전 각도가 이미 0°입니다.");
                return;
            }
            CaptureUndo(resetFlips ? "회전/반전 초기화" : "회전 각도 초기화");
            target.SetTransform(0, resetFlips ? false : target.FlipHorizontal, resetFlips ? false : target.FlipVertical, false);
            SaveConfig(); RefreshPropertyEditor();
            SetStatus(resetFlips ? "회전/반전 전체 초기화 완료" : "회전 각도 0° 초기화 완료  |  반전 상태 유지");
        }

        private void ResetSelectionTransform(bool resetFlips)
        {
            List<OverlayItemForm> targets = SelectedOverlays;
            if (targets.Count == 0) return;
            bool anySupported = false, anyChange = false;
            foreach (OverlayItemForm f in targets)
            {
                if (!f.SupportsTransform) continue;
                anySupported = true;
                if (f.RotationDegrees != 0 || (resetFlips && (f.FlipHorizontal || f.FlipVertical))) anyChange = true;
            }
            if (!anySupported) { SetStatus("OBS 화면은 DWM 방식이라 회전/반전 초기화를 지원하지 않습니다."); return; }
            if (!anyChange) { SetStatus(resetFlips ? "회전/반전이 이미 초기 상태입니다." : "회전 각도가 이미 0°입니다."); return; }
            CaptureUndo(resetFlips ? "회전/반전 초기화" : "회전 각도 초기화");
            foreach (OverlayItemForm f in targets) if (f.SupportsTransform) f.SetTransform(0, resetFlips ? false : f.FlipHorizontal, resetFlips ? false : f.FlipVertical, false);
            SaveConfig(); RefreshPropertyEditor();
            SetStatus(resetFlips ? "회전/반전 전체 초기화 완료" : "회전 각도 0° 초기화 완료  |  반전 상태 유지");
        }

        private void MoveDetailSelection(int dx, int dy)
        {
            List<OverlayItemForm> targets = SelectedOverlays;
            if (targets.Count == 0) return;
            CaptureUndo("세부 위치 이동");
            foreach (OverlayItemForm f in targets)
            {
                if (f.Locked) continue;
                Rectangle r = f.Bounds; r.X += dx; r.Y += dy; f.Bounds = NormalizeBounds(r);
            }
            ReapplyZOrder();
            SaveConfig();
        }

        public int SnapMouseRotation(int proposedDegrees)
        {
            int sensitivity = Math.Max(0, Math.Min(15, rotationSnapDegrees));
            if (sensitivity <= 0) return proposedDegrees;
            int nearest = (int)Math.Round(proposedDegrees / 90.0) * 90;
            return Math.Abs(proposedDegrees - nearest) <= sensitivity ? nearest : proposedDegrees;
        }

        public void ScaleOverlayByMouseWheel(OverlayItemForm source, int wheelDelta, bool groupMode)
        {
            if (source == null || wheelDelta == 0 || !EditMode) return;
            List<OverlayItemForm> targets = groupMode ? GetGroupMembers(source) : new List<OverlayItemForm>(new OverlayItemForm[] { source });
            if (targets.Count == 0) return;
            foreach (OverlayItemForm target in targets)
            {
                if (target.Locked)
                {
                    SetStatus(groupMode ? "그룹에 잠긴 항목이 있어 확대/축소할 수 없습니다." : "잠긴 오버레이는 확대/축소할 수 없습니다.");
                    return;
                }
            }

            int notches = Math.Max(1, Math.Abs(wheelDelta) / 120);
            double oneStep = Math.Max(1, Math.Min(90, zoomStepPercent)) / 100.0;
            double factor = 1.0;
            for (int i = 0; i < notches; i++) factor *= wheelDelta > 0 ? (1.0 + oneStep) : (1.0 - oneStep);

            if (factor < 1.0)
            {
                double minimumFactor = 0.0;
                foreach (OverlayItemForm target in targets)
                {
                    Rectangle r = target.Bounds;
                    minimumFactor = Math.Max(minimumFactor, Math.Max(100.0 / Math.Max(1, r.Width), 60.0 / Math.Max(1, r.Height)));
                }
                if (factor < minimumFactor) factor = minimumFactor;
            }
            else
            {
                double maximumFactor = double.MaxValue;
                foreach (OverlayItemForm target in targets)
                {
                    Rectangle r = target.Bounds;
                    maximumFactor = Math.Min(maximumFactor, Math.Min(10000.0 / Math.Max(1, r.Width), 10000.0 / Math.Max(1, r.Height)));
                }
                if (factor > maximumFactor) factor = maximumFactor;
            }
            if (Math.Abs(factor - 1.0) < 0.0001)
            {
                SetStatus("오버레이 크기 한계에 도달했습니다.");
                return;
            }

            double centerX, centerY;
            if (groupMode && targets.Count > 1)
            {
                int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
                foreach (OverlayItemForm target in targets)
                {
                    Rectangle r = target.Bounds;
                    left = Math.Min(left, r.Left); top = Math.Min(top, r.Top); right = Math.Max(right, r.Right); bottom = Math.Max(bottom, r.Bottom);
                }
                centerX = (left + right) / 2.0; centerY = (top + bottom) / 2.0;
            }
            else
            {
                Rectangle r = source.Bounds;
                centerX = r.Left + r.Width / 2.0; centerY = r.Top + r.Height / 2.0;
            }

            CaptureUndo(groupMode && targets.Count > 1 ? "그룹 확대/축소" : "오버레이 확대/축소");
            foreach (OverlayItemForm target in targets)
            {
                Rectangle old = target.Bounds;
                double oldCenterX = old.Left + old.Width / 2.0;
                double oldCenterY = old.Top + old.Height / 2.0;
                int newWidth = Math.Max(100, Math.Min(10000, (int)Math.Round(old.Width * factor)));
                int newHeight = Math.Max(60, Math.Min(10000, (int)Math.Round(old.Height * factor)));
                double newCenterX = centerX + (oldCenterX - centerX) * factor;
                double newCenterY = centerY + (oldCenterY - centerY) * factor;
                Rectangle next = new Rectangle((int)Math.Round(newCenterX - newWidth / 2.0), (int)Math.Round(newCenterY - newHeight / 2.0), newWidth, newHeight);
                target.Bounds = NormalizeBounds(next);
            }
            ReapplyZOrder();
            SaveConfig();
            RefreshPropertyEditor();
            SetStatus((groupMode && targets.Count > 1 ? "그룹" : "개별") + " 확대/축소 " + (wheelDelta > 0 ? "+" : "-") + zoomStepPercent.ToString() + "%");
        }

        internal void GroupSelectedOverlays()
        {
            List<OverlayItemForm> selected = SelectedOverlays;
            if (selected.Count < 2) { SetStatus("그룹으로 묶을 항목을 Shift+클릭으로 2개 이상 선택하세요."); return; }
            CaptureUndo("그룹 만들기");
            int groupId = nextGroupId++;
            foreach (OverlayItemForm f in selected) f.SetGroupId(groupId, false);
            SaveConfig();
            RefreshPropertyEditor();
            SetStatus(selected.Count.ToString() + "개 항목을 그룹 " + groupId.ToString() + "로 묶었습니다.");
        }

        private void GroupOrUngroupSelectedOverlays()
        {
            List<OverlayItemForm> selected = SelectedOverlays;

            // Toggle behavior for the group hotkey:
            // - One grouped item selected: ungroup its whole group.
            // - Two or more selected items already belonging to the same group: ungroup that group.
            // - Otherwise two or more selected items: create a new group.
            // Buttons/menu keep their original group-only behavior.
            int commonGroupId = 0;
            bool sameExistingGroup = selected.Count > 0;
            foreach (OverlayItemForm f in selected)
            {
                if (f.GroupId <= 0)
                {
                    sameExistingGroup = false;
                    break;
                }
                if (commonGroupId == 0) commonGroupId = f.GroupId;
                else if (f.GroupId != commonGroupId)
                {
                    sameExistingGroup = false;
                    break;
                }
            }

            if (sameExistingGroup && commonGroupId > 0)
            {
                CaptureUndo("그룹 해제");
                foreach (OverlayItemForm f in items)
                    if (f.GroupId == commonGroupId) f.SetGroupId(0, false);
                SaveConfig();
                RefreshPropertyEditor();
                SetStatus("그룹 " + commonGroupId.ToString() + "을 해제했습니다.");
                return;
            }

            if (selected.Count >= 2)
            {
                GroupSelectedOverlays();
                return;
            }

            SetStatus("그룹화/그룹해제: 2개 이상 선택하면 그룹화하고, 같은 그룹을 선택한 상태에서 다시 누르면 그룹을 해제합니다.");
        }

        internal void UngroupSelectedOverlays()
        {
            List<OverlayItemForm> selected = SelectedOverlays;
            HashSet<int> groups = new HashSet<int>();
            foreach (OverlayItemForm f in selected) if (f.GroupId > 0) groups.Add(f.GroupId);
            if (groups.Count == 0) { SetStatus("선택한 항목에 그룹이 없습니다."); return; }
            CaptureUndo("그룹 해제");
            foreach (OverlayItemForm f in items) if (groups.Contains(f.GroupId)) f.SetGroupId(0, false);
            SaveConfig();
            RefreshPropertyEditor();
            SetStatus("선택한 그룹을 해제했습니다.");
        }

        private static bool IsReservedClipboardHotkey(int mods, int vk)
        {
            return mods == Native.MOD_CONTROL && (vk == (int)Keys.V || vk == (int)Keys.C);
        }

        private static bool MatchesEditHotkey(Keys keyData, int mods, int vk)
        {
            if ((int)(keyData & Keys.KeyCode) != vk) return false;
            return HotkeyModifiersFromKeyData(keyData) == mods;
        }

        public bool HandleDetailShortcut(OverlayItemForm source, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.C))
            {
                if (source != null && !IsOverlaySelected(source)) SelectOverlayForEditing(source);
                DuplicateSelectedOverlays();
                return true;
            }
            if (!EditMode) return false;
            if (source != null && keyData == Keys.Delete)
            {
                if (!IsOverlaySelected(source)) SelectOverlayForEditing(source);
                DeleteSelectedOverlays();
                return true;
            }

            // Group actions are available in both Normal and Detail edit modes.
            // This makes Shift+click multi-select -> group shortcut work without changing modes.
            if (MatchesEditHotkey(keyData, hotkeyGroupMods, hotkeyGroupVk))
            {
                GroupOrUngroupSelectedOverlays();
                return true;
            }
            if (MatchesEditHotkey(keyData, hotkeyUngroupMods, hotkeyUngroupVk))
            {
                UngroupSelectedOverlays();
                return true;
            }

            if (source != null && SelectedOverlays.Count == 0) SelectOverlayForEditing(source);

            if (MatchesEditHotkey(keyData, hotkeyRotateMinus1Mods, hotkeyRotateMinus1Vk)) { RotateSelectionBy(-1); return true; }
            if (MatchesEditHotkey(keyData, hotkeyRotatePlus1Mods, hotkeyRotatePlus1Vk)) { RotateSelectionBy(1); return true; }
            if (MatchesEditHotkey(keyData, hotkeyRotateMinus10Mods, hotkeyRotateMinus10Vk)) { RotateSelectionBy(-10); return true; }
            if (MatchesEditHotkey(keyData, hotkeyRotatePlus10Mods, hotkeyRotatePlus10Vk)) { RotateSelectionBy(10); return true; }
            if (MatchesEditHotkey(keyData, hotkeyFlipHorizontalMods, hotkeyFlipHorizontalVk)) { FlipSelection(true); return true; }
            if (MatchesEditHotkey(keyData, hotkeyFlipVerticalMods, hotkeyFlipVerticalVk)) { FlipSelection(false); return true; }
            if (MatchesEditHotkey(keyData, hotkeyResetRotationMods, hotkeyResetRotationVk)) { ResetSelectionTransform(false); return true; }
            if (MatchesEditHotkey(keyData, hotkeyResetTransformMods, hotkeyResetTransformVk)) { ResetSelectionTransform(true); return true; }

            bool shift = (keyData & Keys.Shift) == Keys.Shift;
            Keys key = keyData & Keys.KeyCode;
            int move = shift ? 10 : 1;
            if (key == Keys.Left) { MoveDetailSelection(-move, 0); return true; }
            if (key == Keys.Right) { MoveDetailSelection(move, 0); return true; }
            if (key == Keys.Up) { MoveDetailSelection(0, -move); return true; }
            if (key == Keys.Down) { MoveDetailSelection(0, move); return true; }
            return false;
        }

        private void AddTextInteractive()
        {
            string text = UiPrompt.AskText(this, "텍스트 추가", "화면에 표시할 텍스트", "텍스트");
            if (text == null) return;
            AddText(text);
        }

        private void AddTimerInteractive(TimerMode mode)
        {
            if (mode == TimerMode.Stopwatch) { AddTimer("", mode); return; }
            string time = UiPrompt.AskText(this, mode == TimerMode.OneShot ? "1회성 타이머" : "반복 타이머", "시간 (예: 90, 01:30, 01:02:03)", "05:00");
            if (time == null) return;
            AddTimer(time, mode);
        }

        private void DeletePresetInteractive()
        {
            List<PresetListEntry> presets = GetPresetEntries();
            if (presets.Count == 0) { SetStatus("삭제할 프리셋이 없습니다."); return; }
            using (Form f = new Form())
            using (ListBox list = new ListBox())
            using (Button delete = new Button())
            using (Button cancel = new Button())
            using (Label nameLabel = new Label())
            {
                f.Text = "프리셋 삭제"; f.StartPosition = FormStartPosition.CenterParent; f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.ClientSize = new Size(430, 400); f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false;
                nameLabel.Text = "프리셋 삭제"; nameLabel.SetBounds(15, 14, 390, 24); nameLabel.Font = new Font(f.Font, FontStyle.Bold); f.Controls.Add(nameLabel);
                list.SetBounds(15, 42, 400, 300); foreach (PresetListEntry entry in presets) list.Items.Add(entry); if (list.Items.Count > 0) list.SelectedIndex = 0; f.Controls.Add(list);
                delete.Text = "선택 프리셋 삭제"; delete.SetBounds(230, 356, 112, 30); f.Controls.Add(delete);
                cancel.Text = "취소"; cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(350, 356, 65, 30); f.Controls.Add(cancel); f.CancelButton = cancel;
                delete.Click += delegate
                {
                    PresetListEntry entry = list.SelectedItem as PresetListEntry; if (entry == null) return;
                    if (MessageBox.Show(f, "'" + entry.Name + "' 프리셋을 삭제할까요?", "프리셋 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    try
                    {
                        File.Delete(entry.Path);
                        RemovePresetHotkeyForPath(entry.Path);
                        if (string.Equals(currentPresetName, entry.Name, StringComparison.CurrentCultureIgnoreCase)) currentPresetName = "";
                        ApplyHotkeys();
                        SaveConfig();
                        SetStatus("프리셋 삭제 완료: " + entry.Name);
                        f.DialogResult = DialogResult.OK; f.Close();
                    }
                    catch (Exception ex) { CrashLog.Write(ex, "DeletePreset"); SetStatus("프리셋 삭제 실패: " + ex.Message); }
                };
                f.ShowDialog(this);
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private void TryEnableDarkTitleBar()
        {
            try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
        }

        private Button MakeButton(string text, int x, int y, int w)
        {
            Button b = new Button(); b.Text = text; b.SetBounds(x, y, w, 32); Controls.Add(b); return b;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                int hotkeyId = m.WParam.ToInt32();
                if (WebControlMode)
                {
                    if (hotkeyId == 2) { ToggleHidden(); return; }
                    if (hotkeyId == 3) { ToggleWebControlMode(); return; }
                    return;
                }
                if (hotkeyId == 1) { ToggleEdit(); return; }
                if (hotkeyId == 2) { ToggleHidden(); return; }
                if (hotkeyId == 3) { ToggleWebControlMode(); return; }
                if (hotkeyId == 4) { AddScreenRegionCapture(); return; }
                PresetHotkeyBinding presetBinding;
                if (registeredPresetHotkeys.TryGetValue(hotkeyId, out presetBinding))
                {
                    LoadPresetByHotkey(presetBinding);
                    return;
                }
            }
            base.WndProc(ref m);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (WebControlMode) return base.ProcessCmdKey(ref msg, keyData);
            if (keyData == (Keys.Control | Keys.Z))
            {
                UndoLastAction();
                return true;
            }
            if (HandlePasteShortcut(keyData)) return true;
            if (keyData == Keys.Delete && overlayList.Focused)
            {
                DeleteSelectedOverlays();
                return true;
            }
            if (HandleDetailShortcut(null, keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void CaptureMainLayout()
        {
            mainBaseBounds.Clear();
            mainBaseFonts.Clear();
            CaptureControlTree(this);
            mainLayoutCaptured = true;
        }

        private void CaptureControlTree(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                mainBaseBounds[control] = control.Bounds;
                mainBaseFonts[control] = control.Font.Size;
                if (control.HasChildren) CaptureControlTree(control);
            }
        }

        private void ScaleMainLayout()
        {
            if (!mainLayoutCaptured || applyingMainLayout || WindowState == FormWindowState.Minimized) return;
            applyingMainLayout = true;
            try
            {
                float sx = ClientSize.Width / (float)mainBaseClientSize.Width;
                float sy = ClientSize.Height / (float)mainBaseClientSize.Height;
                float sf = Math.Max(0.78f, Math.Min(1.65f, Math.Min(sx, sy)));
                foreach (KeyValuePair<Control, Rectangle> pair in mainBaseBounds)
                {
                    Control control = pair.Key;
                    if (control == null || control.IsDisposed) continue;
                    Rectangle b = pair.Value;
                    control.SetBounds(
                        (int)Math.Round(b.X * sx),
                        (int)Math.Round(b.Y * sy),
                        Math.Max(1, (int)Math.Round(b.Width * sx)),
                        Math.Max(1, (int)Math.Round(b.Height * sy)));
                    float baseSize;
                    if (mainBaseFonts.TryGetValue(control, out baseSize) && !(control is TrackBar))
                    {
                        float wanted = Math.Max(7F, baseSize * sf);
                        if (Math.Abs(control.Font.Size - wanted) > 0.25F)
                            control.Font = new Font(control.Font.FontFamily, wanted, control.Font.Style);
                    }
                }
                ResizeOverlayColumns();
            }
            finally { applyingMainLayout = false; }
        }

        public void CaptureUndo(string reason)
        {
            try
            {
                Directory.CreateDirectory(undoDir);
                string path = Path.Combine(undoDir, "undo_" + Guid.NewGuid().ToString("N") + ".lopreset");
                SavePresetFile(path, "__UNDO__");
                UndoState state = new UndoState();
                state.Path = path;
                state.Reason = string.IsNullOrWhiteSpace(reason) ? "최근 작업" : reason;
                state.EditMode = EditMode;
                state.DetailEditMode = DetailEditMode;
                state.Hidden = hidden;
                state.HotkeyEditVk = hotkeyEditVk;
                state.HotkeyHideVk = hotkeyHideVk;
                state.HotkeyDetailVk = hotkeyDetailVk;
                state.HotkeyCaptureVk = hotkeyCaptureVk;
                state.HotkeyEditMods = hotkeyEditMods;
                state.HotkeyHideMods = hotkeyHideMods;
                state.HotkeyDetailMods = hotkeyDetailMods;
                state.HotkeyCaptureMods = hotkeyCaptureMods;
                state.HotkeyGroupVk = hotkeyGroupVk; state.HotkeyGroupMods = hotkeyGroupMods;
                state.HotkeyUngroupVk = hotkeyUngroupVk; state.HotkeyUngroupMods = hotkeyUngroupMods;
                state.HotkeyRotateMinus1Vk = hotkeyRotateMinus1Vk; state.HotkeyRotateMinus1Mods = hotkeyRotateMinus1Mods;
                state.HotkeyRotatePlus1Vk = hotkeyRotatePlus1Vk; state.HotkeyRotatePlus1Mods = hotkeyRotatePlus1Mods;
                state.HotkeyRotateMinus10Vk = hotkeyRotateMinus10Vk; state.HotkeyRotateMinus10Mods = hotkeyRotateMinus10Mods;
                state.HotkeyRotatePlus10Vk = hotkeyRotatePlus10Vk; state.HotkeyRotatePlus10Mods = hotkeyRotatePlus10Mods;
                state.HotkeyFlipHorizontalVk = hotkeyFlipHorizontalVk; state.HotkeyFlipHorizontalMods = hotkeyFlipHorizontalMods;
                state.HotkeyFlipVerticalVk = hotkeyFlipVerticalVk; state.HotkeyFlipVerticalMods = hotkeyFlipVerticalMods;
                state.HotkeyResetRotationVk = hotkeyResetRotationVk; state.HotkeyResetRotationMods = hotkeyResetRotationMods;
                state.HotkeyResetTransformVk = hotkeyResetTransformVk; state.HotkeyResetTransformMods = hotkeyResetTransformMods;
                state.ZoomStepPercent = zoomStepPercent;
                state.RotationSnapDegrees = rotationSnapDegrees;
                state.PlacementSnapPixels = placementSnapPixels;
                state.PresetHotkeys = new List<PresetHotkeyBinding>();
                foreach (PresetHotkeyBinding binding in presetHotkeys)
                {
                    PresetHotkeyBinding copy = new PresetHotkeyBinding(); copy.FileName = binding.FileName; copy.Mods = binding.Mods; copy.Vk = binding.Vk;
                    state.PresetHotkeys.Add(copy);
                }
                state.CurrentPresetName = currentPresetName;
                state.MainClientSize = ClientSize;
                undoStates.Add(state);
                while (undoStates.Count > MaxUndoStates)
                {
                    UndoState oldest = undoStates[0];
                    undoStates.RemoveAt(0);
                    try { if (File.Exists(oldest.Path)) File.Delete(oldest.Path); } catch { }
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "CaptureUndo");
            }
        }

        private void UndoLastAction()
        {
            if (undoStates.Count == 0)
            {
                SetStatus("실행 취소할 작업이 없습니다.");
                return;
            }

            UndoState state = undoStates[undoStates.Count - 1];
            undoStates.RemoveAt(undoStates.Count - 1);
            try
            {
                EditMode = state.EditMode;
                DetailEditMode = state.EditMode;
                hotkeyEditVk = state.HotkeyEditVk;
                hotkeyHideVk = state.HotkeyHideVk;
                hotkeyDetailVk = state.HotkeyDetailVk;
                hotkeyCaptureVk = state.HotkeyCaptureVk <= 0 ? Native.VK_F1 : state.HotkeyCaptureVk;
                hotkeyEditMods = state.HotkeyEditMods;
                hotkeyHideMods = state.HotkeyHideMods;
                hotkeyDetailMods = state.HotkeyDetailMods;
                hotkeyCaptureMods = state.HotkeyCaptureMods;
                hotkeyGroupVk = state.HotkeyGroupVk; hotkeyGroupMods = state.HotkeyGroupMods;
                hotkeyUngroupVk = state.HotkeyUngroupVk; hotkeyUngroupMods = state.HotkeyUngroupMods;
                hotkeyRotateMinus1Vk = state.HotkeyRotateMinus1Vk; hotkeyRotateMinus1Mods = state.HotkeyRotateMinus1Mods;
                hotkeyRotatePlus1Vk = state.HotkeyRotatePlus1Vk; hotkeyRotatePlus1Mods = state.HotkeyRotatePlus1Mods;
                hotkeyRotateMinus10Vk = state.HotkeyRotateMinus10Vk; hotkeyRotateMinus10Mods = state.HotkeyRotateMinus10Mods;
                hotkeyRotatePlus10Vk = state.HotkeyRotatePlus10Vk; hotkeyRotatePlus10Mods = state.HotkeyRotatePlus10Mods;
                hotkeyFlipHorizontalVk = state.HotkeyFlipHorizontalVk; hotkeyFlipHorizontalMods = state.HotkeyFlipHorizontalMods;
                hotkeyFlipVerticalVk = state.HotkeyFlipVerticalVk; hotkeyFlipVerticalMods = state.HotkeyFlipVerticalMods;
                hotkeyResetRotationVk = state.HotkeyResetRotationVk <= 0 ? (int)Keys.R : state.HotkeyResetRotationVk; hotkeyResetRotationMods = state.HotkeyResetRotationMods;
                hotkeyResetTransformVk = state.HotkeyResetTransformVk <= 0 ? (int)Keys.R : state.HotkeyResetTransformVk; hotkeyResetTransformMods = state.HotkeyResetTransformVk <= 0 ? Native.MOD_SHIFT : state.HotkeyResetTransformMods;
                zoomStepPercent = state.ZoomStepPercent <= 0 ? 10 : Math.Max(1, Math.Min(90, state.ZoomStepPercent));
                rotationSnapDegrees = state.RotationSnapDegrees == 0 ? 0 : Math.Max(0, Math.Min(15, state.RotationSnapDegrees));
                placementSnapPixels = Math.Max(0, Math.Min(30, state.PlacementSnapPixels));
                presetHotkeys.Clear();
                if (state.PresetHotkeys != null)
                {
                    foreach (PresetHotkeyBinding binding in state.PresetHotkeys)
                    {
                        PresetHotkeyBinding copy = new PresetHotkeyBinding(); copy.FileName = binding.FileName; copy.Mods = binding.Mods; copy.Vk = binding.Vk;
                        presetHotkeys.Add(copy);
                    }
                }
                currentPresetName = state.CurrentPresetName ?? "";
                string ignoredName = LoadPresetFile(state.Path);
                hidden = state.Hidden;
                foreach (OverlayItemForm f in items)
                {
                    f.SetEditMode(EditMode);
                    f.RefreshEffectiveVisibility();
                }
                if (state.MainClientSize.Width >= 200 && state.MainClientSize.Height >= 300)
                    ClientSize = state.MainClientSize;
                ApplyHotkeys();
                UpdateButtons();
                ApplyZOrder();
                SaveConfig();
                SetStatus("실행 취소 완료: " + state.Reason + "  |  Ctrl+Z 또는 실행 취소 버튼");
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "UndoLastAction");
                SetStatus("실행 취소 실패: " + ex.Message);
            }
            finally
            {
                try { if (!string.IsNullOrEmpty(state.Path) && File.Exists(state.Path)) File.Delete(state.Path); } catch { }
            }
        }

        private void ClearAllInteractive()
        {
            if (items.Count == 0)
            {
                SetStatus("삭제할 오버레이가 없습니다.");
                return;
            }
            DialogResult result = MessageBox.Show(this,
                "현재 화면의 오버레이를 전부 삭제할까요?\n\n프리셋 파일은 삭제되지 않으며, 실행 취소로 복구할 수 있습니다.",
                "전체 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result != DialogResult.Yes) return;
            CaptureUndo("전체 삭제");
            ClearAllItems(true);
            hidden = false;
            UpdateButtons();
            SaveConfig();
            SetStatus("전체 오버레이 삭제 완료  |  Ctrl+Z로 복구 가능");
        }

        private void ResetSettingsInteractive()
        {
            DialogResult result = MessageBox.Show(this,
                "프로그램 설정을 초기값으로 돌릴까요?\n\n초기화: 모든 사용자 지정 단축키, 프리셋 단축키, 확대/축소 비율, 편집/숨김 상태, 본창 크기\n유지: 현재 오버레이, 프리셋, 이미지/알람 파일",
                "설정 초기화", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result != DialogResult.Yes) return;

            CaptureUndo("설정 초기화");
            hotkeyEditVk = Native.VK_F8;
            hotkeyHideVk = Native.VK_F9;
            hotkeyDetailVk = Native.VK_F10;
            hotkeyCaptureVk = Native.VK_F1;
            hotkeyEditMods = 0;
            hotkeyHideMods = 0;
            hotkeyDetailMods = 0;
            hotkeyCaptureMods = 0;
            ResetEditActionHotkeysToDefaults();
            zoomStepPercent = 10;
            rotationSnapDegrees = 5;
            placementSnapPixels = 8;
            suppressAutomaticUpdatePrompt = false;
            presetHotkeys.Clear();
            EditMode = true;
            DetailEditMode = true;
            hidden = false;
            currentPresetName = "";
            WindowState = FormWindowState.Normal;
            ClientSize = mainBaseClientSize;
            foreach (OverlayItemForm f in items)
            {
                f.SetEditMode(true);
                f.RefreshEffectiveVisibility();
            }
            ApplyHotkeys();
            UpdateButtons();
            ScaleMainLayout();
            SaveConfig();
            SetStatus("설정 초기화 완료: 단축키/편집 상태/본창 크기 초기화  |  오버레이와 프리셋은 유지됨");
        }

        private OverlayItemForm FindWebOverlayFromWindowHandle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            IntPtr current = hwnd;
            for (int depth = 0; depth < 16 && current != IntPtr.Zero; depth++)
            {
                foreach (OverlayItemForm f in items)
                {
                    if (f == null || f.IsDisposed || f.Type != ItemType.Web || !f.IsHandleCreated) continue;
                    if (f.Handle == current) return f;
                }
                IntPtr parent = Native.GetParent(current);
                if (parent == current) break;
                current = parent;
            }
            return null;
        }

        public bool IsWebInteractionEnabled(OverlayItemForm web)
        {
            if (web == null || web.IsDisposed || web.Type != ItemType.Web) return false;
            return WebControlMode || object.ReferenceEquals(singleWebControlOverlay, web);
        }

        public bool IsSingleWebControl(OverlayItemForm web)
        {
            return !WebControlMode && web != null && object.ReferenceEquals(singleWebControlOverlay, web);
        }

        public void EnterWebControlFromDoubleClick(OverlayItemForm web)
        {
            if (web == null || web.IsDisposed || web.Type != ItemType.Web || !EditMode || WebControlMode) return;
            if (HasSingleWebControl) ExitSingleWebControl(false);
            modeBeforeWebControl = EditorMode.Normal;
            SetEditorMode(EditorMode.WebControl, false);
            SelectOverlayForEditing(web);
            web.FocusWebContent();
            SetStatus("웹 편집 모드  |  ESC / 웹 바깥 클릭 = 일반 편집 모드");
        }

        public void EnterSingleWebControl(OverlayItemForm web)
        {
            if (web == null || web.IsDisposed || web.Type != ItemType.Web || !EditMode || WebControlMode) return;
            if (object.ReferenceEquals(singleWebControlOverlay, web))
            {
                web.FocusWebContent();
                return;
            }
            ExitSingleWebControl(false);
            singleWebControlOverlay = web;
            SelectOverlayForEditing(web);
            web.SetEditMode(true);
            web.FocusWebContent();
            web.RefreshSelectionVisual();
            SetStatus("웹 직접 조작  |  ESC / 바깥 클릭 / F10 = 편집으로 복귀");
        }

        public void ExitWebInteractionFromEscape(OverlayItemForm web)
        {
            if (WebControlMode)
            {
                SetEditorMode(EditorMode.Normal, false);
                SetStatus("편집 모드");
                return;
            }
            if (IsSingleWebControl(web))
            {
                ExitSingleWebControl(true);
            }
        }

        public void ExitSingleWebControl(bool showStatus)
        {
            OverlayItemForm old = singleWebControlOverlay;
            singleWebControlOverlay = null;
            if (old != null && !old.IsDisposed)
            {
                old.SetEditMode(EditMode);
                old.RefreshSelectionVisual();
            }
            if (showStatus && EditMode) SetStatus("편집 모드");
        }

        public void ExitSingleWebControlIfFocusLeft(OverlayItemForm web)
        {
            if (!IsSingleWebControl(web)) return;
            bool stillFocused = false;
            try { stillFocused = web.ContainsFocus; } catch { }
            if (!stillFocused) ExitSingleWebControl(false);
        }

        private void SetEditorMode(EditorMode mode, bool save)
        {
            if (HasSingleWebControl) ExitSingleWebControl(false);
            WebControlMode = mode == EditorMode.WebControl;
            EditMode = mode == EditorMode.Normal;
            DetailEditMode = WebControlMode;
            foreach (OverlayItemForm f in items) f.SetEditMode(EditMode);
            // Keep the main UI visually unchanged in Web Control mode.
            // Input is blocked by IMessageFilter instead of disabling panels/controls,
            // which previously caused the entire CatLayer UI to become dim/odd-looking.
            if (leftPanel != null) leftPanel.Enabled = true;
            if (centerPanel != null) centerPanel.Enabled = true;
            if (rightPanel != null) rightPanel.Enabled = true;
            menuButton.Enabled = true;
            hotkeyEditButton.Enabled = true;
            hotkeyHideButton.Enabled = true;
            trayButton.Enabled = true;
            hotkeyDetailButton.Enabled = true;
            hideButton.Enabled = true;
            UpdateButtons();
            if (WebControlMode) SetStatus("웹 조작 모드  |  웹 클릭/스크롤/입력 가능  |  Ctrl+휠 = 웹 확대/축소  |  ESC / F10 종료");
            if (save) SaveConfig();
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (IsDisposed) return false;

            // WebView2 uses its own native child HWND, so WinForms MouseDoubleClick on the
            // overlay Form is not reliable. Catch the raw double-click message and walk the
            // HWND parent chain until the owning web overlay is found.
            if (!WebControlMode && EditMode && m.Msg == 0x0203) // WM_LBUTTONDBLCLK
            {
                OverlayItemForm web = FindWebOverlayFromWindowHandle(m.HWnd);
                if (web != null)
                {
                    EnterWebControlFromDoubleClick(web);
                    return true;
                }
            }

            // ESC must always leave full Web Control mode as well. WebView2 owns a native
            // child HWND while the page has focus, so handle the raw key message here
            // instead of relying on MainForm.ProcessCmdKey/KeyDown.
            if (WebControlMode && (m.Msg == 0x0100 || m.Msg == 0x0104) &&
                m.WParam.ToInt32() == (int)Keys.Escape)
            {
                SetEditorMode(EditorMode.Normal, false);
                SetStatus("편집 모드");
                return true;
            }

            if (HasSingleWebControl && !WebControlMode)
            {
                if ((m.Msg == 0x0100 || m.Msg == 0x0104) && m.WParam.ToInt32() == (int)Keys.Escape)
                {
                    ExitSingleWebControl(true);
                    return true;
                }
                if (m.Msg == 0x0201 || m.Msg == 0x0204 || m.Msg == 0x0207)
                {
                    Control clicked = null;
                    try { clicked = Control.FromHandle(m.HWnd); } catch { }
                    if (clicked != null)
                    {
                        Form clickedForm = clicked.FindForm();
                        OverlayItemForm overlayForm = clickedForm as OverlayItemForm;
                        if (object.ReferenceEquals(clickedForm, this) ||
                            (overlayForm != null && !IsSingleWebControl(overlayForm)))
                        {
                            ExitSingleWebControl(false);
                            return false;
                        }
                    }
                }
            }

            if (WebControlMode && (m.Msg == 0x0201 || m.Msg == 0x0204 || m.Msg == 0x0207))
            {
                // Double-click enters web edit mode. Any mouse click outside a web overlay
                // immediately returns CatLayer to the normal overlay editor, and the same
                // click is then allowed to continue to its original target.
                OverlayItemForm clickedWeb = FindWebOverlayFromWindowHandle(m.HWnd);
                if (clickedWeb == null)
                {
                    SetEditorMode(EditorMode.Normal, false);
                    SetStatus("편집 모드");
                    return false;
                }
            }

            if (!WebControlMode) return false;
            if (!IsBlockedMainUiInputMessage(m.Msg)) return false;

            Control target = null;
            try { target = Control.FromHandle(m.HWnd); } catch { }
            if (target == null || !IsDescendantOfMainForm(target)) return false;

            // F9/F10 remain available as the emergency hide and Web Control exit controls.
            if (IsSameOrChildControl(target, hideButton) || IsSameOrChildControl(target, hotkeyHideButton) || IsSameOrChildControl(target, hotkeyDetailButton))
                return false;

            return true;
        }

        private bool IsDescendantOfMainForm(Control control)
        {
            Control current = control;
            while (current != null)
            {
                if (object.ReferenceEquals(current, this)) return true;
                current = current.Parent;
            }
            return false;
        }

        private static bool IsSameOrChildControl(Control target, Control allowed)
        {
            if (target == null || allowed == null) return false;
            Control current = target;
            while (current != null)
            {
                if (object.ReferenceEquals(current, allowed)) return true;
                current = current.Parent;
            }
            return false;
        }

        private static bool IsBlockedMainUiInputMessage(int msg)
        {
            // Keyboard/input messages sent to CatLayer's own main controls. WM_HOTKEY is
            // intentionally not included so F9/F10 global shortcuts still work.
            if (msg >= 0x0100 && msg <= 0x0109) return true;
            // Client mouse messages, including wheel/hwheel.
            if (msg >= 0x0200 && msg <= 0x020E) return true;
            // Context menu from right-click/keyboard.
            if (msg == 0x007B) return true;
            return false;
        }

        private void ToggleEdit()
        {
            SetEditorMode(EditMode ? EditorMode.Fixed : EditorMode.Normal, true);
        }

        private void ToggleWebControlMode()
        {
            if (!WebControlMode && HasSingleWebControl)
            {
                ExitSingleWebControl(true);
                return;
            }
            if (!WebControlMode)
            {
                bool hasWeb = false;
                foreach (OverlayItemForm f in items) if (f != null && !f.IsDisposed && f.Type == ItemType.Web && f.IsOverlayVisible) { hasWeb = true; break; }
                if (!hasWeb) { SetStatus("표시 중인 웹 오버레이가 없습니다."); return; }
                modeBeforeWebControl = EditMode ? EditorMode.Normal : EditorMode.Fixed;
                SetEditorMode(EditorMode.WebControl, true);
            }
            else
            {
                EditorMode restore = modeBeforeWebControl == EditorMode.WebControl ? EditorMode.Normal : modeBeforeWebControl;
                SetEditorMode(restore, true);
            }
        }

        private void ToggleDetailEdit()
        {
            ToggleWebControlMode();
        }

        private void CycleEditorMode()
        {
            ToggleEdit();
        }

        private void ToggleHidden()
        {
            hidden = !hidden;
            foreach (OverlayItemForm f in items) f.RefreshEffectiveVisibility();
            UpdateButtons(); SaveConfig();
        }
        private void UpdateButtons()
        {
            editButton.Text = WebControlMode ? "웹 조작 중" : (EditMode ? "편집 모드" : "고정 모드");
            editButton.BackColor = WebControlMode ? Color.FromArgb(72, 48, 120) : (EditMode ? Color.FromArgb(28, 63, 108) : UiPanel2);
            hideButton.Text = hidden ? "전체 표시" : "전체 숨김";
            hotkeyEditButton.Text = "편집  " + HotkeyText(hotkeyEditMods, hotkeyEditVk);
            hotkeyHideButton.Text = "숨김  " + HotkeyText(hotkeyHideMods, hotkeyHideVk);
            hotkeyDetailButton.Text = (WebControlMode ? "웹 종료  " : "웹  ") + HotkeyText(hotkeyDetailMods, hotkeyDetailVk);
            hotkeyDetailButton.BackColor = WebControlMode ? Color.FromArgb(72, 48, 120) : UiPanel2;
            if (mainUiReady && !syncingMainUi) RefreshPropertyEditor();
        }
        private void UpdateBridgeStatus()
        {
            string text = ObsBridge.IsAlive() ? "OBS: Bridge 준비" : "OBS: Bridge 미연결";
            if (string.Equals(lastBridgeStatusText, text, StringComparison.Ordinal)) return;
            lastBridgeStatusText = text;
            obsBridgeLabel.Text = text;
        }

        private async void BeginStartupUpdateCheck()
        {
            try
            {
                if (suppressAutomaticUpdatePrompt) return;
                if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return;
                await Task.Delay(1800);
                if (!IsDisposed && !Disposing && Visible && !suppressAutomaticUpdatePrompt) CheckForUpdates(false);
            }
            catch { }
        }

        private UpdatePromptChoice ShowUpdateChoiceDialog(GitHubReleaseInfo release)
        {
            UpdatePromptChoice choice = UpdatePromptChoice.Later;
            using (Form dialog = new Form())
            {
                dialog.Text = "CatLayer 업데이트";
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(560, 390);
                dialog.BackColor = UiBack;
                dialog.ForeColor = UiText;

                Label title = new Label();
                title.Text = "새 CatLayer 버전이 있습니다";
                title.SetBounds(22, 20, 516, 30);
                title.Font = new Font(Font.FontFamily, 14F, FontStyle.Bold);
                title.ForeColor = UiText;
                dialog.Controls.Add(title);

                Label versions = new Label();
                versions.Text = "현재 버전: " + AppInfo.Version + "    →    새 버전: " + release.Version;
                versions.SetBounds(24, 58, 512, 24);
                versions.ForeColor = UiMuted;
                dialog.Controls.Add(versions);

                TextBox notes = new TextBox();
                notes.Multiline = true;
                notes.ReadOnly = true;
                notes.ScrollBars = ScrollBars.Vertical;
                notes.BackColor = UiPanel2;
                notes.ForeColor = UiText;
                notes.BorderStyle = BorderStyle.FixedSingle;
                notes.SetBounds(24, 92, 512, 212);
                string summary = (release.Body ?? "").Trim();
                if (summary.Length > 1800) summary = summary.Substring(0, 1800) + "...";
                string releaseName = string.IsNullOrWhiteSpace(release.Name) ? "" : (release.Name + "\r\n\r\n");
                notes.Text = releaseName + (string.IsNullOrWhiteSpace(summary) ? "새 버전이 GitHub에 공개되었습니다." : summary);
                dialog.Controls.Add(notes);

                Button update = new Button();
                update.Text = "업데이트";
                update.SetBounds(24, 326, 154, 42);
                StyleButton(update, true);
                update.Click += delegate { choice = UpdatePromptChoice.Update; dialog.DialogResult = DialogResult.OK; dialog.Close(); };
                dialog.Controls.Add(update);

                Button later = new Button();
                later.Text = "나중에 하기";
                later.SetBounds(203, 326, 154, 42);
                StyleButton(later, false);
                later.Click += delegate { choice = UpdatePromptChoice.Later; dialog.DialogResult = DialogResult.Cancel; dialog.Close(); };
                dialog.Controls.Add(later);

                Button never = new Button();
                never.Text = "다시 안 보기";
                never.SetBounds(382, 326, 154, 42);
                StyleButton(never, false);
                never.Click += delegate { choice = UpdatePromptChoice.Never; dialog.DialogResult = DialogResult.Ignore; dialog.Close(); };
                dialog.Controls.Add(never);

                dialog.AcceptButton = update;
                dialog.CancelButton = later;
                dialog.ShowDialog(this);
            }
            return choice;
        }

        private async void CheckForUpdates(bool manual)
        {
            if (updateCheckRunning || updateStartRequested) return;
            if (!manual && suppressAutomaticUpdatePrompt) return;
            if (!manual && !System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return;

            updateCheckRunning = true;
            if (manual) SetStatus("GitHub에서 업데이트 확인 중...");
            try
            {
                GitHubReleaseInfo release = await Task.Run(delegate { return GitHubUpdateService.GetLatestStableRelease(); });
                if (release == null)
                {
                    if (manual) MessageBox.Show(this, "아직 공개된 정식 GitHub Release가 없습니다.\n\nPre-release는 자동 업데이트 대상에서 제외됩니다.", "CatLayer 업데이트", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (GitHubUpdateService.CompareVersions(release.Version, AppInfo.Version) <= 0)
                {
                    if (manual) MessageBox.Show(this, "현재 최신 버전을 사용 중입니다.\n\n현재: " + AppInfo.Version + "\n최신: " + release.Version, "CatLayer 업데이트", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                UpdatePromptChoice choice = ShowUpdateChoiceDialog(release);
                if (choice == UpdatePromptChoice.Never)
                {
                    suppressAutomaticUpdatePrompt = true;
                    SaveConfig();
                    SetStatus("자동 업데이트 알림을 숨겼습니다. 메뉴에서 업데이트 확인 시 다시 활성화됩니다.");
                    return;
                }
                if (choice != UpdatePromptChoice.Update) return;

                if (string.IsNullOrWhiteSpace(release.ZipUrl) || string.IsNullOrWhiteSpace(release.ShaUrl))
                {
                    MessageBox.Show(this,
                        "이 Release에는 자동 업데이트용 파일이 없습니다.\n\n필요한 Assets:\n- CatLayer_v" + release.Version + "_update.zip\n- SHA256.txt\n\nGitHub Release 페이지에서 직접 확인해 주세요.",
                        "CatLayer 업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    if (!string.IsNullOrWhiteSpace(release.HtmlUrl)) { try { Process.Start(release.HtmlUrl); } catch { } }
                    return;
                }
                await DownloadAndStartUpdate(release);
            }
            catch (WebException ex)
            {
                if (manual) MessageBox.Show(this, "GitHub에 연결하지 못했습니다.\n\n" + ex.Message, "CatLayer 업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "GitHub update check");
                if (manual) MessageBox.Show(this, "업데이트 확인 중 오류가 발생했습니다.\n\n" + ex.Message, "CatLayer 업데이트", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                updateCheckRunning = false;
                if (manual && !updateStartRequested) SetStatus("업데이트 확인 완료");
            }
        }

        private async Task DownloadAndStartUpdate(GitHubReleaseInfo release)
        {
            string updater = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Updater.exe");
            if (!File.Exists(updater))
            {
                MessageBox.Show(this, "Updater.exe가 없습니다.\n\n현재 CatLayer 패키지를 다시 설치한 뒤 업데이트를 시도해 주세요.", "CatLayer 업데이트", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string updateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatLayer", "Update");
            Directory.CreateDirectory(updateDir);
            string zipPath = Path.Combine(updateDir, release.ZipName);
            string shaPath = Path.Combine(updateDir, "SHA256.txt");
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            try { if (File.Exists(shaPath)) File.Delete(shaPath); } catch { }
            SetStatus("CatLayer " + release.Version + " 다운로드 중...");

            await Task.Run(delegate
            {
                GitHubUpdateService.DownloadFile(release.ZipUrl, zipPath);
                GitHubUpdateService.DownloadFile(release.ShaUrl, shaPath);
            });

            string expected = GitHubUpdateService.ReadExpectedSha(shaPath, release.ZipName);
            if (string.IsNullOrWhiteSpace(expected)) throw new InvalidDataException("SHA256.txt에서 업데이트 ZIP의 해시를 찾지 못했습니다.");
            string actual = await Task.Run(delegate { return GitHubUpdateService.ComputeSha256(zipPath); });
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(zipPath); } catch { }
                throw new InvalidDataException("업데이트 파일 SHA-256 검증에 실패했습니다. 파일을 적용하지 않습니다.");
            }

            string runner = Path.Combine(updateDir, "CatLayerUpdater_" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(updater, runner, true);
            string installDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string restartExe = Path.Combine(installDir, "CatLayer.exe");
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = runner;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = updateDir;
            psi.Arguments = Process.GetCurrentProcess().Id.ToString() + " \"" + installDir + "\" \"" + zipPath + "\" \"" + restartExe + "\"";
            Process.Start(psi);
            updateStartRequested = true;
            SetStatus("업데이트 적용을 위해 CatLayer를 종료합니다...");
            BeginInvoke(new MethodInvoker(delegate { Close(); }));
        }

        private void SetStatus(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                statusLabel.Text = "준비";
            else
                statusLabel.Text = message;
        }

        private static string KeyNameFromVk(int vk)
        {
            if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x6F).ToString();
            Keys key = (Keys)vk;
            if (key >= Keys.D0 && key <= Keys.D9) return ((char)('0' + (vk - (int)Keys.D0))).ToString();
            return key.ToString();
        }

        private static string HotkeyText(int mods, int vk)
        {
            List<string> parts = new List<string>();
            if ((mods & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mods & Native.MOD_ALT) != 0) parts.Add("Alt");
            if ((mods & Native.MOD_SHIFT) != 0) parts.Add("Shift");
            if ((mods & Native.MOD_WIN) != 0) parts.Add("Win");
            parts.Add(KeyNameFromVk(vk));
            return string.Join("+", parts.ToArray());
        }

        private static bool IsModifierOnlyKey(Keys key)
        {
            return key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey ||
                key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey ||
                key == Keys.Menu || key == Keys.LMenu || key == Keys.RMenu ||
                key == Keys.LWin || key == Keys.RWin;
        }

        private static bool IsFunctionKeyVk(int vk)
        {
            return vk >= (int)Keys.F1 && vk <= (int)Keys.F12;
        }

        private static bool IsSafeGlobalHotkey(int mods, int vk)
        {
            return IsFunctionKeyVk(vk) || mods != 0;
        }

        private static int HotkeyModifiersFromKeyData(Keys keyData)
        {
            int mods = 0;
            if ((keyData & Keys.Control) == Keys.Control) mods |= Native.MOD_CONTROL;
            if ((keyData & Keys.Alt) == Keys.Alt) mods |= Native.MOD_ALT;
            if ((keyData & Keys.Shift) == Keys.Shift) mods |= Native.MOD_SHIFT;
            return mods;
        }

        private static void SetupHotkeyCapture(TextBox box, int[] modsRef, int[] vkRef)
        {
            box.ReadOnly = true;
            box.ShortcutsEnabled = false;
            box.TextAlign = HorizontalAlignment.Center;
            box.Text = HotkeyText(modsRef[0], vkRef[0]);
            box.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                Keys key = e.KeyCode;
                if (!IsModifierOnlyKey(key) && key != Keys.None)
                {
                    modsRef[0] = HotkeyModifiersFromKeyData(e.KeyData);
                    vkRef[0] = (int)key;
                    box.Text = HotkeyText(modsRef[0], vkRef[0]);
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
        }

        private static void SetupOptionalHotkeyCapture(TextBox box, int[] modsRef, int[] vkRef)
        {
            box.ReadOnly = true;
            box.ShortcutsEnabled = false;
            box.TextAlign = HorizontalAlignment.Center;
            box.Text = vkRef[0] > 0 ? HotkeyText(modsRef[0], vkRef[0]) : "지정 안 함";
            box.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                Keys key = e.KeyCode;
                if (key == Keys.Delete || key == Keys.Back || key == Keys.Escape)
                {
                    modsRef[0] = 0; vkRef[0] = 0; box.Text = "지정 안 함";
                }
                else if (!IsModifierOnlyKey(key) && key != Keys.None)
                {
                    modsRef[0] = HotkeyModifiersFromKeyData(e.KeyData);
                    vkRef[0] = (int)key;
                    box.Text = HotkeyText(modsRef[0], vkRef[0]);
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
        }

        private void UnregisterAllHotkeys()
        {
            try { Native.UnregisterHotKey(Handle, 1); } catch { }
            try { Native.UnregisterHotKey(Handle, 2); } catch { }
            try { Native.UnregisterHotKey(Handle, 3); } catch { }
            try { Native.UnregisterHotKey(Handle, 4); } catch { }
            foreach (int id in new List<int>(registeredPresetHotkeys.Keys))
            {
                try { Native.UnregisterHotKey(Handle, id); } catch { }
            }
            registeredPresetHotkeys.Clear();
        }

        private void RemoveMissingPresetHotkeys()
        {
            for (int i = presetHotkeys.Count - 1; i >= 0; i--)
            {
                PresetHotkeyBinding binding = presetHotkeys[i];
                string fileName = binding == null ? "" : Path.GetFileName(binding.FileName ?? "");
                string path = string.IsNullOrWhiteSpace(fileName) ? "" : Path.Combine(presetsDir, fileName);
                bool conflictsCore = binding != null && (
                    (binding.Mods == hotkeyEditMods && binding.Vk == hotkeyEditVk) ||
                    (binding.Mods == hotkeyHideMods && binding.Vk == hotkeyHideVk) ||
                    (binding.Mods == hotkeyDetailMods && binding.Vk == hotkeyDetailVk) ||
                    (binding.Mods == hotkeyCaptureMods && binding.Vk == hotkeyCaptureVk));
                if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(path) || (binding != null && IsReservedClipboardHotkey(binding.Mods, binding.Vk)) || conflictsCore) presetHotkeys.RemoveAt(i);
                else binding.FileName = fileName;
            }
        }

        private void RemovePresetHotkeyForPath(string path)
        {
            string fileName = Path.GetFileName(path ?? "");
            if (string.IsNullOrWhiteSpace(fileName)) return;
            for (int i = presetHotkeys.Count - 1; i >= 0; i--)
            {
                if (string.Equals(Path.GetFileName(presetHotkeys[i].FileName ?? ""), fileName, StringComparison.OrdinalIgnoreCase))
                    presetHotkeys.RemoveAt(i);
            }
        }

        private PresetHotkeyBinding FindPresetHotkey(string path)
        {
            string fileName = Path.GetFileName(path ?? "");
            foreach (PresetHotkeyBinding binding in presetHotkeys)
                if (string.Equals(Path.GetFileName(binding.FileName ?? ""), fileName, StringComparison.OrdinalIgnoreCase)) return binding;
            return null;
        }

        private bool ApplyHotkeys()
        {
            UnregisterAllHotkeys();
            RemoveMissingPresetHotkeys();
            bool ok1 = false, ok2 = false, ok3 = false, ok4 = false;
            try { ok1 = Native.RegisterHotKey(Handle, 1, hotkeyEditMods | Native.MOD_NOREPEAT, hotkeyEditVk); } catch { }
            try { ok2 = Native.RegisterHotKey(Handle, 2, hotkeyHideMods | Native.MOD_NOREPEAT, hotkeyHideVk); } catch { }
            try { ok3 = Native.RegisterHotKey(Handle, 3, hotkeyDetailMods | Native.MOD_NOREPEAT, hotkeyDetailVk); } catch { }
            try { ok4 = Native.RegisterHotKey(Handle, 4, hotkeyCaptureMods | Native.MOD_NOREPEAT, hotkeyCaptureVk); } catch { }

            bool presetOk = true;
            int nextId = PresetHotkeyIdBase;
            foreach (PresetHotkeyBinding binding in presetHotkeys)
            {
                if (binding == null || binding.Vk <= 0) continue;
                string path = Path.Combine(presetsDir, Path.GetFileName(binding.FileName ?? ""));
                if (!File.Exists(path)) continue;
                bool registered = false;
                try { registered = Native.RegisterHotKey(Handle, nextId, binding.Mods | Native.MOD_NOREPEAT, binding.Vk); } catch { }
                if (registered) registeredPresetHotkeys[nextId] = binding;
                else presetOk = false;
                nextId++;
            }

            if (mainUiReady && (!ok1 || !ok2 || !ok3 || !ok4 || !presetOk))
                SetStatus("일부 전역 단축키를 등록하지 못했습니다. 다른 프로그램과 단축키가 겹치는지 확인하세요.");
            return ok1 && ok2 && ok3 && ok4 && presetOk;
        }

        private void ShowHotkeySettings()
        {
            using (Form f = new Form())
            {
                f.Text = "CatLayer 설정 - 단축키";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(570, 666);
                f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false;

                Label title = new Label(); title.Text = "사용자 지정 단축키"; title.Font = new Font(f.Font, FontStyle.Bold); title.SetBounds(18, 14, 520, 24); f.Controls.Add(title);
                Label help = new Label(); help.Text = "입력칸을 클릭한 뒤 원하는 키 조합을 직접 누르세요.\n편집/숨김/웹 조작/영역 캡처는 전역 단축키이며, 나머지는 CatLayer 편집 중에 동작합니다."; help.SetBounds(18, 40, 530, 42); f.Controls.Add(help);

                string[] labels = new string[] {
                    "고정 ↔ 편집", "전체 표시 / 숨김", "웹 조작 모드", "영역 캡처",
                    "그룹화/그룹해제", "그룹 해제",
                    "회전 -1°", "회전 +1°", "회전 -10°", "회전 +10°",
                    "좌우 반전", "상하 반전", "각도 0° (반전 유지)", "회전/반전 전체 초기화"
                };
                int[] ys = new int[] { 82, 114, 146, 178, 222, 254, 298, 330, 362, 394, 438, 470, 502, 534 };
                int[][] modsRefs = new int[][] {
                    new int[] { hotkeyEditMods }, new int[] { hotkeyHideMods }, new int[] { hotkeyDetailMods }, new int[] { hotkeyCaptureMods },
                    new int[] { hotkeyGroupMods }, new int[] { hotkeyUngroupMods },
                    new int[] { hotkeyRotateMinus1Mods }, new int[] { hotkeyRotatePlus1Mods }, new int[] { hotkeyRotateMinus10Mods }, new int[] { hotkeyRotatePlus10Mods },
                    new int[] { hotkeyFlipHorizontalMods }, new int[] { hotkeyFlipVerticalMods },
                    new int[] { hotkeyResetRotationMods }, new int[] { hotkeyResetTransformMods }
                };
                int[][] vkRefs = new int[][] {
                    new int[] { hotkeyEditVk }, new int[] { hotkeyHideVk }, new int[] { hotkeyDetailVk }, new int[] { hotkeyCaptureVk },
                    new int[] { hotkeyGroupVk }, new int[] { hotkeyUngroupVk },
                    new int[] { hotkeyRotateMinus1Vk }, new int[] { hotkeyRotatePlus1Vk }, new int[] { hotkeyRotateMinus10Vk }, new int[] { hotkeyRotatePlus10Vk },
                    new int[] { hotkeyFlipHorizontalVk }, new int[] { hotkeyFlipVerticalVk },
                    new int[] { hotkeyResetRotationVk }, new int[] { hotkeyResetTransformVk }
                };
                TextBox[] boxes = new TextBox[labels.Length];
                for (int i = 0; i < labels.Length; i++)
                {
                    Label label = new Label(); label.Text = labels[i]; label.SetBounds(18, ys[i] + 3, 190, 22); f.Controls.Add(label);
                    boxes[i] = new TextBox(); boxes[i].SetBounds(215, ys[i], 330, 26); f.Controls.Add(boxes[i]);
                    SetupHotkeyCapture(boxes[i], modsRefs[i], vkRefs[i]);
                }

                Button defaults = new Button(); defaults.Text = "기본값"; defaults.SetBounds(18, 611, 82, 30); f.Controls.Add(defaults);
                Button ok = new Button(); ok.Text = "적용"; ok.DialogResult = DialogResult.OK; ok.SetBounds(370, 611, 82, 30); f.Controls.Add(ok);
                Button cancel = new Button(); cancel.Text = "취소"; cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(463, 611, 82, 30); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;

                defaults.Click += delegate
                {
                    int[] defaultVks = new int[] { Native.VK_F8, Native.VK_F9, Native.VK_F10, Native.VK_F1, (int)Keys.G, (int)Keys.G, (int)Keys.Q, (int)Keys.E, (int)Keys.Q, (int)Keys.E, (int)Keys.H, (int)Keys.V, (int)Keys.R, (int)Keys.R };
                    int[] defaultMods = new int[] { 0, 0, 0, 0, Native.MOD_CONTROL, Native.MOD_CONTROL | Native.MOD_SHIFT, 0, 0, Native.MOD_SHIFT, Native.MOD_SHIFT, 0, 0, 0, Native.MOD_SHIFT };
                    for (int i = 0; i < boxes.Length; i++)
                    {
                        modsRefs[i][0] = defaultMods[i]; vkRefs[i][0] = defaultVks[i];
                        boxes[i].Text = HotkeyText(modsRefs[i][0], vkRefs[i][0]);
                    }
                };

                if (f.ShowDialog(this) != DialogResult.OK) return;

                for (int i = 0; i < labels.Length; i++)
                {
                    if (IsReservedClipboardHotkey(modsRefs[i][0], vkRefs[i][0]))
                    {
                        MessageBox.Show(this, "Ctrl+C / Ctrl+V는 오버레이 복사와 이미지 붙여넣기 전용키로 예약되어 있습니다.", "단축키 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
                List<string> hotkeyNames = new List<string>();
                for (int i = 0; i < labels.Length; i++) hotkeyNames.Add(HotkeyText(modsRefs[i][0], vkRefs[i][0]));
                for (int i = 0; i < hotkeyNames.Count; i++)
                {
                    for (int j = i + 1; j < hotkeyNames.Count; j++)
                    {
                        if (string.Equals(hotkeyNames[i], hotkeyNames[j], StringComparison.OrdinalIgnoreCase))
                        {
                            MessageBox.Show(this, "단축키가 중복되었습니다.\n\n" + labels[i] + " / " + labels[j] + "\n" + hotkeyNames[i], "단축키 중복", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                    }
                }
                foreach (PresetHotkeyBinding binding in presetHotkeys)
                {
                    if (binding == null || binding.Vk <= 0) continue;
                    string presetHotkey = HotkeyText(binding.Mods, binding.Vk);
                    for (int i = 0; i < hotkeyNames.Count; i++)
                    {
                        if (string.Equals(hotkeyNames[i], presetHotkey, StringComparison.OrdinalIgnoreCase))
                        {
                            MessageBox.Show(this, "프리셋 단축키와 중복되었습니다.\n\n" + labels[i] + " / " + presetHotkey, "단축키 중복", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                    }
                }
                if (!IsSafeGlobalHotkey(modsRefs[0][0], vkRefs[0][0]) || !IsSafeGlobalHotkey(modsRefs[1][0], vkRefs[1][0]) || !IsSafeGlobalHotkey(modsRefs[2][0], vkRefs[2][0]) || !IsSafeGlobalHotkey(modsRefs[3][0], vkRefs[3][0]))
                {
                    MessageBox.Show(this, "편집/숨김/웹 조작/영역 캡처 전역 단축키에서 문자·숫자를 사용할 때는 Ctrl, Alt 또는 Shift 같은 조합키가 필요합니다.\n\nF1~F12는 단독으로 사용할 수 있습니다.", "단축키 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                hotkeyEditMods = modsRefs[0][0]; hotkeyEditVk = vkRefs[0][0];
                hotkeyHideMods = modsRefs[1][0]; hotkeyHideVk = vkRefs[1][0];
                hotkeyDetailMods = modsRefs[2][0]; hotkeyDetailVk = vkRefs[2][0];
                hotkeyCaptureMods = modsRefs[3][0]; hotkeyCaptureVk = vkRefs[3][0];
                hotkeyGroupMods = modsRefs[4][0]; hotkeyGroupVk = vkRefs[4][0];
                hotkeyUngroupMods = modsRefs[5][0]; hotkeyUngroupVk = vkRefs[5][0];
                hotkeyRotateMinus1Mods = modsRefs[6][0]; hotkeyRotateMinus1Vk = vkRefs[6][0];
                hotkeyRotatePlus1Mods = modsRefs[7][0]; hotkeyRotatePlus1Vk = vkRefs[7][0];
                hotkeyRotateMinus10Mods = modsRefs[8][0]; hotkeyRotateMinus10Vk = vkRefs[8][0];
                hotkeyRotatePlus10Mods = modsRefs[9][0]; hotkeyRotatePlus10Vk = vkRefs[9][0];
                hotkeyFlipHorizontalMods = modsRefs[10][0]; hotkeyFlipHorizontalVk = vkRefs[10][0];
                hotkeyFlipVerticalMods = modsRefs[11][0]; hotkeyFlipVerticalVk = vkRefs[11][0];
                hotkeyResetRotationMods = modsRefs[12][0]; hotkeyResetRotationVk = vkRefs[12][0];
                hotkeyResetTransformMods = modsRefs[13][0]; hotkeyResetTransformVk = vkRefs[13][0];

                bool registered = ApplyHotkeys();
                UpdateButtons(); SaveConfig();
                SetStatus(registered ? "사용자 지정 단축키를 적용했습니다." : "단축키는 저장됐지만 일부 전역 키가 다른 프로그램과 충돌합니다.");
            }
        }

        private void ShowZoomSettings()
        {
            using (Form f = new Form())
            using (Label title = new Label())
            using (Label help = new Label())
            using (DarkNumberBox number = new DarkNumberBox())
            using (Button ok = new Button())
            using (Button cancel = new Button())
            {
                f.Text = "CatLayer 설정 - 확대/축소";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(390, 175);
                f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false;
                f.BackColor = UiPanel; f.ForeColor = UiText;

                title.Text = "마우스 휠 확대/축소 비율"; title.Font = new Font(f.Font, FontStyle.Bold); title.ForeColor = UiText; title.BackColor = Color.Transparent; title.SetBounds(18, 16, 340, 24); f.Controls.Add(title);
                help.Text = "기본값 10%  |  설정 가능 범위 1~90%\n편집 모드: Ctrl+휠 = 오버레이 크기  |  웹 조작 모드: Ctrl+휠 = 웹페이지 확대/축소";
                help.ForeColor = UiMuted; help.BackColor = Color.Transparent; help.SetBounds(18, 44, 350, 38); f.Controls.Add(help);
                number.SetBounds(18, 90, 120, 28); number.Minimum = 1; number.Maximum = 90; number.Value = zoomStepPercent; f.Controls.Add(number);
                Label percent = new Label(); percent.Text = "%"; percent.ForeColor = UiText; percent.BackColor = Color.Transparent; percent.SetBounds(145, 94, 30, 22); f.Controls.Add(percent);

                ok.Text = "적용"; ok.SetBounds(208, 128, 76, 30); StyleButton(ok, false); ok.DialogResult = DialogResult.OK; f.Controls.Add(ok);
                cancel.Text = "취소"; cancel.SetBounds(294, 128, 76, 30); StyleButton(cancel, false); cancel.DialogResult = DialogResult.Cancel; f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog(this) != DialogResult.OK) return;
                zoomStepPercent = Math.Max(1, Math.Min(90, (int)number.Value));
                SaveConfig();
                SetStatus("확대/축소 비율을 " + zoomStepPercent.ToString() + "%로 설정했습니다.");
            }
        }

        private void ShowRotationSnapSettings()
        {
            using (Form f = new Form())
            using (Label title = new Label())
            using (Label help = new Label())
            using (DarkNumberBox number = new DarkNumberBox())
            using (Button ok = new Button())
            using (Button cancel = new Button())
            {
                f.Text = "CatLayer 설정 - 회전 자석"; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(410, 190); f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false; f.BackColor = UiPanel; f.ForeColor = UiText;
                title.Text = "자유 회전 자석 감도"; title.Font = new Font(f.Font, FontStyle.Bold); title.ForeColor = UiText; title.BackColor = Color.Transparent; title.SetBounds(18, 16, 360, 24); f.Controls.Add(title);
                help.Text = "Ctrl+드래그 자유 회전에서 0° / 90° / 180° / 270°에 붙습니다.\n0° = 자석 끄기  |  기본값 5°"; help.ForeColor = UiMuted; help.BackColor = Color.Transparent; help.SetBounds(18, 44, 370, 44); f.Controls.Add(help);
                number.SetBounds(18, 100, 120, 28); number.Minimum = 0; number.Maximum = 15; number.Value = rotationSnapDegrees; f.Controls.Add(number);
                Label degree = new Label(); degree.Text = "°"; degree.ForeColor = UiText; degree.BackColor = Color.Transparent; degree.SetBounds(145, 104, 30, 22); f.Controls.Add(degree);
                ok.Text = "적용"; ok.SetBounds(228, 142, 76, 30); StyleButton(ok, false); ok.DialogResult = DialogResult.OK; f.Controls.Add(ok);
                cancel.Text = "취소"; cancel.SetBounds(314, 142, 76, 30); StyleButton(cancel, false); cancel.DialogResult = DialogResult.Cancel; f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog(this) != DialogResult.OK) return;
                rotationSnapDegrees = Math.Max(0, Math.Min(15, (int)number.Value)); SaveConfig();
                SetStatus(rotationSnapDegrees == 0 ? "회전 자석을 껐습니다." : "회전 자석 감도: ±" + rotationSnapDegrees.ToString() + "°");
            }
        }

        private void ShowPlacementSnapSettings()
        {
            using (Form f = new Form())
            using (Label title = new Label())
            using (Label help = new Label())
            using (DarkNumberBox number = new DarkNumberBox())
            using (Button ok = new Button())
            using (Button cancel = new Button())
            {
                f.Text = "CatLayer 설정 - 배치 자석"; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(420, 190); f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false; f.BackColor = UiPanel; f.ForeColor = UiText;
                title.Text = "오버레이 배치 자석 감도"; title.Font = new Font(f.Font, FontStyle.Bold); title.ForeColor = UiText; title.BackColor = Color.Transparent; title.SetBounds(18, 16, 370, 24); f.Controls.Add(title);
                help.Text = "이동 중 화면 가장자리/중앙과 다른 오버레이의 선에 가까워지면 붙습니다.\n0px = 자석 끄기  |  기본값 8px"; help.ForeColor = UiMuted; help.BackColor = Color.Transparent; help.SetBounds(18, 44, 380, 44); f.Controls.Add(help);
                number.SetBounds(18, 100, 120, 28); number.Minimum = 0; number.Maximum = 30; number.Value = placementSnapPixels; f.Controls.Add(number);
                Label px = new Label(); px.Text = "px"; px.ForeColor = UiText; px.BackColor = Color.Transparent; px.SetBounds(145, 104, 40, 22); f.Controls.Add(px);
                ok.Text = "적용"; ok.SetBounds(238, 142, 76, 30); StyleButton(ok, false); ok.DialogResult = DialogResult.OK; f.Controls.Add(ok);
                cancel.Text = "취소"; cancel.SetBounds(324, 142, 76, 30); StyleButton(cancel, false); cancel.DialogResult = DialogResult.Cancel; f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog(this) != DialogResult.OK) return;
                placementSnapPixels = Math.Max(0, Math.Min(30, (int)number.Value)); SaveConfig();
                SetStatus(placementSnapPixels == 0 ? "배치 자석을 껐습니다." : "배치 자석 감도: " + placementSnapPixels.ToString() + "px");
            }
        }

        private void ShowPresetHotkeySettings()
        {
            List<PresetListEntry> presets = GetPresetEntries();
            if (presets.Count == 0)
            {
                SetStatus("단축키를 지정할 프리셋이 없습니다.");
                return;
            }

            using (Form f = new Form())
            using (Panel scroll = new Panel())
            using (Button clearAll = new Button())
            using (Button ok = new Button())
            using (Button cancel = new Button())
            {
                f.Text = "CatLayer 설정 - 프리셋 단축키";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(650, 520);
                f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false;

                Label title = new Label(); title.Text = "프리셋별 전역 단축키"; title.Font = new Font(f.Font, FontStyle.Bold); title.SetBounds(18, 14, 600, 24); f.Controls.Add(title);
                Label help = new Label(); help.Text = "프리셋마다 원하는 키 조합을 지정할 수 있습니다. Delete/Backspace/Esc를 누르면 해당 단축키가 해제됩니다.\nF1~F12는 단독 사용 가능하며 문자/숫자는 Ctrl/Alt/Shift 조합이 필요합니다."; help.SetBounds(18, 40, 610, 42); f.Controls.Add(help);

                scroll.SetBounds(18, 92, 612, 360); scroll.AutoScroll = true; f.Controls.Add(scroll);
                List<int[]> modsRefs = new List<int[]>();
                List<int[]> vkRefs = new List<int[]>();
                List<TextBox> boxes = new List<TextBox>();
                for (int i = 0; i < presets.Count; i++)
                {
                    PresetListEntry entry = presets[i];
                    PresetHotkeyBinding existing = FindPresetHotkey(entry.Path);
                    int[] modsRef = new int[] { existing == null ? 0 : existing.Mods };
                    int[] vkRef = new int[] { existing == null ? 0 : existing.Vk };
                    modsRefs.Add(modsRef); vkRefs.Add(vkRef);

                    int y = i * 34;
                    Label name = new Label(); name.Text = entry.Name; name.AutoEllipsis = true; name.SetBounds(4, y + 4, 245, 24); scroll.Controls.Add(name);
                    TextBox box = new TextBox(); box.SetBounds(260, y + 1, 320, 26); scroll.Controls.Add(box); boxes.Add(box);
                    SetupOptionalHotkeyCapture(box, modsRef, vkRef);
                }
                scroll.AutoScrollMinSize = new Size(0, presets.Count * 34 + 4);

                clearAll.Text = "전체 해제"; clearAll.SetBounds(18, 468, 90, 30); f.Controls.Add(clearAll);
                clearAll.Click += delegate
                {
                    for (int i = 0; i < boxes.Count; i++)
                    {
                        modsRefs[i][0] = 0; vkRefs[i][0] = 0; boxes[i].Text = "지정 안 함";
                    }
                };

                ok.Text = "적용"; ok.DialogResult = DialogResult.OK; ok.SetBounds(458, 468, 80, 30); f.Controls.Add(ok);
                cancel.Text = "취소"; cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(548, 468, 80, 30); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog(this) != DialogResult.OK) return;

                HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string[] existingNames = new string[] {
                    HotkeyText(hotkeyEditMods, hotkeyEditVk), HotkeyText(hotkeyHideMods, hotkeyHideVk), HotkeyText(hotkeyCaptureMods, hotkeyCaptureVk),
                    HotkeyText(hotkeyGroupMods, hotkeyGroupVk), HotkeyText(hotkeyUngroupMods, hotkeyUngroupVk),
                    HotkeyText(hotkeyRotateMinus1Mods, hotkeyRotateMinus1Vk), HotkeyText(hotkeyRotatePlus1Mods, hotkeyRotatePlus1Vk),
                    HotkeyText(hotkeyRotateMinus10Mods, hotkeyRotateMinus10Vk), HotkeyText(hotkeyRotatePlus10Mods, hotkeyRotatePlus10Vk),
                    HotkeyText(hotkeyFlipHorizontalMods, hotkeyFlipHorizontalVk), HotkeyText(hotkeyFlipVerticalMods, hotkeyFlipVerticalVk),
                    HotkeyText(hotkeyResetRotationMods, hotkeyResetRotationVk), HotkeyText(hotkeyResetTransformMods, hotkeyResetTransformVk)
                };
                foreach (string name in existingNames) used.Add(name);

                for (int i = 0; i < presets.Count; i++)
                {
                    if (vkRefs[i][0] <= 0) continue;
                    if (IsReservedClipboardHotkey(modsRefs[i][0], vkRefs[i][0]))
                    {
                        MessageBox.Show(this, "Ctrl+C / Ctrl+V는 오버레이 복사와 이미지 붙여넣기 전용키로 예약되어 있습니다.\n\n프리셋: " + presets[i].Name,
                            "프리셋 단축키 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (!IsSafeGlobalHotkey(modsRefs[i][0], vkRefs[i][0]))
                    {
                        MessageBox.Show(this, "프리셋 단축키에서 문자/숫자를 사용할 때는 Ctrl, Alt 또는 Shift 같은 조합키가 필요합니다.\n\n프리셋: " + presets[i].Name,
                            "프리셋 단축키 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    string hotkey = HotkeyText(modsRefs[i][0], vkRefs[i][0]);
                    if (!used.Add(hotkey))
                    {
                        MessageBox.Show(this, "이미 사용 중인 단축키입니다.\n\n프리셋: " + presets[i].Name + "\n단축키: " + hotkey,
                            "단축키 중복", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }

                presetHotkeys.Clear();
                for (int i = 0; i < presets.Count; i++)
                {
                    if (vkRefs[i][0] <= 0) continue;
                    PresetHotkeyBinding binding = new PresetHotkeyBinding();
                    binding.FileName = Path.GetFileName(presets[i].Path);
                    binding.Mods = modsRefs[i][0]; binding.Vk = vkRefs[i][0];
                    presetHotkeys.Add(binding);
                }
                bool registered = ApplyHotkeys();
                SaveConfig();
                SetStatus(registered ? "프리셋 단축키를 적용했습니다." : "프리셋 단축키는 저장됐지만 일부 키를 Windows에 등록하지 못했습니다.");
            }
        }

        private void LoadPresetByHotkey(PresetHotkeyBinding binding)
        {
            if (binding == null) return;
            string fileName = Path.GetFileName(binding.FileName ?? "");
            string path = string.IsNullOrWhiteSpace(fileName) ? "" : Path.Combine(presetsDir, fileName);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                RemovePresetHotkeyForPath(path);
                ApplyHotkeys(); SaveConfig();
                SetStatus("프리셋 단축키 대상 파일이 없어 연결을 해제했습니다.");
                return;
            }
            try
            {
                CaptureUndo("프리셋 단축키 불러오기");
                string loadedName = LoadPresetFile(path);
                currentPresetName = loadedName;
                SetStatus("프리셋 단축키 불러오기: " + loadedName);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "LoadPresetByHotkey");
                SetStatus("프리셋 단축키 불러오기 실패: " + ex.Message);
            }
        }

        private void HideToTray()
        {
            Hide();
            if (catLayerOwnsForeground) { catLayerOwnsForeground = false; RefreshOverlaySelectionVisuals(); }
            trayIcon.Visible = true;
            if (!trayHintShown)
            {
                trayHintShown = true;
                try { trayIcon.ShowBalloonTip(2500, "CatLayer", "트레이로 최소화되었습니다. 트레이 아이콘 더블클릭으로 다시 열 수 있어요.", ToolTipIcon.Info); } catch { }
            }
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            trayIcon.Visible = false;
        }

        public void RestoreFromSecondaryLaunch()
        {
            if (IsDisposed) return;
            ShowFromTray();
            string pending = ConsumePendingLaunchArgument();
            if (!string.IsNullOrWhiteSpace(pending) && OpenLaunchFileFromShell(pending, true)) return;
            SetStatus("이미 실행 중인 CatLayer 창을 불러왔습니다.");
        }

        public Rectangle ApplyPlacementSnap(OverlayItemForm source, Rectangle candidate)
        {
            int sensitivity = placementSnapPixels;
            if (sensitivity <= 0 || source == null) return candidate;

            Rectangle sourceVisual = source.GetVisualContentBounds(candidate);
            int bestDx = sensitivity + 1, bestDy = sensitivity + 1;
            int snapDx = 0, snapDy = 0;
            List<int> targetX = new List<int>();
            List<int> targetY = new List<int>();
            Rectangle area = Screen.FromRectangle(sourceVisual).WorkingArea;
            targetX.Add(area.Left); targetX.Add(area.Left + area.Width / 2); targetX.Add(area.Right);
            targetY.Add(area.Top); targetY.Add(area.Top + area.Height / 2); targetY.Add(area.Bottom);
            foreach (OverlayItemForm other in items)
            {
                if (other == null || other == source || other.IsDisposed || !other.IsOverlayVisible) continue;
                if (source.GroupId > 0 && other.GroupId == source.GroupId) continue;
                Rectangle b = other.GetVisualContentBounds(other.Bounds);
                targetX.Add(b.Left); targetX.Add(b.Left + b.Width / 2); targetX.Add(b.Right);
                targetY.Add(b.Top); targetY.Add(b.Top + b.Height / 2); targetY.Add(b.Bottom);
            }

            int[] sourceX = new int[] { sourceVisual.Left, sourceVisual.Left + sourceVisual.Width / 2, sourceVisual.Right };
            int[] sourceY = new int[] { sourceVisual.Top, sourceVisual.Top + sourceVisual.Height / 2, sourceVisual.Bottom };
            foreach (int sx in sourceX)
                foreach (int tx in targetX)
                {
                    int d = tx - sx; int ad = Math.Abs(d);
                    if (ad <= sensitivity && ad < bestDx) { bestDx = ad; snapDx = d; }
                }
            foreach (int sy in sourceY)
                foreach (int ty in targetY)
                {
                    int d = ty - sy; int ad = Math.Abs(d);
                    if (ad <= sensitivity && ad < bestDy) { bestDy = ad; snapDy = d; }
                }
            candidate.Offset(snapDx, snapDy);
            return candidate;
        }

        private void EnsureOverlaysOnScreen(bool announce)
        {
            bool changed = false;
            foreach (OverlayItemForm f in items)
            {
                if (f == null || f.IsDisposed) continue;
                Rectangle old = f.Bounds;
                bool visibleEnough = false;
                foreach (Screen screen in Screen.AllScreens)
                {
                    Rectangle intersection = Rectangle.Intersect(old, screen.WorkingArea);
                    if (intersection.Width >= 30 && intersection.Height >= 30) { visibleEnough = true; break; }
                }
                if (visibleEnough) continue;
                Rectangle next = NormalizeBounds(old);
                if (next != old) { f.Bounds = next; changed = true; }
            }
            if (changed)
            {
                ApplyZOrder(); SaveConfig();
                if (announce) SetStatus("화면 밖 오버레이를 현재 모니터 안으로 자동 복구했습니다.");
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            try
            {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke((MethodInvoker)delegate { EnsureOverlaysOnScreen(true); });
            }
            catch { }
        }

        public Rectangle NormalizeBounds(Rectangle bounds)
        {
            if (bounds.Width < 100) bounds.Width = 100;
            if (bounds.Height < 60) bounds.Height = 60;
            Screen target = null;
            foreach (Screen screen in Screen.AllScreens)
            {
                Rectangle wa = screen.WorkingArea;
                Rectangle center = new Rectangle(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, 1, 1);
                if (wa.IntersectsWith(center)) { target = screen; break; }
            }
            if (target == null) target = Screen.PrimaryScreen;
            Rectangle area = target.WorkingArea;
            if (bounds.X > area.Right - 30 || bounds.Y > area.Bottom - 30 || bounds.Right < area.Left + 30 || bounds.Bottom < area.Top + 30)
            {
                bounds.X = area.Left + Math.Max(0, (area.Width - bounds.Width) / 2);
                bounds.Y = area.Top + Math.Max(0, (area.Height - bounds.Height) / 2);
            }
            return bounds;
        }

        private void LoadRelativePreset(int delta)
        {
            List<PresetListEntry> presets = GetPresetEntries();
            if (presets.Count == 0) { SetStatus("전환할 프리셋이 없습니다."); return; }
            int index = -1;
            for (int i = 0; i < presets.Count; i++)
                if (string.Equals(presets[i].Name, currentPresetName, StringComparison.CurrentCultureIgnoreCase)) { index = i; break; }
            if (index < 0) index = 0; else index = (index + delta + presets.Count) % presets.Count;
            try
            {
                CaptureUndo("프리셋 전환");
                string loadedName = LoadPresetFile(presets[index].Path);
                currentPresetName = loadedName;
                SetStatus("프리셋 전환: " + loadedName);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "LoadRelativePreset");
                SetStatus("프리셋 전환 실패: " + ex.Message);
            }
        }

        private const string StartupRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "CatLayer";
        private const string LegacyStartupValueName = "LightOverlay";

        private string GetInstalledExePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatLayer", "App", "CatLayer.exe");
        }

        private string GetInstalledUninstallerPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatLayer", "App", "Uninstall.exe");
        }

        private string GetStartupTargetPath()
        {
            string installed = GetInstalledExePath();
            return File.Exists(installed) ? installed : Application.ExecutablePath;
        }

        private void MigrateLegacyStartupIfNeeded()
        {
            try
            {
                string installed = GetInstalledExePath();
                if (!File.Exists(installed)) return;
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupRunKey))
                {
                    if (key == null || key.GetValue(StartupValueName) != null) return;
                    object legacy = key.GetValue(LegacyStartupValueName);
                    if (legacy == null) return;
                    key.SetValue(StartupValueName, "\"" + installed + "\"", RegistryValueKind.String);
                    key.DeleteValue(LegacyStartupValueName, false);
                }
            }
            catch (Exception ex) { CrashLog.Write(ex, "MigrateLegacyStartup"); }
        }

        private bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRunKey, false))
                {
                    if (key == null) return false;
                    string value = key.GetValue(StartupValueName, "") as string;
                    if (string.IsNullOrWhiteSpace(value)) return false;
                    string target = GetStartupTargetPath();
                    string normalized = value.Trim().Trim('"');
                    return string.Equals(Path.GetFullPath(normalized), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        private void SetStartupEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupRunKey))
                {
                    if (key == null) throw new InvalidOperationException("시작프로그램 레지스트리를 열 수 없습니다.");
                    if (enabled)
                    {
                        string target = GetStartupTargetPath();
                        key.SetValue(StartupValueName, "\"" + target + "\"", RegistryValueKind.String);
                        SetStatus("컴퓨터 시작 시 실행: ON");
                    }
                    else
                    {
                        key.DeleteValue(StartupValueName, false);
                        SetStatus("컴퓨터 시작 시 실행: OFF");
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "SetStartupEnabled");
                SetStatus("시작 시 실행 설정 실패: " + ex.Message);
                MessageBox.Show(this, "시작 시 실행 설정을 변경하지 못했습니다.\n\n" + ex.Message,
                    "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void RunInstalledUninstaller()
        {
            string path = GetInstalledUninstallerPath();
            if (!File.Exists(path))
            {
                MessageBox.Show(this,
                    "설치된 Uninstall.exe를 찾지 못했습니다.\n\nINSTALL.bat을 다시 실행해 설치를 복구해 주세요.\n\n예상 위치:\n" + path,
                    "CatLayer 제거", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "RunInstalledUninstaller");
                MessageBox.Show(this, "제거 프로그램을 실행하지 못했습니다.\n\n" + ex.Message,
                    "CatLayer 제거", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenPresetFolder()
        {
            try
            {
                Directory.CreateDirectory(presetsDir);
                Process.Start("explorer.exe", "\"" + presetsDir + "\"");
                SetStatus("프리셋 폴더 열기: " + presetsDir);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "OpenPresetFolder");
                MessageBox.Show(this, "프리셋 폴더를 열지 못했습니다.\n\n" + presetsDir + "\n\n" + ex.Message,
                    "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowBridgeHelp()
        {
            string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "obs", "CatLayer_OBS_Bridge.lua");
            if (!File.Exists(script)) script = Path.Combine(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory).FullName, "obs", "CatLayer_OBS_Bridge.lua");
            string msg = "OBS > 도구 > 스크립트 > + 버튼에서\nCatLayer_OBS_Bridge.lua 를 한 번 추가하세요.\n\n그 뒤부터 '+ OBS Program' 버튼이 OBS의 Program 창 프로젝터를 자동 생성합니다.\n\n스크립트 위치:\n" + script;
            MessageBox.Show(this, msg, "OBS Bridge setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            try { if (File.Exists(script)) Process.Start("explorer.exe", "/select,\"" + script + "\""); } catch { }
        }

        private void InstallShortcuts()
        {
            try
            {
                string exe = Application.ExecutablePath;
                string work = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                CreateShortcut(Path.Combine(desktop, "CatLayer.lnk"), exe, work);
                CreateShortcut(Path.Combine(programs, "CatLayer.lnk"), exe, work);
                MessageBox.Show(this, "바탕화면과 시작 메뉴 바로가기를 만들었습니다.\n\n작업 표시줄은 실행 중인 CatLayer 아이콘을 우클릭해서 '작업 표시줄에 고정'을 선택하세요.", "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "바로가기 만들기 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void CreateShortcut(string linkPath, string targetPath, string workingDirectory)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("Windows Script Host를 사용할 수 없습니다.");
            object shell = null;
            object shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
                shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath + ",0" });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                try { if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut); } catch { }
                try { if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); } catch { }
            }
        }

        private static bool IsSupportedImageDropFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".webp";
        }

        private static bool IsStaticImageDropFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".webp";
        }

        private static bool IsGifUrl(string url)
        {
            try
            {
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
                return string.Equals(Path.GetExtension(uri.AbsolutePath), ".gif", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string NormalizeHttpUrlText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string clean = value.Replace("\0", "").Trim();
            int lineEnd = clean.IndexOfAny(new char[] { '\r', '\n' });
            if (lineEnd >= 0) clean = clean.Substring(0, lineEnd).Trim();
            try { clean = WebUtility.HtmlDecode(clean); } catch { }
            Uri uri;
            if (Uri.TryCreate(clean, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return uri.AbsoluteUri;
            return null;
        }

        private static string ReadDataText(IDataObject data, string format)
        {
            try
            {
                if (data == null || !data.GetDataPresent(format)) return null;
                object raw = data.GetData(format);
                string text = raw as string;
                if (text != null) return text.Replace("\0", "").Trim();
                byte[] bytesDirect = raw as byte[];
                if (bytesDirect != null)
                {
                    string direct = Encoding.UTF8.GetString(bytesDirect).Replace("\0", "").Trim();
                    if (!string.IsNullOrEmpty(direct)) return direct;
                }
                Stream stream = raw as Stream;
                if (stream != null)
                {
                    long oldPosition = 0; bool restore = false;
                    try { if (stream.CanSeek) { oldPosition = stream.Position; stream.Position = 0; restore = true; } } catch { }
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buffer = new byte[4096]; int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0 && ms.Length < 262144) ms.Write(buffer, 0, read);
                        try { if (restore) stream.Position = oldPosition; } catch { }
                        byte[] bytes = ms.ToArray();
                        if (bytes.Length == 0) return null;
                        Encoding encoding = format.EndsWith("W", StringComparison.OrdinalIgnoreCase) ? Encoding.Unicode : Encoding.UTF8;
                        string decoded = encoding.GetString(bytes).Replace("\0", "").Trim();
                        if (!string.IsNullOrEmpty(decoded)) return decoded;
                        if (encoding != Encoding.Unicode) return Encoding.Default.GetString(bytes).Replace("\0", "").Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        private static string ReadUrlDataFormat(IDataObject data, string format)
        {
            string text = ReadDataText(data, format);
            if (string.IsNullOrWhiteSpace(text)) return null;
            string normalized = NormalizeHttpUrlText(text);
            if (!string.IsNullOrEmpty(normalized)) return normalized;

            // Chromium may expose: mime-type:filename:https://actual-url
            if (string.Equals(format, "DownloadURL", StringComparison.OrdinalIgnoreCase))
            {
                Match m = Regex.Match(text, "^[^:\\r\\n]+:[^:\\r\\n]*:(?<u>https?://.+)$", RegexOptions.IgnoreCase);
                if (m.Success) return NormalizeHttpUrlText(m.Groups["u"].Value);
            }

            // text/uri-list can contain comments followed by one or more URLs.
            foreach (string line in text.Replace("\r", "").Split('\n'))
            {
                string item = line.Trim();
                if (item.Length == 0 || item.StartsWith("#")) continue;
                normalized = NormalizeHttpUrlText(item);
                if (!string.IsNullOrEmpty(normalized)) return normalized;
            }
            return null;
        }

        private static void AddUniqueUrl(List<string> urls, string value, string baseUrl)
        {
            if (urls == null || string.IsNullOrWhiteSpace(value)) return;
            string clean = value.Trim().Trim('"', '\'');
            try { clean = WebUtility.HtmlDecode(clean); } catch { }
            string normalized = NormalizeHttpUrlText(clean);
            if (string.IsNullOrEmpty(normalized) && !string.IsNullOrEmpty(baseUrl))
            {
                try
                {
                    Uri baseUri; Uri resolved;
                    if (Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri) && Uri.TryCreate(baseUri, clean, out resolved) &&
                        (resolved.Scheme == Uri.UriSchemeHttp || resolved.Scheme == Uri.UriSchemeHttps)) normalized = resolved.AbsoluteUri;
                }
                catch { }
            }
            if (string.IsNullOrEmpty(normalized)) return;
            foreach (string old in urls) if (string.Equals(old, normalized, StringComparison.OrdinalIgnoreCase)) return;
            urls.Add(normalized);
        }

        private static List<string> ExtractHttpImageUrls(IDataObject data)
        {
            List<string> urls = new List<string>();
            if (data == null) return urls;
            string sourceUrl = null;
            try
            {
                string html = ReadDataText(data, DataFormats.Html);
                if (!string.IsNullOrEmpty(html))
                {
                    Match source = Regex.Match(html, "(?:^|[\\r\\n])SourceURL:(?<b>https?://[^\\r\\n]+)", RegexOptions.IgnoreCase);
                    if (source.Success) sourceUrl = NormalizeHttpUrlText(source.Groups["b"].Value.Trim());

                    // Prefer actual IMG resource attributes before surrounding links.
                    MatchCollection imgs = Regex.Matches(html, "<img\\b[^>]*>", RegexOptions.IgnoreCase);
                    foreach (Match img in imgs)
                    {
                        string tag = img.Value;
                        string[] attrs = new string[] { "src", "data-src", "data-original", "data-lazy-src", "data-image-url" };
                        foreach (string attr in attrs)
                        {
                            Match a = Regex.Match(tag, "\\b" + Regex.Escape(attr) + "\\s*=\\s*[\\\"'](?<u>[^\\\"']+)[\\\"']", RegexOptions.IgnoreCase);
                            if (a.Success) AddUniqueUrl(urls, a.Groups["u"].Value, sourceUrl);
                        }
                        Match srcset = Regex.Match(tag, "\\bsrcset\\s*=\\s*[\\\"'](?<u>[^\\\"']+)[\\\"']", RegexOptions.IgnoreCase);
                        if (srcset.Success)
                        {
                            foreach (string part in srcset.Groups["u"].Value.Split(','))
                            {
                                string candidate = part.Trim();
                                int space = candidate.IndexOfAny(new char[] { ' ', '\t' });
                                if (space > 0) candidate = candidate.Substring(0, space);
                                AddUniqueUrl(urls, candidate, sourceUrl);
                            }
                        }
                    }

                    // Some sites put the dragged image URL in meta or link attributes.
                    MatchCollection metas = Regex.Matches(html, "<(?:meta|link)\\b[^>]*(?:content|href)\\s*=\\s*[\\\"'](?<u>https?://[^\\\"']+)[\\\"'][^>]*>", RegexOptions.IgnoreCase);
                    foreach (Match m in metas) AddUniqueUrl(urls, m.Groups["u"].Value, sourceUrl);
                }
            }
            catch { }

            string[] formats = new string[]
            {
                "DownloadURL", "text/uri-list", "text/x-moz-url-data", "text/x-moz-url",
                "UniformResourceLocatorW", "UniformResourceLocator", DataFormats.UnicodeText, DataFormats.Text
            };
            foreach (string format in formats)
            {
                string url = ReadUrlDataFormat(data, format);
                AddUniqueUrl(urls, url, sourceUrl);
            }
            return urls;
        }

        private static string ExtractHttpImageUrl(IDataObject data)
        {
            List<string> urls = ExtractHttpImageUrls(data);
            return urls.Count > 0 ? urls[0] : null;
        }

        private static string DescribeDataFormats(IDataObject data)
        {
            try
            {
                if (data == null) return "(none)";
                string[] formats = data.GetFormats(false);
                if (formats == null || formats.Length == 0) formats = data.GetFormats();
                if (formats == null || formats.Length == 0) return "(none)";
                string joined = string.Join(", ", formats);
                return joined.Length > 1200 ? joined.Substring(0, 1200) + "..." : joined;
            }
            catch { return "(unavailable)"; }
        }

        internal void HandleExternalOverlayDragEnter(DragEventArgs e)
        {
            ImageDropDragEnter(null, e);
        }

        internal void HandleExternalOverlayDragDrop(DragEventArgs e)
        {
            ImageDropDragDrop(null, e);
        }

        private void EnableImageDropRecursive(Control root)
        {
            if (root == null) return;
            try
            {
                root.AllowDrop = true;
                root.DragEnter += ImageDropDragEnter;
                root.DragOver += ImageDropDragEnter;
                root.DragDrop += ImageDropDragDrop;
            }
            catch { }
            foreach (Control child in root.Controls) EnableImageDropRecursive(child);
        }

        private void ImageDropDragEnter(object sender, DragEventArgs e)
        {
            if (object.ReferenceEquals(sender, overlayList) && IsOverlayListInternalDrag(e)) return;
            e.Effect = DragDropEffects.None;
            try
            {
                List<string> webUrls = ExtractHttpImageUrls(e.Data);
                bool webOrigin = webUrls.Count > 0;
                bool allGifUrls = webOrigin;
                foreach (string url in webUrls) if (!IsGifUrl(url)) { allGifUrls = false; break; }
                if (allGifUrls) return;

                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (paths != null)
                    {
                        foreach (string path in paths)
                        {
                            if (IsPresetFile(path) || IsGroupFile(path) || IsWebPackageFile(path) || IsLocalHtmlFile(path))
                            {
                                e.Effect = DragDropEffects.Copy;
                                return;
                            }
                            if ((webOrigin ? IsStaticImageDropFile(path) : IsSupportedImageDropFile(path))) { e.Effect = DragDropEffects.Copy; return; }
                        }
                    }
                }
                if (e.Data.GetDataPresent(DataFormats.Bitmap)) { e.Effect = DragDropEffects.Copy; return; }
                if (HasVirtualFileContents(e.Data)) { e.Effect = DragDropEffects.Copy; return; }
                if (webOrigin) e.Effect = DragDropEffects.Copy;
            }
            catch { e.Effect = DragDropEffects.None; }
        }

        private bool HasVirtualFileContents(IDataObject data)
        {
            try { return data != null && data.GetDataPresent("FileContents"); }
            catch { return false; }
        }

        private string TryImportVirtualStaticImage(IDataObject data)
        {
            if (data == null) return null;
            try
            {
                object raw = null;
                try { if (data.GetDataPresent("FileContents")) raw = data.GetData("FileContents"); } catch { }
                Stream stream = raw as Stream;
                byte[] bytes = raw as byte[];
                if (stream == null && bytes == null) return null;

                using (MemoryStream ms = new MemoryStream())
                {
                    if (bytes != null)
                    {
                        if (bytes.Length > 25 * 1024 * 1024) { SetStatus("웹 이미지는 최대 25MB까지 지원합니다."); return null; }
                        ms.Write(bytes, 0, bytes.Length);
                    }
                    else
                    {
                        long oldPosition = 0; bool restore = false;
                        try { if (stream.CanSeek) { oldPosition = stream.Position; stream.Position = 0; restore = true; } } catch { }
                        byte[] buffer = new byte[81920]; int total = 0, read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            total += read;
                            if (total > 25 * 1024 * 1024) { SetStatus("웹 이미지는 최대 25MB까지 지원합니다."); return null; }
                            ms.Write(buffer, 0, read);
                        }
                        try { if (restore) stream.Position = oldPosition; } catch { }
                    }
                    byte[] rasterBytes = ms.ToArray();
                    string imported = SaveManagedStaticImageBytes(rasterBytes, LooksLikeWebP(rasterBytes) ? ".webp" : "");
                    if (string.IsNullOrEmpty(imported)) SetStatus("웹 이미지 형식을 읽지 못했습니다.");
                    return imported;
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "TryImportVirtualStaticImage");
                return null;
            }
        }

        private Point DefaultImageInsertPoint(int x, int y)
        {
            int baseX = 200, baseY = 200;
            try
            {
                Point point = new Point(x, y);
                Rectangle screen = Screen.FromPoint(point).WorkingArea;
                baseX = Math.Max(screen.Left, Math.Min(point.X - 210, screen.Right - 420));
                baseY = Math.Max(screen.Top, Math.Min(point.Y - 150, screen.Bottom - 300));
            }
            catch { }
            return new Point(baseX, baseY);
        }

        private string SaveManagedStaticImage(Image source)
        {
            try
            {
                if (source == null) return null;
                Directory.CreateDirectory(assetsDir);
                string dest = Path.Combine(assetsDir, "img_" + Guid.NewGuid().ToString("N") + ".png");
                using (Bitmap copy = new Bitmap(source)) copy.Save(dest, ImageFormat.Png);
                return dest;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "SaveManagedStaticImage");
                SetStatus("이미지 저장 실패: " + ex.Message);
                return null;
            }
        }

        private static bool LooksLikeWebP(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
                bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';
        }

        private string SaveManagedStaticImageBytes(byte[] bytes, string extensionHint)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                using (MemoryStream ms = new MemoryStream(bytes, false))
                using (Image image = Image.FromStream(ms, true, true))
                {
                    if (ImageAnimator.CanAnimate(image) || image.RawFormat.Guid == ImageFormat.Gif.Guid)
                    {
                        SetStatus("웹 GIF/애니메이션 이미지는 지원하지 않습니다.");
                        return null;
                    }
                    return SaveManagedStaticImage(image);
                }
            }
            catch
            {
                string ext = (extensionHint ?? "").ToLowerInvariant();
                if (LooksLikeWebP(bytes)) ext = ".webp";
                if (ext != ".webp") return null;
                string temp = Path.Combine(Path.GetTempPath(), "catlayer_webp_" + Guid.NewGuid().ToString("N") + ".webp");
                try
                {
                    File.WriteAllBytes(temp, bytes);
                    using (Image image = OverlayItemForm.LoadRasterImageFile(temp))
                    {
                        if (image == null) return null;
                        return SaveManagedStaticImage(image);
                    }
                }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            }
        }

        private string DownloadStaticWebImage(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (IsGifUrl(url)) { SetStatus("웹 GIF는 드래그/붙여넣기를 지원하지 않습니다."); return null; }
            try
            {
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = "GET"; request.AllowAutoRedirect = true; request.Timeout = 6000; request.ReadWriteTimeout = 6000; request.UserAgent = "CatLayer/" + AppInfo.Version;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    string contentType = (response.ContentType ?? "").ToLowerInvariant();
                    if (contentType.Contains("image/gif")) { SetStatus("웹 GIF는 드래그/붙여넣기를 지원하지 않습니다."); return null; }
                    if (!contentType.StartsWith("image/")) { SetStatus("드래그한 주소가 이미지가 아닙니다."); return null; }
                    const int maxBytes = 25 * 1024 * 1024;
                    if (response.ContentLength > maxBytes) { SetStatus("웹 이미지는 최대 25MB까지 지원합니다."); return null; }
                    using (Stream input = response.GetResponseStream())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buffer = new byte[81920]; int total = 0, read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            total += read; if (total > maxBytes) { SetStatus("웹 이미지는 최대 25MB까지 지원합니다."); return null; }
                            ms.Write(buffer, 0, read);
                        }
                        byte[] rasterBytes = ms.ToArray();
                        string extHint = contentType.Contains("webp") ? ".webp" : Path.GetExtension(uri.AbsolutePath);
                        string imported = SaveManagedStaticImageBytes(rasterBytes, extHint);
                        if (string.IsNullOrEmpty(imported)) SetStatus("웹 이미지 형식을 읽지 못했습니다.");
                        return imported;
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "DownloadStaticWebImage");
                SetStatus("웹 이미지 가져오기 실패: " + ex.Message);
                return null;
            }
        }

        private void AddManagedImagesAt(List<string> managedFiles, Point point, string undoReason, string statusText)
        {
            AddManagedImagesAt(managedFiles, null, point, undoReason, statusText);
        }

        private void AddManagedImagesAt(List<string> managedFiles, List<string> customNames, Point point, string undoReason, string statusText)
        {
            if (managedFiles == null || managedFiles.Count == 0) return;
            CaptureUndo(undoReason);
            for (int i = 0; i < managedFiles.Count; i++)
            {
                int offset = Math.Min(i, 12) * 22;
                string customName = (customNames != null && i < customNames.Count) ? (customNames[i] ?? "") : "";
                CreateItem(ItemType.Image, managedFiles[i], 0, new Rectangle(point.X + offset, point.Y + offset, 420, 300), 100, TimerMode.OneShot, "", false, true, ImageScaleMode.Fit, true, customName);
            }
            SaveConfig();
            SetStatus(statusText + (managedFiles.Count > 1 ? " " + managedFiles.Count.ToString() + "개" : ""));
        }

        private void ImageDropDragDrop(object sender, DragEventArgs e)
        {
            if (object.ReferenceEquals(sender, overlayList) && IsOverlayListInternalDrag(e)) return;
            try
            {
                Point point = DefaultImageInsertPoint(e.X, e.Y);
                List<string> webUrls = ExtractHttpImageUrls(e.Data);
                bool webOrigin = webUrls.Count > 0;

                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (paths != null && paths.Length > 0)
                    {
                        if (paths.Length == 1 && IsLocalHtmlFile(paths[0]))
                        {
                            Rectangle webBounds = new Rectangle(point.X, point.Y, 800, 520);
                            if (AddLocalWebOverlay(paths[0], webBounds)) return;
                        }
                        if (paths.Length == 1 && IsWebPackageFile(paths[0]))
                        {
                            Rectangle webBounds = new Rectangle(point.X, point.Y, 800, 520);
                            if (AddCatLayerWebOverlay(paths[0], webBounds)) return;
                        }
                        if (paths.Length == 1 && (IsPresetFile(paths[0]) || IsGroupFile(paths[0])))
                        {
                            if (IsGroupFile(paths[0])) { if (LoadGroupFileAdditive(paths[0], true, "그룹 드래그 적용 완료: ")) return; }
                            else if (OpenPresetFileFromShell(paths[0], true, "프리셋 드래그 적용 완료: ")) return;
                        }
                        List<string> managedFiles = new List<string>(); List<string> customNames = new List<string>(); int unsupported = 0;
                        foreach (string path in paths)
                        {
                            if (!(webOrigin ? IsStaticImageDropFile(path) : IsSupportedImageDropFile(path))) { unsupported++; continue; }
                            string managed = ImportImageAsset(path); if (!string.IsNullOrEmpty(managed)) { managedFiles.Add(managed); customNames.Add(SuggestedImageNameFromPath(path)); }
                        }
                        if (managedFiles.Count > 0)
                        {
                            AddManagedImagesAt(managedFiles, customNames, point, managedFiles.Count == 1 ? "드래그 이미지 추가" : "드래그 이미지 여러 개 추가", "이미지/GIF 드래그 추가 완료");
                            if (unsupported > 0) SetStatus(managedFiles.Count.ToString() + "개 이미지/GIF 추가 완료  |  미지원 " + unsupported.ToString() + "개 제외");
                            return;
                        }
                    }
                }

                // Browser/Windows may provide the image pixels directly.
                if (e.Data.GetDataPresent(DataFormats.Bitmap))
                {
                    string managed = SaveManagedStaticImage(e.Data.GetData(DataFormats.Bitmap) as Image);
                    if (!string.IsNullOrEmpty(managed)) { AddManagedImagesAt(new List<string>(new string[] { managed }), new List<string>(new string[] { "웹 이미지" }), point, "웹 이미지 드래그 추가", "웹 이미지 드래그 추가 완료 (Bitmap)"); return; }
                }

                // Some browser drags arrive as a Windows virtual file rather than FileDrop/Bitmap.
                if (HasVirtualFileContents(e.Data))
                {
                    string managed = TryImportVirtualStaticImage(e.Data);
                    if (!string.IsNullOrEmpty(managed)) { AddManagedImagesAt(new List<string>(new string[] { managed }), new List<string>(new string[] { "웹 이미지" }), point, "웹 이미지 드래그 추가", "웹 이미지 드래그 추가 완료 (가상 파일)"); return; }
                }

                // Try every candidate URL found in CF_HTML/src/srcset/Chromium/Firefox URL formats.
                int tried = 0;
                foreach (string webUrl in webUrls)
                {
                    if (IsGifUrl(webUrl)) continue;
                    tried++;
                    string managed = DownloadStaticWebImage(webUrl);
                    if (!string.IsNullOrEmpty(managed))
                    {
                        AddManagedImagesAt(new List<string>(new string[] { managed }), new List<string>(new string[] { "웹 이미지" }), point, "웹 이미지 드래그 추가", "웹 이미지 드래그 추가 완료 (URL)");
                        return;
                    }
                    if (tried >= 8) break;
                }

                CrashLog.WriteText("WebImageDrag/Unsupported", "Formats: " + DescribeDataFormats(e.Data));
                if (webOrigin) SetStatus("웹 이미지 주소는 찾았지만 가져오지 못했습니다. crash.log를 확인해주세요.");
                else SetStatus("드롭 데이터에서 이미지를 찾지 못했습니다. crash.log에 형식을 기록했습니다.");
            }
            catch (Exception ex) { CrashLog.Write(ex, "ImageDropDragDrop"); SetStatus("드래그 이미지 추가 실패: " + ex.Message); }
        }

        private IDataObject GetClipboardDataObjectWithRetry()
        {
            for (int i = 0; i < 3; i++)
            {
                try { return Clipboard.GetDataObject(); }
                catch (ExternalException) { Thread.Sleep(35); }
                catch { return null; }
            }
            return null;
        }

        public bool HandlePasteShortcut(Keys keyData)
        {
            if (keyData != (Keys.Control | Keys.V)) return false;
            PasteImageFromClipboard();
            return true;
        }

        private void PasteImageFromClipboard()
        {
            IDataObject data = GetClipboardDataObjectWithRetry();
            if (data == null) { SetStatus("클립보드를 읽지 못했습니다. 잠시 후 다시 시도하세요."); return; }
            try
            {
                Point point = DefaultImageInsertPoint(Cursor.Position.X, Cursor.Position.Y);
                string webUrl = ExtractHttpImageUrl(data);
                if (IsGifUrl(webUrl)) { SetStatus("웹 GIF 붙여넣기는 지원하지 않습니다."); return; }
                if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] paths = data.GetData(DataFormats.FileDrop) as string[];
                    if (paths != null)
                    {
                        List<string> managedFiles = new List<string>();
                        List<string> customNames = new List<string>();
                        foreach (string path in paths)
                        {
                            if (!IsStaticImageDropFile(path)) continue;
                            string managed = ImportImageAsset(path);
                            if (!string.IsNullOrEmpty(managed))
                            {
                                managedFiles.Add(managed);
                                customNames.Add(SuggestedImageNameFromPath(path));
                            }
                        }
                        if (managedFiles.Count > 0) { AddManagedImagesAt(managedFiles, customNames, point, managedFiles.Count == 1 ? "이미지 붙여넣기" : "이미지 여러 개 붙여넣기", "Ctrl+V 이미지 붙여넣기 완료"); return; }
                    }
                }
                if (data.GetDataPresent(DataFormats.Bitmap))
                {
                    string managed = SaveManagedStaticImage(data.GetData(DataFormats.Bitmap) as Image);
                    if (!string.IsNullOrEmpty(managed)) { AddManagedImagesAt(new List<string>(new string[] { managed }), new List<string>(new string[] { "붙여넣은 이미지" }), point, "웹 이미지 붙여넣기", "Ctrl+V 이미지 붙여넣기 완료"); return; }
                }
                if (!string.IsNullOrEmpty(webUrl))
                {
                    string managed = DownloadStaticWebImage(webUrl);
                    if (!string.IsNullOrEmpty(managed)) { AddManagedImagesAt(new List<string>(new string[] { managed }), new List<string>(new string[] { "웹 이미지" }), point, "웹 이미지 붙여넣기", "Ctrl+V 웹 이미지 붙여넣기 완료"); return; }
                }
                SetStatus("클립보드에 붙여넣을 수 있는 정적 이미지가 없습니다.");
            }
            catch (Exception ex) { CrashLog.Write(ex, "PasteImageFromClipboard"); SetStatus("이미지 붙여넣기 실패: " + ex.Message); }
        }

        private void AddScreenRegionCapture()
        {
            Bitmap captured = null;
            Rectangle captureBounds = Rectangle.Empty;
            bool mainWasVisible = Visible;
            try
            {
                // Hide CatLayer itself before the desktop snapshot so the overlay is not captured into the result.
                Hide();
                foreach (OverlayItemForm f in items)
                {
                    try { if (f != null && !f.IsDisposed) f.Hide(); } catch { }
                }
                Application.DoEvents();
                Thread.Sleep(120);

                using (ScreenRegionCaptureForm capture = new ScreenRegionCaptureForm())
                {
                    DialogResult result = capture.ShowDialog();
                    if (result == DialogResult.OK && capture.CapturedImage != null && capture.CapturedScreenBounds.Width > 0 && capture.CapturedScreenBounds.Height > 0)
                    {
                        captured = (Bitmap)capture.CapturedImage.Clone();
                        captureBounds = capture.CapturedScreenBounds;
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "AddScreenRegionCapture");
                SetStatus("화면 영역 캡처 실패: " + ex.Message);
            }
            finally
            {
                if (mainWasVisible && !IsDisposed)
                {
                    try { Show(); WindowState = FormWindowState.Normal; Activate(); } catch { }
                }
                foreach (OverlayItemForm f in items)
                {
                    try { if (f != null && !f.IsDisposed) f.RefreshEffectiveVisibility(); } catch { }
                }
                try { ApplyZOrder(); } catch { }
            }

            if (captured == null)
            {
                SetStatus("화면 영역 캡처 취소");
                return;
            }

            try
            {
                using (captured)
                {
                    string managed = SaveManagedStaticImage(captured);
                    if (string.IsNullOrEmpty(managed)) return;
                    CaptureUndo("화면 영역 캡처");
                    CreateItem(ItemType.Image, managed, 0, captureBounds, 100, TimerMode.OneShot, "", false, true, ImageScaleMode.Fit, true, "영역 캡처 " + DateTime.Now.ToString("HHmmss"));
                    SaveConfig();
                    SetStatus("화면 영역 캡처 완료  |  " + captureBounds.Width.ToString() + " x " + captureBounds.Height.ToString());
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "AddScreenRegionCapture/CreateOverlay");
                SetStatus("캡처 이미지 오버레이 생성 실패: " + ex.Message);
            }
        }

        private static bool IsLocalHtmlFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            string ext = Path.GetExtension(path);
            return string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".htm", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetPrimaryWebPackageExtension() { return ".catlayerweb"; }

        private static bool IsWebPackageFile(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
                string.Equals(Path.GetExtension(path), GetPrimaryWebPackageExtension(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSafeWebRelativePath(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return false;
            string r = relative.Replace('\\', '/').TrimStart('/');
            if (Path.IsPathRooted(r) || r.IndexOf(':') >= 0) return false;
            string[] parts = r.Split('/');
            foreach (string p in parts) if (p == ".." || p == "." || string.IsNullOrWhiteSpace(p)) return false;
            return true;
        }

        private static bool TryParseLocalWebReference(string data, out string id, out string entry)
        {
            id = ""; entry = "";
            string value = (data ?? "").Trim();
            const string prefix = "catlayer-local://";
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = value.Substring(prefix.Length);
            int slash = rest.IndexOf('/');
            id = slash >= 0 ? rest.Substring(0, slash) : rest;
            entry = slash >= 0 ? rest.Substring(slash + 1) : "index.html";
            entry = entry.Replace('\\', '/').TrimStart('/');
            return Regex.IsMatch(id, @"^[a-fA-F0-9]{32}$") && IsSafeWebRelativePath(entry);
        }

        private static bool IsAllowedLocalWebAsset(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            string[] allowed = { ".html", ".htm", ".css", ".js", ".mjs", ".json", ".txt", ".xml", ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".woff", ".woff2", ".ttf", ".otf", ".mp3", ".wav", ".ogg", ".mp4", ".webm", ".vtt" };
            return Array.IndexOf(allowed, ext) >= 0;
        }

        private static bool IsRemoteOrUnsafeWebReference(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            string v = value.Trim().Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(v) || v.StartsWith("#") || v.StartsWith("//")) return true;
            string lower = v.ToLowerInvariant();
            return lower.StartsWith("http:") || lower.StartsWith("https:") || lower.StartsWith("data:") ||
                lower.StartsWith("blob:") || lower.StartsWith("javascript:") || lower.StartsWith("mailto:") ||
                lower.StartsWith("tel:") || lower.StartsWith("file:");
        }

        private static string StripWebReferenceSuffix(string value)
        {
            string v = (value ?? "").Trim().Trim('"', '\'');
            int hash = v.IndexOf('#'); if (hash >= 0) v = v.Substring(0, hash);
            int query = v.IndexOf('?'); if (query >= 0) v = v.Substring(0, query);
            try { v = Uri.UnescapeDataString(v); } catch { }
            return v.Trim();
        }

        private static IEnumerable<string> FindLocalWebReferences(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            MatchCollection attrs = Regex.Matches(text, "(?:src|href|poster)\\s*=\\s*[\"'](?<u>[^\"']+)[\"']", RegexOptions.IgnoreCase);
            foreach (Match m in attrs)
            {
                string u = m.Groups["u"].Value;
                if (!IsRemoteOrUnsafeWebReference(u) && seen.Add(u)) yield return u;
            }
            MatchCollection css = Regex.Matches(text, "url\\(\\s*[\"']?(?<u>[^\\)\"']+)[\"']?\\s*\\)|@import\\s+(?:url\\()?\\s*[\"'](?<i>[^\"']+)[\"']", RegexOptions.IgnoreCase);
            foreach (Match m in css)
            {
                string u = m.Groups["u"].Success ? m.Groups["u"].Value : m.Groups["i"].Value;
                if (!IsRemoteOrUnsafeWebReference(u) && seen.Add(u)) yield return u;
            }
            MatchCollection modules = Regex.Matches(text, "(?:from\\s*|import\\s*\\(\\s*)[\"'](?<u>\\.?\\.?/[^\"']+)[\"']", RegexOptions.IgnoreCase);
            foreach (Match m in modules)
            {
                string u = m.Groups["u"].Value;
                if (!IsRemoteOrUnsafeWebReference(u) && seen.Add(u)) yield return u;
            }
            MatchCollection srcsets = Regex.Matches(text, "srcset\\s*=\\s*[\"'](?<u>[^\"']+)[\"']", RegexOptions.IgnoreCase);
            foreach (Match m in srcsets)
            {
                foreach (string part in m.Groups["u"].Value.Split(','))
                {
                    string[] srcsetParts = part.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    string u = srcsetParts.Length > 0 ? srcsetParts[0] : "";
                    if (!IsRemoteOrUnsafeWebReference(u) && seen.Add(u)) yield return u;
                }
            }
        }

        private string ImportLocalWebEntry(string entryFile)
        {
            if (!IsLocalHtmlFile(entryFile)) return "";
            string sourceRoot = Path.GetDirectoryName(Path.GetFullPath(entryFile));
            string rootFull = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string id = Guid.NewGuid().ToString("N");
            string targetRoot = Path.Combine(webFilesDir, id);
            const int maxFiles = 512;
            const long maxTotalBytes = 48L * 1024L * 1024L;
            try
            {
                Directory.CreateDirectory(targetRoot);
                Queue<string> queue = new Queue<string>();
                HashSet<string> queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string entryFull = Path.GetFullPath(entryFile);
                queue.Enqueue(entryFull); queued.Add(entryFull);
                int copiedCount = 0; long totalBytes = 0;

                while (queue.Count > 0)
                {
                    string source = queue.Dequeue();
                    string full;
                    try { full = Path.GetFullPath(source); } catch { continue; }
                    if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) && !string.Equals(full, rootFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(full) || !IsAllowedLocalWebAsset(full)) continue;
                    FileInfo fi;
                    try { fi = new FileInfo(full); } catch { continue; }
                    if (fi.Length > 16L * 1024L * 1024L) continue;
                    if (++copiedCount > maxFiles) throw new InvalidDataException("로컬 웹 리소스가 너무 많습니다. (최대 512개)");
                    totalBytes += fi.Length;
                    if (totalBytes > maxTotalBytes) throw new InvalidDataException("로컬 웹 리소스 전체가 48MB를 초과합니다.");

                    string relative = full.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string destination = Path.Combine(targetRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(full, destination, true);

                    string ext = (Path.GetExtension(full) ?? "").ToLowerInvariant();
                    if (ext == ".html" || ext == ".htm" || ext == ".css" || ext == ".js" || ext == ".mjs")
                    {
                        string text;
                        try { text = File.ReadAllText(full, Encoding.UTF8); }
                        catch { try { text = File.ReadAllText(full, Encoding.Default); } catch { continue; } }
                        string baseDir = Path.GetDirectoryName(full);
                        foreach (string rawRef in FindLocalWebReferences(text))
                        {
                            string relRef = StripWebReferenceSuffix(rawRef);
                            if (string.IsNullOrWhiteSpace(relRef) || Path.IsPathRooted(relRef)) continue;
                            string candidate;
                            try { candidate = Path.GetFullPath(Path.Combine(baseDir, relRef.Replace('/', Path.DirectorySeparatorChar))); } catch { continue; }
                            if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;
                            if (File.Exists(candidate) && IsAllowedLocalWebAsset(candidate) && queued.Add(candidate)) queue.Enqueue(candidate);
                        }
                    }
                }

                string entryRelative = entryFull.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                string copiedEntry = Path.Combine(targetRoot, entryRelative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(copiedEntry)) throw new IOException("HTML 시작 파일을 복사하지 못했습니다.");
                return "catlayer-local://" + id + "/" + entryRelative;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "ImportLocalWebEntry");
                SetStatus("로컬 HTML 가져오기 실패: " + ex.Message);
                try { if (Directory.Exists(targetRoot)) Directory.Delete(targetRoot, true); } catch { }
                return "";
            }
        }

        private string ImportCatLayerWebPackage(string packagePath, out string packageName)
        {
            packageName = Path.GetFileNameWithoutExtension(packagePath ?? "");
            if (!IsWebPackageFile(packagePath)) return "";
            try
            {
                FileInfo fi = new FileInfo(packagePath);
                if (fi.Length > 64L * 1024L * 1024L) throw new InvalidDataException("CatLayerWeb 파일이 너무 큽니다. (최대 64MB)");
                string[] lines = File.ReadAllLines(packagePath, Encoding.UTF8);
                if (lines.Length < 3 || lines[0].Trim() != "CATLAYER_WEB_V1") throw new InvalidDataException("지원하지 않는 CatLayerWeb 파일입니다.");
                string entry = "index.html";
                List<Tuple<string, byte[]>> files = new List<Tuple<string, byte[]>>();
                long totalBytes = 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.StartsWith("NAME="))
                    {
                        try { packageName = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(5))).Trim(); } catch { }
                        continue;
                    }
                    if (line.StartsWith("ENTRY="))
                    {
                        try { entry = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(6))).Replace('\\', '/').TrimStart('/'); } catch { entry = ""; }
                        continue;
                    }
                    if (!line.StartsWith("FILE|")) continue;
                    string[] p = line.Split(new char[] { '|' }, 3);
                    if (p.Length != 3) continue;
                    string rel;
                    byte[] bytes;
                    try { rel = Encoding.UTF8.GetString(Convert.FromBase64String(p[1])).Replace('\\', '/').TrimStart('/'); bytes = Convert.FromBase64String(p[2]); }
                    catch { continue; }
                    if (!IsSafeWebRelativePath(rel) || !IsAllowedLocalWebAsset(rel)) continue;
                    if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("CatLayerWeb 내부 단일 파일이 너무 큽니다. (최대 16MB)");
                    totalBytes += bytes.Length;
                    if (totalBytes > 48L * 1024L * 1024L) throw new InvalidDataException("CatLayerWeb 내부 데이터가 너무 큽니다. (최대 48MB)");
                    files.Add(Tuple.Create(rel, bytes));
                }
                if (!IsSafeWebRelativePath(entry) || !IsLocalHtmlFileName(entry)) throw new InvalidDataException("CatLayerWeb 시작 HTML이 올바르지 않습니다.");
                string id = Guid.NewGuid().ToString("N");
                string targetRoot = Path.Combine(webFilesDir, id);
                Directory.CreateDirectory(targetRoot);
                try
                {
                    foreach (Tuple<string, byte[]> item in files)
                    {
                        string dest = Path.GetFullPath(Path.Combine(targetRoot, item.Item1.Replace('/', Path.DirectorySeparatorChar)));
                        string rootFull = Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        File.WriteAllBytes(dest, item.Item2);
                    }
                    string entryPath = Path.Combine(targetRoot, entry.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(entryPath)) throw new InvalidDataException("CatLayerWeb 시작 HTML 파일이 없습니다.");
                    if (string.IsNullOrWhiteSpace(packageName)) packageName = Path.GetFileNameWithoutExtension(packagePath);
                    return "catlayer-local://" + id + "/" + entry;
                }
                catch
                {
                    try { if (Directory.Exists(targetRoot)) Directory.Delete(targetRoot, true); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "ImportCatLayerWebPackage");
                SetStatus("CatLayerWeb 불러오기 실패: " + ex.Message);
                return "";
            }
        }

        private static bool IsLocalHtmlFileName(string path)
        {
            string ext = (Path.GetExtension(path ?? "") ?? "").ToLowerInvariant();
            return ext == ".html" || ext == ".htm";
        }

        private bool AddCatLayerWebOverlay(string path, Rectangle? bounds)
        {
            string packageName;
            string data = ImportCatLayerWebPackage(path, out packageName);
            if (string.IsNullOrEmpty(data)) return false;
            CaptureUndo("CatLayerWeb 오버레이 추가");
            CreateItem(ItemType.Web, data, 0, bounds ?? new Rectangle(280, 180, 800, 520), 100, TimerMode.OneShot, "", false, false, ImageScaleMode.Fit, true,
                "웹 · " + (string.IsNullOrWhiteSpace(packageName) ? Path.GetFileNameWithoutExtension(path) : packageName));
            SaveConfig();
            SetStatus("CatLayerWeb 오버레이 추가됨  |  HTML/CSS/JS/리소스가 전용 WebFiles 폴더에 격리되었습니다.");
            return true;
        }

        public void ExportWebPackageInteractive(OverlayItemForm web)
        {
            if (web == null || web.Type != ItemType.Web) return;
            string id, entry;
            if (!TryParseLocalWebReference(web.Data, out id, out entry))
            {
                MessageBox.Show(this, "CatLayer에 가져온 로컬 HTML 웹만 CatLayerWeb 파일로 저장할 수 있습니다.", "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string root = Path.Combine(webFilesDir, id);
            if (!Directory.Exists(root)) { SetStatus("로컬 웹 폴더를 찾을 수 없습니다."); return; }
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "CatLayer Web|*.catlayerweb|All files|*.*";
                d.DefaultExt = "catlayerweb";
                d.AddExtension = true;
                string suggested = string.IsNullOrWhiteSpace(web.CustomName) ? "CatLayerWeb" : web.CustomName;
                suggested = suggested.Replace("웹 · ", "").Trim();
                d.FileName = MakeSafeFileName(suggested) + ".catlayerweb";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    List<string> lines = new List<string>();
                    lines.Add("CATLAYER_WEB_V1");
                    lines.Add("NAME=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(suggested)));
                    lines.Add("ENTRY=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(entry)));
                    long total = 0;
                    foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (!IsAllowedLocalWebAsset(file)) continue;
                        string rel = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                        if (!IsSafeWebRelativePath(rel)) continue;
                        byte[] bytes = File.ReadAllBytes(file);
                        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("단일 웹 리소스가 16MB를 초과합니다: " + rel);
                        total += bytes.Length;
                        if (total > 48L * 1024L * 1024L) throw new InvalidDataException("웹 리소스 전체가 48MB를 초과합니다.");
                        lines.Add("FILE|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(rel)) + "|" + Convert.ToBase64String(bytes));
                    }
                    File.WriteAllLines(d.FileName, lines.ToArray(), new UTF8Encoding(false));
                    SetStatus("CatLayerWeb 파일 저장 완료: " + Path.GetFileName(d.FileName));
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex, "ExportWebPackageInteractive");
                    MessageBox.Show(this, "CatLayerWeb 파일을 저장하지 못했습니다.\n\n" + ex.Message, "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private bool AddLocalWebOverlay(string path, Rectangle? bounds)
        {
            string data = ImportLocalWebEntry(path);
            if (string.IsNullOrEmpty(data)) return false;
            CaptureUndo("로컬 HTML 웹 오버레이 추가");
            CreateItem(ItemType.Web, data, 0, bounds ?? new Rectangle(280, 180, 800, 520), 100, TimerMode.OneShot, "", false, false, ImageScaleMode.Fit, true, "웹 · " + Path.GetFileName(path));
            SaveConfig();
            SetStatus("로컬 HTML 웹 오버레이 추가됨  |  전용 WebFiles 폴더로 격리됨");
            return true;
        }

        private static bool TryNormalizeWebAddress(string input, out string normalized)
        {
            normalized = "";
            string value = (input ?? "").Trim();
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (!Regex.IsMatch(value, @"^[a-zA-Z][a-zA-Z0-9+.-]*://")) value = "https://" + value;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            normalized = uri.AbsoluteUri;
            return true;
        }

        private void AddWebInteractive()
        {
            string input = null;
            using (Form f = new Form())
            using (TextBox box = new TextBox())
            using (Label label = new Label())
            using (Button browse = new Button())
            using (Button ok = new Button())
            using (Button cancel = new Button())
            {
                f.Text = "CatLayer - 웹 오버레이 추가";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(600, 145);
                f.MaximizeBox = false; f.MinimizeBox = false; f.BackColor = UiPanel; f.ForeColor = UiText;
                label.Text = "웹 주소 또는 로컬 HTML / CatLayerWeb 파일"; label.SetBounds(14, 14, 560, 22); label.ForeColor = UiMuted; f.Controls.Add(label);
                box.SetBounds(14, 42, 470, 28); box.Text = "https://"; box.BackColor = UiPanel2; box.ForeColor = UiText; f.Controls.Add(box);
                browse.Text = "파일 선택..."; browse.SetBounds(494, 41, 92, 30); StyleButton(browse, false); f.Controls.Add(browse);
                browse.Click += delegate
                {
                    using (OpenFileDialog d = new OpenFileDialog())
                    {
                        d.Filter = "Web files|*.html;*.htm;*.catlayerweb|HTML|*.html;*.htm|CatLayer Web|*.catlayerweb|All files|*.*";
                        if (d.ShowDialog(f) == DialogResult.OK) box.Text = d.FileName;
                    }
                };
                ok.Text = "추가"; ok.DialogResult = DialogResult.OK; ok.SetBounds(416, 96, 80, 32); StyleButton(ok, false); f.Controls.Add(ok);
                cancel.Text = "취소"; cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(506, 96, 80, 32); StyleButton(cancel, false); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog(this) != DialogResult.OK) return;
                input = box.Text;
            }
            if (string.IsNullOrWhiteSpace(input)) return;
            string localCandidate = input.Trim().Trim('"');
            if (IsLocalHtmlFile(localCandidate))
            {
                if (!AddLocalWebOverlay(localCandidate, null)) MessageBox.Show(this, "로컬 HTML을 가져오지 못했습니다.", "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (IsWebPackageFile(localCandidate))
            {
                if (!AddCatLayerWebOverlay(localCandidate, null)) MessageBox.Show(this, "CatLayerWeb 파일을 가져오지 못했습니다.", "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string url;
            if (!TryNormalizeWebAddress(input, out url))
            {
                MessageBox.Show(this, "HTTP/HTTPS 주소 또는 존재하는 .html/.htm/.catlayerweb 파일을 입력해 주세요.", "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string name = "웹";
            try { name = "웹 · " + new Uri(url).Host; } catch { }
            CaptureUndo("웹 오버레이 추가");
            CreateItem(ItemType.Web, url, 0, new Rectangle(280, 180, 800, 520), 100, TimerMode.OneShot, "", false, false, ImageScaleMode.Fit, true, name);
            SaveConfig();
            SetStatus("웹 오버레이 추가됨  |  F10 웹 조작 모드에서 페이지를 직접 조작할 수 있습니다.");
        }

        public void EditWebCustomCssInteractive(OverlayItemForm web)
        {
            if (web == null || web.Type != ItemType.Web) return;
            using (Form f = new Form())
            using (TextBox box = new TextBox())
            using (Button apply = new Button())
            using (Button clear = new Button())
            using (Button cancel = new Button())
            using (Label help = new Label())
            {
                f.Text = "CatLayer - 웹 커스텀 CSS";
                f.FormBorderStyle = FormBorderStyle.Sizable;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(650, 430);
                f.MinimumSize = new Size(520, 360);
                f.BackColor = UiPanel; f.ForeColor = UiText;
                help.Text = "선택한 웹 오버레이에만 적용됩니다. 페이지 이동/새로고침 후에도 다시 적용됩니다.";
                help.SetBounds(14, 12, 610, 24); help.ForeColor = UiMuted; help.BackColor = Color.Transparent; f.Controls.Add(help);
                box.Multiline = true; box.AcceptsReturn = true; box.AcceptsTab = true; box.ScrollBars = ScrollBars.Both;
                box.WordWrap = false; box.Font = new Font("Consolas", 10F); box.BackColor = UiPanel2; box.ForeColor = UiText;
                box.Text = web.WebCustomCss ?? ""; box.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
                box.SetBounds(14, 42, 622, 330); f.Controls.Add(box);
                apply.Text = "적용"; apply.DialogResult = DialogResult.OK; apply.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; apply.SetBounds(470, 386, 78, 30); StyleButton(apply, false); f.Controls.Add(apply);
                clear.Text = "초기화"; clear.Anchor = AnchorStyles.Bottom | AnchorStyles.Left; clear.SetBounds(14, 386, 78, 30); StyleButton(clear, false); clear.Click += delegate { box.Text = ""; }; f.Controls.Add(clear);
                cancel.Text = "취소"; cancel.DialogResult = DialogResult.Cancel; cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; cancel.SetBounds(558, 386, 78, 30); StyleButton(cancel, false); f.Controls.Add(cancel);
                f.AcceptButton = apply; f.CancelButton = cancel;
                if (f.ShowDialog(this) != DialogResult.OK) return;
                CaptureUndo("웹 커스텀 CSS 변경");
                web.SetWebCustomCss(box.Text, true);
                SetStatus(string.IsNullOrWhiteSpace(box.Text) ? "웹 커스텀 CSS 초기화 완료" : "웹 커스텀 CSS 적용 완료");
            }
        }

        public void ChangeWebUrlInteractive(OverlayItemForm web)
        {
            if (web == null || web.Type != ItemType.Web) return;
            string input = UiPrompt.AskText(this, "웹 주소 변경", "새 웹 주소", web.Data);
            if (input == null) return;
            string normalized;
            string localCandidate = input.Trim().Trim('\"');
            if (IsLocalHtmlFile(localCandidate))
            {
                normalized = ImportLocalWebEntry(localCandidate);
                if (string.IsNullOrEmpty(normalized))
                {
                    MessageBox.Show(this, "로컬 HTML을 가져오지 못했습니다.", "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            else if (IsWebPackageFile(localCandidate))
            {
                string packageName;
                normalized = ImportCatLayerWebPackage(localCandidate, out packageName);
                if (string.IsNullOrEmpty(normalized))
                {
                    MessageBox.Show(this, "CatLayerWeb 파일을 가져오지 못했습니다.", "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            else if (!TryNormalizeWebAddress(input, out normalized))
            {
                MessageBox.Show(this, "HTTP/HTTPS 주소 또는 존재하는 .html/.htm/.catlayerweb 파일 경로를 입력해 주세요.", "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            CaptureUndo("웹 주소 변경");
            if (!web.SetWebUrl(normalized, false))
            {
                SetStatus("웹 주소 변경 실패");
                return;
            }
            SaveConfig();
            RefreshMainUi();
            SetStatus("웹 주소 변경 완료: " + normalized);
        }

        private void AddImage()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string managed = ImportImageAsset(d.FileName);
                if (string.IsNullOrEmpty(managed)) return;
                CaptureUndo("이미지 추가");
                CreateItem(ItemType.Image, managed, 0, new Rectangle(200, 200, 420, 300), 100, TimerMode.OneShot, "", false, true, ImageScaleMode.Fit, true, SuggestedImageNameFromPath(d.FileName)); SaveConfig();
            }
        }
        private void AddText(string s) { CaptureUndo("텍스트 추가"); CreateItem(ItemType.Text, string.IsNullOrWhiteSpace(s) ? "Overlay Text" : s, 0, new Rectangle(240, 240, 420, 110), 100); SaveConfig(); }
        private void AddTimer(string s, TimerMode mode)
        {
            int seconds = mode == TimerMode.Stopwatch ? 0 : Math.Max(1, ParseSeconds(s));
            CaptureUndo("타이머 추가");
            CreateItem(ItemType.Timer, "", seconds, new Rectangle(260, 280, 320, 110), 100, mode, "");
            SaveConfig();
            string kind = mode == TimerMode.OneShot ? "1회성 타이머" : (mode == TimerMode.Repeat ? "반복 타이머" : "타임스톱");
            SetStatus(kind + " 추가됨" + (mode == TimerMode.Stopwatch ? "" : "  |  더블클릭: 재시작"));
        }
        private void AddObsProgram()
        {
            foreach (OverlayItemForm existing in items)
            {
                if (existing.Type == ItemType.ObsProgram)
                {
                    if (hidden) hidden = false;
                    existing.SetOverlayVisible(true);
                    foreach (OverlayItemForm f in items) f.RefreshEffectiveVisibility();
                    UpdateButtons();
                    MoveItemToFront(existing);
                    SaveConfig();
                    return;
                }
            }
            CaptureUndo("OBS 오버레이 추가");
            CreateItem(ItemType.ObsProgram, "OBS Program", 0, new Rectangle(300, 300, 640, 360), 100);
            SaveConfig();
        }

        private static int ParseSeconds(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 60;
            string[] p = s.Trim().Split(':'); int[] v = new int[p.Length];
            for (int i = 0; i < p.Length; i++) if (!int.TryParse(p[i], out v[i])) return 60;
            if (p.Length == 1) return Math.Max(0, v[0]); if (p.Length == 2) return Math.Max(0, v[0] * 60 + v[1]); if (p.Length == 3) return Math.Max(0, v[0] * 3600 + v[1] * 60 + v[2]); return 60;
        }

        private void CreateItem(ItemType type, string data, int seconds, Rectangle bounds, int opacity)
        {
            CreateItem(type, data, seconds, bounds, opacity, TimerMode.OneShot, "", false, true, ImageScaleMode.Fit, true, "");
        }

        private void CreateItem(ItemType type, string data, int seconds, Rectangle bounds, int opacity, TimerMode timerMode, string alarmPath)
        {
            CreateItem(type, data, seconds, bounds, opacity, timerMode, alarmPath, false, true, ImageScaleMode.Fit, true, "");
        }

        private void CreateItem(ItemType type, string data, int seconds, Rectangle bounds, int opacity, TimerMode timerMode, string alarmPath, bool locked, bool preserveAspect, ImageScaleMode scaleMode)
        {
            CreateItem(type, data, seconds, bounds, opacity, timerMode, alarmPath, locked, preserveAspect, scaleMode, true, "");
        }

        private void CreateItem(ItemType type, string data, int seconds, Rectangle bounds, int opacity, TimerMode timerMode, string alarmPath, bool locked, bool preserveAspect, ImageScaleMode scaleMode, bool visible, string customName)
        {
            CreateItem(type, data, seconds, bounds, opacity, timerMode, alarmPath, locked, preserveAspect, scaleMode, visible, customName, 0, false, false, 0);
        }

        private void CreateItem(ItemType type, string data, int seconds, Rectangle bounds, int opacity, TimerMode timerMode, string alarmPath, bool locked, bool preserveAspect, ImageScaleMode scaleMode, bool visible, string customName, int rotationDegrees, bool flipHorizontal, bool flipVertical, int groupId)
        {
            CreateItem(type, data, seconds, bounds, opacity, timerMode, alarmPath, locked, preserveAspect, scaleMode, visible, customName, rotationDegrees, flipHorizontal, flipVertical, groupId, 0, 0, 0, 0);
        }

        private void CreateItem(ItemType type, string data, int seconds, Rectangle bounds, int opacity, TimerMode timerMode, string alarmPath, bool locked, bool preserveAspect, ImageScaleMode scaleMode, bool visible, string customName, int rotationDegrees, bool flipHorizontal, bool flipVertical, int groupId, int cropLeft, int cropTop, int cropRight, int cropBottom, int webZoomPercent = 100, string webCustomCss = "")
        {
            OverlayItemForm f = new OverlayItemForm(this, type, data, seconds, opacity, timerMode, alarmPath, customName);
            f.Bounds = NormalizeBounds(bounds);
            f.SetLocked(locked, false);
            f.SetPreserveAspect(preserveAspect, false);
            f.SetImageScaleMode(scaleMode, false);
            f.SetCrop(cropLeft, cropTop, cropRight, cropBottom, false);
            if (type == ItemType.Web) { f.SetWebZoomPercent(webZoomPercent, false); f.SetWebCustomCss(webCustomCss, false); }
            f.SetTransform(rotationDegrees, flipHorizontal, flipVertical, false);
            f.NormalizeFitBoundsToVisualContent();
            f.SetGroupId(groupId, false);
            if (groupId >= nextGroupId) nextGroupId = groupId + 1;
            items.Add(f);
            f.Show();
            f.SetEditMode(EditMode);
            f.SetOverlayVisible(visible);
            ApplyZOrder();
        }

        public void DeleteItem(OverlayItemForm f)
        {
            if (!items.Contains(f)) return;
            CaptureUndo("오버레이 삭제");
            string oldData = f.Data;
            string oldAlarm = f.AlarmPath;
            ItemType oldType = f.Type;
            items.Remove(f);
            f.Dispose();
            if (oldType == ItemType.Image) TryDeleteUnusedManagedAsset(oldData);
            if (oldType == ItemType.Timer) TryDeleteUnusedManagedSound(oldAlarm);
            ApplyZOrder();
            SaveConfig();
        }

        public void SelectTimerAlarmFile(OverlayItemForm timer)
        {
            if (timer == null || timer.Type != ItemType.Timer || timer.TimerKind == TimerMode.Stopwatch) return;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Audio (*.wav;*.mp3;*.wma)|*.wav;*.mp3;*.wma|WAV (*.wav)|*.wav|MP3 (*.mp3)|*.mp3|WMA (*.wma)|*.wma|All files (*.*)|*.*";
                d.Title = "타이머 알람 소리 선택";
                if (d.ShowDialog(this) != DialogResult.OK) return;

                string managed = ImportSoundAsset(d.FileName);
                if (string.IsNullOrEmpty(managed)) return;

                CaptureUndo("타이머 알람 변경");
                string old = timer.AlarmPath;
                timer.SetTimerAlarmPath(managed, false);
                TryDeleteUnusedManagedSound(old);
                SaveConfig();
                SetStatus("타이머 알람 변경됨: " + Path.GetFileName(d.FileName));
                AudioAlert.Play(managed);
            }
        }

        public void UseDefaultTimerAlarm(OverlayItemForm timer)
        {
            if (timer == null || timer.Type != ItemType.Timer || timer.TimerKind == TimerMode.Stopwatch) return;
            CaptureUndo("타이머 알람 기본값 변경");
            string old = timer.AlarmPath;
            timer.SetTimerAlarmPath("", false);
            TryDeleteUnusedManagedSound(old);
            SaveConfig();
            SetStatus("타이머 알람: 기본 귀여운 소리 ♫");
            AudioAlert.Play("");
        }

        public void MoveItemToFront(OverlayItemForm f) { MoveItem(f, items.Count - 1); }
        public void MoveItemToBack(OverlayItemForm f) { MoveItem(f, 0); }
        public void MoveItemForward(OverlayItemForm f)
        {
            int i = items.IndexOf(f); if (i >= 0 && i < items.Count - 1) MoveItem(f, i + 1);
        }
        public void MoveItemBackward(OverlayItemForm f)
        {
            int i = items.IndexOf(f); if (i > 0) MoveItem(f, i - 1);
        }
        private void MoveItem(OverlayItemForm f, int target)
        {
            int old = items.IndexOf(f); if (old < 0) return;
            target = Math.Max(0, Math.Min(items.Count - 1, target));
            if (old == target) return;
            CaptureUndo("오버레이 우선도 변경");
            items.RemoveAt(old);
            items.Insert(target, f);
            ApplyZOrder();
            SaveConfig();
        }
        public void ReapplyZOrder() { ApplyZOrder(); }
        private void ApplyZOrder()
        {
            // items[0] = back, last item = front. Re-applying HWND_TOPMOST in this order
            // keeps every overlay topmost while preserving the user's layer order.
            for (int i = 0; i < items.Count; i++)
            {
                OverlayItemForm f = items[i];
                if (!f.IsHandleCreated || f.IsDisposed) continue;
                Native.SetWindowPos(f.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            for (int i = 0; i < items.Count; i++)
            {
                OverlayItemForm f = items[i];
                if (f == null || f.IsDisposed) continue;
                f.BringSelectionFrameToFront();
            }
        }

        private string ImportImageAsset(string sourcePath)
        {
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    MessageBox.Show(this, "이미지 파일을 찾을 수 없습니다.", "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }
                Directory.CreateDirectory(assetsDir);
                string ext = Path.GetExtension(sourcePath);
                if (string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase))
                {
                    using (Image probe = OverlayItemForm.LoadRasterImageFile(sourcePath))
                    {
                        if (probe == null)
                        {
                            MessageBox.Show(this, "이 WebP 파일을 Windows 이미지 디코더가 읽지 못했습니다.\nWindows의 WebP 이미지 지원을 확인해주세요.", "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return null;
                        }
                        string webpDest = Path.Combine(assetsDir, "img_" + Guid.NewGuid().ToString("N") + ".png");
                        using (Bitmap copy = new Bitmap(probe)) copy.Save(webpDest, ImageFormat.Png);
                        return webpDest;
                    }
                }
                if (string.IsNullOrEmpty(ext) || ext.Length > 10) ext = ".img";
                string dest = Path.Combine(assetsDir, "img_" + Guid.NewGuid().ToString("N") + ext.ToLowerInvariant());
                File.Copy(sourcePath, dest, false);
                return dest;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "이미지 보관용 복사에 실패했습니다.\n\n" + ex.Message, "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
        }

        private string ImportSoundAsset(string sourcePath)
        {
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    SetStatus("알람 파일을 찾을 수 없습니다.");
                    return null;
                }

                string ext = (Path.GetExtension(sourcePath) ?? "").ToLowerInvariant();
                if (ext != ".wav" && ext != ".mp3" && ext != ".wma")
                {
                    SetStatus("알람은 WAV / MP3 / WMA 파일을 지원합니다.");
                    return null;
                }

                Directory.CreateDirectory(soundsDir);
                string dest = Path.Combine(soundsDir, "alarm_" + Guid.NewGuid().ToString("N") + ext);
                File.Copy(sourcePath, dest, false);
                return dest;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "ImportSoundAsset");
                SetStatus("알람 파일 복사 실패: " + ex.Message);
                return null;
            }
        }

        private bool IsManagedSound(string path)
        {
            try
            {
                string a = Path.GetFullPath(soundsDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string p = Path.GetFullPath(path ?? "");
                return p.StartsWith(a, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private void TryDeleteUnusedManagedSound(string path)
        {
            if (string.IsNullOrEmpty(path) || !IsManagedSound(path)) return;
            foreach (OverlayItemForm item in items)
                if (item.Type == ItemType.Timer && string.Equals(item.AlarmPath, path, StringComparison.OrdinalIgnoreCase)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private bool IsManagedAsset(string path)
        {
            try
            {
                string a = Path.GetFullPath(assetsDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string p = Path.GetFullPath(path ?? "");
                return p.StartsWith(a, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private void TryDeleteUnusedManagedAsset(string path)
        {
            if (!IsManagedAsset(path)) return;
            foreach (OverlayItemForm item in items)
                if (item.Type == ItemType.Image && string.Equals(item.Data, path, StringComparison.OrdinalIgnoreCase)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string GetPrimaryGroupExtension() { return ".catlayergroup"; }
        private static bool IsGroupFile(string path)
        {
            return string.Equals(Path.GetExtension(path ?? ""), GetPrimaryGroupExtension(), StringComparison.OrdinalIgnoreCase);
        }

        private List<OverlayItemForm> GetSelectedGroupItemsForSave()
        {
            List<OverlayItemForm> selected = SelectedOverlays;
            if (selected.Count == 0) return new List<OverlayItemForm>();

            int groupId = selected[0].GroupId;
            bool samePositiveGroup = groupId > 0;
            for (int i = 1; i < selected.Count && samePositiveGroup; i++)
                if (selected[i].GroupId != groupId) samePositiveGroup = false;
            if ((selected.Count == 1 && groupId > 0) || samePositiveGroup)
            {
                List<OverlayItemForm> wholeGroup = new List<OverlayItemForm>();
                foreach (OverlayItemForm item in items)
                    if (item != null && !item.IsDisposed && item.GroupId == groupId) wholeGroup.Add(item);
                return wholeGroup;
            }

            // Multi-selection can be exported as a group without changing the current live grouping.
            if (selected.Count >= 2)
            {
                HashSet<OverlayItemForm> set = new HashSet<OverlayItemForm>(selected);
                List<OverlayItemForm> ordered = new List<OverlayItemForm>();
                foreach (OverlayItemForm item in items) if (set.Contains(item)) ordered.Add(item);
                return ordered;
            }
            return new List<OverlayItemForm>();
        }

        internal void SaveSelectedGroupInteractive()
        {
            List<OverlayItemForm> groupItems = GetSelectedGroupItemsForSave();
            if (groupItems.Count == 0)
            {
                SetStatus("저장할 그룹이 없습니다. 그룹 하나를 선택하거나 2개 이상을 다중 선택하세요.");
                return;
            }
            string suggested = groupItems[0].GroupId > 0 ? "그룹 " + groupItems[0].GroupId.ToString() : "선택 그룹";
            string name = UiPrompt.AskText(this, "그룹 파일 저장", "공유할 그룹 이름", suggested);
            if (name == null) return;
            if (string.IsNullOrWhiteSpace(name)) name = suggested;
            try
            {
                Directory.CreateDirectory(groupsDir);
                string path = Path.Combine(groupsDir, MakeSafeFileName(name) + GetPrimaryGroupExtension());
                SaveGroupFile(path, name, groupItems);
                SetStatus("그룹 파일 저장 완료: " + name + "  |  기존 오버레이는 그대로 유지됩니다.");
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "SaveGroupFile");
                SetStatus("그룹 파일 저장 실패: " + ex.Message);
            }
        }

        internal void LoadGroupInteractive()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "CatLayer group|*.catlayergroup|All files|*.*";
                d.Multiselect = false;
                d.InitialDirectory = Directory.Exists(groupsDir) ? groupsDir : baseDataDir;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                LoadGroupFileAdditive(d.FileName, true, "그룹 불러오기 완료: ");
            }
        }

        private void OpenGroupFolder()
        {
            try
            {
                Directory.CreateDirectory(groupsDir);
                Process.Start("explorer.exe", "\"" + groupsDir + "\"");
                SetStatus("그룹 파일 폴더 열기: " + groupsDir);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "OpenGroupFolder");
                MessageBox.Show(this, "그룹 파일 폴더를 열지 못했습니다.\n\n" + groupsDir + "\n\n" + ex.Message,
                    "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SaveGroupFile(string path, string name, List<OverlayItemForm> groupItems)
        {
            if (groupItems == null || groupItems.Count == 0) throw new InvalidOperationException("저장할 그룹 항목이 없습니다.");
            int originX = int.MaxValue, originY = int.MaxValue;
            foreach (OverlayItemForm f in groupItems)
            {
                originX = Math.Min(originX, f.Bounds.Left);
                originY = Math.Min(originY, f.Bounds.Top);
            }
            if (originX == int.MaxValue) originX = 0;
            if (originY == int.MaxValue) originY = 0;

            List<string> lines = new List<string>();
            lines.Add("CATLAYER_GROUP_V1");
            lines.Add("NAME=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(name ?? "")));
            lines.Add("ORIGIN=" + originX.ToString() + "|" + originY.ToString());
            Dictionary<string, string> assetIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> soundIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int assetNo = 0, soundNo = 0;

            foreach (OverlayItemForm f in groupItems)
            {
                Rectangle r = f.Bounds;
                string payload;
                if (f.Type == ItemType.Image)
                {
                    if (!File.Exists(f.Data)) throw new FileNotFoundException("그룹에 포함할 이미지가 없습니다.", f.Data);
                    string id;
                    if (!assetIds.TryGetValue(f.Data, out id))
                    {
                        id = "A" + (++assetNo).ToString();
                        assetIds[f.Data] = id;
                        string ext = Path.GetExtension(f.Data) ?? "";
                        lines.Add("ASSET|" + id + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(ext)) + "|" + Convert.ToBase64String(File.ReadAllBytes(f.Data)));
                    }
                    payload = id;
                }
                else payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(f.Data ?? ""));

                string soundId = "";
                if (f.Type == ItemType.Timer && f.TimerKind != TimerMode.Stopwatch && !string.IsNullOrEmpty(f.AlarmPath) && File.Exists(f.AlarmPath))
                {
                    if (!soundIds.TryGetValue(f.AlarmPath, out soundId))
                    {
                        soundId = "S" + (++soundNo).ToString();
                        soundIds[f.AlarmPath] = soundId;
                        string ext = Path.GetExtension(f.AlarmPath) ?? ".wav";
                        lines.Add("SOUND|" + soundId + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(ext)) + "|" + Convert.ToBase64String(File.ReadAllBytes(f.AlarmPath)));
                    }
                }

                string customName64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(f.CustomName ?? ""));
                int durationSeconds = f.DurationSeconds;
                lines.Add("ITEM|" + ((int)f.Type).ToString() + "|" + payload + "|" + durationSeconds.ToString() + "|" +
                    (r.X - originX).ToString() + "|" + (r.Y - originY).ToString() + "|" + r.Width.ToString() + "|" + r.Height.ToString() + "|" + f.OpacityPercent.ToString() + "|" +
                    ((int)f.TimerKind).ToString() + "|" + soundId + "|" + (f.Locked ? "1" : "0") + "|" + (f.PreserveAspect ? "1" : "0") + "|" + ((int)f.ScaleMode).ToString() + "|" +
                    (f.IsOverlayVisible ? "1" : "0") + "|" + customName64 + "|" + f.RotationDegrees.ToString() + "|" +
                    (f.FlipHorizontal ? "1" : "0") + "|" + (f.FlipVertical ? "1" : "0") + "|" +
                    f.CropLeft.ToString() + "|" + f.CropTop.ToString() + "|" + f.CropRight.ToString() + "|" + f.CropBottom.ToString() + "|" +
                    f.WebZoomPercent.ToString() + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(f.WebCustomCss ?? "")));
            }
            File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
        }

        private bool LoadGroupFileAdditive(string path, bool captureUndo, string successPrefix)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !IsGroupFile(path)) return false;
                string full = Path.GetFullPath(path.Trim().Trim('"'));
                if (!File.Exists(full)) { SetStatus("그룹 파일을 찾을 수 없습니다."); return false; }
                string[] lines = File.ReadAllLines(full, Encoding.UTF8);
                if (lines.Length < 2 || lines[0].Trim() != "CATLAYER_GROUP_V1") throw new InvalidDataException("지원하지 않는 그룹 파일입니다.");

                string groupName = Path.GetFileNameWithoutExtension(full);
                int savedOriginX = 0, savedOriginY = 0;
                Dictionary<string, string> extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> extractedSounds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                List<string[]> itemParts = new List<string[]>();
                int minX = int.MaxValue, minY = int.MaxValue, maxR = int.MinValue, maxB = int.MinValue;

                Directory.CreateDirectory(assetsDir);
                Directory.CreateDirectory(soundsDir);
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.StartsWith("NAME="))
                    {
                        try { groupName = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(5))); } catch { }
                        continue;
                    }
                    if (line.StartsWith("ORIGIN="))
                    {
                        string[] xy = line.Substring(7).Split('|');
                        if (xy.Length >= 2) { int.TryParse(xy[0], out savedOriginX); int.TryParse(xy[1], out savedOriginY); }
                        continue;
                    }
                    if (line.StartsWith("ASSET|"))
                    {
                        string[] a = line.Split('|');
                        if (a.Length != 4) continue;
                        string ext = Encoding.UTF8.GetString(Convert.FromBase64String(a[2]));
                        if (string.IsNullOrEmpty(ext) || ext.Length > 10 || ext.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ext = ".img";
                        string outPath = Path.Combine(assetsDir, "group_" + Guid.NewGuid().ToString("N") + ext.ToLowerInvariant());
                        File.WriteAllBytes(outPath, Convert.FromBase64String(a[3]));
                        extracted[a[1]] = outPath;
                        continue;
                    }
                    if (line.StartsWith("SOUND|"))
                    {
                        string[] s = line.Split('|');
                        if (s.Length != 4) continue;
                        string ext = (Encoding.UTF8.GetString(Convert.FromBase64String(s[2])) ?? "").ToLowerInvariant();
                        if (ext != ".wav" && ext != ".mp3" && ext != ".wma") ext = ".wav";
                        string outPath = Path.Combine(soundsDir, "group_alarm_" + Guid.NewGuid().ToString("N") + ext);
                        File.WriteAllBytes(outPath, Convert.FromBase64String(s[3]));
                        extractedSounds[s[1]] = outPath;
                        continue;
                    }
                    if (line.StartsWith("ITEM|"))
                    {
                        string[] ip = line.Split('|');
                        if (ip.Length < 25) continue;
                        int rx, ry, w, h;
                        if (!int.TryParse(ip[4], out rx) || !int.TryParse(ip[5], out ry) || !int.TryParse(ip[6], out w) || !int.TryParse(ip[7], out h)) continue;
                        itemParts.Add(ip);
                        minX = Math.Min(minX, rx); minY = Math.Min(minY, ry);
                        maxR = Math.Max(maxR, rx + Math.Max(100, w)); maxB = Math.Max(maxB, ry + Math.Max(60, h));
                    }
                }
                if (itemParts.Count == 0) throw new InvalidDataException("그룹 파일에 불러올 오버레이가 없습니다.");

                int groupWidth = Math.Max(100, maxR - minX), groupHeight = Math.Max(60, maxB - minY);
                int targetOriginX = savedOriginX, targetOriginY = savedOriginY;
                Rectangle requested = new Rectangle(targetOriginX + minX, targetOriginY + minY, groupWidth, groupHeight);
                bool visibleEnough = false;
                foreach (Screen screen in Screen.AllScreens)
                {
                    Rectangle inter = Rectangle.Intersect(requested, screen.WorkingArea);
                    if (inter.Width >= 30 && inter.Height >= 30) { visibleEnough = true; break; }
                }
                if (!visibleEnough)
                {
                    Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
                    targetOriginX = area.Left + Math.Max(0, (area.Width - groupWidth) / 2) - minX;
                    targetOriginY = area.Top + Math.Max(0, (area.Height - groupHeight) / 2) - minY;
                }

                if (captureUndo) CaptureUndo("그룹 파일 불러오기");
                int newGroupId = nextGroupId++;
                bool obsExists = false;
                foreach (OverlayItemForm existing in items) if (existing.Type == ItemType.ObsProgram) { obsExists = true; break; }
                int loaded = 0, skippedObs = 0;
                List<OverlayItemForm> createdItems = new List<OverlayItemForm>();

                foreach (string[] ip in itemParts)
                {
                    int typeValue, sec, rx, ry, w, h, opacity;
                    if (!int.TryParse(ip[1], out typeValue) || !int.TryParse(ip[3], out sec) || !int.TryParse(ip[4], out rx) || !int.TryParse(ip[5], out ry) ||
                        !int.TryParse(ip[6], out w) || !int.TryParse(ip[7], out h) || !int.TryParse(ip[8], out opacity)) continue;
                    ItemType type;
                    if (typeValue == (int)ItemType.Web) type = ItemType.Web;
                    else if (typeValue == 3 || typeValue == 4) type = ItemType.ObsProgram;
                    else if (typeValue >= 0 && typeValue <= 2) type = (ItemType)typeValue;
                    else continue;
                    if (type == ItemType.ObsProgram && obsExists) { skippedObs++; continue; }

                    string data;
                    if (type == ItemType.Image)
                    {
                        if (!extracted.TryGetValue(ip[2], out data)) continue;
                    }
                    else
                    {
                        try { data = Encoding.UTF8.GetString(Convert.FromBase64String(ip[2])); } catch { data = ""; }
                    }
                    if (type == ItemType.ObsProgram) { data = "OBS Program"; obsExists = true; }

                    TimerMode timerMode = TimerMode.OneShot;
                    int timerModeValue;
                    if (int.TryParse(ip[9], out timerModeValue) && timerModeValue >= 0 && timerModeValue <= 2) timerMode = (TimerMode)timerModeValue;
                    string alarmPath = "";
                    if (!string.IsNullOrEmpty(ip[10])) extractedSounds.TryGetValue(ip[10], out alarmPath);
                    bool locked = ip[11] == "1";
                    bool preserveAspect = ip[12] != "0";
                    ImageScaleMode scaleMode = ImageScaleMode.Fit;
                    int scaleValue; if (int.TryParse(ip[13], out scaleValue) && scaleValue >= 0 && scaleValue <= 2) scaleMode = (ImageScaleMode)scaleValue;
                    bool visible = ip[14] != "0";
                    string customName = ""; try { customName = Encoding.UTF8.GetString(Convert.FromBase64String(ip[15])); } catch { }
                    int rotation = 0; int.TryParse(ip[16], out rotation);
                    bool flipH = ip[17] == "1", flipV = ip[18] == "1";
                    int cropL = 0, cropT = 0, cropR = 0, cropB = 0;
                    int.TryParse(ip[19], out cropL); int.TryParse(ip[20], out cropT); int.TryParse(ip[21], out cropR); int.TryParse(ip[22], out cropB);
                    int webZoom = 100; int.TryParse(ip[23], out webZoom);
                    string webCss = ""; try { webCss = Encoding.UTF8.GetString(Convert.FromBase64String(ip[24])); } catch { }

                    Rectangle bounds = new Rectangle(targetOriginX + rx, targetOriginY + ry, Math.Max(100, w), Math.Max(60, h));
                    CreateItem(type, data, sec, bounds, Math.Max(0, Math.Min(100, opacity)), timerMode, alarmPath ?? "", locked, preserveAspect, scaleMode,
                        visible, customName, rotation, flipH, flipV, newGroupId, cropL, cropT, cropR, cropB, webZoom, webCss);
                    createdItems.Add(items[items.Count - 1]);
                    loaded++;
                }

                if (loaded == 0) throw new InvalidDataException("그룹 파일에서 생성할 수 있는 오버레이가 없습니다.");
                hidden = false;
                foreach (OverlayItemForm f in items) f.RefreshEffectiveVisibility();
                ApplyZOrder();
                SaveConfig();
                RefreshMainUi();
                foreach (ListViewItem row in overlayList.Items) row.Selected = createdItems.Contains(row.Tag as OverlayItemForm);
                RefreshPropertyEditor(); RefreshOverlaySelectionVisuals();
                SetStatus((successPrefix ?? "그룹 불러오기 완료: ") + (string.IsNullOrWhiteSpace(groupName) ? "그룹" : groupName) +
                    "  |  " + loaded.ToString() + "개 추가" + (skippedObs > 0 ? "  |  OBS " + skippedObs.ToString() + "개 제외" : ""));
                return true;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "LoadGroupFileAdditive");
                SetStatus("그룹 파일 불러오기 실패: " + ex.Message);
                return false;
            }
        }

        private static string GetPrimaryPresetExtension() { return ".catlayerpreset"; }
        private static bool IsPresetFile(string path)
        {
            string ext = (Path.GetExtension(path ?? "") ?? "").ToLowerInvariant();
            return ext == ".catlayerpreset" || ext == ".lopreset";
        }

        private static string SuggestedImageNameFromPath(string path)
        {
            string name = Path.GetFileName(path ?? "");
            if (string.IsNullOrWhiteSpace(name)) return "이미지";
            return name;
        }

        private bool OpenPresetFileFromShell(string path, bool captureUndo, string successPrefix)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !IsPresetFile(path)) return false;
                string full = Path.GetFullPath(path.Trim().Trim('"'));
                if (!File.Exists(full)) { SetStatus("프리셋 파일을 찾을 수 없습니다."); return false; }
                if (captureUndo) CaptureUndo("프리셋 불러오기");
                string loadedName = LoadPresetFile(full);
                currentPresetName = loadedName;
                SetStatus(successPrefix + loadedName);
                return true;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "OpenPresetFileFromShell");
                SetStatus("프리셋 적용 실패: " + ex.Message);
                return false;
            }
        }

        private string ConsumePendingLaunchArgument()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatLayer", "pending_launch.txt");
                if (!File.Exists(path)) return "";
                string value = File.ReadAllText(path, Encoding.UTF8).Trim();
                try { File.Delete(path); } catch { }
                return value;
            }
            catch { return ""; }
        }

        private bool OpenLaunchFileFromShell(string path, bool captureUndo)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (IsGroupFile(path)) return LoadGroupFileAdditive(path, captureUndo, "그룹 파일 적용 완료: ");
            if (IsPresetFile(path)) return OpenPresetFileFromShell(path, captureUndo, "프리셋 적용 완료: ");
            if (IsWebPackageFile(path)) return AddCatLayerWebOverlay(path, null);
            if (IsLocalHtmlFile(path)) return AddLocalWebOverlay(path, null);
            return false;
        }

        public void ProcessStartupArgument(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            OpenLaunchFileFromShell(path, false);
        }

        private sealed class PresetListEntry
        {
            public string Name;
            public string Path;

            public override string ToString()
            {
                return Name ?? "";
            }
        }

        private string ReadPresetName(string path)
        {
            using (StreamReader reader = new StreamReader(path, Encoding.UTF8, true))
            {
                string header = reader.ReadLine();
                string nameLine = reader.ReadLine();
                string h = header == null ? "" : header.Trim();
                if (h != "LIGHTOVERLAY_PRESET_V1" && h != "LIGHTOVERLAY_PRESET_V2" && h != "LIGHTOVERLAY_PRESET_V3" && h != "LIGHTOVERLAY_PRESET_V4" && h != "LIGHTOVERLAY_PRESET_V5" && h != "LIGHTOVERLAY_PRESET_V6" && h != "LIGHTOVERLAY_PRESET_V7")
                    throw new InvalidDataException("지원하지 않는 프리셋 파일입니다.");

                if (nameLine != null && nameLine.StartsWith("NAME="))
                {
                    string n = Encoding.UTF8.GetString(Convert.FromBase64String(nameLine.Substring(5)));
                    if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
                }
            }
            return Path.GetFileNameWithoutExtension(path);
        }

        private List<PresetListEntry> GetPresetEntries()
        {
            List<PresetListEntry> result = new List<PresetListEntry>();
            Directory.CreateDirectory(presetsDir);
            List<string> files = new List<string>();
            files.AddRange(Directory.GetFiles(presetsDir, "*.catlayerpreset", SearchOption.TopDirectoryOnly));
            files.AddRange(Directory.GetFiles(presetsDir, "*.lopreset", SearchOption.TopDirectoryOnly));

            foreach (string path in files)
            {
                try
                {
                    PresetListEntry entry = new PresetListEntry();
                    entry.Name = ReadPresetName(path);
                    entry.Path = path;
                    result.Add(entry);
                }
                catch
                {
                    // Ignore damaged or unrelated files in the built-in preset library.
                }
            }

            result.Sort(delegate(PresetListEntry a, PresetListEntry b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return result;
        }

        private void SavePresetInteractive()
        {
            string name = UiPrompt.AskText(this, "프리셋 저장", "프리셋 이름", "내 프리셋");
            if (name == null) return;
            if (string.IsNullOrWhiteSpace(name)) name = "내 프리셋";

            try
            {
                Directory.CreateDirectory(presetsDir);
                string path = Path.Combine(presetsDir, MakeSafeFileName(name) + GetPrimaryPresetExtension());
                SavePresetFile(path, name);
                currentPresetName = name;
                SetStatus("프리셋 저장 완료: " + name);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "SavePreset");
                SetStatus("프리셋 저장 실패: " + ex.Message);
            }
        }

        private void LoadPresetInteractive()
        {
            List<PresetListEntry> presets = GetPresetEntries();
            if (presets.Count == 0)
            {
                SetStatus("저장된 프리셋이 없습니다. 먼저 프리셋을 저장하거나 외부 프리셋을 가져오세요.");
                return;
            }

            PresetListEntry selected = null;
            using (Form f = new Form())
            using (ListBox list = new ListBox())
            using (Button load = new Button())
            using (Button close = new Button())
            using (Label label = new Label())
            {
                f.Text = "프리셋 불러오기";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(390, 390);
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = false;

                label.Text = "저장된 프리셋";
                label.AutoSize = true;
                label.Location = new Point(12, 12);
                f.Controls.Add(label);

                list.SetBounds(15, 38, 360, 292);
                foreach (PresetListEntry entry in presets) list.Items.Add(entry);
                if (list.Items.Count > 0) list.SelectedIndex = 0;
                f.Controls.Add(list);

                load.Text = "불러오기";
                load.SetBounds(205, 344, 80, 30);
                f.Controls.Add(load);

                close.Text = "닫기";
                close.SetBounds(295, 344, 80, 30);
                close.DialogResult = DialogResult.Cancel;
                f.Controls.Add(close);
                f.CancelButton = close;

                EventHandler choose = delegate
                {
                    PresetListEntry entry = list.SelectedItem as PresetListEntry;
                    if (entry == null) return;
                    selected = entry;
                    f.DialogResult = DialogResult.OK;
                    f.Close();
                };
                load.Click += choose;
                list.DoubleClick += choose;

                f.ShowDialog(this);
            }

            if (selected == null) return;
            try
            {
                CaptureUndo("프리셋 불러오기");
                string loadedName = LoadPresetFile(selected.Path);
                currentPresetName = loadedName;
                SetStatus("프리셋 불러오기 완료: " + loadedName);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "LoadPreset");
                SetStatus("프리셋 불러오기 실패: " + ex.Message);
            }
        }

        private void ImportPresetInteractive()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "CatLayer preset|*.catlayerpreset;*.lopreset|All files|*.*";
                d.Multiselect = false;
                if (d.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    string presetName = ReadPresetName(d.FileName);
                    Directory.CreateDirectory(presetsDir);
                    string dest = Path.Combine(presetsDir, MakeSafeFileName(presetName) + GetPrimaryPresetExtension());
                    string srcFull = Path.GetFullPath(d.FileName);
                    string dstFull = Path.GetFullPath(dest);

                    if (!string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase))
                        File.Copy(d.FileName, dest, true);

                    SetStatus("외부 프리셋 가져오기 완료: " + presetName + "  (프리셋 불러오기 목록에 추가됨)");
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex, "ImportPreset");
                    SetStatus("외부 프리셋 가져오기 실패: " + ex.Message);
                }
            }
        }

        private static string MakeSafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "preset";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "preset" : name;
        }

        private void SavePresetFile(string path, string name)
        {
            List<string> lines = new List<string>();
            bool containsWeb = false;
            foreach (OverlayItemForm item in items) if (item.Type == ItemType.Web) { containsWeb = true; break; }
            lines.Add(containsWeb ? "LIGHTOVERLAY_PRESET_V7" : "LIGHTOVERLAY_PRESET_V6");
            lines.Add("NAME=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(name ?? "")));
            Dictionary<string, string> assetIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> soundIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int assetNo = 0;
            int soundNo = 0;

            foreach (OverlayItemForm f in items)
            {
                Rectangle r = f.Bounds;
                string payload;
                if (f.Type == ItemType.Image)
                {
                    if (!File.Exists(f.Data)) throw new FileNotFoundException("프리셋에 포함할 이미지가 없습니다.", f.Data);
                    string id;
                    if (!assetIds.TryGetValue(f.Data, out id))
                    {
                        id = "A" + (++assetNo).ToString();
                        assetIds[f.Data] = id;
                        string ext = Path.GetExtension(f.Data) ?? "";
                        byte[] bytes = File.ReadAllBytes(f.Data);
                        lines.Add("ASSET|" + id + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(ext)) + "|" + Convert.ToBase64String(bytes));
                    }
                    payload = id;
                }
                else
                {
                    payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(f.Data ?? ""));
                }

                string soundId = "";
                if (f.Type == ItemType.Timer && f.TimerKind != TimerMode.Stopwatch &&
                    !string.IsNullOrEmpty(f.AlarmPath) && File.Exists(f.AlarmPath))
                {
                    if (!soundIds.TryGetValue(f.AlarmPath, out soundId))
                    {
                        soundId = "S" + (++soundNo).ToString();
                        soundIds[f.AlarmPath] = soundId;
                        string ext = Path.GetExtension(f.AlarmPath) ?? ".wav";
                        byte[] bytes = File.ReadAllBytes(f.AlarmPath);
                        lines.Add("SOUND|" + soundId + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(ext)) + "|" + Convert.ToBase64String(bytes));
                    }
                }

                string customName64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(f.CustomName ?? ""));
                lines.Add("ITEM|" + ((int)f.Type).ToString() + "|" + payload + "|" + f.DurationSeconds + "|" +
                    r.X + "|" + r.Y + "|" + r.Width + "|" + r.Height + "|" + f.OpacityPercent + "|" +
                    ((int)f.TimerKind).ToString() + "|" + soundId + "|" + (f.Locked ? "1" : "0") + "|" + (f.PreserveAspect ? "1" : "0") + "|" + ((int)f.ScaleMode).ToString() + "|" +
                    (f.IsOverlayVisible ? "1" : "0") + "|" + customName64 + "|" + f.RotationDegrees.ToString() + "|" +
                    (f.FlipHorizontal ? "1" : "0") + "|" + (f.FlipVertical ? "1" : "0") + "|" + f.GroupId.ToString() + "|" +
                    f.CropLeft.ToString() + "|" + f.CropTop.ToString() + "|" + f.CropRight.ToString() + "|" + f.CropBottom.ToString() + "|" +
                    f.WebZoomPercent.ToString() + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(f.WebCustomCss ?? "")));
            }
            File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
        }

        private string LoadPresetFile(string path)
        {
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length < 2) throw new InvalidDataException("지원하지 않는 프리셋 파일입니다.");
            string header = lines[0].Trim();
            bool presetV7 = header == "LIGHTOVERLAY_PRESET_V7";
            bool presetV6 = header == "LIGHTOVERLAY_PRESET_V6" || presetV7;
            bool presetV2 = header == "LIGHTOVERLAY_PRESET_V2" || header == "LIGHTOVERLAY_PRESET_V3" || header == "LIGHTOVERLAY_PRESET_V4" || header == "LIGHTOVERLAY_PRESET_V5" || presetV6;
            bool presetV3 = header == "LIGHTOVERLAY_PRESET_V3" || header == "LIGHTOVERLAY_PRESET_V4" || header == "LIGHTOVERLAY_PRESET_V5" || presetV6;
            bool presetV4 = header == "LIGHTOVERLAY_PRESET_V4" || header == "LIGHTOVERLAY_PRESET_V5" || presetV6;
            bool presetV5 = header == "LIGHTOVERLAY_PRESET_V5" || presetV6;
            if (!presetV2 && header != "LIGHTOVERLAY_PRESET_V1")
                throw new InvalidDataException("지원하지 않는 프리셋 파일입니다.");

            string presetName = "프리셋";
            Dictionary<string, string> extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> extractedSounds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("NAME="))
                {
                    presetName = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(5)));
                    continue;
                }

                if (line.StartsWith("ASSET|"))
                {
                    string[] p = line.Split('|');
                    if (p.Length != 4) continue;
                    string ext = Encoding.UTF8.GetString(Convert.FromBase64String(p[2]));
                    if (string.IsNullOrEmpty(ext) || ext.Length > 10 || ext.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ext = ".img";
                    string outPath = Path.Combine(assetsDir, "preset_" + Guid.NewGuid().ToString("N") + ext);
                    File.WriteAllBytes(outPath, Convert.FromBase64String(p[3]));
                    extracted[p[1]] = outPath;
                    continue;
                }

                if (presetV2 && line.StartsWith("SOUND|"))
                {
                    string[] p = line.Split('|');
                    if (p.Length != 4) continue;
                    string ext = (Encoding.UTF8.GetString(Convert.FromBase64String(p[2])) ?? "").ToLowerInvariant();
                    if (ext != ".wav" && ext != ".mp3" && ext != ".wma") ext = ".wav";
                    string outPath = Path.Combine(soundsDir, "preset_alarm_" + Guid.NewGuid().ToString("N") + ext);
                    File.WriteAllBytes(outPath, Convert.FromBase64String(p[3]));
                    extractedSounds[p[1]] = outPath;
                }
            }

            ClearAllItems(true);
            bool obsLoaded = false;
            for (int i = 1; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("ITEM|")) continue;
                string[] p = lines[i].Split('|');
                if ((!presetV2 && p.Length != 9) || (presetV2 && p.Length < 11)) continue;

                int typeValue, sec, x, y, w, h, opacity;
                if (!int.TryParse(p[1], out typeValue) || !int.TryParse(p[3], out sec) ||
                    !int.TryParse(p[4], out x) || !int.TryParse(p[5], out y) ||
                    !int.TryParse(p[6], out w) || !int.TryParse(p[7], out h) ||
                    !int.TryParse(p[8], out opacity)) continue;

                ItemType type;
                if (typeValue == 3 || typeValue == 4) type = ItemType.ObsProgram;
                else if (presetV7 && typeValue == (int)ItemType.Web) type = ItemType.Web;
                else if (typeValue >= 0 && typeValue <= 2) type = (ItemType)typeValue;
                else continue;

                if (type == ItemType.ObsProgram)
                {
                    if (obsLoaded) continue;
                    obsLoaded = true;
                }

                string data;
                if (type == ItemType.Image)
                {
                    if (!extracted.TryGetValue(p[2], out data)) continue;
                }
                else data = Encoding.UTF8.GetString(Convert.FromBase64String(p[2]));
                if (type == ItemType.ObsProgram) data = "OBS Program";

                TimerMode timerMode = TimerMode.OneShot;
                string alarmPath = "";
                bool locked = false;
                bool preserveAspect = true;
                ImageScaleMode scaleMode = ImageScaleMode.Fit;
                bool visible = true;
                string customName = "";
                int rotationDegrees = 0;
                bool flipHorizontal = false;
                bool flipVertical = false;
                int groupId = 0;
                int cropLeft = 0, cropTop = 0, cropRight = 0, cropBottom = 0;
                int webZoomPercent = 100; string webCustomCss = "";
                if (presetV2 && type == ItemType.Timer)
                {
                    int modeValue;
                    if (int.TryParse(p[9], out modeValue) && modeValue >= 0 && modeValue <= 2)
                        timerMode = (TimerMode)modeValue;

                    string soundId = p[10];
                    if (!string.IsNullOrEmpty(soundId))
                        extractedSounds.TryGetValue(soundId, out alarmPath);
                }
                if (presetV3)
                {
                    if (p.Length > 11) locked = p[11] == "1";
                    if (p.Length > 12) preserveAspect = p[12] != "0";
                    int scaleValue;
                    if (p.Length > 13 && int.TryParse(p[13], out scaleValue) && scaleValue >= 0 && scaleValue <= 2) scaleMode = (ImageScaleMode)scaleValue;
                }
                if (presetV4)
                {
                    if (p.Length > 14) visible = p[14] != "0";
                    if (p.Length > 15)
                    {
                        try { customName = Encoding.UTF8.GetString(Convert.FromBase64String(p[15])); } catch { customName = ""; }
                    }
                }
                if (presetV5)
                {
                    if (p.Length > 16) int.TryParse(p[16], out rotationDegrees);
                    if (p.Length > 17) flipHorizontal = p[17] == "1";
                    if (p.Length > 18) flipVertical = p[18] == "1";
                    if (p.Length > 19) int.TryParse(p[19], out groupId);
                    groupId = Math.Max(0, groupId);
                }
                if (presetV6)
                {
                    if (p.Length > 20) int.TryParse(p[20], out cropLeft);
                    if (p.Length > 21) int.TryParse(p[21], out cropTop);
                    if (p.Length > 22) int.TryParse(p[22], out cropRight);
                    if (p.Length > 23) int.TryParse(p[23], out cropBottom);
                }
                if (presetV7 && type == ItemType.Web)
                {
                    if (p.Length > 24) int.TryParse(p[24], out webZoomPercent);
                    if (p.Length > 25) { try { webCustomCss = Encoding.UTF8.GetString(Convert.FromBase64String(p[25])); } catch { webCustomCss = ""; } }
                }

                CreateItem(type, data, sec, new Rectangle(x, y, Math.Max(100, w), Math.Max(60, h)),
                    Math.Max(0, Math.Min(100, opacity)), timerMode, alarmPath ?? "", locked, preserveAspect, scaleMode, visible, customName, rotationDegrees, flipHorizontal, flipVertical, groupId,
                    cropLeft, cropTop, cropRight, cropBottom, webZoomPercent, webCustomCss);
            }

            hidden = false;
            foreach (OverlayItemForm f in items) f.RefreshEffectiveVisibility();
            UpdateButtons();
            ApplyZOrder();
            SaveConfig();
            return string.IsNullOrWhiteSpace(presetName) ? "프리셋" : presetName;
        }

        private void ClearAllItems(bool deleteManagedAssets)
        {
            List<string> oldAssets = new List<string>();
            List<string> oldSounds = new List<string>();
            foreach (OverlayItemForm f in items)
            {
                if (f.Type == ItemType.Image) oldAssets.Add(f.Data);
                if (f.Type == ItemType.Timer && !string.IsNullOrEmpty(f.AlarmPath)) oldSounds.Add(f.AlarmPath);
                f.Dispose();
            }
            items.Clear();
            nextGroupId = 1;
            if (deleteManagedAssets)
            {
                foreach (string path in oldAssets) TryDeleteUnusedManagedAsset(path);
                foreach (string path in oldSounds) TryDeleteUnusedManagedSound(path);
            }
        }

        public void SaveConfig()
        {
            try
            {
                List<string> lines = new List<string>(48 + presetHotkeys.Count + items.Count); lines.Add("EDIT=" + (EditMode ? "1" : "0"));
                lines.Add("HOTKEY_EDIT=" + hotkeyEditVk.ToString());
                lines.Add("HOTKEY_EDIT_MOD=" + hotkeyEditMods.ToString());
                lines.Add("HOTKEY_HIDE=" + hotkeyHideVk.ToString());
                lines.Add("HOTKEY_HIDE_MOD=" + hotkeyHideMods.ToString());
                lines.Add("HOTKEY_DETAIL=" + hotkeyDetailVk.ToString());
                lines.Add("HOTKEY_DETAIL_MOD=" + hotkeyDetailMods.ToString());
                lines.Add("HOTKEY_CAPTURE=" + hotkeyCaptureVk.ToString());
                lines.Add("HOTKEY_CAPTURE_MOD=" + hotkeyCaptureMods.ToString());
                lines.Add("HOTKEY_GROUP=" + hotkeyGroupVk.ToString()); lines.Add("HOTKEY_GROUP_MOD=" + hotkeyGroupMods.ToString());
                lines.Add("HOTKEY_UNGROUP=" + hotkeyUngroupVk.ToString()); lines.Add("HOTKEY_UNGROUP_MOD=" + hotkeyUngroupMods.ToString());
                lines.Add("HOTKEY_ROT_M1=" + hotkeyRotateMinus1Vk.ToString()); lines.Add("HOTKEY_ROT_M1_MOD=" + hotkeyRotateMinus1Mods.ToString());
                lines.Add("HOTKEY_ROT_P1=" + hotkeyRotatePlus1Vk.ToString()); lines.Add("HOTKEY_ROT_P1_MOD=" + hotkeyRotatePlus1Mods.ToString());
                lines.Add("HOTKEY_ROT_M10=" + hotkeyRotateMinus10Vk.ToString()); lines.Add("HOTKEY_ROT_M10_MOD=" + hotkeyRotateMinus10Mods.ToString());
                lines.Add("HOTKEY_ROT_P10=" + hotkeyRotatePlus10Vk.ToString()); lines.Add("HOTKEY_ROT_P10_MOD=" + hotkeyRotatePlus10Mods.ToString());
                lines.Add("HOTKEY_FLIP_H=" + hotkeyFlipHorizontalVk.ToString()); lines.Add("HOTKEY_FLIP_H_MOD=" + hotkeyFlipHorizontalMods.ToString());
                lines.Add("HOTKEY_FLIP_V=" + hotkeyFlipVerticalVk.ToString()); lines.Add("HOTKEY_FLIP_V_MOD=" + hotkeyFlipVerticalMods.ToString());
                lines.Add("HOTKEY_RESET_ROT=" + hotkeyResetRotationVk.ToString()); lines.Add("HOTKEY_RESET_ROT_MOD=" + hotkeyResetRotationMods.ToString());
                lines.Add("HOTKEY_RESET_ALL=" + hotkeyResetTransformVk.ToString()); lines.Add("HOTKEY_RESET_ALL_MOD=" + hotkeyResetTransformMods.ToString());
                lines.Add("ZOOM_STEP=" + zoomStepPercent.ToString());
                lines.Add("ROTATION_SNAP=" + rotationSnapDegrees.ToString());
                lines.Add("PLACEMENT_SNAP=" + placementSnapPixels.ToString());
                lines.Add("AUTO_UPDATE_SUPPRESS=" + (suppressAutomaticUpdatePrompt ? "1" : "0"));
                foreach (PresetHotkeyBinding binding in presetHotkeys)
                {
                    if (binding == null || string.IsNullOrWhiteSpace(binding.FileName) || binding.Vk <= 0) continue;
                    string file64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(binding.FileName));
                    lines.Add("PRESET_HOTKEY=" + file64 + "|" + binding.Mods.ToString() + "|" + binding.Vk.ToString());
                }
                lines.Add("HIDDEN=" + (hidden ? "1" : "0"));
                lines.Add("CURRENT_PRESET=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(currentPresetName ?? "")));
                lines.Add("MAIN_SIZE=" + ClientSize.Width.ToString() + "," + ClientSize.Height.ToString());
                foreach (OverlayItemForm f in items)
                {
                    Rectangle r = f.Bounds;
                    string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(f.Data ?? ""));
                    string alarm64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(f.AlarmPath ?? ""));
                    string customName64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(f.CustomName ?? ""));
                    lines.Add(((int)f.Type).ToString() + "|" + b64 + "|" + f.DurationSeconds + "|" + r.X + "|" + r.Y + "|" +
                        r.Width + "|" + r.Height + "|" + f.OpacityPercent + "|" + ((int)f.TimerKind).ToString() + "|" + alarm64 + "|" + (f.Locked ? "1" : "0") + "|" + (f.PreserveAspect ? "1" : "0") + "|" + ((int)f.ScaleMode).ToString() + "|" +
                        (f.IsOverlayVisible ? "1" : "0") + "|" + customName64 + "|" + f.RotationDegrees.ToString() + "|" +
                        (f.FlipHorizontal ? "1" : "0") + "|" + (f.FlipVertical ? "1" : "0") + "|" + f.GroupId.ToString() + "|" +
                        f.CropLeft.ToString() + "|" + f.CropTop.ToString() + "|" + f.CropRight.ToString() + "|" + f.CropBottom.ToString() + "|" +
                    f.WebZoomPercent.ToString() + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(f.WebCustomCss ?? "")));
                }
                // SaveConfig is called from many UI actions. When state did not change, avoid
                // another temporary file + replace cycle while preserving the old UI refresh side effect.
                string configSignature = string.Join("\n", lines.ToArray());
                bool configUnchanged = File.Exists(configPath) && string.Equals(lastSavedConfigSignature, configSignature, StringComparison.Ordinal);
                if (!configUnchanged)
                {
                    string tempPath = configPath + ".tmp";
                    File.WriteAllLines(tempPath, lines.ToArray(), Encoding.UTF8);
                    if (File.Exists(configPath))
                    {
                        try
                        {
                            File.Replace(tempPath, configPath, configPath + ".bak", true);
                        }
                        catch
                        {
                            try { File.Copy(configPath, configPath + ".bak", true); } catch { }
                            File.Copy(tempPath, configPath, true);
                            try { File.Delete(tempPath); } catch { }
                        }
                    }
                    else File.Move(tempPath, configPath);
                    lastSavedConfigSignature = configSignature;
                }
                if (mainUiReady && !syncingMainUi) RefreshMainUi();
            }
            catch { }
        }

        private void ResetEditActionHotkeysToDefaults()
        {
            hotkeyGroupVk = (int)Keys.G; hotkeyGroupMods = Native.MOD_CONTROL;
            hotkeyUngroupVk = (int)Keys.G; hotkeyUngroupMods = Native.MOD_CONTROL | Native.MOD_SHIFT;
            hotkeyRotateMinus1Vk = (int)Keys.Q; hotkeyRotateMinus1Mods = 0;
            hotkeyRotatePlus1Vk = (int)Keys.E; hotkeyRotatePlus1Mods = 0;
            hotkeyRotateMinus10Vk = (int)Keys.Q; hotkeyRotateMinus10Mods = Native.MOD_SHIFT;
            hotkeyRotatePlus10Vk = (int)Keys.E; hotkeyRotatePlus10Mods = Native.MOD_SHIFT;
            hotkeyFlipHorizontalVk = (int)Keys.H; hotkeyFlipHorizontalMods = 0;
            hotkeyFlipVerticalVk = (int)Keys.V; hotkeyFlipVerticalMods = 0;
            hotkeyResetRotationVk = (int)Keys.R; hotkeyResetRotationMods = 0;
            hotkeyResetTransformVk = (int)Keys.R; hotkeyResetTransformMods = Native.MOD_SHIFT;
        }

        private void ResetConfigLoadState()
        {
            ClearAllItems(false);
            EditMode = true;
            DetailEditMode = true;
            hidden = false;
            nextGroupId = 1;
            hotkeyEditVk = Native.VK_F8;
            hotkeyHideVk = Native.VK_F9;
            hotkeyDetailVk = Native.VK_F10;
            hotkeyCaptureVk = Native.VK_F1;
            hotkeyEditMods = 0;
            hotkeyHideMods = 0;
            hotkeyDetailMods = 0;
            hotkeyCaptureMods = 0;
            ResetEditActionHotkeysToDefaults();
            zoomStepPercent = 10;
            rotationSnapDegrees = 5;
            placementSnapPixels = 8;
            presetHotkeys.Clear();
            currentPresetName = "";
            ClientSize = mainBaseClientSize;
        }

        private void LoadConfig()
        {
            string backupPath = configPath + ".bak";
            if (!File.Exists(configPath) && !File.Exists(backupPath)) return;

            configRecoveredFromBackup = false;
            if (File.Exists(configPath))
            {
                try
                {
                    LoadConfigFromFile(configPath);
                    SaveConfig();
                    return;
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex, "LoadConfig primary");
                    ResetConfigLoadState();
                }
            }

            if (!File.Exists(backupPath)) return;
            try
            {
                LoadConfigFromFile(backupPath);
                configRecoveredFromBackup = true;
                try { File.Copy(backupPath, configPath, true); } catch { }
                SaveConfig();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "LoadConfig backup");
                ResetConfigLoadState();
            }
        }

        private void LoadConfigFromFile(string path)
        {
            string[] lines = File.ReadAllLines(path, Encoding.UTF8); int start = 0;
                bool obsLoaded = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("EDIT=")) { int editValue = 1; int.TryParse(lines[i].Substring(5), out editValue); EditMode = editValue != 0; DetailEditMode = EditMode; start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_EDIT=")) { int.TryParse(lines[i].Substring(12), out hotkeyEditVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_EDIT_MOD=")) { int.TryParse(lines[i].Substring(16), out hotkeyEditMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_HIDE=")) { int.TryParse(lines[i].Substring(12), out hotkeyHideVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_HIDE_MOD=")) { int.TryParse(lines[i].Substring(16), out hotkeyHideMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_DETAIL=")) { int.TryParse(lines[i].Substring(14), out hotkeyDetailVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_DETAIL_MOD=")) { int.TryParse(lines[i].Substring(18), out hotkeyDetailMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_CAPTURE=")) { int.TryParse(lines[i].Substring(15), out hotkeyCaptureVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_CAPTURE_MOD=")) { int.TryParse(lines[i].Substring(19), out hotkeyCaptureMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_GROUP=")) { int.TryParse(lines[i].Substring(13), out hotkeyGroupVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_GROUP_MOD=")) { int.TryParse(lines[i].Substring(17), out hotkeyGroupMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_UNGROUP=")) { int.TryParse(lines[i].Substring(15), out hotkeyUngroupVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_UNGROUP_MOD=")) { int.TryParse(lines[i].Substring(19), out hotkeyUngroupMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_ROT_M1=")) { int.TryParse(lines[i].Substring(14), out hotkeyRotateMinus1Vk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_ROT_M1_MOD=")) { int.TryParse(lines[i].Substring(18), out hotkeyRotateMinus1Mods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_ROT_P1=")) { int.TryParse(lines[i].Substring(14), out hotkeyRotatePlus1Vk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_ROT_P1_MOD=")) { int.TryParse(lines[i].Substring(18), out hotkeyRotatePlus1Mods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_ROT_M10=")) { int.TryParse(lines[i].Substring(15), out hotkeyRotateMinus10Vk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_ROT_M10_MOD=")) { int.TryParse(lines[i].Substring(19), out hotkeyRotateMinus10Mods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_ROT_P10=")) { int.TryParse(lines[i].Substring(15), out hotkeyRotatePlus10Vk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_ROT_P10_MOD=")) { int.TryParse(lines[i].Substring(19), out hotkeyRotatePlus10Mods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_FLIP_H=")) { int.TryParse(lines[i].Substring(14), out hotkeyFlipHorizontalVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_FLIP_H_MOD=")) { int.TryParse(lines[i].Substring(18), out hotkeyFlipHorizontalMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_FLIP_V=")) { int.TryParse(lines[i].Substring(14), out hotkeyFlipVerticalVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_FLIP_V_MOD=")) { int.TryParse(lines[i].Substring(18), out hotkeyFlipVerticalMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_RESET_ROT=")) { int.TryParse(lines[i].Substring(17), out hotkeyResetRotationVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_RESET_ROT_MOD=")) { int.TryParse(lines[i].Substring(21), out hotkeyResetRotationMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_RESET_ALL=")) { int.TryParse(lines[i].Substring(17), out hotkeyResetTransformVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_RESET_ALL_MOD=")) { int.TryParse(lines[i].Substring(21), out hotkeyResetTransformMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("ZOOM_STEP="))
                    {
                        int parsedZoom;
                        if (int.TryParse(lines[i].Substring(10), out parsedZoom)) zoomStepPercent = Math.Max(1, Math.Min(90, parsedZoom));
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("ROTATION_SNAP="))
                    {
                        int parsedSnap; if (int.TryParse(lines[i].Substring(14), out parsedSnap)) rotationSnapDegrees = Math.Max(0, Math.Min(15, parsedSnap));
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("PLACEMENT_SNAP="))
                    {
                        int parsedPlacement; if (int.TryParse(lines[i].Substring(15), out parsedPlacement)) placementSnapPixels = Math.Max(0, Math.Min(30, parsedPlacement));
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("AUTO_UPDATE_SUPPRESS="))
                    {
                        int suppressValue = 0; int.TryParse(lines[i].Substring(21), out suppressValue);
                        suppressAutomaticUpdatePrompt = suppressValue != 0;
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("PRESET_HOTKEY="))
                    {
                        try
                        {
                            string[] hp = lines[i].Substring(14).Split('|');
                            int mods, vk;
                            if (hp.Length == 3 && int.TryParse(hp[1], out mods) && int.TryParse(hp[2], out vk) && vk > 0 && !IsReservedClipboardHotkey(mods, vk))
                            {
                                string fileName = Path.GetFileName(Encoding.UTF8.GetString(Convert.FromBase64String(hp[0])));
                                if (!string.IsNullOrWhiteSpace(fileName))
                                {
                                    PresetHotkeyBinding binding = new PresetHotkeyBinding();
                                    binding.FileName = fileName; binding.Mods = mods; binding.Vk = vk;
                                    presetHotkeys.Add(binding);
                                }
                            }
                        }
                        catch { }
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("HIDDEN=")) { hidden = lines[i].Substring(7) != "0"; start = i + 1; continue; }
                    if (lines[i].StartsWith("CURRENT_PRESET=")) { try { currentPresetName = Encoding.UTF8.GetString(Convert.FromBase64String(lines[i].Substring(15))); } catch { currentPresetName = ""; } start = i + 1; continue; }
                    if (lines[i].StartsWith("MAIN_SIZE="))
                    {
                        try
                        {
                            string[] sizeParts = lines[i].Substring(10).Split(',');
                            int mw, mh;
                            if (sizeParts.Length == 2 && int.TryParse(sizeParts[0], out mw) && int.TryParse(sizeParts[1], out mh) && mw >= 250 && mh >= 350)
                                ClientSize = new Size(mw, mh);
                        }
                        catch { }
                        start = i + 1;
                        continue;
                    }
                    break;
                }
                if (hotkeyEditVk <= 0 || hotkeyEditVk > 0xFE || IsModifierOnlyKey((Keys)hotkeyEditVk) || IsReservedClipboardHotkey(hotkeyEditMods, hotkeyEditVk)) { hotkeyEditVk = Native.VK_F8; hotkeyEditMods = 0; }
                if (hotkeyHideVk <= 0 || hotkeyHideVk > 0xFE || IsModifierOnlyKey((Keys)hotkeyHideVk) || IsReservedClipboardHotkey(hotkeyHideMods, hotkeyHideVk)) { hotkeyHideVk = Native.VK_F9; hotkeyHideMods = 0; }
                if (hotkeyDetailVk <= 0 || hotkeyDetailVk > 0xFE || IsModifierOnlyKey((Keys)hotkeyDetailVk) || IsReservedClipboardHotkey(hotkeyDetailMods, hotkeyDetailVk)) { hotkeyDetailVk = Native.VK_F10; hotkeyDetailMods = 0; }
                if (hotkeyCaptureVk <= 0 || hotkeyCaptureVk > 0xFE || IsModifierOnlyKey((Keys)hotkeyCaptureVk) || IsReservedClipboardHotkey(hotkeyCaptureMods, hotkeyCaptureVk)) { hotkeyCaptureVk = Native.VK_F1; hotkeyCaptureMods = 0; }
                if (hotkeyCaptureMods == 0 && hotkeyCaptureVk == Native.VK_F7) hotkeyCaptureVk = Native.VK_F1;
                if (hotkeyGroupVk <= 0 || IsModifierOnlyKey((Keys)hotkeyGroupVk) || IsReservedClipboardHotkey(hotkeyGroupMods, hotkeyGroupVk)) { hotkeyGroupVk = (int)Keys.G; hotkeyGroupMods = Native.MOD_CONTROL; }
                if (hotkeyUngroupVk <= 0 || IsModifierOnlyKey((Keys)hotkeyUngroupVk) || IsReservedClipboardHotkey(hotkeyUngroupMods, hotkeyUngroupVk)) { hotkeyUngroupVk = (int)Keys.G; hotkeyUngroupMods = Native.MOD_CONTROL | Native.MOD_SHIFT; }
                if (hotkeyRotateMinus1Vk <= 0 || IsModifierOnlyKey((Keys)hotkeyRotateMinus1Vk) || IsReservedClipboardHotkey(hotkeyRotateMinus1Mods, hotkeyRotateMinus1Vk)) { hotkeyRotateMinus1Vk = (int)Keys.Q; hotkeyRotateMinus1Mods = 0; }
                if (hotkeyRotatePlus1Vk <= 0 || IsModifierOnlyKey((Keys)hotkeyRotatePlus1Vk) || IsReservedClipboardHotkey(hotkeyRotatePlus1Mods, hotkeyRotatePlus1Vk)) { hotkeyRotatePlus1Vk = (int)Keys.E; hotkeyRotatePlus1Mods = 0; }
                if (hotkeyRotateMinus10Vk <= 0 || IsModifierOnlyKey((Keys)hotkeyRotateMinus10Vk) || IsReservedClipboardHotkey(hotkeyRotateMinus10Mods, hotkeyRotateMinus10Vk)) { hotkeyRotateMinus10Vk = (int)Keys.Q; hotkeyRotateMinus10Mods = Native.MOD_SHIFT; }
                if (hotkeyRotatePlus10Vk <= 0 || IsModifierOnlyKey((Keys)hotkeyRotatePlus10Vk) || IsReservedClipboardHotkey(hotkeyRotatePlus10Mods, hotkeyRotatePlus10Vk)) { hotkeyRotatePlus10Vk = (int)Keys.E; hotkeyRotatePlus10Mods = Native.MOD_SHIFT; }
                if (hotkeyFlipHorizontalVk <= 0 || IsModifierOnlyKey((Keys)hotkeyFlipHorizontalVk) || IsReservedClipboardHotkey(hotkeyFlipHorizontalMods, hotkeyFlipHorizontalVk)) { hotkeyFlipHorizontalVk = (int)Keys.H; hotkeyFlipHorizontalMods = 0; }
                if (hotkeyFlipVerticalVk <= 0 || IsModifierOnlyKey((Keys)hotkeyFlipVerticalVk) || IsReservedClipboardHotkey(hotkeyFlipVerticalMods, hotkeyFlipVerticalVk)) { hotkeyFlipVerticalVk = (int)Keys.V; hotkeyFlipVerticalMods = 0; }
                if (hotkeyResetRotationVk <= 0 || IsModifierOnlyKey((Keys)hotkeyResetRotationVk) || IsReservedClipboardHotkey(hotkeyResetRotationMods, hotkeyResetRotationVk)) { hotkeyResetRotationVk = (int)Keys.R; hotkeyResetRotationMods = 0; }
                if (hotkeyResetTransformVk <= 0 || IsModifierOnlyKey((Keys)hotkeyResetTransformVk) || IsReservedClipboardHotkey(hotkeyResetTransformMods, hotkeyResetTransformVk)) { hotkeyResetTransformVk = (int)Keys.R; hotkeyResetTransformMods = Native.MOD_SHIFT; }
                zoomStepPercent = Math.Max(1, Math.Min(90, zoomStepPercent));
                rotationSnapDegrees = Math.Max(0, Math.Min(15, rotationSnapDegrees));
                placementSnapPixels = Math.Max(0, Math.Min(30, placementSnapPixels));
                RemoveMissingPresetHotkeys();
                for (int i = start; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    string[] p = lines[i].Split('|');
                    if (p.Length != 7 && p.Length != 8 && p.Length != 10 && p.Length != 13 && p.Length != 15 && p.Length != 19 && p.Length != 23 && p.Length != 25)
                        throw new InvalidDataException("잘못된 설정 항목 형식: line " + (i + 1).ToString());

                    int typeValue, sec, x, y, w, h, opacity = 100;
                    if (!int.TryParse(p[0], out typeValue) || !int.TryParse(p[2], out sec) ||
                        !int.TryParse(p[3], out x) || !int.TryParse(p[4], out y) ||
                        !int.TryParse(p[5], out w) || !int.TryParse(p[6], out h))
                        throw new InvalidDataException("손상된 설정 숫자 값: line " + (i + 1).ToString());
                    if (p.Length >= 8) int.TryParse(p[7], out opacity);
                    opacity = Math.Max(0, Math.Min(100, opacity));

                    ItemType type;
                    if (typeValue == 3 || typeValue == 4) type = ItemType.ObsProgram; // migrate v0.2 Projector / Virtual Camera
                    else if (typeValue == (int)ItemType.Web) type = ItemType.Web;
                    else if (typeValue >= 0 && typeValue <= 2) type = (ItemType)typeValue;
                    else throw new InvalidDataException("알 수 없는 오버레이 형식: line " + (i + 1).ToString());
                    if (type == ItemType.ObsProgram) { if (obsLoaded) continue; obsLoaded = true; }

                    string data = Encoding.UTF8.GetString(Convert.FromBase64String(p[1]));
                    if (type == ItemType.ObsProgram) data = "OBS Program";
                    if (type == ItemType.Image && File.Exists(data) && !IsManagedAsset(data))
                    {
                        string migrated = ImportImageAsset(data);
                        if (!string.IsNullOrEmpty(migrated)) data = migrated;
                    }

                    TimerMode timerMode = TimerMode.OneShot;
                    string alarmPath = "";
                    bool locked = false;
                    bool preserveAspect = true;
                    ImageScaleMode scaleMode = ImageScaleMode.Fit;
                    bool visible = true;
                    string customName = "";
                    int rotationDegrees = 0;
                    bool flipHorizontal = false;
                    bool flipVertical = false;
                    int groupId = 0;
                    int cropLeft = 0, cropTop = 0, cropRight = 0, cropBottom = 0;
                    int webZoomPercent = 100; string webCustomCss = "";
                    if (p.Length >= 10 && type == ItemType.Timer)
                    {
                        int modeValue;
                        if (int.TryParse(p[8], out modeValue) && modeValue >= 0 && modeValue <= 2)
                            timerMode = (TimerMode)modeValue;

                        try { alarmPath = Encoding.UTF8.GetString(Convert.FromBase64String(p[9])); }
                        catch { alarmPath = ""; }

                        if (!string.IsNullOrEmpty(alarmPath))
                        {
                            if (File.Exists(alarmPath) && !IsManagedSound(alarmPath))
                            {
                                string migratedSound = ImportSoundAsset(alarmPath);
                                alarmPath = string.IsNullOrEmpty(migratedSound) ? "" : migratedSound;
                            }
                            else if (!File.Exists(alarmPath))
                            {
                                alarmPath = "";
                            }
                        }
                    }
                    if (p.Length >= 13)
                    {
                        locked = p[10] == "1";
                        preserveAspect = p[11] != "0";
                        int scaleValue;
                        if (int.TryParse(p[12], out scaleValue) && scaleValue >= 0 && scaleValue <= 2) scaleMode = (ImageScaleMode)scaleValue;
                    }
                    if (p.Length >= 15)
                    {
                        visible = p[13] != "0";
                        try { customName = Encoding.UTF8.GetString(Convert.FromBase64String(p[14])); } catch { customName = ""; }
                    }
                    if (p.Length >= 19)
                    {
                        int.TryParse(p[15], out rotationDegrees);
                        flipHorizontal = p[16] == "1";
                        flipVertical = p[17] == "1";
                        int.TryParse(p[18], out groupId);
                        groupId = Math.Max(0, groupId);
                    }
                    if (p.Length >= 23)
                    {
                        int.TryParse(p[19], out cropLeft);
                        int.TryParse(p[20], out cropTop);
                        int.TryParse(p[21], out cropRight);
                        int.TryParse(p[22], out cropBottom);
                    }
                    if (p.Length >= 25 && type == ItemType.Web)
                    {
                        int.TryParse(p[23], out webZoomPercent);
                        try { webCustomCss = Encoding.UTF8.GetString(Convert.FromBase64String(p[24])); } catch { webCustomCss = ""; }
                    }

                    CreateItem(type, data, sec, new Rectangle(x, y, Math.Max(100, w), Math.Max(60, h)),
                        opacity, timerMode, alarmPath, locked, preserveAspect, scaleMode, visible, customName, rotationDegrees, flipHorizontal, flipVertical, groupId,
                        cropLeft, cropTop, cropRight, cropBottom, webZoomPercent, webCustomCss);
                }
            foreach (OverlayItemForm f in items) f.RefreshEffectiveVisibility();
            ApplyZOrder();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { Application.RemoveMessageFilter(this); } catch { }
            base.OnFormClosed(e);
        }
    }

    internal static class CrashLog
    {
        public static void WriteText(string where, string text)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatLayer");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "crash.log");
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + where + "]\r\n" + (text ?? "") + "\r\n\r\n");
            }
            catch { }
        }

        public static void Write(Exception ex, string where)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatLayer");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "crash.log");
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + where + "]\r\n" + ex + "\r\n\r\n");
            }
            catch { }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try
            {
                // Match OBS/modern Windows per-monitor DPI awareness so overlay coordinates stay stable.
                if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) Native.SetProcessDPIAware();
            }
            catch { try { Native.SetProcessDPIAware(); } catch { } }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                CrashLog.Write(e.Exception, "UI thread");
                MessageBox.Show("CatLayer caught an error instead of closing.\n\n" + e.Exception.Message + "\n\nLog: %LOCALAPPDATA%\\CatLayer\\crash.log", "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                if (ex != null) CrashLog.Write(ex, "Unhandled");
            };
            string startupArg = "";
            try
            {
                string[] startupArgs = Environment.GetCommandLineArgs();
                if (startupArgs != null && startupArgs.Length > 1) startupArg = startupArgs[1];
            }
            catch { }
            const string mutexName = @"Local\CatLayer.SingleInstance";
            const string reopenEventName = @"Local\CatLayer.ReopenExisting";
            bool createdNew = false;
            Mutex singleInstance = null;
            EventWaitHandle reopenEvent = null;
            try
            {
                singleInstance = new Mutex(true, mutexName, out createdNew);
                if (!createdNew)
                {
                    if (!string.IsNullOrWhiteSpace(startupArg))
                    {
                        try
                        {
                            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatLayer");
                            Directory.CreateDirectory(dir);
                            File.WriteAllText(Path.Combine(dir, "pending_launch.txt"), startupArg, new UTF8Encoding(false));
                        }
                        catch { }
                    }
                    for (int attempt = 0; attempt < 12; attempt++)
                    {
                        try
                        {
                            using (EventWaitHandle existingEvent = EventWaitHandle.OpenExisting(reopenEventName)) existingEvent.Set();
                            break;
                        }
                        catch { if (attempt < 11) Thread.Sleep(50); }
                    }
                    return;
                }
                reopenEvent = new EventWaitHandle(false, EventResetMode.AutoReset, reopenEventName);
                MainForm mainForm = new MainForm();
                IntPtr mainHandle = mainForm.Handle;
                mainForm.ProcessStartupArgument(startupArg);
                if (mainHandle == IntPtr.Zero) throw new InvalidOperationException("메인 창 핸들을 만들지 못했습니다.");
                EventWaitHandle listenerEvent = reopenEvent;
                Thread listener = new Thread(delegate()
                {
                    while (true)
                    {
                        try
                        {
                            listenerEvent.WaitOne();
                            if (mainForm.IsDisposed) return;
                            mainForm.BeginInvoke(new MethodInvoker(delegate { mainForm.RestoreFromSecondaryLaunch(); }));
                        }
                        catch { return; }
                    }
                });
                listener.IsBackground = true; listener.Name = "CatLayer single-instance listener"; listener.Start();
                Application.Run(mainForm);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "Startup/Fatal");
                try
                {
                    MessageBox.Show(
                        "CatLayer could not start.\n\n" + ex.Message +
                        "\n\nCrash log: " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatLayer", "crash.log"),
                        "CatLayer startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
                Environment.ExitCode = 1;
            }
            finally
            {
                try { if (reopenEvent != null) reopenEvent.Close(); } catch { }
                try { if (singleInstance != null) { if (createdNew) singleInstance.ReleaseMutex(); singleInstance.Close(); } } catch { }
            }
        }
    }
}
