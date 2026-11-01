namespace ZKeyAudioLink.Core.Media;

public sealed class MediaEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>File name inside the app media library (never an absolute source path).</summary>
    public string LibraryFileName { get; set; } = string.Empty;

    public string SourceHash { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public TimeSpan StartOffset { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.Now;

    public string GetLibraryPath() => GetLibraryPath(AppPaths.MediaDirectory);

    public string GetLibraryPath(string mediaDirectory) => Path.Combine(mediaDirectory, LibraryFileName);

    public MediaEntry Clone() => (MediaEntry)MemberwiseClone();
}
