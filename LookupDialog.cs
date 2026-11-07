using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace FsxWeatherInjector
{
    /// <summary>
    /// Look up the latest METAR (and VATSIM online ATIS, when a controller is on) for any airports,
    /// wherever they are. Meant for destination and alternate checks. Stays open while you fly.
    /// </summary>
    public class LookupDialog : Form
    {
        readonly Settings cfg;
        readonly Func<bool> isConnected;
        readonly Func<List<Metar>, int> inject;

        readonly TextBox txtIcao = new TextBox();
        readonly ComboBox cmbSource = new ComboBox();
        readonly Button btnFetch = new Button();
        readonly CheckBox chkAtis = new CheckBox();
        readonly ListView list = new ListView();
        readonly TextBox txtDetail = new TextBox();
        readonly Button btnCopy = new Button();
        readonly Button btnInject = new Button();
        readonly Button btnClose = new Button();
        readonly Label lblStatus = new Label();

        List<LookupResult> results = new List<LookupResult>();
        readonly Dictionary<string, List<AtisInfo>> atis = new Dictionary<string, List<AtisInfo>>(StringComparer.OrdinalIgnoreCase);
        bool atisRequested;
        string atisError;
        bool busy;

        public LookupDialog(Settings settings, Func<bool> fsxConnected, Func<List<Metar>, int> injectIntoFsx, Icon icon)
        {
            cfg = settings;
            isConnected = fsxConnected;
            inject = injectIntoFsx;

            Text = "Airport Weather Lookup";
            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(6F, 13F);
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(780, 560);
            MinimumSize = new Size(660, 440);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            if (icon != null) Icon = icon;

            Build();
            Theme.Apply(this);
            lblStatus.ForeColor = Theme.Muted;
        }

        void Build()
        {
            var lblIcao = new Label { Text = "Airports (ICAO codes, separated by commas or spaces):", AutoSize = true, Location = new Point(12, 12) };
            txtIcao.Location = new Point(12, 32);
            txtIcao.Size = new Size(440, 23);
            txtIcao.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtIcao.CharacterCasing = CharacterCasing.Upper;
            txtIcao.Text = cfg.LookupIcaos ?? "";

            var lblSrc = new Label { Text = "Source:", AutoSize = true, Location = new Point(462, 12), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            cmbSource.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSource.Location = new Point(462, 32);
            cmbSource.Width = 200;
            cmbSource.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cmbSource.Items.AddRange(new object[] { "Use my Settings choice", "NOAA", "VATSIM", "Custom address (Settings)" });
            cmbSource.SelectedIndex = 0;

            btnFetch.Text = "Fetch";
            btnFetch.Location = new Point(672, 30);
            btnFetch.Size = new Size(96, 27);
            btnFetch.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnFetch.Click += (s, e) => FetchAll();

            chkAtis.Text = "Also show VATSIM online ATIS (only when a controller is online at that airport)";
            chkAtis.AutoSize = true;
            chkAtis.Location = new Point(12, 64);
            chkAtis.Checked = true;

            list.Location = new Point(12, 92);
            list.Size = new Size(756, 150);
            list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = true;
            list.Columns.Add("ICAO", 60);
            list.Columns.Add("Source", 120);
            list.Columns.Add("Observed", 150);
            list.Columns.Add("METAR", 400);
            list.SelectedIndexChanged += (s, e) => { ShowDetail(); UpdateButtons(); };

            txtDetail.Location = new Point(12, 252);
            txtDetail.Size = new Size(756, 256);
            txtDetail.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            txtDetail.Multiline = true;
            txtDetail.ReadOnly = true;
            txtDetail.ScrollBars = ScrollBars.Vertical;
            txtDetail.Font = new Font("Consolas", 9.5f);

            lblStatus.AutoSize = false;
            lblStatus.Location = new Point(12, 522);
            lblStatus.Size = new Size(450, 20);
            lblStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            lblStatus.Text = "Type the destination and alternates, then press Fetch.";

            btnCopy.Text = "Copy";
            btnCopy.Size = new Size(80, 28);
            btnCopy.Location = new Point(468, 516);
            btnCopy.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnCopy.Click += (s, e) => CopyDetail();

            btnInject.Text = "Inject into FSX";
            btnInject.Size = new Size(118, 28);
            btnInject.Location = new Point(554, 516);
            btnInject.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnInject.Enabled = false;
            btnInject.Click += (s, e) => InjectSelected();

            btnClose.Text = "Close";
            btnClose.Size = new Size(88, 28);
            btnClose.Location = new Point(680, 516);
            btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnClose.Click += (s, e) => Close();

            Controls.AddRange(new Control[]
            {
                lblIcao, txtIcao, lblSrc, cmbSource, btnFetch, chkAtis, list, txtDetail, lblStatus, btnCopy, btnInject, btnClose
            });
            AcceptButton = btnFetch;
            CancelButton = btnClose;
        }

        // ---- fetching --------------------------------------------------------------

        static List<string> ParseIcaos(string text)
        {
            var seen = new HashSet<string>();
            var l = new List<string>();
            foreach (var part in Regex.Split((text ?? "").ToUpperInvariant(), @"[\s,;]+"))
            {
                if (!Regex.IsMatch(part, "^[A-Z0-9]{4}$")) continue;
                if (seen.Add(part)) l.Add(part);
            }
            return l;
        }

        SourceKind ChosenKind()
        {
            switch (cmbSource.SelectedIndex)
            {
                case 1: return SourceKind.Noaa;
                case 2: return SourceKind.Vatsim;
                case 3: return SourceKind.Custom;
                default: return cfg.GetSourceKind();
            }
        }

        async void FetchAll()
        {
            if (busy) return;

            var icaos = ParseIcaos(txtIcao.Text);
            if (icaos.Count == 0) { SetStatus("Type one or more 4-letter airport codes, for example KJFK, EGLL.", false); return; }
            string trimmedNote = "";
            if (icaos.Count > 12) { icaos = icaos.GetRange(0, 12); trimmedNote = " (first 12 airports only)"; }

            cfg.LookupIcaos = string.Join(", ", icaos.ToArray());
            cfg.Save();

            SourceKind kind = ChosenKind();
            string custom = cfg.CustomUrl ?? "";
            bool fallback = cfg.FallbackToNoaa;
            bool wantAtis = chkAtis.Checked;

            if (kind == SourceKind.Custom && !MetarSources.IsValidTemplate(custom))
            {
                SetStatus("Set a valid custom address under Tools > Settings first.", false);
                return;
            }

            busy = true;
            btnFetch.Enabled = false;
            SetStatus("Fetching…", null);
            Logger.Info("LOOKUP " + string.Join(",", icaos.ToArray()) + " source=" + kind + " atis=" + wantAtis);

            try
            {
                results = await System.Threading.Tasks.Task.Run(() => MetarService.Lookup(icaos, kind, custom, fallback));

                atis.Clear();
                atisRequested = wantAtis;
                atisError = null;
                if (wantAtis)
                {
                    try
                    {
                        var found = await System.Threading.Tasks.Task.Run(() => AtisService.Find(icaos));
                        foreach (var a in found)
                        {
                            List<AtisInfo> l;
                            if (!atis.TryGetValue(a.Icao, out l)) { l = new List<AtisInfo>(); atis[a.Icao] = l; }
                            l.Add(a);
                        }
                    }
                    catch (Exception ex)
                    {
                        atisError = ex.Message;
                        Logger.Error("VATSIM ATIS lookup failed", ex);
                    }
                }

                FillList();
                int ok = 0; foreach (var r in results) if (r.Found) ok++;
                SetStatus(string.Format("{0} of {1} airport(s) found at {2:HH:mm:ss}{3}.", ok, results.Count, DateTime.Now, trimmedNote), ok > 0);
            }
            catch (Exception ex)
            {
                Logger.Error("Lookup failed", ex);
                SetStatus("Lookup failed: " + ex.Message, false);
            }
            finally
            {
                busy = false;
                btnFetch.Enabled = true;
            }
        }

        void FillList()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var r in results)
            {
                var it = new ListViewItem(r.Icao) { Tag = r };
                it.SubItems.Add(r.Found ? r.Station.Source : "-");
                it.SubItems.Add(r.Found ? Observed(r.Station.Raw) : "-");
                it.SubItems.Add(r.Found ? r.Station.Raw : (r.Note ?? "No report"));
                list.Items.Add(it);
            }
            list.EndUpdate();
            if (list.Items.Count > 0) list.Items[0].Selected = true;
            else ShowDetail();
            UpdateButtons();
        }

        static string Observed(string raw)
        {
            Wx w = MetarParser.Parse(raw);
            if (w.Day < 0) return "-";
            string t = string.Format("{0:00}{1:00}Z", w.Hour, w.Minute);
            double? age = MetarParser.AgeMinutes(raw, DateTime.UtcNow);
            return age.HasValue ? t + "  (" + Age(age.Value) + ")" : t;
        }

        static string Age(double minutes)
        {
            if (minutes < 0) minutes = 0;
            int m = (int)Math.Round(minutes);
            if (m < 90) return m + " min ago";
            if (m < 48 * 60) return (m / 60) + " h " + (m % 60) + " min ago";
            return (m / 1440) + " days ago";
        }

        // ---- details ---------------------------------------------------------------

        void ShowDetail()
        {
            var sb = new StringBuilder();
            foreach (ListViewItem it in list.SelectedItems)
            {
                var r = it.Tag as LookupResult;
                if (r == null) continue;
                if (sb.Length > 0) sb.AppendLine().AppendLine(new string('-', 60)).AppendLine();
                Describe(sb, r);
            }
            txtDetail.Text = sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        void Describe(StringBuilder sb, LookupResult r)
        {
            if (!r.Found)
            {
                sb.AppendLine(r.Icao + " - no report");
                sb.AppendLine(r.Note ?? "");
            }
            else
            {
                var s = r.Station;
                sb.AppendLine(s.Icao + " - " + s.Name + "   [" + s.Source + "]");
                sb.AppendLine("Observed: " + Observed(s.Raw));
                sb.AppendLine();
                sb.AppendLine(s.Raw);
                sb.AppendLine();
                sb.AppendLine(MetarParser.Describe(s.Raw));
                if (r.Note != null) { sb.AppendLine(); sb.AppendLine(r.Note); }
            }

            if (!atisRequested) return;
            sb.AppendLine();
            if (atisError != null)
            {
                sb.AppendLine("Online ATIS (VATSIM): could not read the VATSIM feed - " + atisError);
                return;
            }
            List<AtisInfo> l;
            if (atis.TryGetValue(r.Icao, out l) && l.Count > 0)
            {
                foreach (var a in l)
                {
                    sb.AppendLine("Online ATIS (VATSIM) - " + a.Callsign + (string.IsNullOrEmpty(a.Frequency) ? "" : "  " + a.Frequency) +
                                  (string.IsNullOrEmpty(a.Code) ? "" : "  information " + a.Code));
                    sb.AppendLine(string.IsNullOrWhiteSpace(a.Text) ? "(the controller has not published any text)" : a.Text);
                    sb.AppendLine();
                }
            }
            else
            {
                sb.AppendLine("No VATSIM online ATIS at " + r.Icao + " right now. (Only airports with a controller online have one; " +
                              "the METAR above is what a real or simulated ATIS reads from.)");
            }
        }

        void UpdateButtons()
        {
            bool canInject = false;
            foreach (ListViewItem it in list.SelectedItems)
            {
                var r = it.Tag as LookupResult;
                if (r != null && r.Found && r.HasCoords) canInject = true;
            }
            btnInject.Enabled = canInject;
        }

        void CopyDetail()
        {
            try
            {
                if (txtDetail.Text.Length > 0) { Clipboard.SetText(txtDetail.Text); SetStatus("Copied to the clipboard.", true); }
            }
            catch (Exception ex) { SetStatus("Could not copy: " + ex.Message, false); }
        }

        void InjectSelected()
        {
            if (isConnected == null || !isConnected()) { SetStatus("FSX is not running.", false); return; }

            var stations = new List<Metar>();
            foreach (ListViewItem it in list.SelectedItems)
            {
                var r = it.Tag as LookupResult;
                if (r != null && r.Found && r.HasCoords) stations.Add(r.Station);
            }
            if (stations.Count == 0) return;

            try
            {
                int n = inject(stations);
                Logger.Info("LOOKUP injected " + n + " station(s) from the lookup dialog");
                SetStatus("Sent " + n + " station(s) to FSX. FSX uses them as you get near each airport.", true);
            }
            catch (Exception ex)
            {
                Logger.Error("Inject from lookup failed", ex);
                SetStatus("Could not inject: " + ex.Message, false);
            }
        }

        void SetStatus(string text, bool? ok)
        {
            lblStatus.Text = text;
            lblStatus.ForeColor = ok == null ? Theme.Muted : ok.Value ? Theme.Good : Theme.Bad;
        }
    }
}
