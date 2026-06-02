using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;

namespace Voxie.Services;

public sealed class VrChatOscChatboxService : IDisposable
{
    private static readonly TimeSpan ChunkDelay = TimeSpan.FromSeconds(15);
    private readonly UdpClient _udpClient = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public async Task<int> SendAsync(string text, CancellationToken cancellationToken = default)
    {
        var chunks = VrChatChatboxMessageSplitter.Split(text);
        if (chunks.Count == 0)
            return 0;

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            for (var index = 0; index < chunks.Count; index++)
            {
                var packet = BuildChatboxPacket(chunks[index]);
                await _udpClient.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, 9000));
                if (index < chunks.Count - 1)
                    await Task.Delay(ChunkDelay, cancellationToken);
            }
        }
        finally
        {
            _sendLock.Release();
        }

        return chunks.Count;
    }

    public void Dispose()
    {
        _udpClient.Dispose();
        _sendLock.Dispose();
    }

    private static byte[] BuildChatboxPacket(string text)
    {
        using var stream = new MemoryStream();
        WriteOscString(stream, "/chatbox/input");
        WriteOscString(stream, ",sTT");
        WriteOscString(stream, text);
        return stream.ToArray();
    }

    private static void WriteOscString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);
        while (stream.Length % 4 != 0)
            stream.WriteByte(0);
    }
}
