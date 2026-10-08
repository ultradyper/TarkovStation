using System.Net;
using System.Linq;
using Lidgren.Network;
using NUnit.Framework;

namespace Robust.UnitTesting.Shared.Networking;

internal sealed class ConnectionLimitConfigurationTest
{
    [Test]
    public void MaximumConnectionsPerAddressExemptAddressesNormalizesIpv4MappedIpv6()
    {
        var config = new NetPeerConfiguration(nameof(MaximumConnectionsPerAddressExemptAddressesNormalizesIpv4MappedIpv6));
        var address = IPAddress.Parse("192.0.2.10");

        config.MaximumConnectionsPerAddressExemptAddresses = new[]
        {
            IPAddress.Parse("::ffff:192.0.2.10")
        };

        Assert.That(config.MaximumConnectionsPerAddressExemptAddresses.ToArray(), Does.Contain(address));
    }
}
