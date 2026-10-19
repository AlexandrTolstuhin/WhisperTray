using Whisper.net;
using Whisper.net.Ggml;

namespace WhisperEngine;

public sealed class WhisperTranscriber(string modelsDirectory, WhisperModel model)
{
    private const string VadModelFileName = "ggml-silero-v6.2.0.bin";
    private readonly string modelPath = Path.Combine(modelsDirectory, model.FileName);
    private readonly string vadModelPath = Path.Combine(modelsDirectory, VadModelFileName);

    public bool IsModelReady => File.Exists(modelPath) && File.Exists(vadModelPath);

    public async Task EnsureModelAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);

        if (!File.Exists(modelPath))
        {
            await DownloadAsync(
                modelPath,
                () => WhisperGgmlDownloader.Default.GetGgmlModelAsync(model.GgmlType),
                cancellationToken);
        }

        if (!File.Exists(vadModelPath))
        {
            await DownloadAsync(
                vadModelPath,
                () => WhisperGgmlDownloader.Default.GetGgmlSileroVadModelAsync(
                    SileroVadType.V6_2_0,
                    cancellationToken),
                cancellationToken);
        }
    }

    private static async Task DownloadAsync(
        string destinationPath,
        Func<Task<Stream>> download,
        CancellationToken cancellationToken)
    {
        var downloadPath = destinationPath + ".download";

        try
        {
            await using var modelStream = await download();
            await using (var output = File.Create(downloadPath))
            {
                await modelStream.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.Move(downloadPath, destinationPath, true);
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
        return new WhisperTranscriptionSession(
            WhisperFactory.FromPath(modelPath),
            WhisperVadFactory.FromPath(vadModelPath));
    }
}

public sealed class WhisperTranscriptionSession(
    WhisperFactory factory,
    WhisperVadFactory vadFactory) : IAsyncDisposable
{
    private readonly WhisperVadProcessor vadProcessor = vadFactory.CreateBuilder()
        .WithUseGpu(false)
        .Build();

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        Stream audio,
        CancellationToken cancellationToken = default)
    {
        if (!audio.CanSeek)
        {
            throw new InvalidOperationException("Audio stream must be seekable.");
        }

        if (audio.Length < 32044)
        {
            throw new InvalidOperationException("Recording contains less than one second of audio.");
        }

        var speech = await vadProcessor.DetectSpeechAsync(audio, cancellationToken);
        audio.Position = 0;

        if (speech.Count == 0)
        {
            return [];
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
        vadProcessor.Dispose();
        vadFactory.Dispose();
        factory.Dispose();
        return ValueTask.CompletedTask;
    }
}

