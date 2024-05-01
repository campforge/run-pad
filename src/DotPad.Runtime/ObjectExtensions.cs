using DotPad.Runtime.Struct;
using System;
using System.Collections;
using System.Linq;


namespace DotPad.Runtime
{

    public static class ObjectExtensions
    {
        public static T Dump<T>(this T o, string header = null, int maxDepth = DumpQuotas.DefaultMaxDepth, int maxExpandedDepth = DumpQuotas.DefaultMaxExpandedDepth, int maxEnumerableLength = DumpQuotas.DefaultMaxEnumerableLength, int maxStringLength = DumpQuotas.DefaultMaxStringLength)
        {
            DumpCore(o, header, maxDepth);
            return o;
        }

        public static T DumpAs<T, TResult>(this T o, Func<T, TResult> selector, string header = null, int maxDepth = DumpQuotas.DefaultMaxDepth, int maxExpandedDepth = DumpQuotas.DefaultMaxExpandedDepth, int maxEnumerableLength = DumpQuotas.DefaultMaxEnumerableLength, int maxStringLength = DumpQuotas.DefaultMaxStringLength)
        {
            Dump(selector != null ? (object)selector.Invoke(o) : null, header, maxDepth, maxExpandedDepth, maxEnumerableLength, maxStringLength);
            return o;
        }

        public static TEnumerable DumpFirst<TEnumerable>(this TEnumerable enumerable, string header = null, int maxDepth = DumpQuotas.DefaultMaxDepth, int maxExpandedDepth = DumpQuotas.DefaultMaxExpandedDepth, int maxEnumerableLength = DumpQuotas.DefaultMaxEnumerableLength, int maxStringLength = DumpQuotas.DefaultMaxStringLength)
            where TEnumerable : IEnumerable
        {
            Dump(enumerable?.Cast<object>().FirstOrDefault(), header, maxDepth, maxExpandedDepth, maxEnumerableLength, maxStringLength);
            return enumerable;
        }

        public static TEnumerable DumpLast<TEnumerable>(this TEnumerable enumerable, string header = null, int maxDepth = DumpQuotas.DefaultMaxDepth, int maxExpandedDepth = DumpQuotas.DefaultMaxExpandedDepth, int maxEnumerableLength = DumpQuotas.DefaultMaxEnumerableLength, int maxStringLength = DumpQuotas.DefaultMaxStringLength)
            where TEnumerable : IEnumerable
        {
            Dump(enumerable?.Cast<object>().LastOrDefault(), header, maxDepth, maxExpandedDepth, maxEnumerableLength, maxStringLength);
            return enumerable;
        }

        public static TEnumerable DumpElementAt<TEnumerable>(this TEnumerable enumerable, int index, string header = null, int maxDepth = DumpQuotas.DefaultMaxDepth, int maxExpandedDepth = DumpQuotas.DefaultMaxExpandedDepth, int maxEnumerableLength = DumpQuotas.DefaultMaxEnumerableLength, int maxStringLength = DumpQuotas.DefaultMaxStringLength)
            where TEnumerable : IEnumerable
        {
            Dump(enumerable?.Cast<object>().ElementAtOrDefault(index), header, maxDepth, maxExpandedDepth, maxEnumerableLength, maxStringLength);
            return enumerable;
        }

        private static T DumpCore<T>(T o, string description, int? depth)
        {
            Console.WriteLine($"<div><span>{o}</span></div>");
            return o;
        }

    }
}