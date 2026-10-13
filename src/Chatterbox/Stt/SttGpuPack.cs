using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Chatterbox.Stt;

// Optional CUDA acceleration for the Whisper engine, downloaded on demand so
// release packages stay CPU-only. Three parts make it up, each a zip fetched
// from its publisher, verified per file and placed in the app's own
// runtimes\cuda\win-x64\ folder — exactly where Whisper.net's loader probes
// first, so no loader configuration is needed:
//
//  1. whisper.cpp's CUDA build, from the official
//     Whisper.net.Runtime.Cuda.Windows package on NuGet (a nupkg is a zip).
//  2. The CUDA 13 runtime (cudart64_13.dll) and
//  3. cuBLAS (cublas64_13.dll + cublasLt64_13.dll), from NVIDIA's own
//     redistributable archives. The graphics driver does not ship these and
//     nobody has them unless they installed the CUDA Toolkit; up to 1.7.2
//     the pack was only part 1, so on a PC without the toolkit it showed
//     "installed" and Whisper stayed on the CPU, restart after restart. A
//     system-wide CUDA 13 runtime is used instead when one is installed.
//
// Whisper.net decides whether CUDA is usable by asking for "cudart64_13" by
// bare name, which Windows only finds in the system folders and on PATH — or
// among modules already loaded under that name. PreloadRuntime therefore
// loads the three runtime libraries by full path (dependencies first) before
// any Whisper.net native call; ggml-cuda-whisper.dll then resolves the same
// names to the copies already in the process.
//
// The driver's own nvcuda.dll cannot be downloaded — it comes with the
// NVIDIA graphics driver, and CUDA 13 needs a current one.
//
// Takes effect on the next app start (natives load once per process). App
// updates swap only the exe, so an installed pack survives them; deleting
// the whole app folder removes it and the pack shows as downloadable again.
public static class SttGpuPack
{
    public const string Id = "cuda-gpu-pack";
    public const string DisplayName = "GPU acceleration for Whisper (CUDA)";
    public const string License = "MIT (whisper.cpp) + NVIDIA EULA (CUDA runtime)";
    public const string Attribution = "whisper.cpp CUDA build packaged by Whisper.net; CUDA runtime and cuBLAS by NVIDIA";

    // One file inside a part's archive: where it is in the zip, what it is
    // called once installed, and what it must hash to.
    public sealed record PackFile(string Entry, string Name, long Size, string Sha256);

    // One downloadable archive. Runtime parts are skipped when the system
    // already provides a CUDA 13 runtime.
    public sealed record PackPart(string Id, string Label, string Url, long Size, string Sha256, PackFile[] Files, bool Runtime)
    {
        public long ExtractedBytes => Files.Sum(f => f.Size);
    }

    private static PackFile F(string prefix, string name, long size, string sha) => new(prefix + name, name, size, sha);

    public const string LicenseFileName = "NVIDIA-CUDA-LICENSE.txt";

    public static readonly PackPart WhisperCuda = new(
        "whisper-cuda", "whisper.cpp CUDA build",
        "https://api.nuget.org/v3-flatcontainer/whisper.net.runtime.cuda.windows/1.9.1/whisper.net.runtime.cuda.windows.1.9.1.nupkg",
        142_586_522, "3b765c9690c114825af1e9fd48e8f2e6f8b798c314d29e27e09640da479dfceb",
        new[]
        {
            F("build/win-x64/", "ggml-base-whisper.dll", 636_928, "4aee9ddc683134af3f142c6eb084b4cabbbb4a508a8a4a7e1c3edb7fb4c2c015"),
            F("build/win-x64/", "ggml-cpu-whisper.dll", 782_848, "65b8b0524c8c5cff3fabd6312c38a4b03a5fec5cabcedc4c1fb7e761639aa027"),
            F("build/win-x64/", "ggml-cuda-whisper.dll", 154_601_984, "ed197873d29fddd308cb79276288dcf313b64c58894a1ac5db6c3a01a2a87443"),
            F("build/win-x64/", "ggml-whisper.dll", 67_072, "c16bd1fe1d04ad708c407b11ee38f1ee44caa7206e8ffddd4b34fb2f4192781a"),
            F("build/win-x64/", "whisper.dll", 484_864, "ef73371ba9d1ef0ab94316907cd50b9cbec5dd0829b89ba6f3dc19871c32f40a"),
        },
        Runtime: false);

