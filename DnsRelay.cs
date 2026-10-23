using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace NetPilot;

/// <summary>Loopback-only UDP/TCP forwarding, bounded to 64 requests/connections; never logs query names.</summary>
public sealed class DnsRelay : IAsyncDisposable
{
    private readonly DnsTransport _transport;
    private readonly Func<ChannelProfile?> _getActive;
    private readonly SemaphoreSlim _capacity = new(64, 64);
    private readonly object _sync = new();
    private readonly HashSet<Task> _pending = [];
    private readonly List<Socket> _sockets = [];
    private readonly List<Task> _listeners = [];
    private CancellationTokenSource? _lifetime;
    private CancellationTokenRegistration _closeOnCancellation;
    private volatile bool _running;
    private bool _disposed;

    public DnsRelay(DnsTransport transport, Func<ChannelProfile?> getActive)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _getActive = getActive ?? throw new ArgumentNullException(nameof(getActive));
    }
    public bool IsRunning => _running && _lifetime is { IsCancellationRequested: false };

    public Task StartAsync(int port = 53, bool ipv6 = true, CancellationToken token = default)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lifetime != null) throw new InvalidOperationException("Этот экземпляр DNS relay уже запускался.");
            var created = new List<(Socket Socket, bool Tcp)>();
            try
            {
                void Bind(IPAddress address, bool tcp)
                {
                    var socket = new Socket(address.AddressFamily, tcp ? SocketType.Stream : SocketType.Dgram, tcp ? ProtocolType.Tcp : ProtocolType.Udp);
                    created.Add((socket, tcp));
                    socket.ExclusiveAddressUse = true;
                    if (address.AddressFamily == AddressFamily.InterNetworkV6) socket.DualMode = false;
                    socket.Bind(new IPEndPoint(address, port));
                    if (tcp) socket.Listen(32);
                }
                Bind(IPAddress.Loopback, false); Bind(IPAddress.Loopback, true);
                if (ipv6) { Bind(IPAddress.IPv6Loopback, false); Bind(IPAddress.IPv6Loopback, true); }
                _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                _sockets.AddRange(created.Select(x => x.Socket));
                var lifetimeToken = _lifetime.Token;
                _running = true;
                var boundSockets = created.Select(x => x.Socket).ToArray();
                _closeOnCancellation = lifetimeToken.Register(() =>
                {
                    _running = false;
                    foreach (var socket in boundSockets) socket.Dispose();
                });
                foreach (var item in created)
                    _listeners.Add(item.Tcp ? AcceptLoopAsync(item.Socket, lifetimeToken) : DatagramLoopAsync(item.Socket, lifetimeToken));
                return Task.CompletedTask;
            }
            catch
            {
                foreach (var item in created) item.Socket.Dispose();
                _closeOnCancellation.Dispose(); _lifetime?.Dispose(); _lifetime = null; _running = false; _sockets.Clear(); _listeners.Clear();
                throw;
            }
        }
    }

    private async Task DatagramLoopAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[DnsWire.MaxMessage];
        EndPoint any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, token).ConfigureAwait(false);
                if (read.RemoteEndPoint is not IPEndPoint peer || !IPAddress.IsLoopback(peer.Address)) continue;
                var query = buffer[..read.ReceivedBytes];
                try { DnsWire.ValidateQuery(query); }
                catch (InvalidDataException) { continue; } // Do not reflect responses, malformed packets, or unsupported opcodes.
                if (!_capacity.Wait(0))
                {
                    await socket.SendToAsync(DnsWire.Failure(query), SocketFlags.None, peer, token).ConfigureAwait(false);
                    continue;
                }
                Track(ProcessDatagramAsync(socket, peer, query, token));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException || (ex is SocketException && token.IsCancellationRequested)) { }
        catch (SocketException) { StopOnListenerFailure(); }
    }

    private async Task ProcessDatagramAsync(Socket socket, IPEndPoint peer, byte[] query, CancellationToken token)
    {
        try
        {
            var response = await ForwardAsync(query, token).ConfigureAwait(false);
            if (response.Length > DnsWire.UdpPayloadSize(query)) response = DnsWire.Truncate(query, response);
            await socket.SendToAsync(response, SocketFlags.None, peer, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException or InvalidDataException) { }
        finally { _capacity.Release(); }
    }

    private async Task AcceptLoopAsync(Socket socket, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await socket.AcceptAsync(token).ConfigureAwait(false);
                if (client.RemoteEndPoint is not IPEndPoint peer || !IPAddress.IsLoopback(peer.Address) || !_capacity.Wait(0))
                {
                    client.Dispose(); continue;
                }
                client.NoDelay = true;
                Track(ProcessConnectionAsync(client, token));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException || (ex is SocketException && token.IsCancellationRequested)) { }
        catch (SocketException) { StopOnListenerFailure(); }
    }

    private async Task ProcessConnectionAsync(Socket client, CancellationToken token)
    {
        using (client)
        using (var stream = new NetworkStream(client, ownsSocket: false))
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            lifetime.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var prefix = new byte[2];
                for (var count = 0; count < 32; count++)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    idle.CancelAfter(TimeSpan.FromSeconds(10));
                    // ReadAtLeast handles graceful EOF and fragmented two-byte prefixes.
                    var read = await stream.ReadAtLeastAsync(prefix, 2, throwOnEndOfStream: false, idle.Token).ConfigureAwait(false);
                    if (read == 0) return;
                    if (read != 2) throw new InvalidDataException("Оборванный DNS TCP префикс.");
                    var length = BinaryPrimitives.ReadUInt16BigEndian(prefix);
                    if (length < 12) throw new InvalidDataException("Слишком короткий DNS TCP запрос.");
                    var query = new byte[length]; await stream.ReadExactlyAsync(query, idle.Token).ConfigureAwait(false);
                    DnsWire.ValidateQuery(query);
                    var response = await ForwardAsync(query, lifetime.Token).ConfigureAwait(false);
                    BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)response.Length);
                    await stream.WriteAsync(prefix, lifetime.Token).ConfigureAwait(false);
                    await stream.WriteAsync(response, lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException or InvalidDataException) { }
            finally { _capacity.Release(); }
        }
    }

    private async Task<byte[]> ForwardAsync(byte[] query, CancellationToken token)
    {
        try
        {
            var profile = _getActive();
            if (profile == null) return DnsWire.Failure(query);
            var response = await _transport.ExchangeAsync(profile, query, token).ConfigureAwait(false);
            DnsWire.ValidateResponse(query, response);
            return response;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or SocketException or OperationCanceledException or ArgumentException or ObjectDisposedException)
        {
            return DnsWire.Failure(query);
        }
    }

    private void Track(Task task)
    {
        lock (_sync) _pending.Add(task);
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            lock (_sync) _pending.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void StopOnListenerFailure()
    {
        _running = false;
        try { _lifetime?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] listeners;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; _running = false;
            _lifetime?.Cancel();
            foreach (var socket in _sockets) socket.Dispose();
            listeners = _listeners.ToArray();
        }
        await Task.WhenAll(listeners).ConfigureAwait(false);
        Task[] pending; lock (_sync) pending = _pending.ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
        _closeOnCancellation.Dispose(); _lifetime?.Dispose(); _capacity.Dispose();
    }
}
