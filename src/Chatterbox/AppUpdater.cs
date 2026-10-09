using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Chatterbox.Stt;

namespace Chatterbox;

// The latest published release, when it is newer than this build.
public sealed record UpdateInfo(
    Version Version, string Tag, string Title, string Notes, string PageUrl,
    string AssetName, string AssetUrl, long AssetSize, string Sha256);

// What a check found: a newer release, or the latest version when this
// build is current, or why nothing could be determined.
public sealed record UpdateCheck(UpdateInfo? Update, Version? Latest, string? Error);

// In-app updates from the project's GitHub Releases page.
//
// Check: one request to the Releases API says what the latest release is.
// It counts as an update when its tag is a higher version than this build
// and it carries a win-x64 zip plus a SHA-256 line in its notes (the
// publish checklist puts one there). Install: the zip is downloaded, hashed
// against that line, and Chatterbox.exe is taken out of it and staged
// beside the running exe as Chatterbox.exe.new; a marker records the
// pending update; the app exits and the STAGED EXE runs as a helper
// (--finish-update, RunSwapHelper): it waits until this process and its
// WebView2 browser are gone, renames the old exe aside, copies itself
// under the real name and starts the new version. The next start removes
// the old file and the staged one and reports the update in the boot log
// and a toast.
//
// Why not swap in place and then restart (as 1.6.0–1.7.1 did)? A
// single-file .NET exe is its own assembly store: every assembly the app
// has not touched yet is read from the executable, by the path the host
// resolved at startup, the moment it is first needed. Windows lets a
// running exe be renamed, so the rename succeeded — and from then on the
// next first-time load opened the NEW file at the OLD bundle's offsets
// (verified on the Linux build, which dies with FileNotFoundException;
// the runtime's bundle reader is the same on both). The swap "worked"
// only as long as nothing new was loaded before the restart, which
// depends on what the session happened to do. So no managed code may run
// in a process whose file has changed; the helper copies rather than
// moves, so its own file never changes either.
//
// Privacy: the request carries nothing but the app's name and version in
// its User-Agent, like every model download. The startup check is a
// setting, off by default; the manual check is a button press.
public sealed class AppUpdater
{
    public const string ExeName = "Chatterbox.exe";
    private const int CheckTimeoutMs = 15_000;
    private static readonly Regex ShaLine = new(@"SHA-?256\s*[:=]?\s*([0-9a-fA-F]{64})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AssetPattern = new(@"^Chatterbox-.*-win-x64\.zip$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // owner/name of the GitHub repository releases go to — set in the csproj
    // (<UpdateRepository>) and baked in as assembly metadata. Empty until
    // the project is on GitHub: the Updates section then says so and no
    // request is ever made.
    public static string Repository { get; } =
        typeof(AppUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value?.Trim() ?? "";

    // Test hook (--update-url): the full URL of a "latest release" JSON
    // document that stands in for the GitHub API.
    public static string? LatestReleaseUrlOverride { get; set; }

    public static string? DefaultReleaseUrl =>
        LatestReleaseUrlOverride ??
        (Repository.Length > 0 ? $"https://api.github.com/repos/{Repository}/releases/latest" : null);

    public static bool IsConfigured => DefaultReleaseUrl != null;

    // Three-part version of this build (the csproj <Version>).
    public static Version CurrentVersion { get; } =
        typeof(AppUpdater).Assembly.GetName().Version is { } v
            ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0))
            : new Version(0, 0, 0);

    private static readonly HttpClient Shared = new() { Timeout = Timeout.InfiniteTimeSpan };

