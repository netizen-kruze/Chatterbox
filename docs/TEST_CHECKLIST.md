# Chatterbox — live test checklist

Run against a live VRChat client (OSC enabled: Action Menu → Options → OSC).
The dev build is `src\Chatterbox\bin\Debug\net9.0-windows\Chatterbox.exe`;
the shippable artifact is `releases\Chatterbox-<version>-win-x64.zip` (a
single `Chatterbox.exe` — the version is also shown in-app under
Settings → About). Don't run another chatbox writer (any other STT tool)
at the same time as captions here — one chatbox writer at a time.

## A. First run & models

Simulate a fresh machine: quit Chatterbox, rename `%APPDATA%\Chatterbox`
away (restore after this section).

- [ ] Launch: first-run screen shows (two choice cards), Start is disabled,
      status chip reads "no model installed"-equivalent (idle, grey dot).
- [ ] **Quick start**: downloads VAD then tiny.en-q5 in sequence with one
      progress bar; when done, the captions screen replaces first-run and
      Start enables (engine set to Whisper automatically). Total ≈33 MB.
- [ ] **Best quality is GPU-aware**: on this machine (NVIDIA) the card
      offers Whisper turbo-q5 + the CUDA pack (~718 MB) and sets the
      Whisper engine; captions run (CPU) as soon as the model lands, full
      speed after a restart. On a non-NVIDIA machine it offers Parakeet.
- [ ] Models screen: hardware banner shows your tier + recommendation;
      the q5 trio is listed; downloading a model shows live progress +
      Cancel; Cancel actually stops it; Delete removes and the row returns
      to Download.
- [ ] **Verify installed files**: toast reports all files verified. Corrupt
      test (optional): append a byte to a model file → Verify names it.
- [ ] GPU pack (NVIDIA): download completes, toast says restart to
      activate; after restart, Whisper status chip shows `Cuda`.
- [ ] Models → **In use** (moved from Settings in 1.7.0): the engine switch
      and the Whisper model picker change the Active badge below and
      persist across a restart.
- [ ] Restore your real `%APPDATA%` folders.

## B. Captions core

- [ ] Start captions → speak: committed words solid, still-changing words
      dim; previous sentence folds to the smaller dim line above.
- [ ] The same text appears in your in-game chatbox; "in-game window N/144"
      counter updates.
- [ ] Meter strip moves while speaking; Listening pulse shows during
      speech, "Waiting for speech" between.
- [ ] Pause ~3 s → next sentence on a new chatbox line. Stay quiet ≥30 s
      (bubble fades) → next speech starts a fresh window, old text gone.
- [ ] Stop captions → in-game chatbox clears, typing indicator off.
- [ ] Settings: engine Parakeet↔Whisper (takes effect next start), whisper
      model picker honors an explicit choice, update rate change applies
      to a running session, typing indicator toggles live.
- [ ] Unplug/disable the mic mid-session → captions stop with the
      "microphone capture failed" toast (not silent dead air).
- [ ] Start captions with a large Whisper model → the button reads
      "Loading model…" and the window stays responsive (drag it, switch
      sections) until the header flips to the engine name.
- [ ] While talking, the header pace chip reads "keeping up" (green). On a
      slow machine — or Whisper turbo forced onto the CPU — it turns red
      ("falling behind N× · M s late") and after ~10 s a toast offers the
      one-click fix (Switch to Parakeet / Use tiny.en / Open Models /
      Restart Chatterbox); pressing it restarts captions on the new engine.
      `last_boot.log` gets a "recognition falling behind" line and, at
      Stop, a "captions session ended" summary with pass counts and lag.
- [ ] Talk continuously for a minute → captions keep flowing with a steady
      lag (the pace chip stays green on capable hardware), no forced break
      every 28 s, and no word is lost or repeated where the window was
      trimmed (listen for the sentence around 10–15 s in).
- [ ] CPU-only Whisper (no GPU pack): a paragraph of speech transcribes as
      accurately as before — that runtime uses a shortened encoder context
      and no temperature fallback (WhisperNetEngine), and windows are capped
      at 18 s.
- [ ] Whisper on an NVIDIA machine without the GPU pack (or before the
      restart that activates it) → a red toast at Start says the card is
      idle, with a one-click "Open Models" / "Restart Chatterbox" action.

## C. Watched players & auto-start

- [ ] Players screen shows the live "In your instance" list with uids;
      the world name updates when you travel.
