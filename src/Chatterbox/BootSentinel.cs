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
// process down is the classic). That start gets Windows' crash record
// copied into error.log (CrashRecord) and, unless the window was merely
// idle, a safe boot: no automatic captions until a human presses Start. A
// crash can no longer be silent, and cannot loop.
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
        // An idle window that was killed is nothing to guard against; the
        // other two are the crash loops the safe boot exists for.
        public bool WantsSafeBoot => Phase != PhaseWindow;
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

// Windows keeps the only record of a native crash: the Application log's
// "Application Error" (1000) and ".NET Runtime" (1026, with the managed
// stack) events. After an unfinished run, the entries naming this app
// are copied into error.log, where a user finds them without knowing Event
// Viewer exists. Read through wevtutil (in every Windows) as XML — the
// event schema is language-neutral, unlike the text format whose labels
// follow the Windows display language.
public static class CrashRecord
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    public static void CollectInBackground(BootSentinel.Unfinished previous)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var since = previous.StartedAt == DateTime.MinValue
                    ? DateTime.Now.AddDays(-1)
                    : previous.StartedAt.AddMinutes(-1);
                long windowMs = (long)Math.Clamp((DateTime.Now - since).TotalMilliseconds, 60_000, 7L * 24 * 3600 * 1000);
                var events = FilterXml(Query(windowMs), "Chatterbox");
                string who = $"run of {previous.Version} started {previous.StartedAt:yyyy-MM-dd HH:mm:ss} (pid {previous.Pid}) {previous.How}";
                ErrorLog.WriteNote("PreviousStart", events.Count == 0
                    ? who + "; no Windows crash record found — it was probably ended from outside " +
                      "(Task Manager, a companion app) or the power went"
                    : who + "; Windows crash record:" + Environment.NewLine +
                      string.Join(Environment.NewLine + Environment.NewLine, events));
            }
            catch (Exception ex) { ErrorLog.WriteEntry("CrashRecord", ex); }
        });
    }

    private static string Query(long windowMs)
    {
        var psi = new ProcessStartInfo("wevtutil.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("qe");
        psi.ArgumentList.Add("Application");
        psi.ArgumentList.Add("/q:*[System[(Provider[@Name='Application Error'] or Provider[@Name='.NET Runtime']) " +
                             $"and TimeCreated[timediff(@SystemTime) <= {windowMs}]]]");
        psi.ArgumentList.Add("/f:xml");
        psi.ArgumentList.Add("/rd:true");
        psi.ArgumentList.Add("/c:100");   // the run may have lasted hours: other apps' events count too
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("wevtutil did not start");
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } }
        return output;
    }

    // wevtutil emits a bare sequence of <Event> elements. Keeps the events
    // whose data names the app, as "Provider (id) at time" plus the data
    // fields — named ones as name: value, the .NET Runtime's single text
    // blob line by line — trimmed to the lines that matter. The System
    // block's computer and account identifiers are never copied.
    internal static List<string> FilterXml(string xml, string appName, int maxLinesPerEvent = 40)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(xml)) return result;
        XDocument doc;
        try { doc = XDocument.Parse("<Events>" + xml + "</Events>"); }
        catch (Exception ex)
        {
            result.Add("    (crash record unreadable: " + ex.Message + ")");
            return result;
        }

        foreach (var ev in doc.Root!.Elements(Ns + "Event"))
        {
            var data = ev.Element(Ns + "EventData")?.Elements(Ns + "Data").ToList() ?? new List<XElement>();
            if (!data.Any(d => d.Value.Contains(appName, StringComparison.OrdinalIgnoreCase))) continue;

            var sys = ev.Element(Ns + "System");
            var provider = sys?.Element(Ns + "Provider")?.Attribute("Name")?.Value ?? "?";
            var id = sys?.Element(Ns + "EventID")?.Value ?? "?";
            var time = sys?.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value ?? "?";
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
        return result;
    }
}
