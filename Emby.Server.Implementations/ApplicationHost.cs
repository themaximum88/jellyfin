using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Dlna;
using Emby.Dlna.Main;
using Emby.Dlna.Ssdp;
using Emby.Drawing;
using Emby.Notifications;
using Emby.Photos;
using Emby.Server.Implementations.Activity;
using Emby.Server.Implementations.Archiving;
using Emby.Server.Implementations.Channels;
using Emby.Server.Implementations.Collections;
using Emby.Server.Implementations.Configuration;
using Emby.Server.Implementations.Cryptography;
using Emby.Server.Implementations.Data;
using Emby.Server.Implementations.Devices;
using Emby.Server.Implementations.Diagnostics;
using Emby.Server.Implementations.Dto;
using Emby.Server.Implementations.HttpServer;
using Emby.Server.Implementations.HttpServer.Security;
using Emby.Server.Implementations.IO;
using Emby.Server.Implementations.Library;
using Emby.Server.Implementations.LiveTv;
using Emby.Server.Implementations.Localization;
using Emby.Server.Implementations.Net;
using Emby.Server.Implementations.Playlists;
using Emby.Server.Implementations.ScheduledTasks;
using Emby.Server.Implementations.Security;
using Emby.Server.Implementations.Serialization;
using Emby.Server.Implementations.Session;
using Emby.Server.Implementations.SocketSharp;
using Emby.Server.Implementations.TV;
using Emby.Server.Implementations.Updates;
using MediaBrowser.Api;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Events;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Updates;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Dlna;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Controller.Security;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Sorting;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Controller.TV;
using MediaBrowser.LocalMetadata.Savers;
using MediaBrowser.MediaEncoding.BdInfo;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Cryptography;
using MediaBrowser.Model.Diagnostics;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Extensions;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.System;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Updates;
using MediaBrowser.Providers.Chapters;
using MediaBrowser.Providers.Manager;
using MediaBrowser.Providers.Subtitles;
using MediaBrowser.Providers.TV.TheTVDB;
using MediaBrowser.WebDashboard.Api;
using MediaBrowser.XbmcMetadata.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceStack;
using OperatingSystem = MediaBrowser.Common.System.OperatingSystem;

namespace Emby.Server.Implementations
{
    /// <summary>
    /// Class CompositionRoot
    /// </summary>
    public abstract class ApplicationHost : IServerApplicationHost, IDisposable
    {
        /// <summary>
        /// Gets a value indicating whether this instance can self restart.
        /// </summary>
        /// <value><c>true</c> if this instance can self restart; otherwise, <c>false</c>.</value>
        public abstract bool CanSelfRestart { get; }

