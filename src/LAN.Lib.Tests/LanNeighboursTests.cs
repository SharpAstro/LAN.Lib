using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using LAN.Lib;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

public class LanNeighboursTests
{
    // A Sky-Watcher dongle in access-point mode: the mount's own network, and this machine on it.
    private static readonly IPAddress Dongle = IPAddress.Parse("192.168.4.1");
    private static readonly IPAddress Me = IPAddress.Parse("192.168.4.2");
    private static readonly PhysicalAddress DongleMac = PhysicalAddress.Parse("7C-DF-A1-00-11-22");
    private static readonly PhysicalAddress RouterMac = PhysicalAddress.Parse("AA-BB-CC-DD-EE-FF");

    /// <summary>A table answering from rows, recording what it was asked, and able to hang (ignoring its token) or to wait on it.</summary>
    private sealed class FakeTable : ILanNeighbourTable
    {
        public Dictionary<IPAddress, LanNeighbourEntry> Rows { get; } = [];

        public List<IPAddress> Asked { get; } = [];

        public CancellationToken LastToken { get; private set; }

        /// <summary>When set, the table does not answer until released, and does not look at its token.</summary>
        public TaskCompletionSource? Hang { get; set; }

        /// <summary>When set, the table waits forever but ends when its token does, as a well-behaved one would.</summary>
        public bool Cooperative { get; set; }

        public Exception? Throws { get; set; }

        public async Task<LanNeighbourEntry?> FindAsync(IPAddress address, CancellationToken cancellationToken)
        {
            Asked.Add(address);
            LastToken = cancellationToken;
            if (Throws is { } ex)
            {
                throw ex;
            }
            if (Hang is { } hang)
            {
                await hang.Task;
            }
            if (Cooperative)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return Rows.TryGetValue(address, out var row) ? row : null;
        }

        public FakeTable Hold(IPAddress address, PhysicalAddress mac, LanNeighbourState state = LanNeighbourState.Reachable)
        {
            Rows[address] = new LanNeighbourEntry(address, mac, state, "wlan0");
            return this;
        }
    }

    /// <summary>Links answering from subnets (address and prefix length), recording what it was asked.</summary>
    private sealed class FakeLinks(params (string Address, int PrefixLength)[] subnets) : ILanLinks
    {
        public List<IPAddress> Asked { get; } = [];

        public bool IsOnLink(IPAddress address)
        {
            Asked.Add(address);
            foreach (var (network, prefixLength) in subnets)
            {
                if (NetworkInterfaceLinks.InSubnet(address, IPAddress.Parse(network), prefixLength))
                {
                    return true;
                }
            }
            return false;
        }
    }

    private static FakeLinks Wifi() => new FakeLinks(("192.168.4.2", 24), ("fd00::2", 64), ("fe80::abcd", 64));

    private static LanNeighbours Lookup(FakeTable table, FakeLinks? links = null, TimeProvider? time = null) =>
        new LanNeighbours(table, links ?? Wifi(), time ?? new FakeTimeProvider());

    [Fact]
    public async Task TheMacBehindAnAddressOnTheLinkIsTheTablesRow()
    {
        var table = new FakeTable().Hold(Dongle, DongleMac);

        (await Lookup(table).PhysicalAddressOfAsync(Dongle, TestContext.Current.CancellationToken)).ShouldBe(DongleMac);
        table.Asked.ShouldBe([Dongle]);
    }

    [Theory]
    [InlineData(LanNeighbourState.Reachable)]
    [InlineData(LanNeighbourState.Stale)]
    [InlineData(LanNeighbourState.Delay)]
    [InlineData(LanNeighbourState.Probe)]
    [InlineData(LanNeighbourState.Permanent)]
    [InlineData(LanNeighbourState.Complete)]
    public async Task AnyStateThatHoldsAnAddressNamesTheHost(LanNeighbourState state)
    {
        var table = new FakeTable().Hold(Dongle, DongleMac, state);

        (await Lookup(table).PhysicalAddressOfAsync(Dongle, TestContext.Current.CancellationToken)).ShouldBe(DongleMac);
    }

