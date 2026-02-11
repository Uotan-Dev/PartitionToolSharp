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
}