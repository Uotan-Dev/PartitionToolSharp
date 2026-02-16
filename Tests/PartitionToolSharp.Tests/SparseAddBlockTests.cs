using LibSparseSharp;

namespace PartitionToolSharp.Tests;

public class SparseAddBlockTests
{
    [Fact]
    public void TestAddChunkAtSpecificBlock()
    {
        var blockSize = 4096u;
        var sparseFile = new SparseFile(blockSize, blockSize * 100);

        // 在 block 0 添加 1 个块
        sparseFile.AddRawChunk(new byte[blockSize], 0);
        Assert.Single(sparseFile.Chunks);
        Assert.Equal(1u, sparseFile.CurrentBlock);

        // 在 block 10 添加 1 个块，中间应该自动产生 DONT_CARE
        sparseFile.AddFillChunk(0x12345678, blockSize, 10);

        // Chunks 应包含: [RAW(1)], [DONT_CARE(9)], [FILL(1)]
        Assert.Equal(3, sparseFile.Chunks.Count);
        Assert.Equal(SparseFormat.CHUNK_TYPE_DONT_CARE, sparseFile.Chunks[1].Header.ChunkType);
        Assert.Equal(9u, sparseFile.Chunks[1].Header.ChunkSize);
        Assert.Equal(11u, sparseFile.CurrentBlock);
    }

    [Fact]
    public void TestAddChunkWithOverlap_ShouldThrow()
    {
        var blockSize = 4096u;
        var sparseFile = new SparseFile(blockSize, blockSize * 100);

        sparseFile.AddRawChunk(new byte[blockSize * 5], 0); // block 0-4

        // 尝试在 block 3 添加，应该抛出异常
        Assert.Throws<ArgumentException>(() => sparseFile.AddFillChunk(0, blockSize, 3));
    }
}
