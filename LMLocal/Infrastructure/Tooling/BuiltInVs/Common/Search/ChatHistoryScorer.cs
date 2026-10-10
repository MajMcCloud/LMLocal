using System;
using System.Collections;
using System.Collections.Generic;
using LMLocal.Application.Chat;
using LMLocal.Core.Models;

namespace LMLocal.Infrastructure.Tooling.BuiltInVs.Common.Search
{
    /// <summary>
    /// A single conversational turn: the user's question (the section heading) and the final
    /// assistant answer of that turn (the section body).
    /// </summary>
    internal sealed class ChatHistoryTurn
    {
        /// <summary>User message that opens the turn.</summary>
        public string Question { get; set; }

        /// <summary>Final assistant message of the turn (the "концовка"); may be null when the turn has no answer yet.</summary>
        public string Answer { get; set; }

        /// <summary>UTC timestamp of the turn's user message.</summary>
        public DateTime TimestampUtc { get; set; }
    }

    /// <summary>
    /// A chat session model.
    /// </summary>
    internal sealed class ChatHistorySession
    {
        public string SessionId { get; set; }

        /// <summary>First user message of the session (the session title); truncated by the caller for presentation.</summary>
        public string Title { get; set; }

        /// <summary>UTC timestamp of the most recent message in the session (recency tie-break).</summary>
        public DateTime TimestampUtc { get; set; }

        public List<ChatHistoryTurn> Turns { get; set; } = new List<ChatHistoryTurn>();
    }

    /// <summary>
    /// Immutable result of scoring one session.
    /// </summary>
    internal readonly struct ChatHistoryScoreResult
    {
        public ChatHistoryMatchKind Kind { get; }
        public int SubScore { get; }
        public int TurnIndex { get; }
        public string Role { get; }
        public string Heading { get; }
        public string Snippet { get; }

        public bool IsMatch => Kind != ChatHistoryMatchKind.None;

        public ChatHistoryScoreResult(
            ChatHistoryMatchKind kind,
            int subScore,
            int turnIndex,
            string role,
            string heading,
            string snippet)
        {
            Kind = kind;
            SubScore = subScore;
            TurnIndex = turnIndex;
            Role = role;
            Heading = heading;
            Snippet = snippet;
        }

        public static readonly ChatHistoryScoreResult NoMatch =
            new ChatHistoryScoreResult(ChatHistoryMatchKind.None, 0, -1, null, null, null);
    }

    /// <summary>
    /// Stateless projector + tiered scorer for search_chat_history.
    /// </summary>
    internal static class ChatHistoryScorer
    {
        public const int SnippetRadius = 150;

        /// <summary>Maximum length of a matched user question (heading) shown in search results before truncation. A heading may be a consolidated user message (original question + tool-result dumps), so it is truncated for presentation only; the full text is still used for scoring.</summary>
        public const int MaxHeadingLength = 300;

        private const string UserRole = "user";
        private const string AssistantRole = "assistant";
        private const string ToolRole = "tool";

        /// <summary>
        /// Projects a session's chronological log entries into turns.
        /// </summary>
        public static ChatHistorySession ProjectSession(string sessionId, IReadOnlyList<ChatLogEntry> entries)
        {
            var session = new ChatHistorySession
            {
                SessionId = sessionId,
                Title = string.Empty,
                Turns = new List<ChatHistoryTurn>()
            };

            if (entries == null || entries.Count == 0)
                return session;

            ChatHistoryTurn current = null;
            DateTime last = DateTime.MinValue;

            foreach (var entry in entries)
            {
                if (entry?.Message == null)
                    continue;

                last = entry.TimestampUtc;

                var message = entry.Message;
                string role = message.Role ?? string.Empty;

                if (string.Equals(role, ToolRole, StringComparison.OrdinalIgnoreCase))
                    continue;

                string text = ContentTextExtractor.ExtractTextContent(message.Content);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                if (string.Equals(role, UserRole, StringComparison.OrdinalIgnoreCase))
                {
                    current = new ChatHistoryTurn { Question = text, TimestampUtc = entry.TimestampUtc };
                    session.Turns.Add(current);

                    if (session.Turns.Count == 1)
                        session.Title = text;
                }
                else if (string.Equals(role, AssistantRole, StringComparison.OrdinalIgnoreCase))
                {
                    if (HasToolCalls(message) || current == null)
                        continue;

                    current.Answer = text;
                }
            }

            session.TimestampUtc = last;
            return session;
        }

