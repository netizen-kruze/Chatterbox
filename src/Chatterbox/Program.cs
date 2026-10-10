using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Photino.NET;
using Chatterbox.Stt;

namespace Chatterbox;

// Photino host: a WebView2 window showing wwwroot/, speaking the same
// {action,...} / {type,payload} JSON contract. Outbound messages queue in a
// channel until the page has loaded and sent its first request, then drain
// through a single dispatcher so window-thread marshaling stays in one place.
//
// The gate is the page's first message, NOT Photino's WindowCreated: that
// event fires right after the native window is constructed, before the
// WebView2 control is attached, and Photino.Native's SendWebMessage on a
// not-yet-attached WebView is a native null dereference — the process dies
// before the window is even painted. Auto-start on boot (a watched player
// already present) queues state and toast messages that early, which is
// exactly how 1.2.4 crashed on every launch mid-session.
internal static class Program
{
    private static PhotinoWindow _window = null!;
    // Bounded: if the page never connects, hours of meter and state updates
    // must not pile up in memory — the page re-requests state when it does.
    private static readonly Channel<string> ToUi = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    // Completed on the window thread inside a WebView callback — run the
    // dispatcher's continuation elsewhere so draining never happens inline
    // in that callback.
    private static readonly TaskCompletionSource UiReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static volatile bool _exiting;
    private static string[] _args = Array.Empty<string>();
    // Set by RestartApp: after the window has closed and the WebView2
    // browser process has gone, Main starts the successor and leaves.
    private static string? _restartWhy;
    // The native window exists (WindowCreated), so Invoke can be marshaled
    // to it. Before that, tray clicks and game-exit notices have nowhere
    // to go — invoking then is a native null dereference.
    private static volatile bool _windowUp;
    private static long _bootTick;
    private const int PageConnectWatchdogMs = 20_000;

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
    private const int SW_HIDE = 0, SW_SHOW = 5;

