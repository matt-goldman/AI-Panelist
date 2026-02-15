var builder = DistributedApplication.CreateBuilder(args);

var foundry = builder.AddAzureAIFoundry("foundry")
    .RunAsFoundryLocal();

var responses = foundry
    .AddDeployment("responses", "gpt-oss-20b", "1", "OpenAI")
    .AddGp

builder.AddProject<Projects.API>("api");

builder.Build().Run();
