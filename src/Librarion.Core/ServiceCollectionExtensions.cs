using Librarion.Core.Access;
using Librarion.Core.Catalog;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Settings;
using Librarion.Core.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Librarion.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Core services of the server; relative paths in the settings are relative to <paramref name="contentRoot"/>.</summary>
    public static IServiceCollection AddLibrarionCore(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        services.AddLibrarionDatabase(DatabaseOptions.From(configuration, contentRoot));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<TenantDb>();

        services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.SectionName));
        services.Configure<SetupOptions>(configuration.GetSection(SetupOptions.SectionName));
        services.Configure<RightOptions>(configuration.GetSection(RightOptions.SectionName));

        var setup = configuration.GetSection(SetupOptions.SectionName).Get<SetupOptions>() ?? new SetupOptions();
        var codeFile = Path.GetFullPath(setup.CodeFile, contentRoot);
        services.AddSingleton(provider => new GeneratedSetupCode(codeFile, provider.GetRequiredService<ILogger<GeneratedSetupCode>>()));
        services.AddSingleton<IPostConfigureOptions<SetupOptions>>(provider => provider.GetRequiredService<GeneratedSetupCode>());
        services.AddSingleton<IOptionsChangeTokenSource<SetupOptions>>(provider => provider.GetRequiredService<GeneratedSetupCode>());
        services.AddSingleton(provider => new AccountRecovery(
            provider.GetRequiredService<TenantDb>(),
            provider.GetRequiredService<AccessService>(),
            Path.Combine(Path.GetDirectoryName(codeFile)!, "recovery-code.txt"),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<AccountRecovery>>()));

        services.AddSingleton<AccessChanges>();
        services.AddSingleton<AccessService>();
        services.AddSingleton<RightsEvaluator>();
        services.AddSingleton<IUserDirectory, LocalUserDirectory>();
        services.AddSingleton<SignInService>();
        services.AddSingleton<SetupService>();
        services.AddSingleton<SecurityLog>();
        services.AddHostedService<SecurityLogCleanup>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<BookVisibilities>();
        services.AddSingleton<BookQueries>();
        services.AddSingleton<FolderQueries>();
        services.AddSingleton<Librarion.Core.Tagging.DefaultTags>();
        services.AddSingleton<TagQueries>();
        services.AddSingleton<Librarion.Core.Search.SearchSuggestions>();
        services.AddSingleton<Librarion.Core.Search.SearchFacets>();
        services.AddSingleton<Librarion.Core.Reading.BookDownloads>();
        var cacheOptions = configuration.GetSection(CacheOptions.SectionName).Get<CacheOptions>() ?? new CacheOptions();
        services.AddSingleton(new Librarion.Core.Covers.CoverCache(cacheOptions.PathOf(contentRoot, "covers")));
        var dataOptions = configuration.GetSection(DataOptions.SectionName).Get<DataOptions>() ?? new DataOptions();
        services.AddSingleton(new Librarion.Core.Covers.CoverUploads(dataOptions.PathOf(contentRoot, "covers")));
        services.AddSingleton<Librarion.Core.Covers.CoverEditor>();
        services.AddSingleton<Librarion.Core.Covers.TenantCoverStyles>();
        services.AddSingleton(configuration.GetSection(Librarion.Core.Covers.CoverOptions.SectionName).Get<Librarion.Core.Covers.CoverOptions>() ?? new Librarion.Core.Covers.CoverOptions());
        services.AddSingleton<Librarion.Core.Covers.CoverService>();
        services.TryAddSingleton<IMetadataReader, Librarion.Core.Import.Metadata.MetadataReaders>();
        services.AddSingleton<LibraryScanner>();
        services.AddSingleton<MissingBooks>();
        services.AddSingleton<Librarion.Core.Imports.CatalogImporter>();
        services.AddSingleton<Librarion.Core.Imports.ImportFormat, Librarion.Core.Imports.Calibre.CalibreFormat>();
        services.AddSingleton<Librarion.Core.Imports.ImportFormat, Librarion.Core.Imports.Inpx.InpxFormat>();
        services.TryAddSingleton<Librarion.Core.Imports.ImportFormats>();
        services.Configure<LibraryOptions>(configuration.GetSection(LibraryOptions.SectionName));
        services.AddSingleton<LibraryRoots>();
        services.AddSingleton<BackgroundTasks>();
        services.AddSingleton<Librarion.Core.Tagging.TagService>();
        services.AddSingleton<Librarion.Core.Tagging.TagLists>();
        services.AddSingleton<Librarion.Core.Tagging.ImportedTags>();
        services.AddSingleton<Librarion.Core.Tagging.TagPicker>();
        services.AddSingleton<Librarion.Core.Catalog.BookEditor>();
        services.AddSingleton<Librarion.Core.Tagging.BookTagger>();
        services.AddSingleton<Librarion.Core.Merging.TagMerger>();
        services.AddSingleton<TaskSchedules>();
        services.AddSingleton<Librarion.Core.Imports.ImportRequests>();
        services.AddHostedService<TaskScheduleWorker>();
        return services;
    }
}
