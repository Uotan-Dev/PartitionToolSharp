using LibLpSharp;
using LibSparseSharp;

namespace PartitionToolSharp.Tests;

public class BuilderTests
{
    private const ulong DeviceSize = 10 * 1024 * 1024; // 10MB
    private const uint MetadataMaxSize = 65536;
    private const uint MetadataSlotCount = 2;

    [Fact]
    public void TestInitializeFromExistingMetadata()
    {
        // 1. 创建初始元数据
        var builder = MetadataBuilder.New(DeviceSize, MetadataMaxSize, MetadataSlotCount);
        builder.AddPartition("system", "default", MetadataFormat.LP_PARTITION_ATTR_NONE);
        var p = builder.FindPartition("system");
        Assert.NotNull(p);
        builder.ResizePartition(p, 1024 * 1024); // 1MB
        var metadata = builder.Export();

        // 2. 从现有元数据对象初始化新 Builder
        var builder2 = MetadataBuilder.FromMetadata(metadata);
        var metadata2 = builder2.Export();

        // 3. 验证
        Assert.Equal(metadata.Partitions.Count, metadata2.Partitions.Count);
        Assert.Equal(metadata.Partitions[0].GetName(), metadata2.Partitions[0].GetName());
        Assert.Equal(metadata.Extents.Count, metadata2.Extents.Count);
        Assert.Equal(metadata.Geometry.MetadataMaxSize, metadata2.Geometry.MetadataMaxSize);
    }

    [Fact]
    public void TestAddPartition()
    {
        var builder = MetadataBuilder.New(DeviceSize, MetadataMaxSize, MetadataSlotCount);

        var name = "vendor";
        builder.AddPartition(name, "default", MetadataFormat.LP_PARTITION_ATTR_READONLY);

        var partition = builder.FindPartition(name);
        Assert.NotNull(partition);
        Assert.Equal(name, partition.Name);
        Assert.Equal("default", partition.GroupName);
        Assert.Equal(MetadataFormat.LP_PARTITION_ATTR_READONLY, partition.Attributes);
    }

    [Fact]
    public void TestResizePartition()
    {
        var builder = MetadataBuilder.New(DeviceSize, MetadataMaxSize, MetadataSlotCount);
        builder.AddPartition("data", "default", MetadataFormat.LP_PARTITION_ATTR_NONE);
        var partition = builder.FindPartition("data");
        Assert.NotNull(partition);

        ulong newSize = 2 * 1024 * 1024; // 2MB
        builder.ResizePartition(partition, newSize);

        Assert.Equal(newSize, partition.Size);
        // 扇区数 = 2MB / 512
        Assert.Equal(newSize / MetadataFormat.LP_SECTOR_SIZE, (ulong)partition.Extents.Sum(e => (long)e.NumSectors));
    }

    [Fact]
    public void TestMetadataWriterAndReader()
    {
        // 1. 修改并导出元数据
        var builder = MetadataBuilder.New(DeviceSize, MetadataMaxSize, MetadataSlotCount);
        builder.AddPartition("product", "default", MetadataFormat.LP_PARTITION_ATTR_NONE);
        var p = builder.FindPartition("product");
        Assert.NotNull(p);
        builder.ResizePartition(p, 512 * 1024); // 512KB
        var metadata = builder.Export();

        // 2. 将修改后的元数据写入内存流
        var geometryBytes = MetadataWriter.SerializeGeometry(metadata.Geometry);
        var metadataBytes = MetadataWriter.SerializeMetadata(metadata);

        using var ms = new MemoryStream();
        // 模拟磁盘布局：[预留空间 4096] [Geometry 主 4096] [Geometry 备份 4096] [Metadata ...]
        ms.Write(new byte[MetadataFormat.LP_PARTITION_RESERVED_BYTES]);
        ms.Write(geometryBytes);
        ms.Write(geometryBytes); // 备份 Geometry
        ms.Write(metadataBytes);
        ms.Position = 0;

        // 3. 再次读取验证
        var readMetadata = MetadataReader.ReadFromImageStream(ms);
        Assert.Single(readMetadata.Partitions);
        Assert.Equal("product", readMetadata.Partitions[0].GetName());
        Assert.Equal(512 * 1024 / (uint)MetadataFormat.LP_SECTOR_SIZE, readMetadata.Partitions[0].NumExtents > 0 ? readMetadata.Extents[(int)readMetadata.Partitions[0].FirstExtentIndex].NumSectors : 0);
    }

    [Fact]
    public void TestSuperImageBuilder()
    {
        var superBuilder = new SuperImageBuilder(DeviceSize, MetadataMaxSize, MetadataSlotCount);
        superBuilder.AddGroup("qti_dynamic_partitions", DeviceSize);
        superBuilder.AddPartition("system", 1024 * 1024, "qti_dynamic_partitions", MetadataFormat.LP_PARTITION_ATTR_NONE);

        var sparseFile = superBuilder.Build();

        Assert.NotNull(sparseFile);
        Assert.Equal(DeviceSize / 4096, (ulong)sparseFile.Header.TotalBlocks);
        Assert.Contains(sparseFile.Chunks, c => c.Header.ChunkType == SparseFormat.CHUNK_TYPE_RAW);
    }
}
