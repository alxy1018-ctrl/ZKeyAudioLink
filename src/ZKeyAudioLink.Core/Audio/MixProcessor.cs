namespace ZKeyAudioLink.Core.Audio;

public readonly record struct MixLevels(float MicPeak, float MaterialPeak, float SendPeak, float MonitorPeak)
{
    public static MixLevels Silence => new(0, 0, 0, 0);
}

/// <summary>
/// The sample-accurate mixing math, kept free of devices and threads so it can
/// be tested deterministically. Inputs and outputs are interleaved stereo at
/// 48 kHz. The send path is microphone + material; the monitor path is material
/// only. A smoothed limiter protects the send path from summed clipping.
/// </summary>
public sealed class MixProcessor
{
    private const float LimiterThreshold = 0.95f;
    private const float LimiterAttackSeconds = 0.003f;
    private const float LimiterReleaseSeconds = 0.150f;

    private float _micGain = 1f;
    private float _micGainTarget = 1f;
    private float _sendGain = 1f;
    private float _sendGainTarget = 1f;
    private float _monitorGain = 0.8f;
    private float _monitorGainTarget = 0.8f;
    private float _limiterGain = 1f;
    private float[] _rawSend = new float[AudioConstants.BlockFrames * AudioConstants.Channels];

    public MixLevels Levels { get; private set; } = MixLevels.Silence;

    public float MicGain => _micGainTarget;
    public float SendGain => _sendGainTarget;
    public float MonitorGain => _monitorGainTarget;

    public void Configure(float micGain, float sendGain, float monitorGain)
    {
        _micGainTarget = Math.Clamp(micGain, 0f, 4f);
        _sendGainTarget = Math.Clamp(sendGain, 0f, 2f);
        _monitorGainTarget = Math.Clamp(monitorGain, 0f, 2f);
    }

    /// <summary>Jumps smoothed parameters straight to their targets (engine start/device rebuild).</summary>
    public void Reset()
    {
        _micGain = _micGainTarget;
        _sendGain = _sendGainTarget;
        _monitorGain = _monitorGainTarget;
        _limiterGain = 1f;
        Levels = MixLevels.Silence;
    }

    public void Process(
        ReadOnlySpan<float> microphone,
        ReadOnlySpan<float> material,
        int frames,
        float materialEnvelope,
        Span<float> sendDestination,
        Span<float> monitorDestination)
    {
        var samples = frames * AudioConstants.Channels;
        if (microphone.Length < samples || material.Length < samples ||
            sendDestination.Length < samples || monitorDestination.Length < samples)
        {
            throw new ArgumentException("Audio block is shorter than the requested frame count.");
        }

        if (_rawSend.Length < samples)
        {
            _rawSend = new float[Math.Max(samples, AudioConstants.BlockFrames * AudioConstants.Channels)];
        }

        materialEnvelope = Math.Clamp(materialEnvelope, 0f, 1f);
        var sendTarget = _sendGainTarget * materialEnvelope;
        var monitorTarget = _monitorGainTarget * materialEnvelope;

        var micPeak = 0f;
        var materialPeak = 0f;
        var rawPeak = 0f;

        var micStep = (_micGainTarget - _micGain) / frames;
        var sendStep = (sendTarget - _sendGain) / frames;
        var monitorStart = _monitorGain;
        var monitorStep = (monitorTarget - monitorStart) / frames;

        for (var frame = 0; frame < frames; frame++)
        {
            var micGain = _micGain + micStep * frame;
            var sendGain = _sendGain + sendStep * frame;
            var index = frame * AudioConstants.Channels;

            var micLeft = microphone[index] * micGain;
            var micRight = microphone[index + 1] * micGain;
            var matLeft = material[index];
            var matRight = material[index + 1];

            var rawLeft = micLeft + matLeft * sendGain;
            var rawRight = micRight + matRight * sendGain;
            _rawSend[index] = rawLeft;
            _rawSend[index + 1] = rawRight;

            var micAbs = Math.Max(Math.Abs(micLeft), Math.Abs(micRight));
            if (micAbs > micPeak)
            {
                micPeak = micAbs;
            }

            var matAbs = Math.Max(Math.Abs(matLeft), Math.Abs(matRight)) * materialEnvelope;
            if (matAbs > materialPeak)
            {
                materialPeak = matAbs;
            }

            var rawAbs = Math.Max(Math.Abs(rawLeft), Math.Abs(rawRight));
            if (rawAbs > rawPeak)
            {
                rawPeak = rawAbs;
            }
        }

        _micGain = _micGainTarget;
        _sendGain = sendTarget;
        _monitorGain = monitorTarget;

        var desiredLimiter = rawPeak > LimiterThreshold ? LimiterThreshold / rawPeak : 1f;
        var blockSeconds = frames / (float)AudioConstants.SampleRate;
        var attack = MathF.Exp(-blockSeconds / LimiterAttackSeconds);
        var release = MathF.Exp(-blockSeconds / LimiterReleaseSeconds);
        var previousLimiter = _limiterGain;
        _limiterGain = desiredLimiter < _limiterGain
            ? attack * _limiterGain + (1 - attack) * desiredLimiter
            : release * _limiterGain + (1 - release) * desiredLimiter;

        var sendPeak = 0f;
        var monitorPeak = 0f;
        for (var frame = 0; frame < frames; frame++)
        {
            var index = frame * AudioConstants.Channels;
            var limiter = previousLimiter + (_limiterGain - previousLimiter) * (frame / (float)frames);

            var sendLeft = Math.Clamp(_rawSend[index] * limiter, -1f, 1f);
            var sendRight = Math.Clamp(_rawSend[index + 1] * limiter, -1f, 1f);
            sendDestination[index] = sendLeft;
            sendDestination[index + 1] = sendRight;

            var monitorGain = monitorStart + monitorStep * frame;
            var monitorLeft = Math.Clamp(material[index] * monitorGain, -1f, 1f);
            var monitorRight = Math.Clamp(material[index + 1] * monitorGain, -1f, 1f);
            monitorDestination[index] = monitorLeft;
            monitorDestination[index + 1] = monitorRight;

            var sendAbs = Math.Max(Math.Abs(sendLeft), Math.Abs(sendRight));
            if (sendAbs > sendPeak)
            {
                sendPeak = sendAbs;
            }

            var monAbs = Math.Max(Math.Abs(monitorLeft), Math.Abs(monitorRight));
            if (monAbs > monitorPeak)
            {
                monitorPeak = monAbs;
            }
        }

        Levels = new MixLevels(micPeak, materialPeak, sendPeak, monitorPeak);
    }
}
