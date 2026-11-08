// Local-only control connection (127.0.0.1) used by the Stream Deck plugin.
// Line protocol. Plugin -> SoundDeck: "vol Game +2", "vol Game 50", "mute Game", "mixtoggle", "out Game next", "setout Game <id or ->".
// SoundDeck -> plugin: "state Game 75 0" (volume, muted), "mix 1", "out Game <id or -> <device name>",
// and "devices <id>\t<name>\t..." (the outputs the user hasn't hidden).

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SoundDeck
{
    class ControlServer
    {
        public const int Port = 47123;

        TcpListener listener;
        readonly Action<string> onCommand;
        readonly List<StreamWriter> clients = new List<StreamWriter>();
        readonly Dictionary<string, string> state = new Dictionary<string, string>();

        public ControlServer(Action<string> onCommand) { this.onCommand = onCommand; }

        public void Start()
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
                new Thread(AcceptLoop) { IsBackground = true, Name = "Control" }.Start();
            }
            catch { listener = null; }
        }

        void AcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { return; }
                new Thread(() => Serve(c)) { IsBackground = true }.Start();
            }
        }

        void Serve(TcpClient c)
        {
            StreamWriter w = null;
            try
            {
                var s = c.GetStream();
                w = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                lock (clients)
                {
                    foreach (var line in state.Values) w.WriteLine(line);
                    clients.Add(w);
                }
                var r = new StreamReader(s, Encoding.UTF8);
                string cmd;
                bool first = true;
                while ((cmd = r.ReadLine()) != null)
                {
                    // A web page can make the browser send an HTTP request here, with commands hidden in its body.
                    // The plugin never speaks HTTP, so a connection that starts like one is dropped unread.
                    if (first && LooksLikeHttp(cmd)) return;
                    first = false;
                    if (cmd.Trim().Length > 0) onCommand(cmd.Trim());
                }
            }
            catch { }
            finally
            {
                lock (clients) { if (w != null) clients.Remove(w); }
                c.Close();
            }
        }

        static bool LooksLikeHttp(string line)
        {
            if (line.IndexOf(" HTTP/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var m in new[] { "GET ", "POST ", "PUT ", "HEAD ", "OPTIONS ", "DELETE ", "PATCH ", "CONNECT ", "TRACE " })
                if (line.StartsWith(m, StringComparison.Ordinal)) return true;
            return false;
        }

        // Remembers the latest line per key (sent to new clients) and pushes changes to connected ones.
        public void Publish(string key, string line)
        {
            lock (clients)
            {
                string cur;
                if (state.TryGetValue(key, out cur) && cur == line) return;
                state[key] = line;
                foreach (var w in clients.ToArray())
                {
                    try { w.WriteLine(line); } catch { clients.Remove(w); }
                }
            }
        }
    }
}
