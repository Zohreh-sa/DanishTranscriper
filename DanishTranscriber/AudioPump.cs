namespace DanishTranscriber;

public static class AudioPump
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(3);
    const int MaxChunkBytes = PcmBuffer.BytesPerSecond * 12;

    // Capture must be stopped before the token is cancelled so the final device
    // buffer is appended before this method performs its last drain.
    public static async Task RunAsync(
        PcmBuffer audio,
        CancellationToken stop,
        Func<float[], Task> transcribe,
        TimeSpan? interval = null,
        Func<ReadOnlyMemory<byte>, bool>? containsSpeech = null)
    {
        containsSpeech ??= data => SpeechDetector.ContainsSpeech(data.Span);

        while (true)
        {
            try
            {
                await Task.Delay(interval ?? DefaultInterval, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Cancellation means "finish gracefully": drain once more below.
            }

            var finishing = stop.IsCancellationRequested;
            var pcm = audio.Drain();
            try
            {
                for (var offset = 0; offset < pcm.Length; offset += MaxChunkBytes)
                {
                    var count = Math.Min(MaxChunkBytes, pcm.Length - offset);
                    var segment = pcm.AsMemory(offset, count);
                    if (!containsSpeech(segment))
                        continue;

                    // Whisper works more reliably with at least one second of input.
                    var samples = new float[Math.Max(count / 2, 16000)];
                    for (var i = 0; i < count / 2; i++)
                        samples[i] = (short)(pcm[offset + i * 2] | pcm[offset + i * 2 + 1] << 8) / 32768f;

                    try
                    {
                        await transcribe(samples);
                    }
                    finally
                    {
                        Array.Clear(samples);
                    }
                }
            }
            finally
            {
                Array.Clear(pcm);
            }

            // If Stop arrived during inference, finishing was false and another
            // iteration drains audio that arrived while inference was running.
            if (finishing)
                break;
        }
    }
}
