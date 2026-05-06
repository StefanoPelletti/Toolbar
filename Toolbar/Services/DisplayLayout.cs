using System.Windows.Forms;

namespace Toolbar.Services;

public static class DisplayLayout
{
    private const double VisibilityMargin = 80.0;

    public static string Signature()
    {
        var parts = Screen.AllScreens
            .Select(s => $"{s.DeviceName}|{s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width},{s.Bounds.Height}|{(s.Primary ? 1 : 0)}")
            .OrderBy(s => s, StringComparer.Ordinal);
        return string.Join(";", parts);
    }

    public static bool IsVisibleOn(double left, double top, double width, double height)
    {
        // A safe width/height fallback so a freshly-loaded position (before layout) still
        // gets a meaningful visibility check.
        if (double.IsNaN(width)  || width  <= 0) width  = VisibilityMargin;
        if (double.IsNaN(height) || height <= 0) height = VisibilityMargin;

        foreach (var screen in Screen.AllScreens)
        {
            var wa = screen.WorkingArea;
            double ix = Math.Max(0, Math.Min(left + width,  wa.Right)  - Math.Max(left, wa.Left));
            double iy = Math.Max(0, Math.Min(top  + height, wa.Bottom) - Math.Max(top,  wa.Top));
            if (ix >= VisibilityMargin && iy >= VisibilityMargin) return true;
        }
        return false;
    }

    public static (double Left, double Top) DefaultPosition() => (100, 100);
}
