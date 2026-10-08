using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Chatterbox;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Chatterbox.Tests;

// The in-app updater against a fake GitHub: release parsing, the version
// gate, the SHA-256 gate on the download, the exe swap, and the cleanup
// the next start performs. No network, no real exe.
public class AppUpdaterTests : IDisposable
{
    private const string LatestUrl = "http://fake.invalid/latest";
    private const string ZipUrl = "http://fake.invalid/Chatterbox-99.1.2-win-x64.zip";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chatterbox-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _appDir;
    private readonly string _exe;
    private readonly string _marker;

    public AppUpdaterTests()
    {
        _appDir = Path.Combine(_dir, "app");
        Directory.CreateDirectory(_appDir);
        _exe = Path.Combine(_appDir, AppUpdater.ExeName);
        _marker = Path.Combine(_dir, "data", "update_applied.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // A single-file release zip holding "Chatterbox.exe" with these bytes.
    private static byte[] Zip(byte[] exeBytes)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(AppUpdater.ExeName);
            using var s = entry.Open();
            s.Write(exeBytes);
        }
        return ms.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ReleaseJson(string tag, string sha, long size, string assetName = "Chatterbox-99.1.2-win-x64.zip") =>
        new JObject
        {
            ["tag_name"] = tag,
            ["name"] = "Chatterbox " + tag.TrimStart('v'),
            ["html_url"] = "http://fake.invalid/releases/" + tag,
            ["body"] = "Live local captions for VRChat. Windows 10/11 x64. SHA-256: " + sha.ToUpperInvariant(),
            ["assets"] = new JArray(
                new JObject { ["name"] = "Source code (zip)", ["browser_download_url"] = "http://fake.invalid/src.zip", ["size"] = 5 },
                new JObject { ["name"] = assetName, ["browser_download_url"] = ZipUrl, ["size"] = size }),
        }.ToString();

    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> Routes = new();
        public readonly List<string> Requested = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            return Task.FromResult(Routes.TryGetValue(url, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private AppUpdater Updater(FakeHandler handler, string? url = LatestUrl) =>
        new(new HttpClient(handler)) { ReleaseUrl = url, ExePath = _exe, MarkerPath = _marker };

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Bytes(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    // ── parsing ──

    [Theory]
    [InlineData("v1.6.1", "1.6.1")]
    [InlineData("1.6.1", "1.6.1")]
    [InlineData("V2.0", "2.0.0")]
    [InlineData("v1.7.0-beta.1", "1.7.0")]
    public void ParseVersion_ReadsTags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), AppUpdater.ParseVersion(tag));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("release-2")]
    [InlineData("v1.x")]
    public void ParseVersion_RejectsNonVersions(string tag) =>
        Assert.Null(AppUpdater.ParseVersion(tag));

    [Fact]
    public void ParseRelease_PicksTheWinZipAndTheShaLine()
    {
        var sha = new string('a', 64);
        var info = AppUpdater.ParseRelease(ReleaseJson("v99.1.2", sha, 1234));
        Assert.Equal(new Version(99, 1, 2), info.Version);
        Assert.Equal("Chatterbox-99.1.2-win-x64.zip", info.AssetName);
        Assert.Equal(ZipUrl, info.AssetUrl);
        Assert.Equal(1234, info.AssetSize);
        Assert.Equal(sha, info.Sha256);          // lower-cased
        Assert.Equal("Chatterbox 99.1.2", info.Title);
    }

    [Fact]
    public void ParseRelease_RefusesNotesWithoutSha()
    {
        var json = new JObject
        {
            ["tag_name"] = "v99.0.0",
            ["body"] = "no checksum here",
            ["assets"] = new JArray(new JObject { ["name"] = "Chatterbox-99.0.0-win-x64.zip", ["browser_download_url"] = ZipUrl, ["size"] = 1 }),
        }.ToString();
        var ex = Assert.Throws<FormatException>(() => AppUpdater.ParseRelease(json));
        Assert.Contains("SHA-256", ex.Message);
    }

    [Fact]
    public void ParseRelease_RefusesReleasesWithoutTheWinZip()
    {
        var ex = Assert.Throws<FormatException>(() =>
            AppUpdater.ParseRelease(ReleaseJson("v99.0.0", new string('b', 64), 1, assetName: "Chatterbox-99.0.0-linux-x64")));
        Assert.Contains("win-x64", ex.Message);
    }

    // ── check ──

    [Fact]
    public async Task Check_ReportsANewerRelease()
    {
        var zip = Zip(new byte[] { 1, 2, 3 });
        var handler = new FakeHandler();
        handler.Routes[LatestUrl] = () => Text(ReleaseJson("v99.1.2", Sha(zip), zip.Length));
        var r = await Updater(handler).CheckAsync();
        Assert.Null(r.Error);
        Assert.NotNull(r.Update);
        Assert.Equal(new Version(99, 1, 2), r.Latest);
    }

    [Fact]
    public async Task Check_AnOlderOrEqualReleaseIsUpToDate()
    {
        var handler = new FakeHandler();
        handler.Routes[LatestUrl] = () => Text(ReleaseJson("v0.0.1", new string('c', 64), 1));
        var r = await Updater(handler).CheckAsync();
        Assert.Null(r.Error);
        Assert.Null(r.Update);
        Assert.Equal(new Version(0, 0, 1), r.Latest);
    }

    [Fact]
    public async Task Check_NoReleaseYet_IsAPlainMessageNotAnException()
    {
        var r = await Updater(new FakeHandler()).CheckAsync();   // 404
        Assert.Null(r.Update);
        Assert.Contains("no release", r.Error);
    }

    [Fact]
    public async Task Check_UnconfiguredBuild_NeverRequests()
    {
        var handler = new FakeHandler();
        var r = await Updater(handler, url: null).CheckAsync();
        Assert.Empty(handler.Requested);
        Assert.Contains("no update source", r.Error);
    }

    // ── download + stage ──

    [Fact]
    public async Task Download_VerifiesAndStagesTheExe()
    {
        var exeBytes = new byte[] { 0x4D, 0x5A, 9, 9, 9 };
        var zip = Zip(exeBytes);
        var handler = new FakeHandler();
        handler.Routes[LatestUrl] = () => Text(ReleaseJson("v99.1.2", Sha(zip), zip.Length));
        handler.Routes[ZipUrl] = () => Bytes(zip);
        var updater = Updater(handler);
        var check = await updater.CheckAsync();

        long lastGot = -1, lastTotal = -1;
        var (staged, error) = await updater.DownloadAsync(check.Update!, (got, total) => { lastGot = got; lastTotal = total; });
        Assert.Null(error);
        Assert.Equal(Path.Combine(_appDir, "Chatterbox.exe.new"), staged);
        Assert.Equal(exeBytes, File.ReadAllBytes(staged!));
        Assert.Equal(zip.Length, lastGot);
        Assert.Equal(zip.Length, lastTotal);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "data", "updates")));   // no .partial left
    }

    [Fact]
    public async Task Download_RefusesAHashMismatch()
    {
        var zip = Zip(new byte[] { 1 });
        var handler = new FakeHandler();
        handler.Routes[LatestUrl] = () => Text(ReleaseJson("v99.1.2", new string('d', 64), zip.Length));
        handler.Routes[ZipUrl] = () => Bytes(zip);
        var updater = Updater(handler);
        var check = await updater.CheckAsync();

        var (staged, error) = await updater.DownloadAsync(check.Update!, (_, _) => { });
        Assert.Null(staged);
        Assert.Contains("SHA-256", error);
        Assert.False(File.Exists(Path.Combine(_appDir, "Chatterbox.exe.new")));
    }

    // ── apply + the next start ──

    [Fact]
    public void Apply_SwapsTheExeAndTheNextStartCleansUp()
    {
        var oldBytes = new byte[] { 1, 1, 1 };
        var newBytes = new byte[] { 2, 2, 2, 2 };
        File.WriteAllBytes(_exe, oldBytes);
        var staged = _exe + ".new";
        File.WriteAllBytes(staged, newBytes);
        var updater = Updater(new FakeHandler());

        Assert.Null(updater.Apply(staged, new Version(99, 1, 2)));
        Assert.Equal(newBytes, File.ReadAllBytes(_exe));
        Assert.Equal(oldBytes, File.ReadAllBytes(_exe + ".old"));
        Assert.False(File.Exists(staged));
        Assert.True(File.Exists(_marker));

        // The "next start" (same test build, so the version doesn't match 99.1.2).
        var note = updater.FinishPendingUpdate();
        Assert.Contains("99.1.2", note);
        Assert.False(File.Exists(_exe + ".old"));
        Assert.False(File.Exists(_marker));
        Assert.Null(updater.FinishPendingUpdate());   // nothing twice
    }

    [Fact]
    public void Apply_WithoutAStagedFile_LeavesTheExeAlone()
    {
        File.WriteAllBytes(_exe, new byte[] { 7 });
        var error = Updater(new FakeHandler()).Apply(_exe + ".new", new Version(99, 0, 0));
        Assert.NotNull(error);
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(_exe));
        Assert.False(File.Exists(_marker));
    }
}
