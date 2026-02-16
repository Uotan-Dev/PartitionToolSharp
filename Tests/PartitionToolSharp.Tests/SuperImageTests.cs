using LibLpSharp;
using LibSparseSharp;

namespace PartitionToolSharp.Tests;

public class SuperImageTests
{
    private static string GetSuperImgPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "..", "..", "..", "..", "..", "super.img");
        if (File.Exists(path))
        {
            return Path.GetFullPath(path);
        }

        path = "super.img";
        return path;
    }

    [Fact]
    public void TestReadMetadata()
    {
        var superPath = GetSuperImgPath();
        if (!File.Exists(superPath))
        {
            // Skip test if file doesn't exist
            return;
        }

        LpMetadata metadata;
        var isSparse = false;

        // 1. Check if it's sparse and prepare stream
        try
        {
            var header = SparseFile.PeekHeader(superPath);
            if (header.Magic == SparseFormat.SparseHeaderMagic)
            {
                isSparse = true;
            }
        }
        catch
        {
            isSparse = false;
        }

        if (isSparse)
        {
            // 3. 测试 SparseStream 读取数据 (部分要求)
            using var fs = File.OpenRead(superPath);
            using var sparseFile = SparseFile.FromStream(fs);
            using var stream = new SparseStream(sparseFile);

            // 1. 使用 super.img 测试 MetadataReader.Read 读取元数据
            metadata = MetadataReader.ReadFromImageStream(stream);
        }
        else
        {
            metadata = MetadataReader.ReadFromImageFile(superPath);
        }

        // 验证读取结果
        Assert.NotNull(metadata);
        Assert.NotEmpty(metadata.Partitions);

        // 2. 测试从元数据中提取分区信息
        foreach (var partition in metadata.Partitions)
        {
            var name = partition.GetName();
            Assert.False(string.IsNullOrEmpty(name));
        }
    }

    [Fact]
    public void TestSparseImageValidation()
    {
        var superPath = GetSuperImgPath();
        if (!File.Exists(superPath))
        {
            return;
        }

        // 检查是否为 sparse 镜像
        var isSparse = false;
        try
        {
            var header = SparseFile.PeekHeader(superPath);
            isSparse = header.Magic == SparseFormat.SparseHeaderMagic;
        }
        catch { }

        if (isSparse)
        {
            // 3. 如果 super.img 是 sparse 格式，测试 SparseImageValidator.Validate
            var result = SparseImageValidator.ValidateSparseImage(superPath);
            Assert.True(result.Success);
            Assert.NotNull(result.Header);
            Assert.True(result.CalculatedTotalBlocks > 0);
        }
    }

    [Fact]
    public void TestSparseStreamCapabilities()
    {
        var superPath = GetSuperImgPath();
        if (!File.Exists(superPath))
        {
            return;
        }

        var isSparse = false;
        try
        {
            var header = SparseFile.PeekHeader(superPath);
            isSparse = header.Magic == SparseFormat.SparseHeaderMagic;
        }
        catch { }

        if (isSparse)
        {
            // 4. 测试 SparseStream 读取数据
            using var fs = File.OpenRead(superPath);
            using var sparseFile = SparseFile.FromStream(fs);
            using var stream = new SparseStream(sparseFile);

            Assert.True(stream.CanRead);
            Assert.True(stream.CanSeek);

            // 读取前 1024 字节
            var buffer = new byte[1024];
            var readCount = stream.Read(buffer, 0, buffer.Length);
            Assert.True(readCount > 0);

            // 测试随机读取 (Seek)
            if (stream.Length > 8192)
            {
                stream.Seek(4096, SeekOrigin.Begin);
                Assert.Equal(4096, stream.Position);
                var readAfterSeek = stream.Read(buffer, 0, 10);
                Assert.Equal(10, readAfterSeek);
            }
        }
    }
}
