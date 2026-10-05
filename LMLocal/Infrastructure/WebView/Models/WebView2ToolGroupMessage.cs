using System.Collections.Generic;

namespace LMLocal.Infrastructure.WebView
{
    /// <summary>
    /// Message describing a parallel SubAgent fan-out group and its progress.
    /// </summary>
    internal class WebView2ToolGroupMessage : WebView2ScriptMessage
    {
        /// <summary>
        /// Identifier of the fan-out group; identical across the group's start/progress/end messages.
        /// </summary>
        public string GroupId { get; set; }

        /// <summary>
        /// Call ids of every agent in the group, in the original call order.
        /// </summary>
        public IReadOnlyList<string> CallIds { get; set; }

        /// <summary>
        /// Total number of agents in the group.
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// Number of agents that have finished so far.
        /// </summary>
        public int Completed { get; set; }
    }
}
