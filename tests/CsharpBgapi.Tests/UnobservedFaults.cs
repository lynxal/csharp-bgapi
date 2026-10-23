using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace CsharpBgapi.Tests;

/// <summary>
/// Runs a scenario, finalizes what it left unreferenced, and reports whether a faulted task carrying
/// <c>marker</c> went unobserved. The scenario must not leave the task reachable from the caller.
/// </summary>
/// <remarks>
/// Collection is retried for up to a second rather than a fixed number of rounds: measured, the
/// abandoned task took between 2 and 9 rounds (under 20 ms) to be finalized. A false result means
/// nothing was reported within that window.
/// </remarks>
internal static class UnobservedFaults
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    public static bool Raised(string marker, Action scenario)
    {
        bool seen = false;
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs args)
        {
            if (!args.Exception.InnerExceptions.Any(error => error.Message == marker)) return;
            args.SetObserved();
            seen = true;
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            Run(scenario);
            var clock = Stopwatch.StartNew();
            while (!seen && clock.Elapsed < Window)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Yield();
            }
            return seen;
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(Action scenario) => scenario();
}
