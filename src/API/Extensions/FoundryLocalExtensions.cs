using Microsoft.Extensions.AI;

namespace API.Extensions;

public static class FoundryLocalExtensions
{
    public static void AddFoundryLocalAIServices(this WebApplicationBuilder builder)
    {
        var foundryLocal = builder.AddOpenAIClient(
            connectionName: "foundryLocal",
            configureSettings: options =>
            {
                options.Endpoint = new Uri("http://localhost:5273/v1");
            });

        foundryLocal.AddChatClient()
            .UseFunctionInvocation()
            .UseOpenTelemetry(configure: c =>
                c.EnableSensitiveData = builder.Environment.IsDevelopment());
    }
}
