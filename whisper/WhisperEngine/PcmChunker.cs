namespace WhisperEngine;

public sealed record PcmAudioChunk(TimeSpan Start, byte[] Data, bool HasSpeech);

public sealed class PcmChunker(
    int bytesPerSecond,
    TimeSpan targetDuration,
    TimeSpan silenceDuration,
    float silenceThreshold)
{
    private readonly int targetBytes = checked((int)(bytesPerSecond * targetDuration.TotalSeconds));
    private readonly int silenceBytesRequired = checked((int)(bytesPerSecond * silenceDuration.TotalSeconds));
    private readonly int analysisFrameBytes = Math.Max(2, bytesPerSecond / 10) & ~1;
    private readonly MemoryStream buffer = new();
    private long emittedBytes;
    private int silenceBytes;
    private bool hasSpeech;

    public IReadOnlyList<PcmAudioChunk> Add(ReadOnlySpan<byte> data)
    {
        List<PcmAudioChunk>? chunks = null;

        while (!data.IsEmpty)
        {
            var count = Math.Min(data.Length, analysisFrameBytes);
            var frame = data[..count];
            buffer.Write(frame);
            data = data[count..];
            var isSilent = IsSilent(frame);
            silenceBytes = isSilent ? silenceBytes + count : 0;
            hasSpeech |= !isSilent;

            if (buffer.Length >= targetBytes && silenceBytes >= silenceBytesRequired)
            {
                chunks ??= [];
                chunks.Add(TakeChunk());
            }
        }

        return chunks ?? [];
    }

    public PcmAudioChunk? Flush() => buffer.Length == 0 ? null : TakeChunk();

    private PcmAudioChunk TakeChunk()
    {
        var data = buffer.ToArray();
        var start = TimeSpan.FromSeconds((double)emittedBytes / bytesPerSecond);
        emittedBytes += data.Length;
        buffer.SetLength(0);
        silenceBytes = 0;
        var chunk = new PcmAudioChunk(start, data, hasSpeech);
        hasSpeech = false;
        return chunk;
    }

    private bool IsSilent(ReadOnlySpan<byte> data)
    {
        double sum = 0;
        var samples = data.Length / 2;

        for (var index = 0; index < samples; index++)
        {
            var sample = (short)(data[index * 2] | data[index * 2 + 1] << 8);
            var normalized = sample / (double)short.MaxValue;
            sum += normalized * normalized;
        }

        return samples == 0 || Math.Sqrt(sum / samples) < silenceThreshold;
    }
}
