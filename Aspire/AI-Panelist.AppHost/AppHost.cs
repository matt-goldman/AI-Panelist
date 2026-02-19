using k8s.Models;

var builder = DistributedApplication.CreateBuilder(args);

// NOTE: Stopped using this because of too many problems. Client integration doesn't work,
// and Aspire won't use the cached model anyway, so too many papercuts to make it work. Keeping the code here for reference in case we want to try again in the future.
//var foundry = builder.AddAzureAIFoundry("foundry")
//    .RunAsFoundryLocal();

//var responses = foundry
//    .AddDeployment("responses", "gpt-oss-20b-cuda-gpu", "1", "Microsoft");


var ollama = builder
    .AddOllama("ollama")
    .WithDataVolume() // this is how you cache the model apparently
    .WithGPUSupport();

var responses = ollama.AddModel("responses", "gpt-oss:20b");

// TTS Server (Qwen3-TTS)
// -----------------------
// On Windows: PyTorch CUDA has issues, so we run TTS in WSL where CUDA works properly.
// Copy server.py from src/QwenAPI to your WSL environment and set up a Python venv with requirements.txt.
// 
// On Linux/macOS: Comment out the WSL executable below and uncomment the AddPythonApp instead.

// Option 1: WSL (Windows) - run server.py from your WSL environment
var tts = builder.AddExecutable("qwen-tts", "wsl", ".",
    "-d", "Ubuntu-22.04", "--", "bash", "-c",
    "source ~/qwentts/qwen-tts-venv/bin/activate && cd ~/qwentts && python server.py")
    .WithHttpEndpoint(port: 8000, name: "http", isProxied: false);

// Option 2: Native Python (Linux/macOS) - uncomment this and comment out the WSL executable above
// var tts = builder.AddPythonApp("qwen-tts", "../src/QwenAPI", "server.py")
//     .WithHttpEndpoint(port: 8000, name: "http", isProxied: false);

var api = builder.AddProject<Projects.API>("api")
    .WithReference(responses)
        .WaitFor(responses)
    .WaitFor(tts);

builder.AddDevTunnel("devtunnel-public")
    .WithAnonymousAccess()
    .WithReference(api.GetEndpoint("https"));

builder.Build().Run();
