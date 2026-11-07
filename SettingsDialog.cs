using System;
using System.Drawing;
using System.Windows.Forms;

namespace FsxWeatherInjector
{
    /// <summary>Settings: 1. METAR source, 2. Appearance, 3. Station selection.</summary>
    public class SettingsDialog : Form
    {
        readonly Settings cfg;

        readonly ComboBox cmbSource = new ComboBox();
        readonly CheckBox chkFallback = new CheckBox();
        readonly TextBox txtCustom = new TextBox();
        readonly Button btnTest = new Button();
        readonly Label lblTest = new Label();

        readonly ComboBox cmbTheme = new ComboBox();

        readonly CheckBox chkMerge = new CheckBox();
        readonly NumericUpDown numMerge = new NumericUpDown();

        readonly Button btnOk = new Button();
        readonly Button btnCancel = new Button();

        public SettingsDialog(Settings settings, Icon icon)
        {
            cfg = settings;

            Text = "Settings";
            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(6F, 13F);
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 500);
            if (icon != null) Icon = icon;

            Build();
            Load_();
            Theme.Apply(this);
            lblTest.ForeColor = Theme.Muted;
        }

        // ---- layout ----------------------------------------------------------------

        void Build()
        {
            // 1. METAR source
            var g1 = new GroupBox { Text = "1. METAR source", Location = new Point(12, 10), Size = new Size(536, 214) };

            g1.Controls.Add(Lbl("Source:", 14, 29));
            cmbSource.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSource.Location = new Point(110, 25);
            cmbSource.Width = 412;
            cmbSource.Items.AddRange(new object[]
            {
                "NOAA Aviation Weather Center (default)",
                "VATSIM METAR",
                "Custom address (IVAO or another network)"
            });
            cmbSource.SelectedIndexChanged += (s, e) => UpdateEnabled();
            g1.Controls.Add(cmbSource);

            g1.Controls.Add(Note("Airports and their positions always come from NOAA. This setting chooses whose weather report " +
                                 "is injected for each airport.", 14, 54, 508, 32));

            chkFallback.Text = "If the chosen source has no report (or only an old one), use NOAA's";
            chkFallback.AutoSize = true;
            chkFallback.Location = new Point(14, 90);
            g1.Controls.Add(chkFallback);

            g1.Controls.Add(Lbl("Address:", 14, 123));
            txtCustom.Location = new Point(110, 119);
            txtCustom.Width = 412;
            g1.Controls.Add(txtCustom);

            g1.Controls.Add(Note("Put {icaos} where a comma-separated list of airports goes (one request for all), or {icao} for one " +
                                 "request per airport. Example: https://example.com/metar?ids={icaos}", 14, 146, 508, 32));

            btnTest.Text = "Test source";
            btnTest.AutoSize = true;
            btnTest.Location = new Point(14, 180);
            btnTest.Click += (s, e) => TestSource();
            g1.Controls.Add(btnTest);

            lblTest.AutoSize = false;
            lblTest.Location = new Point(124, 185);
            lblTest.Size = new Size(398, 20);
            g1.Controls.Add(lblTest);
            Controls.Add(g1);

            // 2. Appearance
            var g2 = new GroupBox { Text = "2. Appearance", Location = new Point(12, 232), Size = new Size(536, 62) };
            g2.Controls.Add(Lbl("Theme:", 14, 29));
            cmbTheme.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbTheme.Location = new Point(110, 25);
            cmbTheme.Width = 200;
            cmbTheme.Items.AddRange(new object[] { "Light", "Dark", "Follow Windows" });
            g2.Controls.Add(cmbTheme);
            g2.Controls.Add(Note("Message boxes are drawn by Windows and stay light.", 322, 29, 200, 28));
            Controls.Add(g2);

            // 3. Station selection
            var g3 = new GroupBox { Text = "3. Station selection", Location = new Point(12, 302), Size = new Size(536, 142) };
            chkMerge.Text = "Merge nearby stations that report the same weather";
            chkMerge.AutoSize = true;
            chkMerge.Location = new Point(14, 26);
            chkMerge.CheckedChanged += (s, e) => UpdateEnabled();
            g3.Controls.Add(chkMerge);

            g3.Controls.Add(Lbl("Merge stations closer than", 34, 56));
            numMerge.Minimum = 5; numMerge.Maximum = 150; numMerge.Increment = 5;
            numMerge.Location = new Point(190, 52);
            numMerge.Width = 60;
            g3.Controls.Add(numMerge);
            g3.Controls.Add(Lbl("nm", 256, 56));

            g3.Controls.Add(Note("Stations are taken nearest first. If one is within this distance of a station already chosen and has " +
                                 "practically the same wind, visibility, cloud, temperature, pressure and weather, it is left out and the next " +
                                 "station further away is used instead, so your station limit covers more ground.", 14, 84, 508, 50));
            Controls.Add(g3);

            // buttons
            btnOk.Text = "OK";
            btnOk.Size = new Size(90, 28);
            btnOk.Location = new Point(366, 458);
            btnOk.Click += (s, e) => Accept();
            btnCancel.Text = "Cancel";
            btnCancel.Size = new Size(90, 28);
            btnCancel.Location = new Point(462, 458);
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnOk);
            Controls.Add(btnCancel);
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        static Label Lbl(string text, int x, int y)
        {
            return new Label { Text = text, AutoSize = true, Location = new Point(x, y) };
        }

