using System.Buffers.Binary;

namespace LibSparseSharp;

public static class SparseFormat
{
    public const uint SPARSE_HEADER_MAGIC = 0xed26ff3a;

    public const ushort CHUNK_TYPE_RAW = 0xCAC1;
    public const ushort CHUNK_TYPE_FILL = 0xCAC2;
    public const ushort CHUNK_TYPE_DONT_CARE = 0xCAC3;
    public const ushort CHUNK_TYPE_CRC32 = 0xCAC4;

    public const ushort SPARSE_HEADER_SIZE = 28;
    public const ushort CHUNK_HEADER_SIZE = 12;

    public const uint MAX_CHUNK_DATA_SIZE = 64 * 1024 * 1024;
}

public struct SparseHeader
{
    public uint Magic;
    public ushort MajorVersion;
    public ushort MinorVersion;
    public ushort FileHeaderSize;
    public ushort ChunkHeaderSize;
    public uint BlockSize;
    public uint TotalBlocks;
    public uint TotalChunks;
    public uint ImageChecksum;

    public static SparseHeader FromBytes(byte[] data)
    {
        return data.Length < SparseFormat.SPARSE_HEADER_SIZE
            ? throw new ArgumentException("数据长度不足以构建 SparseHeader")
            : new SparseHeader
            {
                Magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0)),
                MajorVersion = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)),
                MinorVersion = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6)),
                FileHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8)),
                ChunkHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10)),
                BlockSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)),
                TotalBlocks = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)),
                TotalChunks = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20)),
                ImageChecksum = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24))
            };
    }

    public readonly byte[] ToBytes()
    {
        var data = new byte[SparseFormat.SPARSE_HEADER_SIZE];
        var span = data.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4), MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6), MinorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(8), FileHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(10), ChunkHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16), TotalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20), TotalChunks);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24), ImageChecksum);

        return data;
    }

    public readonly bool IsValid()
    {
        return Magic == SparseFormat.SPARSE_HEADER_MAGIC &&
               MajorVersion == 1 &&
               FileHeaderSize >= SparseFormat.SPARSE_HEADER_SIZE &&
               ChunkHeaderSize >= SparseFormat.CHUNK_HEADER_SIZE &&
               BlockSize > 0 && BlockSize % 4 == 0;
    }
}

public struct ChunkHeader
{
    public ushort ChunkType;
    public ushort Reserved;
    public uint ChunkSize;
    public uint TotalSize;

    public static ChunkHeader FromBytes(byte[] data)
    {
        return data.Length < SparseFormat.CHUNK_HEADER_SIZE
            ? throw new ArgumentException("数据长度不足以构建 ChunkHeader")
            : new ChunkHeader
            {
                ChunkType = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0)),
                Reserved = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)),
                ChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)),
                TotalSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8))
            };
    }

    public readonly byte[] ToBytes()
    {
        var data = new byte[SparseFormat.CHUNK_HEADER_SIZE];
        var span = data.AsSpan();

        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(0), ChunkType);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2), Reserved);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), ChunkSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), TotalSize);

        return data;
    }

    public readonly bool IsValid()
    {
        return ChunkType is SparseFormat.CHUNK_TYPE_RAW or
               SparseFormat.CHUNK_TYPE_FILL or
               SparseFormat.CHUNK_TYPE_DONT_CARE or
               SparseFormat.CHUNK_TYPE_CRC32;
    }
}