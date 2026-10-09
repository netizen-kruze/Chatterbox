using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Chatterbox.Stt;

// The Parakeet engine's native libraries (sherpa-onnx + ONNX Runtime),
// downloaded on demand so the app package stays small — the natives are
// useless without the (much larger) model download anyway, so they ride
// the same flow. The official runtime package from NuGet (a nupkg is a
// zip) is verified end to end: streamed SHA-256 on the archive, then a
// pinned per-file hash on each extracted DLL. Installs into the app's own
// directory, which the Windows loader searches first — no configuration,
// and usable immediately (no restart: the first Parakeet load happens
// after install). App updates swap only the exe, so an installed pack
// survives them; deleting the whole app folder removes it and the pack
// shows as downloadable again.
public static class SttEnginePack
{
    public const string Id = "parakeet-engine";
    public const string DisplayName = "Parakeet engine (ONNX runtime)";
    public const string License = "Apache-2.0 / MIT";
    public const string Attribution = "sherpa-onnx (Apache-2.0, k2-fsa); ONNX Runtime (MIT, Microsoft)";

    private const string NupkgUrl =
        "https://api.nuget.org/v3-flatcontainer/org.k2fsa.sherpa.onnx.runtime.win-x64/1.13.5/org.k2fsa.sherpa.onnx.runtime.win-x64.1.13.5.nupkg";
    private const long NupkgSizeBytes = 8_340_889;
    private const string NupkgSha256 = "eab958cf8d369f2eb8499b237fdc590bf2ed62338213af032ded1b44e02066e7";
    private const string ArchivePrefix = "runtimes/win-x64/native/";

    private static readonly (string Name, long Size, string Sha256)[] Files =
    {
        ("onnxruntime.dll", 17_378_304, "b9f6713c3602a4742680a7e6a77e3f9ac4a676ad9447bce609e53efcda795d7e"),
        ("sherpa-onnx-c-api.dll", 4_590_592, "eb94a15429dcb830374e0ae5c9e646f98985c67dd7eff9e8b802439ec4ea9388"),
    };

    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) }) { Timeout = Timeout.InfiniteTimeSpan };

    public static void SetUserAgent(string ua) =>
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);

    public static string InstallDir => AppContext.BaseDirectory;

    // The download size shown in the UI (the extracted footprint is ~22 MB).
    public static long SizeBytes => NupkgSizeBytes;

    public static bool IsInstalled() =>
        Files.All(f => new FileInfo(Path.Combine(InstallDir, f.Name)) is { Exists: true } fi && fi.Length == f.Size);

    // (receivedBytes, totalBytes) progress against the nupkg download.
    // The archive is staged in the install folder itself, beside what it
    // unpacks into. A network failure keeps the staged file so the next
    // attempt resumes it (SttDownload.ResumableDownloadAsync); a bad hash, a
    // cancel or a source that changed (the server refuses the resume: 416)
    // drops it, and Delete removes a leftover.
    private static string StagedPath => Path.Combine(InstallDir, Id + ".download");

    public static async Task<(bool Ok, string? Error)> DownloadAsync(
        Action<long, long> onProgress, CancellationToken ct = default)
    {
        var tempNupkg = StagedPath;
        // A folder that cannot be made or a full drive is not a download
        // failure: nothing was fetched, so no "retry continues" promise.
        try
        {
            Directory.CreateDirectory(InstallDir);
            long have = 0;
            try { if (new FileInfo(tempNupkg) is { Exists: true } fi && fi.Length < NupkgSizeBytes) have = fi.Length; } catch { }
            // The rest of the archive, the unpacked libraries, and headroom.
            SttDownload.EnsureFreeSpace(InstallDir, NupkgSizeBytes - have + Files.Sum(f => f.Size) + 16_000_000);
        }
        catch (Exception ex)
        {
            return (false, $"Engine pack: {ex.Message}");
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
                    return (false, "Engine pack: package SHA-256 mismatch — download corrupt or source changed");
            }

            using (var zip = ZipFile.OpenRead(tempNupkg))
            {
                foreach (var file in Files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = zip.GetEntry(ArchivePrefix + file.Name);
                    if (entry == null)
                        return (false, $"Engine pack: {file.Name} missing from the package");

                    var partial = Path.Combine(InstallDir, file.Name + ".partial");
                    entry.ExtractToFile(partial, overwrite: true);

                    if (new FileInfo(partial).Length != file.Size ||
                        SttModelManager.ComputeSha256(partial) != file.Sha256)
                    {
                        File.Delete(partial);
                        return (false, $"Engine pack: {file.Name} failed verification");
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
            return (false, "Engine pack: download failed — the source changed; retry to download it afresh");
        }
        catch (Exception ex)
        {
            keepTemp = true;
            return (false, $"Engine pack: download failed — {ex.Message} (a retry continues where it stopped)");
        }
        finally
        {
            try { if (!keepTemp && File.Exists(tempNupkg)) File.Delete(tempNupkg); } catch { }
        }
    }

    // Re-hash installed files against the pins (the Verify action's coverage).
    public static (int Ok, List<string> Bad) VerifyFiles()
    {
        int ok = 0;
        var bad = new List<string>();
        foreach (var f in Files)
        {
            var path = Path.Combine(InstallDir, f.Name);
            if (File.Exists(path) && new FileInfo(path).Length == f.Size &&
                SttModelManager.ComputeSha256(path) == f.Sha256) ok++;
            else bad.Add($"Parakeet engine ({f.Name})");
        }
        return (ok, bad);
    }

    public static bool Delete()
    {
        try
        {
            foreach (var f in Files)
            {
                var path = Path.Combine(InstallDir, f.Name);
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".partial")) File.Delete(path + ".partial");
            }
            // A staged archive a failed download left for a resume.
            if (File.Exists(StagedPath)) File.Delete(StagedPath);
            return true;
        }
        catch { return false; }
    }
}