        static Label Note(string text, int x, int y, int w, int h)
        {
            return new Label { Text = text, AutoSize = false, Location = new Point(x, y), Size = new Size(w, h) };
        }

        // ---- values ----------------------------------------------------------------

        void Load_()
        {
            cmbSource.SelectedIndex = cfg.GetSourceKind() == SourceKind.Vatsim ? 1 : cfg.GetSourceKind() == SourceKind.Custom ? 2 : 0;
            chkFallback.Checked = cfg.FallbackToNoaa;
            txtCustom.Text = cfg.CustomUrl ?? "";

            cmbTheme.SelectedIndex = cfg.ThemeMode == "dark" ? 1 : cfg.ThemeMode == "system" ? 2 : 0;

            chkMerge.Checked = cfg.MergeStations;
            numMerge.Value = Math.Max(numMerge.Minimum, Math.Min(numMerge.Maximum, cfg.MergeNm));
            UpdateEnabled();
        }

        SourceKind SelectedKind()
        {
            return cmbSource.SelectedIndex == 1 ? SourceKind.Vatsim : cmbSource.SelectedIndex == 2 ? SourceKind.Custom : SourceKind.Noaa;
        }

        void UpdateEnabled()
        {
            bool custom = cmbSource.SelectedIndex == 2;
            chkFallback.Enabled = cmbSource.SelectedIndex != 0;
            txtCustom.Enabled = custom;
            numMerge.Enabled = chkMerge.Checked;
        }

        void Accept()
        {
            if (SelectedKind() == SourceKind.Custom && !MetarSources.IsValidTemplate(txtCustom.Text.Trim()))
            {
                MessageBox.Show(this,
                    "The custom address must start with http:// or https:// and contain {icao} or {icaos}.\n\n" +
                    "Example: https://example.com/metar?ids={icaos}",
                    "Custom address", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtCustom.Focus();
                return;
            }

            cfg.SetSourceKind(SelectedKind());
            cfg.FallbackToNoaa = chkFallback.Checked;
            cfg.CustomUrl = txtCustom.Text.Trim();
            cfg.ThemeMode = cmbTheme.SelectedIndex == 1 ? "dark" : cmbTheme.SelectedIndex == 2 ? "system" : "light";
            cfg.MergeStations = chkMerge.Checked;
            cfg.MergeNm = (int)numMerge.Value;
            cfg.Save();
            Logger.Info(string.Format("Settings saved: source={0} fallback={1} theme={2} merge={3}@{4}nm custom='{5}'",
                cfg.MetarSource, cfg.FallbackToNoaa, cfg.ThemeMode, cfg.MergeStations, cfg.MergeNm, cfg.CustomUrl));

            DialogResult = DialogResult.OK;
            Close();
        }

        // ---- "Test source" ---------------------------------------------------------

        async void TestSource()
        {
            SourceKind kind = SelectedKind();
            string url = txtCustom.Text.Trim();
            if (kind == SourceKind.Custom && !MetarSources.IsValidTemplate(url))
            {
                SetTest("The address needs http(s):// and {icao} or {icaos}.", false);
                return;
            }

            btnTest.Enabled = false;
            SetTest("Testing…", null);
            try
            {
                var wanted = new[] { "KJFK", "EGLL" };
                // No fallback here: we want to know whether the chosen source itself answers.
                var results = await System.Threading.Tasks.Task.Run(() => MetarService.Lookup(wanted, kind, url, false));
                string good = null;
                int n = 0;
                foreach (var r in results)
                    if (r.Found) { n++; if (good == null) good = r.Station.Raw; }

                if (n > 0) SetTest("Works: " + n + " of 2 test airports answered (" + Shorten(good) + ")", true);
                else
                {
                    string why = results.Count > 0 && results[0].Note != null ? results[0].Note : "no reports came back.";
                    SetTest("No answer: " + why, false);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Source test failed", ex);
                SetTest("Failed: " + ex.Message, false);
            }
            finally { btnTest.Enabled = true; }
        }

        void SetTest(string text, bool? ok)
        {
            lblTest.Text = text;
            lblTest.ForeColor = ok == null ? Theme.Muted : ok.Value ? Theme.Good : Theme.Bad;
        }

        static string Shorten(string s)
        {
            if (s == null) return "";
            return s.Length > 44 ? s.Substring(0, 44) + "…" : s;
        }
    }
}
