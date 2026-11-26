namespace DeadlockVoiceWorker;

internal sealed class DraftSchedule
{
    internal const int BudgetMs = 1400;
    private readonly Queue<double> recent = new(3);
    public bool Paused { get; private set; }

    public void BeginUtterance(bool queueEmpty)
    {
        if (!Paused || !queueEmpty) return;
        recent.Clear();
        Paused = false;
    }

    public void Observe(double elapsedMs)
    {
        if (recent.Count == 3) recent.Dequeue();
        recent.Enqueue(elapsedMs);
        Paused = recent.Count >= 2 && recent.TakeLast(2).All(ms => ms >= BudgetMs);
    }
}
