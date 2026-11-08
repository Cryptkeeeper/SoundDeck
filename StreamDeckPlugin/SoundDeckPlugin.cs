// SoundDeck plugin for Stream Deck (+).
// Talks to the Stream Deck app over its WebSocket API, and to SoundDeck over 127.0.0.1:47123.
// Dials: turn = channel volume, press/tap = mute. Key: music in mic on/off.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace SoundDeckPlugin
{
    static class Plugin
    {
        const int SoundDeckPort = 47123;
        const int Step = 2; // percent per dial tick
        static readonly string[] Channels = { "Game", "Chat", "Media", "Aux" };
        static readonly Dictionary<string, string> Colors = new Dictionary<string, string> {
            { "Game", "#FF6A3D" }, { "Chat", "#3DDC97" }, { "Media", "#4DA3FF" }, { "Aux", "#B57BFF" } };

        static ClientWebSocket ws;
        static readonly object sendLock = new object();
        static readonly JavaScriptSerializer json = new JavaScriptSerializer();
        static readonly Dictionary<string, string> contexts = new Dictionary<string, string>(); // context -> channel or "mix"
        static readonly Dictionary<string, int[]> state = new Dictionary<string, int[]>();       // channel -> {volume, muted}
        static bool mixOn;
        static volatile StreamWriter soundDeck;
        static DateTime lastLaunch = DateTime.MinValue;
        static string dir = AppDomain.CurrentDomain.BaseDirectory;

        static void Main(string[] args)
        {
            int port = 0;
            string uuid = null, reg = null;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-port") int.TryParse(args[i + 1], out port);
                else if (args[i] == "-pluginUUID") uuid = args[i + 1];
                else if (args[i] == "-registerEvent") reg = args[i + 1];
            }
            if (port == 0 || uuid == null || reg == null) { Log("Started without Stream Deck arguments"); return; }
            try { Run(port, uuid, reg); }
            catch (Exception ex) { Log(ex.ToString()); }
        }

        static void Run(int port, string uuid, string reg)
        {
            ws = new ClientWebSocket();
            ws.ConnectAsync(new Uri("ws://127.0.0.1:" + port), CancellationToken.None).Wait();
            Send(new Dictionary<string, object> { { "event", reg }, { "uuid", uuid } });
            Log("Registered with Stream Deck");
            new Thread(SoundDeckLoop) { IsBackground = true }.Start();

            var buf = new byte[65536];
            var msg = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                var r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
                if (r.MessageType == WebSocketMessageType.Close) break;
                msg.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;
                string text = Encoding.UTF8.GetString(msg.ToArray());
                msg.SetLength(0);
                try { Handle(text); } catch (Exception ex) { Log(ex.ToString()); }
            }
        }

        static void Handle(string text)
        {
            var m = json.Deserialize<Dictionary<string, object>>(text);
            string ev = Str(m, "event"), ctx = Str(m, "context"), action = Str(m, "action");
            var payload = m.ContainsKey("payload") ? m["payload"] as Dictionary<string, object> : null;
            string target = TargetOf(action);

            switch (ev)
            {
                case "willAppear":
                case "didReceiveSettings":
                    if (target == null) return;
                    lock (contexts) contexts[ctx] = target;
                    SaveSettings(ctx, action, payload);
                    Refresh(ctx, target);
                    break;
                case "willDisappear":
                    lock (contexts) contexts.Remove(ctx);
                    break;
                case "propertyInspectorDidDisappear":
                    lock (panels) panels.Remove(ctx);
                    break;
                case "sendToPlugin":
                    // A settings panel asks for its list (devices, sounds or profiles) when it opens.
                    SendPanel(ctx, action);
                    break;
                case "dialRotate":
                    if (target == null || target == "mix" || payload == null) return;
                    int ticks = Convert.ToInt32(payload["ticks"]);
                    if (!Command("vol " + target + " " + (ticks > 0 ? "+" : "") + (ticks * Step))) { ShowAlert(ctx); return; }
                    lock (state)
                    {
                        int[] s;
                        if (state.TryGetValue(target, out s)) s[0] = Math.Max(0, Math.Min(100, s[0] + ticks * Step));
                    }
                    RefreshTarget(target);
                    break;
                case "dialDown":
                case "touchTap":
                    if (target == null || target == "mix") return;
                    if (!Command("mute " + target)) ShowAlert(ctx);
                    break;
                case "keyDown":
                    if (target == "mix") { if (!Command("mixtoggle")) ShowAlert(ctx); }
                    else if (target != null && target.StartsWith("out:")) { if (!Command(OutputCommand(ctx, target.Substring(4)))) ShowAlert(ctx); }
                    else if (target == "side") { if (!Command("sidetoggle")) ShowAlert(ctx); }
                    else if (target == "chatmute") { if (!Command("chatmute")) ShowAlert(ctx); }
                    else if (target == "stopsounds") { if (!Command("stopsounds")) ShowAlert(ctx); }
                    else if (target == "sound" || target == "profile")
                    {
                        string item = ItemOf(ctx);
                        if (string.IsNullOrEmpty(item)) { ShowAlert(ctx); return; }
                        if (!Command((target == "sound" ? "play " : "profile ") + item)) ShowAlert(ctx);
                    }
                    break;
            }
        }

        // ----- Output buttons -----

        class ButtonSettings { public string Action, Mode, A, B, Item; }
        static string[] sounds = new string[0];             // slot names, index 0 = slot 1
        static List<string> profiles = new List<string>();
        static string activeProfile = "";
        static bool sideOn, chatMuted;

        static string ItemOf(string ctx)
        {
            ButtonSettings b;
            lock (buttons) { if (!buttons.TryGetValue(ctx, out b)) return null; }
            return b.Item;
        }

        // Sound and profile keys: a picked item from a list. Output keys: the device list.
        static void SendPanel(string ctx, string action)
        {
            string target = TargetOf(action);
            if (target != null && target.StartsWith("out:")) { SendDevices(ctx, action); return; }
            lock (panels) panels.Add(ctx);
            var items = new List<Dictionary<string, object>>();
            string label = "Sound";
            if (target == "sound")
            {
                string[] s; lock (outputs) s = sounds;
                for (int i = 0; i < s.Length; i++)
                    items.Add(new Dictionary<string, object> { { "id", (i + 1).ToString() }, { "name", (i + 1) + ". " + (s[i] == "" ? "(empty)" : s[i]) } });
            }
            else if (target == "profile")
            {
                label = "Profile";
                List<string> p; lock (outputs) p = profiles.ToList();
                foreach (var n in p) items.Add(new Dictionary<string, object> { { "id", n }, { "name", n } });
            }
            else return;
            Send(new Dictionary<string, object> { { "action", action }, { "event", "sendToPropertyInspector" }, { "context", ctx },
                { "payload", new Dictionary<string, object> { { "label", label }, { "items", items }, { "online", soundDeck != null } } } });
        }

        static void RefreshPick(string ctx, string target)
        {
            string item = ItemOf(ctx);
            bool online = soundDeck != null;
            string text; bool lit = false;
            if (target == "sound")
            {
                int n; string[] s; lock (outputs) s = sounds;
                if (!online) text = "SoundDeck off";
                else if (!int.TryParse(item, out n) || n < 1) text = "Pick a sound";
                else text = n <= s.Length && s[n - 1] != "" ? s[n - 1] : "Empty slot " + n;
            }
            else
            {
                lit = online && !string.IsNullOrEmpty(item) && item == activeProfile;
                text = !online ? "SoundDeck off" : string.IsNullOrEmpty(item) ? "Pick a profile" : item;
            }
            Send(new Dictionary<string, object> { { "event", "setTitle" }, { "context", ctx },
                { "payload", new Dictionary<string, object> { { "title", "" }, { "target", 0 } } } });
            Send(new Dictionary<string, object> { { "event", "setImage" }, { "context", ctx },
                { "payload", new Dictionary<string, object> { { "image", target == "sound" ? KeyImage.Label("SOUND", "#E8A33D", text, false) : KeyImage.Label("PROFILE", "#3DDC97", text, lit) }, { "target", 0 } } } });
        }
        static readonly Dictionary<string, ButtonSettings> buttons = new Dictionary<string, ButtonSettings>(); // context -> settings
        static readonly Dictionary<string, string[]> outputs = new Dictionary<string, string[]>();          // channel -> {id, name}
        static List<string[]> devices = new List<string[]>();                                                // {id, name}, "-" = System default
        static readonly HashSet<string> panels = new HashSet<string>();                                      // contexts that asked for devices

        static void SaveSettings(string ctx, string action, Dictionary<string, object> payload)
        {
            var s = payload != null && payload.ContainsKey("settings") ? payload["settings"] as Dictionary<string, object> : null;
            var b = new ButtonSettings { Action = action };
            if (s != null) { b.Mode = Str(s, "mode"); b.A = Str(s, "a"); b.B = Str(s, "b"); b.Item = Str(s, "item"); }
            lock (buttons) buttons[ctx] = b;
        }

        // Cycle mode: next shown output. Toggle mode: flip between the button's two picks.
        static string OutputCommand(string ctx, string ch)
        {
            ButtonSettings b;
            lock (buttons) { if (!buttons.TryGetValue(ctx, out b)) b = null; }
            if (b == null || b.Mode != "toggle" || string.IsNullOrEmpty(b.A) || string.IsNullOrEmpty(b.B))
                return "out " + ch + " next";
            string[] cur;
            lock (outputs) { if (!outputs.TryGetValue(ch, out cur)) cur = null; }
            string to = cur != null && cur[0] == b.A ? b.B : b.A;
            return "setout " + ch + " " + to;
        }

        static void SendDevices(string ctx, string action)
        {
            lock (panels) panels.Add(ctx);
            List<string[]> list;
            lock (outputs) list = devices.ToList();
            var arr = list.Select(d => new Dictionary<string, object> { { "id", d[0] }, { "name", d[1] } }).ToList();
            Send(new Dictionary<string, object> { { "action", action }, { "event", "sendToPropertyInspector" }, { "context", ctx },
                { "payload", new Dictionary<string, object> { { "devices", arr }, { "online", soundDeck != null } } } });
        }

        static void RefreshOutput(string ctx, string ch)
        {
            string[] cur;
            lock (outputs) { if (!outputs.TryGetValue(ch, out cur)) cur = null; }
            string dev = soundDeck == null || cur == null ? "SoundDeck off" : cur[1];
            Send(new Dictionary<string, object> { { "event", "setTitle" }, { "context", ctx },
                { "payload", new Dictionary<string, object> { { "title", "" }, { "target", 0 } } } });
            Send(new Dictionary<string, object> { { "event", "setImage" }, { "context", ctx },
                { "payload", new Dictionary<string, object> { { "image", KeyImage.Output(ch, Colors[ch], dev) }, { "target", 0 } } } });
        }

        // "com.sounddeck.controls.game" -> "Game", "...gameout" -> "out:Game", "...mix" -> "mix"
        static string TargetOf(string action)
        {
            if (action == null) return null;
            string last = action.Substring(action.LastIndexOf('.') + 1);
            if (last == "mix" || last == "side" || last == "sound" || last == "stopsounds" || last == "profile" || last == "chatmute") return last;
            if (last.EndsWith("out"))
            {
                string ch = Channels.FirstOrDefault(c => c.Equals(last.Substring(0, last.Length - 3), StringComparison.OrdinalIgnoreCase));
                return ch == null ? null : "out:" + ch;
            }
            return Channels.FirstOrDefault(c => c.Equals(last, StringComparison.OrdinalIgnoreCase));
        }

        static void Refresh(string ctx, string target)
        {
            bool online = soundDeck != null;
            if (target.StartsWith("out:")) { RefreshOutput(ctx, target.Substring(4)); return; }
            if (target == "sound" || target == "profile") { RefreshPick(ctx, target); return; }
            if (target == "stopsounds") return;
            if (target == "mix" || target == "side" || target == "chatmute")
            {
                bool on = online && (target == "mix" ? mixOn : target == "side" ? sideOn : chatMuted);
                Send(new Dictionary<string, object> { { "event", "setState" }, { "context", ctx },
                    { "payload", new Dictionary<string, object> { { "state", on ? 1 : 0 } } } });
                return;
            }
            int[] s;
            lock (state) { if (!state.TryGetValue(target, out s)) s = null; }
            string value;
            int level;
            string color;
            if (!online || s == null) { value = "Off"; level = 0; color = "#555555"; }
            else if (s[1] == 1) { value = "Muted"; level = s[0]; color = "#666666"; }
            else { value = s[0] + "%"; level = s[0]; color = Colors[target]; }
            Send(new Dictionary<string, object> { { "event", "setFeedback" }, { "context", ctx }, { "payload", new Dictionary<string, object> {
                { "title", target },
                { "value", value },
                { "indicator", new Dictionary<string, object> { { "value", level }, { "bar_fill_c", color } } } } } });
        }

        static void RefreshTarget(string target)
        {
            List<string> ctxs;
            lock (contexts) ctxs = contexts.Where(kv => kv.Value == target).Select(kv => kv.Key).ToList();
            foreach (var c in ctxs) Refresh(c, target);
        }

        static void RefreshAll()
        {
            List<KeyValuePair<string, string>> all;
            lock (contexts) all = contexts.ToList();
            foreach (var kv in all) Refresh(kv.Key, kv.Value);
        }

        static void ShowAlert(string ctx)
        {
            Send(new Dictionary<string, object> { { "event", "showAlert" }, { "context", ctx } });
        }

        // ----- SoundDeck connection -----

        static void SoundDeckLoop()
        {
            while (true)
            {
                try
                {
                    using (var c = new TcpClient())
                    {
                        c.Connect(IPAddress.Loopback, SoundDeckPort);
                        var s = c.GetStream();
                        var w = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                        soundDeck = w;
                        Log("Connected to SoundDeck");
                        RefreshAll();
                        var r = new StreamReader(s, Encoding.UTF8);
                        string line;
                        while ((line = r.ReadLine()) != null) OnSoundDeckLine(line);
                    }
                }
                catch { }
                if (soundDeck != null)
                {
                    soundDeck = null;
                    Log("SoundDeck disconnected");
                    RefreshAll();
                }
                Thread.Sleep(2000);
            }
        }

        static void OnSoundDeckLine(string line)
        {
            var p = line.Split(' ');
            if (p[0] == "state" && p.Length == 4)
            {
                int vol, mute;
                if (!int.TryParse(p[2], out vol) || !int.TryParse(p[3], out mute)) return;
                lock (state) state[p[1]] = new[] { vol, mute };
                RefreshTarget(p[1]);
            }
            else if (p[0] == "mix" && p.Length == 2)
            {
                mixOn = p[1] == "1";
                RefreshTarget("mix");
            }
            else if (p[0] == "out" && p.Length >= 4)
            {
                lock (outputs) outputs[p[1]] = new[] { p[2], string.Join(" ", p.Skip(3)) };
                RefreshTarget("out:" + p[1]);
            }
            else if (p[0] == "devices")
            {
                var f = line.Substring(Math.Min(line.Length, 8)).Split('\t');
                var list = new List<string[]>();
                for (int i = 0; i + 1 < f.Length; i += 2) list.Add(new[] { f[i], f[i + 1] });
                lock (outputs) devices = list;
                UpdatePanels();
            }
            else if (p[0] == "sounds")
            {
                lock (outputs) sounds = line.Length > 7 ? line.Substring(7).Split('\t') : new string[0];
                RefreshTarget("sound");
                UpdatePanels();
            }
            else if (p[0] == "profiles")
            {
                var f = line.Length > 9 ? line.Substring(9).Split('\t') : new[] { "-" };
                lock (outputs)
                {
                    activeProfile = f[0] == "-" ? "" : f[0];
                    profiles = f.Skip(1).Where(x => x != "").ToList();
                }
                RefreshTarget("profile");
                UpdatePanels();
            }
            else if (p[0] == "side" && p.Length == 2)
            {
                sideOn = p[1] == "1";
                RefreshTarget("side");
            }
            else if (p[0] == "chatmute" && p.Length == 2)
            {
                chatMuted = p[1] == "1";
                RefreshTarget("chatmute");
            }
        }

        // Re-sends lists to any settings panel that's open.
        static void UpdatePanels()
        {
            List<string> open;
            lock (panels) open = panels.ToList();
            foreach (var c in open)
            {
                ButtonSettings b;
                lock (buttons) { if (!buttons.TryGetValue(c, out b)) continue; }
                SendPanel(c, b.Action);
            }
        }

        // Sends a command to SoundDeck. If it isn't running, starts it and returns false.
        static bool Command(string cmd)
        {
            var w = soundDeck;
            if (w != null)
            {
                try { lock (w) w.WriteLine(cmd); return true; } catch { }
            }
            LaunchSoundDeck();
            return false;
        }

        static void LaunchSoundDeck()
        {
            if ((DateTime.Now - lastLaunch).TotalSeconds < 10) return;
            lastLaunch = DateTime.Now;
            try
            {
                string pathFile = Path.Combine(dir, "sounddeck_path.txt");
                if (!File.Exists(pathFile)) return;
                string exe = File.ReadAllText(pathFile).Trim();
                if (File.Exists(exe) && Process.GetProcessesByName("SoundDeck").Length == 0)
                    Process.Start(new ProcessStartInfo(exe, "--autostart") { UseShellExecute = true });
            }
            catch (Exception ex) { Log(ex.Message); }
        }

        // ----- Helpers -----

        static void Send(Dictionary<string, object> obj)
        {
            if (ws == null || ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(json.Serialize(obj));
            lock (sendLock)
            {
                try { ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait(); }
                catch (Exception ex) { Log("Send failed: " + ex.Message); }
            }
        }

        static string Str(Dictionary<string, object> m, string k)
        {
            object v;
            return m.TryGetValue(k, out v) ? v as string : null;
        }

        static void Log(string s)
        {
            try
            {
                string f = Path.Combine(dir, "plugin.log");
                if (File.Exists(f) && new FileInfo(f).Length > 200000) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + Environment.NewLine);
            }
            catch { }
        }
    }

    // Draws the output-switch key: channel name on top, current device below, in the channel's color.
    static class KeyImage
    {
        public static string Output(string channel, string colorHex, string device)
        {
            const int S = 144;
            var color = ColorTranslator.FromHtml(colorHex);
            using (var bmp = new Bitmap(S, S))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.FromArgb(30, 33, 39));
                using (var bar = new SolidBrush(color)) g.FillRectangle(bar, 0, 0, S, 10);

                var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
                using (var f = new Font("Segoe UI", 22, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(color))
                    g.DrawString(channel.ToUpperInvariant() + " OUT", f, b, new RectangleF(0, 16, S, 32), center);

                // Largest font (down to 15px) that fits the device name in the lower area.
                var area = new RectangleF(6, 52, S - 12, S - 58);
                Font font = null;
                for (int px = 26; px >= 15; px -= 1)
                {
                    if (font != null) font.Dispose();
                    font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
                    var size = g.MeasureString(device, font, (int)area.Width);
                    if (size.Height <= area.Height && LongestWord(g, device, font) <= area.Width) break;
                }
                using (var w = new SolidBrush(Color.White)) g.DrawString(device, font, w, area, center);
                font.Dispose();

                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        // Key with a small colored heading and big text; "lit" fills the key with the color (e.g. active profile).
        public static string Label(string heading, string colorHex, string text, bool lit)
        {
            const int S = 144;
            var color = ColorTranslator.FromHtml(colorHex);
            using (var bmp = new Bitmap(S, S))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(lit ? Color.FromArgb(color.R * 6 / 10, color.G * 6 / 10, color.B * 6 / 10) : Color.FromArgb(30, 33, 39));
                using (var bar = new SolidBrush(color)) g.FillRectangle(bar, 0, 0, S, 10);
                var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
                using (var f = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(lit ? Color.White : color))
                    g.DrawString(heading, f, b, new RectangleF(0, 16, S, 30), center);
                var area = new RectangleF(6, 50, S - 12, S - 56);
                Font font = null;
                for (int px = 30; px >= 15; px--)
                {
                    if (font != null) font.Dispose();
                    font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
                    if (g.MeasureString(text, font, (int)area.Width).Height <= area.Height && LongestWord(g, text, font) <= area.Width) break;
                }
                using (var w = new SolidBrush(Color.White)) g.DrawString(text, font, w, area, center);
                font.Dispose();
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        static float LongestWord(Graphics g, string text, Font f)
        {
            return text.Split(' ').Max(w => g.MeasureString(w, f).Width);
        }
    }
}
