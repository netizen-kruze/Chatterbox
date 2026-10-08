using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Chatterbox.Stt;

namespace Chatterbox;

// <data dir>\boot.inprogress exists only while a start is under way:
// written before the risky work (native extraction, engine loads, window
// creation) and deleted the moment the page connects. Finding it at the
// next start means the previous start never reached the window — it
// crashed, or was killed — so that start gets Windows' crash record copied
// into error.log (CrashRecord) and a safe boot: no automatic captions until
// a human presses Start. A crash can no longer be silent, and cannot loop.
public static class BootSentinel
{
    // Tests point this at a temp file so they never touch the real marker.
    internal static string? PathOverride { get; set; }

    private static string FilePath => PathOverride ?? Path.Combine(SttPaths.DataDir, "boot.inprogress");

    public sealed record Unfinished(string Version, DateTime StartedAt, int Pid);

    // Records this start; returns the previous start if it never finished.
    public static Unfinished? Arm(string version)
    {
        Unfinished? previous = null;
        try
        {
            if (File.Exists(FilePath)) previous = Parse(File.ReadAllText(FilePath));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, $"{version}|{DateTime.Now:o}|{Environment.ProcessId}");
        }
        catch { /* diagnostics must never affect startup */ }
        return previous;
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch { }
    }

    // A marker with unreadable contents still means an unfinished start.
    internal static Unfinished Parse(string text)
    {
        var parts = (text ?? "").Trim().Split('|');
        if (parts.Length != 3) return new Unfinished("?", DateTime.MinValue, 0);
        DateTime.TryParse(parts[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var at);
        int.TryParse(parts[2], out var pid);
        return new Unfinished(parts[0], at, pid);
    }
}

// Windows keeps the only record of a native crash: the Application log's
// "Application Error" (1000) and ".NET Runtime" (1026, with the managed
// stack) events. After an unfinished start, the entries naming this app
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
                string who = $"start of {previous.Version} at {previous.StartedAt:yyyy-MM-dd HH:mm:ss} (pid {previous.Pid}) never reached the window";
                ErrorLog.WriteNote("PreviousStart", events.Count == 0
                    ? who + "; no Windows crash record found — it was probably closed or killed before the window loaded"
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
        psi.ArgumentList.Add("/c:30");
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