        public virtual bool CanLaunchWebBrowser
        {
            get
            {
                if (!Environment.UserInteractive)
                {
                    return false;
                }

                if (StartupOptions.IsService)
                {
                    return false;
                }

                if (OperatingSystem.Id == OperatingSystemId.Windows
                    || OperatingSystem.Id == OperatingSystemId.Darwin)
                {
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Occurs when [has pending restart changed].
        /// </summary>
        public event EventHandler HasPendingRestartChanged;

        /// <summary>
        /// Gets a value indicating whether this instance has changes that require the entire application to restart.
        /// </summary>
        /// <value><c>true</c> if this instance has pending application restart; otherwise, <c>false</c>.</value>
        public bool HasPendingRestart { get; private set; }

        public bool IsShuttingDown { get; private set; }

        /// <summary>
        /// Gets or sets the logger.
        /// </summary>
        /// <value>The logger.</value>
        protected ILogger Logger { get; set; }

        private IPlugin[] _plugins;

        /// <summary>
        /// Gets the plugins.
        /// </summary>
        /// <value>The plugins.</value>
        public IPlugin[] Plugins
        {
            get => _plugins;
            protected set => _plugins = value;
        }

        /// <summary>
        /// Gets or sets the logger factory.
        /// </summary>
        /// <value>The logger factory.</value>
        public ILoggerFactory LoggerFactory { get; protected set; }

        /// <summary>
        /// Gets or sets the application paths.
        /// </summary>
        /// <value>The application paths.</value>
        protected ServerApplicationPaths ApplicationPaths { get; set; }

        /// <summary>
        /// Gets or sets all concrete types.
        /// </summary>
        /// <value>All concrete types.</value>
        public Type[] AllConcreteTypes { get; protected set; }

        /// <summary>
        /// The disposable parts
        /// </summary>
        private readonly List<IDisposable> _disposableParts = new List<IDisposable>();

        /// <summary>
        /// Gets the configuration manager.
        /// </summary>
        /// <value>The configuration manager.</value>
        protected IConfigurationManager ConfigurationManager { get; set; }

        public IFileSystem FileSystemManager { get; set; }

        public PackageVersionClass SystemUpdateLevel
        {
            get
            {
#if BETA
                return PackageVersionClass.Beta;
#else
                return PackageVersionClass.Release;
#endif
            }
        }

        protected IServiceProvider _serviceProvider;

        /// <summary>
        /// Gets the server configuration manager.
        /// </summary>
        /// <value>The server configuration manager.</value>
        public IServerConfigurationManager ServerConfigurationManager => (IServerConfigurationManager)ConfigurationManager;

        /// <summary>
        /// Gets or sets the user manager.
        /// </summary>
        /// <value>The user manager.</value>
        public IUserManager UserManager { get; set; }

        /// <summary>
        /// Gets or sets the library manager.
        /// </summary>
        /// <value>The library manager.</value>
        internal ILibraryManager LibraryManager { get; set; }

        /// <summary>
        /// Gets or sets the directory watchers.
        /// </summary>
        /// <value>The directory watchers.</value>
        private ILibraryMonitor LibraryMonitor { get; set; }

        /// <summary>
        /// Gets or sets the provider manager.
        /// </summary>
        /// <value>The provider manager.</value>
        private IProviderManager ProviderManager { get; set; }

        /// <summary>
        /// Gets or sets the HTTP server.
        /// </summary>
        /// <value>The HTTP server.</value>
        private IHttpServer HttpServer { get; set; }

        private IDtoService DtoService { get; set; }

        public IImageProcessor ImageProcessor { get; set; }

        /// <summary>
        /// Gets or sets the media encoder.
        /// </summary>
        /// <value>The media encoder.</value>
        private IMediaEncoder MediaEncoder { get; set; }

        private ISubtitleEncoder SubtitleEncoder { get; set; }

        private ISessionManager SessionManager { get; set; }

        private ILiveTvManager LiveTvManager { get; set; }

        public LocalizationManager LocalizationManager { get; set; }

        private IEncodingManager EncodingManager { get; set; }

        private IChannelManager ChannelManager { get; set; }

        /// <summary>
        /// Gets or sets the user data repository.
        /// </summary>
        /// <value>The user data repository.</value>
        private IUserDataManager UserDataManager { get; set; }

        private IUserRepository UserRepository { get; set; }

        internal SqliteItemRepository ItemRepository { get; set; }

        private INotificationManager NotificationManager { get; set; }

        private ISubtitleManager SubtitleManager { get; set; }

        private IChapterManager ChapterManager { get; set; }

        private IDeviceManager DeviceManager { get; set; }

        internal IUserViewManager UserViewManager { get; set; }

        private IAuthenticationRepository AuthenticationRepository { get; set; }

        private ITVSeriesManager TVSeriesManager { get; set; }

        private ICollectionManager CollectionManager { get; set; }

        private IMediaSourceManager MediaSourceManager { get; set; }

        private IPlaylistManager PlaylistManager { get; set; }

        private readonly IConfiguration _configuration;

        /// <summary>
        /// Gets or sets the installation manager.
        /// </summary>
        /// <value>The installation manager.</value>
        protected IInstallationManager InstallationManager { get; private set; }

        /// <summary>
        /// Gets or sets the zip client.
        /// </summary>
        /// <value>The zip client.</value>
        protected IZipClient ZipClient { get; private set; }

        protected IHttpResultFactory HttpResultFactory { get; private set; }

        protected IAuthService AuthService { get; private set; }

        public IStartupOptions StartupOptions { get; }

        internal IImageEncoder ImageEncoder { get; private set; }

        protected IProcessFactory ProcessFactory { get; private set; }

        protected readonly IXmlSerializer XmlSerializer;

        protected ISocketFactory SocketFactory { get; private set; }

        protected ITaskManager TaskManager { get; private set; }

        public IHttpClient HttpClient { get; private set; }

        protected INetworkManager NetworkManager { get; set; }

        public IJsonSerializer JsonSerializer { get; private set; }

        protected IIsoManager IsoManager { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ApplicationHost" /> class.
        /// </summary>
        public ApplicationHost(
            ServerApplicationPaths applicationPaths,
            ILoggerFactory loggerFactory,
            IStartupOptions options,
            IFileSystem fileSystem,
            IImageEncoder imageEncoder,
            INetworkManager networkManager,
            IConfiguration configuration)
        {
            _configuration = configuration;

            XmlSerializer = new MyXmlSerializer(fileSystem, loggerFactory);

            NetworkManager = networkManager;
            networkManager.LocalSubnetsFn = GetConfiguredLocalSubnets;

            ApplicationPaths = applicationPaths;
            LoggerFactory = loggerFactory;
            FileSystemManager = fileSystem;

            ConfigurationManager = new ServerConfigurationManager(ApplicationPaths, LoggerFactory, XmlSerializer, FileSystemManager);

            Logger = LoggerFactory.CreateLogger("App");

            StartupOptions = options;

            ImageEncoder = imageEncoder;

            fileSystem.AddShortcutHandler(new MbLinkShortcutHandler(fileSystem));

            NetworkManager.NetworkChanged += NetworkManager_NetworkChanged;
        }

        public string ExpandVirtualPath(string path)
        {
            var appPaths = ApplicationPaths;

            return path.Replace(appPaths.VirtualDataPath, appPaths.DataPath, StringComparison.OrdinalIgnoreCase)
                .Replace(appPaths.VirtualInternalMetadataPath, appPaths.InternalMetadataPath, StringComparison.OrdinalIgnoreCase);
        }

        public string ReverseVirtualPath(string path)
        {
            var appPaths = ApplicationPaths;

            return path.Replace(appPaths.DataPath, appPaths.VirtualDataPath, StringComparison.OrdinalIgnoreCase)
                .Replace(appPaths.InternalMetadataPath, appPaths.VirtualInternalMetadataPath, StringComparison.OrdinalIgnoreCase);
        }

        private string[] GetConfiguredLocalSubnets()
        {
            return ServerConfigurationManager.Configuration.LocalNetworkSubnets;
        }

        private void NetworkManager_NetworkChanged(object sender, EventArgs e)
        {
            _validAddressResults.Clear();
        }

        public string ApplicationVersion { get; } = typeof(ApplicationHost).Assembly.GetName().Version.ToString(3);

        /// <summary>
        /// Gets the current application user agent
        /// </summary>
        /// <value>The application user agent.</value>
        public string ApplicationUserAgent => Name.Replace(' ','-') + '/' + ApplicationVersion;

        /// <summary>
        /// Gets the email address for use within a comment section of a user agent field.
        /// Presently used to provide contact information to MusicBrainz service.
        /// </summary>
        public string ApplicationUserAgentAddress { get; } = "team@jellyfin.org";

        private string _productName;

        /// <summary>
        /// Gets the current application name
        /// </summary>
        /// <value>The application name.</value>
        public string ApplicationProductName
            => _productName ?? (_productName = FileVersionInfo.GetVersionInfo(Assembly.GetEntryAssembly().Location).ProductName);

        private DeviceId _deviceId;

        public string SystemId
        {
            get
            {
                if (_deviceId == null)
                {
                    _deviceId = new DeviceId(ApplicationPaths, LoggerFactory);
                }

                return _deviceId.Value;
            }
        }

        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <value>The name.</value>
        public string Name => ApplicationProductName;

        /// <summary>
        /// Creates an instance of type and resolves all constructor dependencies
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>System.Object.</returns>
        public object CreateInstance(Type type)
            => ActivatorUtilities.CreateInstance(_serviceProvider, type);

        /// <summary>
        /// Creates an instance of type and resolves all constructor dependencies
        /// </summary>
        /// /// <typeparam name="T">The type</typeparam>
        /// <returns>T</returns>
        public T CreateInstance<T>()
            => ActivatorUtilities.CreateInstance<T>(_serviceProvider);

        /// <summary>
        /// Creates the instance safe.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>System.Object.</returns>
        protected object CreateInstanceSafe(Type type)
        {
            try
            {
                Logger.LogDebug("Creating instance of {Type}", type);
                return ActivatorUtilities.CreateInstance(_serviceProvider, type);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error creating {Type}", type);
                return null;
            }
        }

        /// <summary>
        /// Resolves this instance.
        /// </summary>
        /// <typeparam name="T">The type</typeparam>
        /// <returns>``0.</returns>
        public T Resolve<T>() => _serviceProvider.GetService<T>();

        /// <summary>
        /// Gets the export types.
        /// </summary>
        /// <typeparam name="T">The type</typeparam>
        /// <returns>IEnumerable{Type}.</returns>
        public IEnumerable<Type> GetExportTypes<T>()
        {
            var currentType = typeof(T);

            return AllConcreteTypes.Where(i => currentType.IsAssignableFrom(i));
        }

        /// <summary>
        /// Gets the exports.
        /// </summary>
        /// <typeparam name="T">The type</typeparam>
        /// <param name="manageLifetime">if set to <c>true</c> [manage lifetime].</param>
        /// <returns>IEnumerable{``0}.</returns>
        public IEnumerable<T> GetExports<T>(bool manageLifetime = true)
        {
            var parts = GetExportTypes<T>()
                .Select(CreateInstanceSafe)
                .Where(i => i != null)
                .Cast<T>()
                .ToList(); // Convert to list so this isn't executed for each iteration

            if (manageLifetime)
            {
                lock (_disposableParts)
                {
                    _disposableParts.AddRange(parts.OfType<IDisposable>());
                }
            }

            return parts;
        }

        /// <summary>
        /// Runs the startup tasks.
        /// </summary>
        public async Task RunStartupTasksAsync()
        {
            Logger.LogInformation("Running startup tasks");

            Resolve<ITaskManager>().AddTasks(GetExports<IScheduledTask>(false));

            ConfigurationManager.ConfigurationUpdated += OnConfigurationUpdated;

            MediaEncoder.SetFFmpegPath();

            Logger.LogInformation("ServerId: {0}", SystemId);

            var entryPoints = GetExports<IServerEntryPoint>().ToList();

            var stopWatch = new Stopwatch();
            stopWatch.Start();
            await Task.WhenAll(StartEntryPoints(entryPoints, true)).ConfigureAwait(false);
            Logger.LogInformation("Executed all pre-startup entry points in {Elapsed:g}", stopWatch.Elapsed);

            Logger.LogInformation("Core startup complete");
            HttpServer.GlobalResponse = null;

            stopWatch.Restart();
            await Task.WhenAll(StartEntryPoints(entryPoints, false)).ConfigureAwait(false);
            Logger.LogInformation("Executed all post-startup entry points in {Elapsed:g}", stopWatch.Elapsed);
            stopWatch.Stop();
        }

        private IEnumerable<Task> StartEntryPoints(IEnumerable<IServerEntryPoint> entryPoints, bool isBeforeStartup)
        {
            foreach (var entryPoint in entryPoints)
            {
                if (isBeforeStartup != (entryPoint is IRunBeforeStartup))
                {
                    continue;
                }

                Logger.LogDebug("Starting entry point {Type}", entryPoint.GetType());

                yield return entryPoint.RunAsync();
            }
        }

        public async Task InitAsync(IServiceCollection serviceCollection)
        {
            HttpPort = ServerConfigurationManager.Configuration.HttpServerPortNumber;
            HttpsPort = ServerConfigurationManager.Configuration.HttpsPortNumber;

            // Safeguard against invalid configuration
            if (HttpPort == HttpsPort)
            {
                HttpPort = ServerConfiguration.DefaultHttpPort;
                HttpsPort = ServerConfiguration.DefaultHttpsPort;
            }

            JsonSerializer = new JsonSerializer(FileSystemManager);

            if (Plugins != null)
            {
                var pluginBuilder = new StringBuilder();

                foreach (var plugin in Plugins)
                {
                    pluginBuilder.AppendLine(string.Format("{0} {1}", plugin.Name, plugin.Version));
                }

                Logger.LogInformation("Plugins: {plugins}", pluginBuilder.ToString());
            }

            DiscoverTypes();

            await RegisterResources(serviceCollection).ConfigureAwait(false);

            FindParts();

            string contentRoot = ServerConfigurationManager.Configuration.DashboardSourcePath;
            if (string.IsNullOrEmpty(contentRoot))
            {
                contentRoot = ServerConfigurationManager.ApplicationPaths.WebPath;
            }

            var host = new WebHostBuilder()
                .UseKestrel(options =>
                {
                    options.ListenAnyIP(HttpPort);

                    if (EnableHttps && Certificate != null)
                    {
                        options.ListenAnyIP(HttpsPort, listenOptions => { listenOptions.UseHttps(Certificate); });
                    }
                })
                .UseContentRoot(contentRoot)
                .ConfigureServices(services =>
                {
                    services.AddResponseCompression();
                    services.AddHttpContextAccessor();
                })
                .Configure(app =>
                {
                    app.UseWebSockets();

                    app.UseResponseCompression();
                    // TODO app.UseMiddleware<WebSocketMiddleware>();
                    app.Use(ExecuteWebsocketHandlerAsync);
                    app.Use(ExecuteHttpHandlerAsync);
                })
                .Build();

            await host.StartAsync().ConfigureAwait(false);
        }

        private async Task ExecuteWebsocketHandlerAsync(HttpContext context, Func<Task> next)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                await next().ConfigureAwait(false);
                return;
            }

            await HttpServer.ProcessWebSocketRequest(context).ConfigureAwait(false);
        }

        private async Task ExecuteHttpHandlerAsync(HttpContext context, Func<Task> next)
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                await next().ConfigureAwait(false);
                return;
            }

            var request = context.Request;
            var response = context.Response;
            var localPath = context.Request.Path.ToString();

            var req = new WebSocketSharpRequest(request, response, request.Path, Logger);
            await HttpServer.RequestHandler(req, request.GetDisplayUrl(), request.Host.ToString(), localPath, CancellationToken.None).ConfigureAwait(false);
        }

