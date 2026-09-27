using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LAN.Lib;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

public class LanHostNamesTests
{
    private static readonly IPAddress Laptop = IPAddress.Parse("192.168.1.23");

    /// <summary>A resolver answering from tables, counting its lookups, and able to hang until released.</summary>
    private sealed class TableResolver : IHostNameResolver
    {
        public Dictionary<IPAddress, string> Reverse { get; } = [];

        public Dictionary<string, IPAddress[]> Forward { get; } = new Dictionary<string, IPAddress[]>(StringComparer.OrdinalIgnoreCase);

        public int Lookups { get; private set; }

        public TaskCompletionSource? Hang { get; set; }

        public async Task<string?> ReverseAsync(IPAddress address, CancellationToken cancellationToken)
        {
            Lookups++;
            if (Hang is { } hang)
            {
                // Ignores its token on purpose: the budget must hold for a resolver that does.
                await hang.Task;
            }
            return Reverse.TryGetValue(address, out var name) ? name : null;
        }

        public Task<IReadOnlyList<IPAddress>> ForwardAsync(string hostName, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(Forward.TryGetValue(hostName, out var addresses) ? addresses : []);
    }

    private sealed class BrokenResolver : IHostNameResolver
    {
        public Task<string?> ReverseAsync(IPAddress address, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A resolver with a bug of its own");

        public Task<IReadOnlyList<IPAddress>> ForwardAsync(string hostName, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A resolver with a bug of its own");
    }

    [Fact(Timeout = 30_000)]
    public async Task AResolverThatFailsUnexpectedlyGivesNoNameRatherThanLeavingItsCallersWaiting()
    {
        var names = new LanHostNames(new BrokenResolver(), new FakeTimeProvider());

        (await names.ConfirmedNameOfAsync(Laptop, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ANameThatResolvesBackToTheAddressIsItsName()
    {
        var resolver = new TableResolver();
        resolver.Reverse[Laptop] = "Laptop.LAN.";
        resolver.Forward["laptop.lan"] = [IPAddress.Parse("10.0.0.5"), Laptop];
        var names = new LanHostNames(resolver, new FakeTimeProvider());

        (await names.ConfirmedNameOfAsync(Laptop, TestContext.Current.CancellationToken)).ShouldBe("laptop.lan", "lower case, no trailing dot");
    }

    [Fact]
    public async Task ANameThatDoesNotResolveBackIsNoName()
    {
        var resolver = new TableResolver();
        resolver.Reverse[Laptop] = "nas.lan";
        resolver.Forward["nas.lan"] = [IPAddress.Parse("192.168.1.2")];
        var names = new LanHostNames(resolver, new FakeTimeProvider());

        (await names.ConfirmedNameOfAsync(Laptop, TestContext.Current.CancellationToken)).ShouldBeNull("a PTR record alone is whoever runs the zone's claim");
        (await names.ConfirmedNameOfAsync(IPAddress.Parse("192.168.1.99"), TestContext.Current.CancellationToken)).ShouldBeNull("no PTR, no name");
    }

    [Fact]
    public async Task AnIPv4AddressSeenThroughAnIPv6SocketIsTheIPv4Address()
    {
        var resolver = new TableResolver();
        resolver.Reverse[Laptop] = "laptop.lan";
        resolver.Forward["laptop.lan"] = [Laptop.MapToIPv6()];
        var names = new LanHostNames(resolver, new FakeTimeProvider());

        (await names.ConfirmedNameOfAsync(Laptop.MapToIPv6(), TestContext.Current.CancellationToken)).ShouldBe("laptop.lan");
    }

    [Fact]
    public async Task AnAnswerIsReusedForItsLifetimeAMissIncludedAndThenLookedUpAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var resolver = new TableResolver();
        var names = new LanHostNames(resolver, time);

        (await names.ConfirmedNameOfAsync(Laptop, ct)).ShouldBeNull();
        resolver.Reverse[Laptop] = "laptop.lan";
        resolver.Forward["laptop.lan"] = [Laptop];
        (await names.ConfirmedNameOfAsync(Laptop, ct)).ShouldBeNull("a polling app costs one lookup, a miss included");
        resolver.Lookups.ShouldBe(1);

        time.Advance(LanHostNames.CacheLifetime + TimeSpan.FromSeconds(1));
        (await names.ConfirmedNameOfAsync(Laptop, ct)).ShouldBe("laptop.lan");
        resolver.Lookups.ShouldBe(2);
    }

    [Fact(Timeout = 30_000)]
    public async Task AResolverThatNeverAnswersGivesNoNameOnceItsBudgetIsSpent()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var resolver = new TableResolver { Hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        resolver.Reverse[Laptop] = "laptop.lan";
        var names = new LanHostNames(resolver, time);

        var asked = names.ConfirmedNameOfAsync(Laptop, ct);
        asked.IsCompleted.ShouldBeFalse();
        time.Advance(LanHostNames.LookupBudget + TimeSpan.FromMilliseconds(1));

        (await asked).ShouldBeNull();
        resolver.Hang.SetResult();
    }

    [Fact(Timeout = 30_000)]
    public async Task CallersRacingForOneAddressWaitOnOneLookup()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new TableResolver { Hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        resolver.Reverse[Laptop] = "laptop.lan";
        resolver.Forward["laptop.lan"] = [Laptop];
        var names = new LanHostNames(resolver, new FakeTimeProvider());

        var first = names.ConfirmedNameOfAsync(Laptop, ct);
        var second = names.ConfirmedNameOfAsync(Laptop, ct);
        resolver.Hang.SetResult();

        (await first).ShouldBe("laptop.lan");
        (await second).ShouldBe("laptop.lan");
        resolver.Lookups.ShouldBe(1);
    }

    [Fact(Timeout = 30_000)]
    public async Task ACallerThatGivesUpLeavesTheAnswerForTheNext()
    {
        var resolver = new TableResolver { Hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        resolver.Reverse[Laptop] = "laptop.lan";
        resolver.Forward["laptop.lan"] = [Laptop];
        var names = new LanHostNames(resolver, new FakeTimeProvider());

        using (var impatient = new CancellationTokenSource())
        {
            var asked = names.ConfirmedNameOfAsync(Laptop, impatient.Token);
            await impatient.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(asked);
        }
        resolver.Hang.SetResult();

        (await names.ConfirmedNameOfAsync(Laptop, TestContext.Current.CancellationToken)).ShouldBe("laptop.lan");
        resolver.Lookups.ShouldBe(1, "the lookup ran on its own budget, not the caller's token");
    }
}
