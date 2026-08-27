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
        public static readonly string Version = LoadTextFile("VERSION.txt", "unknown");
        public static readonly string BuildLabel = LoadTextFile("BUILD.txt", "");
        public static string DisplayName
        {
            get
            {
                return "CatLayer v" + Version + (string.IsNullOrWhiteSpace(BuildLabel) ? "" : " [" + BuildLabel + "]");
            }
        }

        private static string LoadTextFile(string fileName, string fallback)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                if (File.Exists(path))
                {
                    string value = File.ReadAllText(path, Encoding.ASCII).Trim();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
            catch { }
            return fallback;
        }
    }
    internal static class DesignTheme
    {
        private static readonly Dictionary<string, string> Values = LoadValues();

        public static Color GetColor(string key, Color fallback)
        {
            try
            {
                string value;
                if (!Values.TryGetValue(key ?? "", out value) || string.IsNullOrWhiteSpace(value)) return fallback;
                return ColorTranslator.FromHtml(value.Trim());
            }
            catch { return fallback; }
        }

        private static Dictionary<string, string> LoadValues()
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "design", "theme.ini");
                if (!File.Exists(path)) return values;
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = (raw ?? "").Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("[")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string itemKey = line.Substring(0, eq).Trim();
                    string itemValue = line.Substring(eq + 1).Trim();
                    if (itemKey.Length > 0 && itemValue.Length > 0) values[itemKey] = itemValue;
                }
            }
            catch { }
            return values;
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
        public string ShaName;
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
                bool standardSha = string.Equals(name, "SHA256.txt", StringComparison.OrdinalIgnoreCase);
                bool versionSha = Regex.IsMatch(name, @"^CatLayer_v?" + Regex.Escape(info.Version) + @"_SHA256\.txt$", RegexOptions.IgnoreCase);
                if (standardSha || (versionSha && string.IsNullOrEmpty(info.ShaUrl)))
                {
                    info.ShaName = name;
                    info.ShaUrl = url;
                }
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

    // CatLayer 1.1 release line. Runtime version is read from VERSION.txt.
    internal enum ItemType { Image = 0, Text = 1, Timer = 2, ObsProgram = 3, Web = 5 }
    internal enum TimerMode { OneShot = 0, Repeat = 1, Stopwatch = 2 }
    internal enum ImageScaleMode { Fit = 0, Fill = 1, Stretch = 2 }
    internal enum EditorMode { Fixed = 0, Normal = 1, WebControl = 2, Integrated = 3 }
    internal enum WebInteractionStyle { DoubleClick = 0, SingleClick = 1, Integrated = 2 }

    internal sealed class DarkNumberBox : UserControl
    {
        private readonly TextBox box = new TextBox();
        private readonly Button up = new Button();
        private readonly Button down = new Button();
        private decimal value;
        private decimal minimum = 0;
        private decimal maximum = 100;
        private decimal increment = 1;
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
            b.Click += delegate { Value = Value + (delta * increment); };
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
        public decimal Increment { get { return increment; } set { increment = value <= 0 ? 1 : value; } }
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
        public const int WH_KEYBOARD_LL = 13;
        public const int HC_ACTION = 0;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;
        public const uint LLKHF_ALTDOWN = 0x20;
        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;
        public const int VK_F1 = 0x70;
        public const int VK_F7 = 0x76;
        public const int VK_F8 = 0x77;
        public const int VK_F9 = 0x78;
        public const int VK_F10 = 0x79;
        public const int VK_F11 = 0x7A;

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
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;
        public const uint BI_RGB = 0;
        public const uint DIB_RGB_COLORS = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; public POINT(int X, int Y) { x = X; y = Y; } }
        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; public SIZE(int X, int Y) { cx = X; cy = Y; } }
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

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

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr GetModuleHandle(string lpModuleName);
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
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
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
        [DllImport("gdi32.dll", SetLastError = true)] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);
        [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);
        [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);
        [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(IntPtr hThumbnail, out SIZE pSize);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

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
        // TEST 08: the selection frame is a real per-pixel layered window.
        // Keep a nearly invisible alpha=1 backing surface so resize hit zones remain
        // interactive while avoiding the old TransparencyKey/near-black growth flash.
        private static readonly Color HitSurfaceColor = Color.FromArgb(1, 0, 0, 0);
        // The helper frame intentionally extends farther than the visible border. Most of it
        // remains hit-test transparent, but the extra room lets resize grace work outside the
        // overlay instead of only expanding inward.
        private const int Outside = 80;
        private bool locked;
        private bool interactive;
        private bool fullBorderInteractive;
        private bool alwaysOnTop = true;
        private OverlayItemForm interactiveTarget;
        private long resizeGripGraceUntilUtcTicks;
        private FrameHit resizeGripGraceMode = FrameHit.None;
        private const int ResizeGripNormalRadius = 10;
        private int resizeGripGraceRadius = 30;
        private int resizeGripGraceMs = 500;
        // Poll the physical mouse state while an external resize frame owns a drag.
        // WebView2 uses native child HWNDs and can occasionally make WinForms miss MouseUp;
        // this watchdog keeps the resize following the real cursor and always releases it.
        private readonly Timer frameDragWatchTimer = new Timer();
        private bool frameDragActive;

        // Keep the numeric values aligned with OverlayItemForm.DragMode.
        private enum FrameHit
        {
            None = 0, Move = 1, Left = 2, Right = 3, Top = 4, Bottom = 5,
            TopLeft = 6, TopRight = 7, BottomLeft = 8, BottomRight = 9
        }

        public OverlaySelectionFrameForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = false;
            MouseDown += FrameMouseDown;
            MouseMove += FrameMouseMove;
            MouseUp += FrameMouseUp;

            // TEST 08: selected overlays are covered by this helper frame around their
            // resize border. OLE drag/drop can target the helper instead of the real
            // overlay window, so forward the payload to the same additive CatLayer drop
            // parser. Dropping a new image/link therefore creates another overlay rather
            // than replacing the selected one.
            try
            {
                AllowDrop = true;
                DragEnter += delegate(object sender, DragEventArgs e)
                {
                    if (interactiveTarget != null && !interactiveTarget.IsDisposed) interactiveTarget.ForwardExternalDragEnter(e);
                    else e.Effect = DragDropEffects.None;
                };
                DragOver += delegate(object sender, DragEventArgs e)
                {
                    if (interactiveTarget != null && !interactiveTarget.IsDisposed) interactiveTarget.ForwardExternalDragOver(e);
                    else e.Effect = DragDropEffects.None;
                };
                DragDrop += delegate(object sender, DragEventArgs e)
                {
                    if (interactiveTarget != null && !interactiveTarget.IsDisposed) interactiveTarget.ForwardExternalDragDrop(e);
                };
            }
            catch { }

            frameDragWatchTimer.Interval = 15;
            frameDragWatchTimer.Tick += FrameDragWatchTick;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= unchecked((int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED));
                if (alwaysOnTop) cp.ExStyle |= unchecked((int)Native.WS_EX_TOPMOST);
                else cp.ExStyle &= ~unchecked((int)Native.WS_EX_TOPMOST);
                if (!interactive) cp.ExStyle |= unchecked((int)Native.WS_EX_TRANSPARENT);
                return cp;
            }
        }

        private FrameHit HitResize(Point p, int radius)
        {
            int left = Outside;
            int top = Outside;
            int right = ClientSize.Width - Outside;
            int bottom = ClientSize.Height - Outside;
            if (right <= left || bottom <= top) return FrameHit.None;

            bool inBand = p.X >= left - radius && p.X <= right + radius &&
                          p.Y >= top - radius && p.Y <= bottom + radius;
            if (!inBand) return FrameHit.None;

            bool l = Math.Abs(p.X - left) <= radius;
            bool r = Math.Abs(p.X - right) <= radius;
            bool t = Math.Abs(p.Y - top) <= radius;
            bool b = Math.Abs(p.Y - bottom) <= radius;

            if (t && l) return FrameHit.TopLeft;
            if (t && r) return FrameHit.TopRight;
            if (b && l) return FrameHit.BottomLeft;
            if (b && r) return FrameHit.BottomRight;
            if (l) return FrameHit.Left;
            if (r) return FrameHit.Right;
            if (t) return FrameHit.Top;
            if (b) return FrameHit.Bottom;
            return FrameHit.None;
        }

        private bool IsWithinGraceMode(Point p, FrameHit mode, int radius)
        {
            int left = Outside;
            int top = Outside;
            int right = ClientSize.Width - Outside;
            int bottom = ClientSize.Height - Outside;
            bool xSpan = p.X >= left - radius && p.X <= right + radius;
            bool ySpan = p.Y >= top - radius && p.Y <= bottom + radius;
            bool l = Math.Abs(p.X - left) <= radius;
            bool r = Math.Abs(p.X - right) <= radius;
            bool t = Math.Abs(p.Y - top) <= radius;
            bool b = Math.Abs(p.Y - bottom) <= radius;
            switch (mode)
            {
                case FrameHit.Left: return l && ySpan;
                case FrameHit.Right: return r && ySpan;
                case FrameHit.Top: return t && xSpan;
                case FrameHit.Bottom: return b && xSpan;
                case FrameHit.TopLeft: return l && t;
                case FrameHit.TopRight: return r && t;
                case FrameHit.BottomLeft: return l && b;
                case FrameHit.BottomRight: return r && b;
                default: return false;
            }
        }

        private FrameHit ResizeHitAt(Point p)
        {
            long now = DateTime.UtcNow.Ticks;
            FrameHit direct = HitResize(p, ResizeGripNormalRadius);
            if (direct != FrameHit.None)
            {
                resizeGripGraceMode = direct;
                resizeGripGraceUntilUtcTicks = now + (Math.Max(0, resizeGripGraceMs) * TimeSpan.TicksPerMillisecond);
                return direct;
            }

            // Coyote-style resize grace: after entering any resize edge/corner, preserve that
            // exact resize direction for the configured time while the pointer remains in the
            // configured wider hit zone. Default: 30 px / 500 ms.
            if (resizeGripGraceMode != FrameHit.None && now <= resizeGripGraceUntilUtcTicks &&
                IsWithinGraceMode(p, resizeGripGraceMode, resizeGripGraceRadius))
                return resizeGripGraceMode;

            if (now > resizeGripGraceUntilUtcTicks) resizeGripGraceMode = FrameHit.None;
            return FrameHit.None;
        }

        private bool IsInteractiveBorder(Point p)
        {
            if (ResizeHitAt(p) != FrameHit.None) return true;
            if (!fullBorderInteractive) return false;
            int left = Outside, top = Outside;
            int right = ClientSize.Width - Outside, bottom = ClientSize.Height - Outside;
            const int moveBand = 5;
            bool inBand = p.X >= left - moveBand && p.X <= right + moveBand && p.Y >= top - moveBand && p.Y <= bottom + moveBand;
            bool nearBorder = Math.Abs(p.X - left) <= moveBand || Math.Abs(p.X - right) <= moveBand ||
                              Math.Abs(p.Y - top) <= moveBand || Math.Abs(p.Y - bottom) <= moveBand;
            return inBand && nearBorder;
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
                // Ctrl+drag on normal overlays is reserved for free rotation; let the
                // underlying overlay receive it instead of the helper resize frame.
                if (!fullBorderInteractive && (Control.ModifierKeys & Keys.Control) == Keys.Control)
                {
                    m.Result = new IntPtr(Native.HTTRANSPARENT);
                    return;
                }
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
            ex |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
            if (alwaysOnTop) ex |= Native.WS_EX_TOPMOST;
            else ex &= ~Native.WS_EX_TOPMOST;
            Native.SetExStyle(Handle, ex);
            Native.SetWindowPos(Handle, alwaysOnTop ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        }

        public void SyncToOverlay(Rectangle overlayBounds, bool showFrame, bool isLocked, OverlayItemForm target, bool allowInteractiveBorder, bool targetAlwaysOnTop, int gracePixels, int graceMs)
        {
            if (IsDisposed) return;
            locked = isLocked;
            alwaysOnTop = targetAlwaysOnTop;
            resizeGripGraceRadius = Math.Max(10, Math.Min(Outside, gracePixels));
            resizeGripGraceMs = Math.Max(0, Math.Min(3000, graceMs));
            if (!object.ReferenceEquals(interactiveTarget, target))
            {
                resizeGripGraceMode = FrameHit.None;
                resizeGripGraceUntilUtcTicks = 0;
            }
            interactiveTarget = target;
            fullBorderInteractive = allowInteractiveBorder;
            // Every selected editable overlay gets interactive resize zones on all four edges and corners.
            // Web overlays additionally keep their thin border move zone.
            ApplyInteractionStyle(!isLocked && target != null);
            if (!showFrame || overlayBounds.Width <= 0 || overlayBounds.Height <= 0)
            {
                if (Visible) Hide();
                return;
            }

            Rectangle frameBounds = Rectangle.Inflate(overlayBounds, Outside, Outside);
            RenderLayeredFrame(frameBounds);
            if (!Visible) Show();
            BringFrameToFront();
        }

        public void BringFrameToFront()
        {
            if (!Visible || !IsHandleCreated || IsDisposed) return;
            Native.SetWindowPos(Handle, alwaysOnTop ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        private Cursor CursorForHit(FrameHit hit)
        {
            if (hit == FrameHit.Left || hit == FrameHit.Right) return Cursors.SizeWE;
            if (hit == FrameHit.Top || hit == FrameHit.Bottom) return Cursors.SizeNS;
            if (hit == FrameHit.TopLeft || hit == FrameHit.BottomRight) return Cursors.SizeNWSE;
            if (hit == FrameHit.TopRight || hit == FrameHit.BottomLeft) return Cursors.SizeNESW;
            return Cursors.SizeAll;
        }

        private void FinishFrameDrag()
        {
            if (!frameDragActive) return;
            frameDragActive = false;
            frameDragWatchTimer.Stop();
            OverlayItemForm target = interactiveTarget;
            if (target != null && !target.IsDisposed) target.EndWebFrameDrag();
            try { Capture = false; } catch { }
        }

        private void FrameDragWatchTick(object sender, EventArgs e)
        {
            if (!frameDragActive || !interactive || interactiveTarget == null || interactiveTarget.IsDisposed)
            {
                FinishFrameDrag();
                return;
            }

            // Do not trust MouseUp alone here. Native WebView2 child windows can briefly
            // disturb WinForms capture while their host is resizing. Polling MouseButtons
            // makes both expansion and shrinking follow Cursor.Position continuously.
            if ((Control.MouseButtons & MouseButtons.Left) != MouseButtons.Left)
            {
                FinishFrameDrag();
                return;
            }
            interactiveTarget.ContinueWebFrameDrag();
        }

        private void FrameMouseDown(object sender, MouseEventArgs e)
        {
            if (!interactive || interactiveTarget == null || e.Button != MouseButtons.Left) return;
            FrameHit hit = ResizeHitAt(e.Location);
            if (hit == FrameHit.None && !fullBorderInteractive) return;
            try { Capture = true; } catch { }
            interactiveTarget.BeginSelectionFrameDrag(hit == FrameHit.None ? (int)FrameHit.Move : (int)hit);
            frameDragActive = true;
            frameDragWatchTimer.Start();
        }

        private void FrameMouseMove(object sender, MouseEventArgs e)
        {
            if (!interactive || interactiveTarget == null) return;
            if (frameDragActive && (Control.MouseButtons & MouseButtons.Left) == MouseButtons.Left)
                interactiveTarget.ContinueWebFrameDrag();
            else if (!frameDragActive)
            {
                FrameHit hit = ResizeHitAt(e.Location);
                Cursor = hit != FrameHit.None ? CursorForHit(hit) : (IsInteractiveBorder(e.Location) ? Cursors.SizeAll : Cursors.Default);
            }
        }

        private void FrameMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            FinishFrameDrag();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { frameDragWatchTimer.Stop(); frameDragWatchTimer.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        private void RenderLayeredFrame(Rectangle frameBounds)
        {
            if (frameBounds.Width <= 0 || frameBounds.Height <= 0) return;
            try
            {
                // Accessing Handle here creates the layered HWND while it is still hidden, so
                // its first visible frame already owns a complete alpha surface.
                IntPtr hwnd = Handle;
                int width = Math.Max(1, frameBounds.Width);
                int height = Math.Max(1, frameBounds.Height);
                using (Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        using (SolidBrush hit = new SolidBrush(HitSurfaceColor))
                            g.FillRectangle(hit, 0, 0, width, height);
                        g.CompositingMode = CompositingMode.SourceOver;
                        g.SmoothingMode = SmoothingMode.None;
                        g.PixelOffsetMode = PixelOffsetMode.Half;

                        Color edge = locked ? Color.Gold : Color.FromArgb(235, 80, 200, 255);
                        using (Pen p = new Pen(edge, 2f))
                        {
                            Rectangle r = new Rectangle(Outside - 1, Outside - 1,
                                Math.Max(1, width - (Outside * 2) + 2),
                                Math.Max(1, height - (Outside * 2) + 2));
                            g.DrawRectangle(p, r);
                        }

                        if (!locked)
                        {
                            const int grip = 10;
                            int cornerX = width - Outside;
                            int cornerY = height - Outside;
                            Rectangle gripRect = new Rectangle(cornerX - grip / 2, cornerY - grip / 2, grip, grip);
                            using (Brush b = new SolidBrush(edge)) g.FillRectangle(b, gripRect);
                            using (Pen outline = new Pen(Color.FromArgb(220, 20, 28, 45), 1f)) g.DrawRectangle(outline, gripRect);
                        }
                    }
                    ApplyFrameBitmap(bmp, frameBounds);
                }
                // Update only WinForms' cached geometry. UpdateLayeredWindow already moved and
                // resized the native HWND together with the completed pixels.
                UpdateBounds(frameBounds.X, frameBounds.Y, frameBounds.Width, frameBounds.Height,
                    frameBounds.Width, frameBounds.Height);
            }
            catch { }
        }

        private void ApplyFrameBitmap(Bitmap bitmap, Rectangle frameBounds)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr mem = IntPtr.Zero;
            IntPtr dib = IntPtr.Zero;
            IntPtr dibBits = IntPtr.Zero;
            IntPtr old = IntPtr.Zero;
            BitmapData lockedBits = null;
            try
            {
                mem = Native.CreateCompatibleDC(screen);
                if (mem == IntPtr.Zero) return;

                int width = Math.Max(1, bitmap.Width);
                int height = Math.Max(1, bitmap.Height);
                int rowBytes = checked(width * 4);
                Native.BITMAPINFO info = new Native.BITMAPINFO();
                info.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
                info.bmiHeader.biWidth = width;
                info.bmiHeader.biHeight = -height;
                info.bmiHeader.biPlanes = 1;
                info.bmiHeader.biBitCount = 32;
                info.bmiHeader.biCompression = Native.BI_RGB;
                info.bmiHeader.biSizeImage = (uint)checked(rowBytes * height);

                dib = Native.CreateDIBSection(screen, ref info, Native.DIB_RGB_COLORS, out dibBits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || dibBits == IntPtr.Zero) return;

                lockedBits = bitmap.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                int srcStride = lockedBits.Stride;
                int absStride = Math.Abs(srcStride);
                byte[] row = new byte[rowBytes];
                for (int y = 0; y < height; y++)
                {
                    int sourceY = srcStride >= 0 ? y : (height - 1 - y);
                    IntPtr srcRow = IntPtr.Add(lockedBits.Scan0, sourceY * absStride);
                    Marshal.Copy(srcRow, row, 0, rowBytes);
                    Marshal.Copy(row, 0, IntPtr.Add(dibBits, y * rowBytes), rowBytes);
                }
                bitmap.UnlockBits(lockedBits);
                lockedBits = null;

                old = Native.SelectObject(mem, dib);
                if (old == IntPtr.Zero) return;

                Native.POINT src = new Native.POINT(0, 0);
                Native.POINT dst = new Native.POINT(frameBounds.X, frameBounds.Y);
                Native.SIZE size = new Native.SIZE(width, height);
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = Native.AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;
                Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                if (lockedBits != null)
                {
                    try { bitmap.UnlockBits(lockedBits); } catch { }
                }
                if (old != IntPtr.Zero && mem != IntPtr.Zero) Native.SelectObject(mem, old);
                if (dib != IntPtr.Zero) Native.DeleteObject(dib);
                if (mem != IntPtr.Zero) Native.DeleteDC(mem);
                if (screen != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Per-pixel layered frame: never expose a WinForms fallback background.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Drawing is performed atomically by RenderLayeredFrame/UpdateLayeredWindow.
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
        public bool IsDraggingForEdit { get { return dragging; } }
        public bool Locked { get; private set; }
        public bool PreserveAspect { get; private set; }
        public ImageScaleMode ScaleMode { get; private set; }
        public int RotationDegrees { get; private set; }
        public bool FlipHorizontal { get; private set; }
        public bool FlipVertical { get; private set; }
        // keep the unrotated image size separate from the outer transparent canvas.
        // Rotating changes only the layered-window canvas, never the displayed image scale.
        private int rotationBaseWidth;
        private int rotationBaseHeight;
        private bool rotationAnchorValid;
        private int rotationAnchorLeft;
        private int rotationAnchorTop;
        public int RotationBaseWidth { get { return Math.Max(1, rotationBaseWidth > 0 ? rotationBaseWidth : Width); } }
        public int RotationBaseHeight { get { return Math.Max(1, rotationBaseHeight > 0 ? rotationBaseHeight : Height); } }
        public int GroupId { get; private set; }
        // TEST 11: Hierarchy is intentionally separate from GroupId. Groups are peer selections;
        // hierarchy is a parent/child relationship where moving a parent carries descendants.
        public string ItemId { get; private set; }
        public string ParentItemId { get; private set; }
        public bool AlwaysOnTop { get; private set; }
        public int CropLeft { get; private set; }
        public int CropTop { get; private set; }
        public int CropRight { get; private set; }
        public int CropBottom { get; private set; }
        public int WebZoomPercent { get; private set; }
        public string WebCustomCss { get; private set; }
        public bool SupportsTransform { get { return Type == ItemType.Image || Type == ItemType.Text || Type == ItemType.Timer; } }
        public Size NativeImageSize { get { return image == null ? Size.Empty : new Size(image.Width, image.Height); } }
        private bool UsesPerPixelLayeredSurface { get { return Type != ItemType.ObsProgram && Type != ItemType.Web; } }
        private bool applyingLayeredResize;

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

        public void SetRotationBaseSize(int width, int height)
        {
            if (Type != ItemType.Image) return;
            rotationBaseWidth = Math.Max(1, width);
            rotationBaseHeight = Math.Max(1, height);
        }

        // TEST 12.1: A layered image has two sizes that must stay synchronized:
        // the native HWND/canvas bounds and the logical unrotated render base. Setting only
        // Bounds makes the window resize while the image keeps drawing at the old size until
        // the next manual resize. Update both atomically.
        public void SetImageDisplaySize(int width, int height)
        {
            if (Type != ItemType.Image || IsDisposed) return;
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            rotationBaseWidth = width;
            rotationBaseHeight = height;

            if (RotationDegrees != 0)
            {
                if (!rotationAnchorValid)
                {
                    rotationAnchorLeft = Bounds.Left;
                    rotationAnchorTop = Bounds.Top;
                    rotationAnchorValid = true;
                }
                ApplyImageRotationCanvas();
                return;
            }

            rotationAnchorLeft = Bounds.Left;
            rotationAnchorTop = Bounds.Top;
            rotationAnchorValid = true;
            Rectangle target = owner.NormalizeBounds(new Rectangle(Bounds.Left, Bounds.Top, width, height));

            if (!IsHandleCreated || !overlayVisible || !UsesPerPixelLayeredSurface)
            {
                Bounds = target;
                UpdateSelectionFrame();
                return;
            }

            applyingLayeredResize = true;
            try
            {
                RenderLayeredAt(target);
                UpdateBounds(target.X, target.Y, target.Width, target.Height, target.Width, target.Height);
            }
            finally
            {
                applyingLayeredResize = false;
            }
            UpdateSelectionFrame();
        }

        // TEST 13: Restore a saved visual state without disposing/recreating the overlay Form.
        // This is intentionally internal to the overlay class so private layered-render state stays coherent.
        public void RestoreUndoVisualState(Rectangle savedBounds, int savedRotationBaseWidth, int savedRotationBaseHeight,
            int savedRotationDegrees, bool savedFlipHorizontal, bool savedFlipVertical)
        {
            if (IsDisposed) return;
            Rectangle beforeBounds = Bounds;
            Rectangle target = owner.NormalizeBounds(savedBounds);
            DetailedLog.Write("UNDO_VISUAL",
                "id=" + DetailedLog.ShortId(ItemId) +
                " type=" + Type.ToString() +
                " before=" + DetailedLog.Rect(beforeBounds) +
                " saved=" + DetailedLog.Rect(savedBounds) +
                " normalized=" + DetailedLog.Rect(target) +
                " rot=" + RotationDegrees.ToString() + "->" + savedRotationDegrees.ToString() +
                " base=" + rotationBaseWidth.ToString() + "x" + rotationBaseHeight.ToString() +
                "->" + savedRotationBaseWidth.ToString() + "x" + savedRotationBaseHeight.ToString());
            int normalized = savedRotationDegrees % 360;
            if (normalized > 180) normalized -= 360;
            if (normalized < -180) normalized += 360;

            if (Type == ItemType.Image)
            {
                rotationBaseWidth = Math.Max(1, savedRotationBaseWidth > 0 ? savedRotationBaseWidth : target.Width);
                rotationBaseHeight = Math.Max(1, savedRotationBaseHeight > 0 ? savedRotationBaseHeight : target.Height);
                rotationAnchorLeft = target.Left;
                rotationAnchorTop = target.Top;
                rotationAnchorValid = true;
                RotationDegrees = normalized;
                FlipHorizontal = savedFlipHorizontal;
                FlipVertical = savedFlipVertical;

                if (!IsHandleCreated || !overlayVisible)
                {
                    Bounds = target;
                    UpdateSelectionFrame();
                    return;
                }

                applyingLayeredResize = true;
                try
                {
                    RenderLayeredAt(target);
                    UpdateBounds(target.X, target.Y, target.Width, target.Height, target.Width, target.Height);
                }
                finally { applyingLayeredResize = false; }
                UpdateSelectionFrame();
                return;
            }

            Bounds = target;
            if (SupportsTransform)
            {
                RotationDegrees = normalized;
                FlipHorizontal = savedFlipHorizontal;
                FlipVertical = savedFlipVertical;
                RenderLayered();
            }
            UpdateSelectionFrame();
        }

        private Size GetImageRotationVisibleBaseSize()
        {
            int baseW = RotationBaseWidth;
            int baseH = RotationBaseHeight;
            if (image == null || image.Width <= 0 || image.Height <= 0 || ScaleMode != ImageScaleMode.Fit)
                return new Size(baseW, baseH);

            int srcX = Math.Max(0, Math.Min(image.Width - 1, (int)Math.Round(image.Width * (CropLeft / 10000.0))));
            int srcY = Math.Max(0, Math.Min(image.Height - 1, (int)Math.Round(image.Height * (CropTop / 10000.0))));
            int srcRight = Math.Max(srcX + 1, Math.Min(image.Width, image.Width - (int)Math.Round(image.Width * (CropRight / 10000.0))));
            int srcBottom = Math.Max(srcY + 1, Math.Min(image.Height, image.Height - (int)Math.Round(image.Height * (CropBottom / 10000.0))));
            int srcW = Math.Max(1, srcRight - srcX);
            int srcH = Math.Max(1, srcBottom - srcY);
            double scale = Math.Min(baseW / (double)srcW, baseH / (double)srcH);
            return new Size(
                Math.Max(1, (int)Math.Round(srcW * scale)),
                Math.Max(1, (int)Math.Round(srcH * scale)));
        }

        private Rectangle GetImageRotationCanvasBounds(int degrees, Rectangle currentBounds)
        {
            Size visibleBase = GetImageRotationVisibleBaseSize();
            int visualW = visibleBase.Width;
            int visualH = visibleBase.Height;
            double radians = degrees * Math.PI / 180.0;
            double c = Math.Abs(Math.Cos(radians));
            double si = Math.Abs(Math.Sin(radians));
            int canvasW = Math.Max(MinimumSize.Width, (int)Math.Ceiling(visualW * c + visualH * si));
            int canvasH = Math.Max(MinimumSize.Height, (int)Math.Ceiling(visualW * si + visualH * c));

            int anchorLeft = rotationAnchorValid ? rotationAnchorLeft : currentBounds.Left;
            int anchorTop = rotationAnchorValid ? rotationAnchorTop : currentBounds.Top;

            // remember the pre-rotation X/Y and keep returning to that saved point
            // after the rotation canvas size changes.
            return new Rectangle(anchorLeft, anchorTop, canvasW, canvasH);
        }

        private void ApplyImageRotationCanvas()
        {
            if (Type != ItemType.Image || IsDisposed) return;
            if (rotationBaseWidth <= 0 || rotationBaseHeight <= 0)
            {
                rotationBaseWidth = Math.Max(1, Width);
                rotationBaseHeight = Math.Max(1, Height);
            }
            Rectangle target = GetImageRotationCanvasBounds(RotationDegrees, Bounds);
            if (!IsHandleCreated)
            {
                Bounds = target;
                return;
            }
            if (!overlayVisible)
            {
                Bounds = target;
                return;
            }

            applyingLayeredResize = true;
            try
            {
                RenderLayeredAt(target);
                UpdateBounds(target.X, target.Y, target.Width, target.Height, target.Width, target.Height);
            }
            finally { applyingLayeredResize = false; }
            UpdateSelectionFrame();
        }

        private Rectangle GetSelectionFrameVisualBounds(Rectangle hostBounds)
        {
            // The image HWND itself is now the correctly sized transparent rotation canvas.
            // Using the same bounds keeps the blue frame exactly on that canvas and keeps resize
            // hit-testing aligned with the geometry the user sees.
            if (Type == ItemType.Image) return hostBounds;
            return GetVisualContentBounds(hostBounds);
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
            rotationBaseWidth = Math.Max(1, Bounds.Width);
            rotationBaseHeight = Math.Max(1, Bounds.Height);
        }

        private Image image;
        private bool imageAnimated;
        private bool gifAnimatorActive;
        private EventHandler gifAnimatorHandler;
        private bool gifPaused;
        private int gifManualFrameIndex;
        // ImageAnimator already knows each GIF frame delay. Keep the WinForms timer only as
        // a rare fallback when ImageAnimator cannot start; normal GIF playback is callback driven.
        private readonly Timer gifTimer = new Timer();
        private int gifRenderQueued;
        private long lastGifRenderTicks;
        private const int GifMinimumRenderIntervalMs = 33; // preserve the old ~30fps ceiling
        private readonly Timer renderTimer = new Timer();
        // Reused only for frequently redrawn layered surfaces (animated images/timers).
        // Static overlays keep the old tiny row buffer path so very large still images do not
        // permanently reserve another full-frame byte array.
        private byte[] layeredTransferBuffer;
        private byte[] layeredRowBuffer;
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
        // Capture move/resize undo only after the pointer actually moves. A plain selection
        // click must never add a no-op undo entry, otherwise the first Ctrl+Z appears broken.
        private bool dragUndoCaptured;
        private string dragUndoReason = "";
        private bool programAttachmentDetachedForDrag;
        // Per-pixel layered resize is expensive (GDI+ scale + DIB + UpdateLayeredWindow).
        // Cap live resize rendering at ~50 fps even on 125/500 Hz mice, then always commit
        // the exact final bounds on mouse-up.
        private long lastLayeredResizeRenderTicks;
        private bool pendingLayeredResize;
        private Rectangle pendingLayeredResizeBounds;
        private const int LayeredResizeMinIntervalMs = 20;
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
        // keep the windowed WebView2 host at its old size during resize dragging.
        // Only the external selection frame previews the new size; commit Bounds once on mouse-up.
        private bool webResizePreviewActive;
        private Rectangle webResizePreviewBounds;

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
        private DragMode resizeGraceMode = DragMode.None;
        private long resizeGraceUntilUtcTicks;
        private const int Grip = 10;

        // Release build: resize tracing was a temporary diagnostic and is intentionally disabled.
        private bool resizeTraceActive { get { return false; } }
        private void ResizeTraceStart(DragMode mode) { }
        private void ResizeTraceAdd(string stage, Rectangle target) { }
        private void ResizeTraceFinish(string reason) { }

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
            ItemId = Guid.NewGuid().ToString("N");
            ParentItemId = "";
            AlwaysOnTop = true;
            gifPaused = false;
            gifManualFrameIndex = 0;
            CropLeft = CropTop = CropRight = CropBottom = 0;
            WebZoomPercent = 100;
            WebCustomCss = "";

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(100, 60);
            BackColor = Type == ItemType.ObsProgram ? ObsTransparentKey : (Type == ItemType.Web ? WebTransparentKey : Color.FromArgb(18, 18, 18));
            if (Type == ItemType.Web) TransparencyKey = WebTransparentKey;
            DoubleBuffered = Type != ItemType.ObsProgram && Type != ItemType.Web;
            KeyPreview = true;

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
                        gifAnimatorHandler = OnGifFrameChanged;
                        gifTimer.Interval = GifMinimumRenderIntervalMs;
                        gifTimer.Tick += delegate
                        {
                            // Fallback only. Normal playback is driven by ImageAnimator's real
                            // frame-delay callback instead of repainting every 33 ms unconditionally.
                            try
                            {
                                if (overlayVisible && image != null && !gifAnimatorActive)
                                {
                                    ImageAnimator.UpdateFrames(image);
                                    RenderLayered();
                                }
                            }
                            catch { }
                        };
                        try { ImageAnimator.Animate(image, gifAnimatorHandler); gifAnimatorActive = true; } catch { gifAnimatorActive = false; }
                        if (!gifAnimatorActive) gifTimer.Start();
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

            // Accept CatLayer-supported files/links directly on overlay windows as well as the main window.
            // This is especially important for .html/.htm/.catlayerweb drops because users naturally
            // drop a widget onto the desktop overlay area instead of the CatLayer control window.
            try
            {
                AllowDrop = true;
                DragEnter += delegate(object sender, DragEventArgs e) { ForwardExternalDragEnter(e); };
                DragOver += delegate(object sender, DragEventArgs e) { ForwardExternalDragOver(e); };
                DragDrop += delegate(object sender, DragEventArgs e) { ForwardExternalDragDrop(e); };
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

                if (imageAnimated)
                {
                    ToolStripMenuItem gifMenu = new ToolStripMenuItem("GIF 컨트롤");
                    ToolStripMenuItem gifPlayPause = new ToolStripMenuItem(gifPaused ? "재생" : "일시정지");
                    ToolStripMenuItem gifPrevious = new ToolStripMenuItem("이전 프레임");
                    ToolStripMenuItem gifNext = new ToolStripMenuItem("다음 프레임");
                    ToolStripMenuItem gifFirst = new ToolStripMenuItem("첫 프레임");
                    gifPlayPause.Click += delegate { ToggleGifPlayback(); };
                    gifPrevious.Click += delegate { StepGifFrame(-1); };
                    gifNext.Click += delegate { StepGifFrame(1); };
                    gifFirst.Click += delegate { ResetGifFrame(); };
                    gifMenu.DropDownItems.Add(gifPlayPause);
                    gifMenu.DropDownItems.Add(new ToolStripSeparator());
                    gifMenu.DropDownItems.Add(gifPrevious);
                    gifMenu.DropDownItems.Add(gifNext);
                    gifMenu.DropDownItems.Add(gifFirst);
                    gifMenu.DropDownOpening += delegate { gifPlayPause.Text = gifPaused ? "재생" : "일시정지"; };
                    menu.Items.Add(gifMenu);
                }

                menu.Opening += delegate
                {
                    flipImageHorizontal.Checked = FlipHorizontal;
                    flipImageVertical.Checked = FlipVertical;
                };
            }

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem remoteQuickOpen = new ToolStripMenuItem("리모컨 열기");
            remoteQuickOpen.Click += delegate { owner.ShowRemoteControlFromOverlay(); };
            menu.Items.Add(remoteQuickOpen);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem presetQuickLoad = new ToolStripMenuItem("프리셋 불러오기..."); presetQuickLoad.Click += delegate { owner.LoadPresetInteractive(); };
            ToolStripMenuItem groupQuickLoad = new ToolStripMenuItem("그룹 불러오기..."); groupQuickLoad.Click += delegate { owner.LoadGroupInteractive(); };
            menu.Items.Add(presetQuickLoad);
            menu.Items.Add(groupQuickLoad);
            ToolStripMenuItem programMagnet = new ToolStripMenuItem("프로그램 자석..."); programMagnet.Click += delegate { owner.AttachOverlayToProgramInteractive(this); };
            ToolStripMenuItem programMagnetOff = new ToolStripMenuItem("프로그램 자석 해제"); programMagnetOff.Click += delegate { owner.DetachOverlayFromProgram(this); };
            menu.Items.Add(programMagnet); menu.Items.Add(programMagnetOff);
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

            ToolStripMenuItem hierarchyMenu = new ToolStripMenuItem("Hierarchy 부모/자식");
            ToolStripMenuItem setParent = new ToolStripMenuItem("선택 항목의 부모로 지정");
            ToolStripMenuItem clearParent = new ToolStripMenuItem("부모 해제");
            setParent.Click += delegate { owner.SetHierarchyParentFromSelection(this); };
            clearParent.Click += delegate { owner.ClearHierarchyParent(this); };
            hierarchyMenu.DropDownItems.Add(setParent);
            hierarchyMenu.DropDownItems.Add(clearParent);
            hierarchyMenu.DropDownOpening += delegate
            {
                setParent.Text = owner.GetHierarchyParentMenuText(this);
                setParent.Enabled = owner.CanSetHierarchyParentFromSelection(this);
            };
            menu.Items.Add(hierarchyMenu);
            ToolStripMenuItem autoArrange = new ToolStripMenuItem("선택 항목 자동 정렬");
            autoArrange.Click += delegate { owner.AutoArrangeSelectedOverlays(); };
            menu.Items.Add(autoArrange);

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

            ToolStripMenuItem alwaysOnTopItem = new ToolStripMenuItem("항상 위 (TopMost)");
            alwaysOnTopItem.CheckOnClick = true;
            alwaysOnTopItem.Checked = AlwaysOnTop;
            alwaysOnTopItem.Click += delegate
            {
                owner.CaptureUndo("항상 위 변경");
                SetAlwaysOnTop(alwaysOnTopItem.Checked, true);
            };
            menu.Items.Add(alwaysOnTopItem);

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
                cp.ExStyle |= unchecked((int)Native.WS_EX_TOOLWINDOW);
                if (AlwaysOnTop) cp.ExStyle |= unchecked((int)Native.WS_EX_TOPMOST);
                else cp.ExStyle &= ~unchecked((int)Native.WS_EX_TOPMOST);
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
            if ((Type == ItemType.ObsProgram || UsesPerPixelLayeredSurface) && m.Msg == 0x0014) // WM_ERASEBKGND
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
            if (resizeTraceActive)
            {
                if (m.Msg == 0x0005) ResizeTraceAdd("WM_SIZE:before", Bounds);
                else if (m.Msg == 0x0046) ResizeTraceAdd("WM_WINDOWPOSCHANGING:before", Bounds);
                else if (m.Msg == 0x0047) ResizeTraceAdd("WM_WINDOWPOSCHANGED:before", Bounds);
                else if (m.Msg == 0x000F) ResizeTraceAdd("WM_PAINT:before", Bounds);
                else if (m.Msg == 0x0014) ResizeTraceAdd("WM_ERASEBKGND:before", Bounds);
            }
            base.WndProc(ref m);
            if (resizeTraceActive)
            {
                if (m.Msg == 0x0005) ResizeTraceAdd("WM_SIZE:after", Bounds);
                else if (m.Msg == 0x0046) ResizeTraceAdd("WM_WINDOWPOSCHANGING:after", Bounds);
                else if (m.Msg == 0x0047) ResizeTraceAdd("WM_WINDOWPOSCHANGED:after", Bounds);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Type == ItemType.ObsProgram || UsesPerPixelLayeredSurface) return;
            base.OnPaintBackground(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (UsesPerPixelLayeredSurface) return;
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
            if (resizeTraceActive) ResizeTraceAdd("OnSizeChanged:enter", Bounds);
            base.OnSizeChanged(e);
            if (!IsHandleCreated) return;
            if (applyingLayeredResize)
            {
                UpdateSelectionFrame();
                return;
            }
            if (Type == ItemType.ObsProgram) { UpdateObsChildRect(); Invalidate(); }
            else if (Type == ItemType.Web) ScheduleWebResizeTransparencyRefresh();
            else RenderLayered();
            UpdateSelectionFrame();
            if (resizeTraceActive) ResizeTraceAdd("OnSizeChanged:exit", Bounds);
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
                Native.SetWindowPos(Handle, AlwaysOnTop ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0,
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

            if (AlwaysOnTop) ex |= Native.WS_EX_TOPMOST;
            else ex &= ~Native.WS_EX_TOPMOST;

            Native.SetExStyle(Handle, ex);
            if (Type == ItemType.ObsProgram && obsAttached) ApplyObsDestinationTransparency();
            Native.SetWindowPos(Handle, AlwaysOnTop ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

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
            bool integratedWebFrame = Type == ItemType.Web && owner.IntegratedMode;
            bool showFrame = IsHandleCreated && !IsDisposed && Visible && overlayVisible && (editMode || integratedWebFrame) &&
                owner.ShouldShowSelectionVisuals && owner.IsOverlaySelected(this);
            Rectangle visualBounds = GetSelectionFrameVisualBounds(Bounds);
            selectionFrame.SyncToOverlay(visualBounds, showFrame, Locked, this, Type == ItemType.Web && (editMode || integratedWebFrame), AlwaysOnTop, owner.ResizeGracePixels, owner.ResizeGraceMilliseconds);
        }

        private void PreviewWebResizeFrame(Rectangle previewBounds)
        {
            if (Type != ItemType.Web || selectionFrame == null || selectionFrame.IsDisposed) return;
            bool integratedWebFrame = owner.IntegratedMode;
            bool showFrame = IsHandleCreated && !IsDisposed && Visible && overlayVisible && (editMode || integratedWebFrame) &&
                owner.ShouldShowSelectionVisuals && owner.IsOverlaySelected(this);
            selectionFrame.SyncToOverlay(previewBounds, showFrame, Locked, this, (editMode || integratedWebFrame), AlwaysOnTop, owner.ResizeGracePixels, owner.ResizeGraceMilliseconds);
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

        public void SetAlwaysOnTop(bool value, bool save)
        {
            AlwaysOnTop = value;
            TopMost = value;

            if (IsHandleCreated && !IsDisposed)
            {
                long ex = Native.GetExStyle(Handle);
                if (value) ex |= Native.WS_EX_TOPMOST;
                else ex &= ~Native.WS_EX_TOPMOST;
                Native.SetExStyle(Handle, ex);
                Native.SetWindowPos(Handle, value ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
            }

            UpdateSelectionFrame();
            owner.ReapplyZOrder();
            if (save) owner.SaveConfig();
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
                if (overlayVisible && !gifPaused)
                {
                    if (!gifAnimatorActive && image != null && gifAnimatorHandler != null)
                    {
                        try { ImageAnimator.Animate(image, gifAnimatorHandler); gifAnimatorActive = true; } catch { gifAnimatorActive = false; }
                    }
                    if (gifAnimatorActive) gifTimer.Stop();
                    else if (!gifTimer.Enabled) gifTimer.Start();
                }
                else if (gifPaused)
                {
                    StopGifAnimatorForManualControl();
                }
                else
                {
                    gifTimer.Stop();
                    System.Threading.Interlocked.Exchange(ref gifRenderQueued, 0);
                    System.Threading.Interlocked.Exchange(ref lastGifRenderTicks, 0);
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

        private void OnGifFrameChanged(object sender, EventArgs e)
        {
            if (!imageAnimated || !gifAnimatorActive || !overlayVisible || image == null || IsDisposed || !IsHandleCreated) return;

            // ImageAnimator may invoke this from a worker thread. Coalesce callbacks and keep the
            // previous ~30fps maximum so pathological 5-10ms GIF delays cannot flood the UI queue.
            long now = Stopwatch.GetTimestamp();
            long last = System.Threading.Interlocked.Read(ref lastGifRenderTicks);
            long minTicks = Math.Max(1L, (Stopwatch.Frequency * GifMinimumRenderIntervalMs) / 1000L);
            if (last != 0 && now - last < minTicks) return;
            if (System.Threading.Interlocked.Exchange(ref gifRenderQueued, 1) != 0) return;

            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    System.Threading.Interlocked.Exchange(ref gifRenderQueued, 0);
                    if (!imageAnimated || !gifAnimatorActive || !overlayVisible || image == null || IsDisposed || !IsHandleCreated) return;
                    long renderNow = Stopwatch.GetTimestamp();
                    long previous = System.Threading.Interlocked.Read(ref lastGifRenderTicks);
                    if (previous != 0 && renderNow - previous < minTicks) return;
                    try
                    {
                        ImageAnimator.UpdateFrames(image);
                        System.Threading.Interlocked.Exchange(ref lastGifRenderTicks, renderNow);
                        RenderLayered();
                    }
                    catch { }
                });
            }
            catch
            {
                System.Threading.Interlocked.Exchange(ref gifRenderQueued, 0);
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

            if (Type == ItemType.Image && (rotationBaseWidth <= 0 || rotationBaseHeight <= 0))
            {
                rotationBaseWidth = Math.Max(1, Width);
                rotationBaseHeight = Math.Max(1, Height);
            }

            if (Type == ItemType.Image)
            {
                if (normalized != 0)
                {
                    if (!rotationAnchorValid || RotationDegrees == 0)
                    {
                        rotationAnchorLeft = Bounds.Left;
                        rotationAnchorTop = Bounds.Top;
                        rotationAnchorValid = true;
                    }
                }
                else
                {
                    rotationAnchorLeft = Bounds.Left;
                    rotationAnchorTop = Bounds.Top;
                    rotationAnchorValid = true;
                }
            }

            RotationDegrees = normalized;
            FlipHorizontal = flipHorizontal;
            FlipVertical = flipVertical;

            if (Type == ItemType.Image) ApplyImageRotationCanvas();
            else
            {
                RenderLayered();
                UpdateSelectionFrame();
            }
            if (save) owner.SaveConfig();
        }

        public void SetGroupId(int groupId, bool save)
        {
            GroupId = Math.Max(0, groupId);
            if (save) owner.SaveConfig();
        }

        public void SetHierarchyIdentity(string itemId, string parentItemId)
        {
            string normalized = (itemId ?? "").Trim();
            ItemId = normalized.Length == 0 ? Guid.NewGuid().ToString("N") : normalized;
            ParentItemId = (parentItemId ?? "").Trim();
            if (string.Equals(ItemId, ParentItemId, StringComparison.OrdinalIgnoreCase)) ParentItemId = "";
        }

        public void SetParentItemId(string parentItemId, bool save)
        {
            ParentItemId = (parentItemId ?? "").Trim();
            if (string.Equals(ItemId, ParentItemId, StringComparison.OrdinalIgnoreCase)) ParentItemId = "";
            if (save) owner.SaveConfig();
        }

        public bool IsAnimatedImage { get { return Type == ItemType.Image && imageAnimated; } }
        public bool IsGifPaused { get { return imageAnimated && gifPaused; } }

        private int GetGifFrameCount()
        {
            if (!imageAnimated || image == null) return 0;
            try { return image.GetFrameCount(FrameDimension.Time); } catch { return 0; }
        }

        private void StopGifAnimatorForManualControl()
        {
            gifTimer.Stop();
            if (gifAnimatorActive && image != null && gifAnimatorHandler != null)
            {
                try { ImageAnimator.StopAnimate(image, gifAnimatorHandler); } catch { }
            }
            gifAnimatorActive = false;
            System.Threading.Interlocked.Exchange(ref gifRenderQueued, 0);
            System.Threading.Interlocked.Exchange(ref lastGifRenderTicks, 0);
        }

        public void ToggleGifPlayback()
        {
            if (!imageAnimated || image == null) return;
            gifPaused = !gifPaused;
            if (gifPaused) StopGifAnimatorForManualControl();
            else UpdateRenderActivity();
            if (overlayVisible) RenderLayered();
            owner.ReportStatus(gifPaused ? "GIF 일시정지" : "GIF 재생");
        }

        public void StepGifFrame(int delta)
        {
            if (!imageAnimated || image == null) return;
            int count = GetGifFrameCount();
            if (count <= 0) return;
            gifPaused = true;
            StopGifAnimatorForManualControl();
            gifManualFrameIndex = ((gifManualFrameIndex + delta) % count + count) % count;
            try { image.SelectActiveFrame(FrameDimension.Time, gifManualFrameIndex); } catch { return; }
            if (overlayVisible) RenderLayered();
            owner.ReportStatus("GIF 프레임 " + (gifManualFrameIndex + 1).ToString() + "/" + count.ToString());
        }

        public void ResetGifFrame()
        {
            if (!imageAnimated || image == null) return;
            gifPaused = true;
            StopGifAnimatorForManualControl();
            gifManualFrameIndex = 0;
            try { image.SelectActiveFrame(FrameDimension.Time, 0); } catch { }
            if (overlayVisible) RenderLayered();
            owner.ReportStatus("GIF 첫 프레임");
        }

        internal void ForwardExternalDragEnter(DragEventArgs e)
        {
            if (!editMode || e == null)
            {
                if (e != null) e.Effect = DragDropEffects.None;
                return;
            }
            owner.HandleExternalOverlayDragEnter(e);
        }

        internal void ForwardExternalDragOver(DragEventArgs e)
        {
            if (!editMode || e == null)
            {
                if (e != null) e.Effect = DragDropEffects.None;
                return;
            }
            owner.HandleExternalOverlayDragOver(e);
        }

        internal void ForwardExternalDragDrop(DragEventArgs e)
        {
            if (!editMode || e == null) return;
            owner.HandleExternalOverlayDragDrop(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // When the embedded browser owns focus, let normal web shortcuts (typing,
            // Ctrl+C/V, scrolling, forms, etc.) reach WebView2. Global CatLayer hotkeys
            // such as F8/F9/F10/F11 are registered at the OS level and still work.
            if (Type == ItemType.Web && webView != null && webView.ContainsFocus)
                return base.ProcessCmdKey(ref msg, keyData);
            if (owner.HandleUndoShortcut(keyData)) return true;
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
            RenderLayeredAt(new Rectangle(Left, Top, Width, Height));
        }

        private void RenderLayeredAt(Rectangle targetBounds)
        {
            ResizeTraceAdd("RenderLayeredAt:enter", targetBounds);
            int renderWidth = Math.Max(1, targetBounds.Width);
            int renderHeight = Math.Max(1, targetBounds.Height);
            if (!overlayVisible || !IsHandleCreated || Type == ItemType.ObsProgram || Type == ItemType.Web || targetBounds.Width <= 0 || targetBounds.Height <= 0) return;
            using (Bitmap bmp = new Bitmap(renderWidth, renderHeight, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                // UpdateLayeredWindow with AC_SRC_ALPHA expects premultiplied alpha.
                // Clear the entire resized surface with SourceCopy so newly-added pixels are
                // guaranteed to remain A=0 instead of inheriting an opaque fallback color.
                g.CompositingMode = CompositingMode.SourceCopy;
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceOver;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                int contentWidth = (Type == ItemType.Image && rotationBaseWidth > 0) ? RotationBaseWidth : renderWidth;
                int contentHeight = (Type == ItemType.Image && rotationBaseHeight > 0) ? RotationBaseHeight : renderHeight;
                RectangleF area = new RectangleF(
                    (renderWidth - contentWidth) / 2f,
                    (renderHeight - contentHeight) / 2f,
                    Math.Max(1, contentWidth),
                    Math.Max(1, contentHeight));

                if (SupportsTransform && (RotationDegrees != 0 || FlipHorizontal || FlipVertical))
                {
                    g.TranslateTransform(renderWidth / 2f, renderHeight / 2f);
                    if (RotationDegrees != 0) g.RotateTransform(RotationDegrees);
                    if (FlipHorizontal || FlipVertical) g.ScaleTransform(FlipHorizontal ? -1f : 1f, FlipVertical ? -1f : 1f);
                    g.TranslateTransform(-renderWidth / 2f, -renderHeight / 2f);
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
                    using (Bitmap textLayer = new Bitmap(renderWidth, renderHeight, PixelFormat.Format32bppPArgb))
                    using (Graphics tg = Graphics.FromImage(textLayer))
                    {
                        tg.Clear(Color.Transparent);
                        tg.SmoothingMode = SmoothingMode.AntiAlias;
                        tg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                        float fontSize = Math.Max(14f, Math.Min(160f, renderHeight * 0.42f));
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
                            g.DrawImage(textLayer, new Rectangle(0, 0, renderWidth, renderHeight), 0, 0, renderWidth, renderHeight, GraphicsUnit.Pixel, attrs);
                        }
                    }
                }

                g.ResetTransform();
                g.ResetClip();
                if (resizeTraceActive)
                {
                    Color p1 = bmp.GetPixel(0, 0);
                    Color p2 = bmp.GetPixel(Math.Max(0, bmp.Width - 1), 0);
                    Color p3 = bmp.GetPixel(0, Math.Max(0, bmp.Height - 1));
                    Color p4 = bmp.GetPixel(Math.Max(0, bmp.Width - 1), Math.Max(0, bmp.Height - 1));
                    ResizeTraceAdd("surface fmt=" + bmp.PixelFormat.ToString() +
                        " cornerA=" + p1.A.ToString() + "," + p2.A.ToString() + "," + p3.A.ToString() + "," + p4.A.ToString(), targetBounds);
                }
                ResizeTraceAdd("before ApplyBitmap", targetBounds);
                ApplyBitmap(bmp, targetBounds.Location);
                ResizeTraceAdd("after ApplyBitmap", targetBounds);
            }
            ResizeTraceAdd("RenderLayeredAt:exit", targetBounds);
        }

        private void ApplyBitmap(Bitmap bitmap)
        {
            ApplyBitmap(bitmap, new Point(Left, Top));
        }

        private void ApplyBitmap(Bitmap bitmap, Point screenLocation)
        {
            Bitmap surface = bitmap;
            bool disposeSurface = false;
            if (surface.PixelFormat != PixelFormat.Format32bppPArgb)
            {
                Bitmap converted = new Bitmap(surface.Width, surface.Height, PixelFormat.Format32bppPArgb);
                using (Graphics cg = Graphics.FromImage(converted))
                {
                    cg.CompositingMode = CompositingMode.SourceCopy;
                    using (SolidBrush clearBrush = new SolidBrush(Color.Transparent))
                        cg.FillRectangle(clearBrush, 0, 0, converted.Width, converted.Height);
                    cg.CompositingMode = CompositingMode.SourceOver;
                    cg.DrawImageUnscaled(surface, 0, 0);
                }
                surface = converted;
                disposeSurface = true;
            }

            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr mem = IntPtr.Zero;
            IntPtr dib = IntPtr.Zero;
            IntPtr dibBits = IntPtr.Zero;
            IntPtr old = IntPtr.Zero;
            BitmapData locked = null;
            try
            {
                mem = Native.CreateCompatibleDC(screen);
                if (mem == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed.");

                int width = Math.Max(1, surface.Width);
                int height = Math.Max(1, surface.Height);
                int rowBytes = checked(width * 4);

                Native.BITMAPINFO info = new Native.BITMAPINFO();
                info.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
                info.bmiHeader.biWidth = width;
                info.bmiHeader.biHeight = -height; // top-down BGRA
                info.bmiHeader.biPlanes = 1;
                info.bmiHeader.biBitCount = 32;
                info.bmiHeader.biCompression = Native.BI_RGB;
                info.bmiHeader.biSizeImage = (uint)checked(rowBytes * height);

                dib = Native.CreateDIBSection(screen, ref info, Native.DIB_RGB_COLORS, out dibBits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || dibBits == IntPtr.Zero)
                    throw new InvalidOperationException("CreateDIBSection failed.");

                Rectangle rect = new Rectangle(0, 0, width, height);
                locked = surface.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                int srcStride = locked.Stride;
                int absStride = Math.Abs(srcStride);
                bool frequentSurface = Type == ItemType.Timer || (Type == ItemType.Image && imageAnimated);
                if (frequentSurface && srcStride == rowBytes)
                {
                    int totalBytes = checked(rowBytes * height);
                    if (layeredTransferBuffer == null || layeredTransferBuffer.Length < totalBytes)
                        layeredTransferBuffer = new byte[totalBytes];
                    // Two bulk marshals replace 2 * height marshals on every animated/timer frame.
                    Marshal.Copy(locked.Scan0, layeredTransferBuffer, 0, totalBytes);
                    Marshal.Copy(layeredTransferBuffer, 0, dibBits, totalBytes);
                }
                else
                {
                    if (layeredRowBuffer == null || layeredRowBuffer.Length < rowBytes)
                        layeredRowBuffer = new byte[rowBytes];
                    for (int y = 0; y < height; y++)
                    {
                        int sourceY = srcStride >= 0 ? y : (height - 1 - y);
                        IntPtr srcRow = IntPtr.Add(locked.Scan0, sourceY * absStride);
                        Marshal.Copy(srcRow, layeredRowBuffer, 0, rowBytes);
                        Marshal.Copy(layeredRowBuffer, 0, IntPtr.Add(dibBits, y * rowBytes), rowBytes);
                    }
                }
                surface.UnlockBits(locked);
                locked = null;

                old = Native.SelectObject(mem, dib);
                if (old == IntPtr.Zero) throw new InvalidOperationException("SelectObject failed.");

                Native.POINT src = new Native.POINT(0, 0);
                Native.POINT dst = new Native.POINT(screenLocation.X, screenLocation.Y);
                Native.SIZE size = new Native.SIZE(width, height);
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = Native.AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;

                bool ulwOk = Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, Native.ULW_ALPHA);
                if (resizeTraceActive)
                {
                    int err = ulwOk ? 0 : Marshal.GetLastWin32Error();
                    ResizeTraceAdd("DIB UpdateLayeredWindow ok=" + (ulwOk ? "1" : "0") + " err=" + err.ToString(),
                        new Rectangle(screenLocation.X, screenLocation.Y, width, height));
                }
            }
            catch (Exception ex)
            {
                if (resizeTraceActive)
                    ResizeTraceAdd("DIB ApplyBitmap exception=" + ex.GetType().Name + ":" + ex.Message,
                        new Rectangle(screenLocation.X, screenLocation.Y, bitmap.Width, bitmap.Height));
            }
            finally
            {
                if (locked != null)
                {
                    try { surface.UnlockBits(locked); } catch { }
                }
                if (old != IntPtr.Zero && mem != IntPtr.Zero) Native.SelectObject(mem, old);
                if (dib != IntPtr.Zero) Native.DeleteObject(dib);
                if (mem != IntPtr.Zero) Native.DeleteDC(mem);
                if (screen != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screen);
                if (disposeSurface) surface.Dispose();
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
            webView.Enter += delegate { owner.MarkQuickHideTarget(this); if (editMode) owner.SelectOverlayForEditing(this); };
            webView.GotFocus += delegate { owner.MarkQuickHideTarget(this); if (editMode) owner.SelectOverlayForEditing(this); };
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

            string candidateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatLayer", "WebFiles", id);
            try
            {
                string fullRoot = Path.GetFullPath(candidateRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string fullEntry = Path.GetFullPath(Path.Combine(fullRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!fullEntry.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return false;
                if (!Directory.Exists(fullRoot) || !File.Exists(fullEntry)) return false;
                rootFolder = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return false; }

            relativeEntry = rel;
            hostName = "catlayer-" + id.ToLowerInvariant() + ".local";
            return true;
        }

        internal static bool TryNormalizeWebUrl(string input, out string normalized)
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
var bad='object,embed,applet,frame,frameset,iframe';
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

        private static string PinterestDragBridgeScript()
        {
            // Pinterest makes the close-up container draggable while the IMG itself is
            // draggable=false. Rewrite only that drag payload to the best pin image URL.
            return @"(function(){
'use strict';
function isPinterest(){try{return /(^|\.)pinterest\./i.test(location.hostname);}catch(e){return false;}}
function bestImage(img){
  try{
    var best='',bestW=-1,ss=img.getAttribute('srcset')||'';
    ss.split(',').forEach(function(part){
      var bits=part.trim().split(/\s+/),u=bits[0]||'',w=0;
      if(bits.length>1){var m=/^(\d+(?:\.\d+)?)w$/i.exec(bits[1]);if(m)w=parseFloat(m[1]);}
      if(u&&w>=bestW){best=u;bestW=w;}
    });
    return best||img.currentSrc||img.src||'';
  }catch(e){return '';}
}
function findCloseupImage(target){
  try{
    var root=target&&target.closest?target.closest('[data-test-id=closeup-image],[data-pin-drag-id]'):null;
    if(!root)return null;
    return root.querySelector('[data-test-id=closeup-image-main] img, img[elementtiming^=closeup-image-main], img');
  }catch(e){return null;}
}
function start(){
  if(!isPinterest())return;
  document.addEventListener('dragstart',function(e){
    try{
      if(!e||!e.dataTransfer)return;
      var img=findCloseupImage(e.target);if(!img)return;
      var u=bestImage(img);if(!/^https?:\/\//i.test(u))return;
      e.dataTransfer.setData('text/uri-list',u);
      e.dataTransfer.setData('text/plain',u);
    }catch(x){}
  },true);
}
if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',start,{once:true});else start();
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

        private static void TrySetBooleanProperty(object target, string propertyName, bool value)
        {
            if (target == null || string.IsNullOrWhiteSpace(propertyName)) return;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (property != null && property.CanWrite && property.PropertyType == typeof(bool)) property.SetValue(target, value, null);
            }
            catch { }
        }

        private void ApplyOptionalWebSecuritySettings(CoreWebView2Settings settings)
        {
            // These properties vary by WebView2 SDK/runtime version, so use reflection and
            // silently skip them when unavailable instead of making CatLayer require a newer SDK.
            TrySetBooleanProperty(settings, "IsPasswordAutosaveEnabled", false);
            TrySetBooleanProperty(settings, "IsGeneralAutofillEnabled", false);
            try
            {
                PropertyInfo controllerProperty = webView == null ? null : webView.GetType().GetProperty("CoreWebView2Controller", BindingFlags.Instance | BindingFlags.Public);
                object controller = controllerProperty == null ? null : controllerProperty.GetValue(webView, null);
                TrySetBooleanProperty(controller, "AllowExternalDrop", false);
            }
            catch { }
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
                ApplyOptionalWebSecuritySettings(settings);
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
                await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(PinterestDragBridgeScript());

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
                            string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase);
                        if (!allowed) { e.Cancel = true; owner.ReportStatus("웹에서 허용되지 않은 주소 형식을 차단했습니다."); }
                    }
                    catch { try { e.Cancel = true; } catch { } }
                };

                webView.CoreWebView2.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e)
                {
                    try
                    {
                        // Security policy: CatLayer never hands a popup/new-tab request to the OS
                        // and never creates another WebView from it. The request ends here.
                        e.Handled = true;
                        owner.ReportStatus("웹 새 창/새 탭 열기를 차단했습니다.");
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
                                    Native.SetWindowPos(Handle, AlwaysOnTop ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0,
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
                // TEST 11.1: keep only WebView2's host surface transparent. Do NOT force
                // html/body to transparent: the page decides which regions are transparent.
                // This preserves normal website backgrounds while allowing chat/widgets that
                // explicitly use transparent CSS to blend with the desktop.
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

        public bool NeedsWebStartupRecovery
        {
            get
            {
                if (Type != ItemType.Web) return false;
                try { return !webInitialized || webView == null || webView.IsDisposed || webView.CoreWebView2 == null; }
                catch { return true; }
            }
        }

        public void BeginSelectionFrameDrag(int modeValue)
        {
            bool integratedWebEdit = Type == ItemType.Web && owner.IntegratedMode;
            if ((!editMode && !integratedWebEdit) || Locked) return;

            DragMode requested = DragMode.Move;
            if (modeValue >= (int)DragMode.Left && modeValue <= (int)DragMode.BottomRight)
                requested = (DragMode)modeValue;
            bool resize = requested != DragMode.Move;

            owner.MarkQuickHideTarget(this);
            owner.SelectOverlayForEditing(this);
            // Do not detach a program magnet on mouse-down alone. OnMouseMoveOverlay detaches
            // only after a real move, matching the normal overlay drag path.
            programAttachmentDetachedForDrag = false;
            bool shiftCrop = resize && Type == ItemType.Image && (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
            dragUndoCaptured = false;
            dragUndoReason = shiftCrop ? "이미지 자르기" : (resize ? "오버레이 크기 변경" : "오버레이 이동");
            dragging = true;
            dragMode = requested;
            lastLayeredResizeRenderTicks = 0; pendingLayeredResize = false;
            ResizeTraceStart(requested);
            cropDragging = shiftCrop;
            dragStartMouse = Cursor.Position;
            dragStartBounds = Bounds;
            webResizePreviewActive = false;
            webResizePreviewBounds = dragStartBounds;
            dragStartCropLeft = CropLeft; dragStartCropTop = CropTop; dragStartCropRight = CropRight; dragStartCropBottom = CropBottom;
            dragGroupStartBounds.Clear();
            if (!resize)
            {
                foreach (OverlayItemForm member in owner.GetMoveLinkedMembers(this))
                    if (!member.Locked) dragGroupStartBounds[member] = member.Bounds;
            }
        }

        public void BeginWebFrameDrag(bool resize)
        {
            BeginSelectionFrameDrag(resize ? (int)DragMode.BottomRight : (int)DragMode.Move);
        }

        public void ContinueWebFrameDrag()
        {
            if (!dragging) return;
            OnMouseMoveOverlay(this, new MouseEventArgs(MouseButtons.Left, 0, Width / 2, Height / 2, 0));
        }

        public void EndWebFrameDrag()
        {
            if (!dragging) return;
            OnMouseUpOverlay(this, new MouseEventArgs(MouseButtons.Left, 0, Width / 2, Height / 2, 0));
        }

        // Universal move gesture used by MainForm's right-button message filter. This deliberately
        // reuses the normal move pipeline so group movement, placement snap, program-magnet detach
        // and reattach all behave exactly like a normal left-button move.
        public bool BeginRightButtonMove()
        {
            if (Locked || owner == null || !owner.EditMode || owner.WebControlMode) return false;
            owner.MarkQuickHideTarget(this);
            owner.SelectOverlayForEditing(this);
            dragUndoCaptured = false;
            dragUndoReason = "우클릭 드래그 이동";
            dragging = true;
            dragMode = DragMode.Move;
            lastLayeredResizeRenderTicks = 0; pendingLayeredResize = false;
            cropDragging = false;
            programAttachmentDetachedForDrag = false;
            dragStartMouse = Cursor.Position;
            dragStartBounds = Bounds;
            dragGroupStartBounds.Clear();
            foreach (OverlayItemForm member in owner.GetMoveLinkedMembers(this))
                if (!member.Locked) dragGroupStartBounds[member] = member.Bounds;
            try { Capture = true; } catch { }
            return true;
        }

        public void ContinueRightButtonMove()
        {
            if (!dragging) return;
            OnMouseMoveOverlay(this, new MouseEventArgs(MouseButtons.Right, 0, Width / 2, Height / 2, 0));
        }

        public void EndRightButtonMove()
        {
            if (!dragging) return;
            OnMouseUpOverlay(this, new MouseEventArgs(MouseButtons.Right, 0, Width / 2, Height / 2, 0));
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
                    Native.SetWindowPos(Handle, AlwaysOnTop ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
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

        private static DragMode RawResizeModeAt(Point p, Rectangle visual, int grip)
        {
            bool l = Math.Abs(p.X - visual.Left) <= grip;
            bool r = Math.Abs(p.X - visual.Right) <= grip;
            bool t = Math.Abs(p.Y - visual.Top) <= grip;
            bool b = Math.Abs(p.Y - visual.Bottom) <= grip;
            bool insideBand = p.X >= visual.Left - grip && p.X <= visual.Right + grip &&
                              p.Y >= visual.Top - grip && p.Y <= visual.Bottom + grip;
            if (!insideBand) return DragMode.Move;
            if (t && l) return DragMode.TopLeft;
            if (t && r) return DragMode.TopRight;
            if (b && l) return DragMode.BottomLeft;
            if (b && r) return DragMode.BottomRight;
            if (l) return DragMode.Left;
            if (r) return DragMode.Right;
            if (t) return DragMode.Top;
            if (b) return DragMode.Bottom;
            return DragMode.Move;
        }

        private static bool IsWithinResizeGraceMode(Point p, Rectangle visual, DragMode mode, int grip)
        {
            bool xSpan = p.X >= visual.Left - grip && p.X <= visual.Right + grip;
            bool ySpan = p.Y >= visual.Top - grip && p.Y <= visual.Bottom + grip;
            bool l = Math.Abs(p.X - visual.Left) <= grip;
            bool r = Math.Abs(p.X - visual.Right) <= grip;
            bool t = Math.Abs(p.Y - visual.Top) <= grip;
            bool b = Math.Abs(p.Y - visual.Bottom) <= grip;
            if (mode == DragMode.Left) return l && ySpan;
            if (mode == DragMode.Right) return r && ySpan;
            if (mode == DragMode.Top) return t && xSpan;
            if (mode == DragMode.Bottom) return b && xSpan;
            if (mode == DragMode.TopLeft) return l && t;
            if (mode == DragMode.TopRight) return r && t;
            if (mode == DragMode.BottomLeft) return l && b;
            if (mode == DragMode.BottomRight) return r && b;
            return false;
        }

        private DragMode ModeAt(Point p)
        {
            Rectangle visual = GetVisualContentBounds(Bounds);
            visual.Offset(-Left, -Top);
            if (visual.Width <= 0 || visual.Height <= 0) visual = ClientRectangle;

            long now = DateTime.UtcNow.Ticks;
            DragMode direct = RawResizeModeAt(p, visual, Grip);
            if (direct != DragMode.Move)
            {
                resizeGraceMode = direct;
                resizeGraceUntilUtcTicks = now + (owner.ResizeGraceMilliseconds * TimeSpan.TicksPerMillisecond);
                return direct;
            }

            if (resizeGraceMode != DragMode.None && now <= resizeGraceUntilUtcTicks &&
                IsWithinResizeGraceMode(p, visual, resizeGraceMode, owner.ResizeGracePixels))
                return resizeGraceMode;

            if (now > resizeGraceUntilUtcTicks) resizeGraceMode = DragMode.None;
            return DragMode.Move;
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
            try { Activate(); } catch { }

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

            owner.MarkQuickHideTarget(this);
            owner.SelectOverlayForEditing(this);
            if (Locked) return;
            // Keep an existing program magnet on a simple click. Detach only after the pointer
            // actually moves, otherwise selecting an attached overlay silently broke tracking.
            programAttachmentDetachedForDrag = false;
            cropDragging = Type == ItemType.Image && hitMode != DragMode.Move && shiftHeld;
            dragUndoCaptured = false;
            dragUndoReason = cropDragging ? "이미지 자르기" : (hitMode == DragMode.Move ? "오버레이 이동" : "오버레이 크기 변경");
            dragging = true; dragMode = hitMode; dragStartMouse = Cursor.Position; dragStartBounds = Bounds; Capture = true;
            lastLayeredResizeRenderTicks = 0; pendingLayeredResize = false;
            webResizePreviewActive = false;
            webResizePreviewBounds = dragStartBounds;
            ResizeTraceStart(hitMode);
            dragStartCropLeft = CropLeft; dragStartCropTop = CropTop; dragStartCropRight = CropRight; dragStartCropBottom = CropBottom;
            dragGroupStartBounds.Clear();
            if (dragMode == DragMode.Move)
            {
                foreach (OverlayItemForm member in owner.GetMoveLinkedMembers(this))
                    if (!member.Locked) dragGroupStartBounds[member] = member.Bounds;
            }
        }
        private void OnMouseMoveOverlay(object sender, MouseEventArgs e)
        {
            // In Integrated mode the WebView itself stays interaction-first, so the web overlay's
            // normal editMode is intentionally false. The external selection frame still needs to
            // drive movement/resizing through this method.
            if (!editMode && !(Type == ItemType.Web && owner.IntegratedMode && dragging)) return;

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
            if (!dragUndoCaptured && (dx != 0 || dy != 0))
            {
                owner.CaptureUndo(string.IsNullOrWhiteSpace(dragUndoReason) ? "오버레이 위치/크기 변경" : dragUndoReason);
                dragUndoCaptured = true;
            }
            if (dragMode == DragMode.Move && !programAttachmentDetachedForDrag && (Math.Abs(dx) >= 3 || Math.Abs(dy) >= 3))
            {
                owner.DetachOverlayFromProgramForDrag(this);
                programAttachmentDetachedForDrag = true;
            }
            Rectangle r = dragStartBounds; int minW = 100, minH = 60;
            bool cropResize = cropDragging;
            if (dragMode == DragMode.Move)
            {
                r.X += dx; r.Y += dy;
                if ((Control.ModifierKeys & Keys.Shift) != Keys.Shift)
                {
                    r = owner.ApplyPlacementSnap(this, r);
                }
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
            else
            {
                Rectangle nextBounds = owner.NormalizeBounds(r);
                if (Type == ItemType.Web && dragMode != DragMode.Move)
                {
                    webResizePreviewActive = true;
                    webResizePreviewBounds = nextBounds;
                    ResizeTraceAdd("Web resize preview", nextBounds);
                    PreviewWebResizeFrame(nextBounds);
                }
                else if (dragMode != DragMode.Move && UsesPerPixelLayeredSurface)
                    ApplyLayeredResizeBounds(nextBounds);
                else
                    Bounds = nextBounds;
            }
        }

        private void ApplyLayeredResizeBounds(Rectangle nextBounds)
        {
            ResizeTraceAdd("ApplyLayeredResizeBounds:enter", nextBounds);
            if (Bounds == nextBounds) { ResizeTraceAdd("ApplyLayeredResizeBounds:same", nextBounds); return; }

            if (dragging && dragMode != DragMode.Move)
            {
                long nowTicks = Stopwatch.GetTimestamp();
                long minTicks = Math.Max(1L, (Stopwatch.Frequency * LayeredResizeMinIntervalMs) / 1000L);
                if (lastLayeredResizeRenderTicks != 0 && nowTicks - lastLayeredResizeRenderTicks < minTicks)
                {
                    pendingLayeredResize = true;
                    pendingLayeredResizeBounds = nextBounds;
                    return;
                }
                lastLayeredResizeRenderTicks = nowTicks;
                pendingLayeredResize = false;
            }

            if (Type == ItemType.Image && RotationDegrees != 0 && rotationBaseWidth > 0 && rotationBaseHeight > 0 && !cropDragging)
            {
                // The blue frame is the rotated canvas. Scale the logical unrotated image size
                // from the requested frame delta, then rebuild the exact rotation AABB.
                double sx = nextBounds.Width / (double)Math.Max(1, Width);
                double sy = nextBounds.Height / (double)Math.Max(1, Height);
                double scale;
                if (dragMode == DragMode.Top || dragMode == DragMode.Bottom) scale = sy;
                else if (dragMode == DragMode.Left || dragMode == DragMode.Right) scale = sx;
                else scale = Math.Abs(sx - 1.0) >= Math.Abs(sy - 1.0) ? sx : sy;
                scale = Math.Max(0.05, Math.Min(20.0, scale));
                rotationBaseWidth = Math.Max(1, (int)Math.Round(rotationBaseWidth * scale));
                rotationBaseHeight = Math.Max(1, (int)Math.Round(rotationBaseHeight * scale));
                nextBounds = GetImageRotationCanvasBounds(RotationDegrees, nextBounds);
            }
            else if (Type == ItemType.Image && RotationDegrees == 0 && !cropDragging)
            {
                rotationBaseWidth = Math.Max(1, nextBounds.Width);
                rotationBaseHeight = Math.Max(1, nextBounds.Height);
            }
            if (!overlayVisible || !IsHandleCreated || !UsesPerPixelLayeredSurface)
            {
                Bounds = nextBounds;
                return;
            }

            // UpdateLayeredWindow already moves/resizes the native layered HWND while applying
            // the completed ARGB surface. Calling Bounds = nextBounds afterwards performs a second
            // WinForms/native resize and can expose an empty fallback frame. Keep the native resize
            // single-pass, then synchronize only WinForms' cached bounds with UpdateBounds().
            applyingLayeredResize = true;
            try
            {
                ResizeTraceAdd("before RenderLayeredAt", nextBounds);
                RenderLayeredAt(nextBounds);
                ResizeTraceAdd("after RenderLayeredAt", nextBounds);
                ResizeTraceAdd("before UpdateBounds(cache)", nextBounds);
                UpdateBounds(nextBounds.X, nextBounds.Y, nextBounds.Width, nextBounds.Height,
                    nextBounds.Width, nextBounds.Height);
                ResizeTraceAdd("after UpdateBounds(cache)", nextBounds);
            }
            finally
            {
                applyingLayeredResize = false;
            }
            UpdateSelectionFrame();
            ResizeTraceAdd("ApplyLayeredResizeBounds:exit", nextBounds);
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
                    DetailedLog.Write("ROTATE_END",
                        "id=" + DetailedLog.ShortId(ItemId) +
                        " bounds=" + DetailedLog.Rect(Bounds) +
                        " rotation=" + RotationDegrees.ToString());
                    owner.ReapplyZOrder();
                    owner.SaveConfig();
                    owner.RefreshMouseTransformPreview();
                }
                mouseRotateMoved = false;
                mouseRotateStartDegrees.Clear();
                return;
            }

            if (!dragging) return;
            DragMode completedDragMode = dragMode;
            bool movedOverlay = dragMode == DragMode.Move;
            bool positionChanged = Bounds.Left != dragStartBounds.Left || Bounds.Top != dragStartBounds.Top;
            bool actualMoveDrag = movedOverlay && (programAttachmentDetachedForDrag || positionChanged);
            if (!movedOverlay) ResizeTraceAdd("MouseUp:before-reset", webResizePreviewActive ? webResizePreviewBounds : Bounds);

            if (Type == ItemType.Web && !movedOverlay && webResizePreviewActive)
            {
                Rectangle finalWebBounds = webResizePreviewBounds;
                webResizePreviewActive = false;
                ResizeTraceAdd("Web resize commit", finalWebBounds);
                Bounds = finalWebBounds;
            }
            else webResizePreviewActive = false;

            if (Type != ItemType.Web && !movedOverlay && pendingLayeredResize)
            {
                Rectangle finalLayeredBounds = pendingLayeredResizeBounds;
                pendingLayeredResize = false;
                lastLayeredResizeRenderTicks = 0;
                ApplyLayeredResizeBounds(finalLayeredBounds);
            }

            dragging = false; cropDragging = false; dragMode = DragMode.None; dragGroupStartBounds.Clear(); Capture = false;
            dragUndoCaptured = false; dragUndoReason = "";
            NormalizeFitBoundsToVisualContent();
            // A plain click keeps the existing magnet. After a real drag, try to attach to the
            // nearest program edge even if the pointer happened to return to the original coords.
            if (actualMoveDrag) owner.TryAutoAttachOverlayToNearbyProgram(this);
            programAttachmentDetachedForDrag = false;
            DetailedLog.Write("DRAG_END",
                "id=" + DetailedLog.ShortId(ItemId) +
                " type=" + Type.ToString() +
                " mode=" + completedDragMode.ToString() +
                " start=" + DetailedLog.Rect(dragStartBounds) +
                " final=" + DetailedLog.Rect(Bounds) +
                " changed=" + (dragStartBounds != Bounds).ToString());
            UpdateSelectionFrame(); owner.ReapplyZOrder(); owner.SaveConfig();
            ResizeTraceFinish("MouseUp");
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

    internal sealed class RemoteControlForm : Form
    {
        private sealed class RemoteOverlayEntry
        {
            public OverlayItemForm Overlay;
            public string Text = "";
            public override string ToString() { return Text; }
        }

        private readonly MainForm owner;
        private readonly ListBox overlayList = new ListBox();
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        private bool syncingList;

        public RemoteControlForm(MainForm owner)
        {
            this.owner = owner;
            Text = "CatLayer 리모컨" + (string.IsNullOrWhiteSpace(AppInfo.BuildLabel) ? "" : " [" + AppInfo.BuildLabel + "]");
            // Use the normal sizable frame instead of the very thin ToolWindow resize border.
            // This makes horizontal resizing much easier to grab on Windows.
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(400, 440);
            MinimumSize = new Size(360, 330);
            MaximizeBox = false;
            TopMost = true;
            ShowInTaskbar = true;
            BackColor = Color.FromArgb(13, 29, 55);
            ForeColor = Color.White;

            // Keep the command area compact and stable while letting the overlay list
            // receive almost all extra space when the remote is resized.
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(8);
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.BackColor = BackColor;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 168F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(root);

            TableLayoutPanel buttons = new TableLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0);
            buttons.ColumnCount = 2;
            buttons.RowCount = 4;
            buttons.BackColor = BackColor;
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            root.Controls.Add(buttons, 0, 0);

            // Most frequently used controls first, related actions side-by-side.
            buttons.Controls.Add(CreateButton("편집 / 고정", delegate { owner.RemoteCycleEditorMode(); }), 0, 0);
            buttons.Controls.Add(CreateButton("전체 표시 / 숨김", delegate { owner.RemoteToggleHidden(); }), 1, 0);
            buttons.Controls.Add(CreateButton("빠른 숨김", delegate { owner.RemoteQuickHide(); }), 0, 1);
            buttons.Controls.Add(CreateButton("숨김 취소", delegate { owner.RemoteUndoQuickHide(); }), 1, 1);
            buttons.Controls.Add(CreateButton("선택 표시", delegate { owner.RemoteShowSelected(); }), 0, 2);
            buttons.Controls.Add(CreateButton("웹 새로고침", delegate { owner.RemoteReloadSelectedWeb(); }), 1, 2);
            buttons.Controls.Add(CreateButton("프리셋", delegate { owner.RemoteLoadPreset(); }), 0, 3);
            buttons.Controls.Add(CreateButton("그룹", delegate { owner.RemoteLoadGroup(); }), 1, 3);

            Label listLabel = new Label();
            listLabel.Text = "오버레이 목록";
            listLabel.Dock = DockStyle.Fill;
            listLabel.TextAlign = ContentAlignment.MiddleLeft;
            listLabel.Margin = new Padding(4, 2, 4, 2);
            listLabel.ForeColor = Color.FromArgb(180, 196, 220);
            root.Controls.Add(listLabel, 0, 1);

            overlayList.Dock = DockStyle.Fill;
            overlayList.Margin = new Padding(4, 2, 4, 4);
            overlayList.BackColor = Color.FromArgb(20, 39, 69);
            overlayList.ForeColor = Color.White;
            overlayList.BorderStyle = BorderStyle.FixedSingle;
            overlayList.IntegralHeight = false;
            overlayList.HorizontalScrollbar = true;
            overlayList.SelectedIndexChanged += delegate
            {
                if (syncingList) return;
                RemoteOverlayEntry entry = overlayList.SelectedItem as RemoteOverlayEntry;
                if (entry != null && entry.Overlay != null) owner.RemoteSelectOverlay(entry.Overlay);
            };
            root.Controls.Add(overlayList, 0, 2);

            refreshTimer.Interval = 300;
            refreshTimer.Tick += delegate { RefreshOverlayList(); };
            refreshTimer.Start();
            RefreshOverlayList();

            // TEST 07: the remote can open the same quick functions with right-click.
            ContextMenuStrip remoteRightClickMenu = owner.CreateRemoteRightClickMenu();
            AttachRightClickMenuRecursive(this, remoteRightClickMenu);
            EnableExternalDropRecursive(this);
        }

        private void AttachRightClickMenuRecursive(Control root, ContextMenuStrip menu)
        {
            if (root == null || menu == null) return;
            try { if (root.ContextMenuStrip == null) root.ContextMenuStrip = menu; } catch { }
            foreach (Control child in root.Controls) AttachRightClickMenuRecursive(child, menu);
        }

        private Button CreateButton(string text, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(4, 3, 4, 3);
            b.AutoEllipsis = true;
            b.MinimumSize = new Size(0, 32);
            b.BackColor = Color.FromArgb(20, 39, 69);
            b.ForeColor = Color.White;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(42, 59, 88);
            b.Click += click;
            return b;
        }

        private void RefreshOverlayList()
        {
            if (IsDisposed) return;
            OverlayItemForm keep = owner.RemoteSelectedOverlay;
            if (keep == null)
            {
                RemoteOverlayEntry selected = overlayList.SelectedItem as RemoteOverlayEntry;
                if (selected != null) keep = selected.Overlay;
            }

            List<OverlayItemForm> snapshot = owner.RemoteOverlaySnapshot();
            bool same = snapshot.Count == overlayList.Items.Count;
            if (same)
            {
                for (int i = 0; i < snapshot.Count; i++)
                {
                    RemoteOverlayEntry e = overlayList.Items[i] as RemoteOverlayEntry;
                    string text = owner.RemoteOverlayDisplayText(snapshot[i]);
                    if (e == null || !object.ReferenceEquals(e.Overlay, snapshot[i]) || e.Text != text) { same = false; break; }
                }
            }
            if (same)
            {
                if (keep != null)
                {
                    for (int i = 0; i < overlayList.Items.Count; i++)
                    {
                        RemoteOverlayEntry e = overlayList.Items[i] as RemoteOverlayEntry;
                        if (e != null && object.ReferenceEquals(e.Overlay, keep) && overlayList.SelectedIndex != i)
                        {
                            syncingList = true; overlayList.SelectedIndex = i; syncingList = false; break;
                        }
                    }
                }
                return;
            }

            syncingList = true;
            try
            {
                overlayList.BeginUpdate();
                overlayList.Items.Clear();
                int selectedIndex = -1;
                for (int i = 0; i < snapshot.Count; i++)
                {
                    RemoteOverlayEntry entry = new RemoteOverlayEntry();
                    entry.Overlay = snapshot[i];
                    entry.Text = owner.RemoteOverlayDisplayText(snapshot[i]);
                    overlayList.Items.Add(entry);
                    if (keep != null && object.ReferenceEquals(keep, snapshot[i])) selectedIndex = i;
                }
                if (selectedIndex < 0 && overlayList.Items.Count > 0) selectedIndex = 0;
                overlayList.SelectedIndex = selectedIndex;
                overlayList.EndUpdate();
            }
            finally { syncingList = false; }
        }

        private void EnableExternalDropRecursive(Control root)
        {
            if (root == null) return;
            try
            {
                root.AllowDrop = true;
                root.DragEnter += delegate(object sender, DragEventArgs e) { owner.HandleExternalOverlayDragEnter(e); };
                root.DragOver += delegate(object sender, DragEventArgs e) { owner.HandleExternalOverlayDragOver(e); };
                root.DragDrop += delegate(object sender, DragEventArgs e) { owner.HandleExternalOverlayDragDrop(e); };
            }
            catch { }
            foreach (Control child in root.Controls) EnableExternalDropRecursive(child);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { refreshTimer.Stop(); refreshTimer.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class BeginnerToastForm : Form
    {
        private readonly Label messageLabel = new Label();
        private readonly Button actionButton = new Button();
        private readonly Timer closeTimer = new Timer();
        private Action action;

        public BeginnerToastForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(18, 31, 55);
            ForeColor = Color.FromArgb(236, 240, 248);
            ClientSize = new Size(430, 66);
            Padding = new Padding(1);

            messageLabel.SetBounds(14, 9, 308, 48);
            messageLabel.ForeColor = ForeColor;
            messageLabel.BackColor = Color.Transparent;
            messageLabel.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            messageLabel.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(messageLabel);

            actionButton.SetBounds(330, 17, 86, 32);
            actionButton.FlatStyle = FlatStyle.Flat;
            actionButton.FlatAppearance.BorderSize = 1;
            actionButton.FlatAppearance.BorderColor = Color.FromArgb(74, 91, 125);
            actionButton.BackColor = Color.FromArgb(28, 49, 83);
            actionButton.ForeColor = ForeColor;
            actionButton.Cursor = Cursors.Hand;
            actionButton.Visible = false;
            actionButton.Click += delegate
            {
                Action run = action;
                Hide();
                action = null;
                if (run != null) run();
            };
            Controls.Add(actionButton);

            closeTimer.Interval = 4200;
            closeTimer.Tick += delegate { closeTimer.Stop(); Hide(); action = null; };
        }

        public void ShowMessage(string message, string actionText, Action actionToRun, Rectangle workingArea)
        {
            messageLabel.Text = message ?? "";
            action = actionToRun;
            actionButton.Text = string.IsNullOrWhiteSpace(actionText) ? "" : actionText;
            actionButton.Visible = action != null && !string.IsNullOrWhiteSpace(actionText);
            messageLabel.Width = actionButton.Visible ? 308 : 400;
            int x = workingArea.Right - Width - 18;
            int y = workingArea.Bottom - Height - 18;
            Location = new Point(Math.Max(workingArea.Left + 6, x), Math.Max(workingArea.Top + 6, y));
            closeTimer.Stop();
            if (!Visible) Show();
            BringToFront();
            closeTimer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { closeTimer.Stop(); closeTimer.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }

    internal sealed class OverlayQuickBarForm : Form
    {
        private readonly MainForm owner;
        private readonly Label modeLabel = new Label();
        private readonly Button hideButton = new Button();
        private readonly Button duplicateButton = new Button();
        private readonly Button flipButton = new Button();
        private readonly Button undoButton = new Button();
        private readonly Button deleteButton = new Button();

        public OverlayQuickBarForm(MainForm ownerForm)
        {
            owner = ownerForm;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(18, 31, 55);
            ClientSize = new Size(402, 42);
            Padding = new Padding(1);

            modeLabel.SetBounds(8, 7, 70, 28);
            modeLabel.ForeColor = Color.FromArgb(178, 191, 216);
            modeLabel.BackColor = Color.Transparent;
            modeLabel.TextAlign = ContentAlignment.MiddleCenter;
            modeLabel.Font = new Font("Segoe UI", 8.2F, FontStyle.Bold, GraphicsUnit.Point);
            Controls.Add(modeLabel);

            SetupButton(hideButton, "숨김", 80, 6, 58, false);
            SetupButton(duplicateButton, "복제", 142, 6, 58, false);
            SetupButton(flipButton, "좌우", 204, 6, 58, false);
            SetupButton(undoButton, "되돌림", 266, 6, 62, false);
            SetupButton(deleteButton, "삭제", 332, 6, 62, true);

            hideButton.Click += delegate { owner.BeginnerQuickHideSelected(); };
            duplicateButton.Click += delegate { owner.BeginnerQuickDuplicateSelected(); };
            flipButton.Click += delegate { owner.BeginnerQuickFlipSelected(); };
            undoButton.Click += delegate { owner.BeginnerQuickUndo(); };
            deleteButton.Click += delegate { owner.BeginnerQuickDeleteSelected(); };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE.
                cp.ExStyle |= 0x00000080;
                cp.ExStyle |= 0x08000000;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            // WM_MOUSEACTIVATE -> MA_NOACTIVATE.
            // Mouse buttons still work, but this helper never steals focus from
            // CatLayer's menu, settings dialogs, or the selected overlay.
            if (m.Msg == 0x0021)
            {
                m.Result = (IntPtr)3;
                return;
            }
            base.WndProc(ref m);
        }

        public void ShowPassive(Point location)
        {
            if (Location != location) Location = location;
            if (!Visible) Show();
        }

        private void SetupButton(Button button, string text, int x, int y, int w, bool danger)
        {
            button.Text = text;
            button.SetBounds(x, y, w, 30);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = danger ? Color.FromArgb(126, 68, 80) : Color.FromArgb(65, 82, 113);
            button.BackColor = Color.FromArgb(28, 49, 83);
            button.ForeColor = danger ? Color.FromArgb(255, 130, 135) : Color.FromArgb(236, 240, 248);
            button.Cursor = Cursors.Hand;
            button.TabStop = false;
            Controls.Add(button);
        }

        public void SetModeText(string text) { modeLabel.Text = text ?? ""; }
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
        // TEST 11.5: remember the exact row that opened the list context menu so
        // hierarchy parent selection follows the same "right-clicked item becomes parent" rule
        // as the overlay windows themselves.
        private OverlayItemForm overlayListContextTarget;
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
        private readonly List<Control> legacyMainControls = new List<Control>();
        private Panel compactMainPanel;
        private Panel compactFileZone;
        private Label compactFileTitle;
        private Label compactFileHint;
        private Button compactHelpButton;
        private ContextMenuStrip compactMainMenu;
        private bool compactMainActive;
        private readonly ToolTip beginnerToolTip = new ToolTip();
        private BeginnerToastForm beginnerToast;
        private OverlayQuickBarForm beginnerQuickBar;
        private bool beginnerQuickBarArmed;
        private bool beginnerQuickBarMenuSuppressed;
        private bool beginnerHelpEnabled = true;
        private bool compactDropHighlight;
        private bool shellOverlayStartup;
        private readonly Size compactMainClientSize = new Size(640, 400);
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
        // IMessageFilter sees keyboard input before individual WinForms controls. Latch Z so
        // holding Ctrl+Z performs one undo per physical key press instead of key-repeat undo.
        private bool undoShortcutDown;
        private bool mainUiReady;
        private readonly List<OverlayItemForm> items = new List<OverlayItemForm>();
        private readonly List<OverlayItemForm> overlayListDragSelection = new List<OverlayItemForm>();
        private readonly NotifyIcon trayIcon = new NotifyIcon();
        private bool trayHintShown;
        // Recommended one-hand defaults. Legacy F-keys are kept as slot 2.
        private int hotkeyEditVk = (int)Keys.Q;
        private int hotkeyHideVk = (int)Keys.W;
        private int hotkeyAllHideVk = Native.VK_F11;
        private int hotkeyDetailVk = Native.VK_F10;
        private int hotkeyCaptureVk = (int)Keys.E;
        private int hotkeyQuickShowVk = (int)Keys.W;
        private int hotkeyRemoteVk = 0;
        private int hotkeyWebReloadVk = 0;
        private int hotkeyPresetLoadVk = 0;
        private int hotkeyGroupLoadVk = 0;
        private int hotkeyEditMods = Native.MOD_ALT;
        private int hotkeyHideMods = Native.MOD_ALT;
        private int hotkeyAllHideMods = 0;
        private int hotkeyDetailMods = 0;
        private int hotkeyCaptureMods = Native.MOD_ALT;
        private int hotkeyQuickShowMods = Native.MOD_ALT | Native.MOD_SHIFT;
        private int hotkeyRemoteMods = 0;
        private int hotkeyWebReloadMods = 0;
        private int hotkeyPresetLoadMods = 0;
        private int hotkeyGroupLoadMods = 0;
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

        // TEST 11.6: every shortcut action can have a second binding.
        // Slot 1 remains in the legacy scalar fields above for backward-compatible config migration.
        private sealed class HotkeyBinding
        {
            public int Mods;
            public int Vk;
            public HotkeyBinding() { }
            public HotkeyBinding(int mods, int vk) { Mods = mods; Vk = vk; }
        }
        private static readonly string[] CoreHotkeyActionKeys = new string[]
        {
            "EDIT", "HIDE", "QUICK_SHOW", "ALL_HIDE", "DETAIL", "CAPTURE", "REMOTE", "WEB_RELOAD", "PRESET_LOAD", "GROUP_LOAD",
            "GROUP", "UNGROUP", "ROT_M1", "ROT_P1", "ROT_M10", "ROT_P10", "FLIP_H", "FLIP_V", "RESET_ROT", "RESET_ALL"
        };
        private static readonly string[] GlobalHotkeyActionKeys = new string[]
        {
            "EDIT", "HIDE", "QUICK_SHOW", "ALL_HIDE", "DETAIL", "CAPTURE", "REMOTE", "WEB_RELOAD", "PRESET_LOAD", "GROUP_LOAD"
        };
        private readonly Dictionary<string, HotkeyBinding> secondaryHotkeys = new Dictionary<string, HotkeyBinding>(StringComparer.OrdinalIgnoreCase);

        // Observe global shortcuts without reserving/consuming them.
        private IntPtr sharedKeyboardHook = IntPtr.Zero;
        private Native.LowLevelKeyboardProc sharedKeyboardHookProc;
        private readonly HashSet<int> sharedHotkeyDown = new HashSet<int>();
        private bool sharedHotkeyCaptureSuspended;

        private int zoomStepPercent = 10;
        private int rotationSnapDegrees = 5;
        private int placementSnapPixels = 8;
        private int programMagnetSnapPixels = 36;
        // TEST 13.3: external program magnets can move overlays asynchronously.
        // Keep automatic attachment OFF unless the user explicitly enables it.
        private bool autoProgramMagnetEnabled = false;
        private int resizeGracePixels = 30;
        private int resizeGraceMs = 500;
        // TEST 11: managed copies of very large still images are downscaled by default.
        // The user's original file is never modified. GIF animation is never flattened.
        private bool autoOptimizeImages = true;
        private const int AutoOptimizeMaxDimension = 4096;
        private const long AutoOptimizeMaxPixels = 16000000L;
        public int ResizeGracePixels { get { return Math.Max(10, Math.Min(80, resizeGracePixels)); } }
        public int ResizeGraceMilliseconds { get { return Math.Max(0, Math.Min(3000, resizeGraceMs)); } }
        private sealed class PresetHotkeyBinding
        {
            public string FileName = "";
            public int Mods;
            public int Vk;
        }
        private readonly List<PresetHotkeyBinding> presetHotkeys = new List<PresetHotkeyBinding>();
        private string currentPresetName = "";
        private readonly string configPath;
        private readonly string baseDataDir;
        private readonly string assetsDir;
        private readonly string soundsDir;
        private readonly string presetsDir;
        private readonly string groupsDir;
        private readonly string webFilesDir;
        private readonly string undoDir;
        private readonly string recoveryPath;
        private readonly string sessionMarkerPath;
        private readonly Timer recoveryTimer = new Timer();
        private bool configRecoveredFromSession;
        // SaveConfig is used by both UI actions and background recovery snapshots.
        // Background snapshots must never force a full main-list repaint.
        private bool suppressSaveConfigUiRefresh;
        // TEST 12.1: Config loading normally restores MAIN_SIZE. Undo rebuilds state from a
        // config snapshot too, but must not visibly resize the main window halfway through.
        private bool suppressConfigMainWindowSizeApply = false;
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
        public bool IntegratedMode { get; private set; }
        private OverlayItemForm singleWebControlOverlay;
        public bool HasSingleWebControl { get { return singleWebControlOverlay != null && !singleWebControlOverlay.IsDisposed; } }
        public bool CurrentEditorModeIsSingleWeb { get { return HasSingleWebControl && !WebControlMode && !IntegratedMode; } }
        public EditorMode CurrentEditorMode { get { return WebControlMode ? EditorMode.WebControl : (IntegratedMode ? EditorMode.Integrated : (EditMode ? EditorMode.Normal : EditorMode.Fixed)); } }
        private EditorMode loadedEditorMode = EditorMode.Normal;
        public bool AllHidden { get { return hidden; } }
        private bool hidden;
        private EditorMode modeBeforeWebControl = EditorMode.Normal;
        private int nextGroupId = 1;
        private WebInteractionStyle webInteractionStyle = WebInteractionStyle.DoubleClick;
        // Single-click web interaction must not steal a drag. We arm the click on mouse-down
        // and enter Web Control only after mouse-up when the pointer barely moved.
        private OverlayItemForm singleClickWebCandidate;
        private Point singleClickWebStart;
        private bool singleClickWebMoved;
        private readonly Dictionary<int, string> groupNames = new Dictionary<int, string>();
        private readonly HashSet<int> collapsedGroups = new HashSet<int>();
        private RemoteControlForm remoteControlForm;
        // F9 quick-hide target is set only by an explicit overlay/list click.
        // The main list always keeps a visual selection, so SelectedOverlay alone cannot tell
        // whether the user actually chose a target for the next quick-hide action.
        private OverlayItemForm quickHideTarget;
        // Keep the order of overlays hidden by Quick Hide so Quick Show can restore them
        // in reverse order. This history is session-only; if it is empty we fall back to the
        // first hidden overlay in priority order.
        private readonly List<OverlayItemForm> quickHiddenHistory = new List<OverlayItemForm>();

        // Right-button drag is handled at the application message-filter level so it also works
        // when a WebView2 child HWND owns the mouse in Integrated mode. A short right-click is
        // left untouched and still opens the normal context menu.
        private OverlayItemForm rightDragOverlay;
        private DragDropEffects cachedExternalDragEffect = DragDropEffects.None;
        private long cachedExternalDragEffectUtcTicks;
        private Point rightDragStart;
        private bool rightDragMoved;
        private DateTime suppressRightContextUntil = DateTime.MinValue;

        private sealed class WindowAttachment
        {
            public string ProcessName = "";
            public string WindowTitle = "";
            public IntPtr Hwnd = IntPtr.Zero;
            public int OffsetX;
            public int OffsetY;
            // 0..3 = outside (left/right/above/below), 4..7 = inside edges (left/right/top/bottom), -1 = legacy offset mode.
            public int Side = -1;
        }
        // TEST 13: Ctrl+Z uses logical per-overlay snapshots. It never reloads the whole preset/config
        // for normal overlay operations, so unchanged WebView/GIF/Timer/OBS forms remain alive.
        private sealed class UndoItemSnapshot
        {
            public ItemType Type;
            public string Data = "";
            public int DurationSeconds;
            public Rectangle Bounds;
            public int OpacityPercent;
            public TimerMode TimerKind;
            public string AlarmPath = "";
            public string CustomName = "";
            public bool Visible;
            public bool Locked;
            public bool PreserveAspect;
            public ImageScaleMode ScaleMode;
            public int RotationDegrees;
            public bool FlipHorizontal;
            public bool FlipVertical;
            public int RotationBaseWidth;
            public int RotationBaseHeight;
            public int GroupId;
            public int CropLeft, CropTop, CropRight, CropBottom;
            public int WebZoomPercent;
            public string WebCustomCss = "";
            public string ItemId = "";
            public string ParentItemId = "";
            public bool AlwaysOnTop = true;
            public bool HasAttachment;
            public string AttachProcess = "";
            public string AttachTitle = "";
            public int AttachOffsetX, AttachOffsetY, AttachSide = -1;
        }

        private readonly Dictionary<OverlayItemForm, WindowAttachment> windowAttachments = new Dictionary<OverlayItemForm, WindowAttachment>();
        private readonly List<OverlayItemForm> staleAttachmentKeys = new List<OverlayItemForm>();
        private List<WindowChoice> attachableWindowCache = new List<WindowChoice>();
        private DateTime attachableWindowCacheAt = DateTime.MinValue;

        private sealed class UndoState
        {
            public string Path;
            public string Reason;
            public bool EditMode;
            public bool DetailEditMode;
            public bool Hidden;
            public int HotkeyEditVk;
            public int HotkeyHideVk;
            public int HotkeyAllHideVk;
            public int HotkeyDetailVk;
            public int HotkeyCaptureVk;
            public int HotkeyQuickShowVk;
            public int HotkeyRemoteVk;
            public int HotkeyWebReloadVk;
            public int HotkeyPresetLoadVk;
            public int HotkeyGroupLoadVk;
            public int HotkeyEditMods;
            public int HotkeyHideMods;
            public int HotkeyAllHideMods;
            public int HotkeyDetailMods;
            public int HotkeyCaptureMods;
            public int HotkeyQuickShowMods;
            public int HotkeyRemoteMods;
            public int HotkeyWebReloadMods;
            public int HotkeyPresetLoadMods;
            public int HotkeyGroupLoadMods;
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
            public Dictionary<string, HotkeyBinding> SecondaryHotkeys;
            public int ZoomStepPercent;
            public int RotationSnapDegrees;
            public int PlacementSnapPixels;
            public int ProgramMagnetSnapPixels;
            public int ResizeGracePixels;
            public int ResizeGraceMs;
            public List<PresetHotkeyBinding> PresetHotkeys;
            public string CurrentPresetName;
            public Size MainClientSize;
            public bool CompactMainActive;
            public EditorMode SavedEditorMode;
            public WebInteractionStyle SavedWebInteractionStyle;
            public bool SavedSuppressAutomaticUpdatePrompt;
            public bool SettingsOnly;
            public int LastAppliedChangedItemCount;
            public List<UndoItemSnapshot> Items;
            public List<string> SelectedItemIds;
            public Dictionary<int, string> GroupNames;
            public HashSet<int> CollapsedGroups;
            public int NextGroupId;
            // TEST 12.1: Undo snapshots also remember the active main surface so restoring
            // settings never flashes the legacy window size over the compact UI.
            // TEST 12: Undo snapshots reference managed assets by path instead of embedding
            // every image as Base64. Keep referenced files alive until the undo state expires.
            public HashSet<string> AssetPaths;
            public HashSet<string> SoundPaths;
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
            Text = AppInfo.DisplayName;
            ClientSize = mainBaseClientSize;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimumSize = new Size(460, 320);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = UiBack;
            ForeColor = UiText;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            EditMode = true;
            DetailEditMode = true;
            DoubleBuffered = true;
            KeyPreview = true;

            baseDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatLayer");
            legacyDataMigrated = MigrateLegacyDataIfNeeded(baseDataDir);
            assetsDir = Path.Combine(baseDataDir, "Assets");
            soundsDir = Path.Combine(baseDataDir, "Sounds");
            presetsDir = Path.Combine(baseDataDir, "Presets");
            groupsDir = Path.Combine(baseDataDir, "Groups");
            webFilesDir = Path.Combine(baseDataDir, "WebFiles");
            undoDir = Path.Combine(baseDataDir, "Undo");
            Directory.CreateDirectory(baseDataDir);
            try
            {
                string oldDiagnosticLogs = Path.Combine(baseDataDir, "Logs");
                if (Directory.Exists(oldDiagnosticLogs)) Directory.Delete(oldDiagnosticLogs, true);
                foreach (string oldLogName in new string[]
                {
                    "diagnostic.log",
                    "diagnostic_previous.log",
                    "diagnostic_overflow.log",
                    "pinterest_drag.log",
                    "crash.log"
                })
                {
                    string oldLogPath = Path.Combine(baseDataDir, oldLogName);
                    if (File.Exists(oldLogPath)) File.Delete(oldLogPath);
                }
            }
            catch { }
            Directory.CreateDirectory(assetsDir);
            Directory.CreateDirectory(soundsDir);
            Directory.CreateDirectory(presetsDir);
            Directory.CreateDirectory(groupsDir);
            Directory.CreateDirectory(webFilesDir);
            Directory.CreateDirectory(undoDir);
            try
            {
                foreach (string oldUndo in Directory.GetFiles(undoDir, "*.lopreset")) File.Delete(oldUndo);
                foreach (string oldUndo in Directory.GetFiles(undoDir, "*.config")) File.Delete(oldUndo);
            }
            catch { }
            configPath = Path.Combine(baseDataDir, "config.txt");
            recoveryPath = Path.Combine(baseDataDir, "session_recovery.txt");
            sessionMarkerPath = Path.Combine(baseDataDir, "session.running");

            BuildMainUi();
            EnableImageDropRecursive(this);
            Application.AddMessageFilter(this);

            bridgeUiTimer.Interval = 1000;
            bridgeUiTimer.Tick += delegate { UpdateBridgeStatus(); };
            bridgeUiTimer.Start();

            // Idle CatLayer no longer polls foreground state at 20 Hz forever. Program-magnet
            // attachments still get the original 20 Hz tracking cadence while active.
            foregroundUiTimer.Interval = 125;
            foregroundUiTimer.Tick += delegate { ForegroundUiTimerTick(); };
            foregroundUiTimer.Start();

            recoveryTimer.Interval = 15000;
            recoveryTimer.Tick += delegate { WriteRecoverySnapshot(); };

            trayIcon.Text = AppInfo.DisplayName.Length <= 63 ? AppInfo.DisplayName : "CatLayer " + AppInfo.BuildLabel;
            try { trayIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ContextMenuStrip trayMenu = new ContextMenuStrip();
            ToolStripMenuItem trayShow = new ToolStripMenuItem("열기"); trayShow.Click += delegate { ShowFromTray(); };
            ToolStripMenuItem trayRemote = new ToolStripMenuItem("리모컨 열기"); trayRemote.Click += delegate { ShowRemoteControl(); };
            ToolStripMenuItem trayEdit = new ToolStripMenuItem("고정 / 편집 전환"); trayEdit.Click += delegate { CycleEditorMode(); };
            ToolStripMenuItem trayHide = new ToolStripMenuItem("전체 표시/숨김"); trayHide.Click += delegate { ToggleHidden(); };
            ToolStripMenuItem trayUndo = new ToolStripMenuItem("실행 취소"); trayUndo.Click += delegate { UndoLastAction(); };
            ToolStripMenuItem trayExit = new ToolStripMenuItem("종료"); trayExit.Click += delegate { trayIcon.Visible = false; Close(); };
            trayMenu.Items.Add(trayShow); trayMenu.Items.Add(trayRemote); trayMenu.Items.Add(trayEdit); trayMenu.Items.Add(trayHide); trayMenu.Items.Add(trayUndo); trayMenu.Items.Add(new ToolStripSeparator()); trayMenu.Items.Add(trayExit);
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.DoubleClick += delegate { ShowFromTray(); };
            try { SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged; } catch { }

            CaptureMainLayout();
            mainUiReady = true;
            Load += delegate
            {
                LoadConfigWithSessionRecovery(); SetEditorMode(loadedEditorMode, false); MigrateLegacyStartupIfNeeded(); EnsureOverlaysOnScreen(false); ApplyHotkeys(); UpdateButtons(); UpdateBridgeStatus(); RefreshMainUi(); ScaleMainLayout(); ShowCompactMainUi(true); TryEnableDarkTitleBar();
                recoveryTimer.Start();
                if (configRecoveredFromSession) SetStatus("비정상 종료 감지: 최근 자동저장 작업을 복구했습니다.");
                else if (configRecoveredFromBackup) SetStatus("config.txt 손상 감지: 백업에서 자동 복구됨");
                else if (legacyDataMigrated) SetStatus("LightOverlay 사용자 데이터를 CatLayer로 이전했습니다.");
            };
            Shown += delegate
            {
                if (shellOverlayStartup)
                {
                    shellOverlayStartup = false;
                    BeginInvoke(new MethodInvoker(delegate { HideToTray(); }));
                }
                // Prepare the shared WebView2 environment after CatLayer is already visible.
                // This moves the expensive first-time environment setup away from the moment
                // the user adds the first web overlay, without delaying CatLayer startup UI.
                try { WebOverlayEnvironment.WarmUp(); } catch { }
                Timer webStartupRefresh = new Timer();
                webStartupRefresh.Interval = 1400;
                webStartupRefresh.Tick += delegate
                {
                    webStartupRefresh.Stop();
                    // Healthy WebView2 overlays are already navigating. Reload only overlays whose
                    // first initialization genuinely failed instead of duplicating every page request.
                    foreach (OverlayItemForm wf in items)
                        if (wf != null && !wf.IsDisposed && wf.Type == ItemType.Web && wf.NeedsWebStartupRecovery) wf.ReloadWeb();
                    webStartupRefresh.Dispose();
                };
                webStartupRefresh.Start();
                BeginStartupUpdateCheck();
            };
            Resize += delegate { if (WindowState != FormWindowState.Minimized) ScaleMainLayout(); };
            ResizeEnd += delegate { if (WindowState == FormWindowState.Normal) { ScaleMainLayout(); SaveConfig(); } };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                FinishCleanSession();
                try { SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { }
                UnregisterAllHotkeys();
                bridgeUiTimer.Stop(); foregroundUiTimer.Stop(); recoveryTimer.Stop(); trayIcon.Visible = false;
                try { beginnerToolTip.RemoveAll(); beginnerToolTip.Dispose(); } catch { }
                try { if (beginnerQuickBar != null) beginnerQuickBar.Dispose(); } catch { }
                try { if (beginnerToast != null) beginnerToast.Dispose(); } catch { }
                try { if (bannerBox.Image != null) bannerBox.Image.Dispose(); } catch { }
                try { if (mascotBox.Image != null) mascotBox.Image.Dispose(); } catch { }
                try { if (starsBox.Image != null) starsBox.Image.Dispose(); } catch { }
                foreach (OverlayItemForm f in new List<OverlayItemForm>(items)) f.Dispose();
                try
                {
                    while (undoStates.Count > 0)
                    {
                        UndoState state = undoStates[undoStates.Count - 1];
                        undoStates.RemoveAt(undoStates.Count - 1);
                        ReleaseUndoState(state);
                    }
                }
                catch { }
            };
        }

        private static readonly Color UiBack = DesignTheme.GetColor("Back", Color.FromArgb(7, 18, 38));
        private static readonly Color UiPanel = DesignTheme.GetColor("Panel", Color.FromArgb(13, 29, 55));
        private static readonly Color UiPanel2 = DesignTheme.GetColor("Panel2", Color.FromArgb(20, 39, 69));
        private static readonly Color UiBorder = DesignTheme.GetColor("Border", Color.FromArgb(42, 59, 88));
        private static readonly Color UiAccent = DesignTheme.GetColor("Accent", Color.FromArgb(145, 83, 255));
        private static readonly Color UiAccentSoft = DesignTheme.GetColor("AccentSoft", Color.FromArgb(56, 43, 101));
        private static readonly Color UiText = DesignTheme.GetColor("Text", Color.FromArgb(236, 240, 248));
        private static readonly Color UiMuted = DesignTheme.GetColor("Muted", Color.FromArgb(151, 162, 184));
        private static readonly Color UiDanger = DesignTheme.GetColor("Danger", Color.FromArgb(255, 93, 98));

        private void BuildMainUi()
        {
            Controls.Clear();

            // Keep the mascot asset for the installer, but do not show it in the main window.
            mascotBox.Image = null;
            mascotBox.Visible = false;

            Label appTitle = NewLabel("CatLayer", 20, 17, 220, 28, 16F, FontStyle.Bold, UiText);
            versionLabel.Text = "v" + AppInfo.Version + (string.IsNullOrWhiteSpace(AppInfo.BuildLabel) ? "" : "  [" + AppInfo.BuildLabel + "]");
            versionLabel.SetBounds(158, 19, 160, 22);
            versionLabel.ForeColor = UiMuted;
            versionLabel.BackColor = UiPanel2;
            versionLabel.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(versionLabel);
            starsBox.SetBounds(250, 16, 96, 24);
            starsBox.BackColor = Color.Transparent;
            starsBox.SizeMode = PictureBoxSizeMode.Zoom;
            starsBox.Image = LoadBundledImage("ui_stars.png");
            if (starsBox.Image != null) Controls.Add(starsBox);

            Button compactTopButton = new Button();
            compactTopButton.Text = "새 메인화면"; compactTopButton.SetBounds(886, 14, 106, 30); StyleButton(compactTopButton, false);
            compactTopButton.Click += delegate { ShowCompactMainUi(false); }; Controls.Add(compactTopButton);
            Button remoteTopButton = new Button();
            remoteTopButton.Text = "리모컨"; remoteTopButton.SetBounds(1000, 14, 80, 30); StyleButton(remoteTopButton, false);
            remoteTopButton.Click += delegate { ShowRemoteControl(); }; Controls.Add(remoteTopButton);
            menuButton.Text = "메뉴";
            menuButton.SetBounds(1088, 14, 68, 30);
            StyleButton(menuButton, false);
            Controls.Add(menuButton);
            ContextMenuStrip appMenu = new ContextMenuStrip();
            ToolStripMenuItem compactReturnItem = new ToolStripMenuItem("새 메인화면으로 돌아가기"); compactReturnItem.Click += delegate { ShowCompactMainUi(false); };
            appMenu.Items.Add(compactReturnItem);
            appMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem remoteItem = new ToolStripMenuItem("리모컨 열기"); remoteItem.Click += delegate { ShowRemoteControl(); };
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
            ToolStripMenuItem programMagnetSettingsItem = new ToolStripMenuItem("프로그램 자석 감도..."); programMagnetSettingsItem.Click += delegate { ShowProgramMagnetSettings(); };
            ToolStripMenuItem resizeGraceSettingsItem = new ToolStripMenuItem("크기 조절 판정 여유..."); resizeGraceSettingsItem.Click += delegate { ShowResizeGraceSettings(); };
            ToolStripMenuItem imageOptimizationItem = new ToolStripMenuItem("대형 이미지 자동 최적화 (권장)");
            imageOptimizationItem.CheckOnClick = false;
            imageOptimizationItem.Click += delegate { autoOptimizeImages = !autoOptimizeImages; imageOptimizationItem.Checked = autoOptimizeImages; SaveConfig(); SetStatus(autoOptimizeImages ? "대형 이미지 자동 최적화: ON" : "대형 이미지 자동 최적화: OFF"); };
            ToolStripMenuItem explorerOverlayItem = new ToolStripMenuItem("탐색기 우클릭에 'CatLayer로 띄우기' 표시");
            explorerOverlayItem.CheckOnClick = false;
            explorerOverlayItem.Click += delegate { SetExplorerImageContextMenuEnabled(!IsExplorerImageContextMenuEnabled(), false); explorerOverlayItem.Checked = IsExplorerImageContextMenuEnabled(); };
            ToolStripMenuItem webInteractionMenu = new ToolStripMenuItem("웹 조작 방식");
            ToolStripMenuItem webDoubleClickItem = new ToolStripMenuItem("더블클릭");
            ToolStripMenuItem webSingleClickItem = new ToolStripMenuItem("원클릭");
            ToolStripMenuItem webIntegratedItem = new ToolStripMenuItem("통합");
            webDoubleClickItem.Checked = webInteractionStyle == WebInteractionStyle.DoubleClick;
            webSingleClickItem.Checked = webInteractionStyle == WebInteractionStyle.SingleClick;
            webIntegratedItem.Checked = webInteractionStyle == WebInteractionStyle.Integrated;
            webDoubleClickItem.Click += delegate { SetWebInteractionStyle(WebInteractionStyle.DoubleClick, true); webDoubleClickItem.Checked = true; webSingleClickItem.Checked = false; webIntegratedItem.Checked = false; };
            webSingleClickItem.Click += delegate { SetWebInteractionStyle(WebInteractionStyle.SingleClick, true); webDoubleClickItem.Checked = false; webSingleClickItem.Checked = true; webIntegratedItem.Checked = false; };
            webIntegratedItem.Click += delegate { SetWebInteractionStyle(WebInteractionStyle.Integrated, true); webDoubleClickItem.Checked = false; webSingleClickItem.Checked = false; webIntegratedItem.Checked = true; };
            webInteractionMenu.DropDownItems.Add(webDoubleClickItem);
            webInteractionMenu.DropDownItems.Add(webSingleClickItem);
            webInteractionMenu.DropDownItems.Add(webIntegratedItem);
            ToolStripMenuItem basicSettingsItem = new ToolStripMenuItem("쉬운 설정");
            ToolStripMenuItem beginnerHelpItem = new ToolStripMenuItem("초보자 도움 기능");
            beginnerHelpItem.CheckOnClick = false;
            beginnerHelpItem.Click += delegate { SetBeginnerHelpEnabled(!beginnerHelpEnabled, true); beginnerHelpItem.Checked = beginnerHelpEnabled; };
            ToolStripMenuItem rescueScreenItem = new ToolStripMenuItem("화면 상태 복구"); rescueScreenItem.Click += delegate { RestoreComfortableEditingState(); };
            basicSettingsItem.DropDownItems.Add(beginnerHelpItem);
            basicSettingsItem.DropDownItems.Add(rescueScreenItem);
            basicSettingsItem.DropDownItems.Add(new ToolStripSeparator());
            basicSettingsItem.DropDownItems.Add(imageOptimizationItem);
            basicSettingsItem.DropDownItems.Add(explorerOverlayItem);

            ToolStripMenuItem advancedSettingsItem = new ToolStripMenuItem("고급 설정");
            advancedSettingsItem.DropDownItems.Add(zoomSettingsItem);
            advancedSettingsItem.DropDownItems.Add(rotationSnapSettingsItem);
            advancedSettingsItem.DropDownItems.Add(placementSnapSettingsItem);
            advancedSettingsItem.DropDownItems.Add(programMagnetSettingsItem);
            advancedSettingsItem.DropDownItems.Add(resizeGraceSettingsItem);
            advancedSettingsItem.DropDownItems.Add(webInteractionMenu);

            // BETA7: shortcut settings keep the original v1.2.1 access path.
            // Do not bury or redesign the established shortcut workflow.
            settingsItem.DropDownItems.Add(hotkeySettingsItem);
            settingsItem.DropDownItems.Add(presetHotkeySettingsItem);
            settingsItem.DropDownItems.Add(new ToolStripSeparator());
            settingsItem.DropDownItems.Add(basicSettingsItem);
            settingsItem.DropDownItems.Add(advancedSettingsItem);
            // TEST 10 optimization: the old always-on Pinterest drag diagnostic is removed
            // from the normal UI. The Chromium/Pinterest parser itself remains enabled.
            ToolStripMenuItem startupItem = new ToolStripMenuItem("컴퓨터 시작 시 실행");
            startupItem.CheckOnClick = false;
            startupItem.Click += delegate { SetStartupEnabled(!IsStartupEnabled()); startupItem.Checked = IsStartupEnabled(); };
            ToolStripMenuItem uninstallItem = new ToolStripMenuItem("CatLayer 제거...");
            uninstallItem.Click += delegate { RunInstalledUninstaller(); };
            appMenu.Items.Add(remoteItem);
            appMenu.Items.Add(new ToolStripSeparator());
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
                beginnerQuickBarMenuSuppressed = true;
                HideBeginnerQuickBar(false);
                imageOptimizationItem.Checked = autoOptimizeImages;
                explorerOverlayItem.Checked = IsExplorerImageContextMenuEnabled();
                beginnerHelpItem.Checked = beginnerHelpEnabled;
                webDoubleClickItem.Checked = webInteractionStyle == WebInteractionStyle.DoubleClick;
                webSingleClickItem.Checked = webInteractionStyle == WebInteractionStyle.SingleClick;
                webIntegratedItem.Checked = webInteractionStyle == WebInteractionStyle.Integrated;
                startupItem.Checked = IsStartupEnabled();
                uninstallItem.Enabled = true;
            };
            appMenu.Closed += delegate
            {
                beginnerQuickBarMenuSuppressed = false;
            };
            menuButton.Click += delegate
            {
                HideBeginnerQuickBar(true);
                appMenu.Show(menuButton, new Point(0, menuButton.Height));
            };

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
            overlayList.MouseDoubleClick += delegate(object sender, MouseEventArgs e)
            {
                ListViewItem row = overlayList.GetItemAt(e.X, e.Y);
                OverlayItemForm f = row == null ? null : row.Tag as OverlayItemForm;
                if (f == null || f.GroupId <= 0 || row.Text == null || !(row.Text.StartsWith("▶") || row.Text.StartsWith("▼"))) return;
                if (collapsedGroups.Contains(f.GroupId)) collapsedGroups.Remove(f.GroupId); else collapsedGroups.Add(f.GroupId);
                SaveConfigWithoutUiRefresh(); RefreshMainUi();
            };
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
            ToolStripMenuItem copyOverlay = new ToolStripMenuItem("오버레이 복제");
            copyOverlay.Click += delegate { DuplicateSelectedOverlays(); };
            ToolStripMenuItem webToolsMenu = new ToolStripMenuItem("웹 도구");
            ToolStripMenuItem webChangeAddress = new ToolStripMenuItem("주소 변경..."); webChangeAddress.Click += delegate { OverlayItemForm f = SelectedOverlay; if (f != null) ChangeWebUrlInteractive(f); };
            ToolStripMenuItem webReload = new ToolStripMenuItem("새로고침"); webReload.Click += delegate { OverlayItemForm f = SelectedOverlay; if (f != null) f.ReloadWeb(); };
            ToolStripMenuItem webBack = new ToolStripMenuItem("뒤로"); webBack.Click += delegate { OverlayItemForm f = SelectedOverlay; if (f != null) f.GoBackWeb(); };
            ToolStripMenuItem webForward = new ToolStripMenuItem("앞으로"); webForward.Click += delegate { OverlayItemForm f = SelectedOverlay; if (f != null) f.GoForwardWeb(); };
            ToolStripMenuItem webCss = new ToolStripMenuItem("커스텀 CSS..."); webCss.Click += delegate { OverlayItemForm f = SelectedOverlay; if (f != null) EditWebCustomCssInteractive(f); };
            ToolStripMenuItem webOpacityMenu = new ToolStripMenuItem("전체 투명도..."); webOpacityMenu.Click += delegate { OverlayItemForm f = SelectedOverlay; if (f != null) { int? v = UiPrompt.AskOpacity(this, f.OpacityPercent); if (v.HasValue) { CaptureUndo("웹 전체 투명도 변경"); f.SetOpacityPercent(v.Value, true); } } };
            webToolsMenu.DropDownItems.Add(webChangeAddress); webToolsMenu.DropDownItems.Add(webReload); webToolsMenu.DropDownItems.Add(new ToolStripSeparator()); webToolsMenu.DropDownItems.Add(webBack); webToolsMenu.DropDownItems.Add(webForward); webToolsMenu.DropDownItems.Add(new ToolStripSeparator()); webToolsMenu.DropDownItems.Add(webOpacityMenu); webToolsMenu.DropDownItems.Add(webCss);
            ToolStripMenuItem remoteFromListMenu = new ToolStripMenuItem("리모컨 열기"); remoteFromListMenu.Click += delegate { ShowRemoteControl(); };
            ToolStripMenuItem loadPresetFromMenu = new ToolStripMenuItem("프리셋 불러오기..."); loadPresetFromMenu.Click += delegate { LoadPresetInteractive(); };
            ToolStripMenuItem loadGroupTopLevel = new ToolStripMenuItem("그룹 불러오기..."); loadGroupTopLevel.Click += delegate { LoadGroupInteractive(); };
            ToolStripMenuItem attachProgram = new ToolStripMenuItem("프로그램 자석..."); attachProgram.Click += delegate { AttachSelectedOverlayToProgramInteractive(); };
            ToolStripMenuItem detachProgram = new ToolStripMenuItem("프로그램 자석 해제"); detachProgram.Click += delegate { DetachSelectedOverlayFromProgram(); };
            ToolStripMenuItem makeGroup = new ToolStripMenuItem("선택 항목 그룹 만들기");
            makeGroup.Click += delegate { GroupSelectedOverlays(); };
            ToolStripMenuItem saveGroupFile = new ToolStripMenuItem("그룹 파일로 저장...");
            saveGroupFile.Click += delegate { SaveSelectedGroupInteractive(); };
            ToolStripMenuItem loadGroupFromMenu = new ToolStripMenuItem("그룹 파일 불러오기...");
            loadGroupFromMenu.Click += delegate { LoadGroupInteractive(); };
            ToolStripMenuItem renameGroup = new ToolStripMenuItem("그룹 이름 변경..."); renameGroup.Click += delegate { RenameSelectedGroupInteractive(); };
            ToolStripMenuItem toggleGroupFolder = new ToolStripMenuItem("그룹 접기/펼치기"); toggleGroupFolder.Click += delegate { ToggleSelectedGroupCollapsed(); };
            ToolStripMenuItem breakGroup = new ToolStripMenuItem("그룹 해제");
            breakGroup.Click += delegate { UngroupSelectedOverlays(); };
            ToolStripMenuItem alignMenu = new ToolStripMenuItem("정렬");
            ToolStripMenuItem autoArrangeGrid = new ToolStripMenuItem("자동 정렬 (그리드)"); autoArrangeGrid.Click += delegate { AutoArrangeSelectedOverlays(); };
            ToolStripMenuItem alignLeft = new ToolStripMenuItem("왼쪽 맞춤"); alignLeft.Click += delegate { AlignSelectedOverlays("left"); };
            ToolStripMenuItem alignCenter = new ToolStripMenuItem("가로 중앙 맞춤"); alignCenter.Click += delegate { AlignSelectedOverlays("centerx"); };
            ToolStripMenuItem alignRight = new ToolStripMenuItem("오른쪽 맞춤"); alignRight.Click += delegate { AlignSelectedOverlays("right"); };
            ToolStripMenuItem alignTop = new ToolStripMenuItem("위쪽 맞춤"); alignTop.Click += delegate { AlignSelectedOverlays("top"); };
            ToolStripMenuItem alignMiddle = new ToolStripMenuItem("세로 중앙 맞춤"); alignMiddle.Click += delegate { AlignSelectedOverlays("centery"); };
            ToolStripMenuItem alignBottom = new ToolStripMenuItem("아래쪽 맞춤"); alignBottom.Click += delegate { AlignSelectedOverlays("bottom"); };
            alignMenu.DropDownItems.Add(autoArrangeGrid);
            alignMenu.DropDownItems.Add(new ToolStripSeparator());
            alignMenu.DropDownItems.Add(alignLeft); alignMenu.DropDownItems.Add(alignCenter); alignMenu.DropDownItems.Add(alignRight);
            alignMenu.DropDownItems.Add(new ToolStripSeparator());
            alignMenu.DropDownItems.Add(alignTop); alignMenu.DropDownItems.Add(alignMiddle); alignMenu.DropDownItems.Add(alignBottom);
            ToolStripMenuItem distributeMenu = new ToolStripMenuItem("간격 동일");
            ToolStripMenuItem distributeX = new ToolStripMenuItem("가로 간격 동일"); distributeX.Click += delegate { DistributeSelectedOverlays(true); };
            ToolStripMenuItem distributeY = new ToolStripMenuItem("세로 간격 동일"); distributeY.Click += delegate { DistributeSelectedOverlays(false); };
            distributeMenu.DropDownItems.Add(distributeX); distributeMenu.DropDownItems.Add(distributeY);
            ToolStripMenuItem deleteOverlay = new ToolStripMenuItem("선택 항목 삭제   Del");
            deleteOverlay.Click += delegate { DeleteSelectedOverlays(); };
            overlayListMenu.Items.Add(remoteFromListMenu);
            overlayListMenu.Items.Add(new ToolStripSeparator());
            overlayListMenu.Items.Add(loadPresetFromMenu);
            overlayListMenu.Items.Add(loadGroupTopLevel);
            overlayListMenu.Items.Add(new ToolStripSeparator());
            overlayListMenu.Items.Add(renameOverlay);
            overlayListMenu.Items.Add(copyOverlay);
            overlayListMenu.Items.Add(webToolsMenu);
            overlayListMenu.Items.Add(attachProgram);
            overlayListMenu.Items.Add(detachProgram);
            overlayListMenu.Items.Add(deleteOverlay);
            overlayListMenu.Items.Add(new ToolStripSeparator());
            overlayListMenu.Items.Add(alignMenu);
            overlayListMenu.Items.Add(distributeMenu);
            overlayListMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem listHierarchyMenu = new ToolStripMenuItem("Hierarchy 부모/자식");
            ToolStripMenuItem listSetParent = new ToolStripMenuItem("선택 항목의 부모로 지정"); listSetParent.Click += delegate { SetHierarchyParentFromSelection(overlayListContextTarget ?? SelectedOverlay); };
            ToolStripMenuItem listClearParent = new ToolStripMenuItem("부모 해제"); listClearParent.Click += delegate { ClearHierarchyParent(overlayListContextTarget ?? SelectedOverlay); };
            listHierarchyMenu.DropDownItems.Add(listSetParent); listHierarchyMenu.DropDownItems.Add(listClearParent);
            overlayListMenu.Items.Add(listHierarchyMenu);
            overlayListMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem listGroupMenu = new ToolStripMenuItem("그룹");
            listGroupMenu.DropDownItems.Add(makeGroup);
            listGroupMenu.DropDownItems.Add(saveGroupFile);
            listGroupMenu.DropDownItems.Add(loadGroupFromMenu);
            listGroupMenu.DropDownItems.Add(new ToolStripSeparator());
            listGroupMenu.DropDownItems.Add(renameGroup);
            listGroupMenu.DropDownItems.Add(toggleGroupFolder);
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

                OverlayItemForm hierarchyParent = overlayListContextTarget ?? SelectedOverlay;
                listSetParent.Text = GetHierarchyParentMenuText(hierarchyParent);
                listSetParent.Enabled = CanSetHierarchyParentFromSelection(hierarchyParent);
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
            hotkeyHideButton.SetBounds(214, 8, 112, 32); StyleButton(hotkeyHideButton, false); hotkeyHideButton.Text = "빠른 숨김  F9"; hotkeyHideButton.Click += delegate { ShowHotkeySettings(); }; hotkeys.Controls.Add(hotkeyHideButton);
            hotkeyDetailButton.SetBounds(332, 8, 112, 32); StyleButton(hotkeyDetailButton, false); hotkeyDetailButton.Text = "웹  F10"; hotkeyDetailButton.Click += delegate { ToggleWebControlMode(); }; hotkeys.Controls.Add(hotkeyDetailButton);
            hideButton.SetBounds(450, 8, 112, 32); StyleButton(hideButton, false); hideButton.Click += delegate { ToggleHidden(); }; hotkeys.Controls.Add(hideButton);
            hideButton.Text = "전체 숨김";
            trayButton.SetBounds(568, 8, 126, 32); StyleButton(trayButton, false); trayButton.Text = "백그라운드"; trayButton.Click += delegate { HideToTray(); }; hotkeys.Controls.Add(trayButton);

            Panel status = NewPanel(741, 694, 421, 48);
            obsBridgeLabel.SetBounds(14, 8, 155, 16); obsBridgeLabel.ForeColor = UiMuted; obsBridgeLabel.Font = new Font(Font.FontFamily, 8F); status.Controls.Add(obsBridgeLabel);
            statusLabel.SetBounds(14, 24, 390, 18); statusLabel.ForeColor = UiMuted; statusLabel.Font = new Font(Font.FontFamily, 8F); status.Controls.Add(statusLabel);
            statusLabel.Text = "준비";

            // TEST 07: right-clicking the legacy main-screen background/panels also opens
            // the normal app menu. Its first item returns to the new compact main screen.
            AttachLegacyMainContextMenuRecursive(this, appMenu);

            BuildCompactMainUi();
        }

        private void AttachLegacyMainContextMenuRecursive(Control root, ContextMenuStrip menu)
        {
            if (root == null || menu == null) return;
            try
            {
                bool safeSurface = root is Form || root is Panel || root is Label || root is PictureBox || root is GroupBox;
                if (safeSurface && root.ContextMenuStrip == null) root.ContextMenuStrip = menu;
            }
            catch { }
            foreach (Control child in root.Controls) AttachLegacyMainContextMenuRecursive(child, menu);
        }

        private void BuildCompactMainUi()
        {
            legacyMainControls.Clear();
            foreach (Control control in Controls)
            {
                if (control != null) legacyMainControls.Add(control);
            }

            compactMainPanel = new Panel();
            compactMainPanel.Dock = DockStyle.Fill;
            compactMainPanel.BackColor = UiBack;
            compactMainPanel.TabStop = true;
            Controls.Add(compactMainPanel);
            compactMainPanel.BringToFront();

            Label brand = new Label();
            brand.Text = "CatLayer";
            brand.ForeColor = UiText;
            brand.BackColor = Color.Transparent;
            brand.Font = new Font("Segoe UI", 18F, FontStyle.Bold, GraphicsUnit.Point);
            compactMainPanel.Controls.Add(brand);

            Label build = new Label();
            build.Text = "v" + AppInfo.Version + (string.IsNullOrWhiteSpace(AppInfo.BuildLabel) ? "" : "  [" + AppInfo.BuildLabel + "]");
            build.ForeColor = UiMuted;
            build.BackColor = Color.Transparent;
            build.Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            compactMainPanel.Controls.Add(build);

            Panel fileZone = new Panel();
            compactFileZone = fileZone;
            fileZone.BackColor = UiPanel;
            fileZone.Cursor = Cursors.Hand;
            compactMainPanel.Controls.Add(fileZone);

            Label plus = new Label();
            plus.Text = "+";
            plus.TextAlign = ContentAlignment.MiddleCenter;
            plus.ForeColor = UiAccent;
            plus.BackColor = Color.Transparent;
            plus.Font = new Font("Segoe UI", 40F, FontStyle.Regular, GraphicsUnit.Point);
            plus.Cursor = Cursors.Hand;
            fileZone.Controls.Add(plus);

            Label fileTitle = new Label();
            compactFileTitle = fileTitle;
            fileTitle.Text = "파일 오버레이 추가";
            fileTitle.TextAlign = ContentAlignment.MiddleCenter;
            fileTitle.ForeColor = UiText;
            fileTitle.BackColor = Color.Transparent;
            fileTitle.Font = new Font("Segoe UI", 14F, FontStyle.Bold, GraphicsUnit.Point);
            fileTitle.Cursor = Cursors.Hand;
            fileZone.Controls.Add(fileTitle);

            Label fileHint = new Label();
            compactFileHint = fileHint;
            fileHint.Text = "클릭해서 파일 선택  ·  파일이나 브라우저 이미지를 끌어놔도 됩니다";
            fileHint.TextAlign = ContentAlignment.MiddleCenter;
            fileHint.ForeColor = UiMuted;
            fileHint.BackColor = Color.Transparent;
            fileHint.Font = new Font("Segoe UI", 8.7F, FontStyle.Regular, GraphicsUnit.Point);
            fileHint.Cursor = Cursors.Hand;
            fileZone.Controls.Add(fileHint);

            Label rightHint = new Label();
            rightHint.Text = "이미지를 추가한 뒤 클릭하면 쉬운 조작바가 표시됩니다  ·  우클릭: 전체 메뉴";
            rightHint.TextAlign = ContentAlignment.MiddleCenter;
            rightHint.ForeColor = UiMuted;
            rightHint.BackColor = Color.Transparent;
            rightHint.Font = new Font("Segoe UI", 8.2F, FontStyle.Regular, GraphicsUnit.Point);
            rightHint.Cursor = Cursors.Hand;
            fileZone.Controls.Add(rightHint);

            Button gear = new Button();
            gear.Text = "⚙";
            gear.FlatStyle = FlatStyle.Flat;
            gear.FlatAppearance.BorderSize = 1;
            gear.FlatAppearance.BorderColor = UiBorder;
            gear.BackColor = UiPanel2;
            gear.ForeColor = UiText;
            gear.Font = new Font("Segoe UI Symbol", 16F, FontStyle.Regular, GraphicsUnit.Point);
            gear.Cursor = Cursors.Hand;
            compactMainPanel.Controls.Add(gear);

            Button help = new Button();
            compactHelpButton = help;
            help.Text = "?";
            help.FlatStyle = FlatStyle.Flat;
            help.FlatAppearance.BorderSize = 1;
            help.FlatAppearance.BorderColor = UiBorder;
            help.BackColor = UiPanel2;
            help.ForeColor = UiText;
            help.Font = new Font("Segoe UI", 13F, FontStyle.Bold, GraphicsUnit.Point);
            help.Cursor = Cursors.Hand;
            help.Click += delegate { ShowCompactTutorial(); };
            compactMainPanel.Controls.Add(help);

            Action layoutCompact = delegate
            {
                int cw = Math.Max(1, compactMainPanel.ClientSize.Width);
                int ch = Math.Max(1, compactMainPanel.ClientSize.Height);
                brand.SetBounds(24, 18, Math.Max(220, cw - 170), 36);
                build.SetBounds(26, 54, Math.Max(220, cw - 170), 22);

                int zoneW = Math.Max(300, Math.Min(480, cw - 48));
                int zoneH = Math.Max(190, Math.Min(230, ch - 145));
                int zoneX = Math.Max(24, (cw - zoneW) / 2);
                int zoneY = Math.Max(90, (ch - zoneH) / 2 + 12);
                fileZone.SetBounds(zoneX, zoneY, zoneW, zoneH);

                plus.SetBounds(0, 18, zoneW, 68);
                fileTitle.SetBounds(0, 88, zoneW, 34);
                fileHint.SetBounds(16, 132, Math.Max(1, zoneW - 32), 28);
                rightHint.SetBounds(16, 164, Math.Max(1, zoneW - 32), 24);
                help.SetBounds(Math.Max(8, cw - 114), Math.Max(8, ch - 58), 42, 42);
                gear.SetBounds(Math.Max(8, cw - 62), Math.Max(8, ch - 58), 46, 42);
            };
            compactMainPanel.Resize += delegate { layoutCompact(); };
            fileZone.Resize += delegate
            {
                int zoneW = fileZone.ClientSize.Width;
                plus.Width = zoneW;
                fileTitle.Width = zoneW;
                fileHint.Width = Math.Max(1, zoneW - 32);
                rightHint.Width = Math.Max(1, zoneW - 32);
            };
            layoutCompact();

            compactMainMenu = BuildCompactMainMenu();

            // The legacy MainForm owns a context menu containing "새 메인화면으로 돌아가기".
            // Give every compact-screen surface its own menu explicitly so that legacy
            // navigation never leaks into the already-active new main screen.
            compactMainPanel.ContextMenuStrip = compactMainMenu;
            brand.ContextMenuStrip = compactMainMenu;
            build.ContextMenuStrip = compactMainMenu;
            fileZone.ContextMenuStrip = compactMainMenu;
            plus.ContextMenuStrip = compactMainMenu;
            fileTitle.ContextMenuStrip = compactMainMenu;
            fileHint.ContextMenuStrip = compactMainMenu;
            rightHint.ContextMenuStrip = compactMainMenu;
            gear.ContextMenuStrip = compactMainMenu;
            help.ContextMenuStrip = compactMainMenu;

            MouseEventHandler dismissQuickBarMouse = delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
                    HideBeginnerQuickBar(true);
            };

            compactMainPanel.MouseDown += dismissQuickBarMouse;
            brand.MouseDown += dismissQuickBarMouse;
            build.MouseDown += dismissQuickBarMouse;
            fileZone.MouseDown += dismissQuickBarMouse;
            plus.MouseDown += dismissQuickBarMouse;
            fileTitle.MouseDown += dismissQuickBarMouse;
            fileHint.MouseDown += dismissQuickBarMouse;
            rightHint.MouseDown += dismissQuickBarMouse;
            gear.MouseDown += dismissQuickBarMouse;
            help.MouseDown += dismissQuickBarMouse;

            MouseEventHandler fileZoneMouse = delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) QuickAddFileOverlay();
            };
            // v1.2.1: restore the original compact click target.
            // Only the file-add card and its own text/icon are clickable.
            fileZone.MouseClick += fileZoneMouse;
            plus.MouseClick += fileZoneMouse;
            fileTitle.MouseClick += fileZoneMouse;
            fileHint.MouseClick += fileZoneMouse;
            rightHint.MouseClick += fileZoneMouse;

            gear.Click += delegate { HideBeginnerQuickBar(true); ShowCompactMainMenu(gear.PointToScreen(new Point(0, gear.Height))); };

            // Keep the old controls alive and visible behind this full-size panel. In TEST 05
            // hiding the legacy ListView could make selection/Del handling unreliable.
            compactMainPanel.Visible = true;
            compactMainActive = true;
        }

        private ContextMenuStrip BuildCompactMainMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem settings = new ToolStripMenuItem("설정");
            ToolStripMenuItem hotkeys = new ToolStripMenuItem("사용자 지정 단축키..."); hotkeys.Click += delegate { ShowHotkeySettings(); };
            ToolStripMenuItem presetHotkeys = new ToolStripMenuItem("프리셋 단축키..."); presetHotkeys.Click += delegate { ShowPresetHotkeySettings(); };
            ToolStripMenuItem zoom = new ToolStripMenuItem("확대/축소 비율..."); zoom.Click += delegate { ShowZoomSettings(); };
            ToolStripMenuItem rotationSnap = new ToolStripMenuItem("회전 자석 감도..."); rotationSnap.Click += delegate { ShowRotationSnapSettings(); };
            ToolStripMenuItem placementSnap = new ToolStripMenuItem("배치 자석 감도..."); placementSnap.Click += delegate { ShowPlacementSnapSettings(); };
            ToolStripMenuItem programMagnet = new ToolStripMenuItem("프로그램 자석 감도..."); programMagnet.Click += delegate { ShowProgramMagnetSettings(); };
            ToolStripMenuItem resizeGrace = new ToolStripMenuItem("크기 조절 판정 여유..."); resizeGrace.Click += delegate { ShowResizeGraceSettings(); };
            ToolStripMenuItem imageOptimization = new ToolStripMenuItem("대형 이미지 자동 최적화 (권장)");
            imageOptimization.CheckOnClick = false;
            imageOptimization.Click += delegate { autoOptimizeImages = !autoOptimizeImages; imageOptimization.Checked = autoOptimizeImages; SaveConfig(); SetStatus(autoOptimizeImages ? "대형 이미지 자동 최적화: ON" : "대형 이미지 자동 최적화: OFF"); };
            ToolStripMenuItem explorerOverlay = new ToolStripMenuItem("탐색기 우클릭에 'CatLayer로 띄우기' 표시");
            explorerOverlay.CheckOnClick = false;
            explorerOverlay.Click += delegate { SetExplorerImageContextMenuEnabled(!IsExplorerImageContextMenuEnabled(), false); explorerOverlay.Checked = IsExplorerImageContextMenuEnabled(); };
            ToolStripMenuItem startup = new ToolStripMenuItem("컴퓨터 시작 시 실행");
            startup.Click += delegate { SetStartupEnabled(!IsStartupEnabled()); startup.Checked = IsStartupEnabled(); };
            ToolStripMenuItem update = new ToolStripMenuItem("업데이트 확인..."); update.Click += delegate { CheckForUpdates(true); };
            ToolStripMenuItem shortcut = new ToolStripMenuItem("바로가기 만들기"); shortcut.Click += delegate { InstallShortcuts(); };
            ToolStripMenuItem basicSettings = new ToolStripMenuItem("쉬운 설정");
            ToolStripMenuItem beginnerHelp = new ToolStripMenuItem("초보자 도움 기능");
            beginnerHelp.CheckOnClick = false;
            beginnerHelp.Click += delegate { SetBeginnerHelpEnabled(!beginnerHelpEnabled, true); beginnerHelp.Checked = beginnerHelpEnabled; };
            ToolStripMenuItem rescue = new ToolStripMenuItem("화면 상태 복구"); rescue.Click += delegate { RestoreComfortableEditingState(); };
            basicSettings.DropDownItems.Add(beginnerHelp);
            basicSettings.DropDownItems.Add(rescue);
            basicSettings.DropDownItems.Add(new ToolStripSeparator());
            basicSettings.DropDownItems.Add(imageOptimization);
            basicSettings.DropDownItems.Add(explorerOverlay);
            basicSettings.DropDownItems.Add(startup);

            ToolStripMenuItem advancedSettings = new ToolStripMenuItem("고급 설정");
            advancedSettings.DropDownItems.Add(zoom);
            advancedSettings.DropDownItems.Add(rotationSnap);
            advancedSettings.DropDownItems.Add(placementSnap);
            advancedSettings.DropDownItems.Add(programMagnet);
            advancedSettings.DropDownItems.Add(resizeGrace);

            // BETA7: restore the original shortcut settings access directly
            // under Settings instead of treating shortcuts as an advanced option.
            settings.DropDownItems.Add(hotkeys);
            settings.DropDownItems.Add(presetHotkeys);
            settings.DropDownItems.Add(new ToolStripSeparator());
            settings.DropDownItems.Add(basicSettings);
            settings.DropDownItems.Add(advancedSettings);
            settings.DropDownItems.Add(new ToolStripSeparator());
            settings.DropDownItems.Add(update);
            settings.DropDownItems.Add(shortcut);

            ToolStripMenuItem remote = new ToolStripMenuItem("리모컨");
            remote.Click += delegate { ShowRemoteControl(); };

            ToolStripMenuItem presets = new ToolStripMenuItem("프리셋");
            ToolStripMenuItem savePreset = new ToolStripMenuItem("프리셋 저장..."); savePreset.Click += delegate { SavePresetInteractive(); };
            ToolStripMenuItem loadPreset = new ToolStripMenuItem("프리셋 불러오기..."); loadPreset.Click += delegate { LoadPresetInteractive(); };
            ToolStripMenuItem deletePreset = new ToolStripMenuItem("프리셋 삭제..."); deletePreset.Click += delegate { DeletePresetInteractive(); };
            ToolStripMenuItem importPreset = new ToolStripMenuItem("외부 프리셋 가져오기..."); importPreset.Click += delegate { ImportPresetInteractive(); };
            ToolStripMenuItem presetFolder = new ToolStripMenuItem("프리셋 파일 위치 열기"); presetFolder.Click += delegate { OpenPresetFolder(); };
            presets.DropDownItems.Add(savePreset);
            presets.DropDownItems.Add(loadPreset);
            presets.DropDownItems.Add(deletePreset);
            presets.DropDownItems.Add(new ToolStripSeparator());
            presets.DropDownItems.Add(importPreset);
            presets.DropDownItems.Add(presetFolder);

            ToolStripMenuItem add = new ToolStripMenuItem("오버레이 추가");
            ToolStripMenuItem addFile = new ToolStripMenuItem("파일 오버레이..."); addFile.Click += delegate { QuickAddFileOverlay(); };
            ToolStripMenuItem capture = new ToolStripMenuItem("영역 캡처"); capture.Click += delegate { AddScreenRegionCapture(); };
            ToolStripMenuItem addText = new ToolStripMenuItem("텍스트..."); addText.Click += delegate { AddTextInteractive(); };
            ToolStripMenuItem timer = new ToolStripMenuItem("타이머");
            ToolStripMenuItem oneShot = new ToolStripMenuItem("1회성 타이머"); oneShot.Click += delegate { AddTimerInteractive(TimerMode.OneShot); };
            ToolStripMenuItem repeat = new ToolStripMenuItem("반복 타이머"); repeat.Click += delegate { AddTimerInteractive(TimerMode.Repeat); };
            ToolStripMenuItem stopwatch = new ToolStripMenuItem("타임스톱"); stopwatch.Click += delegate { AddTimerInteractive(TimerMode.Stopwatch); };
            timer.DropDownItems.Add(oneShot); timer.DropDownItems.Add(repeat); timer.DropDownItems.Add(stopwatch);
            ToolStripMenuItem addObs = new ToolStripMenuItem("OBS 화면"); addObs.Click += delegate { AddObsProgram(); };
            ToolStripMenuItem addWeb = new ToolStripMenuItem("웹..."); addWeb.Click += delegate { AddWebInteractive(); };
            add.DropDownItems.Add(addFile);
            add.DropDownItems.Add(capture);
            add.DropDownItems.Add(addText);
            add.DropDownItems.Add(timer);
            add.DropDownItems.Add(addObs);
            add.DropDownItems.Add(addWeb);

            ToolStripMenuItem legacy = new ToolStripMenuItem("기존 메인화면");
            legacy.Click += delegate { ShowLegacyMainUi(); };

            ToolStripMenuItem tutorial = new ToolStripMenuItem("사용 튜토리얼");
            tutorial.Click += delegate { ShowCompactTutorial(); };

            ToolStripMenuItem info = new ToolStripMenuItem("정보 / 이용약관");
            info.Click += delegate { ShowInfoAndTerms(); };

            ToolStripMenuItem uninstall = new ToolStripMenuItem("CatLayer 제거...");
            uninstall.Click += delegate
            {
                HideBeginnerQuickBar(true);
                RunInstalledUninstaller();
            };

            menu.Items.Add(settings);
            menu.Items.Add(remote);
            menu.Items.Add(presets);
            menu.Items.Add(add);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(legacy);
            menu.Items.Add(tutorial);
            menu.Items.Add(info);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(uninstall);

            menu.Opening += delegate
            {
                beginnerQuickBarMenuSuppressed = true;
                HideBeginnerQuickBar(false);
                startup.Checked = IsStartupEnabled();
                imageOptimization.Checked = autoOptimizeImages;
                explorerOverlay.Checked = IsExplorerImageContextMenuEnabled();
                beginnerHelp.Checked = beginnerHelpEnabled;
            };
            menu.Closed += delegate
            {
                beginnerQuickBarMenuSuppressed = false;
            };

            return menu;
        }

        private void ShowCompactMainMenu(Point screenPoint)
        {
            if (compactMainMenu == null) compactMainMenu = BuildCompactMainMenu();
            try { compactMainMenu.Show(screenPoint); }
            catch { if (compactMainPanel != null) compactMainMenu.Show(compactMainPanel, compactMainPanel.PointToClient(screenPoint)); }
        }

        internal ContextMenuStrip CreateRemoteRightClickMenu()
        {
            ContextMenuStrip menu = BuildCompactMainMenu();
            ToolStripMenuItem compact = new ToolStripMenuItem("새 메인화면");
            compact.Click += delegate { ShowCompactMainUi(false); };
            menu.Items.Insert(0, new ToolStripSeparator());
            menu.Items.Insert(0, compact);
            return menu;
        }

        private void SetBeginnerHelpEnabled(bool enabled, bool save)
        {
            beginnerHelpEnabled = enabled;
            if (!beginnerHelpEnabled)
            {
                try { beginnerToolTip.Hide(compactFileZone); } catch { }
                HideBeginnerQuickBar(true);
                if (beginnerToast != null) beginnerToast.Hide();
                SetCompactDropFeedback(false);
            }
            else
            {
                UpdateBeginnerQuickBar();
            }
            if (save) SaveConfig();
            SetStatus(beginnerHelpEnabled ? "초보자 도움 기능: ON" : "초보자 도움 기능: OFF");
        }

        private void ShowBeginnerToast(string message)
        {
            ShowBeginnerToast(message, null, null);
        }

        private void ShowBeginnerToast(string message, string actionText, Action action)
        {
            if (!beginnerHelpEnabled || IsDisposed || string.IsNullOrWhiteSpace(message)) return;
            try
            {
                if (beginnerToast == null || beginnerToast.IsDisposed) beginnerToast = new BeginnerToastForm();
                Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
                beginnerToast.ShowMessage(message, actionText, action, area);
            }
            catch { }
        }

        private string BeginnerModeText()
        {
            if (WebControlMode) return "웹 조작";
            if (IntegratedMode) return "통합";
            return EditMode ? "편집" : "고정";
        }

        private string BeginnerModeDescription()
        {
            if (WebControlMode) return "웹 조작 모드 — 웹페이지를 클릭·스크롤·입력할 수 있습니다. ESC/F10으로 종료";
            if (IntegratedMode) return "통합 모드 — 일반 오버레이 편집과 웹 조작을 함께 사용합니다.";
            if (EditMode) return "편집 모드 — 오버레이를 클릭하고 이동하거나 크기를 바꿀 수 있습니다.";
            return "고정 모드 — 오버레이가 클릭을 통과해 아래 프로그램을 그대로 조작할 수 있습니다.";
        }

        private void ShowBeginnerModeToast()
        {
            ShowBeginnerToast(BeginnerModeDescription());
        }

        private Rectangle SelectedOverlayUnionBounds()
        {
            Rectangle union = Rectangle.Empty;
            foreach (OverlayItemForm f in SelectedOverlays)
            {
                if (f == null || f.IsDisposed || !f.IsOverlayVisible) continue;
                union = union.IsEmpty ? f.Bounds : Rectangle.Union(union, f.Bounds);
            }
            return union;
        }

        private void HideBeginnerQuickBar(bool disarm)
        {
            if (disarm) beginnerQuickBarArmed = false;
            try
            {
                if (beginnerQuickBar != null && !beginnerQuickBar.IsDisposed)
                    beginnerQuickBar.Hide();
            }
            catch { }
        }

        private void ArmBeginnerQuickBar()
        {
            beginnerQuickBarArmed = true;
        }

        private void UpdateBeginnerQuickBar()
        {
            // BETA4: selection alone is not enough. The helper is armed only by an
            // explicit overlay interaction, so clicking the CatLayer main window
            // cannot resurrect the toolbar for an old selection.
            if (!beginnerHelpEnabled || !beginnerQuickBarArmed ||
                !EditMode || !catLayerOwnsForeground || beginnerQuickBarMenuSuppressed ||
                !Enabled || IsDisposed)
            {
                HideBeginnerQuickBar(false);
                return;
            }

            Rectangle target = SelectedOverlayUnionBounds();
            if (target.IsEmpty || SelectedOverlays.Count == 0)
            {
                HideBeginnerQuickBar(true);
                return;
            }

            try
            {
                if (beginnerQuickBar == null || beginnerQuickBar.IsDisposed)
                    beginnerQuickBar = new OverlayQuickBarForm(this);

                beginnerQuickBar.SetModeText(BeginnerModeText());
                Rectangle wa = Screen.FromRectangle(target).WorkingArea;
                int x = target.Left + Math.Max(0, (target.Width - beginnerQuickBar.Width) / 2);
                int y = target.Top - beginnerQuickBar.Height - 8;
                if (y < wa.Top + 4) y = target.Bottom + 8;
                x = Math.Max(wa.Left + 4, Math.Min(x, wa.Right - beginnerQuickBar.Width - 4));
                y = Math.Max(wa.Top + 4, Math.Min(y, wa.Bottom - beginnerQuickBar.Height - 4));

                // TopMost already keeps this helper above overlays. Repeating
                // BringToFront every foreground timer tick caused visible flashing.
                beginnerQuickBar.ShowPassive(new Point(x, y));
            }
            catch { }
        }

        private void SetCompactDropFeedback(bool active)
        {
            if (compactFileZone == null || compactFileZone.IsDisposed || !compactMainActive) return;
            if (active && !beginnerHelpEnabled) return;
            if (compactDropHighlight == active) return;
            compactDropHighlight = active;
            try
            {
                compactFileZone.BackColor = active ? UiAccentSoft : UiPanel;
                if (compactFileTitle != null) compactFileTitle.Text = active ? "여기에 놓으면 오버레이로 추가됩니다" : "파일 오버레이 추가";
                if (compactFileHint != null) compactFileHint.Text = active ? "마우스를 놓으세요" : "클릭해서 파일 선택  ·  파일이나 브라우저 이미지를 끌어놔도 됩니다";
            }
            catch { }
        }

        internal void BeginnerQuickHideSelected()
        {
            List<OverlayItemForm> selected = SelectedOverlays;
            if (selected.Count == 0) return;
            CaptureUndo(selected.Count == 1 ? "오버레이 숨김" : "선택 오버레이 숨김");
            foreach (OverlayItemForm f in selected) if (f != null && !f.IsDisposed) f.SetOverlayVisible(false);
            SaveConfigWithoutUiRefresh();
            RefreshMainUi();
            RefreshOverlaySelectionVisuals();
            SetStatus(selected.Count == 1 ? "오버레이 숨김 완료" : selected.Count.ToString() + "개 오버레이 숨김 완료");
            ShowBeginnerToast("오버레이를 숨겼습니다.", "되돌리기", delegate { UndoLastAction(); });
        }

        internal void BeginnerQuickDuplicateSelected() { DuplicateSelectedOverlays(); }
        internal void BeginnerQuickFlipSelected() { FlipSelection(true); }
        internal void BeginnerQuickUndo() { UndoLastAction(); }
        internal void BeginnerQuickDeleteSelected() { DeleteSelectedOverlays(); }

        private void RestoreComfortableEditingState()
        {
            CaptureUndo("화면 상태 복구");
            if (WebControlMode || IntegratedMode || !EditMode) SetEditorMode(EditorMode.Normal, false);
            hidden = false;
            foreach (OverlayItemForm f in items)
                if (f != null && !f.IsDisposed) { f.SetOverlayVisible(true); f.RefreshEffectiveVisibility(); }
            EnsureOverlaysOnScreen(false);
            UpdateButtons();
            SaveConfigWithoutUiRefresh();
            RefreshMainUi();
            RefreshOverlaySelectionVisuals();
            SetStatus("화면 상태 복구 완료  |  모든 오버레이 표시 + 편집 모드 + 화면 안으로 복구");
            ShowBeginnerToast("화면을 편집하기 쉬운 상태로 복구했습니다.", "되돌리기", delegate { UndoLastAction(); });
        }

        private void ShowCompactMainUi(bool startup)
        {
            if (compactMainPanel == null || compactMainPanel.IsDisposed) return;
            HideBeginnerQuickBar(true);
            compactMainActive = true;
            compactMainPanel.Visible = true;
            compactMainPanel.BringToFront();
            try { compactMainPanel.Focus(); } catch { }
            MinimumSize = new Size(460, 320);
            if (startup || ClientSize.Width > 760 || ClientSize.Height > 520) ClientSize = compactMainClientSize;
            Text = AppInfo.DisplayName;
            SetStatus("새 메인화면");
        }

        private void ShowLegacyMainUi()
        {
            HideBeginnerQuickBar(true);
            compactMainActive = false;
            if (compactMainPanel != null) compactMainPanel.Visible = false;
            MinimumSize = new Size(900, 620);
            ClientSize = mainBaseClientSize;
            ScaleMainLayout();
            Text = AppInfo.DisplayName + " - 기존 메인화면";
            SetStatus("기존 메인화면  |  상단 '새 메인화면' 버튼으로 돌아갈 수 있습니다.");
        }

        private void QuickAddFileOverlay()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "파일 오버레이 추가";
                dialog.Multiselect = true;
                dialog.Filter = "오버레이 파일|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.html;*.htm;*.catlayerweb|이미지 / GIF|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|웹 위젯 / HTML|*.catlayerweb;*.html;*.htm|모든 파일|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.FileNames == null || dialog.FileNames.Length == 0) return;

                Point formCenter = PointToScreen(new Point(Math.Max(1, ClientSize.Width / 2), Math.Max(1, ClientSize.Height / 2)));
                Point insert = DefaultImageInsertPoint(formCenter.X, formCenter.Y);
                List<string> managedImages = new List<string>();
                List<string> names = new List<string>();
                int webOffset = 0;
                int unsupported = 0;

                foreach (string path in dialog.FileNames)
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { unsupported++; continue; }

                    if (IsLocalHtmlFile(path))
                    {
                        Rectangle bounds = new Rectangle(insert.X + webOffset, insert.Y + webOffset, 800, 520);
                        if (AddLocalWebOverlay(path, bounds)) webOffset += 22; else unsupported++;
                        continue;
                    }

                    if (IsWebPackageFile(path))
                    {
                        Rectangle bounds = new Rectangle(insert.X + webOffset, insert.Y + webOffset, 800, 520);
                        if (AddCatLayerWebOverlay(path, bounds)) webOffset += 22; else unsupported++;
                        continue;
                    }

                    if (IsSupportedImageDropFile(path))
                    {
                        string managed = ImportImageAsset(path);
                        if (!string.IsNullOrEmpty(managed))
                        {
                            managedImages.Add(managed);
                            names.Add(SuggestedImageNameFromPath(path));
                        }
                        else unsupported++;
                        continue;
                    }

                    unsupported++;
                }

                if (managedImages.Count > 0)
                {
                    AddManagedImagesAt(managedImages, names, insert, "파일 오버레이 추가", "파일 오버레이 추가 완료");
                }

                if (managedImages.Count == 0 && webOffset == 0 && unsupported > 0)
                    SetStatus("지원되는 파일 오버레이를 찾지 못했습니다.");
                else if (unsupported > 0)
                    SetStatus("파일 오버레이 추가 완료  |  미지원 " + unsupported.ToString() + "개 제외");
            }
        }

        private void ShowCompactTutorial()
        {
            string message =
                "CatLayer 새 메인화면 사용법\r\n\r\n" +
                "1. 가운데 영역 좌클릭: 이미지/GIF/HTML/CatLayerWeb 파일 오버레이 추가\r\n" +
                "2. 파일 또는 브라우저 이미지를 창으로 드래그: 바로 오버레이 추가\r\n" +
                "3. 오버레이를 클릭하면 바로 위/아래에 쉬운 조작바가 표시됩니다.\r\n" +
                "4. 메인화면 우클릭 또는 오른쪽 아래 톱니바퀴: 전체 기능 메뉴\r\n" +
                "5. 실수로 삭제했다면 화면 알림의 '되돌리기' 또는 Ctrl+Z를 사용하세요.\r\n" +
                "6. 화면이 이상해졌다면 설정 > 쉬운 설정 > 화면 상태 복구를 사용하세요.\r\n" +
                "7. 예전 전체 UI가 필요하면 '기존 메인화면' 선택\r\n\r\n" +
                "기본 단축키\r\n" +
                "Alt+Q: 편집/고정 모드 전환  (F8도 사용 가능)\r\n" +
                "Alt+W: 빠른 숨김  (F9도 사용 가능)\r\n" +
                "Alt+Shift+W: 빠른 표시  (Shift+F9도 사용 가능)\r\n" +
                "Alt+E: 영역 캡처  (F7도 사용 가능)\r\n" +
                "Ctrl+C: 선택한 이미지의 원본 이미지 복사\r\n" +
                "F10: 웹 조작 전환\r\n" +
                "F11: 전체 표시/숨김";
            MessageBox.Show(this, message, "CatLayer 사용 튜토리얼", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowInfoAndTerms()
        {
            using (Form form = new Form())
            using (TextBox terms = new TextBox())
            using (Label info = new Label())
            using (Button close = new Button())
            {
                form.Text = "CatLayer 정보 / 이용약관";
                form.StartPosition = FormStartPosition.CenterParent;
                form.ClientSize = new Size(760, 580);
                form.MinimumSize = new Size(620, 460);
                form.BackColor = UiBack;
                form.ForeColor = UiText;
                form.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
                try { form.Icon = Icon; } catch { }

                info.Text = "CatLayer v" + AppInfo.Version + (string.IsNullOrWhiteSpace(AppInfo.BuildLabel) ? "" : "  [" + AppInfo.BuildLabel + "]") + "\r\n화면 위 오버레이 유틸리티";
                info.SetBounds(20, 16, 650, 48);
                info.ForeColor = UiText;
                info.BackColor = Color.Transparent;
                info.Font = new Font("Segoe UI", 11F, FontStyle.Bold, GraphicsUnit.Point);
                form.Controls.Add(info);

                terms.Multiline = true;
                terms.ReadOnly = true;
                terms.ScrollBars = ScrollBars.Vertical;
                terms.SetBounds(20, 76, 720, 438);
                terms.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
                terms.BackColor = UiPanel;
                terms.ForeColor = UiText;
                terms.BorderStyle = BorderStyle.FixedSingle;
                try
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TERMS_KO.txt");
                    terms.Text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "TERMS_KO.txt 파일을 찾을 수 없습니다.";
                }
                catch (Exception ex) { terms.Text = "이용약관을 읽지 못했습니다.\r\n" + ex.Message; }
                form.Controls.Add(terms);

                close.Text = "닫기";
                close.SetBounds(650, 526, 90, 34);
                close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                StyleButton(close, false);
                close.Click += delegate { form.Close(); };
                form.Controls.Add(close);

                form.ShowDialog(this);
            }
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

        private void ForegroundUiTimerTick()
        {
            UpdateForegroundSelectionState();
            UpdateBeginnerQuickBar();
            bool trackingPrograms = windowAttachments.Count > 0;
            if (trackingPrograms) UpdateAttachedWindows();
            int desired = trackingPrograms ? 50 : 125;
            if (foregroundUiTimer.Interval != desired) foregroundUiTimer.Interval = desired;
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
                EditorMode restore = modeBeforeWebControl == EditorMode.WebControl ? EditorMode.Normal : modeBeforeWebControl;
                SetEditorMode(restore, false);
                SetStatus(restore == EditorMode.Integrated ? "통합 모드" : (restore == EditorMode.Fixed ? "고정 모드" : "편집 모드"));
            }
            RefreshOverlaySelectionVisuals();
        }

        private void RefreshOverlaySelectionVisuals()
        {
            foreach (OverlayItemForm f in items)
                if (f != null && !f.IsDisposed) f.RefreshSelectionVisual();
            UpdateBeginnerQuickBar();
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
            ArmBeginnerQuickBar();
            HashSet<OverlayItemForm> wanted = new HashSet<OverlayItemForm>(GetGroupMembers(source));
            foreach (ListViewItem row in overlayList.Items)
            {
                OverlayItemForm f = row.Tag as OverlayItemForm;
                row.Selected = f != null && wanted.Contains(f);
            }
            RefreshPropertyEditor();
            RefreshOverlaySelectionVisuals();
        }

        public void ToggleOverlaySelectionForEditing(OverlayItemForm source)
        {
            if (source == null || overlayList == null || overlayList.IsDisposed) return;
            ArmBeginnerQuickBar();
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
            RefreshOverlaySelectionVisuals();
            SetStatus(SelectedOverlays.Count.ToString() + "개 항목 선택됨  |  그룹화 단축키로 바로 묶을 수 있습니다.");
        }

        public void PrepareOverlayForMouseRotation(OverlayItemForm source)
        {
            if (source == null || overlayList == null || overlayList.IsDisposed) return;
            ArmBeginnerQuickBar();
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
                HashSet<int> shownGroups = new HashSet<int>();
                for (int priority = 1; priority <= items.Count; priority++)
                {
                    OverlayItemForm f = items[items.Count - priority];
                    string displayText = DisplayName(f);
                    if (f.GroupId > 0)
                    {
                        string gname; if (!groupNames.TryGetValue(f.GroupId, out gname) || string.IsNullOrWhiteSpace(gname)) gname = "그룹 " + f.GroupId.ToString();
                        bool collapsed = collapsedGroups.Contains(f.GroupId);
                        bool first = !shownGroups.Contains(f.GroupId);
                        if (first) shownGroups.Add(f.GroupId);
                        if (collapsed)
                        {
                            if (!first) continue;
                            displayText = "▶  " + gname + "   (" + GetGroupMembers(f).Count.ToString() + "개)";
                        }
                        else if (first) displayText = "▼  " + gname + "   ·   " + displayText;
                        else displayText = "      └  " + displayText;
                    }
                    int hierarchyDepth = GetHierarchyDepth(f);
                    if (hierarchyDepth > 0) displayText = new string(' ', Math.Min(6, hierarchyDepth) * 3) + "↳ " + displayText;
                    ListViewItem row = new ListViewItem(displayText);
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
            SaveConfigWithoutUiRefresh();
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
            OverlayItemForm clickedOverlay = hit.Item.Tag as OverlayItemForm;
            if (e.Button == MouseButtons.Right) overlayListContextTarget = clickedOverlay;
            if (e.Button == MouseButtons.Left && clickedOverlay != null) MarkQuickHideTarget(clickedOverlay);
            if (e.Button == MouseButtons.Left && hit.SubItem != null && hit.Item.SubItems.IndexOf(hit.SubItem) == 1)
            {
                OverlayItemForm f = hit.Item.Tag as OverlayItemForm;
                if (f == null) return;
                CaptureUndo("오버레이 표시 변경");
                f.SetOverlayVisible(!f.IsOverlayVisible);
                SaveConfig();
            }
        }

        private void CopyOriginalImageToClipboard(OverlayItemForm source)
        {
            if (source == null || source.IsDisposed || source.Type != ItemType.Image)
            {
                SetStatus("Ctrl+C는 이미지 오버레이의 원본 이미지 복사에 사용합니다.");
                return;
            }

            string path = source.Data ?? "";
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                SetStatus("복사할 원본 이미지 파일을 찾지 못했습니다.");
                return;
            }

            Exception lastError = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                Bitmap clipboardBitmap = null;
                try
                {
                    using (Image loaded = Image.FromFile(path))
                        clipboardBitmap = new Bitmap(loaded);

                    DataObject data = new DataObject();
                    // Bitmap works for drawing/chat applications; FileDrop works for Explorer and
                    // applications that expect a real source file. CatLayer Ctrl+V supports both.
                    data.SetData(DataFormats.Bitmap, true, clipboardBitmap);
                    data.SetData(DataFormats.FileDrop, true, new string[] { path });

                    Clipboard.SetDataObject(data, true, 5, 80);

                    IDataObject verify = Clipboard.GetDataObject();
                    bool hasBitmap = verify != null && verify.GetDataPresent(DataFormats.Bitmap);
                    bool hasFileDrop = verify != null && verify.GetDataPresent(DataFormats.FileDrop);
                    string formats = verify == null ? "null" : string.Join(",", verify.GetFormats());

                    DetailedLog.Write("CLIPBOARD",
                        "copy verify attempt=" + attempt.ToString() +
                        " id=" + DetailedLog.ShortId(source.ItemId) +
                        " bitmap=" + hasBitmap.ToString() +
                        " fileDrop=" + hasFileDrop.ToString() +
                        " formats=" + formats +
                        " path=" + Path.GetFileName(path));

                    if (!hasBitmap && !hasFileDrop)
                        throw new ExternalException("클립보드 재검증에서 이미지/파일 형식을 찾지 못했습니다.");

                    SetStatus("원본 이미지 복사 완료  |  이미지 + 파일 형식으로 클립보드 저장");
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    DetailedLog.Write("CLIPBOARD",
                        "copy attempt=" + attempt.ToString() + " failed " +
                        ex.GetType().Name + ": " + ex.Message);
                    if (attempt < 3) Thread.Sleep(70);
                }
                finally
                {
                    if (clipboardBitmap != null) clipboardBitmap.Dispose();
                }
            }

            SetStatus("이미지 복사 실패: " + (lastError == null ? "알 수 없는 오류" : lastError.Message));
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
                CreateItem(source.Type, source.Data, source.DurationSeconds, bounds, source.OpacityPercent, source.TimerKind, source.AlarmPath, source.Locked, source.PreserveAspect, source.ScaleMode, source.IsOverlayVisible, DisplayName(source) + " 복사", source.RotationDegrees, source.FlipHorizontal, source.FlipVertical, newGroupId, source.CropLeft, source.CropTop, source.CropRight, source.CropBottom, source.WebZoomPercent, source.WebCustomCss, source.RotationBaseWidth, source.RotationBaseHeight, source.AlwaysOnTop);
                copies.Add(items[items.Count - 1]);
            }
            SaveConfigWithoutUiRefresh();
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
            NormalizeHierarchy();
            ApplyZOrder();
            SaveConfig();
            SetStatus(selected.Count == 1 ? "오버레이 삭제 완료  |  Ctrl+Z로 복구 가능" : selected.Count.ToString() + "개 오버레이 삭제 완료  |  Ctrl+Z로 복구 가능");
            ShowBeginnerToast(selected.Count == 1 ? "오버레이를 삭제했습니다." : selected.Count.ToString() + "개 오버레이를 삭제했습니다.", "되돌리기", delegate { UndoLastAction(); });
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

        public void AutoArrangeSelectedOverlays()
        {
            List<OverlayItemForm> selected = GetMovableSelectedOverlays(2);
            if (selected == null) return;
            selected.Sort(delegate(OverlayItemForm a, OverlayItemForm b)
            {
                int cmp = a.Bounds.Top.CompareTo(b.Bounds.Top);
                return cmp != 0 ? cmp : a.Bounds.Left.CompareTo(b.Bounds.Left);
            });

            int startX = int.MaxValue, startY = int.MaxValue;
            foreach (OverlayItemForm f in selected) { startX = Math.Min(startX, f.Bounds.Left); startY = Math.Min(startY, f.Bounds.Top); }
            int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(selected.Count)));
            int rows = (selected.Count + columns - 1) / columns;
            int[] columnWidths = new int[columns];
            int[] rowHeights = new int[rows];
            for (int i = 0; i < selected.Count; i++)
            {
                int col = i % columns, row = i / columns;
                columnWidths[col] = Math.Max(columnWidths[col], selected[i].Bounds.Width);
                rowHeights[row] = Math.Max(rowHeights[row], selected[i].Bounds.Height);
            }
            int[] columnX = new int[columns];
            int[] rowY = new int[rows];
            const int gap = 12;
            columnX[0] = startX;
            for (int c = 1; c < columns; c++) columnX[c] = columnX[c - 1] + columnWidths[c - 1] + gap;
            rowY[0] = startY;
            for (int r = 1; r < rows; r++) rowY[r] = rowY[r - 1] + rowHeights[r - 1] + gap;

            CaptureUndo("오버레이 자동 정렬");

            Dictionary<OverlayItemForm, Rectangle> requested = new Dictionary<OverlayItemForm, Rectangle>();
            for (int i = 0; i < selected.Count; i++)
            {
                Rectangle b = selected[i].Bounds;
                b.X = columnX[i % columns];
                b.Y = rowY[i / columns];
                requested[selected[i]] = b;
            }
            ApplyPositionLayoutWithHierarchy(requested);
            SaveConfigWithoutUiRefresh();
            RefreshMainUi();
            RefreshPropertyEditor();
            RefreshOverlaySelectionVisuals();
            SetStatus(selected.Count.ToString() + "개 오버레이 자동 정렬 완료  |  Ctrl+Z로 복구 가능");
        }

        private OverlayItemForm FindItemById(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId)) return null;
            foreach (OverlayItemForm f in items)
                if (f != null && !f.IsDisposed && string.Equals(f.ItemId, itemId, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        private bool WouldCreateHierarchyCycle(OverlayItemForm child, OverlayItemForm proposedParent)
        {
            if (child == null || proposedParent == null) return false;
            OverlayItemForm cursor = proposedParent;
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (cursor != null)
            {
                if (object.ReferenceEquals(cursor, child) || string.Equals(cursor.ItemId, child.ItemId, StringComparison.OrdinalIgnoreCase)) return true;
                if (!visited.Add(cursor.ItemId ?? "")) break;
                cursor = FindItemById(cursor.ParentItemId);
            }
            return false;
        }

        private int GetHierarchyDepth(OverlayItemForm item)
        {
            int depth = 0;
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            OverlayItemForm cursor = item;
            while (cursor != null && !string.IsNullOrWhiteSpace(cursor.ParentItemId) && depth < 32)
            {
                if (!visited.Add(cursor.ItemId ?? "")) break;
                cursor = FindItemById(cursor.ParentItemId);
                if (cursor == null) break;
                depth++;
            }
            return depth;
        }

        public List<OverlayItemForm> GetHierarchyDescendants(OverlayItemForm parent)
        {
            List<OverlayItemForm> result = new List<OverlayItemForm>();
            if (parent == null) return result;
            Queue<string> pending = new Queue<string>();
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Enqueue(parent.ItemId ?? "");
            visited.Add(parent.ItemId ?? "");
            while (pending.Count > 0)
            {
                string parentId = pending.Dequeue();
                foreach (OverlayItemForm f in items)
                {
                    if (f == null || f.IsDisposed || string.IsNullOrWhiteSpace(f.ParentItemId)) continue;
                    if (!string.Equals(f.ParentItemId, parentId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!visited.Add(f.ItemId ?? "")) continue;
                    result.Add(f);
                    pending.Enqueue(f.ItemId ?? "");
                }
            }
            return result;
        }

        public List<OverlayItemForm> GetMoveLinkedMembers(OverlayItemForm source)
        {
            List<OverlayItemForm> result = new List<OverlayItemForm>();
            if (source == null) return result;
            HashSet<OverlayItemForm> set = new HashSet<OverlayItemForm>();
            List<OverlayItemForm> roots = GetGroupMembers(source);
            foreach (OverlayItemForm root in roots)
            {
                if (root != null && set.Add(root)) result.Add(root);
                foreach (OverlayItemForm child in GetHierarchyDescendants(root)) if (set.Add(child)) result.Add(child);
            }
            if (result.Count == 0) result.Add(source);
            return result;
        }

        private List<OverlayItemForm> GetUniqueMoveLinkedMembers(IEnumerable<OverlayItemForm> roots)
        {
            List<OverlayItemForm> result = new List<OverlayItemForm>();
            HashSet<OverlayItemForm> seen = new HashSet<OverlayItemForm>();
            if (roots == null) return result;
            foreach (OverlayItemForm root in roots)
            {
                if (root == null || root.IsDisposed) continue;
                foreach (OverlayItemForm member in GetMoveLinkedMembers(root))
                    if (member != null && !member.IsDisposed && seen.Add(member)) result.Add(member);
            }
            return result;
        }

        private void DetachProgramMagnetsForLinked(OverlayItemForm source)
        {
            if (source == null) return;
            foreach (OverlayItemForm member in GetMoveLinkedMembers(source))
                if (member != null) windowAttachments.Remove(member);
        }

        private void MoveLinkedOverlaysByDelta(OverlayItemForm source, int dx, int dy, bool detachProgramMagnets)
        {
            if (source == null || (dx == 0 && dy == 0)) return;
            List<OverlayItemForm> linked = GetMoveLinkedMembers(source);
            DetailedLog.Write("HIER_MOVE",
                "source=" + DiagnosticItemLabel(source) +
                " delta=" + dx.ToString() + "," + dy.ToString() +
                " linked=" + linked.Count.ToString() +
                " detachMagnets=" + detachProgramMagnets.ToString());
            if (detachProgramMagnets)
                foreach (OverlayItemForm member in linked) if (member != null) windowAttachments.Remove(member);
            foreach (OverlayItemForm member in linked)
            {
                if (member == null || member.IsDisposed || member.Locked) continue;
                Rectangle before = member.Bounds;
                Rectangle b = before;
                b.Offset(dx, dy);
                Rectangle after = NormalizeBounds(b);
                DetailedLog.Write("HIER_MOVE",
                    "  id=" + DetailedLog.ShortId(member.ItemId) +
                    " " + DetailedLog.Rect(before) + " -> " + DetailedLog.Rect(after));
                member.Bounds = after;
            }
        }

        // Layout commands explicitly position selected items. Unselected hierarchy descendants
        // follow their nearest selected ancestor, while explicitly selected children keep the
        // exact layout position requested for them. This prevents double movement.
        private void ApplyPositionLayoutWithHierarchy(Dictionary<OverlayItemForm, Rectangle> requested)
        {
            if (requested == null || requested.Count == 0) return;
            DetailedLog.Write("LAYOUT", "ApplyPositionLayoutWithHierarchy requested=" + requested.Count.ToString());
            Dictionary<OverlayItemForm, Rectangle> final = new Dictionary<OverlayItemForm, Rectangle>();
            foreach (KeyValuePair<OverlayItemForm, Rectangle> pair in requested)
                if (pair.Key != null && !pair.Key.IsDisposed) final[pair.Key] = NormalizeBounds(pair.Value);

            List<OverlayItemForm> roots = new List<OverlayItemForm>(requested.Keys);
            roots.Sort(delegate(OverlayItemForm a, OverlayItemForm b) { return GetHierarchyDepth(b).CompareTo(GetHierarchyDepth(a)); });
            HashSet<OverlayItemForm> explicitItems = new HashSet<OverlayItemForm>(requested.Keys);
            foreach (OverlayItemForm root in roots)
            {
                if (root == null || root.IsDisposed) continue;
                Rectangle target;
                if (!requested.TryGetValue(root, out target)) continue;
                int dx = target.Left - root.Left, dy = target.Top - root.Top;
                if (dx == 0 && dy == 0) continue;
                foreach (OverlayItemForm member in GetMoveLinkedMembers(root))
                {
                    if (member == null || member.IsDisposed || member.Locked || explicitItems.Contains(member) || final.ContainsKey(member)) continue;
                    Rectangle b = member.Bounds;
                    b.Offset(dx, dy);
                    final[member] = NormalizeBounds(b);
                }
            }

            foreach (OverlayItemForm member in final.Keys) windowAttachments.Remove(member);
            foreach (KeyValuePair<OverlayItemForm, Rectangle> pair in final)
            {
                if (pair.Key == null || pair.Key.IsDisposed || pair.Key.Locked) continue;
                Rectangle before = pair.Key.Bounds;
                DetailedLog.Write("LAYOUT",
                    "  id=" + DetailedLog.ShortId(pair.Key.ItemId) +
                    " " + DetailedLog.Rect(before) + " -> " + DetailedLog.Rect(pair.Value));
                pair.Key.Bounds = pair.Value;
            }
        }

        private bool IsMoveLinkedSnapTarget(OverlayItemForm source, OverlayItemForm candidate)
        {
            if (source == null || candidate == null) return false;
            if (object.ReferenceEquals(source, candidate)) return true;
            if (source.GroupId > 0 && candidate.GroupId == source.GroupId) return true;
            OverlayItemForm cursor = candidate;
            int guard = 0;
            while (cursor != null && !string.IsNullOrWhiteSpace(cursor.ParentItemId) && guard++ < 64)
            {
                cursor = FindItemById(cursor.ParentItemId);
                if (cursor == null) break;
                if (object.ReferenceEquals(cursor, source)) return true;
                if (source.GroupId > 0 && cursor.GroupId == source.GroupId) return true;
            }
            return false;
        }

        private void NormalizeHierarchy()
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (OverlayItemForm f in items)
            {
                string id = (f.ItemId ?? "").Trim();
                if (id.Length == 0 || ids.Contains(id)) f.SetHierarchyIdentity(Guid.NewGuid().ToString("N"), f.ParentItemId);
                ids.Add(f.ItemId);
            }
            foreach (OverlayItemForm f in items)
            {
                OverlayItemForm parent = FindItemById(f.ParentItemId);
                if (parent == null || object.ReferenceEquals(parent, f) || WouldCreateHierarchyCycle(f, parent)) f.SetParentItemId("", false);
            }
        }

        public bool CanSetHierarchyParentFromSelection(OverlayItemForm parent)
        {
            if (parent == null || parent.IsDisposed) return false;
            List<OverlayItemForm> selected = SelectedOverlays;
            if (selected.Count < 2 || !selected.Contains(parent)) return false;
            foreach (OverlayItemForm f in selected)
                if (f != null && !f.IsDisposed && !object.ReferenceEquals(f, parent)) return true;
            return false;
        }

        public string GetHierarchyParentMenuText(OverlayItemForm parent)
        {
            if (parent != null && !parent.IsDisposed)
            {
                List<OverlayItemForm> selected = SelectedOverlays;
                if (selected.Count >= 2 && selected.Contains(parent))
                    return "이 항목을 부모로 지정  (자식 " + (selected.Count - 1).ToString() + "개)";
            }
            return "선택 항목의 부모로 지정  (2개 이상 선택)";
        }

        public void SetHierarchyParentFromSelection(OverlayItemForm parent)
        {
            List<OverlayItemForm> selected = SelectedOverlays;
            if (parent == null || parent.IsDisposed || !selected.Contains(parent))
            {
                SetStatus("부모로 만들 항목까지 Shift+클릭으로 같이 선택한 뒤 그 항목을 우클릭하세요.");
                return;
            }
            if (selected.Count < 2)
            {
                SetStatus("부모와 자식으로 묶을 오버레이를 Shift+클릭으로 2개 이상 선택하세요.");
                return;
            }

            List<OverlayItemForm> children = new List<OverlayItemForm>();
            foreach (OverlayItemForm f in selected)
            {
                if (f == null || f.IsDisposed || object.ReferenceEquals(f, parent)) continue;
                children.Add(f);
            }
            if (children.Count == 0) return;

            // Validate the whole operation first. Do not create a half-updated hierarchy when
            // one selected item would make parent -> child -> ... -> parent circular.
            foreach (OverlayItemForm child in children)
            {
                if (WouldCreateHierarchyCycle(child, parent))
                {
                    SetStatus("부모/자식 순환 관계가 생기는 선택입니다. Hierarchy는 변경하지 않았습니다.");
                    return;
                }
            }

            int changed = 0;
            foreach (OverlayItemForm child in children)
                if (!string.Equals(child.ParentItemId, parent.ItemId, StringComparison.OrdinalIgnoreCase)) changed++;
            if (changed == 0)
            {
                SetStatus("이미 '" + DisplayName(parent) + "' 항목의 자식으로 지정되어 있습니다.");
                return;
            }

            CaptureUndo("Hierarchy 부모 지정");
            foreach (OverlayItemForm child in children) child.SetParentItemId(parent.ItemId, false);
            NormalizeHierarchy();
            SaveConfigWithoutUiRefresh();
            RefreshMainUi();
            RefreshOverlaySelectionVisuals();
            SetStatus("'" + DisplayName(parent) + "'을(를) 부모로 지정했습니다.  |  자식 " + children.Count.ToString() + "개");
        }

        // Compatibility wrapper for any older call path: the argument is now interpreted as the
        // parent that was right-clicked, not as a child that opens a second selection dialog.
        public void SetHierarchyParentInteractive(OverlayItemForm parent)
        {
            SetHierarchyParentFromSelection(parent);
        }

        public void ClearHierarchyParent(OverlayItemForm target)
        {
            List<OverlayItemForm> targets = SelectedOverlays;
            if (target != null && !targets.Contains(target)) { targets.Clear(); targets.Add(target); }
            int changed = 0;
            foreach (OverlayItemForm f in targets) if (f != null && !string.IsNullOrWhiteSpace(f.ParentItemId)) changed++;
            if (changed == 0) { SetStatus("선택한 오버레이에 부모가 없습니다."); return; }
            CaptureUndo("Hierarchy 부모 해제");
            foreach (OverlayItemForm f in targets) if (f != null) f.SetParentItemId("", false);
            SaveConfigWithoutUiRefresh(); RefreshMainUi();
            SetStatus(changed.ToString() + "개 오버레이의 부모 관계를 해제했습니다.");
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
            Dictionary<OverlayItemForm, Rectangle> requested = new Dictionary<OverlayItemForm, Rectangle>();
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
                requested[f] = b;
            }
            ApplyPositionLayoutWithHierarchy(requested);
            SaveConfigWithoutUiRefresh();
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
            Dictionary<OverlayItemForm, Rectangle> requested = new Dictionary<OverlayItemForm, Rectangle>();
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
                    requested[f] = b;
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
                    requested[f] = b;
                    cursor += b.Height + gap;
                }
            }
            ApplyPositionLayoutWithHierarchy(requested);
            SaveConfigWithoutUiRefresh();
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
            int moveDx = next.Left - target.Left, moveDy = next.Top - target.Top;
            MoveLinkedOverlaysByDelta(target, moveDx, moveDy, true);
            SaveConfigWithoutUiRefresh();
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
            int displayWidth = Math.Max(100, (int)Math.Round(src.Width * scale));
            int displayHeight = Math.Max(60, (int)Math.Round(src.Height * scale));
            CaptureUndo(scale == 1.0 ? "원본 크기 복원" : "이미지 크기 비율 복원");
            target.SetImageDisplaySize(displayWidth, displayHeight);
            SaveConfigWithoutUiRefresh();
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
                CreateItem(ItemType.Image, managed, 0, target.Bounds, target.OpacityPercent, target.TimerKind, target.AlarmPath, target.Locked, target.PreserveAspect, target.ScaleMode, target.IsOverlayVisible, newName, target.RotationDegrees, target.FlipHorizontal, target.FlipVertical, target.GroupId, target.CropLeft, target.CropTop, target.CropRight, target.CropBottom, 100, "", target.RotationBaseWidth, target.RotationBaseHeight, target.AlwaysOnTop);
                OverlayItemForm created = items[items.Count - 1];
                items.RemoveAt(items.Count - 1);
                items.Insert(oldIndex, created);
                items.Remove(target);
                try { target.Dispose(); } catch { }
                TryDeleteUnusedManagedAsset(oldData);
                ApplyZOrder();
                SaveConfigWithoutUiRefresh();
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
            List<OverlayItemForm> moving = GetUniqueMoveLinkedMembers(targets);
            bool anyMovable = false;
            foreach (OverlayItemForm f in moving) if (f != null && !f.IsDisposed && !f.Locked) { anyMovable = true; break; }
            if (!anyMovable) return;
            CaptureUndo("세부 위치 이동");
            foreach (OverlayItemForm f in moving)
            {
                if (f == null || f.IsDisposed || f.Locked) continue;
                windowAttachments.Remove(f);
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
            groupNames[groupId] = "그룹 " + groupId.ToString();
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
            foreach (int gid in groups) { groupNames.Remove(gid); collapsedGroups.Remove(gid); }
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
            if (vk <= 0 || (int)(keyData & Keys.KeyCode) != vk) return false;
            return HotkeyModifiersFromKeyData(keyData) == mods;
        }

        private HotkeyBinding GetSecondaryHotkey(string action)
        {
            HotkeyBinding binding;
            if (string.IsNullOrWhiteSpace(action) || !secondaryHotkeys.TryGetValue(action, out binding) || binding == null) return null;
            return binding;
        }

        private void SetSecondaryHotkey(string action, int mods, int vk)
        {
            if (string.IsNullOrWhiteSpace(action) || !IsKnownHotkeyAction(action)) return;
            if (vk <= 0) { secondaryHotkeys.Remove(action); return; }
            secondaryHotkeys[action] = new HotkeyBinding(mods, vk);
        }

        private void ApplyRecommendedQuickHotkeyDefaults()
        {
            hotkeyEditMods = Native.MOD_ALT; hotkeyEditVk = (int)Keys.Q;
            hotkeyHideMods = Native.MOD_ALT; hotkeyHideVk = (int)Keys.W;
            hotkeyQuickShowMods = Native.MOD_ALT | Native.MOD_SHIFT; hotkeyQuickShowVk = (int)Keys.W;
            hotkeyCaptureMods = Native.MOD_ALT; hotkeyCaptureVk = (int)Keys.E;

            SetSecondaryHotkey("EDIT", 0, Native.VK_F8);
            SetSecondaryHotkey("HIDE", 0, Native.VK_F9);
            SetSecondaryHotkey("QUICK_SHOW", Native.MOD_SHIFT, Native.VK_F9);
            SetSecondaryHotkey("CAPTURE", 0, Native.VK_F7);
        }

        private void MigrateLegacyQuickHotkeyDefaults()
        {
            // Migrate only untouched shipped defaults; custom bindings are left alone.
            if (hotkeyEditMods == 0 && hotkeyEditVk == Native.VK_F8 && GetSecondaryHotkey("EDIT") == null)
            {
                hotkeyEditMods = Native.MOD_ALT; hotkeyEditVk = (int)Keys.Q;
                SetSecondaryHotkey("EDIT", 0, Native.VK_F8);
            }
            if (hotkeyHideMods == 0 && hotkeyHideVk == Native.VK_F9 && GetSecondaryHotkey("HIDE") == null)
            {
                hotkeyHideMods = Native.MOD_ALT; hotkeyHideVk = (int)Keys.W;
                SetSecondaryHotkey("HIDE", 0, Native.VK_F9);
            }
            if (hotkeyQuickShowMods == Native.MOD_SHIFT && hotkeyQuickShowVk == Native.VK_F9 && GetSecondaryHotkey("QUICK_SHOW") == null)
            {
                hotkeyQuickShowMods = Native.MOD_ALT | Native.MOD_SHIFT; hotkeyQuickShowVk = (int)Keys.W;
                SetSecondaryHotkey("QUICK_SHOW", Native.MOD_SHIFT, Native.VK_F9);
            }
            if (hotkeyCaptureMods == (Native.MOD_CONTROL | Native.MOD_SHIFT) && hotkeyCaptureVk == (int)Keys.X && GetSecondaryHotkey("CAPTURE") == null)
            {
                hotkeyCaptureMods = Native.MOD_ALT; hotkeyCaptureVk = (int)Keys.E;
                SetSecondaryHotkey("CAPTURE", 0, Native.VK_F7);
            }
        }

        private void RepairBetaQuickHotkeyDrift()
        {
            // v1.2.1 BETA8:
            // Some beta installs could persist the primary modifier together with the
            // secondary F-key, e.g. Alt+F8, instead of the intended pair Alt+Q + F8.
            // Repair ONLY these exact beta-shaped combinations so arbitrary user
            // custom hotkeys are never reset.

            HotkeyBinding second;

            // EDIT: intended Alt+Q + F8
            second = GetSecondaryHotkey("EDIT");
            if (hotkeyEditVk == Native.VK_F8 && hotkeyEditMods == Native.MOD_ALT)
            {
                hotkeyEditMods = Native.MOD_ALT;
                hotkeyEditVk = (int)Keys.Q;
                SetSecondaryHotkey("EDIT", 0, Native.VK_F8);
            }
            else if (hotkeyEditVk == (int)Keys.Q && hotkeyEditMods == Native.MOD_ALT &&
                     second != null && second.Vk == Native.VK_F8 && second.Mods == Native.MOD_ALT)
            {
                SetSecondaryHotkey("EDIT", 0, Native.VK_F8);
            }
            else if (hotkeyEditVk == Native.VK_F8 && hotkeyEditMods == 0 &&
                     second != null && second.Vk == (int)Keys.Q && second.Mods == Native.MOD_ALT)
            {
                hotkeyEditMods = Native.MOD_ALT;
                hotkeyEditVk = (int)Keys.Q;
                SetSecondaryHotkey("EDIT", 0, Native.VK_F8);
            }

            // HIDE: intended Alt+W + F9
            second = GetSecondaryHotkey("HIDE");
            if (hotkeyHideVk == Native.VK_F9 && hotkeyHideMods == Native.MOD_ALT)
            {
                hotkeyHideMods = Native.MOD_ALT;
                hotkeyHideVk = (int)Keys.W;
                SetSecondaryHotkey("HIDE", 0, Native.VK_F9);
            }
            else if (hotkeyHideVk == (int)Keys.W && hotkeyHideMods == Native.MOD_ALT &&
                     second != null && second.Vk == Native.VK_F9 && second.Mods == Native.MOD_ALT)
            {
                SetSecondaryHotkey("HIDE", 0, Native.VK_F9);
            }
            else if (hotkeyHideVk == Native.VK_F9 && hotkeyHideMods == 0 &&
                     second != null && second.Vk == (int)Keys.W && second.Mods == Native.MOD_ALT)
            {
                hotkeyHideMods = Native.MOD_ALT;
                hotkeyHideVk = (int)Keys.W;
                SetSecondaryHotkey("HIDE", 0, Native.VK_F9);
            }

            // QUICK_SHOW: intended Alt+Shift+W + Shift+F9
            second = GetSecondaryHotkey("QUICK_SHOW");
            if (hotkeyQuickShowVk == Native.VK_F9 &&
                hotkeyQuickShowMods == (Native.MOD_ALT | Native.MOD_SHIFT))
            {
                hotkeyQuickShowMods = Native.MOD_ALT | Native.MOD_SHIFT;
                hotkeyQuickShowVk = (int)Keys.W;
                SetSecondaryHotkey("QUICK_SHOW", Native.MOD_SHIFT, Native.VK_F9);
            }
            else if (hotkeyQuickShowVk == (int)Keys.W &&
                     hotkeyQuickShowMods == (Native.MOD_ALT | Native.MOD_SHIFT) &&
                     second != null && second.Vk == Native.VK_F9 &&
                     second.Mods == (Native.MOD_ALT | Native.MOD_SHIFT))
            {
                SetSecondaryHotkey("QUICK_SHOW", Native.MOD_SHIFT, Native.VK_F9);
            }
            else if (hotkeyQuickShowVk == Native.VK_F9 &&
                     hotkeyQuickShowMods == Native.MOD_SHIFT &&
                     second != null && second.Vk == (int)Keys.W &&
                     second.Mods == (Native.MOD_ALT | Native.MOD_SHIFT))
            {
                hotkeyQuickShowMods = Native.MOD_ALT | Native.MOD_SHIFT;
                hotkeyQuickShowVk = (int)Keys.W;
                SetSecondaryHotkey("QUICK_SHOW", Native.MOD_SHIFT, Native.VK_F9);
            }

            // CAPTURE: intended Alt+E + F7
            second = GetSecondaryHotkey("CAPTURE");
            if (hotkeyCaptureVk == Native.VK_F7 && hotkeyCaptureMods == Native.MOD_ALT)
            {
                hotkeyCaptureMods = Native.MOD_ALT;
                hotkeyCaptureVk = (int)Keys.E;
                SetSecondaryHotkey("CAPTURE", 0, Native.VK_F7);
            }
            else if (hotkeyCaptureVk == (int)Keys.E && hotkeyCaptureMods == Native.MOD_ALT &&
                     second != null && second.Vk == Native.VK_F7 && second.Mods == Native.MOD_ALT)
            {
                SetSecondaryHotkey("CAPTURE", 0, Native.VK_F7);
            }
            else if (hotkeyCaptureVk == Native.VK_F7 && hotkeyCaptureMods == 0 &&
                     second != null && second.Vk == (int)Keys.E && second.Mods == Native.MOD_ALT)
            {
                hotkeyCaptureMods = Native.MOD_ALT;
                hotkeyCaptureVk = (int)Keys.E;
                SetSecondaryHotkey("CAPTURE", 0, Native.VK_F7);
            }
        }

        private static bool IsKnownHotkeyAction(string action)
        {
            if (string.IsNullOrWhiteSpace(action)) return false;
            foreach (string key in CoreHotkeyActionKeys) if (string.Equals(key, action, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IsGlobalHotkeyAction(string action)
        {
            if (string.IsNullOrWhiteSpace(action)) return false;
            foreach (string key in GlobalHotkeyActionKeys) if (string.Equals(key, action, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private bool MatchesActionHotkey(Keys keyData, string action, int primaryMods, int primaryVk)
        {
            if (MatchesEditHotkey(keyData, primaryMods, primaryVk)) return true;
            HotkeyBinding second = GetSecondaryHotkey(action);
            return second != null && MatchesEditHotkey(keyData, second.Mods, second.Vk);
        }

        private bool IsCoreHotkeyConflict(int mods, int vk)
        {
            if (vk <= 0) return false;
            int[] primaryMods = new int[] {
                hotkeyEditMods, hotkeyHideMods, hotkeyQuickShowMods, hotkeyAllHideMods, hotkeyDetailMods, hotkeyCaptureMods,
                hotkeyRemoteMods, hotkeyWebReloadMods, hotkeyPresetLoadMods, hotkeyGroupLoadMods,
                hotkeyGroupMods, hotkeyUngroupMods, hotkeyRotateMinus1Mods, hotkeyRotatePlus1Mods, hotkeyRotateMinus10Mods, hotkeyRotatePlus10Mods,
                hotkeyFlipHorizontalMods, hotkeyFlipVerticalMods, hotkeyResetRotationMods, hotkeyResetTransformMods
            };
            int[] primaryVks = new int[] {
                hotkeyEditVk, hotkeyHideVk, hotkeyQuickShowVk, hotkeyAllHideVk, hotkeyDetailVk, hotkeyCaptureVk,
                hotkeyRemoteVk, hotkeyWebReloadVk, hotkeyPresetLoadVk, hotkeyGroupLoadVk,
                hotkeyGroupVk, hotkeyUngroupVk, hotkeyRotateMinus1Vk, hotkeyRotatePlus1Vk, hotkeyRotateMinus10Vk, hotkeyRotatePlus10Vk,
                hotkeyFlipHorizontalVk, hotkeyFlipVerticalVk, hotkeyResetRotationVk, hotkeyResetTransformVk
            };
            for (int i = 0; i < primaryVks.Length; i++) if (primaryVks[i] > 0 && primaryMods[i] == mods && primaryVks[i] == vk) return true;
            foreach (HotkeyBinding binding in secondaryHotkeys.Values) if (binding != null && binding.Vk > 0 && binding.Mods == mods && binding.Vk == vk) return true;
            return false;
        }

        private void NormalizeSecondaryHotkeys()
        {
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int[] primaryMods = new int[] {
                hotkeyEditMods, hotkeyHideMods, hotkeyQuickShowMods, hotkeyAllHideMods, hotkeyDetailMods, hotkeyCaptureMods,
                hotkeyRemoteMods, hotkeyWebReloadMods, hotkeyPresetLoadMods, hotkeyGroupLoadMods,
                hotkeyGroupMods, hotkeyUngroupMods, hotkeyRotateMinus1Mods, hotkeyRotatePlus1Mods, hotkeyRotateMinus10Mods, hotkeyRotatePlus10Mods,
                hotkeyFlipHorizontalMods, hotkeyFlipVerticalMods, hotkeyResetRotationMods, hotkeyResetTransformMods
            };
            int[] primaryVks = new int[] {
                hotkeyEditVk, hotkeyHideVk, hotkeyQuickShowVk, hotkeyAllHideVk, hotkeyDetailVk, hotkeyCaptureVk,
                hotkeyRemoteVk, hotkeyWebReloadVk, hotkeyPresetLoadVk, hotkeyGroupLoadVk,
                hotkeyGroupVk, hotkeyUngroupVk, hotkeyRotateMinus1Vk, hotkeyRotatePlus1Vk, hotkeyRotateMinus10Vk, hotkeyRotatePlus10Vk,
                hotkeyFlipHorizontalVk, hotkeyFlipVerticalVk, hotkeyResetRotationVk, hotkeyResetTransformVk
            };
            for (int i = 0; i < primaryVks.Length; i++) if (primaryVks[i] > 0) used.Add(HotkeyText(primaryMods[i], primaryVks[i]));

            foreach (string action in CoreHotkeyActionKeys)
            {
                HotkeyBinding binding = GetSecondaryHotkey(action);
                if (binding == null) continue;
                bool invalid = binding.Vk <= 0 || binding.Vk > 0xFE || IsModifierOnlyKey((Keys)binding.Vk) || IsReservedClipboardHotkey(binding.Mods, binding.Vk);
                if (!invalid && IsGlobalHotkeyAction(action) && !IsSafeGlobalHotkey(binding.Mods, binding.Vk)) invalid = true;
                string name = invalid ? "" : HotkeyText(binding.Mods, binding.Vk);
                if (invalid || !used.Add(name)) secondaryHotkeys.Remove(action);
            }
        }

        public bool HandleDetailShortcut(OverlayItemForm source, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.C))
            {
                OverlayItemForm copySource = source;
                if (copySource == null || copySource.IsDisposed || copySource.Type != ItemType.Image)
                    copySource = SelectedOverlay;

                if (copySource != null && copySource.Type == ItemType.Image)
                    CopyOriginalImageToClipboard(copySource);
                else
                    SetStatus("Ctrl+C는 이미지 오버레이의 원본 이미지만 복사합니다. 오버레이 복제는 우클릭 메뉴를 사용하세요.");
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
            if (MatchesActionHotkey(keyData, "GROUP", hotkeyGroupMods, hotkeyGroupVk))
            {
                GroupOrUngroupSelectedOverlays();
                return true;
            }
            if (MatchesActionHotkey(keyData, "UNGROUP", hotkeyUngroupMods, hotkeyUngroupVk))
            {
                UngroupSelectedOverlays();
                return true;
            }

            if (source != null && SelectedOverlays.Count == 0) SelectOverlayForEditing(source);

            if (MatchesActionHotkey(keyData, "ROT_M1", hotkeyRotateMinus1Mods, hotkeyRotateMinus1Vk)) { RotateSelectionBy(-1); return true; }
            if (MatchesActionHotkey(keyData, "ROT_P1", hotkeyRotatePlus1Mods, hotkeyRotatePlus1Vk)) { RotateSelectionBy(1); return true; }
            if (MatchesActionHotkey(keyData, "ROT_M10", hotkeyRotateMinus10Mods, hotkeyRotateMinus10Vk)) { RotateSelectionBy(-10); return true; }
            if (MatchesActionHotkey(keyData, "ROT_P10", hotkeyRotatePlus10Mods, hotkeyRotatePlus10Vk)) { RotateSelectionBy(10); return true; }
            if (MatchesActionHotkey(keyData, "FLIP_H", hotkeyFlipHorizontalMods, hotkeyFlipHorizontalVk)) { FlipSelection(true); return true; }
            if (MatchesActionHotkey(keyData, "FLIP_V", hotkeyFlipVerticalMods, hotkeyFlipVerticalVk)) { FlipSelection(false); return true; }
            if (MatchesActionHotkey(keyData, "RESET_ROT", hotkeyResetRotationMods, hotkeyResetRotationVk)) { ResetSelectionTransform(false); return true; }
            if (MatchesActionHotkey(keyData, "RESET_ALL", hotkeyResetTransformMods, hotkeyResetTransformVk)) { ResetSelectionTransform(true); return true; }

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

        private void ExecuteSecondaryGlobalHotkey(string action)
        {
            if (string.IsNullOrWhiteSpace(action)) return;
            // Utility hotkeys remain available even while WebView2 owns keyboard focus.
            if (action == "CAPTURE") { AddScreenRegionCapture(); return; }
            if (action == "REMOTE") { ShowRemoteControl(); return; }
            if (action == "WEB_RELOAD") { RemoteReloadSelectedWeb(); return; }
            if (action == "PRESET_LOAD") { LoadPresetInteractive(); return; }
            if (action == "GROUP_LOAD") { LoadGroupInteractive(); return; }
            if (WebControlMode)
            {
                if (action == "HIDE") { QuickHideOverlay(); return; }
                if (action == "QUICK_SHOW") { QuickShowOverlay(); return; }
                if (action == "ALL_HIDE") { ToggleHidden(); return; }
                if (action == "DETAIL") { ToggleWebControlMode(); return; }
                return;
            }
            if (action == "EDIT") { ToggleEdit(); return; }
            if (action == "HIDE") { QuickHideOverlay(); return; }
            if (action == "QUICK_SHOW") { QuickShowOverlay(); return; }
            if (action == "ALL_HIDE") { ToggleHidden(); return; }
            if (action == "DETAIL") { ToggleWebControlMode(); return; }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (WebControlMode) return base.ProcessCmdKey(ref msg, keyData);
            if (HandleUndoShortcut(keyData)) return true;
            if (HandlePasteShortcut(keyData)) return true;
            if (keyData == Keys.Delete && EditMode && SelectedOverlays.Count > 0 && !ShouldPreserveDeleteForInput(msg.HWnd))
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

        private string DiagnosticItemLabel(OverlayItemForm item)
        {
            if (item == null) return "<null>";
            string dataLabel = "";
            ItemType itemType = item.Type;
            try
            {
                if (itemType == ItemType.Image) dataLabel = Path.GetFileName(item.Data ?? "");
                else if (itemType == ItemType.Web)
                {
                    Uri uri;
                    dataLabel = Uri.TryCreate(item.Data ?? "", UriKind.Absolute, out uri) ? uri.Host : (item.Data ?? "");
                }
                else dataLabel = item.Data ?? "";
            }
            catch { dataLabel = ""; }
            if (dataLabel.Length > 60) dataLabel = dataLabel.Substring(0, 60);
            return "#" + (items.IndexOf(item) + 1).ToString() +
                " id=" + DetailedLog.ShortId(item.ItemId) +
                " type=" + itemType.ToString() +
                " data=" + dataLabel;
        }

        private void DiagnosticScene(string category, string reason)
        {
            if (!DetailedLog.Enabled) return;
            try
            {
                DetailedLog.Write(category, "SCENE BEGIN reason=" + (reason ?? "") +
                    " items=" + items.Count.ToString() +
                    " undoStack=" + undoStates.Count.ToString() +
                    " mode=" + CurrentEditorMode.ToString() +
                    " hidden=" + hidden.ToString());
                HashSet<OverlayItemForm> selected = new HashSet<OverlayItemForm>();
                try { foreach (OverlayItemForm s in SelectedOverlays) selected.Add(s); } catch { }

                for (int i = 0; i < items.Count; i++)
                {
                    OverlayItemForm f = items[i];
                    if (f == null || f.IsDisposed)
                    {
                        DetailedLog.Write(category, "  [" + i.ToString() + "] <disposed/null>");
                        continue;
                    }
                    ItemType itemType = f.Type;
                    WindowAttachment a;
                    bool attached = windowAttachments.TryGetValue(f, out a) && a != null;
                    DetailedLog.Write(category,
                        "  [" + i.ToString() + "] id=" + DetailedLog.ShortId(f.ItemId) +
                        " parent=" + DetailedLog.ShortId(f.ParentItemId) +
                        " type=" + itemType.ToString() +
                        " bounds=" + DetailedLog.Rect(f.Bounds) +
                        " rot=" + f.RotationDegrees.ToString() +
                        " base=" + f.RotationBaseWidth.ToString() + "x" + f.RotationBaseHeight.ToString() +
                        " group=" + f.GroupId.ToString() +
                        " visible=" + f.IsOverlayVisible.ToString() +
                        " locked=" + f.Locked.ToString() +
                        " selected=" + selected.Contains(f).ToString() +
                        " attach=" + (attached ? ((a.ProcessName ?? "") + "/side=" + a.Side.ToString() +
                            "/off=" + a.OffsetX.ToString() + "," + a.OffsetY.ToString() +
                            "/hwnd=0x" + a.Hwnd.ToInt64().ToString("X")) : "none"));
                }
                DetailedLog.Write(category, "SCENE END");
            }
            catch (Exception ex)
            {
                DetailedLog.Write(category, "SCENE LOG ERROR " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private UndoItemSnapshot CaptureUndoItemSnapshot(OverlayItemForm item)
        {
            UndoItemSnapshot snap = new UndoItemSnapshot();
            snap.Type = item.Type;
            snap.Data = item.Data ?? "";
            snap.DurationSeconds = item.DurationSeconds;
            snap.Bounds = item.Bounds;
            snap.OpacityPercent = item.OpacityPercent;
            snap.TimerKind = item.TimerKind;
            snap.AlarmPath = item.AlarmPath ?? "";
            snap.CustomName = item.CustomName ?? "";
            snap.Visible = item.IsOverlayVisible;
            snap.Locked = item.Locked;
            snap.PreserveAspect = item.PreserveAspect;
            snap.ScaleMode = item.ScaleMode;
            snap.RotationDegrees = item.RotationDegrees;
            snap.FlipHorizontal = item.FlipHorizontal;
            snap.FlipVertical = item.FlipVertical;
            snap.RotationBaseWidth = item.RotationBaseWidth;
            snap.RotationBaseHeight = item.RotationBaseHeight;
            snap.GroupId = item.GroupId;
            snap.CropLeft = item.CropLeft; snap.CropTop = item.CropTop; snap.CropRight = item.CropRight; snap.CropBottom = item.CropBottom;
            snap.WebZoomPercent = item.WebZoomPercent;
            snap.WebCustomCss = item.WebCustomCss ?? "";
            snap.ItemId = item.ItemId ?? "";
            snap.ParentItemId = item.ParentItemId ?? "";
            snap.AlwaysOnTop = item.AlwaysOnTop;

            WindowAttachment attachment;
            if (windowAttachments.TryGetValue(item, out attachment) && attachment != null)
            {
                snap.HasAttachment = true;
                snap.AttachProcess = attachment.ProcessName ?? "";
                snap.AttachTitle = attachment.WindowTitle ?? "";
                snap.AttachOffsetX = attachment.OffsetX;
                snap.AttachOffsetY = attachment.OffsetY;
                snap.AttachSide = attachment.Side;
            }
            return snap;
        }

        private void CaptureUndoSettings(UndoState state)
        {
            state.EditMode = EditMode;
            state.DetailEditMode = DetailEditMode;
            state.Hidden = hidden;
            state.HotkeyEditVk = hotkeyEditVk;
            state.HotkeyHideVk = hotkeyHideVk;
            state.HotkeyAllHideVk = hotkeyAllHideVk;
            state.HotkeyDetailVk = hotkeyDetailVk;
            state.HotkeyCaptureVk = hotkeyCaptureVk;
            state.HotkeyQuickShowVk = hotkeyQuickShowVk;
            state.HotkeyRemoteVk = hotkeyRemoteVk;
            state.HotkeyWebReloadVk = hotkeyWebReloadVk;
            state.HotkeyPresetLoadVk = hotkeyPresetLoadVk;
            state.HotkeyGroupLoadVk = hotkeyGroupLoadVk;
            state.HotkeyEditMods = hotkeyEditMods;
            state.HotkeyHideMods = hotkeyHideMods;
            state.HotkeyAllHideMods = hotkeyAllHideMods;
            state.HotkeyDetailMods = hotkeyDetailMods;
            state.HotkeyCaptureMods = hotkeyCaptureMods;
            state.HotkeyQuickShowMods = hotkeyQuickShowMods;
            state.HotkeyRemoteMods = hotkeyRemoteMods;
            state.HotkeyWebReloadMods = hotkeyWebReloadMods;
            state.HotkeyPresetLoadMods = hotkeyPresetLoadMods;
            state.HotkeyGroupLoadMods = hotkeyGroupLoadMods;
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
            state.SecondaryHotkeys = new Dictionary<string, HotkeyBinding>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, HotkeyBinding> pair in secondaryHotkeys)
                if (pair.Value != null && pair.Value.Vk > 0) state.SecondaryHotkeys[pair.Key] = new HotkeyBinding(pair.Value.Mods, pair.Value.Vk);
            state.ZoomStepPercent = zoomStepPercent;
            state.RotationSnapDegrees = rotationSnapDegrees;
            state.PlacementSnapPixels = placementSnapPixels;
            state.ProgramMagnetSnapPixels = programMagnetSnapPixels;
            state.ResizeGracePixels = resizeGracePixels;
            state.ResizeGraceMs = resizeGraceMs;
            state.PresetHotkeys = new List<PresetHotkeyBinding>();
            foreach (PresetHotkeyBinding binding in presetHotkeys)
            {
                PresetHotkeyBinding copy = new PresetHotkeyBinding(); copy.FileName = binding.FileName; copy.Mods = binding.Mods; copy.Vk = binding.Vk;
                state.PresetHotkeys.Add(copy);
            }
            state.CurrentPresetName = currentPresetName;
            state.MainClientSize = ClientSize;
            state.CompactMainActive = compactMainActive;
            state.SavedEditorMode = CurrentEditorMode == EditorMode.WebControl ? (modeBeforeWebControl == EditorMode.WebControl ? EditorMode.Normal : modeBeforeWebControl) : CurrentEditorMode;
            state.SavedWebInteractionStyle = webInteractionStyle;
            state.SavedSuppressAutomaticUpdatePrompt = suppressAutomaticUpdatePrompt;
        }

        public void CaptureUndo(string reason)
        {
            DetailedLog.Write("UNDO", "CAPTURE begin reason=" + (reason ?? "") + " stackBefore=" + undoStates.Count.ToString());
            DiagnosticScene("UNDO_CAPTURE", reason);
            try
            {
                UndoState state = new UndoState();
                state.Path = ""; // no disk/preset snapshot in TEST 13
                state.Reason = string.IsNullOrWhiteSpace(reason) ? "최근 작업" : reason;
                state.SettingsOnly = string.Equals(state.Reason, "설정 초기화", StringComparison.Ordinal);
                CaptureUndoSettings(state);

                state.AssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                state.SoundPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (!state.SettingsOnly)
                {
                    state.Items = new List<UndoItemSnapshot>(items.Count);
                    state.SelectedItemIds = new List<string>();
                    state.GroupNames = new Dictionary<int, string>(groupNames);
                    state.CollapsedGroups = new HashSet<int>(collapsedGroups);
                    state.NextGroupId = nextGroupId;
                    HashSet<OverlayItemForm> selected = new HashSet<OverlayItemForm>(SelectedOverlays);

                    foreach (OverlayItemForm item in items)
                    {
                        if (item == null || item.IsDisposed) continue;
                        UndoItemSnapshot snap = CaptureUndoItemSnapshot(item);
                        state.Items.Add(snap);
                        if (selected.Contains(item) && !string.IsNullOrWhiteSpace(snap.ItemId)) state.SelectedItemIds.Add(snap.ItemId);
                        if (item.Type == ItemType.Image && !string.IsNullOrWhiteSpace(item.Data)) state.AssetPaths.Add(item.Data);
                        if (item.Type == ItemType.Timer && !string.IsNullOrWhiteSpace(item.AlarmPath)) state.SoundPaths.Add(item.AlarmPath);
                    }
                }

                undoStates.Add(state);
                DetailedLog.Write("UNDO", "CAPTURE stored reason=" + state.Reason +
                    " settingsOnly=" + state.SettingsOnly.ToString() +
                    " snapshotItems=" + (state.Items == null ? "0" : state.Items.Count.ToString()) +
                    " stackAfter=" + undoStates.Count.ToString());
                while (undoStates.Count > MaxUndoStates)
                {
                    UndoState oldest = undoStates[0];
                    undoStates.RemoveAt(0);
                    ReleaseUndoState(oldest);
                }
            }
            catch (Exception ex) { CrashLog.Write(ex, "CaptureUndo"); }
        }

        private void RestoreSettingsUndo(UndoState state)
        {
            EditMode = state.EditMode;
            DetailEditMode = state.DetailEditMode;
            hidden = state.Hidden;
            hotkeyEditVk = state.HotkeyEditVk;
            hotkeyHideVk = state.HotkeyHideVk;
            hotkeyAllHideVk = state.HotkeyAllHideVk;
            hotkeyDetailVk = state.HotkeyDetailVk;
            hotkeyCaptureVk = state.HotkeyCaptureVk;
            hotkeyQuickShowVk = state.HotkeyQuickShowVk;
            hotkeyRemoteVk = state.HotkeyRemoteVk;
            hotkeyWebReloadVk = state.HotkeyWebReloadVk;
            hotkeyPresetLoadVk = state.HotkeyPresetLoadVk;
            hotkeyGroupLoadVk = state.HotkeyGroupLoadVk;
            hotkeyEditMods = state.HotkeyEditMods;
            hotkeyHideMods = state.HotkeyHideMods;
            hotkeyAllHideMods = state.HotkeyAllHideMods;
            hotkeyDetailMods = state.HotkeyDetailMods;
            hotkeyCaptureMods = state.HotkeyCaptureMods;
            hotkeyQuickShowMods = state.HotkeyQuickShowMods;
            hotkeyRemoteMods = state.HotkeyRemoteMods;
            hotkeyWebReloadMods = state.HotkeyWebReloadMods;
            hotkeyPresetLoadMods = state.HotkeyPresetLoadMods;
            hotkeyGroupLoadMods = state.HotkeyGroupLoadMods;
            hotkeyGroupVk = state.HotkeyGroupVk; hotkeyGroupMods = state.HotkeyGroupMods;
            hotkeyUngroupVk = state.HotkeyUngroupVk; hotkeyUngroupMods = state.HotkeyUngroupMods;
            hotkeyRotateMinus1Vk = state.HotkeyRotateMinus1Vk; hotkeyRotateMinus1Mods = state.HotkeyRotateMinus1Mods;
            hotkeyRotatePlus1Vk = state.HotkeyRotatePlus1Vk; hotkeyRotatePlus1Mods = state.HotkeyRotatePlus1Mods;
            hotkeyRotateMinus10Vk = state.HotkeyRotateMinus10Vk; hotkeyRotateMinus10Mods = state.HotkeyRotateMinus10Mods;
            hotkeyRotatePlus10Vk = state.HotkeyRotatePlus10Vk; hotkeyRotatePlus10Mods = state.HotkeyRotatePlus10Mods;
            hotkeyFlipHorizontalVk = state.HotkeyFlipHorizontalVk; hotkeyFlipHorizontalMods = state.HotkeyFlipHorizontalMods;
            hotkeyFlipVerticalVk = state.HotkeyFlipVerticalVk; hotkeyFlipVerticalMods = state.HotkeyFlipVerticalMods;
            hotkeyResetRotationVk = state.HotkeyResetRotationVk; hotkeyResetRotationMods = state.HotkeyResetRotationMods;
            hotkeyResetTransformVk = state.HotkeyResetTransformVk; hotkeyResetTransformMods = state.HotkeyResetTransformMods;

            secondaryHotkeys.Clear();
            if (state.SecondaryHotkeys != null)
                foreach (KeyValuePair<string, HotkeyBinding> pair in state.SecondaryHotkeys)
                    if (pair.Value != null && pair.Value.Vk > 0) secondaryHotkeys[pair.Key] = new HotkeyBinding(pair.Value.Mods, pair.Value.Vk);
            NormalizeSecondaryHotkeys();

            zoomStepPercent = state.ZoomStepPercent <= 0 ? 10 : Math.Max(1, Math.Min(90, state.ZoomStepPercent));
            rotationSnapDegrees = state.RotationSnapDegrees == 0 ? 0 : Math.Max(0, Math.Min(15, state.RotationSnapDegrees));
            placementSnapPixels = Math.Max(0, Math.Min(30, state.PlacementSnapPixels));
            programMagnetSnapPixels = Math.Max(0, Math.Min(100, state.ProgramMagnetSnapPixels));
            resizeGracePixels = state.ResizeGracePixels <= 0 ? 30 : Math.Max(10, Math.Min(80, state.ResizeGracePixels));
            resizeGraceMs = state.ResizeGraceMs < 0 ? 500 : Math.Max(0, Math.Min(3000, state.ResizeGraceMs));
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
            suppressAutomaticUpdatePrompt = state.SavedSuppressAutomaticUpdatePrompt;
            webInteractionStyle = state.SavedWebInteractionStyle;
            SetEditorMode(state.SavedEditorMode == EditorMode.WebControl ? EditorMode.Normal : state.SavedEditorMode, false);
            EditMode = state.EditMode;
            DetailEditMode = state.DetailEditMode;

            compactMainActive = state.CompactMainActive;
            if (compactMainPanel != null && !compactMainPanel.IsDisposed)
            {
                compactMainPanel.Visible = compactMainActive;
                if (compactMainActive) compactMainPanel.BringToFront();
            }
            MinimumSize = compactMainActive ? new Size(460, 320) : new Size(900, 620);
            Text = compactMainActive ? AppInfo.DisplayName : AppInfo.DisplayName + " - 기존 메인화면";
            if (state.MainClientSize.Width >= 200 && state.MainClientSize.Height >= 300) ClientSize = state.MainClientSize;
            if (!compactMainActive) ScaleMainLayout();

            foreach (OverlayItemForm f in items)
            {
                if (f == null || f.IsDisposed) continue;
                f.SetEditMode(EditMode && !(IntegratedMode && f.Type == ItemType.Web));
                f.RefreshEffectiveVisibility();
            }
            ApplyHotkeys(); UpdateButtons(); SaveConfigWithoutUiRefresh(); RefreshMainUi(); ApplyZOrder();
        }

        private bool UndoNeedsRecreate(OverlayItemForm current, UndoItemSnapshot wanted)
        {
            if (current == null || current.IsDisposed || wanted == null || current.Type != wanted.Type) return true;
            if (wanted.Type == ItemType.Image || wanted.Type == ItemType.Text)
                return !string.Equals(current.Data ?? "", wanted.Data ?? "", StringComparison.Ordinal);
            if (wanted.Type == ItemType.Timer)
                return current.DurationSeconds != wanted.DurationSeconds || current.TimerKind != wanted.TimerKind ||
                    !string.Equals(current.Data ?? "", wanted.Data ?? "", StringComparison.Ordinal);
            return false;
        }

        private bool UndoAttachmentMatches(OverlayItemForm item, UndoItemSnapshot wanted)
        {
            WindowAttachment current;
            bool hasCurrent = windowAttachments.TryGetValue(item, out current) && current != null;
            if (hasCurrent != wanted.HasAttachment) return false;
            if (!hasCurrent) return true;

            return string.Equals(current.ProcessName ?? "", wanted.AttachProcess ?? "", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(current.WindowTitle ?? "", wanted.AttachTitle ?? "", StringComparison.Ordinal) &&
                current.OffsetX == wanted.AttachOffsetX &&
                current.OffsetY == wanted.AttachOffsetY &&
                current.Side == wanted.AttachSide;
        }

        private bool UndoVisualMatches(OverlayItemForm item, UndoItemSnapshot wanted)
        {
            if (item == null || item.IsDisposed || wanted == null) return false;
            if (item.Bounds != wanted.Bounds) return false;
            if (item.RotationDegrees != wanted.RotationDegrees) return false;
            if (item.FlipHorizontal != wanted.FlipHorizontal || item.FlipVertical != wanted.FlipVertical) return false;
            if (item.Type == ItemType.Image &&
                (item.RotationBaseWidth != Math.Max(1, wanted.RotationBaseWidth) ||
                 item.RotationBaseHeight != Math.Max(1, wanted.RotationBaseHeight))) return false;
            return true;
        }

        private bool UndoItemMatches(OverlayItemForm item, UndoItemSnapshot wanted)
        {
            if (item == null || item.IsDisposed || wanted == null || item.Type != wanted.Type) return false;
            if (!string.Equals(item.Data ?? "", wanted.Data ?? "", StringComparison.Ordinal)) return false;
            if (item.DurationSeconds != wanted.DurationSeconds || item.TimerKind != wanted.TimerKind) return false;
            if (!string.Equals(item.AlarmPath ?? "", wanted.AlarmPath ?? "", StringComparison.Ordinal)) return false;
            if (!string.Equals(item.CustomName ?? "", wanted.CustomName ?? "", StringComparison.Ordinal)) return false;
            if (item.IsOverlayVisible != wanted.Visible || item.Locked != wanted.Locked) return false;
            if (item.AlwaysOnTop != wanted.AlwaysOnTop) return false;
            if (item.OpacityPercent != wanted.OpacityPercent || item.GroupId != wanted.GroupId) return false;
            if (!string.Equals(item.ItemId ?? "", wanted.ItemId ?? "", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(item.ParentItemId ?? "", wanted.ParentItemId ?? "", StringComparison.OrdinalIgnoreCase)) return false;
            if (!UndoVisualMatches(item, wanted)) return false;
            // Program-magnet attachment is external runtime state, not part of ordinary Ctrl+Z.
            // Restoring it can immediately overwrite the position that Ctrl+Z just restored.

            if (item.Type == ItemType.Image)
            {
                if (item.PreserveAspect != wanted.PreserveAspect || item.ScaleMode != wanted.ScaleMode) return false;
                if (item.CropLeft != wanted.CropLeft || item.CropTop != wanted.CropTop ||
                    item.CropRight != wanted.CropRight || item.CropBottom != wanted.CropBottom) return false;
            }
            else if (item.Type == ItemType.Web)
            {
                if (item.WebZoomPercent != wanted.WebZoomPercent ||
                    !string.Equals(item.WebCustomCss ?? "", wanted.WebCustomCss ?? "", StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private void RestoreUndoAttachment(OverlayItemForm item, UndoItemSnapshot wanted)
        {
            // TEST 13.3: intentionally no-op.
            // Program magnets follow external HWND state and are unsafe to resurrect from Undo.
        }

        private void ApplyUndoItemSnapshot(OverlayItemForm item, UndoItemSnapshot wanted)
        {
            if (item == null || item.IsDisposed || wanted == null) return;

            // The snapshot contains every overlay. If this overlay did not change, do nothing at all.
            // This prevents unrelated overlays from being normalized/re-rendered or reattached.
            if (UndoItemMatches(item, wanted)) return;

            if (item.Type == ItemType.Web && !string.Equals(item.Data ?? "", wanted.Data ?? "", StringComparison.Ordinal))
                item.SetWebUrl(wanted.Data, false);

            if (!string.Equals(item.CustomName ?? "", wanted.CustomName ?? "", StringComparison.Ordinal))
                item.SetCustomName(wanted.CustomName, false);
            if (item.Locked != wanted.Locked)
                item.SetLocked(wanted.Locked, false);
            if (item.AlwaysOnTop != wanted.AlwaysOnTop)
                item.SetAlwaysOnTop(wanted.AlwaysOnTop, false);
            if (item.OpacityPercent != wanted.OpacityPercent)
                item.SetOpacityPercent(wanted.OpacityPercent, false);
            if (item.GroupId != wanted.GroupId)
                item.SetGroupId(wanted.GroupId, false);
            if (!string.Equals(item.ItemId ?? "", wanted.ItemId ?? "", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(item.ParentItemId ?? "", wanted.ParentItemId ?? "", StringComparison.OrdinalIgnoreCase))
                item.SetHierarchyIdentity(wanted.ItemId, wanted.ParentItemId);

            if (item.Type == ItemType.Image)
            {
                if (item.PreserveAspect != wanted.PreserveAspect)
                    item.SetPreserveAspect(wanted.PreserveAspect, false);
                if (item.ScaleMode != wanted.ScaleMode)
                    item.SetImageScaleMode(wanted.ScaleMode, false);
                if (item.CropLeft != wanted.CropLeft || item.CropTop != wanted.CropTop ||
                    item.CropRight != wanted.CropRight || item.CropBottom != wanted.CropBottom)
                    item.SetCrop(wanted.CropLeft, wanted.CropTop, wanted.CropRight, wanted.CropBottom, false);
            }
            else if (item.Type == ItemType.Web)
            {
                if (item.WebZoomPercent != wanted.WebZoomPercent)
                    item.SetWebZoomPercent(wanted.WebZoomPercent, false);
                if (!string.Equals(item.WebCustomCss ?? "", wanted.WebCustomCss ?? "", StringComparison.Ordinal))
                    item.SetWebCustomCss(wanted.WebCustomCss, false);
            }
            else if (item.Type == ItemType.Timer)
            {
                if (!string.Equals(item.AlarmPath ?? "", wanted.AlarmPath ?? "", StringComparison.Ordinal))
                    item.SetTimerAlarmPath(wanted.AlarmPath, false);
            }

            if (!UndoVisualMatches(item, wanted))
            {
                if (windowAttachments.Remove(item))
                    DetailedLog.Write("MAGNET_SAFETY", "Ctrl+Z detached current magnet before visual restore id=" + DetailedLog.ShortId(item.ItemId));
                item.RestoreUndoVisualState(wanted.Bounds, wanted.RotationBaseWidth, wanted.RotationBaseHeight,
                    wanted.RotationDegrees, wanted.FlipHorizontal, wanted.FlipVertical);
            }

            if (item.IsOverlayVisible != wanted.Visible)
                item.SetOverlayVisible(wanted.Visible);

            // Do not restore old program-magnet metadata here. The previous external window may
            // have moved, closed, or been recreated since the snapshot was captured.
        }

        private OverlayItemForm CreateItemFromUndoSnapshot(UndoItemSnapshot wanted)
        {
            int before = items.Count;
            CreateItem(wanted.Type, wanted.Data, wanted.DurationSeconds, wanted.Bounds, wanted.OpacityPercent,
                wanted.TimerKind, wanted.AlarmPath, wanted.Locked, wanted.PreserveAspect, wanted.ScaleMode,
                wanted.Visible, wanted.CustomName, wanted.RotationDegrees, wanted.FlipHorizontal, wanted.FlipVertical,
                wanted.GroupId, wanted.CropLeft, wanted.CropTop, wanted.CropRight, wanted.CropBottom,
                wanted.WebZoomPercent, wanted.WebCustomCss, wanted.RotationBaseWidth, wanted.RotationBaseHeight, wanted.AlwaysOnTop);
            if (items.Count <= before) return null;
            OverlayItemForm created = items[items.Count - 1];
            created.SetHierarchyIdentity(wanted.ItemId, wanted.ParentItemId);
            created.RestoreUndoVisualState(wanted.Bounds, wanted.RotationBaseWidth, wanted.RotationBaseHeight,
                wanted.RotationDegrees, wanted.FlipHorizontal, wanted.FlipVertical);
            // Restored/deleted overlays come back detached. Reconnecting to an external window is
            // always an explicit user action after Ctrl+Z.
            return created;
        }

        private bool RestoreOverlayUndo(UndoState state)
        {
            if (state == null || state.Items == null)
            {
                DetailedLog.Write("UNDO_APPLY", "RestoreOverlayUndo invalid state");
                return false;
            }

            DetailedLog.Write("UNDO_APPLY", "BEGIN reason=" + (state.Reason ?? "") +
                " currentItems=" + items.Count.ToString() +
                " wantedItems=" + state.Items.Count.ToString());

            Dictionary<string, OverlayItemForm> currentById = new Dictionary<string, OverlayItemForm>(StringComparer.OrdinalIgnoreCase);
            foreach (OverlayItemForm item in items)
            {
                if (item == null || item.IsDisposed || string.IsNullOrWhiteSpace(item.ItemId)) continue;
                if (!currentById.ContainsKey(item.ItemId)) currentById.Add(item.ItemId, item);
            }

            // Validate before any mutation. Missing undo assets should never leave a half-restored scene.
            HashSet<string> wantedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (UndoItemSnapshot wanted in state.Items)
            {
                if (wanted == null || string.IsNullOrWhiteSpace(wanted.ItemId) || !wantedIds.Add(wanted.ItemId)) return false;
                if (wanted.Type == ItemType.Image && !string.IsNullOrWhiteSpace(wanted.Data) && !File.Exists(wanted.Data))
                {
                    OverlayItemForm existing;
                    if (!currentById.TryGetValue(wanted.ItemId, out existing) || existing == null || existing.IsDisposed ||
                        existing.Type != ItemType.Image || !string.Equals(existing.Data ?? "", wanted.Data ?? "", StringComparison.Ordinal))
                    {
                        SetStatus("실행 취소에 필요한 이미지 파일이 없어 현재 상태를 유지했습니다.");
                        return false;
                    }
                }
            }

            List<OverlayItemForm> restored = new List<OverlayItemForm>(state.Items.Count);
            HashSet<OverlayItemForm> kept = new HashSet<OverlayItemForm>();
            List<string> cleanupAssets = new List<string>();
            List<string> cleanupSounds = new List<string>();
            int undoChangedItems = 0;

            foreach (UndoItemSnapshot wanted in state.Items)
            {
                OverlayItemForm current;
                currentById.TryGetValue(wanted.ItemId, out current);
                bool needsRecreate = UndoNeedsRecreate(current, wanted);
                bool alreadyMatches = !needsRecreate && UndoItemMatches(current, wanted);
                DetailedLog.Write("UNDO_APPLY",
                    "item id=" + DetailedLog.ShortId(wanted.ItemId) +
                    " current=" + (current == null ? "<missing>" : DetailedLog.Rect(current.Bounds)) +
                    " wanted=" + DetailedLog.Rect(wanted.Bounds) +
                    " parentNow=" + (current == null ? "" : DetailedLog.ShortId(current.ParentItemId)) +
                    " parentWanted=" + DetailedLog.ShortId(wanted.ParentItemId) +
                    " recreate=" + needsRecreate.ToString() +
                    " matches=" + alreadyMatches.ToString());
                if (needsRecreate)
                {
                    undoChangedItems++;
                    if (current != null && !current.IsDisposed)
                    {
                        if (current.Type == ItemType.Image && !string.IsNullOrWhiteSpace(current.Data)) cleanupAssets.Add(current.Data);
                        if (current.Type == ItemType.Timer && !string.IsNullOrWhiteSpace(current.AlarmPath)) cleanupSounds.Add(current.AlarmPath);
                        windowAttachments.Remove(current); items.Remove(current); current.Dispose();
                    }
                    current = CreateItemFromUndoSnapshot(wanted);
                    if (current == null) return false;
                }
                else
                {
                    if (!UndoItemMatches(current, wanted)) undoChangedItems++;
                    ApplyUndoItemSnapshot(current, wanted);
                }

                restored.Add(current); kept.Add(current);
            }

            // Current overlays not present in the snapshot were added by the operation being undone.
            List<OverlayItemForm> extras = new List<OverlayItemForm>();
            foreach (OverlayItemForm current in items)
                if (current != null && !current.IsDisposed && !kept.Contains(current)) extras.Add(current);
            foreach (OverlayItemForm extra in extras)
            {
                DetailedLog.Write("UNDO_APPLY", "remove extra " + DiagnosticItemLabel(extra) +
                    " bounds=" + DetailedLog.Rect(extra.Bounds));
                undoChangedItems++;
                if (extra.Type == ItemType.Image && !string.IsNullOrWhiteSpace(extra.Data)) cleanupAssets.Add(extra.Data);
                if (extra.Type == ItemType.Timer && !string.IsNullOrWhiteSpace(extra.AlarmPath)) cleanupSounds.Add(extra.AlarmPath);
                windowAttachments.Remove(extra); items.Remove(extra); extra.Dispose();
            }

            // Restore exact z-order/list order from before the operation.
            items.Clear(); items.AddRange(restored);
            groupNames.Clear();
            if (state.GroupNames != null) foreach (KeyValuePair<int, string> pair in state.GroupNames) groupNames[pair.Key] = pair.Value;
            collapsedGroups.Clear();
            if (state.CollapsedGroups != null) foreach (int id in state.CollapsedGroups) collapsedGroups.Add(id);
            nextGroupId = Math.Max(1, state.NextGroupId);
            NormalizeHierarchy();

            hidden = state.Hidden;
            currentPresetName = state.CurrentPresetName ?? "";
            foreach (OverlayItemForm f in items)
            {
                f.SetEditMode(EditMode && !(IntegratedMode && f.Type == ItemType.Web));
                f.RefreshEffectiveVisibility();
            }
            ApplyZOrder(); SaveConfigWithoutUiRefresh(); RefreshMainUi();

            // Restore selection by stable ItemId after the list rebuild.
            if (overlayList != null && !overlayList.IsDisposed)
            {
                HashSet<string> selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (state.SelectedItemIds != null) foreach (string id in state.SelectedItemIds) if (!string.IsNullOrWhiteSpace(id)) selectedIds.Add(id);
                foreach (ListViewItem row in overlayList.Items)
                {
                    OverlayItemForm f = row.Tag as OverlayItemForm;
                    row.Selected = f != null && selectedIds.Contains(f.ItemId ?? "");
                }
                RefreshPropertyEditor();
            }

            // Undo state is still in undoStates here, so restored old assets remain protected until now.
            foreach (string path in cleanupAssets) TryDeleteUnusedManagedAsset(path);
            foreach (string path in cleanupSounds) TryDeleteUnusedManagedSound(path);
            state.LastAppliedChangedItemCount = undoChangedItems;
            DetailedLog.Write("UNDO_APPLY", "END changed=" + undoChangedItems.ToString() +
                " finalItems=" + items.Count.ToString());
            return true;
        }

        private void UndoLastAction()
        {
            if (undoStates.Count == 0)
            {
                DetailedLog.Write("UNDO", "REQUEST ignored: stack empty");
                SetStatus("실행 취소할 작업이 없습니다."); return;
            }
            int index = undoStates.Count - 1;
            UndoState state = undoStates[index];
            bool success = false;
            DetailedLog.Write("UNDO", "REQUEST begin index=" + index.ToString() +
                " reason=" + (state.Reason ?? "") +
                " settingsOnly=" + state.SettingsOnly.ToString() +
                " stack=" + undoStates.Count.ToString());
            DiagnosticScene("UNDO_BEFORE", state.Reason);
            try
            {
                if (state.SettingsOnly)
                {
                    RestoreSettingsUndo(state);
                    success = true;
                }
                else
                {
                    success = RestoreOverlayUndo(state);
                    if (!success) { SetStatus("실행 취소를 안전하게 적용하지 못해 현재 화면을 유지했습니다."); return; }
                }
                undoStates.RemoveAt(index);
                DetailedLog.Write("UNDO", "REQUEST success reason=" + (state.Reason ?? "") +
                    " changed=" + state.LastAppliedChangedItemCount.ToString() +
                    " stackAfter=" + undoStates.Count.ToString());
                DiagnosticScene("UNDO_AFTER", state.Reason);
                string changedSuffix = state.SettingsOnly ? "" : "  |  변경 " + state.LastAppliedChangedItemCount.ToString() + "개";
                SetStatus("실행 취소 완료: " + state.Reason + changedSuffix + "  |  Ctrl+Z 또는 실행 취소 버튼");
            }
            catch (Exception ex)
            {
                DetailedLog.Write("UNDO", "REQUEST exception " + ex.GetType().Name + ": " + ex.Message);
                DiagnosticScene("UNDO_EXCEPTION", state == null ? "" : state.Reason);
                CrashLog.Write(ex, "UndoLastAction");
                SetStatus("실행 취소 실패: " + ex.Message + "  |  현재 화면을 유지했습니다.");
            }
            finally
            {
                if (success) ReleaseUndoState(state);
            }
        }

        private void ReleaseUndoState(UndoState state)
        {
            if (state == null) return;
            try { if (!string.IsNullOrEmpty(state.Path) && File.Exists(state.Path)) File.Delete(state.Path); } catch { }
            if (state.AssetPaths != null)
                foreach (string asset in state.AssetPaths) TryDeleteUnusedManagedAsset(asset);
            if (state.SoundPaths != null)
                foreach (string sound in state.SoundPaths) TryDeleteUnusedManagedSound(sound);
        }

        private bool IsAssetReferencedByUndo(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            foreach (UndoState state in undoStates)
                if (state != null && state.AssetPaths != null && state.AssetPaths.Contains(path)) return true;
            return false;
        }

        private bool IsSoundReferencedByUndo(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            foreach (UndoState state in undoStates)
                if (state != null && state.SoundPaths != null && state.SoundPaths.Contains(path)) return true;
            return false;
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
            ShowBeginnerToast("전체 오버레이를 삭제했습니다.", "되돌리기", delegate { UndoLastAction(); });
        }

        private void ResetSettingsInteractive()
        {
            DialogResult result = MessageBox.Show(this,
                "프로그램 설정을 초기값으로 돌릴까요?\n\n초기화: 모든 사용자 지정 단축키, 프리셋 단축키, 확대/축소 비율, 자석 감도, 편집/숨김 상태, 본창 크기\n유지: 현재 오버레이, 프리셋, 이미지/알람 파일",
                "설정 초기화", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result != DialogResult.Yes) return;

            CaptureUndo("설정 초기화");
            hotkeyAllHideVk = Native.VK_F11;
            hotkeyDetailVk = Native.VK_F10;
            hotkeyRemoteVk = 0; hotkeyWebReloadVk = 0; hotkeyPresetLoadVk = 0; hotkeyGroupLoadVk = 0;
            hotkeyAllHideMods = 0;
            hotkeyDetailMods = 0;
            hotkeyRemoteMods = 0; hotkeyWebReloadMods = 0; hotkeyPresetLoadMods = 0; hotkeyGroupLoadMods = 0;
            secondaryHotkeys.Clear();
            ApplyRecommendedQuickHotkeyDefaults();
            ResetEditActionHotkeysToDefaults();
            zoomStepPercent = 10;
            rotationSnapDegrees = 5;
            placementSnapPixels = 8;
            programMagnetSnapPixels = 36;
            resizeGracePixels = 30;
            resizeGraceMs = 500;
            suppressAutomaticUpdatePrompt = false;
            presetHotkeys.Clear();
            webInteractionStyle = WebInteractionStyle.DoubleClick;
            IntegratedMode = false;
            WebControlMode = false;
            loadedEditorMode = EditorMode.Normal;
            EditMode = true;
            DetailEditMode = true;
            hidden = false;
            currentPresetName = "";
            WindowState = FormWindowState.Normal;
            ClientSize = compactMainActive ? compactMainClientSize : mainBaseClientSize;
            foreach (OverlayItemForm f in items)
            {
                f.SetEditMode(true);
                f.RefreshEffectiveVisibility();
            }
            ApplyHotkeys();
            UpdateButtons();
            if (!compactMainActive) ScaleMainLayout();
            SaveConfig();
            SetStatus("설정 초기화 완료: 단축키/편집 상태/본창 크기 초기화  |  오버레이와 프리셋은 유지됨");
        }

        private OverlayItemForm FindOverlayFromWindowHandle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            IntPtr current = hwnd;
            for (int depth = 0; depth < 16 && current != IntPtr.Zero; depth++)
            {
                foreach (OverlayItemForm f in items)
                {
                    if (f == null || f.IsDisposed || !f.IsHandleCreated) continue;
                    if (f.Handle == current) return f;
                }
                IntPtr parent = Native.GetParent(current);
                if (parent == current) break;
                current = parent;
            }
            return null;
        }

        private OverlayItemForm FindWebOverlayFromWindowHandle(IntPtr hwnd)
        {
            OverlayItemForm f = FindOverlayFromWindowHandle(hwnd);
            return f != null && f.Type == ItemType.Web ? f : null;
        }

        public bool IsWebInteractionEnabled(OverlayItemForm web)
        {
            if (web == null || web.IsDisposed || web.Type != ItemType.Web) return false;
            return WebControlMode || IntegratedMode || object.ReferenceEquals(singleWebControlOverlay, web);
        }

        public bool IsSingleWebControl(OverlayItemForm web)
        {
            return !WebControlMode && !IntegratedMode && web != null && object.ReferenceEquals(singleWebControlOverlay, web);
        }

        public void EnterWebControlFromDoubleClick(OverlayItemForm web)
        {
            if (web == null || web.IsDisposed || web.Type != ItemType.Web || !EditMode || WebControlMode) return;
            if (IntegratedMode)
            {
                SelectOverlayForEditing(web);
                web.FocusWebContent();
                return;
            }
            if (HasSingleWebControl) ExitSingleWebControl(false);
            modeBeforeWebControl = CurrentEditorMode;
            SetEditorMode(EditorMode.WebControl, false);
            SelectOverlayForEditing(web);
            web.FocusWebContent();
            SetStatus("웹 조작 모드  |  ESC / 웹 바깥 클릭 = 이전 모드");
        }

        public void EnterSingleWebControl(OverlayItemForm web)
        {
            if (web == null || web.IsDisposed || web.Type != ItemType.Web || !EditMode || WebControlMode || IntegratedMode) return;
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
                EditorMode restore = modeBeforeWebControl == EditorMode.WebControl ? EditorMode.Normal : modeBeforeWebControl;
                SetEditorMode(restore, false);
                SetStatus(restore == EditorMode.Integrated ? "통합 모드" : (restore == EditorMode.Fixed ? "고정 모드" : "편집 모드"));
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
            if (mode == EditorMode.Normal && webInteractionStyle == WebInteractionStyle.Integrated) mode = EditorMode.Integrated;
            else if (mode == EditorMode.Integrated && webInteractionStyle != WebInteractionStyle.Integrated) mode = EditorMode.Normal;
            if (HasSingleWebControl) ExitSingleWebControl(false);
            singleClickWebCandidate = null;
            singleClickWebMoved = false;
            WebControlMode = mode == EditorMode.WebControl;
            IntegratedMode = mode == EditorMode.Integrated;
            EditMode = mode == EditorMode.Normal || IntegratedMode;
            DetailEditMode = WebControlMode || IntegratedMode;
            if (!WebControlMode) loadedEditorMode = mode;
            foreach (OverlayItemForm f in items)
            {
                // Integrated mode is normal overlay editing plus an enabled WebView2. Keeping the
                // web overlay itself in edit mode avoids the no-activate/input split that made
                // browser focus and frame movement feel inconsistent. The WebView2 child still
                // receives page clicks directly because IsWebInteractionEnabled() is true.
                f.SetEditMode(EditMode);
            }
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
            else if (IntegratedMode) SetStatus("통합 편집  |  웹 내부는 직접 조작  |  선택 테두리 드래그로 이동/크기 변경  |  일반 오버레이는 편집 가능");
            UpdateBeginnerQuickBar();
            if (save)
            {
                SaveConfig();
                ShowBeginnerModeToast();
            }
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (IsDisposed) return false;

            // Right-click stays a normal context-menu click unless the pointer actually moves.
            // Once movement exceeds 4 px, switch to overlay move mode and consume the rest of the
            // right-button gesture. Because this runs before WinForms/WebView2 dispatch, it works
            // over embedded web content in Integrated mode too.
            if (!WebControlMode && EditMode)
            {
                if (m.Msg == 0x0204) // WM_RBUTTONDOWN
                {
                    OverlayItemForm candidate = FindOverlayFromWindowHandle(m.HWnd);
                    if (candidate != null && !candidate.IsDisposed && candidate.IsOverlayVisible && !candidate.Locked)
                    {
                        rightDragOverlay = candidate;
                        rightDragStart = Cursor.Position;
                        rightDragMoved = false;
                    }
                }
                else if (m.Msg == 0x0200 && rightDragOverlay != null) // WM_MOUSEMOVE
                {
                    if (rightDragOverlay.IsDisposed)
                    {
                        rightDragOverlay = null;
                        rightDragMoved = false;
                    }
                    else
                    {
                        Point now = Cursor.Position;
                        int dx = now.X - rightDragStart.X;
                        int dy = now.Y - rightDragStart.Y;
                        if (!rightDragMoved && (dx * dx + dy * dy) >= 16)
                        {
                            if (rightDragOverlay.BeginRightButtonMove()) rightDragMoved = true;
                            else rightDragOverlay = null;
                        }
                        if (rightDragMoved && rightDragOverlay != null)
                        {
                            rightDragOverlay.ContinueRightButtonMove();
                            return true;
                        }
                    }
                }
                else if (m.Msg == 0x0205 && rightDragOverlay != null) // WM_RBUTTONUP
                {
                    OverlayItemForm draggedOverlay = rightDragOverlay;
                    bool moved = rightDragMoved;
                    rightDragOverlay = null;
                    rightDragMoved = false;
                    if (moved && draggedOverlay != null && !draggedOverlay.IsDisposed)
                    {
                        draggedOverlay.ContinueRightButtonMove();
                        draggedOverlay.EndRightButtonMove();
                        suppressRightContextUntil = DateTime.UtcNow.AddMilliseconds(600);
                        return true;
                    }
                }
                else if (m.Msg == 0x007B && DateTime.UtcNow <= suppressRightContextUntil) // WM_CONTEXTMENU
                {
                    suppressRightContextUntil = DateTime.MinValue;
                    return true;
                }
            }

            // TEST 11.4: Ctrl+Z is handled at application-message-filter level. Overlay forms,
            // selection frames and native child HWNDs can own focus, so ProcessCmdKey alone is
            // not reliable. Text-entry controls and actively interactive WebView2 content keep
            // their own Ctrl+Z. The key-up latch prevents Windows key-repeat from consuming
            // several undo states from one held key press.
            if ((m.Msg == 0x0101 || m.Msg == 0x0105) && m.WParam.ToInt32() == (int)Keys.Z)
                undoShortcutDown = false;

            if (!WebControlMode && (m.Msg == 0x0100 || m.Msg == 0x0104) &&
                m.WParam.ToInt32() == (int)Keys.Z &&
                (Control.ModifierKeys & Keys.Control) == Keys.Control &&
                !ShouldPreserveDeleteForInput(m.HWnd))
            {
                if (!undoShortcutDown)
                {
                    undoShortcutDown = true;
                    UndoLastAction();
                }
                return true;
            }

            // TEST 13.6: Ctrl+C is also handled at application level. In TEST 13.4/13.5
            // it depended on the overlay Form owning focus, so clicking the main list/panel could
            // make image copy look dead even though the overlay shortcut itself was valid.
            if (!WebControlMode && (m.Msg == 0x0100 || m.Msg == 0x0104) &&
                m.WParam.ToInt32() == (int)Keys.C &&
                (Control.ModifierKeys & Keys.Control) == Keys.Control &&
                !ShouldPreserveDeleteForInput(m.HWnd))
            {
                OverlayItemForm copySource = FindOverlayFromWindowHandle(m.HWnd);
                if (copySource == null || copySource.IsDisposed || copySource.Type != ItemType.Image)
                    copySource = SelectedOverlay;

                if (copySource != null && !copySource.IsDisposed && copySource.Type == ItemType.Image)
                {
                    CopyOriginalImageToClipboard(copySource);
                    return true;
                }
            }

            // TEST 08: Ctrl+V is handled at application-message-filter level while editing.
            // This makes a copied image or copied image/page URL additive even when focus is
            // on an overlay window rather than the CatLayer main form. Text inputs and an
            // interactive WebView2 keep their normal paste behavior.
            if (!WebControlMode && EditMode && (m.Msg == 0x0100 || m.Msg == 0x0104) &&
                m.WParam.ToInt32() == (int)Keys.V &&
                (Control.ModifierKeys & Keys.Control) == Keys.Control &&
                !ShouldPreserveDeleteForInput(m.HWnd))
            {
                PasteImageFromClipboard();
                return true;
            }

            // Delete is handled at the application message-filter level as well as by the
            // overlay/list controls. This prevents focus changes to panels or native child
            // windows from making Delete appear to work only intermittently. Text-entry
            // controls and an actively interactive web page keep their normal Delete key.
            if (!WebControlMode && EditMode && (m.Msg == 0x0100 || m.Msg == 0x0104) &&
                m.WParam.ToInt32() == (int)Keys.Delete && SelectedOverlays.Count > 0 &&
                !ShouldPreserveDeleteForInput(m.HWnd))
            {
                DeleteSelectedOverlays();
                return true;
            }

            // Integrated mode deliberately does not intercept browser mouse messages. WebView2
            // receives the exact same click/scroll/text-input path as full Web Control mode.
            // Because integrated web overlays remain in edit mode, WebView2 Enter/GotFocus selects
            // the overlay after the browser receives the click, and the outside frame handles move/resize.

            // Single-click mode distinguishes an actual click from an overlay drag. Previously the
            // mode changed on WM_LBUTTONDOWN, so the edit drag was cancelled before it could start.
            // Arm on down, cancel after a small movement, and enter Web Control after mouse-up.
            if (webInteractionStyle == WebInteractionStyle.SingleClick && !WebControlMode && !IntegratedMode && EditMode)
            {
                if (m.Msg == 0x0201) // WM_LBUTTONDOWN
                {
                    OverlayItemForm candidate = FindWebOverlayFromWindowHandle(m.HWnd);
                    singleClickWebCandidate = candidate;
                    singleClickWebStart = Cursor.Position;
                    singleClickWebMoved = false;
                }
                else if (m.Msg == 0x0200 && singleClickWebCandidate != null) // WM_MOUSEMOVE
                {
                    Point now = Cursor.Position;
                    int dx = now.X - singleClickWebStart.X;
                    int dy = now.Y - singleClickWebStart.Y;
                    if ((dx * dx + dy * dy) > 36) singleClickWebMoved = true;
                }
                else if (m.Msg == 0x0202 && singleClickWebCandidate != null) // WM_LBUTTONUP
                {
                    OverlayItemForm candidate = singleClickWebCandidate;
                    Point now = Cursor.Position;
                    int dx = now.X - singleClickWebStart.X;
                    int dy = now.Y - singleClickWebStart.Y;
                    bool click = !singleClickWebMoved && (dx * dx + dy * dy) <= 36;
                    singleClickWebCandidate = null;
                    singleClickWebMoved = false;
                    if (click && candidate != null && !candidate.IsDisposed)
                    {
                        try
                        {
                            BeginInvoke((MethodInvoker)delegate
                            {
                                if (!IsDisposed && webInteractionStyle == WebInteractionStyle.SingleClick &&
                                    !WebControlMode && !IntegratedMode && EditMode && candidate != null && !candidate.IsDisposed)
                                    EnterWebControlFromDoubleClick(candidate);
                            });
                        }
                        catch { }
                    }
                }
            }

            // WebView2 uses its own native child HWND, so WinForms MouseDoubleClick on the
            // overlay Form is not reliable. Catch the raw double-click message and walk the
            // HWND parent chain until the owning web overlay is found.
            if (webInteractionStyle == WebInteractionStyle.DoubleClick && !WebControlMode && !IntegratedMode && EditMode && m.Msg == 0x0203) // WM_LBUTTONDBLCLK
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
                EditorMode restore = modeBeforeWebControl == EditorMode.WebControl ? EditorMode.Normal : modeBeforeWebControl;
                SetEditorMode(restore, false);
                SetStatus(restore == EditorMode.Integrated ? "통합 모드" : (restore == EditorMode.Fixed ? "고정 모드" : "편집 모드"));
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
                    EditorMode restore = modeBeforeWebControl == EditorMode.WebControl ? EditorMode.Normal : modeBeforeWebControl;
                    SetEditorMode(restore, false);
                    SetStatus(restore == EditorMode.Integrated ? "통합 모드" : (restore == EditorMode.Fixed ? "고정 모드" : "편집 모드"));
                    return false;
                }
            }

            if (!WebControlMode) return false;
            if (!IsBlockedMainUiInputMessage(m.Msg)) return false;

            Control target = null;
            try { target = Control.FromHandle(m.HWnd); } catch { }
            if (target == null || !IsDescendantOfMainForm(target)) return false;

            // F9/F11/F10 remain available as quick hide, whole hide, and Web Control exit controls.
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

        private bool ShouldPreserveDeleteForInput(IntPtr hwnd)
        {
            OverlayItemForm web = FindWebOverlayFromWindowHandle(hwnd);
            if (web != null && IsWebInteractionEnabled(web)) return true;

            Control target = null;
            try { target = Control.FromHandle(hwnd); } catch { }
            if (target == null) return false;

            Control current = target;
            while (current != null)
            {
                if (current is TextBoxBase || current is ComboBox || current is NumericUpDown || current is DomainUpDown)
                    return true;
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
            // Keyboard/input messages sent to CatLayer's own main controls.
            // Shared global shortcuts are observed separately and are never consumed here.
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
                modeBeforeWebControl = CurrentEditorMode;
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
            if (WebControlMode)
            {
                EditorMode restore = modeBeforeWebControl == EditorMode.WebControl ? EditorMode.Normal : modeBeforeWebControl;
                SetEditorMode(restore, true);
                return;
            }
            SetEditorMode(EditMode ? EditorMode.Fixed : EditorMode.Normal, true);
        }

        private void SetWebInteractionStyle(WebInteractionStyle style, bool save)
        {
            webInteractionStyle = style;
            if (WebControlMode)
            {
                EditorMode restore = modeBeforeWebControl == EditorMode.Fixed ? EditorMode.Fixed : EditorMode.Normal;
                SetEditorMode(restore, false);
            }
            else if (EditMode) SetEditorMode(EditorMode.Normal, false);
            else SetEditorMode(EditorMode.Fixed, false);
            if (save) SaveConfig();
            string name = style == WebInteractionStyle.DoubleClick ? "더블클릭" : (style == WebInteractionStyle.SingleClick ? "원클릭" : "통합");
            SetStatus("웹 조작 방식: " + name);
        }

        internal void MarkQuickHideTarget(OverlayItemForm source)
        {
            if (source == null || source.IsDisposed) return;
            quickHideTarget = source;
        }

        private OverlayItemForm FirstVisibleOverlayFromTop()
        {
            for (int priority = 1; priority <= items.Count; priority++)
            {
                OverlayItemForm f = items[items.Count - priority];
                if (f != null && !f.IsDisposed && f.IsOverlayVisible) return f;
            }
            return null;
        }

        private OverlayItemForm FirstHiddenOverlayFromTop()
        {
            for (int priority = 1; priority <= items.Count; priority++)
            {
                OverlayItemForm f = items[items.Count - priority];
                if (f != null && !f.IsDisposed && !f.IsOverlayVisible) return f;
            }
            return null;
        }

        private void RememberQuickHidden(OverlayItemForm target)
        {
            if (target == null || target.IsDisposed) return;
            quickHiddenHistory.Remove(target);
            quickHiddenHistory.Add(target);
        }

        private OverlayItemForm MostRecentQuickHidden()
        {
            for (int i = quickHiddenHistory.Count - 1; i >= 0; i--)
            {
                OverlayItemForm f = quickHiddenHistory[i];
                if (f == null || f.IsDisposed || f.IsOverlayVisible)
                {
                    quickHiddenHistory.RemoveAt(i);
                    continue;
                }
                quickHiddenHistory.RemoveAt(i);
                return f;
            }
            return null;
        }

        private void QuickHideOverlay()
        {
            OverlayItemForm target = quickHideTarget;
            quickHideTarget = null;
            if (target == null || target.IsDisposed || !target.IsOverlayVisible) target = FirstVisibleOverlayFromTop();
            if (target == null)
            {
                SetStatus("숨길 오버레이가 없습니다. 빠른 표시는 " + HotkeyText(hotkeyQuickShowMods, hotkeyQuickShowVk) + " 입니다.");
                return;
            }

            CaptureUndo("빠른 숨김");
            target.SetOverlayVisible(false);
            RememberQuickHidden(target);
            SaveConfigWithoutUiRefresh();
            RefreshMainUi();
            SetStatus("숨김: " + DisplayName(target) + "  |  다시 표시: " + HotkeyText(hotkeyQuickShowMods, hotkeyQuickShowVk));
        }

        private void QuickShowOverlay()
        {
            OverlayItemForm target = quickHideTarget;
            quickHideTarget = null;
            if (target != null && (target.IsDisposed || target.IsOverlayVisible)) target = null;
            if (target == null) target = MostRecentQuickHidden();
            if (target == null) target = FirstHiddenOverlayFromTop();
            if (target == null)
            {
                SetStatus("표시할 숨김 오버레이가 없습니다.");
                return;
            }

            CaptureUndo("빠른 표시");
            target.SetOverlayVisible(true);
            quickHiddenHistory.Remove(target);
            SaveConfigWithoutUiRefresh();
            RefreshMainUi();
            SetStatus("표시: " + DisplayName(target));
        }

        private int QuickShowHotkeyMods()
        {
            return hotkeyQuickShowMods;
        }

        private void ToggleHidden()
        {
            hidden = !hidden;
            foreach (OverlayItemForm f in items) f.RefreshEffectiveVisibility();
            UpdateButtons(); SaveConfig();
            ShowBeginnerToast(hidden ? "전체 오버레이를 숨겼습니다. F11을 다시 누르면 표시됩니다." : "전체 오버레이를 다시 표시했습니다.");
        }
        private void UpdateButtons()
        {
            editButton.Text = WebControlMode ? "웹 조작 중" : (IntegratedMode ? "통합 모드" : (EditMode ? "편집 모드" : "고정 모드"));
            editButton.BackColor = WebControlMode ? Color.FromArgb(72, 48, 120) : (IntegratedMode ? Color.FromArgb(31, 92, 88) : (EditMode ? Color.FromArgb(28, 63, 108) : UiPanel2));
            hideButton.Text = (hidden ? "전체 표시  " : "전체 숨김  ") + HotkeyText(hotkeyAllHideMods, hotkeyAllHideVk);
            hotkeyEditButton.Text = "편집  " + HotkeyText(hotkeyEditMods, hotkeyEditVk);
            hotkeyHideButton.Text = "빠른 숨김  " + HotkeyText(hotkeyHideMods, hotkeyHideVk);
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
                        "이 Release에는 자동 업데이트용 파일이 없습니다.\n\n필요한 Assets:\n- CatLayer_v" + release.Version + ".zip\n- SHA256.txt\n\nBUILD_RELEASE.bat을 실행하면 두 파일이 자동 생성됩니다.\nGitHub Release 페이지에 두 파일을 Asset으로 올려 주세요.",
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
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(SetStatus), message); } catch { }
                return;
            }
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
            if (vk <= 0) return "지정 안 함";
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

        private void RemoveMissingPresetHotkeys()
        {
            for (int i = presetHotkeys.Count - 1; i >= 0; i--)
            {
                PresetHotkeyBinding binding = presetHotkeys[i];
                string fileName = binding == null ? "" : Path.GetFileName(binding.FileName ?? "");
                string path = string.IsNullOrWhiteSpace(fileName) ? "" : Path.Combine(presetsDir, fileName);
                bool conflictsCore = binding != null && IsCoreHotkeyConflict(binding.Mods, binding.Vk);
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

        private static bool IsVirtualKeyDown(int vk)
        {
            return (Native.GetAsyncKeyState(vk) & unchecked((short)0x8000)) != 0;
        }

        private static bool IsSharedHotkeyModifierVk(int vk)
        {
            return vk == Native.VK_SHIFT || vk == (int)Keys.LShiftKey || vk == (int)Keys.RShiftKey ||
                   vk == Native.VK_CONTROL || vk == (int)Keys.LControlKey || vk == (int)Keys.RControlKey ||
                   vk == Native.VK_MENU || vk == (int)Keys.LMenu || vk == (int)Keys.RMenu ||
                   vk == Native.VK_LWIN || vk == Native.VK_RWIN;
        }

        private static int CurrentSharedHotkeyModifiers(Native.KBDLLHOOKSTRUCT info)
        {
            int mods = 0;
            if (IsVirtualKeyDown(Native.VK_CONTROL) ||
                IsVirtualKeyDown((int)Keys.LControlKey) ||
                IsVirtualKeyDown((int)Keys.RControlKey))
                mods |= Native.MOD_CONTROL;

            if (IsVirtualKeyDown(Native.VK_SHIFT) ||
                IsVirtualKeyDown((int)Keys.LShiftKey) ||
                IsVirtualKeyDown((int)Keys.RShiftKey))
                mods |= Native.MOD_SHIFT;

            if ((info.flags & Native.LLKHF_ALTDOWN) != 0 ||
                IsVirtualKeyDown(Native.VK_MENU) ||
                IsVirtualKeyDown((int)Keys.LMenu) ||
                IsVirtualKeyDown((int)Keys.RMenu))
                mods |= Native.MOD_ALT;

            if (IsVirtualKeyDown(Native.VK_LWIN) || IsVirtualKeyDown(Native.VK_RWIN))
                mods |= Native.MOD_WIN;

            return mods;
        }

        private static bool SharedBindingMatches(int actualMods, int actualVk, int bindingMods, int bindingVk)
        {
            return bindingVk > 0 && actualVk == bindingVk && actualMods == bindingMods;
        }

        private bool TryFindSharedGlobalAction(int mods, int vk, out string action)
        {
            action = null;

            if (SharedBindingMatches(mods, vk, hotkeyEditMods, hotkeyEditVk)) action = "EDIT";
            else if (SharedBindingMatches(mods, vk, hotkeyHideMods, hotkeyHideVk)) action = "HIDE";
            else if (SharedBindingMatches(mods, vk, hotkeyQuickShowMods, hotkeyQuickShowVk)) action = "QUICK_SHOW";
            else if (SharedBindingMatches(mods, vk, hotkeyAllHideMods, hotkeyAllHideVk)) action = "ALL_HIDE";
            else if (SharedBindingMatches(mods, vk, hotkeyDetailMods, hotkeyDetailVk)) action = "DETAIL";
            else if (SharedBindingMatches(mods, vk, hotkeyCaptureMods, hotkeyCaptureVk)) action = "CAPTURE";
            else if (SharedBindingMatches(mods, vk, hotkeyRemoteMods, hotkeyRemoteVk)) action = "REMOTE";
            else if (SharedBindingMatches(mods, vk, hotkeyWebReloadMods, hotkeyWebReloadVk)) action = "WEB_RELOAD";
            else if (SharedBindingMatches(mods, vk, hotkeyPresetLoadMods, hotkeyPresetLoadVk)) action = "PRESET_LOAD";
            else if (SharedBindingMatches(mods, vk, hotkeyGroupLoadMods, hotkeyGroupLoadVk)) action = "GROUP_LOAD";

            if (action != null) return true;

            foreach (string candidate in GlobalHotkeyActionKeys)
            {
                HotkeyBinding second = GetSecondaryHotkey(candidate);
                if (second != null &&
                    SharedBindingMatches(mods, vk, second.Mods, second.Vk))
                {
                    action = candidate;
                    return true;
                }
            }
            return false;
        }

        private PresetHotkeyBinding FindSharedPresetHotkey(int mods, int vk)
        {
            foreach (PresetHotkeyBinding binding in presetHotkeys)
                if (binding != null &&
                    SharedBindingMatches(mods, vk, binding.Mods, binding.Vk))
                    return binding;
            return null;
        }

        private IntPtr SharedKeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode == Native.HC_ACTION)
            {
                try
                {
                    int message = wParam.ToInt32();
                    Native.KBDLLHOOKSTRUCT info =
                        (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                            lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    int vk = unchecked((int)info.vkCode);

                    if (message == Native.WM_KEYUP || message == Native.WM_SYSKEYUP)
                    {
                        sharedHotkeyDown.Remove(vk);
                    }
                    else if ((message == Native.WM_KEYDOWN ||
                              message == Native.WM_SYSKEYDOWN) &&
                             !sharedHotkeyCaptureSuspended &&
                             !IsSharedHotkeyModifierVk(vk) &&
                             sharedHotkeyDown.Add(vk))
                    {
                        int mods = CurrentSharedHotkeyModifiers(info);
                        string action;
                        PresetHotkeyBinding preset = null;
                        bool matched = TryFindSharedGlobalAction(mods, vk, out action);
                        if (!matched) preset = FindSharedPresetHotkey(mods, vk);

                        if ((matched || preset != null) && !IsDisposed && IsHandleCreated)
                        {
                            string queuedAction = action;
                            PresetHotkeyBinding queuedPreset = preset;
                            BeginInvoke(new MethodInvoker(delegate
                            {
                                if (queuedAction != null)
                                    ExecuteSecondaryGlobalHotkey(queuedAction);
                                else if (queuedPreset != null)
                                    LoadPresetByHotkey(queuedPreset);
                            }));
                        }
                    }
                }
                catch { }
            }

            // Never consume the event: foreground game/app receives the same key.
            return Native.CallNextHookEx(
                sharedKeyboardHook, nCode, wParam, lParam);
        }

        private bool InstallSharedKeyboardHook()
        {
            if (sharedKeyboardHook != IntPtr.Zero) return true;
            try
            {
                sharedKeyboardHookProc = SharedKeyboardHookCallback;
                IntPtr module = Native.GetModuleHandle(null);
                sharedKeyboardHook = Native.SetWindowsHookEx(
                    Native.WH_KEYBOARD_LL,
                    sharedKeyboardHookProc,
                    module,
                    0);
                return sharedKeyboardHook != IntPtr.Zero;
            }
            catch
            {
                sharedKeyboardHook = IntPtr.Zero;
                return false;
            }
        }

        private void UnregisterAllHotkeys()
        {
            sharedHotkeyDown.Clear();
            if (sharedKeyboardHook != IntPtr.Zero)
            {
                try { Native.UnhookWindowsHookEx(sharedKeyboardHook); } catch { }
                sharedKeyboardHook = IntPtr.Zero;
            }
        }

        private bool ApplyHotkeys()
        {
            RemoveMissingPresetHotkeys();
            bool ok = InstallSharedKeyboardHook();
            if (mainUiReady && !ok)
                SetStatus("전역 단축키 감지를 시작하지 못했습니다. CatLayer를 다시 실행해 주세요.");
            return ok;
        }

        private void ShowHotkeySettings()
        {
            using (Form f = new Form())
            {
                f.Text = "CatLayer 설정 - 단축키";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(980, 706);
                f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false;
                f.BackColor = UiBack; f.ForeColor = UiText;
                f.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
                f.Shown += delegate
                {
                    sharedHotkeyCaptureSuspended = true;
                    try { int dark = 1; DwmSetWindowAttribute(f.Handle, 20, ref dark, 4); } catch { }
                };
                f.FormClosed += delegate { sharedHotkeyCaptureSuspended = false; };

                Label title = new Label();
                title.Text = "사용자 지정 단축키";
                title.Font = new Font(f.Font.FontFamily, 16F, FontStyle.Bold);
                title.ForeColor = UiText; title.BackColor = Color.Transparent;
                title.SetBounds(22, 18, 420, 32); f.Controls.Add(title);

                Label help = new Label();
                help.Text = "각 기능에 단축키를 2개까지 지정할 수 있습니다. 둘 중 어느 키를 눌러도 같은 기능이 실행됩니다.";
                help.Font = new Font(f.Font.FontFamily, 9F, FontStyle.Regular);
                help.ForeColor = UiMuted; help.BackColor = Color.Transparent;
                help.SetBounds(23, 53, 920, 22); f.Controls.Add(help);

                Panel infoBar = new Panel(); infoBar.SetBounds(22, 82, 936, 38); infoBar.BackColor = UiAccentSoft; f.Controls.Add(infoBar);
                Panel infoAccent = new Panel(); infoAccent.SetBounds(0, 0, 4, 38); infoAccent.BackColor = UiAccent; infoBar.Controls.Add(infoAccent);
                Label info = new Label();
                info.Text = "TIP   왼쪽 = 단축키 1 / 오른쪽 = 단축키 2   ·   Delete / Backspace / Esc로 비울 수 있습니다.";
                info.ForeColor = UiText; info.BackColor = Color.Transparent; info.TextAlign = ContentAlignment.MiddleLeft;
                info.SetBounds(14, 0, 900, 38); infoBar.Controls.Add(info);

                string[] labels = new string[] {
                    "고정 ↔ 편집", "빠른 숨김", "빠른 표시", "전체 표시/숨김", "웹 조작 모드", "영역 캡처",
                    "리모컨 열기", "선택 웹 새로고침", "프리셋 불러오기", "그룹 불러오기",
                    "그룹화/그룹해제", "그룹 해제",
                    "회전 -1°", "회전 +1°", "회전 -10°", "회전 +10°",
                    "좌우 반전", "상하 반전", "각도 0° (반전 유지)", "회전/반전 전체 초기화"
                };
                string[] actionKeys = new string[] {
                    "EDIT", "HIDE", "QUICK_SHOW", "ALL_HIDE", "DETAIL", "CAPTURE", "REMOTE", "WEB_RELOAD", "PRESET_LOAD", "GROUP_LOAD",
                    "GROUP", "UNGROUP", "ROT_M1", "ROT_P1", "ROT_M10", "ROT_P10", "FLIP_H", "FLIP_V", "RESET_ROT", "RESET_ALL"
                };
                int[][] modsRefs = new int[][] {
                    new int[] { hotkeyEditMods }, new int[] { hotkeyHideMods }, new int[] { hotkeyQuickShowMods }, new int[] { hotkeyAllHideMods }, new int[] { hotkeyDetailMods }, new int[] { hotkeyCaptureMods },
                    new int[] { hotkeyRemoteMods }, new int[] { hotkeyWebReloadMods }, new int[] { hotkeyPresetLoadMods }, new int[] { hotkeyGroupLoadMods },
                    new int[] { hotkeyGroupMods }, new int[] { hotkeyUngroupMods },
                    new int[] { hotkeyRotateMinus1Mods }, new int[] { hotkeyRotatePlus1Mods }, new int[] { hotkeyRotateMinus10Mods }, new int[] { hotkeyRotatePlus10Mods },
                    new int[] { hotkeyFlipHorizontalMods }, new int[] { hotkeyFlipVerticalMods },
                    new int[] { hotkeyResetRotationMods }, new int[] { hotkeyResetTransformMods }
                };
                int[][] vkRefs = new int[][] {
                    new int[] { hotkeyEditVk }, new int[] { hotkeyHideVk }, new int[] { hotkeyQuickShowVk }, new int[] { hotkeyAllHideVk }, new int[] { hotkeyDetailVk }, new int[] { hotkeyCaptureVk },
                    new int[] { hotkeyRemoteVk }, new int[] { hotkeyWebReloadVk }, new int[] { hotkeyPresetLoadVk }, new int[] { hotkeyGroupLoadVk },
                    new int[] { hotkeyGroupVk }, new int[] { hotkeyUngroupVk },
                    new int[] { hotkeyRotateMinus1Vk }, new int[] { hotkeyRotatePlus1Vk }, new int[] { hotkeyRotateMinus10Vk }, new int[] { hotkeyRotatePlus10Vk },
                    new int[] { hotkeyFlipHorizontalVk }, new int[] { hotkeyFlipVerticalVk },
                    new int[] { hotkeyResetRotationVk }, new int[] { hotkeyResetTransformVk }
                };
                int[][] secondModsRefs = new int[labels.Length][];
                int[][] secondVkRefs = new int[labels.Length][];
                TextBox[] boxes1 = new TextBox[labels.Length];
                TextBox[] boxes2 = new TextBox[labels.Length];
                for (int i = 0; i < labels.Length; i++)
                {
                    HotkeyBinding second = GetSecondaryHotkey(actionKeys[i]);
                    secondModsRefs[i] = new int[] { second == null ? 0 : second.Mods };
                    secondVkRefs[i] = new int[] { second == null ? 0 : second.Vk };
                }

                Panel globalCard = new Panel(); globalCard.SetBounds(22, 132, 458, 484); globalCard.BackColor = UiPanel; f.Controls.Add(globalCard);
                Panel globalTop = new Panel(); globalTop.SetBounds(0, 0, 458, 52); globalTop.BackColor = UiPanel2; globalCard.Controls.Add(globalTop);
                Panel globalAccent = new Panel(); globalAccent.SetBounds(0, 0, 4, 52); globalAccent.BackColor = UiAccent; globalTop.Controls.Add(globalAccent);
                Label globalTitle = new Label(); globalTitle.Text = "전역 단축키"; globalTitle.Font = new Font(f.Font.FontFamily, 11F, FontStyle.Bold); globalTitle.ForeColor = UiText; globalTitle.BackColor = Color.Transparent; globalTitle.SetBounds(16, 8, 180, 22); globalTop.Controls.Add(globalTitle);
                Label globalDesc = new Label(); globalDesc.Text = "게임·다른 프로그램에서도 작동 · 키 입력 통과"; globalDesc.ForeColor = UiMuted; globalDesc.BackColor = Color.Transparent; globalDesc.SetBounds(16, 29, 250, 18); globalTop.Controls.Add(globalDesc);
                Label globalSlot1 = new Label(); globalSlot1.Text = "1"; globalSlot1.TextAlign = ContentAlignment.MiddleCenter; globalSlot1.ForeColor = UiMuted; globalSlot1.BackColor = Color.Transparent; globalSlot1.SetBounds(186, 15, 112, 20); globalTop.Controls.Add(globalSlot1);
                Label globalSlot2 = new Label(); globalSlot2.Text = "2"; globalSlot2.TextAlign = ContentAlignment.MiddleCenter; globalSlot2.ForeColor = UiMuted; globalSlot2.BackColor = Color.Transparent; globalSlot2.SetBounds(308, 15, 112, 20); globalTop.Controls.Add(globalSlot2);

                Panel editCard = new Panel(); editCard.SetBounds(500, 132, 458, 484); editCard.BackColor = UiPanel; f.Controls.Add(editCard);
                Panel editTop = new Panel(); editTop.SetBounds(0, 0, 458, 52); editTop.BackColor = UiPanel2; editCard.Controls.Add(editTop);
                Panel editAccent = new Panel(); editAccent.SetBounds(0, 0, 4, 52); editAccent.BackColor = Color.FromArgb(52, 177, 170); editTop.Controls.Add(editAccent);
                Label editTitle = new Label(); editTitle.Text = "편집 단축키"; editTitle.Font = new Font(f.Font.FontFamily, 11F, FontStyle.Bold); editTitle.ForeColor = UiText; editTitle.BackColor = Color.Transparent; editTitle.SetBounds(16, 8, 180, 22); editTop.Controls.Add(editTitle);
                Label editDesc = new Label(); editDesc.Text = "선택한 오버레이 편집 중 사용"; editDesc.ForeColor = UiMuted; editDesc.BackColor = Color.Transparent; editDesc.SetBounds(16, 29, 250, 18); editTop.Controls.Add(editDesc);
                Label editSlot1 = new Label(); editSlot1.Text = "1"; editSlot1.TextAlign = ContentAlignment.MiddleCenter; editSlot1.ForeColor = UiMuted; editSlot1.BackColor = Color.Transparent; editSlot1.SetBounds(186, 15, 112, 20); editTop.Controls.Add(editSlot1);
                Label editSlot2 = new Label(); editSlot2.Text = "2"; editSlot2.TextAlign = ContentAlignment.MiddleCenter; editSlot2.ForeColor = UiMuted; editSlot2.BackColor = Color.Transparent; editSlot2.SetBounds(308, 15, 112, 20); editTop.Controls.Add(editSlot2);

                for (int i = 0; i < labels.Length; i++)
                {
                    bool global = i < 10;
                    Panel host = global ? globalCard : editCard;
                    int localIndex = global ? i : i - 10;
                    int rowY = 60 + localIndex * 41;
                    Panel row = new Panel(); row.SetBounds(10, rowY, 438, 36); row.BackColor = (localIndex % 2 == 0) ? UiPanel2 : Color.FromArgb(17, 35, 62); host.Controls.Add(row);

                    Label label = new Label(); label.Text = labels[i]; label.ForeColor = UiText; label.BackColor = Color.Transparent;
                    label.AutoEllipsis = true; label.TextAlign = ContentAlignment.MiddleLeft; label.SetBounds(10, 3, 138, 30); row.Controls.Add(label);

                    boxes1[i] = new TextBox(); boxes1[i].SetBounds(150, 5, 134, 26); boxes1[i].BackColor = UiBack; boxes1[i].ForeColor = UiText;
                    boxes1[i].BorderStyle = BorderStyle.FixedSingle; boxes1[i].Font = new Font(f.Font.FontFamily, 8.5F, FontStyle.Bold); row.Controls.Add(boxes1[i]);
                    SetupOptionalHotkeyCapture(boxes1[i], modsRefs[i], vkRefs[i]);

                    boxes2[i] = new TextBox(); boxes2[i].SetBounds(292, 5, 134, 26); boxes2[i].BackColor = UiBack; boxes2[i].ForeColor = UiText;
                    boxes2[i].BorderStyle = BorderStyle.FixedSingle; boxes2[i].Font = new Font(f.Font.FontFamily, 8.5F, FontStyle.Bold); row.Controls.Add(boxes2[i]);
                    SetupOptionalHotkeyCapture(boxes2[i], secondModsRefs[i], secondVkRefs[i]);

                    boxes1[i].Enter += delegate(object sender, EventArgs e) { TextBox b = sender as TextBox; if (b != null) b.BackColor = Color.FromArgb(27, 38, 70); };
                    boxes1[i].Leave += delegate(object sender, EventArgs e) { TextBox b = sender as TextBox; if (b != null) b.BackColor = UiBack; };
                    boxes2[i].Enter += delegate(object sender, EventArgs e) { TextBox b = sender as TextBox; if (b != null) b.BackColor = Color.FromArgb(27, 38, 70); };
                    boxes2[i].Leave += delegate(object sender, EventArgs e) { TextBox b = sender as TextBox; if (b != null) b.BackColor = UiBack; };
                }

                Panel footerLine = new Panel(); footerLine.SetBounds(22, 630, 936, 1); footerLine.BackColor = UiBorder; f.Controls.Add(footerLine);
                Button defaults = new Button(); defaults.Text = "기본값으로 복원"; defaults.SetBounds(22, 648, 126, 32); StyleButton(defaults, false); f.Controls.Add(defaults);
                Button ok = new Button(); ok.Text = "적용"; ok.DialogResult = DialogResult.OK; ok.SetBounds(784, 648, 82, 32); StyleButton(ok, false); ok.BackColor = UiAccent; ok.FlatAppearance.BorderColor = UiAccent; f.Controls.Add(ok);
                Button cancel = new Button(); cancel.Text = "취소"; cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(876, 648, 82, 32); StyleButton(cancel, false); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;

                defaults.Click += delegate
                {
                    int[] defaultVks = new int[] { (int)Keys.Q, (int)Keys.W, (int)Keys.W, Native.VK_F11, Native.VK_F10, (int)Keys.E, 0, 0, 0, 0, (int)Keys.G, (int)Keys.G, (int)Keys.Q, (int)Keys.E, (int)Keys.Q, (int)Keys.E, (int)Keys.H, (int)Keys.V, (int)Keys.R, (int)Keys.R };
                    int[] defaultMods = new int[] { Native.MOD_ALT, Native.MOD_ALT, Native.MOD_ALT | Native.MOD_SHIFT, 0, 0, Native.MOD_ALT, 0, 0, 0, 0, Native.MOD_CONTROL, Native.MOD_CONTROL | Native.MOD_SHIFT, 0, 0, Native.MOD_SHIFT, Native.MOD_SHIFT, 0, 0, 0, Native.MOD_SHIFT };
                    int[] defaultSecondVks = new int[] { Native.VK_F8, Native.VK_F9, Native.VK_F9, 0, 0, Native.VK_F7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                    int[] defaultSecondMods = new int[] { 0, 0, Native.MOD_SHIFT, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                    for (int i = 0; i < boxes1.Length; i++)
                    {
                        modsRefs[i][0] = defaultMods[i]; vkRefs[i][0] = defaultVks[i];
                        boxes1[i].Text = vkRefs[i][0] > 0 ? HotkeyText(modsRefs[i][0], vkRefs[i][0]) : "지정 안 함";
                        secondModsRefs[i][0] = defaultSecondMods[i]; secondVkRefs[i][0] = defaultSecondVks[i];
                        boxes2[i].Text = secondVkRefs[i][0] > 0 ? HotkeyText(secondModsRefs[i][0], secondVkRefs[i][0]) : "지정 안 함";
                    }
                };

                if (f.ShowDialog(this) != DialogResult.OK) return;

                HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < labels.Length; i++)
                {
                    int[] candidateMods = new int[] { modsRefs[i][0], secondModsRefs[i][0] };
                    int[] candidateVks = new int[] { vkRefs[i][0], secondVkRefs[i][0] };
                    for (int slot = 0; slot < 2; slot++)
                    {
                        if (candidateVks[slot] <= 0) continue;
                        if (IsReservedClipboardHotkey(candidateMods[slot], candidateVks[slot]))
                        {
                            MessageBox.Show(this, "Ctrl+C / Ctrl+V는 이미지 원본 복사와 이미지 붙여넣기 전용키로 예약되어 있습니다.\n\n" + labels[i] + " / 단축키 " + (slot + 1).ToString(), "단축키 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        if (i < 10 && !IsSafeGlobalHotkey(candidateMods[slot], candidateVks[slot]))
                        {
                            MessageBox.Show(this, "전역 단축키에서 문자·숫자를 사용할 때는 Ctrl, Alt 또는 Shift 같은 조합키가 필요합니다.\n\nF1~F12는 단독으로 사용할 수 있습니다.", "단축키 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        string keyName = HotkeyText(candidateMods[slot], candidateVks[slot]);
                        if (!used.Add(keyName))
                        {
                            MessageBox.Show(this, "단축키가 중복되었습니다.\n\n" + labels[i] + " / " + keyName, "단축키 중복", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                    }
                }

                foreach (PresetHotkeyBinding binding in presetHotkeys)
                {
                    if (binding == null || binding.Vk <= 0) continue;
                    string presetHotkey = HotkeyText(binding.Mods, binding.Vk);
                    if (used.Contains(presetHotkey))
                    {
                        MessageBox.Show(this, "프리셋 단축키와 중복되었습니다.\n\n" + presetHotkey, "단축키 중복", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }

                hotkeyEditMods = modsRefs[0][0]; hotkeyEditVk = vkRefs[0][0];
                hotkeyHideMods = modsRefs[1][0]; hotkeyHideVk = vkRefs[1][0];
                hotkeyQuickShowMods = modsRefs[2][0]; hotkeyQuickShowVk = vkRefs[2][0];
                hotkeyAllHideMods = modsRefs[3][0]; hotkeyAllHideVk = vkRefs[3][0];
                hotkeyDetailMods = modsRefs[4][0]; hotkeyDetailVk = vkRefs[4][0];
                hotkeyCaptureMods = modsRefs[5][0]; hotkeyCaptureVk = vkRefs[5][0];
                hotkeyRemoteMods = modsRefs[6][0]; hotkeyRemoteVk = vkRefs[6][0];
                hotkeyWebReloadMods = modsRefs[7][0]; hotkeyWebReloadVk = vkRefs[7][0];
                hotkeyPresetLoadMods = modsRefs[8][0]; hotkeyPresetLoadVk = vkRefs[8][0];
                hotkeyGroupLoadMods = modsRefs[9][0]; hotkeyGroupLoadVk = vkRefs[9][0];
                hotkeyGroupMods = modsRefs[10][0]; hotkeyGroupVk = vkRefs[10][0];
                hotkeyUngroupMods = modsRefs[11][0]; hotkeyUngroupVk = vkRefs[11][0];
                hotkeyRotateMinus1Mods = modsRefs[12][0]; hotkeyRotateMinus1Vk = vkRefs[12][0];
                hotkeyRotatePlus1Mods = modsRefs[13][0]; hotkeyRotatePlus1Vk = vkRefs[13][0];
                hotkeyRotateMinus10Mods = modsRefs[14][0]; hotkeyRotateMinus10Vk = vkRefs[14][0];
                hotkeyRotatePlus10Mods = modsRefs[15][0]; hotkeyRotatePlus10Vk = vkRefs[15][0];
                hotkeyFlipHorizontalMods = modsRefs[16][0]; hotkeyFlipHorizontalVk = vkRefs[16][0];
                hotkeyFlipVerticalMods = modsRefs[17][0]; hotkeyFlipVerticalVk = vkRefs[17][0];
                hotkeyResetRotationMods = modsRefs[18][0]; hotkeyResetRotationVk = vkRefs[18][0];
                hotkeyResetTransformMods = modsRefs[19][0]; hotkeyResetTransformVk = vkRefs[19][0];
                for (int i = 0; i < actionKeys.Length; i++) SetSecondaryHotkey(actionKeys[i], secondModsRefs[i][0], secondVkRefs[i][0]);

                NormalizeSecondaryHotkeys();
                bool registered = ApplyHotkeys();
                UpdateButtons(); SaveConfig();
                SetStatus(registered ? "사용자 지정 단축키 1/2를 적용했습니다. 키 입력은 다른 프로그램에도 전달됩니다." : "단축키는 저장됐지만 전역 키 감지를 시작하지 못했습니다.");
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

        private void ShowProgramMagnetSettings()
        {
            using (Form f = new Form())
            using (Label title = new Label())
            using (Label help = new Label())
            using (CheckBox autoEnable = new CheckBox())
            using (DarkNumberBox number = new DarkNumberBox())
            using (Button ok = new Button())
            using (Button cancel = new Button())
            {
                f.Text = "CatLayer 설정 - 프로그램 자석"; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(430, 245); f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false; f.BackColor = UiPanel; f.ForeColor = UiText;
                title.Text = "프로그램 자석"; title.Font = new Font(f.Font, FontStyle.Bold); title.ForeColor = UiText; title.BackColor = Color.Transparent; title.SetBounds(18, 16, 390, 24); f.Controls.Add(title);
                help.Text = "자동 연결은 다른 프로그램 창에 가까이 놓았을 때 자동으로 붙습니다.\n안정성을 위해 기본값은 OFF입니다. 우클릭 '프로그램 자석...' 수동 연결은 항상 사용할 수 있습니다."; help.ForeColor = UiMuted; help.BackColor = Color.Transparent; help.SetBounds(18, 44, 400, 58); f.Controls.Add(help);

                autoEnable.Text = "자동 프로그램 자석 연결 사용";
                autoEnable.Checked = autoProgramMagnetEnabled;
                autoEnable.ForeColor = UiText; autoEnable.BackColor = Color.Transparent;
                autoEnable.SetBounds(18, 104, 260, 24); f.Controls.Add(autoEnable);

                Label distance = new Label(); distance.Text = "자동 연결 감지 거리"; distance.ForeColor = UiText; distance.BackColor = Color.Transparent; distance.SetBounds(18, 136, 160, 22); f.Controls.Add(distance);
                number.SetBounds(18, 160, 120, 28); number.Minimum = 0; number.Maximum = 100; number.Value = programMagnetSnapPixels; f.Controls.Add(number);
                Label px = new Label(); px.Text = "px"; px.ForeColor = UiText; px.BackColor = Color.Transparent; px.SetBounds(145, 164, 40, 22); f.Controls.Add(px);

                ok.Text = "적용"; ok.SetBounds(248, 199, 76, 30); StyleButton(ok, false); ok.DialogResult = DialogResult.OK; f.Controls.Add(ok);
                cancel.Text = "취소"; cancel.SetBounds(334, 199, 76, 30); StyleButton(cancel, false); cancel.DialogResult = DialogResult.Cancel; f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog(this) != DialogResult.OK) return;

                autoProgramMagnetEnabled = autoEnable.Checked;
                programMagnetSnapPixels = Math.Max(0, Math.Min(100, (int)number.Value));
                if (!autoProgramMagnetEnabled)
                {
                    // Automatic attachments from previous builds are unsafe to keep alive.
                    windowAttachments.Clear();
                    DetailedLog.Write("MAGNET_SAFETY", "automatic program magnet disabled; runtime attachments cleared");
                }
                SaveConfig();
                SetStatus(autoProgramMagnetEnabled ?
                    "자동 프로그램 자석: ON · " + programMagnetSnapPixels.ToString() + "px" :
                    "자동 프로그램 자석: OFF · 수동 연결만 사용");
            }
        }

        private void ShowResizeGraceSettings()
        {
            using (Form f = new Form())
            using (Label title = new Label())
            using (Label help = new Label())
            using (Label pxLabel = new Label())
            using (Label msLabel = new Label())
            using (DarkNumberBox px = new DarkNumberBox())
            using (DarkNumberBox ms = new DarkNumberBox())
            using (Button defaults = new Button())
            using (Button ok = new Button())
            using (Button cancel = new Button())
            {
                f.Text = "CatLayer 설정 - 크기 조절 판정";
                f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(470, 275); f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false;
                f.BackColor = UiPanel; f.ForeColor = UiText;
                title.Text = "크기 조절 코요테 타임"; title.Font = new Font(f.Font, FontStyle.Bold); title.ForeColor = UiText; title.BackColor = Color.Transparent; title.SetBounds(18, 16, 420, 24); f.Controls.Add(title);
                help.Text = "모서리/변에 마우스가 한 번 들어오면 잠시 넓은 범위에서도 같은 방향의 크기 조절을 인정합니다.\n기본값: 여유 30px / 시간 500ms";
                help.ForeColor = UiMuted; help.BackColor = Color.Transparent; help.SetBounds(18, 44, 430, 48); f.Controls.Add(help);
                pxLabel.Text = "여유 넓이"; pxLabel.ForeColor = UiText; pxLabel.BackColor = Color.Transparent; pxLabel.SetBounds(18, 108, 100, 24); f.Controls.Add(pxLabel);
                px.SetBounds(122, 104, 110, 28); px.Minimum = 10; px.Maximum = 80; px.Value = ResizeGracePixels; f.Controls.Add(px);
                Label pxUnit = new Label(); pxUnit.Text = "px"; pxUnit.ForeColor = UiText; pxUnit.BackColor = Color.Transparent; pxUnit.SetBounds(240, 108, 38, 24); f.Controls.Add(pxUnit);
                msLabel.Text = "유예 시간"; msLabel.ForeColor = UiText; msLabel.BackColor = Color.Transparent; msLabel.SetBounds(18, 150, 100, 24); f.Controls.Add(msLabel);
                ms.SetBounds(122, 146, 110, 28); ms.Minimum = 0; ms.Maximum = 3000; ms.Increment = 50; ms.Value = ResizeGraceMilliseconds; f.Controls.Add(ms);
                Label msUnit = new Label(); msUnit.Text = "ms"; msUnit.ForeColor = UiText; msUnit.BackColor = Color.Transparent; msUnit.SetBounds(240, 150, 38, 24); f.Controls.Add(msUnit);
                Label note = new Label(); note.Text = "0ms = 시간 유예 없음  |  기본 실제 가장자리 판정은 10px"; note.ForeColor = UiMuted; note.BackColor = Color.Transparent; note.SetBounds(18, 186, 420, 24); f.Controls.Add(note);
                defaults.Text = "기본값"; defaults.SetBounds(18, 226, 82, 30); StyleButton(defaults, false); f.Controls.Add(defaults);
                defaults.Click += delegate { px.Value = 30; ms.Value = 500; };
                ok.Text = "적용"; ok.SetBounds(292, 226, 72, 30); StyleButton(ok, true); ok.DialogResult = DialogResult.OK; f.Controls.Add(ok);
                cancel.Text = "취소"; cancel.SetBounds(374, 226, 72, 30); StyleButton(cancel, false); cancel.DialogResult = DialogResult.Cancel; f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog(this) != DialogResult.OK) return;
                resizeGracePixels = Math.Max(10, Math.Min(80, (int)px.Value));
                resizeGraceMs = Math.Max(0, Math.Min(3000, (int)ms.Value));
                SaveConfig();
                foreach (OverlayItemForm overlay in items) if (overlay != null && !overlay.IsDisposed) overlay.RefreshSelectionVisual();
                SetStatus("크기 조절 판정 여유: " + resizeGracePixels.ToString() + "px / " + resizeGraceMs.ToString() + "ms");
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
                f.Shown += delegate { sharedHotkeyCaptureSuspended = true; };
                f.FormClosed += delegate { sharedHotkeyCaptureSuspended = false; };

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
                    HotkeyText(hotkeyEditMods, hotkeyEditVk), HotkeyText(hotkeyHideMods, hotkeyHideVk), HotkeyText(hotkeyQuickShowMods, hotkeyQuickShowVk), HotkeyText(hotkeyAllHideMods, hotkeyAllHideVk), HotkeyText(hotkeyDetailMods, hotkeyDetailVk), HotkeyText(hotkeyCaptureMods, hotkeyCaptureVk),
                    HotkeyText(hotkeyGroupMods, hotkeyGroupVk), HotkeyText(hotkeyUngroupMods, hotkeyUngroupVk),
                    HotkeyText(hotkeyRotateMinus1Mods, hotkeyRotateMinus1Vk), HotkeyText(hotkeyRotatePlus1Mods, hotkeyRotatePlus1Vk),
                    HotkeyText(hotkeyRotateMinus10Mods, hotkeyRotateMinus10Vk), HotkeyText(hotkeyRotatePlus10Mods, hotkeyRotatePlus10Vk),
                    HotkeyText(hotkeyFlipHorizontalMods, hotkeyFlipHorizontalVk), HotkeyText(hotkeyFlipVerticalMods, hotkeyFlipVerticalVk),
                    HotkeyText(hotkeyResetRotationMods, hotkeyResetRotationVk), HotkeyText(hotkeyResetTransformMods, hotkeyResetTransformVk)
                };
                foreach (string name in existingNames) used.Add(name);
                if (hotkeyRemoteVk > 0) used.Add(HotkeyText(hotkeyRemoteMods, hotkeyRemoteVk));
                if (hotkeyWebReloadVk > 0) used.Add(HotkeyText(hotkeyWebReloadMods, hotkeyWebReloadVk));
                if (hotkeyPresetLoadVk > 0) used.Add(HotkeyText(hotkeyPresetLoadMods, hotkeyPresetLoadVk));
                if (hotkeyGroupLoadVk > 0) used.Add(HotkeyText(hotkeyGroupLoadMods, hotkeyGroupLoadVk));
                foreach (HotkeyBinding secondary in secondaryHotkeys.Values)
                    if (secondary != null && secondary.Vk > 0) used.Add(HotkeyText(secondary.Mods, secondary.Vk));

                for (int i = 0; i < presets.Count; i++)
                {
                    if (vkRefs[i][0] <= 0) continue;
                    if (IsReservedClipboardHotkey(modsRefs[i][0], vkRefs[i][0]))
                    {
                        MessageBox.Show(this, "Ctrl+C / Ctrl+V는 이미지 원본 복사와 이미지 붙여넣기 전용키로 예약되어 있습니다.\n\n프리셋: " + presets[i].Name,
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
                SetStatus(registered ? "프리셋 단축키를 적용했습니다. 키 입력은 다른 프로그램에도 전달됩니다." : "프리셋 단축키는 저장됐지만 전역 키 감지를 시작하지 못했습니다.");
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
            HideBeginnerQuickBar(true);
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
            string pending = ConsumePendingLaunchArgument();
            string shellImagePath;
            if (!string.IsNullOrWhiteSpace(pending) && TryGetShellOverlayPath(pending, out shellImagePath))
            {
                OpenLaunchFileFromShell(pending, true);
                return;
            }
            ShowFromTray();
            if (!string.IsNullOrWhiteSpace(pending) && OpenLaunchFileFromShell(pending, true)) return;
            SetStatus("이미 실행 중인 CatLayer 창을 불러왔습니다.");
        }

        private static void ConsiderPlacementSnap(int sourceCoordinate, int targetCoordinate, int sensitivity, ref int bestDistance, ref int snapDelta)
        {
            int d = targetCoordinate - sourceCoordinate;
            int ad = Math.Abs(d);
            if (ad <= sensitivity && ad < bestDistance) { bestDistance = ad; snapDelta = d; }
        }

        public Rectangle ApplyPlacementSnap(OverlayItemForm source, Rectangle candidate)
        {
            int sensitivity = placementSnapPixels;
            if (sensitivity <= 0 || source == null) return candidate;

            Rectangle sourceVisual = source.GetVisualContentBounds(candidate);
            int bestDx = sensitivity + 1, bestDy = sensitivity + 1;
            int snapDx = 0, snapDy = 0;
            int sx1 = sourceVisual.Left, sx2 = sourceVisual.Left + sourceVisual.Width / 2, sx3 = sourceVisual.Right;
            int sy1 = sourceVisual.Top, sy2 = sourceVisual.Top + sourceVisual.Height / 2, sy3 = sourceVisual.Bottom;

            Rectangle area = Screen.FromRectangle(sourceVisual).WorkingArea;
            int ax1 = area.Left, ax2 = area.Left + area.Width / 2, ax3 = area.Right;
            int ay1 = area.Top, ay2 = area.Top + area.Height / 2, ay3 = area.Bottom;
            ConsiderPlacementSnap(sx1, ax1, sensitivity, ref bestDx, ref snapDx); ConsiderPlacementSnap(sx1, ax2, sensitivity, ref bestDx, ref snapDx); ConsiderPlacementSnap(sx1, ax3, sensitivity, ref bestDx, ref snapDx);
            ConsiderPlacementSnap(sx2, ax1, sensitivity, ref bestDx, ref snapDx); ConsiderPlacementSnap(sx2, ax2, sensitivity, ref bestDx, ref snapDx); ConsiderPlacementSnap(sx2, ax3, sensitivity, ref bestDx, ref snapDx);
            ConsiderPlacementSnap(sx3, ax1, sensitivity, ref bestDx, ref snapDx); ConsiderPlacementSnap(sx3, ax2, sensitivity, ref bestDx, ref snapDx); ConsiderPlacementSnap(sx3, ax3, sensitivity, ref bestDx, ref snapDx);
            ConsiderPlacementSnap(sy1, ay1, sensitivity, ref bestDy, ref snapDy); ConsiderPlacementSnap(sy1, ay2, sensitivity, ref bestDy, ref snapDy); ConsiderPlacementSnap(sy1, ay3, sensitivity, ref bestDy, ref snapDy);
            ConsiderPlacementSnap(sy2, ay1, sensitivity, ref bestDy, ref snapDy); ConsiderPlacementSnap(sy2, ay2, sensitivity, ref bestDy, ref snapDy); ConsiderPlacementSnap(sy2, ay3, sensitivity, ref bestDy, ref snapDy);
            ConsiderPlacementSnap(sy3, ay1, sensitivity, ref bestDy, ref snapDy); ConsiderPlacementSnap(sy3, ay2, sensitivity, ref bestDy, ref snapDy); ConsiderPlacementSnap(sy3, ay3, sensitivity, ref bestDy, ref snapDy);

            foreach (OverlayItemForm other in items)
            {
                if (other == null || other.IsDisposed || !other.IsOverlayVisible || IsMoveLinkedSnapTarget(source, other)) continue;
                Rectangle b = other.GetVisualContentBounds(other.Bounds);
                int tx1 = b.Left, tx2 = b.Left + b.Width / 2, tx3 = b.Right;
                int ty1 = b.Top, ty2 = b.Top + b.Height / 2, ty3 = b.Bottom;
                ConsiderPlacementSnap(sx1, tx1, sensitivity, ref bestDx, ref snapDx); ConsiderPlacementSnap(sx1, tx2, sensitivity, ref bestDx, ref snapDx); ConsiderPlacementSnap(sx1, tx3, sensitivity, ref bestDx, ref snapDx);
                ConsiderPlacementSnap(sx2, tx1, sensitivity, ref bestDx, ref snapDx); ConsiderPlacementSnap(sx2, tx2, sensitivity, ref bestDx, ref snapDx); ConsiderPlacementSnap(sx2, tx3, sensitivity, ref bestDx, ref snapDx);
                ConsiderPlacementSnap(sx3, tx1, sensitivity, ref bestDx, ref snapDx); ConsiderPlacementSnap(sx3, tx2, sensitivity, ref bestDx, ref snapDx); ConsiderPlacementSnap(sx3, tx3, sensitivity, ref bestDx, ref snapDx);
                ConsiderPlacementSnap(sy1, ty1, sensitivity, ref bestDy, ref snapDy); ConsiderPlacementSnap(sy1, ty2, sensitivity, ref bestDy, ref snapDy); ConsiderPlacementSnap(sy1, ty3, sensitivity, ref bestDy, ref snapDy);
                ConsiderPlacementSnap(sy2, ty1, sensitivity, ref bestDy, ref snapDy); ConsiderPlacementSnap(sy2, ty2, sensitivity, ref bestDy, ref snapDy); ConsiderPlacementSnap(sy2, ty3, sensitivity, ref bestDy, ref snapDy);
                ConsiderPlacementSnap(sy3, ty1, sensitivity, ref bestDy, ref snapDy); ConsiderPlacementSnap(sy3, ty2, sensitivity, ref bestDy, ref snapDy); ConsiderPlacementSnap(sy3, ty3, sensitivity, ref bestDy, ref snapDy);
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

        private static readonly string[] ExplorerImageExtensions = new string[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
        private const string ExplorerOverlayVerbName = "CatLayerOverlay";
        private const string ShellOverlayArgumentPrefix = "--overlay-file=";

        private static string ExplorerOverlayRegistryPath(string extension)
        {
            return @"Software\Classes\SystemFileAssociations\" + extension + @"\shell\" + ExplorerOverlayVerbName;
        }

        private bool IsExplorerImageContextMenuEnabled()
        {
            try
            {
                foreach (string extension in ExplorerImageExtensions)
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ExplorerOverlayRegistryPath(extension), false))
                    {
                        if (key == null) return false;
                    }
                }
                return true;
            }
            catch { return false; }
        }

        private void SetExplorerImageContextMenuEnabled(bool enabled, bool showMessage)
        {
            try
            {
                string exe = Application.ExecutablePath;
                foreach (string extension in ExplorerImageExtensions)
                {
                    string path = ExplorerOverlayRegistryPath(extension);
                    if (!enabled)
                    {
                        try { Registry.CurrentUser.DeleteSubKeyTree(path, false); } catch { }
                        continue;
                    }

                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(path))
                    {
                        if (key == null) throw new InvalidOperationException("탐색기 메뉴 레지스트리를 만들지 못했습니다.");
                        key.SetValue("", "CatLayer로 띄우기", RegistryValueKind.String);
                        key.SetValue("Icon", exe + ",0", RegistryValueKind.String);
                    }
                    using (RegistryKey command = Registry.CurrentUser.CreateSubKey(path + @"\command"))
                    {
                        if (command == null) throw new InvalidOperationException("탐색기 명령 레지스트리를 만들지 못했습니다.");
                        command.SetValue("", "\"" + exe + "\" \"" + ShellOverlayArgumentPrefix + "%1\"", RegistryValueKind.String);
                    }
                }

                SetStatus(enabled ? "탐색기 우클릭 'CatLayer로 띄우기'를 켰습니다." : "탐색기 우클릭 'CatLayer로 띄우기'를 껐습니다.");
                if (showMessage)
                {
                    MessageBox.Show(this,
                        enabled
                            ? "이미지 파일 우클릭 메뉴에 'CatLayer로 띄우기'를 추가했습니다.\n\nWindows 11에서는 '더 많은 옵션 표시' 안에 보일 수 있습니다."
                            : "이미지 파일 우클릭 메뉴에서 'CatLayer로 띄우기'를 제거했습니다.",
                        "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "SetExplorerImageContextMenuEnabled");
                if (showMessage) MessageBox.Show(this, ex.Message, "탐색기 우클릭 메뉴 설정 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static bool TryGetShellOverlayPath(string argument, out string path)
        {
            path = "";
            if (string.IsNullOrWhiteSpace(argument) || !argument.StartsWith(ShellOverlayArgumentPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            path = argument.Substring(ShellOverlayArgumentPrefix.Length).Trim().Trim('"');
            return !string.IsNullOrWhiteSpace(path);
        }

        private bool AddImageOverlayFromShell(string path, bool captureUndo)
        {
            try
            {
                if (!IsSupportedImageDropFile(path)) return false;
                string managed = ImportImageAsset(path);
                if (string.IsNullOrEmpty(managed)) return false;
                Point cursor = Cursor.Position;
                Point insert = DefaultImageInsertPoint(cursor.X, cursor.Y);
                AddManagedImagesAt(new List<string> { managed }, new List<string> { SuggestedImageNameFromPath(path) }, insert,
                    "탐색기 이미지 추가", "CatLayer로 띄우기 완료");
                return true;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "AddImageOverlayFromShell");
                SetStatus("CatLayer로 띄우기 실패: " + ex.Message);
                return false;
            }
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

        private static bool IsPinterestPageUrl(string url)
        {
            try
            {
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
                string host = (uri.Host ?? "").Trim().ToLowerInvariant();
                return host == "pinterest.com" || host.EndsWith(".pinterest.com", StringComparison.Ordinal) ||
                       host == "pin.it" || host.EndsWith(".pin.it", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static bool IsPinimgUrl(string url)
        {
            try
            {
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
                string host = (uri.Host ?? "").Trim().ToLowerInvariant();
                return host == "pinimg.com" || host.EndsWith(".pinimg.com", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static string NormalizeEscapedWebUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            return value.Replace("\\u002F", "/").Replace("\\u002f", "/").Replace("\\/", "/");
        }

        private static void AddPinterestImageCandidate(List<string> urls, string value, string baseUrl)
        {
            if (urls == null || string.IsNullOrWhiteSpace(value)) return;
            string clean = NormalizeEscapedWebUrl(value).Trim().Trim('"', '\'');
            string normalized = NormalizeHttpUrlText(clean);
            if (string.IsNullOrEmpty(normalized) && !string.IsNullOrEmpty(baseUrl))
            {
                try
                {
                    Uri baseUri; Uri resolved;
                    if (Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri) && Uri.TryCreate(baseUri, clean, out resolved))
                        normalized = NormalizeHttpUrlText(resolved.AbsoluteUri);
                }
                catch { }
            }
            if (string.IsNullOrEmpty(normalized)) return;

            if (IsPinimgUrl(normalized))
            {
                try
                {
                    Uri uri = new Uri(normalized);
                    Match sized = Regex.Match(uri.AbsolutePath, @"^/(?:\d+x|originals)/(?<rest>.+)$", RegexOptions.IgnoreCase);
                    if (sized.Success && !uri.AbsolutePath.StartsWith("/originals/", StringComparison.OrdinalIgnoreCase))
                    {
                        UriBuilder builder = new UriBuilder(uri);
                        builder.Path = "/originals/" + sized.Groups["rest"].Value;
                        AddUniqueUrl(urls, builder.Uri.AbsoluteUri, null);
                    }
                }
                catch { }
            }
            AddUniqueUrl(urls, normalized, baseUrl);
        }

        private List<string> ResolvePinterestImageUrls(string pageUrl)
        {
            List<string> urls = new List<string>();
            PinterestDragLog.Append("Pinterest resolver start: " + (pageUrl ?? "(null)"));
            if (!IsPinterestPageUrl(pageUrl)) { PinterestDragLog.Append("Pinterest resolver skipped: not Pinterest URL"); return urls; }
            try
            {
                Uri uri;
                if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out uri)) return urls;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = "GET";
                request.AllowAutoRedirect = true;
                request.MaximumAutomaticRedirections = 5;
                request.Timeout = 6000;
                request.ReadWriteTimeout = 6000;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36";
                request.Accept = "text/html,application/xhtml+xml";
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream input = response.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    PinterestDragLog.Append("Pinterest HTTP status=" + ((int)response.StatusCode).ToString() + " " + response.StatusDescription + " contentType=" + (response.ContentType ?? "") + " final=" + (response.ResponseUri == null ? "" : response.ResponseUri.AbsoluteUri));
                    const int maxHtmlBytes = 2 * 1024 * 1024;
                    byte[] buffer = new byte[32768]; int total = 0, read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                        if (total > maxHtmlBytes) break;
                        ms.Write(buffer, 0, read);
                    }
                    string html = Encoding.UTF8.GetString(ms.ToArray());
                    string baseUrl = response.ResponseUri == null ? pageUrl : response.ResponseUri.AbsoluteUri;
                    string scan = NormalizeEscapedWebUrl(html);

                    MatchCollection metas = Regex.Matches(scan, "<meta\\b[^>]*>", RegexOptions.IgnoreCase);
                    foreach (Match meta in metas)
                    {
                        string tag = meta.Value;
                        Match key = Regex.Match(tag, "\\b(?:property|name)\\s*=\\s*[\\\"'](?<k>[^\\\"']+)[\\\"']", RegexOptions.IgnoreCase);
                        if (!key.Success) continue;
                        string k = key.Groups["k"].Value.Trim().ToLowerInvariant();
                        if (k != "og:image" && k != "og:image:url" && k != "twitter:image" && k != "twitter:image:src") continue;
                        Match content = Regex.Match(tag, "\\bcontent\\s*=\\s*[\\\"'](?<u>[^\\\"']+)[\\\"']", RegexOptions.IgnoreCase);
                        if (content.Success) AddPinterestImageCandidate(urls, content.Groups["u"].Value, baseUrl);
                    }

                    MatchCollection pinimgs = Regex.Matches(scan, "https?://(?:[^/\"'<>\\s]+\\.)?pinimg\\.com/[^\"'<>\\s\\\\]+", RegexOptions.IgnoreCase);
                    foreach (Match m in pinimgs) AddPinterestImageCandidate(urls, m.Value, baseUrl);
                }
            }
            catch (Exception ex) { PinterestDragLog.Append("Pinterest resolver exception: " + ex.GetType().Name + ": " + ex.Message); CrashLog.Write(ex, "ResolvePinterestImageUrls"); }
            PinterestDragLog.Append("Pinterest resolver candidates=" + urls.Count.ToString());
            for (int i = 0; i < urls.Count && i < 20; i++) PinterestDragLog.Append("  resolver[" + i.ToString() + "] " + urls[i]);
            return urls;
        }

        private string TryDownloadPinterestPageImage(string pageUrl)
        {
            List<string> candidates = ResolvePinterestImageUrls(pageUrl);
            int tried = 0;
            foreach (string candidate in candidates)
            {
                if (IsGifUrl(candidate)) continue;
                string managed = DownloadStaticWebImage(candidate);
                if (!string.IsNullOrEmpty(managed)) return managed;
                tried++;
                if (tried >= 10) break;
            }
            return null;
        }

        private static byte[] ReadDragFormatBytes(IDataObject data, string format, int maxBytes)
        {
            if (data == null || string.IsNullOrEmpty(format) || maxBytes <= 0) return null;
            try
            {
                bool present = false;
                try { present = data.GetDataPresent(format, false) || data.GetDataPresent(format, true); }
                catch { present = data.GetDataPresent(format); }
                if (!present) return null;

                object raw = null;
                try { raw = data.GetData(format, false); } catch { }
                if (raw == null) { try { raw = data.GetData(format, true); } catch { } }
                if (raw == null) { try { raw = data.GetData(format); } catch { } }

                byte[] direct = raw as byte[];
                if (direct != null)
                {
                    int count = Math.Min(direct.Length, maxBytes);
                    byte[] copy = new byte[count];
                    Buffer.BlockCopy(direct, 0, copy, 0, count);
                    return copy;
                }

                Stream stream = raw as Stream;
                if (stream != null)
                {
                    long oldPosition = 0; bool restore = false;
                    try { if (stream.CanSeek) { oldPosition = stream.Position; stream.Position = 0; restore = true; } } catch { }
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buffer = new byte[81920];
                        int total = 0;
                        while (total < maxBytes)
                        {
                            int want = Math.Min(buffer.Length, maxBytes - total);
                            int read = stream.Read(buffer, 0, want);
                            if (read <= 0) break;
                            ms.Write(buffer, 0, read);
                            total += read;
                        }
                        try { if (restore) stream.Position = oldPosition; } catch { }
                        return ms.ToArray();
                    }
                }
            }
            catch { }
            return null;
        }

        private static bool TryReadChromiumPickleString16(byte[] bytes, ref int pos, out string value)
        {
            value = null;
            if (bytes == null || pos < 0 || pos + 4 > bytes.Length) return false;
            int charCount = BitConverter.ToInt32(bytes, pos);
            pos += 4;
            if (charCount < 0 || charCount > 8 * 1024 * 1024) return false;
            long byteCountLong = (long)charCount * 2L;
            if (byteCountLong > int.MaxValue) return false;
            int byteCount = (int)byteCountLong;
            if (pos + byteCount > bytes.Length) return false;
            value = Encoding.Unicode.GetString(bytes, pos, byteCount);
            pos += byteCount;
            int aligned = (pos + 3) & ~3;
            if (aligned < pos || aligned > bytes.Length) return false;
            pos = aligned;
            return true;
        }

        private static void AddUrlsFromChromiumCustomValue(List<string> urls, string value)
        {
            if (urls == null || string.IsNullOrWhiteSpace(value)) return;
            string scan = NormalizeEscapedWebUrl(value);

            // Pinterest's Chromium drag payload exposes the close-up image here.
            Match preview = Regex.Match(scan, "\\\"previewImageUrl\\\"\\s*:\\s*\\\"(?<u>https?://[^\\\"]+)", RegexOptions.IgnoreCase);
            if (preview.Success) AddPinterestImageCandidate(urls, preview.Groups["u"].Value, null);

            // Keep a generic fallback for other Chromium custom MIME values.
            MatchCollection matches = Regex.Matches(scan, "https?://[^\\\"'<>\\s\\\\]+", RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                string candidate = match.Value;
                if (IsPinimgUrl(candidate)) AddPinterestImageCandidate(urls, candidate, null);
                else AddUniqueUrl(urls, candidate, null);
            }
        }

        private static void AddChromiumCustomMimeUrls(List<string> urls, IDataObject data)
        {
            const string format = "Chromium Web Custom MIME Data Format";
            byte[] bytes = ReadDragFormatBytes(data, format, 4 * 1024 * 1024);
            if (bytes == null || bytes.Length < 8) return;

            bool parsed = false;
            try
            {
                int payloadSize = BitConverter.ToInt32(bytes, 0);
                int headerSize = bytes.Length - payloadSize;
                if (payloadSize >= 0 && headerSize >= 4 && headerSize <= bytes.Length && (headerSize % 4) == 0)
                {
                    int pos = headerSize;
                    if (pos + 4 <= bytes.Length)
                    {
                        int count = BitConverter.ToInt32(bytes, pos);
                        pos += 4;
                        if (count >= 0 && count <= 4096)
                        {
                            parsed = true;
                            for (int i = 0; i < count; i++)
                            {
                                string key; string value;
                                if (!TryReadChromiumPickleString16(bytes, ref pos, out key) ||
                                    !TryReadChromiumPickleString16(bytes, ref pos, out value))
                                {
                                    parsed = false;
                                    break;
                                }
                                AddUrlsFromChromiumCustomValue(urls, key);
                                AddUrlsFromChromiumCustomValue(urls, value);
                            }
                        }
                    }
                }
            }
            catch { parsed = false; }

            // Fallback: even if Chromium changes the Pickle framing, URLs are UTF-16LE in
            // the current payload and can still be recovered from the raw buffer.
            if (!parsed || urls.Count == 0)
            {
                try { AddUrlsFromChromiumCustomValue(urls, Encoding.Unicode.GetString(bytes)); }
                catch { }
                try { AddUrlsFromChromiumCustomValue(urls, Encoding.UTF8.GetString(bytes)); }
                catch { }
            }
        }

        private static List<string> ExtractHttpImageUrls(IDataObject data)
        {
            List<string> urls = new List<string>();
            if (data == null) return urls;

            // Chrome/Edge Pinterest close-up drags do not expose CF_HTML/DownloadURL/Text.
            // They store a base::Pickle map in Chromium Web Custom MIME Data Format.
            // Parse it first so the pinimg preview/original candidate wins before page URLs.
            AddChromiumCustomMimeUrls(urls, data);

            string sourceUrl = null;
            try
            {
                string html = ReadDataText(data, DataFormats.Html);
                if (!string.IsNullOrEmpty(html))
                {
                    Match source = Regex.Match(html, "(?:^|[\\r\\n])SourceURL:(?<b>https?://[^\\r\\n]+)", RegexOptions.IgnoreCase);
                    if (source.Success) sourceUrl = NormalizeHttpUrlText(source.Groups["b"].Value.Trim());
                    string scanHtml = NormalizeEscapedWebUrl(html);

                    // IMG resources first. Pinterest close-up pins put the real image inside a
                    // draggable wrapper and provide the larger file in srcset. The main close-up
                    // IMG is processed before other images and srcset is sorted largest-first.
                    MatchCollection imgs = Regex.Matches(html, "<img\\b[^>]*>", RegexOptions.IgnoreCase);
                    List<string> mainTags = new List<string>();
                    List<string> otherTags = new List<string>();
                    foreach (Match img in imgs)
                    {
                        string tag = img.Value;
                        if (Regex.IsMatch(tag, "elementtiming\\s*=\\s*[\\\"']closeup-image-main", RegexOptions.IgnoreCase)) mainTags.Add(tag);
                        else otherTags.Add(tag);
                    }
                    List<string> orderedTags = new List<string>();
                    orderedTags.AddRange(mainTags);
                    orderedTags.AddRange(otherTags);
                    foreach (string tag in orderedTags)
                    {
                        Match srcset = Regex.Match(tag, "\\bsrcset\\s*=\\s*[\\\"'](?<u>[^\\\"']+)[\\\"']", RegexOptions.IgnoreCase);
                        if (srcset.Success)
                        {
                            List<KeyValuePair<double, string>> candidates = new List<KeyValuePair<double, string>>();
                            foreach (string part in srcset.Groups["u"].Value.Split(','))
                            {
                                string[] bits = part.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                                if (bits.Length == 0) continue;
                                double width = 0;
                                if (bits.Length > 1)
                                {
                                    string descriptor = bits[1].Trim();
                                    if (descriptor.EndsWith("w", StringComparison.OrdinalIgnoreCase))
                                        double.TryParse(descriptor.Substring(0, descriptor.Length - 1), out width);
                                }
                                candidates.Add(new KeyValuePair<double, string>(width, bits[0]));
                            }
                            candidates.Sort(delegate(KeyValuePair<double, string> a, KeyValuePair<double, string> b) { return b.Key.CompareTo(a.Key); });
                            foreach (KeyValuePair<double, string> candidate in candidates)
                            {
                                if (IsPinimgUrl(candidate.Value)) AddPinterestImageCandidate(urls, candidate.Value, sourceUrl);
                                else AddUniqueUrl(urls, candidate.Value, sourceUrl);
                            }
                        }

                        string[] attrs = new string[] { "src", "data-src", "data-original", "data-lazy-src", "data-image-url" };
                        foreach (string attr in attrs)
                        {
                            Match a = Regex.Match(tag, "\\b" + Regex.Escape(attr) + "\\s*=\\s*[\\\"'](?<u>[^\\\"']+)[\\\"']", RegexOptions.IgnoreCase);
                            if (!a.Success) continue;
                            if (IsPinimgUrl(a.Groups["u"].Value)) AddPinterestImageCandidate(urls, a.Groups["u"].Value, sourceUrl);
                            else AddUniqueUrl(urls, a.Groups["u"].Value, sourceUrl);
                        }
                    }

                    // CSS/JSON image candidates come after the explicit IMG resource so a page
                    // full of Pinterest thumbnails does not beat the close-up image.
                    MatchCollection cssImages = Regex.Matches(scanHtml, "(?:background(?:-image)?\\s*:\\s*)?url\\(\\s*[\\\"']?(?<u>https?://[^\\\"')\\s]+)", RegexOptions.IgnoreCase);
                    foreach (Match m in cssImages) AddPinterestImageCandidate(urls, m.Groups["u"].Value, sourceUrl);
                    MatchCollection pinimgUrls = Regex.Matches(scanHtml, "https?://(?:[^/\\\"'<>\\s]+\\.)?pinimg\\.com/[^\\\"'<>\\s\\\\]+", RegexOptions.IgnoreCase);
                    foreach (Match m in pinimgUrls) AddPinterestImageCandidate(urls, m.Value, sourceUrl);

                    // Some sites put the dragged image URL in meta/link attributes.
                    MatchCollection metas = Regex.Matches(html, "<(?:meta|link)\\b[^>]*(?:content|href)\\s*=\\s*[\\\"'](?<u>https?://[^\\\"']+)[\\\"'][^>]*>", RegexOptions.IgnoreCase);
                    foreach (Match m in metas) AddUniqueUrl(urls, m.Groups["u"].Value, sourceUrl);

                    // Pinterest can drag the wrapper as a link instead of the IMG. Keep anchor and
                    // SourceURL candidates late so image URLs win, but web-overlay fallback works.
                    MatchCollection anchors = Regex.Matches(html, "<a\\b[^>]*\\bhref\\s*=\\s*[\\\"'](?<u>https?://[^\\\"']+)[\\\"'][^>]*>", RegexOptions.IgnoreCase);
                    foreach (Match a in anchors) AddUniqueUrl(urls, a.Groups["u"].Value, sourceUrl);
                    AddUniqueUrl(urls, sourceUrl, null);
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

        private static string TruncateDragLogText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return "(empty)";
            string clean = value.Replace("\0", "").Replace("\r", "\\r").Replace("\n", "\\n");
            return clean.Length <= maxLength ? clean : clean.Substring(0, maxLength) + "...";
        }

        private static string DescribeDragDataObject(IDataObject data)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Formats: " + DescribeDataFormats(data));
            if (data == null) return sb.ToString();
            string[] inspect = new string[]
            {
                DataFormats.Html, "DownloadURL", "text/uri-list", "text/x-moz-url-data", "text/x-moz-url",
                "UniformResourceLocatorW", "UniformResourceLocator", DataFormats.UnicodeText, DataFormats.Text,
                "Chromium Web Custom MIME Data Format", "chromium/x-renderer-taint"
            };
            foreach (string format in inspect)
            {
                try
                {
                    bool present = data.GetDataPresent(format);
                    sb.Append(format).Append(" present=").Append(present ? "1" : "0");
                    if (present)
                    {
                        object raw = data.GetData(format);
                        sb.Append(" type=").Append(raw == null ? "null" : raw.GetType().FullName);
                        string value = ReadDataText(data, format);
                        if (!string.IsNullOrWhiteSpace(value)) sb.Append(" value=").Append(TruncateDragLogText(value, 2200));
                    }
                    sb.AppendLine();
                }
                catch (Exception ex) { sb.Append(format).Append(" read-error=").Append(ex.GetType().Name).Append(": ").Append(ex.Message).AppendLine(); }
            }
            try
            {
                if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = data.GetData(DataFormats.FileDrop) as string[];
                    sb.AppendLine("FileDrop: " + (files == null ? "null" : string.Join(" | ", files)));
                }
            }
            catch (Exception ex) { sb.AppendLine("FileDrop read-error: " + ex.Message); }
            try { sb.AppendLine("Bitmap present=" + (data.GetDataPresent(DataFormats.Bitmap) ? "1" : "0")); } catch { }
            try { sb.AppendLine("FileContents present=" + (data.GetDataPresent("FileContents") ? "1" : "0")); } catch { }
            try
            {
                List<string> candidates = ExtractHttpImageUrls(data);
                sb.AppendLine("ExtractHttpImageUrls count=" + candidates.Count.ToString());
                for (int i = 0; i < candidates.Count; i++) sb.AppendLine("  [" + i.ToString() + "] " + candidates[i]);
            }
            catch (Exception ex) { sb.AppendLine("ExtractHttpImageUrls error: " + ex); }
            return sb.ToString();
        }

        internal void HandleExternalOverlayDragEnter(DragEventArgs e)
        {
            ImageDropDragEnter(null, e);
        }

        internal void HandleExternalOverlayDragOver(DragEventArgs e)
        {
            ImageDropDragOver(null, e);
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
                root.DragOver += ImageDropDragOver;
                root.DragLeave += delegate { SetCompactDropFeedback(false); };
                root.DragDrop += ImageDropDragDrop;
            }
            catch { }
            foreach (Control child in root.Controls) EnableImageDropRecursive(child);
        }

        private void ImageDropDragEnter(object sender, DragEventArgs e)
        {
            if (object.ReferenceEquals(sender, overlayList) && IsOverlayListInternalDrag(e)) return;
            if (e == null) return;
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
                            if ((webOrigin ? IsStaticImageDropFile(path) : IsSupportedImageDropFile(path)))
                            {
                                e.Effect = DragDropEffects.Copy;
                                return;
                            }
                        }
                    }
                }
                if (e.Data.GetDataPresent(DataFormats.Bitmap))
                {
                    e.Effect = DragDropEffects.Copy;
                    return;
                }
                if (HasVirtualFileContents(e.Data))
                {
                    e.Effect = DragDropEffects.Copy;
                    return;
                }
                if (webOrigin) e.Effect = DragDropEffects.Copy;
            }
            catch
            {
                e.Effect = DragDropEffects.None;
            }
            finally
            {
                cachedExternalDragEffect = e.Effect;
                cachedExternalDragEffectUtcTicks = DateTime.UtcNow.Ticks;
                SetCompactDropFeedback(e.Effect != DragDropEffects.None);
            }
        }

        private void ImageDropDragOver(object sender, DragEventArgs e)
        {
            if (object.ReferenceEquals(sender, overlayList) && IsOverlayListInternalDrag(e)) return;
            if (e == null) return;
            long now = DateTime.UtcNow.Ticks;
            if (cachedExternalDragEffectUtcTicks != 0 && now - cachedExternalDragEffectUtcTicks <= 2L * TimeSpan.TicksPerSecond)
            {
                e.Effect = cachedExternalDragEffect;
                cachedExternalDragEffectUtcTicks = now;
                return;
            }
            // Defensive fallback. OLE normally guarantees DragEnter before DragOver.
            ImageDropDragEnter(sender, e);
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
                Size target = autoOptimizeImages ? ComputeOptimizedImageSize(source.Width, source.Height) : new Size(source.Width, source.Height);
                string dest = Path.Combine(assetsDir, "img_" + Guid.NewGuid().ToString("N") + ".png");
                using (Bitmap copy = new Bitmap(Math.Max(1, target.Width), Math.Max(1, target.Height), PixelFormat.Format32bppArgb))
                {
                    try { copy.SetResolution(source.HorizontalResolution, source.VerticalResolution); } catch { }
                    using (Graphics g = Graphics.FromImage(copy))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(source, new Rectangle(0, 0, copy.Width, copy.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
                    }
                    copy.Save(dest, ImageFormat.Png);
                }
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
            PinterestDragLog.Append("DownloadStaticWebImage start: " + (url ?? "(null)"));
            if (string.IsNullOrWhiteSpace(url)) { PinterestDragLog.Append("DownloadStaticWebImage reject: empty URL"); return null; }
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
                    PinterestDragLog.Append("Image HTTP status=" + ((int)response.StatusCode).ToString() + " " + response.StatusDescription + " contentType=" + contentType + " length=" + response.ContentLength.ToString() + " final=" + (response.ResponseUri == null ? "" : response.ResponseUri.AbsoluteUri));
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
                        PinterestDragLog.Append("Image decode bytes=" + rasterBytes.Length.ToString() + " extHint=" + extHint + " result=" + (string.IsNullOrEmpty(imported) ? "FAIL" : imported));
                        if (string.IsNullOrEmpty(imported)) SetStatus("웹 이미지 형식을 읽지 못했습니다.");
                        return imported;
                    }
                }
            }
            catch (Exception ex)
            {
                PinterestDragLog.Append("DownloadStaticWebImage exception: " + ex.GetType().Name + ": " + ex.Message);
                CrashLog.Write(ex, "DownloadStaticWebImage");
                SetStatus("웹 이미지 가져오기 실패: " + ex.Message);
                return null;
            }
        }

        private Task<string> DownloadStaticWebImageAsync(string url)
        {
            return Task.Run(delegate { return DownloadStaticWebImage(url); });
        }

        private Task<string> TryDownloadPinterestPageImageAsync(string pageUrl)
        {
            return Task.Run(delegate { return TryDownloadPinterestPageImage(pageUrl); });
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

        private async void ImageDropDragDrop(object sender, DragEventArgs e)
        {
            SetCompactDropFeedback(false);
            if (object.ReferenceEquals(sender, overlayList) && IsOverlayListInternalDrag(e)) return;
            try
            {
                cachedExternalDragEffectUtcTicks = 0;
                Point point = DefaultImageInsertPoint(e.X, e.Y);
                List<string> webUrls = ExtractHttpImageUrls(e.Data);
                bool webOrigin = webUrls.Count > 0;
                PinterestDragLog.Append("DragDrop point=" + point.X.ToString() + "," + point.Y.ToString() + " candidates=" + webUrls.Count.ToString());

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
                    if (IsGifUrl(webUrl)) { PinterestDragLog.Append("Candidate skipped GIF: " + webUrl); continue; }
                    tried++;
                    PinterestDragLog.Append("Candidate try #" + tried.ToString() + ": " + webUrl + " pinterestPage=" + (IsPinterestPageUrl(webUrl) ? "1" : "0") + " pinimg=" + (IsPinimgUrl(webUrl) ? "1" : "0"));
                    SetStatus("웹 이미지 가져오는 중...");
                    string managed = await DownloadStaticWebImageAsync(webUrl);
                    if (string.IsNullOrEmpty(managed) && IsPinterestPageUrl(webUrl))
                    {
                        PinterestDragLog.Append("Direct image failed; trying Pinterest page resolver: " + webUrl);
                        managed = await TryDownloadPinterestPageImageAsync(webUrl);
                    }
                    if (!string.IsNullOrEmpty(managed))
                    {
                        PinterestDragLog.Append("SUCCESS image overlay: " + managed);
                        AddManagedImagesAt(new List<string>(new string[] { managed }), new List<string>(new string[] { "웹 이미지" }), point, "웹 이미지 드래그 추가", "웹 이미지 드래그 추가 완료 (URL)");
                        return;
                    }
                    if (tried >= 8) break;
                }

                if (webUrls.Count > 0)
                {
                    string pageUrl = null;
                    foreach (string candidate in webUrls)
                    {
                        if (IsPinterestPageUrl(candidate)) { pageUrl = candidate; break; }
                    }
                    if (string.IsNullOrEmpty(pageUrl)) pageUrl = webUrls[0];
                    string normalized;
                    if (OverlayItemForm.TryNormalizeWebUrl(pageUrl, out normalized))
                    {
                        PinterestDragLog.Append("FALLBACK web overlay: " + normalized);
                        CaptureUndo("웹 주소 드래그 추가");
                        CreateItem(ItemType.Web, normalized, 0, new Rectangle(point.X, point.Y, 800, 520), 100, TimerMode.OneShot, "", false, true, ImageScaleMode.Fit, true, "웹 페이지");
                        SaveConfig(); SetStatus("드래그한 주소가 이미지가 아니어서 웹 오버레이로 추가했습니다.");
                        return;
                    }
                }
                PinterestDragLog.Append("FAIL unsupported payload. No image or web overlay created.");
                CrashLog.WriteText("WebImageDrag/Unsupported", "Formats: " + DescribeDataFormats(e.Data));
                if (webOrigin) SetStatus("드래그한 웹 주소를 이미지 또는 웹 페이지로 불러오지 못했습니다.");
                else SetStatus("드롭 데이터에서 이미지를 찾지 못했습니다. crash.log에 형식을 기록했습니다.");
            }
            catch (Exception ex) { PinterestDragLog.Append("DragDrop exception: " + ex); CrashLog.Write(ex, "ImageDropDragDrop"); SetStatus("드래그 이미지 추가 실패: " + ex.Message); }
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

        internal bool HandleUndoShortcut(Keys keyData)
        {
            if (keyData != (Keys.Control | Keys.Z)) return false;
            UndoLastAction();
            return true;
        }

        public bool HandlePasteShortcut(Keys keyData)
        {
            if (keyData != (Keys.Control | Keys.V)) return false;
            PasteImageFromClipboard();
            return true;
        }

        private async void PasteImageFromClipboard()
        {
            IDataObject data = GetClipboardDataObjectWithRetry();
            if (data == null) { SetStatus("클립보드를 읽지 못했습니다. 잠시 후 다시 시도하세요."); return; }
            try
            {
                Point point = DefaultImageInsertPoint(Cursor.Position.X, Cursor.Position.Y);
                try
                {
                    if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
                    {
                        string clipText = (data.GetData(DataFormats.UnicodeText) ?? data.GetData(DataFormats.Text) ?? "").ToString().Trim();
                        string normalizedPage;
                        if (TryNormalizeWebAddress(clipText, out normalizedPage) && !IsGifUrl(normalizedPage) && !Regex.IsMatch(normalizedPage, @"\.(png|jpe?g|webp|bmp)(?:[?#].*)?$", RegexOptions.IgnoreCase))
                        {
                            if (IsPinterestPageUrl(normalizedPage))
                            {
                                SetStatus("Pinterest 이미지 가져오는 중...");
                                string pinterestImage = await TryDownloadPinterestPageImageAsync(normalizedPage);
                                if (!string.IsNullOrEmpty(pinterestImage))
                                {
                                    AddManagedImagesAt(new List<string>(new string[] { pinterestImage }), new List<string>(new string[] { "Pinterest 이미지" }), point, "Pinterest 이미지 붙여넣기", "Pinterest 이미지를 일반 이미지 오버레이로 추가했습니다.");
                                    return;
                                }
                            }
                            AddWebOverlayDirect(normalizedPage, new Rectangle(point.X, point.Y, 800, 520));
                            SetStatus("Ctrl+V 링크를 웹 오버레이로 추가했습니다.");
                            return;
                        }
                    }
                }
                catch { }
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
                    SetStatus("웹 이미지 가져오는 중...");
                    string managed = await DownloadStaticWebImageAsync(webUrl);
                    if (string.IsNullOrEmpty(managed) && IsPinterestPageUrl(webUrl)) managed = await TryDownloadPinterestPageImageAsync(webUrl);
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

        private async void AddWebOverlayDirect(string url, Rectangle bounds)
        {
            string normalized;
            if (!TryNormalizeWebAddress(url, out normalized)) return;
            if (!IsGifUrl(normalized) && Regex.IsMatch(normalized, @"\.(png|jpe?g|webp|bmp)(?:[?#].*)?$", RegexOptions.IgnoreCase))
            {
                SetStatus("웹 이미지 가져오는 중...");
                string managed = await DownloadStaticWebImageAsync(normalized);
                if (!string.IsNullOrEmpty(managed))
                {
                    AddManagedImagesAt(new List<string>(new string[] { managed }), new List<string>(new string[] { "웹 이미지" }), new Point(bounds.Left, bounds.Top), "웹 이미지 주소 추가", "이미지 주소를 일반 이미지 오버레이로 추가했습니다.");
                    return;
                }
            }
            string name = "웹";
            try { name = "웹 · " + new Uri(normalized).Host; } catch { }
            CaptureUndo("웹 오버레이 추가");
            CreateItem(ItemType.Web, normalized, 0, bounds, 100, TimerMode.OneShot, "", false, false, ImageScaleMode.Fit, true, name);
            SaveConfig();
        }

        internal void ShowRemoteControlFromOverlay() { ShowRemoteControl(); }

        private void ShowRemoteControl()
        {
            if (remoteControlForm != null && !remoteControlForm.IsDisposed)
            {
                remoteControlForm.Show(); remoteControlForm.BringToFront(); return;
            }
            remoteControlForm = new RemoteControlForm(this);
            remoteControlForm.FormClosed += delegate { remoteControlForm = null; };
            remoteControlForm.Show(this);
        }

        internal void RemoteToggleEdit() { ToggleEdit(); }
        internal void RemoteCycleEditorMode() { CycleEditorMode(); }
        internal void RemoteToggleHidden() { ToggleHidden(); }
        internal void RemoteQuickHide() { QuickHideOverlay(); }
        internal void RemoteQuickShow() { QuickShowOverlay(); }
        internal void RemoteShowSelected()
        {
            OverlayItemForm target = quickHideTarget;
            quickHideTarget = null;
            if (target == null || target.IsDisposed) target = SelectedOverlay;
            if (target == null || target.IsDisposed) { SetStatus("표시할 오버레이를 리모컨 목록에서 선택하세요."); return; }
            if (target.IsOverlayVisible) { SetStatus("이미 표시 중인 오버레이입니다: " + DisplayName(target)); return; }
            CaptureUndo("선택 오버레이 표시");
            target.SetOverlayVisible(true);
            quickHiddenHistory.Remove(target);
            SaveConfigWithoutUiRefresh();
            RefreshMainUi();
            SetStatus("표시: " + DisplayName(target));
        }
        internal void RemoteUndoQuickHide()
        {
            OverlayItemForm target = MostRecentQuickHidden();
            if (target == null)
            {
                SetStatus("취소할 빠른 숨김 기록이 없습니다.");
                return;
            }
            CaptureUndo("빠른 숨김 취소");
            target.SetOverlayVisible(true);
            quickHiddenHistory.Remove(target);
            SaveConfigWithoutUiRefresh();
            RefreshMainUi();
            SetStatus("숨김 취소: " + DisplayName(target));
        }
        internal void RemoteLoadPreset() { LoadPresetInteractive(); }
        internal void RemoteLoadGroup() { LoadGroupInteractive(); }
        internal void RemoteReloadSelectedWeb()
        {
            OverlayItemForm web = SelectedOverlay;
            if (web == null || web.Type != ItemType.Web) { SetStatus("새로고침할 웹 오버레이를 먼저 선택하세요."); return; }
            web.ReloadWeb(); SetStatus("웹 새로고침");
        }

        internal OverlayItemForm RemoteSelectedOverlay { get { return SelectedOverlay; } }

        internal List<OverlayItemForm> RemoteOverlaySnapshot()
        {
            List<OverlayItemForm> result = new List<OverlayItemForm>(items.Count);
            for (int priority = 1; priority <= items.Count; priority++)
            {
                OverlayItemForm f = items[items.Count - priority];
                if (f != null && !f.IsDisposed) result.Add(f);
            }
            return result;
        }

        internal string RemoteOverlayDisplayText(OverlayItemForm f)
        {
            if (f == null) return "";
            string state = f.IsOverlayVisible ? "● " : "○ ";
            string group = f.GroupId > 0 ? "  [G" + f.GroupId.ToString() + "]" : "";
            return state + DisplayName(f) + group;
        }

        internal void RemoteSelectOverlay(OverlayItemForm f)
        {
            if (f == null || f.IsDisposed) return;
            MarkQuickHideTarget(f);
            SelectOverlayForEditing(f);
            RefreshOverlaySelectionVisuals();
        }

        private int SelectedGroupId()
        {
            List<OverlayItemForm> selected = SelectedOverlays;
            if (selected.Count == 0) return 0;
            int gid = selected[0].GroupId;
            if (gid <= 0) return 0;
            foreach (OverlayItemForm f in selected) if (f.GroupId != gid) return 0;
            return gid;
        }

        private void RenameSelectedGroupInteractive()
        {
            int gid = SelectedGroupId();
            if (gid <= 0) { SetStatus("이름을 바꿀 그룹 하나를 선택하세요."); return; }
            string oldName; if (!groupNames.TryGetValue(gid, out oldName) || string.IsNullOrWhiteSpace(oldName)) oldName = "그룹 " + gid.ToString();
            string name = UiPrompt.AskText(this, "그룹 이름 변경", "폴더 이름", oldName);
            if (name == null) return;
            name = name.Trim(); if (name.Length == 0) name = "그룹 " + gid.ToString();
            groupNames[gid] = name; SaveConfigWithoutUiRefresh(); RefreshMainUi(); SetStatus("그룹 이름 변경: " + name);
        }

        private void ToggleSelectedGroupCollapsed()
        {
            int gid = SelectedGroupId();
            if (gid <= 0) { SetStatus("접거나 펼칠 그룹 하나를 선택하세요."); return; }
            bool collapsed;
            if (collapsedGroups.Contains(gid)) { collapsedGroups.Remove(gid); collapsed = false; }
            else { collapsedGroups.Add(gid); collapsed = true; }
            SaveConfigWithoutUiRefresh(); RefreshMainUi(); SetStatus(collapsed ? "그룹 폴더 접음" : "그룹 폴더 펼침");
        }

        private sealed class WindowChoice
        {
            public IntPtr Hwnd;
            public string Title = "";
            public string ProcessName = "";
            public override string ToString() { return string.IsNullOrWhiteSpace(ProcessName) ? Title : ProcessName + " · " + Title; }
        }

        private List<WindowChoice> GetAttachableWindows()
        {
            List<WindowChoice> result = new List<WindowChoice>();
            int ownPid = Process.GetCurrentProcess().Id;
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr lp)
            {
                try
                {
                    if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return true;
                    StringBuilder title = new StringBuilder(512); Native.GetWindowText(hwnd, title, title.Capacity);
                    string t = title.ToString().Trim(); if (t.Length == 0) return true;
                    uint pid; Native.GetWindowThreadProcessId(hwnd, out pid); if (pid == ownPid || pid == 0) return true;
                    Process proc = Process.GetProcessById((int)pid);
                    WindowChoice c = new WindowChoice(); c.Hwnd = hwnd; c.Title = t; c.ProcessName = proc.ProcessName ?? ""; result.Add(c);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            result.Sort(delegate(WindowChoice a, WindowChoice b) { return string.Compare(a.ToString(), b.ToString(), StringComparison.CurrentCultureIgnoreCase); });
            return result;
        }

        private bool TryGetProgramWindowRect(IntPtr hwnd, out Native.RECT rect)
        {
            rect = new Native.RECT();
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
            try
            {
                Native.RECT dwmRect;
                if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out dwmRect, Marshal.SizeOf(typeof(Native.RECT))) == 0 &&
                    dwmRect.right > dwmRect.left && dwmRect.bottom > dwmRect.top)
                {
                    rect = dwmRect;
                    return true;
                }
            }
            catch { }
            return Native.GetWindowRect(hwnd, out rect);
        }

        private List<WindowChoice> GetAttachableWindowsCached()
        {
            if ((DateTime.UtcNow - attachableWindowCacheAt).TotalMilliseconds > 250 || attachableWindowCache == null)
            {
                attachableWindowCache = GetAttachableWindows();
                attachableWindowCacheAt = DateTime.UtcNow;
            }
            return attachableWindowCache;
        }

        private bool TryFindProgramSnap(OverlayItemForm overlay, Rectangle candidateBounds, int snapDistance,
            out WindowChoice best, out Native.RECT bestRect, out int bestSide, out Rectangle snappedBounds)
        {
            best = null; bestRect = new Native.RECT(); bestSide = -1; snappedBounds = candidateBounds;
            if (overlay == null || overlay.IsDisposed || snapDistance <= 0) return false;

            Rectangle visual = overlay.GetVisualContentBounds(candidateBounds);
            int bestScore = int.MaxValue;
            foreach (WindowChoice choice in GetAttachableWindowsCached())
            {
                Native.RECT nr;
                if (choice == null || !Native.IsWindow(choice.Hwnd) || !TryGetProgramWindowRect(choice.Hwnd, out nr)) continue;
                Rectangle wr = Rectangle.FromLTRB(nr.left, nr.top, nr.right, nr.bottom);
                if (wr.Width < 80 || wr.Height < 60) continue;

                int verticalOverlap = Math.Min(visual.Bottom, wr.Bottom) - Math.Max(visual.Top, wr.Top);
                int horizontalOverlap = Math.Min(visual.Right, wr.Right) - Math.Max(visual.Left, wr.Left);

                // Both outside and inside edges are valid magnet targets.
                // Outside: overlay sits just beyond the program edge.
                // Inside: matching overlay/program edges are aligned within the program window.
                if (verticalOverlap >= 6)
                {
                    int[] distances = new int[]
                    {
                        Math.Abs(visual.Right - wr.Left),  // 0 outside-left
                        Math.Abs(visual.Left - wr.Right),  // 1 outside-right
                        Math.Abs(visual.Left - wr.Left),   // 4 inside-left
                        Math.Abs(visual.Right - wr.Right)  // 5 inside-right
                    };
                    int[] sides = new int[] { 0, 1, 4, 5 };
                    for (int k = 0; k < distances.Length; k++)
                    {
                        int d = distances[k];
                        if (d <= snapDistance && d < bestScore) { best = choice; bestRect = nr; bestSide = sides[k]; bestScore = d; }
                    }
                }
                if (horizontalOverlap >= 6)
                {
                    int[] distances = new int[]
                    {
                        Math.Abs(visual.Bottom - wr.Top),  // 2 outside-above
                        Math.Abs(visual.Top - wr.Bottom),  // 3 outside-below
                        Math.Abs(visual.Top - wr.Top),     // 6 inside-top
                        Math.Abs(visual.Bottom - wr.Bottom)// 7 inside-bottom
                    };
                    int[] sides = new int[] { 2, 3, 6, 7 };
                    for (int k = 0; k < distances.Length; k++)
                    {
                        int d = distances[k];
                        if (d <= snapDistance && d < bestScore) { best = choice; bestRect = nr; bestSide = sides[k]; bestScore = d; }
                    }
                }
            }
            if (best == null) return false;

            Rectangle snappedVisual = overlay.GetVisualContentBounds(candidateBounds);
            int dx = 0, dy = 0;
            if (bestSide == 0) dx = bestRect.left - snappedVisual.Right;
            else if (bestSide == 1) dx = bestRect.right - snappedVisual.Left;
            else if (bestSide == 2) dy = bestRect.top - snappedVisual.Bottom;
            else if (bestSide == 3) dy = bestRect.bottom - snappedVisual.Top;
            else if (bestSide == 4) dx = bestRect.left - snappedVisual.Left;
            else if (bestSide == 5) dx = bestRect.right - snappedVisual.Right;
            else if (bestSide == 6) dy = bestRect.top - snappedVisual.Top;
            else if (bestSide == 7) dy = bestRect.bottom - snappedVisual.Bottom;
            snappedBounds = candidateBounds;
            snappedBounds.Offset(dx, dy);
            return true;
        }

        internal Rectangle ApplyProgramWindowSnap(OverlayItemForm overlay, Rectangle candidate)
        {
            if (!autoProgramMagnetEnabled || programMagnetSnapPixels <= 0) return candidate;
            WindowChoice best; Native.RECT rect; int side; Rectangle snapped;
            return TryFindProgramSnap(overlay, candidate, programMagnetSnapPixels, out best, out rect, out side, out snapped) ? snapped : candidate;
        }

        internal bool TryAutoAttachOverlayToNearbyProgram(OverlayItemForm overlay)
        {
            if (!autoProgramMagnetEnabled || programMagnetSnapPixels <= 0) return false;
            if (overlay == null || overlay.IsDisposed) return false;
            WindowChoice best; Native.RECT bestRect; int bestSide; Rectangle snapped;
            // Predictable magnet behavior: free movement while dragging, then snap/attach only
            // when the released overlay is genuinely close to an external window edge.
            if (!TryFindProgramSnap(overlay, overlay.Bounds, programMagnetSnapPixels, out best, out bestRect, out bestSide, out snapped))
                return false;

            int dx = snapped.Left - overlay.Left, dy = snapped.Top - overlay.Top;
            DetachProgramMagnetsForLinked(overlay);
            MoveLinkedOverlaysByDelta(overlay, dx, dy, false);

            Native.RECT r;
            if (!TryGetProgramWindowRect(best.Hwnd, out r)) r = bestRect;
            WindowAttachment a = new WindowAttachment();
            a.ProcessName = best.ProcessName;
            a.WindowTitle = best.Title;
            a.Hwnd = best.Hwnd;
            a.OffsetX = overlay.Left - r.left;
            a.OffsetY = overlay.Top - r.top;
            a.Side = bestSide;
            windowAttachments[overlay] = a;
            DetailedLog.Write("MAGNET_ATTACH",
                "auto id=" + DetailedLog.ShortId(overlay.ItemId) +
                " process=" + (best.ProcessName ?? "") +
                " side=" + bestSide.ToString() +
                " bounds=" + DetailedLog.Rect(overlay.Bounds) +
                " offset=" + a.OffsetX.ToString() + "," + a.OffsetY.ToString());
            SetStatus("프로그램 자석 연결 · " + best.Title);
            return true;
        }

        // User-initiated movement always starts free. This prevents an attached program window
        // tracker from fighting the mouse while the user is trying to reposition the overlay.
        internal void DetachOverlayFromProgramForDrag(OverlayItemForm overlay)
        {
            if (overlay == null) return;
            DetailedLog.Write("MAGNET_DETACH", "drag linked-set source=" + DiagnosticItemLabel(overlay));
            // Group peers and hierarchy descendants move as one linked set. Any member left
            // attached to another program window would fight the user's drag on the next tick.
            DetachProgramMagnetsForLinked(overlay);
        }

        internal void AttachOverlayToProgramInteractive(OverlayItemForm overlay)
        {
            if (overlay == null || overlay.IsDisposed) return;
            List<WindowChoice> windows = GetAttachableWindows();
            if (windows.Count == 0) { SetStatus("붙일 수 있는 프로그램 창이 없습니다."); return; }
            using (Form f = new Form())
            using (ListBox list = new ListBox())
            using (Button ok = new Button())
            using (Button cancel = new Button())
            {
                f.Text = "CatLayer - 프로그램 자석"; f.StartPosition = FormStartPosition.CenterParent; f.ClientSize = new Size(560, 410); f.BackColor = UiPanel; f.ForeColor = UiText;
                list.SetBounds(14, 14, 532, 338); list.BackColor = UiPanel2; list.ForeColor = UiText; foreach (WindowChoice w in windows) list.Items.Add(w); if (list.Items.Count > 0) list.SelectedIndex = 0; f.Controls.Add(list);
                ok.Text = "붙이기"; ok.DialogResult = DialogResult.OK; ok.SetBounds(374, 364, 82, 32); StyleButton(ok, false); f.Controls.Add(ok);
                cancel.Text = "취소"; cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(464, 364, 82, 32); StyleButton(cancel, false); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog(this) != DialogResult.OK || list.SelectedItem == null) return;
                WindowChoice choice = list.SelectedItem as WindowChoice; if (choice == null) return;
                Native.RECT r; if (!TryGetProgramWindowRect(choice.Hwnd, out r)) return;
                int left = r.left, top = r.top, right = r.right, bottom = r.bottom;
                Rectangle visual = overlay.GetVisualContentBounds(overlay.Bounds);
                int[] distances = new int[]
                {
                    Math.Abs(visual.Right - left), Math.Abs(visual.Left - right), Math.Abs(visual.Bottom - top), Math.Abs(visual.Top - bottom),
                    Math.Abs(visual.Left - left), Math.Abs(visual.Right - right), Math.Abs(visual.Top - top), Math.Abs(visual.Bottom - bottom)
                };
                int attachSide = 0, nearest = distances[0];
                for (int k = 1; k < distances.Length; k++) if (distances[k] < nearest) { nearest = distances[k]; attachSide = k; }
                int nx = overlay.Left, ny = overlay.Top;
                if (attachSide == 0) nx += left - visual.Right;
                else if (attachSide == 1) nx += right - visual.Left;
                else if (attachSide == 2) ny += top - visual.Bottom;
                else if (attachSide == 3) ny += bottom - visual.Top;
                else if (attachSide == 4) nx += left - visual.Left;
                else if (attachSide == 5) nx += right - visual.Right;
                else if (attachSide == 6) ny += top - visual.Top;
                else if (attachSide == 7) ny += bottom - visual.Bottom;
                int attachDx = nx - overlay.Left, attachDy = ny - overlay.Top;
                DetachProgramMagnetsForLinked(overlay);
                MoveLinkedOverlaysByDelta(overlay, attachDx, attachDy, false);
                WindowAttachment a = new WindowAttachment(); a.ProcessName = choice.ProcessName; a.WindowTitle = choice.Title; a.Hwnd = choice.Hwnd; a.OffsetX = overlay.Left - r.left; a.OffsetY = overlay.Top - r.top; a.Side = attachSide;
                windowAttachments[overlay] = a;
                DetailedLog.Write("MAGNET_ATTACH",
                    "manual id=" + DetailedLog.ShortId(overlay.ItemId) +
                    " process=" + (choice.ProcessName ?? "") +
                    " side=" + attachSide.ToString() +
                    " bounds=" + DetailedLog.Rect(overlay.Bounds) +
                    " offset=" + a.OffsetX.ToString() + "," + a.OffsetY.ToString());
                SaveConfig();
                SetStatus("프로그램 자석 연결: 창에 붙임 + 이동 따라가기 · " + choice.Title);
            }
        }

        private void AttachSelectedOverlayToProgramInteractive()
        {
            OverlayItemForm overlay = SelectedOverlay;
            if (overlay == null) { SetStatus("프로그램에 붙일 오버레이를 선택하세요."); return; }
            AttachOverlayToProgramInteractive(overlay);
        }

        internal void DetachOverlayFromProgram(OverlayItemForm overlay)
        {
            if (overlay == null) return;
            bool removed = windowAttachments.Remove(overlay);
            DetailedLog.Write("MAGNET_DETACH",
                "manual id=" + DetailedLog.ShortId(overlay.ItemId) +
                " removed=" + removed.ToString() +
                " bounds=" + DetailedLog.Rect(overlay.Bounds));
            if (removed) SetStatus("프로그램 자석을 해제했습니다.");
            else SetStatus("이 오버레이는 프로그램 자석에 연결되어 있지 않습니다.");
        }

        private void DetachSelectedOverlayFromProgram() { DetachOverlayFromProgram(SelectedOverlay); }

        private IntPtr ResolveAttachedWindow(WindowAttachment a)
        {
            if (a == null || a.Hwnd == IntPtr.Zero) return IntPtr.Zero;
            if (!Native.IsWindow(a.Hwnd)) return IntPtr.Zero;

            // HWND is only trusted while it still belongs to the same process that was explicitly
            // attached. Do not fall back to "any explorer/chrome window" when it dies.
            try
            {
                uint pid;
                Native.GetWindowThreadProcessId(a.Hwnd, out pid);
                if (pid == 0) return IntPtr.Zero;
                Process proc = Process.GetProcessById((int)pid);
                if (!string.Equals(proc.ProcessName ?? "", a.ProcessName ?? "", StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }

            return a.Hwnd;
        }

        private void UpdateAttachedWindows()
        {
            if (windowAttachments.Count == 0) return;
            staleAttachmentKeys.Clear();
            HashSet<OverlayItemForm> movedThisTick = new HashSet<OverlayItemForm>();
            foreach (KeyValuePair<OverlayItemForm, WindowAttachment> pair in windowAttachments)
            {
                OverlayItemForm overlay = pair.Key; WindowAttachment a = pair.Value;
                if (overlay == null || overlay.IsDisposed) { if (overlay != null) staleAttachmentKeys.Add(overlay); continue; }
                if (overlay.IsDraggingForEdit) continue;
                IntPtr hwnd = ResolveAttachedWindow(a);
                if (hwnd == IntPtr.Zero)
                {
                    DetailedLog.Write("MAGNET_SAFETY",
                        "detached stale window id=" + DetailedLog.ShortId(overlay.ItemId) +
                        " process=" + (a.ProcessName ?? ""));
                    staleAttachmentKeys.Add(overlay);
                    continue;
                }
                if (Native.IsIconic(hwnd)) continue;
                Native.RECT r; if (!TryGetProgramWindowRect(hwnd, out r)) continue;
                int nx = r.left + a.OffsetX, ny = r.top + a.OffsetY;
                if (a.Side >= 0 && a.Side <= 7)
                {
                    Rectangle visual = overlay.GetVisualContentBounds(overlay.Bounds);
                    int visualLeftOffset = visual.Left - overlay.Left;
                    int visualTopOffset = visual.Top - overlay.Top;
                    int visualRightOffset = visual.Right - overlay.Left;
                    int visualBottomOffset = visual.Bottom - overlay.Top;
                    if (a.Side == 0) { nx = r.left - visualRightOffset; ny = r.top + a.OffsetY; }
                    else if (a.Side == 1) { nx = r.right - visualLeftOffset; ny = r.top + a.OffsetY; }
                    else if (a.Side == 2) { nx = r.left + a.OffsetX; ny = r.top - visualBottomOffset; }
                    else if (a.Side == 3) { nx = r.left + a.OffsetX; ny = r.bottom - visualTopOffset; }
                    else if (a.Side == 4) { nx = r.left - visualLeftOffset; ny = r.top + a.OffsetY; }
                    else if (a.Side == 5) { nx = r.right - visualRightOffset; ny = r.top + a.OffsetY; }
                    else if (a.Side == 6) { nx = r.left + a.OffsetX; ny = r.top - visualTopOffset; }
                    else if (a.Side == 7) { nx = r.left + a.OffsetX; ny = r.bottom - visualBottomOffset; }
                }

                int dx = nx - overlay.Left, dy = ny - overlay.Top;
                if (dx == 0 && dy == 0) continue;
                DetailedLog.WriteThrottled("MAGNET:" + (overlay.ItemId ?? ""), 150, "MAGNET",
                    "source=" + DiagnosticItemLabel(overlay) +
                    " process=" + (a.ProcessName ?? "") +
                    " side=" + a.Side.ToString() +
                    " target=" + nx.ToString() + "," + ny.ToString() +
                    " delta=" + dx.ToString() + "," + dy.ToString());
                foreach (OverlayItemForm member in GetMoveLinkedMembers(overlay))
                {
                    if (member == null || member.IsDisposed || member.IsDraggingForEdit || member.Locked) continue;
                    // An independently attached member owns its own program tracking.
                    if (!object.ReferenceEquals(member, overlay) && windowAttachments.ContainsKey(member)) continue;
                    if (!movedThisTick.Add(member)) continue;
                    Point before = member.Location;
                    Point after = object.ReferenceEquals(member, overlay) ? new Point(nx, ny) : new Point(member.Left + dx, member.Top + dy);
                    DetailedLog.WriteThrottled("MAGNET_MEMBER:" + (member.ItemId ?? ""), 150, "MAGNET",
                        "  member=" + DetailedLog.ShortId(member.ItemId) +
                        " " + before.X.ToString() + "," + before.Y.ToString() +
                        " -> " + after.X.ToString() + "," + after.Y.ToString());
                    member.Location = after;
                }
            }
            for (int i = 0; i < staleAttachmentKeys.Count; i++) windowAttachments.Remove(staleAttachmentKeys[i]);
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
            SaveConfigWithoutUiRefresh();
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

        private void CreateItem(ItemType type, string data, int seconds, Rectangle bounds, int opacity, TimerMode timerMode, string alarmPath, bool locked, bool preserveAspect, ImageScaleMode scaleMode, bool visible, string customName, int rotationDegrees, bool flipHorizontal, bool flipVertical, int groupId, int cropLeft, int cropTop, int cropRight, int cropBottom, int webZoomPercent = 100, string webCustomCss = "", int rotationBaseWidth = 0, int rotationBaseHeight = 0, bool alwaysOnTop = true)
        {
            OverlayItemForm f = new OverlayItemForm(this, type, data, seconds, opacity, timerMode, alarmPath, customName);
            f.Bounds = NormalizeBounds(bounds);
            f.SetLocked(locked, false);
            f.SetPreserveAspect(preserveAspect, false);
            f.SetImageScaleMode(scaleMode, false);
            f.SetCrop(cropLeft, cropTop, cropRight, cropBottom, false);
            if (type == ItemType.Image) f.SetRotationBaseSize(rotationBaseWidth > 0 ? rotationBaseWidth : f.Width, rotationBaseHeight > 0 ? rotationBaseHeight : f.Height);
            if (type == ItemType.Web) { f.SetWebZoomPercent(webZoomPercent, false); f.SetWebCustomCss(webCustomCss, false); }
            f.SetTransform(rotationDegrees, flipHorizontal, flipVertical, false);
            f.NormalizeFitBoundsToVisualContent();
            if (type == ItemType.Image && rotationDegrees == 0) f.SetRotationBaseSize(f.Width, f.Height);
            f.SetGroupId(groupId, false);
            f.SetAlwaysOnTop(alwaysOnTop, false);
            if (groupId >= nextGroupId) nextGroupId = groupId + 1;
            items.Add(f);
            f.Show();
            f.SetEditMode(EditMode && !(IntegratedMode && type == ItemType.Web));
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
            NormalizeHierarchy();
            if (oldType == ItemType.Image) TryDeleteUnusedManagedAsset(oldData);
            if (oldType == ItemType.Timer) TryDeleteUnusedManagedSound(oldAlarm);
            ApplyZOrder();
            SaveConfig();
            SetStatus("오버레이 삭제 완료  |  Ctrl+Z로 복구 가능");
            ShowBeginnerToast("오버레이를 삭제했습니다.", "되돌리기", delegate { UndoLastAction(); });
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
            // items[0] = back, last item = front.
            // TopMost OFF overlays stay in the normal window band; TopMost ON overlays
            // stay in the topmost band. Priority is preserved inside each band.
            for (int i = 0; i < items.Count; i++)
            {
                OverlayItemForm f = items[i];
                if (!f.IsHandleCreated || f.IsDisposed) continue;
                Native.SetWindowPos(f.Handle, f.AlwaysOnTop ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            for (int i = 0; i < items.Count; i++)
            {
                OverlayItemForm f = items[i];
                if (f == null || f.IsDisposed) continue;
                f.BringSelectionFrameToFront();
            }
        }

        private static Size ComputeOptimizedImageSize(int width, int height)
        {
            if (width <= 0 || height <= 0) return Size.Empty;
            double scale = 1.0;
            int maxSide = Math.Max(width, height);
            if (maxSide > AutoOptimizeMaxDimension) scale = Math.Min(scale, AutoOptimizeMaxDimension / (double)maxSide);
            long pixels = (long)width * (long)height;
            if (pixels > AutoOptimizeMaxPixels) scale = Math.Min(scale, Math.Sqrt(AutoOptimizeMaxPixels / (double)pixels));
            if (scale >= 0.999) return new Size(width, height);
            return new Size(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        }

        private static void SaveJpegWithQuality(Image image, string path, long quality)
        {
            ImageCodecInfo encoder = null;
            foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
                if (codec.FormatID == ImageFormat.Jpeg.Guid) { encoder = codec; break; }
            if (encoder == null) { image.Save(path, ImageFormat.Jpeg); return; }
            using (EncoderParameters parameters = new EncoderParameters(1))
            {
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Max(50L, Math.Min(100L, quality)));
                image.Save(path, encoder, parameters);
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
                string ext = (Path.GetExtension(sourcePath) ?? "").ToLowerInvariant();

                // Never flatten/resize GIF here: preserving animation is more important than file-size optimization.
                if (ext == ".gif")
                {
                    string gifDest = Path.Combine(assetsDir, "img_" + Guid.NewGuid().ToString("N") + ".gif");
                    File.Copy(sourcePath, gifDest, false);
                    return gifDest;
                }

                // With optimization disabled, preserve ordinary image bytes exactly and avoid
                // an unnecessary decode/re-encode pass. WebP still needs CatLayer's compatibility conversion.
                if (!autoOptimizeImages && ext != ".webp")
                {
                    string copyExt = string.IsNullOrEmpty(ext) || ext.Length > 10 ? ".img" : ext;
                    string directDest = Path.Combine(assetsDir, "img_" + Guid.NewGuid().ToString("N") + copyExt);
                    File.Copy(sourcePath, directDest, false);
                    return directDest;
                }

                using (Image probe = OverlayItemForm.LoadRasterImageFile(sourcePath))
                {
                    if (probe == null)
                    {
                        if (ext == ".webp")
                            MessageBox.Show(this, "이 WebP 파일을 Windows 이미지 디코더가 읽지 못했습니다.\nWindows의 WebP 이미지 지원을 확인해주세요.", "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        else
                            MessageBox.Show(this, "이미지 파일을 읽지 못했습니다.", "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return null;
                    }

                    bool animated = false;
                    try { animated = ImageAnimator.CanAnimate(probe); } catch { }
                    if (animated)
                    {
                        string animExt = string.IsNullOrEmpty(ext) || ext.Length > 10 ? ".gif" : ext;
                        string animDest = Path.Combine(assetsDir, "img_" + Guid.NewGuid().ToString("N") + animExt);
                        File.Copy(sourcePath, animDest, false);
                        return animDest;
                    }

                    Size target = autoOptimizeImages ? ComputeOptimizedImageSize(probe.Width, probe.Height) : new Size(probe.Width, probe.Height);
                    bool resize = target.Width != probe.Width || target.Height != probe.Height;
                    bool convertWebp = ext == ".webp";
                    if (!resize && !convertWebp)
                    {
                        if (string.IsNullOrEmpty(ext) || ext.Length > 10) ext = ".img";
                        string copyDest = Path.Combine(assetsDir, "img_" + Guid.NewGuid().ToString("N") + ext);
                        File.Copy(sourcePath, copyDest, false);
                        return copyDest;
                    }

                    bool jpeg = ext == ".jpg" || ext == ".jpeg";
                    string outExt = jpeg ? ".jpg" : ".png";
                    string dest = Path.Combine(assetsDir, "img_" + Guid.NewGuid().ToString("N") + outExt);
                    using (Bitmap optimized = new Bitmap(Math.Max(1, target.Width), Math.Max(1, target.Height), PixelFormat.Format32bppArgb))
                    {
                        try { optimized.SetResolution(probe.HorizontalResolution, probe.VerticalResolution); } catch { }
                        using (Graphics g = Graphics.FromImage(optimized))
                        {
                            g.CompositingMode = CompositingMode.SourceCopy;
                            g.CompositingQuality = CompositingQuality.HighQuality;
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = SmoothingMode.HighQuality;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(probe, new Rectangle(0, 0, optimized.Width, optimized.Height), 0, 0, probe.Width, probe.Height, GraphicsUnit.Pixel);
                        }
                        if (jpeg) SaveJpegWithQuality(optimized, dest, 90L);
                        else optimized.Save(dest, ImageFormat.Png);
                    }
                    return dest;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "이미지 보관/최적화에 실패했습니다.\n\n" + ex.Message, "CatLayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            if (IsSoundReferencedByUndo(path)) return;
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
            if (IsAssetReferencedByUndo(path)) return;
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
            lines.Add("CATLAYER_GROUP_V2");
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
                    f.WebZoomPercent.ToString() + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(f.WebCustomCss ?? "")) + "|" +
                    f.RotationBaseWidth.ToString() + "|" + f.RotationBaseHeight.ToString() + "|" +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(f.ItemId ?? "")) + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(f.ParentItemId ?? "")) + "|" +
                    (f.AlwaysOnTop ? "1" : "0"));
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
                bool groupV2 = lines.Length >= 2 && lines[0].Trim() == "CATLAYER_GROUP_V2";
                if (lines.Length < 2 || (!groupV2 && lines[0].Trim() != "CATLAYER_GROUP_V1")) throw new InvalidDataException("지원하지 않는 그룹 파일입니다.");

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
                groupNames[newGroupId] = string.IsNullOrWhiteSpace(groupName) ? ("그룹 " + newGroupId.ToString()) : groupName;
                bool obsExists = false;
                foreach (OverlayItemForm existing in items) if (existing.Type == ItemType.ObsProgram) { obsExists = true; break; }
                int loaded = 0, skippedObs = 0;
                List<OverlayItemForm> createdItems = new List<OverlayItemForm>();
                Dictionary<string, string> hierarchyIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<OverlayItemForm, string> pendingHierarchyParents = new Dictionary<OverlayItemForm, string>();

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
                    int rotationBaseWidth = 0, rotationBaseHeight = 0;
                    if (ip.Length > 26) { int.TryParse(ip[25], out rotationBaseWidth); int.TryParse(ip[26], out rotationBaseHeight); }
                    string oldItemId = "", oldParentItemId = "";
                    bool alwaysOnTop = true;
                    if (groupV2 && ip.Length > 28)
                    {
                        try { oldItemId = Encoding.UTF8.GetString(Convert.FromBase64String(ip[27])); } catch { }
                        try { oldParentItemId = Encoding.UTF8.GetString(Convert.FromBase64String(ip[28])); } catch { }
                    }
                    if (groupV2 && ip.Length > 29) alwaysOnTop = ip[29] != "0";

                    Rectangle bounds = new Rectangle(targetOriginX + rx, targetOriginY + ry, Math.Max(100, w), Math.Max(60, h));
                    CreateItem(type, data, sec, bounds, Math.Max(0, Math.Min(100, opacity)), timerMode, alarmPath ?? "", locked, preserveAspect, scaleMode,
                        visible, customName, rotation, flipH, flipV, newGroupId, cropL, cropT, cropR, cropB, webZoom, webCss, rotationBaseWidth, rotationBaseHeight, alwaysOnTop);
                    OverlayItemForm created = items[items.Count - 1];
                    string newItemId = Guid.NewGuid().ToString("N");
                    created.SetHierarchyIdentity(newItemId, "");
                    if (!string.IsNullOrWhiteSpace(oldItemId)) hierarchyIdMap[oldItemId] = newItemId;
                    if (!string.IsNullOrWhiteSpace(oldParentItemId)) pendingHierarchyParents[created] = oldParentItemId;
                    createdItems.Add(created);
                    loaded++;
                }

                if (loaded == 0) throw new InvalidDataException("그룹 파일에서 생성할 수 있는 오버레이가 없습니다.");
                foreach (KeyValuePair<OverlayItemForm, string> pending in pendingHierarchyParents)
                {
                    string mappedParent;
                    if (hierarchyIdMap.TryGetValue(pending.Value, out mappedParent)) pending.Key.SetParentItemId(mappedParent, false);
                }
                NormalizeHierarchy();
                hidden = false;
                foreach (OverlayItemForm f in items) f.RefreshEffectiveVisibility();
                ApplyZOrder();
                SaveConfigWithoutUiRefresh();
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
            string shellImagePath;
            if (TryGetShellOverlayPath(path, out shellImagePath)) return AddImageOverlayFromShell(shellImagePath, captureUndo);
            if (IsSupportedImageDropFile(path)) return AddImageOverlayFromShell(path, captureUndo);
            if (IsGroupFile(path)) return LoadGroupFileAdditive(path, captureUndo, "그룹 파일 적용 완료: ");
            if (IsPresetFile(path)) return OpenPresetFileFromShell(path, captureUndo, "프리셋 적용 완료: ");
            if (IsWebPackageFile(path)) return AddCatLayerWebOverlay(path, null);
            if (IsLocalHtmlFile(path)) return AddLocalWebOverlay(path, null);
            return false;
        }

        public void ProcessStartupArgument(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string shellImagePath;
            shellOverlayStartup = TryGetShellOverlayPath(path, out shellImagePath);
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
                if (h != "LIGHTOVERLAY_PRESET_V1" && h != "LIGHTOVERLAY_PRESET_V2" && h != "LIGHTOVERLAY_PRESET_V3" && h != "LIGHTOVERLAY_PRESET_V4" && h != "LIGHTOVERLAY_PRESET_V5" && h != "LIGHTOVERLAY_PRESET_V6" && h != "LIGHTOVERLAY_PRESET_V7" && h != "LIGHTOVERLAY_PRESET_V8")
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

        internal void LoadPresetInteractive()
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
            lines.Add("LIGHTOVERLAY_PRESET_V8");
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
                    f.WebZoomPercent.ToString() + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(f.WebCustomCss ?? "")) + "|" +
                    f.RotationBaseWidth.ToString() + "|" + f.RotationBaseHeight.ToString() + "|" +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(f.ItemId ?? "")) + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(f.ParentItemId ?? "")) + "|" +
                    (f.AlwaysOnTop ? "1" : "0"));
            }
            File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
        }

        private string LoadPresetFile(string path)
        {
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length < 2) throw new InvalidDataException("지원하지 않는 프리셋 파일입니다.");
            string header = lines[0].Trim();
            bool presetV8 = header == "LIGHTOVERLAY_PRESET_V8";
            bool presetV7 = header == "LIGHTOVERLAY_PRESET_V7" || presetV8;
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
                int rotationBaseWidth = 0, rotationBaseHeight = 0;
                string itemId = "", parentItemId = "";
                bool alwaysOnTop = true;
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
                if (p.Length > 27) { int.TryParse(p[26], out rotationBaseWidth); int.TryParse(p[27], out rotationBaseHeight); }
                if (presetV8 && p.Length > 29)
                {
                    try { itemId = Encoding.UTF8.GetString(Convert.FromBase64String(p[28])); } catch { }
                    try { parentItemId = Encoding.UTF8.GetString(Convert.FromBase64String(p[29])); } catch { }
                }
                if (presetV8 && p.Length > 30) alwaysOnTop = p[30] != "0";

                CreateItem(type, data, sec, new Rectangle(x, y, Math.Max(100, w), Math.Max(60, h)),
                    Math.Max(0, Math.Min(100, opacity)), timerMode, alarmPath ?? "", locked, preserveAspect, scaleMode, visible, customName, rotationDegrees, flipHorizontal, flipVertical, groupId,
                    cropLeft, cropTop, cropRight, cropBottom, webZoomPercent, webCustomCss, rotationBaseWidth, rotationBaseHeight, alwaysOnTop);
                if (items.Count > 0) items[items.Count - 1].SetHierarchyIdentity(itemId, parentItemId);
            }

            NormalizeHierarchy();
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
            windowAttachments.Clear(); groupNames.Clear(); collapsedGroups.Clear();
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
                EditorMode persistentMode = WebControlMode ? modeBeforeWebControl : CurrentEditorMode;
                if (persistentMode == EditorMode.WebControl) persistentMode = EditorMode.Normal;
                if (persistentMode == EditorMode.Integrated && webInteractionStyle != WebInteractionStyle.Integrated) persistentMode = EditorMode.Normal;
                if (persistentMode == EditorMode.Normal && webInteractionStyle == WebInteractionStyle.Integrated) persistentMode = EditorMode.Integrated;
                List<string> lines = new List<string>(50 + presetHotkeys.Count + items.Count);
                lines.Add("EDIT=" + (persistentMode == EditorMode.Fixed ? "0" : "1"));
                lines.Add("EDITOR_MODE=" + ((int)persistentMode).ToString());
                lines.Add("HOTKEY_EDIT=" + hotkeyEditVk.ToString());
                lines.Add("HOTKEY_EDIT_MOD=" + hotkeyEditMods.ToString());
                lines.Add("HOTKEY_HIDE=" + hotkeyHideVk.ToString());
                lines.Add("HOTKEY_HIDE_MOD=" + hotkeyHideMods.ToString());
                lines.Add("HOTKEY_ALL_HIDE=" + hotkeyAllHideVk.ToString());
                lines.Add("HOTKEY_ALL_HIDE_MOD=" + hotkeyAllHideMods.ToString());
                lines.Add("HOTKEY_DETAIL=" + hotkeyDetailVk.ToString());
                lines.Add("HOTKEY_DETAIL_MOD=" + hotkeyDetailMods.ToString());
                lines.Add("HOTKEY_CAPTURE=" + hotkeyCaptureVk.ToString());
                lines.Add("HOTKEY_CAPTURE_MOD=" + hotkeyCaptureMods.ToString());
                lines.Add("HOTKEY_QUICK_SHOW=" + hotkeyQuickShowVk.ToString());
                lines.Add("HOTKEY_QUICK_SHOW_MOD=" + hotkeyQuickShowMods.ToString());
                lines.Add("HOTKEY_REMOTE=" + hotkeyRemoteVk.ToString());
                lines.Add("HOTKEY_REMOTE_MOD=" + hotkeyRemoteMods.ToString());
                lines.Add("HOTKEY_WEB_RELOAD=" + hotkeyWebReloadVk.ToString());
                lines.Add("HOTKEY_WEB_RELOAD_MOD=" + hotkeyWebReloadMods.ToString());
                lines.Add("HOTKEY_PRESET_LOAD=" + hotkeyPresetLoadVk.ToString());
                lines.Add("HOTKEY_PRESET_LOAD_MOD=" + hotkeyPresetLoadMods.ToString());
                lines.Add("HOTKEY_GROUP_LOAD=" + hotkeyGroupLoadVk.ToString());
                lines.Add("HOTKEY_GROUP_LOAD_MOD=" + hotkeyGroupLoadMods.ToString());
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
                foreach (string action in CoreHotkeyActionKeys)
                {
                    HotkeyBinding secondary = GetSecondaryHotkey(action);
                    if (secondary != null && secondary.Vk > 0) lines.Add("HOTKEY2=" + action + "|" + secondary.Mods.ToString() + "|" + secondary.Vk.ToString());
                }
                lines.Add("ZOOM_STEP=" + zoomStepPercent.ToString());
                lines.Add("ROTATION_SNAP=" + rotationSnapDegrees.ToString());
                lines.Add("PLACEMENT_SNAP=" + placementSnapPixels.ToString());
                lines.Add("PROGRAM_MAGNET_SNAP=" + programMagnetSnapPixels.ToString());
                lines.Add("PROGRAM_MAGNET_AUTO=" + (autoProgramMagnetEnabled ? "1" : "0"));
                lines.Add("RESIZE_GRACE_PIXELS=" + resizeGracePixels.ToString());
                lines.Add("RESIZE_GRACE_MS=" + resizeGraceMs.ToString());
                lines.Add("AUTO_OPTIMIZE_IMAGES=" + (autoOptimizeImages ? "1" : "0"));
                lines.Add("BEGINNER_HELP=" + (beginnerHelpEnabled ? "1" : "0"));
                lines.Add("AUTO_UPDATE_SUPPRESS=" + (suppressAutomaticUpdatePrompt ? "1" : "0"));
                lines.Add("WEB_INTERACTION=" + ((int)webInteractionStyle).ToString());
                foreach (KeyValuePair<int, string> gn in groupNames) if (gn.Key > 0) lines.Add("GROUP_NAME=" + gn.Key.ToString() + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(gn.Value ?? "")));
                foreach (int gid in collapsedGroups) if (gid > 0) lines.Add("GROUP_COLLAPSED=" + gid.ToString());
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
                    WindowAttachment attachment; bool hasAttachment = windowAttachments.TryGetValue(f, out attachment) && attachment != null;
                    string attachProcess64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(hasAttachment ? (attachment.ProcessName ?? "") : ""));
                    string attachTitle64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(hasAttachment ? (attachment.WindowTitle ?? "") : ""));
                    lines.Add(((int)f.Type).ToString() + "|" + b64 + "|" + f.DurationSeconds + "|" + r.X + "|" + r.Y + "|" +
                        r.Width + "|" + r.Height + "|" + f.OpacityPercent + "|" + ((int)f.TimerKind).ToString() + "|" + alarm64 + "|" + (f.Locked ? "1" : "0") + "|" + (f.PreserveAspect ? "1" : "0") + "|" + ((int)f.ScaleMode).ToString() + "|" +
                        (f.IsOverlayVisible ? "1" : "0") + "|" + customName64 + "|" + f.RotationDegrees.ToString() + "|" +
                        (f.FlipHorizontal ? "1" : "0") + "|" + (f.FlipVertical ? "1" : "0") + "|" + f.GroupId.ToString() + "|" +
                        f.CropLeft.ToString() + "|" + f.CropTop.ToString() + "|" + f.CropRight.ToString() + "|" + f.CropBottom.ToString() + "|" +
                        f.WebZoomPercent.ToString() + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(f.WebCustomCss ?? "")) + "|" +
                        (hasAttachment ? "1" : "0") + "|" + attachProcess64 + "|" + attachTitle64 + "|" +
                        (hasAttachment ? attachment.OffsetX.ToString() : "0") + "|" + (hasAttachment ? attachment.OffsetY.ToString() : "0") + "|" +
                        (hasAttachment ? attachment.Side.ToString() : "-1") + "|" + f.RotationBaseWidth.ToString() + "|" + f.RotationBaseHeight.ToString() + "|" +
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(f.ItemId ?? "")) + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(f.ParentItemId ?? "")) + "|" +
                        (f.AlwaysOnTop ? "1" : "0"));
                }
                // SaveConfig is called from many UI actions. When state did not change, avoid
                // another temporary file + replace cycle while preserving the old UI refresh side effect.
                string configSignature = string.Join("\n", lines.ToArray());
                bool configUnchanged = File.Exists(configPath) && string.Equals(lastSavedConfigSignature, configSignature, StringComparison.Ordinal);
                if (!configUnchanged)
                {
                    DetailedLog.Write("CONFIG", "write config items=" + items.Count.ToString() +
                        " main=" + ClientSize.Width.ToString() + "x" + ClientSize.Height.ToString() +
                        " hidden=" + hidden.ToString());
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
                if (mainUiReady && !syncingMainUi && !suppressSaveConfigUiRefresh && !configUnchanged) RefreshMainUi();
            }
            catch { }
        }

        private void SaveConfigWithoutUiRefresh()
        {
            bool previousSuppress = suppressSaveConfigUiRefresh;
            suppressSaveConfigUiRefresh = true;
            try { SaveConfig(); }
            finally { suppressSaveConfigUiRefresh = previousSuppress; }
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
            hotkeyAllHideVk = Native.VK_F11;
            hotkeyDetailVk = Native.VK_F10;
            hotkeyRemoteVk = 0; hotkeyWebReloadVk = 0; hotkeyPresetLoadVk = 0; hotkeyGroupLoadVk = 0;
            hotkeyAllHideMods = 0;
            hotkeyDetailMods = 0;
            hotkeyRemoteMods = 0; hotkeyWebReloadMods = 0; hotkeyPresetLoadMods = 0; hotkeyGroupLoadMods = 0;
            secondaryHotkeys.Clear();
            ApplyRecommendedQuickHotkeyDefaults();
            ResetEditActionHotkeysToDefaults();
            zoomStepPercent = 10;
            rotationSnapDegrees = 5;
            placementSnapPixels = 8;
            programMagnetSnapPixels = 36;
            autoProgramMagnetEnabled = false;
            autoOptimizeImages = true;
            presetHotkeys.Clear();
            groupNames.Clear(); collapsedGroups.Clear(); webInteractionStyle = WebInteractionStyle.DoubleClick; windowAttachments.Clear();
            IntegratedMode = false; WebControlMode = false; loadedEditorMode = EditorMode.Normal;
            currentPresetName = "";
            if (!suppressConfigMainWindowSizeApply) ClientSize = mainBaseClientSize;
        }

        private void LoadConfigWithSessionRecovery()
        {
            configRecoveredFromSession = false;
            DetailedLog.Write("RECOVERY", "LoadConfigWithSessionRecovery begin config=" + File.Exists(configPath).ToString() +
                " recovery=" + File.Exists(recoveryPath).ToString() +
                " marker=" + File.Exists(sessionMarkerPath).ToString());
            bool unclean = false;
            try { unclean = File.Exists(sessionMarkerPath) && File.Exists(recoveryPath); } catch { }
            if (unclean)
            {
                // A normal UI action writes config.txt immediately, while session_recovery.txt
                // is periodic. After a crash, prefer whichever valid snapshot is actually newer
                // so a recent edit is never overwritten by a 15-second-old recovery file.
                string first = recoveryPath, second = configPath;
                try
                {
                    if (File.Exists(configPath) && File.GetLastWriteTimeUtc(configPath) >= File.GetLastWriteTimeUtc(recoveryPath))
                    {
                        first = configPath; second = recoveryPath;
                    }
                }
                catch { }

                DetailedLog.Write("RECOVERY", "candidate first=" + Path.GetFileName(first) + " second=" + Path.GetFileName(second));
                bool loaded = false;
                Exception firstError = null;
                try
                {
                    LoadConfigFromFile(first);
                    loaded = true;
                    configRecoveredFromSession = true;
                }
                catch (Exception ex) { firstError = ex; }

                if (!loaded && !string.IsNullOrEmpty(second) && File.Exists(second))
                {
                    try
                    {
                        ResetConfigLoadState();
                        LoadConfigFromFile(second);
                        loaded = true;
                        configRecoveredFromSession = true;
                    }
                    catch (Exception ex)
                    {
                        CrashLog.Write(ex, "LoadConfig session recovery fallback");
                    }
                }
                if (!loaded)
                {
                    if (firstError != null) CrashLog.Write(firstError, "LoadConfig session recovery");
                    ResetConfigLoadState();
                    LoadConfig();
                }
            }
            else LoadConfig();
            try { File.WriteAllText(sessionMarkerPath, DateTime.Now.ToString("O"), new UTF8Encoding(false)); } catch { }
            WriteRecoverySnapshot();
        }

        private void WriteRecoverySnapshot()
        {
            try
            {
                bool previousSuppress = suppressSaveConfigUiRefresh;
                suppressSaveConfigUiRefresh = true;
                try { SaveConfig(); }
                finally { suppressSaveConfigUiRefresh = previousSuppress; }
                if (!File.Exists(configPath)) return;

                DateTime configWriteUtc = File.GetLastWriteTimeUtc(configPath);
                if (File.Exists(recoveryPath))
                {
                    DateTime recoveryWriteUtc = File.GetLastWriteTimeUtc(recoveryPath);
                    if (configWriteUtc <= recoveryWriteUtc) return;
                }

                string temp = recoveryPath + ".tmp";
                File.Copy(configPath, temp, true);
                if (File.Exists(recoveryPath)) File.Delete(recoveryPath);
                File.Move(temp, recoveryPath);
                DetailedLog.Write("RECOVERY", "snapshot copied config -> session_recovery.txt");
            }
            catch (Exception ex) { CrashLog.Write(ex, "WriteRecoverySnapshot"); }
        }

        private void FinishCleanSession()
        {
            try { recoveryTimer.Stop(); } catch { }
            try { SaveConfig(); } catch { }
            try { if (File.Exists(recoveryPath)) File.Delete(recoveryPath); } catch { }
            try { if (File.Exists(recoveryPath + ".tmp")) File.Delete(recoveryPath + ".tmp"); } catch { }
            try { if (File.Exists(sessionMarkerPath)) File.Delete(sessionMarkerPath); } catch { }
        }

        private void LoadConfig()
        {
            string backupPath = configPath + ".bak";
            if (!File.Exists(configPath) && !File.Exists(backupPath))
            {
                secondaryHotkeys.Clear();
                ApplyRecommendedQuickHotkeyDefaults();
                return;
            }

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
                    if (lines[i].StartsWith("EDIT=")) { int editValue = 1; int.TryParse(lines[i].Substring(5), out editValue); EditMode = editValue != 0; DetailEditMode = EditMode; loadedEditorMode = EditMode ? EditorMode.Normal : EditorMode.Fixed; start = i + 1; continue; }
                    if (lines[i].StartsWith("EDITOR_MODE="))
                    {
                        int modeValue;
                        if (int.TryParse(lines[i].Substring(12), out modeValue) && (modeValue == (int)EditorMode.Fixed || modeValue == (int)EditorMode.Normal || modeValue == (int)EditorMode.Integrated))
                        {
                            loadedEditorMode = (EditorMode)modeValue;
                            EditMode = loadedEditorMode != EditorMode.Fixed;
                            IntegratedMode = loadedEditorMode == EditorMode.Integrated;
                            DetailEditMode = EditMode;
                        }
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("HOTKEY_EDIT=")) { int.TryParse(lines[i].Substring(12), out hotkeyEditVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_EDIT_MOD=")) { int.TryParse(lines[i].Substring(16), out hotkeyEditMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_HIDE=")) { int.TryParse(lines[i].Substring(12), out hotkeyHideVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_HIDE_MOD=")) { int.TryParse(lines[i].Substring(16), out hotkeyHideMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_ALL_HIDE=")) { int.TryParse(lines[i].Substring(16), out hotkeyAllHideVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_ALL_HIDE_MOD=")) { int.TryParse(lines[i].Substring(20), out hotkeyAllHideMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_DETAIL=")) { int.TryParse(lines[i].Substring(14), out hotkeyDetailVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_DETAIL_MOD=")) { int.TryParse(lines[i].Substring(18), out hotkeyDetailMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_CAPTURE=")) { int.TryParse(lines[i].Substring(15), out hotkeyCaptureVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_CAPTURE_MOD=")) { int.TryParse(lines[i].Substring(19), out hotkeyCaptureMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_QUICK_SHOW=")) { int.TryParse(lines[i].Substring(18), out hotkeyQuickShowVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_QUICK_SHOW_MOD=")) { int.TryParse(lines[i].Substring(22), out hotkeyQuickShowMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_REMOTE=")) { int.TryParse(lines[i].Substring(14), out hotkeyRemoteVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_REMOTE_MOD=")) { int.TryParse(lines[i].Substring(18), out hotkeyRemoteMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_WEB_RELOAD=")) { int.TryParse(lines[i].Substring(18), out hotkeyWebReloadVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_WEB_RELOAD_MOD=")) { int.TryParse(lines[i].Substring(22), out hotkeyWebReloadMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_PRESET_LOAD=")) { int.TryParse(lines[i].Substring(19), out hotkeyPresetLoadVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_PRESET_LOAD_MOD=")) { int.TryParse(lines[i].Substring(23), out hotkeyPresetLoadMods); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_GROUP_LOAD=")) { int.TryParse(lines[i].Substring(18), out hotkeyGroupLoadVk); start = i + 1; continue; }
                    if (lines[i].StartsWith("HOTKEY_GROUP_LOAD_MOD=")) { int.TryParse(lines[i].Substring(22), out hotkeyGroupLoadMods); start = i + 1; continue; }
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
                    if (lines[i].StartsWith("HOTKEY2="))
                    {
                        try
                        {
                            string[] hp = lines[i].Substring(8).Split('|');
                            int hm, hv;
                            if (hp.Length == 3 && IsKnownHotkeyAction(hp[0]) && int.TryParse(hp[1], out hm) && int.TryParse(hp[2], out hv) && hv > 0)
                                secondaryHotkeys[hp[0]] = new HotkeyBinding(hm, hv);
                        }
                        catch { }
                        start = i + 1; continue;
                    }
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
                    if (lines[i].StartsWith("PROGRAM_MAGNET_SNAP="))
                    {
                        int parsedProgramMagnet; if (int.TryParse(lines[i].Substring(20), out parsedProgramMagnet)) programMagnetSnapPixels = Math.Max(0, Math.Min(100, parsedProgramMagnet));
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("PROGRAM_MAGNET_AUTO="))
                    {
                        autoProgramMagnetEnabled = lines[i].Substring(20).Trim() == "1";
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("RESIZE_GRACE_PIXELS="))
                    {
                        int parsedResizePixels; if (int.TryParse(lines[i].Substring(20), out parsedResizePixels)) resizeGracePixels = Math.Max(10, Math.Min(80, parsedResizePixels));
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("RESIZE_GRACE_MS="))
                    {
                        int parsedResizeMs; if (int.TryParse(lines[i].Substring(16), out parsedResizeMs)) resizeGraceMs = Math.Max(0, Math.Min(3000, parsedResizeMs));
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("AUTO_OPTIMIZE_IMAGES="))
                    {
                        autoOptimizeImages = lines[i].Substring(21) != "0";
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("BEGINNER_HELP="))
                    {
                        beginnerHelpEnabled = lines[i].Substring(14) != "0";
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("BEGINNER_INTRO_SEEN="))
                    {
                        // Legacy v1.2.1 beta key: first-run guide was removed in BETA5.
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("AUTO_UPDATE_SUPPRESS="))
                    {
                        int suppressValue = 0; int.TryParse(lines[i].Substring(21), out suppressValue);
                        suppressAutomaticUpdatePrompt = suppressValue != 0;
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("WEB_INTERACTION="))
                    {
                        int webStyle;
                        if (int.TryParse(lines[i].Substring(16), out webStyle) && webStyle >= 0 && webStyle <= 2) webInteractionStyle = (WebInteractionStyle)webStyle;
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("WEB_SINGLE_CLICK="))
                    {
                        webInteractionStyle = lines[i].Substring(17) != "0" ? WebInteractionStyle.SingleClick : WebInteractionStyle.DoubleClick;
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("GROUP_NAME="))
                    {
                        try { string[] gp = lines[i].Substring(11).Split('|'); int gid; if (gp.Length == 2 && int.TryParse(gp[0], out gid) && gid > 0) groupNames[gid] = Encoding.UTF8.GetString(Convert.FromBase64String(gp[1])); } catch { }
                        start = i + 1; continue;
                    }
                    if (lines[i].StartsWith("GROUP_COLLAPSED=")) { int gid; if (int.TryParse(lines[i].Substring(16), out gid) && gid > 0) collapsedGroups.Add(gid); start = i + 1; continue; }
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
                            if (!suppressConfigMainWindowSizeApply && sizeParts.Length == 2 && int.TryParse(sizeParts[0], out mw) && int.TryParse(sizeParts[1], out mh) && mw >= 250 && mh >= 350)
                                ClientSize = new Size(mw, mh);
                        }
                        catch { }
                        start = i + 1;
                        continue;
                    }
                    break;
                }
                if (webInteractionStyle == WebInteractionStyle.Integrated && loadedEditorMode == EditorMode.Normal) loadedEditorMode = EditorMode.Integrated;
                if (webInteractionStyle != WebInteractionStyle.Integrated && loadedEditorMode == EditorMode.Integrated) loadedEditorMode = EditorMode.Normal;
                if (hotkeyEditVk < 0 || hotkeyEditVk > 0xFE || (hotkeyEditVk > 0 && (IsModifierOnlyKey((Keys)hotkeyEditVk) || IsReservedClipboardHotkey(hotkeyEditMods, hotkeyEditVk)))) { hotkeyEditVk = (int)Keys.Q; hotkeyEditMods = Native.MOD_ALT; }
                if (hotkeyHideVk < 0 || hotkeyHideVk > 0xFE || (hotkeyHideVk > 0 && (IsModifierOnlyKey((Keys)hotkeyHideVk) || IsReservedClipboardHotkey(hotkeyHideMods, hotkeyHideVk)))) { hotkeyHideVk = (int)Keys.W; hotkeyHideMods = Native.MOD_ALT; }
                if (hotkeyAllHideVk < 0 || hotkeyAllHideVk > 0xFE || (hotkeyAllHideVk > 0 && (IsModifierOnlyKey((Keys)hotkeyAllHideVk) || IsReservedClipboardHotkey(hotkeyAllHideMods, hotkeyAllHideVk)))) { hotkeyAllHideVk = Native.VK_F11; hotkeyAllHideMods = 0; }
                if (hotkeyDetailVk < 0 || hotkeyDetailVk > 0xFE || (hotkeyDetailVk > 0 && (IsModifierOnlyKey((Keys)hotkeyDetailVk) || IsReservedClipboardHotkey(hotkeyDetailMods, hotkeyDetailVk)))) { hotkeyDetailVk = Native.VK_F10; hotkeyDetailMods = 0; }
                if (hotkeyCaptureVk < 0 || hotkeyCaptureVk > 0xFE || (hotkeyCaptureVk > 0 && (IsModifierOnlyKey((Keys)hotkeyCaptureVk) || IsReservedClipboardHotkey(hotkeyCaptureMods, hotkeyCaptureVk)))) { hotkeyCaptureVk = (int)Keys.E; hotkeyCaptureMods = Native.MOD_ALT; }
                // Migrate the old shipped screenshot default (F1) to the new game-friendly default.
                if (hotkeyCaptureMods == 0 && hotkeyCaptureVk == Native.VK_F1) { hotkeyCaptureVk = (int)Keys.E; hotkeyCaptureMods = Native.MOD_ALT; if (GetSecondaryHotkey("CAPTURE") == null) SetSecondaryHotkey("CAPTURE", 0, Native.VK_F7); }
                if (hotkeyQuickShowVk < 0 || hotkeyQuickShowVk > 0xFE || (hotkeyQuickShowVk > 0 && (IsModifierOnlyKey((Keys)hotkeyQuickShowVk) || IsReservedClipboardHotkey(hotkeyQuickShowMods, hotkeyQuickShowVk)))) { hotkeyQuickShowVk = (int)Keys.W; hotkeyQuickShowMods = Native.MOD_ALT | Native.MOD_SHIFT; }
                if (hotkeyRemoteVk < 0 || hotkeyRemoteVk > 0xFE || (hotkeyRemoteVk > 0 && (IsModifierOnlyKey((Keys)hotkeyRemoteVk) || IsReservedClipboardHotkey(hotkeyRemoteMods, hotkeyRemoteVk)))) { hotkeyRemoteVk = 0; hotkeyRemoteMods = 0; }
                if (hotkeyWebReloadVk < 0 || hotkeyWebReloadVk > 0xFE || (hotkeyWebReloadVk > 0 && (IsModifierOnlyKey((Keys)hotkeyWebReloadVk) || IsReservedClipboardHotkey(hotkeyWebReloadMods, hotkeyWebReloadVk)))) { hotkeyWebReloadVk = 0; hotkeyWebReloadMods = 0; }
                if (hotkeyPresetLoadVk < 0 || hotkeyPresetLoadVk > 0xFE || (hotkeyPresetLoadVk > 0 && (IsModifierOnlyKey((Keys)hotkeyPresetLoadVk) || IsReservedClipboardHotkey(hotkeyPresetLoadMods, hotkeyPresetLoadVk)))) { hotkeyPresetLoadVk = 0; hotkeyPresetLoadMods = 0; }
                if (hotkeyGroupLoadVk < 0 || hotkeyGroupLoadVk > 0xFE || (hotkeyGroupLoadVk > 0 && (IsModifierOnlyKey((Keys)hotkeyGroupLoadVk) || IsReservedClipboardHotkey(hotkeyGroupLoadMods, hotkeyGroupLoadVk)))) { hotkeyGroupLoadVk = 0; hotkeyGroupLoadMods = 0; }
                if (hotkeyGroupVk < 0 || hotkeyGroupVk > 0xFE || (hotkeyGroupVk > 0 && (IsModifierOnlyKey((Keys)hotkeyGroupVk) || IsReservedClipboardHotkey(hotkeyGroupMods, hotkeyGroupVk)))) { hotkeyGroupVk = (int)Keys.G; hotkeyGroupMods = Native.MOD_CONTROL; }
                if (hotkeyUngroupVk < 0 || hotkeyUngroupVk > 0xFE || (hotkeyUngroupVk > 0 && (IsModifierOnlyKey((Keys)hotkeyUngroupVk) || IsReservedClipboardHotkey(hotkeyUngroupMods, hotkeyUngroupVk)))) { hotkeyUngroupVk = (int)Keys.G; hotkeyUngroupMods = Native.MOD_CONTROL | Native.MOD_SHIFT; }
                if (hotkeyRotateMinus1Vk < 0 || hotkeyRotateMinus1Vk > 0xFE || (hotkeyRotateMinus1Vk > 0 && (IsModifierOnlyKey((Keys)hotkeyRotateMinus1Vk) || IsReservedClipboardHotkey(hotkeyRotateMinus1Mods, hotkeyRotateMinus1Vk)))) { hotkeyRotateMinus1Vk = (int)Keys.Q; hotkeyRotateMinus1Mods = 0; }
                if (hotkeyRotatePlus1Vk < 0 || hotkeyRotatePlus1Vk > 0xFE || (hotkeyRotatePlus1Vk > 0 && (IsModifierOnlyKey((Keys)hotkeyRotatePlus1Vk) || IsReservedClipboardHotkey(hotkeyRotatePlus1Mods, hotkeyRotatePlus1Vk)))) { hotkeyRotatePlus1Vk = (int)Keys.E; hotkeyRotatePlus1Mods = 0; }
                if (hotkeyRotateMinus10Vk < 0 || hotkeyRotateMinus10Vk > 0xFE || (hotkeyRotateMinus10Vk > 0 && (IsModifierOnlyKey((Keys)hotkeyRotateMinus10Vk) || IsReservedClipboardHotkey(hotkeyRotateMinus10Mods, hotkeyRotateMinus10Vk)))) { hotkeyRotateMinus10Vk = (int)Keys.Q; hotkeyRotateMinus10Mods = Native.MOD_SHIFT; }
                if (hotkeyRotatePlus10Vk < 0 || hotkeyRotatePlus10Vk > 0xFE || (hotkeyRotatePlus10Vk > 0 && (IsModifierOnlyKey((Keys)hotkeyRotatePlus10Vk) || IsReservedClipboardHotkey(hotkeyRotatePlus10Mods, hotkeyRotatePlus10Vk)))) { hotkeyRotatePlus10Vk = (int)Keys.E; hotkeyRotatePlus10Mods = Native.MOD_SHIFT; }
                if (hotkeyFlipHorizontalVk < 0 || hotkeyFlipHorizontalVk > 0xFE || (hotkeyFlipHorizontalVk > 0 && (IsModifierOnlyKey((Keys)hotkeyFlipHorizontalVk) || IsReservedClipboardHotkey(hotkeyFlipHorizontalMods, hotkeyFlipHorizontalVk)))) { hotkeyFlipHorizontalVk = (int)Keys.H; hotkeyFlipHorizontalMods = 0; }
                if (hotkeyFlipVerticalVk < 0 || hotkeyFlipVerticalVk > 0xFE || (hotkeyFlipVerticalVk > 0 && (IsModifierOnlyKey((Keys)hotkeyFlipVerticalVk) || IsReservedClipboardHotkey(hotkeyFlipVerticalMods, hotkeyFlipVerticalVk)))) { hotkeyFlipVerticalVk = (int)Keys.V; hotkeyFlipVerticalMods = 0; }
                if (hotkeyResetRotationVk < 0 || hotkeyResetRotationVk > 0xFE || (hotkeyResetRotationVk > 0 && (IsModifierOnlyKey((Keys)hotkeyResetRotationVk) || IsReservedClipboardHotkey(hotkeyResetRotationMods, hotkeyResetRotationVk)))) { hotkeyResetRotationVk = (int)Keys.R; hotkeyResetRotationMods = 0; }
                if (hotkeyResetTransformVk < 0 || hotkeyResetTransformVk > 0xFE || (hotkeyResetTransformVk > 0 && (IsModifierOnlyKey((Keys)hotkeyResetTransformVk) || IsReservedClipboardHotkey(hotkeyResetTransformMods, hotkeyResetTransformVk)))) { hotkeyResetTransformVk = (int)Keys.R; hotkeyResetTransformMods = Native.MOD_SHIFT; }
                zoomStepPercent = Math.Max(1, Math.Min(90, zoomStepPercent));
                rotationSnapDegrees = Math.Max(0, Math.Min(15, rotationSnapDegrees));
                placementSnapPixels = Math.Max(0, Math.Min(30, placementSnapPixels));
                programMagnetSnapPixels = Math.Max(0, Math.Min(100, programMagnetSnapPixels));
                resizeGracePixels = Math.Max(10, Math.Min(80, resizeGracePixels));
                resizeGraceMs = Math.Max(0, Math.Min(3000, resizeGraceMs));
                MigrateLegacyQuickHotkeyDefaults();
                RepairBetaQuickHotkeyDrift();
                NormalizeSecondaryHotkeys();
                RemoveMissingPresetHotkeys();
                for (int i = start; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    string[] p = lines[i].Split('|');
                    if (p.Length != 7 && p.Length != 8 && p.Length != 10 && p.Length != 13 && p.Length != 15 && p.Length != 19 && p.Length != 23 && p.Length != 25 && p.Length != 30 && p.Length != 31 && p.Length != 33 && p.Length != 35)
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
                    bool restoreAttachment = p.Length >= 30 && p[25] == "1";
                    string attachProcess = "", attachTitle = ""; int attachX = 0, attachY = 0, attachSide = -1;
                    int rotationBaseWidth = 0, rotationBaseHeight = 0;
                    if (p.Length >= 33) { int.TryParse(p[31], out rotationBaseWidth); int.TryParse(p[32], out rotationBaseHeight); }
                    string itemId = "", parentItemId = "";
                    bool alwaysOnTop = true;
                    if (p.Length >= 35)
                    {
                        try { itemId = Encoding.UTF8.GetString(Convert.FromBase64String(p[33])); } catch { }
                        try { parentItemId = Encoding.UTF8.GetString(Convert.FromBase64String(p[34])); } catch { }
                    }
                    if (p.Length >= 36) alwaysOnTop = p[35] != "0";
                    if (restoreAttachment)
                    {
                        try { attachProcess = Encoding.UTF8.GetString(Convert.FromBase64String(p[26])); } catch { }
                        try { attachTitle = Encoding.UTF8.GetString(Convert.FromBase64String(p[27])); } catch { }
                        int.TryParse(p[28], out attachX); int.TryParse(p[29], out attachY);
                        if (p.Length >= 31) int.TryParse(p[30], out attachSide);
                    }

                    CreateItem(type, data, sec, new Rectangle(x, y, Math.Max(100, w), Math.Max(60, h)),
                        opacity, timerMode, alarmPath, locked, preserveAspect, scaleMode, visible, customName, rotationDegrees, flipHorizontal, flipVertical, groupId,
                        cropLeft, cropTop, cropRight, cropBottom, webZoomPercent, webCustomCss, rotationBaseWidth, rotationBaseHeight, alwaysOnTop);
                    if (items.Count > 0) items[items.Count - 1].SetHierarchyIdentity(itemId, parentItemId);
                    if (restoreAttachment && items.Count > 0)
                    {
                        // TEST 13.3: attachments saved by older builds are deliberately not resumed.
                        // A process/title is not a stable Windows window identity; Explorer/Chrome can
                        // have multiple matching windows and previous builds could attach to the wrong one.
                        DetailedLog.Write("MAGNET_SAFETY",
                            "ignored persisted attachment id=" + DetailedLog.ShortId(items[items.Count - 1].ItemId) +
                            " process=" + (attachProcess ?? "") + " title=" + (attachTitle ?? ""));
                    }
                }
            NormalizeHierarchy();
            foreach (OverlayItemForm f in items) f.RefreshEffectiveVisibility();
            ApplyZOrder();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { Application.RemoveMessageFilter(this); } catch { }
            base.OnFormClosed(e);
        }
    }

    internal static class DetailedLog
    {
        // Release compatibility shim. Detailed runtime logging was removed in v1.2.0.
        public static readonly bool Enabled = false;
        public static void Write(string category, string message) { }
        public static void WriteThrottled(string key, int minimumMs, string category, string message) { }
        public static string ShortId(string value)
        {
            string s = value ?? "";
            return s.Length <= 8 ? s : s.Substring(0, 8);
        }
        public static string Rect(Rectangle r)
        {
            return r.X.ToString() + "," + r.Y.ToString() + "," +
                   r.Width.ToString() + "x" + r.Height.ToString();
        }
    }

    internal static class PinterestDragLog
    {
        // Release compatibility shim. Drag diagnostic file logging is disabled/removed.
        [Conditional("CATLAYER_DRAG_DIAGNOSTICS")]
        public static void Append(string text) { }
        [Conditional("CATLAYER_DRAG_DIAGNOSTICS")]
        public static void AppendUnique(string text) { }
        [Conditional("CATLAYER_DRAG_DIAGNOSTICS")]
        public static void BeginIfNew(IDataObject data) { }
        [Conditional("CATLAYER_DRAG_DIAGNOSTICS")]
        public static void EnsureFile() { }
    }

    internal static class CrashLog
    {
        // v1.2.0 release: file-based logging removed.
        public static void WriteText(string where, string text) { }
        public static void Write(Exception ex, string where) { }
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
