using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Toolbar.Services;

// Two-level icon cache: in-memory (ConcurrentDictionary) and on-disk PNG files
// under %AppData%\Toolbar\iconcache. The key encodes the absolute path plus
// last-write-time so an updated program's icon is re-extracted automatically
// without an explicit invalidation step.
//
// TryGet and Store must be called on the UI thread (BitmapImage /
// PngBitmapEncoder are DispatcherObjects). The disk write is fire-and-forget
// on a ThreadPool thread so the UI is never blocked by file I/O.
internal static class IconCache
{
    private static readonly string CacheDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Toolbar", "iconcache");

    private static readonly ConcurrentDictionary<string, ImageSource?> _mem = new();

    internal static string CacheKey(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith("::"))
            return path.ToUpperInvariant();
        try
        {
            if (File.Exists(path))
                return path.ToUpperInvariant() + "|" + File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch { }
        return path.ToUpperInvariant();
    }

    internal static ImageSource? TryGet(string key)
    {
        if (_mem.TryGetValue(key, out var cached)) return cached;

        var disk = DiskPath(key);
        if (!File.Exists(disk)) return null;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource   = new Uri(disk, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            _mem[key] = bmp;
            return bmp;
        }
        catch { return null; }
    }

    internal static void Store(string key, ImageSource? icon)
    {
        if (icon is not BitmapSource bmp) return;
        _mem[key] = bmp;

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            var ms = new MemoryStream();
            encoder.Save(ms);
            var bytes = ms.ToArray();

            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    File.WriteAllBytes(DiskPath(key), bytes);
                }
                catch { }
            });
        }
        catch { }
    }

    private static string DiskPath(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return System.IO.Path.Combine(CacheDir, Convert.ToHexString(hash)[..24] + ".png");
    }
}
