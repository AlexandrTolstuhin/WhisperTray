using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WhisperTray;

public sealed class CallDetector : IDisposable
{
    private static readonly HashSet<string> CallProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ms-teams",
        "telegram",
        "yandextelemost",
        "ktalk"
    };

    private readonly System.Threading.Timer timer;
    private DateTimeOffset lastNotification;
    private int polling;

    public CallDetector()
    {
        timer = new System.Threading.Timer(Poll);
    }

    public event EventHandler<string>? CallDetected;

    public void Start() => timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(2));

    public void Dispose() => timer.Dispose();

    private void Poll(object? state)
    {
        if (Interlocked.Exchange(ref polling, 1) != 0)
        {
            return;
        }

        try
        {
            var process = FindActiveCallProcess();

            if (process is not null && DateTimeOffset.Now - lastNotification > TimeSpan.FromMinutes(30))
            {
                lastNotification = DateTimeOffset.Now;
                CallDetected?.Invoke(this, process);
            }
        }
        catch
        {
        }
        finally
        {
            Volatile.Write(ref polling, 0);
        }
    }

    private static string? FindActiveCallProcess()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;

        for (var index = 0; index < sessions.Count; index++)
        {
            using var session = sessions[index];

            if (session.State != AudioSessionState.AudioSessionStateActive ||
                session.AudioMeterInformation.MasterPeakValue < 0.001f)
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById((int)session.GetProcessID);

                if (CallProcesses.Contains(process.ProcessName))
                {
                    return process.ProcessName;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}

