using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace DataPortStudio.Services;

/// <summary>A newer release found on GitHub, with the installer to download.</summary>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string Notes,
    string PageUrl,
    string AssetName,
    string DownloadUrl,
    long Size,
    string? Sha256);

/// <summary>
/// Checks GitHub for the latest DataPortStudio release, downloads its Setup.exe and runs it.
/// </summary>
public static class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/robertorenz/DataPortStudio/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DataPortStudio/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>The running version (major.minor.build).</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>Returns the latest release if it is newer than this build and ships a Setup.exe; otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        using var response = await Http.GetAsync(LatestReleaseApi, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
        var root = doc.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var latest) || latest <= CurrentVersion) return null;

        if (!root.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase)) continue;

            string? sha = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d
                && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = d["sha256:".Length..];

            return new UpdateInfo(
                latest,
                tag,
                root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "",
                name,
                asset.GetProperty("browser_download_url").GetString() ?? "",
                asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                sha);
        }
        return null;
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash >= 0) text = text[..dash];
        if (Version.TryParse(text, out var v))
        {
            version = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>
    /// Downloads the installer to %TEMP%\DataPortStudio\Updates, reporting progress 0..1,
    /// and verifies its SHA-256 when GitHub publishes one. Returns the local path.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DataPortStudio", "Updates");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(update.AssetName));
        var partial = path + ".part";

        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.Size;

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (total > 0) progress?.Report((double)received / total);
            }

            if (update.Sha256 is { Length: > 0 } expected)
            {
                var actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    target.Close();
                    File.Delete(partial);
                    throw new InvalidDataException(LocalizationManager.Instance["Update_ChecksumFailed"]);
                }
            }
        }

        File.Move(partial, path, overwrite: true);
        progress?.Report(1);
        return path;
    }

    /// <summary>
    /// Starts the installer silently (it shows its own progress and a UAC prompt) and asks it to
    /// relaunch DataPortStudio when done. The caller must shut the app down right after.
    /// </summary>
    public static void LaunchInstaller(string installerPath)
    {
        var args = "/SILENT /SP- /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /relaunch=1";

        // Keep a per-user install per-user, so updating doesn't create a second copy in Program Files.
        var exe = Environment.ProcessPath ?? "";
        var userPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        args += exe.StartsWith(userPrograms, StringComparison.OrdinalIgnoreCase) ? " /CURRENTUSER" : " /ALLUSERS";

        Process.Start(new ProcessStartInfo(installerPath, args) { UseShellExecute = true });
    }
}
