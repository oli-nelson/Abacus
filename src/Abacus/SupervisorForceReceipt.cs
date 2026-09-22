namespace Abacus;

/// <summary>One operator force request, completed by its owning supervisor check.
/// Does not carry the potentially sensitive prompt into runtime publications.</summary>
internal sealed class SupervisorForceReceipt
{
    private readonly TaskCompletionSource<AgentControlOutcome> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<AgentControlOutcome> Completion => completion.Task;
    internal void Finish(string outcome) => completion.TrySetResult(new(outcome));
}
