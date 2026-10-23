using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CsharpBgapi;
using CsharpBgapi.Events;
using CsharpBgapi.Protocol;
using CsharpBgapi.Serial;
using CsharpBgapi.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

if (args.Contains("--inventory"))
{
    var definitions = Wire.LoadDefinitions();
    foreach (var api in new[] { "bt", "btmesh" })
        foreach (var cls in definitions.FindAllClasses(api))
            foreach (var evt in cls.Events.Where(e => e.Parameters.Any(p => p.ResolvedType == "uint16array")))
                Console.WriteLine($"{api} {cls.Name} {evt.Name}: {string.Join(",", evt.Parameters.Select(p => p.Name + ":" + p.ResolvedType))}");
    return;
}
if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine("ReleaseValidation --output <json> [--count 4000] [--repetitions 3] [--seed 20261023] [--rate 2000]");
    Console.WriteLine("Hardware-free only. Runs warmup, sustained, burst, backlog, overload, subscriber-only, slow-callback, logging and fault workloads.");
    return;
}
string Value(string key, string fallback) => Array.IndexOf(args, key) is var i && i >= 0
    ? i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"Missing value for {key}")
    : fallback;
int count = int.Parse(Value("--count", "4000"));
int repetitions = int.Parse(Value("--repetitions", "3"));
int seed = int.Parse(Value("--seed", "20261023"));
int rate = int.Parse(Value("--rate", "2000"));
if (count < 100 || count > 1_000_000 || repetitions < 1 || rate < 1) throw new ArgumentException("Use count 100..1000000, positive repetitions and rate.");
string output = Path.GetFullPath(Value("--output", "artifacts/validation/performance.json"));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var rows = new List<object>();
await Measure("warmup", Math.Min(count, 500), seed, rate, Path.GetDirectoryName(output)!);
var stability = await MeasureStability(count);
foreach (var scenario in new[] { "sustained", "burst", "unpaced", "backlog", "overload", "subscriber-only", "slow-selector", "slow-callback", "logging", "fault" })
    for (int repetition = 0; repetition < repetitions; repetition++)
    {
        var row = await Measure(scenario, count, seed + repetition, rate, Path.GetDirectoryName(output)!);
        rows.Add(row);
        Console.WriteLine($"{scenario} repetition {repetition + 1}: {row}");
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            utc = DateTimeOffset.UtcNow, runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processors = Environment.ProcessorCount,
            candidate = typeof(BgapiDevice).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            workload = new { count, repetitions, seed, sustainedRate = rate, producers = 4, waiters = 4, subscribers = 1,
                commands = 100, responseTimeoutSeconds = 10, readerPollMs = 10, warmupMessages = Math.Min(count, 500),
                eventBytes = "21 or 260 (seeded)", responseBytes = 6, burstSize = Workload.BurstSize,
                burstGapMs = Workload.BurstGap.TotalMilliseconds,
                slowCallbackMicroseconds = Workload.SlowWorkMicroseconds, slowSelectorSpinIterations = 100,
                overload = "capacity 32; 4 waiters claim in order and spend 100 us per claim; claims + evictions = count",
                allocationScope = "process-wide, includes emulator and harness",
                latencyScope = "inject-to-callback/claim; write-to-command-continuation; caller-to-write separately",
                pacing = "sustained: per-producer stopwatch pacing; burst: 64-frame batches per producer, each followed by an idle gap longer than the reader poll; others unpaced",
                budgets = "not supplied; measurements are not acceptance thresholds" },
            stability, rows
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

static async Task<object[]> MeasureStability(int count)
{
    var options = Options.Create(new CsharpBgapiOptions { MaxQueuedEvents = count + 1, ReaderLoopReadTimeoutMs = 10 });
    using var port = new SimulatedConnection();
    using var device = new BgapiDevice(options, new BgapiConnector(options,
        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, (_, _) => port));
    device.LoadDefaultXapis();
    var frames = Enumerable.Range(0, count).Select(i => Wire.Event(device.Definitions, i)).ToArray();
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int delivered = 0;
    device.Subscribe("bt_evt_user_message_to_host", _ =>
    {
        if (Interlocked.Increment(ref delivered) == count) completion.TrySetResult();
    });
    device.Open("simulated");
    var rows = new object[12];
    using var process = Process.GetCurrentProcess();
    for (int cycle = 0; cycle < rows.Length; cycle++)
    {
        delivered = 0;
        completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var frame in frames) port.Inject(frame);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        for (int i = 0; i < count; i++)
            (Wire.Sequence(device.EventQueue.GetEvent(TimeSpan.Zero)!) == i).Assert("cycle sequence", "stability", cycle);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        process.Refresh();
        rows[cycle] = new { cycle, count, managedLiveBytes = GC.GetTotalMemory(false),
            workingSetBytes = process.WorkingSet64, threads = process.Threads.Count, handles = process.HandleCount,
            retained = device.EventQueue.Count, drops = device.EventQueue.DroppedEventCount };
    }
    device.Close();
    return rows;
}

