using System;
using System.Collections.Generic;

namespace LMLocal.Core.Models
{
    /// <summary>
    /// A parsed chat session with its messages in chronological order, each paired with its UTC timestamp.
    /// </summary>
    public sealed class ChatSessionLog
    {
        /// <summary>Session identifier (GUID) as stored in the jsonl log.</summary>
        public string SessionId { get; set; }

        /// <summary>Session messages in chronological order (oldest first).</summary>
        public List<ChatLogEntry> Entries { get; set; }
    }

    /// <summary>
    /// A single parsed chat log line: the deserialized message and its UTC timestamp.
    /// </summary>
    public sealed class ChatLogEntry
    {
        /// <summary>Deserialized chat message.</summary>
        public ChatMessage Message { get; set; }

        /// <summary>UTC timestamp of the log line (or <see cref="DateTime.MinValue"/> when absent/unparsable).</summary>
        public DateTime TimestampUtc { get; set; }
    }
}
