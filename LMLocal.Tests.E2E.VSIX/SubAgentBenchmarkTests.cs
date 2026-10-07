using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LMLocal.Tests.E2E.VSIX
{
    /// <summary>
    /// Manual end-to-end benchmark for the built-in SubAgents: real model (LM Studio),
    /// real VS Experimental instance, real tools. One VS instance for the whole run.
    /// </summary>
    [TestClass, TestCategory("SubAgentBenchmark")]
    public class SubAgentBenchmarkTests
    {
        private const string PipeName = "LMLocal.Ipc";

        private const string Explorer = "code_explorer_subagent";
        private const string Reader = "code_reader_subagent";
        private const string Analyzer = "symbol_analyzer_subagent";

        public TestContext TestContext { get; set; }

        // ---------------------------------------------------------------------
        // Data model
        // ---------------------------------------------------------------------

        private sealed class Scenario
        {
            public string Id;
            public string AgentId;
            public string Prompt;
            public string[] MustContain = new string[0];

            // Alternative evidence groups (OR-sets). Each inner array is a group;
            public string[][] MustContainAny = new string[0][];

            // Ordered evidence: each marker must appear in the answer AND its first occurrence must come after the first occurrence of the previous marker.
            public string[] MustContainInOrder = new string[0];
            // Hard negative evidence: each string MUST NOT appear anywhere in the answer.
            public string[] MustNotContain = new string[0];
            // Exactly-once evidence: each marker must appear exactly once in the final answer.
            // 0 occurrences = missing; 2+ = the same content arrived twice (printed in the reply
            // text AND expanded from a ref marker by the runner, or via overlapping markers).
            public string[] MustAppearExactlyOnce = new string[0];
            public bool ExpectNotFound;
            public bool CheckRefusal = true;
            public TimeSpan ReadTimeout = TimeSpan.FromMinutes(5);
        }

        private sealed class Violation
        {
            public string Text;
            public bool Hard;
            public Violation(string text, bool hard)
            {
                Text = text;
                Hard = hard;
            }
        }

        private sealed class BenchmarkResult
        {
            public string Id;
            public string AgentId;
            public string Prompt;
            public string Verdict;
            public string Content;
            public List<string> ToolsUsed = new List<string>();
            public int Rounds;
            public double DurationMs;
            public List<string> Violations = new List<string>();
        }

        // Mirror of SubAgentsRunResponse serialized over the pipe.
        private sealed class RunResponseDto
        {
            public bool Success { get; set; }
            public string Content { get; set; }
            public string Error { get; set; }
            public string Model { get; set; }
            public string RunId { get; set; }
            public long DurationMs { get; set; }
            public int? PromptTokens { get; set; }
            public int? CompletionTokens { get; set; }
            public int? TotalTokens { get; set; }
            public double TokensPerSecond { get; set; }
            public int Rounds { get; set; }
            public List<string> ToolsUsed { get; set; }
            public int ExpandedRefCount { get; set; }
            public int UnresolvedRefCount { get; set; }
        }

        // ---------------------------------------------------------------------
        // Scenario builders
        // ---------------------------------------------------------------------

        private static Scenario Sc(string id, string agentId, string prompt, params string[] mustContain)
        {
            return new Scenario
            {
                Id = id,
                AgentId = agentId,
                Prompt = prompt,
                MustContain = mustContain ?? new string[0]
            };
        }

        private static Scenario NotFoundSc(string id, string agentId, string prompt, params string[] mustContain)
        {
            var s = Sc(id, agentId, prompt, mustContain);
            s.ExpectNotFound = true;
            return s;
        }


        /// <summary>
        /// Verbatim-file reads can be very long, so we disable the refusal check for them. The agent is expected to return the file content verbatim, not a refusal.
        /// </summary>
        private static Scenario ScVerbatim(string id, string agentId, string prompt, params string[] mustContain)
        {
            var s = Sc(id, agentId, prompt, mustContain);
            s.CheckRefusal = false;
            return s;
        }


        // ---------------------------------------------------------------------
        // Scenario list (prompts and expectations are in English)
        // ---------------------------------------------------------------------

        private static List<Scenario> BuildScenarios()
        {
            var list = new List<Scenario>();
            var guid = Guid.NewGuid().ToString("N").Substring(0, 12);
            //----A.1 code_explorer_subagent: discovery(10)------------------ -
            list.Add(Sc("A1.1", Explorer,
                "Find where the class ToolCallRecord is declared. Report the file path and line number.",
                "StreamCompletionResult.cs", "114"));

            list.Add(Sc("A1.2", Explorer,
                "Find all occurrences of GetAsync in the solution. Report how many were found and list the first few files.",
                "GetAsync"));

            list.Add(Sc("A1.3", Explorer,
                "Find the file that defines the class ChatLogSerializer.",
                "ChatLogSerializer.cs", "Persistence"));

            list.Add(NotFoundSc("A1.4", Explorer,
                "Check whether any .csproj in the solution references the NuGet package Newtonsoft.Json. Distinguish a PackageReference directed at that package from a using Newtonsoft.Json directive in source files.",
                "PackageReference"));

            list.Add(Sc("A1.5", Explorer,
                "Find the file named StreamProcessor.cs and report its exact path.",
                "StreamProcessor.cs"));

            list.Add(Sc("A1.6", Explorer,
                "Find all occurrences of ConsolidateLastExchangeAsync in the production project LMLocal only (exclude test projects).",
                "ConsolidateLastExchangeAsync"));

            list.Add(NotFoundSc("A1.7", Explorer,
                $"Search for the literal text bench-{guid}-{guid}."));

            list.Add(Sc("A1.8", Explorer,
                "Search for 'using Newtonsoft' and return the complete result list, handling pagination. State the total count and whether the list is truncated.",
                "Newtonsoft"));

            list.Add(NotFoundSc("A1.9", Explorer,
                "Describe the project structure of a project named LMLocal.Web.",
                "LMLocal.Web"));

            list.Add(Sc("A1.10", Explorer,
                "Search for the exact identifier \"ToolCallRecord\" and report all matching file paths and line numbers from all pages.",
                "StreamCompletionResult.cs", "114"));

            // ---- A.2 code_reader_subagent: deterministic reading (9) ----------
            list.Add(Sc("A2.1", Reader,
                "Read the entire file LMLocal/Infrastructure/Persistence/ChatLogSerializer.cs and return it verbatim.",
                "MaxPromptLength"));

            list.Add(ScVerbatim("A2.2", Reader,
                "Read lines 1-12 of LMLocal\\GettingStarted.txt and return them verbatim.",
                "LM Local", "Visual Studio"));

            list.Add(Sc("A2.3", Reader,
                "Find the file named exactly StreamProcessor.cs and read it (there is also a StreamProcessorFactory.cs in the same folder - do not pick the factory).",
                "ToolCallRecord", "LlmSseParser"));

            list.Add(ScVerbatim("A2.4", Reader,
                "Read these three files and return them verbatim: LMLocal\\Infrastructure\\Persistence\\ChatLogSerializer.cs, LMLocal\\Infrastructure\\Persistence\\ChatPersistenceService.cs, LMLocal\\Core\\Models\\StreamChunk.cs.",
                "MaxPromptLength", "SaveLastMessageAsync", "ChunkKind"));

            list.Add(ScVerbatim("A2.5", Reader,
                "Read these 4 files and return them verbatim: ChatLogSerializer.cs, StreamCompletionResult.cs, StreamProcessor.cs, ChatHistoryManager.cs.",
                "ChatLogSerializer", "ToolCallRecord", "ProcessStreamAsync", "ConsolidateLastExchangeAsync"));

            list.Add(NotFoundSc("A2.6", Reader,
                "Read LMLocal/Infrastructure/Persistence/NoSuchSerializer.cs.",
                "NoSuchSerializer.cs"));

            list.Add(Sc("A2.7", Reader,
                "Read LMLocal/Infrastructure/Persistence/ChatLogSerializer.cs. If the entire file is not returned, clearly state that it is partial and identify the missing portion.",
                "MaxPromptLength"));

            var a208 = ScVerbatim("A2.8", Reader,
                "Read the file at exact path LMLocal.Tests.E2E.VSIX\\Fixtures\\reader-truth-fixture.md and return it verbatim.",
                "## 1. First Section", "## Appendix A. References", "## 2. Second Section", "Tail-Anchor-Open");
            a208.MustAppearExactlyOnce = new[] { "Seed-One-Anchor", "Ref-Anchor-Alpha", "Tail-Anchor-Open" };
            list.Add(a208);

            var a209 = Sc("A2.9", Reader,
                "Read the file at exact path LMLocal.Tests.E2E.VSIX\\Fixtures\\reader-truth-fixture.md. Verify whether these headings appear in this exact order: \"## 1. First Section\", \"## 2. Second Section\", \"## 3. Third Section\", \"## Appendix A. References\". Use the actual file contents and line positions. Do not infer or assume any heading or position that is not present in the retrieved content.",
                "## Appendix A. References");
            a209.MustContainInOrder = new[] { "## 1. First Section", "## Appendix A. References", "## 2. Second Section", "## 3. Third Section" };
            list.Add(a209);

            list.Add(Sc("A2.10", Reader,
                "Read the entire file LMLocal.Tests.E2E.VSIX\\Fixtures\\reader-large-fixture.md (400 lines) and return it verbatim. Continue reading until the entire file has been retrieved; do not stop while has_more_results is true.",
                "Reader-Start", "Reader-End"));

            var a211 = ScVerbatim("A2.11", Reader,
                "In LMLocal\\Infrastructure\\Security\\TestConnectionErrorClassifier.cs return VERBATIM only the single line that contains ExceptionFormatter.Format. Return no other line of the file and do not mark the file as missing.",
                "return ExceptionFormatter.Format(ex);");
            a211.MustNotContain = new[] { "Missing files" };
            list.Add(a211);

            var a212 = ScVerbatim("A2.12", Reader,
                "In LMLocal\\Application\\ModelsList\\ModelsListService.cs return VERBATIM only the two lines that contain ExceptionFormatter.Format. Return no other line of the file and do not mark the file as missing.",
                "var errorResponse = new UnifiedListModelsResponse { Error = ExceptionFormatter.Format(ex) };",
                "return new UnifiedListModelsResponse { Error = ExceptionFormatter.Format(ex) };");
            a212.MustNotContain = new[] { "Missing files" };
            list.Add(a212);

            var a213 = ScVerbatim("A2.13", Reader,
                "Read the file at exact path LMLocal.Tests.E2E.VSIX\\Fixtures\\reader-ranges-fixture.md and return ONLY lines 11-15 and lines 30-39 VERBATIM, with each line carrying its actual 1-based line number. Do not include any other line of the file, do not substitute different ranges, and do not mark the file as missing.",
                "RANGE-ANCHOR-A1-OPEN", "RANGE-ANCHOR-A1-CLOSE", "RANGE-ANCHOR-A3-CLOSE", "RANGE-ANCHOR-APP-OPEN");
            a213.MustContainInOrder = new[] { "RANGE-ANCHOR-A1-OPEN", "RANGE-ANCHOR-A1-CLOSE", "RANGE-ANCHOR-A3-CLOSE", "RANGE-ANCHOR-APP-OPEN" };
            a213.MustNotContain = new[] { "RANGE-ANCHOR-A2-OPEN", "RANGE-ANCHOR-A2-CLOSE", "RANGE-ANCHOR-A3-OPEN", "RANGE-ANCHOR-APP-CLOSE", "RANGE-ANCHOR-TAIL-OPEN", "RANGE-ANCHOR-A1-OPEN-COPY", "RANGE-ANCHOR-A1-CLOSE-COPY", "# End of fixture" };
            list.Add(a213);

            var a214 = ScVerbatim("A2.14", Reader,
                "Read the file at exact path LMLocal.Tests.E2E.VSIX\\Fixtures\\reader-symbols-fixture.md and return it verbatim, with each line carrying its 1-based line number. Preserve every character exactly — multiple spaces, tabs, single and double quotes, and every bracket. Do not normalize, drop, or rebalance any of them.",
                "SYM-SPACE-MID: two  spaces  between  words",
                "SYM-QUOTE-NESTED-SINGLE: 'outer \"inner\" value'",
                "SYM-QUOTE-AT-END: trailing quote\"",
                "SYM-PAREN-OPEN: function(",
                "SYM-PAREN-BALANCED: ((a, b), [c, d], {e: f})",
                "SYM-BRACKET-MIXED: {[(\"a\", {\"b\": [1,2]})]}",
                "SYM-EMPTY-AFTER: this line follows the empty line above",
                "SYM-END-TAIL: last line anchor");

            a214.MustNotContain = new[]
            {
                "SYM-EMPTY-UNWANTED",
                "//SECTION-SKIP",
                "///"
            };
            list.Add(a214);

            //----A.3 symbol_analyzer_subagent: symbols & references(9)------ -
            list.Add(Sc("A3.1", Analyzer,
                "Find the C# symbol ToolCallRecord and report its declaration file and line.",
                "StreamCompletionResult.cs", "114"));

            list.Add(Sc("A3.2", Analyzer,
                "Find all references to ChatLogSerializer. Report file:line coordinates.",
                "ChatLogSerializer", "ChatPersistenceService.cs"));

            list.Add(Sc("A3.3", Analyzer,
                "Inspect the type StreamCompletionResult and list its public members with file:line locations.",
                "ContentResponse", "ToolCalls"));

            list.Add(Sc("A3.4", Analyzer,
                "Find the JavaScript symbol lmInit and report its file and line. Use the semantic JavaScript tool.",
                "app.js"));

            list.Add(NotFoundSc("A3.5", Analyzer,
                "Find the symbol GeminiThoughtSignaturePolicyEnforcer.",
                "GeminiThoughtSignaturePolicyEnforcer"));

            list.Add(Sc("A3.6", Analyzer,
                "Find the symbol ToolCallRecord, then read its declaration context.",
                "ToolCallRecord", "StreamCompletionResult.cs"));

            var a37 = Sc("A3.7", Analyzer,
                "Find all references to the ToolCalls property of the type ChatMessage (declared in LMLocal/Core/Models/ChatMessage.cs). Ignore the same-named ToolCalls property on other types such as Message and StreamCompletionResult. Retrieve ALL pages of references and state the total reference count.",
                "ToolCalls", "ChatMessage.cs");
            a37.MustContainAny = new[] { new[] { "ApiRequestBuilder.cs", "ApiRequestBuilder" } };
            list.Add(a37);

            var a38 = Sc("A3.8", Analyzer,
                "Inspect ConsolidateLastExchangeAsync and determine whether ToolCalls is copied by reference or reconstructed. Read the actual source before answering.",
                "ConsolidateLastExchangeAsync", "ToolCalls");
            a38.MustContainAny = new[] { new[] { "reference", "same instance", "reused", "not reconstructed" } };
            list.Add(a38);

            list.Add(Sc("A3.9", Analyzer,
                "Find the C# method AddAssistantMessage in LMLocal/Application/Chat/ChatHistoryManager.cs. It has an interface declaration and two concrete overloads. Select the concrete implementation overload that accepts IReadOnlyList<ToolCallRecord> and return its declaration line together with its full implementation body.",
                "AddAssistantMessage", "toolCallObjects", "normalizedArguments"));




            return list;
        }

        // ---------------------------------------------------------------------
        // Main run
        // ---------------------------------------------------------------------
        public static IEnumerable<object[]> GetIterations()
        {
            const int from = 1;
            const int to = 1;
            return Enumerable.Range(from, to - from + 1).Select(i => new object[] { i });
        }

        [TestMethod]
        [DynamicData(nameof(GetIterations))]
        //[Ignore("Disable to run benchmark")]
        public async Task Run_Full_Benchmark(int iteration)
        {
            // Requires a running LM Studio + an Experimental VS instance; never auto-run.
            if (iteration != 1)
                await Task.Delay(TimeSpan.FromSeconds(3));

            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(180)))
            {
                var vs = await VsLauncher.StartExperimentalInstanceAsync(cts.Token);
                try
                {
                    IpcClient client = await IpcClient.ConnectAsync(PipeName, TimeSpan.FromMinutes(2), cts.Token);
                    try
                    {
                        // 1) Readiness gate: solution must be open (subagents return "No solution is open" otherwise). Existing IPC command.
                        var solutionPath = GetSolutionPath();
                        var resp = await client.SendCommandAsync("OpenSolution|" + solutionPath, cts.Token);
                        Assert.AreEqual("OK", resp, "OpenSolution IPC command failed");

                        await WaitForSolutionLoadedAsync(client, cts.Token);

                        var root = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
                        var results = new List<BenchmarkResult>();

                        // 2) Run every scenario sequentially on the same VS instance.
                        foreach (var scenario in BuildScenarios())
                        {
                            if (cts.IsCancellationRequested) break;

                            BenchmarkResult result;
                            try
                            {
                                result = await RunScenarioAsync(client, scenario, root, cts.Token);
                            }
                            catch (TimeoutException tex)
                            {
                                // Read timeout: the client is broken and cannot be reused.
                                result = TimeoutResult(scenario, tex);
                                client.Dispose();
                                TestContext.WriteLine("[" + scenario.Id + "] read timeout, reconnecting...");
                                client = await IpcClient.ConnectAsync(PipeName, TimeSpan.FromMinutes(2), cts.Token);
                            }

                            results.Add(result);
                            TestContext.WriteLine("[" + result.Id + "] " + result.Verdict
                                + " (" + Math.Round(result.DurationMs / 1000.0, 1) + "s) "
                                + string.Join("; ", result.Violations));

                            // Let LM Studio cool down between runs.
                            await Task.Delay(1000, cts.Token);
                        }

                        SaveReport(results, root);

                        var failed = results.Where(r => r.Verdict == "FAIL").Select(r => r.Id).ToList();
                        if (failed.Count > 0)
                        {
                            Assert.Fail("Critical FAILs: " + string.Join(", ", failed)
                                + ". Full report saved next to the .sln.");
                        }
                    }
                    finally
                    {
                        client.Dispose();
                    }
                }
                finally
                {
                    TryKill(vs);
                }
            }
        }

        // ---------------------------------------------------------------------
        // Per-scenario runner + evaluation
        // ---------------------------------------------------------------------

        private static async Task<BenchmarkResult> RunScenarioAsync(
            IpcClient client,
            Scenario scenario,
            string solutionRoot,
            CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            var escapedPrompt = Uri.EscapeDataString(scenario.Prompt);
            var command = "RunSubAgent|" + scenario.AgentId + "|" + escapedPrompt;

            var json = await client.SendCommandAsync(command, scenario.ReadTimeout, ct);
            sw.Stop();

            RunResponseDto response = null;
            string parseError = null;
            try
            {
                response = JsonConvert.DeserializeObject<RunResponseDto>(json);
            }
            catch (Exception ex)
            {
                parseError = ex.Message;
            }

            return Evaluate(scenario, response, parseError, sw.Elapsed, solutionRoot);
        }

        private static BenchmarkResult Evaluate(
            Scenario s,
            RunResponseDto r,
            string parseError,
            TimeSpan duration,
            string solutionRoot)
        {
            var result = new BenchmarkResult
            {
                Id = s.Id,
                AgentId = s.AgentId,
                Prompt = s.Prompt,
                DurationMs = duration.TotalMilliseconds
            };

            var violations = new List<Violation>();
            string content = r != null ? r.Content ?? string.Empty : string.Empty;

            if (parseError != null)
            {
                violations.Add(new Violation("parse_error: " + parseError, true));
            }

            if (r == null)
            {
                result.Verdict = "FAIL";
                result.Violations.Add("no_response");
                return result;
            }

            result.ToolsUsed = r.ToolsUsed ?? new List<string>();
            result.Rounds = r.Rounds;
            result.Content = Truncate(content);

            if (!r.Success)
            {
                violations.Add(new Violation("run_failed: " + (r.Error ?? "no error message"), true));
            }

            // Soft diagnostics (только если сценарий это разрешает).
            if (s.CheckRefusal
                && Regex.IsMatch(content, @"(?i)\b(i can't|cannot|sorry|i don't know|i am not able)\b"))
            {
                violations.Add(new Violation("refusal", false));
            }
            if (!r.Success && r.Rounds >= 10)
            {
                violations.Add(new Violation("max_rounds_reached", false));
            }

            // Expected "NOT FOUND" honesty marker.
            bool notFoundMarker = false;
            if (s.ExpectNotFound)
            {
                notFoundMarker = Regex.IsMatch(content, @"(?i)(\bnot_found\b|not found|does not exist|no (matches|results|references|instances|project|files?|explicit|such|direct|zero)|couldn't find|could not find|not exist|none of|i found no|found no|returned no|zero (matches|results)|did not find|no .* found)");
                if (!notFoundMarker)
                {
                    violations.Add(new Violation("expected_not_found_marker_missing", true));
                }
            }

            // Required evidence in the answer.
            foreach (var ev in s.MustContain ?? new string[0])
            {
                if (notFoundMarker)
                {
                    break;
                }
                if (content.IndexOf(ev, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    violations.Add(new Violation("missing_evidence: " + ev, true));
                }
            }

            // Alternative evidence groups (OR-groups). 
            if (!notFoundMarker)
            {
                foreach (var group in s.MustContainAny ?? new string[0][])
                {
                    if (group == null || group.Length == 0)
                    {
                        continue;
                    }

                    bool any = group.Any(alt => content.IndexOf(alt, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!any)
                    {
                        violations.Add(new Violation("missing_evidence(any of): " + string.Join(" | ", group), true));
                    }
                }
            }

            // Ordered evidence: all markers must be present and appear in the given relative order (checked by first occurrence). 
            if (!notFoundMarker && s.MustContainInOrder != null && s.MustContainInOrder.Length > 0)
            {
                int prevIdx = -1;
                for (int i = 0; i < s.MustContainInOrder.Length; i++)
                {
                    var marker = s.MustContainInOrder[i];
                    var idx = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0)
                    {
                        violations.Add(new Violation("missing_evidence(ordered): " + marker, true));
                        break;
                    }

                    if (i > 0 && idx <= prevIdx)
                    {
                        violations.Add(new Violation(
                            "wrong_order: '" + s.MustContainInOrder[i] + "' appears before/at '" + s.MustContainInOrder[i - 1] + "'", true));
                        break;
                    }

                    prevIdx = idx;
                }
            }

            // Hard negative evidence: each marker MUST NOT appear anywhere in the answer.
            if (!notFoundMarker && s.MustNotContain != null)
            {
                foreach (var banned in s.MustNotContain)
                {
                    if (content.IndexOf(banned, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        violations.Add(new Violation("forbidden_evidence: " + banned, true));
                    }
                }
            }

            // Exactly-once evidence: each marker must occur exactly once. 0 = missing,
            // 2+ = the same content arrived twice (printed in the answer text AND expanded
            // from a ref marker by the runner, or via overlapping/multiple ref markers).
            if (!notFoundMarker && s.MustAppearExactlyOnce != null)
            {
                foreach (var marker in s.MustAppearExactlyOnce)
                {
                    if (string.IsNullOrEmpty(marker))
                    {
                        continue;
                    }

                    var count = CountOccurrences(content, marker);
                    if (count == 0)
                    {
                        violations.Add(new Violation("missing_evidence(exactly_once): " + marker, true));
                    }
                    else if (count > 1)
                    {
                        violations.Add(new Violation("duplicated_evidence: '" + marker + "' appears " + count + " times", true));
                    }
                }
            }

            // Invented-path cross-check (only for non-mutating scenarios).
            if (notFoundMarker)
            {
                // Honest NOT_FOUND — no invented-path check needed: the non-existing path is exactly what we expected to see.
            }
            else
            {
                foreach (var p in ExtractPaths(content))
                {
                    // Skip paths that came from the prompt itself (e.g. NOT FOUND scenarios).
                    var inPrompt = s.Prompt.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (inPrompt)
                    {
                        continue;
                    }

                    // If a file with the same name exists anywhere in the solution, the
                    // agent likely quoted a truncated/relative path (e.g.
                    // "ChatSessionStream\StreamProcessor.cs" for
                    // "LMLocal\Application\ChatSessionStream\StreamProcessor.cs") — do not
                    // flag it as invented. ResolveFullPath already handles absolute paths.
                    var fileName = Path.GetFileName(p.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(fileName) && FileExistsByName(solutionRoot, fileName))
                    {
                        continue;
                    }

                    // Regex false-positive guard: "Foo.cs" extracted from inside
                    // "Foo.csproj" (no real Foo.cs exists, but Foo.csproj does).
                    if (p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        && HasRealFileWithDifferentExtension(solutionRoot, p, ".csproj"))
                    {
                        continue;
                    }

                    var full = ResolveFullPath(solutionRoot, p);
                    if (full != null && !File.Exists(full) && !Directory.Exists(full))
                    {
                        violations.Add(new Violation("invented_path: " + p, true));
                    }
                }
            }

            result.Verdict = violations.Count == 0 ? "PASS"
                : violations.Any(v => v.Hard) ? "FAIL"
                : "PARTIAL";
            result.Violations.AddRange(violations.Select(v => v.Text));

            return result;
        }

        private static BenchmarkResult TimeoutResult(Scenario s, TimeoutException tex)
        {
            var r = new BenchmarkResult
            {
                Id = s.Id,
                AgentId = s.AgentId,
                Prompt = s.Prompt,
                Verdict = "FAIL"
            };
            r.Violations.Add("client_read_timeout: " + tex.Message);
            return r;
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static async Task WaitForSolutionLoadedAsync(IpcClient client, CancellationToken ct)
        {
            for (int i = 0; i < 90; i++)
            {
                var resp = await client.SendCommandAsync("RunTool|GetSolutionOverview", TimeSpan.FromSeconds(20), ct);
                if (IsSolutionLoaded(resp))
                {
                    return;
                }
                await Task.Delay(2000, ct);
            }
            throw new TimeoutException("Solution did not load within 180s.");
        }

        private static bool IsSolutionLoaded(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || !json.StartsWith("{"))
            {
                return false;
            }

            try
            {
                var obj = JObject.Parse(json);
                return obj["success"]?.Value<bool>() == true
                    && !string.IsNullOrWhiteSpace((string)obj["solution_name"]);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string GetSolutionPath()
        {
            var solutionPath = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "LMLocal.sln"));
            if (!File.Exists(solutionPath))
            {
                throw new InvalidOperationException("Solution not found at '" + solutionPath + "'");
            }
            return solutionPath;
        }

        private static string ResolveFullPath(string solutionRoot, string candidate)
        {
            var trimmed = candidate.Trim().TrimStart('.', '\\', '/');
            if (trimmed.Length == 0) return null;

            // Absolute Windows path (e.g. C:\...).
            if (trimmed.Length >= 2 && trimmed[1] == ':')
            {
                return Path.GetFullPath(trimmed);
            }

            if (string.IsNullOrEmpty(solutionRoot)) return null;

            var normalized = trimmed.Replace('/', '\\');
            try
            {
                return Path.GetFullPath(Path.Combine(solutionRoot, normalized));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Returns true if a file with the given name exists anywhere under solutionRoot.
        /// </summary>
        private static bool FileExistsByName(string solutionRoot, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(solutionRoot))
                return false;
            try
            {
                return Directory.EnumerateFiles(solutionRoot, fileName, SearchOption.AllDirectories).Any();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns true if a file with the same directory + base name exists in the
        /// solution but with a different extension (e.g. "LMLocal\\LMLocal.cs" -> the real "LMLocal\\LMLocal.csproj"). 
        /// </summary>
        private static bool HasRealFileWithDifferentExtension(string solutionRoot, string path, string realExtension)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(solutionRoot))
                return false;

            var dir = Path.GetDirectoryName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(stem)) return false;

            var candidate = Path.GetFileName(path) + realExtension; // "LMLocal.cs" + ".csproj"
            if (string.IsNullOrWhiteSpace(dir))
            {
                return File.Exists(Path.Combine(solutionRoot, candidate));
            }

            var resolvedDir = ResolveFullPath(solutionRoot, dir);
            return resolvedDir != null && File.Exists(Path.Combine(resolvedDir, candidate));
        }

        // Only paths with a directory separator are cross-checked.
        private static List<string> ExtractPaths(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return new List<string>();

            // Negative lookahead (?![.\w]) ensures ".cs" is NOT followed by another
            // word character or dot — this prevents matching ".cs" inside ".csproj"
            // (which would produce a .cs path that does not exist on disk).
            // '.' in [\w\\/.]+ matches project names like LMLocal.Tests.Unit.
            var patterns = new[]
            {
                @"[\w\\/.]+\.cs(?![.\w])",
                @"[\w\\/.]+\.csproj(?![.\w])",
                @"[\w\\/.]+\.json(?![.\w])"
            };

            var deny = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Newtonsoft.Json"
            };

            return patterns
                .SelectMany(p => Regex.Matches(content, p, RegexOptions.None).Cast<Match>())
                .Select(m => m.Value)
                .Where(v =>
                    !deny.Contains(v) &&
                    (v.IndexOf('\\') >= 0 || v.IndexOf('/') >= 0))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string Truncate(string content)
        {
            const int max = 6000;
            if (content == null) return null;
            return content.Length <= max ? content : content.Substring(0, max) + "\n...[truncated]";
        }

        /// <summary>
        /// Counts non-overlapping occurrences of <paramref name="value"/> in <paramref name="text"/>
        /// using ordinal, case-insensitive matching.
        /// </summary>
        private static int CountOccurrences(string text, string value)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(text)) return 0;

            int count = 0;
            int idx = 0;
            while ((idx = text.IndexOf(value, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                idx += value.Length;
            }
            return count;
        }

        private static void SaveReport(List<BenchmarkResult> results, string solutionRoot)
        {
            var fileName = "benchmark-report-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".json";
            var path = Path.Combine(solutionRoot ?? ".", fileName);

            var summary = new JObject
            {
                ["total"] = results.Count,
                ["pass"] = results.Count(r => r.Verdict == "PASS"),
                ["partial"] = results.Count(r => r.Verdict == "PARTIAL"),
                ["fail"] = results.Count(r => r.Verdict == "FAIL")
            };

            var items = new JArray();
            foreach (var r in results)
            {
                items.Add(new JObject
                {
                    ["id"] = r.Id,
                    ["agent"] = r.AgentId,
                    ["prompt"] = r.Prompt,
                    ["verdict"] = r.Verdict,
                    ["content"] = r.Content,
                    ["toolsUsed"] = new JArray(r.ToolsUsed ?? new List<string>()),
                    ["rounds"] = r.Rounds,
                    ["durationMs"] = r.DurationMs,
                    ["violations"] = new JArray(r.Violations ?? new List<string>())
                });
            }

            var report = new JObject
            {
                ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                ["summary"] = summary,
                ["results"] = items
            };

            File.WriteAllText(path, report.ToString(Formatting.Indented));
            Console.WriteLine("Report saved to: " + path);
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                }
                process.Dispose();
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
