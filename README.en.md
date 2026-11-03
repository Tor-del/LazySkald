# LazySkald

[Документация на русском](README.md)

## GitHub builds and releases

GitHub Actions checks the application, builds a Windows x64 installer and portable
ZIP, and tests isolated installation, two upgrades and uninstall on pushes and pull
requests. Download the `LazySkald-win-x64` artifact from **Windows build and checks**.

For the initial publication, create an **empty** GitHub repository without a README,
license or .gitignore, then run these commands from this folder, replacing
`OWNER/REPOSITORY` with your repository:

```powershell
git remote add origin https://github.com/OWNER/REPOSITORY.git
git push -u origin main
git tag v0.5.0
git push origin v0.5.0
```

A `v<version>` tag starts **Publish release**. After all checks pass, it publishes
a GitHub Release containing the installer `.exe`, a portable `.zip` with the .NET
runtime, and `SHA256SUMS.txt`. The tag must exactly match `Version` in
`Directory.Build.props`. For subsequent releases, update that version, commit and
push the changes, then push the matching new tag. No additional secrets are needed;
only the publishing job has write access through the built-in `GITHUB_TOKEN`.

To build the same packages locally, run `./tools/build-release.ps1` in PowerShell 7
on Windows with the SDK from `global.json`. Internet access is needed for dependencies
and Inno Setup. The output folders `artifacts/release` and
`artifacts/LazySkald-win-x64-<version>` must not already exist. Packages are unsigned.

LazySkald reads text from a selected screen region using local Windows OCR
and a choice of Windows TTS, Microsoft Edge TTS, or an OpenAI-compatible server.
Windows 11 x64-compatible systems are supported. The installer and portable build
include the .NET runtime; Docker and Python are not required.

## Start

Run `LazySkald-<version>-Setup-x64.exe`, or open `LazySkald.exe` in the portable
folder. Choose **Application → Interface language → English** and click **Save**.
The interface also supports Russian, Simplified Chinese, Japanese, Spanish,
Korean, French and German. Language changes take effect immediately; click
**Save** to keep the selection for the next launch.

The settings follow the pipeline in seven tabs:

- **Recognition:** OCR provider (Windows 11 OCR by default), language and capture method.
- **Translation:** optional LLM translation, provider, model and prompt.
- **Genderization:** optional narrator gender detection using LM Studio, Ollama or OpenAI API, with independent connection profiles, model discovery, prompt, temperature and timeout.
- **Speech:** default voice settings; when genderization is enabled, a role list with add/remove controls for female and male roles. Select a role to edit its own TTS provider, voice, rate, playback device and server settings.
- **Checks:** recognized or manually entered text, translation, detected gender and speech preview.
- **Controls:** five independent hotkeys and game mode.
- **Application:** interface language and tray behavior.

Genderization runs after translation and receives both original and spoken text.
It returns male, female or unknown; ambiguous or mixed narrator genders use the
default voice. Each role has an independent TTS provider and full settings, including server URL,
model, encrypted key and format. New roles start with a copy of the default profile.
You can remove a role to use the default profile for that gender. Turning genderization
off preserves the role profiles for later use. Previous voice-only roles migrate with
their original TTS provider. Detected gender accompanies the selected voice in the TTS request.
Genderization is disabled by default. Its API keys are encrypted with Windows DPAPI.
Stopping cancels the LLM request. Failed, empty or truncated responses stop speech;
unexpected non-empty classification responses use unknown gender.

Press **Ctrl + Alt + R**, drag to select text, and release the mouse button.
Press it again to recognize another fragment and append it to the reading queue without stopping current speech.
The Controls tab offers a shortcut to recognize the last selected area again and append it to the queue. It captures fresh content on the original monitor; without a previous area, it opens selection. The area is remembered until the app closes, and cancelling selection preserves it. This shortcut is unassigned by default.
The Controls tab also offers separate start/stop, cancel current and go to next,
and cancel all shortcuts; these are unassigned by default. Start/stop cancels all
work when busy. Press Delete or Backspace in a shortcut field to clear it, then
save settings. Each action must use a different shortcut.
**Esc** cancels selection. The original application is restored before OCR begins.

## Playback devices

Choose **Windows default device** or a specific output on the Speech tab and save.
The selection uses a stable endpoint ID and applies to the next reading. Each
added role can use its own playback device. Refresh the list after connecting new hardware. If a saved device is
unavailable, playback reports an error rather than silently using another output.
The app does not change Windows master volume or the audio output used by games.

## Providers and privacy

- **Windows TTS:** installed Microsoft voices, fully offline.
- **Microsoft Edge TTS:** text is sent to Microsoft's consumer speech service.
  This is not the subscription-based Azure Speech API. Voice availability can vary.
- **OpenAI-compatible server:** configure the complete `/v1/audio/speech` URL,
  model, voice, optional API key and MP3 or PCM WAV response. Text is sent to this
  address. The key is encrypted with Windows DPAPI for the current user.

