using System.Collections.Generic;
using System.Linq;
using LMLocal.Application.Tool;

namespace LMLocal.Application.SubAgents
{
    /// <summary>
    /// Outcome of a parallel SubAgent group. <see cref="Results"/> preserves the input call order.
    /// </summary>
    internal sealed class SubAgentsParallelRunResult
    {
        public SubAgentsParallelRunResult(IReadOnlyList<ToolExecutionResult> results)
        {
            Results = results ?? new List<ToolExecutionResult>();
        }

        /// <summary>Per-call outcomes, in the same order as the calls passed to the runner.</summary>
        public IReadOnlyList<ToolExecutionResult> Results { get; }

        /// <summary>Number of calls that completed without an error.</summary>
        public int Succeeded => Results.Count(r => r != null && string.IsNullOrEmpty(r.Error));

        /// <summary>Number of calls that failed.</summary>
        public int Failed => Results.Count - Succeeded;

        /// <summary>Error texts of the failed calls (group-level summary).</summary>
        public IReadOnlyList<string> Errors =>
            Results.Where(r => r != null && !string.IsNullOrEmpty(r.Error))
                   .Select(r => r.Error)
                   .ToList();
    }
}
