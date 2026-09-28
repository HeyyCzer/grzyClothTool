using grzyClothTool.Helpers;

namespace grzyClothTool.UnitTests.Helpers;

public class MemoryBudgetTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task AcquireAsync_GrantsImmediatelyWithinCapacity()
    {
        var budget = new MemoryBudget(100);

        using var a = await budget.AcquireAsync(40);
        using var b = await budget.AcquireAsync(60);

        Assert.Equal(100, budget.InUse);
    }

    [Fact]
    public async Task AcquireAsync_WaitsUntilEnoughIsReleased()
    {
        var budget = new MemoryBudget(100);
        var first = await budget.AcquireAsync(80);

        var second = budget.AcquireAsync(50);
        await Task.Delay(Short);
        Assert.False(second.IsCompleted);

        first.Dispose();
        using var lease = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(50, budget.InUse);
    }

    [Fact]
    public async Task AcquireAsync_ServesWaitersInOrder()
    {
        var budget = new MemoryBudget(100);
        var holder = await budget.AcquireAsync(100);

        var big = budget.AcquireAsync(90);
        var small = budget.AcquireAsync(10);
        await Task.Delay(Short);

        // The small request fits the free space only after the big one, never ahead of it.
        Assert.False(big.IsCompleted);
        Assert.False(small.IsCompleted);

        holder.Dispose();
        using var bigLease = await big.WaitAsync(TimeSpan.FromSeconds(5));
        using var smallLease = await small.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(100, budget.InUse);
    }

    [Fact]
    public async Task AcquireAsync_GrantsOversizedRequestWhenIdle()
    {
        var budget = new MemoryBudget(100);

        using var lease = await budget.AcquireAsync(1_000).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(100, budget.InUse);
    }

    [Fact]
    public async Task Dispose_ReleasesOnlyOnce()
    {
        var budget = new MemoryBudget(100);
        var lease = await budget.AcquireAsync(30);
        using var other = await budget.AcquireAsync(20);

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(20, budget.InUse);
    }

    [Fact]
    public async Task AcquireAsync_CancelledWaiterDoesNotBlockOthers()
    {
        var budget = new MemoryBudget(100);
        var holder = await budget.AcquireAsync(60);

        using var cts = new CancellationTokenSource();
        var cancelled = budget.AcquireAsync(80, cts.Token);
        var next = budget.AcquireAsync(40);
        await Task.Delay(Short);
        Assert.False(next.IsCompleted);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        using var nextLease = await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(100, budget.InUse);

        holder.Dispose();
        Assert.Equal(40, budget.InUse);
    }
}
