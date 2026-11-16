namespace PR_Bot.Core.Extensions;

using Microsoft.Extensions.DependencyInjection;
using PR_Bot.Core.Analyzers;
using PR_Bot.Core.Configuration;
using PR_Bot.Core.Services;

/// <summary>
/// Dependency Injection extension methods for PR_Bot.Core.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core PR Bot services.
    /// </summary>
    public static IServiceCollection AddPrBotCore(
        this IServiceCollection services,
        Action<PrBotOptions>? configure = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.AddSingleton<IGitHubService, GitHubService>();
        services.AddSingleton<IReviewEngine, ReviewEngine>();

        return services;
    }
}
