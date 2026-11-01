using ZKeyAudioLink.Core.Configuration;
using ZKeyAudioLink.Core.Diagnostics;

namespace ZKeyAudioLink.Core.Media;

public enum ImportStatus
{
    Imported,
    Duplicate,
    Failed
}

public sealed record ImportResult(ImportStatus Status, MediaEntry? Entry, string Message);

/// <summary>
/// Owns the app-managed copies of audio files and the persisted media list.
/// The original imported file is only read; deletion never touches it.
/// </summary>
public sealed class MediaLibrary : IDisposable
{
    private readonly SettingsStore _store;
    private readonly AppLog? _log;
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private readonly string _mediaDirectory;

    public MediaLibrary(SettingsStore store, AppLog? log = null, string? mediaDirectory = null)
    {
        _store = store;
        _log = log;
        _mediaDirectory = mediaDirectory ?? AppPaths.MediaDirectory;
    }

    public IReadOnlyList<MediaEntry> Entries => _store.Settings.Media;

    public MediaEntry? Find(Guid id) => _store.Settings.Media.FirstOrDefault(m => m.Id == id);

    public MediaEntry? Selected => _store.Settings.SelectedMediaId is { } id ? Find(id) : null;

    public void SetSelected(Guid? id)
    {
        _store.Settings.SelectedMediaId = id;
        _store.Save();
    }

    public async Task<ImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_mediaDirectory);

            if (!AudioFileInspector.IsSupportedExtension(sourcePath))
            {
                return new ImportResult(ImportStatus.Failed, null, "只支持 WAV 和 MP3 文件。");
            }

            if (!AudioFileInspector.TryInspect(sourcePath, out var info, out var inspectError) || info is null)
            {
                return new ImportResult(ImportStatus.Failed, null, inspectError);
            }

            string hash;
            try
            {
                hash = await AudioFileInspector.ComputeHashAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new ImportResult(ImportStatus.Failed, null, $"读取文件失败：{ex.Message}");
            }

            var duplicate = _store.Settings.Media.FirstOrDefault(m => m.SourceHash == hash);
            if (duplicate is not null)
            {
                return new ImportResult(ImportStatus.Duplicate, duplicate, $"《{duplicate.DisplayName}》已经在库里，没有重复导入。");
            }

            var entry = new MediaEntry
            {
                Id = Guid.NewGuid(),
                DisplayName = Path.GetFileNameWithoutExtension(sourcePath),
                SourceFileName = Path.GetFileName(sourcePath),
                SourceHash = hash,
                Duration = info.Duration,
                StartOffset = TimeSpan.Zero,
                ImportedAt = DateTimeOffset.Now,
                LibraryFileName = Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath).ToLowerInvariant()
            };

            var destination = entry.GetLibraryPath(_mediaDirectory);
            try
            {
                await Task.Run(() => File.Copy(sourcePath, destination, overwrite: false), cancellationToken).ConfigureAwait(false);
                var copied = new FileInfo(destination);
                if (!copied.Exists || copied.Length == 0)
                {
                    throw new IOException("复制后的文件为空。");
                }
            }
            catch (Exception ex)
            {
                TryDelete(destination);
                return new ImportResult(ImportStatus.Failed, null, $"导入失败：{ex.Message}");
            }

            _store.Settings.Media.Add(entry);
            if (!_store.Save())
            {
                _store.Settings.Media.Remove(entry);
                TryDelete(destination);
                return new ImportResult(ImportStatus.Failed, null, "写入配置失败，导入已回滚。");
            }

            _log?.Info("media imported", new { name = entry.DisplayName, duration = entry.Duration.TotalSeconds });
            return new ImportResult(ImportStatus.Imported, entry, "导入完成。");
        }
        catch (OperationCanceledException)
        {
            return new ImportResult(ImportStatus.Failed, null, "导入已取消。");
        }
        finally
        {
            _importGate.Release();
        }
    }

    public bool TrySetStartOffset(Guid id, TimeSpan offset, out string error)
    {
        var entry = Find(id);
        if (entry is null)
        {
            error = "条目不存在。";
            return false;
        }

        if (offset < TimeSpan.Zero || offset >= entry.Duration)
        {
            error = "开始时间必须在 0 与总时长之间（不含总时长）。";
            return false;
        }

        entry.StartOffset = offset;
        _store.Save();
        error = string.Empty;
        return true;
    }

    public bool Remove(Guid id, out string error)
    {
        var entry = Find(id);
        if (entry is null)
        {
            error = "条目不存在。";
            return false;
        }

        _store.Settings.Media.Remove(entry);
        if (_store.Settings.SelectedMediaId == id)
        {
            _store.Settings.SelectedMediaId = null;
        }

        if (!_store.Save())
        {
            error = "配置保存失败，条目未删除。";
            return false;
        }

        TryDelete(entry.GetLibraryPath(_mediaDirectory));
        _log?.Info("media removed", new { name = entry.DisplayName });
        error = string.Empty;
        return true;
    }

    public IReadOnlyList<MediaEntry> FindMissingFiles() =>
        _store.Settings.Media.Where(m => !File.Exists(m.GetLibraryPath(_mediaDirectory))).ToList();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A leftover library copy is harmless; the entry is already gone.
        }
    }

    public void Dispose() => _importGate.Dispose();
}
