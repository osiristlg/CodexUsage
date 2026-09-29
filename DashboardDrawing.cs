using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace CodexUsageDashboard;

internal static class DashboardDrawing
{
    public static void Configure(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    public static void RoundedPanel(Graphics g, Rectangle bounds, int radius, Color fill, Color edge)
    {
        using var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using var brush = new SolidBrush(fill);
        using var glow = new Pen(Color.FromArgb(Math.Max(8, edge.A / 3), edge), 5f);
        using var border = new Pen(edge, 1f);
        g.FillPath(brush, path);
        g.DrawPath(glow, path);
        g.DrawPath(border, path);
    }
}
