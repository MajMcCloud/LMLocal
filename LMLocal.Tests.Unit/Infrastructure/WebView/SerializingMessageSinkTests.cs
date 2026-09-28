using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LMLocal.Infrastructure.WebView;
using LMLocal.Infrastructure.WebView.Messaging;
using NUnit.Framework;

namespace LMLocal.Tests.Unit.Infrastructure.WebView
{
    [TestFixture]
    public class SerializingMessageSinkTests
    {
        private static void TrackMax(ref int max, int value)
        {
            int prev;
            do
            {
                prev = Volatile.Read(ref max);
                if (value <= prev)
                    return;
            }
            while (Interlocked.CompareExchange(ref max, value, prev) != prev);
        }

        [Test]
        public async Task SendAsync_ForwardsAllMessagesExactlyOnce()
        {
            var received = new List<string>();
            var sink = new SerializingMessageSink(m =>
            {
                lock (received)
                {
                    received.Add(m.Payload);
                }
                return Task.CompletedTask;
            });

            var tasks = Enumerable.Range(0, 20)
                .Select(i => sink.SendAsync(new WebView2ScriptMessage { Type = WebView2MessageType.StreamContent, Payload = i.ToString() }))
                .ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            Assert.That(received, Has.Count.EqualTo(20));
            Assert.That(received.Distinct().Count(), Is.EqualTo(20));
        }

        [Test]
        public async Task SendAsync_SerializesConcurrentSends()
        {
            int active = 0;
            int max = 0;

            var sink = new SerializingMessageSink(async m =>
            {
                var now = Interlocked.Increment(ref active);
                TrackMax(ref max, now);
                await Task.Delay(30).ConfigureAwait(false);
                Interlocked.Decrement(ref active);
            });

            var tasks = Enumerable.Range(0, 10)
                .Select(i => sink.SendAsync(new WebView2ScriptMessage { Type = WebView2MessageType.StreamContent, Payload = i.ToString() }))
                .ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            Assert.That(max, Is.EqualTo(1), "Only one send may be in flight at a time.");
        }

        [Test]
        public void SendAsync_PropagatesInnerException()
        {
            var sink = new SerializingMessageSink(m => throw new InvalidOperationException("inner"));

            Assert.ThrowsAsync<InvalidOperationException>(() =>
                sink.SendAsync(new WebView2ScriptMessage { Type = WebView2MessageType.StreamContent }));
        }

        [Test]
        public void Constructor_NullInner_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new SerializingMessageSink(null));
        }
    }
}
