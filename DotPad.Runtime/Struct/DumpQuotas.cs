using System.Diagnostics.Contracts;

namespace DotPad.Runtime.Struct
{

    internal struct DumpQuotas
    {
        internal const int DefaultMaxDepth = 4;
        internal const int DefaultMaxExpandedDepth = 1;
        internal const int DefaultMaxStringLength = 10000;
        internal const int DefaultMaxEnumerableLength = 10000;

        public int MaxDepth { get; }
        public int MaxExpandedDepth { get; }
        public int MaxEnumerableLength { get; }
        public int MaxStringLength { get; }

        public DumpQuotas(int maxDepth, int maxExpandedDepth, int maxEnumerableLength, int maxStringLength)
        {
            MaxDepth = maxDepth;
            MaxExpandedDepth = maxExpandedDepth;
            MaxEnumerableLength = maxEnumerableLength;
            MaxStringLength = maxStringLength;
        }

        public static DumpQuotas Default { get; } = new DumpQuotas(DefaultMaxDepth, DefaultMaxExpandedDepth, DefaultMaxEnumerableLength, DefaultMaxStringLength);

        [Pure]
        internal DumpQuotas StepDown() =>
            new DumpQuotas (MaxDepth - 1, MaxExpandedDepth - 1, MaxEnumerableLength, MaxStringLength);

        [Pure]
        internal DumpQuotas WithMaxDepth(int maxDepth) =>
			new DumpQuotas (maxDepth, MaxExpandedDepth, MaxEnumerableLength, MaxStringLength);
    }
}