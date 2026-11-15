using Bifrost.Server.Extensions;

namespace Bifrost.Tests.Extensions;

public class RestartPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Quick_crashes_wait_1_2_4_8_seconds_then_give_up_at_the_fifth()
    {
        var policy = new RestartPolicy();
        var now = T0;
        var delays = new List<TimeSpan?>();
        for (var i = 0; i < 5; i++)
        {
            delays.Add(policy.OnExit(startedAt: now, exitedAt: now.AddSeconds(1)));
            now = now.AddSeconds(1) + (delays[^1] ?? TimeSpan.Zero);
        }

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), null], delays);
    }

    [Fact]
    public void Delays_are_capped_at_60_seconds()
    {
        var policy = new RestartPolicy(maxCrashes: 100);
        var now = T0;
        TimeSpan? last = null;
        for (var i = 0; i < 10; i++)
        {
            last = policy.OnExit(now, now);
            now += TimeSpan.FromMinutes(1);
        }

        Assert.Equal(TimeSpan.FromSeconds(60), last);
    }

    [Fact]
    public void A_run_longer_than_10_minutes_resets_the_count()
    {
        var policy = new RestartPolicy();
        for (var i = 0; i < 4; i++) policy.OnExit(T0, T0);

        var delay = policy.OnExit(startedAt: T0, exitedAt: T0.AddMinutes(11));

        Assert.Equal(TimeSpan.FromSeconds(1), delay);
    }

    [Fact]
    public void Crashes_more_than_10_minutes_apart_do_not_add_up()
    {
        var policy = new RestartPolicy();
        TimeSpan? last = null;
        for (var i = 0; i < 8; i++)
        {
            var at = T0.AddMinutes(11 * i);
            last = policy.OnExit(at, at);
        }

        Assert.NotNull(last);
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var policy = new RestartPolicy();
        for (var i = 0; i < 5; i++) policy.OnExit(T0, T0);

        policy.Reset();

        Assert.Equal(TimeSpan.FromSeconds(1), policy.OnExit(T0, T0));
    }
}
