using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using LMLocal.Core.Common;
using NUnit.Framework;

namespace LMLocal.Tests.Unit.Core.Common
{
    [TestFixture]
    public class ExceptionFormatterTests
    {
        // ---------- Format ----------

        [Test]
        public void Format_WhenNull_ReturnsUnknownError()
        {
            Assert.That(ExceptionFormatter.Format(null), Is.EqualTo("Unknown error"));
        }

        [Test]
        public void Format_WhenOperationCanceled_ReturnsRequestTimedOut()
        {
            Assert.That(ExceptionFormatter.Format(new OperationCanceledException()), Is.EqualTo("Request timed out"));
        }

        [Test]
        public void Format_WhenTaskCanceled_ReturnsRequestTimedOut()
        {
            Assert.That(ExceptionFormatter.Format(new System.Threading.Tasks.TaskCanceledException()), Is.EqualTo("Request timed out"));
        }

        [Test]
        public void Format_WhenPlainException_ReturnsItsMessage()
        {
            Assert.That(ExceptionFormatter.Format(new InvalidOperationException("connection refused")),
                Is.EqualTo("connection refused"));
        }

        [Test]
        public void Format_WhenHttpRequestExceptionWrappingWebExceptionWrappingSocket_ExtractsSocketMessage()
        {
            var socket = new SocketException((int)SocketError.ConnectionRefused);
            var web = new WebException("The underlying connection was closed", socket);
            var http = new HttpRequestException("An error occurred while sending the request.", web);

            // The deepest inner exception (SocketException) carries the useful OS detail.
            Assert.That(ExceptionFormatter.Format(http), Is.EqualTo(socket.Message));
        }

        [Test]
        public void Format_WhenGenericHttpWrapperPresent_BypassesWrapper()
        {
            var inner = new WebException("No such host is known");
            var http = new HttpRequestException("An error occurred while sending the request.", inner);

            Assert.That(ExceptionFormatter.Format(http), Is.EqualTo("No such host is known"));
        }

        [Test]
        public void Format_WhenAggregateException_UnrollsToInner()
        {
            var aggregate = new AggregateException(
                new InvalidOperationException("boom"),
                new InvalidOperationException("other"));

            Assert.That(ExceptionFormatter.Format(aggregate), Is.EqualTo("boom"));
        }

        [Test]
        public void Format_WhenRootMessageIsGeneric_FallsBackToChain()
        {
            // Wrapper on top, generic aggregate message underneath -> chain is returned (non-empty).
            var http = new HttpRequestException(
                "An error occurred while sending the request.",
                new InvalidOperationException("One or more errors occurred"));

            var result = ExceptionFormatter.Format(http);

            Assert.That(result, Is.Not.Null.And.Not.Empty);
            Assert.That(result, Does.Not.Contain("An error occurred while sending the request"));
        }

        [Test]
        public void Format_TrimsWhitespace()
        {
            Assert.That(ExceptionFormatter.Format(new Exception("  spaced  ")), Is.EqualTo("spaced"));
        }

        // ---------- GetRootCause ----------

        [Test]
        public void GetRootCause_WhenNull_ReturnsNull()
        {
            Assert.That(ExceptionFormatter.GetRootCause(null), Is.Null);
        }

        [Test]
        public void GetRootCause_ReturnsDeepestException()
        {
            var root = new Exception("root");
            var middle = new Exception("middle", root);
            var outer = new Exception("outer", middle);

            Assert.That(ExceptionFormatter.GetRootCause(outer), Is.SameAs(root));
        }

        [Test]
        public void GetRootCause_UnrollsAggregateException()
        {
            var first = new Exception("first");
            var aggregate = new AggregateException(first, new Exception("second"));

            Assert.That(ExceptionFormatter.GetRootCause(aggregate), Is.SameAs(first));
        }

        [Test]
        public void GetRootCause_WhenNoInner_ReturnsItself()
        {
            var ex = new Exception("solo");
            Assert.That(ExceptionFormatter.GetRootCause(ex), Is.SameAs(ex));
        }

        // ---------- ToMessageChain ----------

        [Test]
        public void ToMessageChain_WhenNull_ReturnsNull()
        {
            Assert.That(ExceptionFormatter.ToMessageChain(null), Is.Null);
        }

        [Test]
        public void ToMessageChain_DeduplicatesIdenticalMessages()
        {
            var inner = new Exception("same");
            var outer = new Exception("same", inner);

            Assert.That(ExceptionFormatter.ToMessageChain(outer), Is.EqualTo("same"));
        }

        [Test]
        public void ToMessageChain_RespectsMaxDepth()
        {
            var e1 = new Exception("a");
            var e2 = new Exception("b", e1);
            var e3 = new Exception("c", e2);
            var e4 = new Exception("d", e3);

            Assert.That(ExceptionFormatter.ToMessageChain(e4, maxDepth: 2), Is.EqualTo("d \u2192 c"));
        }

        [Test]
        public void ToMessageChain_SkipsLeadingGenericWrapper()
        {
            var inner = new Exception("real cause");
            var outer = new HttpRequestException("An error occurred while sending the request.", inner);

            Assert.That(ExceptionFormatter.ToMessageChain(outer), Is.EqualTo("real cause"));
        }

        [Test]
        public void ToMessageChain_WhenAllEmpty_ReturnsUnknownError()
        {
            var inner = new Exception(string.Empty);
            var outer = new Exception(string.Empty, inner);

            Assert.That(ExceptionFormatter.ToMessageChain(outer), Is.EqualTo("Unknown error"));
        }
    }
}
