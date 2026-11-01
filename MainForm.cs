using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace FsxWeatherInjector
{
    public class MainForm : Form
    {
        public const string AppName = "FSX Live Weather Injector";
        public const string AppVersion = "0.5";

        SimLink link;
        readonly Settings cfg = Settings.Load();

        readonly Timer tick = new Timer { Interval = 5000 };   // connect / first-injection retry
        readonly Timer refresh = new Timer();                  // periodic weather refresh

        readonly Label lblConn = new Label();
        readonly Label lblPos = new Label();
        readonly CheckBox chkEnabled = new CheckBox();
        readonly CheckBox chkSync = new CheckBox();
        readonly NumericUpDown numInterval = new NumericUpDown();
        readonly NumericUpDown numStations = new NumericUpDown();
        readonly NumericUpDown numRadius = new NumericUpDown();
        readonly Button btnNow = new Button();
        readonly Button btnDonate = new Button();
        readonly ListView list = new ListView();
        readonly StatusStrip status = new StatusStrip();
        readonly ToolStripStatusLabel lblStatus = new ToolStripStatusLabel();
        readonly MenuStrip menu = new MenuStrip();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip trayMenu = new ContextMenuStrip();

        bool busy;
        bool pendingFirst = true;
        bool fatalShown;

        public MainForm()
        {
            Text = AppName + " v" + AppVersion;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(880, 450);
            MinimumSize = new Size(640, 380);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = LoadIcon(32);

            BuildUi();
            BuildMenus();
            LoadSettingsIntoUi();

            tray.Icon = LoadIcon(SystemInformation.SmallIconSize.Width);
            tray.Text = AppName;
            tray.ContextMenuStrip = trayMenu;
            tray.Visible = true;
            tray.DoubleClick += (s, e) => OpenWindow();

            Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) Hide(); };
            FormClosing += (s, e) => { SaveSettings(); tray.Visible = false; if (link != null) link.Dispose(); };

            tick.Tick += (s, e) => OnTick();
            refresh.Tick += (s, e) => RequestUpdate();
        }

        static Icon LoadIcon(int size)
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                    if (s != null) return new Icon(s, new Size(size, size));
            }
            catch { }
            return SystemIcons.Application;
        }

        // ---- UI construction ---------------------------------------------------------

        void BuildUi()
        {
            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 10, 10, 4),
                WrapContents = true, FlowDirection = FlowDirection.TopDown
            };

            lblConn.AutoSize = true; lblConn.Padding = new Padding(0, 6, 16, 0);
            lblPos.AutoSize = true; lblPos.Padding = new Padding(0, 6, 16, 0);
            lblPos.Text = "Position: –";

            chkEnabled.Text = "Inject live weather"; chkEnabled.AutoSize = true; chkEnabled.Padding = new Padding(0, 3, 16, 0);
            chkEnabled.CheckedChanged += (s, e) => OnEnabledChanged();

            chkSync.Text = "Sync sim time to real UTC (keeps ATIS in step)";
            chkSync.AutoSize = true; chkSync.Padding = new Padding(0, 3, 0, 0);

            SetupNum(numInterval, 1, 120);
            SetupNum(numStations, 1, 12);
            SetupNum(numRadius, 25, 1000); numRadius.Increment = 25;
            numInterval.ValueChanged += (s, e) => { refresh.Interval = (int)numInterval.Value * 60000; };

            btnNow.Text = "Update now"; btnNow.AutoSize = true;
            btnNow.Click += (s, e) => UpdateWeatherNow();

            btnDonate.Text = "♥ Donate"; btnDonate.AutoSize = true;
            btnDonate.Click += (s, e) => DonateDialog.ShowFor(this, Icon);

            var row1 = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            row1.Controls.AddRange(new Control[] { lblConn, lblPos, chkSync });

            var row2 = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            row2.Controls.AddRange(new Control[]
            {
                chkEnabled,
                Lbl("Refresh every"), numInterval, Lbl("min   "),
                Lbl("Stations"), numStations, Lbl("   Radius"), numRadius, Lbl("nm   "),
                btnNow, btnDonate
            });

            top.Controls.Add(row1);
            top.Controls.Add(row2);

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.HideSelection = false;
            list.Columns.Add("ICAO", 60);
            list.Columns.Add("Distance", 70, HorizontalAlignment.Right);
            list.Columns.Add("Station", 170);
            list.Columns.Add("METAR", 440);

            status.Items.Add(lblStatus);
            lblStatus.Text = "Waiting for FSX…";

            // Add order matters for docking: menu last so it sits at the very top.
            Controls.Add(list);
            Controls.Add(top);
            Controls.Add(status);
            Controls.Add(menu);
            MainMenuStrip = menu;
            SetConn(false);
        }

        void BuildMenus()
        {
            // Top menu bar
            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add("&Update Weather Now", null, (s, e) => UpdateWeatherNow());
            file.DropDownItems.Add("&Sync Time", null, (s, e) => SyncTimeNow());
            file.DropDownItems.Add("&Open " + AppName, null, (s, e) => OpenWindow());
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("E&xit", null, (s, e) => ExitApp());

            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add("Check &SimConnect Installation", null, (s, e) => CheckSimConnect());
            help.DropDownItems.Add("Open &Log File", null, (s, e) => OpenLog());
            help.DropDownItems.Add("Open Log &Folder", null, (s, e) => OpenLogFolder());
            help.DropDownItems.Add(new ToolStripSeparator());
            help.DropDownItems.Add("&About", null, (s, e) => ShowAbout());

            menu.Items.Add(file);
            menu.Items.Add(help);

            // Tray right-click menu
            trayMenu.Items.Add("Update Weather now", null, (s, e) => UpdateWeatherNow());
            trayMenu.Items.Add("Sync Time", null, (s, e) => SyncTimeNow());
            var open = new ToolStripMenuItem("Open " + AppName, null, (s, e) => OpenWindow());
            open.Font = new Font(open.Font, FontStyle.Bold);
            trayMenu.Items.Add(open);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit", null, (s, e) => ExitApp());
        }

        static void SetupNum(NumericUpDown n, int min, int max)
        {
            n.Minimum = min; n.Maximum = max; n.Width = 55;
        }

        static Label Lbl(string t)
        {
            return new Label { Text = t, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        }

        // ---- settings ----------------------------------------------------------------

        void LoadSettingsIntoUi()
        {
            chkEnabled.Checked = cfg.Enabled;
            chkSync.Checked = cfg.SyncTime;
            numInterval.Value = cfg.IntervalMin;
            numStations.Value = cfg.Stations;
            numRadius.Value = cfg.RadiusNm;
            refresh.Interval = cfg.IntervalMin * 60000;
        }

        void SaveSettings()
        {
            cfg.Enabled = chkEnabled.Checked;
            cfg.SyncTime = chkSync.Checked;
            cfg.IntervalMin = (int)numInterval.Value;
            cfg.Stations = (int)numStations.Value;
            cfg.RadiusNm = (int)numRadius.Value;
            cfg.Save();
        }

        // ---- lifecycle ---------------------------------------------------------------

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Logger.Info(string.Format("Settings: inject={0} syncTime={1} interval={2}min stations={3} radius={4}nm",
                chkEnabled.Checked, chkSync.Checked, numInterval.Value, numStations.Value, numRadius.Value));
            link = new SimLink(Handle);
            link.ConnectionChanged += OnConnectionChanged;
            link.PositionReceived += OnPosition;
            link.Log += msg => SetStatus(msg);
            link.Fatal += msg =>
            {
                SetStatus("SimConnect problem – see Help > Check SimConnect Installation");
                if (fatalShown) return;
                fatalShown = true;
                // Show after the current call finishes so the window is fully created.
                BeginInvoke(new Action(() => MessageBox.Show(this,
                    "SimConnect could not be initialised, so the app cannot talk to FSX.\n\n" + msg + "\n\n" +
                    "Open Help > Check SimConnect Installation for the exact fix, then use it again to retry.\n" +
                    "Log: " + Logger.FilePath,
                    "SimConnect problem", MessageBoxButtons.OK, MessageBoxIcon.Error)));
            };
            tick.Start();
            OnTick();
        }

        protected override void WndProc(ref Message m)
        {
            if (link != null) link.HandleMessage(ref m);
            base.WndProc(ref m);
        }

        // ---- menu / tray actions -----------------------------------------------------

        void OpenWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        void ExitApp()
        {
            Application.Exit();
        }

        void UpdateWeatherNow()
        {
            if (link == null || !link.Connected) { SetStatus("FSX is not running – nothing to update"); return; }
            if (!chkEnabled.Checked) { SetStatus("Tick 'Inject live weather' first"); return; }
            pendingFirst = true;
            RequestUpdate();
        }

        void SyncTimeNow()
        {
            if (link == null || !link.Connected) { SetStatus("FSX is not running – cannot sync time"); return; }
            if (link.SyncTimeToUtc())
                SetStatus(string.Format("Sim time set to real UTC {0:HH:mm}", DateTime.UtcNow));
        }

        void CheckSimConnect()
        {
            Logger.Info("USER: Help > Check SimConnect Installation");
            // Let the runtime safety net try again after the user has fixed something.
            link.ClearFatal();
            fatalShown = false;
            if (!link.Connected) link.TryConnect();   // FSX not running is fine

            var r = SimConnectCheck.Run(null);
            Logger.Info("MANUAL " + SimConnectCheck.Summary(r));
            if (r.IsFatal) SimConnectCheck.ShowProblem(r, this);
            else MessageBox.Show(this, SimConnectCheck.Report(r), "SimConnect check",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void OpenLog()
        {
            try
            {
                if (!File.Exists(Logger.FilePath)) Logger.Info("Log opened by user");
                Process.Start("notepad.exe", "\"" + Logger.FilePath + "\"");
            }
            catch (Exception ex) { SetStatus("Could not open log: " + ex.Message); }
        }

        void OpenLogFolder()
        {
            try
            {
                Directory.CreateDirectory(Logger.Dir);
                Process.Start("explorer.exe", "\"" + Logger.Dir + "\"");
            }
            catch (Exception ex) { SetStatus("Could not open folder: " + ex.Message); }
        }

        void ShowAbout()
        {
            MessageBox.Show(this,
                AppName + "\nVersion " + AppVersion + "\n\n" +
                "Injects real-world METARs into Microsoft Flight Simulator X through SimConnect, " +
                "and can keep the sim clock on real UTC time so airport ATIS reports match the current weather.\n\n" +
                "Authors:\n" +
                "   Claude (Anthropic)\n" +
                "   FridgeOp\n\n" +
                "Log file:\n" + Logger.FilePath,
                "About " + AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---- connection / scheduling -------------------------------------------------

        void OnTick()
        {
            if (!link.Connected) link.TryConnect();
            else if (pendingFirst && chkEnabled.Checked) RequestUpdate();
        }

        void OnConnectionChanged(bool connected)
        {
            SetConn(connected);
            if (connected)
            {
                pendingFirst = true;
                if (chkEnabled.Checked) link.SetCustomMode();
                refresh.Start();
                SetStatus("Connected to FSX");
            }
            else
            {
                refresh.Stop();
                SetStatus("Waiting for FSX…");
            }
        }

        void SetConn(bool on)
        {
            lblConn.Text = on ? "● FSX connected" : "● FSX not running";
            lblConn.ForeColor = on ? Color.ForestGreen : Color.Firebrick;
        }

        void OnEnabledChanged()
        {
            if (link == null || !link.Connected) return;
            if (chkEnabled.Checked)
            {
                link.SetCustomMode();
                pendingFirst = true;
                RequestUpdate();
            }
            else SetStatus("Injection paused – FSX keeps the last injected weather until you change its weather mode");
        }

        void RequestUpdate()
        {
            if (link != null && link.Connected && chkEnabled.Checked && !busy) link.RequestPosition();
        }

        // ---- weather update ----------------------------------------------------------

        async void OnPosition(double lat, double lon)
        {
            lblPos.Text = string.Format("Position: {0:F2}, {1:F2}", lat, lon);
            if (busy || !chkEnabled.Checked) return;
            busy = true;
            try
            {
                int n = (int)numStations.Value;
                double r = (double)numRadius.Value;
                SetStatus("Fetching METARs…");
                var metars = await System.Threading.Tasks.Task.Run(() => MetarService.Fetch(lat, lon, r, n));

                if (metars.Count == 0) { SetStatus("No METARs found within radius – try increasing it"); return; }

                // Set the sim clock first so the injected observations line up with sim time (and ATIS).
                if (chkSync.Checked) link.SyncTimeToUtc();
                link.Inject(metars);
                pendingFirst = false;
                Fill(metars);
                SetStatus(string.Format("Updated {0:HH:mm} – {1} station(s){2}", DateTime.Now, metars.Count,
                    chkSync.Checked ? ", time synced to UTC" : ""));
            }
            catch (Exception ex)
            {
                Logger.Error("Weather update failed", ex);
                SetStatus("Weather fetch failed: " + ex.Message);
            }
            finally { busy = false; }
        }

        void Fill(List<Metar> metars)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var m in metars)
            {
                var it = new ListViewItem(m.Icao);
                it.SubItems.Add(m.DistanceNm.ToString("F0") + " nm");
                it.SubItems.Add(m.Name);
                it.SubItems.Add(m.Raw);
                list.Items.Add(it);
            }
            list.EndUpdate();
        }

        void SetStatus(string s)
        {
            lblStatus.Text = s;
            Logger.Info("STATUS  " + s);
        }
    }
}
