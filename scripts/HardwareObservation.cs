// Compile in a temporary PackageReference consumer of the exact candidate. Opening hardware is opt-in.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CsharpBgapi;
using CsharpBgapi.Events;
using Microsoft.Extensions.Options;

string Required(string key)
{
    int i = Array.IndexOf(args, key);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"Missing {key}");
}
string? Optional(string key) => Array.IndexOf(args, key) is var i && i >= 0
    ? i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"Missing value for {key}")
    : null;
string port = Required("--port");
int baud = int.Parse(Required("--baud"));
var handshake = Enum.Parse<Handshake>(Required("--handshake"));
int capacity = int.Parse(Required("--queue-capacity"));
int seconds = int.Parse(Required("--seconds"));
double responseSeconds = double.Parse(Required("--response-seconds"), System.Globalization.CultureInfo.InvariantCulture);
int callers = int.Parse(Optional("--callers") ?? "1");
int interval = int.Parse(Optional("--interval-ms") ?? "1000");
string[]? command = Optional("--command")?.Split('.');
if (command is not null && command.Length != 3) throw new ArgumentException("Command must be api.class.command");
if (baud <= 0 || capacity <= 0 || seconds <= 0 || callers <= 0 || interval < 0 || responseSeconds <= 0)
    throw new ArgumentException("Rates, capacity, duration and timeout must be positive; interval may be zero");
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var device = new BgapiDevice(Options.Create(new CsharpBgapiOptions
    { MaxQueuedEvents = capacity, ResponseTimeoutSeconds = responseSeconds }));
var xapi = Optional("--xapi-dir");
if (xapi is null) device.LoadDefaultXapis();
else foreach (string file in new[] { "sl_bt.xapi", "sl_btmesh.xapi" }) device.LoadXapi(Path.Combine(xapi, file));
var subscriptions = new ConcurrentDictionary<string, long>();
var outcomes = new ConcurrentDictionary<string, long>();
long claims = 0, commandCount = 0;
double maxCommandMs = 0;
var latencyGate = new object();
foreach (string api in new[] { "bt", "btmesh" })
    foreach (var cls in device.Definitions.FindAllClasses(api))
        foreach (var evt in cls.Events)
        {
            string name = evt.FullName;
            device.Subscribe(name, _ => subscriptions.AddOrUpdate(name, 1, (_, count) => count + 1));
        }
Console.WriteLine(JsonSerializer.Serialize(new { type = "environment", utc = DateTimeOffset.UtcNow,
    runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    candidate = typeof(BgapiDevice).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    port, baud, handshake = handshake.ToString(), capacity, seconds, responseSeconds, callers, interval,
    command = command is null ? "none (observe only)" : string.Join('.', command), definitions = xapi ?? "embedded" }));
device.Open(port, baud, handshake);
var consumer = Task.Factory.StartNew(() =>
{
    try
    {
        while (!cancellation.IsCancellationRequested)
        {
            // Count each claim immediately; cancellation must not hide a partially collected batch.
            if (device.EventQueue.GetEvent(TimeSpan.FromMilliseconds(100)) is not null)
                Interlocked.Increment(ref claims);
        }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
var workers = command is null ? Array.Empty<Task>() : Enumerable.Range(0, callers).Select(async _ =>
{
    try
    {
        while (!cancellation.IsCancellationRequested)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                var response = await device.SendCommandAsync(command[0], command[1], command[2], ct: cancellation.Token);
                outcomes.AddOrUpdate(response.Status.ToString(), 1, (_, count) => count + 1);
                Interlocked.Increment(ref commandCount);
                lock (latencyGate) maxCommandMs = Math.Max(maxCommandMs, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                outcomes.AddOrUpdate(error.GetType().Name, 1, (_, count) => count + 1);
                // A broken connection requires an explicit, recorded recovery, not a busy retry loop.
                cancellation.Cancel();
            }
            await Task.Delay(interval, cancellation.Token);
        }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
}).ToArray();
using var process = Process.GetCurrentProcess();
try
{
    while (!cancellation.IsCancellationRequested)
    {
        await Task.Delay(1000, cancellation.Token);
        Sample("sample");
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
finally
{
    // Finish pending calls before Dispose: the current candidate disposes the command semaphore.
    cancellation.Cancel();
    await Task.WhenAll(workers.Append(consumer));
    device.Close();
    Sample("final");
}
void Sample(string type)
{
    process.Refresh();
    double maximum;
    lock (latencyGate) maximum = maxCommandMs;
    Console.WriteLine(JsonSerializer.Serialize(new { type, utc = DateTimeOffset.UtcNow, subscriptions,
        claims = Interlocked.Read(ref claims), queued = device.EventQueue.Count,
        eventDrops = device.EventQueue.DroppedEventCount, byteDrops = device.DroppedByteCount,
        commands = Interlocked.Read(ref commandCount), outcomes, maxCommandEndToEndMs = maximum,
        managedLiveBytes = GC.GetTotalMemory(false), workingSetBytes = process.WorkingSet64,
        cpuSeconds = process.TotalProcessorTime.TotalSeconds, threads = process.Threads.Count,
        wireLoss = "unverified without sender counts/identities", eventLatency = "requires sender timing" }));
}
