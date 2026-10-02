namespace DanishTranscriber;

public static class SpeechDetector
{
    const int SampleRate = 16000;
    const int BytesPerSample = 2;
    const int FrameSamples = SampleRate / 50; // 20 ms
    const int FrameBytes = FrameSamples * BytesPerSample;

    public static bool ContainsSpeech(ReadOnlySpan<byte> pcm)
    {
        var frameCount = pcm.Length / FrameBytes;
        if (frameCount < 4)
            return false;

        var rmsValues = new double[frameCount];
        var peakValues = new double[frameCount];

        for (var frame = 0; frame < frameCount; frame++)
        {
            double sumSquares = 0;
            var peak = 0;
            var frameStart = frame * FrameBytes;

            for (var i = 0; i < FrameSamples; i++)
            {
                var offset = frameStart + i * BytesPerSample;
                var sample = (short)(pcm[offset] | pcm[offset + 1] << 8);
                var absolute = Math.Abs((int)sample);
                peak = Math.Max(peak, absolute);
                sumSquares += (double)sample * sample;
            }

            rmsValues[frame] = Math.Sqrt(sumSquares / FrameSamples) / short.MaxValue;
            peakValues[frame] = peak / (double)short.MaxValue;
        }

        // Estimate the room noise from the quietest fifth of the current chunk.
        var orderedRms = rmsValues.ToArray();
        Array.Sort(orderedRms);
        var noiseFloor = Math.Min(orderedRms[Math.Max(0, orderedRms.Length / 5 - 1)], 0.004);
        var rmsThreshold = Math.Max(0.006, noiseFloor * 2.8);
        var peakThreshold = Math.Max(0.020, noiseFloor * 4.0);

        var totalSpeechFrames = 0;
        var consecutiveSpeechFrames = 0;
        for (var frame = 0; frame < frameCount; frame++)
        {
            if (rmsValues[frame] >= rmsThreshold && peakValues[frame] >= peakThreshold)
            {
                totalSpeechFrames++;
                consecutiveSpeechFrames++;
                if (consecutiveSpeechFrames >= 3 || totalSpeechFrames >= 5)
                    return true;
            }
            else
            {
                consecutiveSpeechFrames = 0;
            }
        }

        return false;
    }
}
