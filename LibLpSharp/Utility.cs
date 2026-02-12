namespace LibLpSharp;

public static class Utility
{
    public static uint GetTotalMetadataSize(uint metadataMaxSize, uint maxSlots)
    {
        return MetadataFormat.LP_PARTITION_RESERVED_BYTES +
               ((MetadataFormat.LP_METADATA_GEOMETRY_SIZE + (metadataMaxSize * maxSlots)) * 2);
    }

    public static string GetSlotSuffix(uint slotNumber) => slotNumber == 0 ? "_a" : "_b";

    public static ulong AlignTo(ulong value, uint alignment)
    {
        if (alignment == 0)
        {
            return value;
        }


        var remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    public static ulong DetectFilesystemSize(Stream stream, ulong partitionStartOffset)
    {
        try
        {
            var buffer = new byte[2048];
            stream.Seek((long)partitionStartOffset + 1024, SeekOrigin.Begin);
            if (stream.Read(buffer, 0, buffer.Length) < buffer.Length)
            {
                return 0;
            }


            if (BitConverter.ToUInt32(buffer, 0) == 0xE0F5E1E2)
            {
                return (ulong)BitConverter.ToUInt32(buffer, 32) << buffer[28];
            }


            if (BitConverter.ToUInt16(buffer, 0x38) == 0xEF53)
            {
                return (ulong)BitConverter.ToUInt32(buffer, 0x4) * (1024u << (int)BitConverter.ToUInt32(buffer, 0x18));
            }


            if (BitConverter.ToUInt32(buffer, 0) == 0xF2F52010)
            {
                return (ulong)BitConverter.ToUInt32(buffer, 0x48) * 4096;
            }

        }
        catch { }
        return 0;
    }
}
