using System.Runtime.Versioning;
using API.Services.Interfaces;
using NAudio.Wave;
using Shared;

namespace API.Services.Implementations;

/// <summary>
/// Windows audio capture using NAudio/WinMM. This is the pre-Linux implementation,
/// kept intact as the proven fallback when running the panelist on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NAudioAudioCaptureSource : IAudioCaptureSource
{
    private readonly AudioDeviceInfo _device;
    private readonly ILogger _logger;
    private WaveInEvent? _waveIn;

    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;

    public NAudioAudioCaptureSource(AudioDeviceInfo device, ILogger logger)
    {
        _device = device;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(_device.Id, out var deviceNumber))
        {
            throw new InvalidOperationException(
                $"NAudio capture expects a numeric device index, got '{_device.Id}'.");
        }

        _waveIn = new WaveInEvent
        {
            DeviceNumber       = deviceNumber,
            WaveFormat         = new WaveFormat(IAudioCaptureFactory.SampleRate, 16, 1),
            BufferMilliseconds = 100
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
            {
                _logger.LogError(e.Exception, "Audio recording stopped with error on device {DeviceName}",
                    _device.EffectiveName);
            }
            else
            {
                _logger.LogInformation("Audio recording stopped normally on device {DeviceName}",
                    _device.EffectiveName);
            }
        };

        _waveIn.StartRecording();

        _logger.LogInformation("NAudio capture started on device {DeviceName} (ID: {DeviceId})",
            _device.EffectiveName, _device.Id);

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
        return Task.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // 16-bit PCM to float normalised to [-1, 1].
        var samples = new float[e.BytesRecorded / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = BitConverter.ToInt16(e.Buffer, i * 2);
            samples[i] = sample / 32768f;
        }

        SamplesAvailable?.Invoke(this, new AudioSamplesEventArgs(samples));
    }

    public void Dispose()
    {
        _waveIn?.Dispose();
        _waveIn = null;
    }
}

/// <summary>
/// Creates Windows/NAudio capture streams.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NAudioAudioCaptureFactory(ILogger<NAudioAudioCaptureFactory> logger) : IAudioCaptureFactory
{
    public IAudioCaptureSource Create(AudioDeviceInfo device) => new NAudioAudioCaptureSource(device, logger);
}
