namespace Abacus;

/// <summary>
/// Keeps merge-slot ownership honest. A merge slot may only be held by an agent whose
/// harness is still running, and only such an agent may sit in the waiter queue, so a
/// slot claimed by a crashed or stopped harness would otherwise block every other agent
/// forever. Reclaiming is deliberately limited to agents configured for this run: a
/// holder or waiter Abacus does not host may belong to another machine or run.
/// </summary>
public sealed class MergeSlotReclaimer(Beads beads, TextWriter log)
{
    private string? lastFailure;

    public async Task<MergeSlotStatus> ReclaimAsync(
        string workspace,
        IReadOnlySet<string> configuredAgents,
        Func<string, bool> hasRunningHarness,
        MergeSlotStatus status,
        CancellationToken cancellationToken)
    {
        if (!status.Exists || (status.Holder is null && status.Waiters.Count == 0))
        {
            return status;
        }

        var holder = status.IsHeld ? status.Holder : null;
        var abandonedHolder = holder is not null
            && configuredAgents.Contains(holder)
            && !hasRunningHarness(holder);

        // Beads never removes waiters, so drop the current holder and every configured
        // agent whose harness is gone while keeping foreign and live waiters in order.
        var queue = new List<string>();
        var queueChanged = false;
        var droppedStaleWaiter = false;
        foreach (var waiter in status.Waiters)
        {
            if (configuredAgents.Contains(waiter) && !hasRunningHarness(waiter))
            {
                queueChanged = true;
                droppedStaleWaiter = true;
                continue;
            }

            if (string.Equals(waiter, holder, StringComparison.Ordinal))
            {
                queueChanged = true;
                continue;
            }

            if (queue.Contains(waiter, StringComparer.Ordinal))
            {
                queueChanged = true;
                continue;
            }

            queue.Add(waiter);
        }

        if (!abandonedHolder && !queueChanged)
        {
            lastFailure = null;
            return status;
        }

        var updated = status;
        var effects = new List<string>();
        var failures = new List<string>();
        if (abandonedHolder)
        {
            var release = await beads.ReleaseMergeSlotAsync(workspace, holder!, cancellationToken);
            if (release.Succeeded)
            {
                updated = updated with { Holder = null };
                effects.Add($"released the Beads merge slot held by {holder}, which has no running harness");
            }
            else
            {
                failures.Add(
                    $"could not release the Beads merge slot held by {holder}: {Beads.FailureDetail(release)}");
            }
        }

        if (queueChanged)
        {
            if (status.Id is not { } slotId)
            {
                failures.Add("could not update the Beads merge-slot queue: the slot id is missing");
            }
            else
            {
                var rewrite = await beads.SetMergeSlotWaitersAsync(
                    slotId,
                    workspace,
                    queue,
                    cancellationToken);
                if (rewrite.Succeeded)
                {
                    updated = updated with { Waiters = queue };
                    if (droppedStaleWaiter)
                    {
                        effects.Add("removed merge-slot waiters that have no running harness");
                    }
                }
                else
                {
                    failures.Add($"could not update the Beads merge-slot queue: {Beads.FailureDetail(rewrite)}");
                }
            }
        }

        if (effects.Count > 0)
        {
            await log.WarningAsync("abacus", string.Join("; ", effects));
        }

        if (failures.Count > 0)
        {
            await ReportFailureAsync(string.Join("; ", failures));
        }
        else
        {
            lastFailure = null;
        }

        return updated;
    }

    private async Task ReportFailureAsync(string message)
    {
        if (string.Equals(lastFailure, message, StringComparison.Ordinal))
        {
            return;
        }

        lastFailure = message;
        await log.WarningAsync("abacus", message);
    }
}
