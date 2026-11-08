// Headphone tuning: pick your headphone model and SoundDeck adds a correction EQ made for it, from the free
// AutoEq database (github.com/jaakkopasanen/AutoEq). It sits under your own EQ bands, so presets and sliders
// still work on top. The downloaded filters are checked line by line and rewritten, so only plain EQ filters
// ever reach Equalizer APO's config.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace SoundDeck
{
    class HeadphoneEntry
    {
        public string Name, Path, Source;
        public override string ToString() { return Name + "   (" + Source + ")"; }
    }

    static class AutoEq
    {
        const string Base = "https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results/";
        static string CacheFile { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundDeck", "autoeq-index.md"); } }

        static string Download(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "SoundDeck/" + AppVersion.Value);
                wc.Encoding = Encoding.UTF8;
                return wc.DownloadString(url);
            }
        }

        // The list of every headphone AutoEq has, cached for two weeks.
        public static List<HeadphoneEntry> LoadIndex()
        {
            string text = null;
            try
            {
                if (File.Exists(CacheFile) && (DateTime.Now - File.GetLastWriteTime(CacheFile)).TotalDays < 14) text = File.ReadAllText(CacheFile);
            }
            catch { }
            if (text == null)
            {
                try
                {
                    text = Download(Base + "INDEX.md");
                    try { File.WriteAllText(CacheFile, text); } catch { }
                }
                catch
                {
                    if (File.Exists(CacheFile)) text = File.ReadAllText(CacheFile); // offline: use an old list
                    else throw;
                }
            }
            var list = new List<HeadphoneEntry>();
            var rx = new Regex(@"^- \[(.+?)\]\(\./([^)\s]+(?:\([^)\s]*\)[^)\s]*)*)\) by (.+?)\s*$");
            foreach (var line in text.Split('\n'))
            {
                var m = rx.Match(line.TrimEnd('\r'));
                if (!m.Success) continue;
                string path = m.Groups[2].Value;
                if (path.Contains("..") || path.Contains(":") || path.StartsWith("/")) continue;
                list.Add(new HeadphoneEntry { Name = m.Groups[1].Value, Path = path, Source = m.Groups[3].Value });
            }
            return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => SourceRank(e.Source)).ToList();
        }

        // Measurements from these are the most trusted, so they're listed first.
        static int SourceRank(string s)
        {
            if (s.StartsWith("oratory1990")) return 0;
            if (s.StartsWith("crinacle")) return 1;
            if (s.StartsWith("Rtings")) return 2;
            return 3;
        }

        // Downloads the headphone's correction and returns it as clean Equalizer APO lines.
        public static List<string> LoadFilters(HeadphoneEntry e)
        {
            string last = e.Path.Substring(e.Path.LastIndexOf('/') + 1);
            string text = Download(Base + e.Path + "/" + last + "%20ParametricEQ.txt");
            return Clean(text);
        }

        static readonly Regex PreampRx = new Regex(@"^Preamp:\s*(-?\d+(?:\.\d+)?)\s*dB$", RegexOptions.IgnoreCase);
        static readonly Regex FilterRx = new Regex(@"^Filter\s*\d*:\s*ON\s+(PK|LSC|HSC|LS|HS|LP|HP)\s+Fc\s+(\d+(?:\.\d+)?)\s*Hz(?:\s+Gain\s+(-?\d+(?:\.\d+)?)\s*dB)?(?:\s+Q\s+(\d+(?:\.\d+)?))?$", RegexOptions.IgnoreCase);

        public static List<string> Clean(string text)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var outp = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                var m = PreampRx.Match(line);
                if (m.Success)
                {
                    double v = double.Parse(m.Groups[1].Value, inv);
                    if (v >= -30 && v <= 10) outp.Add("Preamp: " + v.ToString("0.0", inv) + " dB");
                    continue;
                }
                m = FilterRx.Match(line);
                if (!m.Success) continue;
                double fc = double.Parse(m.Groups[2].Value, inv);
                if (fc < 10 || fc > 24000) continue;
                var sb = new StringBuilder("Filter: ON " + m.Groups[1].Value.ToUpperInvariant() + " Fc " + fc.ToString("0.#", inv) + " Hz");
                if (m.Groups[3].Success)
                {
                    double g = double.Parse(m.Groups[3].Value, inv);
                    if (g < -30 || g > 30) continue;
                    sb.Append(" Gain " + g.ToString("0.0", inv) + " dB");
                }
                if (m.Groups[4].Success)
                {
                    double q = double.Parse(m.Groups[4].Value, inv);
                    if (q <= 0 || q > 20) continue;
                    sb.Append(" Q " + q.ToString("0.00", inv));
                }
                outp.Add(sb.ToString());
            }
            if (!outp.Any(l => l.StartsWith("Filter"))) throw new Exception("that headphone has no EQ data");
            return outp;
        }
    }

    partial class MainForm
    {
        Label hpName;
        LinkLabel hpRemove;

        void BuildHeadphoneEq()
        {
            var p = pages["Equalizer"];
            var pick = Btn(p, "Headphone tuning...", 450, 169, 170, 28);
            tips.SetToolTip(pick, "Pick your headphone model to add a correction EQ made for it (from the free AutoEq database). Your sliders and presets still work on top.");
            pick.Click += (s, e) => PickHeadphones();
            hpName = Lbl(p, "", 630, 174, 9f, false, Dim);
            hpName.MaximumSize = new Size(260, 0);
            hpRemove = new LinkLabel { Text = "Remove", Left = 630, Top = 192, AutoSize = true, LinkColor = Accent, ActiveLinkColor = Accent, Font = new Font("Segoe UI", 8.5f) };
            hpRemove.LinkClicked += (s, e) =>
            {
                string k = EqKey("hp");
                if (k == null) return;
                Settings.Set(k, ""); Settings.Set(EqKey("hpeq"), "");
                LoadHeadphoneLabel();
                ScheduleApo();
            };
            p.Controls.Add(hpRemove);
            LoadHeadphoneLabel();
        }

        void LoadHeadphoneLabel()
        {
            if (hpName == null) return;
            string k = EqKey("hp");
            string name = k == null ? "" : Settings.Get(k, "");
            hpName.Text = name == "" ? "No headphone tuning" : "Tuned for " + name;
            hpName.ForeColor = name == "" ? Dim : Good;
            hpRemove.Visible = name != "";
        }

        // Lines to add to Equalizer APO for this device's headphone tuning (empty if none).
        static IEnumerable<string> HeadphoneLines(string guid)
        {
            string v = Settings.Get("eq." + guid + ".hpeq", "");
            if (v == "") return new string[0];
            return AutoEq.Clean(v.Replace('|', '\n'));
        }

        void PickHeadphones()
        {
            if (EqKey("hp") == null) { MessageBox.Show(this, "Pick a device first.", "SoundDeck"); return; }
            using (var dlg = new HeadphonePicker())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Filters == null) return;
                Settings.Set(EqKey("hp"), dlg.Picked.ToString().Replace("   (", " (").Replace("=", " "));
                Settings.Set(EqKey("hpeq"), string.Join("|", dlg.Filters));
                if (!eqOn.Checked) eqOn.Checked = true; // also saves and writes the config
                LoadHeadphoneLabel();
                ScheduleApo();
            }
        }
    }

    // Search box + list of headphones. Downloads the picked one's filters before closing.
    class HeadphonePicker : Form
    {
        public HeadphoneEntry Picked;
        public List<string> Filters;
        List<HeadphoneEntry> all = new List<HeadphoneEntry>();
        TextBox search;
        ListBox list;
        Label status;
        Button ok;

        static readonly Color Bg = Color.FromArgb(18, 20, 24), Card = Color.FromArgb(42, 46, 54), Fg = Color.FromArgb(232, 234, 237), Dim = Color.FromArgb(154, 160, 166);

        public HeadphonePicker()
        {
            Text = "Headphone tuning";
            ClientSize = new Size(520, 520);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Bg; ForeColor = Fg; Font = new Font("Segoe UI", 9.5f);

            var info = new Label { Left = 16, Top = 14, Width = 488, Height = 40, ForeColor = Dim, Text = "Type your headphone or headset model. If it's listed more than once, the first one is usually the best measurement." };
            Controls.Add(info);
            search = new TextBox { Left = 16, Top = 58, Width = 488, BackColor = Card, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Enabled = false };
            search.TextChanged += (s, e) => Filter();
            search.KeyDown += (s, e) => { if (e.KeyCode == Keys.Down && list.Items.Count > 0) { list.Focus(); list.SelectedIndex = 0; e.Handled = true; } };
            Controls.Add(search);
            list = new ListBox { Left = 16, Top = 90, Width = 488, Height = 340, BackColor = Card, ForeColor = Fg, BorderStyle = BorderStyle.None, IntegralHeight = false };
            list.SelectedIndexChanged += (s, e) => ok.Enabled = list.SelectedItem is HeadphoneEntry;
            list.DoubleClick += (s, e) => Choose();
            Controls.Add(list);
            status = new Label { Left = 16, Top = 440, Width = 488, Height = 22, ForeColor = Dim, Text = "Loading the headphone list..." };
            Controls.Add(status);
            ok = new Button { Text = "Use this one", Left = 270, Top = 472, Width = 130, Height = 32, Enabled = false, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(255, 106, 61), ForeColor = Color.White };
            ok.FlatAppearance.BorderSize = 0;
            ok.Click += (s, e) => Choose();
            Controls.Add(ok);
            var cancel = new Button { Text = "Cancel", Left = 410, Top = 472, Width = 94, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Card, ForeColor = Fg, DialogResult = DialogResult.Cancel };
            cancel.FlatAppearance.BorderSize = 0;
            Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;

            new Thread(() =>
            {
                List<HeadphoneEntry> got = null; string err = null;
                try { got = AutoEq.LoadIndex(); } catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (err != null) { status.Text = "Couldn't download the headphone list: " + err; return; }
                        all = got;
                        status.Text = all.Count + " headphones. Measurements: AutoEq by Jaakko Pasanen.";
                        search.Enabled = true; search.Focus();
                        Filter();
                    }));
                }
                catch { }
            }) { IsBackground = true }.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { } // dark title bar
        }
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void Filter()
        {
            var words = search.Text.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            list.BeginUpdate();
            list.Items.Clear();
            if (words.Length > 0)
            {
                foreach (var e in all.Where(x => words.All(w => x.Name.ToLowerInvariant().Contains(w))).Take(400)) list.Items.Add(e);
            }
            list.EndUpdate();
            ok.Enabled = false;
            if (words.Length > 0 && list.Items.Count == 0) status.Text = "Nothing found. Try fewer words, like just the brand and model number.";
            else if (all.Count > 0) status.Text = words.Length == 0 ? all.Count + " headphones. Start typing to search." : list.Items.Count + " found.";
        }

        void Choose()
        {
            var e = list.SelectedItem as HeadphoneEntry;
            if (e == null) return;
            ok.Enabled = false; list.Enabled = false; search.Enabled = false;
            status.Text = "Downloading the tuning for " + e.Name + "...";
            new Thread(() =>
            {
                List<string> f = null; string err = null;
                try { f = AutoEq.LoadFilters(e); } catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (err != null)
                        {
                            status.Text = "Couldn't get it: " + err;
                            ok.Enabled = true; list.Enabled = true; search.Enabled = true;
                            return;
                        }
                        Picked = e; Filters = f;
                        DialogResult = DialogResult.OK;
                    }));
                }
                catch { }
            }) { IsBackground = true }.Start();
        }
    }
}
