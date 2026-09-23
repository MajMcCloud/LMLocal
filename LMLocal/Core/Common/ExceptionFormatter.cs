using System;
using System.Text;

namespace LMLocal.Core.Common
{
    /// <summary>
    /// Extracts meaningful diagnostic messages from exception hierarchies.
    /// </summary>
    internal static class ExceptionFormatter
    {
        /// <summary>Fallback returned when nothing meaningful can be extracted.</summary>
        internal const string DefaultFallback = "Unknown error";

        /// <summary>Message used for timeouts / cancellations.</summary>
        internal const string TimeoutMessage = "Request timed out";

        /// <summary>
        /// Known framework placeholder messages that carry no diagnostic value and should be bypassed in favour of the inner cause.
        /// </summary>
        private static readonly string[] GenericWrappers =
        {
            "An error occurred while sending the request",
            "One or more errors occurred"
        };

        /// <summary>
        /// Produces a compact, user-presentable message that surfaces the root cause of the given exception instead of a generic framework wrapper.
        /// </summary>
        public static string Format(Exception ex)
        {
            if (ex == null)
                return DefaultFallback;

            if (ex is OperationCanceledException)
                return TimeoutMessage;

            var root = GetRootCause(ex);
            var rootMessage = root?.Message?.Trim();

            if (!string.IsNullOrWhiteSpace(rootMessage) && !IsGenericWrapper(rootMessage))
                return rootMessage;

            return ToMessageChain(ex, maxDepth: 3);
        }

        /// <summary>
        /// Walks the exception hierarchy and returns the deepest inner exception, unrolling <see cref="AggregateException"/> along the way.
        /// </summary>
        public static Exception GetRootCause(Exception ex)
        {
            if (ex == null)
                return null;

            var current = ex;
            while (true)
            {
                if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
                {
                    current = aggregate.InnerExceptions[0];
                    continue;
                }

                if (current.InnerException == null)
                    return current;

                current = current.InnerException;
            }
        }

        /// <summary>
        /// Formats an exception chain as a compact arrow-separated string, skipping duplicate and empty messages and ignoring a leading generic wrapper.
        /// </summary>
        public static string ToMessageChain(Exception ex, int maxDepth = 3)
        {
            if (ex == null)
                return null;

            var builder = new StringBuilder();
            string previousMessage = null;
            int depth = 0;

            for (var current = ex; current != null && depth < maxDepth; depth++)
            {
                if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
                    current = aggregate.InnerExceptions[0];

                var message = current.Message?.Trim();
                bool isLeadingGenericWrapper =
                    depth == 0 && IsGenericWrapper(message) && current.InnerException != null;

                if (!string.IsNullOrWhiteSpace(message) &&
                    !string.Equals(message, previousMessage, StringComparison.OrdinalIgnoreCase) &&
                    !isLeadingGenericWrapper)
                {
                    if (builder.Length > 0)
                        builder.Append(" \u2192 ");

                    builder.Append(message);
                    previousMessage = message;
                }

                current = current.InnerException;
            }

            return builder.Length > 0 ? builder.ToString() : DefaultFallback;
        }

        private static bool IsGenericWrapper(string message)
        {
            if (string.IsNullOrEmpty(message))
                return false;

            for (int i = 0; i < GenericWrappers.Length; i++)
            {
                if (message.IndexOf(GenericWrappers[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }
    }
}