        /// <summary>
        /// Scores a session by selecting the highest tier reached across its turns.
        /// </summary>
        public static ChatHistoryScoreResult Score(ChatHistorySession session, string query)
        {
            if (session?.Turns == null || session.Turns.Count == 0)
                return ChatHistoryScoreResult.NoMatch;

            string[] tokens = KnowledgeScorer.TokenizeQuery(query);
            if (tokens.Length == 0)
                return ChatHistoryScoreResult.NoMatch;

            bool multiWord = tokens.Length > 1;

            // Tier 5 — whole phrase inside a user question.
            int questionExactCount = 0;
            int questionExactTurn = -1;
            int questionExactIndex = -1;
            string questionExactHeading = null;

            // Tier 4 — whole phrase inside a final assistant answer.
            int answerExactCount = 0;
            int answerExactTurn = -1;
            int answerExactIndex = -1;
            string answerExactHeading = null;

            // Tier 3 — all query tokens inside a single turn.
            int turnsAllTokens = 0;
            int turnAllTokensTurn = -1;
            int turnAllTokensIndex = -1;
            string turnAllTokensHeading = null;
            string turnAllTokensSide = null;

            // Tier 1 — partial token coverage within a turn.
            int bestPartial = 0;
            int partialTurn = -1;
            int partialIndex = -1;
            string partialHeading = null;
            string partialSide = null;

            // Tier 2 — all query tokens somewhere in the session, but spread across turns.
            var sessionTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int firstTokenTurn = -1;
            int firstTokenIndex = -1;
            string firstTokenSide = null;

            for (int t = 0; t < session.Turns.Count; t++)
            {
                var turn = session.Turns[t];
                string question = turn.Question ?? string.Empty;
                string answer = turn.Answer ?? string.Empty;

                int qIndex = FindPhrase(question, query, multiWord);
                if (qIndex >= 0)
                {
                    questionExactCount += CountPhrase(question, query, multiWord);
                    if (questionExactTurn < 0)
                    {
                        questionExactTurn = t;
                        questionExactIndex = qIndex;
                        questionExactHeading = question;
                    }
                }

                int aIndex = answer.Length > 0 ? FindPhrase(answer, query, multiWord) : -1;
                if (aIndex >= 0)
                {
                    answerExactCount += CountPhrase(answer, query, multiWord);
                    if (answerExactTurn < 0)
                    {
                        answerExactTurn = t;
                        answerExactIndex = aIndex;
                        answerExactHeading = question;
                    }
                }

                int matchedInTurn = 0;
                int firstInQuestion = -1;
                int firstInAnswer = -1;
                bool allInTurn = true;

                foreach (string token in tokens)
                {
                    bool inQuestion = KnowledgeScorer.TryIndexOfTokenWithWordBoundaries(question, token, out int inQ);

                    int inA = -1;
                    bool inAnswer = answer.Length > 0 && KnowledgeScorer.TryIndexOfTokenWithWordBoundaries(answer, token, out inA);

                    if (inQuestion || inAnswer)
                    {
                        matchedInTurn++;
                        sessionTokens.Add(token);

                        if (inQuestion && firstInQuestion < 0) firstInQuestion = inQ;
                        if (inAnswer && firstInAnswer < 0) firstInAnswer = inA;

                        if (firstTokenTurn < 0)
                        {
                            firstTokenTurn = t;
                            firstTokenSide = inQuestion ? UserRole : AssistantRole;
                            firstTokenIndex = inQuestion ? inQ : inA;
                        }
                    }
                    else
                    {
                        allInTurn = false;
                    }
                }

                if (matchedInTurn > bestPartial)
                {
                    bestPartial = matchedInTurn;
                    partialTurn = t;
                    partialHeading = question;
                    partialSide = firstInQuestion >= 0 ? UserRole : AssistantRole;
                    partialIndex = firstInQuestion >= 0 ? firstInQuestion : firstInAnswer;
                }

                if (allInTurn && multiWord)
                {
                    turnsAllTokens++;
                    if (turnAllTokensTurn < 0)
                    {
                        turnAllTokensTurn = t;
                        turnAllTokensHeading = question;
                        turnAllTokensSide = firstInQuestion >= 0 ? UserRole : AssistantRole;
                        turnAllTokensIndex = firstInQuestion >= 0 ? firstInQuestion : firstInAnswer;
                    }
                }
            }

            bool sessionAllTokens = multiWord && firstTokenTurn >= 0 && sessionTokens.Count >= tokens.Length;

            if (questionExactCount > 0)
            {
                return Build(
                    ChatHistoryMatchKind.QuestionExact, questionExactCount, questionExactTurn, UserRole,
                    questionExactHeading, session.Turns[questionExactTurn].Question, questionExactIndex);
            }

            if (answerExactCount > 0)
            {
                return Build(
                    ChatHistoryMatchKind.AnswerExact, answerExactCount, answerExactTurn, AssistantRole,
                    answerExactHeading, session.Turns[answerExactTurn].Answer, answerExactIndex);
            }

            if (turnsAllTokens > 0)
            {
                return Build(
                    ChatHistoryMatchKind.TurnAllTokens, turnsAllTokens, turnAllTokensTurn, turnAllTokensSide,
                    turnAllTokensHeading, TextFor(session.Turns[turnAllTokensTurn], turnAllTokensSide), turnAllTokensIndex);
            }

            if (sessionAllTokens)
            {
                return Build(
                    ChatHistoryMatchKind.SessionAllTokens, 1, firstTokenTurn, firstTokenSide,
                    session.Turns[firstTokenTurn].Question, TextFor(session.Turns[firstTokenTurn], firstTokenSide), firstTokenIndex);
            }

            if (bestPartial > 0)
            {
                return Build(
                    ChatHistoryMatchKind.TurnPartial, bestPartial, partialTurn, partialSide,
                    partialHeading, TextFor(session.Turns[partialTurn], partialSide), partialIndex);
            }

            return ChatHistoryScoreResult.NoMatch;
        }