    [STAThread]
    private static int Main(string[] args)
    {
        _bootTick = Environment.TickCount64;
        _args = args;
        InstallCrashHandlers();
        // Started as the update helper (the staged exe, by the instance
        // that just quit): swap and relaunch, nothing else — before the
        // mutex, the WebView2 check or any window (AppUpdater explains).
        if (args.Length > 0 && args[0] == AppUpdater.FinishSwitch) return AppUpdater.RunSwapHelper(args);
        WaitForPredecessor(args);
        // Live captions are latency work: trade a little memory for GC
        // pauses that stay short during long sessions.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        // Test hooks, used by tools\smoke.ps1 and the checklist: an alternate
        // data folder, an alternate VRChat log folder, and "treat VRChat as
        // running" so the auto-start path is exercised without the game.
        if (ArgValue(args, "--data-dir") is { } dataDir) SttPaths.DataDir = Path.GetFullPath(dataDir);
        // Recorded before anything (the WebView2 profile, the error log,
        // the boot sentinel) can create the folder: a data folder deleted
        // to reset the app must read as a first run, not as a settings
        // file that is momentarily invisible (SttSettings.LoadWithRetry).
        SttSettings.DataDirExistedAtBoot = Directory.Exists(SttPaths.DataDir);
        // A fake "latest release" document standing in for the GitHub API.
        if (ArgValue(args, "--update-url") is { } updateUrl) AppUpdater.LatestReleaseUrlOverride = updateUrl;
        var vrchatLogDir = ArgValue(args, "--vrchat-log-dir");
        bool assumeGame = args.Contains("--assume-vrchat-running");

        // WebView2's profile (a cache; no personal data) gets a folder of its
        // own. Photino's default is ONE folder shared by every Photino app on
        // the machine, and WebView2 hosts that share a profile share a browser
        // process — another app's exit, or the old instance's across an
        // in-app restart, could take this window down with it. A test data
        // folder keeps its own copy, so a test instance never shares with
        // the real one.
        string? webViewDir = ArgValue(args, "--data-dir") != null
            ? Path.Combine(SttPaths.DataDir, "webview")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chatterbox", "WebView2");
        try { Directory.CreateDirectory(webViewDir); }
        catch (Exception ex) { ErrorLog.WriteEntry("WebViewDir", ex); webViewDir = null; }

        // Steam launch-option wrapper mode: setting VRChat's launch options
        // to  "...\Chatterbox.exe" %command%  makes Steam start US with the
        // game's own command line. Launch the game FIRST, unconditionally —
        // even if another Chatterbox instance owns the mutex, VRChat must
        // start. In this mode the app also exits when the game exits.
        bool withVrchat = false;
        if (args.Length > 0 && File.Exists(args[0]))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(args[0])
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(args[0]) ?? "",
                };
                foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
                System.Diagnostics.Process.Start(psi)?.Dispose();
                withVrchat = true;
            }
            catch (Exception ex) { ErrorLog.WriteEntry("WrapperLaunch", ex); }
        }

        // Two instances would double-capture the mic, double-write the
        // chatbox, and race the settings file. (The game, if any, was
        // already launched above — the running instance takes it from here.)
        using var mutex = new Mutex(initiallyOwned: true, "Chatterbox-single-instance", out bool first);
        if (!first) return 0;

        // Zero-telemetry policy: this app's only network traffic is the
        // user-initiated model/GPU downloads and localhost OSC. WebView2's
        // own background networking (component updates, domain-reliability
        // pings, crash upload) is switched off via its documented
        // environment hook, set before the browser process spawns.
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
            "--disable-background-networking --disable-component-update " +
            "--disable-domain-reliability --disable-breakpad --no-pings");

        // Photino needs the WebView2 Evergreen Runtime (preinstalled on
        // Win11/current Win10; a fresh machine without it gets a blank
        // window). Detect and point at Microsoft's installer instead.
        if (!WebView2Present())
        {
            // MB_YESNO | MB_ICONINFORMATION; IDYES == 6.
            int pick = MessageBoxW(IntPtr.Zero,
                "Chatterbox needs Microsoft Edge WebView2, which isn't installed.\n\n" +
                "Open the Microsoft download page now? (Small installer, one click.)",
                "Chatterbox", 0x00000004u | 0x00000040u);
            if (pick == 6)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
            return 1;
        }

        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        // From here on, a start that dies before the page connects leaves
        // the sentinel behind for the next start to report (BootSentinel).
        var unfinished = BootSentinel.Arm(version);
        if (unfinished != null) CrashRecord.CollectInBackground(unfinished);
        // Gate on THIS run's extraction succeeding, not on index.html
        // existing — a leftover from a previous version would otherwise
        // mask a failure and silently run a stale UI against a new backend.
        bool uiOk = false;
        try { ExtractUiAssets(); uiOk = true; }
        catch (Exception ex) { ErrorLog.WriteEntry("ExtractUiAssets", ex); }
        if (!uiOk || !File.Exists(Path.Combine(UiDir, "wwwroot", "index.html")))
        {
            // Without the extracted UI there is nothing to show — say so
            // instead of dying silently before any window exists. 0x10 =
            // MB_ICONERROR.
            MessageBoxW(IntPtr.Zero,
                "Chatterbox couldn't unpack its interface files to the temp folder.\n\n" +
                "Details were written to %APPDATA%\\Chatterbox\\error.log.",
                "Chatterbox", 0x00000010u);
            BootSentinel.Clear();
            return 1;
        }
        ExtractWhisperNatives();
        if (!File.Exists(Path.Combine(SttPaths.NativeDir, "whisper.dll")))
        {
            // Captions cannot run without these natives (the voice detector
            // loads through them), and the usual cause is an unwritable app
            // folder — name it now, or Start would later fail with a
            // misleading model-load error. 0x30 = MB_ICONWARNING.
            MessageBoxW(IntPtr.Zero,
                "Chatterbox couldn't unpack its speech components next to Chatterbox.exe — " +
                "the folder doesn't allow writing.\n\n" +
                "Move the Chatterbox folder somewhere you can write to (Desktop, Documents — " +
                "not Program Files) and start it again. Captions can't run until then.",
                "Chatterbox", 0x00000030u);
        }
        // The translation runtime's loader says which build it picked
        // (vulkan or a CPU variant) — the only truthful source for the
        // "runs on your GPU" claim.
        LlamaTranslator.OnLoaderLog += line => BootLog.Append("llama loader: " + line);

        // Model/GPU-pack downloads identify this app by name and version.
        var ua = $"Chatterbox/{version}";
        SttModelManager.SetUserAgent(ua);
        SttGpuPack.SetUserAgent(ua);
        SttEnginePack.SetUserAgent(ua);
        SttNativePack.SetUserAgent(ua);
        AppUpdater.SetUserAgent(ua);

        using var watcher = new PresenceWatcher(vrchatLogDir);
        if (assumeGame) watcher.IsGameRunning = () => true;
        // What the watcher does and sees — which file it follows, a poll
        // that fails, players coming and going (counts only) — goes to the
        // boot log, where a "Players shows nothing" report can be read off.
        // Repeats are folded and the total is capped: a failure every second
        // must not write a log that grows all session.
        watcher.DebugLog += line =>
        {
            if (line == _lastWatcherLine || Interlocked.Increment(ref _watcherLines) > 300) return;
            _lastWatcherLine = line;
            BootLog.Append(line);
        };
        using var ctrl = new StandaloneSttController(watcher, SendToUi);
        using var tray = new TrayService();

        _window = new PhotinoWindow()
            .SetTitle("Chatterbox")
            .SetSize(920, 640)
            .SetMinSize(720, 520)
            .Center()
            .SetResizable(true)
            .RegisterWindowCreatedHandler((_, _) => _windowUp = true)
            .RegisterWindowClosingHandler((_, _) =>
            {
                if (_exiting) return false;          // real exit (tray menu)
                ShowWindow(_window.WindowHandle, SW_HIDE); // hide to tray
                return true;                          // cancel the close
            })
            .RegisterWebMessageReceivedHandler((_, raw) => OnUiMessage(ctrl, raw));
        if (webViewDir != null) _window.SetTemporaryFilesPath(webViewDir);
        // The browser process carries the user agent on its command line:
        // identify it as this app's, not as a generic "Photino WebView" that
        // other Photino-based tooling on the machine may mistake for its own.
        _window.SetUserAgent($"Chatterbox/{version}");
        var icon = Path.Combine(UiDir, "app.ico");
        if (File.Exists(icon)) _window.SetIconFile(icon);

        tray.OnOpen += () =>
        {
            if (!_windowUp) return;
            _window.Invoke(() =>
            {
                ShowWindow(_window.WindowHandle, SW_SHOW);
                SetForegroundWindow(_window.WindowHandle);
            });
        };
        tray.OnExit += () => RequestExit("the tray menu's Exit");
        // Sign-out, shutdown or restart: Windows ends the process right after
        // this, so it is a wanted exit, not a crash — the marker goes now.
        tray.OnSessionEnding += () =>
        {
            BootLog.Append("exit: Windows is ending the session (sign-out, shutdown or restart)");
            BootSentinel.Clear();
        };

        // Wrapper mode: once the game has been seen running, its exit ends
        // this app too (the watcher's process check is the authority).
        bool sawGame = false;
        watcher.GameRunningChanged += running =>
        {
            if (running) { sawGame = true; return; }
            if (!withVrchat || !sawGame || _exiting) return;
            // Logged, through the window, with the hard-exit backstop should
            // the window thread never answer.
            RequestExit("VRChat exiting (Steam launch-option mode)");
        };

        _ = RunUiDispatcherAsync();
        watcher.Start();
        // The game may already be running when the watcher boots (fast
        // spawn) — that state change fires no event, so seed it here.
        if (watcher.GameRunning) sawGame = true;
        BootLog.Write(version, args, ctrl.Settings, watcher.GameRunning, watcher.DescribeLog(), MachineProfile.Describe());
        // An update the previous start installed: clear its leftovers and
        // say so (the toast waits for the page like everything else).
        if (AppUpdater.LatestReleaseUrlOverride is { } updateSource)
            BootLog.Append($"update source overridden: {updateSource}");
        if (new AppUpdater().FinishPendingUpdate() is { } updated)
        {
            BootLog.Append("update: " + updated);
            SendToUi("toast", new { ok = true, msg = "Chatterbox " + updated });
        }
        if (unfinished != null)
            BootLog.Append($"previous run ({unfinished.Version} started {unfinished.StartedAt:HH:mm:ss}, pid {unfinished.Pid}) " +
                           $"{unfinished.How} — crashed or was killed" +
                           (unfinished.WantsSafeBoot ? ". SAFE BOOT: captions won't auto-start until Start is pressed" : "") +
                           "; the Windows crash record goes to error.log");
        SttSettings.MarkHasRun(version);
        ctrl.EnsureVoiceDetectorAtBoot();
        ctrl.ArmBootReconcile(safeBootReason: unfinished is { WantsSafeBoot: true } ? unfinished.How : null);
        ctrl.RestartRequested += RestartApp;

        // A page that never connects means a blank window (WebView2
        // trouble, a broken extraction) — say so where a report finds it.
        using var watchdog = new System.Threading.Timer(_ =>
        {
            if (UiReady.Task.IsCompleted) return;
            var note = $"page has not connected {PageConnectWatchdogMs / 1000} s after start — the window is probably blank; " +
                       $"WebView2 {(WebView2Present() ? "is installed" : "NOT found")}";
            BootLog.Append("ui: " + note);
            ErrorLog.WriteNote("Ui", note);
        }, null, PageConnectWatchdogMs, Timeout.Infinite);

        _window.Load(Path.Combine(UiDir, "wwwroot", "index.html"));
        _window.WaitForClose();
        _exiting = true;
        if (_restartWhy != null) LaunchSuccessor(_restartWhy, ctrl.PendingUpdateSwap);
        // The watcher's poll must not reach a controller that is being
        // disposed (the usings unwind ctrl first); Stop waits for one in flight.
        watcher.Stop();
        BootSentinel.Clear();
        return 0;
    }

    private static string? _lastWatcherLine;
    private static int _watcherLines;

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // Exit requested from the tray or by the game closing: through the
    // window when it exists, straight out when it doesn't yet — and out
    // regardless after 10 s, should the window thread never answer.
    private static void RequestExit(string why)
    {
        if (_exiting) return;
        _exiting = true;
        BootLog.Append($"exit requested by {why}");
        // The backstop is a wanted exit, not a crash: the marker goes too.
        // Armed BEFORE the window is asked to close: that request waits for
        // the window thread, and a window thread that never answers — the
        // case the backstop exists for — would otherwise hold it forever.
        _ = Task.Delay(10_000).ContinueWith(_ => { try { BootLog.Append("exit: hard exit after 10 s"); BootSentinel.Clear(); Environment.Exit(0); } catch { } });
        CloseWindowOrExit();
    }

    private static void CloseWindowOrExit()
    {
        if (_windowUp)
        {
            try { _window.Invoke(() => _window.Close()); return; } catch { }
        }
        BootSentinel.Clear();
        Environment.Exit(0);
    }

    // Managed crashes used to die silently; now they leave a stack in
    // error.log and say so. Native crashes still can't be caught — the boot
    // sentinel reports those at the next start.
    private static void InstallCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            ErrorLog.WriteEntry("Unhandled", ex ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
            BootLog.Append($"unhandled exception: {ex?.GetType().Name}: {ex?.Message}");
            MessageBoxW(IntPtr.Zero,
                "Chatterbox hit an unexpected error and has to close.\n\n" +
                $"{ex?.GetType().Name}: {ex?.Message}\n\n" +
                @"Details were written to %APPDATA%\Chatterbox\error.log.",
                "Chatterbox", 0x00000010u);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.WriteEntry("UnobservedTask", e.Exception);
            e.SetObserved();
        };
    }

    // "--after <pid>": this process was launched by a running instance that
    // is about to exit (RestartApp) — let it release the single-instance
    // mutex first instead of losing the race and quitting.
    private static void WaitForPredecessor(string[] args)
    {
        int i = Array.IndexOf(args, "--after");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.WaitForExit(15_000);
        }
        catch { /* already gone */ }
        // Its browser process may still be shutting down (see
        // WebViewProcesses) — attaching to it would leave this window black.
        WebViewProcesses.WaitForExit(pid, 10_000);
    }

    // Relaunch — for a native-runtime change (the GPU pack binds at load
    // time) or to run a freshly installed update. The window closes first;
    // once WaitForClose returns, Main launches the successor (LaunchSuccessor),
    // so the new process never meets this one's WebView2 on its way out.
    private static void RestartApp(string why)
    {
        if (_exiting) return;
        _restartWhy = why;
        _exiting = true;
        BootLog.Append($"restarting {why}");
        // Off the window thread: this runs inside a WebView callback, and
        // the close should happen after that callback has returned.
        _ = Task.Run(() => { try { _window.Invoke(() => _window.Close()); } catch { } });
    }

    // Starts the next instance of this exe, which waits for this process
    // (--after) before taking the single-instance mutex. After an update
    // the staged exe is started instead, as the helper that swaps the
    // files once this process and its browser are gone and then starts
    // the new version (AppUpdater.RunSwapHelper) — this exe is never
    // renamed or replaced while it runs. Only the test/option flags are
    // carried over: in Steam wrapper mode args[0] is the game's exe, and
    // the restarted app must not launch the game again.
    private static void LaunchSuccessor(string why, string? pendingSwap)
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            var args = ForwardedArgs(_args).ToList();
            if (pendingSwap != null)
            {
                if (AppUpdater.StartSwapHelper(pendingSwap, exe, relaunch: true, args) is { } helperError)
                {
                    // Without the helper the old version comes back; the
                    // next start clears the staged file and the marker
                    // explains that the swap did not happen.
                    BootLog.Append($"update: the staged exe could not be started as the helper ({helperError}); restarting without the update");
                    ErrorLog.WriteNote("RestartApp", "update helper failed to start: " + helperError);
                }
                else return;
            }
            // The browser process goes a moment after the window; the
            // successor must not attach to it.
            WebViewProcesses.WaitForExit(Environment.ProcessId, 10_000);
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--after");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("RestartApp", ex);
            BootLog.Append($"restart {why} FAILED to launch the new process: {ex.Message}");
        }
    }

    private static readonly string[] ValueFlags = { "--data-dir", "--vrchat-log-dir", "--update-url" };
    private static readonly string[] SwitchFlags = { "--assume-vrchat-running" };

    private static IEnumerable<string> ForwardedArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (SwitchFlags.Contains(args[i])) yield return args[i];
            else if (ValueFlags.Contains(args[i]) && i + 1 < args.Length)
            {
                yield return args[i];
                yield return args[++i];
            }
        }
    }

    private static void SendToUi(string type, object? payload) =>
        ToUi.Writer.TryWrite(JsonConvert.SerializeObject(new { type, payload }));

    private static async Task RunUiDispatcherAsync()
    {
        await UiReady.Task.ConfigureAwait(false);
        await foreach (var json in ToUi.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_exiting) return;
            try { _window.Invoke(() => _window.SendWebMessage(json)); }
            catch (Exception ex) { ErrorLog.WriteEntry("UiDispatcher", ex); }
        }
    }

    // Runs on the window thread, called by the attached WebView — the first
    // call is the proof that the page is up and listening, which releases
    // everything queued during boot (in order, ahead of this reply).
    private static void OnUiMessage(StandaloneSttController ctrl, string raw)
    {
        if (UiReady.TrySetResult())
        {
            BootLog.Append($"ui: page connected {Environment.TickCount64 - _bootTick} ms after start, " +
                           $"{ToUi.Reader.Count} queued message(s) released");
            BootSentinel.Mark(BootSentinel.PhaseWindow);   // the start made it; the marker now guards the run
            ctrl.UiConnected();
        }
        try
        {
            var msg = JObject.Parse(raw);
            ctrl.HandleMessage(msg["action"]?.ToString() ?? "", msg);
        }
        catch (Exception ex) { ErrorLog.WriteEntry("OnUiMessage", ex); }
    }

    // The UI lives inside the executable (single-file friendly) and is
    // re-extracted to a temp folder on every launch - a few hundred KB.
    internal static string UiDir { get; private set; } = "";

    private static void ExtractUiAssets()
    {
        UiDir = Path.Combine(Path.GetTempPath(), "Chatterbox", "ui");
        var asm = typeof(Program).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("ui/", StringComparison.Ordinal)) continue;
            var rel = name[3..].Replace('/', Path.DirectorySeparatorChar)
                               .Replace('\\', Path.DirectorySeparatorChar);
            var dest = Path.Combine(UiDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // A stray read-only attribute from a previous run must not
            // wedge every future launch.
            if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
            using var src = asm.GetManifestResourceStream(name)!;
            using var dst = File.Create(dest);
            src.CopyTo(dst);
        }
    }

    // whisper.cpp CPU natives ride inside the exe and are placed beside it
    // at boot: under a single-file publish the host would extract bundled
    // natives to its own temp dir, which Whisper.net's loader never probes.
    // runtimes\win-x64 next to the exe IS probed (the CUDA pack proves it).
    // The VAD loads through whisper.cpp too, so captions need these even on
    // Parakeet. Always overwritten (~2 MB each): an exe-swap update must
    // never leave a stale native behind, and same-size version collisions
    // are realistic with alignment-padded PE files.
    private static void ExtractWhisperNatives()
    {
        try
        {
            var asm = typeof(Program).Assembly;
            // The AVX2/FMA build and the no-AVX build, each into the folder
            // Whisper.net probes for it; the loader picks by what the CPU
            // has (the no-AVX folder must EXIST for an older CPU not to be
            // refused outright — see Chatterbox.csproj).
            foreach (var (prefix, dir) in new[]
            {
                ("natives/" + SttPaths.Rid + "/", SttPaths.NativeDir),
                ("natives/noavx/" + SttPaths.Rid + "/", SttPaths.NoAvxNativeDir),
            })
            {
                Directory.CreateDirectory(dir);
                foreach (var name in asm.GetManifestResourceNames())
                {
                    if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var dest = Path.Combine(dir, name[prefix.Length..]);
                    if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
                    using var src = asm.GetManifestResourceStream(name)!;
                    using var dst = File.Create(dest);
                    src.CopyTo(dst);
                }
            }
            BootLog.Append("whisper natives: " + (WhisperNetEngine.CpuHasAvx
                ? "AVX2+FMA present — the AVX build is used"
                : $"this CPU lacks AVX2/FMA — the no-AVX build in {SttPaths.NoAvxNativeDir} is used (slower; Parakeet is the better engine here)"));
        }
        catch (Exception ex) { ErrorLog.WriteEntry("ExtractWhisperNatives", ex); }
    }

    private static bool WebView2Present()
    {
        // Evergreen runtime registers its client key with a "pv" version.
        var keys = new[]
        {
            @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
            @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        };
        foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        foreach (var key in keys)
        {
            try
            {
                using var rk = root.OpenSubKey(key);
                if (rk?.GetValue("pv") is string pv && pv.Length > 0 && pv != "0.0.0.0") return true;
            }
            catch { }
        }
        return false;
    }
}
