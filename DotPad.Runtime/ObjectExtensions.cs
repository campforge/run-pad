using System.Collections;
using DotPad.Runtime.Struct;


namespace DotPad.Runtime;

public static class ObjectExtensions
{
	public static T Dump<T>(this T o, string? header = null, int maxDepth = DumpQuotas.DefaultMaxDepth, int maxExpandedDepth = DumpQuotas.DefaultMaxExpandedDepth, int maxEnumerableLength = DumpQuotas.DefaultMaxEnumerableLength, int maxStringLength = DumpQuotas.DefaultMaxStringLength)
	{
		Dumped?.Invoke(new DumpData(o, header, new DumpQuotas(maxDepth, maxExpandedDepth, maxEnumerableLength, maxStringLength)));
		return o;
	}

	public static Span<T> Dump<T>(this Span<T> o, string? header = null, int maxDepth = DumpQuotas.DefaultMaxDepth, int maxExpandedDepth = DumpQuotas.DefaultMaxExpandedDepth, int maxEnumerableLength = DumpQuotas.DefaultMaxEnumerableLength, int maxStringLength = DumpQuotas.DefaultMaxStringLength)
	{
		Dump(o.ToArray());
		return o;
	}

	public static ReadOnlySpan<T> Dump<T>(this ReadOnlySpan<T> o, string? header = null, int maxDepth = DumpQuotas.DefaultMaxDepth, int maxExpandedDepth = DumpQuotas.DefaultMaxExpandedDepth, int maxEnumerableLength = DumpQuotas.DefaultMaxEnumerableLength, int maxStringLength = DumpQuotas.DefaultMaxStringLength)
	{
		Dump(o.ToArray());
		return o;
	}

	public static Memory<T> Dump<T>(this Memory<T> o, string? header = null, int maxDepth = DumpQuotas.DefaultMaxDepth, int maxExpandedDepth = DumpQuotas.DefaultMaxExpandedDepth, int maxEnumerableLength = DumpQuotas.DefaultMaxEnumerableLength, int maxStringLength = DumpQuotas.DefaultMaxStringLength)
	{
		Dump(o.ToArray());
		return o;
	}

	public static ReadOnlyMemory<T> Dump<T>(this ReadOnlyMemory<T> o, string? header = null, int maxDepth = DumpQuotas.DefaultMaxDepth, int maxExpandedDepth = DumpQuotas.DefaultMaxExpandedDepth, int maxEnumerableLength = DumpQuotas.DefaultMaxEnumerableLength, int maxStringLength = DumpQuotas.DefaultMaxStringLength)
	{
		Dump(o.ToArray());
		return o;
	}

	public static T DumpAs<T, TResult>(this T o, Func<T, TResult>? selector, string? header = null, int maxDepth = DumpQuotas.DefaultMaxDepth, int maxExpandedDepth = DumpQuotas.DefaultMaxExpandedDepth, int maxEnumerableLength = DumpQuotas.DefaultMaxEnumerableLength, int maxStringLength = DumpQuotas.DefaultMaxStringLength)
	{
		Dump(selector != null ? (object?)selector.Invoke(o) : null, header, maxDepth, maxExpandedDepth, maxEnumerableLength, maxStringLength);
		return o;
	}

	public static TEnumerable DumpFirst<TEnumerable>(this TEnumerable enumerable, string? header = null, int maxDepth = DumpQuotas.DefaultMaxDepth, int maxExpandedDepth = DumpQuotas.DefaultMaxExpandedDepth, int maxEnumerableLength = DumpQuotas.DefaultMaxEnumerableLength, int maxStringLength = DumpQuotas.DefaultMaxStringLength)
		where TEnumerable : IEnumerable
	{
		Dump(enumerable?.Cast<object>().FirstOrDefault(), header, maxDepth, maxExpandedDepth, maxEnumerableLength, maxStringLength);
		return enumerable!;
	}

	public static TEnumerable DumpLast<TEnumerable>(this TEnumerable enumerable, string? header = null, int maxDepth = DumpQuotas.DefaultMaxDepth, int maxExpandedDepth = DumpQuotas.DefaultMaxExpandedDepth, int maxEnumerableLength = DumpQuotas.DefaultMaxEnumerableLength, int maxStringLength = DumpQuotas.DefaultMaxStringLength)
		where TEnumerable : IEnumerable
	{
		Dump(enumerable?.Cast<object>().LastOrDefault(), header, maxDepth, maxExpandedDepth, maxEnumerableLength, maxStringLength);
		return enumerable!;
	}

	public static TEnumerable DumpElementAt<TEnumerable>(this TEnumerable enumerable, int index, string? header = null, int maxDepth = DumpQuotas.DefaultMaxDepth, int maxExpandedDepth = DumpQuotas.DefaultMaxExpandedDepth, int maxEnumerableLength = DumpQuotas.DefaultMaxEnumerableLength, int maxStringLength = DumpQuotas.DefaultMaxStringLength)
		where TEnumerable : IEnumerable
	{
		Dump(enumerable?.Cast<object>().ElementAtOrDefault(index), header, maxDepth, maxExpandedDepth, maxEnumerableLength, maxStringLength);
		return enumerable!;
	}

	internal static event DumpDelegate? Dumped;

	internal delegate void DumpDelegate(in DumpData data);
}
