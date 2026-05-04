using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Toolbar.Services;

public static class IconExtractor
{
    public static ImageSource? FromPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return FromDirectory(path);

            var resolved = ResolvePath(path);
            if (resolved is null) return null;

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(resolved);
            if (icon is null) return null;

            return Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        catch
        {
            return null;
        }
    }

    public static ImageSource? FromFile(string iconPath)
    {
        try
        {
            var ext = System.IO.Path.GetExtension(iconPath).ToLowerInvariant();

            if (ext == ".ico")
            {
                using var icon = new System.Drawing.Icon(iconPath, 48, 48);
                return Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }

            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(iconPath);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }

    // Uses SHGetFileInfo to obtain the shell icon for a folder path.
    private static ImageSource? FromDirectory(string path)
    {
        var shfi = new SHFILEINFO();
        var result = SHGetFileInfo(path, 0, ref shfi,
            (uint)Marshal.SizeOf(shfi),
            SHGFI_ICON | SHGFI_LARGEICON);

        if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
            return null;

        try
        {
            return Imaging.CreateBitmapSourceFromHIcon(
                shfi.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DestroyIcon(shfi.hIcon);
        }
    }

    private static string? ResolvePath(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".lnk") return ResolveLnk(path);
        return File.Exists(path) ? path : null;
    }

    private static string? ResolveLnk(string lnkPath)
    {
        try
        {
            Type? shellLinkType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellLinkType is null) return null;

            dynamic? shell = Activator.CreateInstance(shellLinkType);
            if (shell is null) return null;

            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string target = shortcut.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    // ── P/Invoke ─────────────────────────────────────────────────────────────

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

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
