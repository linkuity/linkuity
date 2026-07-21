using System.Text.Json;
using System.Text.Json.Serialization;
using Linkuity.Api.Endpoints;
using Linkuity.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
});

builder.Services.AddRuntimeInfrastructure(builder.Configuration);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok());
app.MapProjectEndpoints();
app.MapRunEndpoints();

app.Run();

public partial class Program;