- [ ] **Watch** on an instance player adds them (with uid); ✕ removes.
- [ ] Manual add by display name shows "name only — id fills in when
      seen"; when that player next joins, the uid appears on the row.
- [ ] Auto-start ON + watched player joins you → captions start with the
      "— {name} joined" toast. Last watched player leaves → captions stop.
- [ ] You travel to a world with a watched player already there → captions
      start within ~5–15 s. Travel to a world with none → captions stop
      ("no watched players in this world" — not "{name} left").
- [ ] Launch Chatterbox while already in an instance with a watched player →
      the window opens normally and captions start shortly after launch with
      the "— {name} is here" toast (boot reconcile). Regression check: before
      1.2.5 this exact launch crashed before the window appeared, because
      the boot-time state messages were sent into a WebView that did not
      exist yet.
- [ ] Manual Stop while a watched player is present → stays stopped until
      they re-join or you change worlds.
- [ ] Quit VRChat during an auto session → captions stop within ~15 s.

## D. Window, tray, instances

- [ ] Close (✕) → window hides, tray icon remains, captions keep running
      (verify in-game). Left-click tray → window returns, state intact.
- [ ] Tray right-click → Exit quits fully (chatbox cleared, mic released,
      process gone).
- [ ] Launching Chatterbox.exe again while running → no second window, no
      error (single instance).
