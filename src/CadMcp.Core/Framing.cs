using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CadMcp.Contracts;

namespace CadMcp.Core;

// A 32-bit little-endian byte length precedes each UTF-8 JSON message.
// Reads are exact: pipe reads can return less than a complete frame.
public static class Framing
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    public static async Task WriteAsync(Stream stream, object message, CancellationToken token)
    {
        var body = Utf8.GetBytes(Wire.Serialize(message));
        if (body.Length > Wire.MaxFrameBytes) throw new CadFault("FRAME_TOO_LARGE", "El mensaje supera 4 MiB.");
        var header = BitConverter.GetBytes(body.Length);
        await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
        await stream.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken token) where T : class
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, true, token).ConfigureAwait(false)) return null;
        var length = BitConverter.ToInt32(header, 0);
        if (length < 1 || length > Wire.MaxFrameBytes)
            throw new CadFault("INVALID_FRAME", "Longitud de mensaje inválida.");
        var body = new byte[length];
        await ReadExactlyAsync(stream, body, false, token).ConfigureAwait(false);
        return Wire.Deserialize<T>(Utf8.GetString(body));
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, bool allowEof, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, token).ConfigureAwait(false);
            if (read == 0)
            {
                if (allowEof && offset == 0) return false;
                throw new EndOfStreamException("Conexión cerrada durante un mensaje.");
            }
            offset += read;
        }
        return true;
    }
}
