using System;
using System.Runtime.InteropServices;

namespace Chatterbox.Stt;

public enum SttTier
{
    Gpu,       // NVIDIA CUDA available — whisper runs GPU-accelerated
    CpuHigh,   // strong CPU — base.en comfortably real-time
    CpuLow,    // modest CPU — tiny.en
    CpuMinimal, // very weak hardware — smallest quantized model only
}

// Hardware tier autodetect: CUDA probe -> GPU tier, else CPU tier by
// cores/RAM. Classify() is pure for unit testing; Detect() feeds it live
// machine facts once per process (hardware does not change under us, and
// the probe loads a driver DLL).
public static class SttHardwareTier
{
    private static readonly Lazy<SttTier> Detected = new(() =>
        Classify(HasNvidiaDriver(), Environment.ProcessorCount, TotalRamGB()));

    public static SttTier Detect() => Detected.Value;

    public static SttTier Classify(bool hasCuda, int cores, double ramGB)
    {
        if (hasCuda && ramGB >= 8) return SttTier.Gpu;
        if (cores >= 8 && ramGB >= 8) return SttTier.CpuHigh;
        if (cores >= 4 && ramGB >= 4) return SttTier.CpuLow;
        return SttTier.CpuMinimal;
    }

    // Recommended whisper model id per tier; "" = no recommendation (the
    // host maps it to the smallest quantized model). GPU tier gets
    // large-v3-turbo (q5): near large-v3 accuracy, still far faster than
    // realtime on any CUDA card that clears the tier bar.
    public static string RecommendedModelId(SttTier tier) => tier switch
    {
        SttTier.Gpu => "large-v3-turbo-q5",
        SttTier.CpuHigh => "base.en",
        SttTier.CpuLow => "tiny.en",
        _ => "",
    };

    public static string Label(SttTier tier) => tier switch
    {
        SttTier.Gpu => "GPU (NVIDIA)",
        SttTier.CpuHigh => "CPU (high)",
        SttTier.CpuLow => "CPU (low)",
        _ => "Minimal",
    };

    // The NVIDIA driver ships nvcuda.dll; its presence is what whisper.cpp's
    // CUDA runtime needs to even attempt GPU init (which itself falls back to
    // CPU when no usable device exists).
    private static bool HasNvidiaDriver()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (NativeLibrary.TryLoad("nvcuda.dll", out var handle))
        {
            NativeLibrary.Free(handle);
            return true;
        }
        return false;
    }

    private static double TotalRamGB() =>
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0;
}
