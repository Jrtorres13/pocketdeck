// PocketDeck - turns a phone into a Stream Deck style controller for this PC.
// Written in C# 5 so the compiler built into Windows (.NET Framework 4.x csc.exe) can build it.
// Build: csc /target:winexe /out:PocketDeck.exe /win32icon:icon.ico
//        /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll PocketDeck.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PocketDeck
{
    static class Program
    {
        public const string Version = "1.2.0";
        public static string AppDir;
        public static string DataDir;
        public static bool Headless;
        public static bool DryRun;
        public static Server Srv;
        static Mutex mutex;

        [STAThread]
        static void Main(string[] args)
        {
            AppDir = AppDomain.CurrentDomain.BaseDirectory;
            DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketDeck", "data");
            int port = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--headless") Headless = true;
                else if (args[i] == "--dry-run") DryRun = true;
                else if (args[i] == "--data" && i + 1 < args.Length) DataDir = args[++i];
                else if (args[i] == "--app" && i + 1 < args.Length) AppDir = args[++i];
                else if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
            }
            Directory.CreateDirectory(DataDir);

            bool created;
            mutex = new Mutex(true, Headless ? "PocketDeckHeadless" + port : "PocketDeckSingleInstance", out created);
            if (!created && !Headless)
            {
                // Already running: just open the editor.
                Config c = Config.Load();
                OpenUrl("http://localhost:" + c.Port + "/");
                return;
            }

            Config cfg = Config.Load();
            if (port > 0) cfg.Port = port;
            Srv = new Server(cfg);
            try { Srv.Start(); }
            catch (Exception ex)
            {
                if (Headless) { Console.Error.WriteLine(ex.ToString()); return; }
                MessageBox.Show("PocketDeck could not start its server.\n\n" + ex.Message, "PocketDeck");
                return;
            }

            if (Headless)
            {
                Console.WriteLine("PocketDeck listening on port " + Srv.ActivePort + " PIN " + cfg.Pin);
                Thread.Sleep(Timeout.Infinite);
                return;
            }
            if (Array.IndexOf(args, "--connect") >= 0) OpenUrl("http://localhost:" + Srv.ActivePort + "/#connect");
            Application.EnableVisualStyles();
            Application.Run(new TrayContext(cfg));
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }

    // ---------------------------------------------------------------- config
    class Config
    {
        public int Port = 8765;
        public string Pin = "";
        public List<Dictionary<string, object>> Devices = new List<Dictionary<string, object>>();
        static readonly object fileLock = new object();

        static string PathOf() { return Path.Combine(Program.DataDir, "config.json"); }

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                if (File.Exists(PathOf()))
                {
                    var d = Json.Parse(File.ReadAllText(PathOf())) as Dictionary<string, object>;
                    if (d != null)
                    {
                        if (d.ContainsKey("port")) c.Port = Convert.ToInt32(d["port"]);
                        if (d.ContainsKey("pin")) c.Pin = Convert.ToString(d["pin"]);
                        if (d.ContainsKey("devices") && d["devices"] is IEnumerable)
                            foreach (object o in (IEnumerable)d["devices"])
                                if (o is Dictionary<string, object>) c.Devices.Add((Dictionary<string, object>)o);
                    }
                }
            }
            catch { }
            if (string.IsNullOrEmpty(c.Pin)) { c.Pin = NewPin(); c.Save(); }
            return c;
        }

        public void Save()
        {
            lock (fileLock)
            {
                var d = new Dictionary<string, object>();
                d["port"] = Port; d["pin"] = Pin; d["devices"] = Devices;
                Util.WriteAtomic(PathOf(), Json.Write(d));
            }
        }

        public static string NewPin() { return (Util.RandomInt(900000) + 100000).ToString(); }

        public bool HasToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            lock (Devices)
            {
                foreach (var d in Devices)
                    if (d.ContainsKey("token") && Util.SafeEquals(Convert.ToString(d["token"]), token))
                    {
                        d["seen"] = DateTime.UtcNow.ToString("o");
                        return true;
                    }
            }
            return false;
        }
    }

    // ---------------------------------------------------------------- tray
    class TrayContext : ApplicationContext
    {
        NotifyIcon icon;
        Config cfg;
        ToolStripMenuItem pauseItem, startupItem;

        public TrayContext(Config c)
        {
            cfg = c;
            icon = new NotifyIcon();
            try { icon.Icon = new Icon(Path.Combine(Program.AppDir, "icon.ico")); }
            catch { icon.Icon = SystemIcons.Application; }
            icon.Text = "PocketDeck (port " + Program.Srv.ActivePort + ")";
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open deck editor", null, delegate { Open(""); });
            menu.Items.Add("Connect a phone...", null, delegate { Open("#connect"); });
            menu.Items.Add(new ToolStripSeparator());
            pauseItem = new ToolStripMenuItem("Pause all buttons", null, delegate
            {
                Program.Srv.Paused = !Program.Srv.Paused;
                pauseItem.Checked = Program.Srv.Paused;
                icon.Text = Program.Srv.Paused ? "PocketDeck (paused)" : "PocketDeck (port " + Program.Srv.ActivePort + ")";
            });
            menu.Items.Add(pauseItem);
            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate
            {
                Startup.Set(!Startup.IsOn());
                startupItem.Checked = Startup.IsOn();
            });
            startupItem.Checked = Startup.IsOn();
            menu.Items.Add(startupItem);
            menu.Items.Add("Unpair all phones (new PIN)", null, delegate
            {
                if (MessageBox.Show("Disconnect every paired phone and make a new PIN?", "PocketDeck", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    Program.Srv.UnpairAll();
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit PocketDeck", null, delegate { icon.Visible = false; Application.Exit(); Environment.Exit(0); });
            icon.ContextMenuStrip = menu;
            icon.DoubleClick += delegate { Open(""); };
            icon.BalloonTipClicked += delegate { Open("#connect"); };
            icon.Visible = true;
            if (cfg.Devices.Count == 0)
                icon.ShowBalloonTip(6000, "PocketDeck is running", "Click here to connect your phone.", ToolTipIcon.Info);
        }

        void Open(string hash) { Program.OpenUrl("http://localhost:" + Program.Srv.ActivePort + "/" + hash); }
    }

    static class Startup
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool IsOn()
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key)) return k != null && k.GetValue("PocketDeck") != null; }
            catch { return false; }
        }
        public static void Set(bool on)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (on) k.SetValue("PocketDeck", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("PocketDeck", false);
                }
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- http server
    class Server
    {
        Config cfg;
        TcpListener listener;
        public int ActivePort;
        public volatile bool Paused;
        int failedPins; DateTime lockedUntil = DateTime.MinValue;
        readonly object pairLock = new object();
        readonly object runLock = new object();

        public Server(Config c) { cfg = c; }

        public void Start()
        {
            Exception last = null;
            for (int p = cfg.Port; p < cfg.Port + 10; p++)
            {
                try
                {
                    listener = new TcpListener(IPAddress.Any, p);
                    listener.Start();
                    ActivePort = p;
                    last = null;
                    break;
                }
                catch (Exception ex) { last = ex; listener = null; }
            }
            if (last != null) throw last;
            var t = new Thread(AcceptLoop); t.IsBackground = true; t.Start();
        }

        public void UnpairAll()
        {
            lock (cfg.Devices) cfg.Devices.Clear();
            cfg.Pin = Config.NewPin();
            cfg.Save();
        }

        void AcceptLoop()
        {
            while (true)
            {
                try
                {
                    TcpClient c = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { Handle(c); });
                }
                catch { Thread.Sleep(50); }
            }
        }

        class Req
        {
            public string Method, Path, Query = "", Body = "";
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public bool Local;
            public string H(string k) { string v; return Headers.TryGetValue(k, out v) ? v : ""; }
        }

        void Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 15000; client.SendTimeout = 15000; client.NoDelay = true;
                    NetworkStream ns = client.GetStream();
                    Req r = Read(ns);
                    if (r == null) return;
                    IPEndPoint ep = client.Client.RemoteEndPoint as IPEndPoint;
                    r.Local = ep != null && IPAddress.IsLoopback(ep.Address);
                    Route(r, ns);
                }
                catch { }
            }
        }

        static Req Read(NetworkStream ns)
        {
            var head = new MemoryStream();
            int matched = 0;
            while (matched < 4)
            {
                int b = ns.ReadByte();
                if (b < 0) return null;
                head.WriteByte((byte)b);
                if (head.Length > 32768) return null;
                if ((matched % 2 == 0 && b == '\r') || (matched % 2 == 1 && b == '\n')) matched++;
                else matched = b == '\r' ? 1 : 0;
            }
            string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var r = new Req();
            r.Method = first[0].ToUpperInvariant();
            string target = first[1];
            int q = target.IndexOf('?');
            r.Path = q >= 0 ? target.Substring(0, q) : target;
            if (q >= 0) r.Query = target.Substring(q + 1);
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) r.Headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
            }
            int len = 0;
            int.TryParse(r.H("Content-Length"), out len);
            if (len > 25 * 1024 * 1024) return null;
            if (len > 0)
            {
                byte[] buf = new byte[len];
                int got = 0;
                while (got < len)
                {
                    int n = ns.Read(buf, got, len - got);
                    if (n <= 0) return null;
                    got += n;
                }
                r.Body = Encoding.UTF8.GetString(buf);
            }
            return r;
        }

        static void Send(NetworkStream ns, int code, string type, byte[] body)
        {
            string status = code == 200 ? "OK" : code == 401 ? "Unauthorized" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : code == 429 ? "Too Many Requests" : "Error";
            string h = "HTTP/1.1 " + code + " " + status + "\r\n" +
                       "Content-Type: " + type + "\r\n" +
                       "Content-Length: " + body.Length + "\r\n" +
                       "Cache-Control: no-store\r\n" +
                       "X-Content-Type-Options: nosniff\r\n" +
                       "Referrer-Policy: no-referrer\r\n" +
                       "Connection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(h);
            ns.Write(hb, 0, hb.Length);
            ns.Write(body, 0, body.Length);
            ns.Flush();
        }

        static void SendJson(NetworkStream ns, int code, object o)
        {
            Send(ns, code, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Json.Write(o)));
        }

        static Dictionary<string, object> Obj(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        // Host header must be an IP, localhost or this PC's name (blocks DNS-rebinding pages).
        static bool HostOk(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string h = host;
            if (h.StartsWith("["))
            {
                int e = h.IndexOf(']');
                return e > 0;
            }
            int c = h.LastIndexOf(':');
            if (c > 0) h = h.Substring(0, c);
            h = h.ToLowerInvariant();
            IPAddress ip;
            if (IPAddress.TryParse(h, out ip)) return true;
            if (h == "localhost") return true;
            string m = Environment.MachineName.ToLowerInvariant();
            return h == m || h == m + ".local" || h == m + ".lan" || h == m + ".home";
        }

        void Route(Req r, NetworkStream ns)
        {
            if (!HostOk(r.H("Host"))) { SendJson(ns, 403, Obj("error", "bad host")); return; }

            if (r.Method == "GET" && (r.Path == "/" || r.Path == "/index.html"))
            {
                string f = Path.Combine(Program.AppDir, "index.html");
                Send(ns, 200, "text/html; charset=utf-8", File.ReadAllBytes(f));
                return;
            }
            if (r.Method == "GET" && (r.Path == "/icon.png" || r.Path == "/icon.ico"))
            {
                string f = Path.Combine(Program.AppDir, r.Path.Substring(1));
                if (File.Exists(f)) Send(ns, 200, r.Path.EndsWith(".png") ? "image/png" : "image/x-icon", File.ReadAllBytes(f));
                else SendJson(ns, 404, Obj("error", "not found"));
                return;
            }
            if (r.Method == "GET" && r.Path == "/manifest.json")
            {
                var icons = new ArrayList();
                icons.Add(Obj("src", "/icon.png", "sizes", "512x512", "type", "image/png", "purpose", "any maskable"));
                SendJson(ns, 200, Obj("name", "PocketDeck", "short_name", "PocketDeck", "start_url", "/",
                    "display", "fullscreen", "orientation", "any", "background_color", "#0B1120", "theme_color", "#0B1120", "icons", icons));
                return;
            }
            if (!r.Path.StartsWith("/api/")) { SendJson(ns, 404, Obj("error", "not found")); return; }

            // Every API call needs this header. Browsers can't add it cross-site without CORS, which we never allow.
            if (r.H("X-Deck") != "1") { SendJson(ns, 403, Obj("error", "missing header")); return; }

            if (r.Path == "/api/hello")
            {
                bool authed = r.Local || cfg.HasToken(r.H("X-Deck-Token"));
                SendJson(ns, 200, Obj("app", "PocketDeck", "version", Program.Version, "pc", Environment.MachineName,
                    "paired", authed, "local", r.Local, "paused", Paused));
                return;
            }
            if (r.Path == "/api/pair" && r.Method == "POST") { Pair(r, ns); return; }

            bool ok = r.Local || cfg.HasToken(r.H("X-Deck-Token"));
            if (!ok) { SendJson(ns, 401, Obj("error", "not paired")); return; }

            switch (r.Path)
            {
                case "/api/profile":
                    if (r.Method == "GET")
                    {
                        string f = Path.Combine(Program.DataDir, "profile.json");
                        string body = File.Exists(f) ? File.ReadAllText(f, Encoding.UTF8) : "null";
                        Send(ns, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"rev\":\"" + ProfileRev() + "\",\"profile\":" + body + "}"));
                    }
                    else if (r.Method == "PUT" || r.Method == "POST")
                    {
                        object parsed = Json.Parse(r.Body);
                        if (!(parsed is Dictionary<string, object>)) { SendJson(ns, 400, Obj("error", "profile must be an object")); return; }
                        Util.WriteAtomic(Path.Combine(Program.DataDir, "profile.json"), r.Body);
                        SendJson(ns, 200, Obj("ok", true, "rev", ProfileRev()));
                    }
                    return;
                case "/api/run":
                    {
                        if (Paused) { SendJson(ns, 200, Obj("ok", false, "error", "Paused on the PC")); return; }
                        var a = Json.Parse(r.Body) as Dictionary<string, object>;
                        if (a == null) { SendJson(ns, 400, Obj("ok", false, "error", "bad action")); return; }
                        string err = null;
                        // One action at a time so multi-actions never interleave.
                        lock (runLock)
                        {
                            try { Actions.Run(a, 0); }
                            catch (Exception ex) { err = ex.Message; }
                        }
                        Sys.Invalidate();
                        SendJson(ns, 200, err == null ? Obj("ok", true, "state", Sys.State()) : Obj("ok", false, "error", err));
                        return;
                    }
                case "/api/state":
                    SendJson(ns, 200, Obj("ok", true, "state", Sys.State(), "paused", Paused, "rev", ProfileRev()));
                    return;
                case "/api/apps":
                    SendJson(ns, 200, Obj("apps", AppList.Get()));
                    return;
                case "/api/icon":
                    {
                        // Body: {"path": "C:\\...\\app.lnk"} for a program/file icon, or {"url": "https://site"} for a website icon.
                        var b = Json.Parse(r.Body) as Dictionary<string, object>;
                        string path = b != null && b.ContainsKey("path") ? Convert.ToString(b["path"]) : "";
                        string url = b != null && b.ContainsKey("url") ? Convert.ToString(b["url"]) : "";
                        try
                        {
                            string data = url != "" ? AppIcon.FromSite(url) : AppIcon.FromFile(path);
                            SendJson(ns, 200, Obj("ok", true, "data", data));
                        }
                        catch (Exception ex) { SendJson(ns, 200, Obj("ok", false, "error", ex.Message)); }
                        return;
                    }
                case "/api/audio-devices":
                    try { SendJson(ns, 200, Obj("ok", true, "out", Audio.Devices(0), "in", Audio.Devices(1))); }
                    catch (Exception ex) { SendJson(ns, 200, Obj("ok", false, "error", ex.Message)); }
                    return;
                case "/api/sessions":
                    {
                        var names = new ArrayList();
                        try { foreach (string k in (Program.DryRun ? new List<string> { "spotify", "discord", "chrome" } : new List<string>(Audio.Sessions().Keys))) names.Add(k); } catch { }
                        SendJson(ns, 200, Obj("ok", true, "apps", names));
                        return;
                    }
                case "/api/sounds":
                    try
                    {
                        if (r.Method == "POST")
                        {
                            var b = Json.Parse(r.Body) as Dictionary<string, object>;
                            if (b == null) throw new Exception("Bad upload");
                            string saved = Sounds.Save(Convert.ToString(b["name"]), Convert.ToString(b["data"]));
                            SendJson(ns, 200, Obj("ok", true, "name", saved, "sounds", Sounds.List()));
                        }
                        else SendJson(ns, 200, Obj("ok", true, "sounds", Sounds.List()));
                    }
                    catch (Exception ex) { SendJson(ns, 200, Obj("ok", false, "error", ex.Message)); }
                    return;
                case "/api/connect":
                    if (!r.Local) { SendJson(ns, 403, Obj("error", "only on the PC")); return; }
                    {
                        var devs = new ArrayList();
                        lock (cfg.Devices)
                            foreach (var d in cfg.Devices)
                                devs.Add(Obj("name", d.ContainsKey("name") ? d["name"] : "Phone", "added", d.ContainsKey("added") ? d["added"] : "", "seen", d.ContainsKey("seen") ? d["seen"] : ""));
                        SendJson(ns, 200, Obj("pin", cfg.Pin, "port", ActivePort, "urls", Util.LanUrls(ActivePort), "devices", devs, "pc", Environment.MachineName));
                    }
                    return;
                case "/api/unpair-all":
                    if (!r.Local) { SendJson(ns, 403, Obj("error", "only on the PC")); return; }
                    UnpairAll();
                    SendJson(ns, 200, Obj("ok", true, "pin", cfg.Pin));
                    return;
                case "/api/unpair":
                    {
                        string tok = r.H("X-Deck-Token");
                        lock (cfg.Devices) cfg.Devices.RemoveAll(d => d.ContainsKey("token") && Convert.ToString(d["token"]) == tok);
                        cfg.Save();
                        SendJson(ns, 200, Obj("ok", true));
                        return;
                    }
            }
            SendJson(ns, 404, Obj("error", "not found"));
        }

        static string ProfileRev()
        {
            string f = Path.Combine(Program.DataDir, "profile.json");
            return File.Exists(f) ? File.GetLastWriteTimeUtc(f).Ticks.ToString() : "0";
        }

        void Pair(Req r, NetworkStream ns)
        {
            var b = Json.Parse(r.Body) as Dictionary<string, object>;
            string pin = b != null && b.ContainsKey("pin") ? Convert.ToString(b["pin"]).Trim() : "";
            string name = b != null && b.ContainsKey("device") ? Convert.ToString(b["device"]) : "Phone";
            if (name.Length > 60) name = name.Substring(0, 60);
            lock (pairLock)
            {
                if (DateTime.UtcNow < lockedUntil) { SendJson(ns, 429, Obj("error", "Too many wrong PINs. Wait a minute.")); return; }
                if (!Util.SafeEquals(pin, cfg.Pin))
                {
                    failedPins++;
                    if (failedPins >= 5) { lockedUntil = DateTime.UtcNow.AddSeconds(60); failedPins = 0; }
                    SendJson(ns, 401, Obj("error", "Wrong PIN"));
                    return;
                }
                failedPins = 0;
                string token = Util.RandomHex(24);
                lock (cfg.Devices) cfg.Devices.Add(Obj("token", token, "name", name, "added", DateTime.UtcNow.ToString("o"), "seen", DateTime.UtcNow.ToString("o")));
                cfg.Save();
                SendJson(ns, 200, Obj("ok", true, "token", token, "pc", Environment.MachineName));
            }
        }
    }

    // ---------------------------------------------------------------- actions
    static class Actions
    {
        static string S(Dictionary<string, object> a, string k)
        {
            object v; return a.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : "";
        }

        public static void Run(Dictionary<string, object> a, int depth)
        {
            if (depth > 4) throw new Exception("Multi-action nested too deep");
            string type = S(a, "type");
            if (Program.DryRun)
            {
                File.AppendAllText(Path.Combine(Program.DataDir, "dryrun.log"), Json.Write(a) + "\n");
                if (type != "multi") return;
            }
            switch (type)
            {
                case "hotkey": Input.Hotkey(S(a, "keys")); break;
                case "text":
                    Input.Type(S(a, "text"));
                    if (S(a, "enter") == "True" || S(a, "enter") == "true") Input.Hotkey("enter");
                    break;
                case "open":
                case "url":
                    {
                        string mode = S(a, "mode");
                        if (type == "open" && mode != "" && mode != "open") { WinOps.App(mode, S(a, "target"), S(a, "args")); break; }
                        string target = Environment.ExpandEnvironmentVariables(S(a, "target").Trim());
                        if (target == "") throw new Exception("Nothing to open");
                        if (type == "url" && target.IndexOf("://") < 0) target = "https://" + target;
                        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
                        string args = S(a, "args");
                        if (args != "") psi.Arguments = args;
                        try { string dir = Path.GetDirectoryName(target); if (Directory.Exists(dir)) psi.WorkingDirectory = dir; } catch { }
                        Process.Start(psi);
                        break;
                    }
                case "media":
                    {
                        string k = S(a, "key");
                        string map = k == "playpause" ? "playpause" : k == "next" ? "nexttrack" : k == "prev" ? "prevtrack" : k == "stop" ? "mediastop" :
                                     k == "volup" ? "volumeup" : k == "voldown" ? "volumedown" : k == "mute" ? "volumemute" : "";
                        if (map == "") throw new Exception("Unknown media key");
                        Input.Hotkey(map);
                        break;
                    }
                case "audio":
                    {
                        string op = S(a, "op");
                        if (op == "mute") Audio.ToggleMute(false);
                        else if (op == "micmute") Audio.ToggleMute(true);
                        else if (op == "set") Audio.SetVolume(Convert.ToDouble(a.ContainsKey("value") ? a["value"] : 50));
                        else if (op == "up") Audio.Step(+1, a.ContainsKey("value") ? Convert.ToDouble(a["value"]) : 5);
                        else if (op == "down") Audio.Step(-1, a.ContainsKey("value") ? Convert.ToDouble(a["value"]) : 5);
                        else throw new Exception("Unknown audio action");
                        break;
                    }
                case "system":
                    {
                        string c = S(a, "cmd");
                        if (c == "lock") Native.LockWorkStation();
                        else if (c == "sleep") Application.SetSuspendState(PowerState.Suspend, false, false);
                        else if (c == "screenshot") Input.Hotkey("win+shift+s");
                        else if (c == "showdesktop") Input.Hotkey("win+d");
                        else if (c == "taskmgr") Input.Hotkey("ctrl+shift+esc");
                        else if (c == "explorer") Process.Start("explorer.exe");
                        else if (c == "settings") Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true });
                        else if (c == "monitoroff") Native.SendMessage((IntPtr)0xFFFF, 0x0112, (IntPtr)0xF170, (IntPtr)2);
                        else if (c == "shutdown") Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                        else if (c == "restart") Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
                        else if (c == "signout") Process.Start(new ProcessStartInfo("shutdown", "/l") { CreateNoWindow = true, UseShellExecute = false });
                        else throw new Exception("Unknown system action");
                        break;
                    }
                case "command":
                    {
                        string cmd = S(a, "cmd");
                        if (cmd.Trim() == "") throw new Exception("No command");
                        bool show = S(a, "show") == "True" || S(a, "show") == "true";
                        var psi = new ProcessStartInfo("cmd.exe", (show ? "/k " : "/c ") + cmd);
                        psi.UseShellExecute = show;
                        psi.CreateNoWindow = !show;
                        psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        Process.Start(psi);
                        break;
                    }
                case "window": WinOps.Window(S(a, "op")); break;
                case "app": WinOps.App(S(a, "op"), S(a, "target"), S(a, "args")); break;
                case "mouse": { int n = 3; int.TryParse(S(a, "value"), out n); WinOps.Mouse(S(a, "op"), n); break; }
                case "appvol":
                    Audio.AppVolume(S(a, "app"), S(a, "op"), a.ContainsKey("value") ? Convert.ToDouble(a["value"]) : 10);
                    break;
                case "output": Audio.Output(S(a, "op"), S(a, "id"), S(a, "flow") == "1" ? 1 : 0); break;
                case "brightness":
                    {
                        string op = S(a, "op"); double v = a.ContainsKey("value") ? Convert.ToDouble(a["value"]) : 10;
                        if (op == "set") Brightness.Set((int)v); else Brightness.Step(op == "down" ? -1 : 1, (int)v);
                        break;
                    }
                case "sound":
                    if (S(a, "op") == "stop") Sounds.StopAll();
                    else { int vol = 100; int.TryParse(S(a, "volume"), out vol); Sounds.Play(S(a, "file"), vol <= 0 ? 100 : vol, S(a, "restart") == "True" || S(a, "restart") == "true"); }
                    break;
                case "delay":
                    {
                        int ms = 0; int.TryParse(S(a, "ms"), out ms);
                        Thread.Sleep(Math.Max(0, Math.Min(ms, 30000)));
                        break;
                    }
                case "multi":
                    {
                        object steps;
                        if (a.TryGetValue("steps", out steps) && steps is IEnumerable)
                            foreach (object s in (IEnumerable)steps)
                            {
                                var d = s as Dictionary<string, object>;
                                if (d == null) continue;
                                if (Program.DryRun && S(d, "type") != "multi") { File.AppendAllText(Path.Combine(Program.DataDir, "dryrun.log"), Json.Write(d) + "\n"); continue; }
                                Run(d, depth + 1);
                                Thread.Sleep(30);
                            }
                        break;
                    }
                default: throw new Exception("Unknown action: " + type);
            }
        }
    }

    // ---------------------------------------------------------------- keyboard input
    static class Input
    {
        static readonly Dictionary<string, ushort> Keys = BuildKeys();
        static readonly HashSet<ushort> Extended = new HashSet<ushort> { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x6F, 0x90, 0xA3, 0xA5, 0x5B, 0x5C, 0x5D, 0x2C };

        static Dictionary<string, ushort> BuildKeys()
        {
            var k = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            for (char c = 'a'; c <= 'z'; c++) k[c.ToString()] = (ushort)char.ToUpperInvariant(c);
            for (char c = '0'; c <= '9'; c++) k[c.ToString()] = (ushort)c;
            for (int i = 1; i <= 24; i++) k["f" + i] = (ushort)(0x6F + i);
            for (int i = 0; i <= 9; i++) k["num" + i] = (ushort)(0x60 + i);
            string[,] named = {
                {"ctrl","11"},{"control","11"},{"shift","10"},{"alt","12"},{"win","5B"},{"meta","5B"},{"cmd","5B"},{"super","5B"},
                {"rctrl","A3"},{"rshift","A1"},{"ralt","A5"},{"altgr","A5"},
                {"enter","0D"},{"return","0D"},{"tab","09"},{"esc","1B"},{"escape","1B"},{"space","20"},{"backspace","08"},
                {"delete","2E"},{"del","2E"},{"insert","2D"},{"ins","2D"},{"home","24"},{"end","23"},{"pageup","21"},{"pgup","21"},
                {"pagedown","22"},{"pgdn","22"},{"up","26"},{"down","28"},{"left","25"},{"right","27"},{"capslock","14"},
                {"printscreen","2C"},{"prtsc","2C"},{"pause","13"},{"numlock","90"},{"scrolllock","91"},{"menu","5D"},{"apps","5D"},
                {"nummult","6A"},{"numplus","6B"},{"numminus","6D"},{"numdot","6E"},{"numdiv","6F"},
                {";","BA"},{"semicolon","BA"},{"=","BB"},{"equals","BB"},{"plus","BB"},{",","BC"},{"comma","BC"},{"-","BD"},{"minus","BD"},
                {".","BE"},{"period","BE"},{"/","BF"},{"slash","BF"},{"`","C0"},{"backtick","C0"},{"[","DB"},{"\\","DC"},{"backslash","DC"},
                {"]","DD"},{"'","DE"},{"quote","DE"},
                {"playpause","B3"},{"nexttrack","B0"},{"prevtrack","B1"},{"mediastop","B2"},{"volumemute","AD"},{"volumedown","AE"},{"volumeup","AF"},
                {"browserback","A6"},{"browserforward","A7"},{"browserrefresh","A8"},{"browserhome","AC"},{"calculator","B7"},{"mail","B4"}
            };
            for (int i = 0; i < named.GetLength(0); i++) k[named[i, 0]] = Convert.ToUInt16(named[i, 1], 16);
            return k;
        }

        public static ushort Vk(string name)
        {
            ushort v;
            if (Keys.TryGetValue(name.Trim(), out v)) return v;
            throw new Exception("Unknown key: " + name);
        }

        // "ctrl+shift+m" or chords "ctrl+k, ctrl+c"
        public static void Hotkey(string combo)
        {
            if (string.IsNullOrWhiteSpace(combo)) throw new Exception("No keys set");
            foreach (string chord in combo.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = new List<string>();
                string c = chord.Trim();
                // Allow "ctrl++" for the plus key.
                if (c.EndsWith("++")) { c = c.Substring(0, c.Length - 2); parts.Add("plus"); }
                var list = new List<string>(c.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries));
                list.AddRange(parts);
                var vks = new List<ushort>();
                foreach (string p in list) vks.Add(Vk(p));
                if (vks.Count == 0) continue;
                var inputs = new List<Native.INPUT>();
                foreach (ushort v in vks) inputs.Add(Key(v, false));
                for (int i = vks.Count - 1; i >= 0; i--) inputs.Add(Key(vks[i], true));
                SendAll(inputs);
                Thread.Sleep(40);
            }
        }

        public static void Type(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var inputs = new List<Native.INPUT>();
            foreach (char ch in text.Replace("\r\n", "\n"))
            {
                if (ch == '\n') { inputs.Add(Key(0x0D, false)); inputs.Add(Key(0x0D, true)); continue; }
                if (ch == '\t') { inputs.Add(Key(0x09, false)); inputs.Add(Key(0x09, true)); continue; }
                inputs.Add(Uni(ch, false)); inputs.Add(Uni(ch, true));
            }
            // Send in small batches so slow apps keep up.
            for (int i = 0; i < inputs.Count; i += 40)
            {
                SendAll(inputs.GetRange(i, Math.Min(40, inputs.Count - i)));
                Thread.Sleep(8);
            }
        }

        static Native.INPUT Key(ushort vk, bool up)
        {
            var i = new Native.INPUT();
            i.type = 1;
            i.U.ki.wVk = vk;
            i.U.ki.wScan = (ushort)Native.MapVirtualKey(vk, 0);
            i.U.ki.dwFlags = (up ? 2u : 0u) | (Extended.Contains(vk) ? 1u : 0u);
            return i;
        }

        static Native.INPUT Uni(char ch, bool up)
        {
            var i = new Native.INPUT();
            i.type = 1;
            i.U.ki.wVk = 0;
            i.U.ki.wScan = ch;
            i.U.ki.dwFlags = 4u | (up ? 2u : 0u);
            return i;
        }

        static void SendAll(List<Native.INPUT> inputs)
        {
            var arr = inputs.ToArray();
            uint sent = Native.SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(Native.INPUT)));
            if (sent != arr.Length) throw new Exception("Windows blocked the key press (an admin window may be focused)");
        }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public InputUnion U; }

        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] public static extern bool LockWorkStation();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    }

    // ---------------------------------------------------------------- audio (Core Audio COM)
    static class Audio
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore props);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            void RegisterControlChangeNotify(IntPtr notify);
            void UnregisterControlChangeNotify(IntPtr notify);
            void GetChannelCount(out uint count);
            void SetMasterVolumeLevel(float level, ref Guid ctx);
            void SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            void GetMasterVolumeLevel(out float level);
            void GetMasterVolumeLevelScalar(out float level);
            void SetChannelVolumeLevel(uint ch, float level, ref Guid ctx);
            void SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
            void GetChannelVolumeLevel(uint ch, out float level);
            void GetChannelVolumeLevelScalar(uint ch, out float level);
            void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        static IMMDevice Device(bool mic)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev;
            int hr = en.GetDefaultAudioEndpoint(mic ? 1 : 0, mic ? 2 : 1, out dev);
            if (hr != 0 || dev == null) hr = en.GetDefaultAudioEndpoint(mic ? 1 : 0, 0, out dev);
            if (hr != 0 || dev == null) throw new Exception(mic ? "No microphone found" : "No speakers found");
            return dev;
        }

        static IAudioEndpointVolume Endpoint(bool mic)
        {
            IMMDevice dev = Device(mic);
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            object o;
            Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out o));
            return (IAudioEndpointVolume)o;
        }

        public static void ToggleMute(bool mic)
        {
            var ep = Endpoint(mic);
            bool m; ep.GetMute(out m);
            Guid g = Guid.Empty;
            ep.SetMute(!m, ref g);
        }

        public static void SetVolume(double pct)
        {
            var ep = Endpoint(false);
            Guid g = Guid.Empty;
            ep.SetMasterVolumeLevelScalar((float)Math.Max(0, Math.Min(1, pct / 100.0)), ref g);
            ep.SetMute(false, ref g);
        }

        public static void Step(int dir, double pct)
        {
            var ep = Endpoint(false);
            float cur; ep.GetMasterVolumeLevelScalar(out cur);
            SetVolume(Math.Round(cur * 100) + dir * pct);
        }

        public static Dictionary<string, object> State()
        {
            var d = new Dictionary<string, object>();
            if (Program.DryRun) { d["volume"] = 42; d["muted"] = false; d["micMuted"] = false; return d; }
            try
            {
                var ep = Endpoint(false);
                float v; bool m;
                ep.GetMasterVolumeLevelScalar(out v); ep.GetMute(out m);
                d["volume"] = (int)Math.Round(v * 100); d["muted"] = m;
            }
            catch { }
            try { var mic = Endpoint(true); bool mm; mic.GetMute(out mm); d["micMuted"] = mm; } catch { }
            return d;
        }

        // ---- devices (switch speakers / headphones)
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint n);
            [PreserveSig] int Item(uint i, out IMMDevice dev);
        }

        [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint n);
            [PreserveSig] int GetAt(uint i, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT v);
        }
        [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
        [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p, p2; }
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT v);

        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigCom { }
        [Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
            [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
            [PreserveSig] int ResetDeviceFormat(IntPtr a);
            [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
            [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
            [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
            [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
            [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
        }

        static string DeviceName(IMMDevice d)
        {
            IPropertyStore ps;
            if (d.OpenPropertyStore(0, out ps) != 0 || ps == null) return "Audio device";
            var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
            PROPVARIANT v;
            if (ps.GetValue(ref key, out v) != 0) return "Audio device";
            string s = v.vt == 31 ? Marshal.PtrToStringUni(v.p) : "Audio device";
            PropVariantClear(ref v);
            return s;
        }

        // flow 0 = speakers/headphones, 1 = microphones
        public static ArrayList Devices(int flow)
        {
            var l = new ArrayList();
            if (Program.DryRun)
            {
                if (flow == 0) { l.Add(Sys.Obj("id", "spk", "name", "Speakers (Realtek Audio)", "default", true)); l.Add(Sys.Obj("id", "hp", "name", "Headphones (USB)", "default", false)); }
                else l.Add(Sys.Obj("id", "mic", "name", "Microphone Array", "default", true));
                return l;
            }
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            string def = "";
            IMMDevice dd;
            if (en.GetDefaultAudioEndpoint(flow, 1, out dd) == 0 && dd != null) dd.GetId(out def);
            IMMDeviceCollection col;
            Marshal.ThrowExceptionForHR(en.EnumAudioEndpoints(flow, 1, out col));
            uint n; col.GetCount(out n);
            for (uint i = 0; i < n; i++)
            {
                IMMDevice d; if (col.Item(i, out d) != 0 || d == null) continue;
                string id; d.GetId(out id);
                l.Add(Sys.Obj("id", id, "name", DeviceName(d), "default", id == def));
            }
            return l;
        }

        public static void SetDefault(string id)
        {
            var pc = (IPolicyConfig)new PolicyConfigCom();
            for (int role = 0; role < 3; role++) Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(id, role));
        }

        public static void Output(string op, string id, int flow)
        {
            var list = Devices(flow);
            if (list.Count == 0) throw new Exception("No audio devices found");
            if (op == "next")
            {
                int cur = 0;
                for (int i = 0; i < list.Count; i++) if ((bool)((Dictionary<string, object>)list[i])["default"]) cur = i;
                id = (string)((Dictionary<string, object>)list[(cur + 1) % list.Count])["id"];
            }
            if (string.IsNullOrEmpty(id)) throw new Exception("No device picked");
            SetDefault(id);
        }

        // ---- per-app volume (the Windows volume mixer)
        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr ctl);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr vol);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
        }
        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int n);
            [PreserveSig] int GetSession(int i, out IAudioSessionControl2 s);
        }
        [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int st);
            [PreserveSig] int GetDisplayName(out IntPtr s);
            [PreserveSig] int SetDisplayName(IntPtr s, IntPtr ctx);
            [PreserveSig] int GetIconPath(out IntPtr s);
            [PreserveSig] int SetIconPath(IntPtr s, IntPtr ctx);
            [PreserveSig] int GetGroupingParam(out Guid g);
            [PreserveSig] int SetGroupingParam(IntPtr g, IntPtr ctx);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
            [PreserveSig] int GetSessionIdentifier(out IntPtr s);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr s);
            [PreserveSig] int GetProcessId(out uint pid);
        }
        [Guid("87CE5498-68D6-44E5-9215-6F0D9C3F00C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float v, ref Guid ctx);
            [PreserveSig] int GetMasterVolume(out float v);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool m, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool m);
        }

        // app name (lowercase exe, no .exe) -> its volume controls
        static List<KeyValuePair<string, ISimpleAudioVolume>> SessionList()
        {
            var res = new List<KeyValuePair<string, ISimpleAudioVolume>>();
            IMMDevice dev = Device(false);
            Guid iid = typeof(IAudioSessionManager2).GUID;
            object o;
            Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out o));
            IAudioSessionEnumerator e;
            Marshal.ThrowExceptionForHR(((IAudioSessionManager2)o).GetSessionEnumerator(out e));
            int n; e.GetCount(out n);
            for (int i = 0; i < n; i++)
            {
                IAudioSessionControl2 s;
                if (e.GetSession(i, out s) != 0 || s == null) continue;
                uint pid; s.GetProcessId(out pid);
                if (pid == 0) continue; // system sounds
                string name;
                try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName.ToLowerInvariant(); } catch { continue; }
                res.Add(new KeyValuePair<string, ISimpleAudioVolume>(name, (ISimpleAudioVolume)s));
            }
            return res;
        }

        public static Dictionary<string, object> Sessions()
        {
            var d = new Dictionary<string, object>();
            foreach (var kv in SessionList())
            {
                if (d.ContainsKey(kv.Key)) continue;
                float v; bool m; kv.Value.GetMasterVolume(out v); kv.Value.GetMute(out m);
                d[kv.Key] = Sys.Obj("volume", (int)Math.Round(v * 100), "muted", m);
            }
            return d;
        }

        public static void AppVolume(string app, string op, double value)
        {
            string name = WinOps.ExeName(app);
            if (name == "") throw new Exception("No app picked");
            bool found = false; Guid g = Guid.Empty;
            bool? newMute = null; float? newVol = null;
            foreach (var kv in SessionList())
            {
                if (kv.Key != name) continue;
                found = true;
                var sv = kv.Value;
                if (op == "mute")
                {
                    if (newMute == null) { bool m; sv.GetMute(out m); newMute = !m; }
                    sv.SetMute(newMute.Value, ref g);
                }
                else
                {
                    if (newVol == null)
                    {
                        float cur; sv.GetMasterVolume(out cur);
                        double t = op == "set" ? value : Math.Round(cur * 100) + (op == "up" ? value : -value);
                        newVol = (float)Math.Max(0, Math.Min(1, t / 100.0));
                    }
                    sv.SetMasterVolume(newVol.Value, ref g);
                    if (newVol.Value > 0) sv.SetMute(false, ref g);
                }
            }
            if (!found) throw new Exception("That app isn't playing sound right now");
            Sys.Invalidate();
        }
    }

    // ---------------------------------------------------------------- live PC state (stats, focused app, running apps)
    static class Sys
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MEMSTATUS { public uint len, load; public ulong total, avail, totalPage, availPage, totalVirt, availVirt, availExt; }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMSTATUS m);
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        static readonly object gate = new object();
        static Dictionary<string, object> cached; static DateTime cachedAt = DateTime.MinValue;
        static long lastIdle, lastTotal; static int cpu;
        static int bright = -1; static DateTime brightAt = DateTime.MinValue;

        public static void Invalidate() { lock (gate) cachedAt = DateTime.MinValue; }
        public static void BrightnessChanged(int v) { bright = v; brightAt = DateTime.UtcNow; Invalidate(); }

        // Everything the phone shows live. Cached so several phones polling don't add load.
        public static Dictionary<string, object> State()
        {
            lock (gate)
            {
                if (cached != null && (DateTime.UtcNow - cachedAt).TotalMilliseconds < 700) return cached;
                var d = Audio.State();
                if (Program.DryRun)
                {
                    d["cpu"] = 23; d["ram"] = 61; d["battery"] = 80; d["charging"] = true; d["brightness"] = 60;
                    d["activeApp"] = "chrome"; d["running"] = new ArrayList { "chrome", "spotify", "explorer", "discord" };
                    d["apps"] = new Dictionary<string, object> { { "spotify", Obj("volume", 70, "muted", false) }, { "discord", Obj("volume", 100, "muted", true) } };
                }
                else
                {
                    try { d["cpu"] = Cpu(); } catch { }
                    try { var m = new MEMSTATUS(); m.len = (uint)Marshal.SizeOf(typeof(MEMSTATUS)); if (GlobalMemoryStatusEx(ref m)) d["ram"] = (int)m.load; } catch { }
                    try
                    {
                        var ps = SystemInformation.PowerStatus;
                        if ((ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryLifePercent <= 1f)
                        { d["battery"] = (int)Math.Round(ps.BatteryLifePercent * 100); d["charging"] = ps.PowerLineStatus == PowerLineStatus.Online; }
                    }
                    catch { }
                    try { d["activeApp"] = ActiveApp(); } catch { }
                    try
                    {
                        var names = new SortedSet<string>();
                        foreach (var p in Process.GetProcesses()) { try { names.Add(p.ProcessName.ToLowerInvariant()); } catch { } p.Dispose(); }
                        d["running"] = new ArrayList(new List<string>(names));
                    }
                    catch { }
                    try { d["apps"] = Audio.Sessions(); } catch { }
                    if ((DateTime.UtcNow - brightAt).TotalSeconds > 15) { brightAt = DateTime.UtcNow; try { bright = Brightness.Get(); } catch { bright = -1; } }
                    if (bright >= 0) d["brightness"] = bright;
                }
                cached = d; cachedAt = DateTime.UtcNow;
                return d;
            }
        }

        static int Cpu()
        {
            long idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) return cpu;
            long total = kernel + user; // kernel time includes idle
            long dt = total - lastTotal, di = idle - lastIdle;
            if (lastTotal != 0 && dt > 0) cpu = (int)Math.Round(100.0 * (dt - di) / dt);
            lastTotal = total; lastIdle = idle;
            return Math.Max(0, Math.Min(100, cpu));
        }

        public static string ActiveApp()
        {
            uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            if (pid == 0) return "";
            using (var p = Process.GetProcessById((int)pid)) return p.ProcessName.ToLowerInvariant();
        }

        public static Dictionary<string, object> Obj(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
    }

    // ---------------------------------------------------------------- windows and apps
    static class WinOps
    {
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

        public static void Window(string op)
        {
            IntPtr fg = Sys.GetForegroundWindow();
            switch (op)
            {
                case "left": Input.Hotkey("win+left"); break;
                case "right": Input.Hotkey("win+right"); break;
                case "max": ShowWindow(fg, IsZoomed(fg) ? 9 : 3); break;
                case "min": ShowWindow(fg, 6); break;
                case "nextmon": Input.Hotkey("win+shift+right"); break;
                case "close": PostMessage(fg, 0x0010, IntPtr.Zero, IntPtr.Zero); break;
                case "ontop":
                    {
                        bool top = (GetWindowLong(fg, -20) & 0x8) != 0;
                        SetWindowPos(fg, top ? new IntPtr(-2) : new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
                        break;
                    }
                case "switch": Input.Hotkey("win+tab"); break;
                case "deskleft": Input.Hotkey("ctrl+win+left"); break;
                case "deskright": Input.Hotkey("ctrl+win+right"); break;
                case "desknew": Input.Hotkey("ctrl+win+d"); break;
                default: throw new Exception("Unknown window action");
            }
        }

        // "C:\...\Spotify.exe", "spotify", or a Start Menu .lnk -> "spotify"
        public static string ExeName(string target)
        {
            string t = Environment.ExpandEnvironmentVariables((target ?? "").Trim().Trim('"'));
            if (t.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(t))
            {
                try
                {
                    Type st = Type.GetTypeFromProgID("WScript.Shell");
                    object sh = Activator.CreateInstance(st);
                    object lnk = st.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { t });
                    string tp = Convert.ToString(lnk.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, lnk, null));
                    if (tp != "") t = tp;
                }
                catch { }
            }
            string n = Path.GetFileName(t);
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            return n.TrimEnd(':').ToLowerInvariant();
        }

        static Process[] Find(string target)
        {
            string n = ExeName(target);
            if (n == "") throw new Exception("No app set");
            return Process.GetProcessesByName(n);
        }

        public static void App(string op, string target, string args)
        {
            if (op == "close" || op == "kill")
            {
                var ps = Find(target);
                if (ps.Length == 0) throw new Exception("That app is not running");
                foreach (var p in ps)
                {
                    try { if (op == "kill") p.Kill(); else if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
                }
                Sys.Invalidate();
                return;
            }
            // "focus": bring it to the front if running, otherwise start it.
            foreach (var p in Find(target))
            {
                IntPtr hw = p.MainWindowHandle;
                if (hw == IntPtr.Zero) continue;
                if (IsIconic(hw)) ShowWindow(hw, 9);
                keybd_event(0x12, 0, 0, IntPtr.Zero); keybd_event(0x12, 0, 2, IntPtr.Zero); // Alt tap lets us take focus
                SetForegroundWindow(hw);
                return;
            }
            var psi = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(target.Trim())) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
            Process.Start(psi);
        }

        public static void Mouse(string op, int amount)
        {
            var list = new List<Native.INPUT>();
            Func<uint, int, Native.INPUT> M = (flags, data) => { var i = new Native.INPUT(); i.type = 0; i.U.mi.dwFlags = flags; i.U.mi.mouseData = unchecked((uint)data); return i; };
            if (amount <= 0) amount = 3;
            switch (op)
            {
                case "left": list.Add(M(0x2, 0)); list.Add(M(0x4, 0)); break;
                case "double": list.Add(M(0x2, 0)); list.Add(M(0x4, 0)); list.Add(M(0x2, 0)); list.Add(M(0x4, 0)); break;
                case "right": list.Add(M(0x8, 0)); list.Add(M(0x10, 0)); break;
                case "middle": list.Add(M(0x20, 0)); list.Add(M(0x40, 0)); break;
                case "up": list.Add(M(0x800, 120 * amount)); break;
                case "down": list.Add(M(0x800, -120 * amount)); break;
                case "sleft": list.Add(M(0x1000, -120 * amount)); break;
                case "sright": list.Add(M(0x1000, 120 * amount)); break;
                default: throw new Exception("Unknown mouse action");
            }
            var arr = list.ToArray();
            Native.SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(Native.INPUT)));
        }
    }

    // ---------------------------------------------------------------- laptop screen brightness (WMI)
    static class Brightness
    {
        public static int Get()
        {
            using (var s = new System.Management.ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                foreach (System.Management.ManagementObject mo in s.Get()) return Convert.ToInt32(mo["CurrentBrightness"]);
            return -1;
        }

        public static void Set(int v)
        {
            v = Math.Max(0, Math.Min(100, v));
            bool any = false;
            using (var s = new System.Management.ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
                foreach (System.Management.ManagementObject mo in s.Get()) { mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)v }); any = true; }
            if (!any) throw new Exception("This screen's brightness can't be set from Windows (works on laptop screens)");
            Sys.BrightnessChanged(v);
        }

        public static void Step(int dir, int pct)
        {
            int cur = Get();
            if (cur < 0) throw new Exception("This screen's brightness can't be set from Windows (works on laptop screens)");
            Set(cur + dir * pct);
        }
    }

    // ---------------------------------------------------------------- soundboard (MCI on its own thread)
    static class Sounds
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int mciSendString(string cmd, StringBuilder ret, int len, IntPtr cb);
        static Control host; static readonly ManualResetEvent ready = new ManualResetEvent(false);
        static readonly HashSet<string> open = new HashSet<string>();
        public static readonly string[] Exts = { ".mp3", ".wav", ".wma", ".m4a", ".aac" };

        public static string Dir() { string d = Path.Combine(Program.DataDir, "sounds"); Directory.CreateDirectory(d); return d; }

        static void Start()
        {
            if (host != null) return;
            var t = new Thread(() => { host = new Control(); host.CreateControl(); var unused = host.Handle; ready.Set(); Application.Run(); });
            t.IsBackground = true; t.SetApartmentState(ApartmentState.STA); t.Start();
            ready.WaitOne(5000);
        }

        static string Mci(string cmd)
        {
            var sb = new StringBuilder(256);
            int err = mciSendString(cmd, sb, sb.Capacity, IntPtr.Zero);
            if (err != 0) throw new Exception("Could not play that sound (error " + err + ")");
            return sb.ToString();
        }

        static void OnHost(Action a)
        {
            Start();
            Exception ex = null;
            host.Invoke((MethodInvoker)delegate { try { a(); } catch (Exception e) { ex = e; } });
            if (ex != null) throw ex;
        }

        public static string Resolve(string file)
        {
            string f = Environment.ExpandEnvironmentVariables((file ?? "").Trim().Trim('"'));
            if (f == "") throw new Exception("No sound picked");
            if (!Path.IsPathRooted(f)) f = Path.Combine(Dir(), Path.GetFileName(f));
            if (!File.Exists(f)) throw new Exception("Sound file not found on the PC");
            return f;
        }

        // Press once to play, press again while it plays to stop (like a soundboard pad).
        public static void Play(string file, int volume, bool restart)
        {
            string f = Resolve(file);
            string alias = "pd" + ((uint)f.ToLowerInvariant().GetHashCode()).ToString("x");
            OnHost(() =>
            {
                if (open.Contains(alias))
                {
                    string mode = "";
                    try { mode = Mci("status " + alias + " mode"); } catch { }
                    try { Mci("close " + alias); } catch { }
                    open.Remove(alias);
                    if (mode == "playing" && !restart) return;
                }
                Mci("open \"" + f + "\" type mpegvideo alias " + alias);
                open.Add(alias);
                try { Mci("setaudio " + alias + " volume to " + Math.Max(0, Math.Min(1000, volume * 10))); } catch { }
                Mci("play " + alias);
            });
        }

        public static void StopAll()
        {
            OnHost(() => { foreach (string a in open) { try { Mci("close " + a); } catch { } } open.Clear(); });
        }

        public static ArrayList List()
        {
            var l = new ArrayList();
            foreach (string f in Directory.GetFiles(Dir()))
                if (Array.IndexOf(Exts, Path.GetExtension(f).ToLowerInvariant()) >= 0) l.Add(Path.GetFileName(f));
            return l;
        }

        public static string Save(string name, string base64)
        {
            string n = Path.GetFileName(name ?? "");
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            if (Array.IndexOf(Exts, Path.GetExtension(n).ToLowerInvariant()) < 0) throw new Exception("Use an mp3, wav, m4a, aac or wma file");
            byte[] data = Convert.FromBase64String(base64);
            if (data.Length > 15 * 1024 * 1024) throw new Exception("Sound is too big (15 MB max)");
            File.WriteAllBytes(Path.Combine(Dir(), n), data);
            return n;
        }
    }

    // ---------------------------------------------------------------- start menu apps
    static class AppList
    {
        static ArrayList cache; static DateTime cachedAt;
        public static ArrayList Get()
        {
            if (cache != null && (DateTime.UtcNow - cachedAt).TotalMinutes < 5) return cache;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<KeyValuePair<string, string>>();
            string[] roots = {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try
                {
                    foreach (string f in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories))
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext != ".lnk" && ext != ".url" && ext != ".appref-ms") continue;
                        string name = Path.GetFileNameWithoutExtension(f);
                        string ln = name.ToLowerInvariant();
                        if (ln.Contains("uninstall") || ln.Contains("readme") || ln.Contains("help") && ln.Length < 10) continue;
                        if (seen.Add(name)) items.Add(new KeyValuePair<string, string>(name, f));
                    }
                }
                catch { }
            }
            items.Sort((x, y) => string.Compare(x.Key, y.Key, StringComparison.OrdinalIgnoreCase));
            var list = new ArrayList();
            foreach (var kv in items)
            {
                var d = new Dictionary<string, object>(); d["name"] = kv.Key; d["path"] = kv.Value; list.Add(d);
            }
            cache = list; cachedAt = DateTime.UtcNow;
            return list;
        }
    }

    // ---------------------------------------------------------------- real app / website icons
    static class AppIcon
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        public static string FromFile(string path)
        {
            path = Environment.ExpandEnvironmentVariables((path ?? "").Trim().Trim('"'));
            if (path == "") throw new Exception("Pick an app or file first");
            if (Program.DryRun) return Png(new Bitmap(8, 8));
            Bitmap bmp = null;
            try { bmp = Shell(path, 128); } catch { }
            if (bmp == null)
            {
                string full = path;
                if (!File.Exists(full)) full = FindOnPath(path);
                if (full == null) throw new Exception("No icon found for that path");
                using (Icon ic = Icon.ExtractAssociatedIcon(full)) bmp = ic.ToBitmap();
            }
            using (bmp) return Png(bmp);
        }

        static string FindOnPath(string name)
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { string f = Path.Combine(dir.Trim(), name); if (File.Exists(f)) return f; } catch { }
            }
            return null;
        }

        static Bitmap Shell(string path, int size)
        {
            IShellItemImageFactory f;
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f);
            IntPtr hbm;
            SIZE sz; sz.cx = size; sz.cy = size;
            int hr = f.GetImage(sz, 0x1 | 0x4, out hbm); // BIGGERSIZEOK | ICONONLY
            if (hr != 0 || hbm == IntPtr.Zero) return null;
            try
            {
                using (Bitmap raw = Image.FromHbitmap(hbm))
                {
                    // FromHbitmap drops alpha: re-read the same pixels as 32-bit ARGB.
                    var rect = new Rectangle(0, 0, raw.Width, raw.Height);
                    var data = raw.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, raw.PixelFormat);
                    try
                    {
                        using (var argb = new Bitmap(data.Width, data.Height, data.Stride, System.Drawing.Imaging.PixelFormat.Format32bppArgb, data.Scan0))
                            return new Bitmap(argb);
                    }
                    finally { raw.UnlockBits(data); }
                }
            }
            finally { DeleteObject(hbm); }
        }

        public static string FromSite(string url)
        {
            if (Program.DryRun) return Png(new Bitmap(8, 8));
            if (url.IndexOf("://") < 0) url = "https://" + url;
            string host = new Uri(url).Host;
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            using (var wc = new WebClient())
            {
                byte[] bytes = wc.DownloadData("https://www.google.com/s2/favicons?sz=128&domain=" + Uri.EscapeDataString(host));
                using (var ms = new MemoryStream(bytes))
                using (var img = Image.FromStream(ms))
                using (var bmp = new Bitmap(img)) return Png(bmp);
            }
        }

        static string Png(Bitmap bmp)
        {
            int max = 128;
            Bitmap outBmp = bmp;
            if (bmp.Width > max || bmp.Height > max)
            {
                double k = Math.Min((double)max / bmp.Width, (double)max / bmp.Height);
                outBmp = new Bitmap(bmp, new System.Drawing.Size((int)(bmp.Width * k), (int)(bmp.Height * k)));
            }
            using (var ms = new MemoryStream())
            {
                outBmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                if (outBmp != bmp) outBmp.Dispose();
                return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
        }
    }

    // ---------------------------------------------------------------- helpers
    static class Json
    {
        static JavaScriptSerializer Ser()
        {
            var s = new JavaScriptSerializer(); s.MaxJsonLength = int.MaxValue; s.RecursionLimit = 200; return s;
        }
        public static object Parse(string s) { return Ser().DeserializeObject(s); }
        public static string Write(object o) { return Ser().Serialize(o); }
    }

    static class Util
    {
        static readonly RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();

        public static int RandomInt(int max)
        {
            byte[] b = new byte[4];
            lock (rng) rng.GetBytes(b);
            return (int)(BitConverter.ToUInt32(b, 0) % (uint)max);
        }

        public static string RandomHex(int bytes)
        {
            byte[] b = new byte[bytes];
            lock (rng) rng.GetBytes(b);
            var sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public static bool SafeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Copy(path, path + ".bak", true); } catch { }
                File.Delete(path);
            }
            File.Move(tmp, path);
        }

        public static ArrayList LanUrls(int port)
        {
            var wifi = new List<string>(); var other = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    string desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
                    if (desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper-v") || desc.Contains("vethernet") || desc.Contains("bluetooth") || desc.Contains("loopback")) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = ua.Address.ToString();
                        if (ip.StartsWith("169.254.") || ip.StartsWith("127.")) continue;
                        string url = "http://" + ip + ":" + port + "/";
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) wifi.Add(url); else other.Add(url);
                    }
                }
            }
            catch { }
            var all = new ArrayList();
            foreach (string u in wifi) all.Add(u);
            foreach (string u in other) all.Add(u);
            return all;
        }
    }
}
