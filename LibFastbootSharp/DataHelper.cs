using System.Runtime.InteropServices;

namespace LibFastbootSharp;

public static class DataHelper
{
    public static T Bytes2Struct<T>(byte[] data) where T : struct
    {
        var length = Marshal.SizeOf<T>();
        if (data.Length < length)
        {
            throw new ArgumentException("Data too short for structure");
        }
        var ptr = Marshal.AllocHGlobal(length);
        Marshal.Copy(data, 0, ptr, length);
        T str = Marshal.PtrToStructure<T>(ptr);
        Marshal.FreeHGlobal(ptr);
        return str;
    }

    public static byte[] Struct2Bytes<T>(T str) where T : struct
    {
        var length = Marshal.SizeOf(str);
        var data = new byte[length];
        var ptr = Marshal.AllocHGlobal(length);
        Marshal.StructureToPtr(str, ptr, true);
        Marshal.Copy(ptr, data, 0, length);
        Marshal.FreeHGlobal(ptr);
        return data;
    }
}
