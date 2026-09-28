using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LMLocal.Application.Abstractions.Ports;
using LMLocal.Core.Models;
using LMLocal.Infrastructure.Persistence;
using LMLocal.Infrastructure.Tooling;
using LMLocal.Infrastructure.Tooling.BuiltInVs.Common;
using LMLocal.Infrastructure.Tooling.BuiltInVs.Common.Search;
using LMLocal.Infrastructure.Tooling.BuiltInVs.Implementations;
using Moq;
using NUnit.Framework;

namespace LMLocal.Tests.Unit.Infrastructure.Tooling.BuiltInVs.Implementations
{
    [TestFixture]
    public class SearchChatHistoryTests
    {
        private static readonly DateTime T0 = new DateTime(2024, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        private Mock<IChatPersistenceService> _persistenceMock;
        private Mock<ISettingsManager> _settingsMock;
        private SearchResultCache _cache;
        private SearchChatHistory _tool;

        [SetUp]
        public void SetUp()
        {
            _persistenceMock = new Mock<IChatPersistenceService>();
            _settingsMock = new Mock<ISettingsManager>();
            _settingsMock.SetupGet(s => s.Current).Returns(new AppSettings { EnableChatLogging = true });
            _cache = new SearchResultCache();
            _tool = new SearchChatHistory(_persistenceMock.Object, _cache, _settingsMock.Object);

            _persistenceMock
                .Setup(p => p.ReadAllSessionsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ChatSessionLog>());
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private static ChatLogEntry Msg(string role, string text, int minute = 0)
        {
            return new ChatLogEntry
            {
                Message = new ChatMessage(role, text),
                TimestampUtc = T0.AddMinutes(minute)
            };
        }

        private static ChatSessionLog Log(string sessionId, params ChatLogEntry[] entries)
        {
            return new ChatSessionLog
            {
                SessionId = sessionId,
                Entries = new List<ChatLogEntry>(entries)
            };
        }

        private void SetupSessions(params ChatSessionLog[] sessions)
        {
            _persistenceMock
                .Setup(p => p.ReadAllSessionsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ChatSessionLog>(sessions));
        }

        private static ChatHistorySearchResponse Run(SearchChatHistory tool, Dictionary<string, object> parameters)
        {
            return (ChatHistorySearchResponse)tool.ExecuteAsync(parameters, CancellationToken.None).GetAwaiter().GetResult();
        }

        // ---------------------------------------------------------------
        // Metadata
        // ---------------------------------------------------------------

        [Test]
        public void GetToolInfo_ReturnsCorrectMetadata()
        {
            var info = _tool.GetToolInfo();

            Assert.That(info.Name, Is.EqualTo("search_chat_history"));
            Assert.That(_tool.ToolName, Is.EqualTo("search_chat_history"));
            Assert.That(_tool.AccessLevel, Is.EqualTo(ToolAccessLevel.ReadOnly));
            Assert.That(info.Parameters.Properties, Contains.Key("query"));
            Assert.That(info.Parameters.Properties.ContainsKey("scope"), Is.False);
            Assert.That(info.Parameters.Properties, Contains.Key("max_results"));
            Assert.That(info.Parameters.Required, Is.EquivalentTo(new[] { "query" }));
        }

        // ---------------------------------------------------------------
        // Basic search
        // ---------------------------------------------------------------

        [Test]
        public void Execute_MatchInQuestion_ReturnsUserRole()
        {
            SetupSessions(Log("s1", Msg("user", "How to configure NLog?", 0), Msg("assistant", "Use InternalLogger.", 1)));

            var resp = Run(_tool, new Dictionary<string, object> { { "query", "NLog" } });

            Assert.That(resp.Success, Is.True);
            Assert.That(resp.Results, Has.Count.EqualTo(1));
            Assert.That(resp.Results[0].Role, Is.EqualTo("user"));
            Assert.That(resp.Results[0].Heading, Is.EqualTo("How to configure NLog?"));
            Assert.That(resp.Results[0].Snippet, Is.Not.Empty);
            Assert.That(resp.TotalMatches, Is.EqualTo(1));
        }

        [Test]
        public void Execute_MatchInAnswer_ReturnsAssistantRole()
        {
            SetupSessions(Log("s1", Msg("user", "how do I log?", 0), Msg("assistant", "Use NLog via InternalLogger.", 1)));

            var resp = Run(_tool, new Dictionary<string, object> { { "query", "NLog" } });

            Assert.That(resp.Results, Has.Count.EqualTo(1));
            Assert.That(resp.Results[0].Role, Is.EqualTo("assistant"));
            Assert.That(resp.Results[0].Heading, Is.EqualTo("how do I log?"));
        }

        [Test]
        public void Execute_LongSessionTitle_IsTruncatedForPresentation()
        {
            string longQuestion = new string('x', 250) + " NLog";
            SetupSessions(Log("s1", Msg("user", longQuestion, 0)));

            var resp = Run(_tool, new Dictionary<string, object> { { "query", "NLog" } });

            Assert.That(resp.Results, Has.Count.EqualTo(1));
            Assert.That(resp.Results[0].SessionTitle.Length, Is.LessThan(longQuestion.Length));
            Assert.That(resp.Results[0].SessionTitle.EndsWith("..."), Is.True);
        }

        [Test]
        public void Execute_ExcludesCurrentSession()
        {
            var currentGuid = Guid.NewGuid();
            _persistenceMock.SetupGet(p => p.CurrentSessionId).Returns((Guid?)currentGuid);

            string currentId = currentGuid.ToString();
            string otherId = Guid.NewGuid().ToString();

            SetupSessions(
                Log(currentId, Msg("user", "NLog config for the current session", 0)),
                Log(otherId, Msg("user", "NLog config in an older session", 1)));

            var resp = Run(_tool, new Dictionary<string, object> { { "query", "NLog" } });

            Assert.That(resp.Results, Has.Count.EqualTo(1));
            Assert.That(resp.Results[0].SessionId, Is.EqualTo(otherId));
        }

        // ---------------------------------------------------------------
        // Outcomes
        // ---------------------------------------------------------------

        [Test]
        public void Execute_NoSessions_ReturnsNoFilesOutcome()
        {
            SetupSessions();

            var resp = Run(_tool, new Dictionary<string, object> { { "query", "NLog" } });

            Assert.That(resp.Success, Is.True);
            Assert.That(resp.TotalMatches, Is.EqualTo(0));
            Assert.That(resp.Outcome, Is.EqualTo(ChatHistorySearchOutcome.NoFiles));
        }

        [Test]
        public void Execute_NoMatches_ReturnsNoMatchesOutcome()
        {
            SetupSessions(Log("s1", Msg("user", "completely unrelated topic", 0)));

            var resp = Run(_tool, new Dictionary<string, object> { { "query", "Flibbertygibbet" } });

            Assert.That(resp.Success, Is.True);
            Assert.That(resp.TotalMatches, Is.EqualTo(0));
            Assert.That(resp.Outcome, Is.EqualTo(ChatHistorySearchOutcome.NoMatches));
        }

        // ---------------------------------------------------------------
        // scope (reserved: kept internal for the future; not exposed in the tool schema)
        // ---------------------------------------------------------------

        [Test]
        public void Execute_ScopeAll_Works()
        {
            SetupSessions(Log("s1", Msg("user", "NLog config", 0)));

            var resp = Run(_tool, new Dictionary<string, object> { { "query", "NLog" }, { "scope", "all" } });

            Assert.That(resp.Success, Is.True);
            Assert.That(resp.Results, Has.Count.EqualTo(1));
        }

        [Test]
        public void Execute_ScopeCurrentSolution_IsAccepted()
        {
            SetupSessions(Log("s1", Msg("user", "NLog config", 0)));

            var resp = Run(_tool, new Dictionary<string, object> { { "query", "NLog" }, { "scope", "current_solution" } });

            Assert.That(resp.Success, Is.True);
            Assert.That(resp.Results, Has.Count.EqualTo(1));
        }

        [Test]
        public void Execute_InvalidScope_ReturnsError()
        {
            var resp = Run(_tool, new Dictionary<string, object> { { "query", "NLog" }, { "scope", "nonsense" } });

            Assert.That(resp.Success, Is.False);
            Assert.That(resp.ErrorMessage, Does.Contain("scope"));
        }

        // ---------------------------------------------------------------
        // Ranking / limits
        // ---------------------------------------------------------------

        [Test]
        public void Execute_RespectsMaxResults_And_ReportsTotalMatches()
        {
            SetupSessions(
                Log("s1", Msg("user", "NLog config one", 0)),
                Log("s2", Msg("user", "NLog config two", 1)));

            var resp = Run(_tool, new Dictionary<string, object> { { "query", "NLog" }, { "max_results", 1 } });

            Assert.That(resp.Results, Has.Count.EqualTo(1));
            Assert.That(resp.TotalMatches, Is.EqualTo(2));
        }

        [Test]
        public void Execute_QuestionMatch_RanksAboveAnswerMatch()
        {
            SetupSessions(
                Log("answer-session", Msg("user", "how do I log?", 5), Msg("assistant", "Use NLog.", 6)),
                Log("question-session", Msg("user", "NLog configuration", 0)));

            var resp = Run(_tool, new Dictionary<string, object> { { "query", "NLog" } });

            Assert.That(resp.Results, Has.Count.EqualTo(2));
            Assert.That(resp.Results[0].SessionId, Is.EqualTo("question-session"));
        }

        [Test]
        public void Execute_SameTierAndSubScore_NewerSessionRanksFirst()
        {
            SetupSessions(
                Log("older", Msg("user", "NLog config", 0)),
                Log("newer", Msg("user", "NLog config", 30)));

            var resp = Run(_tool, new Dictionary<string, object> { { "query", "NLog" } });

            Assert.That(resp.Results, Has.Count.EqualTo(2));
            Assert.That(resp.Results[0].SessionId, Is.EqualTo("newer"));
            Assert.That(resp.Results[1].SessionId, Is.EqualTo("older"));
        }

        [Test]
        public void Execute_EqualTimestampAndTier_OrdersBySessionId()
        {
            SetupSessions(
                Log("zzz", Msg("user", "NLog config", 0)),
                Log("aaa", Msg("user", "NLog config", 0)));

            var resp = Run(_tool, new Dictionary<string, object> { { "query", "NLog" } });

            Assert.That(resp.Results, Has.Count.EqualTo(2));
            Assert.That(resp.Results[0].SessionId, Is.EqualTo("aaa"));
            Assert.That(resp.Results[1].SessionId, Is.EqualTo("zzz"));
        }

        [Test]
        public void Execute_ScansAtMostMaxSessionsToScan()
        {
            var sessions = new List<ChatSessionLog>();
            for (int i = 0; i < SearchChatHistory.MaxSessionsToScan + 5; i++)
                sessions.Add(Log($"s{i:D5}", Msg("user", "NLog config", i)));

            SetupSessions(sessions.ToArray());

            var resp = Run(_tool, new Dictionary<string, object> { { "query", "NLog" } });

            Assert.That(resp.Success, Is.True);
            Assert.That(resp.TotalMatches, Is.EqualTo(SearchChatHistory.MaxSessionsToScan));
        }

        // ---------------------------------------------------------------
        // Validation
        // ---------------------------------------------------------------

        [Test]
        public void Execute_NullParameters_ReturnsError()
        {
            var resp = Run(_tool, null);

            Assert.That(resp.Success, Is.False);
            Assert.That(resp.ErrorMessage, Does.Contain("Parameters"));
        }

        [Test]
        public void Execute_EmptyQuery_ReturnsError()
        {
            var resp = Run(_tool, new Dictionary<string, object> { { "query", "   " } });

            Assert.That(resp.Success, Is.False);
            Assert.That(resp.ErrorMessage, Does.Contain("query"));
        }

        // ---------------------------------------------------------------
        // Read-only + caching
        // ---------------------------------------------------------------

        [Test]
        public void Execute_DoesNotUseMutatingLoadPath()
        {
            SetupSessions(Log("s1", Msg("user", "NLog config", 0)));

            Run(_tool, new Dictionary<string, object> { { "query", "NLog" } });

            _persistenceMock.Verify(p => p.LoadSessionByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _persistenceMock.Verify(p => p.ReadAllSessionsAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void Execute_SecondCall_UsesCache_DoesNotRescan()
        {
            SetupSessions(Log("s1", Msg("user", "NLog config", 0)));

            var parameters = new Dictionary<string, object> { { "query", "NLog" } };
            Run(_tool, parameters);
            Run(_tool, parameters);

            _persistenceMock.Verify(p => p.ReadAllSessionsAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        // ---------------------------------------------------------------
        // Messages
        // ---------------------------------------------------------------

        [Test]
        public void GetProcessingMessage_ContainsQuery()
        {
            var msg = _tool.GetProcessingMessage(new Dictionary<string, object> { { "query", "NLog" } });

            Assert.That(msg, Does.Contain("NLog"));
            Assert.That(msg, Does.Contain("chat history"));
        }

        [Test]
        public void GetCompletionMessage_ForSuccess_ReturnsCount()
        {
            var resp = new ChatHistorySearchResponse
            {
                Success = true,
                Outcome = ChatHistorySearchOutcome.Success,
                TotalMatches = 3,
                Results = new List<ChatHistoryMatchResult>()
            };

            Assert.That(_tool.GetCompletionMessage(resp), Does.Contain("Found 3"));
        }

        [Test]
        public void GetCompletionMessage_ForNoMatches_ReturnsNoOccurrences()
        {
            var resp = new ChatHistorySearchResponse
            {
                Success = true,
                Outcome = ChatHistorySearchOutcome.NoMatches,
                TotalMatches = 0,
                Results = new List<ChatHistoryMatchResult>()
            };

            Assert.That(_tool.GetCompletionMessage(resp), Does.Contain("No occurrences"));
        }

        [Test]
        public void GetCompletionMessage_ForNoFiles_ReturnsNoHistory()
        {
            var resp = new ChatHistorySearchResponse
            {
                Success = true,
                Outcome = ChatHistorySearchOutcome.NoFiles,
                TotalMatches = 0,
                Results = new List<ChatHistoryMatchResult>()
            };

            Assert.That(_tool.GetCompletionMessage(resp), Does.Contain("No chat history"));
        }

        [Test]
        public void GetCompletionMessage_ForNoFiles_LoggingDisabled_ReportsDisabled()
        {
            _settingsMock.SetupGet(s => s.Current).Returns(new AppSettings { EnableChatLogging = false });

            var resp = new ChatHistorySearchResponse
            {
                Success = true,
                Outcome = ChatHistorySearchOutcome.NoFiles,
                TotalMatches = 0,
                Results = new List<ChatHistoryMatchResult>()
            };

            Assert.That(_tool.GetCompletionMessage(resp), Does.Contain("disabled"));
        }
    }
}
