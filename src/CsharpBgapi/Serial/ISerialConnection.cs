using System.IO.Ports;

namespace CsharpBgapi.Serial;

// Keep the OS boundary replaceable so validation drives the real connector and reader loop.
internal interface ISerialConnection : IDisposable
{
    bool IsOpen { get; }
    Stream BaseStream { get; }
    int ReadTimeout { set; }
    int WriteTimeout { set; }
    Handshake Handshake { get; set; }
    bool DtrEnable { set; }
    bool RtsEnable { set; }
    void Open();
    void Close();
    void Write(byte[] buffer, int offset, int count);
}

internal sealed class SerialConnection(string portName, int baudRate) : ISerialConnection
{
    private readonly SerialPort _port = new(portName, baudRate, Parity.None, 8, StopBits.One);
    public bool IsOpen => _port.IsOpen;
    public Stream BaseStream => _port.BaseStream;
    public int ReadTimeout { set => _port.ReadTimeout = value; }
    public int WriteTimeout { set => _port.WriteTimeout = value; }
    public Handshake Handshake { get => _port.Handshake; set => _port.Handshake = value; }
    public bool DtrEnable { set => _port.DtrEnable = value; }
    public bool RtsEnable { set => _port.RtsEnable = value; }
    public void Open() => _port.Open();
    public void Close() => _port.Close();
    public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);
    public void Dispose() => _port.Dispose();
}