OCR and screenshot processing are local. Screenshots and recognized text are not
stored in the diagnostic log. Settings and logs are under
`%LOCALAPPDATA%/ScreenReader`.

OCR continuation lines are joined with spaces when the next nonempty line does
not start with an uppercase letter. Uppercase starts keep their line break.

## Installation and updates

The per-user Inno Setup installer installs under
`%LOCALAPPDATA%/Programs/LazySkald`. Future installers with the same AppId
close the app gracefully, uninstall the registered old version, and install the
new version. User settings are preserved. Old portable copies are not registered
installations and should be closed before the first install.

See [installer/README.md](installer/README.md) in the source repository for the
reusable installer configuration, release commands and isolated upgrade tests.

## Development

Use .NET 10 SDK:

```powershell
dotnet build src/ScreenReader.App -c Release
dotnet run --project tests/ScreenReader.Checks -- --devices --windows --ui
./build-installer.ps1
```

`ScreenReader.Speech` defines the provider contract. Register new implementations
through `SpeechProviderRegistry` without changing capture, OCR, cancellation or
the audio pipeline. The shared player supports MP3 and PCM/Float32 WAV.

The icon has an editable SVG source and 16–256 px ICO variants. Regenerate it with
`node tools/build-icons.cjs [path-to-sharp]`; generated assets are already included.

See `THIRD-PARTY-NOTICES.md` for dependency licenses.

## Cache and queue

Enable caching on the Cache tab and save settings. Caching is off by default.
Completed readings persist in `%LOCALAPPDATA%\ScreenReader\cache`, including
original text, translation, detected gender and audio. Exact text and matching
translation, genderization and TTS settings reuse the saved track without
provider requests. Output device changes do not invalidate audio. Cancelled or
failed readings are never saved.

Select an entry to inspect its text and translation, play it again, delete it,
or clear the entire cache with one button. Replay joins the end of the queue.
Disabling caching preserves existing entries. There is currently no automatic
size limit.

The Queue tab shows active jobs in submission order, their text, submission
time and processing stage. Completed jobs disappear. Cancel the current job
or the entire queue using the buttons on this tab.

## LLM translation

Enable **Translation → Translate before speaking**, choose LM Studio, Ollama or
Custom OpenAI API, then refresh models or enter a model ID. Base URLs default to
http://127.0.0.1:1234/v1, http://127.0.0.1:11434 and https://api.openai.com/v1.
Start the local server and install/load a chat model before refreshing.

The target language (default ru-RU) controls the translation prompt and voice list,
independently of OCR language. Select a suitable voice on Speech after changing it.
Choose Russian, English, Simplified Chinese, Japanese, Spanish, Korean, French or
German from the editable language menu, or enter another locale code manually.
These eight languages are also listed in Recognition; each requires its Windows
OCR package. Edge TTS includes male and female voice defaults for all eight
languages and loads the full voice list from the service when refreshed.
The system prompt supports {targetLanguage}; source text is a separate user message.
Temperature defaults to 0.2 and can be omitted for models that do not support it.
The request timeout defaults to 120 seconds. URL, model and encrypted key are saved
separately for each provider. Save to apply to screen capture; Listen uses current
controls and shows the translation separately from the source.

Translation is off by default for existing settings. When enabled, source text is
sent to the configured LLM server and only the translation is sent to TTS. Empty,
truncated and failed translations stop speech. The usual stop hotkey also cancels
translation. Text, translations and API keys are not written to the diagnostic log.

Protocol references: [LM Studio](https://lmstudio.ai/docs/developer/openai-compat),
[Ollama chat](https://docs.ollama.com/api/chat),
[Ollama models](https://docs.ollama.com/api/tags).

## Set up Ollama and TranslateGemma

Open **Ollama** to check the local server, installed models, RAM and dedicated VRAM. **Install / download model** downloads the official installer if needed, installs Ollama for the current user, starts the server and pulls the selected TranslateGemma through the streaming API. Downloads show percentages and bytes; installation shows an activity indicator. Downloads can be cancelled and retried. Installation is allowed to finish safely.

Available sizes: **4B (~3.3 GB)**, **12B (~8.1 GB)** and **27B (~17 GB)**. Automatic selection recommends 12B with at least 16 GB RAM and 12 GB VRAM, 27B with at least 32 GB RAM and 24 GB VRAM, otherwise 4B to limit CPU latency. These are conservative guidelines with room for the system and context, not GPU compatibility guarantees. Manual selection is available. Ollama itself needs about 4 GB of disk space.

After downloading, click **Use for translation**, then **Save**. This enables translation and selects the local model while preserving other provider and genderization profiles. Ollama appears first in both LLM lists; existing saved provider selections remain unchanged.

Model sizes: [official TranslateGemma catalog](https://ollama.com/library/translategemma/tags). Installation: [Ollama for Windows](https://docs.ollama.com/windows).
