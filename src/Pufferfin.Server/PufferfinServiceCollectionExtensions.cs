using System;
using System.IO;
using System.Net.Http;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pufferfin.Catalog;
using Pufferfin.Catalog.Configuration;
using Pufferfin.Server.Arr;
using Pufferfin.Server.Audiobooks;
using Pufferfin.Server.AutoConfig;
using Pufferfin.Server.AutoConfig.Steps;
using Pufferfin.Server.Catalog;
using Pufferfin.Server.Configuration;
using Pufferfin.Server.Connectivity;
using Pufferfin.Server.Deletion;
using Pufferfin.Server.Requests;
using Pufferfin.Server.Requests.Integration;
using Pufferfin.Server.Requests.Workflow;
using Pufferfin.Server.Seeding;
using Pufferfin.Server.Stack;
using Pufferfin.Server.Storage;
using Pufferfin.Server.Users;

namespace Pufferfin.Server;

/// <summary>
/// Registers the Pufferfin services.
/// </summary>
public static class PufferfinServiceCollectionExtensions
{
    /// <summary>
    /// The name of the store file, in the server configuration directory.
    /// </summary>
    public const string SettingsFileName = "pufferfin.json";

    /// <summary>
    /// The name of the per user settings file, in the server configuration directory.
    /// </summary>
    public const string UserSettingsFileName = "pufferfin-users.json";

    /// <summary>
    /// The name of the requests file, in the server configuration directory.
    /// </summary>
    public const string RequestsFileName = "pufferfin-requests.json";

    /// <summary>
    /// The name of the seeding policy file, in the server configuration directory.
    /// </summary>
    public const string SeedingFileName = "pufferfin-seeding.json";

    /// <summary>
    /// The name of the scheduled deletions file, in the server configuration directory.
    /// </summary>
    public const string ScheduledDeletionsFileName = "pufferfin-scheduled-deletions.json";

    /// <summary>
    /// Adds the Pufferfin configuration store, connection tests, *arr clients, auto-configuration, per user settings,
    /// the catalog, the Requests module, the seeding policy, the deletion and the storage screen.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddPufferfin(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEnvironmentReader, ProcessEnvironmentReader>();
        services.AddSingleton(provider => new SettingsFile(
            Path.Combine(provider.GetRequiredService<IApplicationPaths>().ConfigurationDirectoryPath, SettingsFileName),
            provider.GetRequiredService<ILogger<SettingsFile>>()));
        services.AddSingleton<IPufferfinConfigurationStore, PufferfinConfigurationStore>();
        services.AddSingleton<StackLinks>();

