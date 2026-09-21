# Code and security review — 2026-09-21

Reviewed all source and workflow files at base commit `8abaf91fe77bd0819ed818b8cdcf332fe52f8a9d`.

## Findings and changes

| Priority | Finding in the original code | Correction |
| --- | --- | --- |
| High | Build fails with NU1202: NAudio 3.1.0 targets .NET 9, but the application targets .NET 8. | Reference the required NAudio.WinMM 2.2.1 component, compatible with .NET 8, and lock resolved application dependencies. |
| High | Stop cancels inference and never flushes audio collected since the last 12-second interval. Closing also loses pending speech. | Await the microphone's final buffer and drain all pending audio, including stop during inference and clips shorter than two seconds. Closing waits for completion. |
| High | The processing task is discarded; an inference/save exception silently ends transcription while capture continues. Restart can overlap an old task. | Retain/await the worker, report failures, serialize stop, isolate session lifetime, and release capture resources on failures. |
| Medium | All audio is retained for the entire session and copied every 12 seconds, with no bound. | Retain only unprocessed audio, cap the pending buffer at 120 seconds, and explicitly stop/report overload. Inference handles at most 12 seconds per call. Clear consumed sample arrays. |
| Medium | A cancelled download leaves a final model file; later starts trust its existence. The model comes from a mutable branch without verification. | Download to a unique temporary file; verify the pinned model digest before replacement and before reuse. Cancellation/failure cleans temporary downloads. This is integrity hardening, not evidence of an exploited vulnerability. |
| Medium | Session files use second-resolution names, allowing rapid starts or multiple instances to overwrite transcripts. Failed saves may leave plaintext temporary documents. | Use GUID-suffixed session and temporary filenames, replace only after successful document creation, and clean temporary files on failure. |
| Low | Clear does not immediately update the DOCX; clipboard failures are unhandled. | Persist clearing and display file/clipboard errors. |
| Low | CI only runs after pushes to main, with no regression checks or explicit token permissions. | Run on pull requests, set contents:read, run regression tests and dependency vulnerability reporting. |

## Verification

- Release build completed with zero warnings and zero errors using .NET SDK 8.0.425 on Windows.
- Self-contained Windows x64 publish completed successfully with the workflow's publish settings.
- All 11 automated regression cases passed: bounded/copy-safe buffer; short final audio; stop during inference; backlog chunking; inference failure/array clearing; offline cache reuse; corrupt cache replacement; hash mismatch; cancellation; Danish DOCX round trip/clear; locked-file failure preserving the old document and removing temporary text.
- Resolved all 16 direct/transitive NuGet packages and compared their versions with the NuGet vulnerability feed on the review date: no matching advisories. This is a point-in-time package advisory check, not proof that native libraries are vulnerability-free.
- Local package downloads used HTTPS and were restored from a local feed because this environment's .NET HTTPS transport could not connect to NuGet. Normal CI uses NuGet directly.

## Remaining validation and limits

- Live microphone capture, device disconnects, close/restart interaction, real Danish recognition, and native Whisper inference still require an interactive Windows smoke test. Automated audio tests inject samples and substitute inference; they do not exercise the microphone or native model.
- Transcripts remain intentionally unencrypted in Documents. OS paging/crash dumps can contain audio; clipboard history and folder sync can retain text. No audio-upload path or embedded credential was found in the reviewed source.
- Abrupt process termination or power failure can still lose audio not yet transcribed. Model loading and speech inference cannot be interrupted by Stop without sacrificing the current segment; graceful stopping may take time.
- Review is limited to this repository's current source and resolved package advisories. No penetration testing, GitHub account/organization audit, or exhaustive native dependency audit was performed.

## Sources

- [NAudio 3.1.0 target frameworks](https://www.nuget.org/packages/NAudio/3.1.0)
- [Pinned upstream model and SHA-256](https://huggingface.co/ggerganov/whisper.cpp/blob/90a64d80ea254cf67575b41a5971f972c79f7b45/ggml-small.bin)
- [NuGet vulnerability feed](https://api.nuget.org/v3/vulnerabilities/index.json)
