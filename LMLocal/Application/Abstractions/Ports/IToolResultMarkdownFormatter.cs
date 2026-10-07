using System.Collections.Generic;

namespace LMLocal.Application.Abstractions.Ports
{
    /// <summary>
    /// Converts raw tool result JSON (from tool messages in chat history) into human-readable Markdown.
    /// </summary>
    public interface IToolResultMarkdownFormatter
    {
        /// <summary>
        /// Formats a collection of tool results (function name + JSON content) into a single Markdown section. Only results from known tools (read_file_lines, get_solution_overview, get_active_document) are included;
        /// </summary>
        string FormatToolResults(IEnumerable<(string FunctionName, string JsonContent)> toolResults);
    }
}