    [Theory]
    [InlineData(LanNeighbourState.Incomplete)]
    [InlineData(LanNeighbourState.Unreachable)]
    [InlineData((LanNeighbourState)99)]
    public async Task AStateThatHoldsNoAddressIsNoMacEvenWhenTheRowCarriesOne(LanNeighbourState state)
    {
        var table = new FakeTable().Hold(Dongle, DongleMac, state);

        (await Lookup(table).PhysicalAddressOfAsync(Dongle, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task AnAddressTheTableHoldsNothingForIsNoMac()
    {
        (await Lookup(new FakeTable()).PhysicalAddressOfAsync(Dongle, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Theory]
    [InlineData("00-00-00-00-00-00")]
    [InlineData("FF-FF-FF-FF-FF-FF")]
    public async Task AnAllZeroOrAllOnesMacNamesNoHost(string mac)
    {
        var table = new FakeTable().Hold(Dongle, PhysicalAddress.Parse(mac), LanNeighbourState.Reachable);

        (await Lookup(table).PhysicalAddressOfAsync(Dongle, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ARowWithNoAddressAtAllIsNoMac()
    {
        var table = new FakeTable().Hold(Dongle, PhysicalAddress.None, LanNeighbourState.Reachable);

        (await Lookup(table).PhysicalAddressOfAsync(Dongle, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task TheDirectedBroadcastAddressOfTheLinkIsNoHost()
    {
        var broadcast = IPAddress.Parse("192.168.4.255");
        var table = new FakeTable().Hold(broadcast, PhysicalAddress.Parse("FF-FF-FF-FF-FF-FF"), LanNeighbourState.Permanent);

        (await Lookup(table).PhysicalAddressOfAsync(broadcast, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("224.0.0.251")]
    [InlineData("239.255.255.250")]
    [InlineData("255.255.255.255")]
    [InlineData("240.0.0.1")]
    [InlineData("ff02::1")]
    [InlineData("ff02::fb")]
    public async Task AnAddressNoHostAnswersForIsNoMacAndTheTableIsNeverAsked(string text)
    {
        var address = IPAddress.Parse(text);
        // Even a table, and links, that would say yes: the address itself rules it out.
        var table = new FakeTable().Hold(address, DongleMac);
        var everywhere = new FakeLinks(("0.0.0.0", 0), ("::", 0));

        (await Lookup(table, everywhere).PhysicalAddressOfAsync(address, TestContext.Current.CancellationToken)).ShouldBeNull();
        table.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAddressOffEveryLocalSubnetIsNoMacNotTheRoutersAndTheTableIsNeverAsked()
    {
        // 8.8.8.8 is reached through the router, so the machine's entry for it, if it had one, would be the router's.
        var elsewhere = IPAddress.Parse("8.8.8.8");
        var table = new FakeTable().Hold(elsewhere, RouterMac);
        var sameNetworkNumberDifferentSubnet = IPAddress.Parse("192.168.5.1");
        table.Hold(sameNetworkNumberDifferentSubnet, RouterMac);
        var lookup = Lookup(table);

        (await lookup.PhysicalAddressOfAsync(elsewhere, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await lookup.PhysicalAddressOfAsync(sameNetworkNumberDifferentSubnet, TestContext.Current.CancellationToken)).ShouldBeNull();
        table.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task AMachineOnNoLinkAtAllFindsNothing()
    {
        var table = new FakeTable().Hold(Dongle, DongleMac);

        (await Lookup(table, new FakeLinks()).PhysicalAddressOfAsync(Dongle, TestContext.Current.CancellationToken)).ShouldBeNull();
        table.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIPv4AddressSeenThroughAnIPv6SocketIsTheIPv4Address()
    {
        var table = new FakeTable().Hold(Dongle, DongleMac);
        var links = Wifi();

        (await Lookup(table, links).PhysicalAddressOfAsync(Dongle.MapToIPv6(), TestContext.Current.CancellationToken)).ShouldBe(DongleMac);
        table.Asked.ShouldBe([Dongle], "the table is asked about the IPv4 address, the one it holds");
        links.Asked.ShouldBe([Dongle]);
    }

    [Fact]
    public async Task AMappedLoopbackIsStillLoopback()
    {
        var mapped = IPAddress.Loopback.MapToIPv6();
        var table = new FakeTable().Hold(IPAddress.Loopback, DongleMac);

        (await Lookup(table).PhysicalAddressOfAsync(mapped, TestContext.Current.CancellationToken)).ShouldBeNull();
        table.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIPv6AddressOnTheLinkIsLookedUpToo()
    {
        var neighbour = IPAddress.Parse("fd00::17");
        var linkLocal = IPAddress.Parse("fe80::17");
        var table = new FakeTable().Hold(neighbour, DongleMac).Hold(linkLocal, RouterMac);
        var lookup = Lookup(table);

        (await lookup.PhysicalAddressOfAsync(neighbour, TestContext.Current.CancellationToken)).ShouldBe(DongleMac);
        (await lookup.PhysicalAddressOfAsync(linkLocal, TestContext.Current.CancellationToken)).ShouldBe(RouterMac);
        (await lookup.PhysicalAddressOfAsync(IPAddress.Parse("2001:db8::1"), TestContext.Current.CancellationToken)).ShouldBeNull("off every local prefix");
    }

    [Fact]
    public async Task AnAnswerIsNeverReusedBecauseTheSameAddressCanNameAnotherDeviceLater()
    {
        var ct = TestContext.Current.CancellationToken;
        var other = PhysicalAddress.Parse("7C-DF-A1-33-44-55");
        var table = new FakeTable().Hold(Dongle, DongleMac);
        var lookup = Lookup(table);

        (await lookup.PhysicalAddressOfAsync(Dongle, ct)).ShouldBe(DongleMac);
        // The user joins the next mount's network: the same 192.168.4.1, another radio.
        table.Hold(Dongle, other);
        (await lookup.PhysicalAddressOfAsync(Dongle, ct)).ShouldBe(other);
        table.Rows.Clear();
        (await lookup.PhysicalAddressOfAsync(Dongle, ct)).ShouldBeNull("and a miss is not remembered either");
        table.Asked.Count.ShouldBe(3);
    }

    [Fact(Timeout = 30_000)]
    public async Task ATableThatNeverAnswersGivesNoMacOnceItsBudgetIsSpentEvenWhenItIgnoresItsToken()
    {
        var time = new FakeTimeProvider();
        var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var table = new FakeTable { Hang = hang }.Hold(Dongle, DongleMac);

        var asked = Lookup(table, time: time).PhysicalAddressOfAsync(Dongle, TestContext.Current.CancellationToken);
        asked.IsCompleted.ShouldBeFalse();
        time.Advance(LanNeighbours.LookupBudget - TimeSpan.FromMilliseconds(1));
        asked.IsCompleted.ShouldBeFalse("still inside the budget");
        time.Advance(TimeSpan.FromMilliseconds(2));

        (await asked).ShouldBeNull();
        hang.SetResult();
    }

    [Fact(Timeout = 30_000)]
    public async Task ATableThatHonoursItsTokenIsToldWhenTheBudgetIsSpent()
    {
        var time = new FakeTimeProvider();
        var table = new FakeTable { Cooperative = true }.Hold(Dongle, DongleMac);

        var asked = Lookup(table, time: time).PhysicalAddressOfAsync(Dongle, TestContext.Current.CancellationToken);
        table.LastToken.IsCancellationRequested.ShouldBeFalse();
        time.Advance(LanNeighbours.LookupBudget + TimeSpan.FromMilliseconds(1));

        (await asked).ShouldBeNull();
        table.LastToken.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task ACallerThatGivesUpGetsItsOwnCancellationAndTheTableIsToldToStop()
    {
        var table = new FakeTable { Cooperative = true }.Hold(Dongle, DongleMac);
        using var impatient = new CancellationTokenSource();

        var asked = Lookup(table).PhysicalAddressOfAsync(Dongle, impatient.Token);
        asked.IsCompleted.ShouldBeFalse();
        await impatient.CancelAsync();

        var thrown = await Should.ThrowAsync<OperationCanceledException>(asked);
        thrown.CancellationToken.ShouldBe(impatient.Token, "not the budget's token");
        table.LastToken.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task ATokenAlreadyCancelledAsksNothing()
    {
        var table = new FakeTable().Hold(Dongle, DongleMac);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(Lookup(table).PhysicalAddressOfAsync(Dongle, cancelled.Token));
        table.Asked.ShouldBeEmpty();
    }

    [Fact(Timeout = 30_000)]
    public async Task ATableThatFailsUnexpectedlyGivesNoMacRatherThanAnException()
    {
        var ct = TestContext.Current.CancellationToken;
        var thrown = new FakeTable { Throws = new InvalidOperationException("A table with a bug of its own") };
        var cancelledItself = new FakeTable { Throws = new OperationCanceledException() };

        (await Lookup(thrown).PhysicalAddressOfAsync(Dongle, ct)).ShouldBeNull();
        (await Lookup(cancelledItself).PhysicalAddressOfAsync(Dongle, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ATableThatReturnsAFaultedTaskGivesNoMac()
    {
        var lookup = new LanNeighbours(new FaultedTable(), Wifi(), new FakeTimeProvider());

        (await lookup.PhysicalAddressOfAsync(Dongle, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    private sealed class FaultedTable : ILanNeighbourTable
    {
        public Task<LanNeighbourEntry?> FindAsync(IPAddress address, CancellationToken cancellationToken) =>
            Task.FromException<LanNeighbourEntry?>(new InvalidOperationException("faulted"));
    }

    [Fact]
    public async Task ANullAddressIsAnArgumentErrorNotAMissingMac()
    {
        await Should.ThrowAsync<ArgumentNullException>(Lookup(new FakeTable()).PhysicalAddressOfAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnEmptyTableForAPlatformWithoutOneAnswersNothing()
    {
        var table = LanNeighbourTables.Empty;

        (await table.FindAsync(Dongle, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await new LanNeighbours(table, Wifi(), new FakeTimeProvider()).PhysicalAddressOfAsync(Dongle, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public void TheTableOfThisMachineIsWindowsOnWindowsLinuxsOnLinuxAndEmptyElsewhereAndNotReadHere()
    {
        // Only the choice is checked: asking it would read this machine's own table (the live test does).
        var table = LanNeighbourTables.ForThisMachine();

        if (OperatingSystem.IsWindows())
        {
            // Read only: a fill can answer with a proxy-ARP router's MAC for a host that is not there.
            table.ShouldBeOfType<WindowsNeighbourTable>().FillsMissing.ShouldBeFalse();
        }
        else if (OperatingSystem.IsLinux())
        {
            table.ShouldBeOfType<LinuxNeighbourTable>();
        }
        else
        {
            // macOS is here on purpose: its table is a routing-socket dump nobody has run a parse of yet, so it holds nothing.
            table.ShouldBeSameAs(LanNeighbourTables.Empty);
        }
    }

    [Fact]
    public void ARowIsUsableWhenItsStateHoldsAnAddressAndTheAddressNamesAHost()
    {
        new LanNeighbourEntry(Dongle, DongleMac, LanNeighbourState.Stale).IsUsable.ShouldBeTrue();
        new LanNeighbourEntry(Dongle, DongleMac, LanNeighbourState.Incomplete).IsUsable.ShouldBeFalse();
        new LanNeighbourEntry(Dongle, PhysicalAddress.None, LanNeighbourState.Reachable).IsUsable.ShouldBeFalse();
        new LanNeighbourEntry(Dongle, new PhysicalAddress(new byte[6]), LanNeighbourState.Reachable).IsUsable.ShouldBeFalse();
        // A longer address (InfiniBand's is 20 octets) is judged by the same two rules, not by its length.
        var twenty = new byte[20];
        new LanNeighbourEntry(Dongle, new PhysicalAddress(twenty), LanNeighbourState.Reachable).IsUsable.ShouldBeFalse();
        twenty[19] = 1;
        new LanNeighbourEntry(Dongle, new PhysicalAddress(twenty), LanNeighbourState.Reachable).IsUsable.ShouldBeTrue();
    }
}
