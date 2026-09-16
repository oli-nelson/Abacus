namespace Abacus;

/// <summary>One attempt per observed empty-backlog transition in this controller run.</summary>
public sealed class ContinuationState
{
    private int armed = 1;

    public bool IsArmed() => Volatile.Read(ref armed) != 0;
    public void ObserveUnfinished() => Rearm();
    public bool TryConsume() => Interlocked.Exchange(ref armed, 0) != 0;
    public void Rearm() => Interlocked.Exchange(ref armed, 1);
}
