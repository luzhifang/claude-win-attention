// Claude Code attention host (Windows Terminal): stacked corner cards, taskbar badge, tab jump.
// C# 5 / .NET Framework 4.x, compiled with the in-box csc.exe (by win-attention.py under WSL, by run.sh under
// Git Bash on native Windows). One host process serves every alert; later invocations hand their payload to it
// over a named pipe and exit.
//   claude-attention-XXXX.exe <base64>   show an alert (payload built by win-attention.py or by --hook)
//   claude-attention-XXXX.exe --hook <file>   native Windows: read the hook JSON that run.sh saved to <file>
//                                             (deleted after reading), decide, then show. A GUI-subsystem exe
//                                             cannot rely on getting stdin, hence the file.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Web.Script.Serialization;

namespace ClaudeAttention {

[ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ITaskbarList3 {
  void HrInit(); void AddTab(IntPtr h); void DeleteTab(IntPtr h); void ActivateTab(IntPtr h); void SetActiveAlt(IntPtr h);
  void MarkFullscreenWindow(IntPtr h, [MarshalAs(UnmanagedType.Bool)] bool f);
  void SetProgressValue(IntPtr h, ulong done, ulong total); void SetProgressState(IntPtr h, int flags);
  void RegisterTab(IntPtr t, IntPtr m); void UnregisterTab(IntPtr t); void SetTabOrder(IntPtr t, IntPtr b); void SetTabActive(IntPtr t, IntPtr m, uint r);
  void ThumbBarAddButtons(IntPtr h, uint c, IntPtr b); void ThumbBarUpdateButtons(IntPtr h, uint c, IntPtr b); void ThumbBarSetImageList(IntPtr h, IntPtr i);
  void SetOverlayIcon(IntPtr h, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string d);
}
[ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)] class TaskbarListCls {}

static class W {
  [StructLayout(LayoutKind.Sequential)] public struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
  [DllImport("user32.dll")] public static extern bool FlashWindowEx(ref FLASHWINFO p);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
  public const string WtClass = "CASCADIA_HOSTING_WINDOW_CLASS";
}

class Alert {
  public string Id = Guid.NewGuid().ToString("N");
  public string Key, Project, Label, Summary, Hint, Glyph, Marker, Title;
  public Color Color; public int Progress; public bool Demo;
  public DateTime Created = DateTime.Now;
  public IntPtr Wt = IntPtr.Zero; public AutomationElement Tab;
  public bool Located, WaitFg, Badged, Closing, CardHidden, Loaded, Positioned, Overflow;
  public volatile bool TabSelected, TabGone;
  public double TargetTop = double.NaN;
  public Window Card;
}

// Native Windows hook entry: same decisions as win-attention.py (keep the two in sync), minus the WSL tty marker.
static class HookMode {
  const string Hint = "点击跳到该标签页 · 右键关闭";
  static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

  static Dictionary<string, object> Obj(string json) {
    if (string.IsNullOrWhiteSpace(json)) return null;
    try { return Js.Deserialize<Dictionary<string, object>>(json); } catch { return null; }
  }
  static object Get(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
  static string S(Dictionary<string, object> d, string k) {
    object v = Get(d, k); return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
  }
  static IEnumerable<Dictionary<string, object>> Items(object list) {
    var e = list as IEnumerable;
    if (e == null || list is string) yield break;
    foreach (var o in e) { var d = o as Dictionary<string, object>; if (d != null) yield return d; }
  }

  static string ReadAll(string path) {
    try {
      using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
      using (var r = new StreamReader(fs, Encoding.UTF8)) return r.ReadToEnd();
    } catch { return ""; }
  }
  static IEnumerable<string> TailReversed(string path, int n) {
    var all = ReadAll(path).Split('\n');
    for (int i = all.Length - 1, k = 0; i >= 0 && k < n; i--, k++) yield return all[i];
  }

  // /btw runs with a fresh prompt_id that never reaches the transcript; prompt suggestions and recaps
  // (also untyped background subagents) reuse the latest turn's prompt_id, so a known id means "not /btw"
  static bool KnownPrompt(string path, string promptId) {
    if (promptId == "") return false;
    try {
      using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
        fs.Seek(Math.Max(0, fs.Length - 2000000), SeekOrigin.Begin);
        using (var r = new StreamReader(fs, Encoding.UTF8))
          return r.ReadToEnd().Contains("\"promptId\":\"" + promptId + "\"");
      }
    } catch { return false; }
  }

