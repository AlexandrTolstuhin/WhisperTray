using WhisperEngine;
using Xunit;

namespace WhisperEngine.Tests;

public sealed class TranscriptStoreTests
{
    [Fact]
    public void GetPath_GroupsByDateAndPrefixesFileName()
    {
        var startedAt = new DateTimeOffset(2026, 10, 18, 9, 7, 5, TimeSpan.FromHours(5));

        var path = TranscriptStore.GetPath("transcriptions", startedAt);

        Assert.Equal(
            Path.Combine("transcriptions", "2026-10-18", "2026-10-18_09-07-05.md"),
            path);
    }

    [Theory]
    [InlineData("large-v3", "ggml-large-v3.bin")]
    [InlineData("large-v3-turbo", "ggml-large-v3-turbo.bin")]
    [InlineData("medium", "ggml-medium.bin")]
    [InlineData("small", "ggml-small.bin")]
    public void WhisperModels_FindReturnsExpectedFile(string id, string fileName)
    {
        Assert.Equal(fileName, WhisperModels.Find(id).FileName);
    }

    [Fact]
    public void PcmChunker_WaitsForSilenceAfterTargetAndFlushesRemainder()
    {
        var chunker = new PcmChunker(20, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.2), 0.05f);

        Assert.Empty(chunker.Add(ToPcm(10000, 10000, 10000, 10000, 10000, 10000, 10000, 10000, 10000, 10000, 10000, 10000)));
        var chunks = chunker.Add(ToPcm(0, 0));
        chunker.Add(ToPcm(10000, 10000, 10000));
        var remainder = chunker.Flush();

        var chunk = Assert.Single(chunks);
        Assert.Equal(TimeSpan.Zero, chunk.Start);
        Assert.Equal(28, chunk.Data.Length);
        Assert.True(chunk.HasSpeech);
        Assert.NotNull(remainder);
        Assert.Equal(TimeSpan.FromSeconds(1.4), remainder.Start);
        Assert.Equal(6, remainder.Data.Length);
    }

    [Fact]
    public void PcmChunker_MarksSilentChunk()
    {
        var chunker = new PcmChunker(20, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.2), 0.05f);

        var chunk = Assert.Single(chunker.Add(ToPcm(0, 0, 0, 0, 0, 0, 0, 0, 0, 0)));

        Assert.False(chunk.HasSpeech);
    }

    private static byte[] ToPcm(params short[] samples) =>
        samples.SelectMany(BitConverter.GetBytes).ToArray();
}

