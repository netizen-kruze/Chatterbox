using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Chatterbox.Stt;

// Which CUDA build of whisper.cpp this PC can run.
public enum CudaVariant
{
    None,     // no NVIDIA driver, or a driver / card neither build supports
    Cuda13,   // Turing or newer (compute capability 7.5+) with a driver that supports CUDA 13 (580+)
    Cuda12,   // older cards (GTX 900 / 10 series, compute 5.0+), or a driver that only supports CUDA 12
}

// What the NVIDIA driver says about itself and the first GPU. Asked of the
// driver's own nvcuda.dll (cuInit creates no context and allocates nothing
// on the card), once per process, with a time limit — a driver that hangs
// must not hang the app.
//
// Why it matters: CUDA 13 dropped every architecture before Turing, and it
// needs driver branch 580 or newer. A GTX 1080, or an RTX card on a 2023
// driver, loads the CUDA 13 runtime fine and then gets no device — Whisper
// silently runs on the CPU. Those PCs need the CUDA 12 build instead, and
// a driver older than CUDA 12 needs an update before anything can work.
public static class CudaProbe
{
    public sealed record Facts(bool DriverPresent, int DriverCuda, double? ComputeCapability, string? DeviceName, string? Problem);

    public const double Cuda13MinCompute = 7.5;
    public const double Cuda12MinCompute = 5.0;
    public const int Cuda13Api = 13000;
    public const int Cuda12Api = 12000;
    private const int ProbeTimeoutMs = 8000;

    private static readonly Lazy<Facts> Probed = new(Probe);
    public static Facts Current => Probed.Value;

    // CHATTERBOX_CUDA_BUILD=12 or 13 overrides the choice — the way out when
    // a driver misreports itself, and how the other build is tested on one PC.
    public const string OverrideVariable = "CHATTERBOX_CUDA_BUILD";

    private static readonly Lazy<(CudaVariant Variant, string Verdict)> Judged = new(() =>
    {
        var forced = Environment.GetEnvironmentVariable(OverrideVariable)?.Trim();
        var judged = Judge(Current);
        if (!Current.DriverPresent || (forced != "12" && forced != "13")) return judged;
        return (forced == "12" ? CudaVariant.Cuda12 : CudaVariant.Cuda13,
            $"CUDA {forced} build, forced by {OverrideVariable} (the driver's own answer: {judged.Verdict})");
    });
    public static CudaVariant Preferred => Judged.Value.Variant;
    public static string Verdict => Judged.Value.Verdict;

    // Can this PC run that build at all? (The CUDA 12 build also runs on
    // new cards, so an installed one stays usable after a driver update.)
    public static bool Usable(CudaVariant v) => v != CudaVariant.None && (v == Preferred || Usable(Current, v));

    internal static bool Usable(Facts f, CudaVariant v)
    {
        if (!f.DriverPresent || v == CudaVariant.None) return false;
        // Nothing known beyond "there is a driver": behave as before 1.7.4.
        if (f.DriverCuda == 0 && f.ComputeCapability == null) return v == CudaVariant.Cuda13;
        double cc = f.ComputeCapability ?? Cuda13MinCompute;
        int api = f.DriverCuda == 0 ? Cuda13Api : f.DriverCuda;
        return v == CudaVariant.Cuda13
            ? cc >= Cuda13MinCompute && api >= Cuda13Api
            : cc >= Cuda12MinCompute && api >= Cuda12Api;
    }

    internal static (CudaVariant Variant, string Verdict) Judge(Facts f)
    {
        if (!f.DriverPresent) return (CudaVariant.None, "no NVIDIA driver");
        string card = f.DeviceName ?? "NVIDIA card";
        string cc = f.ComputeCapability is { } c ? "compute " + c.ToString("0.0", CultureInfo.InvariantCulture) : "compute capability unknown";
        string api = f.DriverCuda > 0 ? $"driver supports CUDA {f.DriverCuda / 1000}.{f.DriverCuda % 1000 / 10}" : "driver's CUDA level unknown";
        string facts = $"{card}, {cc}, {api}";
        if (f.DriverCuda == 0 && f.ComputeCapability == null)
            return (CudaVariant.Cuda13, $"NVIDIA driver present, details unknown ({f.Problem ?? "probe gave nothing"}) — the CUDA 13 build is assumed");
        if (Usable(f, CudaVariant.Cuda13)) return (CudaVariant.Cuda13, $"CUDA 13 build ({facts})");
        if (Usable(f, CudaVariant.Cuda12))
            return (CudaVariant.Cuda12, f.ComputeCapability is { } c2 && c2 < Cuda13MinCompute
                ? $"CUDA 12 build — this card predates CUDA 13 ({facts})"
                : $"CUDA 12 build — the graphics driver is too old for CUDA 13, which needs driver 580 or newer ({facts})");
        if (f.ComputeCapability is { } c3 && c3 < Cuda12MinCompute)
            return (CudaVariant.None, $"this card is too old for GPU acceleration ({facts})");
        return (CudaVariant.None, $"the NVIDIA graphics driver is too old for GPU acceleration — update it ({facts})");
    }

    private static Facts Probe()
    {
        if (!OperatingSystem.IsWindows()) return new Facts(false, 0, null, null, null);
        if (!NativeLibrary.TryLoad("nvcuda.dll", out var handle)) return new Facts(false, 0, null, null, null);
        NativeLibrary.Free(handle);
        try
        {
            var task = Task.Run(Ask);
            return task.Wait(ProbeTimeoutMs) ? task.Result : new Facts(true, 0, null, null, "the driver did not answer in time");
        }
        catch (Exception ex)
        {
            return new Facts(true, 0, null, null, (ex.InnerException ?? ex).Message);
        }
    }

    private static Facts Ask()
    {
        int api = 0;
        try { if (cuDriverGetVersion(out int v) == 0) api = v; } catch (EntryPointNotFoundException) { }
        int rc = cuInit(0);
        if (rc != 0) return new Facts(true, api, null, null, $"cuInit returned {rc}");
        if (cuDeviceGetCount(out int count) != 0 || count < 1) return new Facts(true, api, null, null, "the driver reports no CUDA device");
        // Device 0 is the one whisper.cpp uses.
        if (cuDeviceGet(out int dev, 0) != 0) return new Facts(true, api, null, null, "device 0 could not be opened");
        double? cc = null;
        if (cuDeviceGetAttribute(out int major, 75, dev) == 0 && cuDeviceGetAttribute(out int minor, 76, dev) == 0)
            cc = major + minor / 10.0;
        string? name = null;
        var buf = new byte[128];
        if (cuDeviceGetName(buf, buf.Length, dev) == 0)
        {
            int n = Array.IndexOf(buf, (byte)0);
            name = Encoding.ASCII.GetString(buf, 0, n < 0 ? buf.Length : n).Trim();
            if (name.Length == 0) name = null;
        }
        return new Facts(true, api, cc, name, null);
    }

    [DllImport("nvcuda.dll")] private static extern int cuInit(uint flags);
    [DllImport("nvcuda.dll")] private static extern int cuDriverGetVersion(out int version);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGetCount(out int count);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGet(out int device, int ordinal);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGetAttribute(out int value, int attribute, int device);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGetName(byte[] name, int length, int device);
}
