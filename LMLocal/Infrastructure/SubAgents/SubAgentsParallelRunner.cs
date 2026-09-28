using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LMLocal.Application.SubAgents;
using LMLocal.Application.Tool;
using LMLocal.Core.Common;
using LMLocal.Core.Models;

namespace LMLocal.Infrastructure.SubAgents
{
    /// <summary>
    /// Runs a SubAgent fan-out group bounded by <c>maxParallel</c>: at most that many calls run at once,
    /// the rest wait for a free slot (e.g. five calls with <c>maxParallel = 2</c> run 2 → 2 → 1).
    /// Outcomes are returned in the original call order.
    /// </summary>
    internal sealed class SubAgentsParallelRunner : ISubAgentsParallelRunner
    {
        public async Task<SubAgentsParallelRunResult> RunAsync(
            IReadOnlyList<ToolCallRecord> calls,
            SubAgentsParallelWork work,
            int maxParallel,
            CancellationToken cancellationToken)
        {
            if (calls == null)
                throw new ArgumentNullException(nameof(calls));
            if (work == null)
                throw new ArgumentNullException(nameof(work));
            if (maxParallel < 1)
                throw new ArgumentOutOfRangeException(nameof(maxParallel), "maxParallel must be >= 1.");

            if (calls.Count == 0)
                return new SubAgentsParallelRunResult(new List<ToolExecutionResult>());

            var results = new ToolExecutionResult[calls.Count];
            var tasks = new Task[calls.Count];

            // One shared gate bounds the whole group; it lives for the duration of the fan-out.
            using (var gate = new SemaphoreSlim(maxParallel, maxParallel))
            {
                for (int i = 0; i < calls.Count; i++)
                {
                    tasks[i] = RunOneAsync(calls[i], work, gate, cancellationToken, results, i);
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            return new SubAgentsParallelRunResult(results);
        }

        private static async Task RunOneAsync(
            ToolCallRecord call,
            SubAgentsParallelWork work,
            SemaphoreSlim gate,
            CancellationToken cancellationToken,
            ToolExecutionResult[] results,
            int index)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results[index] = await work(call, cancellationToken).ConfigureAwait(false)
                    ?? new ToolExecutionResult
                    {
                        ToolId = call?.CallId,
                        ToolName = call?.FunctionName,
                        Error = "SubAgent run produced no result."
                    };
            }
            catch (OperationCanceledException)
            {
                // Group cancellation: let it surface so the session can move to the cancelled state.
                throw;
            }
            catch (Exception ex)
            {
                // A single failing call must not fault the whole group.
                InternalLogger.Error($"SubAgentsParallelRunner: call '{call?.FunctionName}' failed", ex);
                results[index] = new ToolExecutionResult
                {
                    ToolId = call?.CallId,
                    ToolName = call?.FunctionName,
                    Error = ex.Message
                };
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
