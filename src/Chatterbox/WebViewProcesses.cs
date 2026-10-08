using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Chatterbox;

// The WebView2 browser process (msedgewebview2.exe) belongs to the host
// that created it and outlives that host by a moment. Across an in-app
// restart the old host must be gone, browser and all, before the new one
// attaches — a new window that attaches to a browser on its way out goes
// black with it. The parent id of a browser process survives its parent's
// exit, so an exited host's leftovers can still be found.
internal static class WebViewProcesses
{
    private const string BrowserName = "msedgewebview2";

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2a;
        public IntPtr Reserved2b;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        ref ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);

    private static int ParentPid(Process p)
    {
        try
        {
            var info = new ProcessBasicInformation();
            int status = NtQueryInformationProcess(p.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
            return status == 0 ? (int)info.InheritedFromUniqueProcessId : -1;
        }
        catch
        {
            return -1;   // not ours to inspect — treat as not owned
        }
    }

    // Any browser process started by the given host, running or exiting.
    public static bool AnyOwnedBy(int hostPid)
    {
        var all = Process.GetProcessesByName(BrowserName);
        try
        {
            foreach (var p in all)
                if (ParentPid(p) == hostPid) return true;
            return false;
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }
    }

    // Blocks until the host's browser processes are gone, or the timeout.
    public static void WaitForExit(int hostPid, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && AnyOwnedBy(hostPid))
            Thread.Sleep(150);
    }
}
