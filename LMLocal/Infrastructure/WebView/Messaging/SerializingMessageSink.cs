using System;
using System.Threading;
using System.Threading.Tasks;

namespace LMLocal.Infrastructure.WebView.Messaging
{
    /// <summary>
    /// Serializes every call to a downstream WebView2 message sink.
    /// </summary>
    internal sealed class SerializingMessageSink
    {
        private readonly Func<WebView2ScriptMessage, Task> _inner;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public SerializingMessageSink(Func<WebView2ScriptMessage, Task> inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public async Task SendAsync(WebView2ScriptMessage message)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _inner(message).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
