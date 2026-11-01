using NAudio.Wave;

namespace ZKeyAudioLink.Core.Media;

public sealed record AudioFileInfo(TimeSpan Duration, int SampleRate, int Channels, string Codec);

public static class AudioFileInspector
{
    public static readonly string[] SupportedExtensions = { ".wav", ".mp3" };

    public static bool IsSupportedExtension(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool TryInspect(string path, out AudioFileInfo? info, out string error)
    {
        info = null;
        error = string.Empty;

        if (!File.Exists(path))
        {
            error = "文件不存在。";
            return false;
        }

        var extension = Path.GetExtension(path);
        if (!IsSupportedExtension(path))
        {
            error = $"不支持的格式 {extension}。首版支持 WAV（PCM/浮点）和 MP3。";
            return false;
        }

        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length == 0)
            {
                error = "文件为空。";
                return false;
            }

            if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                using var wave = new WaveFileReader(path);
                if (wave.WaveFormat.Encoding != WaveFormatEncoding.Pcm &&
                    wave.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
                {
                    error = $"WAV 编码为 {wave.WaveFormat.Encoding}，首版只承诺 PCM / IEEE Float。";
                    return false;
                }

                if (wave.TotalTime <= TimeSpan.Zero || double.IsNaN(wave.TotalTime.TotalSeconds))
                {
                    error = "音频时长无效。";
                    return false;
                }

                info = new AudioFileInfo(wave.TotalTime, wave.WaveFormat.SampleRate, wave.WaveFormat.Channels, wave.WaveFormat.Encoding.ToString());
                return true;
            }

            using var reader = new AudioFileReader(path);
            if (reader.TotalTime <= TimeSpan.Zero || double.IsNaN(reader.TotalTime.TotalSeconds) || double.IsInfinity(reader.TotalTime.TotalSeconds))
            {
                error = "无法读取音频时长（文件可能损坏或缺少解码器）。";
                return false;
            }

            info = new AudioFileInfo(reader.TotalTime, reader.WaveFormat.SampleRate, reader.WaveFormat.Channels, "MP3");
            return true;
        }
        catch (Exception ex)
        {
            error = $"无法解码音频：{ex.Message}";
            return false;
        }
    }

    public static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
