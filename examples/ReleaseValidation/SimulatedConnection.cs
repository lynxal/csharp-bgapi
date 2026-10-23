using System.Collections.Concurrent;
using System.IO.Ports;
using System.Threading.Channels;
using CsharpBgapi.Serial;

namespace CsharpBgapi.Validation;

// Byte-stream emulator shared by deterministic tests and the explicitly invoked load driver.
// The production PendingRead, accumulator, decoder and device reader remain in the path.
internal sealed class SimulatedConnection : Stream, ISerialConnection
{
    private readonly Channel<Func<byte[]>> _input = Channel.CreateUnbounded<Func<byte[]>>();
    private byte[] _chunk = [];
    private int _offset;
    private int _activeReads;
    private int _activeWrites;
    private volatile bool _open;
    public bool IsOpen => _open;
    public Task<int>? ControlledRead { get; init; }
    public Stream BaseStream => this;
    public Handshake Handshake { get; set; }
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }
    public override int ReadTimeout { get; set; }
    public override int WriteTimeout { get; set; }
    public bool Disposed { get; private set; }
    public bool CompleteOnClose { get; set; } = true;
    public Action? OnOpen { get; set; }
    public Action<byte[]>? OnWrite { get; set; }
    public ConcurrentQueue<byte[]> Writes { get; } = new();
    public SemaphoreSlim ReadStarted { get; } = new(0);
    public int ReadCalls;
    public int OverlappingReads;
    public int OverlappingWrites;
    public int CancellableReads;
    public void Open() { OnOpen?.Invoke(); _open = true; }
    public override void Close()
    {
        _open = false;
        if (CompleteOnClose) _input.Writer.TryComplete();
    }
    public void Inject(byte[] bytes) => _input.Writer.TryWrite(() => bytes);
    public void Fault(Exception exception) => _input.Writer.TryWrite(() => throw exception);
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _activeReads) != 1) Interlocked.Increment(ref OverlappingReads);
        if (cancellationToken.CanBeCanceled) Interlocked.Increment(ref CancellableReads);
        Interlocked.Increment(ref ReadCalls);
        ReadStarted.Release();
        try
        {
            if (ControlledRead is not null) return await ControlledRead.ConfigureAwait(false);
            if (_offset == _chunk.Length)
            {
                if (!await _input.Reader.WaitToReadAsync(cancellationToken)) return 0;
                _chunk = (await _input.Reader.ReadAsync(cancellationToken))();
                _offset = 0;
            }
            int length = Math.Min(count, _chunk.Length - _offset);
            _chunk.AsSpan(_offset, length).CopyTo(buffer.AsSpan(offset));
            _offset += length;
            return length;
        }
        finally { Interlocked.Decrement(ref _activeReads); }
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (Interlocked.Increment(ref _activeWrites) != 1) Interlocked.Increment(ref OverlappingWrites);
        try
        {
            var bytes = buffer.AsSpan(offset, count).ToArray();
            Writes.Enqueue(bytes);
            OnWrite?.Invoke(bytes);
        }
        finally { Interlocked.Decrement(ref _activeWrites); }
    }
    protected override void Dispose(bool disposing) { Disposed = true; Close(); base.Dispose(disposing); }
    void IDisposable.Dispose() => Dispose(true);
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
