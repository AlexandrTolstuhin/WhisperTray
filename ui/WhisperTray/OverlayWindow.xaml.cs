using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WhisperEngine;

namespace WhisperTray;

public partial class OverlayWindow : Window
{
    private readonly AppPaths paths;
    private readonly UserSettings settings;
    private readonly DispatcherTimer microphoneTestTimer;
    private readonly DispatcherTimer statusTimer;
    private RecordingSession? recording;
    private Task? liveTranscription;
    private string? liveTranscriptPath;
    private WasapiRecorder? microphoneTestRecorder;
    private MMDevice? microphoneTestDevice;
    private DateTimeOffset startedAt;
    private DateTimeOffset microphoneTestEndsAt;
    private float microphoneTestPeak;
    private bool initializing = true;
    private bool allowClose;

    public OverlayWindow(AppPaths paths)
    {
        InitializeComponent();
        this.paths = paths;
        settings = UserSettings.Load();
        microphoneTestTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, MicrophoneTestTimer_Tick, Dispatcher);
        microphoneTestTimer.Stop();
        statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        statusTimer.Tick += (_, _) => HideStatus();

        ModelComboBox.ItemsSource = WhisperModels.All;
        ModelComboBox.SelectedItem = WhisperModels.Find(settings.ModelId);

