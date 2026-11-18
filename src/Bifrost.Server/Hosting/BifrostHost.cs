using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Server.Api;
using Bifrost.Server.Extensions;
using Bifrost.Server.People;
using Bifrost.Server.Questions;
using Bifrost.Server.Security;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Json;
using Scalar.AspNetCore;

namespace Bifrost.Server.Hosting;

/// <summary>
/// The loopback API (design §6.1, §6.4): HTTP on 127.0.0.1 and ::1 only, a bearer device token on every
/// route but server info, a <c>Host</c> allowlist, no CORS, and no cookies except the host screen's session (ADR 003). The host admin has a second
/// way in, the admin socket (design §5): there the kernel says which account is calling, and only the
/// recorded admin account is let through, as the "This computer" device.
/// </summary>
public sealed partial class BifrostHost : IAsyncDisposable
{
    private const int PortAttempts = 20;
    private const string FallbackKey = "bifrost.fallback";
    private const string AdminPeerKey = "bifrost.admin-peer";
    private const UnixFileMode AdminFolderMode = (UnixFileMode)0b111_101_101; // 0755: other accounts must reach the socket
    private const UnixFileMode AdminSocketMode = (UnixFileMode)0b110_110_110; // 0666: connecting needs write; the peer check decides
    private static readonly string[] AllowedHosts = ["localhost", "127.0.0.1", "[::1]"];

    private readonly BifrostHostOptions _options;
    private readonly PeopleService _people;
    private readonly ILoggerProvider? _extraLogs;
    private readonly ExtensionManager? _extensions;
    private readonly QuestionService? _questions;
    private readonly ExtensionInstaller? _installer;
    private readonly ExtensionUpdates? _updates;
    private readonly PairingService _pairing;
    private JoinService? _join;
    private volatile bool _rotationPending;
    private readonly AuthFailureLimiter _limiter;
    private readonly AuthFailureLimiter _joinLimiter;
    private readonly AuthFailureLimiter _everyone;
    private readonly TimeProvider _time;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _app;

    private BifrostHost(BifrostHostOptions options, PeopleService people, TimeProvider time, ILoggerProvider? extraLogs,
        ExtensionManager? extensions, QuestionService? questions, ExtensionInstaller? installer, ExtensionUpdates? updates)
    {
        _updates = updates;
        _installer = installer;
        _extensions = extensions;
        _questions = questions;
        _time = time;
        _hostSessions = new HostSessions(time);
        _pairing = new PairingService(people, time);
        _options = options;
        _people = people;
        _extraLogs = extraLogs;
        _limiter = new AuthFailureLimiter(maxFailures: 20, window: TimeSpan.FromMinutes(1), time);
        // One budget for the whole network: past the per-source table's cap a new source is not counted, and
        // a flood from many addresses must still slow down. Valid tokens are never limited.
        _everyone = new AuthFailureLimiter(maxFailures: 300, window: TimeSpan.FromMinutes(1), time);
        _joinLimiter = new AuthFailureLimiter(maxFailures: 10, window: TimeSpan.FromMinutes(1), time);
    }

    public int Port { get; private set; }

    /// <param name="extraLogs">An additional log sink, used by tests to check that no secret is ever logged (D7).</param>
    /// <param name="extensions">The extensions to report and route to; none when null.</param>
    /// <param name="questions">The questions admins see and answer here; no question routes when null.</param>
    /// <param name="installer">Installs signed extension packages; installing is refused when null.</param>
    /// <param name="updates">Fetches releases from GitHub and offers updates; none when null.</param>
    public static BifrostHost Create(BifrostHostOptions options, PeopleService people, TimeProvider time, ILoggerProvider? extraLogs = null,
        ExtensionManager? extensions = null, QuestionService? questions = null, ExtensionInstaller? installer = null, ExtensionUpdates? updates = null) =>
        new(options, people, time, extraLogs, extensions, questions, installer, updates);

