using HeyNorm.Config;
using WebRtcVadSharp;

namespace HeyNorm.Audio;

public enum UtteranceEvent { Waiting, Speaking, Complete, IdleTimeout }

/// <summary>
/// Feeds mic frames through WebRTC VAD. Collects one utterance: starts at the first speech,
/// completes after a pause, and reports an idle timeout if nobody says anything.
/// </summary>
public sealed class UtteranceRecorder : IDisposable
{
    private const int VadFrameSamples = 320; // 20 ms at 16 kHz — WebRTC VAD accepts 10/20/30 ms

    // The VAD fires a beat after speech begins — at aggressiveness 3, late enough that "How
    // many inches in a foot" reached Whisper as "inches in a foot". Keep the audio just before
    // the trigger and lead the utterance with it. Callers can opt out: measured on Piper's
    // voice, any lead-in at all (even 40 ms) turned "Hey Norm" into "Pay Norm".
    private const int PreRollChunks = 15; // 300 ms

    private readonly WebRtcVad _vad;
    private readonly SpeechSettings _settings;
    private readonly List<short> _utterance = new(MicrophoneSource.SampleRate * 10);
    private readonly List<short> _vadBuffer = new(VadFrameSamples * 2);
    private readonly byte[] _vadBytes = new byte[VadFrameSamples * 2];
    private readonly Queue<short[]> _preRoll = new(PreRollChunks);

    private bool _inSpeech;
    private int _silentSamples;
    private int _idleSamples;

    public UtteranceRecorder(SpeechSettings settings)
    {
        _settings = settings;
        _vad = new WebRtcVad
        {
            SampleRate = SampleRate.Is16kHz,
            FrameLength = FrameLength.Is20ms,
            OperatingMode = (OperatingMode)Math.Clamp(settings.VadAggressiveness, 0, 3),
        };
    }

    public void Reset()
    {
        _utterance.Clear();
        _vadBuffer.Clear();
        _preRoll.Clear();
        _inSpeech = false;
        _silentSamples = 0;
        _idleSamples = 0;
    }

    /// <param name="idleTimeoutSeconds">Overrides the configured idle timeout for this frame.</param>
    public UtteranceEvent Feed(short[] frame, bool withPreRoll = true, int? idleTimeoutSeconds = null)
    {
        _vadBuffer.AddRange(frame);
        var result = _inSpeech ? UtteranceEvent.Speaking : UtteranceEvent.Waiting;

        while (_vadBuffer.Count >= VadFrameSamples)
        {
            var chunk = _vadBuffer.GetRange(0, VadFrameSamples);
            _vadBuffer.RemoveRange(0, VadFrameSamples);
            Buffer.BlockCopy(chunk.ToArray(), 0, _vadBytes, 0, _vadBytes.Length);
            var speech = _vad.HasSpeech(_vadBytes);

            if (speech)
            {
                if (!_inSpeech && withPreRoll)
                {
                    foreach (var earlier in _preRoll) _utterance.AddRange(earlier);
                    _preRoll.Clear();
                }
                _inSpeech = true;
                _silentSamples = 0;
                _idleSamples = 0;
            }

            if (_inSpeech)
            {
                _utterance.AddRange(chunk);
                if (!speech) _silentSamples += VadFrameSamples;

                if (_silentSamples >= Ms(_settings.EndOfUtteranceSilenceMs) ||
                    _utterance.Count >= MicrophoneSource.SampleRate * _settings.MaxUtteranceSeconds)
                    return UtteranceEvent.Complete;

                result = UtteranceEvent.Speaking;
            }
            else
            {
                if (_preRoll.Count == PreRollChunks) _preRoll.Dequeue();
                _preRoll.Enqueue(chunk.ToArray());

                _idleSamples += VadFrameSamples;
                if (_idleSamples >= MicrophoneSource.SampleRate * (idleTimeoutSeconds ?? _settings.SessionIdleTimeoutSeconds))
                    return UtteranceEvent.IdleTimeout;
            }
        }

        return result;
    }

    /// <summary>The captured utterance as float samples in [-1, 1], which Whisper expects.</summary>
    public float[] TakeUtterance()
    {
        var samples = new float[_utterance.Count];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = _utterance[i] / 32768f;
        Reset();
        return samples;
    }

    private static int Ms(int ms) => MicrophoneSource.SampleRate * ms / 1000;

    public void Dispose() => _vad.Dispose();
}
