var builder = DistributedApplication.CreateBuilder(args);

var foundry = builder.AddAzureAIFoundry("foundry")
    .RunAsFoundryLocal();

var responses = foundry
    .AddDeployment("responses", "gpt-oss-20b-cuda-gpu", "1", "Microsoft");

builder.AddProject<Projects.API>("api")
    .WithReference(responses)
    .WaitFor(responses);

builder.Build().Run();
