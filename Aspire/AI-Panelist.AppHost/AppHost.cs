var builder = DistributedApplication.CreateBuilder(args);

var foundry = builder.AddAzureAIFoundry("foundry")
    .RunAsFoundryLocal();

var responses = foundry
    .AddDeployment("responses", "gpt-oss-20b-cuda-gpu", "1", "Microsoft");

var tts = builder.AddExecutable("qwen-tts", "wsl", ".",
    "-d", "Ubuntu-22.04", "--", "bash", "-c",
    "source ~/qwentts/qwen-tts-venv/bin/activate && cd ~/qwentts && python optimized_server.py")
    .WithHttpEndpoint(port: 8000, name: "http", isProxied: false);

builder.AddProject<Projects.API>("api")
    .WithReference(responses)
        .WaitFor(responses)
    .WaitFor(tts);

builder.Build().Run();
