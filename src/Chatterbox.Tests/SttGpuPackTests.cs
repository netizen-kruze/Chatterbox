using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// The GPU pack: pinned sources, which parts a download fetches, and how a
// system-wide CUDA runtime is found.
public class SttGpuPackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chatterbox-gpupack-" + Guid.NewGuid().ToString("N"));

    public SttGpuPackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void EveryPartIsPinnedToItsPublishersHostWithHashes()
    {
        Assert.Equal(3, SttGpuPack.Parts.Length);
        foreach (var part in SttGpuPack.Parts)
        {
            Assert.StartsWith("https://", part.Url);
            Assert.Contains(new Uri(part.Url).Host, new[] { "api.nuget.org", "developer.download.nvidia.com" });
            Assert.True(part.Size > 0);
            Assert.Matches("^[0-9a-f]{64}$", part.Sha256);
            Assert.NotEmpty(part.Files);
            foreach (var f in part.Files)
            {
                Assert.True(f.Size > 0);
                Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
                Assert.DoesNotContain('/', f.Name);
                Assert.Contains('/', f.Entry);
            }
        }
        var names = SttGpuPack.Parts.SelectMany(p => p.Files).Select(f => f.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void TheRuntimePartsProvideExactlyTheLibrariesThePreloadExpects()
    {
        var runtimeLibs = SttGpuPack.Parts.Where(p => p.Runtime).SelectMany(p => p.Files)
            .Select(f => f.Name).Where(n => n != SttGpuPack.LicenseFileName).OrderBy(n => n).ToArray();
        Assert.Equal(SttGpuPack.RuntimeLoadOrder.OrderBy(n => n).ToArray(), runtimeLibs);
        // cudart first (Whisper.net asks for it by name), cublasLt before cublas (its dependency).
        Assert.Equal("cudart64_13.dll", SttGpuPack.RuntimeLoadOrder[0]);
        Assert.True(Array.IndexOf(SttGpuPack.RuntimeLoadOrder, "cublasLt64_13.dll") <
                    Array.IndexOf(SttGpuPack.RuntimeLoadOrder, "cublas64_13.dll"));
        Assert.Contains(SttGpuPack.Parts.Where(p => p.Runtime).SelectMany(p => p.Files), f => f.Name == SttGpuPack.LicenseFileName);
    }

    [Theory]
    [InlineData(false, false, false, "whisper-cuda,cuda-runtime,cublas")]
    [InlineData(true, false, false, "cuda-runtime,cublas")]   // an install from before 1.7.3 on a PC without the CUDA Toolkit
    [InlineData(true, false, true, "")]
    [InlineData(false, false, true, "whisper-cuda")]
    [InlineData(false, true, false, "whisper-cuda")]
    [InlineData(true, true, false, "")]
    public void ADownloadFetchesWhatIsMissingUnlessTheSystemHasTheRuntime(bool whisper, bool bundled, bool system, string expected)
    {
        var plan = SttGpuPack.Plan(p => p.Runtime ? bundled : whisper, system);
        Assert.Equal(expected, string.Join(",", plan.Select(p => p.Id)));
    }

    [Fact]
    public void ALibraryIsFoundInTheFirstSearchFolderThatHasIt()
    {
        var a = Directory.CreateDirectory(Path.Combine(_dir, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(_dir, "b")).FullName;
        File.WriteAllText(Path.Combine(b, "cudart64_13.dll"), "x");
        var dirs = new[] { Path.Combine(_dir, "missing"), "::not a path::", a, b };
        Assert.Equal(Path.Combine(b, "cudart64_13.dll"), SttGpuPack.FindIn(dirs, "cudart64_13.dll"));
        Assert.Null(SttGpuPack.FindIn(dirs, "cublas64_13.dll"));
        Assert.Null(SttGpuPack.FindIn(Array.Empty<string>(), "cudart64_13.dll"));
    }

    [Fact]
    public void TheSystemSearchStartsInTheSystemFolderAndWalksPath()
    {
        var dirs = SttGpuPack.SystemSearchDirs().ToList();
        Assert.Equal(Environment.SystemDirectory, dirs[0]);
        Assert.DoesNotContain(dirs, d => d.Length == 0 || d.Contains('"'));
    }
}
