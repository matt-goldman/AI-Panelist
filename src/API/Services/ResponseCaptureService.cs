using System.Threading.Channels;
using API.Configuration;
using Microsoft.Extensions.Options;

namespace API.Services;

/// <summary>
/// Captures LLM responses and TTS audio to disk asynchronously without impacting pipeline latency.
/// Uses a producer-consumer pattern with Channel&lt;T&gt; to ensure writes happen on a background thread.
/// </summary>
public class ResponseCaptureService : IHostedService, IDisposable
{
    private readonly ILogger<ResponseCaptureService> _logger;
    private readonly ResponseCaptureOptions _options;
    private readonly Channel<CaptureItem> _writeChannel;
    private Task? _writerTask;
    private CancellationTokenSource? _cts;

    public ResponseCaptureService(
        ILogger<ResponseCaptureService> logger,
        IOptions<ResponseCaptureOptions> options)
    {
        _logger = logger;
        _options = options.Value;

        // Bounded channel prevents unbounded memory growth if writes are slow
        _writeChannel = Channel.CreateBounded<CaptureItem>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Response capture is disabled");
            return Task.CompletedTask;
        }

        // Ensure output directories exist
        EnsureDirectoryExists(_options.TextOutputDirectory);
        EnsureDirectoryExists(_options.AudioOutputDirectory);

        _cts = new CancellationTokenSource();
        _writerTask = Task.Run(() => ProcessWriteQueueAsync(_cts.Token), _cts.Token);

        _logger.LogInformation("Response capture service started. Text: {TextDir}, Audio: {AudioDir}",
            _options.TextOutputDirectory, _options.AudioOutputDirectory);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_writerTask is null) return;

        _logger.LogInformation("Stopping response capture service, draining queue...");

        // Signal completion and wait for remaining items to be written
        _writeChannel.Writer.Complete();

        try
        {
            // Give the writer task time to drain
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await _writerTask.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Response capture shutdown timed out, some items may not have been written");
        }

        _cts?.Cancel();
        _logger.LogInformation("Response capture service stopped");
    }

    /// <summary>
    /// Queue an LLM text response for capture. Returns immediately (non-blocking).
    /// </summary>
    public void CaptureTextResponse(string response, string? contextSummary = null)
    {
        if (!_options.Enabled || !_options.CaptureTextResponses) return;

        var timestamp = DateTime.UtcNow;
        var item = new CaptureItem
        {
            Type = CaptureType.Text,
            Timestamp = timestamp,
            TextContent = response,
            ContextSummary = contextSummary
        };

        if (!_writeChannel.Writer.TryWrite(item))
        {
            _logger.LogWarning("Response capture queue full, dropping text response from {Timestamp}", timestamp);
        }
    }

    /// <summary>
    /// Queue TTS audio bytes for capture. Returns immediately (non-blocking).
    /// </summary>
    public void CaptureAudioResponse(byte[] audioBytes, string? associatedText = null)
    {
        if (!_options.Enabled || !_options.CaptureAudioResponses) return;

        var timestamp = DateTime.UtcNow;
        var item = new CaptureItem
        {
            Type = CaptureType.Audio,
            Timestamp = timestamp,
            AudioBytes = audioBytes,
            TextContent = associatedText
        };

        if (!_writeChannel.Writer.TryWrite(item))
        {
            _logger.LogWarning("Response capture queue full, dropping audio response from {Timestamp}", timestamp);
        }
    }

    private async Task ProcessWriteQueueAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Response capture writer thread started");

        await foreach (var item in _writeChannel.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                switch (item.Type)
                {
                    case CaptureType.Text:
                        await WriteTextFileAsync(item);
                        break;
                    case CaptureType.Audio:
                        await WriteAudioFileAsync(item);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing capture file for {Type} at {Timestamp}",
                    item.Type, item.Timestamp);
            }
        }

        _logger.LogDebug("Response capture writer thread completed");
    }

    private async Task WriteTextFileAsync(CaptureItem item)
    {
        var filename = $"response_{item.Timestamp:yyyyMMdd_HHmmss_fff}.txt";
        var filepath = Path.Combine(_options.TextOutputDirectory, filename);

        var content = $"""
            Timestamp: {item.Timestamp:O}
            
            --- Response ---
            {item.TextContent}
            
            --- Context Summary ---
            {item.ContextSummary ?? "(none)"}
            """;

        await File.WriteAllTextAsync(filepath, content);
        _logger.LogDebug("Captured text response to {File}", filename);
    }

    private async Task WriteAudioFileAsync(CaptureItem item)
    {
        var filename = $"response_{item.Timestamp:yyyyMMdd_HHmmss_fff}.wav";
        var filepath = Path.Combine(_options.AudioOutputDirectory, filename);

        await File.WriteAllBytesAsync(filepath, item.AudioBytes!);
        _logger.LogDebug("Captured audio response to {File} ({Bytes} bytes)", filename, item.AudioBytes!.Length);

        // Also write a companion text file with the spoken text if available
        if (!string.IsNullOrEmpty(item.TextContent))
        {
            var textFilename = $"response_{item.Timestamp:yyyyMMdd_HHmmss_fff}.txt";
            var textFilepath = Path.Combine(_options.AudioOutputDirectory, textFilename);
            await File.WriteAllTextAsync(textFilepath, item.TextContent);
        }
    }

    private void EnsureDirectoryExists(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            _logger.LogInformation("Created capture directory: {Path}", path);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private enum CaptureType
    {
        Text,
        Audio
    }

    private record CaptureItem
    {
        public CaptureType Type { get; init; }
        public DateTime Timestamp { get; init; }
        public string? TextContent { get; init; }
        public string? ContextSummary { get; init; }
        public byte[]? AudioBytes { get; init; }
    }
}
