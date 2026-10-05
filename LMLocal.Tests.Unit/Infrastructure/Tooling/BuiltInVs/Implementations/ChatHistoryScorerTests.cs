using System;
using System.Collections.Generic;
using LMLocal.Core.Models;
using LMLocal.Infrastructure.Tooling.BuiltInVs.Common.Search;
using NUnit.Framework;

namespace LMLocal.Tests.Unit.Infrastructure.Tooling.BuiltInVs.Implementations
{
    [TestFixture]
    public class ChatHistoryScorerTests
    {
        private static readonly DateTime T0 = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private static ChatLogEntry Msg(string role, string text, int minute = 0, object toolCalls = null)
        {
            return new ChatLogEntry
            {
                Message = new ChatMessage(role, text) { ToolCalls = toolCalls },
                TimestampUtc = T0.AddMinutes(minute)
            };
        }

        private static ChatHistorySession Session(params ChatHistoryTurn[] turns)
        {
            return new ChatHistorySession
            {
                SessionId = "session-1",
                Title = "title",
                Turns = new List<ChatHistoryTurn>(turns)
            };
        }

        private static ChatHistoryTurn Turn(string question, string answer = null)
        {
            return new ChatHistoryTurn { Question = question, Answer = answer, TimestampUtc = T0 };
        }

        // ---------------------------------------------------------------
        // Projection: turn extraction (user heading + final assistant body).
        // ---------------------------------------------------------------

        [Test]
        public void ProjectSession_UserOpensTurn_AssistantIsAnswer()
        {
            var session = ChatHistoryScorer.ProjectSession("s", new[]
            {
                Msg("user", "Q1", 0),
                Msg("assistant", "A1", 1)
            });

            Assert.That(session.Turns.Count, Is.EqualTo(1));
            Assert.That(session.Turns[0].Question, Is.EqualTo("Q1"));
            Assert.That(session.Turns[0].Answer, Is.EqualTo("A1"));
        }

        [Test]
        public void ProjectSession_AssistantWithToolCalls_IsSkipped()
        {
            var toolCalls = new List<object> { "call" };

            var session = ChatHistoryScorer.ProjectSession("s", new[]
            {
                Msg("user", "Q1", 0),
                Msg("assistant", "A1", 1),
                Msg("assistant", "intermediate narration", 2, toolCalls)
            });

            Assert.That(session.Turns[0].Answer, Is.EqualTo("A1"),
                "An assistant message carrying tool calls must not become the turn answer.");
        }

        [Test]
        public void ProjectSession_ToolRoleMessages_AreSkipped()
        {
            var session = ChatHistoryScorer.ProjectSession("s", new[]
            {
                Msg("user", "Q1", 0),
                Msg("tool", "tool output", 1),
                Msg("assistant", "A1", 2)
            });

            Assert.That(session.Turns.Count, Is.EqualTo(1));
            Assert.That(session.Turns[0].Answer, Is.EqualTo("A1"));
        }

        [Test]
        public void ProjectSession_EmptyOrWhitespaceContent_IsSkipped()
        {
            var session = ChatHistoryScorer.ProjectSession("s", new[]
            {
                Msg("user", "", 0),
                Msg("assistant", "   ", 1),
                Msg("user", "Q1", 2)
            });

            Assert.That(session.Turns.Count, Is.EqualTo(1));
            Assert.That(session.Turns[0].Question, Is.EqualTo("Q1"));
        }

        [Test]
        public void ProjectSession_LastAssistantMessageWins()
        {
            var session = ChatHistoryScorer.ProjectSession("s", new[]
            {
                Msg("user", "Q1", 0),
                Msg("assistant", "A1-first", 1),
                Msg("assistant", "A1-final", 2)
            });

            Assert.That(session.Turns[0].Answer, Is.EqualTo("A1-final"));
        }

        [Test]
        public void ProjectSession_MultipleTurns()
        {
            var session = ChatHistoryScorer.ProjectSession("s", new[]
            {
                Msg("user", "Q1", 0),
                Msg("assistant", "A1", 1),
                Msg("user", "Q2", 2),
                Msg("assistant", "A2", 3)
            });

            Assert.That(session.Turns.Count, Is.EqualTo(2));
            Assert.That(session.Turns[0].Question, Is.EqualTo("Q1"));
            Assert.That(session.Turns[1].Question, Is.EqualTo("Q2"));
            Assert.That(session.Turns[1].Answer, Is.EqualTo("A2"));
        }

