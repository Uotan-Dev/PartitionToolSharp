using System.Buffers.Binary;

namespace LibSparseSharp;
/// <summary>
/// 将 SparseFile 包装为只读 Stream，允许随机访问
/// </summary>
public class SparseStream(SparseFile sparseFile) : Stream
{
    private readonly SparseFile _sparseFile = sparseFile;
    private readonly long _length = (long)sparseFile.Header.TotalBlocks * sparseFile.Header.BlockSize;
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > _length)
            {
                throw new ArgumentOutOfRangeException(nameof(Position));
            }

            _position = value;
        }
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length)
        {
            return 0;
        }

        var toRead = (int)Math.Min(count, _length - _position);
        var totalRead = 0;
        Span<byte> fillValue = stackalloc byte[4];

        while (totalRead < toRead)
        {
            var (chunk, startBlock) = FindChunkAtOffset(_position);
            if (chunk == null)
            {
                break;
            }

            var chunkStartOffset = (long)startBlock * _sparseFile.Header.BlockSize;
            var offsetInChunk = _position - chunkStartOffset;
            var chunkRemaining = ((long)chunk.Header.ChunkSize * _sparseFile.Header.BlockSize) - offsetInChunk;
            var currentReadSize = (int)Math.Min(toRead - totalRead, chunkRemaining);

            switch (chunk.Header.ChunkType)
            {
                case SparseFormat.CHUNK_TYPE_RAW:
                    if (chunk.DataProvider != null)
                    {
                        var read = chunk.DataProvider.Read(offsetInChunk, buffer, offset + totalRead, currentReadSize);
                        if (read < currentReadSize)
                        {
                            // 理论上大小正确时不应发生，但以防万一还是填充 0
                            Array.Clear(buffer, offset + totalRead + read, currentReadSize - read);
                        }
                    }
                    else
                    {
                        Array.Clear(buffer, offset + totalRead, currentReadSize);
                    }
                    break;
                case SparseFormat.CHUNK_TYPE_FILL:
                    BinaryPrimitives.WriteUInt32LittleEndian(fillValue, chunk.FillValue);
                    for (var i = 0; i < currentReadSize; i++)
                    {
                        buffer[offset + totalRead + i] = fillValue[(int)((offsetInChunk + i) % 4)];
                    }
                    break;
                case SparseFormat.CHUNK_TYPE_DONT_CARE:
                default:
                    Array.Clear(buffer, offset + totalRead, currentReadSize);
                    break;
            }

            _position += currentReadSize;
            totalRead += currentReadSize;
        }

        return totalRead;
    }

    private (SparseChunk? chunk, uint startBlock) FindChunkAtOffset(long offset)
    {
        var targetBlock = (uint)(offset / _sparseFile.Header.BlockSize);
        uint currentBlock = 0;
        foreach (var chunk in _sparseFile.Chunks)
        {
            if (targetBlock >= currentBlock && targetBlock < currentBlock + chunk.Header.ChunkSize)
            {
                return (chunk, currentBlock);
            }
            currentBlock += chunk.Header.ChunkSize;
        }
        return (null, 0);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        switch (origin)
        {
            case SeekOrigin.Begin: Position = offset; break;
            case SeekOrigin.Current: Position += offset; break;
            case SeekOrigin.End: Position = _length + offset; break;
            default:
                break;
        }
        return Position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}