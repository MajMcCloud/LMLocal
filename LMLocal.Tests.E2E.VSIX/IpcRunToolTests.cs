using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace LMLocal.Tests.E2E.VSIX
{
    /// <summary>
    /// End-to-end coverage for the remaining built-in tools reachable through the
    /// 'RunTool|&lt;tool_name&gt;|&lt;arg1&gt;|&lt;arg2&gt;' IPC pass-through.
    /// Build/run tests (build_solution, run_tests) are intentionally excluded —
    /// they need a full build/runner and cannot be driven reliably from the harness.
    /// </summary>
    [TestClass]
    public class IpcRunToolTests
    {
        private const string PipeName = "LMLocal.Ipc";

        /// <summary>Scratch dir inside the solution so write tools pass the inside-solution guard.</summary>
        private static string ScratchDir =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "LMLocal.Tests.E2E.VSIX", "Scratch");

        private static string ScratchPath(string name) => Path.Combine(ScratchDir, name);

        private void EnsureScratchExists() => Directory.CreateDirectory(ScratchDir);

        private static void CleanupFiles(params string[] paths)
        {
            foreach (var p in paths)
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
        }

        private static string UriEnc(string value) => Uri.EscapeDataString(value ?? string.Empty);

        private async Task<string> SendAsync(IpcClient client, string command, CancellationToken ct)
        {
            return await client.SendCommandAsync(command, TimeSpan.FromMinutes(2), ct);
        }

        [TestMethod]
        public async Task RunTool_CreateFile_ThenDelete_ReturnsJsonAsync()
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                EnsureScratchExists();
                var path = ScratchPath("e2e_probe.txt");
                CleanupFiles(path);
                var vs = await VsLauncher.StartExperimentalInstanceAsync(cts.Token);
                try
                {
                    var solutionPath = GetSolutionPath();
                    var client = await IpcClient.ConnectAsync(PipeName, TimeSpan.FromMinutes(1), cts.Token);
                    using (client)
                    {
                        Assert.AreEqual("OK", await SendAsync(client, $"OpenSolution|{solutionPath}", cts.Token));

                        // create_file
                        var rel = "LMLocal.Tests.E2E.VSIX\\Scratch\\e2e_probe.txt";
                        var content = "line1\nline2\nline3";
                        var resp = await SendAsync(client, $"RunTool|create_file|{UriEnc(rel)}|{UriEnc(content)}", cts.Token);
                        var obj = JObject.Parse(resp);
                        Assert.IsTrue((bool)obj["success"], "create_file should succeed: " + resp);
                        Assert.IsTrue(File.Exists(path), "created file should exist on disk");

                        // read back: the tool returns every line prefixed with its 1-based number.
                        resp = await SendAsync(client, $"RunTool|read_file_lines|{UriEnc(rel)}|1|10", cts.Token);
                        obj = JObject.Parse(resp);
                        var lines = obj["content"] as JArray;
                        Assert.IsNotNull(lines, "'content' should be an array");
                        Assert.IsGreaterThanOrEqualTo(3, lines.Count, "expected at least the 3 written lines, got: " + lines.Count);

                        var joined = string.Join("\n", lines.Select(l => l?.ToString()));
                        Assert.Contains("1: line1", joined, "line 1 should be present with its prefix. Got: " + joined);
                        Assert.Contains("2: line2", joined, "line 2 should be present with its prefix. Got: " + joined);
                        Assert.Contains("3: line3", joined, "line 3 should be present with its prefix. Got: " + joined);

                        // delete_file
                        resp = await SendAsync(client, $"RunTool|delete_file|{UriEnc(rel)}", cts.Token);
                        obj = JObject.Parse(resp);
                        Assert.IsTrue((bool)obj["success"], "delete_file should succeed: " + resp);
                        Assert.IsFalse(File.Exists(path), "file should be removed from disk");
                    }
                }
                finally
                {
                    CleanupFiles(path);
                    TryKill(vs);
                }
            }
        }

        [TestMethod]
        public async Task RunTool_ReplaceFileContent_ThenDelete_ReturnsJsonAsync()
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                EnsureScratchExists();
                var path = ScratchPath("e2e_replace.txt");
                CleanupFiles(path);
                var vs = await VsLauncher.StartExperimentalInstanceAsync(cts.Token);
                try
                {
                    var solutionPath = GetSolutionPath();
                    var client = await IpcClient.ConnectAsync(PipeName, TimeSpan.FromMinutes(1), cts.Token);
                    using (client)
                    {
                        Assert.AreEqual("OK", await SendAsync(client, $"OpenSolution|{solutionPath}", cts.Token));
                        var rel = "LMLocal.Tests.E2E.VSIX\\Scratch\\e2e_replace.txt";

                        await SendAsync(client, $"RunTool|create_file|{UriEnc(rel)}|{UriEnc("OLD_A\nOLD_B\nOLD_C")}", cts.Token);

                        var resp = await SendAsync(client, $"RunTool|replace_file_content|{UriEnc(rel)}|{UriEnc("NEW_CONTENT")}", cts.Token);
                        var obj = JObject.Parse(resp);
                        Assert.IsTrue((bool)obj["success"], "replace_file_content should succeed: " + resp);
                        Assert.AreEqual("NEW_CONTENT", File.ReadAllText(path).Trim(), "content should be fully replaced");

                        await SendAsync(client, $"RunTool|delete_file|{UriEnc(rel)}", cts.Token);
                    }
                }
                finally
                {
                    CleanupFiles(path);
                    TryKill(vs);
                }
            }
        }

        [TestMethod]
        public async Task RunTool_InsertFileLines_ThenDelete_ReturnsJsonAsync()
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                EnsureScratchExists();
                var path = ScratchPath("e2e_insert.txt");
                CleanupFiles(path);
                var vs = await VsLauncher.StartExperimentalInstanceAsync(cts.Token);
                try
                {
                    var solutionPath = GetSolutionPath();
                    var client = await IpcClient.ConnectAsync(PipeName, TimeSpan.FromMinutes(1), cts.Token);
                    using (client)
                    {
                        Assert.AreEqual("OK", await SendAsync(client, $"OpenSolution|{solutionPath}", cts.Token));
                        var rel = "LMLocal.Tests.E2E.VSIX\\Scratch\\e2e_insert.txt";

                        await SendAsync(client, $"RunTool|create_file|{UriEnc(rel)}|{UriEnc("A\nB\nC")}", cts.Token);

                        // insert after line 1 (0-based semantics in tool: position=1 means after line 1)
                        var resp = await SendAsync(client, $"RunTool|insert_file_lines|{UriEnc(rel)}|1|{UriEnc("INSERTED")}", cts.Token);
                        var obj = JObject.Parse(resp);
                        Assert.IsTrue((bool)obj["success"], "insert_file_lines should succeed: " + resp);
                        Assert.IsGreaterThan(0, (int)obj["lines_inserted"], "lines_inserted should be positive");

                        var full = File.ReadAllText(path);
                        Assert.Contains("INSERTED", full, "inserted line should be present");

                        await SendAsync(client, $"RunTool|delete_file|{UriEnc(rel)}", cts.Token);
                    }
                }
                finally
                {
                    CleanupFiles(path);
                    TryKill(vs);
                }
            }
        }

        [TestMethod]
        public async Task RunTool_ReplaceFileLines_ThenDelete_ReturnsJsonAsync()
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                EnsureScratchExists();
                var path = ScratchPath("e2e_rpl_l.txt");
                CleanupFiles(path);
                var vs = await VsLauncher.StartExperimentalInstanceAsync(cts.Token);
                try
                {
                    var solutionPath = GetSolutionPath();
                    var client = await IpcClient.ConnectAsync(PipeName, TimeSpan.FromMinutes(1), cts.Token);
                    using (client)
                    {
                        Assert.AreEqual("OK", await SendAsync(client, $"OpenSolution|{solutionPath}", cts.Token));
                        var rel = "LMLocal.Tests.E2E.VSIX\\Scratch\\e2e_rpl_l.txt";

                        await SendAsync(client, $"RunTool|create_file|{UriEnc(rel)}|{UriEnc("aaa\nbbb\nccc")}", cts.Token);

                        // replace lines 2..2 ("bbb") with "REPLACED"
                        var resp = await SendAsync(client, $"RunTool|replace_file_lines|{UriEnc(rel)}|2|{UriEnc("bbb")}|{UriEnc("REPLACED")}", cts.Token);
                        var obj = JObject.Parse(resp);
                        Assert.IsTrue((bool)obj["success"], "replace_file_lines should succeed: " + resp);

                        var full = File.ReadAllText(path);
                        Assert.Contains("REPLACED", full, "replacement should be present");
                        Assert.DoesNotContain("bbb", full, "old line should be gone");

                        await SendAsync(client, $"RunTool|delete_file|{UriEnc(rel)}", cts.Token);
                    }
                }
                finally
                {
                    CleanupFiles(path);
                    TryKill(vs);
                }
            }
        }

        [TestMethod]
        public async Task RunTool_FormatDocument_FormatsCSharpAsync()
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                EnsureScratchExists();
                var path = ScratchPath("e2e_format.cs");
                CleanupFiles(path);
                var vs = await VsLauncher.StartExperimentalInstanceAsync(cts.Token);
                try
                {
                    var solutionPath = GetSolutionPath();
                    var client = await IpcClient.ConnectAsync(PipeName, TimeSpan.FromMinutes(1), cts.Token);
                    using (client)
                    {
                        Assert.AreEqual("OK", await SendAsync(client, $"OpenSolution|{solutionPath}", cts.Token));
                        var rel = "LMLocal.Tests.E2E.VSIX\\Scratch\\e2e_format.cs";

                        var code = "namespace Demo { public class C { public void M( ) { int x=1; } } }";
                        await SendAsync(client, $"RunTool|create_file|{UriEnc(rel)}|{UriEnc(code)}", cts.Token);

                        var resp = await SendAsync(client, $"RunTool|format_document|{UriEnc(rel)}|false", cts.Token);
                        var obj = JObject.Parse(resp);
                        Assert.IsTrue((bool)obj["success"], "format_document should succeed: " + resp);
                        Assert.IsTrue(File.Exists(path), "formatted file should still exist");

                        await SendAsync(client, $"RunTool|delete_file|{UriEnc(rel)}", cts.Token);
                    }
                }
                finally
                {
                    CleanupFiles(path);
                    TryKill(vs);
                }
            }
        }

        [TestMethod]
        public async Task RunTool_SearchSolutionKnowledge_ReturnsJsonAsync()
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                var vs = await VsLauncher.StartExperimentalInstanceAsync(cts.Token);
                try
                {
                    var solutionPath = GetSolutionPath();
                    var client = await IpcClient.ConnectAsync(PipeName, TimeSpan.FromMinutes(1), cts.Token);
                    using (client)
                    {
                        Assert.AreEqual("OK", await SendAsync(client, $"OpenSolution|{solutionPath}", cts.Token));

                        var resp = await SendAsync(client, $"RunTool|search_solution_knowledge|{UriEnc("subagents")}|5", cts.Token);
                        Assert.IsFalse(string.IsNullOrEmpty(resp), "Response should not be empty");
                        Assert.StartsWith("{", resp, $"Response should be JSON, but got: {resp}");

                        var obj = JObject.Parse(resp);
                        Assert.IsTrue(obj.ContainsKey("success"), "Response should contain 'success' key");
                        Assert.IsTrue(obj.ContainsKey("results"), "Response should contain 'results' key");
                        Assert.IsTrue(obj.ContainsKey("total_matches"), "Response should contain 'total_matches' key");
                    }
                }
                finally
                {
                    TryKill(vs);
                }
            }
        }

        [TestMethod]
        public async Task RunTool_SearchChatHistory_ReturnsJsonAsync()
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                var vs = await VsLauncher.StartExperimentalInstanceAsync(cts.Token);
                try
                {
                    var solutionPath = GetSolutionPath();
                    var client = await IpcClient.ConnectAsync(PipeName, TimeSpan.FromMinutes(1), cts.Token);
                    using (client)
                    {
                        Assert.AreEqual("OK", await SendAsync(client, $"OpenSolution|{solutionPath}", cts.Token));

                        var resp = await SendAsync(client, $"RunTool|search_chat_history|{UriEnc("tools")}|5", cts.Token);
                        Assert.IsFalse(string.IsNullOrEmpty(resp), "Response should not be empty");
                        Assert.StartsWith("{", resp, $"Response should be JSON, but got: {resp}");

                        var obj = JObject.Parse(resp);
                        Assert.IsTrue(obj.ContainsKey("success"), "Response should contain 'success' key");
                        Assert.IsTrue(obj.ContainsKey("results"), "Response should contain 'results' key");
                        Assert.IsTrue(obj.ContainsKey("total_matches"), "Response should contain 'total_matches' key");
                    }
                }
                finally
                {
                    TryKill(vs);
                }
            }
        }

        [TestMethod]
        public async Task RunTool_InspectType_ReturnsJsonAsync()
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                var vs = await VsLauncher.StartExperimentalInstanceAsync(cts.Token);
                try
                {
                    var solutionPath = GetSolutionPath();
                    var client = await IpcClient.ConnectAsync(PipeName, TimeSpan.FromMinutes(1), cts.Token);
                    using (client)
                    {
                        Assert.AreEqual("OK", await SendAsync(client, $"OpenSolution|{solutionPath}", cts.Token));

                        var resp = await SendAsync(client, $"RunTool|inspect_type|{UriEnc("System.IO.Path")}", cts.Token);
                        Assert.IsFalse(string.IsNullOrEmpty(resp), "Response should not be empty");
                        Assert.StartsWith("{", resp, $"Response should be JSON, but got: {resp}");

                        var obj = JObject.Parse(resp);
                        Assert.IsTrue(obj.ContainsKey("success"), "Response should contain 'success' key");
                        Assert.IsTrue(obj.ContainsKey("type_name"), "Response should contain 'type_name' key");
                        Assert.IsTrue(obj.ContainsKey("data") || obj.ContainsKey("matches"),
                            "Response should contain 'data' or 'matches'");
                    }
                }
                finally
                {
                    TryKill(vs);
                }
            }
        }

        [TestMethod]
        public async Task RunTool_GetSymbolInfoJs_ReturnsJsonAsync()
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                var vs = await VsLauncher.StartExperimentalInstanceAsync(cts.Token);
                try
                {
                    var solutionPath = GetSolutionPath();
                    var client = await IpcClient.ConnectAsync(PipeName, TimeSpan.FromMinutes(1), cts.Token);
                    using (client)
                    {
                        Assert.AreEqual("OK", await SendAsync(client, $"OpenSolution|{solutionPath}", cts.Token));

                        var resp = await SendAsync(client, $"RunTool|get_symbol_info_js|{UriEnc("lmInit")}", cts.Token);
                        Assert.IsFalse(string.IsNullOrEmpty(resp), "Response should not be empty");
                        Assert.StartsWith("{", resp, $"Response should be JSON, but got: {resp}");

                        var obj = JObject.Parse(resp);
                        Assert.IsTrue(obj.ContainsKey("success"), "Response should contain 'success' key");
                        Assert.IsTrue(obj.ContainsKey("symbol_name"), "Response should contain 'symbol_name' key");
                        Assert.IsTrue(obj.ContainsKey("files"), "Response should contain 'files' key");
                    }
                }
                finally
                {
                    TryKill(vs);
                }
            }
        }

        [TestMethod]
        public async Task RunTool_SetFileProjectStatus_UnknownProject_ReturnsJsonErrorAsync()
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                var vs = await VsLauncher.StartExperimentalInstanceAsync(cts.Token);
                try
                {
                    var solutionPath = GetSolutionPath();
                    var client = await IpcClient.ConnectAsync(PipeName, TimeSpan.FromMinutes(1), cts.Token);
                    using (client)
                    {
                        Assert.AreEqual("OK", await SendAsync(client, $"OpenSolution|{solutionPath}", cts.Token));

                        // Nonexistent project path → tool returns JSON error payload.
                        var resp = await SendAsync(client,
                            $"RunTool|set_file_project_status|{UriEnc("LMLocal.Tests.E2E.VSIX\\Scratch\\nope.cs")}|{UriEnc("BenchScratch\\NoProject.csproj")}|true",
                            cts.Token);
                        Assert.IsFalse(string.IsNullOrEmpty(resp), "Response should not be empty");
                        Assert.StartsWith("{", resp, $"Response should be JSON, but got: {resp}");

                        var obj = JObject.Parse(resp);
                        Assert.IsTrue(obj.ContainsKey("success"), "Response should contain 'success' key");
                        Assert.IsFalse((bool)obj["success"], "set_file_project_status should fail for a missing project");
                        Assert.IsTrue(obj.ContainsKey("error_message"), "Response should contain 'error_message' key");
                    }
                }
                finally
                {
                    TryKill(vs);
                }
            }
        }

        private string GetSolutionPath()
        {
            var solutionPath = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "LMLocal.sln")
            );

            if (!System.IO.File.Exists(solutionPath))
                throw new InvalidOperationException($"Test solution not found at '{solutionPath}'");
            return solutionPath;
        }

        private static void TryKill(System.Diagnostics.Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                    process.Kill();

                process.Dispose();
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        public TestContext TestContext { get; set; }
    }
}
