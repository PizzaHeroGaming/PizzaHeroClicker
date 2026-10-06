using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace PizzaHeroClicker.Services;

/// <summary>A published release; see <see cref="UpdateService"/>.</summary>
/// <param name="InstallerUrl">Null when the release has no installer this app is willing to run by itself.</param>
public sealed record UpdateInfo(Version Version, string Notes, string PageUrl, string? InstallerUrl, string? InstallerName, long InstallerSize, string? Sha256);

/// <summary>
/// Looks for a newer version among the project's GitHub releases and downloads its installer.
/// Nothing is ever installed from here: the caller asks the user first and then runs the file.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string Owner = "PizzaHeroGaming";
    public const string Repo = "PizzaHeroClicker";
    public static string ReleasesPage => $"https://github.com/{Owner}/{Repo}/releases";
    private static string LatestApi => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
    private const string InstallerPrefix = "PizzaHeroClicker-Setup-";
    private const string DigestPrefix = "sha256:";
    private const long MaxInstallerBytes = 500L * 1024 * 1024;

    private readonly HttpClient _http;

    public UpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) }; // long enough for the installer on a slow line
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"PizzaHeroClicker/{CurrentVersion}"); // GitHub rejects requests without one
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version CurrentVersion { get; } = Trim(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>The latest published release, or null if the project has none yet. Throws if GitHub cannot be reached.</summary>
    public async Task<UpdateInfo?> GetLatestAsync(CancellationToken cancel = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.GetAsync(LatestApi, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
    }

    /// <summary>Reads GitHub's description of one release. Null if it is not a usable, numbered release.</summary>
    public static UpdateInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Flag(root, "draft") || Flag(root, "prerelease")) return null;
            if (!TryParseVersion(Text(root, "tag_name"), out var version)) return null;

            string? url = null, name = null, sha = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string assetName = Text(asset, "name"), assetUrl = Text(asset, "browser_download_url"), digest = Text(asset, "digest");
                    if (!assetName.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase) || !assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    // Only an installer from this project's own releases, with a checksum to hold it to, is run from here.
                    if (!IsOwnDownload(assetUrl)) continue;
                    if (!digest.StartsWith(DigestPrefix, StringComparison.OrdinalIgnoreCase) || digest.Length != DigestPrefix.Length + 64) continue;
                    long assetSize = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
                    if (assetSize <= 0 || assetSize > MaxInstallerBytes) continue;
                    (url, name, size, sha) = (assetUrl, Path.GetFileName(assetName), assetSize, digest[DigestPrefix.Length..].ToLowerInvariant());
                    break;
                }
            }

            string page = Text(root, "html_url");
            if (!page.StartsWith($"https://github.com/{Owner}/{Repo}/", StringComparison.OrdinalIgnoreCase)) page = ReleasesPage;
            return new UpdateInfo(version, Text(root, "body").Trim(), page, url, name, size, sha);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"v1.2.0", "1.2" and "1.2.0.0" all name versions; anything else does not.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        text = (text ?? "").Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];
        if (!Version.TryParse(text, out var parsed)) return false;
        version = Trim(parsed);
        return true;
    }

    public static bool IsNewer(Version candidate, Version current) => Trim(candidate) > Trim(current);

    /// <summary>
    /// Downloads the installer to a temporary folder and checks it against the size and SHA-256
    /// GitHub lists for it. Returns the path; throws (and deletes the file) if anything is off.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        if (update.InstallerUrl is null || update.InstallerName is null || update.Sha256 is null)
            throw new InvalidOperationException("This release has no installer that can be checked.");

        string folder = Path.Combine(Path.GetTempPath(), "PizzaHeroClicker-update");
        Directory.CreateDirectory(folder);
        foreach (string old in Directory.EnumerateFiles(folder)) TryDelete(old);
        string path = Path.Combine(folder, update.InstallerName);
        try
        {
            using (var response = await _http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[128 * 1024];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > update.InstallerSize) throw new InvalidDataException("The download is larger than the release says it should be.");
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                    progress?.Report((double)total / update.InstallerSize);
                }
                if (total != update.InstallerSize) throw new InvalidDataException("The download is incomplete.");
            }

            await using (var check = File.OpenRead(path))
            {
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(check, cancel).ConfigureAwait(false)).ToLowerInvariant();
                if (actual != update.Sha256) throw new InvalidDataException("The download does not match the checksum published with the release.");
            }
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind in the temp folder; the next update clears it.
        }
    }

    private static bool IsOwnDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith($"/{Owner}/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase);

    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public void Dispose() => _http.Dispose();
}
