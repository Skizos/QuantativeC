using QuantAnalyst.Core;
using QuantAnalyst.Trading.Observation;
using QuantAnalyst.Trading.Oms;

namespace QuantAnalyst.Trading.Tests;

/// <summary>Watching a session can't change it (docs/plans/11-app-redesign.md).</summary>
public sealed class ObservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AGuardedObserver_SwallowsWhateverItsObserverThrows()
    {
        var throwing = new Throwing();
        ISessionObserver guarded = GuardedObserver.Wrap(throwing)!;
        guarded.Started(new SessionStarted(Now, null, null, null, "test", []));
        guarded.Quote(new QuoteTick(Now, new OrderbookId("5240"), 70m, 71m, 70.5m));
        guarded.Account(new AccountTick(Now, 5000m, 5000m, 5000m, 0m, []));
        guarded.Order(new OrderTick(Now, Guid.NewGuid(), new OrderbookId("5240"), "ERIC B", OrderSide.Buy, 1, 70m, OmsState.Working, 0, null, Now));
        guarded.Decision(new DecisionTick(Now, 0, []));
        Assert.Equal(5, throwing.Calls);
    }

    [Fact]
    public void Wrapping_KeepsNoneAsNone_AndDoesNotWrapTwice()
    {
        Assert.Null(GuardedObserver.Wrap(null));
        ISessionObserver once = GuardedObserver.Wrap(new Throwing())!;
        Assert.IsType<GuardedObserver>(once);
        Assert.Same(once, GuardedObserver.Wrap(once));
    }

    private sealed class Throwing : ISessionObserver
    {
        public int Calls { get; private set; }

        public void Started(SessionStarted e) => Fail();

        public void Quote(QuoteTick e) => Fail();

        public void Account(AccountTick e) => Fail();

        public void Order(OrderTick e) => Fail();

        public void Decision(DecisionTick e) => Fail();

        private void Fail()
        {
            Calls++;
            throw new InvalidOperationException("broken observer");
        }
    }
}