    // NVIDIA's redistributable archives for CUDA 13.0.0 (the versions the
    // CUDA Toolkit 13.0 installs; developer.download.nvidia.com publishes
    // their sizes and hashes in redistrib_13.0.0.json).
    public static readonly PackPart CudaRuntime = new(
        "cuda-runtime", "CUDA 13 runtime",
        "https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-13.0.48-archive.zip",
        2_873_274, "82fb29001895810ce02e88b180cbaa90607b852a7f67a63f18f202079eaa2966",
        new[]
        {
            F("cuda_cudart-windows-x86_64-13.0.48-archive/bin/x64/", "cudart64_13.dll", 470_640, "58c509047f9dd1879cf82c6d5375a95c02c7bd295ae6c8362c745c936e2afc31"),
            new PackFile("cuda_cudart-windows-x86_64-13.0.48-archive/LICENSE", LicenseFileName, 67_875,
                "f176e5fd245a4ff1d7aee743e345e14b29c4b724f150e4858c4c6a313966d1fb"),
        },
        Runtime: true);

    public static readonly PackPart CuBlas = new(
        "cublas", "cuBLAS",
        "https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-13.0.0.19-archive.zip",
        399_809_469, "c8fb12715b9639f51983315cc4b195e272128c675aa3766b13c4f470b892e6c8",
        new[]
        {
            F("libcublas-windows-x86_64-13.0.0.19-archive/bin/x64/", "cublasLt64_13.dll", 477_896_816, "e1c26671801ecd435baf97c6e9cfe28196cf55005c2039356b48030b09ce5dd5"),
            F("libcublas-windows-x86_64-13.0.0.19-archive/bin/x64/", "cublas64_13.dll", 50_288_240, "5d083083cdd613577496791dd96d00fe3a78c955684b53ca46a8ffa3e0b1e170"),
        },
        Runtime: true);

    public static readonly PackPart[] Parts = { WhisperCuda, CudaRuntime, CuBlas };

    // The runtime libraries in dependency order: each must be in the process
    // (by name) before the next one asks for it. cudart first because
    // Whisper.net's own CUDA check asks for it by name.
    public static readonly string[] RuntimeLoadOrder = { "cudart64_13.dll", "cublasLt64_13.dll", "cublas64_13.dll" };

    // What a system-wide CUDA 13 runtime (NVIDIA's CUDA Toolkit 13) must
    // provide for the pack to skip its own copy.
    public static string[] CudaRuntimeLibraries => RuntimeLoadOrder;

    // A connect that never answers gives up after 30 s; a transfer that
    // stalls is cut by SttDownload's idle timeout, never by a total one.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) }) { Timeout = Timeout.InfiniteTimeSpan };

