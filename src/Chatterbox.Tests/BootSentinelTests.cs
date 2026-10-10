using Chatterbox;
using Xunit;

namespace Chatterbox.Tests;

// The boot sentinel: a marker that exists only during a start, so the next
// start can tell that the previous one never reached the window.
public class BootSentinelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chatterbox-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public BootSentinelTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "boot.inprogress");
        BootSentinel.PathOverride = _path;
        BootSentinel.ResetForTests();
    }

    public void Dispose()
    {
        BootSentinel.PathOverride = null;
        BootSentinel.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ThePhaseFollowsTheRunAndIsReportedNextTime()
    {
        Assert.Null(BootSentinel.Arm("1.7.2"));
        Assert.EndsWith("|boot", File.ReadAllText(_path));
        BootSentinel.Mark(BootSentinel.PhaseWindow);
        Assert.EndsWith("|window", File.ReadAllText(_path));
        BootSentinel.Mark(BootSentinel.PhaseCaptions);
        Assert.EndsWith("|captions", File.ReadAllText(_path));

        // "The next start": the run above died with captions running.
        BootSentinel.ResetForTests();
        var previous = BootSentinel.Arm("1.7.2");
        Assert.NotNull(previous);
        Assert.Equal(BootSentinel.PhaseCaptions, previous!.Phase);
        Assert.Equal("ended while captions were running", previous.How);
        Assert.Equal(Environment.ProcessId, previous.Pid);
        // A crash during captions holds the next start's captions back...
        Assert.True(previous.WantsSafeBoot(crashRecorded: true));
    }

    [Fact]
    public void BeingEndedFromOutsideDuringCaptionsIsNotACrash()
    {
        // The everyday end on a machine where a companion app starts
        // Chatterbox with the game: TerminateProcess when the game closes,
        // captions still running. Windows records no crash for that, and
        // the next launch must auto-start as usual.
        BootSentinel.Arm("1.7.2");
        BootSentinel.Mark(BootSentinel.PhaseWindow);
        BootSentinel.Mark(BootSentinel.PhaseCaptions);
        BootSentinel.ResetForTests();
        var previous = BootSentinel.Arm("1.7.2");
        Assert.Equal(BootSentinel.PhaseCaptions, previous!.Phase);
        Assert.False(previous.WantsSafeBoot(crashRecorded: false));
        // No answer from the record is not evidence of a crash either.
        Assert.False(previous.WantsSafeBoot(crashRecorded: null));
    }

    [Fact]
    public void AnIdleWindowThatWasKilledIsReportedButDoesNotForceASafeBoot()
    {
        BootSentinel.Arm("1.7.2");
        BootSentinel.Mark(BootSentinel.PhaseWindow);
        BootSentinel.ResetForTests();
        var previous = BootSentinel.Arm("1.7.2");
        Assert.Equal(BootSentinel.PhaseWindow, previous!.Phase);
        Assert.False(previous.WantsSafeBoot(crashRecorded: false));
        Assert.False(previous.WantsSafeBoot(crashRecorded: null));
        Assert.Contains("idle", previous.How);
        // A recorded crash is another matter: the engine an auto-start
        // loads is loaded while the marker still says "window".
        Assert.True(previous.WantsSafeBoot(crashRecorded: true));
    }

    [Fact]
    public void AStartThatNeverReachedTheWindowStillReadsAsBefore()
    {
        BootSentinel.Arm("1.7.2");
        BootSentinel.ResetForTests();
        var previous = BootSentinel.Arm("1.7.2");
        Assert.Equal(BootSentinel.PhaseBoot, previous!.Phase);
        Assert.Equal("never reached the window", previous.How);
        // With or without a crash record, as since 1.3.0.
        Assert.True(previous.WantsSafeBoot(crashRecorded: true));
        Assert.True(previous.WantsSafeBoot(crashRecorded: false));
        Assert.True(previous.WantsSafeBoot(crashRecorded: null));
    }

    [Fact]
    public void AMarkerFromBeforeThePhasesIsAStart()
    {
        File.WriteAllText(_path, "1.7.1|2026-10-08T20:00:00.0000000+00:00|4242");
        var previous = BootSentinel.Arm("1.7.2");
        Assert.Equal("1.7.1", previous!.Version);
        Assert.Equal(4242, previous.Pid);
        Assert.Equal(BootSentinel.PhaseBoot, previous.Phase);
    }

    [Fact]
    public void ClearRemovesOnlyThisProcessesOwnMarker()
    {
        // A successor's marker (another pid) must survive the predecessor's late Clear.
        File.WriteAllText(_path, "1.7.2|2026-10-09T05:00:00.0000000+00:00|99999|window");
        BootSentinel.Clear();
        Assert.True(File.Exists(_path));
        BootSentinel.Arm("1.7.2");               // ours now
        BootSentinel.Clear();
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void AStopAfterTheCleanExitDoesNotBringTheMarkerBack()
    {
        // Main clears the marker, then the usings dispose the controller,
        // whose Stop marks the window phase for a session that was running.
        BootSentinel.Arm("1.7.2");
        BootSentinel.Mark(BootSentinel.PhaseCaptions);
        BootSentinel.Clear();
        BootSentinel.Mark(BootSentinel.PhaseWindow);
        Assert.False(File.Exists(_path));
        Assert.Null(BootSentinel.Arm("1.7.2"));   // the next start: nothing to report
    }

    [Fact]
    public void MarkWithoutArmTouchesNothing()
    {
        BootSentinel.Mark(BootSentinel.PhaseCaptions);   // --bench, tests: never armed
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void FirstStartHasNothingToReportAndLeavesAMarker()
    {
        Assert.Null(BootSentinel.Arm("1.3.0"));
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void AClearedStartIsNotReported()
    {
        BootSentinel.Arm("1.3.0");
        BootSentinel.Clear();
        Assert.False(File.Exists(_path));
        Assert.Null(BootSentinel.Arm("1.3.0"));
    }

    [Fact]
    public void AnUnclearedStartIsReportedWithItsDetails()
    {
        BootSentinel.Arm("1.2.9");
        var previous = BootSentinel.Arm("1.3.0");
        Assert.NotNull(previous);
        Assert.Equal("1.2.9", previous!.Version);
        Assert.Equal(Environment.ProcessId, previous.Pid);
        Assert.True((DateTime.Now - previous.StartedAt).TotalMinutes < 1);
        Assert.True(File.Exists(_path)); // and this start is armed in turn
    }

    [Fact]
    public void AGarbledMarkerStillCountsAsUnfinished()
    {
        File.WriteAllText(_path, "??");
        var previous = BootSentinel.Arm("1.3.0");
        Assert.NotNull(previous);
        Assert.Equal("?", previous!.Version);
        Assert.Equal(0, previous.Pid);
    }
}

// The Windows crash record, read from wevtutil's XML (language-neutral).
public class CrashRecordTests
{
    private const string Ev = "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>";
    private const string Exe = "Chatterbox.exe";

    private static string Sample(int stackFrames) =>
        Ev + "<System><Provider Name='Application Error'/><EventID>1000</EventID>" +
        "<TimeCreated SystemTime='2026-09-05T05:36:58.4367430Z'/><Computer>BOX</Computer>" +
        "<Security UserID='S-1-5-21-1-2-3-1001'/></System><EventData>" +
        "<Data Name='AppName'>Chatterbox.exe</Data><Data Name='AppVersion'>1.2.4.0</Data>" +
        "<Data Name='ExceptionCode'>c0000005</Data><Data Name='PackageFullName'></Data></EventData></Event>" +
        Ev + "<System><Provider Name='Application Error'/><EventID>1000</EventID>" +
        "<TimeCreated SystemTime='2026-09-05T05:30:00.0000000Z'/><Computer>BOX</Computer></System>" +
        "<EventData><Data Name='AppName'>Other.exe</Data></EventData></Event>" +
        Ev + "<System><Provider Name='.NET Runtime'/><EventID Qualifiers='0'>1026</EventID>" +
        "<TimeCreated SystemTime='2026-09-05T05:36:58.2272039Z'/><Computer>BOX</Computer></System>" +
        "<EventData><Data>Application: Chatterbox.exe\nDescription: The process was terminated due to an unhandled exception.\nStack:\n" +
        string.Concat(Enumerable.Range(0, stackFrames).Select(i => $"   at Frame{i}()\n")) +
        "</Data></EventData></Event>";

    [Fact]
    public void KeepsOnlyThisAppsEventsWithoutMachineOrAccountIdentifiers()
    {
        var events = CrashRecord.Read(Sample(5), Exe).Events;
        Assert.Equal(2, events.Count);
        Assert.Contains("Application Error (1000) at 2026-09-05T05:36:58.4367430Z", events[0]);
        Assert.Contains("AppName: Chatterbox.exe", events[0]);
        Assert.Contains("ExceptionCode: c0000005", events[0]);
        Assert.DoesNotContain("PackageFullName", events[0]);   // empty fields are dropped
        var all = string.Join("\n", events);
        Assert.DoesNotContain("Other.exe", all);
        Assert.DoesNotContain("BOX", all);
        Assert.DoesNotContain("S-1-5", all);
        Assert.Contains(".NET Runtime (1026)", events[1]);
        Assert.Contains("at Frame0()", events[1]);
    }

    [Fact]
    public void TrimsALongStackToTheFirstFrames()
    {
        var events = CrashRecord.Read(Sample(60), Exe, maxLinesPerEvent: 20).Events;
        var runtime = events[1];
        Assert.Contains("at Frame0()", runtime);
        Assert.DoesNotContain("at Frame40()", runtime);
        Assert.True(runtime.Split('\n').Length <= 20);
    }

    [Fact]
    public void EmptyOrUnreadableOutputIsHandled()
    {
        Assert.Empty(CrashRecord.Read("", Exe).Events);
        Assert.Empty(CrashRecord.Read(Ev + "<System/><EventData><Data Name='AppName'>Other.exe</Data></EventData></Event>", Exe).Events);
        var broken = CrashRecord.Read("<Event><unclosed>", Exe);
        Assert.Empty(broken.Events);
        Assert.Contains("unreadable", broken.Problem);
    }

    [Fact]
    public void AnotherProgramsCrashThatMentionsThisAppIsNotOurs()
    {
        // A build tool that crashed in a folder named after the app, and a
        // test host whose stack runs through the app's code: both name it,
        // neither is it.
        string others =
            Ev + "<System><Provider Name='Application Error'/><EventID>1000</EventID>" +
            "<TimeCreated SystemTime='2026-09-05T05:36:58.0000000Z'/></System><EventData>" +
            "<Data Name='AppName'>dotnet.exe</Data>" +
            @"<Data Name='AppPath'>C:\Projects\Chatterbox\tools\dotnet.exe</Data></EventData></Event>" +
            Ev + "<System><Provider Name='.NET Runtime'/><EventID>1026</EventID>" +
            "<TimeCreated SystemTime='2026-09-05T05:36:58.0000000Z'/></System><EventData>" +
            "<Data>Application: testhost.exe\nStack:\n   at Chatterbox.Program.Main()\n   in Chatterbox.exe\n</Data></EventData></Event>";
        var finding = CrashRecord.Read(others, Exe);
        Assert.Empty(finding.Events);
        Assert.False(finding.Crashed);
        // The update's helper runs as the staged copy of the app: that is it.
        string helper =
            Ev + "<System><Provider Name='Application Error'/><EventID>1000</EventID>" +
            "<TimeCreated SystemTime='2026-09-05T05:36:58.0000000Z'/></System><EventData>" +
            "<Data Name='AppName'>Chatterbox.exe.new</Data></EventData></Event>";
        Assert.True(CrashRecord.Read(helper, Exe).Crashed);
        // An entry without field names puts the program first.
        string unnamed =
            Ev + "<System><Provider Name='Application Error'/><EventID>1000</EventID>" +
            "<TimeCreated SystemTime='2026-09-05T05:36:58.0000000Z'/></System><EventData>" +
            "<Data>chatterbox.exe</Data><Data>1.2.4.0</Data><Data>c0000005</Data></EventData></Event>";
        Assert.True(CrashRecord.Read(unnamed, Exe).Crashed);
    }

    [Fact]
    public void ACrashFromBeforeTheRunStartedBelongsToAnEarlierRun()
    {
        // The sample's entries are stamped 05:36:58.227 and 05:36:58.436 UTC.
        static DateTime Utc(int minute, int second, int ms = 0) => new(2026, 9, 5, 5, minute, second, ms, DateTimeKind.Utc);
        // A run that started after them did not cause them: it was ended
        // from outside, however recently the app had crashed before.
        var later = CrashRecord.Read(Sample(3), Exe, Utc(36, 59));
        Assert.Empty(later.Events);
        Assert.False(later.Crashed);
        // A run that started before them did.
        var earlier = CrashRecord.Read(Sample(3), Exe, Utc(30, 0));
        Assert.Equal(2, earlier.Events.Count);
        Assert.True(earlier.Crashed);
        // The cut is exact, entry by entry.
        var between = CrashRecord.Read(Sample(3), Exe, Utc(36, 58, 300));
        Assert.Single(between.Events);
        Assert.Contains("Application Error (1000)", between.Events[0]);
        // An entry whose time cannot be read is kept rather than dropped.
        string undated = Ev + "<System><Provider Name='Application Error'/><EventID>1000</EventID></System>" +
                         "<EventData><Data Name='AppName'>Chatterbox.exe</Data></EventData></Event>";
        Assert.True(CrashRecord.Read(undated, Exe, Utc(36, 59)).Crashed);
    }

    [Fact]
    public void AWindowThatStoppedRespondingAndWasClosedCountsLikeACrash()
    {
        string hang =
            Ev + "<System><Provider Name='Application Hang'/><EventID>1002</EventID>" +
            "<TimeCreated SystemTime='2026-09-05T05:36:58.0000000Z'/><Computer>BOX</Computer></System><EventData>" +
            "<Data Name='AppName'>Chatterbox.exe</Data><Data Name='AppVersion'>1.7.2.0</Data>" +
            "<Data Name='HangType'>Top level window is idle</Data></EventData></Event>";
        var finding = CrashRecord.Read(hang, Exe);
        Assert.True(finding.Crashed);
        Assert.Contains("Application Hang (1002)", finding.Events[0]);
        Assert.Contains("HangType: Top level window is idle", finding.Events[0]);
        Assert.True(Run(BootSentinel.PhaseCaptions).WantsSafeBoot(finding.Crashed));
    }

    // ── crash, or ended from outside? ──

    private static BootSentinel.Unfinished Run(string phase) =>
        new("1.7.2", new DateTime(2026, 10, 9, 21, 31, 4), 4242, phase);

    [Fact]
    public void TheRecordSaysCrashNoCrashOrNothing()
    {
        Assert.True(CrashRecord.Read(Sample(3), Exe).Crashed);
        // Windows recorded nothing, or only other apps' crashes: no crash.
        Assert.False(CrashRecord.Read("", Exe).Crashed);
        Assert.False(CrashRecord.Read(Ev + "<System/><EventData><Data Name='AppName'>Other.exe</Data></EventData></Event>", Exe).Crashed);
        // Output that cannot be parsed is no answer — not a crash, and it
        // says why.
        var broken = CrashRecord.Read("<Event><unclosed>", Exe);
        Assert.Null(broken.Crashed);
        Assert.False(broken.Readable);
        Assert.Empty(broken.Events);
        Assert.Contains("unreadable", broken.Problem);
        // So is a record that could not be asked at all.
        var failed = new CrashRecord.Finding(Array.Empty<string>(), "wevtutil failed with code 5: Access is denied.");
        Assert.Null(failed.Crashed);
    }

    [Fact]
    public void ARunEndedFromOutsideBootsNormallyAndStaysOutOfTheErrorLog()
    {
        // What a companion app's TerminateProcess leaves behind: the marker
        // in the captions phase and nothing in the Application log.
        var previous = Run(BootSentinel.PhaseCaptions);
        var finding = CrashRecord.Read("", Exe);
        bool safeBoot = previous.WantsSafeBoot(finding.Crashed);
        Assert.False(safeBoot);
        Assert.Null(CrashRecord.Note(previous, finding));
        var line = CrashRecord.Describe(previous, finding, safeBoot);
        Assert.StartsWith("previous run (1.7.2 started 21:31:04, pid 4242) ended while captions were running", line);
        Assert.Contains("ended from outside", line);
        Assert.Contains("captions auto-start as usual", line);
        Assert.DoesNotContain("SAFE BOOT", line);
        // The same while idle.
        var idle = Run(BootSentinel.PhaseWindow);
        Assert.Null(CrashRecord.Note(idle, finding));
        Assert.DoesNotContain("SAFE BOOT", CrashRecord.Describe(idle, finding, idle.WantsSafeBoot(finding.Crashed)));
    }

    [Fact]
    public void ARecordedCrashIsASafeBootWithItsRecordInTheErrorLog()
    {
        foreach (var phase in new[] { BootSentinel.PhaseCaptions, BootSentinel.PhaseWindow, BootSentinel.PhaseBoot })
        {
            var previous = Run(phase);
            var finding = CrashRecord.Read(Sample(3), Exe);
            bool safeBoot = previous.WantsSafeBoot(finding.Crashed);
            Assert.True(safeBoot, phase);
            var note = CrashRecord.Note(previous, finding);
            Assert.NotNull(note);
            Assert.Contains("Windows crash record:", note);
            Assert.Contains("ExceptionCode: c0000005", note);
            var line = CrashRecord.Describe(previous, finding, safeBoot);
            Assert.Contains("Windows recorded a crash", line);
            Assert.Contains("SAFE BOOT", line);
        }
    }

    [Fact]
    public void AStartThatNeverReachedTheWindowIsNotedEvenWithoutARecord()
    {
        var previous = Run(BootSentinel.PhaseBoot);
        var finding = CrashRecord.Read("", Exe);
        Assert.True(previous.WantsSafeBoot(finding.Crashed));
        Assert.Contains("no Windows crash record found", CrashRecord.Note(previous, finding));
        var line = CrashRecord.Describe(previous, finding, safeBoot: true);
        Assert.Contains("previous run (", line);
        Assert.Contains("never reached the window", line);
        Assert.Contains("SAFE BOOT", line);
    }

    [Fact]
    public void ARecordThatGivesNoAnswerNeverHoldsARunningAppBack()
    {
        var previous = Run(BootSentinel.PhaseCaptions);
        // Not answered in time: decided without it, and said so.
        Assert.False(previous.WantsSafeBoot(null));
        Assert.Contains("did not answer in time", CrashRecord.Describe(previous, null, safeBoot: false));
        // Answered, but unreadable: no safe boot, the boot log says why,
        // and error.log is left alone — a machine whose record can never
        // be read would otherwise get a note at every start after a kill.
        var broken = CrashRecord.Read("<Event><unclosed>", Exe);
        Assert.False(previous.WantsSafeBoot(broken.Crashed));
        var line = CrashRecord.Describe(previous, broken, safeBoot: false);
        Assert.Contains("could not be read (unreadable output:", line);
        Assert.Contains("captions auto-start as usual", line);
        Assert.Null(CrashRecord.Note(previous, broken));
        // A start that never reached the window is noted there as ever,
        // with the reason in place of the record.
        var boot = Run(BootSentinel.PhaseBoot);
        Assert.True(boot.WantsSafeBoot(broken.Crashed));
        Assert.Contains("could not be read (unreadable output:", CrashRecord.Note(boot, broken));
    }
}
