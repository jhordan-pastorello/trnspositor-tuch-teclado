using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace DeskLink;

internal sealed class LinkClient
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DESKLINK_DISCOVER1");
    private readonly Config _cfg;
    private readonly BlockingCollection<byte[]> _queue = new(new ConcurrentQueue<byte[]>(), 8192);
    private readonly object _writeLock = new();

    private volatile bool _connected;
    private volatile int _remoteW = 2560;
    private volatile int _remoteH = 1440;
    private TcpClient? _tcp;
    private SslStream? _ssl;

    public LinkClient(Config cfg) => _cfg = cfg;

    public bool IsConnected => _connected;
    public int RemoteWidth => _remoteW;
    public int RemoteHeight => _remoteH;

    public event Action<byte, byte[]>? OnFrame;

    public void Send(byte[] frame)
    {
        if (!_connected) return;
        _queue.TryAdd(frame);
    }

    public void Start()
    {
        var t = new Thread(Run) { IsBackground = true, Name = "desklink-link" };
        t.Start();
    }

    private void Run()
    {
        while (true)
        {
            try
            {
                var (host, port) = Discover();
                Connect(host, port);
                _connected = true;
                Log.Write($"conectado a {host}:{port} (remoto {_remoteW}x{_remoteH})");
                Pump();
            }
            catch (Exception ex)
            {
                Log.Write("link: " + ex.Message);
            }

            if (_connected) Log.Write("desconectado");
            _connected = false;
            Drain();

            try { _ssl?.Dispose(); } catch { }
            try { _tcp?.Dispose(); } catch { }
            _ssl = null;
            _tcp = null;
            Thread.Sleep(2000);
        }
    }

    private void Drain()
    {
        while (_queue.TryTake(out _)) { }
    }

    // ---------------------------------------------------------- descoberta --
    private (string host, int port) Discover()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.ServerHost))
            return (_cfg.ServerHost, _cfg.ServerPort);

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        udp.Client.ReceiveTimeout = 700;

        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                udp.Send(Magic, Magic.Length, new IPEndPoint(IPAddress.Broadcast, _cfg.DiscoveryPort));
            }
            catch (Exception ex)
            {
                Log.Write("broadcast falhou: " + ex.Message);
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(900);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    IPEndPoint? from = null;
                    var data = udp.Receive(ref from!);
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    var port = root.TryGetProperty("port", out var p) ? p.GetInt32() : _cfg.ServerPort;
                    if (root.TryGetProperty("w", out var w)) _remoteW = w.GetInt32();
                    if (root.TryGetProperty("h", out var h)) _remoteH = h.GetInt32();
                    Log.Write($"descoberto em {from!.Address}:{port}");
                    return (from.Address.ToString(), port);
                }
                catch (SocketException)
                {
                    break;
                }
            }
        }

        throw new IOException("servidor DeskLink nao encontrado na rede");
    }

    // ------------------------------------------------------------- conexao --
    private void Connect(string host, int port)
    {
        _tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 8000, SendTimeout = 8000 };
        _tcp.Connect(host, port);

        _ssl = new SslStream(_tcp.GetStream(), false, ValidateCert);
        _ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
        {
            TargetHost = "desklink",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        });

        var sw = Native.GetSystemMetrics(Native.SM_CXSCREEN);
        var sh = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        Write(Proto.HelloMsg(Environment.MachineName, sw, sh));

        var (type, payload) = ReadFrame(_ssl);
        if (type != Proto.HelloAck)
            throw new IOException("resposta inesperada do servidor: " + type);

        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.TryGetProperty("w", out var w)) _remoteW = w.GetInt32();
        if (doc.RootElement.TryGetProperty("h", out var h)) _remoteH = h.GetInt32();
    }

    private bool ValidateCert(object? sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (cert == null) return false;
        var fp = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
        var pinned = _cfg.ServerFingerprint;
        if (string.IsNullOrEmpty(pinned))
        {
            _cfg.ServerFingerprint = fp;
            _cfg.Save();
            Log.Write("certificado confiado e fixado (TOFU): " + fp);
            return true;
        }
        if (!string.Equals(pinned, fp, StringComparison.OrdinalIgnoreCase))
        {
            Log.Write($"CERTIFICADO DIFERENTE: esperado {pinned}, recebido {fp}");
            return false;
        }
        return true;
    }

    private void Pump()
    {
        var lastBeat = DateTime.UtcNow;
        while (true)
        {
            if (_queue.TryTake(out var frame, 30))
                Write(frame);

            if ((DateTime.UtcNow - lastBeat).TotalSeconds >= 5)
            {
                Write(Proto.HeartbeatMsg());
                lastBeat = DateTime.UtcNow;
            }

            var sock = _tcp!.Client;
            if (sock.Poll(0, SelectMode.SelectRead))
            {
                if (sock.Available == 0)
                    throw new IOException("servidor encerrou a conexao");

                var (type, payload) = ReadFrame(_ssl!);
                if (type == Proto.Clipboard || type == Proto.ClipboardImage)
                    OnFrame?.Invoke(type, payload);
            }
        }
    }

    private void Write(byte[] frame)
    {
        lock (_writeLock)
        {
            _ssl!.Write(frame);
            _ssl.Flush();
        }
    }

    private static (byte type, byte[] payload) ReadFrame(Stream s)
    {
        var header = ReadExact(s, 5);
        var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1));
        var payload = len > 0 ? ReadExact(s, len) : Array.Empty<byte>();
        return (header[0], payload);
    }

    private static byte[] ReadExact(Stream s, int count)
    {
        var buf = new byte[count];
        int off = 0;
        while (off < count)
        {
            int n = s.Read(buf, off, count - off);
            if (n <= 0) throw new IOException("conexao fechada pelo servidor");
            off += n;
        }
        return buf;
    }
}
