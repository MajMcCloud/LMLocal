using System;
using System.Linq;
using System.Threading.Tasks;
using LMLocal.Application.Abstractions.Ports;
using LMLocal.Core.Models;
using LMLocal.Infrastructure.Persistence;
using Moq;
using NUnit.Framework;

namespace LMLocal.Tests.Unit.Infrastructure
{
    /// <summary>
    /// Reproduces the parallel subagent fan-out logging scenario: each run creates its OWN
    /// <see cref="ChatPersistenceService"/> but they all append to the same hourly jsonl file.
    /// The process-wide, path-keyed <see cref="IFileLockManager"/> must keep those appends from
    /// racing so no message is lost.
    /// </summary>
    [TestFixture]
    public class ChatPersistenceServiceConcurrencyTests
    {
        private const string Dir = "C:/tests/subagentslogs";

        private static Mock<ISettingsManager> Settings()
        {
            var settings = new Mock<ISettingsManager>();
            settings.Setup(x => x.Current).Returns(new AppSettings { EnableChatLogging = true });
            settings.Setup(x => x.ChatHistoryFileLabel).Returns("subagents");
            return settings;
        }

        [Test]
        public async Task TwoInstances_SameHourlyFile_DoNotLoseWrites()
        {
            // CancelableFileSystem delays every append/write, widening the race window between the
            // "file exists?" check and the actual bytes hitting the store.
            var fs = new CancelableFileSystem();

            var first = new ChatPersistenceService(Settings().Object, fs, Dir);
            var second = new ChatPersistenceService(Settings().Object, fs, Dir);

            var firstBatch = Enumerable.Range(0, 5).Select(i => new ChatMessage("user", "a" + i)).ToList();
            var secondBatch = Enumerable.Range(0, 5).Select(i => new ChatMessage("user", "b" + i)).ToList();

            var t1 = first.SaveMessagesAsync(firstBatch);
            var t2 = second.SaveMessagesAsync(secondBatch);

            await Task.WhenAll(t1, t2).ConfigureAwait(false);

            var file = fs.GetFiles(Dir, "*.jsonl").Single();
            var lines = fs.ReadAllText(file)
                          .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            Assert.That(lines.Length, Is.EqualTo(10),
                "All writes from both instances must survive; a missing line means the writers raced on the shared file.");
            Assert.That(lines.All(l => l.Contains("\"role\":\"user\"")), Is.True);
        }
    }
}
