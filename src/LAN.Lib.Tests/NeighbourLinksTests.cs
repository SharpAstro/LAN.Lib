using System.Collections.Generic;
using System.Net;
using LAN.Lib;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

/// <summary>The on-link check behind <see cref="LanNeighbours"/>, through an adapter list the test gives (never this machine's).</summary>
public class NeighbourLinksTests
{
    private static NetworkInterfaceLinks Links(params (string Address, int PrefixLength)[] subnets)
    {
        var list = new List<(IPAddress Address, int PrefixLength)>();
        foreach (var (address, prefixLength) in subnets)
        {
            list.Add((IPAddress.Parse(address), prefixLength));
        }
        return new NetworkInterfaceLinks(() => list);
    }

    [Theory]
    [InlineData("192.168.1.23", "192.168.1.1", 24, true)]
    [InlineData("192.168.1.255", "192.168.1.1", 24, true)]
    [InlineData("192.168.2.23", "192.168.1.1", 24, false)]
    [InlineData("192.168.4.1", "192.168.4.2", 30, true)]
    [InlineData("192.168.4.3", "192.168.4.2", 30, true)]
    [InlineData("192.168.4.4", "192.168.4.2", 30, false)]
    [InlineData("192.168.1.100", "192.168.1.1", 25, true)]
    [InlineData("192.168.1.200", "192.168.1.1", 25, false)]
    [InlineData("10.200.3.4", "10.0.0.1", 8, true)]
    [InlineData("11.0.0.1", "10.0.0.1", 8, false)]
    [InlineData("192.168.200.1", "192.168.1.1", 16, true)]
    [InlineData("192.169.0.1", "192.168.1.1", 16, false)]
    [InlineData("8.8.8.8", "192.168.1.1", 0, true)]
    [InlineData("192.168.1.2", "192.168.1.1", 32, false)]
    [InlineData("192.168.1.1", "192.168.1.1", 32, true)]
    public void AnIPv4AddressIsInASubnetWhenTheirPrefixBitsAgree(string address, string network, int prefixLength, bool expected)
    {
        NetworkInterfaceLinks.InSubnet(IPAddress.Parse(address), IPAddress.Parse(network), prefixLength).ShouldBe(expected);
    }

    [Theory]
    [InlineData("fd00::17", "fd00::2", 64, true)]
    [InlineData("fd00:0:0:1::17", "fd00::2", 64, false)]
    [InlineData("fd00:0:0:1::17", "fd00::2", 48, true)]
    [InlineData("fe80::17", "fe80::abcd", 64, true)]
    [InlineData("2001:db8::1", "fd00::2", 64, false)]
    public void AnIPv6AddressIsInASubnetWhenTheirPrefixBitsAgree(string address, string network, int prefixLength, bool expected)
    {
        NetworkInterfaceLinks.InSubnet(IPAddress.Parse(address), IPAddress.Parse(network), prefixLength).ShouldBe(expected);
    }

    [Fact]
    public void AScopeIdSaysWhichLinkNotWhichSubnet()
    {
        NetworkInterfaceLinks.InSubnet(IPAddress.Parse("fe80::17%3"), IPAddress.Parse("fe80::abcd%5"), 64).ShouldBeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(33)]
    public void AGarbledPrefixLengthMatchesNothing(int prefixLength)
    {
        NetworkInterfaceLinks.InSubnet(IPAddress.Parse("192.168.1.1"), IPAddress.Parse("192.168.1.1"), prefixLength).ShouldBeFalse();
    }

    [Fact]
    public void AnAddressOfAnotherFamilyIsNeverInASubnet()
    {
        NetworkInterfaceLinks.InSubnet(IPAddress.Parse("192.168.1.1"), IPAddress.Parse("::"), 0).ShouldBeFalse();
        NetworkInterfaceLinks.InSubnet(IPAddress.Parse("::1"), IPAddress.Parse("0.0.0.0"), 0).ShouldBeFalse();
    }

    [Fact]
    public void AnAddressIsOnALinkWhenItIsInTheSubnetOfAnyAdapter()
    {
        var links = Links(("10.0.0.5", 24), ("192.168.4.2", 24), ("fd00::2", 64));

        links.IsOnLink(IPAddress.Parse("192.168.4.1")).ShouldBeTrue();
        links.IsOnLink(IPAddress.Parse("10.0.0.99")).ShouldBeTrue();
        links.IsOnLink(IPAddress.Parse("fd00::9")).ShouldBeTrue();
        links.IsOnLink(IPAddress.Parse("192.168.5.1")).ShouldBeFalse();
        links.IsOnLink(IPAddress.Parse("8.8.8.8")).ShouldBeFalse("beyond the router");
    }

    [Fact]
    public void AnIPv4AddressSeenThroughAnIPv6SocketIsOnTheIPv4Link()
    {
        Links(("192.168.4.2", 24)).IsOnLink(IPAddress.Parse("192.168.4.1").MapToIPv6()).ShouldBeTrue();
    }

    [Fact]
    public void NoAdaptersIsNoLink()
    {
        Links().IsOnLink(IPAddress.Parse("192.168.4.1")).ShouldBeFalse();
    }

    [Fact]
    public void ANullAddressIsAnArgumentError()
    {
        Should.Throw<System.ArgumentNullException>(() => Links().IsOnLink(null!));
    }
}
