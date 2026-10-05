using LMLocal.Application.SubAgents.ReadRefs;
using NUnit.Framework;

namespace LMLocal.Tests.Unit.Application.SubAgents
{
    [TestFixture]
    public class ReadLedgerTests
    {
        [Test]
        public void TryResolve_ExactRange_IsResolved()
        {
            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord("src/Program.cs", 10, 40));

            Assert.That(ledger.TryResolve("src/Program.cs", new LineRange(10, 40), out var record), Is.True);
            Assert.That(record.StartLine, Is.EqualTo(10));
            Assert.That(record.EndLine, Is.EqualTo(40));
        }

        [Test]
        public void TryResolve_SubRange_IsResolved()
        {
            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord("src/Program.cs", 1, 120));

            Assert.That(ledger.TryResolve("src/Program.cs", new LineRange(30, 39), out _), Is.True);
            Assert.That(ledger.TryResolve("src/Program.cs", new LineRange(1, 120), out _), Is.True);
        }

        [Test]
        public void TryResolve_RangeExceedingRecord_IsNotResolved()
        {
            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord("src/Program.cs", 10, 40));

            Assert.That(ledger.TryResolve("src/Program.cs", new LineRange(1, 40), out _), Is.False);
            Assert.That(ledger.TryResolve("src/Program.cs", new LineRange(30, 41), out _), Is.False);
        }

        [Test]
        public void TryResolve_IsCaseInsensitive_AndSeparatorInsensitive()
        {
            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord(@"LMLocal\Core\Stream.cs", 1, 10));

            Assert.That(ledger.TryResolve("lmlocal/core/stream.cs", new LineRange(1, 10), out _), Is.True);
            Assert.That(ledger.TryResolve(@"LMLOCAL\CORE\STREAM.CS", new LineRange(5, 6), out _), Is.True);
        }

        [Test]
        public void TryResolve_DoubledBackslashes_AreNormalized()
        {
            // The model copies the ref out of a JSON payload, where '\' is escaped as '\\'.
            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord(@"LMLocal\Core\Stream.cs", 1, 10));

            Assert.That(ledger.TryResolve(@"LMLocal\\Core\\Stream.cs", new LineRange(1, 10), out _), Is.True);
        }

        [Test]
        public void TryResolve_UnknownPath_IsNotResolved()
        {
            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord("src/Program.cs", 1, 10));

            Assert.That(ledger.TryResolve("src/Other.cs", new LineRange(1, 10), out _), Is.False);
        }

        [Test]
        public void TryResolve_EmptyPath_IsNotResolved()
        {
            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord("src/Program.cs", 1, 10));

            Assert.That(ledger.TryResolve(string.Empty, new LineRange(1, 10), out _), Is.False);
        }

        [Test]
        public void Add_EmptyPath_IsIgnored()
        {
            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord(string.Empty, 1, 10));

            Assert.That(ledger.Count, Is.EqualTo(0));
        }

        [Test]
        public void TryResolve_ContiguousPages_ResolveAsUnion()
        {
            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord("src/Big.cs", 1, 80));
            ledger.Add(new ReadRecord("src/Big.cs", 81, 160));

            Assert.That(ledger.TryResolve("src/Big.cs", new LineRange(1, 160), out var record), Is.True);
            Assert.That(record.StartLine, Is.EqualTo(1));
            Assert.That(record.EndLine, Is.EqualTo(160));
        }

        [Test]
        public void TryResolve_OverlappingPages_ResolveAsUnion()
        {
            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord("src/Big.cs", 1, 80));
            ledger.Add(new ReadRecord("src/Big.cs", 60, 160));

            Assert.That(ledger.TryResolve("src/Big.cs", new LineRange(1, 160), out _), Is.True);
        }

        [Test]
        public void TryResolve_SubRangeInsideUnion_IsResolved()
        {
            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord("src/Big.cs", 1, 80));
            ledger.Add(new ReadRecord("src/Big.cs", 81, 160));

            Assert.That(ledger.TryResolve("src/Big.cs", new LineRange(75, 90), out _), Is.True);
        }

        [Test]
        public void TryResolve_RangeSpanningGap_IsNotResolved()
        {
            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord("src/Big.cs", 1, 80));
            ledger.Add(new ReadRecord("src/Big.cs", 100, 160));

            // 81..99 were never read.
            Assert.That(ledger.TryResolve("src/Big.cs", new LineRange(1, 160), out _), Is.False);
            Assert.That(ledger.TryResolve("src/Big.cs", new LineRange(1, 80), out _), Is.True);
            Assert.That(ledger.TryResolve("src/Big.cs", new LineRange(100, 160), out _), Is.True);
        }

        [Test]
        public void TryResolve_UnionIsPerPath()
        {
            var ledger = new ReadLedger();
            ledger.Add(new ReadRecord("src/A.cs", 1, 80));
            ledger.Add(new ReadRecord("src/B.cs", 81, 160));

            Assert.That(ledger.TryResolve("src/A.cs", new LineRange(1, 160), out _), Is.False);
        }
    }
}
