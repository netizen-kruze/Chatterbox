using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Chatterbox.Stt;

// Optional CUDA acceleration for the Whisper engine, downloaded on demand so
// release packages stay CPU-only (~300 MB smaller). The natives are pulled
// from the official Whisper.net.Runtime.Cuda.Windows package on NuGet (a
// nupkg is a zip), verified per-file, and placed in the app's own
// runtimes\cuda\win-x64\ directory — exactly where Whisper.net's loader
// probes first, so no loader configuration is needed. Takes effect on the
// next app start (natives load once per process). App updates swap only
// the exe, so an installed pack survives them; deleting the whole app
// folder removes it and the pack shows as downloadable again.
public static class SttGpuPack
{
    public const string Id = "cuda-gpu-pack";
    public const string DisplayName = "GPU acceleration for Whisper (CUDA)";
    public const string License = "MIT";
    public const string Attribution = "whisper.cpp CUDA build (MIT), packaged by Whisper.net";

    private const string NupkgUrl =
        "https://api.nuget.org/v3-flatcontainer/whisper.net.runtime.cuda.windows/1.9.1/whisper.net.runtime.cuda.windows.1.9.1.nupkg";
    private const long NupkgSizeBytes = 142_586_522;
    private const string NupkgSha256 = "3b765c9690c114825af1e9fd48e8f2e6f8b798c314d29e27e09640da479dfceb";
    private const string ArchivePrefix = "build/win-x64/";

    private static readonly (string Name, long Size, string Sha256)[] Files =
    {
        ("ggml-base-whisper.dll", 636_928, "4aee9ddc683134af3f142c6eb084b4cabbbb4a508a8a4a7e1c3edb7fb4c2c015"),
        ("ggml-cpu-whisper.dll", 782_848, "65b8b0524c8c5cff3fabd6312c38a4b03a5fec5cabcedc4c1fb7e761639aa027"),
        ("ggml-cuda-whisper.dll", 154_601_984, "ed197873d29fddd308cb79276288dcf313b64c58894a1ac5db6c3a01a2a87443"),
        ("ggml-whisper.dll", 67_072, "c16bd1fe1d04ad708c407b11ee38f1ee44caa7206e8ffddd4b34fb2f4192781a"),
        ("whisper.dll", 484_864, "ef73371ba9d1ef0ab94316907cd50b9cbec5dd0829b89ba6f3dc19871c32f40a"),
    };

    // A connect that never answers gives up after 30 s; a transfer that
    // stalls is cut by SttDownload's idle timeout, never by a total one.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) }) { Timeout = Timeout.InfiniteTimeSpan };

    // Identifies the app to the download host — see SttModelManager.SetUserAgent.
    public static void SetUserAgent(string ua) =>
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);

    public static string InstallDir => Path.Combine(
        AppContext.BaseDirectory, "runtimes", "cuda", "win-x64");

    // The download size shown in the UI (the extracted footprint is ~157 MB).
    public static long SizeBytes => NupkgSizeBytes;

    public static bool IsInstalled() =>
        Files.All(f => new FileInfo(Path.Combine(InstallDir, f.Name)) is { Exists: true } fi && fi.Length == f.Size);

    // (receivedBytes, totalBytes) progress against the nupkg download.
    // The archive is staged in the install folder itself, not the temp
    // folder, so it sits on the drive the extraction needs anyway. A
    // network failure keeps the staged file so the next attempt resumes it
    // (SttDownload.ResumableDownloadAsync); a bad hash or a cancel drops it.
    public static async Task<(bool Ok, string? Error)> DownloadAsync(
        Action<long, long> onProgress, CancellationToken ct = default)
    {
        var tempNupkg = Path.Combine(InstallDir, Id + ".download");
        // A folder that cannot be made or a full drive is not a download
        // failure: nothing was fetched, so no "retry continues" promise.
        try
        {
            Directory.CreateDirectory(InstallDir);
            SttDownload.EnsureFreeSpace(InstallDir, NupkgSizeBytes + Files.Sum(f => f.Size) + 64_000_000);
        }
        catch (Exception ex)
        {
            return (false, $"GPU pack: {ex.Message}");
        }
        bool keepTemp = false;
        try
        {
            using (var sha = SHA256.Create())
            {
                long received = await SttDownload.ResumableDownloadAsync(Http, NupkgUrl, tempNupkg, NupkgSizeBytes, sha,
                    got => onProgress(got, NupkgSizeBytes), ct);
                var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (received != NupkgSizeBytes || hash != NupkgSha256)
                    return (false, "GPU pack: package SHA-256 mismatch — download corrupt or source changed");
            }

            using (var zip = ZipFile.OpenRead(tempNupkg))
            {
                foreach (var file in Files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = zip.GetEntry(ArchivePrefix + file.Name);
                    if (entry == null)
                        return (false, $"GPU pack: {file.Name} missing from the package");

                    var partial = Path.Combine(InstallDir, file.Name + ".partial");
                    entry.ExtractToFile(partial, overwrite: true);

                    if (new FileInfo(partial).Length != file.Size ||
                        SttModelManager.ComputeSha256(partial) != file.Sha256)
                    {
                        File.Delete(partial);
                        return (false, $"GPU pack: {file.Name} failed verification");
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
        catch (Exception ex)
        {
            keepTemp = true;
            return (false, $"GPU pack: download failed — {ex.Message} (a retry continues where it stopped)");
        }
        finally
        {
            try { if (!keepTemp && File.Exists(tempNupkg)) File.Delete(tempNupkg); } catch { }
        }
    }

    // Re-hash every installed pack file against the pinned hashes — the
    // Verify action's coverage of the native DLLs (IsInstalled is size-only).
    public static (int Ok, List<string> Bad) VerifyFiles()
    {
        int ok = 0;
        var bad = new List<string>();
        foreach (var f in Files)
        {
            var path = Path.Combine(InstallDir, f.Name);
            if (File.Exists(path) && new FileInfo(path).Length == f.Size &&
                SttModelManager.ComputeSha256(path) == f.Sha256) ok++;
            else bad.Add($"GPU acceleration ({f.Name})");
        }
        return (ok, bad);
    }

    // Removes every pack file, any staged or half-extracted leftover, and the
    // folder itself when nothing else is in it.
    public static bool Delete()
    {
        try
        {
            if (!Directory.Exists(InstallDir)) return true;
            foreach (var f in Files)
            {
                var path = Path.Combine(InstallDir, f.Name);
                if (File.Exists(path)) File.Delete(path);
            }
            foreach (var pattern in new[] { "*.partial", "*.download" })
                foreach (var stray in Directory.EnumerateFiles(InstallDir, pattern)) File.Delete(stray);
            if (!Directory.EnumerateFileSystemEntries(InstallDir).Any()) Directory.Delete(InstallDir);
            return true;
        }
        catch { return false; }
    }
}
