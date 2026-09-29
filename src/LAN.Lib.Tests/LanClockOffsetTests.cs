using System;
using LAN.Lib;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

public class LanClockOffsetTests
{
    private static readonly DateTimeOffset Local = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    // Whole ticks: AddSeconds(double) truncates 600.005 s to a tick short.
    private static DateTimeOffset At(double seconds) => Local.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    [Fact]
    public void BeforeAnySampleThereIsNoOffsetAndATimeIsUnchanged()
    {
        var clock = new LanClockOffset();

        clock.HasSample.ShouldBeFalse();
        clock.Offset.ShouldBe(TimeSpan.Zero);
        clock.RoundTrip.ShouldBe(TimeSpan.Zero);
        clock.ToLocal(Local).ShouldBe(Local);
        clock.ToPeer(Local).ShouldBe(Local);
    }

    [Fact]
    public void ThePeerReadItsClockAtTheMidpointOfTheRoundTrip()
    {
        // Asked at 10:00:00.000, answered at 10:00:00.100: the peer read its clock at about 10:00:00.050 here, and read
        // 10:10:00.050 there, so it is ten minutes ahead.
        var clock = new LanClockOffset();

        clock.Observe(At(0), At(600.05), At(0.1));

        clock.HasSample.ShouldBeTrue();
        clock.Offset.ShouldBe(TimeSpan.FromMinutes(10));
        clock.RoundTrip.ShouldBe(TimeSpan.FromMilliseconds(100));
        clock.ToLocal(At(600)).ShouldBe(At(0), "what the peer calls 10:10 is 10:00 here");
        clock.ToPeer(At(0)).ShouldBe(At(600));
    }

    [Fact]
    public void APeerBehindThisComputerHasANegativeOffset()
    {
        var clock = new LanClockOffset();

        clock.Observe(At(0), At(-90), At(0));

        clock.Offset.ShouldBe(TimeSpan.FromSeconds(-90));
        clock.ToLocal(At(-90)).ShouldBe(At(0));
    }

    [Fact]
    public void TheShortestRoundTripInTheWindowIsTheEstimate()
    {
        // A slow answer says as much about the network as about the clock: a 2 s round trip that reads a second off does
        // not displace a 10 ms one.
        var clock = new LanClockOffset();
        clock.Observe(At(0), At(600.005), At(0.01));

        clock.Observe(At(10), At(612), At(12));

        clock.Offset.ShouldBe(TimeSpan.FromMinutes(10));
        clock.RoundTrip.ShouldBe(TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public void ASampleThatHasLeftTheWindowNoLongerCounts()
    {
        // The peer's clock stepped ten seconds forward. Once the window holds only samples taken after the step, the
        // estimate has followed it, even though the old sample had the shorter round trip.
        var clock = new LanClockOffset(window: 3);
        clock.Observe(At(0), At(600.0005), At(0.001));

        clock.Observe(At(1), At(611.05), At(1.1));
        clock.Observe(At(2), At(612.05), At(2.1));
        clock.Offset.ShouldBe(TimeSpan.FromMinutes(10), "the old sample is still in the window");

        clock.Observe(At(3), At(613.05), At(3.1));
        clock.Offset.ShouldBe(TimeSpan.FromSeconds(610), "and now it is not");
    }

    [Fact]
    public void OnATieTheNewerSampleWins()
    {
        var clock = new LanClockOffset();
        clock.Observe(At(0), At(600), At(0));

        clock.Observe(At(1), At(602), At(1));

        clock.Offset.ShouldBe(TimeSpan.FromSeconds(601));
    }

    [Fact]
    public void AnAnswerThatArrivedBeforeItWasAskedForSaysNothing()
    {
        var clock = new LanClockOffset();

        clock.Observe(At(5), At(600), At(4));

        clock.HasSample.ShouldBeFalse();
        clock.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(-600)]
    [InlineData(600)]
    public void ANeverAndAnEndlessTimeAreNotShiftedWhicheverWayTheClocksDiffer(int peerAheadBySeconds)
    {
        // A camera that has not started an exposure reports MinValue. MinValue plus a negative span throws, which killed
        // the render thread of every window that attached to a running session (found live, 2026-09-29).
        var clock = new LanClockOffset();
        clock.Observe(At(0), At(peerAheadBySeconds), At(0));

        clock.ToLocal(DateTimeOffset.MinValue).ShouldBe(DateTimeOffset.MinValue);
        clock.ToLocal(default).ShouldBe(default);
        clock.ToLocal(DateTimeOffset.MaxValue).ShouldBe(DateTimeOffset.MaxValue);
        clock.ToPeer(DateTimeOffset.MinValue).ShouldBe(DateTimeOffset.MinValue);
        clock.ToPeer(DateTimeOffset.MaxValue).ShouldBe(DateTimeOffset.MaxValue);
    }

    [Fact]
    public void ATimeNextToTheEdgeOfTheCalendarIsClampedNeverThrown()
    {
        var clock = new LanClockOffset();
        clock.Observe(At(0), At(3600), At(0));

        clock.ToLocal(DateTimeOffset.MinValue.AddSeconds(1)).ShouldBe(DateTimeOffset.MinValue);
        clock.ToPeer(DateTimeOffset.MaxValue.AddSeconds(-1)).ShouldBe(DateTimeOffset.MaxValue);
    }

    [Fact]
    public void AWindowOfLessThanOneIsRefused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new LanClockOffset(window: 0));
    }
}
