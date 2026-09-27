using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LAN.Lib;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

public class LanGrantsTests
{
    /// <summary>A store in memory that remembers each save, and can be told to refuse the next one.</summary>
    private sealed class MemoryStore : ILanGrantStore
    {
        public IReadOnlyList<LanGrant> Saved { get; private set; } = [];

        public int Saves { get; private set; }

        public bool RefuseNext { get; set; }

        public ValueTask<IReadOnlyList<LanGrant>> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Saved);

        public async ValueTask SaveAsync(IReadOnlyList<LanGrant> grants, CancellationToken cancellationToken)
        {
            // A save that yields, so two racing writes interleave if nothing orders them.
            await Task.Yield();
            if (RefuseNext)
            {
                RefuseNext = false;
                throw new System.IO.IOException("The disk is full");
            }
            Saved = [.. grants];
            Saves++;
        }
    }

    [Fact]
    public async Task AGrantsTokenVerifiesAndNothingElseDoes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var grants = new LanGrants(new MemoryStore(), new FakeTimeProvider());

        var issued = await grants.GrantAsync("Laptop", ct);

        grants.TryVerify(issued.Token, out var grant).ShouldBeTrue();
        grant.ShouldBe(issued.Grant);
        grant.Label.ShouldBe("Laptop");
        grants.TryVerify(issued.Token + "x", out _).ShouldBeFalse();
        grants.TryVerify("", out _).ShouldBeFalse();
        grants.TryVerify(null, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task TheStoreHoldsOnlyTheTokensHash()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryStore();
        using var grants = new LanGrants(store, new FakeTimeProvider());

        var issued = await grants.GrantAsync("Laptop", ct);

        var saved = store.Saved.ShouldHaveSingleItem();
        saved.TokenHash.ShouldNotContain(issued.Token, Case.Sensitive, "a copy of the store must grant nothing");
        saved.TokenHash.Length.ShouldBe(64);
        issued.Token.Length.ShouldBeGreaterThanOrEqualTo(43, "256 random bits");
    }

    [Fact]
    public async Task AGrantIsRememberedAcrossALoad()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryStore();
        LanGrantIssued issued;
        using (var before = new LanGrants(store, new FakeTimeProvider()))
        {
            issued = await before.GrantAsync("Laptop", ct);
        }

        using var after = new LanGrants(store, new FakeTimeProvider());
        after.TryVerify(issued.Token, out _).ShouldBeFalse("nothing is known before the store is read");
        await after.LoadAsync(ct);

        after.TryVerify(issued.Token, out var grant).ShouldBeTrue("a grant is remembered until revoked");
        grant.Id.ShouldBe(issued.Grant.Id);
    }

    [Fact]
    public async Task ARevokedTokenIsRefusedAndTheRevokeIsSaved()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryStore();
        using var grants = new LanGrants(store, new FakeTimeProvider());
        var laptop = await grants.GrantAsync("Laptop", ct);
        var tablet = await grants.GrantAsync("Tablet", ct);

        (await grants.RevokeAsync(laptop.Grant.Id, ct)).ShouldBeTrue();
        (await grants.RevokeAsync(laptop.Grant.Id, ct)).ShouldBeFalse("there is nothing left to revoke");

        grants.TryVerify(laptop.Token, out _).ShouldBeFalse();
        grants.TryVerify(tablet.Token, out _).ShouldBeTrue();
        store.Saved.Select(grant => grant.Id).ShouldBe([tablet.Grant.Id]);
        store.Saves.ShouldBe(3, "a revoke of nothing saves nothing");
    }

    [Fact]
    public async Task AWriteTheStoreRefusedDidNotHappen()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryStore();
        using var grants = new LanGrants(store, new FakeTimeProvider());
        var laptop = await grants.GrantAsync("Laptop", ct);

        store.RefuseNext = true;
        await Should.ThrowAsync<System.IO.IOException>(async () => await grants.GrantAsync("Tablet", ct));
        grants.All.ShouldBe([laptop.Grant], "a grant the store refused must not verify");

        store.RefuseNext = true;
        await Should.ThrowAsync<System.IO.IOException>(async () => await grants.RevokeAsync(laptop.Grant.Id, ct));
        grants.TryVerify(laptop.Token, out _).ShouldBeTrue("a revoke the store refused must not look done");
    }

    [Fact]
    public async Task RacingGrantsAreAllSaved()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryStore();
        using var grants = new LanGrants(store, new FakeTimeProvider());

        var issued = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => grants.GrantAsync($"Peer {i}", ct).AsTask()));

        store.Saved.Count.ShouldBe(12, "two racing writes must never save their snapshots in the wrong order and lose one");
        foreach (var each in issued)
        {
            grants.TryVerify(each.Token, out _).ShouldBeTrue();
        }
    }
}