        [Test]
        public void ProjectSession_Title_IsFullFirstUserMessage()
        {
            string longQuestion = new string('x', 250);

            var session = ChatHistoryScorer.ProjectSession("s", new[]
            {
                Msg("user", longQuestion, 0),
                Msg("assistant", "A1", 1),
                Msg("user", "Second question", 2)
            });

            // The scorer keeps the raw text; presentation truncation is the caller's (tool's) job.
            Assert.That(session.Title, Is.EqualTo(longQuestion));
        }

        [Test]
        public void ProjectSession_Timestamp_IsLastEntry()
        {
            var session = ChatHistoryScorer.ProjectSession("s", new[]
            {
                Msg("user", "Q1", 0),
                Msg("assistant", "A1", 5)
            });

            Assert.That(session.TimestampUtc, Is.EqualTo(T0.AddMinutes(5)));
        }

        [Test]
        public void ProjectSession_NullOrEmpty_ReturnsEmptySession()
        {
            Assert.That(ChatHistoryScorer.ProjectSession("s", null).Turns, Is.Empty);
            Assert.That(ChatHistoryScorer.ProjectSession("s", new List<ChatLogEntry>()).Turns, Is.Empty);
        }

        // ---------------------------------------------------------------
        // Strict tier ordering.
        // ---------------------------------------------------------------

        [Test]
        public void Score_QuestionExact_RanksAboveAnswerExact()
        {
            var inQuestion = ChatHistoryScorer.Score(
                Session(Turn("How to configure NLog?", "unrelated answer")), "NLog");

            var inAnswer = ChatHistoryScorer.Score(
                Session(Turn("how do I log?", "Use NLog for logging")), "NLog");

            Assert.That(inQuestion.Kind, Is.EqualTo(ChatHistoryMatchKind.QuestionExact));
            Assert.That(inAnswer.Kind, Is.EqualTo(ChatHistoryMatchKind.AnswerExact));
            Assert.That(inQuestion.Kind > inAnswer.Kind, Is.True);
        }

        [Test]
        public void Score_AnswerExact_RanksAboveTurnAllTokens()
        {
            var exact = ChatHistoryScorer.Score(
                Session(Turn("how do I log?", "Use NLog for logging")), "NLog");

            var allTokens = ChatHistoryScorer.Score(
                Session(Turn("about chat", "and history")), "chat history");

            Assert.That(exact.Kind, Is.EqualTo(ChatHistoryMatchKind.AnswerExact));
            Assert.That(allTokens.Kind, Is.EqualTo(ChatHistoryMatchKind.TurnAllTokens));
            Assert.That(exact.Kind > allTokens.Kind, Is.True);
        }

        [Test]
        public void Score_TurnAllTokens_RanksAboveSessionAllTokens()
        {
            var inTurn = ChatHistoryScorer.Score(
                Session(Turn("about chat", "and history")), "chat history");

            var spread = ChatHistoryScorer.Score(
                Session(Turn("chat stuff"), Turn("history stuff")), "chat history");

            Assert.That(inTurn.Kind, Is.EqualTo(ChatHistoryMatchKind.TurnAllTokens));
            Assert.That(spread.Kind, Is.EqualTo(ChatHistoryMatchKind.SessionAllTokens));
            Assert.That(inTurn.Kind > spread.Kind, Is.True);
        }

        [Test]
        public void Score_SessionAllTokens_RanksAboveTurnPartial()
        {
            var spread = ChatHistoryScorer.Score(
                Session(Turn("chat stuff"), Turn("history stuff")), "chat history");

            var partial = ChatHistoryScorer.Score(
                Session(Turn("chat only")), "chat history");

            Assert.That(spread.Kind, Is.EqualTo(ChatHistoryMatchKind.SessionAllTokens));
            Assert.That(partial.Kind, Is.EqualTo(ChatHistoryMatchKind.TurnPartial));
            Assert.That(spread.Kind > partial.Kind, Is.True);
        }

