namespace DanishTranscriber;

// Only unprocessed 16 kHz, mono, signed 16-bit samples are retained.
public sealed class PcmBuffer(int capacity = 32000 * 120)
{
    public const int BytesPerSecond = 32000;
    private readonly Queue<byte[]> buffers = new();
    private readonly object gate = new();
    private int length;

    public bool TryAppend(byte[] data, int count)
    {
        if (count < 0 || count > data.Length || count % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        lock (gate)
        {
            if (count > capacity - length) return false;
            if (count > 0) buffers.Enqueue(data.AsSpan(0, count).ToArray());
            length += count;
            return true;
        }
    }

    public byte[] Drain()
    {
        lock (gate)
        {
            var result = new byte[length];
            var offset = 0;
            while (buffers.TryDequeue(out var data))
            {
                data.CopyTo(result, offset);
                offset += data.Length;
                Array.Clear(data);
            }
            length = 0;
            return result;
        }
    }

    public void Clear() => Array.Clear(Drain());
}
