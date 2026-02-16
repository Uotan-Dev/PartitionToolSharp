using LibSparseSharp;

namespace PartitionToolSharp.Tests;

public class SparseFuzzerTests
{
    private static void RunSmokeTest(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            using var sparseFile = SparseFile.FromStream(stream);
            if (sparseFile == null)
            {
                return;
            }

            using var sparseStream = new SparseStream(sparseFile);
            var buffer = new byte[65536];
            int read;
            while ((read = sparseStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                _ = buffer[0];
                _ = buffer[read - 1];
            }
        }
        catch (InvalidDataException) { /* 预期内的错误数据异常 */ }
        catch (EndOfStreamException) { /* 预期内的流结束异常 */ }
        catch (OverflowException) { /* 解析极端畸形数据可能导致溢出 */ }
        catch (IndexOutOfRangeException) { throw; } // 解析器不应因外部数据导致越界
        catch (NullReferenceException) { throw; }   // 解析器不应出现空引用
    }

    [Fact]
    public void TestSparseFuzzer_WithValidMinimalData()
    {
        var header = new SparseHeader
        {
            Magic = SparseFormat.SPARSE_HEADER_MAGIC,
            MajorVersion = 1,
            MinorVersion = 0,
            FileHeaderSize = SparseFormat.SPARSE_HEADER_SIZE,
            ChunkHeaderSize = SparseFormat.CHUNK_HEADER_SIZE,
            BlockSize = 4096,
            TotalBlocks = 1,
            TotalChunks = 1,
            ImageChecksum = 0
        };

        var chunkHeader = new ChunkHeader
        {
            ChunkType = SparseFormat.CHUNK_TYPE_FILL,
            Reserved = 0,
            ChunkSize = 1,
            TotalSize = SparseFormat.CHUNK_HEADER_SIZE + 4
        };

        using var ms = new MemoryStream();
        ms.Write(header.ToBytes());
        ms.Write(chunkHeader.ToBytes());
        var fillValue = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(fillValue, 0x12345678u);
        ms.Write(fillValue);

        var exception = Record.Exception(() => RunSmokeTest(ms.ToArray()));
        Assert.Null(exception);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(1337)]
    public void TestSparseFuzzer_WithRandomData(int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < 100; i++)
        {
            var size = random.Next(0, 4096);
            var data = new byte[size];
            random.NextBytes(data);
            RunSmokeTest(data);
        }
    }

    [Fact]
    public void TestSparseFuzzer_WithCorruptedHeader()
    {
        var data = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data, SparseFormat.SPARSE_HEADER_MAGIC);
        var exception = Record.Exception(() => RunSmokeTest(data));
        Assert.Null(exception); // 不应崩溃
    }
}
