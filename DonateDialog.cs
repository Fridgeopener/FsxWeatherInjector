using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace FsxWeatherInjector
{
    /// <summary>Small modal dialog with a message and a button that opens the donation page.</summary>
    public class DonateDialog : Form
    {
        public const string Url = "https://www.paypal.com/paypalme/gamerx1st";

        public const string Message =
            "Yes, This is Vibe Coded but testing and bug fixing everything still takes time. " +
            "So, any money you can spare is definitely appreciated";

        public DonateDialog(Icon icon)
        {
            Text = "Support " + MainForm.AppName;
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Padding = new Padding(20);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            if (icon != null) Icon = icon;

            var text = new Label
            {
                Text = Message,
                AutoSize = true,
                MaximumSize = new Size(360, 0), // wraps the text at 360 px
                Margin = new Padding(0, 0, 0, 18)
            };

            var button = new Button
            {
                Text = "Donate via PayPal",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(14, 4, 14, 4),
                Anchor = AnchorStyles.None // centred in its row
            };
            button.Click += (s, e) => OpenLink();

            var layout = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(text, 0, 0);
            layout.Controls.Add(button, 0, 1);
            Controls.Add(layout);

            AcceptButton = button;
        }

        void OpenLink()
        {
            Logger.Info("USER: clicked Donate link " + Url);
            try
            {
                Process.Start(Url);
            }
            catch (Exception ex)
            {
                Logger.Error("Could not open donate link", ex);
                MessageBox.Show(this, "Could not open your browser automatically.\n\nYou can open this address yourself:\n" + Url,
                    MainForm.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>Opens the dialog modally over <paramref name="owner"/>.</summary>
        public static void ShowFor(IWin32Window owner, Icon icon)
        {
            Logger.Info("USER: opened Donate dialog");
            using (var dlg = new DonateDialog(icon)) dlg.ShowDialog(owner);
        }
    }
}
