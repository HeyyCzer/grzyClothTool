using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace grzyClothTool.Helpers;
#nullable enable

/// <summary>
/// Async limiter weighted by estimated bytes: lets many small textures run in parallel while a few huge
/// ones share the same budget. Waiters are served in FIFO order so big textures are never starved, and a
/// request larger than the whole budget is granted alone instead of waiting forever.
/// </summary>
public sealed class MemoryBudget
{
    private readonly object _lock = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private long _inUse;

    public long Capacity { get; }

    public long InUse
    {
        get
        {
            lock (_lock)
            {
                return _inUse;
            }
        }
    }

    public MemoryBudget(long capacityBytes)
    {
        Capacity = Math.Max(1, capacityBytes);
    }

    /// <summary>
    /// Default budget for a build: 40% of the memory available to the process, between 512 MB and 8 GB.
    /// </summary>
    public static long GetDefaultCapacity()
    {
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return Math.Clamp(available * 2 / 5, 512L * 1024 * 1024, 8L * 1024 * 1024 * 1024);
    }

    public async Task<IDisposable> AcquireAsync(long cost, CancellationToken cancellationToken = default)
    {
        cost = Math.Clamp(cost, 1, Capacity);

        Waiter waiter;
        LinkedListNode<Waiter> node;
        lock (_lock)
        {
            if (_waiters.Count == 0 && _inUse + cost <= Capacity)
            {
                _inUse += cost;
                return new Lease(this, cost);
            }

            waiter = new Waiter(cost);
            node = _waiters.AddLast(waiter);
        }

        using (cancellationToken.Register(() => Cancel(node, cancellationToken)))
        {
            await waiter.Completion.Task.ConfigureAwait(false);
        }

        return new Lease(this, cost);
    }

    private void Cancel(LinkedListNode<Waiter> node, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            // Already granted (removed from the list) - the lease will be released by its owner.
            if (node.List == null)
            {
                return;
            }

            _waiters.Remove(node);
            GrantWaiters(); // the head may have been blocking smaller requests behind it
        }

        node.Value.Completion.TrySetCanceled(cancellationToken);
    }

    private void Release(long cost)
    {
        lock (_lock)
        {
            _inUse -= cost;
            GrantWaiters();
        }
    }

    // Must be called under _lock.
    private void GrantWaiters()
    {
        while (_waiters.First is { } first && _inUse + first.Value.Cost <= Capacity)
        {
            _waiters.RemoveFirst();
            _inUse += first.Value.Cost;
            first.Value.Completion.TrySetResult();
        }
    }

    private sealed class Waiter(long cost)
    {
        public long Cost { get; } = cost;

        // Continuations run outside the lock so a woken worker never executes while it is held.
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease(MemoryBudget owner, long cost) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(cost);
            }
        }
    }
}
