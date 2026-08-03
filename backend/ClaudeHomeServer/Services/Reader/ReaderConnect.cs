using System.Net;
using System.Net.Sockets;

namespace ClaudeHomeServer.Services.Reader;

/// <summary>Сырое TCP-подключение для <c>ConnectCallback</c> клиента "link-reader" (см. Program.cs).</summary>
internal static class ReaderConnect
{
    public static async ValueTask<Stream> RawAsync(EndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
