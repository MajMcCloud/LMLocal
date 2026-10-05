using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LMLocal.Application.SubAgents.ReadRefs;
using LMLocal.Tests.Unit.Infrastructure;
using NUnit.Framework;

namespace LMLocal.Tests.Unit.Application.SubAgents
{
    [TestFixture]
    public class ReadRefExpanderTests
    {
        private const string RelativePath = @"src\Program.cs";
        private const string AbsolutePath = @"C:\sol\src\Program.cs";

        private static ReadRefExpander CreateExpander(ReadLedger ledger, InMemoryFileSystem fileSystem)
        {
            return new ReadRefExpander(ledger, fileSystem);
        }

        private static void WriteFile(InMemoryFileSystem fileSystem, int lineCount)
        {
            var sb = new StringBuilder();
            for (int i = 1; i <= lineCount; i++)
            {
                if (i > 1)
                    sb.Append('\n');
                sb.Append("line " + i);
            }

            fileSystem.WriteAllBytesAsync(AbsolutePath, Encoding.UTF8.GetBytes(sb.ToString())).Wait();
        }

        [Test]
        public async Task Expand_LedgeredMarker_IsReplacedWithRenderedContent()
        {
            var fileSystem = new InMemoryFileSystem();
            WriteFile(fileSystem, 10);

            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord(RelativePath, 1, 10, AbsolutePath));

            var expander = CreateExpander(ledger, fileSystem);
            var result = await expander.ExpandAsync("Before\n@src\\Program.cs:1-10\nAfter", CancellationToken.None);

            Assert.That(result.ExpandedRefCount, Is.EqualTo(1));
            Assert.That(result.UnresolvedRefCount, Is.EqualTo(0));
            Assert.That(result.MalformedRefCount, Is.EqualTo(0));
            Assert.That(result.Content, Does.Contain("1: line 1"));
            Assert.That(result.Content, Does.Contain("10: line 10"));
            Assert.That(result.Content, Does.Not.Contain("@src\\Program.cs:1-10"));
            Assert.That(result.Content, Does.StartWith("Before"));
            Assert.That(result.Content, Does.EndWith("After"));
            Assert.That(result.Content, Does.Contain("````csharp"));
        }

        [Test]
        public async Task Expand_SubRangeOfRecordedRead_IsResolved()
        {
            var fileSystem = new InMemoryFileSystem();
            WriteFile(fileSystem, 200);

            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord(RelativePath, 1, 200, AbsolutePath));

            var expander = CreateExpander(ledger, fileSystem);
            var result = await expander.ExpandAsync("@src\\Program.cs:30-39", CancellationToken.None);

