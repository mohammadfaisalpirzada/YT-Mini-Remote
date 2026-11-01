// =====================================================================
//  YT Mini Remote  -  The Master Sahib
//  Chhota floating controller jo Chrome par chalti YouTube ko Windows
//  media keys ke zariye control karta hai.
//  [grip] [prev] [play/pause] [next] [mute] [vol-] [vol+] [x]
//   - Grip pakar ke drag karein -> position set (khud yaad rehti hai)
//   - Idle par transparent, hover par dark
// =====================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Net;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("YT Mini Remote")]
[assembly: System.Reflection.AssemblyProduct("YT Mini Remote - The Master Sahib")]
[assembly: System.Reflection.AssemblyCompany("The Master Sahib")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]

static class Native
{
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);

    public static void Press(byte vk)
    {
        keybd_event(vk, 0, 1, UIntPtr.Zero);   // KEYEVENTF_EXTENDEDKEY (down)
        keybd_event(vk, 0, 3, UIntPtr.Zero);   // EXTENDEDKEY | KEYUP
    }
    public static void Drag(IntPtr h) { ReleaseCapture(); SendMessage(h, 0xA1, 2, 0); } // WM_NCLBUTTONDOWN, HTCAPTION

    // ---- Chrome windows ke titles parh kar YouTube dhoondna ----
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    public static void CloseChromeWindows()
    {
        var pids = new List<uint>();
        foreach (var pr in Process.GetProcessesByName("chrome")) { pids.Add((uint)pr.Id); pr.Dispose(); }
        var sb = new StringBuilder(512);
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (!pids.Contains(pid)) return true;
            sb.Length = 0;
            if (GetWindowText(h, sb, sb.Capacity) > 0) PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
            return true;
        }, IntPtr.Zero);
    }

    public static bool IsYouTubeWindow(IntPtr h)
    {
        var sb = new StringBuilder(512);
        return GetWindowText(h, sb, sb.Capacity) > 0 && sb.ToString().Contains("- YouTube");
    }

    // Chrome ki woh window jis ka active tab YouTube hai (na mile to koi bhi Chrome window)
    public static IntPtr FindYouTubeWindow()
    {
        var pids = new List<uint>();
        foreach (var pr in Process.GetProcessesByName("chrome")) { pids.Add((uint)pr.Id); pr.Dispose(); }
        IntPtr yt = IntPtr.Zero, any = IntPtr.Zero;
        var sb = new StringBuilder(512);
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (!pids.Contains(pid)) return true;
            sb.Length = 0;
            if (GetWindowText(h, sb, sb.Capacity) == 0) return true;
            string t = sb.ToString();
            if (t.Contains("- YouTube")) { yt = h; return false; }
            if (any == IntPtr.Zero && t.Contains("Chrome")) any = h;
            return true;
        }, IntPtr.Zero);
        return yt != IntPtr.Zero ? yt : any;
    }

    // chromeOpen = Chrome ki koi window khuli hai; youTube = kisi window ka active tab YouTube hai
    public static void Scan(out bool chromeOpen, out bool youTube)
    {
        var pids = new List<uint>();
        foreach (var pr in Process.GetProcessesByName("chrome")) { pids.Add((uint)pr.Id); pr.Dispose(); }

        bool open = false, yt = false;
        if (pids.Count > 0)
        {
            var sb = new StringBuilder(512);
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid)) return true;
                sb.Length = 0;
                if (GetWindowText(h, sb, sb.Capacity) == 0) return true;
                string t = sb.ToString();
                if (t.Contains("Chrome")) open = true;
                if (t.Contains("- YouTube")) { open = true; yt = true; }
                return true;
            }, IntPtr.Zero);
        }
        chromeOpen = open; youTube = yt;
    }
}

// ---------- Chrome ke media player se seedha baat (Windows 10 1809+) ----------
// Windows "Global Media Controls" ke zariye Chrome ka session dhoond kar
// play/pause/next/prev bhejta hai aur batata hai ke video chal rahi hai ya ruki hai.
static class Log
{
    static readonly object g = new object();
    static string file;
    public static void W(string msg)
    {
        try
        {
            lock (g)
            {
                if (file == null)
                {
                    string d = Environment.GetEnvironmentVariable("YTR_DIR");
                    if (string.IsNullOrEmpty(d)) d = AppDomain.CurrentDomain.BaseDirectory;
                    file = Path.Combine(d, "debug.txt");
                    File.WriteAllText(file, "YT Mini Remote debug  " + DateTime.Now + "\r\nOS: " + Environment.OSVersion + "  CLR: " + Environment.Version + "\r\n");
                }
                File.AppendAllText(file, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
        }
        catch { }
    }
}

static class Media
{
    static int lastCount = -1;
    static readonly object gate = new object();
    static object mgr;
    static bool broken;
    static MethodInfo asTask;

    static Type T(string n)
    {
        return Type.GetType("Windows.Media.Control." + n + ", Windows.Media.Control, ContentType=WindowsRuntime");
    }

    static object Await(object op, Type resultType)
    {
        if (asTask == null)
        {
            var asm = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
            foreach (var m in asm.GetType("System.WindowsRuntimeSystemExtensions").GetMethods())
                if (m.Name == "AsTask" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1") { asTask = m; break; }
        }
        var task = (Task)asTask.MakeGenericMethod(resultType).Invoke(null, new object[] { op });
        if (!task.Wait(2000)) return null;
        return task.GetType().GetProperty("Result").GetValue(task, null);
    }

    static object Session()
    {
        if (broken) return null;
        try
        {
            var tM = T("GlobalSystemMediaTransportControlsSessionManager");
            var tS = T("GlobalSystemMediaTransportControlsSession");
            if (tM == null || tS == null) { Log.W("WinRT types NOT found (tM=" + (tM != null) + ", tS=" + (tS != null) + ")"); broken = true; return null; }
            if (mgr == null) { mgr = Await(tM.GetMethod("RequestAsync").Invoke(null, null), tM); Log.W("Manager: " + (mgr != null)); }
            if (mgr == null) return null;
            var list = tM.GetMethod("GetSessions").Invoke(mgr, null) as System.Collections.IEnumerable;
            if (list == null) { Log.W("GetSessions returned null"); return null; }
            var ids = new List<string>(); object found = null;
            foreach (var se in list)
            {
                var id = tS.GetProperty("SourceAppUserModelId").GetValue(se, null) as string;
                ids.Add(id);
                if (found == null && id != null && id.IndexOf("chrome", StringComparison.OrdinalIgnoreCase) >= 0) found = se;
            }
            if (ids.Count != lastCount) { lastCount = ids.Count; Log.W("Sessions: [" + string.Join(" | ", ids.ToArray()) + "]  chromeFound=" + (found != null)); }
            return found;
        }
        catch (Exception ex) { Log.W("Session() ERROR: " + ex); broken = true; }
        return null;
    }

    // 1 = chal rahi hai, 0 = ruki hai, -1 = Chrome me koi media nahi / maloom nahi
    public static int Status()
    {
        lock (gate)
        {
            try
            {
                var se = Session();
                if (se == null) return -1;
                var info = T("GlobalSystemMediaTransportControlsSession").GetMethod("GetPlaybackInfo").Invoke(se, null);
                var st = T("GlobalSystemMediaTransportControlsSessionPlaybackInfo").GetProperty("PlaybackStatus").GetValue(info, null);
                return st != null && st.ToString() == "Playing" ? 1 : 0;
            }
            catch (Exception ex) { Log.W("Status ERROR: " + ex.Message); return -1; }
        }
    }

    // method = TryTogglePlayPauseAsync / TrySkipNextAsync / TrySkipPreviousAsync
    public static bool Do(string method)
    {
        lock (gate)
        {
            try
            {
                var se = Session();
                if (se == null) { Log.W(method + ": no Chrome session -> media key"); return false; }
                var op = T("GlobalSystemMediaTransportControlsSession").GetMethod(method).Invoke(se, null);
                var r = Await(op, typeof(bool));
                Log.W(method + " -> " + (r == null ? "null/timeout" : r.ToString()));
                return r is bool && (bool)r;
            }
            catch (Exception ex) { Log.W(method + " ERROR: " + ex); return false; }
        }
    }
}

// ---------- WebView2 (Edge engine) DLLs exe ke andar embedded ----------
// Pehli dafa %LOCALAPPDATA%\YTMiniRemote\wv2 me nikal kar wahan se load hoti hain.
static class WV2
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string path);
    public static string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YTMiniRemote", "wv2");
    public static string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YTMiniRemote", "browser");

    public static void Setup()
    {
        Directory.CreateDirectory(Dir);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in new[] { "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll",
                                     Environment.Is64BitProcess ? "WebView2Loader.x64.dll" : "WebView2Loader.x86.dll" })
        {
            string outName = name.StartsWith("WebView2Loader") ? "WebView2Loader.dll" : name;
            string target = Path.Combine(Dir, outName);
            using (var st = asm.GetManifestResourceStream(name))
            {
                if (st == null) continue;
                if (File.Exists(target) && new FileInfo(target).Length == st.Length) continue;
                try { using (var f = File.Create(target)) st.CopyTo(f); } catch { }
            }
        }
        SetDllDirectory(Dir);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string n = new AssemblyName(e.Name).Name;
            string f = Path.Combine(Dir, n + ".dll");
            return File.Exists(f) ? Assembly.LoadFrom(f) : null;
        };
    }
}

