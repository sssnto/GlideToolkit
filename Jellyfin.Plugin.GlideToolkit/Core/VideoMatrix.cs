using System.Buffers.Binary;

namespace Jellyfin.Plugin.GlideToolkit.Core;

/// Exact port of Glide's bounded ISO BMFF matrix edit. Only 36 bytes change.
public static class VideoMatrix
{
    public sealed record Patch(long FileSize, long Offset, byte[] Original, byte[] Replacement);
    private sealed record Box(string Type, long Payload, long End);
    public static Patch Inspect(Stream file, int turns)
    {
        if (turns is < 0 or > 3) throw new RotationException("invalid_angle");
        var movies = Boxes(file, 0, file.Length).Where(b => b.Type == "moov").ToArray();
        if (movies.Length != 1) throw new RotationException("invalid_container");
        var children = Boxes(file, movies[0].Payload, movies[0].End);
        if (children.Any(b => b.Type == "cmov")) throw new RotationException("unsupported_format");
        var videoHeaders = new List<Box>();
        foreach (var track in children.Where(b => b.Type == "trak"))
        {
            var parts = Boxes(file, track.Payload, track.End);
            var media = parts.Where(b => b.Type == "mdia").ToArray();
            if (media.Length != 1) throw new RotationException("invalid_container");
            var handlers = Boxes(file, media[0].Payload, media[0].End).Where(b => b.Type == "hdlr").ToArray();
            if (handlers.Length != 1 || handlers[0].End - handlers[0].Payload < 12) throw new RotationException("invalid_container");
            if (System.Text.Encoding.ASCII.GetString(Read(file, handlers[0].Payload + 8, 4)) != "vide") continue;
            var headers = parts.Where(b => b.Type == "tkhd").ToArray();
            if (headers.Length != 1) throw new RotationException("invalid_container");
            videoHeaders.Add(headers[0]);
        }
        if (videoHeaders.Count != 1) throw new RotationException(videoHeaders.Count > 1 ? "multiple_video_tracks" : "invalid_container");
        var header = videoHeaders[0];
        var version = Read(file, header.Payload, 1)[0];
        if (version > 1) throw new RotationException("invalid_container");
        var offset = header.Payload + (version == 1 ? 52 : 40);
        if (offset > header.End || header.End - offset < 44) throw new RotationException("invalid_container");
        var data = Read(file, offset, 44);
        var matrix = Enumerable.Range(0, 9).Select(i => (long)BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(i * 4, 4))).ToArray();
        var w = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(36, 4));
        var h = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(40, 4));
        if (w == 0 || h == 0 || matrix[2] != 0 || matrix[5] != 0 || matrix[8] != 1 << 30) throw new RotationException("unsupported_matrix");
        long a = matrix[0], b = matrix[1], c = matrix[3], d = matrix[4], tx = matrix[6], ty = matrix[7];
        if (new[] { a, b, c, d }.Any(v => Math.Abs(v) > 8 * 65536) || a * d - b * c == 0) throw new RotationException("unsupported_matrix");
        for (var i = 0; i < turns; i++) (a, b, c, d, tx, ty) = (-b, a, -d, c, -ty, tx);
        var corners = new (long X, long Y)[] { (0, 0), (w, 0), (0, h), (w, h) };
        tx -= corners.Min(p => (a * p.X + c * p.Y) / 65536 + tx);
        ty -= corners.Min(p => (b * p.X + d * p.Y) / 65536 + ty);
        var values = new[] { a, b, 0, c, d, 0, tx, ty, 1L << 30 };
        if (values.Any(v => v < int.MinValue || v > int.MaxValue)) throw new RotationException("unsupported_matrix");
        var output = new byte[36];
        for (var i = 0; i < 9; i++) BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(i * 4, 4), (int)values[i]);
        return new(file.Length, offset, data[..36], output);
    }
    public static void Apply(Stream file, Patch patch)
    {
        if (file.Length != patch.FileSize || !Read(file, patch.Offset, 36).SequenceEqual(patch.Original)) throw new RotationException("file_changed");
        file.Position = patch.Offset; file.Write(patch.Replacement); file.Flush();
    }
    private static List<Box> Boxes(Stream file, long from, long end)
    {
        var result = new List<Box>(); var offset = from;
        while (offset < end)
        {
            if (end - offset < 8 || result.Count >= 100000) throw new RotationException("invalid_container");
            var bytes = Read(file, offset, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(bytes); long header = 8;
            if (size == 1)
            {
                if (end - offset < 16) throw new RotationException("invalid_container");
                var large = BinaryPrimitives.ReadUInt64BigEndian(Read(file, offset + 8, 8));
                if (large > long.MaxValue) throw new RotationException("invalid_container");
                size = (long)large; header = 16;
            }
            else if (size == 0) size = end - offset;
            if (size < header || size > end - offset) throw new RotationException("invalid_container");
            result.Add(new(System.Text.Encoding.ASCII.GetString(bytes, 4, 4), offset + header, offset + size)); offset += size;
        }
        return result;
    }
    private static byte[] Read(Stream file, long offset, int count)
    {
        file.Position = offset; var bytes = new byte[count];
        try { file.ReadExactly(bytes); } catch (EndOfStreamException) { throw new RotationException("invalid_container"); }
        return bytes;
    }
}
