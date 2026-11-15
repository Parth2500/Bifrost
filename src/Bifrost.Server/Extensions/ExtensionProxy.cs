using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Bifrost.Server.Api;
using Bifrost.Server.People;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

namespace Bifrost.Server.Extensions;

/// <summary>
/// Routes <c>/x/&lt;name&gt;/…</c> to the extension's private socket (design §8.5). The device is
/// authenticated before this runs. Every identity header the client sent is removed, and Bifröst's
/// own are added: the extension trusts them because only Bifröst can reach its socket.
/// </summary>
internal sealed class ExtensionProxy(ExtensionManager? extensions, IHttpForwarder forwarder)
{
    public const string Prefix = "/x";
    private static readonly ForwarderRequestConfig RequestConfig = new() { ActivityTimeout = TimeSpan.FromMinutes(10) };
    private readonly ConcurrentDictionary<string, HttpMessageInvoker> _invokers = new(StringComparer.Ordinal);

    public void Map(IEndpointRouteBuilder app) => app.Map(Prefix + "/{name}/{**rest}", ForwardAsync);

    private async Task ForwardAsync(HttpContext context, string name)
    {
        if (extensions?.Get(name) is not { } extension)
        {
            await Problem.WriteAsync(context, 404, "extension_not_found", "No such extension.");
            return;
        }
        if (extension.State != ExtensionState.Running)
        {
            await Unavailable(context);
            return;
        }

        var prefix = $"{Prefix}/{name}";
        var invoker = _invokers.GetOrAdd(name, _ => CreateInvoker(extension.SocketPath));
        var transformer = new IdentityTransformer(context.Principal(), prefix);
        var error = await forwarder.SendAsync(context, "http://localhost", invoker, RequestConfig, transformer);
        if (error != ForwarderError.None && !context.Response.HasStarted)
        {
            context.Response.Clear();
            await Unavailable(context);
        }
    }

    private static Task Unavailable(HttpContext context) =>
        Problem.WriteAsync(context, 503, "extension_unavailable", "That extension is not running right now.");

    private static HttpMessageInvoker CreateInvoker(string socketPath) => new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ActivityHeadersPropagator = null,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    });

    /// <summary>The header rules of the extension contract.</summary>
    private sealed class IdentityTransformer(Principal caller, string prefix) : HttpTransformer
    {
        private static readonly string[] Dropped = ["Authorization", "Cookie", "Forwarded", "Host"];

        public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest,
            string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

            foreach (var headers in new System.Net.Http.Headers.HttpHeaders?[] { proxyRequest.Headers, proxyRequest.Content?.Headers })
            {
                if (headers is null) continue;
                foreach (var name in headers.Select(h => h.Key).ToList())
                {
                    if (name.StartsWith("X-Bifrost-", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                        || Dropped.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        headers.Remove(name);
                    }
                }
            }

            proxyRequest.Headers.TryAddWithoutValidation("X-Bifrost-Person-Id", caller.PersonId.ToString());
            proxyRequest.Headers.TryAddWithoutValidation("X-Bifrost-Role", caller.IsAdmin ? "admin" : "member");
            proxyRequest.Headers.TryAddWithoutValidation("X-Bifrost-Device-Id", caller.DeviceId.ToString());
            proxyRequest.Headers.TryAddWithoutValidation("X-Bifrost-Device-Kind",
                caller.Kind == Core.Models.DeviceKind.BrowserFallback ? "browser-fallback" : "app");
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", prefix);

            // The same path Bifröst routed on (dot segments already resolved), minus the prefix. An
            // encoded %2F stays encoded, so it stays inside one segment.
            httpContext.Request.Path.StartsWithSegments(prefix, out var rest);
            var path = rest.HasValue ? rest.ToUriComponent() : "/";
            proxyRequest.RequestUri = new Uri("http://localhost" + path + httpContext.Request.QueryString.ToUriComponent(), UriKind.Absolute);
            proxyRequest.Headers.Host = null;
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext httpContext, HttpResponseMessage? proxyResponse,
            CancellationToken cancellationToken)
        {
            var result = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
            httpContext.Response.Headers.Remove("Set-Cookie"); // no cookies on Bifröst's listeners (design §6.4)
            return result;
        }
    }
}
