using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tebrazi.Documents.Application.Abstractions;
using Tebrazi.Documents.Application.Configuration;
using Tebrazi.Documents.Infrastructure.Services;

namespace Tebrazi.Documents.Infrastructure.DependencyInjection;

public static class DocumentsInfrastructureModule
{
    public static IServiceCollection AddDocumentsModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<FileStorageOptions>()
            .Bind(configuration.GetSection(FileStorageOptions.SectionName))
            .ValidateOnStart();

        // Swap this one registration for a blob-backed implementation and every consumer
        // follows, because they all depend on IFileStorageService rather than on disk.
        services.AddSingleton<IFileStorageService, LocalFileStorageService>();

        return services;
    }
}
