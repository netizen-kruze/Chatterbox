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
      says the last start didn't finish, captions do NOT auto-start (even
      with a watched player present), `last_boot.log` has the "previous
      start … never reached the window" line and `error.log` a
      "PreviousStart" entry (with Windows' crash record when one exists).
      The launch after that is normal again.

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
      stt_settings.json) and is off by default.
- [ ] Update flow, offline-safe: serve a fake release with
      `python -m http.server` — a `latest.json` in GitHub's release shape
      (tag `vX.Y.Z` above the build, an asset named
      `Chatterbox-X.Y.Z-win-x64.zip`, notes containing
      `SHA-256: <hash of that zip>`) — and launch a copy of the app with
      `--update-url http://127.0.0.1:8000/latest.json --data-dir <throwaway>`.
      Check → "Version X.Y.Z is available"; Update now → progress bar,
      "Installing…", Chatterbox restarts by itself; the new instance toasts
      "Chatterbox updated from … to X.Y.Z", `last_boot.log` carries the
      "update:" lines, and no `Chatterbox.exe.old` / `.new` is left beside
      the exe. A wrong SHA-256 in the notes → "SHA-256 mismatch", nothing
      replaced. Update now while captions run → "Stop captions before
      updating".

---
Record results here with date + commit.
