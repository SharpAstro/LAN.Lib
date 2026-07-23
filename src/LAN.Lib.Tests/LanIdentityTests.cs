using System;
using System.IO;
using LAN.Lib;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

public class LanIdentityTests
{
    [Fact]
    public void Create_WithNullPath_HasEmptyNodeId_AndFreshPeerId()
    {
        var id = LanIdentity.Create(null);

        id.NodeId.ShouldBe("");
        id.PeerId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void PeerId_IsAlwaysFresh_PerProcess()
    {
        // Even with no persistence, two identities must never share a peer id (the self-echo filter
        // needs it unique per running process — the two-instances-one-machine bug).
        LanIdentity.Create(null).PeerId.ShouldNotBe(LanIdentity.Create(null).PeerId);
    }

    [Fact]
    public void Create_MintsStableNodeId_ThenReloadsTheSameOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LAN.Lib.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "node-id.txt");
        try
        {
            var first = LanIdentity.Create(path);
            first.NodeId.ShouldNotBeNullOrEmpty();
            File.Exists(path).ShouldBeTrue();

            var second = LanIdentity.Create(path);

            // Node id is stable across "restarts"...
            second.NodeId.ShouldBe(first.NodeId);
            // ...but the per-process peer id is minted fresh each time.
            second.PeerId.ShouldNotBe(first.PeerId);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
