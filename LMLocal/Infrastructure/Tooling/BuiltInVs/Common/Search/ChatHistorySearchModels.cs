using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace LMLocal.Infrastructure.Tooling.BuiltInVs.Common.Search
{
    /// <summary>
    /// Outcome state used internally by GetCompletionMessage to distinguish "no chat logs found" from "logs scanned but no matches".
    /// </summary>
    internal enum ChatHistorySearchOutcome
    {
        None = 0,
        Success = 1,
        NoFiles = 2,
        NoMatches = 3
    }

    /// <summary>
    /// Match-quality tiers for search_chat_history. Higher value = better.
    /// </summary>
    internal enum ChatHistoryMatchKind
    {
        None = 0,
        TurnPartial = 1,
        SessionAllTokens = 2,
        TurnAllTokens = 3,
        AnswerExact = 4,
        QuestionExact = 5
    }

    /// <summary>
    /// Response for the search_chat_history tool.
    /// </summary>
    public class ChatHistorySearchResponse
    {
        [JsonIgnore]
        internal ChatHistorySearchOutcome Outcome { get; set; } = ChatHistorySearchOutcome.None;

        [JsonProperty("results")]
        public List<ChatHistoryMatchResult> Results { get; set; }

        [JsonProperty("total_matches")]
        public int TotalMatches { get; set; }

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("error_message", NullValueHandling = NullValueHandling.Ignore)]
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// A single chat-history match: the best matching turn of one session, with its session title, the turn's user question (heading) and a markdown snippet around the match.
    /// </summary>
    public class ChatHistoryMatchResult
    {
        /// <summary>
        /// Highest match tier reached by this session. Used for ranking only; not serialized.
        /// </summary>
        [JsonIgnore]
        internal ChatHistoryMatchKind Kind { get; set; }

        /// <summary>
        /// Sub-score used only to rank sessions within the same <see cref="Kind"/>. Not serialized.
        /// </summary>
        [JsonIgnore]
        internal int SubScore { get; set; }

        /// <summary>
        /// Raw recency timestamp used for deterministic tie-breaks. Not serialized.
        /// </summary>
        [JsonIgnore]
        internal DateTime TimestampUtc { get; set; }

        /// <summary>
        /// Session identifier, used for deterministic tie-breaks only.
        /// </summary>
        [JsonIgnore]
        internal string SessionId { get; set; }

        /// <summary>
        /// Index of the matched turn within the session, used for deterministic tie-breaks only.
        /// </summary>
        [JsonIgnore]
        internal int TurnIndex { get; set; }

        /// <summary>
        /// First user message of the session (truncated); identifies which conversation matched.
        /// </summary>
        [JsonProperty("session_title")]
        public string SessionTitle { get; set; }

        /// <summary>
        /// ISO-8601 timestamp of the most recent message in the matched session.
        /// </summary>
        [JsonProperty("timestamp", NullValueHandling = NullValueHandling.Ignore)]
        public string Timestamp { get; set; }

        /// <summary>
        /// Where the best match sits: "user" (in the question) or "assistant" (in the answer).
        /// </summary>
        [JsonProperty("role")]
        public string Role { get; set; }

        /// <summary>
        /// The user question (heading) of the matched turn.
        /// </summary>
        [JsonProperty("heading", NullValueHandling = NullValueHandling.Ignore)]
        public string Heading { get; set; }

        /// <summary>
        /// Markdown snippet around the best match.
        /// </summary>
        [JsonProperty("snippet")]
        public string Snippet { get; set; }
    }
}