        // Request logging is removed: a TMDB v3 key travels in the query string.
        services.AddHttpClient(ServiceConnectionTester.HttpClientName, client => client.Timeout = ServiceConnectionTester.DefaultTimeout + TimeSpan.FromSeconds(5))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = ServiceConnectionTester.DefaultTimeout })
            .RemoveAllLoggers();
        services.AddSingleton<IServiceConnectionTester, ServiceConnectionTester>();

        // The *arr clients. Keys travel in headers, but a base URL may carry credentials: no request logging.
        services.AddHttpClient(ArrHttpClient.HttpClientName, client => client.Timeout = ArrHttpClient.SearchTimeout + TimeSpan.FromSeconds(5))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = ServiceConnectionTester.DefaultTimeout })
            .RemoveAllLoggers();
        services.AddSingleton<ArrHttpClient>();
        services.AddSingleton<ArrClient>();
        services.AddSingleton<ProwlarrClient>();
        services.AddSingleton<Requests.Search.VariantReleaseSearch>();
        services.AddSingleton<QBittorrentClient>();
        services.AddSingleton<BazarrClient>();

        // Auto-configuration steps, in the order they run: qBittorrent first, the download clients point to its categories.
        services.AddSingleton<IAutoConfigStep, QBittorrentStep>();
        AddArrStep(services, (client, service) => new RootFoldersStep(client, service));
        AddArrStep(services, (client, service) => new FolderNamingStep(client, service));
        AddArrStep(services, (client, service) => new DownloadClientStep(client, service));
        AddArrStep(services, (client, service) => new LanguageProfilesStep(client, service));
        services.AddSingleton<IAutoConfigStep, ProwlarrStep>();
        services.AddSingleton<IAutoConfigStep, BazarrStep>();
        AddArrStep(services, (provider, client, service) => new NotificationStep(
            client,
            service,
            provider.GetRequiredService<RequestStore>(),
            () => Uri.TryCreate(provider.GetRequiredService<IServerApplicationHost>().GetApiUrlForLocalAccess(), UriKind.Absolute, out var url) ? url : null));

        services.AddSingleton<IAutoConfigRunner, AutoConfigRunner>();
        services.AddHostedService<AutoConfigReplayService>();

        services.AddSingleton<IPufferfinUserSettingsStore>(provider => new PufferfinUserSettingsStore(
            Path.Combine(provider.GetRequiredService<IApplicationPaths>().ConfigurationDirectoryPath, UserSettingsFileName),
            provider.GetRequiredService<ILogger<PufferfinUserSettingsStore>>()));

        // The catalog reads the store, not the environment, and the Letterboxd account each user set for themselves.
        // Registered before AddPufferfinCatalog, whose environment based fallbacks are TryAdd.
        services.AddSingleton<ICatalogSettingsProvider, StoreCatalogSettingsProvider>();
        services.AddSingleton<ILetterboxdAccountProvider, StoreLetterboxdAccountProvider>();
        services.AddPufferfinCatalog();

        // The Requests module: a background processor drives the requests, whether or not a client is connected.
        services.AddSingleton(provider => new RequestStore(
            Path.Combine(provider.GetRequiredService<IApplicationPaths>().ConfigurationDirectoryPath, RequestsFileName),
            provider.GetRequiredService<ILogger<RequestStore>>()));
        services.AddSingleton<ITitleMetadata, TmdbTitleMetadata>();
        services.AddSingleton<IMediaLibrary, JellyfinMediaLibrary>();
        services.AddSingleton<IRequestWorkflow, MovieWorkflow>();
        services.AddSingleton<IRequestWorkflow, SeriesWorkflow>();

        // Audiobooks: Shelfmark searches and downloads, Pufferfin files them as hard links and checks the setup.
        services.AddHttpClient(AudiobookLibrary.HttpClientName, client => client.Timeout = AudiobookLibrary.CoverTimeout + TimeSpan.FromSeconds(5))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = true, UseCookies = false, ConnectTimeout = ServiceConnectionTester.DefaultTimeout })
            .RemoveAllLoggers();
        services.AddSingleton<ShelfmarkClient>();
        services.AddSingleton<IAudiobookMetadata, AudibleAudiobookMetadata>();
        services.AddSingleton<AudiobookLibrary>();
        services.AddSingleton<AudiobookTorrentLocator>();
        services.AddSingleton<AudiobookHealthCheck>();
        services.AddHostedService<AudiobookStartupCheck>();
        services.AddSingleton<IRequestWorkflow, AudiobookWorkflow>();
        services.AddSingleton<RequestProcessor>();
        services.AddSingleton<IRequestWaker>(provider => provider.GetRequiredService<RequestProcessor>());
        services.AddHostedService(provider => provider.GetRequiredService<RequestProcessor>());
        services.AddHostedService<RequestUpdatesPublisher>();

        // Every account starts with a picture of its own
        services.AddSingleton<DefaultAvatars>();
        services.AddHostedService<DefaultAvatarStartup>();
        services.AddScoped<IEventConsumer<UserCreatedEventArgs>, DefaultAvatarOnUserCreated>();
        services.AddSingleton<RequestService>();

        // Seeding policy, deletion and storage: an hourly task keeps private torrents seeding until their minimum.
        services.TryAddSingleton<IFileLinks, NativeFileLinks>();
        services.TryAddSingleton<IDiskSpace, DriveDiskSpace>();
        services.AddSingleton(provider => new SeedingStore(
            Path.Combine(provider.GetRequiredService<IApplicationPaths>().ConfigurationDirectoryPath, SeedingFileName),
            provider.GetRequiredService<ILogger<SeedingStore>>()));
        services.AddSingleton<SeedPolicyRunner>();
        services.AddHostedService<SeedPolicyService>();
        services.AddSingleton<HardlinkCheck>();
        services.AddSingleton<StorageService>();
        services.AddSingleton<ILibraryContent, JellyfinLibraryContent>();
        services.AddSingleton<TitleTorrentResolver>();
        services.AddSingleton<ContentDeletionService>();

        // Temporary films leave seven days after their import, their private torrent once it seeded its minimum.
        services.AddSingleton<TemporaryExpiryRunner>();
        services.AddHostedService<TemporaryExpiryService>();

        // The content manager: every title on disk with its torrents, deleted now or seven days later.
        services.AddSingleton(provider => new ScheduledDeletionStore(
            Path.Combine(provider.GetRequiredService<IApplicationPaths>().ConfigurationDirectoryPath, ScheduledDeletionsFileName),
            provider.GetRequiredService<ILogger<ScheduledDeletionStore>>()));
        services.AddSingleton<ContentInventory>();
        services.AddSingleton<ScheduledDeletionRunner>();
        services.AddHostedService<ScheduledDeletionService>();

        return services;
    }

    private static void AddArrStep(IServiceCollection services, Func<ArrClient, PufferfinService, IAutoConfigStep> create)
        => AddArrStep(services, (_, client, service) => create(client, service));

    private static void AddArrStep(IServiceCollection services, Func<IServiceProvider, ArrClient, PufferfinService, IAutoConfigStep> create)
    {
        services.AddSingleton(provider => create(provider, provider.GetRequiredService<ArrClient>(), PufferfinService.Radarr));
        services.AddSingleton(provider => create(provider, provider.GetRequiredService<ArrClient>(), PufferfinService.Sonarr));
    }
}
