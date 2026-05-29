using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Toolbar.Services;

public sealed class UpdateInfo
{
    public required Version Version { get; init; }
    public required string Tag { get; init; }
    public required string DownloadUrl { get; init; }
    public string? Sha256 { get; init; }
    public long? Size { get; init; }
}

public static class UpdateService
{
    private const string ApiUrl =
        "https://api.github.com/repos/StefanoPelletti/Toolbar/releases/latest";

    private const string AssetName = "Toolbar.exe";
    private const string OldSuffix = ".old";
    private const string NewSuffix = ".new";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub rejects API requests without a User-Agent.
        c.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Toolbar", CurrentVersion().ToString()));
        c.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static Version CurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        return Normalize(v);
    }

    /// <summary>
    /// Returns the latest release if it is strictly newer than the running version, else null.
    /// </summary>
    public static async Task<UpdateInfo?> CheckLatestAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(ApiUrl, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GhRelease>(stream, JsonOpts, ct)
            .ConfigureAwait(false);

        if (release is null || string.IsNullOrEmpty(release.TagName)) return null;

        var latest = Normalize(ParseTag(release.TagName));
        if (latest <= CurrentVersion()) return null;

        var asset = release.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null || string.IsNullOrEmpty(asset.BrowserDownloadUrl)) return null;

        return new UpdateInfo
        {
            Version = latest,
            Tag = release.TagName,
            DownloadUrl = asset.BrowserDownloadUrl,
            Sha256 = ExtractSha256(asset.Digest),
            Size = asset.Size,
        };
    }

    /// <summary>
    /// Downloads the new exe alongside the running one, verifies SHA-256 (when provided),
    /// performs the rename-self swap, launches the new process, and returns. The caller is
    /// expected to shut down the application immediately after.
    /// </summary>
    public static async Task DownloadAndApplyAsync(
        UpdateInfo info,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine current exe path.");

        var dir = Path.GetDirectoryName(currentExe)
            ?? throw new InvalidOperationException("Cannot determine exe directory.");

        var newPath = currentExe + NewSuffix;
        var oldPath = currentExe + OldSuffix;

        // Probe write access to the exe folder before we burn 75 MB of bandwidth.
        EnsureFolderWritable(dir);

        // Stale leftovers from a previous failed attempt.
        TryDelete(newPath);

        await DownloadToFileAsync(info.DownloadUrl, newPath, info.Size, progress, ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrEmpty(info.Sha256))
            await VerifySha256Async(newPath, info.Sha256!, ct).ConfigureAwait(false);

        // Windows lets you rename a running exe but not delete or overwrite it,
        // so we move the live one aside, then drop the new one into its place.
        TryDelete(oldPath);
        File.Move(currentExe, oldPath);
        try
        {
            File.Move(newPath, currentExe);
        }
        catch
        {
            // Roll back so the user is not left without an exe.
            try { File.Move(oldPath, currentExe); } catch { /* best effort */ }
            throw;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = currentExe,
            WorkingDirectory = dir,
            // Tells the new instance this is an update relaunch, so it waits for the
            // outgoing process to release the single-instance mutex instead of
            // exiting immediately on contention.
            Arguments = "--updated",
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Deletes the renamed-aside previous exe left behind by a prior update. Best-effort —
    /// if the previous process has not fully released the file yet, the next startup will
    /// pick it up.
    /// </summary>
    public static void CleanupLeftover()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            TryDelete(exe + OldSuffix);
        }
        catch { /* best effort */ }
    }

    private static async Task DownloadToFileAsync(
        string url, string destPath, long? expectedSize,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? expectedSize ?? -1L;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        double lastReported = -1;

        while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;

            if (progress is not null && total > 0)
            {
                var p = (double)read / total;
                // Avoid spamming the UI thread with sub-percent updates.
                if (p - lastReported >= 0.01 || p >= 1.0)
                {
                    progress.Report(p);
                    lastReported = p;
                }
            }
        }

        if (progress is not null) progress.Report(1.0);
    }

    private static async Task VerifySha256Async(string path, string expectedHex, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash);

        if (!string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            throw new InvalidDataException("Downloaded file failed SHA-256 verification.");
        }
    }

    private static void EnsureFolderWritable(string dir)
    {
        var probe = Path.Combine(dir, ".toolbar-update-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(probe, []);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                "Cannot update: the app folder is read-only. " +
                "Move Toolbar.exe to a writable location (e.g. your user folder) and try again.",
                ex);
        }
        finally
        {
            TryDelete(probe);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static Version ParseTag(string tag)
    {
        var s = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(s, out var v) ? v : new Version(0, 0, 0, 0);
    }

    private static Version Normalize(Version v) =>
        new(v.Major,
            v.Minor < 0 ? 0 : v.Minor,
            v.Build < 0 ? 0 : v.Build,
            v.Revision < 0 ? 0 : v.Revision);

    private static string? ExtractSha256(string? digest)
    {
        if (string.IsNullOrEmpty(digest)) return null;
        const string prefix = "sha256:";
        return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..]
            : null;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class GhRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("assets")] public List<GhAsset>? Assets { get; set; }
    }

    private sealed class GhAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
    }
}