// ---------- YouTube player (remote ke andar) ----------
// Mini mode: sirf video, remote ke upar, hamesha upar.  Big mode: poori YouTube (search, playlist, login).
public class Player : Form
{
    Microsoft.Web.WebView2.WinForms.WebView2 web;
    bool ready, mini;
    Rectangle bigBounds = Rectangle.Empty, miniBounds = Rectangle.Empty;
    readonly Form remote;
    public event Action ModeChanged;
    public bool IsMini { get { return mini; } }
    readonly string cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YTMiniRemote", "player.txt");

    const string MINI_CSS =
        "html,body{overflow:hidden!important;background:#000!important}" +
        "#masthead-container,#secondary,#below,#chat,ytd-mini-guide-renderer,tp-yt-app-drawer,#guide{display:none!important}" +
        "#movie_player{position:fixed!important;top:0!important;left:0!important;width:100vw!important;height:100vh!important;z-index:2147483646!important;background:#000!important}" +
        "#movie_player video{width:100vw!important;height:100vh!important;left:0!important;top:0!important;object-fit:contain!important}" +
        ".ytp-chrome-bottom,.ytp-chrome-top,.ytp-gradient-bottom,.ytp-gradient-top,.ytp-ce-element,.ytp-pause-overlay,.ytp-paid-content-overlay,.iv-branding{display:none!important}";

