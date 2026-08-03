using FluentAssertions;
using RenPack.Services;
using Xunit;

namespace RenPack.Tests;

public sealed class MediaPlaybackServiceTests
{
    private static byte[] FakeJpeg(int payloadSize, byte fillByte = 0x00)
    {
        var b = new byte[payloadSize + 4];
        b[0] = 0xFF; b[1] = 0xD8;                              // SOI
        for (int i = 2; i < b.Length - 2; i++) b[i] = fillByte;
        b[^2] = 0xFF; b[^1] = 0xD9;                            // EOI
        return b;
    }

    [Fact]
    public async Task JpegStreamReader_yields_multiple_frames_from_concatenated_stream()
    {
        var f1 = FakeJpeg(10, 0xAA);
        var f2 = FakeJpeg(20, 0xBB);
        var f3 = FakeJpeg(30, 0xCC);
        var combined = new byte[f1.Length + f2.Length + f3.Length];
        Buffer.BlockCopy(f1, 0, combined, 0, f1.Length);
        Buffer.BlockCopy(f2, 0, combined, f1.Length, f2.Length);
        Buffer.BlockCopy(f3, 0, combined, f1.Length + f2.Length, f3.Length);

        using var ms = new MemoryStream(combined);
        var frames = new List<byte[]>();
        await foreach (var frame in MediaPlaybackService.JpegStreamReader.ReadAsync(ms, TestContext.Current.CancellationToken))
            frames.Add(frame);

        frames.Should().HaveCount(3);
        frames[0].Should().Equal(f1);
        frames[1].Should().Equal(f2);
        frames[2].Should().Equal(f3);
    }

    [Fact]
    public async Task JpegStreamReader_handles_partial_reads_across_frame_boundary()
    {
        // Ein Stream der frame-fuer-frame liest, wo Frame-Grenzen genau
        // zwischen Read-Chunks liegen — das reale Verhalten wenn ffmpeg
        // per Pipe streamt.
        var f1 = FakeJpeg(50, 0xAA);
        var f2 = FakeJpeg(30, 0xBB);
        var combined = new byte[f1.Length + f2.Length];
        Buffer.BlockCopy(f1, 0, combined, 0, f1.Length);
        Buffer.BlockCopy(f2, 0, combined, f1.Length, f2.Length);

        // Chunked-Stream — 20 bytes at a time. Grenze mitten in f1 und dann in f2.
        var stream = new ChunkedStream(combined, chunkSize: 20);
        var frames = new List<byte[]>();
        await foreach (var frame in MediaPlaybackService.JpegStreamReader.ReadAsync(stream, TestContext.Current.CancellationToken))
            frames.Add(frame);

        frames.Should().HaveCount(2);
        frames[0].Should().Equal(f1);
        frames[1].Should().Equal(f2);
    }

    [Fact]
    public async Task JpegStreamReader_empty_stream_yields_nothing()
    {
        using var ms = new MemoryStream(Array.Empty<byte>());
        var frames = new List<byte[]>();
        await foreach (var frame in MediaPlaybackService.JpegStreamReader.ReadAsync(ms, TestContext.Current.CancellationToken))
            frames.Add(frame);
        frames.Should().BeEmpty();
    }

    [Fact]
    public async Task JpegStreamReader_ignores_incomplete_trailing_frame()
    {
        // Vollstaendiger f1, dann angefangenes f2 (SOI, kein EOI) → nur f1.
        var f1 = FakeJpeg(10, 0xAA);
        var incomplete = new byte[] { 0xFF, 0xD8, 0x00, 0x11, 0x22 };
        var combined = new byte[f1.Length + incomplete.Length];
        Buffer.BlockCopy(f1, 0, combined, 0, f1.Length);
        Buffer.BlockCopy(incomplete, 0, combined, f1.Length, incomplete.Length);

        using var ms = new MemoryStream(combined);
        var frames = new List<byte[]>();
        await foreach (var frame in MediaPlaybackService.JpegStreamReader.ReadAsync(ms, TestContext.Current.CancellationToken))
            frames.Add(frame);

        frames.Should().HaveCount(1);
        frames[0].Should().Equal(f1);
    }

    /// <summary>Test-Stream der einen ByteBuffer in fixen Chunks liefert —
    /// simuliert das reale Verhalten eines ffmpeg-Pipes wo Reads oft partial
    /// zurueckkommen.</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private int _pos;

        public ChunkedStream(byte[] data, int chunkSize)
        {
            _data = data; _chunkSize = chunkSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int available = _data.Length - _pos;
            int n = Math.Min(Math.Min(count, _chunkSize), available);
            if (n <= 0) return 0;
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
