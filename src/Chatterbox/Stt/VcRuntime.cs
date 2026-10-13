using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Chatterbox.Stt;

// Every whisper.cpp library (the bundled CPU builds and both CUDA builds)
// imports Microsoft's Visual C++ 2015-2022 runtime: vcruntime140.dll,
// vcruntime140_1.dll, msvcp140.dll and vcomp140.dll (OpenMP). Windows does
// not ship it; most PCs have it because some other program installed it. On
// one that doesn't, the speech libraries cannot load and Start fails with a
// model-load error that names none of this.
//
// The check looks for the files where Windows would (never by loading
// them). The fix is Microsoft's own installer, fetched from Microsoft's
// permanent link, accepted only with a valid Microsoft signature, and run
// with its own progress window — it asks for administrator rights itself.
public static class VcRuntime
{
    public static readonly string[] Needed = { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "vcomp140.dll" };

    public const string InstallerUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
    private const string InstallerName = "vc_redist.x64.exe";

    internal static List<string> Missing(IEnumerable<string> dirs)
    {
        var list = dirs.ToList();
        return Needed.Where(n => SttGpuPack.FindIn(list, n) == null).ToList();
    }

    // The folder of the speech libraries counts too: files beside the
    // library that needs them are found first.
    public static List<string> Missing() =>
        Missing(new[] { SttPaths.NativeDir }.Concat(SttGpuPack.SystemSearchDirs()));

    public static string Describe()
    {
        var missing = Missing();
        return missing.Count == 0 ? "present" : "MISSING " + string.Join(", ", missing);
    }

    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) }) { Timeout = TimeSpan.FromMinutes(10) };

    // Downloads, verifies and runs Microsoft's installer. The message is
    // ready to show either way.
    public static async Task<(bool Ok, string Message)> InstallAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(SttPaths.DataDir, InstallerName);
        try
        {
            Directory.CreateDirectory(SttPaths.DataDir);
            using (var response = await Http.GetAsync(InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(path);
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
            }
            var problem = SignatureProblem(path);
            if (problem != null)
                return (false, $"Microsoft's Visual C++ installer was downloaded but not run: {problem}. Install it by hand from {InstallerUrl}");

            // /passive: Microsoft's own progress window, no questions.
            // UseShellExecute lets Windows ask for administrator rights.
            using var proc = Process.Start(new ProcessStartInfo(path, "/install /passive /norestart") { UseShellExecute = true });
            if (proc == null) return (false, "Microsoft's Visual C++ installer could not be started.");
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return Outcome(proc.ExitCode, Missing().Count);
        }
        catch (OperationCanceledException) { return (false, "The Visual C++ runtime install was cancelled."); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "The Visual C++ runtime was not installed — the administrator prompt was declined.");
        }
        catch (Exception ex)
        {
            return (false, $"The Visual C++ runtime could not be installed — {ex.Message}. Install it by hand from {InstallerUrl}");
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    // 0 = installed, 3010 = installed (Windows wants a restart), 1638 = a
    // newer version is already there, 1602 = cancelled by the user.
    internal static (bool Ok, string Message) Outcome(int exitCode, int stillMissing) => exitCode switch
    {
        0 or 1638 when stillMissing == 0 => (true, "Microsoft's Visual C++ runtime is installed — restart Chatterbox to use it."),
        3010 => (true, "Microsoft's Visual C++ runtime is installed — restart Windows, then start Chatterbox."),
        1602 => (false, "The Visual C++ runtime was not installed — the installer was cancelled."),
        0 or 1638 => (false, $"Microsoft's installer finished, but files are still missing. Install it by hand from {InstallerUrl}"),
        _ => (false, $"Microsoft's Visual C++ installer failed (code {exitCode}). Install it by hand from {InstallerUrl}"),
    };

    // null when the file carries a valid signature that chains to a trusted
    // root AND was signed by Microsoft; otherwise why not.
    public static string? SignatureProblem(string path)
    {
        try
        {
            int trust = VerifyTrust(path);
            if (trust != 0) return $"its signature is not valid (0x{trust:X8})";
#pragma warning disable SYSLIB0057
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return SignedByMicrosoft(cert.Subject) ? null : $"it is signed by someone else ({cert.Subject})";
        }
        catch (Exception ex) { return "its signature could not be checked — " + ex.Message; }
    }

    internal static bool SignedByMicrosoft(string subject) =>
        subject.Split(',').Select(p => p.Trim()).Any(p => p.Equals("O=Microsoft Corporation", StringComparison.Ordinal));

    private static int VerifyTrust(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE: never waits on the network
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = pFile,
                dwProvFlags = 0x1000,      // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