    // Har page par chalne wala script: mini mode me mouse = drag/resize, right-click = big
    const string BOOT = @"(function(){
  if (window.__ytr) return; window.__ytr = 1;
  window.__ytrMini = false;
  window.__ytrSetMini = function(on){
    window.__ytrMini = on;
    var st = document.getElementById('__ytr_css');
    if (on && !st){ st = document.createElement('style'); st.id='__ytr_css'; st.textContent = '%CSS%'; (document.head||document.documentElement).appendChild(st); }
    if (!on && st) st.remove();
    setTimeout(function(){ window.dispatchEvent(new Event('resize')); }, 50);
    setTimeout(function(){ window.dispatchEvent(new Event('resize')); }, 400);
  };
  function post(m){ try{ chrome.webview.postMessage(m); }catch(e){} }
  window.addEventListener('mousedown', function(e){
    if (!window.__ytrMini || e.button !== 0) return;
    e.preventDefault(); e.stopPropagation(); post('drag');
  }, true);
  window.addEventListener('click', function(e){ if (window.__ytrMini){ e.preventDefault(); e.stopPropagation(); } }, true);
  window.addEventListener('dblclick', function(e){ if (window.__ytrMini){ e.preventDefault(); e.stopPropagation(); } }, true);
  window.addEventListener('contextmenu', function(e){
    if (!window.__ytrMini) return;
    e.preventDefault(); e.stopPropagation(); post('big');
  }, true);
  window.addEventListener('wheel', function(e){
    if (!window.__ytrMini) return;
    e.preventDefault(); e.stopPropagation(); post(e.deltaY < 0 ? 'bigger' : 'smaller');
  }, {capture:true, passive:false});
})();";

    public Player(Form remoteForm)
    {
        remote = remoteForm;
        Text = "YT Mini Player";
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        MinimumSize = new Size(160, 90);
        try { Icon = Remote.MakeIcon(); } catch { }
        web = new Microsoft.Web.WebView2.WinForms.WebView2();
        web.Dock = DockStyle.Fill;
        web.DefaultBackgroundColor = Color.Black;
        Controls.Add(web);
        LoadCfg();
        SetBig();   // pehle big (YouTube chunein / login)
        Init();
    }

    async void Init()
    {
        try
        {
            Directory.CreateDirectory(WV2.DataDir);
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, WV2.DataDir, null);
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(BOOT.Replace("%CSS%", MINI_CSS));
            core.WebMessageReceived += (s, e) =>
            {
                string m = null;
                try { m = e.TryGetWebMessageAsString(); } catch { }
                if (m == "drag") { Native.Drag(Handle); miniBounds = Bounds; SaveCfg(); }
                else if (m == "big") SetBig();
                else if (m == "bigger" || m == "smaller") ResizeMini(m == "bigger");
            };
            core.NavigationCompleted += (s, e) => ApplyMode();
            core.DocumentTitleChanged += (s, e) => { try { Text = core.DocumentTitle + " - YT Mini Player"; } catch { } };
            core.Navigate("https://www.youtube.com/");
            ready = true;
            Log.W("Player ready");
        }
        catch (Exception ex)
        {
            Log.W("Player init ERROR: " + ex);
            MessageBox.Show(this, "Could not start the built-in YouTube player.\r\n\r\n" + ex.Message, "YT Mini Remote");
        }
    }

    void ApplyMode()
    {
        if (!ready) return;
        try { web.CoreWebView2.ExecuteScriptAsync("window.__ytrSetMini && window.__ytrSetMini(" + (mini ? "true" : "false") + ")"); } catch { }
    }

    // Big = normal window (YouTube browse);  minimize dabane par Mini ban jata hai
    public void SetBig()
    {
        mini = false;
        TopMost = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        if (bigBounds.IsEmpty)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            bigBounds = new Rectangle(wa.Left + wa.Width / 2 - 520, wa.Top + wa.Height / 2 - 330, 1040, 660);
        }
        WindowState = FormWindowState.Normal;
        Bounds = bigBounds;
        ApplyMode();
        if (Visible) Activate();
        if (ModeChanged != null) ModeChanged();
    }

    public void SetMini()
    {
        if (!mini && WindowState == FormWindowState.Normal && FormBorderStyle == FormBorderStyle.Sizable) bigBounds = Bounds;
        mini = true;
        WindowState = FormWindowState.Normal;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        if (miniBounds.IsEmpty)
        {
            int w = remote.Width, h = (int)(w * 9 / 16.0);
            int y = remote.Top - h - 4;
            var wa = Screen.FromControl(remote).WorkingArea;
            if (y < wa.Top) y = remote.Bottom + 4;
            miniBounds = new Rectangle(remote.Left, y, w, h);
        }
        Bounds = miniBounds;
        ApplyMode();
        SaveCfg();
        if (ModeChanged != null) ModeChanged();
    }

    // Remote ke saath saath mini player bhi chale
    public void MoveBy(int dx, int dy)
    {
        if (miniBounds.IsEmpty) return;
        miniBounds.Offset(dx, dy);
        if (mini && Visible) Location = miniBounds.Location;
        SaveCfg();
    }

    void ResizeMini(bool up)
    {
        int w = Math.Max(160, Math.Min(1280, (int)(Width * (up ? 1.1 : 0.9))));
        int h = (int)(w * 9 / 16.0);
        Bounds = new Rectangle(Right - w, Bottom - h, w, h);   // neeche-right kona apni jagah
        miniBounds = Bounds; SaveCfg();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Big window ka minimize = Mini player
        if (!mini && WindowState == FormWindowState.Minimized) BeginInvoke((Action)(() => SetMini()));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // X dabane par sirf chhupao (music chalta rahe), app Exit par hi band
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; SetMini(); return; }
        SaveCfg();
        base.OnFormClosing(e);
    }

    // ---------- Remote ke commands ----------
    public void Js(string code, Action<string> done)
    {
        if (!ready) { if (done != null) done(null); return; }
        try
        {
            var t = web.CoreWebView2.ExecuteScriptAsync(code);
            var ui = System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext();
            t.ContinueWith(x => { if (done != null) done(x.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? x.Result : null); }, ui);
        }
        catch { if (done != null) done(null); }
    }

    const string V = "(document.querySelector('#movie_player video')||document.querySelector('video'))";
    public void PlayPause(Action<string> done) { Js("(function(){var v=" + V + ";if(!v)return 'none';if(v.paused)v.play();else v.pause();return v.paused?'paused':'playing';})()", done); }
    public void PlayPauseStop() { Js("(function(){var v=" + V + ";if(v)v.pause();})()", null); }
    public void Next() { Js("(function(){var b=document.querySelector('.ytp-next-button');if(b)b.click();})()", null); }
    public void Prev() { Js("(function(){var b=document.querySelector('.ytp-prev-button');var v=" + V + ";if(b&&b.offsetParent!==null&&!(v&&v.currentTime>3))b.click();else if(v)v.currentTime=0;})()", null); }
    public void Status(Action<string> done) { Js("(function(){var v=" + V + ";if(!v)return 'none';return v.paused?'paused':'playing';})()", done); }

    void LoadCfg()
    {
        try
        {
            if (!File.Exists(cfg)) return;
            var v = File.ReadAllText(cfg).Trim().Split(new char[] { ',' });
            var b = new Rectangle(int.Parse(v[0]), int.Parse(v[1]), int.Parse(v[2]), int.Parse(v[3]));
            foreach (var sc in Screen.AllScreens) if (sc.WorkingArea.Contains(b.Location)) { miniBounds = b; break; }
        }
        catch { }
    }

    void SaveCfg()
    {
        try
        {
            if (miniBounds.IsEmpty) return;
            Directory.CreateDirectory(Path.GetDirectoryName(cfg));
            File.WriteAllText(cfg, miniBounds.X + "," + miniBounds.Y + "," + miniBounds.Width + "," + miniBounds.Height);
        }
        catch { }
    }
}

// ---------- Namaz/Azan ke auqat (PrayTimes algorithm, Karachi method, Hanafi Asr) ----------
public static class PrayCalc
{
    static double D2R(double d) { return d * System.Math.PI / 180.0; }
    static double R2D(double r) { return r * 180.0 / System.Math.PI; }
    static double Sin(double d) { return System.Math.Sin(D2R(d)); }
    static double Cos(double d) { return System.Math.Cos(D2R(d)); }
    static double Tan(double d) { return System.Math.Tan(D2R(d)); }
    static double ArcSin(double x) { return R2D(System.Math.Asin(x)); }
    static double ArcCos(double x) { return R2D(System.Math.Acos(System.Math.Max(-1, System.Math.Min(1, x)))); }
    static double ArcTan2(double y, double x) { return R2D(System.Math.Atan2(y, x)); }
    static double ArcCot(double x) { return R2D(System.Math.Atan(1 / x)); }
    static double Fix(double a, double b) { a = a - b * System.Math.Floor(a / b); return a < 0 ? a + b : a; }

    static double jDate, lat, lng;

    static void SunPos(double jd, out double decl, out double eqt)
    {
        double D = jd - 2451545.0;
        double g = Fix(357.529 + 0.98560028 * D, 360);
        double q = Fix(280.459 + 0.98564736 * D, 360);
        double L = Fix(q + 1.915 * Sin(g) + 0.020 * Sin(2 * g), 360);
        double e = 23.439 - 0.00000036 * D;
        double RA = Fix(ArcTan2(Cos(e) * Sin(L), Cos(L)) / 15, 24);
        eqt = q / 15 - RA;
        decl = ArcSin(Sin(e) * Sin(L));
    }

    static double MidDay(double t) { double d, eq; SunPos(jDate + t, out d, out eq); return Fix(12 - eq, 24); }

