using API.Services.Interfaces;
using Microsoft.CognitiveServices.Speech;

namespace API.Services.Implementations;

/// <summary>
/// Text-to-speech service using Azure Cognitive Services
/// </summary>
public class AzureTextToSpeechService : ITextToSpeechService
{
    private readonly ILogger<AzureTextToSpeechService> _logger;
    private readonly SpeechConfig _speechConfig;
    private CancellationTokenSource? _cts;

    public bool IsSpeaking { get; private set; }

    public AzureTextToSpeechService(
        ILogger<AzureTextToSpeechService> logger,
        IConfiguration configuration)
    {
        _logger = logger;

        var subscriptionKey = configuration["AzureVoiceResourceKey"];
        var region = configuration["AzureVoiceRegion"];

        if (string.IsNullOrEmpty(subscriptionKey) || string.IsNullOrEmpty(region))
        {
            throw new InvalidOperationException(
                "Azure Speech credentials not configured. Please set AzureVoiceResourceKey and AzureVoiceRegion in configuration.");
        }

        _speechConfig = SpeechConfig.FromSubscription(subscriptionKey, region);
        
        // Use Australian voice as mentioned in the original VoiceService.cs
        var voiceName = configuration["AzureVoiceSettings:VoiceName"] ?? "en-AU-WilliamNeural";
        _speechConfig.SpeechSynthesisVoiceName = voiceName;
        
        _logger.LogInformation("Azure TTS initialized with voice: {VoiceName}", voiceName);
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default, Func<Task>? onPlaybackStarting = null)
    {
        _logger.LogInformation("Azure TTS: Speaking {Length} characters", text.Length);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsSpeaking = true;

        try
        {
            using var synthesizer = new SpeechSynthesizer(_speechConfig);
            
            // Register cancellation
            using var _ = cancellationToken.Register(() =>
            {
                _logger.LogInformation("Azure TTS: Cancellation requested");
                // Azure SDK doesn't have direct cancellation, synthesizer disposal will stop it
            });

            // Azure SDK synthesizes and plays immediately, so notify before calling
            if (onPlaybackStarting != null)
            {
                await onPlaybackStarting();
            }

            var result = await synthesizer.SpeakTextAsync(text);

            if (result.Reason == ResultReason.SynthesizingAudioCompleted)
            {
                _logger.LogInformation("Azure TTS: Speech completed successfully");
            }
            else if (result.Reason == ResultReason.Canceled)
            {
                var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
                _logger.LogWarning("Azure TTS: Cancelled - {Reason}", cancellation.Reason);
                
                if (cancellation.Reason == CancellationReason.Error)
                {
                    _logger.LogError("Azure TTS: Error - {ErrorCode}: {ErrorDetails}", 
                        cancellation.ErrorCode, cancellation.ErrorDetails);
                    throw new InvalidOperationException(
                        $"Azure TTS error: {cancellation.ErrorCode} - {cancellation.ErrorDetails}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Azure TTS: Speech cancelled by user");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure TTS: Error during speech synthesis");
            throw;
        }
        finally
        {
            IsSpeaking = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public Task StopAsync()
    {
        _logger.LogInformation("Azure TTS: Stopping speech");
        _cts?.Cancel();
        // Azure Speech SDK doesn't have a direct stop method for default speaker
        // Cancellation via token is the recommended approach
        return Task.CompletedTask;
    }
}
