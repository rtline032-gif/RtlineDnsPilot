#if NET6_0
using System.Net;
using System.Net.Sockets;

namespace NetPilot.Tests;

// TcpListener became IDisposable after .NET 6. Keep fixture teardown on both runtimes.
internal sealed class LegacyTestTcpListener(IPAddress address, int port) : TcpListener(address, port), IDisposable
{
    public void Dispose() => Stop();
}
#endif
