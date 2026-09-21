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
