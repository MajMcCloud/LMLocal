using System.Collections.Generic;
using System.Text;
using LMLocal.Core.Common;

namespace LMLocal.Application.SubAgents.ReadRefs
{
    /// <summary>
    /// Single source of truth for rendering numbered file content as Markdown.
    /// </summary>
    internal static class FileContentRenderer
    {
        /// <summary>
        /// Renders lines as "N: text" joined by '\n'. Lines with a non-positive line number are emitted verbatim.
        /// </summary>
        public static string RenderNumberedLines(IEnumerable<NumberedLine> lines)
        {
            var rendered = new List<string>();
            if (lines != null)
            {
                foreach (var line in lines)
                {
                    if (line.LineNumber > 0)
                        rendered.Add(line.LineNumber + ": " + (line.Text ?? string.Empty));
                    else
                        rendered.Add(line.Text ?? string.Empty);
                }
            }

            return string.Join("\n", rendered);
        }

        /// <summary>
        /// Appends the standard fenced file block.
        /// </summary>
        public static void AppendFileBlock(StringBuilder sb, string filePath, IEnumerable<NumberedLine> lines, bool hasMore)
        {
            var lang = MarkdownLanguageHelper.GetLanguageFromExtension(filePath);

            sb.Append("**`" + filePath + "`**");
            if (hasMore)
                sb.Append(" *(truncated, more lines available)*");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("````" + lang);
            sb.AppendLine(RenderNumberedLines(lines));
            sb.AppendLine("````");
            sb.AppendLine();
        }

        /// <summary>
        /// Appends a fenced code block with numbered lines and <b>no file-path header</b>, and <b>no trailing newline</b>.
        /// </summary>
        public static void AppendFencedContentBlock(StringBuilder sb, string filePath, IEnumerable<NumberedLine> lines)
        {
            var lang = MarkdownLanguageHelper.GetLanguageFromExtension(filePath);

            sb.AppendLine("````" + lang);
            sb.AppendLine(RenderNumberedLines(lines));
            sb.Append("````");
        }
    }
}
