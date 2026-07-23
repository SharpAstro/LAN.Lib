using System.Collections.Generic;
using LAN.Lib;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

public class LanProtocolTests
{
    [Fact]
    public void Announce_RoundTrips_CoreFields()
    {
        var line = LanProtocol.EncodeAnnounce("peer-abc", "tianwen-server", 1888, "Observatory PC");

        var msg = LanProtocol.Parse(line);

        msg.Kind.ShouldBe(LanMessageKind.Announce);
        msg.PeerId.ShouldBe("peer-abc");
        msg.Service.ShouldBe("tianwen-server");
        msg.Port.ShouldBe(1888);
        msg.Name.ShouldBe("Observatory PC");
    }

    [Theory]
    [InlineData("Alice Smith")]     // spaces would split tokens without url-encoding
    [InlineData("møøse 🐴")]        // unicode
    [InlineData("a b\tc")]          // whitespace variety
    public void Announce_NameWithSpecialChars_SurvivesEncoding(string name)
    {
        var msg = LanProtocol.Parse(LanProtocol.EncodeAnnounce("id", "svc", 1, name));

        msg.Kind.ShouldBe(LanMessageKind.Announce);
        msg.Name.ShouldBe(name);
    }

    [Fact]
    public void Announce_ServiceWithSpaces_SurvivesEncoding()
    {
        var msg = LanProtocol.Parse(LanProtocol.EncodeAnnounce("id", "my service", 1, "n"));

        msg.Kind.ShouldBe(LanMessageKind.Announce);
        msg.Service.ShouldBe("my service");
    }

    [Fact]
    public void Announce_EmptyName_RoundTripsAsEmpty()
    {
        var msg = LanProtocol.Parse(LanProtocol.EncodeAnnounce("id", "svc", 1, ""));

        msg.Kind.ShouldBe(LanMessageKind.Announce);
        msg.Name.ShouldBe("");
    }

    [Fact]
    public void Announce_Properties_RoundTrip()
    {
        var props = new Dictionary<string, string>
        {
            [LanProtocol.PropNodeId] = "abc123",
            [LanProtocol.PropMachineName] = "My Laptop", // spaces survive (url-encoded)
            [LanProtocol.PropPid] = "4242",
            ["ver"] = "4.2.1",
        };

        var msg = LanProtocol.Parse(LanProtocol.EncodeAnnounce("id", "svc", 1, "Seb", props));

        msg.Properties.ShouldNotBeNull();
        msg.Properties[LanProtocol.PropNodeId].ShouldBe("abc123");
        msg.Properties[LanProtocol.PropMachineName].ShouldBe("My Laptop");
        msg.Properties[LanProtocol.PropPid].ShouldBe("4242");
        msg.Properties["ver"].ShouldBe("4.2.1");
    }

    [Fact]
    public void Announce_PropertyValueWithEquals_SurvivesEncoding()
    {
        // A value containing '=' must not confuse the key=value split.
        var props = new Dictionary<string, string> { ["token"] = "a=b=c" };

        var msg = LanProtocol.Parse(LanProtocol.EncodeAnnounce("id", "svc", 1, "n", props));

        msg.Properties.ShouldNotBeNull();
        msg.Properties["token"].ShouldBe("a=b=c");
    }

    [Fact]
    public void Announce_WithoutProperties_ParsesWithEmptyBag()
    {
        var msg = LanProtocol.Parse(LanProtocol.EncodeAnnounce("id", "svc", 1, "Seb"));

        msg.Kind.ShouldBe(LanMessageKind.Announce);
        msg.Properties.ShouldNotBeNull();
        msg.Properties.Count.ShouldBe(0);
    }

    [Fact]
    public void Bye_RoundTrips_PeerId()
    {
        var msg = LanProtocol.Parse(LanProtocol.EncodeBye("peer-x"));

        msg.Kind.ShouldBe(LanMessageKind.Bye);
        msg.PeerId.ShouldBe("peer-x");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello world")]                    // wrong magic
    [InlineData("SALAN 1")]                         // no verb
    [InlineData("SALAN 1 ANNOUNCE id svc 1")]       // announce missing name (only 6 tokens)
    [InlineData("CHESSLAN 1 ANNOUNCE id svc 1 n")]  // foreign magic on the shared port
    public void Parse_ForeignOrGarbled_ReturnsUnknown(string line)
    {
        LanProtocol.Parse(line).Kind.ShouldBe(LanMessageKind.Unknown);
    }
}
