using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// Microphone re-resolution after Windows renumbers devices: exact name
// wins, a clipped WinMM name still matches its longer saved form, a saved
// index is honored only when no name is known, and every dead end is the
// Windows default input (-1).
public class SttAudioDevicesTests
{
    private static readonly string[] Names =
    {
        "Microphone (Realtek Audio)",
        "Headset Microphone (Arctis No",   // WinMM clips product names at 31 chars
        "Line In (Realtek Audio)",
    };

    [Fact]
    public void ExactNameWinsRegardlessOfSavedIndex()
    {
        Assert.Equal(2, SttAudioDevices.ResolveAmong(Names, 0, "Line In (Realtek Audio)"));
    }

    [Fact]
    public void AClippedReportedNameMatchesTheLongerSavedName()
    {
        Assert.Equal(1, SttAudioDevices.ResolveAmong(Names, 0, "Headset Microphone (Arctis Nova Pro Wireless)"));
    }

    [Fact]
    public void WithoutANameTheIndexIsHonoredWhileInRange()
    {
        Assert.Equal(2, SttAudioDevices.ResolveAmong(Names, 2, null));
        Assert.Equal(-1, SttAudioDevices.ResolveAmong(Names, 7, ""));
    }

    [Fact]
    public void AnUnknownNameFallsBackToTheWindowsDefault()
    {
        Assert.Equal(-1, SttAudioDevices.ResolveAmong(Names, 1, "Some USB Mic that left"));
        Assert.Equal(-1, SttAudioDevices.ResolveAmong(Array.Empty<string>(), 0, "anything"));
    }
}
