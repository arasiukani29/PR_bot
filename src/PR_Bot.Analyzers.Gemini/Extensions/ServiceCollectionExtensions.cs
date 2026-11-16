namespace PR_Bot.Analyzers.Gemini.Extensions;

using Microsoft.Extensions.DependencyInjection;
using PR_Bot.Core.Analyzers;

/// <summary>
/// Dependency injection extension methods for Gemini review analyzer.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Google Gemini AI code review analyzer and its API client.
    /// </summary>
    public static IServiceCollection AddGeminiAnalyzer(
        this IServiceCollection services,
        Action<GeminiOptions>? configure = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.AddHttpClient<GeminiApiClient>();
        services.AddSingleton<IReviewAnalyzer, GeminiReviewAnalyzer>();

        return services;
    }
}
