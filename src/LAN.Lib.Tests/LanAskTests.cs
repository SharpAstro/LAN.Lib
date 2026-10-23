using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LAN.Lib;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

public sealed class LanAskTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lask-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
    private readonly LanHeldTokenFile _store;

    public LanAskTests() => _store = new LanHeldTokenFile(Path.Combine(_folder, "client-grants.json"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private Task<LanAskResult> Ask(IEnumerator<LanAskReading> readings, Func<CancellationToken, Task<LanAskOffer>>? offer = null, Action? asked = null, List<string>? ids = null)
    {
        return LanAsk.RunAsync(
            "Node.Local:1",
            offer ?? (_ => Task.FromResult(new LanAskOffer("ticket-1"))),
            (id, _) =>
            {
                ids?.Add(id);
                readings.MoveNext();
                return Task.FromResult(readings.Current);
            },
            _store, _time, asked, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static IEnumerator<LanAskReading> Sequence(params LanAskReading[] readings) => ((IEnumerable<LanAskReading>)readings).GetEnumerator();

    /// <summary>Lets the loop reach its next wait, then moves the clock by one interval.</summary>
    private async Task Tick()
    {
        await Task.Yield();
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        _time.Advance(LanAsk.DefaultInterval);
    }

    [Fact]
    public async Task APendingTicketIsReadAgain_AndTheFirstAcceptedReadKeepsTheToken()
    {
        var ids = new List<string>();
        var asked = 0;
        var run = Ask(Sequence(new(LanInviteOutcome.Pending), new(LanInviteOutcome.Accepted, null), new(LanInviteOutcome.Accepted, "tok")), asked: () => asked++, ids: ids);

        for (var i = 0; i < 3 && !run.IsCompleted; i++)
        {
            await Tick();
        }
        var result = await run;

        result.Outcome.ShouldBe(LanAskOutcome.Accepted);
        asked.ShouldBe(1);
        ids.ShouldBe(["ticket-1", "ticket-1", "ticket-1"]);
        _store.TokenFor("node.local:1").ShouldBe("tok");
    }

    [Theory]
    [InlineData(LanInviteOutcome.Declined, LanAskOutcome.Declined)]
    [InlineData(LanInviteOutcome.Withdrawn, LanAskOutcome.Lapsed)]
    [InlineData(LanInviteOutcome.Unknown, LanAskOutcome.Lapsed)]
    public async Task AnAnswerThatIsNotAnAcceptanceEndsTheAskAndKeepsNothing(LanInviteOutcome read, LanAskOutcome expected)
    {
        var run = Ask(Sequence(new LanAskReading(read, "ignored")));

        await Tick();
        var result = await run;

        result.Outcome.ShouldBe(expected);
        _store.Endpoints.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoAnswerWithinTheTimeoutTimesOut()
    {
        var reads = 0;
        var run = LanAsk.RunAsync(
            "n:1",
            _ => Task.FromResult(new LanAskOffer("t")),
            (_, _) =>
            {
                reads++;
                return Task.FromResult(new LanAskReading(LanInviteOutcome.Pending));
            },
            _store, _time, interval: TimeSpan.FromSeconds(10), timeout: TimeSpan.FromSeconds(30),
            cancellationToken: TestContext.Current.CancellationToken);

        for (var i = 0; i < 4 && !run.IsCompleted; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromSeconds(10));
        }
        var result = await run;

        result.Outcome.ShouldBe(LanAskOutcome.TimedOut);
        reads.ShouldBe(3);
        _store.Endpoints.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefusedOfferEndsAtOnceWithItsReason_AndNeverTellsTheNodeIsAsked()
    {
        var asked = false;
        var result = await Ask(Sequence(), _ => Task.FromResult(new LanAskOffer(null, "busy")), () => asked = true);

        result.ShouldBe(new LanAskResult(LanAskOutcome.Refused, "busy"));
        asked.ShouldBeFalse();
    }

    [Fact]
    public async Task CancellingStopsTheWait()
    {
        using var cts = new CancellationTokenSource();
        var run = LanAsk.RunAsync("n:1", _ => Task.FromResult(new LanAskOffer("t")),
            (_, _) => Task.FromResult(new LanAskReading(LanInviteOutcome.Pending)), _store, _time, cancellationToken: cts.Token);

        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => run);
    }
}
