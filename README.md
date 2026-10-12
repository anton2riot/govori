# Govori

A desktop voice-to-text app built with .NET 9, Avalonia and PortAudio. Hold a shortcut, speak, release it, and Govori transcribes your audio with Gemini and pastes the result into the active text field.

## Features

- Hold-to-record shortcut, a Record button, and local transcription history.
- An 18-pixel indicator: grey while preparing, green when the microphone is ready, amber while transcribing. A quiet 100 ms chime signals that the microphone is ready to speak.
- Automatic paste with clipboard restoration and protection against pasting into a different window.
- Retry saved recordings after network errors; successful recordings are deleted.
- English and Russian interfaces. The system language is selected by default; unsupported languages fall back to English. Change **Settings → Language** without restarting. The interface language does not change the language of your dictation.
- Optional startup at login and a tray menu.

## Windows quick start

Build from source with the .NET 9 SDK and PowerShell 7 (`pwsh`). Run the commands from the repository root:

```powershell
./build.ps1
./install.ps1 -StartWithSystem
```

The installer copies the self-contained app to `%LOCALAPPDATA%\Dictophone.CrossPlatform\app` and starts it in the tray. The .NET runtime is not required on the target machine. Startup uses a `Govori.lnk` shortcut in the user's Windows Startup folder pointing to this stable installation path, so moving the source folder does not break it. No administrator access is required.

Open **Settings → Gemini API key**, paste your key and click **Save key**. It applies immediately and is kept separately from settings in `gemini-key.bin`. Windows encrypts it with DPAPI for the current user; Linux and macOS restrict the file to the current user. **Delete key** removes this saved copy. You can also use `GEMINI_API_KEY` in the environment or `%LOCALAPPDATA%\Dictophone.CrossPlatform\.env`; the key saved in Settings takes precedence, followed by the environment variable and then `.env`. Use `.env.example` as a reference; never commit your actual key.

By default, hold **F8** to record, release it to transcribe, and press **Ctrl+Alt+J** to open history. **Esc** cancels recording or processing. Wait for the green indicator before speaking. Set your own shortcuts in Settings; they save automatically after you release the keys. Existing installations keep their shortcuts and other settings.

Closing the window hides it in the tray. Click the tray icon to reopen it, or choose History / Settings from its menu. Choose Exit to quit. Start at login can also be enabled or disabled in Settings. `start.ps1` / `start.cmd` opens the installed app, or builds and opens a portable copy if needed.

Paste works in fields that accept Ctrl+V (Command+V on macOS). If the active window changes while Gemini is processing, copy the text from history. Pasting into an elevated Windows app requires Govori to run at the same privilege level. RightAlt may conflict with AltGr on some keyboard layouts.

## Configuration and privacy

User settings, history, recordings and credentials live in the per-user application data directory, outside this repository. On Windows this is `%LOCALAPPDATA%\Dictophone.CrossPlatform`. Portable copies can also read an ignored `.env` beside the executable or in their parent folders.

```dotenv
GEMINI_API_KEY=
GEMINI_MODEL=gemini-2.5-flash
DICTATION_CONTEXT=
```

`DICTATION_CONTEXT` is an optional list of spelling hints for names and technical terms. `GEMINI_MODEL` and `DICTATION_CONTEXT` are read from the selected `.env` file when no saved settings exist; saved settings take precedence afterward. Only `GEMINI_API_KEY` is also read directly from the process environment. API keys are never bundled into the executable.

Recorded audio and spelling hints are sent to Google's Gemini API over HTTPS. Pricing, quotas and service latency depend on your API account and model. Recording stops after three minutes by default. Failed or interrupted transcriptions, cancelled processing and responses without text retain audio for retry; deleting a history entry deletes its saved audio. Audio is removed after a non-empty transcription, when recording is cancelled before processing, or when captured audio is too short or quiet. Transcriptions are stored locally in history until you delete them. Google's processing and retention rules depend on your Gemini API account and service terms.

Speech is captured as mono PCM16 at 16 kHz when the device supports it, with the native device rate as a fallback. Transcription starts after you release the shortcut; it is not streaming transcription.

## Build and verification

```powershell
./build.ps1 -Verify
# Optional live API test with a prepared speech WAV:
./build.ps1 -Verify -LiveAudio path/to/speech.wav
# Install an already built Windows copy:
./install.ps1
```

Build output is ignored under `artifacts/publish/<runtime>`. Verification uses a separate data directory, briefly accesses the microphone, and opens a test input window. It checks shortcuts, language selection and history migration, tray reopening, WAV recording, indicator size, paste safety and clipboard restoration. Reports and UI images are written under `artifacts/verification`. The live API test expects speech containing the number 42 and the word "dictation" (English or Russian).

`build.ps1` accepts `-OutputDirectory`, `-BuildArtifactsDirectory` and `-VerificationDirectory` to keep generated files outside the checkout. `install.ps1 -SourceDirectory <published-directory>` installs a custom build. GitHub Actions builds on Windows, Linux and macOS; hardware and desktop integration checks require a real desktop session.

## Linux and macOS

```bash
bash build.sh
./artifacts/publish/linux-x64/Govori.CrossPlatform
# Or select a runtime explicitly:
bash build.sh osx-arm64
```

The shared interface, recording and Gemini client are cross-platform. Windows desktop integration has been tested. Linux and macOS adapters are implemented and require verification on those systems.

- **Linux / X11:** global shortcuts use XGrabKey; paste uses XTest. Install X11, Xtst, the audio backend libraries used by PortAudio, and Avalonia's desktop dependencies. Ubuntu packages typically include `libx11-6`, `libxtst6`, `libasound2`, `libpulse0` and `libfontconfig1`. Package names vary by distribution.
- **Wayland:** use the Record button, history and copy. Global shortcuts and automatic paste currently require an X11 session.
- **macOS:** allow Microphone, Input Monitoring and Accessibility access. Global shortcuts do not suppress the original key; function keys may require Fn. Distribution as a signed `.app` with microphone usage metadata is not implemented yet.

## Repository layout

- `src/Govori.Desktop`: app and platform adapters.
- `src/Govori.Desktop/Localization`: English and Russian string catalogs. Keep catalog keys and formatting placeholders in sync when adding translations.
- `build.ps1`, `build.sh`: self-contained publishing.
- `install.ps1`: per-user Windows installation and optional startup.
- `tools/check-repository.ps1`: checks the working tree and staged files for common secrets, non-empty credential literals, absolute machine paths, unreviewed URL hosts, user data and generated binaries. It also rejects ignored files that are already tracked. Approved hosts are limited to the Gemini endpoint and framework references; review any new host before adding it to the allowlist.

Run `pwsh -File tools/check-repository.ps1` before publishing. The same check runs in CI on all three operating systems. `.gitignore` excludes credentials, recordings, history, machine-specific files and build output; it does not protect files that were already committed. Publish the source through Git. Build output, verification reports and UI images are local artifacts and should not be added to Git or included in a source archive. The checker covers the current working tree and Git index; it does not scan past commits. Before publishing an existing repository, inspect its history separately and revoke any previously exposed credentials.

## License

Licensed under the MIT License. See [LICENSE](LICENSE).
