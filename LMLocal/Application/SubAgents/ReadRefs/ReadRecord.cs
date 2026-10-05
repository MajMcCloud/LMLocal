namespace LMLocal.Application.SubAgents.ReadRefs
{
    /// <summary>
    /// A record of an actual <c>read_file_lines</c> result that was returned to the model.
    /// </summary>
    internal readonly struct ReadRecord
    {
        public ReadRecord(string relativePath, int startLine, int endLine, string absolutePath = null)
        {
            RelativePath = relativePath ?? string.Empty;
            AbsolutePath = absolutePath;
            StartLine = startLine;
            EndLine = endLine;
        }

        public string RelativePath { get; }

        /// <summary>
        /// Absolute on-disk path captured when the read happened (null when unknown).
        /// </summary>
        public string AbsolutePath { get; }

        public int StartLine { get; }

        public int EndLine { get; }

        public LineRange Range => new LineRange(StartLine, EndLine);

        public bool Contains(LineRange range) => range.Start >= StartLine && range.End <= EndLine;
    }
}
