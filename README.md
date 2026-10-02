# DanishTranscriber

Windows 11 Danish speech-to-text application. Recognition runs locally with Whisper. Audio is kept only in bounded memory and is not intentionally saved. Every Start creates a DOCX in "Documents\\Danish Transcripts".

## Behaviour

- Checks the microphone buffer every 3 seconds.
- Ignores quiet chunks before they reach Whisper.
- Suppresses an immediately repeated result.
- Stops gracefully and transcribes the final buffered speech before saving.
- Limits unprocessed audio to 120 seconds and stops with a visible error if inference cannot keep up.
- Downloads a revision-pinned Whisper small model on first run and verifies its SHA-256 before use.
- Works offline after the verified model is cached.

## Build

GitHub Actions restores, builds, runs regression tests, checks package vulnerability information, publishes a self-contained Windows x64 application, and uploads "DanishTranscriber-Windows-x64".

Open Actions → Test and Build Windows App, select the latest successful run, and download the artifact.

Whisper.net CPU runtime requires Windows 11/Server 2022+ and Microsoft Visual C++ x64 Redistributable.
