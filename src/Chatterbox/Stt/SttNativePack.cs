using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Chatterbox.Stt;

// A set of native libraries fetched on demand from an official NuGet
// package (a nupkg is a zip): the archive's SHA-256 is checked while it
// streams, then every extracted file is checked against its own pin before
// it is moved into place. The translation runtime ships this way — its
// natives are too large to embed in the exe and useless without the
// (larger) model download anyway — exactly like the Parakeet engine pack.
// Files land under <app>\runtimes\win-x64\native\<variant>\, the directory
// layout the LLamaSharp loader probes, so no configuration is needed; app
// updates swap only the exe, so an installed pack survives them.
public sealed class SttNativePack
{
    public readonly record struct PackFile(string Subdir, string Name, long Size, string Sha256);

    public string Id { get; }
    public string DisplayName { get; }
    public string License { get; }
    public string Attribution { get; }
    public long SizeBytes { get; }
    private readonly string _nupkgUrl;
    private readonly string _nupkgSha256;
    private readonly string _archivePrefix;
    private readonly PackFile[] _files;

    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) }) { Timeout = Timeout.InfiniteTimeSpan };

    public static void SetUserAgent(string ua) =>
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);

    public SttNativePack(string id, string displayName, string license, string attribution,
        string nupkgUrl, long nupkgSize, string nupkgSha256, string archivePrefix, PackFile[] files)
    {
        Id = id; DisplayName = displayName; License = license; Attribution = attribution;
        _nupkgUrl = nupkgUrl; SizeBytes = nupkgSize; _nupkgSha256 = nupkgSha256;
        _archivePrefix = archivePrefix; _files = files;
    }

    public static string InstallRoot => Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");

    public IReadOnlyList<PackFile> Files => _files;

    private static string PathFor(PackFile f) => Path.Combine(InstallRoot, f.Subdir, f.Name);

    public bool IsInstalled() =>
        _files.All(f => new FileInfo(PathFor(f)) is { Exists: true } fi && fi.Length == f.Size);

    public async Task<(bool Ok, string? Error)> DownloadAsync(Action<long, long> onProgress, CancellationToken ct = default)
    {
        var tempNupkg = Path.Combine(Path.GetTempPath(), $"chatterbox-{Id}-{Guid.NewGuid():N}.nupkg");
        try
        {
            using (var response = await Http.GetAsync(_nupkgUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                SttDownload.EnsureFreeSpace(Path.GetTempPath(), SizeBytes);
                SttDownload.EnsureFreeSpace(InstallRoot, _files.Sum(f => f.Size) + SizeBytes);
                using var sha = SHA256.Create();
                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var target = File.Create(tempNupkg))
                {
                    await SttDownload.CopyAsync(source, target, sha, received => onProgress(received, SizeBytes), ct);
                }
                var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (hash != _nupkgSha256)
                    return (false, $"{DisplayName}: package SHA-256 mismatch — download corrupt or source changed");
            }

            using (var zip = ZipFile.OpenRead(tempNupkg))
            {
                foreach (var file in _files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = zip.GetEntry(_archivePrefix + file.Subdir + "/" + file.Name);
                    if (entry == null) return (false, $"{DisplayName}: {file.Subdir}/{file.Name} missing from the package");

                    var dir = Path.Combine(InstallRoot, file.Subdir);
                    Directory.CreateDirectory(dir);
                    var partial = Path.Combine(dir, file.Name + ".partial");
                    entry.ExtractToFile(partial, overwrite: true);
                    if (new FileInfo(partial).Length != file.Size || SttModelManager.ComputeSha256(partial) != file.Sha256)
                    {
                        File.Delete(partial);
                        return (false, $"{DisplayName}: {file.Name} failed verification");
                    }
                    File.Move(partial, Path.Combine(dir, file.Name), overwrite: true);
                }
            }
            return (true, null);
        }
        catch (OperationCanceledException) { return (false, "download cancelled"); }
        catch (Exception ex) { return (false, $"{DisplayName}: download failed — {ex.Message}"); }
        finally
        {
            try { if (File.Exists(tempNupkg)) File.Delete(tempNupkg); } catch { }
        }
    }

    // Re-hash installed files against the pins (the Verify action's coverage).
    public (int Ok, List<string> Bad) VerifyFiles()
    {
        int ok = 0;
        var bad = new List<string>();
        foreach (var f in _files)
        {
            var path = PathFor(f);
            if (File.Exists(path) && new FileInfo(path).Length == f.Size && SttModelManager.ComputeSha256(path) == f.Sha256) ok++;
            else bad.Add($"{DisplayName} ({f.Subdir}/{f.Name})");
        }
        return (ok, bad);
    }

    public bool Delete()
    {
        try
        {
            foreach (var f in _files)
            {
                var path = PathFor(f);
                if (File.Exists(path)) File.Delete(path);
            }
            foreach (var dir in _files.Select(f => Path.Combine(InstallRoot, f.Subdir)).Distinct())
            {
                try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            }
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("SttNativePack.Delete", ex);
            return false;
        }
    }
}

