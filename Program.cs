// Beakpoint Insights, Inc. licenses this file to you under the MIT license.

using System.Reflection;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AzureVMDemo;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // Create a path to configuration files
        //var basePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
        //var projectPath = Path.GetFullPath(Path.Combine(basePath, Path.Combine("..", "..", "..")));

        //Console.WriteLine($"Base path: {basePath}");
        //Console.WriteLine($"Project path: {projectPath}");
        //Console.WriteLine($"AppContext base directory: {System.AppContext.BaseDirectory}");
        
        // Configuration setup
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
            .AddEnvironmentVariables();

        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient();

        // Resolve IMemoryCache from the service provider
        #pragma warning disable ASP0000
        ServiceProvider serviceProvider = builder.Services.BuildServiceProvider();
        IMemoryCache memoryCache = serviceProvider.GetRequiredService<IMemoryCache>();
        HttpClient httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();

        var attributes = GetVmMetadataAsync(memoryCache, httpClient).GetAwaiter().GetResult();

        // Get Telemetry receiver address and API key
        var apiKey = builder.Configuration["Beakpoint:Otel:ApiKey"];
        var url = builder.Configuration["Beakpoint:Otel:Url"] ?? throw new InvalidOperationException("Beakpoint Otel Url is not configured");

        // Add OpenTelemetry
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracingBuilder => tracingBuilder
                .SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddAttributes(attributes))
                .AddAspNetCoreInstrumentation(options => {
                    options.EnrichWithHttpRequest = (activity, request) => {
                        foreach(var attr in attributes) {
                            activity.SetTag(attr.Key, attr.Value);
                        }
                    };
                })
                .AddHttpClientInstrumentation(options =>{
                    options.EnrichWithHttpRequestMessage = (activity, request) => {
                        foreach(var attr in attributes) {
                            activity.SetTag(attr.Key, attr.Value);
                        }
                    };
                })
                .AddOtlpExporter(opts => {
                    opts.Protocol = OtlpExportProtocol.HttpProtobuf;
                    opts.ExportProcessorType = ExportProcessorType.Simple;
                    opts.Endpoint = new Uri(url);
                    opts.Headers = $"x-bkpt-key={apiKey}";
                }));

        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.Listen(System.Net.IPAddress.Parse("0.0.0.0"), 5227);
        });
        
        var app = builder.Build();

        app.MapGet("/", () => {
            var attributes = GetVmMetadataAsync(memoryCache, httpClient).GetAwaiter().GetResult();
            return attributes;
        });

        app.Run();
    }

    public static async Task<Dictionary<string, object>> GetVmMetadataAsync(IMemoryCache memoryCache, HttpClient httpClient)
    {
        string metadata;
        if (memoryCache.TryGetValue("vm_metadata", out string? cachedMetadata) && cachedMetadata != null)
        {
            metadata = cachedMetadata;
        }
        else {
            httpClient.DefaultRequestHeaders.Add("Metadata", "true");
            var IMDSResponse = await httpClient.GetAsync("http://169.254.169.254/metadata/instance?api-version=2021-02-01");
            IMDSResponse.EnsureSuccessStatusCode();

            metadata = await IMDSResponse.Content.ReadAsStringAsync();

            memoryCache.Set("vm_metadata", metadata);
        }

        var attributes = new Dictionary<string, object>();
        var imds = JsonSerializer.Deserialize<JsonElement>(metadata);
        var compute = imds.GetProperty("compute");
        
        string? location = compute.GetProperty("location").GetString(); // ArmRegionName
        string? size = compute.GetProperty("vmSize").GetString(); // ArmSkuName
        string? os = compute.GetProperty("osType").GetString(); // ProductName (partially)
        string? priority = compute.GetProperty("priority").GetString(); // MeterName (partially)

        attributes["azure.vm.service_name"] = "Virtual Machines";
        if (location != null) {
            attributes["azure.vm.location"] = location;
        }
        if (size != null) {
            attributes["azure.vm.size"] = size;
        }
        if (os != null) {
            attributes["azure.vm.os"] = os;
        }
        if (priority != null) {
            attributes["azure.vm.priority"] = priority;
        }

        return attributes;
    }
}
