using Azure.Data.Tables;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StatusMonitor.Core.Services;
using StatusMonitor.Core.Storage;
using StatusMonitor.Functions.Auth;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults(worker =>
    {
        worker.UseWhen<TenantAuthenticationMiddleware>(context =>
            context.FunctionDefinition.InputBindings.Values.Any(b => b.Type == "httpTrigger"));
    })
    .ConfigureServices((builder, services) =>
    {
        var configuration = builder.Configuration;
        var storageConnection = configuration["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("AzureWebJobsStorage is not configured.");

        services.AddSingleton(new TableServiceClient(storageConnection));
        services.AddSingleton(new QueueServiceClient(storageConnection));

        services.AddSingleton<ITenantRepository, TableTenantRepository>();
        services.AddSingleton<IMonitoredUrlRepository, TableMonitoredUrlRepository>();
        services.AddSingleton<IUrlStatusRepository, TableUrlStatusRepository>();
        services.AddSingleton<IQueuePublisher, StorageQueuePublisher>();

        services.AddHttpClient(UrlPollerService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("StatusMonitor/1.0 (+https://github.com/statusmonitor)");
        });

        services.AddSingleton<UrlPollerService>();
        services.AddSingleton<UrlIngestionService>();

        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.SectionName));
        services.AddSingleton<TenantAuthenticator>();

        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
    })
    .Build();

host.Run();
