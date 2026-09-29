using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WpadManager.App
{
    internal enum ThemeMode { System, Light, Dark }

    // Colors of one theme. Light keeps the native Windows look (system colors) and only
    // defines the app's own accents; Dark replaces every surface.
    internal sealed class Palette
    {
        public bool Dark;
        public Color Window;        // form background
        public Color Surface;       // toolbar, status bar, grid headers, read-only fields, buttons
        public Color Input;         // editable fields, lists, grid cells
        public Color Border;
        public Color Text;
        public Color TextMuted;     // disabled rules
        public Color Accent;        // the "(default)" row
        public Color Selection;
        public Color SelectionText;
        public Color Hover;
        public Color Pressed;
        public Color GridLine;
        public Color Critical, Error, Warning, Info, Ok;
    }

    // Light / dark theming for WinForms on .NET Framework 4.x, which has no built-in dark
    // mode. Theme.Apply(form) recolors every control of a form; the toolbar gets its own
    // renderer, and the title bar and scrollbars are switched through the same Windows
    // APIs Explorer uses (Windows 10 1809+; older systems simply keep them light).
    // The choice — "system", "light" or "dark" — is persisted in the workspace next to the
    // UI language; "system" follows the Windows app-mode setting. In high-contrast mode
    // nothing is recolored. MessageBox and the file dialogs are drawn by Windows itself.
    internal static class Theme
    {
        public static ThemeMode Mode = ThemeMode.System;

        private static readonly Palette LightP = MakeLight();
        private static readonly Palette DarkP = MakeDark();

        public static bool IsDark
        {
            get
            {
                if (SystemInformation.HighContrast) return false;
                if (Mode == ThemeMode.Dark) return true;
                if (Mode == ThemeMode.Light) return false;
                return SystemPrefersDark();
            }
        }

        public static Palette P { get { return IsDark ? DarkP : LightP; } }

        public static string Code()
        {
            if (Mode == ThemeMode.Light) return "light";
            if (Mode == ThemeMode.Dark) return "dark";
            return "system";
        }

        public static void Set(string code)
        {
            string c = code != null ? code.Trim().ToLowerInvariant() : "";
            Mode = c == "light" ? ThemeMode.Light : c == "dark" ? ThemeMode.Dark : ThemeMode.System;
        }

        // Windows "Choose your app mode": AppsUseLightTheme = 0 means dark.
        public static bool SystemPrefersDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("AppsUseLightTheme") : null;
                    return v is int && (int)v == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        // Text color for a findings line, by its "[SEVERITY]" prefix.
        public static Color SeverityColor(string line, Color fallback)
        {
            if (SystemInformation.HighContrast || line == null) return fallback;
            Palette p = P;
            if (line.StartsWith("[CRITICAL]", StringComparison.Ordinal)) return p.Critical;
            if (line.StartsWith("[ERROR]", StringComparison.Ordinal)) return p.Error;
            if (line.StartsWith("[WARNING]", StringComparison.Ordinal)) return p.Warning;
            if (line.StartsWith("[INFO]", StringComparison.Ordinal)) return p.Info;
            return fallback;
        }

        // ---- applying ----

        public static void Apply(Form f)
        {
            Palette p = P;
            f.BackColor = p.Window;
            f.ForeColor = p.Text;
            ApplyTitleBar(f, p.Dark);
            // Light = the native look: freshly created controls already have it.
            if (!p.Dark) return;
            foreach (Control c in f.Controls) ApplyDark(c, p);
        }

        private static void ApplyDark(Control c, Palette p)
        {
            ToolStrip ts = c as ToolStrip;   // also StatusStrip; its children are items
            if (ts != null) { StyleToolStrip(ts, p); return; }

            DataGridView g = c as DataGridView;
            TextBoxBase tb = c as TextBoxBase;
            ListBox lb = c as ListBox;
            ComboBox cb = c as ComboBox;
            Button bt = c as Button;
            CheckBox ck = c as CheckBox;

            if (g != null) StyleGrid(g, p);
            else if (tb != null)
            {
                tb.BackColor = tb.ReadOnly ? p.Surface : p.Input;
                tb.ForeColor = p.Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
                if (tb.Multiline) SetExplorerTheme(tb, "DarkMode_Explorer");
            }
            else if (lb != null)
            {
                lb.BackColor = p.Input;
                lb.ForeColor = p.Text;
                lb.BorderStyle = BorderStyle.FixedSingle;
                SetExplorerTheme(lb, "DarkMode_Explorer");
            }
            else if (cb != null) StyleCombo(cb, p);
            else if (bt != null)
            {
                bt.FlatStyle = FlatStyle.Flat;
                bt.UseVisualStyleBackColor = false;
                bt.BackColor = p.Surface;
                bt.ForeColor = p.Text;
                bt.FlatAppearance.BorderColor = p.Border;
                bt.FlatAppearance.MouseOverBackColor = p.Hover;
                bt.FlatAppearance.MouseDownBackColor = p.Pressed;
            }
            else if (ck != null)
            {
                ck.FlatStyle = FlatStyle.Flat;
                ck.FlatAppearance.BorderColor = p.Border;
                ck.FlatAppearance.CheckedBackColor = p.Input;
                ck.FlatAppearance.MouseOverBackColor = p.Hover;
            }
            // Labels and panels inherit BackColor/ForeColor from the form (ambient).

            foreach (Control child in c.Controls) ApplyDark(child, p);
        }

        // Standard style lets Windows draw the combo with its own dark theme (the one the
        // file dialogs use); Flat style would draw light borders and a light arrow button.
        private static void StyleCombo(ComboBox cb, Palette p)
        {
            cb.FlatStyle = FlatStyle.Standard;
            cb.BackColor = p.Input;
            cb.ForeColor = p.Text;
            SetExplorerTheme(cb, "DarkMode_CFD");
        }

        private static void StyleGrid(DataGridView g, Palette p)
        {
            g.EnableHeadersVisualStyles = false;
            g.BackgroundColor = p.Input;
            g.GridColor = p.GridLine;
            g.BorderStyle = BorderStyle.FixedSingle;
            g.DefaultCellStyle.BackColor = p.Input;
            g.DefaultCellStyle.ForeColor = p.Text;
            g.DefaultCellStyle.SelectionBackColor = p.Selection;
            g.DefaultCellStyle.SelectionForeColor = p.SelectionText;
            g.ColumnHeadersDefaultCellStyle.BackColor = p.Surface;
            g.ColumnHeadersDefaultCellStyle.ForeColor = p.Text;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.Surface;
            g.ColumnHeadersDefaultCellStyle.SelectionForeColor = p.Text;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            foreach (Control sb in g.Controls)
                if (sb is ScrollBar) SetExplorerTheme(sb, "DarkMode_Explorer");
        }

        private static void StyleToolStrip(ToolStrip ts, Palette p)
        {
            ts.Renderer = new DarkToolStripRenderer(p);
            ts.BackColor = p.Surface;
            ts.ForeColor = p.Text;
            foreach (ToolStripItem it in ts.Items) StyleItem(it, ts.Renderer, p);
        }

        private static void StyleItem(ToolStripItem it, ToolStripRenderer renderer, Palette p)
        {
            it.ForeColor = p.Text;
            ToolStripComboBox tcb = it as ToolStripComboBox;
            if (tcb != null) StyleCombo(tcb.ComboBox, p);

            ToolStripDropDownItem dd = it as ToolStripDropDownItem;
            if (dd != null)
            {
                dd.DropDown.Renderer = renderer;
                dd.DropDown.BackColor = p.Surface;
                dd.DropDown.ForeColor = p.Text;
                foreach (ToolStripItem sub in dd.DropDownItems) StyleItem(sub, renderer, p);
            }
        }

        // ---- palettes ----

        private static Palette MakeLight()
        {
            Palette p = new Palette();
            p.Dark = false;
            p.Window = SystemColors.Control;
            p.Surface = SystemColors.Control;
            p.Input = SystemColors.Window;
            p.Border = SystemColors.ControlDark;
            p.Text = SystemColors.ControlText;
            p.TextMuted = Color.Gray;
            p.Accent = Color.DarkBlue;
            p.Selection = SystemColors.Highlight;
            p.SelectionText = SystemColors.HighlightText;
            p.Hover = SystemColors.ControlLight;
            p.Pressed = SystemColors.ControlDark;
            p.GridLine = SystemColors.ControlDark;
            p.Critical = Color.FromArgb(176, 0, 0);
            p.Error = Color.FromArgb(196, 43, 28);
            p.Warning = Color.FromArgb(138, 90, 0);
            p.Info = Color.FromArgb(0, 95, 184);
            p.Ok = Color.FromArgb(16, 124, 16);
            return p;
        }

        // Contrast of text on Input (30,30,30): Text ~14:1, TextMuted ~5:1, Accent ~7.5:1,
        // severity colors >= 6:1 — all above WCAG AA for normal text.
        private static Palette MakeDark()
        {
            Palette p = new Palette();
            p.Dark = true;
            p.Window = Color.FromArgb(32, 32, 32);
            p.Surface = Color.FromArgb(43, 43, 43);
            p.Input = Color.FromArgb(30, 30, 30);
            p.Border = Color.FromArgb(75, 75, 75);
            p.Text = Color.FromArgb(230, 230, 230);
            p.TextMuted = Color.FromArgb(140, 140, 140);
            p.Accent = Color.FromArgb(110, 178, 250);
            p.Selection = Color.FromArgb(38, 79, 120);
            p.SelectionText = Color.White;
            p.Hover = Color.FromArgb(62, 62, 64);
            p.Pressed = Color.FromArgb(0, 84, 153);
            p.GridLine = Color.FromArgb(60, 60, 60);
            p.Critical = Color.FromArgb(255, 110, 110);
            p.Error = Color.FromArgb(244, 135, 113);
            p.Warning = Color.FromArgb(229, 192, 123);
            p.Info = Color.FromArgb(117, 190, 255);
            p.Ok = Color.FromArgb(137, 209, 133);
            return p;
        }

        // ---- native bits: dark title bar and scrollbars ----

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;         // Windows 10 20H1+ / 11
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;     // Windows 10 1809-1909
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4,
                           SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

        private static void ApplyTitleBar(Form f, bool dark)
        {
            if (!f.IsHandleCreated)
            {
                f.HandleCreated += delegate { SetDarkTitleBar(f.Handle, dark); };
                return;
            }
            SetDarkTitleBar(f.Handle, dark);
            // Repaint the frame of an already visible window (theme switched at runtime).
            SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        private static void SetDarkTitleBar(IntPtr hwnd, bool dark)
        {
            try
            {
                int v = dark ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref v, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private static void SetExplorerTheme(Control c, string name)
        {
            if (c.IsHandleCreated) CallSetWindowTheme(c.Handle, name);
            else c.HandleCreated += delegate { CallSetWindowTheme(c.Handle, name); };
        }

        private static void CallSetWindowTheme(IntPtr hwnd, string name)
        {
            try { SetWindowTheme(hwnd, name, null); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
    }

    // ToolStrip / StatusStrip / drop-down renderer for the dark palette.
    internal sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
    {
        private readonly Palette _p;

        public DarkToolStripRenderer(Palette p) : base(new DarkColorTable(p))
        {
            _p = p;
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? _p.Text : _p.TextMuted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = _p.Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // A check mark drawn as text, so it is visible on the dark drop-down.
            Rectangle r = e.ImageRectangle;
            using (SolidBrush b = new SolidBrush(_p.Selection)) e.Graphics.FillRectangle(b, r);
            TextRenderer.DrawText(e.Graphics, "✓", e.Item.Font, r, _p.SelectionText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            Rectangle r = e.Item.ContentRectangle;
            using (Pen pen = new Pen(_p.Border))
            {
                if (e.Vertical)
                {
                    int x = r.Left + r.Width / 2;
                    e.Graphics.DrawLine(pen, x, r.Top + 3, x, r.Bottom - 3);
                }
                else
                {
                    int y = r.Top + r.Height / 2;
                    e.Graphics.DrawLine(pen, r.Left + 2, y, r.Right - 2, y);
                }
            }
        }
    }

    internal sealed class DarkColorTable : ProfessionalColorTable
    {
        private readonly Palette _p;

        public DarkColorTable(Palette p) { _p = p; UseSystemColors = false; }

        public override Color ToolStripGradientBegin { get { return _p.Surface; } }
        public override Color ToolStripGradientMiddle { get { return _p.Surface; } }
        public override Color ToolStripGradientEnd { get { return _p.Surface; } }
        public override Color ToolStripBorder { get { return _p.Surface; } }
        public override Color ToolStripDropDownBackground { get { return _p.Surface; } }
        public override Color ToolStripContentPanelGradientBegin { get { return _p.Window; } }
        public override Color ToolStripContentPanelGradientEnd { get { return _p.Window; } }
        public override Color ToolStripPanelGradientBegin { get { return _p.Window; } }
        public override Color ToolStripPanelGradientEnd { get { return _p.Window; } }
        public override Color StatusStripGradientBegin { get { return _p.Surface; } }
        public override Color StatusStripGradientEnd { get { return _p.Surface; } }
        public override Color MenuStripGradientBegin { get { return _p.Surface; } }
        public override Color MenuStripGradientEnd { get { return _p.Surface; } }
        public override Color MenuBorder { get { return _p.Border; } }
        public override Color MenuItemBorder { get { return _p.Hover; } }
        public override Color MenuItemSelected { get { return _p.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return _p.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return _p.Hover; } }
        public override Color MenuItemPressedGradientBegin { get { return _p.Pressed; } }
        public override Color MenuItemPressedGradientMiddle { get { return _p.Pressed; } }
        public override Color MenuItemPressedGradientEnd { get { return _p.Pressed; } }
        public override Color ImageMarginGradientBegin { get { return _p.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return _p.Surface; } }
        public override Color ImageMarginGradientEnd { get { return _p.Surface; } }
        public override Color ButtonSelectedHighlight { get { return _p.Hover; } }
        public override Color ButtonSelectedHighlightBorder { get { return _p.Hover; } }
        public override Color ButtonSelectedBorder { get { return _p.Hover; } }
        public override Color ButtonSelectedGradientBegin { get { return _p.Hover; } }
        public override Color ButtonSelectedGradientMiddle { get { return _p.Hover; } }
        public override Color ButtonSelectedGradientEnd { get { return _p.Hover; } }
        public override Color ButtonPressedHighlight { get { return _p.Pressed; } }
        public override Color ButtonPressedHighlightBorder { get { return _p.Pressed; } }
        public override Color ButtonPressedBorder { get { return _p.Pressed; } }
        public override Color ButtonPressedGradientBegin { get { return _p.Pressed; } }
        public override Color ButtonPressedGradientMiddle { get { return _p.Pressed; } }
        public override Color ButtonPressedGradientEnd { get { return _p.Pressed; } }
        public override Color ButtonCheckedHighlight { get { return _p.Selection; } }
        public override Color ButtonCheckedHighlightBorder { get { return _p.Selection; } }
        public override Color ButtonCheckedGradientBegin { get { return _p.Selection; } }
        public override Color ButtonCheckedGradientMiddle { get { return _p.Selection; } }
        public override Color ButtonCheckedGradientEnd { get { return _p.Selection; } }
        public override Color CheckBackground { get { return _p.Selection; } }
        public override Color CheckSelectedBackground { get { return _p.Selection; } }
        public override Color CheckPressedBackground { get { return _p.Selection; } }
        public override Color OverflowButtonGradientBegin { get { return _p.Surface; } }
        public override Color OverflowButtonGradientMiddle { get { return _p.Surface; } }
        public override Color OverflowButtonGradientEnd { get { return _p.Surface; } }
        public override Color SeparatorDark { get { return _p.Border; } }
        public override Color SeparatorLight { get { return _p.Surface; } }
        public override Color GripDark { get { return _p.Border; } }
        public override Color GripLight { get { return _p.Surface; } }
    }
}
