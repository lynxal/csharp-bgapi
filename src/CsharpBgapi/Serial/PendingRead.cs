namespace CsharpBgapi.Serial;

/// <summary>
/// At most one read in flight against a stream, and that read is never cancelled. A caller that
/// stops waiting leaves it pending and collects it on its next call, so bytes the stream hands
/// over late still reach the caller.
/// </summary>
/// <remarks>
/// <see cref="System.IO.Ports.SerialPort.Read(byte[], int, int)"/> with a timeout loses bytes on
/// Linux. SerialStream.Unix serves the timed read as a cancellable request: its I/O loop peeks the
/// request while it is still open, reads the fd INTO the request's buffer, then completes it with
/// TrySetResult — a no-op once the timeout has cancelled the request in between. The bytes have
/// left the kernel and reach nobody. A reader polling with a short timeout ends every quiet gap in a
/// timed-out read, so a frame arriving at that instant vanishes whole or loses its head: Hub
/// firmware job 196 lost query_block_status_complete at block 433 of 553 on 2026-10-22, with only
/// a 3-byte resync to show for it, and restarted a 78% transfer from block 0. Never cancelling the
/// read removes that window rather than narrowing it.
///
/// Each instance owns its buffer: a read abandoned on a closed port may still complete into it, so
/// a replacement must never share it.
/// </remarks>
internal sealed class PendingRead
{
    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private Task<int>? _inFlight;

    public PendingRead(Stream stream, int bufferSize)
    {
        _stream = stream;
        _buffer = new byte[bufferSize];
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the in-flight read, starting one when none is.
    /// </summary>
    /// <returns>
    /// The bytes the read delivered — empty when it completed with none — or null while it is still
    /// in flight, in which case it stays in flight for the next call. The memory is valid until the
    /// next call. A faulted read rethrows its original exception once; the call after that starts a
    /// fresh read.
    /// </returns>
    public ReadOnlyMemory<byte>? TryTake(TimeSpan timeout)
    {
        var read = _inFlight ??= _stream.ReadAsync(_buffer, 0, _buffer.Length, CancellationToken.None);

        try
        {
            if (!read.Wait(timeout))
                return null;
        }
        catch (AggregateException)
        {
            // Completed by faulting; GetResult below rethrows the original exception unwrapped.
        }

        _inFlight = null;
        int count = read.GetAwaiter().GetResult();
        return _buffer.AsMemory(0, count);
    }

    /// <summary>
    /// Drops the in-flight read without waiting for it. Its eventual fault — the usual end of a read
    /// on a port that has since closed — is observed so it never surfaces as an unobserved task
    /// exception.
    /// </summary>
    public void Abandon()
    {
        _inFlight?.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _inFlight = null;
    }
}
