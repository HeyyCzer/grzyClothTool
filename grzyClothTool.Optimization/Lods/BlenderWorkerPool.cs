using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace grzyClothTool.Optimization.Lods;

/// <summary>Answer of the Blender worker for one file.</summary>
public sealed record BlenderJobResult(bool Ok, string? OutputXml, string? Error, IReadOnlyList<string> Log);

/// <summary>
/// Keeps up to <see cref="LodGenerationOptions.MaxWorkers"/> Blender processes running blender_lod_worker.py, so
/// Blender and Sollumz start once per worker instead of once per file. Jobs go to a worker's stdin as JSON lines and
/// answers come back on stdout, prefixed by a marker that separates them from Blender's own output.
/// </summary>
public sealed class BlenderWorkerPool : IAsyncDisposable
{
    private const string Marker = "@@GRZY@@ ";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5); // first run installs Sollumz dependencies

    private readonly LodGenerationOptions _options;
    private readonly string _blender;
    private readonly ConcurrentQueue<Worker> _idle = new();
    private readonly SemaphoreSlim _slots;
    // Workers start one at a time: the first one may be installing the Sollumz dependencies into the shared folder.
    private readonly SemaphoreSlim _startup = new(1);
    private readonly List<Worker> _all = [];
    private string? _script;
    private string? _fatalError;
    private int _nextJobId;

    public BlenderWorkerPool(LodGenerationOptions options)
    {
        _options = options;
        _blender = BlenderLocator.Resolve(options.BlenderPath);
        _slots = new SemaphoreSlim(Math.Max(1, options.MaxWorkers));
    }

    /// <summary>Blender and Sollumz versions reported by the first worker, once one has started.</summary>
    public string? Versions { get; private set; }

    public async Task<BlenderJobResult> RunAsync(string inputXml, string outputDir, CancellationToken cancellationToken)
    {
        // One job per worker at a time, so there are never more than MaxWorkers Blender processes.
        await _slots.WaitAsync(cancellationToken);
        try
        {
            return await RunOnWorkerAsync(inputXml, outputDir, cancellationToken);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<BlenderJobResult> RunOnWorkerAsync(string inputXml, string outputDir, CancellationToken cancellationToken)
    {
        if (_fatalError != null)
        {
            return new BlenderJobResult(false, null, _fatalError, []);
        }

        if (!_idle.TryDequeue(out var worker))
        {
            try
            {
                worker = await StartWorkerAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Blender or Sollumz cannot start: don't try again for every file.
                _fatalError ??= ex.Message;
                return new BlenderJobResult(false, null, _fatalError, []);
            }
        }

        var job = new JsonObject
        {
            ["id"] = Interlocked.Increment(ref _nextJobId),
            ["input"] = inputXml,
            ["output_dir"] = outputDir,
            ["ratios"] = new JsonObject
            {
                ["medium"] = _options.MediumRatio,
                ["low"] = _options.LowRatio
            }
        };

        JsonObject? answer;
        try
        {
            answer = await worker.SendAsync(job, _options.JobTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
        {
            // A worker that timed out or died is not reused; the next job starts a fresh one.
            Discard(worker);
            return new BlenderJobResult(false, null, ex is TimeoutException
                ? $"Blender took longer than {_options.JobTimeout.TotalMinutes:0} min and was stopped"
                : $"Blender stopped unexpectedly{worker.StderrTail()}", []);
        }
        catch
        {
            Discard(worker);
            throw;
        }

        _idle.Enqueue(worker);

        var log = answer["log"]?.AsArray().Select(n => n?.GetValue<string>() ?? "").ToList() ?? [];
        return answer["ok"]?.GetValue<bool>() == true
            ? new BlenderJobResult(true, answer["output"]?.GetValue<string>(), null, log)
            : new BlenderJobResult(false, null, answer["error"]?.GetValue<string>() ?? "unknown Blender error", log);
    }

    private async Task<Worker> StartWorkerAsync(CancellationToken cancellationToken)
    {
        await _startup.WaitAsync(cancellationToken);
        try
        {
            if (_fatalError != null)
            {
                throw new InvalidOperationException(_fatalError);
            }
            return await StartWorkerCoreAsync(cancellationToken);
        }
        finally
        {
            _startup.Release();
        }
    }

    private async Task<Worker> StartWorkerCoreAsync(CancellationToken cancellationToken)
    {
        _script ??= ExtractScript();

        var start = new ProcessStartInfo(_blender)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        start.ArgumentList.Add("--background");
        if (_options.Sollumz == SollumzSource.Folder)
        {
            // Clean Blender: the user's add-ons (maybe another Sollumz) are not loaded next to ours.
            start.ArgumentList.Add("--factory-startup");
        }
        start.ArgumentList.Add("--python-exit-code");
        start.ArgumentList.Add("1");
        start.ArgumentList.Add("--python");
        start.ArgumentList.Add(_script);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add("--sollumz");
        if (_options.Sollumz == SollumzSource.Folder)
        {
            var folder = _options.SollumzFolder;
            if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, "__init__.py")))
            {
                throw new InvalidOperationException($"Sollumz add-on not found in '{folder}'.");
            }
            start.ArgumentList.Add(Path.GetFullPath(folder));
            start.ArgumentList.Add("--data-dir");
            start.ArgumentList.Add(_options.DataFolder);
        }
        else
        {
            start.ArgumentList.Add("installed");
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start '{_blender}'.");
        var worker = new Worker(process);
        lock (_all)
        {
            _all.Add(worker);
        }

        var ready = await worker.WaitForMessageAsync(StartupTimeout, cancellationToken);
        if (ready?["fatal"] is { } fatal)
        {
            Discard(worker);
            throw new InvalidOperationException($"Blender/Sollumz setup failed: {fatal.GetValue<string>()}");
        }
        if (ready?["ready"] == null)
        {
            Discard(worker);
            throw new InvalidOperationException($"Blender did not start the LOD worker{worker.StderrTail()}");
        }

        Versions ??= $"Blender {ready["blender"]}, Sollumz {ready["sollumz"]}";
        return worker;
    }

    private void Discard(Worker worker)
    {
        worker.Dispose();
        lock (_all)
        {
            _all.Remove(worker);
        }
    }

    private static string ExtractScript()
    {
        using var stream = typeof(BlenderWorkerPool).Assembly.GetManifestResourceStream("grzyClothTool.Optimization.Lods.blender_lod_worker.py")
            ?? throw new InvalidOperationException("blender_lod_worker.py is not embedded in the assembly.");
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();

        var folder = Path.Combine(Path.GetTempPath(), "grzyOptimizer");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"blender_lod_worker_{(uint)content.GetHashCode():x8}.py");
        File.WriteAllText(path, content);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        List<Worker> workers;
        lock (_all)
        {
            workers = [.. _all];
            _all.Clear();
        }

        foreach (var worker in workers)
        {
            await worker.StopAsync();
        }
    }

    private sealed class Worker : IDisposable
    {
        private readonly Process _process;
        private readonly Channel<JsonObject> _messages = Channel.CreateUnbounded<JsonObject>();
        private readonly Queue<string> _stderr = new();

        public Worker(Process process)
        {
            _process = process;
            _ = Task.Run(ReadStdoutAsync);
            _ = Task.Run(ReadStderrAsync);
        }

        public async Task<JsonObject> SendAsync(JsonObject job, TimeSpan timeout, CancellationToken cancellationToken)
        {
            await _process.StandardInput.WriteLineAsync(job.ToJsonString().AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);

            var id = job["id"]!.GetValue<int>();
            while (true)
            {
                var message = await WaitForMessageAsync(timeout, cancellationToken)
                    ?? throw new IOException("Blender closed its output.");
                if (message["id"]?.GetValue<int>() == id)
                {
                    return message;
                }
            }
        }

        public async Task<JsonObject?> WaitForMessageAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            try
            {
                return await _messages.Reader.ReadAsync(linked.Token);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException();
            }
        }

        public string StderrTail()
        {
            lock (_stderr)
            {
                return _stderr.Count == 0 ? "." : ":\n" + string.Join("\n", _stderr);
            }
        }

        private async Task ReadStdoutAsync()
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (!line.StartsWith(Marker, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    try
                    {
                        if (JsonNode.Parse(line[Marker.Length..]) is JsonObject message)
                        {
                            _messages.Writer.TryWrite(message);
                        }
                    }
                    catch (JsonException)
                    {
                        // Not ours after all.
                    }
                }
            }
            catch (Exception)
            {
                // Process killed while reading.
            }
            finally
            {
                _messages.Writer.TryComplete();
            }
        }

        private async Task ReadStderrAsync()
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync() is { } line)
                {
                    lock (_stderr)
                    {
                        _stderr.Enqueue(line);
                        while (_stderr.Count > 15)
                        {
                            _stderr.Dequeue();
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Process killed while reading.
            }
        }

        public async Task StopAsync()
        {
            try
            {
                // Closing stdin ends the worker's job loop and Blender exits by itself.
                _process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception)
            {
                // Fall through to Dispose, which kills it.
            }
            Dispose();
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Already gone.
            }
            _process.Dispose();
        }
    }
}
