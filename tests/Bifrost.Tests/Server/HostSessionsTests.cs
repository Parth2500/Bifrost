using Bifrost.Server.Security;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Server;

public sealed class HostSessionsTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_link_opens_one_session_and_works_once()
    {
        var sessions = new HostSessions(_time);
        var link = sessions.NewLink(out _)!;
        var session = sessions.Redeem(link);
        Assert.NotNull(session);
        Assert.True(sessions.Validate(session));
        Assert.Null(sessions.Redeem(link));
    }

    [Fact]
    public void A_link_expires_after_two_minutes()
    {
        var sessions = new HostSessions(_time);
        var link = sessions.NewLink(out var expires)!;
        Assert.Equal(_time.GetUtcNow() + HostSessions.LinkLife, expires);
        _time.Advance(HostSessions.LinkLife);
        Assert.Null(sessions.Redeem(link));
    }

    [Fact]
    public void An_unknown_secret_opens_nothing()
    {
        var sessions = new HostSessions(_time);
        sessions.NewLink(out _);
        Assert.Null(sessions.Redeem("not-a-secret"));
        Assert.False(sessions.Validate("not-a-session"));
    }

    [Fact]
    public void A_session_ends_after_an_idle_hour_but_use_keeps_it_alive()
    {
        var sessions = new HostSessions(_time);
        var session = sessions.Redeem(sessions.NewLink(out _)!)!;
        _time.Advance(TimeSpan.FromMinutes(50));
        Assert.True(sessions.Validate(session));
        _time.Advance(TimeSpan.FromMinutes(50));
        Assert.True(sessions.Validate(session));
        _time.Advance(HostSessions.IdleLife);
        Assert.False(sessions.Validate(session));
    }

    [Fact]
    public void A_session_ends_after_eight_hours_however_busy()
    {
        var sessions = new HostSessions(_time);
        var session = sessions.Redeem(sessions.NewLink(out _)!)!;
        for (var i = 0; i < 8; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(59));
            sessions.Validate(session);
        }
        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.False(sessions.Validate(session));
    }

    [Fact]
    public void Logging_out_ends_the_session()
    {
        var sessions = new HostSessions(_time);
        var session = sessions.Redeem(sessions.NewLink(out _)!)!;
        sessions.End(session);
        Assert.False(sessions.Validate(session));
    }

    [Fact]
    public void Only_five_links_can_be_open_at_once()
    {
        var sessions = new HostSessions(_time);
        for (var i = 0; i < 5; i++) Assert.NotNull(sessions.NewLink(out _));
        Assert.Null(sessions.NewLink(out _));
        _time.Advance(HostSessions.LinkLife);
        Assert.NotNull(sessions.NewLink(out _));
    }

    [Fact]
    public void Secrets_are_long_and_never_repeat()
    {
        var sessions = new HostSessions(_time);
        var a = sessions.NewLink(out _)!;
        var b = sessions.NewLink(out _)!;
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 43); // 256 bits
    }
}
