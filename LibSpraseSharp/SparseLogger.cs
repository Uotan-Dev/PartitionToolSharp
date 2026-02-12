using System;

namespace LibSparseSharp;

public static class SparseLogger
{
    public static Action<string>? LogMessage { get; set; }
    
    public static void Info(string message) => LogMessage?.Invoke(message);
}
