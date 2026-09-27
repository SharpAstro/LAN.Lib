using System;
using System.Threading;
using LAN.Lib;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace LAN.Lib.Tests;

public class LanInvitesTests
{
    private static readonly TimeSpan Lapse = TimeSpan.FromSeconds(10);

    [Fact]
    public void AnOfferWaitsAndASecondIsRefusedWhileItDoes()
    {
        var invites = new LanInvites<int>(new FakeTimeProvider(), Lapse);

        invites.TryOffer("Laptop", 1, out var first).ShouldBeTrue();
        invites.Pending.ShouldBe(first);
        invites.OutcomeOf(first.Id).ShouldBe(LanInviteOutcome.Pending);

        invites.TryOffer("Tablet", 2, out var second).ShouldBeFalse("a person answers one question at a time");
        second.ShouldBeNull();
        invites.Pending.ShouldBe(first);
    }

    [Theory]
    [InlineData(true, LanInviteOutcome.Accepted)]
    [InlineData(false, LanInviteOutcome.Declined)]
    public void AnAnswerSettlesTheInviteAndFreesTheSlot(bool accept, LanInviteOutcome outcome)
    {
        var invites = new LanInvites<string>(new FakeTimeProvider(), Lapse);
        invites.TryOffer("Laptop", "payload", out var invite).ShouldBeTrue();

        invites.Answer(invite.Id, accept).ShouldBe(invite, "the answer hands back what the application needs to act on");

        invites.Pending.ShouldBeNull();
        invites.OutcomeOf(invite.Id).ShouldBe(outcome, "an asker that polls learns the answer by id");
        invites.TryOffer("Tablet", "next", out _).ShouldBeTrue();
    }

    [Fact]
    public void AnAnswerToAnInviteNoLongerWaitingAnswersNothing()
    {
        var invites = new LanInvites<int>(new FakeTimeProvider(), Lapse);
        invites.TryOffer("Laptop", 1, out var first).ShouldBeTrue();
        invites.Withdraw(first.Id).ShouldBeTrue();
        invites.TryOffer("Tablet", 2, out var second).ShouldBeTrue();

        invites.Answer(first.Id, accept: true).ShouldBeNull("an answer meant for the laptop must not accept the tablet");

        invites.Pending.ShouldBe(second);
        invites.OutcomeOf(first.Id).ShouldBe(LanInviteOutcome.Withdrawn);
        invites.Answer(second.Id, accept: true).ShouldBe(second);
        invites.Answer(second.Id, accept: false).ShouldBeNull("an invite is answered once");
        invites.OutcomeOf(second.Id).ShouldBe(LanInviteOutcome.Accepted);
    }

    [Fact]
    public void AnInviteWhoseAskerIsNotSeenLapsesAndCannotBeAcceptedAfter()
    {
        var time = new FakeTimeProvider();
        var invites = new LanInvites<int>(time, Lapse);
        invites.TryOffer("Laptop", 1, out var invite).ShouldBeTrue();

        time.Advance(Lapse - TimeSpan.FromSeconds(1));
        invites.Seen(invite.Id).ShouldBeTrue();
        time.Advance(Lapse - TimeSpan.FromSeconds(1));
        invites.Pending.ShouldBe(invite, "a poll within the lapse keeps it waiting");

        time.Advance(TimeSpan.FromSeconds(2));
        invites.Pending.ShouldBeNull("an asker that walked away must not be granted anything");
        invites.OutcomeOf(invite.Id).ShouldBe(LanInviteOutcome.Withdrawn);
        invites.Seen(invite.Id).ShouldBeFalse();
        invites.Answer(invite.Id, accept: true).ShouldBeNull();
        invites.TryOffer("Tablet", 2, out _).ShouldBeTrue("a lapsed invite frees the slot");
    }

    [Fact]
    public void AnInfiniteLapseLeavesTheWithdrawalToTheApplication()
    {
        var time = new FakeTimeProvider();
        var invites = new LanInvites<int>(time, Timeout.InfiniteTimeSpan);
        invites.TryOffer("Opponent", 1, out var invite).ShouldBeTrue();

        time.Advance(TimeSpan.FromDays(3));

        invites.Pending.ShouldBe(invite, "chess withdraws an invite when its socket closes, not by the clock");
    }

    [Fact]
    public void AnOutcomeIsForgottenAfterItsRetention()
    {
        var time = new FakeTimeProvider();
        var invites = new LanInvites<int>(time, Lapse);
        invites.TryOffer("Laptop", 1, out var invite).ShouldBeTrue();
        invites.Answer(invite.Id, accept: false);

        time.Advance(LanInvites<int>.OutcomeRetention - TimeSpan.FromSeconds(1));
        invites.OutcomeOf(invite.Id).ShouldBe(LanInviteOutcome.Declined);
        time.Advance(TimeSpan.FromSeconds(2));
        invites.OutcomeOf(invite.Id).ShouldBe(LanInviteOutcome.Unknown);
        invites.OutcomeOf("never-offered").ShouldBe(LanInviteOutcome.Unknown);
    }

    [Fact]
    public void ChangedIsRaisedWhenTheWaitingInviteChangesAndOnlyThen()
    {
        var time = new FakeTimeProvider();
        var invites = new LanInvites<int>(time, Lapse);
        var raised = 0;
        invites.Changed += () => raised++;

        invites.TryOffer("Laptop", 1, out var first).ShouldBeTrue();
        raised.ShouldBe(1);
        invites.TryOffer("Tablet", 2, out _).ShouldBeFalse();
        invites.Seen(first.Id).ShouldBeTrue();
        invites.Answer("someone-else", accept: true).ShouldBeNull();
        raised.ShouldBe(1, "a refused offer, a poll and a stale answer change nothing waiting");

        invites.Answer(first.Id, accept: true).ShouldNotBeNull();
        raised.ShouldBe(2);

        invites.TryOffer("Tablet", 2, out _).ShouldBeTrue();
        raised.ShouldBe(3);
        time.Advance(Lapse + TimeSpan.FromSeconds(1));
        invites.Sweep();
        raised.ShouldBe(4, "a sweep says when a lapse withdrew the invite");
        invites.Sweep();
        raised.ShouldBe(4);
    }

    /// <summary>A clock that runs something once, the next time it is read: the moment an update has read the state and
    /// not yet written it, which is where two writers race.</summary>
    private sealed class InterleavingTime(FakeTimeProvider inner) : TimeProvider
    {
        private Action? _once;

        public void Once(Action action) => _once = action;

        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Exchange(ref _once, null)?.Invoke();
            return inner.GetUtcNow();
        }
    }

    [Fact]
    public void AnOfferThatLosesTheRaceForTheSlotIsRefusedNotWrittenOverTheWinner()
    {
        var time = new InterleavingTime(new FakeTimeProvider());
        var invites = new LanInvites<int>(time, Lapse);
        LanInvite<int>? tablet = null;
        time.Once(() => invites.TryOffer("Tablet", 2, out tablet).ShouldBeTrue());

        invites.TryOffer("Laptop", 1, out var laptop).ShouldBeFalse("the tablet's offer landed between the laptop's read and its write");

        laptop.ShouldBeNull();
        invites.Pending.ShouldNotBeNull().ShouldBe(tablet);
    }
}
