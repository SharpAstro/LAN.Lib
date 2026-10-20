using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LAN.Lib;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

public sealed class LanGrantFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lgf-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task AMissingFileIsNoGrants()
    {
        var file = new LanGrantFile(Path.Combine(_folder, "grants.json"));

        (await file.LoadAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GrantsSavedAreTheGrantsLoadedAndATokenStillVerifiesAfterAReload()
    {
        var path = Path.Combine(_folder, "nested", "grants.json");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero));
        using var grants = new LanGrants(new LanGrantFile(path), time);
        var issued = await grants.GrantAsync("desktop/pilot-gui", CancellationToken.None);

        File.Exists(path).ShouldBeTrue();
        File.Exists(path + ".tmp").ShouldBeFalse();
        File.ReadAllText(path).ShouldNotContain(issued.Token);

        using var reloaded = new LanGrants(new LanGrantFile(path), time);
        await reloaded.LoadAsync(CancellationToken.None);
        reloaded.All.ShouldHaveSingleItem().ShouldBe(issued.Grant);
        reloaded.TryVerify(issued.Token, out var grant).ShouldBeTrue();
        grant.ShouldBe(issued.Grant);
    }

    [Fact]
    public async Task ReadsTheFileTheNodesAlreadyWrote()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "grants.json");
        // As pilot's FileGrantStore and tianwen's NodeAccessJsonContext (camelCase, indented) wrote it.
        File.WriteAllText(path, """
            [
              {
                "id": "0192f0c4a1b27c3e8d9f00112233aabb",
                "label": "laptop/pilot-gui",
                "grantedAt": "2026-10-19T08:15:00+00:00",
                "tokenHash": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
              }
            ]
            """);

        var grant = (await new LanGrantFile(path).LoadAsync(CancellationToken.None)).ShouldHaveSingleItem();

        grant.ShouldBe(new LanGrant("0192f0c4a1b27c3e8d9f00112233aabb", "laptop/pilot-gui",
            new DateTimeOffset(2026, 10, 19, 8, 15, 0, TimeSpan.Zero), "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"));
    }
}
