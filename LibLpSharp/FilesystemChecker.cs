namespace LibLpSharp;

public static class FilesystemChecker
{
    private const int SUPERBLOCK_OFFSET = 1024;

    public static ulong DetectFilesystemSize(Stream stream, ulong partitionStartOffset)
    {
        try
        {
            var buffer = new byte[2048];
            stream.Seek((long)partitionStartOffset + SUPERBLOCK_OFFSET, SeekOrigin.Begin);
            if (stream.Read(buffer, 0, buffer.Length) < buffer.Length)
            {
                LpLogger.Info($"Filesystem probe failed: Could not read 2048 bytes at offset {partitionStartOffset + SUPERBLOCK_OFFSET}");
                return 0;
            }

            // 1. Detect EROFS
            if (BitConverter.ToUInt32(buffer, 0) == 0xE0F5E1E2)
            {
                var blocks = BitConverter.ToUInt32(buffer, 32);
                int blkSizeLog2 = buffer[28];
                var totalSize = (ulong)blocks << blkSizeLog2;
                LpLogger.Info($"Detected EROFS: {totalSize
                 / 1024 / 1024.0:F2} MiB");
                return totalSize;
            }

            // 2. Detect EXT4
            if (BitConverter.ToUInt16(buffer, 0x38) == 0xEF53)
            {
                var blocks = BitConverter.ToUInt32(buffer, 0x4);
                var blkSizeLog2 = BitConverter.ToUInt32(buffer, 0x18);
                var blkSize = 1024u << (int)blkSizeLog2;
                var totalSize = (ulong)blocks * blkSize;
                LpLogger.Info($"Detected EXT4: {totalSize / 1024 / 1024.0:F2} MiB");
                return totalSize;
            }

            // 3. Detect F2FS
            if (BitConverter.ToUInt32(buffer, 0) == 0xF2F52010)
            {
                var blocks = BitConverter.ToUInt32(buffer, 0x48);
                var totalSize = (ulong)blocks * 4096;
                LpLogger.Info($"Detected F2FS: {totalSize / 1024 / 1024.0:F2} MiB");
                return totalSize;
            }
        }
        catch (Exception ex)
        {
            LpLogger.Info($"Filesystem probe error: {ex.Message}");
        }

        return 0;
    }
}
