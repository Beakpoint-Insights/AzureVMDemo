// Beakpoint Insights, Inc. licenses this file to you under the MIT license.

using System.Reflection;
using Microsoft.Extensions.Caching.Memory;
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
        var basePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
        var projectPath = Path.GetFullPath(Path.Combine(basePath, Path.Combine("..", "..", "..")));
        
        // Configuration setup
        builder.Configuration
            .SetBasePath(projectPath)
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

        // Add OpenTelemetry
        //builder.Services.AddOpenTelemetry()
        //    .WithTracing(tracingBuilder => tracingBuilder
        //        .SetResourceBuilder(ResourceBuilder.CreateDefault()
        //            .AddAttributes(attributes))
        //        .AddAspNetCoreInstrumentation(options => {
        //            options.EnrichWithHttpRequest = (activity, request) => {
        //                foreach(var attr in attributes) {
        //                    activity.SetTag(attr.Key, attr.Value);
        //                }
        //            };
        //        })
        //        .AddHttpClientInstrumentation(options =>{
        //            options.EnrichWithHttpRequestMessage = (activity, request) => {
        //                foreach(var attr in attributes) {
        //                    activity.SetTag(attr.Key, attr.Value);
        //                }
        //            };
        //        })
        //        .AddAWSInstrumentation()
        //        .AddOtlpExporter(opts => {
        //            opts.Protocol = OtlpExportProtocol.HttpProtobuf;
        //            opts.ExportProcessorType = ExportProcessorType.Simple;
        //            opts.Endpoint = new Uri(url);
        //            opts.Headers = $"x-bkpt-key={apiKey}";
        //        }));
        
        var app = builder.Build();

        app.MapGet("/", () => {
            var metadata = GetVmMetadataAsync(memoryCache, httpClient).GetAwaiter().GetResult();
            return metadata;
        });

        app.Run();
    }

    public static async Task<Dictionary<string, object>?> GetVmMetadataAsync(IMemoryCache memoryCache, HttpClient httpClient)
    {
        if (memoryCache.TryGetValue("vm_metadata", out Dictionary<string, object>? cachedMetadata) && cachedMetadata != null)
        {
            return cachedMetadata;
        }

        httpClient.DefaultRequestHeaders.Add("Metadata", "true");
        var response = await httpClient.GetAsync("http://169.254.169.254/metadata/instance?api-version=2020-09-01");
        response.EnsureSuccessStatusCode();

        var metadata = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        memoryCache.Set("vm_metadata", metadata, TimeSpan.FromMinutes(5)); // Cache for 5 minutes
        return metadata;
    }
}