        public static IStreamHelper StreamHelper { get; set; }

        /// <summary>
        /// Registers resources that classes will depend on
        /// </summary>
        protected async Task RegisterResources(IServiceCollection serviceCollection)
        {
            serviceCollection.AddMemoryCache();

            serviceCollection.AddSingleton(ConfigurationManager);
            serviceCollection.AddSingleton<IApplicationHost>(this);

            serviceCollection.AddSingleton<IApplicationPaths>(ApplicationPaths);

            serviceCollection.AddSingleton<IConfiguration>(_configuration);

            serviceCollection.AddSingleton(JsonSerializer);

            serviceCollection.AddSingleton(LoggerFactory);
            serviceCollection.AddLogging();
            serviceCollection.AddSingleton(Logger);

            serviceCollection.AddSingleton(FileSystemManager);
            serviceCollection.AddSingleton<TvDbClientManager>();

            HttpClient = new HttpClientManager.HttpClientManager(
                ApplicationPaths,
                LoggerFactory.CreateLogger<HttpClientManager.HttpClientManager>(),
                FileSystemManager,
                () => ApplicationUserAgent);
            serviceCollection.AddSingleton(HttpClient);

            serviceCollection.AddSingleton(NetworkManager);

            IsoManager = new IsoManager();
            serviceCollection.AddSingleton(IsoManager);

            TaskManager = new TaskManager(ApplicationPaths, JsonSerializer, LoggerFactory, FileSystemManager);
            serviceCollection.AddSingleton(TaskManager);

            serviceCollection.AddSingleton(XmlSerializer);

            ProcessFactory = new ProcessFactory();
            serviceCollection.AddSingleton(ProcessFactory);

            ApplicationHost.StreamHelper = new StreamHelper();
            serviceCollection.AddSingleton(StreamHelper);

            serviceCollection.AddSingleton(typeof(ICryptoProvider), typeof(CryptographyProvider));

            SocketFactory = new SocketFactory();
            serviceCollection.AddSingleton(SocketFactory);

            serviceCollection.AddSingleton(typeof(IInstallationManager), typeof(InstallationManager));

            ZipClient = new ZipClient();
            serviceCollection.AddSingleton(ZipClient);

            HttpResultFactory = new HttpResultFactory(LoggerFactory, FileSystemManager, JsonSerializer, StreamHelper);
            serviceCollection.AddSingleton(HttpResultFactory);

            serviceCollection.AddSingleton<IServerApplicationHost>(this);
            serviceCollection.AddSingleton<IServerApplicationPaths>(ApplicationPaths);

            serviceCollection.AddSingleton(ServerConfigurationManager);

            LocalizationManager = new LocalizationManager(ServerConfigurationManager, JsonSerializer, LoggerFactory);
            await LocalizationManager.LoadAll().ConfigureAwait(false);
            serviceCollection.AddSingleton<ILocalizationManager>(LocalizationManager);

            serviceCollection.AddSingleton<IBlurayExaminer>(new BdInfoExaminer(FileSystemManager));

            UserDataManager = new UserDataManager(LoggerFactory, ServerConfigurationManager, () => UserManager);
            serviceCollection.AddSingleton(UserDataManager);

            var displayPreferencesRepo = new SqliteDisplayPreferencesRepository(LoggerFactory, JsonSerializer, ApplicationPaths, FileSystemManager);
            serviceCollection.AddSingleton<IDisplayPreferencesRepository>(displayPreferencesRepo);

            ItemRepository = new SqliteItemRepository(ServerConfigurationManager, this, JsonSerializer, LoggerFactory, LocalizationManager);
            serviceCollection.AddSingleton<IItemRepository>(ItemRepository);

            AuthenticationRepository = GetAuthenticationRepository();
            serviceCollection.AddSingleton(AuthenticationRepository);

            UserRepository = GetUserRepository();

            UserManager = new UserManager(LoggerFactory, ServerConfigurationManager, UserRepository, XmlSerializer, NetworkManager, () => ImageProcessor, () => DtoService, this, JsonSerializer, FileSystemManager);
            serviceCollection.AddSingleton(UserManager);

            LibraryManager = new LibraryManager(this, LoggerFactory, TaskManager, UserManager, ServerConfigurationManager, UserDataManager, () => LibraryMonitor, FileSystemManager, () => ProviderManager, () => UserViewManager);
            serviceCollection.AddSingleton(LibraryManager);

            // TODO wtaylor: investigate use of second music manager
            var musicManager = new MusicManager(LibraryManager);
            serviceCollection.AddSingleton<IMusicManager>(new MusicManager(LibraryManager));

            LibraryMonitor = new LibraryMonitor(LoggerFactory, LibraryManager, ServerConfigurationManager, FileSystemManager);
            serviceCollection.AddSingleton(LibraryMonitor);

            serviceCollection.AddSingleton<ISearchEngine>(new SearchEngine(LoggerFactory, LibraryManager, UserManager));

            CertificateInfo = GetCertificateInfo(true);
            Certificate = GetCertificate(CertificateInfo);

            HttpServer = new HttpListenerHost(
                this,
                LoggerFactory,
                ServerConfigurationManager,
                _configuration,
                NetworkManager,
                JsonSerializer,
                XmlSerializer,
                CreateHttpListener())
            {
                GlobalResponse = LocalizationManager.GetLocalizedString("StartupEmbyServerIsLoading")
            };

            serviceCollection.AddSingleton(HttpServer);

            ImageProcessor = GetImageProcessor();
            serviceCollection.AddSingleton(ImageProcessor);

            TVSeriesManager = new TVSeriesManager(UserManager, UserDataManager, LibraryManager, ServerConfigurationManager);
            serviceCollection.AddSingleton(TVSeriesManager);

            DeviceManager = new DeviceManager(AuthenticationRepository, JsonSerializer, LibraryManager, LocalizationManager, UserManager, FileSystemManager, LibraryMonitor, ServerConfigurationManager);
            serviceCollection.AddSingleton(DeviceManager);

            MediaSourceManager = new MediaSourceManager(ItemRepository, ApplicationPaths, LocalizationManager, UserManager, LibraryManager, LoggerFactory, JsonSerializer, FileSystemManager, UserDataManager, () => MediaEncoder);
            serviceCollection.AddSingleton(MediaSourceManager);

            SubtitleManager = new SubtitleManager(LoggerFactory, FileSystemManager, LibraryMonitor, MediaSourceManager, LocalizationManager);
            serviceCollection.AddSingleton(SubtitleManager);

            ProviderManager = new ProviderManager(HttpClient, SubtitleManager, ServerConfigurationManager, LibraryMonitor, LoggerFactory, FileSystemManager, ApplicationPaths, () => LibraryManager, JsonSerializer);
            serviceCollection.AddSingleton(ProviderManager);

            DtoService = new DtoService(LoggerFactory, LibraryManager, UserDataManager, ItemRepository, ImageProcessor, ProviderManager, this, () => MediaSourceManager, () => LiveTvManager);
            serviceCollection.AddSingleton(DtoService);

            ChannelManager = new ChannelManager(UserManager, DtoService, LibraryManager, LoggerFactory, ServerConfigurationManager, FileSystemManager, UserDataManager, JsonSerializer, ProviderManager);
            serviceCollection.AddSingleton(ChannelManager);

            SessionManager = new SessionManager(UserDataManager, LoggerFactory, LibraryManager, UserManager, musicManager, DtoService, ImageProcessor, this, AuthenticationRepository, DeviceManager, MediaSourceManager);
            serviceCollection.AddSingleton(SessionManager);

            serviceCollection.AddSingleton<IDlnaManager>(
                new DlnaManager(XmlSerializer, FileSystemManager, ApplicationPaths, LoggerFactory, JsonSerializer, this));

            CollectionManager = new CollectionManager(LibraryManager, ApplicationPaths, LocalizationManager, FileSystemManager, LibraryMonitor, LoggerFactory, ProviderManager);
            serviceCollection.AddSingleton(CollectionManager);

            PlaylistManager = new PlaylistManager(LibraryManager, FileSystemManager, LibraryMonitor, LoggerFactory, UserManager, ProviderManager);
            serviceCollection.AddSingleton(PlaylistManager);

            LiveTvManager = new LiveTvManager(this, ServerConfigurationManager, LoggerFactory, ItemRepository, ImageProcessor, UserDataManager, DtoService, UserManager, LibraryManager, TaskManager, LocalizationManager, JsonSerializer, FileSystemManager, () => ChannelManager);
            serviceCollection.AddSingleton(LiveTvManager);

            UserViewManager = new UserViewManager(LibraryManager, LocalizationManager, UserManager, ChannelManager, LiveTvManager, ServerConfigurationManager);
            serviceCollection.AddSingleton(UserViewManager);

            NotificationManager = new NotificationManager(LoggerFactory, UserManager, ServerConfigurationManager);
            serviceCollection.AddSingleton(NotificationManager);

            serviceCollection.AddSingleton<IDeviceDiscovery>(
                new DeviceDiscovery(LoggerFactory, ServerConfigurationManager, SocketFactory));

            ChapterManager = new ChapterManager(LibraryManager, LoggerFactory, ServerConfigurationManager, ItemRepository);
            serviceCollection.AddSingleton(ChapterManager);

            MediaEncoder = new MediaBrowser.MediaEncoding.Encoder.MediaEncoder(
                LoggerFactory,
                JsonSerializer,
                StartupOptions.FFmpegPath,
                ServerConfigurationManager,
                FileSystemManager,
                () => SubtitleEncoder,
                () => MediaSourceManager,
                ProcessFactory,
                5000,
                LocalizationManager);
            serviceCollection.AddSingleton(MediaEncoder);

            EncodingManager = new MediaEncoder.EncodingManager(FileSystemManager, LoggerFactory, MediaEncoder, ChapterManager, LibraryManager);
            serviceCollection.AddSingleton(EncodingManager);

            var activityLogRepo = GetActivityLogRepository();
            serviceCollection.AddSingleton(activityLogRepo);
            serviceCollection.AddSingleton<IActivityManager>(new ActivityManager(LoggerFactory, activityLogRepo, UserManager));

            var authContext = new AuthorizationContext(AuthenticationRepository, UserManager);
            serviceCollection.AddSingleton<IAuthorizationContext>(authContext);
            serviceCollection.AddSingleton<ISessionContext>(new SessionContext(UserManager, authContext, SessionManager));

            AuthService = new AuthService(UserManager, authContext, ServerConfigurationManager, SessionManager, NetworkManager);
            serviceCollection.AddSingleton(AuthService);

            SubtitleEncoder = new MediaBrowser.MediaEncoding.Subtitles.SubtitleEncoder(LibraryManager, LoggerFactory, ApplicationPaths, FileSystemManager, MediaEncoder, JsonSerializer, HttpClient, MediaSourceManager, ProcessFactory);
            serviceCollection.AddSingleton(SubtitleEncoder);

            serviceCollection.AddSingleton(typeof(IResourceFileManager), typeof(ResourceFileManager));

            displayPreferencesRepo.Initialize();

            var userDataRepo = new SqliteUserDataRepository(LoggerFactory, ApplicationPaths);

            SetStaticProperties();

            ((UserManager)UserManager).Initialize();

            ((UserDataManager)UserDataManager).Repository = userDataRepo;
            ItemRepository.Initialize(userDataRepo, UserManager);
            ((LibraryManager)LibraryManager).ItemRepository = ItemRepository;

            _serviceProvider = serviceCollection.BuildServiceProvider();
        }