            Assert.That(result.ExpandedRefCount, Is.EqualTo(1));
            Assert.That(result.Content, Does.Contain("30: line 30"));
            Assert.That(result.Content, Does.Contain("39: line 39"));
            Assert.That(result.Content, Does.Not.Contain("29: line 29"));
            Assert.That(result.Content, Does.Not.Contain("40: line 40"));
        }

        [Test]
        public async Task Expand_UnledgeredMarker_IsLeftUntouched()
        {
            var fileSystem = new InMemoryFileSystem();
            WriteFile(fileSystem, 10);

            var ledger = new ReadLedger(); // empty on purpose
            var expander = CreateExpander(ledger, fileSystem);

            var result = await expander.ExpandAsync("keep @src\\Program.cs:1-10 as-is", CancellationToken.None);

            Assert.That(result.ExpandedRefCount, Is.EqualTo(0));
            Assert.That(result.UnresolvedRefCount, Is.EqualTo(1));
            Assert.That(result.Content, Is.EqualTo("keep @src\\Program.cs:1-10 as-is"));
        }

        [Test]
        public async Task Expand_FabricatedRangeOutsideRecordedRead_IsLeftUntouched()
        {
            var fileSystem = new InMemoryFileSystem();
            WriteFile(fileSystem, 200);

            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord(RelativePath, 1, 50, AbsolutePath)); // only 1..50 were actually read

            var expander = CreateExpander(ledger, fileSystem);
            var result = await expander.ExpandAsync("@src\\Program.cs:51-60", CancellationToken.None);

            Assert.That(result.ExpandedRefCount, Is.EqualTo(0));
            Assert.That(result.UnresolvedRefCount, Is.EqualTo(1));
            Assert.That(result.Content, Is.EqualTo("@src\\Program.cs:51-60"));
        }

        [Test]
        public async Task Expand_MarkerWithJsonEscapedBackslashes_IsResolved()
        {
            var fileSystem = new InMemoryFileSystem();
            WriteFile(fileSystem, 10);

            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord(RelativePath, 1, 10, AbsolutePath));

            var expander = CreateExpander(ledger, fileSystem);
            // The model copied the ref out of the (JSON) tool payload, so backslashes are doubled.
            var result = await expander.ExpandAsync(@"@src\\Program.cs:1-10", CancellationToken.None);

            Assert.That(result.ExpandedRefCount, Is.EqualTo(1));
            Assert.That(result.Content, Does.Contain("1: line 1"));
        }

        [Test]
        public async Task Expand_NoMarkers_IsNoOp()
        {
            var fileSystem = new InMemoryFileSystem();
            var ledger = new ReadLedger();
            var expander = CreateExpander(ledger, fileSystem);

            const string content = "plain answer without any markers";
            var result = await expander.ExpandAsync(content, CancellationToken.None);

            Assert.That(result.Content, Is.EqualTo(content));
            Assert.That(result.ExpandedRefCount, Is.EqualTo(0));
            Assert.That(result.UnresolvedRefCount, Is.EqualTo(0));
        }

        [Test]
        public async Task Expand_MultipleMarkers_AreExpandedInOrder()
        {
            var fileSystem = new InMemoryFileSystem();
            WriteFile(fileSystem, 20);

            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord(RelativePath, 1, 20, AbsolutePath));

            var expander = CreateExpander(ledger, fileSystem);
            var result = await expander.ExpandAsync("@src\\Program.cs:1-2 then @src\\Program.cs:19-20", CancellationToken.None);

            Assert.That(result.ExpandedRefCount, Is.EqualTo(2));

            int first = result.Content.IndexOf("1: line 1", System.StringComparison.Ordinal);
            int second = result.Content.IndexOf("19: line 19", System.StringComparison.Ordinal);
            Assert.That(first, Is.GreaterThanOrEqualTo(0));
            Assert.That(second, Is.GreaterThan(first));
        }

        [Test]
        public async Task Expand_NotRecursive_ContentContainingMarkersIsNotRescanned()
        {
            var fileSystem = new InMemoryFileSystem();
            // The file itself literally contains a marker-like line.
            fileSystem.WriteAllBytesAsync(AbsolutePath, Encoding.UTF8.GetBytes("@evil.cs:1-2\nsecond")).Wait();

            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord(RelativePath, 1, 2, AbsolutePath));

            var expander = CreateExpander(ledger, fileSystem);
            var result = await expander.ExpandAsync("@src\\Program.cs:1-2", CancellationToken.None);

            Assert.That(result.ExpandedRefCount, Is.EqualTo(1));
            Assert.That(result.Content, Does.Contain("@evil.cs:1-2"));
        }

        [Test]
        public async Task Expand_RendersFencedBlockWithoutFilePathHeader()
        {
            var fileSystem = new InMemoryFileSystem();
            WriteFile(fileSystem, 10);

            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord(RelativePath, 1, 10, AbsolutePath));

            var expander = CreateExpander(ledger, fileSystem);
            var result = await expander.ExpandAsync("@src\\Program.cs:1-10", CancellationToken.None);

            // The model prints the "**File: path**" header itself; the expander must not add a second one.
            Assert.That(result.Content, Does.Not.Contain("**`"));
            Assert.That(result.Content, Does.StartWith("````csharp"));
            Assert.That(result.Content, Does.EndWith("````"));
        }

        [Test]
        public async Task Expand_MarkerWithoutRecordedAbsolutePath_IsLeftUntouched()
        {
            var fileSystem = new InMemoryFileSystem();
            WriteFile(fileSystem, 10);

            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord(RelativePath, 1, 10)); // no absolute path captured

            var expander = CreateExpander(ledger, fileSystem);
            var result = await expander.ExpandAsync("@src\\Program.cs:1-10", CancellationToken.None);

            Assert.That(result.ExpandedRefCount, Is.EqualTo(0));
            Assert.That(result.UnresolvedRefCount, Is.EqualTo(1));
        }

        [Test]
        public async Task Expand_PagedReads_SingleMarkerForUnionIsResolved()
        {
            var fileSystem = new InMemoryFileSystem();
            WriteFile(fileSystem, 160);

            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord(RelativePath, 1, 80, AbsolutePath));
            ledger.Add(new ReadRecord(RelativePath, 81, 160, AbsolutePath));

            var expander = CreateExpander(ledger, fileSystem);
            var result = await expander.ExpandAsync("@src\\Program.cs:1-160", CancellationToken.None);

            Assert.That(result.ExpandedRefCount, Is.EqualTo(1));
            Assert.That(result.Content, Does.Contain("1: line 1"));
            Assert.That(result.Content, Does.Contain("160: line 160"));
        }
    }
}
