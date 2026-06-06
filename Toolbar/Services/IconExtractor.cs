using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Toolbar.Services;

public static class IconExtractor
{
    // Optional resolvedLnkTarget lets the caller pass a pre-resolved .lnk target
    // so the COM IShellLink call is only made once per shortcut (P3).
    public static ImageSource? FromPath(string path, string? resolvedLnkTarget = null)
    {
        try
        {
            var iconPath = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                ? resolvedLnkTarget ?? ResolveLnkTarget(path) ?? path
                : path;
            return ShellItemImage(iconPath) ?? ShellIcon(iconPath);
        }
        catch
        {
            return null;
        }
    }

    // Callable from any STA thread — uses System.Drawing (not BitmapImage) so
    // it does not require a WPF Dispatcher (P1). Caps at 64×64 so a
    // 1024² custom PNG isn't held in memory for a 32 px tile (P5).
    public static ImageSource? FromFile(string iconPath)
    {
        try
        {
            var ext = Path.GetExtension(iconPath).ToLowerInvariant();

            if (ext == ".ico")
            {
                using var icon = new System.Drawing.Icon(iconPath, 256, 256);
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze(); // B4: was missing; required for cross-thread use
                return src;
            }

            // Raster images (PNG, BMP, JPEG …): load via System.Drawing so this
            // method is safe to call from the background STA loader thread.
            using var bmp = new System.Drawing.Bitmap(iconPath);
            int outSz = Math.Min(64, Math.Max(bmp.Width, bmp.Height));

            using var canvas = new System.Drawing.Bitmap(
                outSz, outSz, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(canvas))
            {
                g.InterpolationMode =
                    System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, 0, 0, outSz, outSz);
            }

            // LockBits + BitmapSource.Create preserves alpha correctly and
            // can be called from any thread (no Dispatcher required).
            var data = canvas.LockBits(
                new System.Drawing.Rectangle(0, 0, canvas.Width, canvas.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var result = BitmapSource.Create(
                    data.Width, data.Height, 96, 96,
                    PixelFormats.Bgra32, null,
                    data.Scan0, data.Stride * data.Height, data.Stride);
                result.Freeze();
                return result;
            }
            finally
            {
                canvas.UnlockBits(data);
            }
        }
        catch
        {
            return null;
        }
    }

    // ── IShellItemImageFactory ───────────────────────────────────────────────

    private static ImageSource? ShellItemImage(string path)
    {
        object? obj = null;
        try
        {
            var iid = IID_IShellItemImageFactory;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out obj);
            if (hr != 0 || obj is null) return null;

            var factory = (IShellItemImageFactory)obj;
            var sz = new SIZE { cx = 48, cy = 48 };
            hr = factory.GetImage(sz, SIIGBF.ResizeToFit, out var hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;

            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(
                    hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                DeleteObject(hbm);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (obj is not null)
                Marshal.ReleaseComObject(obj); // P4: release IShellItemImageFactory RCW
        }
    }

    // ── SHGetFileInfo fallback ───────────────────────────────────────────────

    private static ImageSource? ShellIcon(string path)
    {
        var shfi = new SHFILEINFO();
        var result = SHGetFileInfo(path, 0, ref shfi,
            (uint)Marshal.SizeOf(shfi),
            SHGFI_ICON | SHGFI_LARGEICON);

        if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(
                shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DestroyIcon(shfi.hIcon);
        }
    }

    // ── .lnk resolution via IShellLink ──────────────────────────────────────

    public static string? ResolveLnkTarget(string lnkPath)
    {
        object? shell = null;
        try
        {
            shell = new ShellLink();
            var shellLnk = (IShellLinkW)shell;
            var pf       = (IPersistFile)shell;
            pf.Load(lnkPath, 0 /* STGM_READ */);

            var sb = new StringBuilder(260);
            shellLnk.GetPath(sb, 260, IntPtr.Zero, 0);
            var target = sb.ToString();
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shell is not null)
                Marshal.ReleaseComObject(shell); // P4: release IShellLink + IPersistFile RCW
        }
    }

    // ── COM interfaces ───────────────────────────────────────────────────────

    private static Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage([In] SIZE sizetImage, [In] SIIGBF uFlags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [Flags]
    private enum SIIGBF : int
    {
        ResizeToFit    = 0x00,
        BiggerSizeOk   = 0x01,
        MemoryOnly     = 0x02,
        IconOnly       = 0x04,
        ThumbnailOnly  = 0x08,
        InCacheOnly    = 0x10,
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        [PreserveSig] int GetHotkey(out short pwHotkey);
        [PreserveSig] int SetHotkey(short wHotkey);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    // ── P/Invoke ─────────────────────────────────────────────────────────────

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON      = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
