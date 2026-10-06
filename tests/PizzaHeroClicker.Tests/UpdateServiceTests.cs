using PizzaHeroClicker.Services;
using Xunit;

namespace PizzaHeroClicker.Tests;

public class UpdateServiceTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Release(string tag = "v1.2.0", string assetName = "PizzaHeroClicker-Setup-1.2.0.exe",
        string? url = null, string? digest = "sha256:" + Sha, long size = 61_000_000, string extra = "")
    {
        url ??= $"https://github.com/PizzaHeroGaming/PizzaHeroClicker/releases/download/{tag}/{assetName}";
        string digestJson = digest is null ? "null" : $"\"{digest}\"";
        return $$"""
            {
              "tag_name": "{{tag}}",
              "html_url": "https://github.com/PizzaHeroGaming/PizzaHeroClicker/releases/tag/{{tag}}",
              "body": "  New: things.\n",
              {{extra}}
              "assets": [
                { "name": "notes.txt", "browser_download_url": "https://github.com/PizzaHeroGaming/PizzaHeroClicker/releases/download/{{tag}}/notes.txt", "size": 10, "digest": "sha256:{{Sha}}" },
                { "name": "{{assetName}}", "browser_download_url": "{{url}}", "size": {{size}}, "digest": {{digestJson}} }
              ]
            }
            """;
    }

    [Fact]
    public void ReadsAReleaseWithItsInstaller()
    {
        var info = UpdateService.ParseRelease(Release());
        Assert.NotNull(info);
        Assert.Equal(new Version(1, 2, 0), info.Version);
        Assert.Equal("New: things.", info.Notes);
        Assert.Equal("PizzaHeroClicker-Setup-1.2.0.exe", info.InstallerName);
        Assert.Equal(61_000_000, info.InstallerSize);
        Assert.Equal(Sha, info.Sha256);
        Assert.StartsWith("https://github.com/PizzaHeroGaming/PizzaHeroClicker/releases/download/", info.InstallerUrl);
    }

    [Theory]
    [InlineData("https://example.com/PizzaHeroGaming/PizzaHeroClicker/releases/download/v1.2.0/PizzaHeroClicker-Setup-1.2.0.exe")] // another site
    [InlineData("http://github.com/PizzaHeroGaming/PizzaHeroClicker/releases/download/v1.2.0/PizzaHeroClicker-Setup-1.2.0.exe")]  // not https
    [InlineData("https://github.com/SomeoneElse/PizzaHeroClicker/releases/download/v1.2.0/PizzaHeroClicker-Setup-1.2.0.exe")]     // another account
    [InlineData("https://github.com.evil.example/PizzaHeroGaming/PizzaHeroClicker/releases/download/v1.2.0/x.exe")]
    public void AnInstallerFromAnywhereElseIsNotOffered(string url)
    {
        var info = UpdateService.ParseRelease(Release(url: url));
        Assert.NotNull(info);          // the release is still reported, so the user can be sent to its page...
        Assert.Null(info.InstallerUrl); // ...but nothing will be downloaded and run
    }

    [Theory]
    [InlineData(null)]
    [InlineData("md5:0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:tooshort")]
    public void AnInstallerWithoutAChecksumIsNotOffered(string? digest) =>
        Assert.Null(UpdateService.ParseRelease(Release(digest: digest))!.InstallerUrl);

    [Fact]
    public void OnlyTheSetupExeCountsAsAnInstaller() =>
        Assert.Null(UpdateService.ParseRelease(Release(assetName: "SomethingElse.exe"))!.InstallerUrl);

    [Theory]
    [InlineData("\"draft\": true,")]
    [InlineData("\"prerelease\": true,")]
    public void DraftsAndPrereleasesAreIgnored(string extra) => Assert.Null(UpdateService.ParseRelease(Release(extra: extra)));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\": \"nightly\"}")]
    public void RubbishIsNotARelease(string json) => Assert.Null(UpdateService.ParseRelease(json));

    [Theory]
    [InlineData("v1.2.0", 1, 2, 0)]
    [InlineData("V2.0", 2, 0, 0)]
    [InlineData("1.10.3.0", 1, 10, 3)]
    public void ParsesVersionTags(string tag, int major, int minor, int build)
    {
        Assert.True(UpdateService.TryParseVersion(tag, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData("1.2.0-beta")]
    public void RejectsTagsThatAreNotVersions(string tag) => Assert.False(UpdateService.TryParseVersion(tag, out _));

    [Fact]
    public void NewerMeansAHigherNumberNotADifferentOne()
    {
        var current = new Version(1, 1, 0);
        Assert.True(UpdateService.IsNewer(new Version(1, 1, 1), current));
        Assert.True(UpdateService.IsNewer(new Version(1, 10, 0), new Version(1, 9, 0)));
        Assert.False(UpdateService.IsNewer(new Version(1, 1, 0), current));
        Assert.False(UpdateService.IsNewer(new Version(1, 0, 9), current));
        Assert.False(UpdateService.IsNewer(new Version(1, 1), new Version(1, 1, 0, 0))); // 1.1 and 1.1.0.0 are the same version
    }
}