    static double SunAngleTime(double angle, double t, bool ccw)
    {
        double decl, eq; SunPos(jDate + t, out decl, out eq);
        double noon = MidDay(t);
        double T = ArcCos((-Sin(angle) - Sin(decl) * Sin(lat)) / (Cos(decl) * Cos(lat))) / 15;
        return noon + (ccw ? -T : T);
    }

    static double AsrTime(double factor, double t)
    {
        double decl, eq; SunPos(jDate + t, out decl, out eq);
        double angle = -ArcCot(factor + Tan(System.Math.Abs(lat - decl)));
        return SunAngleTime(angle, t, false);
    }

    static double Julian(int y, int m, int d)
    {
        if (m <= 2) { y -= 1; m += 12; }
        double A = System.Math.Floor(y / 100.0);
        double B = 2 - A + System.Math.Floor(A / 4);
        return System.Math.Floor(365.25 * (y + 4716)) + System.Math.Floor(30.6001 * (m + 1)) + d + B - 1524.5;
    }

    public static readonly string[] Names = { "Fajr", "Dhuhr", "Asr", "Maghrib", "Isha" };

    // Wapsi: is din ke 5 auqat (local time). tz = UTC se ghante.
    public static System.DateTime[] Times(System.DateTime date, double latitude, double longitude, double tz)
    {
        lat = latitude; lng = longitude;
        jDate = Julian(date.Year, date.Month, date.Day) - lng / (15 * 24.0);
        // day portions ka andaza, phir 2 dafa behtar
        double[] t = { 5, 6, 12, 13, 18, 18, 18 };   // fajr, sunrise, dhuhr, asr, sunset, maghrib, isha
        for (int it = 0; it < 2; it++)
        {
            for (int i = 0; i < t.Length; i++) t[i] /= 24.0;
            t = new double[] {
                SunAngleTime(18, t[0], true),          // Fajr 18 deg (Karachi)
                SunAngleTime(0.833, t[1], true),
                MidDay(t[2]),
                AsrTime(2, t[3]),                      // Hanafi
                SunAngleTime(0.833, t[4], false),
                SunAngleTime(0.833, t[5], false),      // Maghrib = sunset
                SunAngleTime(18, t[6], false)          // Isha 18 deg (Karachi)
            };
        }
        double adj = tz - lng / 15;
        double[] pick = { t[0], t[2], t[3], t[5], t[6] };
        var res = new System.DateTime[5];
        for (int i = 0; i < 5; i++)
        {
            double h = pick[i] + adj;
            if (i == 1) h += 1 / 60.0;                 // Dhuhr: zawal ke 1 min baad
            res[i] = date.Date.AddMinutes(System.Math.Round(h * 60));
        }
        return res;
    }
}

// ---------- Location + azan settings ----------
static class AzanCfg
{
    static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YTMiniRemote"); } }
    public static string LocFile { get { return Path.Combine(Dir, "location.txt"); } }
    public static string OffFile { get { return Path.Combine(Dir, "azan-times.txt"); } }
    public static string OnFile { get { return Path.Combine(Dir, "azan-off.txt"); } }

    // Default: Karachi
    public static double Lat = 24.8607, Lng = 67.0011;
    public static string City = "Karachi (default)";
    public static DateTime LocDate = DateTime.MinValue;
    public static int[] Offset = new int[5];          // har namaz ke minute aage/peeche
    public static string[] Fixed = new string[5];     // ya masjid ka fixed waqt, jaise 13:30

    public static bool Enabled
    {
        get { return !File.Exists(OnFile); }
        set { try { Directory.CreateDirectory(Dir); if (value) File.Delete(OnFile); else File.WriteAllText(OnFile, "off"); } catch { } }
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(LocFile))
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var v = File.ReadAllText(LocFile).Split('|');
                Lat = double.Parse(v[0], ci); Lng = double.Parse(v[1], ci); City = v[2];
                LocDate = DateTime.ParseExact(v[3], "yyyy-MM-dd", ci);
            }
        }
        catch { }
        LoadOffsets();
    }

    public static void LoadOffsets()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (!File.Exists(OffFile))
                File.WriteAllText(OffFile,
                    "# YT Mini Remote - Azan settings\r\n" +
                    "# Pause starts 2 minutes before azan and lasts 7 minutes.\r\n" +
                    "# Each line: Prayer = minutes to add (e.g. +15 or -5)  OR  a fixed mosque time (e.g. 13:30)\r\n" +
                    "# 0 = use the calculated time for your location.\r\n" +
                    "Fajr = 0\r\nDhuhr = 13:00\r\nAsr = 0\r\nMaghrib = 0\r\nIsha = 0\r\n");
            // Purani default file (Zuhr = 0) ko naye default (Zuhr = 13:00) par le aao
            var txt = File.ReadAllText(OffFile);
            if (txt.Contains("Dhuhr = 0\r\n") && txt.Contains("Fajr = 0\r\n") && txt.Contains("Asr = 0\r\n") && txt.Contains("Isha = 0\r\n"))
                File.WriteAllText(OffFile, txt.Replace("Dhuhr = 0\r\n", "Dhuhr = 13:00\r\n"));
            Offset = new int[5]; Fixed = new string[5];
            foreach (var raw in File.ReadAllLines(OffFile))
            {
                var line = raw.Trim();
                if (line.StartsWith("#") || !line.Contains("=")) continue;
                var parts = line.Split('=');
                int i = Array.IndexOf(PrayCalc.Names, parts[0].Trim());
                if (i < 0) continue;
                var val = parts[1].Trim();
                if (val.Contains(":")) Fixed[i] = val;
                else { int m; if (int.TryParse(val.Replace("+", ""), out m)) Offset[i] = m; }
            }
        }
        catch (Exception ex) { Log.W("Azan settings ERROR: " + ex.Message); }
    }

    // IP se shehar/location (din me ek dafa)
    public static void RefreshLocation()
    {
        if (LocDate.Date == DateTime.Today) return;
        try
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
            string json = null;
            foreach (var url in new[] { "https://ipwho.is/", "https://ipapi.co/json/", "http://ip-api.com/json/" })
            {
                try { using (var wc = new WebClient()) { wc.Headers["User-Agent"] = "YTMiniRemote"; json = wc.DownloadString(url); } }
                catch { json = null; }
                if (json != null && (json.Contains("\"latitude\"") || json.Contains("\"lat\""))) break;
                json = null;
            }
            if (json == null) { Log.W("Location lookup failed, using " + City); return; }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            Func<string, string> num = k =>
            {
                var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + k + "\"\\s*:\\s*(-?[0-9.]+)");
                return m.Success ? m.Groups[1].Value : null;
            };
            string la = num("latitude") ?? num("lat"), lo = num("longitude") ?? num("lon");
            var cm = System.Text.RegularExpressions.Regex.Match(json, "\"city\"\\s*:\\s*\"([^\"]*)\"");
            if (la == null || lo == null) return;
            Lat = double.Parse(la, ci); Lng = double.Parse(lo, ci);
            City = cm.Success ? cm.Groups[1].Value : "Your location";
            LocDate = DateTime.Today;
            Directory.CreateDirectory(Dir);
            File.WriteAllText(LocFile, Lat.ToString(ci) + "|" + Lng.ToString(ci) + "|" + City + "|" + LocDate.ToString("yyyy-MM-dd", ci));
            Log.W("Location: " + City + " " + Lat + "," + Lng);
        }
        catch (Exception ex) { Log.W("Location ERROR: " + ex.Message); }
    }

    // Aaj ke azan ke auqat (settings ke saath)
    public static DateTime[] Today(DateTime day)
    {
        double tz = TimeZoneInfo.Local.GetUtcOffset(day.Date.AddHours(12)).TotalHours;
        var t = PrayCalc.Times(day, Lat, Lng, tz);
        for (int i = 0; i < 5; i++)
        {
            TimeSpan ts;
            if (Fixed[i] != null && TimeSpan.TryParse(Fixed[i], out ts)) t[i] = day.Date + ts;
            else t[i] = t[i].AddMinutes(Offset[i]);
        }
        return t;
    }
}

