using Dam.Application.Abstractions;

namespace Dam.Infrastructure.Knowledge;

/// <summary>
/// A read-only, seekable view of a stored object that fetches ranges on demand, so a zip can be opened (its directory is at the end, its entries are read one at a
/// time) without downloading a whole bundle. Reads block on the store's async call, so use it from a worker thread (never from one with a synchronisation context).
/// </summary>
public sealed class ObjectRangeStream(IObjectStore store, string bucket, string key, long length, CancellationToken ct) : Stream
{
    private const int BlockSize = 256 * 1024;
    private long _position;
    private long _blockStart = -1;
    private byte[] _block = [];

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (count > 0 && _position < length)
        {
            if (_blockStart < 0 || _position < _blockStart || _position >= _blockStart + _block.Length)
            {
                var start = _position / BlockSize * BlockSize;
                var want = Math.Min(BlockSize, length - start);
                _block = store.ReadRangeAsync(bucket, key, start, want, ct).GetAwaiter().GetResult();
                _blockStart = start;
                if (_block.Length == 0) break;
            }
            var inBlock = (int)(_position - _blockStart);
            var n = Math.Min(count, _block.Length - inBlock);
            if (n <= 0) break;
            Buffer.BlockCopy(_block, inBlock, buffer, offset, n);
            offset += n;
            count -= n;
            _position += n;
            total += n;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        };
        if (target < 0) throw new IOException("Cannot seek before the start of the object.");
        _position = Math.Min(target, length);
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
