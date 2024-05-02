
using System;
using System.Text.Json;

// ReSharper disable once UnusedType.Global
// ReSharper disable once CheckNamespace
public static class ObjectExtensions
{
    public static T Dump<T>(this T o, string header = null, int? maxDepth = null)
    {
        DumpCore(o, header, maxDepth);
        return o;
    }

    private static T DumpCore<T>(T o, string header, int? maxDepth)
    {
        Console.WriteLine($"dump <{typeof(T)}>: {
            JsonSerializer.Serialize(o, new JsonSerializerOptions()
            {
                WriteIndented = false,
                MaxDepth = maxDepth ?? 0
            })} : {header}");
        return o;
    }
}