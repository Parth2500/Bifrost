using Android.Content;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using AndroidX.Activity;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using Bifrost.Client;
using Android.Gms.Extensions;
using Xamarin.Google.MLKit.Vision.CodeScanner;
using Xamarin.Google.MLKit.Vision.Barcode.Common;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.TextField;

namespace Bifrost.Android;

/// <summary>
/// The thin shell (design §10): pair with an invitation link, then show an extension's screens in a web view
/// whose every request goes through the pinned, signed-in client. A web view cannot pin a self-signed key or
/// add the token to sub-requests on its own, so it is not allowed to reach the network by itself.
/// </summary>
[Activity(Label = "Bifröst", MainLauncher = true, Exported = true, Theme = "@style/Theme.Bifrost")]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "bifrost", DataHost = "pair")]
public sealed class MainActivity : AppCompatActivity
{
    private const string Store = "home";
    private static readonly Area Downloads = Area.Extension("sleipnir");
    private BifrostClient? _client;
    private WebView? _web;
    private BackToPreviousPage? _back;
    private Action? _reload;
    private ArcsView? _stateArcs;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        WindowCompat.SetDecorFitsSystemWindows(Window!, false); // one edge-to-edge behaviour on every Android version; the screens apply the insets
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

    private void ShowPairing(string? link, string? notice = null)
    {
        SetContentView(Resource.Layout.activity_pair);
        var root = FindViewById<View>(Resource.Id.pair_root)!;
        // Content keeps clear of the system bars and the keyboard; the teal hero reaches under the status bar.
        var hero = FindViewById<View>(Resource.Id.pair_hero)!;
        var heroTop = hero.PaddingTop;
        ApplyInsets(root, (l, t, r, b, ime) =>
        {
            root.SetPadding(l, 0, r, Math.Max(b, ime));
            hero.SetPadding(hero.PaddingLeft, heroTop + t, hero.PaddingRight, hero.PaddingBottom);
        });

        var arcs = new ArcsView(this);
        arcs.Set(ArcState.Off);
        FindViewById<FrameLayout>(Resource.Id.pair_arcs)!.AddView(arcs, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var input = FindViewById<TextInputEditText>(Resource.Id.pair_input)!;
        var check = FindViewById<View>(Resource.Id.pair_check)!;
        var status = FindViewById<TextView>(Resource.Id.pair_status)!;
        var pair = FindViewById<MaterialButton>(Resource.Id.pair_button)!;
        input.Text = link;
        FindViewById<MaterialButton>(Resource.Id.scan_button)!.Click += async (_, _) =>
        {
            try
            {
                // Google's scanner runs in Play services, so the app needs no camera permission. The link is only filled in: the person still checks the card and presses Pair.
                var options = new GmsBarcodeScannerOptions.Builder().SetBarcodeFormats(Barcode.FormatQrCode).Build();
                var code = await GmsBarcodeScanning.GetClient(this, options).StartScan().AsAsync<Barcode>();
                input.Text = code.RawValue;
            }
            catch (System.OperationCanceledException)
            {
                // the person backed out of the scanner
            }
            catch (Java.Lang.Exception)
            {
                status.SetTextColor(ErrorColour());
                status.Text = "The scanner is not available on this phone. Paste the link instead.";
            }
        };
        if (notice is not null) status.Text = notice;
        status.SetTextColor(notice is null ? ErrorColour() : OnSurfaceColour());

        // Say which home and which key the link names, so the person can check before pairing.
        void Describe()
        {
            try
            {
                var parsed = PairingLink.Parse(input.Text ?? "");
                FindViewById<TextView>(Resource.Id.pair_home)!.Text = parsed.HomeName is { Length: > 0 } ? parsed.HomeName : "Your home";
                FindViewById<TextView>(Resource.Id.pair_address)!.Text = parsed.Address;
                FindViewById<TextView>(Resource.Id.pair_key)!.Text = "Key " + string.Join(' ', parsed.Fingerprint[..16].ToUpperInvariant().Chunk(4).Select(c => new string(c)));
                check.Visibility = ViewStates.Visible;
            }
            catch (FormatException)
            {
                check.Visibility = ViewStates.Gone;
            }
        }
        input.TextChanged += (_, _) => Describe();
        pair.Click += async (_, _) =>
        {
            status.Text = "";
            pair.Enabled = false;
            pair.Text = "Pairing…";
            FindViewById<View>(Resource.Id.pair_arcs)!.Visibility = ViewStates.Visible;
            arcs.Set(ArcState.Searching);
            try
            {
                var parsed = PairingLink.Parse(input.Text ?? "");
                var home = await BifrostPairing.PairWithLinkAsync(parsed, global::Android.OS.Build.Model ?? "Android", "android");
                Save(home, parsed.HomeName);
                arcs.Set(ArcState.Connected);
                Show(home);
            }
            catch (Exception e) when (e is FormatException or BifrostPairing.PairingRefusedException or HttpRequestException)
            {
                arcs.Set(ArcState.Off);
                status.SetTextColor(ErrorColour());
                status.Text = e.Message; // a failed handshake or a refusal, in words the person can act on
                pair.Enabled = true;
                pair.Text = "Pair";
            }
        };
        Describe(); // a link that came with the intent is described at once

        // Path B (design §7.2): no link and no camera. The device shows a code, the admin compares it on the host screen and approves.
        var codeBox = FindViewById<View>(Resource.Id.code_box)!;
        FindViewById<MaterialButton>(Resource.Id.code_toggle)!.Click += (_, _) => codeBox.Visibility = codeBox.Visibility == ViewStates.Visible ? ViewStates.Gone : ViewStates.Visible;
        var joinButton = FindViewById<MaterialButton>(Resource.Id.code_join)!;
        var cancelButton = FindViewById<MaterialButton>(Resource.Id.code_cancel)!;
        var codeValue = FindViewById<TextView>(Resource.Id.code_value)!;
        var codeHelp = FindViewById<View>(Resource.Id.code_help)!;
        CancellationTokenSource? joining = null;
        void EndJoin()
        {
            joining?.Dispose();
            joining = null;
            joinButton.Enabled = true;
            cancelButton.Visibility = codeValue.Visibility = codeHelp.Visibility = ViewStates.Gone;
        }
        cancelButton.Click += (_, _) => joining?.Cancel();
        joinButton.Click += async (_, _) =>
        {
            var address = (FindViewById<TextInputEditText>(Resource.Id.code_address)!.Text ?? "").Trim();
            status.Text = "";
            if (!Uri.TryCreate("https://" + address, UriKind.Absolute, out var parsed) || parsed.Port <= 0 || address.Contains('/'))
            {
                status.SetTextColor(ErrorColour());
                status.Text = "Type the address as host:port, for example 192.168.1.9:7781.";
                return;
            }
            joining = new CancellationTokenSource();
            joinButton.Enabled = false;
            cancelButton.Visibility = ViewStates.Visible;
            arcs.Set(ArcState.Searching);
            FindViewById<View>(Resource.Id.pair_arcs)!.Visibility = ViewStates.Visible;
            try
            {
                var home = await BifrostPairing.JoinAsync(address, global::Android.OS.Build.Model ?? "Android", "android", code => RunOnUiThread(() =>
                {
                    codeValue.Text = string.Join(' ', code.Chunk(4).Select(c => new string(c)));
                    codeValue.Visibility = codeHelp.Visibility = ViewStates.Visible;
                }), ct: joining.Token);
                Save(home, "");
                arcs.Set(ArcState.Connected);
                Show(home);
            }
            catch (OperationCanceledException)
            {
                arcs.Set(ArcState.Off);
                EndJoin();
            }
            catch (Exception e) when (e is FormatException or BifrostPairing.PairingRefusedException or HttpRequestException)
            {
                arcs.Set(ArcState.Off);
                status.SetTextColor(ErrorColour());
                status.Text = e.Message;
                EndJoin();
            }
        };
    }

    private void Show(PairedHome home)
    {
        _client?.Dispose();
        _client = new BifrostClient(home);
        SetContentView(Resource.Layout.activity_shell);
        var root = FindViewById<View>(Resource.Id.shell_root)!;
        var appbar = FindViewById<View>(Resource.Id.appbar)!;
        ApplyInsets(root, (l, t, r, b, ime) =>
        {
            root.SetPadding(l, 0, r, 0); // the bottom bar pads itself for the gesture area
            appbar.SetPadding(0, t, 0, 0);
        });

        var name = Prefs().GetString("name", null) is { Length: > 0 } n ? n : home.Address; // pairings made before the name was kept show the address
        FindViewById<TextView>(Resource.Id.bar_home)!.Text = name;
        var toolbar = FindViewById<AndroidX.AppCompat.Widget.Toolbar>(Resource.Id.toolbar)!;
        toolbar.InflateMenu(Resource.Menu.shell);
        toolbar.MenuItemClick += (_, e) =>
        {
            if (e.Item?.ItemId != Resource.Id.forget) return;
            new MaterialAlertDialogBuilder(this)
                .SetTitle("Forget this home?")!
                .SetMessage("This device will need a new invitation to pair again.")!
                .SetPositiveButton("Forget", async (_, _) => await ForgetAndSignOutAsync())!
                .SetNegativeButton("Cancel", (_, _) => { })!
                .Show()
                .GetButton((int)DialogButtonType.Positive)?.SetTextColor(ErrorColour());
        };

        FindViewById<MaterialButton>(Resource.Id.retry)!.Click += (_, _) => _reload?.Invoke();
        var stateArcs = new ArcsView(this, onGround: true);
        FindViewById<FrameLayout>(Resource.Id.unreachable_arcs)!.AddView(stateArcs, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _stateArcs = stateArcs;
        var arcs = new ArcsView(this);
        FindViewById<FrameLayout>(Resource.Id.bar_arcs)!.AddView(arcs, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        var nav = FindViewById<BottomNavigationView>(Resource.Id.nav)!;
        nav.ItemSelected += (_, e) =>
        {
            if (e.Item.ItemId == Resource.Id.nav_downloads) ShowArea(Downloads, "Downloads", name, arcs);
            else ShowArea(Area.HomePages, "Home", name, arcs);
        };
        _back = new BackToPreviousPage(() => _web);
        OnBackPressedDispatcher.AddCallback(this, _back);
        nav.SelectedItemId = Resource.Id.nav_home; // fires ItemSelected, which loads the first area
    }

    /// <summary>The person chose to forget: sign this device out on the home first, so its token stops working, then forget it here.</summary>
    private async Task ForgetAndSignOutAsync()
    {
        var signedOut = false;
        try
        {
            signedOut = _client is { } client && await client.RevokeSelfAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException or OperationCanceledException or ArgumentException)
        {
            // unreachable or too slow: handled below as "not signed out"
        }
        // Forgotten here either way (the person asked), but never silently: if the home was not told, say so.
        ForgetHome(signedOut ? null : "The home could not be reached. Ask the admin to sign this device out.");
    }

    /// <summary>Drops the pairing and goes back to the pairing screen: the person chose to, or the home refused our token.</summary>
    private void ForgetHome(string? notice)
    {
        _back?.Remove();
        _back = null;
        Prefs().Edit()!.Clear()!.Apply();
        _web?.Destroy();
        _web = null;
        _client?.Dispose();
        _client = null;
        ShowPairing(null, notice);
    }

    /// <summary>One web view per area, each allowed to reach that area only; the bottom bar switches between them.</summary>
    private void ShowArea(Area area, string areaName, string homeName, ArcsView arcs)
    {
        var client = _client!;
        var content = FindViewById<FrameLayout>(Resource.Id.content)!;
        var progress = FindViewById<LinearProgressIndicator>(Resource.Id.progress)!;
        var unreachable = FindViewById<View>(Resource.Id.unreachable)!;
        var subtitle = FindViewById<TextView>(Resource.Id.bar_area)!;

        if (_web is not null)
        {
            content.RemoveView(_web);
            _web.Destroy();
        }
        var web = new WebView(this);
        _web = web;
        web.Settings.JavaScriptEnabled = true;
        web.Settings.AllowFileAccess = false;
        web.Settings.AllowContentAccess = false;
        // A service worker would fetch outside this app's client; none is allowed to.
        ServiceWorkerController.Instance.SetServiceWorkerClient(new RefusingWorkerClient());

        void SignedOut() => RunOnUiThread(() => ForgetHome("This device was signed out by the home. Pair again with a new invitation."));
        void Reachable(bool ok, bool mainFrame) => RunOnUiThread(() =>
        {
            arcs.Set(ok ? ArcState.Connected : ArcState.Off);
            subtitle.Text = ok ? $"{areaName} · On your Wi-Fi" : $"{areaName} · Can't reach the home";
            if (mainFrame) unreachable.Visibility = ok ? ViewStates.Gone : ViewStates.Visible;
            if (!ok && mainFrame)
            {
                _stateArcs?.Set(ArcState.Off);
                FindViewById<TextView>(Resource.Id.unreachable_title)!.Text = $"Can't reach {homeName}";
                FindViewById<TextView>(Resource.Id.unreachable_hint)!.Text = "Check that you are on the home Wi-Fi and the home machine is on.";
            }
        });
        void Unavailable() => RunOnUiThread(() =>
        {
            arcs.Set(ArcState.Connected); // the home answered; it is this area that has nothing to show
            _stateArcs?.Set(ArcState.Off);
            subtitle.Text = $"{areaName} · Not available";
            FindViewById<TextView>(Resource.Id.unreachable_title)!.Text = $"{areaName} isn't available on {homeName}";
            FindViewById<TextView>(Resource.Id.unreachable_hint)!.Text = "It may not be installed on the home, or it has stopped.";
            unreachable.Visibility = ViewStates.Visible;
        });
        web.SetWebViewClient(new PinnedClient(client, area, SignedOut, Reachable, Unavailable,
            loading => RunOnUiThread(() => progress.Visibility = loading ? ViewStates.Visible : ViewStates.Invisible), () => _back?.Update()));
        web.AddJavascriptInterface(new Bridge(client, area, SignedOut), "bifrost"); // only this area's pages ever load here
        content.AddView(web, 0, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        subtitle.Text = $"{areaName} · On your Wi-Fi";
        unreachable.Visibility = ViewStates.Gone;
        _reload = () => { unreachable.Visibility = ViewStates.Gone; web.LoadUrl(client.AddressOf(area).ToString()); };
        web.LoadUrl(client.AddressOf(area).ToString());
    }

    /// <summary>The token is kept encrypted by a Keystore key (<see cref="TokenVault"/>). A pairing from before that, with the token in plain text, is sealed on first read.</summary>
    private PairedHome? Load()
    {
        var p = Prefs();
        string? token = null;
        if (p.GetString("token_sealed", null) is { } sealedToken) token = TokenVault.Open(sealedToken);
        else if (p.GetString("token", null) is { } plain)
        {
            token = plain;
            p.Edit()!.PutString("token_sealed", TokenVault.Seal(plain))!.Remove("token")!.Apply(); // the plain copy goes now
        }
        return token is not null && p.GetString("address", null) is { } address
            && p.GetString("fp", null) is { } fp && Guid.TryParse(p.GetString("device", null), out var device)
            ? new PairedHome(address, fp, token, device) : null;
    }

    private void Save(PairedHome h, string homeName) => Prefs().Edit()!
        .PutString("address", h.Address)!.PutString("fp", h.Fingerprint)!.PutString("token_sealed", TokenVault.Seal(h.Token))!.Remove("token")!
        .PutString("device", h.DeviceId.ToString())!.PutString("name", homeName)!.Apply();

    private ISharedPreferences Prefs() => GetSharedPreferences(Store, FileCreationMode.Private)!;

    private global::Android.Graphics.Color ErrorColour() => new(Resources!.GetColor(Resource.Color.bifrost_error, Theme));
    private global::Android.Graphics.Color OnSurfaceColour()
    {
        var value = new global::Android.Util.TypedValue();
        Theme!.ResolveAttribute(Resource.Attribute.colorOnSurface, value, true); // ink in light, pale in dark
        return new global::Android.Graphics.Color(value.Data);
    }

    private static void ApplyInsets(View root, Action<int, int, int, int, int> apply) =>
        ViewCompat.SetOnApplyWindowInsetsListener(root, new InsetsListener(apply));

    private sealed class InsetsListener(Action<int, int, int, int, int> apply) : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(View v, WindowInsetsCompat insets)
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout())!;
            var ime = insets.GetInsets(WindowInsetsCompat.Type.Ime())!;
            apply(bars.Left, bars.Top, bars.Right, bars.Bottom, ime.Bottom);
            return insets; // not consumed: the bottom bar pads itself from the same insets
        }
    }

    /// <summary>System Back goes to the previous page inside the area first, and leaves the app only from the first page.</summary>
    private sealed class BackToPreviousPage(Func<WebView?> web) : OnBackPressedCallback(false)
    {
        public void Update() => Enabled = web()?.CanGoBack() == true;

        public override void HandleOnBackPressed() => web()?.GoBack();
    }

    /// <summary>What a page uses to send a request with a body, since the web view hands the app only GETs.</summary>
    private sealed class Bridge(BifrostClient client, Area area, Action refused) : Java.Lang.Object
    {
        [JavascriptInterface]
        [Java.Interop.Export("send")]
        public string Send(string method, string path, string body)
        {
            try
            {
                var (status, text) = client.SendAsync(area, method, path, body).GetAwaiter().GetResult();
                if (status == 401) refused(); // the home no longer knows this device
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

    private sealed class PinnedClient(BifrostClient client, Area area, Action refused, Action<bool, bool> reachable, Action unavailable, Action<bool> loading, Action navigated) : WebViewClient
    {
        // What a page may load and send: only itself. Scripts from elsewhere would be refused above anyway;
        // this closes fetch, forms, frames and images to other hosts in the page's own eyes too.
        private static readonly Dictionary<string, string> PageRules = new()
        {
            ["Content-Security-Policy"] = "connect-src 'self'; form-action 'self'; frame-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; base-uri 'self'",
        };

        public override void OnPageStarted(WebView? view, string? url, global::Android.Graphics.Bitmap? favicon)
        {
            loading(true);
            base.OnPageStarted(view, url, favicon);
        }

        public override void OnPageFinished(WebView? view, string? url)
        {
            loading(false);
            navigated();
            base.OnPageFinished(view, url);
        }

        // A script cannot send the person to another site from inside this app.
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) =>
            !(Uri.TryCreate(request?.Url?.ToString(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
              && u.Authority == client.AddressOf(area).Authority
              && u.AbsolutePath.StartsWith(area.Root, StringComparison.Ordinal));

        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            var main = request?.IsForMainFrame == true;
            if (request?.Url?.ToString() is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var address)) return Refused();
            try
            {
                var response = client.GetHomeAsync(area, address, request.RequestHeaders?.ToDictionary(h => h.Key, h => h.Value)).GetAwaiter().GetResult();
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) refused(); // signed out on the home: pair again
                else if (main && (int)response.StatusCode >= 400) unavailable(); // a page the area could not give: say so, not the raw error
                else reachable(true, main);
                var type = response.Content.Headers.ContentType;
                return new WebResourceResponse(type?.MediaType ?? "application/octet-stream", type?.CharSet ?? "utf-8", (int)response.StatusCode,
                    response.ReasonPhrase ?? "OK", new Dictionary<string, string>(PageRules), response.Content.ReadAsStream());
            }
            catch (ArgumentException)
            {
                return Refused(); // another host or area: refused before anything was sent, which says nothing about the home
            }
            catch (HttpRequestException)
            {
                reachable(false, main); // out of reach, plain HTTP or a key that does not match: the person is told
                return Refused();
            }
        }

        internal static WebResourceResponse Refused() =>
            new("text/plain", "utf-8", 403, "Forbidden", new Dictionary<string, string>(), new MemoryStream());
    }
}
