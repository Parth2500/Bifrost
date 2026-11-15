using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Core.Api;
using Bifrost.Server.Api;
using Bifrost.Server.People;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Json;

namespace Bifrost.Server.Extensions;

/// <summary>
/// What an extension can ask Bifröst (design §8.6), on a second private socket per extension
/// (<c>bifrost.sock</c>, 0600 in the extension's 0700 folder). No tokens: only Bifröst's own account
/// can reach the socket, and the socket a request arrived on says which extension asked.
/// </summary>
public sealed class ExtensionApiHost : IAsyncDisposable
{
    private const string ConnectionKey = "bifrost.extension";
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private readonly IReadOnlyList<ExtensionSupervisor> _extensions;
    private readonly PeopleService _people;
    private readonly Action<IEndpointRouteBuilder>? _extraRoutes;
    private WebApplication? _app;

    /// <param name="extraRoutes">More routes, from later parts of Bifröst (questions) or from tests.</param>
    public ExtensionApiHost(IReadOnlyList<ExtensionSupervisor> extensions, PeopleService people, Action<IEndpointRouteBuilder>? extraRoutes = null)
    {
        _extensions = extensions;
        _people = people;
        _extraRoutes = extraRoutes;
    }

    public async Task StartAsync()
    {
        if (_extensions.Count == 0) return;
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddRoutingCore();
        builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        builder.Services.Configure<JsonOptions>(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        });
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 64 * 1024;
            foreach (var extension in _extensions)
            {
                var folder = Path.GetDirectoryName(extension.ApiSocketPath)!;
                Directory.CreateDirectory(folder, OwnerOnly);
                if (File.GetUnixFileMode(folder) != OwnerOnly) File.SetUnixFileMode(folder, OwnerOnly);
                if (File.Exists(extension.ApiSocketPath)) File.Delete(extension.ApiSocketPath);
                var name = extension.Name;
                kestrel.ListenUnixSocket(extension.ApiSocketPath, listen => listen.Use(next => connection =>
                {
                    connection.Items[ConnectionKey] = name;
                    return next(connection);
                }));
            }
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            // Each listener tags its connections with its extension's name (below), so the socket a
            // request came in on says who asked. There is no other way in.
            if (context.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(ConnectionKey, out var tag) != true || tag is not string name)
            {
                await Problem.WriteAsync(context, 403, "unknown_extension", "This socket belongs to no extension.");
                return;
            }
            context.SetExtension(name);
            await next();
        });

        app.MapGet("/v1/people/{id:guid}", async (Guid id) =>
            await _people.GetPersonAsync(id) is { } p
                ? Results.Ok(new ExtensionPersonDto(p.Id, p.DisplayName, p.Role))
                : Problem.Create(404, "person_not_found", "No such person."));
        app.MapGet("/v1/devices/{id:guid}", async (Guid id) =>
            await _people.GetDeviceAsync(id) is { } d
                ? Results.Ok(new ExtensionDeviceDto(d.Id, d.PersonId, d.Name, d.Kind))
                : Problem.Create(404, "device_not_found", "No such device."));
        _extraRoutes?.Invoke(app);
        app.MapFallback(() => Problem.Create(404, "not_found", "No such endpoint."));

        await app.StartAsync();
        _app = app;
        foreach (var extension in _extensions) File.SetUnixFileMode(extension.ApiSocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is { } app)
        {
            _app = null;
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

public static class HttpContextExtension
{
    private const string Key = "bifrost.extension";

    internal static void SetExtension(this HttpContext context, string name) => context.Items[Key] = name;

    /// <summary>The extension whose API socket this request came in on.</summary>
    public static string Extension(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (string)context.Items[Key]!;
    }
}
