using System;

namespace LMLocal.Application.SubAgents.ReadRefs
{
    /// <summary>
    /// Inclusive, 1-based line range used by ref markers and the read ledger.
    /// </summary>
    internal readonly struct LineRange : IEquatable<LineRange>
    {
        public LineRange(int start, int end)
        {
            Start = start;
            End = end;
        }

        public int Start { get; }

        public int End { get; }

        public int Count => End - Start + 1;

        public bool IsValid => Start >= 1 && End >= Start;

        /// <summary>
        /// True when <paramref name="other"/> is fully contained in this range.
        /// </summary>
        public bool Contains(LineRange other) => other.Start >= Start && other.End <= End;

        public bool Equals(LineRange other) => Start == other.Start && End == other.End;

        public override bool Equals(object obj) => obj is LineRange other && Equals(other);

        public override int GetHashCode() => (Start * 397) ^ End;

        /// <summary>
        /// Canonical textual form: "32" for a single line, "1-120" for a range.
        /// </summary>
        public override string ToString() => Start == End ? Start.ToString() : Start + "-" + End;
    }
}