        public static void LogEnvironmentInfo(ILogger logger, IApplicationPaths appPaths)
        {
            // Distinct these to prevent users from reporting problems that aren't actually problems
            var commandLineArgs = Environment
                .GetCommandLineArgs()
                .Distinct();

            logger.LogInformation("Arguments: {Args}", commandLineArgs);
            logger.LogInformation("Operating system: {OS}", OperatingSystem.Name);
            logger.LogInformation("Architecture: {Architecture}", RuntimeInformation.OSArchitecture);
            logger.LogInformation("64-Bit Process: {Is64Bit}", Environment.Is64BitProcess);
            logger.LogInformation("User Interactive: {IsUserInteractive}", Environment.UserInteractive);
            logger.LogInformation("Processor count: {ProcessorCount}", Environment.ProcessorCount);
            logger.LogInformation("Program data path: {ProgramDataPath}", appPaths.ProgramDataPath);
            logger.LogInformation("Web resources path: {WebPath}", appPaths.WebPath);
            logger.LogInformation("Application directory: {ApplicationPath}", appPaths.ProgramSystemPath);
        }

        private X509Certificate2 GetCertificate(CertificateInfo info)
        {
            var certificateLocation = info?.Path;

            if (string.IsNullOrWhiteSpace(certificateLocation))
            {
                return null;
            }

            try
            {
                if (!File.Exists(certificateLocation))
                {
                    return null;
                }

                // Don't use an empty string password
                var password = string.IsNullOrWhiteSpace(info.Password) ? null : info.Password;

                var localCert = new X509Certificate2(certificateLocation, password);
                // localCert.PrivateKey = PrivateKey.CreateFromFile(pvk_file).RSA;
                if (!localCert.HasPrivateKey)
                {
                    Logger.LogError("No private key included in SSL cert {CertificateLocation}.", certificateLocation);
                    return null;
                }

                return localCert;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading cert from {CertificateLocation}", certificateLocation);
                return null;
            }
        }

