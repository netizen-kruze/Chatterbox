using System;
using NAudio.Wave;

namespace Chatterbox.Stt;

// Microphone capture: WaveInEvent (WinMM — inherently shared-mode, so
// VRChat can hold the same mic concurrently), 16 kHz mono 16-bit, 100 ms
// buffers. Device indices come from SttAudioDevices, resolved by the caller.
public sealed class SttCaptureService : IDisposable
{
    // Copies of each capture buffer (16 kHz mono 16-bit PCM). Raised on the
    // NAudio callback thread — handlers must only hand the chunk off (e.g.
    // SttPipeline.Push) and never do inference or UI work here.
    public event Action<byte[]>? OnChunk;
    public event Action<string>? OnLog;

    private WaveInEvent? _waveIn;
    private volatile float _meterLevel;

    public float MeterLevel => _meterLevel;
    public bool IsRunning => _waveIn != null;

    public void Start(int deviceIndex)
    {
        Stop();

        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceIndex,
            WaveFormat = new WaveFormat(ISttEngine.SampleRate, 1),
            BufferMilliseconds = 100
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();
    }

    public void Stop()
    {
        var waveIn = _waveIn;
        if (waveIn == null) return;
        _waveIn = null;

        try
        {
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.RecordingStopped -= OnRecordingStopped;
            waveIn.StopRecording();
            waveIn.Dispose();
        }
        catch (Exception ex) { OnLog?.Invoke($"STT capture stop error: {ex.Message}"); }

        _meterLevel = 0f;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int count = e.BytesRecorded;
        if (count < 2) return;

        UpdateMeter(e.Buffer.AsSpan(0, count));
        OnChunk?.Invoke(e.Buffer.AsSpan(0, count).ToArray());
    }

    // Raised only for unexpected stops (device unplugged, driver failure) —
    // Stop() unsubscribes the WinMM handler before stopping, so a normal
    // stop never reaches here.
    public event Action<Exception?>? OnUnexpectedStop;

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            OnLog?.Invoke($"STT capture stopped with error: {e.Exception.Message}");
        OnUnexpectedStop?.Invoke(e.Exception);
    }

    // Block RMS drives the UI meter; the gain lifts normal speech into the
    // upper half of the bar (value chosen by ear during development).
    private const float MeterGain = 6f;

    private void UpdateMeter(ReadOnlySpan<byte> block)
    {
        int sampleCount = block.Length / 2;
        if (sampleCount == 0) { _meterLevel = 0f; return; }
        double energy = 0;
        for (int at = 0; at + 1 < block.Length; at += 2)
        {
            float normalized = unchecked((short)(block[at] | (block[at + 1] << 8))) / 32768f;
            energy += normalized * normalized;
        }
        _meterLevel = MathF.Min(1f, MathF.Sqrt((float)(energy / sampleCount)) * MeterGain);
    }

    public void Dispose() => Stop();
}
