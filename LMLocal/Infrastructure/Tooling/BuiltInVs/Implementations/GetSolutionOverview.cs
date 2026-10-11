using LMLocal.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LMLocal.Core.Common;
using LMLocal.Infrastructure.Tooling.BuiltInVs.Abstractions;
using LMLocal.Infrastructure.Tooling.BuiltInVs.Common;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace LMLocal.Infrastructure.Tooling.BuiltInVs.Implementations
{
    internal interface IGetSolutionOverview : IBuiltInTool
    {
    }

    internal class GetSolutionOverview : IGetSolutionOverview
    {
        private readonly IVsDependencies _vsDependencies;

        public string ToolName => "get_solution_overview";
        public ToolAccessLevel AccessLevel => ToolAccessLevel.ReadOnly;

        public GetSolutionOverview(IVsDependencies vsDependencies)
        {
            _vsDependencies = vsDependencies ?? throw new ArgumentNullException(nameof(vsDependencies));
        }

        public ToolDefinition GetToolInfo()
        {
            return new ToolDefinition
            {
                Name = ToolName,
                Description = "Returns a high-level summary of the currently active Visual Studio workspace. 'workspace_kind' tells what is open: 'Solution' (a .sln is loaded), 'Folder' (only a working folder was opened — no solution; the project list is then built by scanning the folder for project files such as *.csproj/*.vcxproj, and 'top_level_directories' lists its sub-folders), 'None' or 'Unknown'. Also returns name, path, project list (language, file count, test project flag), solution folders, 'discovery_mode' (how the projects were found) and total file count. Use when you need to discover the workspace structure or choose a project. The projects array is limited to 200 entries; has_more_results=true means some projects were not included. Results are cached for performance — call once and refer to it.",
                Parameters = new ToolParameters
                {
                    Type = "object",
                    Properties = new Dictionary<string, ToolDetails>(),
                    Required = new List<string>()
                }
            };
        }

        public async Task<object> ExecuteAsync(Dictionary<string, object> parameters, CancellationToken cancellationToken = default)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                // GetSolution() re-acquires SVsSolution on demand; it is available even in
                // "open a folder" mode (it just has no solution file). The fallback root lets a
                // folder overview still be produced in the rare case the service is unavailable.
                var solution = _vsDependencies.GetSolution();
                string fallbackRoot = _vsDependencies.GetSolutionDirectory();

                var overview = SolutionInspector.GetSolutionOverview(solution, fallbackRoot, maxProjects: 200);

                if (overview.WorkspaceKind == WorkspaceKind.None)
                {
                    return Error(WorkspaceKind.None, "No solution or folder is currently open");
                }

                var response = new SolutionOverviewResponse
                {
                    WorkspaceKind = overview.WorkspaceKind,
                    DiscoveryMode = overview.DiscoveryMode,
                    WorkspaceRoot = overview.WorkspaceRoot,
                    SolutionName = overview.SolutionName,
                    SolutionPath = overview.SolutionPath,
                    TotalProjects = overview.TotalProjects,
                    TotalFiles = overview.TotalFiles,
                    HasMoreResults = overview.Truncated,
                    SolutionFolders = overview.SolutionFolders,
                    TopLevelDirectories = overview.TopLevelDirectories,
                    Projects = new List<ProjectOverviewItem>(),
                    Success = true,
                    ErrorMessage = null
                };

                foreach (var project in overview.Projects)
                {
                    response.Projects.Add(new ProjectOverviewItem
                    {
                        Name = project.Name,
                        Language = project.Language,
                        Path = project.Path,
                        FileCount = project.FileCount,
                        IsTestProject = project.IsTestProject
                    });
                }

                return response;
            }
            catch (Exception ex)
            {
                InternalLogger.Error($"Failed to generate solution overview: {ex}");
                return Error(WorkspaceKind.Unknown, ex.Message);
            }
        }

        private SolutionOverviewResponse Error(WorkspaceKind workspaceKind, string errorMessage)
        {
            return new SolutionOverviewResponse
            {
                Success = false,
                ErrorMessage = errorMessage,
                WorkspaceKind = workspaceKind,
                DiscoveryMode = ProjectDiscoveryMode.Unknown,
                WorkspaceRoot = null,
                SolutionName = null,
                SolutionPath = null,
                TotalProjects = 0,
                TotalFiles = 0,
                HasMoreResults = false,
                Projects = new List<ProjectOverviewItem>(),
                SolutionFolders = new List<string>(),
                TopLevelDirectories = new List<string>()
            };
        }

        public string GetProcessingMessage(Dictionary<string, object> parameters)
        {
            return "Loading solution structure... ";
        }

        public string GetCompletionMessage(object result)
        {
            if (result is SolutionOverviewResponse solutionResult)
            {
                if (!solutionResult.Success)
                    return $"Failed to load: {solutionResult.ErrorMessage}";

                string summary = $"{solutionResult.TotalProjects} {Pluralizer.Pluralize(solutionResult.TotalProjects, "project", "projects")}, {solutionResult.TotalFiles} {Pluralizer.Pluralize(solutionResult.TotalFiles, "file", "files")}";

                if (solutionResult.WorkspaceKind == WorkspaceKind.Folder)
                    return $"Loaded workspace folder ({summary}). No solution file is open.";

                return $"Loaded {summary}.";
            }
            return "Solution overview loaded.";
        }

        public class SolutionOverviewResponse
        {
            /// <summary>What Visual Studio currently has open: a solution file, a plain folder, or nothing.</summary>
            [JsonProperty("workspace_kind")]
            [JsonConverter(typeof(StringEnumConverter))]
            public WorkspaceKind WorkspaceKind { get; set; }

            /// <summary>How the project list was produced (loaded projects vs. project files scanned on disk).</summary>
            [JsonProperty("discovery_mode")]
            [JsonConverter(typeof(StringEnumConverter))]
            public ProjectDiscoveryMode DiscoveryMode { get; set; }

            /// <summary>Root directory of the workspace (solution directory or the open folder).</summary>
            [JsonProperty("workspace_root", NullValueHandling = NullValueHandling.Ignore)]
            public string WorkspaceRoot { get; set; }

            [JsonProperty("solution_name")]
            public string SolutionName { get; set; }

            [JsonProperty("solution_path")]
            public string SolutionPath { get; set; }

            [JsonProperty("total_projects")]
            public int TotalProjects { get; set; }

            [JsonProperty("total_files")]
            public int TotalFiles { get; set; }

            [JsonProperty("has_more_results")]
            public bool HasMoreResults { get; set; }

            [JsonProperty("projects")]
            public List<ProjectOverviewItem> Projects { get; set; }

            [JsonProperty("solution_folders")]
            public List<string> SolutionFolders { get; set; }

            /// <summary>Immediate sub-directories of the workspace root; populated for folder workspaces only.</summary>
            [JsonProperty("top_level_directories")]
            public List<string> TopLevelDirectories { get; set; }

            [JsonProperty("success")]
            public bool Success { get; set; }

            [JsonProperty("error_message", NullValueHandling = NullValueHandling.Ignore)]
            public string ErrorMessage { get; set; }
        }

        public class ProjectOverviewItem
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("language")]
            public string Language { get; set; }

            [JsonProperty("path")]
            public string Path { get; set; }

            [JsonProperty("file_count")]
            public int FileCount { get; set; }

            [JsonProperty("is_test_project")]
            public bool IsTestProject { get; set; }
        }
    }
}
