using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FsxWeatherInjector
{
    /// <summary>
    /// Light / dark colours for every window. Call Theme.Apply(form) once a form is built; it walks all
    /// controls and menus. Standard message boxes are drawn by Windows and always stay light.
    /// </summary>
    public static class Theme
    {
        public static bool Dark { get; private set; }

        public static Color Back = SystemColors.Control;
        public static Color Surface = SystemColors.Window;     // text boxes, lists
        public static Color Fore = SystemColors.ControlText;
        public static Color Muted = SystemColors.GrayText;
        public static Color Border = SystemColors.ControlDark;
        public static Color Header = SystemColors.Control;
        public static Color Good = Color.ForestGreen;
        public static Color Bad = Color.Firebrick;

        static readonly HashSet<ListView> wired = new HashSet<ListView>();

        /// <summary>mode: "light", "dark" or "system" (follow the Windows app setting).</summary>
        public static void SetMode(string mode)
        {
            mode = (mode ?? "light").ToLowerInvariant();
            Dark = mode == "dark" || (mode == "system" && WindowsUsesDark());

            if (Dark)
            {
                Back = Color.FromArgb(32, 32, 34);
                Surface = Color.FromArgb(45, 45, 48);
                Fore = Color.FromArgb(235, 235, 235);
                Muted = Color.FromArgb(160, 160, 165);
                Border = Color.FromArgb(85, 85, 90);
                Header = Color.FromArgb(58, 58, 62);
                Good = Color.FromArgb(90, 205, 120);
                Bad = Color.FromArgb(255, 110, 100);
            }
            else
            {
                Back = SystemColors.Control;
                Surface = SystemColors.Window;
                Fore = SystemColors.ControlText;
                Muted = SystemColors.GrayText;
                Border = SystemColors.ControlDark;
                Header = SystemColors.Control;
                Good = Color.ForestGreen;
                Bad = Color.Firebrick;
            }
        }

        static bool WindowsUsesDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        // ---- applying ------------------------------------------------------------------

        public static void Apply(Control root)
        {
            var form = root as Form;
            if (form != null)
            {
                form.BackColor = Back;
                form.ForeColor = Fore;
                TitleBar(form);
            }
            Walk(root);
        }

        static void Walk(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                Style(c);
                // Composite controls own their children; do not restyle those.
                if (c is NumericUpDown || c is ComboBox || c is ListView || c is TextBox) continue;
                Walk(c);
            }
        }

        static void Style(Control c)
        {
            var strip = c as ToolStrip;
            if (strip != null) { ApplyStrip(strip); return; }

            var button = c as Button;
            if (button != null)
            {
                if (Dark)
                {
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = Border;
                    button.BackColor = Surface;
                    button.ForeColor = Fore;
                    button.UseVisualStyleBackColor = false;
                }
                else
                {
                    button.FlatStyle = FlatStyle.Standard;
                    button.UseVisualStyleBackColor = true;
                    button.ForeColor = SystemColors.ControlText;
                }
                return;
            }

            var check = c as CheckBox;
            var radio = c as RadioButton;
            if (check != null || radio != null)
            {
                var b = (ButtonBase)c;
                b.FlatStyle = Dark ? FlatStyle.Flat : FlatStyle.Standard;
                b.BackColor = Back;
                b.ForeColor = Fore;
                return;
            }

            var tb = c as TextBox;
            if (tb != null)
            {
                tb.BackColor = Surface;
                tb.ForeColor = Fore;
                tb.BorderStyle = Dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
                return;
            }

            var cb = c as ComboBox;
            if (cb != null)
            {
                cb.FlatStyle = Dark ? FlatStyle.Flat : FlatStyle.Standard;
                cb.BackColor = Surface;
                cb.ForeColor = Fore;
                return;
            }

            var num = c as NumericUpDown;
            if (num != null)
            {
                num.BackColor = Surface;
                num.ForeColor = Fore;
                return;
            }

            var lv = c as ListView;
            if (lv != null) { StyleList(lv); return; }

            var gb = c as GroupBox;
            if (gb != null) { gb.BackColor = Back; gb.ForeColor = Fore; return; }

            c.BackColor = Back;
            c.ForeColor = Fore;
        }

        // ---- list view -----------------------------------------------------------------

        static void StyleList(ListView lv)
        {
            lv.BackColor = Surface;
            lv.ForeColor = Fore;
            lv.GridLines = !Dark;
            lv.BorderStyle = Dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;

            if (!wired.Contains(lv))
            {
                wired.Add(lv);
                lv.DrawColumnHeader += DrawHeader;
                lv.DrawItem += (s, e) => e.DrawDefault = true;
                lv.DrawSubItem += (s, e) => e.DrawDefault = true;
                lv.Disposed += (s, e) => wired.Remove(lv);
            }
            // Owner drawing is only needed to paint dark column headers.
            lv.OwnerDraw = Dark;

            try
            {
                if (lv.IsHandleCreated) SetWindowTheme(lv.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
                else lv.HandleCreated += (s, e) => { try { SetWindowTheme(lv.Handle, Theme.Dark ? "DarkMode_Explorer" : "Explorer", null); } catch { } };
            }
            catch { }
        }

        static void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var back = new SolidBrush(Header)) e.Graphics.FillRectangle(back, e.Bounds);
            using (var pen = new Pen(Border))
            {
                e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
            }
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            if (e.Header.TextAlign == HorizontalAlignment.Right) flags |= TextFormatFlags.Right;
            var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, e.Font, r, Fore, flags);
        }

        // ---- menus and status bars -----------------------------------------------------

        public static void ApplyStrip(ToolStrip s)
        {
            if (Dark)
            {
                s.Renderer = new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false };
                s.BackColor = Color.FromArgb(45, 45, 48);
            }
            else
            {
                s.Renderer = new ToolStripProfessionalRenderer();
                s.BackColor = SystemColors.Control;
            }
            s.ForeColor = Fore;
            foreach (ToolStripItem i in s.Items) StyleItem(i);
        }

        static void StyleItem(ToolStripItem item)
        {
            item.ForeColor = Fore;
            item.BackColor = Dark ? Color.FromArgb(37, 37, 38) : SystemColors.Control;
            var dd = item as ToolStripDropDownItem;
            if (dd != null)
            {
                if (dd.DropDown != null)
                {
                    dd.DropDown.BackColor = Dark ? Color.FromArgb(37, 37, 38) : SystemColors.Control;
                    dd.DropDown.ForeColor = Fore;
                }
                foreach (ToolStripItem child in dd.DropDownItems) StyleItem(child);
            }
        }

        class DarkColors : ProfessionalColorTable
        {
            static readonly Color Menu = Color.FromArgb(37, 37, 38);
            static readonly Color Bar = Color.FromArgb(45, 45, 48);
            static readonly Color Hot = Color.FromArgb(70, 70, 76);
            static readonly Color Line = Color.FromArgb(85, 85, 90);

            public DarkColors() { UseSystemColors = false; }

            public override Color ToolStripDropDownBackground { get { return Menu; } }
            public override Color MenuBorder { get { return Line; } }
            public override Color MenuItemBorder { get { return Hot; } }
            public override Color MenuItemSelected { get { return Hot; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hot; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hot; } }
            public override Color MenuItemPressedGradientBegin { get { return Hot; } }
            public override Color MenuItemPressedGradientMiddle { get { return Hot; } }
            public override Color MenuItemPressedGradientEnd { get { return Hot; } }
            public override Color MenuStripGradientBegin { get { return Bar; } }
            public override Color MenuStripGradientEnd { get { return Bar; } }
            public override Color ImageMarginGradientBegin { get { return Menu; } }
            public override Color ImageMarginGradientMiddle { get { return Menu; } }
            public override Color ImageMarginGradientEnd { get { return Menu; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Line; } }
            public override Color StatusStripGradientBegin { get { return Bar; } }
            public override Color StatusStripGradientEnd { get { return Bar; } }
            public override Color ToolStripBorder { get { return Bar; } }
            public override Color CheckBackground { get { return Hot; } }
            public override Color CheckSelectedBackground { get { return Hot; } }
            public override Color CheckPressedBackground { get { return Hot; } }
        }

        // ---- dark title bar (Windows 10 1809+ / 11; harmless elsewhere) ----------------

        static void TitleBar(Form f)
        {
            Action set = () =>
            {
                try
                {
                    int on = Dark ? 1 : 0;
                    // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (newer builds), 19 = the same flag on early 20H1 builds
                    if (DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)) != 0)
                        DwmSetWindowAttribute(f.Handle, 19, ref on, sizeof(int));
                }
                catch { }
            };
            if (f.IsHandleCreated) set();
            else f.HandleCreated += (s, e) => set();
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);
    }
}
