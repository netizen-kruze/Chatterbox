using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// The GPU pack: pinned sources, which build a PC gets, which parts a
// download fetches, and how a system-wide CUDA runtime is found.
public class SttGpuPackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chatterbox-gpupack-" + Guid.NewGuid().ToString("N"));

    public SttGpuPackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void EveryPartOfEveryBuildIsPinnedToItsPublishersHostWithHashes()
    {
        Assert.Equal(2, SttGpuPack.Variants.Length);
        foreach (var v in SttGpuPack.Variants)
        {
            Assert.Equal(3, v.Parts.Length);
            Assert.False(v.Whisper.Runtime);
            foreach (var part in v.Parts)
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
            var names = v.Parts.SelectMany(p => p.Files).Select(f => f.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
        }
        // The two builds live apart, in the folders Whisper.net probes, and share no download.
        Assert.Equal("cuda", SttGpuPack.Cuda13.Folder);
        Assert.Equal("cuda12", SttGpuPack.Cuda12.Folder);
        var ids = SttGpuPack.Variants.SelectMany(v => v.Parts).Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        var urls = SttGpuPack.Variants.SelectMany(v => v.Parts).Select(p => p.Url).ToList();
        Assert.Equal(urls.Count, urls.Distinct().Count());
    }

    [Theory]
    [InlineData(0, "13")]
    [InlineData(1, "12")]
    public void TheRuntimePartsProvideExactlyTheLibrariesThePreloadExpects(int index, string major)
    {
        var v = SttGpuPack.Variants[index];
        var runtimeLibs = v.Parts.Where(p => p.Runtime).SelectMany(p => p.Files)
            .Select(f => f.Name).Where(n => n != SttGpuPack.LicenseFileName).OrderBy(n => n).ToArray();
        Assert.Equal(v.RuntimeLoadOrder.OrderBy(n => n).ToArray(), runtimeLibs);
        // cudart first (Whisper.net asks for it by name), cublasLt before cublas (its dependency).
        Assert.Equal($"cudart64_{major}.dll", v.RuntimeLoadOrder[0]);
        Assert.True(Array.IndexOf(v.RuntimeLoadOrder, $"cublasLt64_{major}.dll") <
                    Array.IndexOf(v.RuntimeLoadOrder, $"cublas64_{major}.dll"));
        Assert.Contains(v.Parts.Where(p => p.Runtime).SelectMany(p => p.Files), f => f.Name == SttGpuPack.LicenseFileName);
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
        var plan = SttGpuPack.Plan(SttGpuPack.Cuda13, p => p.Runtime ? bundled : whisper, system);
        Assert.Equal(expected, string.Join(",", plan.Select(p => p.Id)));
    }

    [Fact]
    public void ThePcGetsTheBuildItsCardAndDriverSupport()
    {
        static bool none(SttGpuPack.Variant _) => false;
        Assert.Same(SttGpuPack.Cuda13, SttGpuPack.Choose(CudaVariant.Cuda13, _ => true, none));
        Assert.Same(SttGpuPack.Cuda12, SttGpuPack.Choose(CudaVariant.Cuda12, _ => true, none));
        // Nothing usable: the pack still has a shape (it is simply not offered).
        Assert.Same(SttGpuPack.Cuda13, SttGpuPack.Choose(CudaVariant.None, _ => false, none));
    }

    [Fact]
    public void ACompleteUsableBuildIsKeptWhenThePreferredOneIsNotInstalled()
    {
        // CUDA 12 build installed under an old driver; the driver was updated since.
        Assert.Same(SttGpuPack.Cuda12, SttGpuPack.Choose(CudaVariant.Cuda13, _ => true, v => v == SttGpuPack.Cuda12));
        // Both complete: the preferred one.
        Assert.Same(SttGpuPack.Cuda13, SttGpuPack.Choose(CudaVariant.Cuda13, _ => true, _ => true));
        // A CUDA 13 build left on a PC whose card cannot run it is not kept.
        Assert.Same(SttGpuPack.Cuda12, SttGpuPack.Choose(CudaVariant.Cuda12, k => k == CudaVariant.Cuda12, v => v == SttGpuPack.Cuda13));
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

    // ── which build: the driver's answers ──────────────────────────

    private static CudaProbe.Facts Pc(int api, double? cc, string? name = "Test card") => new(true, api, cc, name, null);

    [Theory]
    [InlineData(13000, 8.9, CudaVariant.Cuda13)]   // RTX 40 series, current driver
    [InlineData(13000, 7.5, CudaVariant.Cuda13)]   // GTX 16 / RTX 20 series: the oldest CUDA 13 runs on
    [InlineData(13000, 6.1, CudaVariant.Cuda12)]   // GTX 10 series on driver 580: CUDA 13 dropped the card
    [InlineData(13000, 5.2, CudaVariant.Cuda12)]   // GTX 900 series
    [InlineData(12090, 8.6, CudaVariant.Cuda12)]   // RTX 30 series on a driver from before 580
    [InlineData(12000, 6.1, CudaVariant.Cuda12)]
    [InlineData(11080, 8.6, CudaVariant.None)]     // a driver from before CUDA 12
    [InlineData(13000, 3.5, CudaVariant.None)]     // a card from before Maxwell
    public void TheBuildFollowsTheCardAndTheDriver(int api, double cc, CudaVariant expected)
    {
        var (variant, verdict) = CudaProbe.Judge(Pc(api, cc));
        Assert.Equal(expected, variant);
        Assert.Contains("Test card", verdict);
    }

    [Fact]
    public void TheVerdictSaysWhatToDoAboutIt()
    {
        Assert.Contains("predates CUDA 13", CudaProbe.Judge(Pc(13000, 6.1)).Verdict);
        Assert.Contains("driver 580 or newer", CudaProbe.Judge(Pc(12090, 8.6)).Verdict);
        Assert.Contains("update it", CudaProbe.Judge(Pc(11080, 8.6)).Verdict);
        Assert.Contains("too old for GPU acceleration", CudaProbe.Judge(Pc(13000, 3.5)).Verdict);
        Assert.Equal("no NVIDIA driver", CudaProbe.Judge(new CudaProbe.Facts(false, 0, null, null, null)).Verdict);
    }

    [Fact]
    public void ADriverThatTellsNothingIsTreatedAsBefore()
    {
        var silent = new CudaProbe.Facts(true, 0, null, null, "the driver did not answer in time");
        var (variant, verdict) = CudaProbe.Judge(silent);
        Assert.Equal(CudaVariant.Cuda13, variant);
        Assert.Contains("did not answer in time", verdict);
        Assert.True(CudaProbe.Usable(silent, CudaVariant.Cuda13));
        Assert.False(CudaProbe.Usable(silent, CudaVariant.Cuda12));
    }

    [Fact]
    public void TheCuda12BuildStaysUsableOnANewCardWithANewDriver()
    {
        Assert.True(CudaProbe.Usable(Pc(13000, 8.9), CudaVariant.Cuda12));
        Assert.True(CudaProbe.Usable(Pc(13000, 8.9), CudaVariant.Cuda13));
        Assert.False(CudaProbe.Usable(Pc(13000, 6.1), CudaVariant.Cuda13));
        Assert.False(CudaProbe.Usable(Pc(13000, 8.9), CudaVariant.None));
        Assert.False(CudaProbe.Usable(new CudaProbe.Facts(false, 0, null, null, null), CudaVariant.Cuda13));
    }

    // ── Microsoft's Visual C++ runtime ─────────────────────────────

    [Fact]
    public void TheVisualCRuntimeIsMissingUntilEveryFileIsSomewhereWindowsLooks()
    {
        var a = Directory.CreateDirectory(Path.Combine(_dir, "sys")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(_dir, "app")).FullName;
        Assert.Equal(VcRuntime.Needed, VcRuntime.Missing(new[] { a, b }));
        File.WriteAllText(Path.Combine(a, "vcruntime140.dll"), "x");
        File.WriteAllText(Path.Combine(a, "vcruntime140_1.dll"), "x");
        File.WriteAllText(Path.Combine(a, "msvcp140.dll"), "x");
        Assert.Equal(new[] { "vcomp140.dll" }, VcRuntime.Missing(new[] { a, b }));
        File.WriteAllText(Path.Combine(b, "vcomp140.dll"), "x");
        Assert.Empty(VcRuntime.Missing(new[] { a, b }));
    }

    [Theory]
    [InlineData(0, 0, true, "restart Chatterbox")]
    [InlineData(1638, 0, true, "restart Chatterbox")]
    [InlineData(3010, 0, true, "restart Windows")]
    [InlineData(1602, 4, false, "cancelled")]
    [InlineData(0, 2, false, "still missing")]
    [InlineData(1603, 4, false, "code 1603")]
    public void TheInstallersResultIsReportedAsItIs(int exitCode, int stillMissing, bool ok, string says)
    {
        var (isOk, message) = VcRuntime.Outcome(exitCode, stillMissing);
        Assert.Equal(ok, isOk);
        Assert.Contains(says, message);
    }

    [Theory]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", true)]
    [InlineData("CN=Microsoft Corporation Ltd, O=Microsoft Corporation Ltd, C=US", false)]
    [InlineData("CN=O=Microsoft Corporation Fan Club, O=Someone Else, C=US", false)]
    [InlineData("", false)]
    public void OnlyMicrosoftsOwnSignatureIsAccepted(string subject, bool accepted) =>
        Assert.Equal(accepted, VcRuntime.SignedByMicrosoft(subject));
}