        private IImageProcessor GetImageProcessor()
        {
            return new ImageProcessor(LoggerFactory, ServerConfigurationManager.ApplicationPaths, FileSystemManager, ImageEncoder, () => LibraryManager, () => MediaEncoder);
        }

        /// <summary>
        /// Gets the user repository.
        /// </summary>
        /// <returns>Task{IUserRepository}.</returns>
        private IUserRepository GetUserRepository()
        {
            var repo = new SqliteUserRepository(LoggerFactory, ApplicationPaths, JsonSerializer);

            repo.Initialize();

            return repo;
        }

        private IAuthenticationRepository GetAuthenticationRepository()
        {
            var repo = new AuthenticationRepository(LoggerFactory, ServerConfigurationManager);

            repo.Initialize();

            return repo;
        }

        private IActivityRepository GetActivityLogRepository()
        {
            var repo = new ActivityRepository(LoggerFactory, ServerConfigurationManager.ApplicationPaths, FileSystemManager);

            repo.Initialize();

            return repo;
        }

        /// <summary>
        /// Dirty hacks
        /// </summary>
        private void SetStaticProperties()
        {
            ItemRepository.ImageProcessor = ImageProcessor;

            // For now there's no real way to inject these properly
            BaseItem.Logger = LoggerFactory.CreateLogger("BaseItem");
            BaseItem.ConfigurationManager = ServerConfigurationManager;
            BaseItem.LibraryManager = LibraryManager;
            BaseItem.ProviderManager = ProviderManager;
            BaseItem.LocalizationManager = LocalizationManager;
            BaseItem.ItemRepository = ItemRepository;
            User.UserManager = UserManager;
            BaseItem.FileSystem = FileSystemManager;
            BaseItem.UserDataManager = UserDataManager;
            BaseItem.ChannelManager = ChannelManager;
            Video.LiveTvManager = LiveTvManager;
            Folder.UserViewManager = UserViewManager;
            UserView.TVSeriesManager = TVSeriesManager;
            UserView.CollectionManager = CollectionManager;
            BaseItem.MediaSourceManager = MediaSourceManager;
            CollectionFolder.XmlSerializer = XmlSerializer;
            CollectionFolder.JsonSerializer = JsonSerializer;
            CollectionFolder.ApplicationHost = this;
            AuthenticatedAttribute.AuthService = AuthService;
        }

        private async void PluginInstalled(object sender, GenericEventArgs<PackageVersionInfo> args)
        {
            string dir = Path.Combine(ApplicationPaths.PluginsPath, args.Argument.name);
            var types = Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories)
                        .Select(x => Assembly.LoadFrom(x))
                        .SelectMany(x => x.ExportedTypes)
                        .Where(x => x.IsClass && !x.IsAbstract && !x.IsInterface && !x.IsGenericType)
                        .ToList();

            types.AddRange(types);

            var plugins = types.Where(x => x.IsAssignableFrom(typeof(IPlugin)))
                    .Select(CreateInstanceSafe)
                    .Where(x => x != null)
                    .Cast<IPlugin>()
                    .Select(LoadPlugin)
                    .Where(x => x != null)
                    .ToArray();

            int oldLen = _plugins.Length;
            Array.Resize<IPlugin>(ref _plugins, _plugins.Length + plugins.Length);
            plugins.CopyTo(_plugins, oldLen);

            var entries = types.Where(x => x.IsAssignableFrom(typeof(IServerEntryPoint)))
                .Select(CreateInstanceSafe)
                .Where(x => x != null)
                .Cast<IServerEntryPoint>()
                .ToList();

            await Task.WhenAll(StartEntryPoints(entries, true));
            await Task.WhenAll(StartEntryPoints(entries, false));
        }

