using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ZKeyAudioLink.Core.Audio;
using ZKeyAudioLink.Core.Media;

namespace ZKeyAudioLink.Doctor;

/// <summary>
/// Standalone verification tool. It never writes into the user's configuration
/// and is meant to be run from a terminal:
///   list                         enumerate endpoints and mix formats
///   render --in file [--offset]  render the mixed send/monitor streams to WAV
///   linktest [--seconds 3]       loop a tone through CABLE Input → CABLE Output
/// </summary>
public static class Program
{
    private const int SampleRate = AudioConstants.SampleRate;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var command = args.Length == 0 ? "list" : args[0].ToLowerInvariant();
            return command switch
            {
                "list" => ListDevices(),
                "render" => await RenderAsync(args.Skip(1).ToArray()),
                "linktest" => LinkTest(args.Skip(1).ToArray()),
                "capture" => Capture(args.Skip(1).ToArray()),
                "-h" or "--help" or "help" => Help(),
                _ => Help()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAILED: " + ex.Message);
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            Z 键音频连麦 · 验证工具

            list
                列出所有音频端点、endpoint ID 和设备混音格式。

            render --in <音频文件> [--offset mm:ss.fff] [--out <输出目录>] [--mic-tone]
                不打开任何播放设备，用离线混音器把“发送给虚拟声卡的流”和“耳机监听流”
                渲染为 send.wav / monitor.wav。--mic-tone 会把一路 440 Hz 测试人声混进发送流，
                用来确认监听流不含人声。

            linktest [--seconds 3]
                需要已安装 VB-CABLE。向 CABLE Input 播放 1 kHz 测试音，
                同时从 CABLE Output 录制，验证虚拟线两个方向都能通。
                这一步不能替代 QQ / OOPZ 的真实对端通话测试。

            capture --device <名称片段> [--seconds 5] [--out capture.wav]
                从指定录音端点录制一段 48 kHz float 立体声 WAV，并打印 RMS。
                用于在真实播放时检查 CABLE Output 是否真的有信号。
            """);
        return 0;
    }

    private static int ListDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            Console.WriteLine($"=== {(flow == DataFlow.Render ? "播放（输出）设备，应用写入" : "录音（输入）设备，通话软件读取")} ===");
            using var collection = enumerator.EnumerateAudioEndPoints(flow, DeviceState.All);
            for (var i = 0; i < collection.Count; i++)
            {
                using var device = collection[i];
                var mix = "?";
                try
                {
                    using var client = device.CreateAudioClient();
                    var format = client.MixFormat;
                    mix = $"{format.SampleRate} Hz, {format.Channels} ch, {format.BitsPerSample} bit, {format.Encoding}";
                }
                catch (Exception ex)
                {
                    mix = ex.GetType().Name;
                }

                var state = device.State switch
                {
                    DeviceState.Active => "ACTIVE",
                    DeviceState.Disabled => "DISABLED",
                    DeviceState.NotPresent => "NOT PRESENT",
                    DeviceState.Unplugged => "UNPLUGGED",
                    _ => device.State.ToString()
                };
                Console.WriteLine($"  [{state}] {device.FriendlyName}");
                Console.WriteLine($"      id  = {device.ID}");
                Console.WriteLine($"      mix = {mix}");
            }
        }

        try
        {
            using var enumerator2 = new MMDeviceEnumerator();
            var cableIn = Find(enumerator2, DataFlow.Render, "CABLE Input");
            var cableOut = Find(enumerator2, DataFlow.Capture, "CABLE Output");
            Console.WriteLine();
            Console.WriteLine(cableIn is null
                ? "VB-CABLE: 未检测到 CABLE Input（播放端），请先安装基础版 VB-CABLE。"
                : "VB-CABLE: 已检测到 CABLE Input。");
            Console.WriteLine(cableOut is null
                ? "VB-CABLE: 未检测到 CABLE Output（录音端）。"
                : "VB-CABLE: 已检测到 CABLE Output。");
        }
        catch
        {
            // ignore
        }

        return 0;
    }

    private static async Task<int> RenderAsync(string[] args)
    {
        var input = Option(args, "--in");
        if (string.IsNullOrWhiteSpace(input))
        {
            Console.Error.WriteLine("缺少 --in <音频文件>。");
            return 2;
        }

        input = Path.GetFullPath(input);
        if (!AudioFileInspector.TryInspect(input, out var info, out var error) || info is null)
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var offset = TimeSpan.Zero;
        var offsetText = Option(args, "--offset");
        if (offsetText is not null)
        {
            if (!TimeCode.TryParse(offsetText, out offset) || offset < TimeSpan.Zero || offset >= info.Duration)
            {
                Console.Error.WriteLine("--offset 超出文件时长或格式不合法。");
                return 2;
            }
        }

        var outDirectory = Option(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, "artifacts");
        Directory.CreateDirectory(outDirectory);
        var sendPath = Path.Combine(outDirectory, "send.wav");
        var monitorPath = Path.Combine(outDirectory, "monitor.wav");
        var micTone = args.Contains("--mic-tone", StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"input:   {input}");
        Console.WriteLine($"format:  {info.SampleRate} Hz / {info.Channels} ch / {info.Codec}, {info.Duration}");
        Console.WriteLine($"offset:  {TimeCode.Format(offset)}");
        Console.WriteLine($"mic:     {(micTone ? "440 Hz tone at 0.1 amplitude (test only)" : "silence")}");

        using var sendSink = new FloatWavSink(sendPath);
        using var monitorSink = new FloatWavSink(monitorPath);
        using var source = new FileMaterialSource(input, offset);
        await source.PrefillAsync(CancellationToken.None);

        IMicSource mic = micTone ? new ToneMicSource() : new SilenceMicSource();
        var completion = new TaskCompletionSource<MaterialCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        using var engine = new MixEngine(mic, sendSink, monitorSink);
        engine.MaterialCompletion += c => completion.TrySetResult(c);
        engine.Start();
        engine.StartMaterial(1, source, offset);
        var result = await completion.Task.WaitAsync(TimeSpan.FromMinutes(30));
        stopwatch.Stop();
        engine.Dispose();

        sendSink.FinalizeFile();
        monitorSink.FinalizeFile();
        Console.WriteLine($"reason:  {result.Reason}");
        Console.WriteLine($"send:    {sendPath} ({sendSink.FrameCount} frames, {sendSink.FrameCount / (double)SampleRate:F2} s)");
        Console.WriteLine($"monitor: {monitorPath} ({monitorSink.FrameCount} frames)");
        Console.WriteLine($"render:  {stopwatch.ElapsedMilliseconds} ms wall clock");
        return result.Reason == MaterialStopReason.EndOfStream ? 0 : 3;
    }

    private static int LinkTest(string[] args)
    {
        var seconds = 3.0;
        var secondsText = Option(args, "--seconds");
        if (secondsText is not null && double.TryParse(secondsText, out var parsed))
        {
            seconds = Math.Clamp(parsed, 1, 15);
        }

        using var enumerator = new MMDeviceEnumerator();
        var renderInfo = Find(enumerator, DataFlow.Render, "CABLE Input");
        var captureInfo = Find(enumerator, DataFlow.Capture, "CABLE Output");
        if (renderInfo is null || captureInfo is null)
        {
            Console.Error.WriteLine("未找到 CABLE Input / CABLE Output。请先安装基础版 VB-CABLE 并重启。");
            return 2;
        }

        using var render = enumerator.GetDevice(renderInfo.ID);
        using var capture = enumerator.GetDevice(captureInfo.ID);
        Console.WriteLine($"CABLE Input:  {render.FriendlyName}");
        Console.WriteLine($"CABLE Output: {capture.FriendlyName}");

        var captured = new List<float>();
        var gate = new object();
        var start = new Stopwatch();
        double firstSignal = -1;
        double preSignalRms = 0;

        using var recorder = new WasapiRecorderBuilder()
            .WithDevice(capture)
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(50)
            .WithFormat(AudioConstants.InternalWaveFormat)
            .Build();
        Console.WriteLine($"capture format: {recorder.WaveFormat}");

        recorder.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
        {
            var floats = MemoryMarshal.Cast<byte, float>(buffer);
            lock (gate)
            {
                for (var frame = 0; frame < floats.Length / 2; frame++)
                {
                    var value = (floats[frame * 2] + floats[frame * 2 + 1]) * 0.5f;
                    if (firstSignal < 0 && Math.Abs(value) > 0.01f)
                    {
                        firstSignal = start.Elapsed.TotalSeconds;
                    }

                    captured.Add(floats[frame * 2]);
                    captured.Add(floats[frame * 2 + 1]);
                }
            }
        };

        using var player = new WasapiPlayerBuilder()
            .WithDevice(render)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(80)
            .Build();
        var tone = new ToneWaveProvider(TimeSpan.FromSeconds(seconds), frequency: 1000, amplitude: 0.3f);
        player.Init(tone);

        recorder.StartRecording();
        Thread.Sleep(300);
        lock (gate)
        {
            var preFrames = Math.Min(captured.Count, SampleRate * 2 / 2);
            preSignalRms = Rms(captured.Take(preFrames).ToList());
            captured.Clear();
        }

        start.Restart();
        player.Play();
        Thread.Sleep((int)(seconds * 1000) + 800);
        player.Stop();
        recorder.StopRecording();

        float[] snapshot;
        lock (gate)
        {
            snapshot = captured.ToArray();
        }

        var frames = snapshot.Length / 2;
        Console.WriteLine($"captured: {frames} frames ({frames / (double)SampleRate:F2} s)");
        Console.WriteLine($"pre-roll RMS: {preSignalRms:F5}");
        Console.WriteLine($"signal RMS:   {Rms(snapshot):F5}");
        Console.WriteLine(firstSignal < 0
            ? "RESULT: FAIL - CABLE Output 没有检测到测试音。"
            : $"RESULT: PASS - CABLE Output 检测到测试音；从开始播放到收到首个信号约 {firstSignal * 1000:F0} ms（含捕获缓冲，不作精确延迟结论）。");
        return firstSignal < 0 ? 3 : 0;
    }

    private static int Capture(string[] args)
    {
        var deviceName = Option(args, "--device") ?? "CABLE Output";
        var seconds = 5.0;
        var secondsText = Option(args, "--seconds");
        if (secondsText is not null && double.TryParse(secondsText, out var parsed))
        {
            seconds = Math.Clamp(parsed, 1, 60);
        }

        var outputPath = Path.GetFullPath(Option(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, "capture.wav"));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var enumerator = new MMDeviceEnumerator();
        using var device = Find(enumerator, DataFlow.Capture, deviceName);
        if (device is null)
        {
            Console.Error.WriteLine($"未找到录音端点：{deviceName}");
            return 2;
        }

        Console.WriteLine($"device: {device.FriendlyName}");
        Console.WriteLine($"record: {seconds:F1} s -> {outputPath}");

        using var sink = new FloatWavSink(outputPath);
        double sum = 0;
        long samples = 0;
        using var recorder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(50)
            .WithFormat(AudioConstants.InternalWaveFormat)
            .Build();
        recorder.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
        {
            var floats = MemoryMarshal.Cast<byte, float>(buffer);
            sink.Write(floats);
            for (var i = 0; i < floats.Length; i += 17)
            {
                sum += floats[i] * floats[i];
                samples++;
            }
        };

        recorder.StartRecording();
        Thread.Sleep((int)(seconds * 1000));
        recorder.StopRecording();
        sink.FinalizeFile();
        var rms = samples > 0 ? Math.Sqrt(sum / samples) : 0;
        Console.WriteLine($"captured: {sink.FrameCount} frames, rms={rms:F5}");
        return 0;
    }

    private static double Rms(IReadOnlyList<float> samples)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / samples.Count);
    }

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static MMDevice? Find(MMDeviceEnumerator enumerator, DataFlow flow, string namePart)
    {
        using var collection = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        for (var i = 0; i < collection.Count; i++)
        {
            var device = collection[i];
            if (device.FriendlyName.Contains(namePart, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }

            device.Dispose();
        }

        return null;
    }
}

internal sealed class SilenceMicSource : IMicSource
{
    public int BufferedFrames => 0;

    public int Read(Span<float> destination)
    {
        destination.Clear();
        return 0;
    }
}

internal sealed class ToneMicSource : IMicSource
{
    private double _phase;

    public int BufferedFrames => AudioConstants.SampleRate;

    public int Read(Span<float> destination)
    {
        var frames = destination.Length / AudioConstants.Channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (float)(0.1 * Math.Sin(_phase));
            _phase += 2 * Math.PI * 440 / AudioConstants.SampleRate;
            destination[frame * 2] = value;
            destination[frame * 2 + 1] = value;
        }

        return frames;
    }
}

internal sealed class ToneWaveProvider : IWaveProvider
{
    private readonly long _totalFrames;
    private long _framesWritten;
    private double _phase;

    public ToneWaveProvider(TimeSpan duration, double frequency, float amplitude)
    {
        _totalFrames = (long)(duration.TotalSeconds * AudioConstants.SampleRate);
        Frequency = frequency;
        Amplitude = amplitude;
        WaveFormat = AudioConstants.InternalWaveFormat;
    }

    public WaveFormat WaveFormat { get; }
    public double Frequency { get; }
    public float Amplitude { get; }

    public int Read(Span<byte> buffer)
    {
        var floats = MemoryMarshal.Cast<byte, float>(buffer);
        var frames = floats.Length / 2;
        for (var frame = 0; frame < frames; frame++)
        {
            if (_framesWritten >= _totalFrames)
            {
                floats[(frame * 2)..].Clear();
                return buffer.Length;
            }

            var value = (float)(Amplitude * Math.Sin(_phase));
            _phase += 2 * Math.PI * Frequency / AudioConstants.SampleRate;
            floats[frame * 2] = value;
            floats[frame * 2 + 1] = value;
            _framesWritten++;
        }

        return buffer.Length;
    }
}

/// <summary>Writes IEEE-float stereo 48 kHz WAV without loading the whole stream.</summary>
internal sealed class FloatWavSink : IAudioSink, IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private long _dataBytes;
    private bool _finalized;

    public FloatWavSink(string path)
    {
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);
        WriteHeader(0);
    }

    public string Name => "wav";
    public int BufferedFrames => 0;
    public int CapacityFrames => int.MaxValue;
    public long DroppedFrames => 0;
    public long FrameCount { get; private set; }

    public void Write(ReadOnlySpan<float> frames)
    {
        _writer.Write(MemoryMarshal.AsBytes(frames));
        _dataBytes += frames.Length * sizeof(float);
        FrameCount += frames.Length / 2;
    }

    public void Clear() { }

    public void FinalizeFile()
    {
        if (_finalized)
        {
            return;
        }

        _finalized = true;
        _writer.Flush();
        _stream.Seek(0, SeekOrigin.Begin);
        WriteHeader(_dataBytes);
        _writer.Flush();
        _stream.Seek(0, SeekOrigin.End);
    }

    private void WriteHeader(long dataBytes)
    {
        _writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        _writer.Write((uint)(36 + dataBytes));
        _writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        _writer.Write(Encoding.ASCII.GetBytes("fmt "));
        _writer.Write(16);
        _writer.Write((ushort)3); // IEEE float
        _writer.Write((ushort)AudioConstants.Channels);
        _writer.Write(AudioConstants.SampleRate);
        _writer.Write(AudioConstants.SampleRate * AudioConstants.Channels * sizeof(float));
        _writer.Write((ushort)(AudioConstants.Channels * sizeof(float)));
        _writer.Write((ushort)32);
        _writer.Write(Encoding.ASCII.GetBytes("data"));
        _writer.Write((uint)dataBytes);
    }

    public void Dispose()
    {
        FinalizeFile();
        _writer.Dispose();
        _stream.Dispose();
    }
}


