using grzyClothTool.Views;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace grzyClothTool.Helpers;
#nullable enable

public record BuildLogEntry(TimeSpan Elapsed, LogType Level, string Message)
{
    public string Text => $"[{Elapsed:hh\\:mm\\:ss}] {Message}";
    public bool IsWarning => Level == LogType.Warning;
    public bool IsError => Level == LogType.Error;
}

public record BuildProgressSnapshot(
    double Percent,
    long DoneWork,
    long TotalWork,
    int DoneItems,
    TimeSpan Elapsed,
    TimeSpan? Remaining,
    string Phase,
    int Warnings,
    int Errors);

/// <summary>
/// Thread-safe progress and log sink for a resource build. Build workers push entries from any thread;
/// the build window polls <see cref="GetSnapshot"/> and <see cref="DrainPending"/> on a timer so thousands
/// of files never flood the dispatcher.
/// </summary>
public class BuildReporter
{
    // Relative cost of each unit of work, used for the percentage and the ETA.
    public const long WeightCopy = 1;
    public const long WeightYddResave = 4;
    public const long WeightTextureConvert = 5;
    public const long WeightTextureOptimize = 20;

    private static readonly TimeSpan MinElapsedForEta = TimeSpan.FromSeconds(3);
    private const double MinPercentForEta = 1.0;

    private readonly ConcurrentQueue<BuildLogEntry> _pending = new();
    private readonly List<BuildLogEntry> _all = [];
    private readonly Stopwatch _stopwatch = new();

    private long _totalWork;
    private long _doneWork;
    private int _doneItems;
    private int _warnings;
    private int _errors;
    private volatile string _phase = "Preparing";
    private volatile bool _finished;
    private volatile bool _completed;

    public void Start(long totalWork)
    {
        Interlocked.Exchange(ref _totalWork, Math.Max(1, totalWork));
        _stopwatch.Restart();
        Info($"Build started. Estimated work: {totalWork} units.");
    }

    /// <param name="completed">False when the build failed or was cancelled: the percentage then stays where it stopped.</param>
    public void Finish(bool completed = true)
    {
        _completed = completed;
        _finished = true;
        _stopwatch.Stop();
    }

    public void SetPhase(string phase)
    {
        _phase = phase;
        Info($"== {phase} ==");
    }

    public void Complete(long weight, string? message = null)
    {
        Interlocked.Add(ref _doneWork, weight);
        Interlocked.Increment(ref _doneItems);

        if (message != null)
        {
            Info(message);
        }
    }

    public void Info(string message) => Add(LogType.Info, message);

    public void Warning(string message)
    {
        Interlocked.Increment(ref _warnings);
        Add(LogType.Warning, message);
        LogHelper.Log(message, LogType.Warning);
    }

    public void Error(string message)
    {
        Interlocked.Increment(ref _errors);
        Add(LogType.Error, message);
        LogHelper.Log(message, LogType.Error);
    }

    public BuildProgressSnapshot GetSnapshot()
    {
        long total = Interlocked.Read(ref _totalWork);
        long done = Math.Min(Interlocked.Read(ref _doneWork), total);
        var elapsed = _stopwatch.Elapsed;

        double percent = total > 0 ? done * 100.0 / total : 0;
        if (!_completed)
        {
            // Manifests/meta files are written after the tracked work, so never claim 100% early.
            percent = Math.Min(percent, 99.0);
        }

        TimeSpan? remaining = null;
        if (_completed)
        {
            percent = 100;
            remaining = TimeSpan.Zero;
        }
        else if (_finished)
        {
            remaining = null;
        }
        else if (done > 0 && percent >= MinPercentForEta && elapsed >= MinElapsedForEta)
        {
            remaining = TimeSpan.FromTicks((long)(elapsed.Ticks * (double)(total - done) / done));
        }

        return new BuildProgressSnapshot(percent, done, total, Volatile.Read(ref _doneItems), elapsed, remaining, _phase,
            Volatile.Read(ref _warnings), Volatile.Read(ref _errors));
    }

    public List<BuildLogEntry> DrainPending(int max = int.MaxValue)
    {
        var drained = new List<BuildLogEntry>();
        while (drained.Count < max && _pending.TryDequeue(out var entry))
        {
            drained.Add(entry);
        }
        return drained;
    }

    public string GetFullLog()
    {
        lock (_all)
        {
            return string.Join(Environment.NewLine, _all.Select(e => $"{e.Text}"));
        }
    }

    private void Add(LogType level, string message)
    {
        var entry = new BuildLogEntry(_stopwatch.Elapsed, level, level switch
        {
            LogType.Warning => $"WARN  {message}",
            LogType.Error => $"ERROR {message}",
            _ => message
        });

        lock (_all)
        {
            _all.Add(entry);
        }
        _pending.Enqueue(entry);
    }
}