// ---------- Setup: Windows ke saath start (user ki marzi se) ----------
static class AutoStart
{
    static string LinkFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "YT Mini Remote.vbs"); } }
    static string AskedFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YTMiniRemote", "setup-asked.txt"); } }

    public static bool IsOn { get { return File.Exists(LinkFile); } }
    public static bool Asked { get { return File.Exists(AskedFile); } }
    public static void MarkAsked() { try { Directory.CreateDirectory(Path.GetDirectoryName(AskedFile)); File.WriteAllText(AskedFile, "1"); } catch { } }

    public static bool Set(bool on)
    {
        try
        {
            if (!on) { if (File.Exists(LinkFile)) File.Delete(LinkFile); return true; }
            string exe = Application.ExecutablePath;
            string vbs =
                "' YT Mini Remote - Windows start hone par tray me chalata hai\r\n" +
                "exe = \"" + exe + "\"\r\n" +
                "Set fso = CreateObject(\"Scripting.FileSystemObject\")\r\n" +
                "For i = 1 To 90\r\n" +
                "  If fso.FileExists(exe) Then Exit For\r\n" +
                "  WScript.Sleep 2000\r\n" +
                "Next\r\n" +
                "If fso.FileExists(exe) Then CreateObject(\"WScript.Shell\").Run \"\"\"\" & exe & \"\"\" /background\", 0, False\r\n";
            File.WriteAllText(LinkFile, vbs);
            return true;
        }
        catch (Exception ex) { Log.W("AutoStart ERROR: " + ex.Message); return false; }
    }
}

// Apna tooltip: focus nahi leta, hamesha upar, remote ke upar/neeche dikhta hai
class TipForm : Form
{
    readonly Label lbl = new Label();
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020; return cp; } // NOACTIVATE|TOOLWINDOW|TRANSPARENT(click-through)
    }
    public TipForm()
    {
        FormBorderStyle = FormBorderStyle.None; TopMost = true; ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual; BackColor = Color.FromArgb(220, 45, 45);
        lbl.AutoSize = true; lbl.ForeColor = Color.White; lbl.BackColor = Color.FromArgb(220, 45, 45);
        lbl.Font = new Font("Segoe UI Semibold", 9f); lbl.Padding = new Padding(8, 4, 8, 4);
        Controls.Add(lbl);
    }
    public void ShowAt(string text, Control target, Form owner)
    {
        lbl.Text = text;
        Size = lbl.PreferredSize;
        var r = target.RectangleToScreen(target.ClientRectangle);
        var wa = Screen.FromControl(owner).WorkingArea;
        int x = r.Left + r.Width / 2 - Width / 2;
        x = Math.Max(wa.Left + 2, Math.Min(x, wa.Right - Width - 2));
        int y = owner.Top - Height - 4;                       // remote ke upar
        if (y < wa.Top) y = owner.Bottom + 4;                 // jagah na ho to neeche
        Location = new Point(x, y);
        if (!Visible) Show();
    }
}

public class Remote : Form
{
    // ---------- Settings ----------
    const double IDLE = 0.40, HOVER = 0.95;
    const int VOLSTEP = 2;                       // har click = 2 steps (~4%)
    const byte VK_NEXT = 0xB0, VK_PREV = 0xB1, VK_PLAY = 0xB3,
               VK_MUTE = 0xAD, VK_VDOWN = 0xAE, VK_VUP = 0xAF;

    // Ye rang taskbar ke hisaab se khud badalte hain (ApplyTheme)
    Color BG = Color.FromArgb(22, 22, 22);
    Color FG = Color.FromArgb(235, 235, 235);
    Color HL = Color.FromArgb(62, 62, 62);
    Color DIM = Color.FromArgb(150, 150, 150);
    Label grip;
    static readonly Color RED = Color.FromArgb(220, 45, 45);

    readonly string cfgDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YTMiniRemote");
    string CfgFile { get { return Path.Combine(cfgDir, "pos.txt"); } }

    const int H = 34;
    readonly Font iconFont = new Font("Segoe MDL2 Assets", 11f);
    readonly TipForm tip = new TipForm();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    Label bVol;
    bool muted, hovering;
    bool exiting;          // tray se Exit dabaya
    public bool dismissed; // user ne chhupaya -> is YouTube session me dobara khud na khule
    bool autoShown;        // YouTube dekh kar khud khula tha
    readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer();
    NotifyIcon tray;
    Label bMini;
    Player player;
    Label bPlay;
    volatile bool shown;
    System.Threading.Timer statusTimer;
    readonly System.Windows.Forms.Timer playerStatus = new System.Windows.Forms.Timer();
    volatile bool playerActive;
    bool playerPlaying;
    // ---- Azan pause ----
    readonly System.Windows.Forms.Timer azanTimer = new System.Windows.Forms.Timer();
    DateTime azanDay = DateTime.MinValue; DateTime[] azanTimes;
    string azanDone = "";            // is namaz ka pause ho chuka
    DateTime azanUntil = DateTime.MinValue;
    bool azanResume, azanResumePlayer;
    ToolStripMenuItem miAzan;
    DateTime azanCfgTime = DateTime.MinValue, azanLocSeen = DateTime.MinValue;
    // Player khula ho ya peeche chal raha ho -> remote ke buttons player ko
    bool PlayerOn { get { return player != null && (player.Visible || playerPlaying); } }

    // Click par focus na churaye + taskbar/Alt+Tab me na aaye
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    public Remote()
    {
        Text = "YT Mini Remote";
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = BG;
        ClientSize = new Size(254, H);
        Opacity = IDLE;
        Location = LoadPos();

        // Grip
        grip = Btn(0xE700, 0, 20, "YT Mini Remote  \u2022  Drag to move");
        grip.Cursor = Cursors.SizeAll;
        grip.ForeColor = DIM;
        grip.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { tip.Hide(); Native.Drag(Handle); SavePos(); } };

