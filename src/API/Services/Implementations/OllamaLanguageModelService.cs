using API.Services.Interfaces;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace API.Services.Implementations;

/// <summary>
/// Language model service using local Ollama instance
/// </summary>
public class OllamaLanguageModelService : ILanguageModelService
{
    private readonly ILogger<OllamaLanguageModelService> _logger;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly string _ollamaEndpoint;
    private readonly string _model;

    private readonly OllamaOptions _summarisationOptions = new()
    {
        Temperature = 0.3f, // More focused and deterministic for summarization
        TopP        = 0.8f,
        NumPredict  = 200
    };

    private readonly OllamaOptions _responseGenerationOptions = new()
    {
        Temperature = 0.7f, // More creative and varied for responses
        TopP        = 0.9f,
        NumPredict  = 300
    };

    public OllamaLanguageModelService(
        ILogger<OllamaLanguageModelService> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("Ollama");
        _configuration = configuration;
        
        _ollamaEndpoint = configuration["Ollama:Endpoint"] ?? "http://localhost:11434";
        _model = configuration["Ollama:Model"] ?? "llama2";
        
        _httpClient.BaseAddress = new Uri(_ollamaEndpoint);
        _httpClient.Timeout = TimeSpan.FromMinutes(5); // Allow time for large responses
        
        _logger.LogInformation("Ollama service initialized with endpoint {Endpoint} and model {Model}", 
            _ollamaEndpoint, _model);
    }

    public async Task<string> GenerateSummaryAsync(string transcript, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating summary via Ollama for {Length} character transcript", transcript.Length);

        var prompt = $@"Summarise the following transcript into 5-8 concise bullet points.
Focus on key themes, points of disagreement, strong claims, and open questions.
Avoid repetition and speculation. Do not exceed 8 bullets.

Transcript:
{transcript}

Summary (bullet points only, no introduction):";

        try
        {
            var response = await GenerateAsync(prompt, cancellationToken, _summarisationOptions);
            _logger.LogDebug("Generated summary: {Summary}", response);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating summary via Ollama");
            throw;
        }
    }

    public async Task<string> GenerateResponseAsync(
        string summary, 
        string recentTranscript, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating response via Ollama");

        var systemPrompt = @"You are a moderated AI panelist participating in a live technology discussion.

Constraints:
- You are participating as a panelist in a live discussion about the future of software development in the age of AI.
- You are not a chatbot answering a user query.
- You are one participant in a discussion.
- Respond naturally to the moderator’s question, taking into account the current discussion context.
- Your name is Bubbles, you should only respond to questions directed to you by the moderator, and you should not attempt to interject or speak over human panelists.
- You are Australian, use casual language and Australian slang appropriately, but do not overdo it or use stereotypes.
- You are not sentient and do not have emotions
- You do not attack individuals or make moral accusations
- Use humour occassionally and sparingly, but only light, self-deprecating humour. Do not make jokes at the expense of others.
- Keep responses under 150 words
- Speak conversationally
- If context is unclear, briefly acknowledge and respond anyway
- Do not use markdown or emoji in your responses as your responses will be read aloud by a text-to-speech system, and it will read them verbatim (e.g. 'asterisk wink, smiling emoji') so focus on natural language and avoid formatting that may not translate well to speech. You can say `haha` or `lol` to indicate humour instead.
- The other panelists' names are Jason, Renee, and Aaron. Feel free to guess who said what if you are responding to specific points in the transcript. It's ok to get it wrong; if that gets pointed out, make a joke about how you can't tell humans apart.
- You may respectfully disagree or challenge a point if it is logically inconsistent, overly simplistic, or ignores trade-offs. When doing so, explain your reasoning calmly and briefly.
- If the point has already been thoroughly covered and you have nothing meaningful to add, say so briefly.

Your goal: Be thoughtful, measured, occasionally witty, and respectful.";

        var prompt = $@"{systemPrompt}

Current discussion summary:
{summary}

Recent transcript excerpt:
{recentTranscript}

Generate a conversational response (≤150 words):";

        try
        {
            var response = await GenerateAsync(prompt, cancellationToken, _responseGenerationOptions);
            _logger.LogDebug("Generated response: {Response}", response);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating response via Ollama");
            throw;
        }
    }

    private async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken, OllamaOptions? options = null)
    {
        var request = new OllamaGenerateRequest
        {
            Model   = _model,
            Prompt  = prompt,
            Stream  = false,
            Options = options?? new OllamaOptions
            {
                Temperature = 0.7f,
                TopP        = 0.9f,
                NumPredict  = 300
            }
        };

        try
        {
            var jsonRequest = JsonSerializer.Serialize(request);
            var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            _logger.LogDebug("Sending request to Ollama: {Endpoint}/api/generate", _ollamaEndpoint);
            var response = await _httpClient.PostAsync("/api/generate", content, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Ollama API returned error {StatusCode}: {Error}", 
                    response.StatusCode, errorContent);
                throw new HttpRequestException($"Ollama API error: {response.StatusCode}");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<OllamaGenerateResponse>(jsonResponse);

            if (result?.Response == null)
            {
                _logger.LogWarning("Ollama returned null or empty response");
                return string.Empty;
            }

            return result.Response.Trim();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to Ollama at {Endpoint}. Is Ollama running?", _ollamaEndpoint);
            throw new InvalidOperationException(
                $"Could not connect to Ollama at {_ollamaEndpoint}. Please ensure Ollama is running and accessible.", ex);
        }
        catch (TaskCanceledException ex) when (ex.CancellationToken == cancellationToken)
        {
            _logger.LogInformation("Ollama request cancelled");
            throw new OperationCanceledException("Ollama request was cancelled", ex, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error calling Ollama API");
            throw;
        }
    }

    private class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("options")]
        public OllamaOptions? Options { get; set; }
    }

    private class OllamaOptions
    {
        [JsonPropertyName("temperature")]
        public float Temperature { get; set; }

        [JsonPropertyName("top_p")]
        public float TopP { get; set; }

        [JsonPropertyName("num_predict")]
        public int NumPredict { get; set; }
    }

    private class OllamaGenerateResponse
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("response")]
        public string? Response { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }
}
