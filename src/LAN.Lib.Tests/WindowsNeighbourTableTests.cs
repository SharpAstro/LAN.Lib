using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using LAN.Lib;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

/// <summary>
/// The Windows table's flow (read the table, fill an IPv4 address it has no row for, never wait past the caller's token,
/// never pile fills up) over a table and a link the test answers. The native calls themselves are the live test's.
/// </summary>
public class WindowsNeighbourTableTests
{
    private static readonly IPAddress Dongle = IPAddress.Parse("192.168.4.1");
    private static readonly PhysicalAddress DongleMac = PhysicalAddress.Parse("7C-DF-A1-00-11-22");
    private static readonly PhysicalAddress RouterMac = PhysicalAddress.Parse("AA-BB-CC-DD-EE-FF");

    /// <summary>What the table is asked and the link is asked, and what each answers. The link can block like <c>SendARP</c>.</summary>
    private sealed class Fakes
    {
        public ConcurrentDictionary<IPAddress, LanNeighbourEntry> Rows { get; } = new();

        public ConcurrentQueue<IPAddress> Read { get; } = new();

        public ConcurrentQueue<IPAddress> Asked { get; } = new();

        public ConcurrentDictionary<IPAddress, PhysicalAddress> Link { get; } = new();

        /// <summary>When set, a link ask does not return until it is set, and says so first through <see cref="Started"/>.</summary>
        public ManualResetEventSlim? Hold { get; set; }

        public SemaphoreSlim Started { get; } = new(0);

        public LanNeighbourEntry? ReadTable(IPAddress address)
        {
            Read.Enqueue(address);
            return Rows.TryGetValue(address, out var row) ? row : null;
        }

        public PhysicalAddress? AskLink(IPAddress address)
        {
            Asked.Enqueue(address);
            Started.Release();
            Hold?.Wait();
            return Link.TryGetValue(address, out var mac) ? mac : null;
        }

        public WindowsNeighbourTable Table(bool fillMissing = true) => new(ReadTable, AskLink, fillMissing);

        public Fakes Learnt(IPAddress address, PhysicalAddress mac, LanNeighbourState state = LanNeighbourState.Reachable)
        {
            Rows[address] = new LanNeighbourEntry(address, mac, state, "12");
            return this;
        }

        public Task AskStarted() => Started.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ARowTheMachineAlreadyHoldsIsAnsweredWithoutAskingTheLink()
    {
        var fakes = new Fakes().Learnt(Dongle, DongleMac, LanNeighbourState.Stale);

        var entry = await fakes.Table().FindAsync(Dongle, TestContext.Current.CancellationToken);

        entry!.PhysicalAddress.ShouldBe(DongleMac);
        entry.State.ShouldBe(LanNeighbourState.Stale, "the row as it is, not a claim of freshness");
        fakes.Asked.ShouldBeEmpty("a usable row is never refreshed on the wire");
    }

    [Fact]
    public async Task AnIpv4AddressWithNoRowIsAskedForOnTheLinkAndAnsweredAsReachable()
    {
        var fakes = new Fakes();
        fakes.Link[Dongle] = DongleMac;

        var entry = await fakes.Table().FindAsync(Dongle, TestContext.Current.CancellationToken);

        entry.ShouldBe(new LanNeighbourEntry(Dongle, DongleMac, LanNeighbourState.Reachable));
        entry!.IsUsable.ShouldBeTrue();
        fakes.Asked.ToArray().ShouldBe([Dongle]);
    }

    [Theory]
    [InlineData(LanNeighbourState.Incomplete)]
    [InlineData(LanNeighbourState.Unreachable)]
    public async Task ARowThatHoldsNoAddressIsFilledAndKeepsItsInterface(LanNeighbourState state)
    {
        var fakes = new Fakes().Learnt(Dongle, PhysicalAddress.None, state);
        fakes.Link[Dongle] = DongleMac;

        var entry = await fakes.Table().FindAsync(Dongle, TestContext.Current.CancellationToken);

        entry.ShouldBe(new LanNeighbourEntry(Dongle, DongleMac, LanNeighbourState.Reachable, "12"));
    }

