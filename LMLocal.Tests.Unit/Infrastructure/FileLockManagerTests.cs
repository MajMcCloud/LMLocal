using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LMLocal.Infrastructure.Persistence;
using NUnit.Framework;

namespace LMLocal.Tests.Unit.Infrastructure
{
    [TestFixture]
    public class FileLockManagerTests
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
        public async Task WaitAsync_SamePath_Serializes()
        {
            var manager = new FileLockManager();
            int active = 0;
            int max = 0;

            var tasks = Enumerable.Range(0, 20).Select(async _ =>
            {
                await manager.WaitAsync("C:/tmp/same.jsonl").ConfigureAwait(false);
                try
                {
                    var now = Interlocked.Increment(ref active);
                    TrackMax(ref max, now);
                    await Task.Delay(10).ConfigureAwait(false);
                    Interlocked.Decrement(ref active);
                }
                finally
                {
                    manager.Release("C:/tmp/same.jsonl");
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            Assert.That(max, Is.EqualTo(1), "Only one writer may hold the same path at a time.");
        }

        [Test]
        public async Task WaitAsync_DifferentPaths_DoNotSerialize()
        {
            var manager = new FileLockManager();
            int active = 0;
            int max = 0;

            var tasks = new[] { "C:/tmp/a.jsonl", "C:/tmp/b.jsonl", "C:/tmp/c.jsonl" }.Select(async path =>
            {
                await manager.WaitAsync(path).ConfigureAwait(false);
                try
                {
                    var now = Interlocked.Increment(ref active);
                    TrackMax(ref max, now);
                    await Task.Delay(80).ConfigureAwait(false);
                    Interlocked.Decrement(ref active);
                }
                finally
                {
                    manager.Release(path);
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            Assert.That(max, Is.GreaterThanOrEqualTo(2), "Independent paths must not block each other.");
        }

        [Test]
        public async Task WaitAsync_SamePathDifferentCasingOrSeparators_UsesSameGate()
        {
            var manager = new FileLockManager();

            await manager.WaitAsync(@"C:\Temp\a.jsonl").ConfigureAwait(false);
            try
            {
                bool blocked = false;
                try
                {
                    using (var cts = new CancellationTokenSource(50))
                    {
                        await manager.WaitAsync("c:/temp/A.JSONL", cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    blocked = true;
                }

                Assert.That(blocked, Is.True, "The same path in different casing/separators must map to the same gate.");
            }
            finally
            {
                manager.Release(@"C:\Temp\a.jsonl");
            }
        }

        [Test]
        public async Task WaitAsync_CancelledWhileWaiting_Throws_AndDoesNotConsumeTheGate()
        {
            var manager = new FileLockManager();

            await manager.WaitAsync("C:/tmp/cancel.jsonl").ConfigureAwait(false);
            try
            {
                bool cancelled = false;
                try
                {
                    using (var cts = new CancellationTokenSource(50))
                    {
                        await manager.WaitAsync("C:/tmp/cancel.jsonl", cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                Assert.That(cancelled, Is.True);
            }
            finally
            {
                manager.Release("C:/tmp/cancel.jsonl");
            }

            // The cancelled wait must not have consumed a permit: the gate is still usable.
            await manager.WaitAsync("C:/tmp/cancel.jsonl").ConfigureAwait(false);
            manager.Release("C:/tmp/cancel.jsonl");
        }
    }
}
