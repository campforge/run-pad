using System;
using System.Runtime.CompilerServices;
using System.Text;

// ReSharper disable once CheckNamespace
public static class ObjectExtensions
{
    internal const char DumpLinePrefixChar = '\x1e'; // ASCII Record Separator — non-printable, won't appear in normal output

    public static T Dump<T>(this T o,
        string? label = null,
        int? maxDepth = null,
        int? maxRows = null,
        [CallerArgumentExpression("o")] string? expression = null)
    {
        var actualLabel = label ?? expression;
        var html = DotPad.Runtime.DumpRenderer.Render(o, actualLabel, maxDepth, maxRows);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
        Console.WriteLine(DumpLinePrefixChar + encoded);
        return o;
    }
}

// CallerArgumentExpression is not in netstandard2.0 BCL — define it so the C# compiler can use it.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName) =>
            ParameterName = parameterName;

        public string ParameterName { get; }
    }
}