        Btn(0xE892,  20, 30, "Previous video").Click += (s, e) => { if (PlayerOn) player.Prev(); else Send("TrySkipPreviousAsync", VK_PREV); };
        bPlay = Btn(0xE768, 50, 30, "Play");
        bPlay.Click += (s, e) =>
        {
            SetPlaying(bPlay.Tag as string == "Play");   // foran icon badlo
            if (PlayerOn) player.PlayPause(r => { if (r == "\"playing\"") SetPlaying(true); else if (r == "\"paused\"") SetPlaying(false); });
            else Send("TryTogglePlayPauseAsync", VK_PLAY);
        };
        Btn(0xE893,  80, 30, "Next video").Click     += (s, e) => { if (PlayerOn) player.Next(); else Send("TrySkipNextAsync", VK_NEXT); };

        bVol = Btn(0xE767, 110, 30, "Mute");
        bVol.Click += (s, e) => { Native.Press(VK_MUTE); SetMuted(!muted); };

        Btn(0xE738, 140, 30, "Volume down").Click += (s, e) => { for (int i = 0; i < VOLSTEP; i++) Native.Press(VK_VDOWN); };
        Btn(0xE710, 170, 30, "Volume up").Click += (s, e) =>
        {
            for (int i = 0; i < VOLSTEP; i++) Native.Press(VK_VUP);
            if (muted) SetMuted(false);   // Windows volume-up khud unmute kar deta hai
        };

        bMini = Btn(0xE70E, 200, 30, "Open YouTube player");
        bMini.Click += (s, e) => TogglePlayer();

        var bX = Btn(0xE711, 230, 24, "Hide (reopen from tray icon)");
        bX.MouseEnter += (s, e) => bX.BackColor = RED;
        bX.Click += (s, e) => { dismissed = true; HideRemote(); };

        // Hover = dark
        timer.Interval = 80;
        timer.Tick += (s, e) =>
        {
            bool inside = Bounds.Contains(Control.MousePosition);
            if (inside != hovering) { hovering = inside; Opacity = inside ? HOVER : IDLE; }
        };

        // YouTube watcher: har 1.5 second me Chrome check
        watch.Interval = 1500;
        watch.Tick += (s, e) => CheckYouTube();
        watch.Start();

        // Taskbar ka rang sirf start par ek dafa liya jata hai
        ApplyTheme(TaskbarColor());

        BuildTray();

        // Har second: video chal rahi hai ya ruki? (sirf jab remote dikh raha ho)
        playerStatus.Interval = 1000;
        playerStatus.Tick += (s, e) =>
        {
            if (player == null) { playerActive = false; return; }
            player.Status(r =>
            {
                playerPlaying = r == "\"playing\"";
                playerActive = PlayerOn;
                if (!PlayerOn) return;
                if (r == "\"playing\"") SetPlaying(true); else if (r == "\"paused\"") SetPlaying(false);
            });
        };
        playerStatus.Start();

        // Azan: har 10 second check
        AzanCfg.Load();
        ThreadPool.QueueUserWorkItem(st => AzanCfg.RefreshLocation());
        azanTimer.Interval = 10000;
        azanTimer.Tick += (s, e) => AzanTick();
        azanTimer.Start();

