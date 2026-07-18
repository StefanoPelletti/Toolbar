using System.Runtime.InteropServices;
using System.Windows;
using Screen = System.Windows.Forms.Screen;

namespace Toolbar.Services;

public static class DisplayLayout
{
    // Must stay at or below the smallest bar footprint (1 column/row at scale
    // 1.0 = 62 DIP), otherwise a slim bar docked flush to a right/bottom edge
    // fails the pre-layout visibility probe below and loses its position on
    // every restart.
    private const double VisibilityMargin = 60.0;

    // WinForms Screen reports device pixels; WPF Left/Top/ActualWidth are DIPs.
    // The app is system-DPI-aware (WPF default — no per-monitor manifest), so a
    // single process-wide factor converts between the two spaces, fixed for the
    // session. Mixing the two raw (as this code once did) breaks edge-docking
    // and off-screen detection on any display scaled above 100%.
    private static readonly double DipScale = QueryDipScale();

    private static double QueryDipScale()
    {
        try { return GetDpiForSystem() / 96.0; }
        catch { return 1.0; } // pre-1607 Windows — cannot run .NET 10 anyway
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private static Rect ToDip(System.Drawing.Rectangle r) => new(
        r.X / DipScale, r.Y / DipScale, r.Width / DipScale, r.Height / DipScale);

    /// <summary>
    /// Working area (in WPF DIPs) of the screen containing the given DIP point.
    /// </summary>
    public static Rect WorkingAreaAt(double dipX, double dipY)
    {
        var wa = Screen.FromPoint(new System.Drawing.Point(
            (int)(dipX * DipScale), (int)(dipY * DipScale))).WorkingArea;
        return ToDip(wa);
    }

    public static string Signature()
    {
        var parts = Screen.AllScreens
            .Select(s => $"{s.DeviceName}|{s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width},{s.Bounds.Height}|{(s.Primary ? 1 : 0)}")
            .OrderBy(s => s, StringComparer.Ordinal);
        return string.Join(";", parts);
    }

    // left/top/width/height are WPF DIPs (WindowPositions entries store DIPs).
    public static bool IsVisibleOn(double left, double top, double width, double height)
    {
        // A safe width/height fallback so a freshly-loaded position (before layout) still
        // gets a meaningful visibility check.
        if (double.IsNaN(width)  || width  <= 0) width  = VisibilityMargin;
        if (double.IsNaN(height) || height <= 0) height = VisibilityMargin;

        // A window smaller than the margin can never overlap by the full margin
        // even when it is entirely on-screen — require its own size instead.
        double needX = Math.Min(width,  VisibilityMargin);
        double needY = Math.Min(height, VisibilityMargin);

        foreach (var screen in Screen.AllScreens)
        {
            var wa = ToDip(screen.WorkingArea);
            double ix = Math.Max(0, Math.Min(left + width,  wa.Right)  - Math.Max(left, wa.Left));
            double iy = Math.Max(0, Math.Min(top  + height, wa.Bottom) - Math.Max(top,  wa.Top));
            if (ix >= needX && iy >= needY) return true;
        }
        return false;
    }

    public static (double Left, double Top) DefaultPosition() => (100, 100);
}
