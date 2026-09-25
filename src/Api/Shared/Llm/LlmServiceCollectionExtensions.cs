using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Api.Shared.Llm;

public static class LlmServiceCollectionExtensions
{
    public const string HttpClientName = "llm";

    public static IServiceCollection AddLlm(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Llm");
        services.Configure<LlmOptions>(section);
        var options = section.Get<LlmOptions>() ?? new LlmOptions();
        if (options.Models.Count == 0 || !options.Models.Contains(options.DefaultModel))
        {
            throw new InvalidOperationException("Llm:Models 가 비어 있거나 Llm:DefaultModel 이 Models 에 없습니다");
        }

        services.AddHttpClient(HttpClientName, http =>
        {
            http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        services.AddKernel();
        foreach (var model in options.Models)
        {
            if (options.Provider == "openai")
            {
                services.AddOpenAIChatCompletion(
                    modelId: model,
                    endpoint: new Uri(options.BaseUrl.TrimEnd('/') + "/v1"),
                    apiKey: options.ApiKey,
                    serviceId: model);
            }
            else
            {
                services.AddKeyedSingleton<IChatCompletionService>(model, (sp, _) => new OllamaChatCompletionService(
                    model,
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
                    sp.GetRequiredService<IOptions<LlmOptions>>().Value));
            }
        }
        services.AddScoped<LlmClient>();
        return services;
    }
}