        statusTimer = new System.Threading.Timer(st =>
        {
            if (!shown || playerActive) return;
            int status = Media.Status();
            if (status < 0) return;
            try { BeginInvoke((Action)(() => SetPlaying(status == 1))); } catch { }
        }, null, 1000, 1000);
    }

    // Pehle Chrome ke session ko seedha command; na chale to media key
    void Send(string method, byte fallbackKey)
    {
        ThreadPool.QueueUserWorkItem(st =>
        {
            Log.W("Click: " + method);
            if (!Media.Do(method)) { Native.Press(fallbackKey); Log.W("Media key sent: 0x" + fallbackKey.ToString("X")); }
        });
    }

    // Chal rahi ho to Pause icon, ruki ho to Play icon
    void SetPlaying(bool playing)
    {
        string want = playing ? "Pause" : "Play";
        if (bPlay.Tag as string == want) return;
        bPlay.Text = ((char)(playing ? 0xE769 : 0xE768)).ToString();
        bPlay.Tag = want;
        if (tip.Visible && bPlay.ClientRectangle.Contains(bPlay.PointToClient(Control.MousePosition)))
            tip.ShowAt(want, bPlay, this);
    }

    void CheckYouTube()
    {
        bool chrome, yt;
        try { Native.Scan(out chrome, out yt); } catch { return; }
        if (yt && !dismissed && !Visible) { ShowRemote(); autoShown = true; }
        if (!yt) dismissed = false;                       // YouTube se hate -> agli dafa phir khud khule
        if (!chrome && Visible && autoShown) { HideRemote(); autoShown = false; }   // Chrome band -> remote bhi chhup
    }

    public void ShowRemote()
    {
        if (!Visible) { Opacity = IDLE; hovering = false; Show(); }
        shown = true;
        TopMost = false; TopMost = true;
    }

    // Up arrow: player kholo (pehli dafa big, phir mini) / band karo
    void TogglePlayer()
    {
        tip.Hide();
        if (player == null)
        {
            try
            {
                player = new Player(this);
                player.ModeChanged += UpdatePlayerButton;
                player.VisibleChanged += (s, e) => { playerActive = PlayerOn; UpdatePlayerButton(); };
                player.Show();
            }
            catch (Exception ex) { Log.W("Player ERROR: " + ex); MessageBox.Show(this, "Built-in player failed: " + ex.Message, "YT Mini Remote"); player = null; }
            return;
        }
        if (player.Visible && player.IsMini) player.Hide();          // mini chhupao, gaana chalta rahe (remote se control)
        else if (player.Visible) player.SetMini();
        else { player.Show(); player.SetMini(); }
        UpdatePlayerButton();
    }

    Point lastLoc = Point.Empty;
    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (!lastLoc.IsEmpty && player != null) player.MoveBy(Left - lastLoc.X, Top - lastLoc.Y);
        lastLoc = Location;
    }

    // ---------- Azan: 2 min pehle pause, 7 min tak ruka rahe, phir wapas chalu ----------
    void AzanTick()
    {
        var now = DateTime.Now;
        // Settings file badli ya location aa gayi -> auqat dobara
        DateTime mt = DateTime.MinValue;
        try { mt = File.GetLastWriteTime(AzanCfg.OffFile); } catch { }
        if (mt != azanCfgTime || AzanCfg.LocDate != azanLocSeen)
        {
            azanCfgTime = mt; azanLocSeen = AzanCfg.LocDate;
            AzanCfg.LoadOffsets(); azanTimes = null;
        }
        if (azanDay != now.Date || azanTimes == null)
        {
            if (AzanCfg.LocDate.Date != now.Date) ThreadPool.QueueUserWorkItem(st => AzanCfg.RefreshLocation());
            azanTimes = AzanCfg.Today(now); azanDay = now.Date;
        }

        // Pause chal raha hai
        if (now < azanUntil)
        {
            ForcePause();   // beech me koi chala de to phir rok do
            return;
        }
        if (azanUntil != DateTime.MinValue)
        {
            azanUntil = DateTime.MinValue;
            if (azanResume)
            {
                Log.W("Azan pause over - resuming");
                if (azanResumePlayer && player != null) player.Js("(function(){var v=(document.querySelector('#movie_player video')||document.querySelector('video'));if(v)v.play();})()", null);
                else ThreadPool.QueueUserWorkItem(st => Media.Do("TryPlayAsync"));
            }
            azanResume = false;
            return;
        }

        if (!AzanCfg.Enabled) return;
        for (int i = 0; i < 5; i++)
        {
            var start = azanTimes[i].AddMinutes(-2);
            var end = start.AddMinutes(7);
            string key = now.ToString("yyyyMMdd") + i;
            if (now >= start && now < end && azanDone != key)
            {
                azanDone = key;
                azanUntil = end;
                StartAzanPause(PrayCalc.Names[i], azanTimes[i], end);
                return;
            }
        }
    }

    void StartAzanPause(string name, DateTime at, DateTime until)
    {
        Log.W("Azan " + name + " " + at.ToString("HH:mm") + " - pause until " + until.ToString("HH:mm"));
        azanResume = false; azanResumePlayer = false;
        // Player chal raha ho?
        if (player != null && playerPlaying) { azanResume = true; azanResumePlayer = true; }
        ThreadPool.QueueUserWorkItem(st =>
        {
            bool chromePlaying = Media.Status() == 1;
            try { BeginInvoke((Action)(() => { if (chromePlaying && !azanResumePlayer) azanResume = true; ForcePause(); })); } catch { }
        });
        try
        {
            tray.ShowBalloonTip(6000, name + " azan (" + at.ToString("h:mm tt") + ")",
                "YouTube paused until " + until.ToString("h:mm tt") + ".", ToolTipIcon.Info);
        }
        catch { }
    }

    void ForcePause()
    {
        if (player != null) player.PlayPauseStop();
        ThreadPool.QueueUserWorkItem(st => { if (Media.Status() == 1) Media.Do("TryPauseAsync"); });
        SetPlaying(false);
    }

    void ShowAzanTimes()
    {
        AzanCfg.LoadOffsets();
        azanTimes = AzanCfg.Today(DateTime.Now); azanDay = DateTime.Today;
        var sb = new StringBuilder();
        sb.AppendLine("Location: " + AzanCfg.City);
        sb.AppendLine();
        for (int i = 0; i < 5; i++)
            sb.AppendLine(PrayCalc.Names[i].PadRight(10) + azanTimes[i].ToString("h:mm tt") +
                          "   (pause " + azanTimes[i].AddMinutes(-2).ToString("h:mm") + " - " + azanTimes[i].AddMinutes(5).ToString("h:mm tt") + ")");
        sb.AppendLine();
        sb.AppendLine("Azan pause is " + (AzanCfg.Enabled ? "ON" : "OFF") + ".");
        sb.AppendLine("Your mosque's times differ? Use 'Adjust azan times' in the tray menu.");
        MessageBox.Show(this, sb.ToString(), "Today's azan times");
    }

    void UpdatePlayerButton()
    {
        bool open = player != null && player.Visible && player.IsMini;
        bMini.Text = ((char)(open ? 0xE70D : 0xE70E)).ToString();
        bMini.Tag = player == null || !player.Visible ? "Open YouTube player"
                  : player.IsMini ? "Hide player" : "Make it mini";
    }

    void HideRemote()
    {
        shown = false; tip.Hide(); SavePos(); Hide();
        if (player != null && player.Visible) { player.PlayPauseStop(); player.Hide(); }   // remote band -> player bhi band
    }

    // ---------- Tray icon ----------
    void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show remote", null, (s, e) => { dismissed = false; ShowRemote(); });
        menu.Items.Add("Hide remote", null, (s, e) => { dismissed = true; HideRemote(); });
        menu.Items.Add("Open YouTube player (big)", null, (s, e) => { if (player == null) TogglePlayer(); else { player.Show(); player.SetBig(); } });
        menu.Items.Add(new ToolStripSeparator());
        var miAuto = new ToolStripMenuItem("Start with Windows") { Checked = AutoStart.IsOn, CheckOnClick = true };
        miAuto.CheckedChanged += (s, e) => AutoStart.Set(miAuto.Checked);
        menu.Items.Add(miAuto);
        miAzan = new ToolStripMenuItem("Pause for azan") { Checked = AzanCfg.Enabled, CheckOnClick = true };
        miAzan.CheckedChanged += (s, e) => { AzanCfg.Enabled = miAzan.Checked; if (!miAzan.Checked) azanUntil = DateTime.MinValue; };
        menu.Items.Add(miAzan);
        menu.Items.Add("Today's azan times", null, (s, e) => ShowAzanTimes());
        menu.Items.Add("Adjust azan times", null, (s, e) =>
        {
            AzanCfg.LoadOffsets();
            try { Process.Start("notepad.exe", "\"" + AzanCfg.OffFile + "\""); } catch { }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => ExitApp());

        Icon ico;
        try { ico = MakeIcon(); } catch { ico = SystemIcons.Application; }
        tray = new NotifyIcon { Icon = ico, Text = "YT Mini Remote", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (Visible) { dismissed = true; HideRemote(); } else { dismissed = false; ShowRemote(); }
        };
    }

    // ---------- Taskbar colour match ----------
    static Color TaskbarColor()
    {
        try
        {
            var sc = Screen.PrimaryScreen;
            Rectangle b = sc.Bounds, w = sc.WorkingArea;
            Point p;
            if (w.Bottom < b.Bottom)      p = new Point(b.Left + 2, b.Bottom - 2);   // neeche
            else if (w.Top > b.Top)       p = new Point(b.Left + 2, b.Top + 1);      // upar
            else if (w.Left > b.Left)     p = new Point(b.Left + 1, b.Bottom - 2);   // left
            else if (w.Right < b.Right)   p = new Point(b.Right - 2, b.Bottom - 2);  // right
            else return ThemeFallback();                                              // auto-hide taskbar
            using (var bmp = new Bitmap(1, 1))
            {
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(p, Point.Empty, new Size(1, 1));
                var c = bmp.GetPixel(0, 0);
                return Color.FromArgb(255, c.R, c.G, c.B);
            }
        }
        catch { return ThemeFallback(); }
    }

    // Windows light/dark setting se rang (jab taskbar na dikhe)
    static Color ThemeFallback()
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                if (v is int && (int)v == 1) return Color.FromArgb(243, 243, 243);
            }
        }
        catch { }
        return Color.FromArgb(32, 32, 32);
    }

    static Color Shift(Color c, int amt)
    {
        Func<int, int> f = x => Math.Max(0, Math.Min(255, x + amt));
        return Color.FromArgb(f(c.R), f(c.G), f(c.B));
    }

    void ApplyTheme(Color bg)
    {
        if (bg.ToArgb() == BG.ToArgb()) return;
        bool light = (bg.R * 299 + bg.G * 587 + bg.B * 114) / 1000 > 140;
        BG  = bg;
        FG  = light ? Color.FromArgb(25, 25, 25) : Color.FromArgb(240, 240, 240);
        HL  = Shift(bg, light ? -28 : 32);
        DIM = light ? Color.FromArgb(110, 110, 110) : Color.FromArgb(160, 160, 160);
        BackColor = BG;
        foreach (Control c in Controls)
        {
            c.BackColor = BG;
            c.ForeColor = (c == grip) ? DIM : FG;
        }
    }

    // Laal play icon (tray ke liye) code se banaya - koi file nahi chahiye
    public static Icon MakeIcon()
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var gp = new GraphicsPath();
            int d = 12; var r = new Rectangle(1, 5, 30, 22);
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            using (var b = new SolidBrush(Color.FromArgb(220, 40, 40))) g.FillPath(b, gp);
            g.FillPolygon(Brushes.White, new[] { new Point(12, 10), new Point(12, 22), new Point(22, 16) });
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void ExitApp()
    {
        exiting = true;
        watch.Stop(); timer.Stop(); azanTimer.Stop(); shown = false; statusTimer.Dispose();
        SavePos();
        tray.Visible = false; tray.Dispose();
        tip.Close();
        if (player != null) { try { player.Dispose(); } catch { } }
        Application.Exit();
    }

    Label Btn(int glyph, int x, int w, string tipText)
    {
        var l = new Label
        {
            Text = ((char)glyph).ToString(),
            Font = iconFont,
            ForeColor = FG,
            BackColor = BG,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(x, 0, w, H),
            Cursor = Cursors.Hand
        };
        l.Tag = tipText;
        l.MouseEnter += (s, e) => { l.BackColor = HL; tip.ShowAt((string)l.Tag, l, this); };
        l.MouseLeave += (s, e) => { l.BackColor = BG; tip.Hide(); };
        Controls.Add(l);
        return l;
    }

    void SetMuted(bool m)
    {
        muted = m;
        bVol.Text = ((char)(m ? 0xE74F : 0xE767)).ToString();
        bVol.Tag = m ? "Unmute (currently muted)" : "Mute";
        if (tip.Visible) tip.ShowAt((string)bVol.Tag, bVol, this);
    }

    Point LoadPos()
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        var def = new Point(wa.Right - Width - 20, wa.Bottom - H - 20);
        try
        {
            if (File.Exists(CfgFile))
            {
                var xy = File.ReadAllText(CfgFile).Trim().Split(new char[] { ',' });
                var p = new Point(int.Parse(xy[0]), int.Parse(xy[1]));
                foreach (var s in Screen.AllScreens)
                    if (s.WorkingArea.Contains(p)) return p;
            }
        }
        catch { }
        return def;
    }

    void SavePos()
    {
        try { Directory.CreateDirectory(cfgDir); File.WriteAllText(CfgFile, Left + "," + Top); }
        catch { }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Rounded corners
        int d = 16, w = Width, h = Height;
        var gp = new GraphicsPath();
        gp.AddArc(0, 0, d, d, 180, 90);
        gp.AddArc(w - d, 0, d, d, 270, 90);
        gp.AddArc(w - d, h - d, d, d, 0, 90);
        gp.AddArc(0, h - d, d, d, 90, 90);
        gp.CloseFigure();
        Region = new Region(gp);
        timer.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideRemote(); return; }
        base.OnFormClosing(e);
    }

    // Chalane ke tareeqe:
    //   YTMiniRemote.exe              -> remote foran dikhe + YouTube watcher
    //   YTMiniRemote.exe /background  -> sirf tray me, YouTube chalne par khud khule
    [STAThread]
    public static void Main(string[] args)
    {
        Application.ThreadException += (s, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);
        try { WV2.Setup(); } catch (Exception ex) { Log.W("WV2 setup ERROR: " + ex.Message); }
        try { Run(args); } catch (Exception ex) { Crash(ex); }
    }

    static void Crash(Exception ex)
    {
        string msg = ex == null ? "Unknown error" : ex.ToString();
        try
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YTMiniRemote");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "error.txt"), DateTime.Now + "\r\n" + msg);
        }
        catch { }
        MessageBox.Show(msg, "YT Mini Remote - Error");
    }

    static void Run(string[] args)
    {
        bool background = Array.Exists(args, a => a.Equals("/background", StringComparison.OrdinalIgnoreCase));
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "TheMasterSahib_YTRemote3_Show");

        var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "TheMasterSahib_YTRemote3_Quit");
        bool fresh;
        var mutex = new Mutex(true, "TheMasterSahib_YTRemote3", out fresh);
        {
            if (!fresh)
            {
                if (background) return;
                // Purana remote chal raha hai -> usay band karwa kar naya chalao (updates foran lagen)
                quitSignal.Set();
                try { if (!mutex.WaitOne(6000)) { showSignal.Set(); return; } }
                catch (AbandonedMutexException) { }
            }

            Application.EnableVisualStyles();
            try { Application.SetCompatibleTextRenderingDefault(false); } catch { }
            var remote = new Remote();
            var h = remote.Handle;

            quitSignal.Reset();
            ThreadPool.RegisterWaitForSingleObject(quitSignal, (st, to) =>
            {
                try { remote.BeginInvoke((Action)(() => remote.ExitApp())); } catch { }
            }, null, -1, true);

            ThreadPool.RegisterWaitForSingleObject(showSignal, (st, to) =>
            {
                try { remote.BeginInvoke((Action)(() => { remote.dismissed = false; remote.ShowRemote(); })); } catch { }
            }, null, -1, false);

            if (!background) remote.ShowRemote();
            if (!background && !AutoStart.Asked && !AutoStart.IsOn)
            {
                AutoStart.MarkAsked();
                var r = MessageBox.Show(remote,
                    "Start YT Mini Remote automatically when Windows starts?\r\n\r\n" +
                    "It will wait quietly in the tray, open when YouTube plays, and pause for azan every day.\r\n" +
                    "(You can change this later from the tray menu: Start with Windows)",
                    "YT Mini Remote - Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r == DialogResult.Yes) AutoStart.Set(true);
            }
            Application.Run();
            try { mutex.ReleaseMutex(); } catch { }
        }
    }
}
