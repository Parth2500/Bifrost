using System.Net;
using Bifrost.Server.Security;

namespace Bifrost.Tests.Server;

public sealed class HostAddressesTests
{
    [Fact]
    public void The_address_a_connection_arrived_on_is_the_machines_own_with_no_lookup()
    {
        var arrivedOn = IPAddress.Parse("203.0.113.9"); // a documentation address no machine here owns

        Assert.True(HostAddresses.IsOwn(IPAddress.Parse("203.0.113.9"), arrivedOn));
        Assert.True(HostAddresses.IsOwn(IPAddress.Parse("203.0.113.9").MapToIPv6(), arrivedOn)); // dual-stack listeners report v4 as v6
        Assert.True(HostAddresses.IsOwn(IPAddress.Loopback, null));
        Assert.True(HostAddresses.IsOwn(IPAddress.IPv6Loopback, null));
    }

    [Fact]
    public void Some_other_address_is_not_it_so_a_name_pointed_at_the_machine_by_someone_else_is_refused()
    {
        Assert.False(HostAddresses.IsOwn(IPAddress.Parse("203.0.113.77"), IPAddress.Parse("203.0.113.9")));
        Assert.False(HostAddresses.IsOwn(IPAddress.Parse("203.0.113.77"), null));
    }

    [Fact]
    public void The_machines_address_is_found_without_listing_interfaces_when_it_has_a_route_out()
    {
        var lan = HostAddresses.Lan();

        // A build machine may be offline; then there is nothing to find, which is also a valid answer.
        if (lan is not null)
        {
            Assert.True(IPAddress.TryParse(lan, out var address));
            Assert.False(IPAddress.IsLoopback(address!));
        }
    }
}
