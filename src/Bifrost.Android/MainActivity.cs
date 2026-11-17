using Android.Content;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using Bifrost.Client;

namespace Bifrost.Android;

/// <summary>
/// The thin shell (design §10): pair with an invitation link, then show an extension's screens in a web view
/// whose every request goes through the pinned, signed-in client. A web view cannot pin a self-signed key or
/// add the token to sub-requests on its own, so it is not allowed to reach the network by itself.
/// </summary>
[Activity(Label = "Bifröst", MainLauncher = true, Exported = true)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "bifrost", DataHost = "pair")]
public sealed class MainActivity : Activity
{
    private const string Store = "home";
    private BifrostClient? _client;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var saved = Load();
        if (saved is not null) Show(saved);
        else ShowPairing(Intent?.DataString);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent?.DataString is { } link) ShowPairing(link);
    }

    private void ShowPairing(string? link)
    {
        var field = new EditText(this) { Hint = "bifrost://pair?…", Text = link };
        var status = new TextView(this);
        var pair = new Button(this) { Text = "Pair" };
        pair.Click += async (_, _) =>
        {
            status.Text = "Pairing…";
            try
            {
                var home = await BifrostPairing.PairWithLinkAsync(PairingLink.Parse(field.Text ?? ""), global::Android.OS.Build.Model ?? "Android", "android");
                Save(home);
                Show(home);
            }
            catch (Exception e) when (e is FormatException or BifrostPairing.PairingRefusedException or HttpRequestException)
            {
                status.Text = e.Message; // a failed handshake or a refusal, in words the person can act on
            }
        };
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(32, 32, 32, 32);
        layout.AddView(field);
        layout.AddView(pair);
        layout.AddView(status);
        SetContentView(layout);
    }

    private void Show(PairedHome home)
    {
        _client?.Dispose();
        _client = new BifrostClient(home);
        var web = new WebView(this);
        web.Settings.JavaScriptEnabled = true;
        web.Settings.AllowFileAccess = false;
        web.Settings.AllowContentAccess = false;
        web.SetWebViewClient(new PinnedClient(_client));
        SetContentView(web);
        web.LoadUrl(_client.ExtensionAddress("sleipnir").ToString());
    }

    // shortcut: plain private preferences, readable by anyone with root; move to the Android Keystore before release.
    private PairedHome? Load()
    {
        var p = GetSharedPreferences(Store, FileCreationMode.Private)!;
        return p.GetString("token", null) is { } token && p.GetString("address", null) is { } address
            && p.GetString("fp", null) is { } fp && Guid.TryParse(p.GetString("device", null), out var device)
            ? new PairedHome(address, fp, token, device) : null;
    }

    private void Save(PairedHome h) => GetSharedPreferences(Store, FileCreationMode.Private)!.Edit()!
        .PutString("address", h.Address)!.PutString("fp", h.Fingerprint)!.PutString("token", h.Token)!.PutString("device", h.DeviceId.ToString())!.Apply();

    private sealed class PinnedClient(BifrostClient client) : WebViewClient
    {
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            if (request?.Url?.ToString() is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var address)) return Refused();
            try
            {
                var response = client.GetHomeAsync(address, request.RequestHeaders?.ToDictionary(h => h.Key, h => h.Value)).GetAwaiter().GetResult();
                var type = response.Content.Headers.ContentType;
                return new WebResourceResponse(type?.MediaType ?? "application/octet-stream", type?.CharSet ?? "utf-8", (int)response.StatusCode,
                    response.ReasonPhrase ?? "OK", new Dictionary<string, string>(), response.Content.ReadAsStream());
            }
            catch (Exception e) when (e is ArgumentException or HttpRequestException)
            {
                return Refused(); // another host, plain HTTP or a key that does not match: nothing is sent, nothing is shown
            }
        }

        private static WebResourceResponse Refused() =>
            new("text/plain", "utf-8", 403, "Forbidden", new Dictionary<string, string>(), new MemoryStream());
    }
}