  static string LastAssistantText(string path) {
    foreach (var line in TailReversed(path, 300)) {
      var rec = Obj(line);
      if (S(rec, "type") != "assistant") continue;
      var texts = Items(Get(Get(rec, "message") as Dictionary<string, object>, "content"))
                    .Where(c => S(c, "type") == "text").Select(c => S(c, "text")).ToList();
      if (texts.Any(t => t.Trim() != "")) return string.Join("\n", texts);
    }
    return "";
  }

  // name of the newest tool_use in the transcript that has no tool_result yet ("" if none)
  static string PendingTool(string path) {
    var done = new HashSet<string>();
    foreach (var line in TailReversed(path, 200)) {
      var content = Items(Get(Get(Obj(line), "message") as Dictionary<string, object>, "content")).ToList();
      for (int i = content.Count - 1; i >= 0; i--) {
        var c = content[i];
        if (S(c, "type") == "tool_result") done.Add(S(c, "tool_use_id"));
        else if (S(c, "type") == "tool_use") return done.Contains(S(c, "id")) ? "" : S(c, "name");
      }
    }
    return "";
  }

  // the WT tab title reads "<spinner> <session name>": the /rename name when set (terminalTitleFromRename,
  // on by default), else the AI title. Return both, custom first, \x1f-separated; the host tries each.
  static string AiTitle(string path) {
    string raw = ReadAll(path);
    var out_ = new List<string>();
    foreach (var kf in new[] { new[] { "custom-title", "customTitle" }, new[] { "ai-title", "aiTitle" } }) {
      int i = raw.LastIndexOf("\"type\":\"" + kf[0] + "\"", StringComparison.Ordinal);
      if (i < 0) continue;
      int a = raw.LastIndexOf('\n', i) + 1, b = raw.IndexOf('\n', i);
      string t = S(Obj(b < 0 ? raw.Substring(a) : raw.Substring(a, b - a)), kf[1]).Trim();
      if (t != "" && !out_.Contains(t)) out_.Add(t);
    }
    return string.Join("\x1f", out_);
  }

  static string Clean(string text, int limit) {
    text = Regex.Replace(text ?? "", "[`*#>|]+", "");
    text = Regex.Replace(text, @"\s+", " ").Trim();
    return text.Length > limit ? text.Substring(0, limit) : text;
  }

  public static void Run(string[] args, string exe) {
    string json = "";
    if (args.Length > 1 && File.Exists(args[1])) {
      json = ReadAll(args[1]);
      try { File.Delete(args[1]); } catch { }
    }
    var data = Obj(json) ?? new Dictionary<string, object>();
    string ev = S(data, "hook_event_name"); if (ev == "") ev = "Stop";
    string session = S(data, "session_id"), transcript = S(data, "transcript_path");

    string dataDir = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA");
    if (string.IsNullOrEmpty(dataDir))
      dataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "win-attention");
    try { Directory.CreateDirectory(dataDir); } catch { }
    try {  // one line per trigger, event types only (no content)
      File.AppendAllText(System.IO.Path.Combine(dataDir, "win-attention.log"), string.Format("{0} {1} tool={2} type={3} session={4} argv={5}\n",
        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), ev, S(data, "tool_name") == "" ? "-" : S(data, "tool_name"),
        S(data, "notification_type") == "" ? "-" : S(data, "notification_type"),
        session == "" ? "-" : session.Substring(0, Math.Min(8, session.Length)), "--hook"), new UTF8Encoding(false));
    } catch { }

    string sid = Regex.Replace(session == "" ? "-" : session, @"[^\w-]", "");
    string flag = System.IO.Path.Combine(dataDir, "compacting-" + sid.Substring(0, Math.Min(40, sid.Length)));
    if (ev == "PreCompact") {  // compaction also fires an untyped SubagentStop; flag it so that one is not taken for /btw
      try { File.WriteAllText(flag, ""); } catch { }
      return;
    }
    string cwd = S(data, "cwd"); if (cwd == "") cwd = Environment.CurrentDirectory;
    string project = System.IO.Path.GetFileName(cwd.TrimEnd('/', '\\')); if (project == "") project = "Claude Code";

