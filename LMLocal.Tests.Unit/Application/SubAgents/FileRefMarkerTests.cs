using System.Linq;
using LMLocal.Application.SubAgents.ReadRefs;
using NUnit.Framework;

namespace LMLocal.Tests.Unit.Application.SubAgents
{
    [TestFixture]
    public class FileRefMarkerTests
    {
        // ---------------------------------------------------------------------
        // Format
        // ---------------------------------------------------------------------

        [Test]
        public void Format_BarePath_SingleLine()
        {
            var marker = new FileRefMarker(@"LMLocal\Core\Models\StreamChunk.cs", new[] { new LineRange(32, 32) });

            Assert.That(marker.Format(), Is.EqualTo(@"@LMLocal\Core\Models\StreamChunk.cs:32"));
        }

        [Test]
        public void Format_BarePath_Range()
        {
            var marker = new FileRefMarker(@"LMLocal\Core\Models\StreamChunk.cs", new[] { new LineRange(1, 120) });

            Assert.That(marker.Format(), Is.EqualTo(@"@LMLocal\Core\Models\StreamChunk.cs:1-120"));
        }

        [Test]
        public void Format_PathWithSpaces_IsQuoted()
        {
            var marker = new FileRefMarker("dir with spaces/file.cs", new[] { new LineRange(10, 20) });

            Assert.That(marker.Format(), Is.EqualTo("@\"dir with spaces/file.cs\":10-20"));
        }

        [Test]
        public void Format_MultipleRanges_AreCommaSeparated()
        {
            var marker = new FileRefMarker("a.cs", new[] { new LineRange(11, 15), new LineRange(30, 39) });

            Assert.That(marker.Format(), Is.EqualTo("@a.cs:11-15,30-39"));
        }

        // ---------------------------------------------------------------------
        // Parse
        // ---------------------------------------------------------------------

        [Test]
        public void Parse_SingleLine_NoRange()
        {
            var markers = FileRefMarker.ParseAll("see @file.cs:32 here");

            Assert.That(markers.Count, Is.EqualTo(1));
            Assert.That(markers[0].Path, Is.EqualTo("file.cs"));
            Assert.That(markers[0].Ranges.Count, Is.EqualTo(1));
            Assert.That(markers[0].Ranges[0], Is.EqualTo(new LineRange(32, 32)));
        }

        [Test]
        public void Parse_Range()
        {
            var markers = FileRefMarker.ParseAll(@"@LMLocal\Core\Models\StreamChunk.cs:1-120");

            Assert.That(markers.Count, Is.EqualTo(1));
            Assert.That(markers[0].Path, Is.EqualTo(@"LMLocal\Core\Models\StreamChunk.cs"));
            Assert.That(markers[0].Ranges[0], Is.EqualTo(new LineRange(1, 120)));
        }

        [Test]
        public void Parse_QuotedPathWithSpaces()
        {
            var markers = FileRefMarker.ParseAll("@\"path with spaces/file.cs\":10-20");

            Assert.That(markers.Count, Is.EqualTo(1));
            Assert.That(markers[0].Path, Is.EqualTo("path with spaces/file.cs"));
            Assert.That(markers[0].Ranges[0], Is.EqualTo(new LineRange(10, 20)));
        }

        [Test]
        public void Parse_WhitespaceAfterColon_IsTolerated()
        {
            var markers = FileRefMarker.ParseAll("@file.cs: 1-10");

            Assert.That(markers.Count, Is.EqualTo(1));
            Assert.That(markers[0].Ranges[0], Is.EqualTo(new LineRange(1, 10)));
        }

        [Test]
        public void Parse_MultipleRanges()
        {
            var markers = FileRefMarker.ParseAll(@"@LMLocal.Tests.E2E.VSIX\Fixtures\reader-ranges-fixture.md:11-15,30-39");

            Assert.That(markers.Count, Is.EqualTo(1));
            Assert.That(markers[0].Ranges.Count, Is.EqualTo(2));
            Assert.That(markers[0].Ranges[0], Is.EqualTo(new LineRange(11, 15)));
            Assert.That(markers[0].Ranges[1], Is.EqualTo(new LineRange(30, 39)));
        }

        [Test]
        public void Parse_SurroundingBackticks_AreConsumed()
        {
            const string text = "`@file.cs:1-10`";
            var markers = FileRefMarker.ParseAll(text);

            Assert.That(markers.Count, Is.EqualTo(1));
            Assert.That(markers[0].StartIndex, Is.EqualTo(0));
            Assert.That(markers[0].Length, Is.EqualTo(text.Length));
            Assert.That(text.Substring(markers[0].StartIndex, markers[0].Length), Is.EqualTo(text));
        }

        [Test]
        public void Parse_MultipleMarkers_ReturnsInOrderWithCorrectSpans()
        {
            const string text = "a @one.cs:1-2 b @two.cs:3 c";
            var markers = FileRefMarker.ParseAll(text);

            Assert.That(markers.Count, Is.EqualTo(2));
            Assert.That(markers[0].Path, Is.EqualTo("one.cs"));
            Assert.That(markers[1].Path, Is.EqualTo("two.cs"));

            Assert.That(text.Substring(markers[0].StartIndex, markers[0].Length), Is.EqualTo("@one.cs:1-2"));
            Assert.That(text.Substring(markers[1].StartIndex, markers[1].Length), Is.EqualTo("@two.cs:3"));
        }

