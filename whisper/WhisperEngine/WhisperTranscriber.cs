using Whisper.net;
using Whisper.net.Ggml;

namespace WhisperEngine;

public sealed class WhisperTranscriber(string modelsDirectory, WhisperModel model)
{
    private readonly string modelPath = Path.Combine(modelsDirectory, model.FileName);

    public bool IsModelReady => File.Exists(modelPath);

    public async Task EnsureModelAsync(CancellationToken cancellationToken = default)
    {
        if (IsModelReady)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        var downloadPath = modelPath + ".download";

        try
        {
            await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(model.GgmlType);
            await using (var output = File.Create(downloadPath))
            {
                await modelStream.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.Move(downloadPath, modelPath, true);
        }
        catch
        {
            File.Delete(downloadPath);
            throw;
        }
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        string wavePath,
        CancellationToken cancellationToken = default)
    {
        await using var session = await CreateSessionAsync(cancellationToken);
        await using var audio = File.OpenRead(wavePath);
        return await session.TranscribeAsync(audio, cancellationToken);
    }

    public async Task<WhisperTranscriptionSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureModelAsync(cancellationToken);
        return new WhisperTranscriptionSession(WhisperFactory.FromPath(modelPath));
    }
}

public sealed class WhisperTranscriptionSession(WhisperFactory factory) : IAsyncDisposable
{
    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        Stream audio,
        CancellationToken cancellationToken = default)
    {
        if (audio.CanSeek && audio.Length < 32044)
        {
            throw new InvalidOperationException("Recording contains less than one second of audio.");
        }

        using var processor = factory.CreateBuilder()
            .WithLanguage("auto")
            .Build();
        var segments = new List<TranscriptSegment>();
        await foreach (var segment in processor.ProcessAsync(audio, cancellationToken))
        {
            segments.Add(new TranscriptSegment(segment.Start, segment.End, segment.Text));
        }

        return segments;
    }

    public ValueTask DisposeAsync()
    {
        factory.Dispose();
        return ValueTask.CompletedTask;
    }
}