    string label, color, glyph, summary; int progress;
    if (ev == "Stop") {
      label = "✔ 任务完成"; color = "#34D399"; glyph = "✔"; progress = 2;
      summary = S(data, "last_assistant_message"); if (summary == "") summary = LastAssistantText(transcript);
    } else if (ev == "Notification") {
      // AskUserQuestion / ExitPlanMode get a permission_prompt a few seconds later; PreToolUse already showed the card
      string pending = PendingTool(transcript);
      if (pending == "AskUserQuestion" || pending == "ExitPlanMode") return;
      label = "⚠ 需要你确认"; color = "#F59E0B"; glyph = "!"; progress = 8; summary = S(data, "message");
    } else if (ev == "SubagentStop") {
      if (S(data, "agent_type") != "") return;  // Agent-tool subagents are typed; /btw is not
      if (KnownPrompt(transcript, S(data, "prompt_id"))) return;  // prompt suggestion / recap
      try { if (File.Exists(flag) && (DateTime.Now - File.GetLastWriteTime(flag)).TotalSeconds < 900) return; } catch { }
      label = "💬 /btw 已回答"; color = "#2DD4BF"; glyph = "i"; progress = 0; summary = S(data, "last_assistant_message");
    } else if (ev == "PostCompact") {
      try { File.Delete(flag); } catch { }
      label = "≡ 对话已压缩"; color = "#9CA3AF"; glyph = "≡"; progress = 0;
      summary = S(data, "trigger") == "auto" ? "自动压缩完成，任务会继续" : "手动压缩（/compact）完成";
    } else if (ev == "PreToolUse" && S(data, "tool_name") == "ExitPlanMode") {
      label = "◆ 计划待你审批"; color = "#A78BFA"; glyph = "◆"; progress = 1; summary = "计划已写好，等你审阅";
    } else if (ev == "PreToolUse") {
      label = "? 需要你选择"; color = "#60A5FA"; glyph = "?"; progress = 1;
      summary = S(Items(Get(Get(data, "tool_input") as Dictionary<string, object>, "questions")).FirstOrDefault(), "question");
    } else return;

    string key = session != "" ? session : (Environment.GetEnvironmentVariable("WT_SESSION") ?? "");
    if (key == "") key = project;
    if (ev == "SubagentStop") key += ":btw";  // do not replace the session's other card
    var fields = new List<KeyValuePair<string, string>> {
      new KeyValuePair<string, string>("key", key), new KeyValuePair<string, string>("project", Clean(project, 80)),
      new KeyValuePair<string, string>("label", label), new KeyValuePair<string, string>("color", color),
      new KeyValuePair<string, string>("summary", Clean(summary, 140)), new KeyValuePair<string, string>("glyph", glyph),
      new KeyValuePair<string, string>("progress", progress.ToString(CultureInfo.InvariantCulture)),
      new KeyValuePair<string, string>("hint", Hint), new KeyValuePair<string, string>("demo", args.Contains("--demo") ? "1" : "0"),
      new KeyValuePair<string, string>("marker", ""), new KeyValuePair<string, string>("title", AiTitle(transcript)) };
    string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", fields.Select(kv => kv.Key + "\t" + kv.Value))));
    // detached child (no inherited handles) so the hook returns at once; it joins or becomes the host
    try { Process.Start(new ProcessStartInfo(exe, b64) { UseShellExecute = true }); } catch { }
  }
}

class Host {
  const double CardSeconds = 1800, BadgeSeconds = 1800, LocateMs = 3000;
  const int MaxCards = 4;

  readonly string pipeName, foundDir;
  readonly Mutex mutex;
  readonly object gate = new object();
  Application app; Dispatcher disp;
  readonly List<Alert> alerts = new List<Alert>();
  readonly Dictionary<IntPtr, string> badgeOwner = new Dictionary<IntPtr, string>();
  readonly Dictionary<IntPtr, IntPtr> badgeIcon = new Dictionary<IntPtr, IntPtr>();
  ITaskbarList3 tb;
  Window pill; TextBlock pillText;
  DateTime emptySince = DateTime.Now;
  volatile bool exiting; bool uiaBusy; int tickNo;

  public Host(string pipeName, string foundDir, Mutex mutex) { this.pipeName = pipeName; this.foundDir = foundDir; this.mutex = mutex; }

