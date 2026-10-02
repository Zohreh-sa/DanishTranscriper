# Code review status — 2026-09-30

The main application now uses the reviewed components instead of leaving them as unused helper files:

- PcmBuffer bounds unprocessed audio and copies reusable device buffers.
- AudioPump checks audio every 3 seconds, ignores silence, drains final audio on Stop, and bounds each inference call.
- ModelCache downloads from a pinned model revision, verifies SHA-256, and never promotes a partial download.
- TranscriptDocument uses unique session and temporary names with atomic replacement.
- MainWindow retains and awaits the transcription worker, stops capture before the final drain, handles worker failures, saves Clear immediately, and closes gracefully.
- CI builds the solution, runs regression tests, reports vulnerable dependencies, and only then publishes the Windows artifact.

Remaining validation: live microphone capture, real Danish recognition, microphone disconnection, and long-running inference require an interactive Windows smoke test. Transcripts are intentionally stored unencrypted in the user's Documents folder.