static async Task<object> Measure(string scenario, int count, int seed, int rate, string evidenceDirectory)
{
    // Both saturate a 32-event queue. subscriber-only has no waiters; overload keeps slow waiters
    // claiming while the queue evicts, so the two used to be the same workload under two names.
    bool noWaiters = scenario == "subscriber-only";
    bool overload = scenario == "overload";
    int capacity = noWaiters || overload ? 32 : count + 100;
    var logger = new CountingLogger(scenario == "logging");
    var options = Options.Create(new CsharpBgapiOptions { MaxQueuedEvents = capacity, ReaderLoopReadTimeoutMs = 10,
        ResponseTimeoutSeconds = 10, StopReaderTimeoutSeconds = 1 });
    using var port = new SimulatedConnection();
    using var device = new BgapiDevice(options, new BgapiConnector(options, logger, (_, _) => port), logger);
    device.LoadDefaultXapis();
    var definitions = device.Definitions;
    var injection = new object();
    var injectedAt = new long[count];
    var subscriptionLatency = new double[count];
    var eventLatency = new double[count];
    var subscribers = new int[count];
    var claims = new int[count];
    var sentAt = new long[100];
    var queueLatency = new double[100];
    var responseLatency = new double[100];
    // Nonnegative entries are event IDs; -(id + 1) entries are command responses.
    // Record the actual interleaving, since a random seed alone cannot replay thread scheduling.
    var injectionOrder = new int[count + 100];
    var splitOffsets = new int[count];
    int injectionIndex = 0;
    var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int deliveries = 0, highWater = 0;
    const string eventName = "bt_evt_user_message_to_host";
    device.Subscribe(eventName, message =>
    {
        int id = Wire.Sequence(message);
        if (id < 0) return; // Deliberate ignored backlog, counted separately below.
        if (scenario == "slow-callback") Workload.SlowWork();
        subscribers[id]++;
        subscriptionLatency[id] = Stopwatch.GetElapsedTime(injectedAt[id]).TotalMilliseconds;
        highWater = Math.Max(highWater, device.EventQueue.Count);
        if (Interlocked.Increment(ref deliveries) == count) dispatched.TrySetResult();
    });
    port.OnWrite = command =>
    {
        int id = command[BgapiHeader.Size]; // system.halt's uint8 input identifies this synthetic call.
        sentAt[id] = Stopwatch.GetTimestamp();
        lock (injection)
        {
            injectionOrder[injectionIndex++] = -(id + 1);
            port.Inject(Wire.Response(definitions));
        }
    };
    device.Open("simulated");
    // A fixed 60 s used to cancel valid large runs (sustained --count 200000 --rate 2000 needs 100 s),
    // so the paced or slowed part of each scenario is allowed for twice over on top of it.
    double pacedSeconds = scenario switch
    {
        "sustained" => (double)count / rate,
        "burst" => (double)count / 4 / Workload.BurstSize * Workload.BurstGap.TotalSeconds,
        "slow-callback" or "overload" => count * Workload.SlowWorkMicroseconds / 1e6,
        _ => 0,
    };
    using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60 + 2 * pacedSeconds));
    if (scenario == "backlog")
        for (int i = 0; i < 100; i++) device.EventQueue.Enqueue(device.Protocol.DecodeMessage(Wire.Event(definitions, -1 - i)));
    var waiters = noWaiters ? [] : Enumerable.Range(0, 4).Select(lane => Task.Factory.StartNew(() =>
    {
        int expected = lane;
        var selector = new CallableSelector((message, _) =>
        {
            if (scenario == "slow-selector") Thread.SpinWait(100);
            int id = Wire.Sequence(message);
            return id >= 0 && id % 4 == lane ? EventCategory.SelectFinal : EventCategory.Ignore;
        });
        if (overload)
        {
            // Evictions leave gaps, so only order is checked. Completion is read before each wait:
            // once every event has been dispatched, every one is queued or evicted, and a wait that
            // then times out means none is left for this lane.
            int last = -1;
            while (true)
            {
                bool allDispatched = dispatched.Task.IsCompleted;
                BgapiMessage message;
                try { message = device.WaitEvent(selector, TimeSpan.FromMilliseconds(50), ct: guard.Token).Single(); }
                catch (BgapiWaitEventException)
                {
                    if (allDispatched) return;
                    continue;
                }
                int id = Wire.Sequence(message);
                if (id <= last) throw new InvalidOperationException($"{scenario} seed={seed}: lane {lane} claimed {id} after {last}");
                last = id;
                claims[id]++;
                eventLatency[id] = Stopwatch.GetElapsedTime(injectedAt[id]).TotalMilliseconds;
                Workload.SlowWork();
            }
        }
        while (expected < count)
        {
            var message = device.WaitEvent(selector, TimeSpan.FromSeconds(30), ct: guard.Token).Single();
            int id = Wire.Sequence(message);
            if (id != expected) throw new InvalidOperationException($"{scenario} seed={seed}: lane {lane} expected {expected}, got {id}");
            claims[id]++;
            eventLatency[id] = Stopwatch.GetElapsedTime(injectedAt[id]).TotalMilliseconds;
            expected += 4;
        }
    }, guard.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    long liveBefore = GC.GetTotalMemory(false);
    long allocationBefore = GC.GetTotalAllocatedBytes(true);
    var gcBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
    using var process = Process.GetCurrentProcess();
    TimeSpan cpuBefore = process.TotalProcessorTime;
    long clock = Stopwatch.GetTimestamp();
    var commands = Enumerable.Range(0, 100).Select(async id =>
    {
        long requested = Stopwatch.GetTimestamp();
        var response = await device.SendCommandAsync("bt", "system", "halt", new() { ["halt"] = (byte)id }, ct: guard.Token);
        if (response.Status != SlStatus.OK) throw new InvalidOperationException($"{scenario}: command {id} returned {response.Status}");
        queueLatency[id] = Stopwatch.GetElapsedTime(requested, sentAt[id]).TotalMilliseconds;
        responseLatency[id] = Stopwatch.GetElapsedTime(sentAt[id]).TotalMilliseconds;
    }).ToArray();
    // Dedicated threads, like the waiters: spinning producers on pool threads used to starve the
    // pool that completes simulated reads and command continuations, so on a small host the
    // latencies measured the harness and a split frame could outlast the partial-frame budget.
    var producers = Enumerable.Range(0, 4).Select(lane => Task.Factory.StartNew(() =>
    {
        var random = new Random(seed + lane);
        int produced = 0;
        long start = Stopwatch.GetTimestamp();
        for (int id = lane; id < count; id += 4)
        {
            guard.Token.ThrowIfCancellationRequested();
            if (scenario == "sustained")
                while (Stopwatch.GetElapsedTime(start).TotalSeconds < produced * 4.0 / rate) Thread.SpinWait(100);
            // A gap longer than the reader poll lets the reader go idle, so each batch is a
            // quiet-to-burst transition. A bare Thread.Yield made this the unpaced workload.
            if (scenario == "burst" && produced > 0 && produced % Workload.BurstSize == 0) Thread.Sleep(Workload.BurstGap);
            var frame = Wire.Event(definitions, id, random.Next(2) == 0 ? 16 : 255);
            lock (injection)
            {
                injectedAt[id] = Stopwatch.GetTimestamp();
                if (scenario == "fault") port.Inject([0xFF]); // Unknown device id, deliberately discarded.
                int split = random.Next(1, frame.Length);
                injectionOrder[injectionIndex++] = id;
                splitOffsets[id] = split;
                port.Inject(frame[..split]);
                port.Inject(frame[split..]);
            }
            produced++;
        }
    }, guard.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
    try
    {
        await Task.WhenAll(producers.Concat(commands).Concat(waiters)).WaitAsync(guard.Token);
        await dispatched.Task.WaitAsync(guard.Token);
    }
    catch
    {
        // Keep the failing stimulus schedule even when the normal result row cannot be produced.
        lock (injection)
            File.WriteAllText(Path.Combine(evidenceDirectory, $"{scenario}-{seed}-failure.json"),
                JsonSerializer.Serialize(new { scenario, seed, injectionIndex, injectionOrder, splitOffsets,
                    claims, subscribers, eventDrops = device.EventQueue.DroppedEventCount, byteDrops = device.DroppedByteCount }));
        throw;
    }
    double seconds = Stopwatch.GetElapsedTime(clock).TotalSeconds;
    long allocated = GC.GetTotalAllocatedBytes(true) - allocationBefore;
    var collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gcBefore[i]).ToArray();
    double cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
    subscribers.All(n => n == 1).Assert("subscription accounting", scenario, seed);
    if (overload)
    {
        // How many survive depends on scheduling; that each event is claimed once or evicted does not.
        (claims.All(n => n <= 1) && claims.Sum() + device.EventQueue.DroppedEventCount == count
            && device.EventQueue.Count == 0).Assert("overload claim/eviction accounting", scenario, seed);
        (device.EventQueue.DroppedEventCount > 0).Assert("deliberate overload reports drops", scenario, seed);
    }
    else
    {
        (noWaiters || claims.All(n => n == 1)).Assert("claim accounting", scenario, seed);
        long expectedDrops = noWaiters ? count - capacity : 0;
        (device.EventQueue.DroppedEventCount == expectedDrops).Assert("queue drop accounting", scenario, seed);
    }
    (device.DroppedByteCount == (scenario == "fault" ? count : 0)).Assert("byte drop accounting", scenario, seed);
    (port.OverlappingReads == 0 && port.OverlappingWrites == 0 && port.CancellableReads == 0).Assert("serialized IO", scenario, seed);
    int retained = device.EventQueue.Count;
    long dropped = device.EventQueue.DroppedEventCount, byteDrops = device.DroppedByteCount;
    device.EventQueue.Clear();
    device.Close();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    process.Refresh();
    return new
    {
        scenario, seed, seconds, messagesPerSecond = (count + 100) / seconds, cpuSeconds,
        cpuPercentOfOneCore = cpuSeconds / seconds * 100, allocatedBytesPerMessage = (double)allocated / (count + 100),
        gcCollections = collections, liveBefore, liveAfterDrain = GC.GetTotalMemory(false),
        workingSetBytes = process.WorkingSet64, peakWorkingSetBytes = process.PeakWorkingSet64,
        processThreads = process.Threads.Count, handles = process.HandleCount,
        capacity, observedQueueHighWater = highWater, retainedBeforeDrain = retained, eventDrops = dropped, byteDrops,
        subscriptions = deliveries, claims = claims.Sum(), commands = commands.Length, formattedLogMessages = logger.Count,
        injectionOrder, splitOffsets,
        eventLatencyMs = noWaiters ? null : Percentiles([.. eventLatency.Where((_, id) => claims[id] == 1)]),
        subscriptionLatencyMs = Percentiles(subscriptionLatency),
        commandQueueDelayMs = Percentiles(queueLatency), commandResponseLatencyMs = Percentiles(responseLatency)
    };
}

static object Percentiles(double[] values)
{
    Array.Sort(values);
    double P(double percentile) => values[(int)Math.Ceiling(percentile * values.Length) - 1];
    return new { p50 = P(.50), p95 = P(.95), p99 = P(.99), max = values[^1] };
}

internal sealed class CountingLogger(bool enabled) : ILogger
{
    public int Count;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => enabled;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!enabled) return;
        _ = formatter(state, exception); // Formatting sink: measures logging work without printing payloads or flooding stdout.
        Interlocked.Increment(ref Count);
    }
}

internal static class Workload
{
    public const int BurstSize = 64;
    // Longer than the 10 ms reader poll, so the reader goes idle between batches.
    public static readonly TimeSpan BurstGap = TimeSpan.FromMilliseconds(20);
    public const int SlowWorkMicroseconds = 100;

    public static void SlowWork()
    {
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start).TotalMicroseconds < SlowWorkMicroseconds) Thread.SpinWait(10);
    }
}

internal static class ValidationCheck
{
    public static void Assert(this bool condition, string check, string scenario, int seed)
    {
        if (!condition) throw new InvalidOperationException($"{scenario}, seed {seed}: failed {check}");
    }
}
