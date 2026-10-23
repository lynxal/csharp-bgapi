// Compiled unchanged into fresh consumers of the released baseline and local candidate.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CsharpBgapi;
using CsharpBgapi.Events;
using CsharpBgapi.Protocol;

using var device = new BgapiDevice();
device.LoadDefaultXapis();
var definition = device.Definitions.FindAllClasses("bt").Single(c => c.Name == "user").Events.Single(e => e.Name == "message_to_host");
var header = new BgapiHeader((byte)(0x80 | device.Definitions.GetDeviceId("bt") << 3), 17, definition.ClassIndex, definition.Index);
byte[] frame = [.. header.ToBytes(), 16, .. new byte[16]];
var message = device.Protocol.DecodeMessage(frame);
var ignored = new BgapiMessage(header, ReadOnlyMemory<byte>.Empty) { EventName = "unselected_synthetic_event" };
var rows = new List<object>();
foreach (string scenario in new[] { "codec", "queue", "backlog", "ignored", "subscriptions" })
{
    for (int repetition = -1; repetition < 5; repetition++)
    {
        const int count = 100000;
        var queue = new BgapiEventQueue();
        var selector = new NameParamSelector(message.EventName!);
        if (scenario == "backlog") for (int i = 0; i < 100; i++) queue.Enqueue(ignored);
        Action<BgapiMessage> callback = _ => { };
        long before = GC.GetTotalAllocatedBytes(true);
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++)
        {
            switch (scenario)
            {
                case "codec": device.Protocol.DecodeMessage(frame); break;
                case "queue": case "backlog":
                    queue.Enqueue(message);
                    if (queue.WaitEvent(selector, TimeSpan.FromSeconds(1)).Count != 1) throw new Exception("event accounting");
                    break;
                case "ignored":
                    queue.Enqueue(ignored);
                    if (i % 100 == 99) queue.Clear();
                    break;
                case "subscriptions": device.Subscribe(message.EventName!, callback); device.Unsubscribe(message.EventName!, callback); break;
            }
        }
        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        if (repetition >= 0) rows.Add(new { scenario, repetition, count, seconds, messagesPerSecond = count / seconds,
            allocatedBytesPerMessage = (double)allocated / count });
    }
}
Console.WriteLine(JsonSerializer.Serialize(new { version = typeof(BgapiDevice).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(), warmup = "100000 operations per scenario", repetitions = 5,
    scope = "common public codec/queue/subscription registration only; no serial/command comparison", rows }, new JsonSerializerOptions { WriteIndented = true }));
