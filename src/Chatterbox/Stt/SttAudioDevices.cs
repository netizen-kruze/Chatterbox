using System;

namespace Chatterbox.Stt;

// WinMM input devices. A microphone choice is remembered as index + name
// and re-resolved every time it's used, because Windows renumbers WinMM
// devices whenever hardware comes or goes — the name is what actually
// identifies the microphone. WaveInEvent treats device number -1 as "the
// Windows default input", which is also our answer when nothing matches.
public static class SttAudioDevices
{
    public static string[] GetInputNames()
    {
        try
        {
            var names = new string[NAudio.Wave.WaveInEvent.DeviceCount];
            for (int device = 0; device < names.Length; device++)
                names[device] = NAudio.Wave.WaveInEvent.GetCapabilities(device).ProductName ?? "";
            return names;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static string InputNameAt(int index)
    {
        var names = GetInputNames();
        return index >= 0 && index < names.Length ? names[index] : "";
    }

    public static int ResolveInput(int savedIndex, string? savedName) =>
        ResolveAmong(GetInputNames(), savedIndex, savedName);

    // Single scored pass: an exact name match wins outright; failing that,
    // the best prefix match — WinMM reports product names clipped to 31
    // characters, so a full name saved from elsewhere can be longer than
    // what enumeration returns. With no name saved, a still-in-range index
    // is honored; every dead end resolves to the Windows default (-1).
    internal static int ResolveAmong(string[] names, int savedIndex, string? savedName)
    {
        if (string.IsNullOrEmpty(savedName))
            return savedIndex >= 0 && savedIndex < names.Length ? savedIndex : -1;

        int best = -1, bestRank = 0;
        for (int device = 0; device < names.Length; device++)
        {
            int rank = Rank(names[device], savedName);
            if (rank <= bestRank) continue;
            best = device;
            bestRank = rank;
            if (rank == 2) break;
        }
        return best;
    }

    private static int Rank(string reported, string saved)
    {
        if (reported.Length == 0) return 0;
        if (string.Equals(reported, saved, StringComparison.Ordinal)) return 2;
        return saved.StartsWith(reported, StringComparison.Ordinal) ? 1 : 0;
    }
}
