using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Chatterbox.Stt;

namespace Chatterbox;

// <data dir>\boot.inprogress exists for the whole life of a run: written
// before the risky work (native extraction, engine loads, window creation)
// with the phase "boot", moved to "window" when the page connects, to
// "captions" while a session runs and back, and deleted only by a clean
// exit (the tray's Exit, the game closing in wrapper mode, an in-app
// restart, or Windows ending the session). Finding it at the next start
// means the previous run ended without one — it crashed, or was killed —
// and the phase says when: a start that never reached the window, an idle
// window, or captions in progress (a Whisper pass on a GPU that takes the
// process down is the classic). Which of the two it was, Windows knows: a
// crash leaves a record in the Application log, an end from outside leaves
// none (CrashRecord). A recorded crash is copied into error.log and makes
// that start a safe boot — no automatic captions until a human presses
// Start — and so does a start that never reached the window. A crash can
// no longer be silent, and cannot loop.
//
// An end from outside is the everyday case, not the exception: a companion
// app that starts Chatterbox with the game stops it with TerminateProcess
// when the game closes, usually with captions still running. That is not a
// crash, and it must never cost the next launch its auto-start.
public static class BootSentinel
{
    public const string PhaseBoot = "boot", PhaseWindow = "window", PhaseCaptions = "captions";

    // Tests point this at a temp file so they never touch the real marker.
    internal static string? PathOverride { get; set; }

    private static string FilePath => PathOverride ?? Path.Combine(SttPaths.DataDir, "boot.inprogress");
    private static readonly object Gate = new();
    private static string? _version;    // set by Arm: this process owns the marker
    private static string _startedAt = "";

    public sealed record Unfinished(string Version, DateTime StartedAt, int Pid, string Phase)
    {
        // "never reached the window" / "ended while captions were running" / …
        public string How => Phase switch
        {
            PhaseCaptions => "ended while captions were running",
            PhaseWindow => "ended without a clean exit while idle",
            _ => "never reached the window",
        };
        // Must the next start hold captions back? A start that never
        // reached the window: always, as since 1.3.0. Once the window was
        // up: only when Windows recorded a crash for that run
        // (crashRecorded: true = it did, false = it did not, null = the
        // record gave no answer). No answer counts as no crash: an end
        // during a run never held anything back before 1.7.2, and a real
        // crash loop is caught on its next round.
        public bool WantsSafeBoot(bool? crashRecorded) => Phase == PhaseBoot || crashRecorded == true;
    }

    // Records this start; returns the previous run if it never exited cleanly.
    public static Unfinished? Arm(string version)
    {
        Unfinished? previous = null;
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath)) previous = Parse(File.ReadAllText(FilePath));
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                _version = version;
                _startedAt = DateTime.Now.ToString("o");
                File.WriteAllText(FilePath, Line(PhaseBoot));
            }
            catch { /* diagnostics must never affect startup */ }
        }
        return previous;
    }

    // The run moved on (page connected, captions started or stopped).
    public static void Mark(string phase)
    {
        lock (Gate)
        {
            if (_version == null) return;   // not armed in this process (tests, the update helper)
            try { File.WriteAllText(FilePath, Line(phase)); } catch { }
        }
    }

    // A clean exit. Only this process's own marker is removed: a successor
    // started by an in-app restart has armed its own by now. It is also
    // final for this process: the controller is disposed after Main's Clear
    // and stops a running session there, and that Stop must not write the
    // marker back (the next start would report a crash that never was).
    public static void Clear()
    {
        lock (Gate)
        {
            _version = null;
            try
            {
                if (!File.Exists(FilePath)) return;
                var owner = Parse(File.ReadAllText(FilePath));
                if (owner.Pid == 0 || owner.Pid == Environment.ProcessId) File.Delete(FilePath);
            }
            catch { try { File.Delete(FilePath); } catch { } }
        }
    }

    private static string Line(string phase) => $"{_version}|{_startedAt}|{Environment.ProcessId}|{phase}";

    // A marker with unreadable contents still means an unfinished start.
    // Three fields is the format before 1.7.2 (no phase: a start).
    internal static Unfinished Parse(string text)
    {
        var parts = (text ?? "").Trim().Split('|');
        if (parts.Length is not (3 or 4)) return new Unfinished("?", DateTime.MinValue, 0, PhaseBoot);
        DateTime.TryParse(parts[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var at);
        int.TryParse(parts[2], out var pid);
        var phase = parts.Length == 4 && parts[3] is PhaseWindow or PhaseCaptions ? parts[3] : PhaseBoot;
        return new Unfinished(parts[0], at, pid, phase);
    }

    // Tests: forget that this process armed anything.
    internal static void ResetForTests() { lock (Gate) { _version = null; _startedAt = ""; } }
}

