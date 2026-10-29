namespace PamisWang.ClaudeAgentSdk.Tests;

/// <summary>每次讀取只給一個預先切好的片段，模擬管線一次只送來一部分資料。</summary>
internal sealed class ChunkedStream(IEnumerable<byte[]> chunks) : Stream
{
    private readonly Queue<byte[]> _chunks = new(chunks);
    private byte[] _current = [];
    private int _offset;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(Span<byte> buffer)
    {
        if (_offset == _current.Length)
        {
            if (!_chunks.TryDequeue(out var next))
            {
                return 0;
            }

            _current = next;
            _offset = 0;
        }

        // 一次最多給到這個片段的結尾，不跨到下一個片段
        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsSpan(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
