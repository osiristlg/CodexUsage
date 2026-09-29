namespace CodexUsageDashboard;

internal sealed class ProjectTotalsForm : Form
{
    // Native tooltip fades can composite the underlying grid through our custom paint.
    private readonly ToolTip modelTip = new()
    {
        OwnerDraw = true, ShowAlways = true, UseFading = false, UseAnimation = false
    };
    private readonly Font tipFont = new("Segoe UI", 9f);
    private int hoveredRow = -1;

    public ProjectTotalsForm(Dictionary<string, Dictionary<string, long>> projects, string source)
    {
        var theme = ThemeCatalog.Current;
        Text = "30-day project totals";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(860, 680);
        MinimumSize = new Size(520, 360);
        BackColor = theme.Background;
        ForeColor = theme.Text;
        Font = new Font("Segoe UI", 10f);

        var ranked = projects.Select(project => (Name: project.Key, Models: project.Value,
            Tokens: project.Value.Values.Sum())).OrderByDescending(project => project.Tokens)
            .ThenBy(project => project.Name).ToArray();
        var total = ranked.Sum(project => project.Tokens);
        var heading = new TotalsHeading(total, ranked.Length, source) { Dock = DockStyle.Top, Height = 194 };
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = theme.Panel, BorderStyle = BorderStyle.None,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EnableHeadersVisualStyles = false, ShowCellToolTips = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 44, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Blend(theme.Panel, theme.Muted, .12f), RowTemplate = { Height = 44 },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = theme.Panel, ForeColor = theme.Text,
                SelectionBackColor = theme.Panel, SelectionForeColor = theme.Text,
                Padding = new Padding(8, 0, 8, 0)
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = theme.Background, ForeColor = theme.Muted,
                SelectionBackColor = theme.Background, SelectionForeColor = theme.Muted,
                Padding = new Padding(8, 0, 8, 0)
            }
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Project", HeaderText = "PROJECT", FillWeight = 48 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Tokens", HeaderText = "TOTAL TOKENS", FillWeight = 32,
            DefaultCellStyle = new DataGridViewCellStyle { Format = "N0", Alignment = DataGridViewContentAlignment.MiddleRight }
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Share", HeaderText = "SHARE", FillWeight = 20 });
        foreach (var project in ranked)
        {
            var index = grid.Rows.Add(project.Name, project.Tokens, total > 0 ? (double)project.Tokens / total : 0d);
            var tooltip = project.Name + "\n" + string.Join("\n", project.Models
                .OrderByDescending(model => model.Value).ThenBy(model => model.Key)
                .Select(model => $"{model.Key}: {model.Value:N0} tokens"));
            foreach (DataGridViewCell cell in grid.Rows[index].Cells) cell.ToolTipText = tooltip;
        }
        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics is null) return;
            DashboardDrawing.Configure(e.Graphics);
            using var background = new SolidBrush(theme.Panel);
            e.Graphics.FillRectangle(background, e.CellBounds);
            using var divider = new Pen(Blend(theme.Panel, theme.Muted, .12f));
            e.Graphics.DrawLine(divider, e.CellBounds.Left, e.CellBounds.Bottom - 1,
                e.CellBounds.Right, e.CellBounds.Bottom - 1);
            if (e.RowIndex == hoveredRow)
            {
                using var glow = new SolidBrush(Color.FromArgb(22, theme.Tertiary));
                using var edge = new Pen(Color.FromArgb(120, theme.Tertiary));
                var bounds = e.CellBounds;
                e.Graphics.FillRectangle(glow, bounds);
                e.Graphics.DrawLine(edge, bounds.Left, bounds.Top, bounds.Right, bounds.Top);
                e.Graphics.DrawLine(edge, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
                if (e.ColumnIndex == 0) e.Graphics.DrawLine(edge, bounds.Left, bounds.Top, bounds.Left, bounds.Bottom);
                if (e.ColumnIndex == 2) e.Graphics.DrawLine(edge, bounds.Right - 1, bounds.Top, bounds.Right - 1, bounds.Bottom);
            }
            if (e.ColumnIndex != 2)
            {
                e.Paint(e.ClipBounds, DataGridViewPaintParts.ContentForeground);
                e.Handled = true;
                return;
            }
            var share = (double)(e.Value ?? 0d);
            var track = new Rectangle(e.CellBounds.X + 12, e.CellBounds.Bottom - 12,
                Math.Max(1, e.CellBounds.Width - 24), 4);
            using var muted = new SolidBrush(Color.FromArgb(45, theme.Muted));
            using var accent = new SolidBrush(theme.Primary);
            e.Graphics.FillRectangle(muted, track);
            if (share > 0) e.Graphics.FillRectangle(accent, track.X, track.Y, Math.Max(2, (float)(track.Width * share)), track.Height);
            TextRenderer.DrawText(e.Graphics, $"{share:P1}", grid.Font,
                new Rectangle(e.CellBounds.X + 12, e.CellBounds.Y + 4, track.Width, 24), theme.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            e.Handled = true;
        };
        modelTip.Popup += (_, e) =>
        {
            if (hoveredRow < 0) return;
            var project = ranked[hoveredRow];
            var width = Math.Max(300, project.Models.Select(model =>
                TextRenderer.MeasureText(model.Key + "   " + model.Value.ToString("N0"), tipFont).Width + 50).DefaultIfEmpty(300).Max());
            width = Math.Max(width, TextRenderer.MeasureText(project.Name, tipFont).Width + 28);
            e.ToolTipSize = new Size(width, 62 + project.Models.Count * 22);
        };
        modelTip.Draw += (_, e) =>
        {
            if (hoveredRow < 0) return;
            var project = ranked[hoveredRow];
            ClipTooltipWindow(e.Graphics);
            DashboardDrawing.Configure(e.Graphics);
            e.Graphics.Clear(theme.Background);
            DashboardDrawing.RoundedPanel(e.Graphics, Rectangle.Inflate(e.Bounds, -1, -1), 8,
                Color.FromArgb(Math.Max(0, theme.Panel.R - 4), Math.Max(0, theme.Panel.G - 4), Math.Max(0, theme.Panel.B - 4)),
                Color.FromArgb(145, theme.Tertiary));
            using var label = new Font("Segoe UI Semibold", 8.5f);
            using var value = new Font("Segoe UI Semibold", 10f);
            using var muted = new SolidBrush(theme.Muted);
            using var text = new SolidBrush(theme.Text);
            e.Graphics.DrawString(project.Name, label, muted, 12, 9);
            e.Graphics.DrawString($"{project.Tokens:N0} tokens", value, text, 12, 27);
            var i = 0;
            foreach (var model in project.Models.OrderByDescending(model => model.Value).ThenBy(model => model.Key))
            {
                var y = 53 + i * 22;
                using var dot = new SolidBrush(theme.Series[i % theme.Series.Length]);
                e.Graphics.FillEllipse(dot, 13, y + 4, 7, 7);
                e.Graphics.DrawString(model.Key, label, muted, 28, y);
                var count = model.Value.ToString("N0");
                var size = e.Graphics.MeasureString(count, label);
                e.Graphics.DrawString(count, label, text, e.Bounds.Right - size.Width - 12, y);
                i++;
            }
        };
        grid.CellMouseEnter += (_, e) =>
        {
            if (hoveredRow == e.RowIndex) return;
            hoveredRow = e.RowIndex;
            grid.Invalidate();
            modelTip.Hide(grid);
            modelTip.SetToolTip(grid, e.RowIndex >= 0 ? grid.Rows[e.RowIndex].Cells[0].ToolTipText : "");
        };
        grid.MouseLeave += (_, _) => { hoveredRow = -1; grid.Invalidate(); modelTip.Hide(grid); };
        grid.Scroll += (_, _) => modelTip.Hide(grid);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 20), BackColor = theme.Background };
        body.Controls.Add(grid);
        Controls.Add(body);
        Controls.Add(heading);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { modelTip.Dispose(); tipFont.Dispose(); }
        base.Dispose(disposing);
    }

    private static void ClipTooltipWindow(Graphics graphics)
    {
        var dc = graphics.GetHdc();
        nint window;
        try { window = WindowFromDC(dc); }
        finally { graphics.ReleaseHdc(dc); }
        if (window == 0 || !GetWindowRect(window, out var bounds)) return;
        var region = CreateRoundRectRgn(0, 0, bounds.Right - bounds.Left + 1,
            bounds.Bottom - bounds.Top + 1, 16, 16);
        // Windows owns the region after a successful assignment.
        if (region != 0 && SetWindowRgn(window, region, false) == 0) DeleteObject(region);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint WindowFromDC(nint dc);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint window, nint region,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool redraw);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);

    private static Color Blend(Color background, Color foreground, float amount) => Color.FromArgb(
        (int)(background.R + (foreground.R - background.R) * amount),
        (int)(background.G + (foreground.G - background.G) * amount),
        (int)(background.B + (foreground.B - background.B) * amount));

    private sealed class TotalsHeading(long total, int projects, string source) : Control
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var theme = ThemeCatalog.Current;
            DashboardDrawing.Configure(e.Graphics);
            using var caption = new Font("Segoe UI Semibold", 10f);
            using var value = new Font("Segoe UI Variable Display", 32f, FontStyle.Bold);
            using var muted = new SolidBrush(theme.Muted);
            using var text = new SolidBrush(theme.Text);
            DashboardDrawing.RoundedPanel(e.Graphics, new Rectangle(20, 14, Math.Max(20, Width - 40), Height - 30), 18,
                theme.Panel, Color.FromArgb(55, theme.Secondary));
            e.Graphics.DrawString("PROJECT TOTALS  /  30 DAYS", caption, muted, 40, 30);
            e.Graphics.DrawString($"{total:N0}", value, text, 35, 53);
            e.Graphics.DrawString($"{DateTime.Today.AddDays(-29):MMM d} – {DateTime.Today:MMM d}  ·  {source}  ·  {projects} projects",
                caption, muted, 40, 120);
            e.Graphics.DrawString(projects == 0 ? "No project usage recorded in this period." :
                "Hover over a project to explore its model usage", caption, muted, 40, 145);
        }
    }
}