        /// <summary>
        /// Finds the parts.
        /// </summary>
        protected void FindParts()
        {
            InstallationManager = _serviceProvider.GetService<IInstallationManager>();
            InstallationManager.PluginInstalled += PluginInstalled;

            if (!ServerConfigurationManager.Configuration.IsPortAuthorized)
            {
                ServerConfigurationManager.Configuration.IsPortAuthorized = true;
                ConfigurationManager.SaveConfiguration();
            }

            ConfigurationManager.AddParts(GetExports<IConfigurationFactory>());
            Plugins = GetExports<IPlugin>()
                        .Select(LoadPlugin)
                        .Where(i => i != null)
                        .ToArray();

            HttpServer.Init(GetExports<IService>(false), GetExports<IWebSocketListener>(), GetUrlPrefixes());

            LibraryManager.AddParts(
                GetExports<IResolverIgnoreRule>(),
                GetExports<IItemResolver>(),
                GetExports<IIntroProvider>(),
                GetExports<IBaseItemComparer>(),
                GetExports<ILibraryPostScanTask>());

            ProviderManager.AddParts(
                GetExports<IImageProvider>(),
                GetExports<IMetadataService>(),
                GetExports<IMetadataProvider>(),
                GetExports<IMetadataSaver>(),
                GetExports<IExternalId>());

            ImageProcessor.AddParts(GetExports<IImageEnhancer>());

            LiveTvManager.AddParts(GetExports<ILiveTvService>(), GetExports<ITunerHost>(), GetExports<IListingsProvider>());

            SubtitleManager.AddParts(GetExports<ISubtitleProvider>());

            ChannelManager.AddParts(GetExports<IChannel>());

            MediaSourceManager.AddParts(GetExports<IMediaSourceProvider>());

            NotificationManager.AddParts(GetExports<INotificationService>(), GetExports<INotificationTypeFactory>());
            UserManager.AddParts(GetExports<IAuthenticationProvider>(), GetExports<IPasswordResetProvider>());

            IsoManager.AddParts(GetExports<IIsoMounter>());
        }

