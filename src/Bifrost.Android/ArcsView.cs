using Android.Animation;
using Android.Content;
using Android.Graphics;
using Android.Views;

namespace Bifrost.Android;

public enum ArcState { Off, Searching, Connected }

/// <summary>
/// The three arcs of the Bifröst mark as a connection gauge: all lit when the home is reachable, dim when it is
/// not, filling one by one while a connection is being made. Never the only signal: the bar's words say the same.
/// </summary>
public sealed class ArcsView : View
{
    private readonly Paint _paint = new(PaintFlags.AntiAlias) { StrokeCap = Paint.Cap.Round };
    private ValueAnimator? _fill;
    private int _step = 3;
    private ArcState _state = ArcState.Connected;

    private readonly bool _onGround;

    /// <param name="onGround">Drawn on the page ground, not the teal bar: light ink in dark mode.</param>
    public ArcsView(Context context, bool onGround = false) : base(context) => _onGround = onGround;

    public void Set(ArcState state)
    {
        if (state == _state) return;
        _state = state;
        _fill?.Cancel();
        _fill = null;
        _step = state == ArcState.Connected ? 3 : 0;
        if (state == ArcState.Searching)
        {
            // Respects the system's remove-animations setting: the duration scale makes this jump to its end.
            _fill = ValueAnimator.OfInt(0, 4)!;
            _fill.SetDuration(1200);
            _fill.RepeatCount = ValueAnimator.Infinite;
            _fill.SetInterpolator(new global::Android.Views.Animations.LinearInterpolator());
            _fill.Update += (_, e) => { _step = (int)e.Animation.AnimatedValue!; Invalidate(); };
            _fill.Start();
        }
        ContentDescription = state switch { ArcState.Connected => "Connected to your home", ArcState.Searching => "Connecting", _ => "Not connected" };
        Invalidate();
    }

    protected override void OnDetachedFromWindow()
    {
        _fill?.Cancel();
        base.OnDetachedFromWindow();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var stroke = Height / 7f;
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = stroke;
        var outer = Math.Min(Width / 2f, Height) - stroke / 2;
        var cx = Width / 2f;
        var baseY = Height - stroke / 2;
        for (var i = 0; i < 3; i++)
        {
            var r = outer * (i + 1) / 3f;
            var night = (Resources!.Configuration!.UiMode & global::Android.Content.Res.UiMode.NightMask) == global::Android.Content.Res.UiMode.NightYes;
            _paint.Color = _onGround && night ? new Color(0xE4, 0xEE, 0xED) : new Color(Resources!.GetColor(Resource.Color.bifrost_ink, null));
            _paint.Alpha = i < _step ? 255 : (_onGround ? 110 : 90);
            canvas.DrawArc(new RectF(cx - r, baseY - r, cx + r, baseY + r), 180, 180, false, _paint);
        }
    }
}
