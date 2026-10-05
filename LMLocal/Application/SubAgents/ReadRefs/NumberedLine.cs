namespace LMLocal.Application.SubAgents.ReadRefs
{
    /// <summary>
    /// A single source line carrying its 1-based line number and exact raw text (no prefix, no trailing newline).
    /// </summary>
    internal readonly struct NumberedLine
    {
        public NumberedLine(int lineNumber, string text)
        {
            LineNumber = lineNumber;
            Text = text;
        }

        public int LineNumber { get; }

        public string Text { get; }
    }
}