    // Identifies the app to the download hosts — see SttModelManager.SetUserAgent.
    public static void SetUserAgent(string ua) =>
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);

    public static string InstallDir => Path.Combine(
        AppContext.BaseDirectory, "runtimes", "cuda", "win-x64");

    // ── state ──────────────────────────────────────────────────────

    private static bool FilePresent(PackFile f) =>
        new FileInfo(Path.Combine(InstallDir, f.Name)) is { Exists: true } fi && fi.Length == f.Size;

    public static bool PartInstalled(PackPart part) => part.Files.All(FilePresent);

    // The whisper.cpp CUDA natives are there (size check only, as before).
    public static bool WhisperInstalled() => PartInstalled(WhisperCuda);

    // The pack's own copy of the CUDA 13 runtime is complete.
    public static bool BundledRuntimeInstalled() => Parts.Where(p => p.Runtime).All(PartInstalled);

    // Where Windows looks for a library asked for by bare name, apart from
    // the app's own folder: the system folder, then PATH. Never answered by
    // loading anything.
    internal static IEnumerable<string> SystemSearchDirs()
    {
        yield return Environment.SystemDirectory;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var d = dir.Trim().Trim('"');
            if (d.Length > 0) yield return d;
        }
    }

    internal static string? FindIn(IEnumerable<string> dirs, string name)
    {
        foreach (var dir in dirs)
        {
            try
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }
            catch { /* a malformed PATH entry */ }
        }
        return null;
    }

    public static string? SystemLibraryPath(string name) => FindIn(SystemSearchDirs(), name);

    // The system provides a CUDA 13 runtime (the CUDA Toolkit puts its bin
    // folder on PATH).
    public static bool SystemRuntimePresent() => CudaRuntimeLibraries.All(n => SystemLibraryPath(n) != null);

    // Can ggml-cuda-whisper's runtime dependencies be satisfied right now?
    public static bool CudaRuntimePresent() => BundledRuntimeInstalled() || SystemRuntimePresent();

    // "Installed" means it can actually work: natives plus a runtime.
    public static bool IsInstalled() => WhisperInstalled() && CudaRuntimePresent();

    // Which parts a download fetches now: what is missing, minus the runtime
    // parts when the system already provides a CUDA 13 runtime.
    internal static List<PackPart> Plan(Func<PackPart, bool> partInstalled, bool systemRuntime)
    {
        var plan = new List<PackPart>();
        foreach (var part in Parts)
        {
            if (partInstalled(part)) continue;
            if (part.Runtime && systemRuntime) continue;
            plan.Add(part);
        }
        return plan;
    }

    private static List<PackPart> Plan() => Plan(PartInstalled, SystemRuntimePresent());

    // The download size shown in the UI: what a download would fetch now, or
    // the footprint of what is installed.
    public static long SizeBytes
    {
        get
        {
            var plan = Plan();
            return plan.Count > 0 ? plan.Sum(p => p.Size) : Parts.Where(PartInstalled).Sum(p => p.Size);
        }
    }

    // What to append to a message that promises acceleration when the
    // natives are there but no runtime is; "" otherwise.
    public static string RuntimeNote() =>
        CudaRuntimePresent()
            ? ""
            : ". It still needs its CUDA runtime part — download GPU acceleration again on the Models screen to fetch it";

    // ── loading ────────────────────────────────────────────────────

    private static int _preloaded;
    private static readonly List<IntPtr> Handles = new(); // kept for the life of the process
    public static string RuntimeStatus { get; private set; } = "not checked";

    // The complete pack was in place when this process started (natives
    // plus a runtime that loaded): whisper.cpp had every chance to pick
    // CUDA, so a CPU engine now is a loader verdict, not something a
    // restart fixes.
    public static bool ArmedAtStartup { get; private set; }

    // Loads the CUDA 13 runtime into the process by full path, dependencies
    // first, so that Whisper.net's bare-name lookup of cudart64_13 and
    // ggml-cuda-whisper's imports resolve to these copies. Bundled libraries
    // win; a system runtime is loaded by its real path too, so "present" and
    // "loadable" mean the same thing. Once per process, before any
    // Whisper.net native call; never unloads.
    public static void PreloadRuntime()
    {
        if (Interlocked.Exchange(ref _preloaded, 1) != 0) return;
        SweepDoomed();
        if (!WhisperInstalled()) { RuntimeStatus = "GPU pack not installed"; return; }
        bool bundled = BundledRuntimeInstalled();
        if (!bundled && !SystemRuntimePresent())
        {
            RuntimeStatus = "CUDA 13 runtime missing (cudart64_13.dll, cublas64_13.dll, cublasLt64_13.dll) — download GPU acceleration again on the Models screen";
            return;
        }
        foreach (var name in RuntimeLoadOrder)
        {
            var path = bundled ? Path.Combine(InstallDir, name) : SystemLibraryPath(name);
            if (path != null && NativeLibrary.TryLoad(path, out var handle))
            {
                Handles.Add(handle);
                continue;
            }
            RuntimeStatus = $"{(bundled ? "bundled" : "system")} CUDA 13 runtime failed to load: {name}" +
                            (path == null ? " (path unknown)" : $" from {path}");
            ErrorLog.WriteNote("SttGpuPack.PreloadRuntime", RuntimeStatus);
            return;
        }
        RuntimeStatus = bundled
            ? "bundled CUDA 13 runtime loaded"
            : $"system CUDA 13 runtime loaded ({Path.GetDirectoryName(SystemLibraryPath(RuntimeLoadOrder[0]))})";
        ArmedAtStartup = true;
    }

    // One line for the boot log: the pack and the runtime as this start saw
    // them.
    public static string Status() =>
        $"pack {(WhisperInstalled() ? "installed" : "not installed")}; runtime: {RuntimeStatus}";

    // ── download ───────────────────────────────────────────────────

    // (receivedBytes, totalBytes) progress across every part still needed.
    public static async Task<(bool Ok, string? Error)> DownloadAsync(
        Action<long, long> onProgress, CancellationToken ct = default)
    {
        var plan = Plan();
        if (plan.Count == 0) return (true, null);
        long total = plan.Sum(p => p.Size);
        long done = 0;
        // A folder that cannot be made or a full drive is not a download
        // failure: nothing was fetched, so no "retry continues" promise.
        try
        {
            Directory.CreateDirectory(InstallDir);
            // The parts are staged one at a time in the install folder and
            // each is deleted once extracted; bytes a previous attempt
            // already staged count.
            long staging = plan.Max(p => p.Size - StagedBytes(p));
            SttDownload.EnsureFreeSpace(InstallDir, staging + plan.Sum(p => p.ExtractedBytes) + 64_000_000);
        }
        catch (Exception ex)
        {
            return (false, $"GPU pack: {ex.Message}");
        }
        foreach (var part in plan)
        {
            long offset = done;
            var (ok, error) = await FetchPartAsync(part, received => onProgress(offset + received, total), ct);
            if (!ok) return (false, error);
            done += part.Size;
        }
        return (true, null);
    }

    // The archive is staged in the install folder itself, not the temp
    // folder, so it sits on the drive the extraction needs anyway. A network
    // failure keeps the staged file so the next attempt resumes it
    // (SttDownload.ResumableDownloadAsync); a bad hash, a cancel or a source
    // that changed (the server refuses the resume: 416) drops it.
    private static string StagedPath(PackPart part) =>
        Path.Combine(InstallDir, (part == WhisperCuda ? Id : part.Id) + ".download");   // part 1 keeps its pre-1.7.3 name, so a half-done download resumes

    private static long StagedBytes(PackPart part)
    {
        try { if (new FileInfo(StagedPath(part)) is { Exists: true } fi && fi.Length < part.Size) return fi.Length; } catch { }
        return 0;
    }

    private static async Task<(bool Ok, string? Error)> FetchPartAsync(
        PackPart part, Action<long> onProgress, CancellationToken ct)
    {
        var temp = StagedPath(part);
        bool keepTemp = false;
        try
        {
            Directory.CreateDirectory(InstallDir);
            using (var sha = SHA256.Create())
            {
                long received = await SttDownload.ResumableDownloadAsync(Http, part.Url, temp, part.Size, sha, onProgress, ct);
                var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (received != part.Size || hash != part.Sha256)
                    return (false, $"GPU pack ({part.Label}): package SHA-256 mismatch — download corrupt or source changed");
            }

            using (var zip = ZipFile.OpenRead(temp))
            {
                foreach (var file in part.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = zip.GetEntry(file.Entry);
                    if (entry == null)
                        return (false, $"GPU pack ({part.Label}): {file.Name} missing from the package");

                    var partial = Path.Combine(InstallDir, file.Name + ".partial");
                    entry.ExtractToFile(partial, overwrite: true);

                    if (new FileInfo(partial).Length != file.Size ||
                        SttModelManager.ComputeSha256(partial) != file.Sha256)
                    {
                        File.Delete(partial);
                        return (false, $"GPU pack ({part.Label}): {file.Name} failed verification");
                    }
                    File.Move(partial, Path.Combine(InstallDir, file.Name), overwrite: true);
                }
            }
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, "download cancelled");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The staged file is longer than what the server now has: it can
            // never be continued, so it goes and the next try starts clean.
            return (false, $"GPU pack ({part.Label}): download failed — the source changed; retry to download it afresh");
        }
        catch (Exception ex)
        {
            keepTemp = true;
            return (false, $"GPU pack ({part.Label}): download failed — {ex.Message} (a retry continues where it stopped)");
        }
        finally
        {
            try { if (!keepTemp && File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    // Re-hash every installed pack file against the pinned hashes — the
    // Verify action's coverage of the natives (install detection is
    // size-only). A part that is absent altogether is not the user's problem
    // (a system runtime stands in for it); a part with any file present must
    // be whole.
    public static (int Ok, List<string> Bad) VerifyFiles()
    {
        int ok = 0;
        var bad = new List<string>();
        foreach (var part in Parts)
        {
            if (!part.Files.Any(f => File.Exists(Path.Combine(InstallDir, f.Name)))) continue;
            foreach (var f in part.Files)
            {
                var path = Path.Combine(InstallDir, f.Name);
                if (File.Exists(path) && new FileInfo(path).Length == f.Size &&
                    SttModelManager.ComputeSha256(path) == f.Sha256) ok++;
                else bad.Add($"GPU acceleration ({f.Name})");
            }
        }
        return (ok, bad);
    }

    private const string DoomedSuffix = ".delete";

    // Removes every pack file, any staged or half-extracted leftover, and the
    // folder itself when nothing else is in it. A library this process has
    // loaded cannot be deleted while it runs, but Windows lets it be renamed:
    // it is set aside under a ".delete" name, so the pack reads as removed at
    // once, and SweepDoomed clears it at the next start. False = something
    // was only set aside (or could not be touched at all).
    public static bool Delete()
    {
        bool clean = true;
        try
        {
            if (!Directory.Exists(InstallDir)) return true;
            foreach (var f in Parts.SelectMany(p => p.Files))
            {
                var path = Path.Combine(InstallDir, f.Name);
                if (!File.Exists(path)) continue;
                try { File.Delete(path); }
                catch
                {
                    clean = false;
                    try { File.Move(path, path + DoomedSuffix, overwrite: true); } catch { }
                }
            }
            foreach (var pattern in new[] { "*.partial", "*.download" })
                foreach (var stray in Directory.EnumerateFiles(InstallDir, pattern))
                    try { File.Delete(stray); } catch { clean = false; }
            if (!Directory.EnumerateFileSystemEntries(InstallDir).Any()) Directory.Delete(InstallDir);
            return clean;
        }
        catch { return false; }
    }

    // Clears what a Delete in an earlier run could only set aside. Called
    // once at startup, before anything from the folder is loaded.
    public static void SweepDoomed()
    {
        try
        {
            if (!Directory.Exists(InstallDir)) return;
            foreach (var stray in Directory.EnumerateFiles(InstallDir, "*" + DoomedSuffix))
                try { File.Delete(stray); } catch { }
            if (!Directory.EnumerateFileSystemEntries(InstallDir).Any()) Directory.Delete(InstallDir);
        }
        catch { }
    }
}
