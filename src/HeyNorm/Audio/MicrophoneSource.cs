using System.Threading.Channels;
using NAudio.Wave;

namespace HeyNorm.Audio;

/// <summary>
/// Captures 16 kHz, 16-bit mono audio and emits fixed-size 512-sample frames
/// onto a channel, so one processing loop can consume them in order.
/// </summary>
public sealed class MicrophoneSource : IDisposable
{
    public const int SampleRate = 16000;

    /// <summary>32 ms. A whole number of the VAD's 20 ms frames is not required — the
    /// recorder re-chunks — but a small frame keeps the loop responsive.</summary>
    public const int DefaultFrameLength = 512;

    private readonly WaveInEvent _waveIn;
    private readonly int _frameLength;
    private readonly short[] _pending;
    private int _pendingCount;
    private readonly Channel<short[]> _frames =
        Channel.CreateBounded<short[]>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

    public MicrophoneSource(int frameLength = DefaultFrameLength, int deviceNumber = 0)
    {
        _frameLength = frameLength;
        _pending = new short[frameLength];
        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 50,
        };
        _waveIn.DataAvailable += OnData;
    }

    public ChannelReader<short[]> Frames => _frames.Reader;

    public void Start() => _waveIn.StartRecording();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            _pending[_pendingCount++] = BitConverter.ToInt16(e.Buffer, i);
            if (_pendingCount == _frameLength)
            {
                _frames.Writer.TryWrite((short[])_pending.Clone());
                _pendingCount = 0;
            }
        }
    }

    public void Dispose()
    {
        _waveIn.StopRecording();
        _waveIn.Dispose();
        _frames.Writer.TryComplete();
    }
}