// The translation runtime (llama.cpp, packaged by LLamaSharp) as two packs:
// the CPU build, which every machine can run (four variants; the loader
// picks the best one for the processor), and the Vulkan build, which runs
// the model on any GPU — NVIDIA, AMD or Intel — without a CUDA runtime.
// Pins are the official 0.27.0 packages on nuget.org, hashed file by file.
public static class SttTranslatePacks
{
    private const string Version = "0.27.0";
    private const string Prefix = "LLamaSharpRuntimes/win-x64/native/";

    public static readonly SttNativePack Cpu = new(
        "translate-engine", "Translation engine (llama.cpp, CPU)", "MIT",
        "llama.cpp (MIT, ggml-org), packaged by LLamaSharp (MIT)",
        $"https://api.nuget.org/v3-flatcontainer/llamasharp.backend.cpu/{Version}/llamasharp.backend.cpu.{Version}.nupkg",
        36_337_071, "5a3416a712c2fe9a4a70f8915f8a7e5b82948f5c74a517a2c3ee29158204bb26", Prefix, new SttNativePack.PackFile[]
        {
            new("avx", "ggml-base.dll", 615_936, "7b110a7049afb7b7c653af4f8f3c023e59995b673df8468db7620a9ff5d9a4d5"),
            new("avx", "ggml-cpu.dll", 820_736, "447502e055c0df2ffd7ceba6e800e1608029bd1d6c760e5a1b29b2f6d9671a72"),
            new("avx", "ggml.dll", 67_072, "90653a391d5cc6af47b52b23ef539a057122faabcf4da362cba32115220ecf40"),
            new("avx", "llama.dll", 2_055_680, "43465972ebdc01fddb0b09feb613230a2844e6658e0b2da88025ddfdd51deddb"),
            new("avx2", "ggml-base.dll", 615_936, "09835b5faec3acc7e88f0ddee1ddf8a303108c9664fc581c6b5bff03f1032353"),
            new("avx2", "ggml-cpu.dll", 878_592, "5c579f09d7b4f782c534b03f5962ff82cdddf9374aa443175f9db3cbc86b7b3c"),
            new("avx2", "ggml.dll", 67_072, "44ec41894eb611c9bb86055cedb6759603fcb6a9dc994abccb15f08a031b486a"),
            new("avx2", "llama.dll", 2_055_680, "1aa8f6f65386b7b977f9a0bb631d6d7e2a30d3beb3f5747e80e1b114f4885fb8"),
            new("avx512", "ggml-base.dll", 615_936, "6a45172a836430e372adb31039dd48a94fd5f2a2c023c0f2f7629e0e0faa8fe5"),
            new("avx512", "ggml-cpu.dll", 979_456, "9bbd41bbda9904f72194edccffe91068f56e5b6d49d874a556db08b320e222c7"),
            new("avx512", "ggml.dll", 67_072, "23b4bcf921c6e77d6e0a7864f740f7eb4aec2cb946453f46b8fa5d337460ad8f"),
            new("avx512", "llama.dll", 2_055_680, "1aa8f6f65386b7b977f9a0bb631d6d7e2a30d3beb3f5747e80e1b114f4885fb8"),
            new("noavx", "ggml-base.dll", 615_936, "57803fba605d6e3eae4bfc48bf6b120c03ddd3926ad5a0e3bb2b11598b20f598"),
            new("noavx", "ggml-cpu.dll", 708_096, "bb3d65715350df4b9f2d605367e7063e27c40146330e6ed9807598c41aec08a5"),
            new("noavx", "ggml.dll", 67_072, "905ec5ea9db9bd1e795cf2cb4f7f9399c50e82a74379efe75815de7715da45cf"),
            new("noavx", "llama.dll", 2_055_680, "6e18361b4fdce03588c19cc21a818ffcb4d3326d3debaa898af4cc27fd10c877"),
        });

    public static readonly SttNativePack Gpu = new(
        "translate-gpu-pack", "GPU acceleration for translation (Vulkan)", "MIT",
        "llama.cpp Vulkan build (MIT, ggml-org), packaged by LLamaSharp (MIT)",
        $"https://api.nuget.org/v3-flatcontainer/llamasharp.backend.vulkan.windows/{Version}/llamasharp.backend.vulkan.windows.{Version}.nupkg",
        20_194_168, "0fe18b028a3348d53d69a4375733f19e7246bd6a8f5eda18bec93cd555485285", Prefix, new SttNativePack.PackFile[]
        {
            new("vulkan", "ggml-base.dll", 615_936, "7e838ba129672c59ad728f56fc5685cadefda56ad50a8fcbccbb9c851ed90b4a"),
            new("vulkan", "ggml-vulkan.dll", 61_978_112, "515171efa8556572795ea37f78193ef56d3dd5079d3d221979be89c107938038"),
            new("vulkan", "ggml.dll", 67_584, "7465a5e0b304255a62fb2e5d01c106cc39fc0f4362bfd99ff99acb1278cacccc"),
            new("vulkan", "llama.dll", 2_055_680, "e9f3127597085ce9d9a0e49e4bd1f62efe35624c76f0964e802dc7c60b5156bb"),
        });

    public static SttNativePack? Find(string id) => id == Cpu.Id ? Cpu : id == Gpu.Id ? Gpu : null;
}
