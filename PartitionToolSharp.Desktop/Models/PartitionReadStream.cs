using System;
using System.Collections.Generic;
using System.IO;
using LibLpSharp;

namespace PartitionToolSharp.Desktop.Models;

public class PartitionReadStream : Stream
{
    private readonly Stream _baseStream;
    private readonly List<IDisposable> _disposables;
    private readonly List<LpMetadataExtent> _extents;
    private readonly long _totalLength;
    private long _position;

    public PartitionReadStream(Stream baseStream, LpMetadata metadata, LpMetadataPartition partition, IEnumerable<IDisposable>? additionalDisposables = null)
    {
        _baseStream = baseStream;
        _disposables = [baseStream];
        if (additionalDisposables != null)
        {
            _disposables.AddRange(additionalDisposables);
        }

        _extents = [];
        _totalLength = 0;

        for (uint i = 0; i < partition.NumExtents; i++)
        {
            var extent = metadata.Extents[(int)(partition.FirstExtentIndex + i)];
            _extents.Add(extent);
            _totalLength += (long)extent.NumSectors * 512;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _totalLength;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > _totalLength)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _position = value;
        }
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _totalLength)
        {
            return 0;
        }

        var totalRead = 0;
        var currentOffsetInPartition = _position;

        while (count > 0 && currentOffsetInPartition < _totalLength)
        {
            // Find which extent we are in
            long extentStartOffsetInPartition = 0;
            LpMetadataExtent? targetExtent = null;

            foreach (var extent in _extents)
            {
                var extentLength = (long)extent.NumSectors * 512;
                if (currentOffsetInPartition < extentStartOffsetInPartition + extentLength)
                {
                    targetExtent = extent;
                    break;
                }
                extentStartOffsetInPartition += extentLength;
            }

            if (targetExtent == null)
            {
                break;
            }

            var offsetInExtent = currentOffsetInPartition - extentStartOffsetInPartition;
            var remainingInExtent = ((long)targetExtent.Value.NumSectors * 512) - offsetInExtent;
            var toRead = (int)Math.Min(count, remainingInExtent);

            if (targetExtent.Value.TargetType == MetadataFormat.LP_TARGET_TYPE_LINEAR)
            {
                var physicalOffset = ((long)targetExtent.Value.TargetData * 512) + offsetInExtent;
                _baseStream.Seek(physicalOffset, SeekOrigin.Begin);
                var read = _baseStream.Read(buffer, offset, toRead);
                if (read <= 0)
                {
                    break;
                }

                totalRead += read;
                _position += read;
                offset += read;
                count -= read;
                currentOffsetInPartition += read;
            }
            else if (targetExtent.Value.TargetType == MetadataFormat.LP_TARGET_TYPE_ZERO)
            {
                // Fill with zeros for zero extents
                Array.Clear(buffer, offset, toRead);
                totalRead += toRead;
                _position += toRead;
                offset += toRead;
                count -= toRead;
                currentOffsetInPartition += toRead;
            }
            else
            {
                throw new NotSupportedException($"Unsupported extent type: {targetExtent.Value.TargetType}");
            }
        }

        return totalRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        switch (origin)
        {
            case SeekOrigin.Begin: Position = offset; break;
            case SeekOrigin.Current: Position += offset; break;
            case SeekOrigin.End: Position = _totalLength + offset; break;
            default: break;
        }
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var d in _disposables)
            {
                d.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
