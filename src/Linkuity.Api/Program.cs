using System.Text.Json;
using System.Text.Json.Serialization;
using Linkuity.Api.Endpoints;
using Linkuity.Api.Infrastructure;
using Linkuity.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
});

builder.Services.AddRuntimeInfrastructure(builder.Configuration);
builder.Services.AddScoped<JobService>();
builder.Services.AddScoped<CsvNormalizationService>();
builder.Services.AddScoped<Neo4jExportService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok());
app.MapJobEndpoints();
app.MapProjectEndpoints();

app.Run();

public partial class Program;
