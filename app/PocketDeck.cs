// PocketDeck - turns a phone into a Stream Deck style controller for this PC.
// Written in C# 5 so the compiler built into Windows (.NET Framework 4.x csc.exe) can build it.
// Build: csc /target:winexe /out:PocketDeck.exe /win32icon:icon.ico
//        /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll PocketDeck.cs
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
        public const string Version = "1.1.0";
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
                        SendJson(ns, 200, err == null ? Obj("ok", true, "state", Audio.State()) : Obj("ok", false, "error", err));
                        return;
                    }
                case "/api/state":
                    SendJson(ns, 200, Obj("ok", true, "state", Audio.State(), "paused", Paused, "rev", ProfileRev()));
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
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
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

        static IAudioEndpointVolume Endpoint(bool mic)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev;
            int hr = en.GetDefaultAudioEndpoint(mic ? 1 : 0, mic ? 2 : 1, out dev);
            if (hr != 0 || dev == null) hr = en.GetDefaultAudioEndpoint(mic ? 1 : 0, 0, out dev);
            if (hr != 0 || dev == null) throw new Exception(mic ? "No microphone found" : "No speakers found");
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
