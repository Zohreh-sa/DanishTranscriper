# DanishTranscriber
Windows 11 MVP. Danish speech-to-text runs locally. Audio is kept in RAM and is not intentionally saved. Every Start creates a DOCX in `Documents\Danish Transcripts`.

## GitHub build
1. Create an empty GitHub repository.
2. Upload the contents of this ZIP.
3. Commit to `main`.
4. Actions → Build Windows App → Run workflow.
5. Download artifact `DanishTranscriber-Windows-x64`.

First run downloads the multilingual Whisper small model (~466 MB). After that, classroom transcription is offline.
Whisper.net CPU runtime requires Windows 11/Server 2022+ and Microsoft Visual C++ 2022 x64 Redistributable.

## Reliability and privacy
- Stop and closing the window finish pending transcription before saving. This can take time on slower CPUs.
- Each session has a unique DOCX filename. Clear also updates that session's saved document; new speech may subsequently appear.
- Unprocessed audio is bounded to two minutes per buffer. If processing cannot keep up, recording stops and reports the gap instead of silently discarding audio or growing memory indefinitely.
- The model is downloaded to a temporary file, checked against a pinned SHA-256 digest, and only then installed. Cached models are checked before each session. Failed/cancelled downloads can be retried.
- Audio is not deliberately written to disk or uploaded. Windows paging/crash dumps may still contain process memory. DOCX transcripts are **unencrypted** in Documents, which may be synced by OneDrive or another service. Copy All places text on the Windows clipboard.

## Development and regression tests
Use the .NET 8 SDK on Windows:

```powershell
dotnet run --project DanishTranscriber.Tests/DanishTranscriber.Tests.csproj -c Release
dotnet publish DanishTranscriber/DanishTranscriber.csproj -c Release -r win-x64 --self-contained true
dotnet list DanishTranscriber/DanishTranscriber.csproj package --vulnerable --include-transitive
```

The regression runner exits nonzero on failure and does not access a microphone or download a Whisper model. Pull requests run these tests and the Windows publish workflow. See [the review report](REVIEW.md) for findings and remaining manual checks.
