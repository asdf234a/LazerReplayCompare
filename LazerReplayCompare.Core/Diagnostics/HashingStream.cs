using System.Security.Cryptography;

namespace LazerReplayCompare;

/// <summary>
/// Passes reads and writes through to another stream while hashing (SHA-256) every byte that goes by, so a file can be
/// serialized or parsed straight from disk, in small chunks, and still compared by content with what was there before.
/// </summary>
public sealed class HashingStream : Stream
{
    private readonly Stream inner;
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[]? result;

    public HashingStream(Stream inner)
    {
        this.inner = inner;
    }

    /// <summary>The hash of everything read or written so far (taken once; the stream is then only good for closing).</summary>
    public byte[] Hash => result ??= hash.GetHashAndReset();

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        if (read > 0)
            hash.AppendData(buffer[..read]);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        hash.AppendData(buffer);
        inner.Write(buffer);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            hash.Dispose();
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