    // Identifies the app to GitHub by name and version (a User-Agent is
    // required by its API) — see SttModelManager.SetUserAgent.
    public static void SetUserAgent(string ua) =>
        Shared.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);

    private readonly HttpClient _http;

    // The "latest release" document to ask; tests point it at a fake.
    public string? ReleaseUrl { get; init; } = DefaultReleaseUrl;
    // The running exe (tests point this at a temp copy).
    public string ExePath { get; init; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExeName);
    // Written when an update is applied, read and removed by the next start.
    public string MarkerPath { get; init; } = Path.Combine(SttPaths.DataDir, "update_applied.json");

    public AppUpdater(HttpClient? http = null) => _http = http ?? Shared;

    // "v1.6.1" / "1.6.1" / "1.6" → a three-part version; null when the tag
    // is not a version at all.
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];
        int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s[..cut];
        var parts = s.Split('.');
        if (parts.Length is < 1 or > 4) return null;
        var nums = new int[3];
        for (int i = 0; i < Math.Min(parts.Length, 3); i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return null;
        return new Version(nums[0], nums[1], nums[2]);
    }

    // Reads a GitHub "release" JSON document. Throws FormatException with
    // the reason when it can't be used as an update.
    public static UpdateInfo ParseRelease(string json)
    {
        var j = JObject.Parse(json);
        var tag = j["tag_name"]?.ToString() ?? "";
        var version = ParseVersion(tag) ?? throw new FormatException($"release tag '{tag}' is not a version number");
        var assets = j["assets"] as JArray ?? new JArray();
        var asset = assets.FirstOrDefault(a => AssetPattern.IsMatch(a["name"]?.ToString() ?? ""))
            ?? throw new FormatException("the release has no Chatterbox-<version>-win-x64.zip file");
        var url = asset["browser_download_url"]?.ToString() ?? "";
        if (url.Length == 0) throw new FormatException("the release's zip has no download address");
        var notes = j["body"]?.ToString() ?? "";
        var sha = ShaLine.Match(notes);
        if (!sha.Success)
            throw new FormatException("the release notes carry no SHA-256 line, so a download could not be verified");
        return new UpdateInfo(version, tag, j["name"]?.ToString() is { Length: > 0 } name ? name : tag, notes,
            j["html_url"]?.ToString() ?? "", asset["name"]!.ToString(), url,
            asset["size"]?.Value<long>() ?? 0, sha.Groups[1].Value.ToLowerInvariant());
    }

    // One request. Never throws: the result says what happened.
    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        var url = ReleaseUrl;
        if (url == null) return new UpdateCheck(null, null, "no update source is configured in this build");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeoutMs);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheck(null, null, "GitHub shows no release — none has been published yet, or the repository is still private");
            if (!response.IsSuccessStatusCode)
                return new UpdateCheck(null, null, $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var release = ParseRelease(json);
            return release.Version > CurrentVersion
                ? new UpdateCheck(release, release.Version, null)
                : new UpdateCheck(null, release.Version, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new UpdateCheck(null, null, $"no answer from GitHub within {CheckTimeoutMs / 1000} s");
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheck(null, null, "check cancelled");
        }
        catch (Exception ex)
        {
            return new UpdateCheck(null, null, ex.Message);
        }
    }

    // Downloads the release zip, verifies it against the published SHA-256,
    // and stages its Chatterbox.exe next to the running one as
    // Chatterbox.exe.new. Returns that path, or the error.
    public async Task<(string? StagedExe, string? Error)> DownloadAsync(
        UpdateInfo update, Action<long, long> onProgress, CancellationToken ct = default)
    {
        var appDir = Path.GetDirectoryName(ExePath)!;
        var staged = Path.Combine(appDir, ExeName + ".new");
        var tempDir = Path.Combine(Path.GetDirectoryName(MarkerPath)!, "updates");
        var tempZip = Path.Combine(tempDir, update.AssetName + ".partial");
        try
        {
            Directory.CreateDirectory(tempDir);
            using var response = await _http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long total = update.AssetSize > 0 ? update.AssetSize : response.Content.Headers.ContentLength ?? 0;
            SttDownload.EnsureFreeSpace(tempDir, Math.Max(total, 1) * 2);
            SttDownload.EnsureFreeSpace(appDir, Math.Max(total, 1) * 2);

            using var sha = SHA256.Create();
            long received;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = File.Create(tempZip))
            {
                received = await SttDownload.CopyAsync(source, target, sha, got => onProgress(got, total), ct).ConfigureAwait(false);
            }
            if (update.AssetSize > 0 && received != update.AssetSize)
                return (null, $"size mismatch ({received} vs {update.AssetSize} bytes)");
            var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (hash != update.Sha256)
                return (null, "SHA-256 mismatch — the download is corrupt or the release was changed; nothing was installed");

            using (var zip = ZipFile.OpenRead(tempZip))
            {
                // Only a top-level Chatterbox.exe counts (FullName carries any folder).
                var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, ExeName, StringComparison.OrdinalIgnoreCase));
                if (entry == null) return (null, $"{ExeName} is not in the downloaded zip");
                entry.ExtractToFile(staged, overwrite: true);
                if (new FileInfo(staged).Length != entry.Length)
                {
                    File.Delete(staged);
                    return (null, "the extracted file is incomplete");
                }
            }
            return (staged, null);
        }
        catch (OperationCanceledException)
        {
            return (null, "download cancelled");
        }
        catch (Exception ex)
        {
            return (null, $"download failed — {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // The staged exe waiting beside the running one, once PrepareSwap
    // accepted it; null until then. Program starts it as the swap helper
    // when the window has closed.
    public string? PendingSwap { get; private set; }

    // Accepts the staged exe for the swap: checks it is there, beside the
    // running exe, in a folder that can be written, records the pending
    // update in the marker the next start reads, and remembers the file
    // as PendingSwap. The files themselves are not touched — the helper
    // does that after this process has exited (see the note at the top).
    // Returns the error, or null.
    public string? PrepareSwap(string stagedExe, Version to)
    {
        try
        {
            if (!File.Exists(stagedExe)) return "the downloaded file is gone";
            var dir = Path.GetDirectoryName(Path.GetFullPath(ExePath))!;
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(stagedExe)), dir, StringComparison.OrdinalIgnoreCase))
                return "the downloaded file is not beside the running exe";
            // The helper renames and copies in this folder; say so now
            // rather than fail after the app has already quit.
            var probe = Path.Combine(dir, $".chatterbox-write-test-{Environment.ProcessId}");
            try { File.WriteAllText(probe, ""); File.Delete(probe); }
            catch (Exception ex) { return $"the app folder can't be written ({ex.Message})"; }
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, JsonConvert.SerializeObject(new
            {
                from = CurrentVersion.ToString(3),
                to = to.ToString(3),
                at = DateTime.UtcNow.ToString("o"),
            }));
            PendingSwap = stagedExe;
            return null;
        }
        catch (Exception ex)
        {
            return $"could not replace {ExeName} — {ex.Message}";
        }
    }

    // ── the swap, done by the staged exe after this process has exited ──

    public const string FinishSwitch = "--finish-update";

    // What the helper is told: the exe to take over, the pid to outlive,
    // whether to start the new version afterwards, and that start's own
    // arguments (the forwarded test switches).
    public sealed record HelperRequest(string Exe, int WaitForPid, bool Relaunch, string[] RelaunchArgs);

    public static List<string> HelperArgs(string exe, int waitForPid, bool relaunch, IEnumerable<string> relaunchArgs)
    {
        var list = new List<string> { FinishSwitch, exe, waitForPid.ToString(CultureInfo.InvariantCulture), relaunch ? "1" : "0" };
        list.AddRange(relaunchArgs);
        return list;
    }

    // null when these are not helper arguments.
    public static HelperRequest? ParseHelperArgs(string[] args)
    {
        if (args.Length < 4 || args[0] != FinishSwitch) return null;
        if (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) return null;
        return new HelperRequest(args[1], pid, args[3] == "1", args[4..]);
    }

    // Starts the staged exe as the helper for this process; it does its
    // work once this process is gone. Returns the error, or null.
    public static string? StartSwapHelper(string stagedExe, string exe, bool relaunch, IEnumerable<string> relaunchArgs)
    {
        try
        {
            var psi = new ProcessStartInfo(stagedExe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            foreach (var a in HelperArgs(exe, Environment.ProcessId, relaunch, relaunchArgs)) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // The helper's whole life — Program hands "--finish-update …" here
    // before anything else happens: wait for the old process (bounded; a
    // hung exit must not hold the update forever) and its browser to be
    // gone, swap, start the new version under its real name, leave.
    public static int RunSwapHelper(string[] args)
    {
        var req = ParseHelperArgs(args);
        if (req == null) return 2;
        // Logs go where the app's go (--data-dir is among the forwarded
        // arguments when a test instance updates).
        int d = Array.IndexOf(req.RelaunchArgs, "--data-dir");
        if (d >= 0 && d + 1 < req.RelaunchArgs.Length)
            try { SttPaths.DataDir = Path.GetFullPath(req.RelaunchArgs[d + 1]); } catch { }
        try
        {
            using var old = Process.GetProcessById(req.WaitForPid);
            old.WaitForExit(60_000);
        }
        catch { /* already gone */ }
        WebViewProcesses.WaitForExit(req.WaitForPid, 10_000);
        var error = PerformSwap(req.Exe, Environment.ProcessPath ?? "");
        if (error != null) ErrorLog.WriteNote("AppUpdater.Helper", error);
        if (!req.Relaunch) return error == null ? 0 : 1;
        try
        {
            var psi = new ProcessStartInfo(req.Exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(req.Exe) ?? "",
            };
            foreach (var a in req.RelaunchArgs) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--after");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("AppUpdater.Helper", ex);
            return 1;
        }
        return error == null ? 0 : 1;
    }

    // The swap itself, run by the helper from its own staged file: the exe
    // to replace is renamed aside (its process has exited by now) and the
    // helper's OWN file is COPIED under that name — never moved, so the
    // file the helper runs from stays where its loader expects it. If the
    // copy fails, the old exe gets its name back. Returns the error, or
    // null.
    public static string? PerformSwap(string exe, string stagedSelf)
    {
        var old = exe + ".old";
        try
        {
            if (!File.Exists(stagedSelf)) return $"the staged file is gone ({stagedSelf})";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Copy(stagedSelf, exe, overwrite: true); }
            catch
            {
                try { File.Move(old, exe); } catch { }
                throw;
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"could not replace {Path.GetFileName(exe)} — {ex.Message}";
        }
    }

    // Called at boot: clears what the previous version left behind (its
    // exe renamed aside, the staged exe that ran as the helper, or a
    // download that never got installed) and reports the update it
    // finished, if any.
    public string? FinishPendingUpdate()
    {
        foreach (var leftover in new[] { ExePath + ".old", ExePath + ".new" })
        {
            try { if (File.Exists(leftover)) File.Delete(leftover); }
            catch { /* still held by the old process or the helper — the next start gets it */ }
        }
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            var marker = JObject.Parse(File.ReadAllText(MarkerPath));
            File.Delete(MarkerPath);
            var from = marker["from"]?.ToString() ?? "?";
            var to = marker["to"]?.ToString() ?? "?";
            var now = CurrentVersion.ToString(3);
            return now == to
                ? $"updated from {from} to {to}"
                : $"update to {to} was prepared, but this is version {now} — the exe was not swapped";
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("AppUpdater.Finish", ex);
            return null;
        }
    }
}