- [ ] Crash recovery: with Chatterbox closed, create an empty
      `%APPDATA%\Chatterbox\boot.inprogress`, then launch → a red toast
      says the last run never reached the window, captions do NOT
      auto-start (even with a watched player present), `last_boot.log` has
      the "previous run … never reached the window" line and `error.log` a
      "PreviousStart" entry (with Windows' crash record when one exists).
      The launch after that is normal again.
- [ ] **Ended from outside during captions is not a crash** (what a
      companion app does at every game exit): Start captions, then end
      Chatterbox.exe in Task Manager (End task) or with
      `taskkill /F /IM Chatterbox.exe` → the next launch has NO
      red toast, captions auto-start as usual, `last_boot.log` says
      "previous run … ended while captions were running — Windows recorded
      no crash, so it was ended from outside … captions auto-start as
      usual", and nothing is added to `error.log`. The same with the
      window idle → "ended without a clean exit while idle", no safe boot,
      nothing in `error.log`. While Chatterbox runs, `boot.inprogress`
      ends in `|window`, or `|captions` during a session.
- [ ] **A real crash** (no by-hand trigger in the app — the
      CrashRecordTests cover the decision; if it ever happens live):
      Windows' Application log holds an "Application Error", ".NET
      Runtime" or "Application Hang" event filed under Chatterbox.exe and
      stamped after that run started, the next launch shows the red toast
      "The last run of Chatterbox ended while captions were running …",
      captions do NOT auto-start, `last_boot.log` has "Windows recorded a
      crash or hang for it" and "SAFE BOOT", and `error.log` carries the
      record. An older event, or another program's crash that merely
      mentions Chatterbox, does not count. (To stage one: any exe named
      Chatterbox.exe that dies of an unhandled exception leaves such an
      event; write a `boot.inprogress` that started before it.)
- [ ] **Sign-out / shutdown counts as a clean exit**: with Chatterbox in
      the tray, sign out of Windows and back in → the next launch has no
      "previous run" line in `last_boot.log`, no red toast, and the old
      `last_boot.log` ended with "exit: Windows is ending the session".
- [ ] **Tray Exit and the game closing are logged**: `last_boot.log` says
      "exit requested by the tray menu's Exit" (or "VRChat exiting (Steam
      launch-option mode)"), and the process is gone within 10 s even if
      the window hangs ("exit: hard exit after 10 s").

## H. Translation (1.7.0)

- [ ] The Translate tab shows whether the model and engine are installed
      (an **Open Models** button when they are not) and the last translated
      sentence once captions run.
- [ ] Models → Components lists the Hy-MT2 translation model (1.1 GB), the
      Translation engine (36 MB) and GPU acceleration for translation
      (Vulkan, 20 MB); each downloads with progress, Cancel works, and
      **Verify installed files** covers the pack DLLs.
- [ ] Translate tab: switch on, "Translate into" Japanese, Start, speak a
      sentence and pause → the chatbox shows the Japanese sentence (check
      CJK renders in-game over OSC), the Captions page shows it under your
      words in amber, and `last_boot.log` has a "translation: … loaded in
      N ms (CPU, T threads | Vulkan GPU) → Japanese" line.
- [ ] "Show the original too" → chatbox shows "translation (original)".
- [ ] Translation on with the model or engine pack missing → Start still
      works, captions run untranslated, a toast with **Open Models** says
      why. Same when the model fails to load.
- [ ] Switching translation off while captions run stops translating at
      the next sentence; on again resumes (the model loads once, 1–3 s).
- [ ] Stop frees the model (Task Manager: memory drops by about 1.2 GB).
- [ ] **GPU claim is truthful**: with the Vulkan pack installed, the
      `translation:` line in `last_boot.log` says "Vulkan GPU" only when the
      `llama loader:` lines show `native\vulkan\llama.dll` was loaded; if it
      says "CPU (avx2), N threads", the Translate banner and a toast at
      Start say why (no `vulkaninfo.exe` in `System32`, or a restart is
      needed). Installing the Vulkan pack while translation already ran in
      this session → its toast says "restart Chatterbox to use it".

## E. Coexistence

- [ ] Another VRChat companion app running at the same time: both apps
      stable; Chatterbox captions work alongside it. (Keep any other
      chatbox writer disabled so the two don't fight over the window.)
- [ ] VRChat holds the mic throughout — game voice unaffected (shared mode).

## G. Other machines (any PC — no VRChat session needed)

- [ ] `.\tools\smoke.ps1 -Exe <path to the unzipped Chatterbox.exe>` →
      "SMOKE TEST PASSED" (boots against a throwaway data folder with a
      watched player already present; the 1.2.4 crash path).
- [ ] Settings → Speed check → Run → a row per installed engine. The
      verdict matches how captions feel live: *fast* ≈ a second behind,
      *usable* two to three, *too slow* = falling behind. `bench.log`
      holds the same rows plus the machine line.
- [ ] `last_boot.log` starts with a `machine:` line naming the CPU, GPU
      and Windows build correctly (Windows 11 is recognized from the build
      number even though the registry says Windows 10).
- [ ] Non-English Windows: after a crash, the next start's error.log
      "PreviousStart" entry still contains the Windows record (the crash
      record is read as XML, not localized text).
- [ ] Disconnect the network in the middle of a model download → it fails
      within about a minute with "no data received", never hangs forever.
- [ ] A drive with less free space than the model needs → the download
      refuses up front with a "not enough space" message.
- [ ] Tray icon clicked in the first second after launch (before the
      window exists) → nothing happens, no crash.

## Stability

- [ ] **Microphone lost mid-session** (unplug the USB mic): within a few
      seconds a red "Captions stopped — microphone capture failed (…)" toast;
      Start works again; an auto-started session restarts by itself about
      5 s later on the input Windows now treats as the default
      (`last_boot.log`: "auto-started captions stopped (…); presence
      recheck armed to bring them back").
- [ ] **Engine pass throws** (no by-hand trigger — the unit test
      AWorkerExceptionIsReportedNotSwallowed covers the path; if it ever
      happens live): the session stops with the red "Captions stopped —
      recognition failed (...)" toast instead of staying "running" in
      silence, and Start works again.
- [ ] **Stop during a long pass** on a slow CPU (Parakeet): the app never
      crashes; `last_boot.log` may say "engine disposal deferred".
- [ ] **Sustained overload** (large model on a weak CPU): captions lag but
      never more than roughly window + 10 s; `last_boot.log` shows
      `skipped N s of audio to catch up` rather than minutes of lag.
- [ ] **Download retry continues where it stopped**: disconnect the network
      in the middle of a model (or GPU pack) download → the failure toast
      ends in "(a retry continues where it stopped)" and the `.partial`
      (or `.download`) file stays; reconnect, download again → the progress
      bar starts where it left off and the file verifies. Cancel instead →
      the partial file is removed.
- [ ] **Settings folder read-only or disk full**: changing a setting shows
      a red "Settings could not be saved — …" toast instead of "Saved".
- [ ] **Microphone list gone stale**: with two USB microphones plugged in,
      open the Captions screen, unplug the first one WITHOUT reopening the
      list, pick the second from the stale list and Start → captions use
      the second microphone (the level meter moves with it; the next
      start's `last_boot.log` mic line names it), never a different device.
- [ ] **Old CPU (no AVX2/FMA)**, if one is at hand: `last_boot.log` says
      "whisper natives: this CPU lacks AVX2/FMA — the no-AVX build in …
      is used", and both engines caption. On any other CPU it says "AVX2+FMA
      present". `runtimes\noavx\win-x64` exists beside the exe either way.
- [ ] **Engine pack resumes**: disconnect the network in the middle of the
      Parakeet engine download → the toast ends in "(a retry continues
      where it stopped)" and `parakeet-engine.download` stays beside the
      exe; download again → it continues and verifies. Delete removes it.
- [ ] **Repeated toasts do not pile up**: the same message shown twice
      (e.g. Start with translation on but not installed, Stop, Start)
      leaves one toast, and never more than four are on screen.

## F. Portable zip (ideally on the second machine)

- [ ] Unzip `Chatterbox-<version>-win-x64.zip` to a user folder → a single
      `Chatterbox.exe`, runs with no install; Settings → About shows the
      matching version and the Read me / License / Third-party notices
      expand (and their text is selectable/copyable).
- [ ] First launch after unzip creates `runtimes\win-x64\` beside the exe
      (the bundled whisper/VAD natives, written at boot) and the embedded
      browser's cache under `%LOCALAPPDATA%\Chatterbox\WebView2\` (1.6.0+;
      earlier versions shared Photino's default folder with every other
      Photino app); pressing Start then actually produces captions — this
      is the single-file native-loading regression test.
- [ ] Fresh machine: WebView2 present → window renders; if absent, the
      dialog points to Microsoft's installer.
- [ ] No VRChat running → Players shows "VRChat is not running"; app is
      otherwise fine.
- [ ] VRChat running with its Settings → Debug → Logging off → about 30 s
      after VRChat starts, Players shows "VRChat's log is empty" with the
      Logging instructions, and `last_boot.log` gets a "vrchat log: still
      empty" line. Turn Logging on and rejoin the world → the players list
      fills in and the boot log adds "vrchat log: being written now".
- [ ] Update simulation: replace `Chatterbox.exe` only → settings, watched
      players, and models survive (%APPDATA%), and the Parakeet engine /
      CUDA packs beside the exe keep working. Deleting the whole folder
      instead also loses those packs — Models must offer them for
      re-download afterwards.
- [ ] Update from 1.5.2 or older (1.5.3 moved the voice detector to Silero
      VAD v6.2.0): with `ggml-silero-v5.1.2.bin` but no
      `ggml-silero-v6.2.0.bin` in `%APPDATA%\Chatterbox\models\stt\`, launch
      → `last_boot.log` gets "voice detector: Silero VAD v6.2.0 not
      installed (found ggml-silero-v5.1.2.bin) — downloading 864 KB in the
      background" and then "downloaded and verified; removed
      ggml-silero-v5.1.2.bin"; a toast says the same, the captions screen
      shows normally (no first-run cards), and Start / the boot auto-start
      work. Offline, Start fails with the "Captions need the voice detector
      … couldn't be downloaded" toast — never a "model not found" error.
- [ ] Settings → Updates (1.6.0+): a build with `<UpdateRepository>` set
      says "You have the latest version (x.y.z)" after Check for updates
      (one request to api.github.com); a build without it shows the greyed
      "no update source configured" line and never goes online. "Check
      when Chatterbox starts" persists (`CheckUpdatesAtStartup` in
      stt_settings.json) and is on by default.
- [ ] Update flow, offline-safe: serve a fake release with
      `python -m http.server` — a `latest.json` in GitHub's release shape
      (tag `vX.Y.Z` above the build, an asset named
      `Chatterbox-X.Y.Z-win-x64.zip`, notes containing
      `SHA-256: <hash of that zip>`) — and launch a copy of the app with
      `--update-url http://127.0.0.1:8000/latest.json --data-dir <throwaway>`.
      Check → "Version X.Y.Z is available"; Update now → progress bar,
      "Installing…", Chatterbox exits and comes back by itself (1.7.2: the
      staged `Chatterbox.exe.new` runs as the helper — Task Manager shows
      it for a second — waits for the old process and its
      msedgewebview2 to be gone, swaps the files and starts the new
      version; the old exe is never renamed while it runs); the new
      instance toasts "Chatterbox updated from … to X.Y.Z", `last_boot.log`
      carries the "update:" lines, and no `Chatterbox.exe.old` / `.new` is
      left beside the exe. A wrong SHA-256 in the notes → "SHA-256
      mismatch", nothing replaced. Update now while captions run → "Stop
      captions before updating". From a folder you can't write to
      (Program Files) → "Update failed — the app folder can't be written",
      the app keeps running.

---
Record results here with date + commit.
