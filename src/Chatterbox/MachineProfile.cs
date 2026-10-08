using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Chatterbox.Stt;
using Microsoft.Win32;

namespace Chatterbox;

// One line describing the machine — CPU, threads, RAM, GPUs, Windows build,
// hardware tier, runtime — for the boot log and the speed check, so a
// report from another machine says what it ran on without anyone asking.
// Read from the registry once; nothing here touches the network.
public static class MachineProfile
{
    private static readonly Lazy<string> Cached = new(Build);

    public static string Describe() => Cached.Value;

    private static string Build()
    {
        var parts = new List<string>();
        try
        {
            var cpu = Cpu();
            if (cpu.Length > 0) parts.Add(cpu);
            parts.Add($"{Environment.ProcessorCount} threads");
            parts.Add($"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0:0} GB RAM");
            var gpus = Gpus();
            if (gpus.Count > 0) parts.Add("GPU: " + string.Join(", ", gpus));
            parts.Add(Os());
            parts.Add("tier " + SttHardwareTier.Label(SttHardwareTier.Detect()));
            parts.Add($".NET {Environment.Version} {RuntimeInformation.ProcessArchitecture}");
        }
        catch (Exception ex) { parts.Add("profile error: " + ex.Message); }
        return string.Join(" · ", parts);
    }

    private static string Cpu()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string ?? "").Trim();
        }
        catch { return ""; }
    }

    // Display adapters from the device class key. Entries without a memory
    // size are virtual displays (streaming or VR monitors) and are skipped.
    private static List<string> Gpus()
    {
        var list = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return list;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !int.TryParse(sub, out _)) continue;
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string desc || desc.Length == 0) continue;
                var mem = k.GetValue("HardwareInformation.qwMemorySize");
                if (mem == null) continue;
                long bytes = mem switch { long l => l, int i => i, _ => 0 };
                list.Add(bytes > 0 ? $"{desc} ({bytes / 1073741824.0:0} GB)" : desc);
            }
        }
        catch { }
        return list;
    }

    private static string Os()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = k?.GetValue("ProductName") as string ?? "Windows";
            var display = k?.GetValue("DisplayVersion") as string ?? "";
            int build = int.TryParse(k?.GetValue("CurrentBuild") as string, out var b) ? b : 0;
            int ubr = k?.GetValue("UBR") is int u ? u : 0;
            return OsLabel(product, display, build, ubr);
        }
        catch { return Environment.OSVersion.VersionString; }
    }

    // The registry still says "Windows 10" on Windows 11 — the build tells.
    internal static string OsLabel(string product, string display, int build, int ubr)
    {
        if (build >= 22000 && product.Contains("Windows 10", StringComparison.Ordinal))
            product = product.Replace("Windows 10", "Windows 11");
        var sb = new System.Text.StringBuilder(product);
        if (display.Length > 0) sb.Append(' ').Append(display);
        if (build > 0) sb.Append(" build ").Append(build).Append('.').Append(ubr);
        return sb.ToString();
    }
}