        [Test]
        public void Score_ManyPartialTurns_DoNotOutrankTurnAllTokens()
        {
            var allTokens = ChatHistoryScorer.Score(
                Session(Turn("about chat", "and history")), "chat history");

            // Four turns, each matching a single token — still only TurnPartial tier.
            var manyPartial = ChatHistoryScorer.Score(
                Session(
                    Turn("chat one"),
                    Turn("chat two"),
                    Turn("chat three"),
                    Turn("chat four")),
                "chat history");

            Assert.That(allTokens.Kind, Is.EqualTo(ChatHistoryMatchKind.TurnAllTokens));
            Assert.That(manyPartial.Kind, Is.EqualTo(ChatHistoryMatchKind.TurnPartial));
            Assert.That(allTokens.Kind > manyPartial.Kind, Is.True);
        }

        // ---------------------------------------------------------------
        // Turn selection / boundaries.
        // ---------------------------------------------------------------

        [Test]
        public void Score_MatchReportedForCorrectTurn()
        {
            var result = ChatHistoryScorer.Score(
                Session(
                    Turn("completely unrelated"),
                    Turn("How to configure NLog?")),
                "NLog");

            Assert.That(result.Kind, Is.EqualTo(ChatHistoryMatchKind.QuestionExact));
            Assert.That(result.TurnIndex, Is.EqualTo(1));
            Assert.That(result.Heading, Is.EqualTo("How to configure NLog?"));
        }

        [Test]
        public void Score_TurnAllTokens_RequiresAllTokensInOneTurn()
        {
            var result = ChatHistoryScorer.Score(
                Session(
                    Turn("chat here"),
                    Turn("history there")),
                "chat history");

            Assert.That(result.Kind, Is.Not.EqualTo(ChatHistoryMatchKind.TurnAllTokens));
            Assert.That(result.Kind, Is.EqualTo(ChatHistoryMatchKind.SessionAllTokens));
        }

        [Test]
        public void Score_HeadingContributesToTurnTokens()
        {
            // "chat" in the question + "history" in the answer → both tokens within one turn.
            var result = ChatHistoryScorer.Score(
                Session(Turn("chat topic", "history details")), "chat history");

            Assert.That(result.Kind, Is.EqualTo(ChatHistoryMatchKind.TurnAllTokens));
        }

        // ---------------------------------------------------------------
        // Role + snippet.
        // ---------------------------------------------------------------

        [Test]
        public void Score_QuestionMatch_RoleIsUser()
        {
            var result = ChatHistoryScorer.Score(
                Session(Turn("How to configure NLog?")), "NLog");

            Assert.That(result.Role, Is.EqualTo("user"));
        }

        [Test]
        public void Score_AnswerMatch_RoleIsAssistant()
        {
            var result = ChatHistoryScorer.Score(
                Session(Turn("how do I log?", "Use NLog")), "NLog");

            Assert.That(result.Role, Is.EqualTo("assistant"));
        }

        [Test]
        public void Score_Snippet_IsPopulatedAroundMatch()
        {
            var result = ChatHistoryScorer.Score(
                Session(Turn("How to configure NLog logging for the service?")), "NLog");

            Assert.That(result.Snippet, Is.Not.Empty);
            Assert.That(result.Snippet, Does.Contain("NLog"));
        }

        [Test]
        public void Score_AnswerSnippet_ComesFromAnswer()
        {
            var result = ChatHistoryScorer.Score(
                Session(Turn("some question", "Configure logging via NLog in the startup path.")), "NLog");

            Assert.That(result.Snippet, Does.Contain("NLog"));
        }

        // ---------------------------------------------------------------
        // Word boundaries + edge cases.
        // ---------------------------------------------------------------

        [Test]
        public void Score_SingleWord_RespectsWordBoundaries()
        {
            // "api" inside "rapid" must not match.
            var result = ChatHistoryScorer.Score(
                Session(Turn("the system is rapid")), "api");

            Assert.That(result.Kind, Is.EqualTo(ChatHistoryMatchKind.None));
        }

        [Test]
        public void Score_SingleWord_MatchesAtWordBoundary()
        {
            var result = ChatHistoryScorer.Score(
                Session(Turn("the api is fast")), "api");

            Assert.That(result.Kind, Is.EqualTo(ChatHistoryMatchKind.QuestionExact));
        }

