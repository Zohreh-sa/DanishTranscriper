namespace DanishTranscriber;

public static class AudioPump
{
    // The caller cancels stop only after capture has delivered its final buffer.
    public static async Task RunAsync(PcmBuffer audio, CancellationToken stop,
        Func<float[], Task> transcribe, TimeSpan? interval = null)
    {
        while (true)
        {
            try { await Task.Delay(interval ?? TimeSpan.FromSeconds(12), stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            // If stop arrives during inference, the next iteration must drain again.
            var finishing = stop.IsCancellationRequested;
            var pcm = audio.Drain();
            try
            {
                for (var offset = 0; offset < pcm.Length; offset += PcmBuffer.BytesPerSecond * 12)
                {
                    var count = Math.Min(PcmBuffer.BytesPerSecond * 12, pcm.Length - offset);
                    // Whisper needs at least a second; pad short final speech with silence.
                    var samples = new float[Math.Max(count / 2, 16000)];
                    for (var i = 0; i < count / 2; i++)
                        samples[i] = (short)(pcm[offset + i * 2] | pcm[offset + i * 2 + 1] << 8) / 32768f;
                    try { await transcribe(samples); }
                    finally { Array.Clear(samples); }
                }
            }
            finally { Array.Clear(pcm); }
            if (finishing) break;
        }
    }
}
