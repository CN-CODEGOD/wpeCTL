// WpeCtl — 控制 Wallpaper Engine 的模块（C# 5 / .NET Framework 4.0）
//
// 两条通道，各自负责能做的那部分：
//   1) 读状态 —— 直接解析引擎的 config.json
//      <user>.general.wallpaperconfig.selectedwallpapers.Monitor<N>.file
//      （引擎写完即落盘，比抓它控制台输出可靠：control 客户端会把 stdout 从管道 detach 掉）
//   2) 发命令 —— 投递 WM_COMMAND，或调用引擎自带 control CLI
//      wallpaper64.exe -control play|pause|stop|mute|unmute|openWallpaper -file <p>|openPlaylist -playlist <n>
//
// control 命令名来自 wallpaper64.exe 的 FUN_140008270 分派；
// WM_COMMAND id 来自 FUN_140021e50 的 0x111 分支。
//
// 编译： tools\build.cmd

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace WpeCtl
{
    internal static class Native
    {
        public const uint WM_COMMAND = 0x0111;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder sb, int max);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    }

    /// <summary>WM_COMMAND id → 语义（引擎内部热键/托盘菜单那一组，均为 toggle）。</summary>
    public static class Commands
    {
        public const uint OpenEditor = 0x9c52;
        public const uint OpenSettings = 0x9c53;
        public const uint Pause = 0x9c56;
        public const uint Stop = 0x9c57;
        public const uint OpenBrowser = 0x9c59;
        public const uint Screenshot = 0x9c5b;
        public const uint NextWallpaper = 0x9c5d;
        public const uint Mute = 0x9c5f;

        public static Dictionary<string, uint> ByName()
        {
            Dictionary<string, uint> d = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            d["editor"] = OpenEditor;
            d["settings"] = OpenSettings;
            d["pause"] = Pause;
            d["stop"] = Stop;
            d["browse"] = OpenBrowser;
            d["screenshot"] = Screenshot;
            d["next"] = NextWallpaper;
            d["mute"] = Mute;
            return d;
        }
    }

    public sealed class WpeWindow
    {
        public IntPtr Handle;
        public uint ProcessId;
        public string ClassName = "";
        public string Title = "";
        public string ProcessName = "";
        public string ExecutablePath = "";

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "hwnd=0x{0:X} pid={1} proc={2} class={3} title=\"{4}\"",
                Handle.ToInt64(), ProcessId, ProcessName, ClassName, Title);
        }
    }

    public static class WpeEngine
    {
        private const string EventWindowClass = "WPEEventWindow";

        public static WpeWindow Describe(IntPtr hWnd)
        {
            WpeWindow w = new WpeWindow();
            w.Handle = hWnd;

            StringBuilder sb = new StringBuilder(256);
            Native.GetClassNameW(hWnd, sb, sb.Capacity);
            w.ClassName = sb.ToString();

            sb = new StringBuilder(512);
            Native.GetWindowTextW(hWnd, sb, sb.Capacity);
            w.Title = sb.ToString();

            uint pid;
            Native.GetWindowThreadProcessId(hWnd, out pid);
            w.ProcessId = pid;
            try
            {
                Process p = Process.GetProcessById((int)pid);
                w.ProcessName = p.ProcessName;
                try { w.ExecutablePath = p.MainModule.FileName; }
                catch (Exception) { w.ExecutablePath = ""; }
            }
            catch (ArgumentException)
            {
                w.ProcessName = "";
            }
            return w;
        }

        public static WpeWindow Find()
        {
            IntPtr h = Native.FindWindowW(EventWindowClass, null);
            if (h != IntPtr.Zero && Native.IsWindow(h))
                return Describe(h);

            WpeWindow fallback = null;
            Native.EnumProc cb = delegate(IntPtr hwnd, IntPtr l)
            {
                WpeWindow c = Describe(hwnd);
                if (c.ProcessName != null &&
                    c.ProcessName.StartsWith("wallpaper", StringComparison.OrdinalIgnoreCase) &&
                    c.ClassName.StartsWith("WPE", StringComparison.OrdinalIgnoreCase))
                {
                    fallback = c;
                    return false;
                }
                return true;
            };
            Native.EnumWindows(cb, IntPtr.Zero);
            return fallback;
        }

        public static List<WpeWindow> ListWindows()
        {
            List<WpeWindow> list = new List<WpeWindow>();
            Native.EnumProc cb = delegate(IntPtr hwnd, IntPtr l)
            {
                WpeWindow w = Describe(hwnd);
                if (w.ClassName.StartsWith("WPE", StringComparison.OrdinalIgnoreCase))
                    list.Add(w);
                return true;
            };
            Native.EnumWindows(cb, IntPtr.Zero);
            return list;
        }

        public static bool Post(IntPtr hWnd, uint commandId)
        {
            if (hWnd == IntPtr.Zero || !Native.IsWindow(hWnd))
                return false;
            return Native.PostMessageW(hWnd, Native.WM_COMMAND, new IntPtr(unchecked((int)commandId)), IntPtr.Zero);
        }
    }

    /// <summary>引擎目录、config.json 解析。</summary>
    public static class EngineInfo
    {
        public static string Directory()
        {
            WpeWindow w = WpeEngine.Find();
            if (w != null && !string.IsNullOrEmpty(w.ExecutablePath))
                return Path.GetDirectoryName(w.ExecutablePath);

            string dir = Environment.GetEnvironmentVariable("WALLPAPER_ENGINE_DIR");
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                return dir;
            return null;
        }

        public static string Exe()
        {
            string dir = Directory();
            if (dir == null) return null;
            string x64 = Path.Combine(dir, "wallpaper64.exe");
            if (File.Exists(x64)) return x64;
            string x86 = Path.Combine(dir, "wallpaper32.exe");
            if (File.Exists(x86)) return x86;
            return null;
        }

        public static string ConfigPath()
        {
            string dir = Directory();
            return dir == null ? null : Path.Combine(dir, "config.json");
        }

        /// <summary>读取并解析 config.json（带重试，避开引擎写入瞬间）。</summary>
        public static Dictionary<string, object> LoadConfig(out string path, out string error)
        {
            path = ConfigPath();
            error = null;
            if (path == null)
            {
                error = "engine directory not found";
                return null;
            }
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    ser.MaxJsonLength = 64 * 1024 * 1024;
                    Dictionary<string, object> root = ser.DeserializeObject(json) as Dictionary<string, object>;
                    if (root != null) return root;
                    error = "config root is not an object";
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                Thread.Sleep(120);
            }
            return null;
        }

        public static Dictionary<string, object> General(Dictionary<string, object> root)
        {
            if (root == null) return null;
            foreach (object userNode in root.Values)
            {
                Dictionary<string, object> user = userNode as Dictionary<string, object>;
                if (user == null) continue;
                object generalNode;
                if (user.TryGetValue("general", out generalNode))
                {
                    Dictionary<string, object> general = generalNode as Dictionary<string, object>;
                    if (general != null) return general;
                }
            }
            return null;
        }

        public static Dictionary<string, object> AsDict(object o)
        {
            return o as Dictionary<string, object>;
        }
    }

    /// <summary>引擎自带 control CLI（只用来发写命令）。</summary>
    public static class Control
    {
        private static readonly object Gate = new object();

        public sealed class Result
        {
            public bool Ok;
            public int ExitCode;
            public string Error = "";
        }

        public static Result Run(string controlArguments, int timeoutMs)
        {
            Result r = new Result();
            lock (Gate)
            {
                string exe = EngineInfo.Exe();
                if (exe == null)
                {
                    r.Error = "engine executable not found (set WALLPAPER_ENGINE_DIR)";
                    r.ExitCode = -1;
                    return r;
                }

                ProcessStartInfo psi = new ProcessStartInfo(exe, "-control " + controlArguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);

                try
                {
                    using (Process p = Process.Start(psi))
                    {
                        if (!p.WaitForExit(timeoutMs))
                        {
                            try { p.Kill(); } catch (Exception) { }
                            r.Error = "control command timed out";
                            return r;
                        }
                        r.ExitCode = p.ExitCode;
                        r.Ok = p.ExitCode == 0;
                        if (!r.Ok) r.Error = "exit code " + p.ExitCode.ToString(CultureInfo.InvariantCulture);
                        return r;
                    }
                }
                catch (Exception ex)
                {
                    r.Error = ex.Message;
                    r.ExitCode = -1;
                    return r;
                }
            }
        }

        public static bool Simple(string command, out string error)
        {
            Result r = Run(command, 20000);
            error = r.Ok ? null : r.Error;
            return r.Ok;
        }

        /// <summary>目录 → 其中的 project.json；文件原样返回。</summary>
        public static string ResolveTarget(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            path = path.Trim().Trim('"');
            if (System.IO.Directory.Exists(path))
            {
                string pj = Path.Combine(path, "project.json");
                return File.Exists(pj) ? pj : null;
            }
            return File.Exists(path) ? path : null;
        }

        public static bool OpenWallpaper(string path, int monitor, out string reported, out string error)
        {
            reported = null;
            error = null;
            string target = ResolveTarget(path);
            if (target == null)
            {
                error = "path not found or has no project.json: " + path;
                return false;
            }

            string args = "openWallpaper -file \"" + target + "\"";
            if (monitor >= 0) args += " -monitor " + monitor.ToString(CultureInfo.InvariantCulture);
            Result r = Run(args, 40000);
            if (!r.Ok) { error = r.Error; return false; }

            string expectedDir = Path.GetDirectoryName(target);
            string expectedDirNorm = expectedDir == null ? null : expectedDir.Replace('/', '\\');

            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(250);
                string cur = WallpaperState.Current(monitor, out error);
                if (cur == null) continue;
                reported = cur;
                string curNorm = cur.Replace('/', '\\');
                if (string.Equals(curNorm, target.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
                    return true;
                if (expectedDirNorm != null &&
                    curNorm.StartsWith(expectedDirNorm + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            error = "command accepted but config.json still reports: " + (reported ?? "(none)");
            return false;
        }

        /// <summary>
        /// config.json 报回的可能是打包产物（scene.pkg），还原时必须改投同目录的 project.json，
        /// 否则 openWallpaper 不认这种路径。
        /// </summary>
        public static string ResolveRestoreTarget(string reportedPath)
        {
            if (string.IsNullOrEmpty(reportedPath)) return null;
            string path = reportedPath.Trim().Trim('"');
            if (!File.Exists(path))
                return ResolveTarget(path);

            string name = Path.GetFileName(path).ToLowerInvariant();
            if (name.EndsWith(".pkg") || name.EndsWith(".tex"))
            {
                string pj = Path.Combine(Path.GetDirectoryName(path), "project.json");
                if (File.Exists(pj)) return pj;
            }
            return path;
        }

        public static bool OpenPlaylist(string name, int monitor, out string error)
        {
            string args = "openPlaylist -playlist \"" + name + "\"";
            if (monitor >= 0) args += " -monitor " + monitor.ToString(CultureInfo.InvariantCulture);
            Result r = Run(args, 40000);
            error = r.Ok ? null : r.Error;
            return r.Ok;
        }

        /// <summary>config.json 里的播放列表名（&lt;user&gt;.general.playlists[].name）。</summary>
        public static List<string> Playlists(out string configPath, out string error)
        {
            List<string> names = new List<string>();
            Dictionary<string, object> root = EngineInfo.LoadConfig(out configPath, out error);
            Dictionary<string, object> general = EngineInfo.General(root);
            if (general == null) return names;

            object listsNode;
            if (!general.TryGetValue("playlists", out listsNode)) return names;
            object[] lists = listsNode as object[];
            if (lists == null) return names;

            foreach (object item in lists)
            {
                Dictionary<string, object> entry = EngineInfo.AsDict(item);
                if (entry == null) continue;
                object nameNode;
                if (entry.TryGetValue("name", out nameNode) && nameNode != null)
                    names.Add(Convert.ToString(nameNode, CultureInfo.InvariantCulture));
            }
            return names;
        }

        /// <summary>一次 insertCurrent 的逐显示器记录。</summary>
        public sealed class InsertRecord
        {
            public string Monitor = "";
            public int Index;
            public string Original = "";
            public string Inserted;
            public bool Restored;
            public string Error;
        }

        private static int MonitorIndexOfKey(string key)
        {
            if (key == null) return 0;
            string digits = "";
            foreach (char c in key) { if (c >= '0' && c <= '9') digits += c; }
            int n;
            return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        /// <summary>
        /// 临时把 path 插到当前壁纸前面：holdMs 后（holdForever 时直到 Ctrl+C）自动回到原壁纸。
        /// monitor 小于 0 表示所有显示器。
        /// </summary>
        public static bool InsertCurrent(string path, int holdMs, bool holdForever, int monitor,
                                         out List<InsertRecord> records, out string error)
        {
            records = new List<InsertRecord>();
            error = null;

            Dictionary<string, string> originals;
            if (monitor >= 0)
            {
                string one = WallpaperState.Current(monitor, out error);
                if (one == null) return false;
                originals = new Dictionary<string, string>();
                originals["Monitor" + monitor.ToString(CultureInfo.InvariantCulture)] = one;
            }
            else
            {
                originals = WallpaperState.All(out error);
                if (originals.Count == 0)
                {
                    error = error ?? "no wallpaper state";
                    return false;
                }
            }

            string target = ResolveTarget(path);
            if (target == null)
            {
                error = "path not found or has no project.json: " + path;
                return false;
            }

            foreach (KeyValuePair<string, string> kv in originals)
            {
                InsertRecord rec = new InsertRecord();
                rec.Monitor = kv.Key;
                rec.Index = MonitorIndexOfKey(kv.Key);
                rec.Original = kv.Value;

                string inserted;
                string err;
                if (!OpenWallpaper(target, rec.Index, out inserted, out err))
                {
                    rec.Error = err;
                    records.Add(rec);
                    error = err;
                    RestoreRecords(records);
                    return false;
                }
                rec.Inserted = inserted;
                records.Add(rec);
            }

            Hold(holdMs, holdForever);
            RestoreRecords(records);

            bool allOk = true;
            foreach (InsertRecord r in records) { if (!r.Restored) allOk = false; }
            if (!allOk && error == null) error = "one or more monitors were not restored";
            return allOk;
        }

        private static void RestoreRecords(List<InsertRecord> records)
        {
            foreach (InsertRecord rec in records)
            {
                if (rec.Restored || string.IsNullOrEmpty(rec.Original)) continue;
                string restoreTarget = ResolveRestoreTarget(rec.Original);
                if (restoreTarget == null)
                {
                    rec.Error = "restore target not found: " + rec.Original;
                    continue;
                }

                string reported;
                string err = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (OpenWallpaper(restoreTarget, rec.Index, out reported, out err))
                    {
                        rec.Restored = true;
                        rec.Error = null;
                        break;
                    }
                    rec.Error = err;
                    Thread.Sleep(400);
                }
            }
        }

        private static volatile bool cancelRequested;

        private static void Hold(int holdMs, bool holdForever)
        {
            ConsoleCancelEventHandler handler = delegate(object s, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                cancelRequested = true;
            };
            Console.CancelKeyPress += handler;
            try
            {
                if (holdForever)
                {
                    while (!cancelRequested) Thread.Sleep(100);
                    return;
                }
                int waited = 0;
                while (waited < holdMs && !cancelRequested)
                {
                    int step = holdMs - waited < 100 ? holdMs - waited : 100;
                    Thread.Sleep(step);
                    waited += step;
                }
            }
            finally
            {
                Console.CancelKeyPress -= handler;
                cancelRequested = false;
            }
        }
    }

    /// <summary>从 config.json 读当前壁纸（引擎应用后立即落盘）。</summary>
    public static class WallpaperState
    {
        public static string Current(int monitor, out string error)
        {
            string path;
            Dictionary<string, object> root = EngineInfo.LoadConfig(out path, out error);
            Dictionary<string, object> general = EngineInfo.General(root);
            if (general == null)
            {
                error = error ?? "config.json has no <user>.general node";
                return null;
            }

            object cfgNode;
            if (!general.TryGetValue("wallpaperconfig", out cfgNode)) { error = "no wallpaperconfig"; return null; }
            Dictionary<string, object> cfg = EngineInfo.AsDict(cfgNode);
            if (cfg == null) { error = "wallpaperconfig is not an object"; return null; }

            object selNode;
            if (!cfg.TryGetValue("selectedwallpapers", out selNode)) { error = "no selectedwallpapers"; return null; }
            Dictionary<string, object> selected = EngineInfo.AsDict(selNode);
            if (selected == null) { error = "selectedwallpapers is not an object"; return null; }

            string key = "Monitor" + (monitor >= 0 ? monitor : 0).ToString(CultureInfo.InvariantCulture);
            object entryNode;
            if (!selected.TryGetValue(key, out entryNode))
            {
                foreach (string k in selected.Keys)
                {
                    key = k;
                    entryNode = selected[k];
                    break;
                }
            }
            Dictionary<string, object> entry = EngineInfo.AsDict(entryNode);
            if (entry == null) { error = "no entry for " + key; return null; }

            object fileNode;
            if (!entry.TryGetValue("file", out fileNode) || fileNode == null) { error = "entry has no file"; return null; }
            return Convert.ToString(fileNode, CultureInfo.InvariantCulture);
        }

        /// <summary>所有显示器的当前壁纸。</summary>
        public static Dictionary<string, string> All(out string error)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path;
            Dictionary<string, object> root = EngineInfo.LoadConfig(out path, out error);
            Dictionary<string, object> general = EngineInfo.General(root);
            if (general == null) return result;

            object cfgNode;
            if (!general.TryGetValue("wallpaperconfig", out cfgNode)) return result;
            Dictionary<string, object> cfg = EngineInfo.AsDict(cfgNode);
            if (cfg == null) return result;
            object selNode;
            if (!cfg.TryGetValue("selectedwallpapers", out selNode)) return result;
            Dictionary<string, object> selected = EngineInfo.AsDict(selNode);
            if (selected == null) return result;

            foreach (KeyValuePair<string, object> kv in selected)
            {
                Dictionary<string, object> entry = EngineInfo.AsDict(kv.Value);
                if (entry == null) continue;
                object fileNode;
                if (entry.TryGetValue("file", out fileNode) && fileNode != null)
                    result[kv.Key] = Convert.ToString(fileNode, CultureInfo.InvariantCulture);
            }
            return result;
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            bool json = false;
            int monitor = -1;
            int holdMs = 10000;
            bool keep = false;
            List<string> rest = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--json", StringComparison.OrdinalIgnoreCase)) json = true;
                else if (string.Equals(args[i], "--monitor", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    monitor = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (string.Equals(args[i], "--for", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    holdMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (string.Equals(args[i], "--keep", StringComparison.OrdinalIgnoreCase)) keep = true;
                else rest.Add(args[i]);
            }

            if (rest.Count == 0) { Usage(); return 1; }

            string verb = rest[0].ToLowerInvariant();
            switch (verb)
            {
                case "status": return CmdStatus(json);
                case "list":
                case "windows": return CmdList(json);
                case "current": return CmdCurrent(json, monitor);
                case "playlists": return CmdPlaylists(json);
                case "play":
                case "pause":
                case "stop":
                case "mute":
                case "unmute":
                case "next":
                case "hideicons":
                case "showicons": return CmdSimple(verb, json);
                case "set":
                    if (rest.Count < 2) { Usage(); return 1; }
                    return CmdSet(rest[1], json, monitor);
                case "playlist":
                    if (rest.Count < 2) { Usage(); return 1; }
                    return CmdPlaylist(rest[1], json, monitor);
                case "insert":
                case "insertcurrent":
                    if (rest.Count < 2) { Usage(); return 1; }
                    return CmdInsert(rest[1], json, monitor, holdMs, keep);
                case "toggle":
                    if (rest.Count < 2) { Usage(); return 1; }
                    return CmdToggle(rest[1], json);
                case "raw":
                    if (rest.Count < 2) { Usage(); return 1; }
                    return CmdRaw(rest[1], json);
                default:
                    Console.Error.WriteLine("unknown command: " + verb);
                    Usage();
                    return 1;
            }
        }

        private static int CmdStatus(bool json)
        {
            WpeWindow w = WpeEngine.Find();
            if (w == null)
            {
                if (json) Console.WriteLine("{\"found\":false,\"engine\":\"not running\"}");
                else Console.WriteLine("engine not found (wallpaper64/32.exe not running)");
                return 2;
            }
            if (json) Console.WriteLine("{\"found\":true,\"window\":" + ToJson(w) + "}");
            else Console.WriteLine("engine found: " + w.ToString());
            return 0;
        }

        private static int CmdList(bool json)
        {
            List<WpeWindow> all = WpeEngine.ListWindows();
            if (json)
            {
                Console.WriteLine("[");
                for (int i = 0; i < all.Count; i++)
                    Console.WriteLine((i > 0 ? "," : "") + ToJson(all[i]));
                Console.WriteLine("]");
            }
            else if (all.Count == 0) Console.WriteLine("no WPE* windows found");
            else foreach (WpeWindow w in all) Console.WriteLine(w.ToString());
            return 0;
        }

        private static int CmdCurrent(bool json, int monitor)
        {
            string error;
            if (monitor < 0)
            {
                Dictionary<string, string> all = WallpaperState.All(out error);
                if (all.Count == 0)
                {
                    if (json) Console.WriteLine("{\"ok\":false,\"error\":\"" + Escape(error ?? "no state") + "\"}");
                    else Console.Error.WriteLine("failed to read wallpaper state: " + error);
                    return 2;
                }
                if (json)
                {
                    StringBuilder sb = new StringBuilder("{\"ok\":true,\"monitors\":{");
                    bool first = true;
                    foreach (KeyValuePair<string, string> kv in all)
                    {
                        if (!first) sb.Append(",");
                        first = false;
                        sb.Append("\"" + Escape(kv.Key) + "\":\"" + Escape(kv.Value) + "\"");
                    }
                    sb.Append("}}");
                    Console.WriteLine(sb.ToString());
                }
                else foreach (KeyValuePair<string, string> kv in all) Console.WriteLine(kv.Key + ": " + kv.Value);
                return 0;
            }

            string cur = WallpaperState.Current(monitor, out error);
            if (cur == null)
            {
                if (json) Console.WriteLine("{\"ok\":false,\"error\":\"" + Escape(error ?? "no state") + "\"}");
                else Console.Error.WriteLine("failed to read wallpaper state: " + error);
                return 2;
            }
            if (json) Console.WriteLine("{\"ok\":true,\"monitor\":" + monitor.ToString(CultureInfo.InvariantCulture) +
                                       ",\"path\":\"" + Escape(cur) + "\"}");
            else Console.WriteLine(cur);
            return 0;
        }

        private static int CmdPlaylists(bool json)
        {
            string cfg, error;
            List<string> names = Control.Playlists(out cfg, out error);
            if (json)
            {
                StringBuilder sb = new StringBuilder("{\"ok\":true,\"config\":\"" + Escape(cfg ?? "") + "\",\"playlists\":[");
                for (int i = 0; i < names.Count; i++) sb.Append((i > 0 ? "," : "") + "\"" + Escape(names[i]) + "\"");
                sb.Append("]}");
                Console.WriteLine(sb.ToString());
            }
            else if (names.Count == 0) Console.WriteLine("no playlists defined in " + (cfg ?? "config.json"));
            else foreach (string n in names) Console.WriteLine(n);
            return 0;
        }

        private static int CmdSimple(string verb, bool json)
        {
            string command = verb;
            if (verb == "hideicons") command = "hideIcons";
            if (verb == "showicons") command = "showIcons";

            string error;
            bool ok = Control.Simple(command, out error);
            if (json)
                Console.WriteLine("{\"ok\":" + (ok ? "true" : "false") + ",\"command\":\"" + Escape(command) + "\"" +
                                  (ok ? "" : ",\"error\":\"" + Escape(error) + "\"") + "}");
            else if (ok) Console.WriteLine("ok: " + command);
            else Console.Error.WriteLine("failed: " + error);
            return ok ? 0 : 3;
        }

        private static int CmdSet(string path, bool json, int monitor)
        {
            string reported, error;
            bool ok = Control.OpenWallpaper(path, monitor, out reported, out error);
            if (json)
                Console.WriteLine("{\"ok\":" + (ok ? "true" : "false") + ",\"requested\":\"" + Escape(path) + "\"" +
                                  (reported != null ? ",\"current\":\"" + Escape(reported) + "\"" : "") +
                                  (ok ? "" : ",\"error\":\"" + Escape(error) + "\"") + "}");
            else if (ok) Console.WriteLine("now playing: " + (reported ?? path));
            else Console.Error.WriteLine("failed: " + error);
            return ok ? 0 : 3;
        }

        private static int CmdPlaylist(string name, bool json, int monitor)
        {
            string error;
            bool ok = Control.OpenPlaylist(name, monitor, out error);
            if (json)
                Console.WriteLine("{\"ok\":" + (ok ? "true" : "false") + ",\"playlist\":\"" + Escape(name) + "\"" +
                                  (ok ? "" : ",\"error\":\"" + Escape(error) + "\"") + "}");
            else if (ok) Console.WriteLine("ok: playlist " + name);
            else Console.Error.WriteLine("failed: " + error);
            return ok ? 0 : 3;
        }

        private static int CmdInsert(string path, bool json, int monitor, int holdMs, bool keep)
        {
            List<Control.InsertRecord> records;
            string error;
            bool ok = Control.InsertCurrent(path, holdMs, keep, monitor, out records, out error);

            if (json)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"ok\":" + (ok ? "true" : "false") + ",\"command\":\"insertCurrent\"");
                sb.Append(",\"requested\":\"" + Escape(path) + "\"");
                sb.Append(",\"hold\":\"" + (keep ? "until-ctrl-c" : holdMs.ToString(CultureInfo.InvariantCulture) + "ms") + "\"");
                sb.Append(",\"monitors\":[");
                for (int i = 0; i < records.Count; i++)
                {
                    Control.InsertRecord r = records[i];
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"monitor\":\"" + Escape(r.Monitor) + "\"");
                    sb.Append(",\"original\":\"" + Escape(r.Original) + "\"");
                    sb.Append(",\"inserted\":\"" + Escape(r.Inserted ?? "") + "\"");
                    sb.Append(",\"restored\":" + (r.Restored ? "true" : "false"));
                    if (r.Error != null) sb.Append(",\"error\":\"" + Escape(r.Error) + "\"");
                    sb.Append("}");
                }
                sb.Append("]");
                if (error != null) sb.Append(",\"error\":\"" + Escape(error) + "\"");
                sb.Append("}");
                Console.WriteLine(sb.ToString());
            }
            else
            {
                foreach (Control.InsertRecord r in records)
                {
                    Console.WriteLine(r.Monitor + ": " + r.Original + "  ->  " + (r.Inserted ?? "(failed)") +
                                      (r.Restored ? "  [restored]" : "  [NOT RESTORED]") +
                                      (r.Error != null ? "  " + r.Error : ""));
                }
                if (!ok) Console.Error.WriteLine("failed: " + error);
            }
            return ok ? 0 : 3;
        }

        private static int CmdToggle(string action, bool json)
        {
            uint id;
            if (!Commands.ByName().TryGetValue(action, out id))
            {
                Console.Error.WriteLine("unknown toggle: " + action);
                return 1;
            }
            WpeWindow w = WpeEngine.Find();
            if (w == null)
            {
                if (json) Console.WriteLine("{\"ok\":false,\"error\":\"engine-not-found\"}");
                else Console.Error.WriteLine("engine not found");
                return 2;
            }
            bool ok = WpeEngine.Post(w.Handle, id);
            if (json)
                Console.WriteLine("{\"ok\":" + (ok ? "true" : "false") + ",\"toggle\":\"" + Escape(action) +
                                  "\",\"id\":\"0x" + id.ToString("x", CultureInfo.InvariantCulture) + "\"}");
            else Console.WriteLine((ok ? "posted " : "FAILED ") + action);
            return ok ? 0 : 3;
        }

        private static int CmdRaw(string idText, bool json)
        {
            uint id = ParseId(idText);
            WpeWindow w = WpeEngine.Find();
            if (w == null)
            {
                if (json) Console.WriteLine("{\"ok\":false,\"error\":\"engine-not-found\"}");
                else Console.Error.WriteLine("engine not found");
                return 2;
            }
            bool ok = WpeEngine.Post(w.Handle, id);
            if (json)
                Console.WriteLine("{\"ok\":" + (ok ? "true" : "false") + ",\"id\":\"0x" +
                                  id.ToString("x", CultureInfo.InvariantCulture) + "\"}");
            else Console.WriteLine((ok ? "posted " : "FAILED ") + "0x" + id.ToString("x", CultureInfo.InvariantCulture));
            return ok ? 0 : 3;
        }

        private static uint ParseId(string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return uint.Parse(s, CultureInfo.InvariantCulture);
        }

        private static string Escape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string ToJson(WpeWindow w)
        {
            return "{\"hwnd\":\"0x" + w.Handle.ToInt64().ToString("x", CultureInfo.InvariantCulture) +
                   "\",\"pid\":" + w.ProcessId.ToString(CultureInfo.InvariantCulture) +
                   ",\"process\":\"" + Escape(w.ProcessName) + "\"" +
                   ",\"class\":\"" + Escape(w.ClassName) + "\"" +
                   ",\"title\":\"" + Escape(w.Title) + "\"}";
        }

        private static void Usage()
        {
            Console.Error.WriteLine("wpectl — 控制 Wallpaper Engine");
            Console.Error.WriteLine("  wpectl status|list [--json]");
            Console.Error.WriteLine("  wpectl current [--monitor N] [--json]        当前壁纸（读 config.json）");
            Console.Error.WriteLine("  wpectl set <path|project.json|项目目录> [--monitor N]");
            Console.Error.WriteLine("  wpectl play|pause|stop|mute|unmute|next");
            Console.Error.WriteLine("  wpectl playlists                             列出播放列表");
            Console.Error.WriteLine("  wpectl playlist <name> [--monitor N]");
            Console.Error.WriteLine("  wpectl insert <path> [--for ms|--keep] [--monitor N]");
                Console.Error.WriteLine("    临时插入一个壁纸，到点（默认 10000ms）或 Ctrl+C 后自动回到原壁纸；别名 insertcurrent");
                Console.Error.WriteLine("  wpectl toggle <pause|stop|mute|screenshot|browse|settings|editor>");
            Console.Error.WriteLine("  wpectl raw <id>");
            Console.Error.WriteLine("  exit: 0 ok, 1 usage, 2 engine not found, 3 command failed");
        }
    }
}