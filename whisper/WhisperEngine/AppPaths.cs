namespace WhisperEngine;

public sealed record AppPaths(string Root)
{
    public string Models => Path.Combine(Root, "models");

    public string TemporaryRecordings => Path.Combine(Root, ".tmp", "recordings");

    public string Transcriptions => Path.Combine(Root, "transcriptions");

    public static AppPaths Discover(string startPath)
    {
        for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WhisperTray.slnx")))
            {
                return new AppPaths(directory.FullName);
            }
        }

        return new AppPaths(Path.GetFullPath(startPath));
    }
}

