using System.Net;
using Bifrost.Server.Api;
using Bifrost.Server.Extensions;
using Bifrost.Server.Security;
using Microsoft.AspNetCore.Connections.Features;

namespace Bifrost.Server.Hosting;

/// <summary>The host screen (ADR 003): a browser on this machine, signed in by a link from the admin socket.</summary>
public sealed partial class BifrostHost
{
    internal const string HostCookie = "bifrost_host";
    private readonly HostSessions _hostSessions;

    private static bool IsAdminSocket(HttpContext context) =>
        context.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(AdminPeerKey) == true;

    /// <summary>
    /// A request that really came from a browser on this machine: loopback at both ends, not the plain-HTTP
    /// fallback, and a loopback <c>Host</c> (a page on another site that points its name at 127.0.0.1 has its own).
    /// </summary>
    private static bool IsHostBrowser(HttpContext context) =>
        !IsFallback(context)
        && context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)
        && context.Connection.LocalIpAddress is { } local && IPAddress.IsLoopback(local)
        && AllowedHosts.Contains(context.Request.Host.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>Browsers always send <c>Origin</c> on a request that changes something; one that is missing or foreign is refused.</summary>
    private static bool SameOrigin(HttpContext context) =>
        Uri.TryCreate(context.Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
        && origin.Scheme == Uri.UriSchemeHttp
        && string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);

    /// <returns>True when the request carries a live host session; the caller has already checked <see cref="IsHostBrowser"/>.</returns>
    private bool HasHostSession(HttpContext context) =>
        context.Request.Cookies.TryGetValue(HostCookie, out var session) && !string.IsNullOrEmpty(session) && _hostSessions.Validate(session);

    private void MapHostScreen(WebApplication app, IEndpointRouteBuilder api)
    {
        // Asked over the admin socket, where the kernel names the account: this is the whole sign-in.
        api.MapPost("/host/login-link", (HttpContext context) =>
        {
            if (!IsAdminSocket(context)) return Problem.Create(403, "host_only", "Only this computer's admin account can ask for a login link, over the admin socket.");
            if (_hostSessions.NewLink(out var expires) is not { } secret) return Problem.Create(429, "too_many_links", "Too many login links are open. Wait two minutes.");
            return Results.Ok(new { url = $"http://127.0.0.1:{Port}/host/login?t={secret}", expiresAt = expires });
        });

        api.MapPost("/host/logout", (HttpContext context) =>
        {
            if (context.Request.Cookies.TryGetValue(HostCookie, out var session)) _hostSessions.End(session);
            context.Response.Cookies.Delete(HostCookie, new CookieOptions { Path = "/" });
            return Results.NoContent();
        });

        api.MapGet("/host/info", (HttpContext context) =>
        {
            if (!context.Principal().IsLocal) return Problem.Create(403, "host_only", "Only this computer's admin can see this.");
            var lan = _options.Lan;
            return Results.Ok(new
            {
                homeName = lan?.Name ?? Environment.MachineName,
                machine = Environment.MachineName,
                version = typeof(BifrostHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                loopbackPort = Port,
                lan = lan is null ? null : new { port = lan.Port, fingerprint = HomeKey.FingerprintOf(lan.Certificate), fallbackPort = lan.FallbackPort },
            });
        });

        // A signed package, as the request body; its signature as base64 in a header. Host only, like all changing of the home.
        api.MapPost("/extensions/install", async (HttpContext context) =>
        {
            if (!context.Principal().IsLocal) return Problem.Create(403, "host_only", "Only this computer's admin can install an extension.");
            if (_installer is null) return Problem.Create(409, "install_unavailable", "This Bifröst cannot install extensions.");
            byte[] signature;
            var header = context.Request.Headers["X-Bifrost-Signature"].ToString();
            if (header.Length == 0) return Problem.Create(400, "invalid_request", "Send the signature as base64 in X-Bifrost-Signature.");
            try { signature = Convert.FromBase64String(header); }
            catch (FormatException) { return Problem.Create(400, "invalid_request", "Send the signature as base64 in X-Bifrost-Signature."); }
            context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 512L * 1024 * 1024;
            try
            {
                var done = await _installer.InstallAsync(context.Request.Body, signature, context.RequestAborted);
                await _people.RecordAsync(context.Principal(), "extension.installed", Guid.Empty, $"{done.Name} {done.Version}");
                var configured = _extensions?.Get(done.Name) is not null;
                // A running extension picks the new version up when it restarts; a new one needs Bifröst to start it.
                return Results.Ok(new { done.Name, done.Version, done.Previous, restart = configured ? "extension" : "bifrost" });
            }
            catch (ExtensionInstaller.InstallRefusedException e)
            {
                await _people.RecordAsync(context.Principal(), "extension.install_refused", Guid.Empty, e.Code);
                return Problem.Create(e.Code == "bad_signature" ? 403 : 422, e.Code, e.Message);
            }
        });

        // Where extensions come from: the owner's GitHub releases (design §8.3). All host only.
        api.MapGet("/extensions/sources", (HttpContext context) =>
            !context.Principal().IsLocal ? Problem.Create(403, "host_only", "Only this computer's admin can see this.")
            : Results.Ok(ExtensionUpdates.Repositories.Select(r => new { name = r.Key, repository = r.Value, installed = _installer?.CurrentVersion(r.Key) })));

        api.MapPost("/extensions/check", async (HttpContext context) =>
        {
            if (!context.Principal().IsLocal) return Problem.Create(403, "host_only", "Only this computer's admin can check for updates.");
            if (_updates is null) return Problem.Create(409, "install_unavailable", "This Bifröst cannot fetch releases.");
            var offered = await _updates.OfferUpdatesAsync(context.RequestAborted);
            return Results.Ok(offered.Select(o => new { o.Name, o.Version }));
        });

        api.MapPost("/extensions/{name}/install-latest", async (HttpContext context, string name) =>
        {
            if (!context.Principal().IsLocal) return Problem.Create(403, "host_only", "Only this computer's admin can install an extension.");
            if (_updates is null) return Problem.Create(409, "install_unavailable", "This Bifröst cannot fetch releases.");
            try
            {
                var done = await _updates.InstallLatestAsync(name, context.RequestAborted);
                await _people.RecordAsync(context.Principal(), "extension.installed", Guid.Empty, $"{done.Name} {done.Version} (GitHub)");
                return Results.Ok(new { done.Name, done.Version, done.Previous, restart = _extensions?.Get(done.Name) is not null ? "extension" : "bifrost" });
            }
            catch (ExtensionInstaller.InstallRefusedException e)
            {
                await _people.RecordAsync(context.Principal(), "extension.install_refused", Guid.Empty, e.Code);
                return Problem.Create(e.Code == "bad_signature" ? 403 : 422, e.Code, e.Message);
            }
            catch (ExtensionUpdates.UpdateException e)
            {
                return Problem.Create(e.Code == "no_source" ? 404 : 422, e.Code, e.Message);
            }
            catch (HttpRequestException)
            {
                return Problem.Create(502, "github_unreachable", "Could not reach GitHub. The home needs the internet for this, and only for this.");
            }
        });

        // Running an extension is changing the home: host only, like the rest of what changes it (design §5).
        api.MapPost("/extensions/{name}/{action}", async (HttpContext context, string name, string action) =>
        {
            if (!context.Principal().IsLocal) return Problem.Create(403, "host_only", "Only this computer's admin can start or stop an extension.");
            if (_extensions?.Get(name) is not { } extension) return Problem.Create(404, "extension_not_found", "No such extension.");
            var running = extension.State is ExtensionState.Starting or ExtensionState.Running;
            switch (action)
            {
                case "start" when running:
                    return Problem.Create(409, "already_running", "That extension is already running.");
                case "start":
                    await extension.StartAsync();
                    break;
                case "stop":
                    await extension.StopAsync();
                    break;
                case "rollback":
                    if (_installer is null) return Problem.Create(409, "install_unavailable", "This Bifröst cannot install extensions.");
                    try { _installer.Rollback(name); }
                    catch (ExtensionInstaller.InstallRefusedException e) { return Problem.Create(409, e.Code, e.Message); }
                    await _people.RecordAsync(context.Principal(), "extension.rolled_back", Guid.Empty, name);
                    await extension.StopAsync();
                    await extension.StartAsync();
                    break;
                case "restart":
                    await extension.StopAsync();
                    await extension.StartAsync();
                    break;
                default:
                    return Problem.Create(404, "no_such_action", "Use start, stop or restart.");
            }
            return Results.Accepted();
        });

        // The page and its parts hold no data of their own; what they show comes from the API, behind the session.
        app.MapGet("/host/login", (HttpContext context, string? t) =>
        {
            if (!IsHostBrowser(context)) return Results.NotFound();
            // A good link always works, however many wrong ones were tried: a 256-bit secret cannot be guessed, so only
            // wrong guesses are slowed, and nobody can lock the admin out by spamming them.
            if (string.IsNullOrEmpty(t) || _hostSessions.Redeem(t) is not { } session)
            {
                var blocked = _limiter.IsBlocked("host-login");
                _limiter.RecordFailure("host-login");
                return Results.Text(HostPage.Expired, "text/html; charset=utf-8", statusCode: blocked ? 429 : 401);
            }
            // Session only (gone when the browser closes), never readable by the page, never sent from another site.
            context.Response.Cookies.Append(HostCookie, session, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true });
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            return Results.Redirect("/host/"); // the secret leaves the address bar at once
        });
        app.MapGet("/host/", (HttpContext context) => HostAsset(context, HostPage.Html, "text/html; charset=utf-8"));
        app.MapGet("/host/app.css", (HttpContext context) => HostAsset(context, HostPage.Css, "text/css; charset=utf-8"));
        app.MapGet("/host/app.js", (HttpContext context) => HostAsset(context, HostPage.Script, "text/javascript; charset=utf-8"));
        app.MapGet("/host/mark.png", (HttpContext context) => HostAsset(context, HostPage.Mark(), "image/png"));
    }

    private static IResult HostAsset(HttpContext context, string text, string type) => HostAsset(context, System.Text.Encoding.UTF8.GetBytes(text), type);

    private static IResult HostAsset(HttpContext context, byte[] bytes, string type)
    {
        if (!IsHostBrowser(context)) return Results.NotFound();
        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Content-Type-Options"] = "nosniff";
        headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        return Results.Bytes(bytes, type);
    }
}
