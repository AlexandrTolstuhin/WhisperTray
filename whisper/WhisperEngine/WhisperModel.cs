using Whisper.net.Ggml;

namespace WhisperEngine;

public sealed record WhisperModel
{
    internal WhisperModel(string id, string displayName, string fileName, GgmlType ggmlType)
    {
        Id = id;
        DisplayName = displayName;
        FileName = fileName;
        GgmlType = ggmlType;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string FileName { get; }

    internal GgmlType GgmlType { get; }
}

public static class WhisperModels
{
    public static IReadOnlyList<WhisperModel> All { get; } =
    [
        new("large-v3", "Large v3 · точнее", "ggml-large-v3.bin", GgmlType.LargeV3),
        new("large-v3-turbo", "Large v3 Turbo · быстрее", "ggml-large-v3-turbo.bin", GgmlType.LargeV3Turbo),
        new("medium", "Medium · баланс", "ggml-medium.bin", GgmlType.Medium),
        new("small", "Small · экономнее", "ggml-small.bin", GgmlType.Small)
    ];

    public static WhisperModel Default => All[0];

    public static WhisperModel Find(string? id) =>
        All.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;
}
