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
        Assert.True(previous.WantsSafeBoot);
        Assert.Equal(Environment.ProcessId, previous.Pid);
    }

    [Fact]
    public void AnIdleWindowThatWasKilledIsReportedButDoesNotForceASafeBoot()
    {
        BootSentinel.Arm("1.7.2");
        BootSentinel.Mark(BootSentinel.PhaseWindow);
        BootSentinel.ResetForTests();
        var previous = BootSentinel.Arm("1.7.2");
        Assert.Equal(BootSentinel.PhaseWindow, previous!.Phase);
        Assert.False(previous.WantsSafeBoot);
        Assert.Contains("idle", previous.How);
    }

    [Fact]
    public void AStartThatNeverReachedTheWindowStillReadsAsBefore()
    {
        BootSentinel.Arm("1.7.2");
        BootSentinel.ResetForTests();
        var previous = BootSentinel.Arm("1.7.2");
        Assert.Equal(BootSentinel.PhaseBoot, previous!.Phase);
        Assert.Equal("never reached the window", previous.How);
        Assert.True(previous.WantsSafeBoot);
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
        var events = CrashRecord.FilterXml(Sample(5), "Chatterbox");
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
        var events = CrashRecord.FilterXml(Sample(60), "Chatterbox", maxLinesPerEvent: 20);
        var runtime = events[1];
        Assert.Contains("at Frame0()", runtime);
        Assert.DoesNotContain("at Frame40()", runtime);
        Assert.True(runtime.Split('\n').Length <= 20);
    }

    [Fact]
    public void EmptyOrUnreadableOutputIsHandled()
    {
        Assert.Empty(CrashRecord.FilterXml("", "Chatterbox"));
        Assert.Empty(CrashRecord.FilterXml(Ev + "<System/><EventData><Data Name='AppName'>Other.exe</Data></EventData></Event>", "Chatterbox"));
        var broken = CrashRecord.FilterXml("<Event><unclosed>", "Chatterbox");
        Assert.Single(broken);
        Assert.Contains("unreadable", broken[0]);
    }
}
