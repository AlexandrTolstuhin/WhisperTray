using System.Text;

namespace WhisperEngine;

public static class TranscriptStore
{
    public static string GetPath(string root, DateTimeOffset startedAt)
    {
        var date = startedAt.ToString("yyyy-MM-dd");
        return Path.Combine(root, date, $"{startedAt:yyyy-MM-dd_HH-mm-ss}.md");
    }

    public static async Task<string> WriteAsync(
        string root,
        DateTimeOffset startedAt,
        IReadOnlyCollection<TranscriptSegment> segments,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(root, startedAt);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var text = new StringBuilder()
            .AppendLine("# Транскрибация")
            .AppendLine()
            .AppendLine($"Начало: {startedAt:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine("Модель: large-v3")
            .AppendLine();

        foreach (var segment in segments)
        {
            text.AppendLine($"[{segment.Start:hh\\:mm\\:ss} - {segment.End:hh\\:mm\\:ss}] {segment.Text.Trim()}");
        }

        await File.WriteAllTextAsync(path, text.ToString(), new UTF8Encoding(false), cancellationToken);
        return path;
    }

    public static async Task<string> CreateLiveAsync(
        string root,
        DateTimeOffset startedAt,
        string model,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(root, startedAt);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = new StringBuilder()
            .AppendLine("# Транскрибация")
            .AppendLine()
            .AppendLine($"Начало: {startedAt:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"Модель: {model}")
            .AppendLine()
            .ToString();
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), cancellationToken);
        return path;
    }

    public static async Task AppendAsync(
        string path,
        string speaker,
        IReadOnlyCollection<TranscriptSegment> segments,
        CancellationToken cancellationToken = default)
    {
        if (segments.Count == 0)
        {
            return;
        }

        var text = new StringBuilder();

        foreach (var segment in segments)
        {
            text.AppendLine($"[{segment.Start:hh\\:mm\\:ss} - {segment.End:hh\\:mm\\:ss}] {speaker}: {segment.Text.Trim()}");
        }

        await File.AppendAllTextAsync(path, text.ToString(), new UTF8Encoding(false), cancellationToken);
    }

    public static Task CompleteAsync(
        string path,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default) =>
        File.AppendAllTextAsync(
            path,
            $"{Environment.NewLine}Завершено: {completedAt:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}",
            new UTF8Encoding(false),
            cancellationToken);
}