// Windows keeps the only record of how a process died: the Application
// log's "Application Error" (1000) and ".NET Runtime" (1026, with the
// managed stack) entries for a crash, and "Application Hang" (1002) for a
// window that stopped responding and was closed. After an unfinished run
// the log is asked for the entries about this app since that run started:
// their presence is what tells a crash from an end from outside (which
// leaves none), and they are copied into error.log, where a user finds
// them without knowing Event Viewer exists. Read through wevtutil (in
// every Windows) as XML — the event schema is language-neutral, unlike
// the text format whose labels follow the Windows display language.
public static class CrashRecord
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";
    private const int QueryTimeoutMs = 10_000;

    // What the record says about one run: the entries about this app —
    // none when Windows recorded no crash. When the record itself could
    // not be read, Problem says why and there are no entries.
    public sealed record Finding(IReadOnlyList<string> Events, string? Problem = null)
    {
        public bool Readable => Problem == null;
        // true = Windows recorded a crash, false = it recorded none,
        // null = no answer.
        public bool? Crashed => Readable ? Events.Count > 0 : null;
    }

    // The name Windows files this program's entries under: the running
    // exe's file name.
    private static string ExeName =>
        Path.GetFileName(Environment.ProcessPath) is { Length: > 0 } name ? name : "Chatterbox.exe";

    // Starts reading the record for the previous run (tens of
    // milliseconds, off the starting thread). The task never faults.
    public static System.Threading.Tasks.Task<Finding> FindAsync(BootSentinel.Unfinished previous) =>
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // A marker whose start time cannot be read: the last day.
                DateTime? startedUtc = previous.StartedAt == DateTime.MinValue ? null : previous.StartedAt.ToUniversalTime();
                var sinceUtc = startedUtc?.AddMinutes(-1) ?? DateTime.UtcNow.AddDays(-1);
                long windowMs = (long)Math.Clamp((DateTime.UtcNow - sinceUtc).TotalMilliseconds, 60_000, 7L * 24 * 3600 * 1000);
                return Read(Query(windowMs), ExeName, startedUtc);
            }
            catch (Exception ex)
            {
                return new Finding(Array.Empty<string>(), ex.Message);
            }
        });

    // The boot log's line about the previous run. finding is null when the
    // record had not answered by the time the start needed to know.
    public static string Describe(BootSentinel.Unfinished previous, Finding? finding, bool safeBoot)
    {
        string head = $"previous run ({previous.Version} started {previous.StartedAt:HH:mm:ss}, pid {previous.Pid}) {previous.How}";
        bool reachedWindow = previous.Phase != BootSentinel.PhaseBoot;
        string what = finding == null
            ? " — crashed or was killed; Windows' crash record did not answer in time"
            : finding.Crashed switch
            {
                true => " — Windows recorded a crash or hang for it (copied to error.log)",
                false => reachedWindow
                    ? " — Windows recorded no crash, so it was ended from outside (a companion app, Task Manager, a power loss)"
                    : " — crashed or was killed; Windows recorded no crash for it",
                null => $" — crashed or was killed; Windows' crash record could not be read ({finding.Problem})",
            };
        return head + what + (safeBoot
            ? ". SAFE BOOT: captions won't auto-start until Start is pressed"
            : "; captions auto-start as usual");
    }

    // Puts what was found where it belongs. A recorded crash goes to
    // error.log with its record. A start that never reached the window is
    // noted there even without one, as since 1.3.0. Anything else — a run
    // that was ended from outside once the window was up (every game exit,
    // with a companion app that stops Chatterbox with the game), or one
    // whose record could not be read — is no error and stays out of
    // error.log: the boot log's "previous run" line is its whole trace.
    public static void Report(BootSentinel.Unfinished previous, Finding finding)
    {
        try { if (Note(previous, finding) is { } note) ErrorLog.WriteNote("PreviousStart", note); }
        catch (Exception ex) { ErrorLog.WriteEntry("CrashRecord", ex); }
    }

    // The error.log note the previous run calls for, or null for none.
    internal static string? Note(BootSentinel.Unfinished previous, Finding finding)
    {
        string who = $"run of {previous.Version} started {previous.StartedAt:yyyy-MM-dd HH:mm:ss} (pid {previous.Pid}) {previous.How}";
        if (finding.Events.Count > 0)
            return who + "; Windows crash record:" + Environment.NewLine +
                   string.Join(Environment.NewLine + Environment.NewLine, finding.Events);
        if (previous.Phase != BootSentinel.PhaseBoot) return null;
        return who + (finding.Readable
            ? "; no Windows crash record found — it was probably closed or killed before the window loaded"
            : $"; Windows' crash record could not be read ({finding.Problem})");
    }

    private static string Query(long windowMs)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wevtutil.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("qe");
        psi.ArgumentList.Add("Application");
        psi.ArgumentList.Add("/q:*[System[(Provider[@Name='Application Error'] or Provider[@Name='Application Hang'] " +
                             $"or Provider[@Name='.NET Runtime']) and TimeCreated[timediff(@SystemTime) <= {windowMs}]]]");
        psi.ArgumentList.Add("/f:xml");
        psi.ArgumentList.Add("/rd:true");
        psi.ArgumentList.Add("/c:100");   // the run may have lasted hours: other apps' events count too
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("wevtutil did not start");
        // Both pipes are read while it runs: a query that never returns is
        // ended after its time instead of holding this thread, and one that
        // fails says why (on stderr, with nothing on stdout — which read on
        // its own would pass for "no crash recorded").
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(QueryTimeoutMs))
        {
            try { p.Kill(); } catch { }
            throw new TimeoutException($"wevtutil did not answer within {QueryTimeoutMs / 1000} s");
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"wevtutil failed with code {p.ExitCode}: {FirstLine(error.GetAwaiter().GetResult())}");
        return output.GetAwaiter().GetResult();
    }

    private static string FirstLine(string text)
    {
        var line = (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "no message";
        return line.Length <= 200 ? line : line[..200];
    }

    // wevtutil emits a bare sequence of <Event> elements. Keeps the entries
    // filed under this app — never one that merely mentions it: another
    // program's crash whose paths or stack name this app is not ours — and,
    // given the run's start, only those since then: an older one belongs to
    // an earlier run, and the start after that one reported it. Each is
    // rendered as "Provider (id) at time" plus the data fields — named
    // ones as name: value, the .NET Runtime's single text blob line by
    // line — trimmed to the lines that matter. The System block's computer
    // and account identifiers are never copied.
    internal static Finding Read(string xml, string exeName, DateTime? notBeforeUtc = null, int maxLinesPerEvent = 40)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(xml)) return new Finding(result);
        XDocument doc;
        try { doc = XDocument.Parse("<Events>" + xml + "</Events>"); }
        catch (Exception ex) { return new Finding(result, "unreadable output: " + ex.Message); }

        foreach (var ev in doc.Root!.Elements(Ns + "Event"))
        {
            var data = ev.Element(Ns + "EventData")?.Elements(Ns + "Data").ToList() ?? new List<XElement>();
            if (!Subject(data).Contains(exeName, StringComparison.OrdinalIgnoreCase)) continue;

            var sys = ev.Element(Ns + "System");
            var provider = sys?.Element(Ns + "Provider")?.Attribute("Name")?.Value ?? "?";
            var id = sys?.Element(Ns + "EventID")?.Value ?? "?";
            var time = sys?.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value ?? "?";
            if (notBeforeUtc is { } cut &&
                DateTime.TryParse(time, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at) &&
                at < cut) continue;
            var lines = new List<string> { $"{provider} ({id}) at {time}" };
            foreach (var d in data)
            {
                var value = d.Value.Trim();
                if (value.Length == 0) continue;
                var name = d.Attribute("Name")?.Value;
                if (name != null) lines.Add($"  {name}: {value}");
                else
                    foreach (var raw in value.Split('\n'))
                    {
                        var line = raw.Trim();
                        if (line.Length > 0) lines.Add("  " + line);
                    }
            }
            result.Add(string.Join(Environment.NewLine, lines.Take(maxLinesPerEvent).Select(l => "    " + l)));
        }
        return new Finding(result);
    }

    // The program an entry is filed under: the AppName field of a crash or
    // hang entry, or the "Application: …" line that opens the .NET
    // Runtime's text. (An entry without field names puts the program first.)
    private static string Subject(List<XElement> data)
    {
        var named = data.FirstOrDefault(d => string.Equals(d.Attribute("Name")?.Value, "AppName", StringComparison.OrdinalIgnoreCase));
        if (named != null) return named.Value.Trim();
        foreach (var d in data)
        {
            var value = d.Value.Trim();
            if (value.Length > 0) return value.Split('\n')[0].Trim();
        }
        return "";
    }
}
