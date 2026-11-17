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
    private static readonly Area Downloads = Area.Extension("sleipnir");
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
        // A link from another app must never replace a pairing: a paired device ignores it (forget the home first).
        if (_client is null && intent?.DataString is { } link) ShowPairing(link);
    }

    private void ShowPairing(string? link)
    {
        var wide = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        // Colours set outright: the theme's text colour can be the same as its background.
        var field = new EditText(this) { Hint = "bifrost://pair?…", Text = link, LayoutParameters = wide };
        field.SetTextColor(global::Android.Graphics.Color.Black);
        field.SetHintTextColor(global::Android.Graphics.Color.Gray);
        field.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14);
        var status = new TextView(this) { LayoutParameters = wide };
        status.SetTextColor(global::Android.Graphics.Color.Black);
        // Say which home and which key the link names, so the person can check before pairing.
        void Describe()
        {
            try
            {
                var parsed = PairingLink.Parse(field.Text ?? "");
                status.Text = $"This link pairs with {parsed.HomeName} at {parsed.Address}\nKey {parsed.Fingerprint[..16]}…\nPair only if that is your home.";
            }
            catch (FormatException)
            {
                status.Text = "";
            }
        }
        field.TextChanged += (_, _) => Describe();
        var pair = new Button(this) { Text = "Pair", LayoutParameters = wide };
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
        layout.SetPadding(32, BarsHeight() + 16, 32, 32);
        layout.AddView(field);
        layout.AddView(status);
        layout.AddView(pair);
        SetContentView(layout);
        Describe(); // a link that came with the intent is described at once
    }

    private void Show(PairedHome home)
    {
        _client?.Dispose();
        _client = new BifrostClient(home);
        ShowArea(Area.HomePages);
    }

    /// <summary>One web view per area, each allowed to reach that area only; the buttons above switch between them.</summary>
    private void ShowArea(Area area)
    {
        var client = _client!;
        var web = new WebView(this);
        web.Settings.JavaScriptEnabled = true;
        web.Settings.AllowFileAccess = false;
        web.Settings.AllowContentAccess = false;
        // A service worker would fetch outside this app's client; none is allowed to.
        ServiceWorkerController.Instance.SetServiceWorkerClient(new RefusingWorkerClient());
        web.SetWebViewClient(new PinnedClient(client, area));
        web.AddJavascriptInterface(new Bridge(client, area), "bifrost"); // only this area's pages ever load here

        Button Tab(string label, Area target) { var b = new Button(this) { Text = label }; b.Click += (_, _) => ShowArea(target); return b; }
        var tabs = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        tabs.AddView(Tab("Home", Area.HomePages), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        tabs.AddView(Tab("Downloads", Downloads), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        var column = new LinearLayout(this) { Orientation = Orientation.Vertical };
        // Newer Android draws behind the status and title bars; leave room for them.
        column.SetPadding(0, BarsHeight(), 0, 0);
        column.AddView(tabs);
        column.AddView(web, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        SetContentView(column);
        web.LoadUrl(client.AddressOf(area).ToString());
    }

    private int BarsHeight()
    {
        var bar = new global::Android.Util.TypedValue();
        var height = Theme!.ResolveAttribute(global::Android.Resource.Attribute.ActionBarSize, bar, true)
            ? global::Android.Util.TypedValue.ComplexToDimensionPixelSize(bar.Data, Resources!.DisplayMetrics!) : 0;
        var status = Resources!.GetIdentifier("status_bar_height", "dimen", "android");
        return height + (status > 0 ? Resources.GetDimensionPixelSize(status) : 0);
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

    /// <summary>What a page uses to send a request with a body, since the web view hands the app only GETs.</summary>
    private sealed class Bridge(BifrostClient client, Area area) : Java.Lang.Object
    {
        [JavascriptInterface]
        [Java.Interop.Export("send")]
        public string Send(string method, string path, string body)
        {
            try
            {
                var (status, text) = client.SendAsync(area, method, path, body).GetAwaiter().GetResult();
                return System.Text.Json.JsonSerializer.Serialize(new { status, body = text });
            }
            catch (Exception e) when (e is ArgumentException or HttpRequestException)
            {
                return System.Text.Json.JsonSerializer.Serialize(new { status = 0, body = "" }); // refused before it was sent, or the home is out of reach
            }
        }
    }

    private sealed class RefusingWorkerClient : ServiceWorkerClient
    {
        public override WebResourceResponse? ShouldInterceptRequest(IWebResourceRequest? request) => PinnedClient.Refused();
    }

    private sealed class PinnedClient(BifrostClient client, Area area) : WebViewClient
    {
        // What a page may load and send: only itself. Scripts from elsewhere would be refused above anyway;
        // this closes fetch, forms, frames and images to other hosts in the page's own eyes too.
        private static readonly Dictionary<string, string> PageRules = new()
        {
            ["Content-Security-Policy"] = "connect-src 'self'; form-action 'self'; frame-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; base-uri 'self'",
        };

        // A script cannot send the person to another site from inside this app.
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) =>
            !(Uri.TryCreate(request?.Url?.ToString(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
              && u.Authority == client.AddressOf(area).Authority
              && u.AbsolutePath.StartsWith(area.Root, StringComparison.Ordinal));

        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            if (request?.Url?.ToString() is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var address)) return Refused();
            try
            {
                var response = client.GetHomeAsync(area, address, request.RequestHeaders?.ToDictionary(h => h.Key, h => h.Value)).GetAwaiter().GetResult();
                var type = response.Content.Headers.ContentType;
                return new WebResourceResponse(type?.MediaType ?? "application/octet-stream", type?.CharSet ?? "utf-8", (int)response.StatusCode,
                    response.ReasonPhrase ?? "OK", new Dictionary<string, string>(PageRules), response.Content.ReadAsStream());
            }
            catch (Exception e) when (e is ArgumentException or HttpRequestException)
            {
                return Refused(); // another host, plain HTTP or a key that does not match: nothing is sent, nothing is shown
            }
        }

        internal static WebResourceResponse Refused() =>
            new("text/plain", "utf-8", 403, "Forbidden", new Dictionary<string, string>(), new MemoryStream());
    }
}
