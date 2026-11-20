using System.IO.Compression;
using System.Text.Json;

namespace TurboTick.Networking;

/// <summary>Packs an authoritative frame for the wire, compressed.
///
/// The frame is the bulk of the traffic: a 41-colonist colony produces about 11 KB per frame, and at 60 frames per
/// second with two replicas that is 10-11 Mbps of upload - more than a typical home connection has to spare. Measured
/// with Brotli at <see cref="CompressionLevel.Fastest"/> the same payload drops to 0.6 KB from 2.0 KB, roughly 70%.
///
/// Why a nested compressed field instead of compressing the whole envelope: this project has **four** independent
/// frame encoders (`WireProtocol`, `AdminVerification`, and two test fixtures). A flag byte on the outer frame shifted
/// every other encoder's frames by one byte and broke three tests, which is a protocol change across all four. Doing it
/// inside the message leaves the outer framing untouched and only the two call sites that build and read a frame care.
/// </summary>
internal static class FrameCodec
{
    /// <summary>Below this the compression header costs more than it saves.</summary>
    private const int Threshold = 512;

    /// <summary>Returns the message with either <c>Frame</c> or <c>FrameZip</c> set, never both.</summary>
    internal static WireMessage Pack(WireMessage message)
    {
        if (message.Frame is not { } frame) return message;
        try
        {
            var raw = JsonSerializer.SerializeToUtf8Bytes(frame, JsonWire.Options);
            if (raw.Length < Threshold) return message;
            using var output = new MemoryStream(raw.Length / 3 + 64);
            using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
                brotli.Write(raw, 0, raw.Length);
            return message with { Frame = null, FrameZip = output.ToArray() };
        }
        catch (Exception)
        {
            // Compression is an optimisation: if it fails, send the plain frame rather than nothing.
            return message;
        }
    }

    /// <summary>Restores <c>Frame</c> from <c>FrameZip</c>. Returns false when the compressed payload cannot be read,
    /// so the caller can say so instead of silently dropping a frame - a replica that quietly stops receiving looks
    /// exactly like a replica that is up to date.</summary>
    internal static bool TryUnpack(WireMessage message, out WireMessage result, out string? error)
    {
        result = message;
        error = null;
        if (message.Frame is not null || message.FrameZip is not { Length: > 0 } zip) return true;
        try
        {
            using var input = new MemoryStream(zip);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(zip.Length * 3);
            brotli.CopyTo(output);
            var frame = JsonSerializer.Deserialize<AuthoritativeFrame>(output.ToArray(), JsonWire.Options);
            if (frame is null)
            {
                error = "Compressed frame decoded to null.";
                return false;
            }
            result = message with { Frame = frame, FrameZip = null };
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }
}
