using System.Buffers.Binary;

namespace LibSparseSharp;

/// <summary>
/// Android sparse 镜像格式定义
/// </summary>
public static class SparseFormat
{
    public const uint SPARSE_HEADER_MAGIC = 0xed26ff3a;

    public const ushort CHUNK_TYPE_RAW = 0xCAC1;
    public const ushort CHUNK_TYPE_FILL = 0xCAC2;
    public const ushort CHUNK_TYPE_DONT_CARE = 0xCAC3;
    public const ushort CHUNK_TYPE_CRC32 = 0xCAC4;

    public const ushort SPARSE_HEADER_SIZE = 28;
    public const ushort CHUNK_HEADER_SIZE = 12;

    /// <summary>
    /// 单个 Chunk 处理的最大数据大小（避免 32-bit TotalSize 溢出，并保持与 libsparse 兼容）
    /// </summary>
    public const uint MAX_CHUNK_DATA_SIZE = 64 * 1024 * 1024; // 64MB
}

/// <summary>
/// Sparse 文件头结构
/// </summary>
public struct SparseHeader
{
    // 引用自 libsparse 的结构定义
    public uint Magic;           // 0xed26ff3a
    public ushort MajorVersion;  // (0x1) - 若主版本更高则不解析该镜像
    public ushort MinorVersion;  // (0x0) - 允许更高的次版本
    public ushort FileHeaderSize; // 第一版格式的文件头大小为 28 字节
    public ushort ChunkHeaderSize; // 第一版格式的 Chunk 头大小为 12 字节
    public uint BlockSize;       // 块大小（字节），必须是 4 的倍数（通常为 4096）
    public uint TotalBlocks;     // 输出镜像的总块数
    public uint TotalChunks;     // 输入镜像的总 Chunk 数
    public uint ImageChecksum;   // 原始数据的 CRC32 校验和

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

/// <summary>
/// Chunk 头结构
/// </summary>
public struct ChunkHeader
{
    public ushort ChunkType;     // 0xCAC1 -> 原始数据; 0xCAC2 -> 填充; 0xCAC3 -> 忽略
    public ushort Reserved;      // 预留字段
    public uint ChunkSize;       // 在输出镜像中的块数
    public uint TotalSize;       // 输入文件中该 Chunk 的总字节数（含头和数据）

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