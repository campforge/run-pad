using System;
using System.Text;

// ReSharper disable once CheckNamespace
public static class ObjectExtensions
{
    internal const char DumpLinePrefixChar = '\x1e'; // ASCII Record Separator — non-printable, won't appear in normal output

    public static T Dump<T>(this T o, string? label = null, int? maxDepth = null)
    {
        var html = DotPad.Runtime.DumpRenderer.Render(o, label, maxDepth);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
        Console.WriteLine(DumpLinePrefixChar + encoded);
        return o;
    }
}