        var inputs = GetAudioInputs();
        InputComboBox.ItemsSource = inputs;
        InputComboBox.SelectedItem = inputs.FirstOrDefault(input => input.Id == settings.InputDeviceId) ?? inputs[0];
        AutostartCheckBox.IsChecked = StartupRegistration.IsEnabled;
        initializing = false;
    }

    public void OfferCall(string processName)
    {
        if (recording is null)
        {
            ShowStatus($"Обнаружен активный звонок: {processName}", true);
        }
    }

    public void PositionAtBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 20;
        Top = area.Bottom - Height - 20;
    }

    public async Task CloseForExitAsync()
    {
        statusTimer.Stop();
        await StopMicrophoneTestAsync(false);

        if (recording is not null)
        {
            await StopAndTranscribeAsync();
        }

        allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!allowClose)
        {
            e.Cancel = true;
            _ = StopMicrophoneTestAsync(false);
            Hide();
        }

        base.OnClosing(e);
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        RecordButton.IsEnabled = false;

        try
        {
            if (recording is null)
            {
                await StartRecordingAsync();
            }
            else
            {
                await StopAndTranscribeAsync();
            }
        }
        catch (Exception exception)
        {
            ShowStatus($"Ошибка: {exception.Message}", true);
            SetSettingsEnabled(recording is null);
        }
        finally
        {
            RecordButton.IsEnabled = true;
        }
    }

    private async Task StartRecordingAsync()
    {
        var model = (WhisperModel)ModelComboBox.SelectedItem;
        var input = (AudioInputOption)InputComboBox.SelectedItem;
        var transcriber = new WhisperTranscriber(paths.Models, model);
        startedAt = DateTimeOffset.Now;
        Directory.CreateDirectory(paths.TemporaryRecordings);
        var audioPath = Path.Combine(paths.TemporaryRecordings, $"{startedAt:yyyy-MM-dd_HH-mm-ss}.wav");
        var session = new RecordingSession(audioPath, input.Id);

        try
        {
            await session.StartAsync();
            var transcriptPath = await TranscriptStore.CreateLiveAsync(
                paths.Transcriptions,
                startedAt,
                model.DisplayName);
            recording = session;
            liveTranscriptPath = transcriptPath;
            liveTranscription = TranscribeLiveAsync(session, transcriber, transcriptPath);
        }
        catch
        {
            await session.StopAsync();
            await session.DisposeAsync();
            throw;
        }

        SetSettingsEnabled(false);
        ShowStatus(transcriber.IsModelReady
            ? $"Запись и транскрибация · {Path.GetFileName(liveTranscriptPath)}"
            : $"Запись · загружается {model.DisplayName}");
        RecordButton.Content = "Остановить запись";
    }

    private async Task StopAndTranscribeAsync()
    {
        var completedRecording = recording!;
        var transcription = liveTranscription!;
        var transcriptPath = liveTranscriptPath!;
        recording = null;
        liveTranscription = null;
        liveTranscriptPath = null;
        await completedRecording.StopAsync();
        await completedRecording.DisposeAsync();

        ShowStatus("Завершаю последний фрагмент...");

        try
        {
            await transcription;
            await TranscriptStore.CompleteAsync(transcriptPath, DateTimeOffset.Now);
            File.Delete(completedRecording.Path);
            ShowStatus($"Готово: {Path.GetFileName(transcriptPath)}", true);
        }
        catch (Exception exception)
        {
            ShowStatus($"Не удалось распознать: {exception.Message} Запись сохранена: {completedRecording.Path}", true);
        }
        finally
        {
            SetSettingsEnabled(true);
            RecordButton.Content = "Начать транскрибацию";
        }
    }

    private async Task TranscribeLiveAsync(
        RecordingSession source,
        WhisperTranscriber transcriber,
        string transcriptPath)
    {
        try
        {
            await using var session = await transcriber.CreateSessionAsync();

            await foreach (var chunk in source.ReadChunksAsync())
            {
                if (chunk.Audio.Data.Length < 32000)
                {
                    continue;
                }

                using var audio = CreateWaveStream(chunk.Audio.Data);
                var segments = await session.TranscribeAsync(audio);
                var adjustedSegments = segments
                    .Select(segment => new TranscriptSegment(
                        segment.Start + chunk.Audio.Start,
                        segment.End + chunk.Audio.Start,
                        segment.Text))
                    .ToArray();
                var speaker = chunk.Speaker == SpeakerRole.Self ? "Я" : "Собеседник";
                await TranscriptStore.AppendAsync(transcriptPath, speaker, adjustedSegments);

                var completedThrough = chunk.Audio.Start + TimeSpan.FromSeconds(chunk.Audio.Data.Length / 32000d);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (recording == source)
                    {
                        ShowStatus($"Идёт запись · расшифровано {completedThrough:mm\\:ss}");
                    }
                });
            }
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() =>
                ShowStatus($"Фоновая транскрибация остановлена: {exception.Message}", true));
            throw;
        }
    }

    private static MemoryStream CreateWaveStream(byte[] pcm)
    {
        var buffer = new MemoryStream();

        using (var writer = new WaveFileWriter(buffer, new WaveFormat(16000, 16, 1)))
        {
            writer.Write(pcm, 0, pcm.Length);
        }

        return new MemoryStream(buffer.ToArray(), false);
    }

    private async void MicrophoneTest_Click(object sender, RoutedEventArgs e)
    {
        if (microphoneTestRecorder is not null)
        {
            await StopMicrophoneTestAsync();
            return;
        }

        try
        {
            var input = (AudioInputOption)InputComboBox.SelectedItem;
            using var enumerator = new MMDeviceEnumerator();
            microphoneTestDevice = input.Id is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                : enumerator.GetDevice(input.Id);
            microphoneTestRecorder = new WasapiRecorderBuilder()
                .WithDevice(microphoneTestDevice)
                .WithPollingSync()
                .Build();
            microphoneTestRecorder.StartRecording();
            microphoneTestEndsAt = DateTimeOffset.Now.AddSeconds(8);
            microphoneTestPeak = 0;
            MicrophoneLevel.Value = 0;
            MicrophoneTestButton.Content = "Стоп";
            ShowStatus("Говорите: индикатор покажет уровень микрофона");
            SetSettingsEnabled(false);
            MicrophoneTestButton.IsEnabled = true;
            RecordButton.IsEnabled = false;
            microphoneTestTimer.Start();
        }
        catch (Exception exception)
        {
            await StopMicrophoneTestAsync(false);
            ShowStatus($"Не удалось проверить микрофон: {exception.Message}", true);
        }
    }

    private async void MicrophoneTestTimer_Tick(object? sender, EventArgs e)
    {
        var peak = microphoneTestDevice?.AudioMeterInformation.MasterPeakValue ?? 0;
        microphoneTestPeak = Math.Max(microphoneTestPeak, peak);
        MicrophoneLevel.Value = peak;

        if (DateTimeOffset.Now >= microphoneTestEndsAt)
        {
            await StopMicrophoneTestAsync();
        }
    }

    private async Task StopMicrophoneTestAsync(bool showResult = true)
    {
        microphoneTestTimer.Stop();
        MicrophoneTestButton.IsEnabled = false;

        var recorder = microphoneTestRecorder;
        microphoneTestRecorder = null;

        if (recorder is not null)
        {
            recorder.StopRecording();
            await recorder.DisposeAsync();
        }

        microphoneTestDevice?.Dispose();
        microphoneTestDevice = null;
        MicrophoneLevel.Value = 0;
        MicrophoneTestButton.Content = "Проверить";
        SetSettingsEnabled(recording is null);
        RecordButton.IsEnabled = true;

        if (showResult)
        {
            ShowStatus(microphoneTestPeak >= 0.01f
                ? "Микрофон работает, голосовой сигнал получен"
                : "Сигнала не видно. Проверьте устройство и доступ к микрофону", true);
        }
    }

    private void Settings_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (initializing || ModelComboBox.SelectedItem is not WhisperModel model || InputComboBox.SelectedItem is not AudioInputOption input)
        {
            return;
        }

        settings.ModelId = model.Id;
        settings.InputDeviceId = input.Id;

        try
        {
            settings.Save();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"Не удалось сохранить настройки: {exception.Message}", true);
        }
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupRegistration.SetEnabled(AutostartCheckBox.IsChecked == true);
            ShowStatus(AutostartCheckBox.IsChecked == true
                ? "Автозапуск включён"
                : "Автозапуск выключен", true);
        }
        catch (Exception exception)
        {
            AutostartCheckBox.IsChecked = StartupRegistration.IsEnabled;
            ShowStatus($"Не удалось изменить автозапуск: {exception.Message}", true);
        }
    }

    private async void Hide_Click(object sender, RoutedEventArgs e)
    {
        await StopMicrophoneTestAsync(false);
        Hide();
    }

    private void SetSettingsEnabled(bool enabled)
    {
        ModelComboBox.IsEnabled = enabled;
        InputComboBox.IsEnabled = enabled;
        MicrophoneTestButton.IsEnabled = enabled;
    }

    private void ShowStatus(string text, bool autoHide = false)
    {
        statusTimer.Stop();
        StatusText.Text = text;

        if (autoHide)
        {
            statusTimer.Start();
        }
    }

    private void HideStatus()
    {
        statusTimer.Stop();
        StatusText.Text = "Локальная транскрибация";
    }

    private static IReadOnlyList<AudioInputOption> GetAudioInputs()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        var inputs = new List<AudioInputOption>
        {
            new(null, $"По умолчанию · {defaultDevice.FriendlyName}")
        };

        foreach (var device in devices)
        {
            inputs.Add(new AudioInputOption(device.ID, device.FriendlyName));
        }

        return inputs;
    }

    private sealed record AudioInputOption(string? Id, string DisplayName);
}
