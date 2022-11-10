using System.Reflection;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Asp.Versioning.Conventions;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Podcast.API.Routes;
using Podcast.Infrastructure.Data;
using Podcast.Infrastructure.Http;
using Podcast.Infrastructure.Http.Feeds;
using Swashbuckle.AspNetCore.SwaggerGen;

var builder = WebApplication.CreateBuilder(args);

// Database and storage related-services
var dbConnectionString = builder.Configuration.GetConnectionString("PodcastDb") ?? throw new InvalidOperationException("Missing connection string configuration");
builder.Services.AddSqlServer<PodcastDbContext>(dbConnectionString);

var queueConnectionString = builder.Configuration.GetConnectionString("FeedQueue") ?? throw new InvalidOperationException("Missing feed queue configuration");

builder.Services.AddSingleton(new QueueClient(queueConnectionString, "feed-queue"));
builder.Services.AddHttpClient<IFeedClient, FeedClient>();
builder.Services.AddTransient<JitterHandler>();
builder.Services.AddHttpClient<ShowClient>().AddHttpMessageHandler<JitterHandler>();

// Authentication and authorization-related services
builder.Services.AddAuthentication().AddJwtBearer();
builder.Services.AddAuthorization();

// OpenAPI and versioning-related services
builder.Services.AddSwaggerGen();
builder.Services.Configure<SwaggerGeneratorOptions>(opts =>
{
    opts.InferSecuritySchemes = true;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(2, 0);
    options.ReportApiVersions = true;
    options.AssumeDefaultVersionWhenUnspecified = true;
});

builder.Services.AddOutputCache();

builder.Services.AddCors(setup =>
{
    setup.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// Rate-limiting and output caching-related services
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("feeds", options =>
{
    options.PermitLimit = 5;
    options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    options.QueueLimit = 0;
    options.Window = TimeSpan.FromSeconds(2);
    options.AutoReplenishment = false;
}));

var serviceName = builder.Environment.ApplicationName;
var serviceVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
var serviceResource =
        ResourceBuilder
         .CreateDefault()
         .AddService(serviceName: serviceName, serviceVersion: serviceVersion);

//var azureMonitorConnectionString = builder.Configuration.GetConnectionString("AzureMonitor") ?? throw new InvalidOperationException("Missing azure monitor configuration");

builder.Services.AddOpenTelemetryTracing(tracing =>
    tracing.SetResourceBuilder(serviceResource)
    .AddJaegerExporter()
    .AddHttpClientInstrumentation()
    .AddAspNetCoreInstrumentation()
    .AddEntityFrameworkCoreInstrumentation()
);

builder.Services.AddOpenTelemetryMetrics(metrics =>
{
    metrics
    .SetResourceBuilder(serviceResource)
    .AddPrometheusExporter()
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddRuntimeInstrumentation()
    .AddProcessInstrumentation()
    .AddHttpClientInstrumentation()
    .AddEventCountersInstrumentation(ec =>
    {
        ec.AddEventSources("Microsoft.AspNetCore.Hosting");
    });
});

builder.Logging.AddOpenTelemetry(logging =>
{
    logging
     .AttachLogsToActivityEvent()
     .SetResourceBuilder(serviceResource);
});

var app = builder.Build();

await EnsureDbAsync(app.Services);

// Register required middlewares
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "NetPodcast Api v1");
});

app.UseCors();
app.UseRateLimiter();
app.UseOutputCache();

app.MapPrometheusScrapingEndpoint();
app.MapGet("/version", () => serviceVersion);

var versionSet = app.NewApiVersionSet()
                    .HasApiVersion(1.0)
                    .HasApiVersion(2.0)
                    .ReportApiVersions()
                    .Build();

var shows = app.MapGroup("/shows");
var categories = app.MapGroup("/categories");
var episodes = app.MapGroup("/episodes");
var feeds = app.MapGroup("/feeds");

shows
    .MapShowsApi()
    .WithApiVersionSet(versionSet)
    .MapToApiVersion(1.0)
    .MapToApiVersion(2.0);

categories
    .MapCategoriesApi()
    .WithApiVersionSet(versionSet)
    .MapToApiVersion(1.0);

episodes
    .MapEpisodesApi()
    .WithApiVersionSet(versionSet)
    .MapToApiVersion(1.0);

feeds
    .MapFeedsApi()
    .WithApiVersionSet(versionSet)
    .MapToApiVersion(2.0)
    .RequireRateLimiting("feeds");

app.Run();

static async Task EnsureDbAsync(IServiceProvider sp)
{
    await using var db = sp.CreateScope().ServiceProvider.GetRequiredService<PodcastDbContext>();
    await db.Database.MigrateAsync();
}