    [Fact]
    public async Task WhenNobodyAnswersTheLinkTheRowIsReturnedAsItIs()
    {
        var fakes = new Fakes().Learnt(Dongle, PhysicalAddress.None, LanNeighbourState.Incomplete);

        var entry = await fakes.Table().FindAsync(Dongle, TestContext.Current.CancellationToken);

        entry!.State.ShouldBe(LanNeighbourState.Incomplete);
        entry.IsUsable.ShouldBeFalse();
        fakes.Asked.Count.ShouldBe(1);
    }

    [Fact]
    public async Task WhenNobodyAnswersAndThereIsNoRowThereIsNoEntry()
    {
        var fakes = new Fakes();

        (await fakes.Table().FindAsync(Dongle, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ATableToldNotToFillNeverPutsAPacketOnTheWire()
    {
        var fakes = new Fakes();
        fakes.Link[Dongle] = DongleMac;

        var entry = await fakes.Table(fillMissing: false).FindAsync(Dongle, TestContext.Current.CancellationToken);

        entry.ShouldBeNull();
        fakes.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIpv6AddressWithNoRowIsNotFilledBecauseSendArpIsIpv4()
    {
        var fakes = new Fakes();
        var address = IPAddress.Parse("fe80::1234");

        (await fakes.Table().FindAsync(address, TestContext.Current.CancellationToken)).ShouldBeNull();

        fakes.Read.ToArray().ShouldBe([address], "the table is read for it");
        fakes.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIpv4AddressSeenThroughAnIpv6SocketIsTheIpv4Address()
    {
        var fakes = new Fakes().Learnt(Dongle, DongleMac);

        var entry = await fakes.Table().FindAsync(Dongle.MapToIPv6(), TestContext.Current.CancellationToken);

        entry!.PhysicalAddress.ShouldBe(DongleMac);
        fakes.Read.ToArray().ShouldBe([Dongle]);
    }

    [Fact]
    public async Task ACallerThatGivesUpDoesNotWaitForTheLinkAndTheSlotComesBackWhenTheCallDoes()
    {
        var fakes = new Fakes { Hold = new ManualResetEventSlim() };
        fakes.Link[Dongle] = DongleMac;
        var table = fakes.Table();
        using var caller = new CancellationTokenSource();

        var lookup = table.FindAsync(Dongle, caller.Token);
        await fakes.AskStarted();
        table.FillsInFlight.ShouldBe(1);
        await caller.CancelAsync();

        // SendARP cannot be cancelled: the caller is free at once and the call is still out, holding its slot.
        await Should.ThrowAsync<OperationCanceledException>(() => lookup);
        table.FillsInFlight.ShouldBe(1);

        fakes.Hold!.Set();
        SpinWait.SpinUntil(() => table.FillsInFlight == 0, TimeSpan.FromSeconds(30)).ShouldBeTrue("the slot comes back when the call returns");
    }

    [Fact]
    public async Task ATableWhoseFillsAreAllOutDoesWithoutTheFillRatherThanPilingThreadsUp()
    {
        var fakes = new Fakes { Hold = new ManualResetEventSlim() };
        var table = fakes.Table();
        using var caller = new CancellationTokenSource();
        var out1 = IPAddress.Parse("192.168.4.11");
        var out2 = IPAddress.Parse("192.168.4.12");
        var third = IPAddress.Parse("192.168.4.13");

        var first = table.FindAsync(out1, caller.Token);
        var second = table.FindAsync(out2, caller.Token);
        for (var i = 0; i < WindowsNeighbourTable.MaxFillsInFlight; i++)
        {
            await fakes.AskStarted();
        }
        table.FillsInFlight.ShouldBe(WindowsNeighbourTable.MaxFillsInFlight);

        var entry = await table.FindAsync(third, TestContext.Current.CancellationToken);

        entry.ShouldBeNull("no room: no fill, and the table has no row");
        fakes.Asked.ToArray().ShouldNotContain(third);
        table.FillsInFlight.ShouldBe(WindowsNeighbourTable.MaxFillsInFlight, "the refused lookup took no slot");

        fakes.Hold!.Set();
        await Task.WhenAll(first, second);
        SpinWait.SpinUntil(() => table.FillsInFlight == 0, TimeSpan.FromSeconds(30)).ShouldBeTrue();

        // With the room free again the next one is filled.
        fakes.Hold = null;
        fakes.Link[third] = DongleMac;
        (await table.FindAsync(third, TestContext.Current.CancellationToken))!.PhysicalAddress.ShouldBe(DongleMac);
    }

    [Fact]
    public async Task ALookupThroughLanNeighboursIsFilledAndGivesTheHostsMac()
    {
        var fakes = new Fakes();
        fakes.Link[Dongle] = DongleMac;
        var lookup = new LanNeighbours(fakes.Table(), OnTheDonglesLink(), new FakeTimeProvider());

        (await lookup.PhysicalAddressOfAsync(Dongle, TestContext.Current.CancellationToken)).ShouldBe(DongleMac);
    }

    [Fact]
    public async Task ALinkThatNeverAnswersGivesNoMacAtTheLookupBudgetAndNeverHoldsTheCaller()
    {
        var fakes = new Fakes { Hold = new ManualResetEventSlim() };
        var time = new FakeTimeProvider();
        var table = fakes.Table();
        var lookup = new LanNeighbours(table, OnTheDonglesLink(), time);

        var pending = lookup.PhysicalAddressOfAsync(Dongle, TestContext.Current.CancellationToken);
        await fakes.AskStarted();
        pending.IsCompleted.ShouldBeFalse();
        time.Advance(LanNeighbours.LookupBudget);

        (await pending).ShouldBeNull("the budget is the lookup's, and SendARP cannot be told to stop");
        table.FillsInFlight.ShouldBe(1);
        fakes.Hold!.Set();
        SpinWait.SpinUntil(() => table.FillsInFlight == 0, TimeSpan.FromSeconds(30)).ShouldBeTrue();
    }

    [Fact]
    public async Task ARouterRowIsNeverTheAnswerForAnAddressOffEveryLink()
    {
        // LanNeighbours keeps its own rule over this table: an address off every subnet would answer from the router's row.
        var router = IPAddress.Parse("192.168.4.254");
        var fakes = new Fakes().Learnt(router, RouterMac);
        fakes.Link[IPAddress.Parse("8.8.8.8")] = RouterMac;
        var lookup = new LanNeighbours(fakes.Table(), OnTheDonglesLink(), new FakeTimeProvider());

        (await lookup.PhysicalAddressOfAsync(IPAddress.Parse("8.8.8.8"), TestContext.Current.CancellationToken)).ShouldBeNull();

        fakes.Read.ShouldBeEmpty();
        fakes.Asked.ShouldBeEmpty("an off-link address is never put on the wire either");
    }

    [Fact]
    public async Task ATableWhoseNativeLibraryIsMissingHasNoAnswerForTheFillAndNoException()
    {
        var table = new WindowsNeighbourTable(_ => null, _ => throw new EntryPointNotFoundException(), fillMissing: true);

        (await table.FindAsync(Dongle, TestContext.Current.CancellationToken)).ShouldBeNull();
        SpinWait.SpinUntil(() => table.FillsInFlight == 0, TimeSpan.FromSeconds(30)).ShouldBeTrue();
    }

    private static NetworkInterfaceLinks OnTheDonglesLink() =>
        new(() => [(IPAddress.Parse("192.168.4.2"), 24)]);
}