    public async Task StartAsync(CancellationToken ct = default)
    {
        var first = _options.Port;
        var attempts = first == 0 ? 1 : PortAttempts;
        // Both loopback addresses share one port. IPv6 is used only if this machine can really bind
        // ::1 (the OS can claim support while it is disabled by sysctl).
        var withIPv6 = first != 0 && IPv6LoopbackUsable.Value;
        Exception? last = null;

        for (var i = 0; i < attempts; i++)
        {
            var app = Build(first == 0 ? 0 : first + i, withIPv6);
            try
            {
                await app.StartAsync(ct);
                if (_options.Admin is { } admin) File.SetUnixFileMode(admin.Path, AdminSocketMode);
                _app = app;
                Port = new Uri(app.Urls.First()).Port;
                return;
            }
            catch (Exception e) when (IsAddressInUse(e))
            {
                last = e; // only a busy port moves on to the next one
                await app.DisposeAsync();
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        throw new IOException($"Could not listen on loopback ports {first}–{first + attempts - 1}: they are all in use.", last);
    }

    private static readonly Lazy<bool> IPv6LoopbackUsable = new(() =>
    {
        if (!Socket.OSSupportsIPv6) return false;
        try
        {
            using var probe = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    });

    private static bool IsAddressInUse(Exception e)
    {
        for (Exception? inner = e; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }) return true;
            if (inner.GetType().Name == "AddressInUseException") return true;
        }
        return false;
    }

    public Task WaitForShutdownAsync(CancellationToken ct = default) => _shutdown.Task.WaitAsync(ct);

    public async Task StopAsync()
    {
        _shutdown.TrySetResult();
        if (_app is { } app)
        {
            _app = null;
            // One slow or stuck client must not hold up the stop: systemd sends SIGKILL soon after SIGTERM.
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await app.StopAsync(grace.Token); }
            catch (OperationCanceledException) { }
            await app.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private WebApplication Build(int port, bool withIPv6)
    {
        // An empty builder reads no appsettings.json and no environment variables, so nothing outside
        // this process can add a listener (for example Kestrel__Endpoints__*) or widen the bind address.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Logging.AddConsole();
        if (_extraLogs is not null) builder.Logging.AddProvider(_extraLogs);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None); // busy-port retries are normal
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(3));
        builder.Services.AddRoutingCore();
        builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true); // so bad bodies become problem+json
        builder.Services.AddOpenApi();
        builder.Services.AddHttpForwarder();
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 1024 * 1024;
            kestrel.Listen(IPAddress.Loopback, port);
            if (withIPv6) kestrel.Listen(IPAddress.IPv6Loopback, port);
            if (_options.Lan is { } lan)
            {
                kestrel.Listen(IPAddress.IPv6Any, lan.Port, listen => listen.UseHttps(lan.Certificate)); // dual-stack
                if (lan.FallbackPort is { } plain)
                {
                    kestrel.Listen(IPAddress.IPv6Any, plain, listen => listen.Use(next => connection =>
                    {
                        connection.Items[FallbackKey] = true; // tagged per connection: this listener is plain HTTP and view-only
                        return next(connection);
                    }));
                }
            }
            if (_options.Admin is { } admin)
            {
                var folder = Path.GetDirectoryName(admin.Path)!;
                Directory.CreateDirectory(folder);
                if (File.GetUnixFileMode(folder) != AdminFolderMode) File.SetUnixFileMode(folder, AdminFolderMode); // whatever the umask said
                if (File.Exists(admin.Path)) File.Delete(admin.Path); // left by a crash; the instance lock says nobody is using it
                kestrel.ListenUnixSocket(admin.Path, listen => listen.Use(next => connection =>
                {
                    // Asked once per connection, from the kernel. A missing answer is kept as "nobody".
                    connection.Items[AdminPeerKey] = new AdminPeer(PeerCredentials.Of(connection.Features.Get<IConnectionSocketFeature>()?.Socket)?.Uid);
                    return next(connection);
                }));
            }
        });
        builder.Services.Configure<JsonOptions>(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        });

        var app = builder.Build();
        app.Use(ReportFailuresAsProblems);
        app.Use(RejectForeignHosts);
        app.Use(Authenticate);

        app.MapOpenApi();
        app.MapScalarApiReference(scalar => scalar.DisableDefaultFonts()); // the default fonts come from Scalar's CDN

        var api = app.MapGroup("/api/v1");
        ServerEndpoints.Map(api, () => _shutdown.TrySetResult());
        MeEndpoints.Map(api);
        new PeopleEndpoints(_people).Map(api);
        new DeviceEndpoints(_people).Map(api);
        _join = _options.Lan is { } lanJoin ? new JoinService(_people, System.Security.Cryptography.SHA256.HashData(lanJoin.Certificate.PublicKey.ExportSubjectPublicKeyInfo()), _time) : null;
        new JoinEndpoints(_join, _people, _joinLimiter).Map(api);
        if (_options.RotateHomeKey is { } rotate)
        {
            // Host only: the admin socket, never the network, and never a token.
            api.MapPost("/identity/rotate", async (HttpContext c) =>
            {
                if (!c.Principal().IsLocal) return Problem.Create(403, "host_only", "Only this computer's admin can rotate the home key.");
                // Stop all pairing first, so no new token is issued from here on; sign everyone out; rotate;
                // then sign out again for a pairing that was already in flight when the flag went up.
                // A failure in between leaves the key unchanged and the devices signed out, never the reverse.
                _rotationPending = true;
                var revoked = await _people.RevokeAllRemoteAsync(c.Principal());
                var fingerprint = rotate();
                revoked += await _people.RevokeAllRemoteAsync(c.Principal());
                return Results.Ok(new { fingerprint, revokedDevices = revoked });
            });
        }
        new PairingEndpoints(_pairing, _people, _options.Lan is { } l ? HomeKey.FingerprintOf(l.Certificate) : null, _options.Lan?.Port, _options.Lan?.Name ?? Environment.MachineName).Map(api);
        MapHostScreen(app, api);
        ExtensionEndpoints.Map(api, _extensions);
        if (_questions is not null) QuestionEndpoints.MapForAdmins(api, _questions);
        new ExtensionProxy(_extensions, app.Services.GetRequiredService<Yarp.ReverseProxy.Forwarder.IHttpForwarder>()).Map(app);
        // The page holds no data of its own, so it needs no token; what it shows comes from the API with one.
        app.MapGet(HomePage.Path, () => Results.Content(HomePage.Html, "text/html; charset=utf-8"));
        app.MapFallback("/api/{**rest}", () => Problem.Create(404, "not_found", "No such endpoint."));
        return app;
    }

    private static async Task ReportFailuresAsProblems(HttpContext context, Func<Task> next)
    {
        try
        {
            await next();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away or the server is stopping: nothing to report.
        }
        catch (BadHttpRequestException e) when (!context.Response.HasStarted)
        {
            var code = e.StatusCode == 413 ? "payload_too_large" : "invalid_request";
            await Problem.WriteAsync(context, e.StatusCode, code, "The request could not be read.");
        }
        catch (Exception e) when (!context.Response.HasStarted)
        {
            // The path only: the query string could carry something a client should not have put there.
            LogUnhandled(Logger(context), e, context.Request.Method, context.Request.Path);
            await Problem.WriteAsync(context, 500, "internal_error", "Something went wrong.");
        }
    }

    private static async Task RejectForeignHosts(HttpContext context, Func<Task> next)
    {
        if (!AllowedHosts.Contains(context.Request.Host.Host, StringComparer.OrdinalIgnoreCase) && !IsOwnLanName(context))
        {
            await Problem.WriteAsync(context, 400, "host_not_allowed", "This server only answers to its own names.");
            return;
        }
        await next();
    }

    /// <summary>The home's own LAN names and addresses (design §6.4); any other Host is a rebinding attempt.</summary>
    private static bool IsOwnLanName(HttpContext context)
    {
        var host = context.Request.Host.Host;
        if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || host.Equals(Environment.MachineName + ".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address)) return false;
        return HostAddresses.IsOwn(address, context.Connection.LocalIpAddress);
    }

    /// <summary>
    /// When wrong guesses across the whole network pass the budget, each further one waits. Nothing is refused
    /// on this account, so a flood from many addresses cannot lock the owner out of pairing.
    /// </summary>
    private async Task SlowDownIfFlooded(CancellationToken ct)
    {
        if (!_everyone.IsBlocked("*")) return;
        try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
        catch (OperationCanceledException) { }
    }

    private async Task Authenticate(HttpContext context, Func<Task> next)
    {
        if (context.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(AdminPeerKey, out var tag) == true)
        {
            await AuthenticateAdminSocket(context, (tag as AdminPeer)?.Uid, next);
            return;
        }

        // A browser on this machine with a live host session is the admin's own device (ADR 003). Anything else falls through to tokens.
        // Only for the API the host screen calls. Extension pages (/x/) share this origin, so a script in one must never get the
        // admin's session; they stay behind device tokens until they are served from an origin of their own.
        if (IsHostBrowser(context) && HasHostSession(context) && context.Request.Path.StartsWithSegments("/api"))
        {
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !SameOrigin(context))
            {
                await Problem.WriteAsync(context, 403, "cross_site", "This request did not come from the host screen.");
                return;
            }
            if (await _people.LocalPrincipalAsync() is { } local)
            {
                context.SetPrincipal(local);
                await next();
                return;
            }
        }

        var isPublic = HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/api/v1/server";
        var fallback = IsFallback(context);
        if (HttpMethods.IsPost(context.Request.Method)
            && (context.Request.Path == PairingEndpoints.PairPath || context.Request.Path.StartsWithSegments(JoinEndpoints.PublicPrefix)))
        {
            var asker = LimiterKey(context.Connection.RemoteIpAddress);
            if (_rotationPending)
            {
                await Problem.WriteAsync(context, 503, "restart_pending", "The home key was rotated. Pairing resumes when Bifröst restarts.");
                return;
            }
            if (IsFallback(context) && context.Request.Path == PairingEndpoints.PairPath)
            {
                // An invitation secret and an app token must not cross plain HTTP.
                await Problem.WriteAsync(context, 403, "view_only", "This listener is not encrypted: pair through the app.");
                return;
            }
            if (_limiter.IsBlocked(asker))
            {
                await Problem.WriteAsync(context, 429, "too_many_attempts", "Too many failed attempts. Try again later.");
                return;
            }
            await SlowDownIfFlooded(context.RequestAborted); // slower, never refused: a valid secret still gets through
            await next();
            if (context.Response.StatusCode == 401)
            {
                _limiter.RecordFailure(asker); // a wrong secret
                _everyone.RecordFailure("*");
            }
            return;
        }
        var guarded = context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments(ExtensionProxy.Prefix);
        if (isPublic || !guarded)
        {
            await next();
            return;
        }

        var presented = BearerToken(context.Request);
        if (presented is null)
        {
            // No credentials (what a cross-site browser request looks like): refuse, but don't let
            // anyone use these to exhaust the budget that protects the real owner.
            await Unauthorized(context);
            return;
        }

        // A known, unrevoked device token always works: a 256-bit token can't be guessed, so throttling
        // only needs to slow down wrong guesses, and must never lock the owner out.
        if (await _people.AuthenticateAsync(presented) is { } principal)
        {
            // The fallback is plain HTTP: only a browser-fallback token may travel it, and nobody it
            // identifies may change anything, on any listener (design §7.3).
            var viewOnly = principal.Kind == Bifrost.Core.Models.DeviceKind.BrowserFallback;
            if ((fallback && !viewOnly) || (viewOnly && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)))
            {
                await Problem.WriteAsync(context, 403, "view_only", fallback && !viewOnly
                    ? "This listener is not encrypted: pair this device through the app."
                    : "A browser paired through the fallback can only look.");
                return;
            }
            context.SetPrincipal(principal);
            await next();
            return;
        }

        var source = LimiterKey(context.Connection.RemoteIpAddress);
        if (_limiter.IsBlocked(source))
        {
            await Problem.WriteAsync(context, 429, "too_many_attempts", "Too many failed attempts. Try again later.");
            return;
        }
        _limiter.RecordFailure(source);
        _everyone.RecordFailure("*");
        await SlowDownIfFlooded(context.RequestAborted); // a wrong guess in a flood waits; a right token never got here
        LogRefusedToken(Logger(context), source); // D5: the reason goes to the log, the client gets one answer; D7: never the token
        await Unauthorized(context);
    }

    /// <summary>
    /// On the admin socket the calling account is the only credential, for every route. A bearer
    /// token there is ignored: it could only ever lower or confuse who is asking.
    /// </summary>
    private async Task AuthenticateAdminSocket(HttpContext context, uint? peerUid, Func<Task> next)
    {
        if (_options.Admin is not { } admin || peerUid != admin.Uid)
        {
            LogNotTheAdmin(Logger(context), peerUid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown");
            await Problem.WriteAsync(context, 403, "not_the_admin", "Only this computer's admin account can use this socket.");
            return;
        }
        if (await _people.LocalPrincipalAsync() is not { } principal)
        {
            await Problem.WriteAsync(context, 503, "not_ready", "Bifröst has no admin yet.");
            return;
        }
        context.SetPrincipal(principal);
        await next();
    }

    /// <summary>IPv4-mapped addresses count as IPv4, and an IPv6 host gets its whole /64, so one machine cannot mint sources.</summary>
    internal static bool IsFallback(HttpContext context) =>
        context.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(FallbackKey) == true;

    internal static string LimiterKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }

    private sealed record AdminPeer(uint? Uid);

    private static Task Unauthorized(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Problem.WriteAsync(context, 401, "unauthorized", "A valid bearer token is required.");
    }

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : null;
    }

    private static ILogger Logger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger<BifrostHost>();

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled error on {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused an unknown or revoked device token from {Source}")]
    private static partial void LogRefusedToken(ILogger logger, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused account {Uid} on the admin socket: it is not the admin")]
    private static partial void LogNotTheAdmin(ILogger logger, string uid);
}