        // ---------------------------------------------------------------------
        // Malformed / rejected
        // ---------------------------------------------------------------------

        [Test]
        public void Parse_NoColon_IsRejected()
        {
            Assert.That(FileRefMarker.ParseAll("email me at user@host").Count, Is.EqualTo(0));
        }

        [Test]
        public void Parse_NoRanges_IsRejected()
        {
            Assert.That(FileRefMarker.ParseAll("@file.cs:").Count, Is.EqualTo(0));
        }

        [Test]
        public void Parse_EndBeforeStart_IsRejected()
        {
            Assert.That(FileRefMarker.ParseAll("@file.cs:20-10").Count, Is.EqualTo(0));
        }

        [Test]
        public void Parse_NumberAtIntMax_IsAccepted()
        {
            var markers = FileRefMarker.ParseAll("@file.cs:2147483647");

            Assert.That(markers.Count, Is.EqualTo(1));
            Assert.That(markers[0].Ranges[0], Is.EqualTo(new LineRange(2147483647, 2147483647)));
        }

        [Test]
        public void Parse_NumberBeyondIntMax_IsRejected()
        {
            // A line number beyond int.MaxValue is rejected, not silently saturated to int.MaxValue.
            Assert.That(FileRefMarker.ParseAll("@file.cs:2147483648").Count, Is.EqualTo(0));
            Assert.That(FileRefMarker.ParseAll("@file.cs:99999999999999999").Count, Is.EqualTo(0));
            Assert.That(FileRefMarker.CountMalformed("@file.cs:99999999999999999"), Is.EqualTo(1));
        }

        [Test]
        public void Parse_RangeEndBeyondIntMax_IsRejected()
        {
            Assert.That(FileRefMarker.ParseAll("@file.cs:1-99999999999999999").Count, Is.EqualTo(0));
        }

        [Test]
        public void Parse_AfterWordCharacter_IsRejected()
        {
            // An '@' glued to a preceding word character is not a marker (emails, URLs, identifiers).
            Assert.That(FileRefMarker.ParseAll("user@host:8080").Count, Is.EqualTo(0));
            Assert.That(FileRefMarker.ParseAll("x@file.cs:1").Count, Is.EqualTo(0));
            Assert.That(FileRefMarker.CountMalformed("user@host:8080"), Is.EqualTo(0));
        }

        [Test]
        [TestCase("(@file.cs:1)")]
        [TestCase("[@file.cs:1]")]
        [TestCase(">@file.cs:1")]
        [TestCase("@file.cs:1")]
        public void Parse_AtStartOrAfterDelimiter_IsAccepted(string text)
        {
            Assert.That(FileRefMarker.ParseAll(text).Count, Is.EqualTo(1));
        }

        [Test]
        public void Parse_VerbatimStringInSource_IsNotABareMarker()
        {
            // @"C:\temp\file.txt" has no "':' digits" tail, so it must not be reported as malformed either.
            var markers = FileRefMarker.ParseAll("var p = @\"C:\\temp\\file.txt\";");

            Assert.That(markers.Count, Is.EqualTo(0));
            Assert.That(FileRefMarker.CountMalformed("var p = @\"C:\\temp\\file.txt\";"), Is.EqualTo(0));
        }

        [Test]
        public void CountMalformed_CountsIntendedButUnparseableMarkers()
        {
            // Empty path before the colon → looks intended (ends with ":<digits>") but cannot be parsed.
            Assert.That(FileRefMarker.CountMalformed("@:1-2"), Is.EqualTo(1));
        }

        [Test]
        public void CountMalformed_DoesNotCountParsedMarkers()
        {
            const string text = "see @file.cs:1-10";
            var markers = FileRefMarker.ParseAll(text);
            Assert.That(markers.Count, Is.EqualTo(1));
            Assert.That(FileRefMarker.CountMalformed(text), Is.EqualTo(0));
        }

        // ---------------------------------------------------------------------
        // Round-trip
        // ---------------------------------------------------------------------

        [TestCase(@"@file.cs:32")]
        [TestCase(@"@dir\file.cs:1-120")]
        [TestCase("@\"dir with spaces/file.cs\":10-20")]
        [TestCase("@a.cs:11-15,30-39")]
        public void RoundTrip_FormatThenParse_IsStable(string marker)
        {
            var parsed = FileRefMarker.ParseAll(marker);
            Assert.That(parsed.Count, Is.EqualTo(1));

            var reformatted = parsed[0].Format();
            var reparsed = FileRefMarker.ParseAll(reformatted);

            Assert.That(reparsed.Count, Is.EqualTo(1));
            Assert.That(reparsed[0].Path, Is.EqualTo(parsed[0].Path));
            Assert.That(reparsed[0].Ranges.SequenceEqual(parsed[0].Ranges), Is.True);
        }
    }
}
