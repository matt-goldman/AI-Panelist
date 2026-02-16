var builder = DistributedApplication.CreateBuilder(args);

// NOTE: Stopped using this because of too many problems. Clent integration doesn't work,
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

var tts = builder.AddExecutable("qwen-tts", "wsl", ".",
    "-d", "Ubuntu-22.04", "--", "bash", "-c",
    "source ~/qwentts/qwen-tts-venv/bin/activate && cd ~/qwentts && python optimized_server.py")
    .WithHttpEndpoint(port: 8000, name: "http", isProxied: false);

builder.AddProject<Projects.API>("api")
    .WithReference(responses)
        .WaitFor(responses)
    .WaitFor(tts);

builder.Build().Run();