        [Test]
        public void Score_EmptyOrNullQuery_ReturnsNoMatch()
        {
            Assert.That(ChatHistoryScorer.Score(Session(Turn("Q1")), "").IsMatch, Is.False);
            Assert.That(ChatHistoryScorer.Score(Session(Turn("Q1")), null).IsMatch, Is.False);
            Assert.That(ChatHistoryScorer.Score(Session(Turn("Q1")), "   ").IsMatch, Is.False);
        }

        [Test]
        public void Score_NoMatch_ReturnsNoMatch()
        {
            var result = ChatHistoryScorer.Score(Session(Turn("nothing relevant")), "zzzz");

            Assert.That(result.IsMatch, Is.False);
            Assert.That(result.Kind, Is.EqualTo(ChatHistoryMatchKind.None));
        }

        [Test]
        public void Score_EmptySession_ReturnsNoMatch()
        {
            var session = new ChatHistorySession { SessionId = "s", Turns = new List<ChatHistoryTurn>() };

            Assert.That(ChatHistoryScorer.Score(session, "anything").IsMatch, Is.False);
        }

        // ---------------------------------------------------------------
        // Tokenizer reuse: phrases, dedup, boundaries, apostrophes.
        // ---------------------------------------------------------------

        [Test]
        public void Score_MultiWord_RequiresAdjacentPhraseForExact()
        {
            // Adjacent phrase → QuestionExact.
            var adjacent = ChatHistoryScorer.Score(Session(Turn("about chat history in general")), "chat history");
            Assert.That(adjacent.Kind, Is.EqualTo(ChatHistoryMatchKind.QuestionExact));

            // Same tokens, not adjacent → only TurnAllTokens (no exact phrase).
            var separated = ChatHistoryScorer.Score(Session(Turn("chat and then history")), "chat history");
            Assert.That(separated.Kind, Is.EqualTo(ChatHistoryMatchKind.TurnAllTokens));
        }

        [Test]
        public void Score_DuplicateTokens_AreDeduplicated()
        {
            // "chat chat history" tokenizes to {chat, history}, same as "chat history".
            var result = ChatHistoryScorer.Score(Session(Turn("about chat", "and history")), "chat chat history");

            Assert.That(result.Kind, Is.EqualTo(ChatHistoryMatchKind.TurnAllTokens));
        }

        [Test]
        public void Score_UnderscoreAndDash_AreWordBoundaries()
        {
            Assert.That(
                ChatHistoryScorer.Score(Session(Turn("see foo_bar here")), "foo").Kind,
                Is.EqualTo(ChatHistoryMatchKind.QuestionExact));

            Assert.That(
                ChatHistoryScorer.Score(Session(Turn("see foo-bar here")), "foo").Kind,
                Is.EqualTo(ChatHistoryMatchKind.QuestionExact));

            // No boundary → no match.
            Assert.That(
                ChatHistoryScorer.Score(Session(Turn("foobar")), "foo").Kind,
                Is.EqualTo(ChatHistoryMatchKind.None));
        }

        [Test]
        public void Score_ApostropheIsKeptInsideToken()
        {
            var result = ChatHistoryScorer.Score(Session(Turn("what's new")), "what's");

            Assert.That(result.Kind, Is.EqualTo(ChatHistoryMatchKind.QuestionExact));
        }

        [Test]
        public void Score_ExactSubScore_CountsOccurrences()
        {
            var result = ChatHistoryScorer.Score(Session(Turn("NLog NLog NLog")), "NLog");

            Assert.That(result.Kind, Is.EqualTo(ChatHistoryMatchKind.QuestionExact));
            Assert.That(result.SubScore, Is.EqualTo(3));
        }

        [Test]
        public void ProjectSession_ContentParts_TextIsExtracted()
        {
            var parts = new List<ContentPart>
            {
                new ContentPart { Type = "text", Text = "NLog setup" },
                new ContentPart { Type = "image_url", ImageUrl = new ImageUrlInfo { Url = "data:image/png;base64,AAAA" } }
            };

            var entry = new ChatLogEntry
            {
                Message = new ChatMessage("user", parts),
                TimestampUtc = T0
            };

            var session = ChatHistoryScorer.ProjectSession("s", new[] { entry });

            Assert.That(session.Turns.Count, Is.EqualTo(1));
            Assert.That(session.Turns[0].Question, Is.EqualTo("NLog setup"));
        }
    }
}


































