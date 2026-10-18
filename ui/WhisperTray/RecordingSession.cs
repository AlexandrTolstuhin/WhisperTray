using NAudio.Extras;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.Threading.Channels;
using WhisperEngine;

namespace WhisperTray;

public enum SpeakerRole
{
    Self,
    Other
}

public sealed record SpeakerAudioChunk(SpeakerRole Speaker, PcmAudioChunk Audio);

public sealed class RecordingSession(string path, string? microphoneDeviceId) : IAsyncDisposable
{
    private const int SampleRate = 16000;
    private const int BytesPerSecond = SampleRate * 2;
    private readonly CancellationTokenSource pumpCancellation = new();
    private readonly Channel<SpeakerAudioChunk> audioChunks = Channel.CreateUnbounded<SpeakerAudioChunk>();
    private readonly Stopwatch captureClock = new();
    // shortcut: A fixed noise threshold can delay updates on noisy microphones; make it adaptive if that becomes noticeable.
    private readonly PcmChunker systemChunker = CreateChunker();
    private readonly PcmChunker microphoneChunker = CreateChunker();
    private long systemStartedAtTicks = -1;
    private long microphoneStartedAtTicks = -1;
    private RealtimeCaptureMixer? mixedAudio;
    private RealtimeCaptureMixer? systemAudio;
    private RealtimeCaptureMixer? microphoneAudio;
    private WasapiRecorder? systemRecorder;
    private WasapiRecorder? microphoneRecorder;
    private MMDevice? microphoneDevice;
    private WaveFileWriter? writer;
    private Task? pump;

    public string Path { get; } = path;

    public IAsyncEnumerable<SpeakerAudioChunk> ReadChunksAsync(CancellationToken cancellationToken = default) =>
        audioChunks.Reader.ReadAllAsync(cancellationToken);

    private static PcmChunker CreateChunker() => new(
        BytesPerSecond,
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMilliseconds(400),
        0.01f);

    public Task StartAsync()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);
        mixedAudio = new RealtimeCaptureMixer(format);
        systemAudio = new RealtimeCaptureMixer(format);
        microphoneAudio = new RealtimeCaptureMixer(format);
        systemRecorder = new WasapiRecorderBuilder().WithLoopbackCapture().WithPollingSync().Build();
        var microphoneBuilder = new WasapiRecorderBuilder().WithPollingSync();

        if (microphoneDeviceId is not null)
        {
            using var enumerator = new MMDeviceEnumerator();
            microphoneDevice = enumerator.GetDevice(microphoneDeviceId);
            microphoneBuilder.WithDevice(microphoneDevice);
        }

        microphoneRecorder = microphoneBuilder.Build();
        var mixedSystemInput = mixedAudio.AddInput(systemRecorder.WaveFormat);
        var mixedMicrophoneInput = mixedAudio.AddInput(microphoneRecorder.WaveFormat);
        var systemInput = systemAudio.AddInput(systemRecorder.WaveFormat);
        var microphoneInput = microphoneAudio.AddInput(microphoneRecorder.WaveFormat);
        systemRecorder.DataAvailable += (data, _, _, _) =>
        {
            Interlocked.CompareExchange(ref systemStartedAtTicks, captureClock.Elapsed.Ticks, -1);
            mixedSystemInput.AddSamples(data);
            systemInput.AddSamples(data);
        };
        microphoneRecorder.DataAvailable += (data, _, _, _) =>
        {
            Interlocked.CompareExchange(ref microphoneStartedAtTicks, captureClock.Elapsed.Ticks, -1);
            mixedMicrophoneInput.AddSamples(data);
            microphoneInput.AddSamples(data);
        };
        writer = new WaveFileWriter(Path, new WaveFormat(SampleRate, 16, 1));

        mixedAudio.Start();
        systemAudio.Start();
        microphoneAudio.Start();
        captureClock.Restart();
        systemRecorder.StartRecording();
        microphoneRecorder.StartRecording();
        pump = Task.Run(() => PumpAsync(pumpCancellation.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        pumpCancellation.Cancel();

        if (pump is not null)
        {
            try
            {
                await pump;
            }
            catch (OperationCanceledException)
            {
            }
        }

        systemRecorder?.StopRecording();
        microphoneRecorder?.StopRecording();
        writer?.Dispose();
        writer = null;
    }

    public async ValueTask DisposeAsync()
    {
        writer?.Dispose();
        if (systemRecorder is not null)
        {
            await systemRecorder.DisposeAsync();
        }

        if (microphoneRecorder is not null)
        {
            await microphoneRecorder.DisposeAsync();
        }

        microphoneDevice?.Dispose();

        pumpCancellation.Dispose();
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var mixedSamples = new float[3200];
        var sourceSamples = new float[mixedSamples.Length];
        var bytes = new byte[mixedSamples.Length * 2];
        Exception? failure = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = mixedAudio!.Read(mixedSamples, 0, mixedSamples.Length);

                if (count > 0)
                {
                    var byteCount = ConvertToPcm(mixedSamples, count, bytes);
                    writer!.Write(bytes, 0, byteCount);
                }

                var systemCount = QueueSourceChunks(
                    systemAudio!,
                    sourceSamples,
                    bytes,
                    systemChunker,
                    SpeakerRole.Other,
                    systemStartedAtTicks);
                var microphoneCount = QueueSourceChunks(
                    microphoneAudio!,
                    sourceSamples,
                    bytes,
                    microphoneChunker,
                    SpeakerRole.Self,
                    microphoneStartedAtTicks);

                if (count == 0 && systemCount == 0 && microphoneCount == 0)
                {
                    await Task.Delay(5, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            QueueRemainder(systemChunker, SpeakerRole.Other, systemStartedAtTicks);
            QueueRemainder(microphoneChunker, SpeakerRole.Self, microphoneStartedAtTicks);

            audioChunks.Writer.TryComplete(failure);
        }
    }

    private int QueueSourceChunks(
        RealtimeCaptureMixer source,
        float[] samples,
        byte[] bytes,
        PcmChunker chunker,
        SpeakerRole speaker,
        long startedAtTicks)
    {
        var count = source.Read(samples, 0, samples.Length);

        if (count == 0)
        {
            return 0;
        }

        var byteCount = ConvertToPcm(samples, count, bytes);

        foreach (var chunk in chunker.Add(bytes.AsSpan(0, byteCount)))
        {
            QueueChunk(speaker, chunk, startedAtTicks);
        }

        return count;
    }

    private void QueueRemainder(PcmChunker chunker, SpeakerRole speaker, long startedAtTicks)
    {
        var remainder = chunker.Flush();

        if (remainder is not null)
        {
            QueueChunk(speaker, remainder, startedAtTicks);
        }
    }

    private void QueueChunk(SpeakerRole speaker, PcmAudioChunk chunk, long startedAtTicks)
    {
        if (chunk.HasSpeech)
        {
            audioChunks.Writer.TryWrite(new SpeakerAudioChunk(
                speaker,
                chunk with { Start = chunk.Start + TimeSpan.FromTicks(Math.Max(0, startedAtTicks)) }));
        }
    }

    private static int ConvertToPcm(float[] samples, int count, byte[] bytes)
    {
        for (var index = 0; index < count; index++)
        {
            var sample = (short)(Math.Clamp(samples[index], -1f, 1f) * short.MaxValue);
            bytes[index * 2] = (byte)sample;
            bytes[index * 2 + 1] = (byte)(sample >> 8);
        }

        return count * 2;
    }
}

