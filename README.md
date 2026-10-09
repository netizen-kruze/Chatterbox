# Chatterbox

Live captions for VRChat, built for deaf and hard-of-hearing players. Your
speech is transcribed **on your own PC** and streamed into your in-game
chatbox — nothing you say ever leaves your machine (the only network access
is downloading model files, checksum-verified).

Works alongside the official VRChat client — no account login, no game
mod, no install.

![Chatterbox captioning](docs/screenshot.png)

## Download

Get the latest `Chatterbox-<version>-win-x64.zip` from the **Releases**
page of this repository — it contains a single `Chatterbox.exe`, no
installer. Each release lists the zip's SHA-256; the running app shows its
version under **Settings → About**. The exe is not code-signed, so Windows
SmartScreen may warn on first run — choose **More info → Run anyway**.

## Quick start (60 seconds)

1. Chatterbox is a single `Chatterbox.exe` — put it in its own folder
   anywhere you can write to (Desktop, `C:\Tools\…` — **not** Program
   Files). On first launch it unpacks its bundled speech components
   (~2 MB) into a `runtimes\` folder next to the exe — that's why the
   folder must be writable — and optional downloads install there too.
2. In VRChat: **Action Menu → Options → OSC → Enabled**.
3. Run `Chatterbox.exe`. On first launch, pick a model:
   **Quick start** (~33 MB) or **Best quality** (recommended, ~670 MB —
   ~718 MB on NVIDIA machines, where it includes GPU acceleration).
4. Press **Start captions** and talk. Your words appear in the app and in
   your VRChat chatbox.

Closing the window hides Chatterbox to the system tray — captions keep
running. Left-click the tray icon to reopen; right-click → Exit to quit.

## Auto-start players

On the **Players** screen, press **Auto-Start** next to someone in your
instance (or add a display name / `usr_` id by hand). With **Auto-start
captions** on, captions start whenever one of those players is with you and
stop when the last one leaves — across world changes, and even if you
launch Chatterbox mid-session.

## Start and stop with VRChat (Steam)

To have Chatterbox live and die with the game, set VRChat's Steam **launch
options** (Library → VRChat → Properties → Launch Options) to:

```
"C:\path\to\Chatterbox.exe" %command%
```

Steam then starts Chatterbox, which launches VRChat with its normal command
line and **exits automatically when VRChat closes**. Combined with
auto-start players, captions become fully hands-off. Launched normally
(without `%command%`), the app stays running independently in the tray.

## GPU acceleration

Whisper models run dramatically faster on NVIDIA cards: on the **Models**
screen, download **GPU acceleration for Whisper (CUDA)** (~143 MB) and
restart the app. Parakeet (the recommended engine) is fast on CPU either
way.

## Translation

Chatterbox can translate your captions before they reach the chatbox, so
people who read another language can follow you — still entirely on your
own machine. On the **Models** screen download the **Hy-MT2 1.8B
translation model** (1.1 GB, Tencent, Apache-2.0) and the **Translation
engine** (36 MB, llama.cpp); on any GPU — NVIDIA, AMD or Intel — the
optional **GPU acceleration for translation (Vulkan)** pack (20 MB) makes
it several times faster. Then under **Settings → Translation** switch
**Translate my captions** on and pick the language. Each finished sentence
is translated in about 0.1 s on a GPU and 0.5 s on a modern CPU; the
chatbox shows the translation, optionally followed by your original words
in brackets. The engine and model selectors live on the Models screen
under **In use**.

The model was chosen by a timed comparison of the small open-weight
translators (`docs/TRANSLATION_BENCH-2026-10-08.md`): the fastest one that
was also accurate. The picker lists the languages it does best.

## Requirements

- Windows 10/11, 64-bit.
- Microsoft Edge WebView2 Runtime — preinstalled on Windows 11 and current
  Windows 10; if missing, Chatterbox points you to Microsoft's one-click
  installer at startup.
- A microphone. VRChat can keep using it at the same time (shared mode).

## Updates

**Settings → Updates → Check for updates** asks the project's GitHub
Releases page for a newer version and installs it in place: the zip is
downloaded, checked against the SHA-256 published with the release, the
exe is swapped and Chatterbox restarts. Turn on **Check when Chatterbox
starts** to be told at startup when a new version exists (it never
installs anything by itself). Your settings, auto-start players, and
downloaded models live in `%APPDATA%\Chatterbox\` and survive updates;
the optional Parakeet engine and GPU acceleration packs install **next to
the exe** and survive too.

Manual updates still work: replace `Chatterbox.exe` with the one from the
new zip. Keep the app folder (just swap the exe); if you delete the whole
folder, re-download the packs from the **Models** screen. When a new
version needs a newer voice-detection model (under 1 MB), Chatterbox
downloads and verifies it by itself the first time it starts — the one
download it makes without you pressing Download.

To fully uninstall: delete the app folder, `%APPDATA%\Chatterbox\`,
`%LOCALAPPDATA%\Chatterbox\` (the embedded browser's cache), and the
registry key `HKEY_CURRENT_USER\Software\Chatterbox` (one value that
records the app has run before).

## Privacy & network

Chatterbox sends **no telemetry, no analytics, no pings — nothing.** Its
complete network activity:

- Downloading model files you request (Hugging Face, including the
  translation model) and the optional engine/GPU packs (nuget.org) — every download checksum-verified. The one
  download that starts on its own: after an update that changed the small
  voice-detection model, the new file (under 1 MB, same source, same
  checksum check) is fetched the first time Chatterbox starts.
- Checking for updates — when you press **Check for updates**, or at
  startup unless you switched that off under Settings → Updates (it is on by default): one request to GitHub's
  Releases API, which sees the app's name and version and nothing else.
- Caption text to VRChat over OSC on **your own machine only**
  (`127.0.0.1:9000` — never leaves the PC).

That is the entire list. The embedded WebView2 browser is additionally
launched with its background networking, component updates, crash upload,
and reliability pings disabled. Your speech is transcribed locally and is
never transmitted anywhere, and so is its translation.

## Troubleshooting

- **Nothing appears in-game**: check VRChat's OSC is enabled (Action Menu →
  Options → OSC). Chatterbox sends to `127.0.0.1:9000`.
- **Players says VRChat's log is empty**: VRChat is running with its own
  logging switched off, so there is nothing for Chatterbox to read — no
  world, no players, no auto-start. In VRChat, open Settings → Debug and
  turn Logging on, then rejoin your world; if Logging was already on,
  restart VRChat. `last_boot.log` notes when the log was found empty and
  when it started being written.
- **Start is disabled**: a recognition model and the voice detector must be
  installed first — the app guides you on first run; see **Models**.
- **Words appear slowly**: lower the update rate to 1.0 s (VRChat's rate
  limit is the floor), pick a smaller model, or add the GPU pack. Lag no
  longer grows with sentence length: once the earlier words of a long
  sentence are settled, Chatterbox re-transcribes only its recent seconds,
  and audio is queued rather than dropped when a pass runs long. On the
  CPU, Whisper runs a shortened encoder context for speed.
- **Captions stopped on their own**: the mic was disconnected, or auto-stop
  fired because the last watched player left.
- **Settings or players missing after a launch**: check `last_boot.log` —
  it records which settings file was read and how many auto-start players
  it held. If the file was missing at start, Chatterbox keeps watching for
  it and reloads automatically once it appears (a toast confirms).
- **Captions fall behind while you talk**: the header chip says so
  ("falling behind 2.3× · 3.1 s late"), and after ten seconds of that
  Chatterbox offers the fix for your machine as a one-click toast — switch
  to Parakeet, use a smaller Whisper model, install GPU acceleration, or
  restart to activate it. `last_boot.log` records every session's pace
  (passes, average and worst pass time, worst lag) for bug reports.
- **Chatterbox crashed or vanished**: the next start notices (a
  `boot.inprogress` marker survived), copies Windows' crash record into
  `error.log`, and runs a safe boot — captions are not auto-started until
  you press Start once, so a crash can never loop.
- Errors are logged to `%APPDATA%\Chatterbox\error.log`.

Don't run Chatterbox's captions at the same time as another chatbox
writer (any other speech-to-text or OSC chatbox tool) — they will fight
over the in-game window.

Bug reports are welcome — please attach `%APPDATA%\Chatterbox\error.log`,
`last_boot.log` (what the app saw at its last start, including the
machine it ran on) and, for anything speed-related, `bench.log` from
**Settings → Speed check**.

## Verifying on another machine

Two checks tell you within a minute whether Chatterbox is stable and fast
on a given PC — no VRChat session needed:

1. **Speed check**, in the app: **Settings → Speed check → Run**. Every
   installed engine transcribes a bundled 14-second clip; the table shows
   load time, the time for a 6-second pass (what live captioning repeats
   over and over), the full-clip time, how many words came out right, and
   a verdict. *fast* means captions run about a second behind speech,
   *usable* two to three, *too slow* means switch engine or model. The
   result is also written to `%APPDATA%\Chatterbox\bench.log` together
   with a line describing the machine — attach it to any "it's slow"
   report.
2. **Smoke test**, for testers with the repository: run
   `.\tools\smoke.ps1 -Exe C:\path\to\Chatterbox.exe`. It boots the app
   against a throwaway data folder and a fake VRChat log in which a
   watched player is already present, then reports PASS or FAIL for:
   survives, page connected, settings read, fake VRChat log read, boot
   auto-start check ran, no crash events. Nothing touches your real
   settings or game log.

Every `last_boot.log` starts with a `machine:` line (CPU, threads, RAM,
GPUs, Windows build, hardware tier), so a report from any machine says
what it ran on.

## Building from source

Requires the .NET 9 SDK on Windows.

```
dotnet build Chatterbox.sln          # debug build + tests project
dotnet test src\Chatterbox.Tests\Chatterbox.Tests.csproj
.\build.ps1                          # release: single-file exe zipped into releases\
```

## License

Chatterbox is free software under the **MIT License** — see
[LICENSE](LICENSE). Third-party credits are in [NOTICE.txt](NOTICE.txt),
full third-party license texts in
[THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md), and all of it is also
shown in-app under **Settings → About**.

Speech recognition uses OpenAI Whisper models (MIT, ggml conversions by
whisper.cpp) and the NVIDIA Parakeet TDT 0.6B v2 model
([CC BY 4.0](https://creativecommons.org/licenses/by/4.0/), int8 ONNX
export by k2-fsa/sherpa-onnx), downloaded at the user's request and never
bundled.