        private IPlugin LoadPlugin(IPlugin plugin)
        {
            try
            {
                if (plugin is IPluginAssembly assemblyPlugin)
                {
                    var assembly = plugin.GetType().Assembly;
                    var assemblyName = assembly.GetName();
                    var assemblyFilePath = assembly.Location;

                    var dataFolderPath = Path.Combine(ApplicationPaths.PluginsPath, Path.GetFileNameWithoutExtension(assemblyFilePath));

                    assemblyPlugin.SetAttributes(assemblyFilePath, dataFolderPath, assemblyName.Version);

                    try
                    {
                        var idAttributes = assembly.GetCustomAttributes(typeof(GuidAttribute), true);
                        if (idAttributes.Length > 0)
                        {
                            var attribute = (GuidAttribute)idAttributes[0];
                            var assemblyId = new Guid(attribute.Value);

                            assemblyPlugin.SetId(assemblyId);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Error getting plugin Id from {PluginName}.", plugin.GetType().FullName);
                    }
                }

                if (plugin is IHasPluginConfiguration hasPluginConfiguration)
                {
                    hasPluginConfiguration.SetStartupInfo(s => Directory.CreateDirectory(s));
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading plugin {PluginName}", plugin.GetType().FullName);
                return null;
            }

            return plugin;
        }

        /// <summary>
        /// Discovers the types.
        /// </summary>
        protected void DiscoverTypes()
        {
            Logger.LogInformation("Loading assemblies");

            AllConcreteTypes = GetTypes(GetComposablePartAssemblies()).ToArray();
        }

        private IEnumerable<Type> GetTypes(IEnumerable<Assembly> assemblies)
        {
            foreach (var ass in assemblies)
            {
                Type[] exportedTypes;
                try
                {
                    exportedTypes = ass.GetExportedTypes();
                }
                catch (TypeLoadException ex)
                {
                    Logger.LogError(ex, "Error getting exported types from {Assembly}", ass.FullName);
                    continue;
                }

                foreach (Type type in exportedTypes)
                {
                    if (type.IsClass && !type.IsAbstract && !type.IsInterface && !type.IsGenericType)
                    {
                        yield return type;
                    }
                }
            }
        }

        private CertificateInfo CertificateInfo { get; set; }

        protected X509Certificate2 Certificate { get; private set; }

        private IEnumerable<string> GetUrlPrefixes()
        {
            var hosts = new[] { "+" };

            return hosts.SelectMany(i =>
            {
                var prefixes = new List<string>
                {
                    "http://" + i + ":" + HttpPort + "/"
                };

                if (CertificateInfo != null)
                {
                    prefixes.Add("https://" + i + ":" + HttpsPort + "/");
                }

                return prefixes;
            });
        }

        protected IHttpListener CreateHttpListener() => new WebSocketSharpListener(Logger);

        private CertificateInfo GetCertificateInfo(bool generateCertificate)
        {
            if (!string.IsNullOrWhiteSpace(ServerConfigurationManager.Configuration.CertificatePath))
            {
                // Custom cert
                return new CertificateInfo
                {
                    Path = ServerConfigurationManager.Configuration.CertificatePath,
                    Password = ServerConfigurationManager.Configuration.CertificatePassword
                };
            }

            // Generate self-signed cert
            var certHost = GetHostnameFromExternalDns(ServerConfigurationManager.Configuration.WanDdns);
            var certPath = Path.Combine(ServerConfigurationManager.ApplicationPaths.ProgramDataPath, "ssl", "cert_" + (certHost + "2").GetMD5().ToString("N") + ".pfx");
            const string Password = "embycert";

            return new CertificateInfo
            {
                Path = certPath,
                Password = Password
            };
        }

        /// <summary>
        /// Called when [configuration updated].
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void OnConfigurationUpdated(object sender, EventArgs e)
        {
            var requiresRestart = false;

            // Don't do anything if these haven't been set yet
            if (HttpPort != 0 && HttpsPort != 0)
            {
                // Need to restart if ports have changed
                if (ServerConfigurationManager.Configuration.HttpServerPortNumber != HttpPort ||
                    ServerConfigurationManager.Configuration.HttpsPortNumber != HttpsPort)
                {
                    if (ServerConfigurationManager.Configuration.IsPortAuthorized)
                    {
                        ServerConfigurationManager.Configuration.IsPortAuthorized = false;
                        ServerConfigurationManager.SaveConfiguration();

                        requiresRestart = true;
                    }
                }
            }

            if (!HttpServer.UrlPrefixes.SequenceEqual(GetUrlPrefixes(), StringComparer.OrdinalIgnoreCase))
            {
                requiresRestart = true;
            }

            var currentCertPath = CertificateInfo?.Path;
            var newCertInfo = GetCertificateInfo(false);
            var newCertPath = newCertInfo?.Path;

            if (!string.Equals(currentCertPath, newCertPath, StringComparison.OrdinalIgnoreCase))
            {
                requiresRestart = true;
            }

            if (requiresRestart)
            {
                Logger.LogInformation("App needs to be restarted due to configuration change.");

                NotifyPendingRestart();
            }
        }

        /// <summary>
        /// Notifies that the kernel that a change has been made that requires a restart
        /// </summary>
        public void NotifyPendingRestart()
        {
            Logger.LogInformation("App needs to be restarted.");

            var changed = !HasPendingRestart;

            HasPendingRestart = true;

            if (changed)
            {
                EventHelper.QueueEventIfNotNull(HasPendingRestartChanged, this, EventArgs.Empty, Logger);
            }
        }

        /// <summary>
        /// Restarts this instance.
        /// </summary>
        public void Restart()
        {
            if (!CanSelfRestart)
            {
                throw new PlatformNotSupportedException("The server is unable to self-restart. Please restart manually.");
            }

            if (IsShuttingDown)
            {
                return;
            }

            IsShuttingDown = true;

            Task.Run(async () =>
            {
                try
                {
                    await SessionManager.SendServerRestartNotification(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error sending server restart notification");
                }

                Logger.LogInformation("Calling RestartInternal");

                RestartInternal();
            });
        }

        protected abstract void RestartInternal();

        /// <summary>
        /// Gets the composable part assemblies.
        /// </summary>
        /// <returns>IEnumerable{Assembly}.</returns>
        protected IEnumerable<Assembly> GetComposablePartAssemblies()
        {
            if (Directory.Exists(ApplicationPaths.PluginsPath))
            {
                foreach (var file in Directory.EnumerateFiles(ApplicationPaths.PluginsPath, "*.dll", SearchOption.AllDirectories))
                {
                    Assembly plugAss;
                    try
                    {
                        plugAss = Assembly.LoadFrom(file);
                    }
                    catch (FileLoadException ex)
                    {
                        Logger.LogError(ex, "Failed to load assembly {Path}", file);
                        continue;
                    }

                    Logger.LogInformation("Loaded assembly {Assembly} from {Path}", plugAss.FullName, file);
                    yield return plugAss;
                }
            }

            // Include composable parts in the Api assembly
            yield return typeof(ApiEntryPoint).Assembly;

            // Include composable parts in the Dashboard assembly
            yield return typeof(DashboardService).Assembly;

            // Include composable parts in the Model assembly
            yield return typeof(SystemInfo).Assembly;

            // Include composable parts in the Common assembly
            yield return typeof(IApplicationHost).Assembly;

            // Include composable parts in the Controller assembly
            yield return typeof(IServerApplicationHost).Assembly;

            // Include composable parts in the Providers assembly
            yield return typeof(ProviderUtils).Assembly;

            // Include composable parts in the Photos assembly
            yield return typeof(PhotoProvider).Assembly;

            // Emby.Server implementations
            yield return typeof(InstallationManager).Assembly;

            // MediaEncoding
            yield return typeof(MediaBrowser.MediaEncoding.Encoder.MediaEncoder).Assembly;

            // Dlna
            yield return typeof(DlnaEntryPoint).Assembly;

            // Local metadata
            yield return typeof(BoxSetXmlSaver).Assembly;

            // Notifications
            yield return typeof(NotificationManager).Assembly;

            // Xbmc
            yield return typeof(ArtistNfoProvider).Assembly;

            foreach (var i in GetAssembliesWithPartsInternal())
            {
                yield return i;
            }
        }

        protected abstract IEnumerable<Assembly> GetAssembliesWithPartsInternal();

        /// <summary>
        /// Gets the system status.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token</param>
        /// <returns>SystemInfo.</returns>
        public async Task<SystemInfo> GetSystemInfo(CancellationToken cancellationToken)
        {
            var localAddress = await GetLocalApiUrl(cancellationToken).ConfigureAwait(false);

            string wanAddress;

            if (string.IsNullOrEmpty(ServerConfigurationManager.Configuration.WanDdns))
            {
                wanAddress = await GetWanApiUrlFromExternal(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                wanAddress = GetWanApiUrl(ServerConfigurationManager.Configuration.WanDdns);
            }

            return new SystemInfo
            {
                HasPendingRestart = HasPendingRestart,
                IsShuttingDown = IsShuttingDown,
                Version = ApplicationVersion,
                WebSocketPortNumber = HttpPort,
                CompletedInstallations = InstallationManager.CompletedInstallations.ToArray(),
                Id = SystemId,
                ProgramDataPath = ApplicationPaths.ProgramDataPath,
                WebPath = ApplicationPaths.WebPath,
                LogPath = ApplicationPaths.LogDirectoryPath,
                ItemsByNamePath = ApplicationPaths.InternalMetadataPath,
                InternalMetadataPath = ApplicationPaths.InternalMetadataPath,
                CachePath = ApplicationPaths.CachePath,
                HttpServerPortNumber = HttpPort,
                SupportsHttps = SupportsHttps,
                HttpsPortNumber = HttpsPort,
                OperatingSystem = OperatingSystem.Id.ToString(),
                OperatingSystemDisplayName = OperatingSystem.Name,
                CanSelfRestart = CanSelfRestart,
                CanLaunchWebBrowser = CanLaunchWebBrowser,
                WanAddress = wanAddress,
                HasUpdateAvailable = HasUpdateAvailable,
                TranscodingTempPath = ApplicationPaths.TranscodingTempPath,
                ServerName = FriendlyName,
                LocalAddress = localAddress,
                SupportsLibraryMonitor = true,
                EncoderLocation = MediaEncoder.EncoderLocation,
                SystemArchitecture = RuntimeInformation.OSArchitecture,
                SystemUpdateLevel = SystemUpdateLevel,
                PackageName = StartupOptions.PackageName
            };
        }

        public WakeOnLanInfo[] GetWakeOnLanInfo()
        {
            return NetworkManager.GetMacAddresses()
                .Select(i => new WakeOnLanInfo
                {
                    MacAddress = i
                })
                .ToArray();
        }

        public async Task<PublicSystemInfo> GetPublicSystemInfo(CancellationToken cancellationToken)
        {
            var localAddress = await GetLocalApiUrl(cancellationToken).ConfigureAwait(false);

            string wanAddress;

            if (string.IsNullOrEmpty(ServerConfigurationManager.Configuration.WanDdns))
            {
                wanAddress = await GetWanApiUrlFromExternal(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                wanAddress = GetWanApiUrl(ServerConfigurationManager.Configuration.WanDdns);
            }
            return new PublicSystemInfo
            {
                Version = ApplicationVersion,
                ProductName = ApplicationProductName,
                Id = SystemId,
                OperatingSystem = OperatingSystem.Id.ToString(),
                WanAddress = wanAddress,
                ServerName = FriendlyName,
                LocalAddress = localAddress
            };
        }

        public bool EnableHttps => SupportsHttps && ServerConfigurationManager.Configuration.EnableHttps;

        public bool SupportsHttps => Certificate != null || ServerConfigurationManager.Configuration.IsBehindProxy;

        public async Task<string> GetLocalApiUrl(CancellationToken cancellationToken)
        {
            try
            {
                // Return the first matched address, if found, or the first known local address
                var addresses = await GetLocalIpAddressesInternal(false, 1, cancellationToken).ConfigureAwait(false);

                foreach (var address in addresses)
                {
                    return GetLocalApiUrl(address);
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error getting local Ip address information");
            }

            return null;
        }

        public async Task<string> GetWanApiUrlFromExternal(CancellationToken cancellationToken)
        {
            const string Url = "http://ipv4.icanhazip.com";
            try
            {
                using (var response = await HttpClient.Get(new HttpRequestOptions
                {
                    Url = Url,
                    LogErrorResponseBody = false,
                    LogErrors = false,
                    LogRequest = false,
                    BufferContent = false,
                    CancellationToken = cancellationToken
                }).ConfigureAwait(false))
                {
                    string res = await response.ReadToEndAsync().ConfigureAwait(false);
                    return GetWanApiUrl(res.Trim());
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error getting WAN Ip address information");
            }

            return null;
        }

        public string GetLocalApiUrl(IpAddressInfo ipAddress)
        {
            if (ipAddress.AddressFamily == IpAddressFamily.InterNetworkV6)
            {
                return GetLocalApiUrl("[" + ipAddress.Address + "]");
            }

            return GetLocalApiUrl(ipAddress.Address);
        }

        public string GetLocalApiUrl(string host)
        {
            if (EnableHttps)
            {
                return string.Format("https://{0}:{1}",
                    host,
                    HttpsPort.ToString(CultureInfo.InvariantCulture));
            }
            return string.Format("http://{0}:{1}",
                    host,
                    HttpPort.ToString(CultureInfo.InvariantCulture));
        }

        public string GetWanApiUrl(IpAddressInfo ipAddress)
        {
            if (ipAddress.AddressFamily == IpAddressFamily.InterNetworkV6)
            {
                return GetWanApiUrl("[" + ipAddress.Address + "]");
            }

            return GetWanApiUrl(ipAddress.Address);
        }

        public string GetWanApiUrl(string host)
        {
            if (EnableHttps)
            {
                return string.Format("https://{0}:{1}",
                    host,
                    ServerConfigurationManager.Configuration.PublicHttpsPort.ToString(CultureInfo.InvariantCulture));
            }
            return string.Format("http://{0}:{1}",
                    host,
                    ServerConfigurationManager.Configuration.PublicPort.ToString(CultureInfo.InvariantCulture));
        }

        public Task<List<IpAddressInfo>> GetLocalIpAddresses(CancellationToken cancellationToken)
        {
            return GetLocalIpAddressesInternal(true, 0, cancellationToken);
        }

        private async Task<List<IpAddressInfo>> GetLocalIpAddressesInternal(bool allowLoopback, int limit, CancellationToken cancellationToken)
        {
            var addresses = ServerConfigurationManager
                .Configuration
                .LocalNetworkAddresses
                .Select(NormalizeConfiguredLocalAddress)
                .Where(i => i != null)
                .ToList();

            if (addresses.Count == 0)
            {
                addresses.AddRange(NetworkManager.GetLocalIpAddresses(ServerConfigurationManager.Configuration.IgnoreVirtualInterfaces));
            }

            var resultList = new List<IpAddressInfo>();

            foreach (var address in addresses)
            {
                if (!allowLoopback)
                {
                    if (address.Equals(IpAddressInfo.Loopback) || address.Equals(IpAddressInfo.IPv6Loopback))
                    {
                        continue;
                    }
                }

                var valid = await IsIpAddressValidAsync(address, cancellationToken).ConfigureAwait(false);
                if (valid)
                {
                    resultList.Add(address);

                    if (limit > 0 && resultList.Count >= limit)
                    {
                        return resultList;
                    }
                }
            }

            return resultList;
        }

        private IpAddressInfo NormalizeConfiguredLocalAddress(string address)
        {
            var index = address.Trim('/').IndexOf('/');

            if (index != -1)
            {
                address = address.Substring(index + 1);
            }

            if (NetworkManager.TryParseIpAddress(address.Trim('/'), out IpAddressInfo result))
            {
                return result;
            }

            return null;
        }

        private readonly ConcurrentDictionary<string, bool> _validAddressResults = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private async Task<bool> IsIpAddressValidAsync(IpAddressInfo address, CancellationToken cancellationToken)
        {
            if (address.Equals(IpAddressInfo.Loopback) ||
                address.Equals(IpAddressInfo.IPv6Loopback))
            {
                return true;
            }

            var apiUrl = GetLocalApiUrl(address);
            apiUrl += "/system/ping";

            if (_validAddressResults.TryGetValue(apiUrl, out var cachedResult))
            {
                return cachedResult;
            }

#if DEBUG
            const bool LogPing = true;
#else
            const bool LogPing = false;
#endif

            try
            {
                using (var response = await HttpClient.SendAsync(
                    new HttpRequestOptions
                    {
                        Url = apiUrl,
                        LogErrorResponseBody = false,
                        LogErrors = LogPing,
                        LogRequest = LogPing,
                        BufferContent = false,
                        CancellationToken = cancellationToken
                    }, HttpMethod.Post).ConfigureAwait(false))
                {
                    using (var reader = new StreamReader(response.Content))
                    {
                        var result = await reader.ReadToEndAsync().ConfigureAwait(false);
                        var valid = string.Equals(Name, result, StringComparison.OrdinalIgnoreCase);

                        _validAddressResults.AddOrUpdate(apiUrl, valid, (k, v) => valid);
                        Logger.LogDebug("Ping test result to {0}. Success: {1}", apiUrl, valid);
                        return valid;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Logger.LogDebug("Ping test result to {0}. Success: {1}", apiUrl, "Cancelled");
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Ping test result to {0}. Success: {1}", apiUrl, false);

                _validAddressResults.AddOrUpdate(apiUrl, false, (k, v) => false);
                return false;
            }
        }

        public string FriendlyName =>
            string.IsNullOrEmpty(ServerConfigurationManager.Configuration.ServerName)
                ? Environment.MachineName
                : ServerConfigurationManager.Configuration.ServerName;

        public int HttpPort { get; private set; }

        public int HttpsPort { get; private set; }

        /// <summary>
        /// Shuts down.
        /// </summary>
        public async Task Shutdown()
        {
            if (IsShuttingDown)
            {
                return;
            }

            IsShuttingDown = true;

            try
            {
                await SessionManager.SendServerShutdownNotification(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error sending server shutdown notification");
            }

            ShutdownInternal();
        }

        protected abstract void ShutdownInternal();

        public event EventHandler HasUpdateAvailableChanged;

        private bool _hasUpdateAvailable;

        public bool HasUpdateAvailable
        {
            get => _hasUpdateAvailable;
            set
            {
                var fireEvent = value && !_hasUpdateAvailable;

                _hasUpdateAvailable = value;

                if (fireEvent)
                {
                    HasUpdateAvailableChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// Removes the plugin.
        /// </summary>
        /// <param name="plugin">The plugin.</param>
        public void RemovePlugin(IPlugin plugin)
        {
            var list = Plugins.ToList();
            list.Remove(plugin);
            Plugins = list.ToArray();
        }

        /// <summary>
        /// This returns localhost in the case of no external dns, and the hostname if the
        /// dns is prefixed with a valid Uri prefix.
        /// </summary>
        /// <param name="externalDns">The external dns prefix to get the hostname of.</param>
        /// <returns>The hostname in <paramref name="externalDns"/></returns>
        private static string GetHostnameFromExternalDns(string externalDns)
        {
            if (string.IsNullOrEmpty(externalDns))
            {
                return "localhost";
            }

            try
            {
                return new Uri(externalDns).Host;
            }
            catch
            {
                return externalDns;
            }
        }

        public virtual void LaunchUrl(string url)
        {
            if (!CanLaunchWebBrowser)
            {
                throw new NotSupportedException();
            }

            var process = ProcessFactory.Create(new ProcessOptions
            {
                FileName = url,
                EnableRaisingEvents = true,
                UseShellExecute = true,
                ErrorDialog = false
            });

            process.Exited += ProcessExited;

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error launching url: {url}", url);
                throw;
            }
        }

        private static void ProcessExited(object sender, EventArgs e)
        {
            ((IProcess)sender).Dispose();
        }

        public virtual void EnableLoopback(string appName)
        {
        }

        private bool _disposed = false;

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool dispose)
        {
            if (_disposed)
            {
                return;
            }

            if (dispose)
            {
                var type = GetType();

                Logger.LogInformation("Disposing {Type}", type.Name);

                var parts = _disposableParts.Distinct().Where(i => i.GetType() != type).ToList();
                _disposableParts.Clear();

                foreach (var part in parts)
                {
                    Logger.LogInformation("Disposing {Type}", part.GetType().Name);

                    try
                    {
                        part.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Error disposing {Type}", part.GetType().Name);
                    }
                }

                UserRepository.Dispose();
            }

            UserRepository = null;

            _disposed = true;
        }
    }

    internal class CertificateInfo
    {
        public string Path { get; set; }
        public string Password { get; set; }
    }
}