        private static ChatHistoryScoreResult Build(
            ChatHistoryMatchKind kind,
            int subScore,
            int turnIndex,
            string role,
            string heading,
            string content,
            int index)
        {
            string snippet = index >= 0
                ? KnowledgeScorer.BuildSnippet(content ?? string.Empty, index, SnippetRadius)
                : string.Empty;

            return new ChatHistoryScoreResult(kind, subScore, turnIndex, role, heading, snippet);
        }

        private static string TextFor(ChatHistoryTurn turn, string side)
        {
            return string.Equals(side, UserRole, StringComparison.Ordinal)
                ? (turn.Question ?? string.Empty)
                : (turn.Answer ?? string.Empty);
        }

        /// <summary>
        /// Finds the first occurrence of the query in <paramref name="text"/>.
        /// Multi-word queries match as a plain substring; single-word queries require word boundaries.
        /// </summary>
        private static int FindPhrase(string text, string query, bool multiWord)
        {
            if (string.IsNullOrEmpty(text))
                return -1;

            if (multiWord)
                return text.IndexOf(query, StringComparison.OrdinalIgnoreCase);

            return KnowledgeScorer.TryIndexOfTokenWithWordBoundaries(text, query, out int index) ? index : -1;
        }

        /// <summary>
        /// Counts occurrences of the query in <paramref name="text"/> using the same matching rules as <see cref="FindPhrase"/>.
        /// </summary>
        private static int CountPhrase(string text, string query, bool multiWord)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
                return 0;

            int count = 0;
            int i = 0;

            while (i < text.Length)
            {
                int found = text.IndexOf(query, i, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                    break;

                if (multiWord)
                {
                    count++;
                }
                else
                {
                    bool leftBoundary = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
                    bool rightBoundary = found + query.Length >= text.Length || !char.IsLetterOrDigit(text[found + query.Length]);
                    if (leftBoundary && rightBoundary)
                        count++;
                }

                i = found + query.Length;
            }

            return count;
        }

        /// <summary>
        /// True when the message carries a non-empty tool-call collection.
        /// </summary>
        private static bool HasToolCalls(ChatMessage message)
        {
            if (message.ToolCalls == null)
                return false;

            if (message.ToolCalls is ICollection collection)
                return collection.Count > 0;

            return true;
        }
    }
}
