namespace WhisperEngine;

public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

