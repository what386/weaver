using Microsoft.Extensions.DependencyInjection;
using Weaver.Services;
using Weaver.ViewModels;

namespace Weaver;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWeaverServices(this IServiceCollection services)
    {
        // Register settings
        services.AddSingleton(new Models.AppSettings());

        // Register services
        services.AddSingleton<GCodeParser>();
        services.AddSingleton<GCodeCompiler>();
        services.AddSingleton<ThreeMFExtractor>();
        services.AddSingleton<ThreeMFCompiler>();
        services.AddSingleton<IFileService, FileService>();

        // Register ViewModels
        services.AddTransient<MainWindowViewModel>();

        return services;
    }
}
