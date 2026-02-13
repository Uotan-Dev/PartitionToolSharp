using System.Security.Cryptography;

namespace LibLpSharp;

public static class MetadataWriter
{
    public static unsafe byte[] SerializeGeometry(LpMetadataGeometry geometry)
    {
        geometry.Magic = MetadataFormat.LP_METADATA_GEOMETRY_MAGIC;
        geometry.StructSize = (uint)sizeof(LpMetadataGeometry);

        for (var i = 0; i < 32; i++)
        {
            geometry.Checksum[i] = 0;
        }

        var blob = new byte[sizeof(LpMetadataGeometry)];
        fixed (byte* p = blob)
        {
            *(LpMetadataGeometry*)p = geometry;
        }

        using (var sha256 = SHA256.Create())
        {
            var hash = sha256.ComputeHash(blob);
            fixed (byte* p = blob)
            {
                var pg = (LpMetadataGeometry*)p;
                for (var i = 0; i < 32; i++)
                {
                    pg->Checksum[i] = hash[i];
                }
            }
        }

        var padded = new byte[MetadataFormat.LP_METADATA_GEOMETRY_SIZE];
        Array.Copy(blob, padded, blob.Length);
        return padded;
    }

    public static unsafe byte[] SerializeMetadata(LpMetadata metadata)
    {
        var header = metadata.Header;

        var partitions = TableToBytes(metadata.Partitions);
        var extents = TableToBytes(metadata.Extents);
        var groups = TableToBytes(metadata.Groups);
        var blockDevices = TableToBytes(metadata.BlockDevices);

        header.Partitions.Offset = 0;
        header.Partitions.NumEntries = (uint)metadata.Partitions.Count;
        header.Partitions.EntrySize = (uint)sizeof(LpMetadataPartition);

        header.Extents.Offset = (uint)partitions.Length;
        header.Extents.NumEntries = (uint)metadata.Extents.Count;
        header.Extents.EntrySize = (uint)sizeof(LpMetadataExtent);

        header.Groups.Offset = header.Extents.Offset + (uint)extents.Length;
        header.Groups.NumEntries = (uint)metadata.Groups.Count;
        header.Groups.EntrySize = (uint)sizeof(LpMetadataPartitionGroup);

        header.BlockDevices.Offset = header.Groups.Offset + (uint)groups.Length;
        header.BlockDevices.NumEntries = (uint)metadata.BlockDevices.Count;
        header.BlockDevices.EntrySize = (uint)sizeof(LpMetadataBlockDevice);

        header.TablesSize = header.BlockDevices.Offset + (uint)blockDevices.Length;

        var tables = new byte[header.TablesSize];
        Array.Copy(partitions, 0, tables, header.Partitions.Offset, partitions.Length);
        Array.Copy(extents, 0, tables, header.Extents.Offset, extents.Length);
        Array.Copy(groups, 0, tables, header.Groups.Offset, groups.Length);
        Array.Copy(blockDevices, 0, tables, header.BlockDevices.Offset, blockDevices.Length);

        using var sha256 = SHA256.Create();
        var tableHash = sha256.ComputeHash(tables);
        for (var i = 0; i < 32; i++)
        {
            header.TablesChecksum[i] = tableHash[i];
        }

        header.Magic = MetadataFormat.LP_METADATA_HEADER_MAGIC;
        header.HeaderSize = (uint)sizeof(LpMetadataHeader);

        // 计算头部校验和之前先将其清零
        for (var i = 0; i < 32; i++)
        {
            header.HeaderChecksum[i] = 0;
        }

        var headerBytes = new byte[header.HeaderSize];
        fixed (byte* p = headerBytes)
        {
            *(LpMetadataHeader*)p = header;
        }

        var headerHash = sha256.ComputeHash(headerBytes);
        fixed (byte* p = &headerBytes[12]) // 对应 header_checksum 的偏移
        {
            for (var i = 0; i < 32; i++)
            {
                p[i] = headerHash[i];
            }
        }

        var result = new byte[headerBytes.Length + tables.Length];
        Array.Copy(headerBytes, 0, result, 0, headerBytes.Length);
        Array.Copy(tables, 0, result, headerBytes.Length, tables.Length);
        return result;
    }

    private static unsafe byte[] TableToBytes<T>(List<T> list) where T : unmanaged
    {
        if (list.Count == 0)
        {
            return [];
        }

        var entrySize = sizeof(T);
        var result = new byte[list.Count * entrySize];
        fixed (byte* p = result)
        {
            var pt = (T*)p;
            for (var i = 0; i < list.Count; i++)
            {
                pt[i] = list[i];
            }
        }
        return result;
    }

    public static bool WriteToImageFile(string path, LpMetadata metadata)
    {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        WriteToImageStream(stream, metadata);
        return true;
    }

    public static void WriteToImageStream(Stream stream, LpMetadata metadata)
    {
        var geometryBlob = SerializeGeometry(metadata.Geometry);
        var metadataBlob = SerializeMetadata(metadata);

        if (metadataBlob.Length > metadata.Geometry.MetadataMaxSize)
        {
            throw new InvalidOperationException("序列化后的元数据大小超过了几何数据中定义的 MetadataMaxSize");
        }

        // 写入主几何块及其备份
        stream.Seek(MetadataFormat.LP_PARTITION_RESERVED_BYTES, SeekOrigin.Begin);
        stream.Write(geometryBlob, 0, geometryBlob.Length);
        stream.Write(geometryBlob, 0, geometryBlob.Length);

        // 将元数据写入所有插槽
        for (uint i = 0; i < metadata.Geometry.MetadataSlotCount; i++)
        {
            var primaryOffset = MetadataReader.GetPrimaryMetadataOffset(metadata.Geometry, i);
            stream.Seek(primaryOffset, SeekOrigin.Begin);
            stream.Write(metadataBlob, 0, metadataBlob.Length);

            var backupOffset = MetadataReader.GetBackupMetadataOffset(metadata.Geometry, i);
            stream.Seek(backupOffset, SeekOrigin.Begin);
            stream.Write(metadataBlob, 0, metadataBlob.Length);
        }
    }
}