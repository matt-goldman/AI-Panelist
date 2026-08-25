var builder = DistributedApplication.CreateBuilder(args);

// NOTE: Stopped using this because of too many problems. Client integration doesn't work,
// and Aspire won't use the cached model anyway, so too many papercuts to make it work. Keeping the code here for reference in case we want to try again in the future.
//var foundry = builder.AddAzureAIFoundry("foundry")
//    .RunAsFoundryLocal();

//var responses = foundry
//    .AddDeployment("responses", "gpt-oss-20b-cuda-gpu", "1", "Microsoft");


// Commented Ollama and run it natively. Easier now on Linux and more reliable access to GPU without container passthrough

// var ollama = builder
//     .AddOllama("ollama")
//     .WithDataVolume() // this is how you cache the model apparently
//     .WithGPUSupport();
//
// var responses = ollama.AddModel("responses", "gpt-oss:20b");

// TTS Server (Qwen3-TTS)
// -----------------------
// On Windows: PyTorch CUDA has issues, so we run TTS in WSL where CUDA works properly.
// On Linux: run it natively — removing the WSL hop is one of the things the Linux move buys us.
//
// Configure via appsettings:
//   QwenTts:WorkingDirectory  directory containing server.py (default: the repo's src/QwenAPI)
//   QwenTts:VenvPath          venv directory, relative to WorkingDirectory (default: .venv)
//   QwenTts:WslDistro         Windows only (default: Ubuntu-22.04)
//   QwenTts:RefAudio          reference audio for voice cloning, passed as TTS_REF_AUDIO
//   QwenTts:RefText           transcript of that audio, passed as TTS_REF_TEXT
var ttsWorkingDirectory = builder.Configuration["QwenTts:WorkingDirectory"] ?? "../../src/QwenAPI";
var ttsVenvPath = builder.Configuration["QwenTts:VenvPath"] ?? ".venv";

var tts = OperatingSystem.IsWindows()
    ? builder.AddExecutable("qwen-tts", "wsl", ".",
        "-d", builder.Configuration["QwenTts:WslDistro"] ?? "Ubuntu-22.04", "--", "bash", "-c",
        "source ~/qwentts/qwen-tts-venv/bin/activate && cd ~/qwentts && python server.py")
    // Invoke the venv interpreter directly rather than sourcing activate. Activation
    // scripts hard-code the absolute path the venv was created at, so renaming or moving
    // the venv silently falls back to system Python - which has no torch, and fails deep
    // inside server.py rather than at launch.
    : builder.AddExecutable("qwen-tts", "bash", ttsWorkingDirectory,
        "-c", $"exec \"{ttsVenvPath}/bin/python\" server.py");

tts = tts
    .WithHttpEndpoint(port: 8000, name: "http", isProxied: false)
    // /health returns 503 until the model has compiled and warmed up (~2 min from cold),
    // so WaitFor below actually waits for a usable TTS server rather than just a live port.
    .WithHttpHealthCheck("/health", endpointName: "http");

var refAudio = builder.Configuration["QwenTts:RefAudio"];
if (!string.IsNullOrWhiteSpace(refAudio))
{
    tts = tts.WithEnvironment("TTS_REF_AUDIO", refAudio);
}

var refText = builder.Configuration["QwenTts:RefText"];
if (!string.IsNullOrWhiteSpace(refText))
{
    tts = tts.WithEnvironment("TTS_REF_TEXT", refText);
}

var api = builder.AddProject<Projects.API>("api")
    // .WithReference(responses)
    //     .WaitFor(responses)
    .WaitFor(tts);

// builder.AddDevTunnel("devtunnel-public")
//     .WithAnonymousAccess()
//     .WithReference(api.GetEndpoint("https"));

builder.Build().Run();