  // ---------- entry ----------
  [STAThread]
  static void Main(string[] args) {
    string msg = args.Length > 0 ? args[0] : "";
    string exe = Assembly.GetEntryAssembly().Location;
    if (msg == "--hook") { HookMode.Run(args, exe); return; }
    string errLog = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe), "error.log");
    AppDomain.CurrentDomain.UnhandledException += (s, e) => {
      try { File.AppendAllText(errLog, DateTime.Now.ToString("s") + " " + e.ExceptionObject + "\r\n"); } catch { }
    };
    string name = System.IO.Path.GetFileNameWithoutExtension(exe) + "-" + Environment.UserName;
    var mutex = new Mutex(false, "Local\\" + name);
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < 8000) {
      if (TrySend(name, msg)) return;
      bool own;
      try { own = mutex.WaitOne(0); } catch (AbandonedMutexException) { own = true; }
      if (own) { new Host(name, System.IO.Path.GetDirectoryName(exe), mutex).Run(msg); return; }
      Thread.Sleep(150);
    }
  }

  static bool TrySend(string pipe, string msg) {
    try {
      using (var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut)) {
        c.Connect(250);
        var b = Encoding.UTF8.GetBytes(msg + "\n");
        c.Write(b, 0, b.Length); c.Flush();
        return c.ReadByte() == '1';
      }
    } catch { return false; }
  }

  void Run(string first) {
    app = new Application(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    disp = app.Dispatcher;
    string errLog = System.IO.Path.Combine(foundDir, "error.log");
    app.DispatcherUnhandledException += (s, e) => {
      try { File.AppendAllText(errLog, DateTime.Now.ToString("s") + " " + e.Exception + "\r\n"); } catch { }
      e.Handled = true;
    };
    var t = new Thread(PipeLoop); t.IsBackground = true; t.Start();
    disp.BeginInvoke(new Action(() => Add(first)));
    var timer = new DispatcherTimer(); timer.Interval = TimeSpan.FromMilliseconds(300);
    timer.Tick += (s, e) => Tick(); timer.Start();
    app.Run();
    alerts.Clear(); UpdateBadges();
    try { mutex.ReleaseMutex(); } catch { }
  }

  void PipeLoop() {
    while (true) {
      try {
        using (var s = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte)) {
          s.WaitForConnection();
          var buf = new List<byte>(); int b;
          while ((b = s.ReadByte()) >= 0 && b != '\n') buf.Add((byte)b);
          string m = Encoding.UTF8.GetString(buf.ToArray());
          bool ok;
          lock (gate) { ok = !exiting; if (ok) disp.BeginInvoke(new Action(() => Add(m))); }
          s.WriteByte(ok ? (byte)'1' : (byte)'0'); s.Flush();
          try { s.WaitForPipeDrain(); } catch { }
        }
      } catch { Thread.Sleep(200); }
    }
  }

  // ---------- payload ----------
  readonly object logLock = new object();  // locate threads log concurrently; unlocked appends collide and get dropped
  void Log(string line) {
    lock (logLock) try {
      string f = System.IO.Path.Combine(foundDir, "host.log");
      if (File.Exists(f) && new FileInfo(f).Length > 200000) File.Delete(f);
      File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + "\r\n");
    } catch { }
  }

  static Dictionary<string, string> Parse(string b64) {
    var d = new Dictionary<string, string>();
    try {
      foreach (var line in Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('\n')) {
        int i = line.IndexOf('\t'); if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
      }
    } catch { }
    return d;
  }
  static string G(Dictionary<string, string> d, string k) { string v; return d.TryGetValue(k, out v) ? v : ""; }

  void Add(string b64) {
    var d = Parse(b64);
    if (G(d, "label") == "") return;
    var a = new Alert();
    a.Project = G(d, "project"); a.Label = G(d, "label"); a.Summary = G(d, "summary"); a.Hint = G(d, "hint"); a.Glyph = G(d, "glyph");
    a.Key = G(d, "key") != "" ? G(d, "key") : a.Project;
    a.Marker = G(d, "marker"); a.Title = G(d, "title"); a.Demo = G(d, "demo") == "1";
    int.TryParse(G(d, "progress"), out a.Progress);
    try { a.Color = (Color)ColorConverter.ConvertFromString(G(d, "color")); } catch { a.Color = Color.FromRgb(0x60, 0xA5, 0xFA); }

    foreach (var old in alerts.Where(x => x.Key == a.Key).ToList()) Dismiss(old);   // same tab/session: replace
    alerts.Add(a); emptySince = DateTime.MaxValue;
    BuildCard(a);
    StartLocate(a);
  }

  // ---------- tab lookup (UI Automation runs off the UI thread) ----------
  void StartLocate(Alert a) {
    string marker = a.Marker, title = a.Title;
    ThreadPool.QueueUserWorkItem(_ => {
      IntPtr hwnd = IntPtr.Zero; AutomationElement tab = null; bool sel = false; string how = "fallback-window";
      // title may hold several candidates (custom /rename name first, then AI title), separated by \x1f
      foreach (string t in title.Split('\x1f')) {
        if (t == "" || tab != null) continue;
        try { FindTabByTitle(t, out hwnd, out tab); } catch { }
        if (tab != null) how = "title";
      }
      if (tab == null && marker != "") {
        var sw = Stopwatch.StartNew();
        while (tab == null && sw.ElapsedMilliseconds < LocateMs) {
          try { FindTab(marker, out hwnd, out tab); } catch { }
          if (tab == null) Thread.Sleep(100);
        }
        if (tab != null) {
          how = "marker";
          try { File.WriteAllText(System.IO.Path.Combine(foundDir, "found-" + marker), ""); } catch { }
        }
      }
      if (tab != null) { try { sel = ((SelectionItemPattern)tab.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected; } catch { } }
      Log(string.Format("locate key={0} by={1} selected={2}", a.Key.Length > 8 ? a.Key.Substring(0, 8) : a.Key, how, sel));
      disp.BeginInvoke(new Action(() => OnLocated(a, hwnd, tab, sel)));
    });
  }

  // Claude Code titles its tab "<spinner> <ai title>"; only trust a unique match
  static void FindTabByTitle(string title, out IntPtr hwnd, out AutomationElement tab) {
    hwnd = IntPtr.Zero; tab = null; int hits = 0;
    var wins = AutomationElement.RootElement.FindAll(TreeScope.Children,
      new PropertyCondition(AutomationElement.ClassNameProperty, W.WtClass));
    var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
    foreach (AutomationElement w in wins) {
      foreach (AutomationElement t in w.FindAll(TreeScope.Descendants, cond)) {
        string n = (t.Current.Name ?? "").Trim();
        if (n == title || n.EndsWith(" " + title)) { hits++; hwnd = new IntPtr(w.Current.NativeWindowHandle); tab = t; }
      }
    }
    if (hits != 1) { hwnd = IntPtr.Zero; tab = null; }
  }

  static void FindTab(string marker, out IntPtr hwnd, out AutomationElement tab) {
    hwnd = IntPtr.Zero; tab = null;
    var wins = AutomationElement.RootElement.FindAll(TreeScope.Children,
      new PropertyCondition(AutomationElement.ClassNameProperty, W.WtClass));
    var cond = new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                                new PropertyCondition(AutomationElement.NameProperty, marker));
    foreach (AutomationElement w in wins) {
      var t = w.FindFirst(TreeScope.Descendants, cond);
      if (t != null) { hwnd = new IntPtr(w.Current.NativeWindowHandle); tab = t; return; }
    }
  }

  void OnLocated(Alert a, IntPtr hwnd, AutomationElement tab, bool sel) {
    if (hwnd == IntPtr.Zero) hwnd = W.FindWindow(W.WtClass, null);
    a.Wt = hwnd; a.Tab = tab; a.TabSelected = sel; a.Located = true;
    bool watching = hwnd != IntPtr.Zero && W.GetForegroundWindow() == hwnd && (tab == null || sel);
    a.WaitFg = !a.Demo && !watching && hwnd != IntPtr.Zero;
    if (a.Closing || hwnd == IntPtr.Zero || (watching && !a.Demo)) return;
    Flash(hwnd); a.Badged = true; UpdateBadges();
  }

  // ---------- watcher ----------
  void Tick() {
    tickNo++;
    var now = DateTime.Now; IntPtr fg = W.GetForegroundWindow();
    foreach (var a in alerts.ToList()) {
      if (a.Closing) continue;
      double age = (now - a.Created).TotalSeconds;
      if (a.Located && a.Wt != IntPtr.Zero && !W.IsWindow(a.Wt)) { Dismiss(a); continue; }   // terminal window closed
      if (a.TabGone) { Dismiss(a); continue; }                                                // tab closed
      bool watching = a.Located && a.Wt != IntPtr.Zero && fg == a.Wt && (a.Tab == null || a.TabSelected);
      if (a.WaitFg && watching && age > 0.8) { Dismiss(a); continue; }
      if (age > CardSeconds) { if (!a.WaitFg) { Dismiss(a); continue; } if (!a.CardHidden) HideCard(a); }
      if (age > BadgeSeconds) Dismiss(a);
    }

    if (alerts.Count == 0 && (now - emptySince).TotalSeconds > 3) {
      lock (gate) exiting = true;
      disp.BeginInvoke(DispatcherPriority.Background, new Action(() => {
        if (alerts.Count == 0) app.Shutdown(); else lock (gate) exiting = false;
      }));
      return;
    }

    if (uiaBusy) return;
    var snap = alerts.Where(x => x.Located && x.Tab != null && !x.Closing).ToList();
    if (snap.Count == 0) return;
    uiaBusy = true; bool checkAlive = tickNo % 10 == 0;
    ThreadPool.QueueUserWorkItem(_ => {
      foreach (var a in snap) {
        try {
          if (fg == a.Wt) a.TabSelected = ((SelectionItemPattern)a.Tab.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected;
          else { a.TabSelected = false; if (checkAlive) { var n = a.Tab.Current.Name; } }
        } catch (ElementNotAvailableException) { a.TabGone = true; } catch { }
      }
      disp.BeginInvoke(new Action(() => uiaBusy = false));
    });
  }

  // ---------- cards ----------
  const string CardXaml = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
  WindowStyle='None' AllowsTransparency='True' Background='Transparent' Topmost='True' ShowInTaskbar='False' ShowActivated='False'
  ResizeMode='NoResize' SizeToContent='Height' Width='396' WindowStartupLocation='Manual' Left='-10000' Top='-10000' Opacity='0'
  FontFamily='Segoe UI Variable Text, Segoe UI, Microsoft JhengHei UI'>
  <Grid Margin='18'>
    <Grid.RenderTransform><TranslateTransform x:Name='Slide' X='48'/></Grid.RenderTransform>
    <Border x:Name='Card' CornerRadius='14' Background='#F2202024' BorderBrush='#2EFFFFFF' BorderThickness='1' Cursor='Hand'>
      <Border.Effect><DropShadowEffect BlurRadius='28' ShadowDepth='6' Direction='270' Opacity='0.5' Color='Black'/></Border.Effect>
      <Grid>
        <Grid.ColumnDefinitions><ColumnDefinition Width='5'/><ColumnDefinition/></Grid.ColumnDefinitions>
        <Border x:Name='Accent' CornerRadius='14,0,0,14'/>
        <StackPanel Grid.Column='1' Margin='18,14,18,14'>
          <DockPanel LastChildFill='True'>
            <Ellipse x:Name='Dot' Width='8' Height='8' VerticalAlignment='Center' Margin='0,0,8,0'/>
            <TextBlock x:Name='Time' DockPanel.Dock='Right' Foreground='#80FFFFFF' FontSize='11' VerticalAlignment='Center'/>
            <TextBlock Text='Claude Code' Foreground='#B3FFFFFF' FontSize='11.5' FontWeight='SemiBold' VerticalAlignment='Center'/>
          </DockPanel>
          <TextBlock x:Name='Project' Foreground='White' FontSize='16' FontWeight='SemiBold' Margin='0,10,0,0' TextTrimming='CharacterEllipsis'/>
          <TextBlock x:Name='Label' FontSize='13.5' FontWeight='SemiBold' Margin='0,2,0,0'/>
          <TextBlock x:Name='Summary' Foreground='#D0FFFFFF' FontSize='12.5' TextWrapping='Wrap' TextTrimming='CharacterEllipsis'
                     LineHeight='19' MaxHeight='57' Margin='0,8,0,0'/>
          <TextBlock x:Name='Hint' Foreground='#66FFFFFF' FontSize='11' HorizontalAlignment='Right' Margin='0,12,0,0'/>
        </StackPanel>
      </Grid>
    </Border>
  </Grid>
</Window>";

  static SolidColorBrush Br(Color c, byte alpha) { return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)); }

  static void NoActivate(Window w) {
    w.SourceInitialized += (s, e) => {
      var h = new WindowInteropHelper(w).Handle;
      W.SetWindowLong(h, -20, W.GetWindowLong(h, -20) | 0x80 | 0x08000000);   // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
    };
  }

  void BuildCard(Alert a) {
    var w = (Window)XamlReader.Parse(CardXaml);
    a.Card = w;   // Loaded can fire inside Show()
    ((Border)w.FindName("Accent")).Background = Br(a.Color, 255);
    ((Ellipse)w.FindName("Dot")).Fill = Br(a.Color, 255);
    ((Border)w.FindName("Card")).BorderBrush = Br(a.Color, 70);
    var label = (TextBlock)w.FindName("Label"); label.Text = a.Label; label.Foreground = Br(a.Color, 255);
    ((TextBlock)w.FindName("Time")).Text = a.Created.ToString("HH:mm");
    ((TextBlock)w.FindName("Project")).Text = a.Project;
    var sum = (TextBlock)w.FindName("Summary"); sum.Text = a.Summary;
    if (a.Summary == "") sum.Visibility = Visibility.Collapsed;
    ((TextBlock)w.FindName("Hint")).Text = a.Hint;
    NoActivate(w);
    w.MouseLeftButtonUp += (s, e) => { FocusTab(a); Dismiss(a); };
    w.MouseRightButtonUp += (s, e) => Dismiss(a);
    w.Loaded += (s, e) => { a.Loaded = true; Layout(); };
    w.Show();
  }

  static void Anim(DependencyObject o, DependencyProperty p, double from, double to, int ms) {
    var an = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms));
    var ease = new CubicEase(); ease.EasingMode = EasingMode.EaseOut; an.EasingFunction = ease;
    ((IAnimatable)o).BeginAnimation(p, an);
  }

  static void FadeOut(Window w, int ms, Action done) {
    var an = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(ms));
    an.Completed += (s, e) => done();
    w.BeginAnimation(UIElement.OpacityProperty, an);
  }

  // newest card sits in the corner, older ones stack upwards; past MaxCards a "+N" pill appears on top
  void Layout() {
    var vis = alerts.Where(x => !x.Closing && !x.CardHidden).OrderByDescending(x => x.Created).ToList();
    if (vis.Take(MaxCards).Any(x => !x.Loaded)) return;   // wait until every shown card has measured itself
    var wa = SystemParameters.WorkArea;
    double bottom = wa.Bottom; int shown = 0;
    foreach (var a in vis) {
      var w = a.Card;
      if (shown >= MaxCards) { if (!a.Overflow) { a.Overflow = true; w.Hide(); } continue; }
      if (a.Overflow) { a.Overflow = false; w.Show(); }
      double top = bottom - w.ActualHeight;
      w.Left = wa.Right - w.ActualWidth;
      if (!a.Positioned) {
        a.Positioned = true; w.Top = top;
        Anim(w, UIElement.OpacityProperty, 0, 1, 320);
        Anim((TranslateTransform)w.FindName("Slide"), TranslateTransform.XProperty, 48, 0, 420);
      } else if (Math.Abs(a.TargetTop - top) > 0.5) {
        Anim(w, Window.TopProperty, w.Top, top, 260);
      }
      a.TargetTop = top;
      bottom = top + 26;   // 18px shadow margin on each window -> 10px visual gap
      shown++;
    }
    int extra = vis.Count - shown;
    if (extra <= 0) { if (pill != null) pill.Hide(); return; }
    EnsurePill();
    pillText.Text = "还有 " + extra + " 个提醒";
    pill.Show(); pill.UpdateLayout();
    pill.Left = wa.Right - 18 - pill.ActualWidth;
    pill.Top = bottom - 16 - pill.ActualHeight;
  }

  void EnsurePill() {
    if (pill != null) return;
    pill = new Window();
    pill.WindowStyle = WindowStyle.None; pill.AllowsTransparency = true; pill.Background = Brushes.Transparent;
    pill.Topmost = true; pill.ShowInTaskbar = false; pill.ShowActivated = false; pill.ResizeMode = ResizeMode.NoResize;
    pill.SizeToContent = SizeToContent.WidthAndHeight; pill.Left = -10000; pill.Top = -10000;
    pillText = new TextBlock(); pillText.Foreground = Br(Colors.White, 0xCC); pillText.FontSize = 12;
    pillText.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft JhengHei UI");
    var b = new Border(); b.CornerRadius = new CornerRadius(10); b.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x24));
    b.BorderBrush = Br(Colors.White, 0x2E); b.BorderThickness = new Thickness(1); b.Padding = new Thickness(12, 5, 12, 5);
    b.Child = pillText; pill.Content = b;
    NoActivate(pill);
  }

  void HideCard(Alert a) {
    a.CardHidden = true;
    var w = a.Card;
    if (a.Overflow || !w.IsVisible) { w.Hide(); Layout(); return; }
    FadeOut(w, 300, () => { if (a.CardHidden) w.Hide(); });
    Layout();
  }

  void Dismiss(Alert a) {
    if (a.Closing) return;
    a.Closing = true; alerts.Remove(a);
    var w = a.Card;
    if (w != null) { if (w.IsVisible && !a.CardHidden) FadeOut(w, 220, () => w.Close()); else w.Close(); }
    UpdateBadges(); Layout();
    if (alerts.Count == 0) emptySince = DateTime.Now;
  }

  void FocusTab(Alert a) {
    IntPtr h = a.Wt != IntPtr.Zero ? a.Wt : W.FindWindow(W.WtClass, null);
    if (h == IntPtr.Zero) return;
    if (W.IsIconic(h)) W.ShowWindow(h, 9);
    if (!W.SetForegroundWindow(h)) {   // Alt tap lets a background process hand focus over
      W.keybd_event(0x12, 0, 0, UIntPtr.Zero); W.keybd_event(0x12, 0, 2, UIntPtr.Zero);
      W.SetForegroundWindow(h);
    }
    var tab = a.Tab;
    string key = a.Key.Length > 8 ? a.Key.Substring(0, 8) : a.Key;
    ThreadPool.QueueUserWorkItem(_ => {
      int[] old = null;   // terminal of the tab shown before the switch
      if (tab != null) try {
        var sel = (SelectionItemPattern)tab.GetCurrentPattern(SelectionItemPattern.Pattern);
        if (!sel.Current.IsSelected) { old = TermRid(h); sel.Select(); }
      } catch { }
      Log(string.Format("focus key={0} {1}", key, FocusTerminal(h, old)));
    });
  }

  static readonly PropertyCondition TermCond = new PropertyCondition(AutomationElement.ClassNameProperty, "TermControl");

  static int[] TermRid(IntPtr h) {
    try { var t = AutomationElement.FromHandle(h).FindFirst(TreeScope.Descendants, TermCond); return t == null ? null : t.GetRuntimeId(); }
    catch { return null; }
  }

  // UIA Select only switches the tab; keyboard focus does not follow, so typing / Enter went nowhere.
  // Wait for the new tab's terminal (only the shown tab's TermControl is in the tree) and focus it.
  static string FocusTerminal(IntPtr h, int[] old) {
    string last = "none";
    for (int i = 0; i < 12; i++) {
      Thread.Sleep(i == 0 ? 30 : 80);
      try {
        var term = AutomationElement.FromHandle(h).FindFirst(TreeScope.Descendants, TermCond);
        if (term == null) { last = "no-term"; continue; }
        if (old != null && i < 8 && term.GetRuntimeId().SequenceEqual(old)) { last = "old-term"; continue; }
        if (term.Current.HasKeyboardFocus) return "ok(already)";
        term.SetFocus();
        if (AutomationElement.FocusedElement.Current.ClassName == "TermControl") return "ok(set)";
        last = "focused=" + AutomationElement.FocusedElement.Current.ClassName;
      } catch (Exception e) { last = e.GetType().Name; }
    }
    return "fail(" + last + ")";
  }

  // ---------- taskbar ----------
  ITaskbarList3 TB() { if (tb == null) { tb = (ITaskbarList3)new TaskbarListCls(); tb.HrInit(); } return tb; }

  static void Flash(IntPtr h) {
    var fi = new W.FLASHWINFO();
    fi.cbSize = (uint)Marshal.SizeOf(typeof(W.FLASHWINFO)); fi.hwnd = h; fi.dwFlags = 14;   // FLASHW_TRAY | FLASHW_TIMERNOFG
    W.FlashWindowEx(ref fi);
  }

  // each terminal window shows the badge of its newest pending alert; cleared when none is left
  void UpdateBadges() {
    var want = new Dictionary<IntPtr, Alert>();
    foreach (var a in alerts) {
      if (!a.Badged || a.Closing || a.Wt == IntPtr.Zero) continue;
      Alert cur;
      if (!want.TryGetValue(a.Wt, out cur) || a.Created > cur.Created) want[a.Wt] = a;
    }
    foreach (var h in badgeOwner.Keys.ToList()) {
      if (want.ContainsKey(h)) continue;
      try { TB().SetOverlayIcon(h, IntPtr.Zero, ""); TB().SetProgressState(h, 0); } catch { }
      FreeIcon(h); badgeOwner.Remove(h);
    }
    foreach (var kv in want) {
      string owner;
      if (badgeOwner.TryGetValue(kv.Key, out owner) && owner == kv.Value.Id) continue;
      IntPtr icon = MakeIcon(kv.Value);
      int p = kv.Value.Progress;
      try {
        TB().SetOverlayIcon(kv.Key, icon, "Claude Code");
        TB().SetProgressState(kv.Key, p);
        if (p == 2 || p == 4 || p == 8) TB().SetProgressValue(kv.Key, 100, 100);
      } catch { }
      FreeIcon(kv.Key);
      badgeIcon[kv.Key] = icon; badgeOwner[kv.Key] = kv.Value.Id;
    }
  }

  void FreeIcon(IntPtr h) {
    IntPtr icon;
    if (badgeIcon.TryGetValue(h, out icon)) { W.DestroyIcon(icon); badgeIcon.Remove(h); }
  }

  static IntPtr MakeIcon(Alert a) {
    using (var bmp = new System.Drawing.Bitmap(32, 32))
    using (var g = System.Drawing.Graphics.FromImage(bmp)) {
      g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
      using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, a.Color.R, a.Color.G, a.Color.B)))
        g.FillEllipse(fill, 1, 1, 30, 30);
      using (var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2)) g.DrawEllipse(pen, 1, 1, 30, 30);
      using (var sf = new System.Drawing.StringFormat())
      using (var font = new System.Drawing.Font("Segoe UI Symbol", 16, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel)) {
        sf.Alignment = System.Drawing.StringAlignment.Center; sf.LineAlignment = System.Drawing.StringAlignment.Center;
        g.DrawString(a.Glyph, font, System.Drawing.Brushes.White, new System.Drawing.RectangleF(0, 1, 32, 32), sf);
      }
      return bmp.GetHicon();
    }
  }
}
}
