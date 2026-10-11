using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LMLocal.Core.Common;

namespace LMLocal.Infrastructure.Tooling.BuiltInVs.Common.Projects
{
    /// <summary>
    /// Project file discovered while scanning a folder workspace ("open a folder" mode).
    /// </summary>
    internal sealed class FolderProject
    {
        /// <summary>Absolute path to the project file (*.csproj, *.vcxproj, ...).</summary>
        public string Path { get; set; }

        /// <summary>Path relative to the workspace root (what the tool reports to the model).</summary>
        public string RelativePath { get; set; }

        public string Name { get; set; }

        public string Extension { get; set; }

        /// <summary>True when the project can be built by dotnet/msbuild in folder mode.</summary>
        public bool IsBuildable { get; set; }
    }

    /// <summary>
    /// Outcome of selecting the projects to build in a folder workspace.
    /// </summary>
    internal sealed class FolderProjectSelection
    {
        public List<FolderProject> Selected { get; } = new List<FolderProject>();

        /// <summary>Project files that were matched by name but cannot be built in folder mode.</summary>
        public List<string> NonBuildableSelected { get; } = new List<string>();

        /// <summary>Project files that were found but cannot be built this way (native, web, python, ...).</summary>
        public List<string> Skipped { get; } = new List<string>();

        /// <summary>True when the scan hit its project limit and may have missed project files.</summary>
        public bool Truncated { get; set; }

        public List<string> AmbiguousMatches { get; } = new List<string>();
    }

    /// <summary>
    /// Discovers and selects project files inside a folder workspace. Unlike solution mode there is no
    /// DTE project collection here — the projects only exist on disk — so names, relative paths and
    /// buildability are derived from the file system.
    /// </summary>
    internal static class FolderProjectSelector
    {
        /// <summary>Project extensions that `dotnet build` / MSBuild can handle without a VS shell.</summary>
        private static readonly HashSet<string> BuildableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".csproj", ".vbproj", ".fsproj"
        };

        /// <summary>Project extensions that are reported as skipped (need the VS build engine / other toolchains).</summary>
        private static readonly HashSet<string> KnownNonBuildableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".vcxproj", ".sqlproj", ".shproj", ".wixproj", ".njsproj", ".jsproj", ".pyproj"
        };

        private const int MaxDiscoveredProjects = 200;

        /// <summary>
        /// True when a project file can be built outside of Visual Studio (SDK-style or legacy managed project).
        /// </summary>
        public static bool IsBuildableProjectFile(string projectFilePath)
        {
            if (string.IsNullOrEmpty(projectFilePath)) return false;
            return BuildableExtensions.Contains(Path.GetExtension(projectFilePath));
        }

        /// <summary>
        /// True when the extension belongs to a project type we recognize but cannot build in folder mode.
        /// </summary>
        public static bool IsKnownNonBuildableProjectFile(string projectFilePath)
        {
            if (string.IsNullOrEmpty(projectFilePath)) return false;
            return KnownNonBuildableExtensions.Contains(Path.GetExtension(projectFilePath));
        }

        /// <summary>
        /// Finds all project files below <paramref name="rootDirectory"/> (build / dependency folders pruned).
        /// </summary>
        public static List<FolderProject> Discover(string rootDirectory, out bool truncated, int maxProjects = MaxDiscoveredProjects)
        {
            var result = new List<FolderProject>();
            truncated = false;

            if (string.IsNullOrEmpty(rootDirectory) || !Directory.Exists(rootDirectory))
                return result;

            if (maxProjects <= 0) maxProjects = MaxDiscoveredProjects;

            var stack = new Stack<string>();
            stack.Push(rootDirectory);

            while (stack.Count > 0)
            {
                string directory = stack.Pop();

                foreach (string file in SafeGetFiles(directory))
                {
                    string extension = Path.GetExtension(file);
                    if (string.IsNullOrEmpty(extension)) continue;
                    if (!BuildableExtensions.Contains(extension) && !KnownNonBuildableExtensions.Contains(extension))
                        continue;

                    if (result.Count >= maxProjects)
                    {
                        truncated = true;
                        continue;
                    }

                    result.Add(new FolderProject
                    {
                        Path = file,
                        RelativePath = ToRelativePath(rootDirectory, file),
                        Name = Path.GetFileNameWithoutExtension(file),
                        Extension = extension,
                        IsBuildable = BuildableExtensions.Contains(extension)
                    });
                }

                foreach (string subDirectory in SafeGetDirectories(directory))
                {
                    if (SolutionInspector.ShouldSkipDirectory(Path.GetFileName(subDirectory))) continue;
                    if (IsReparsePoint(subDirectory)) continue;
                    stack.Push(subDirectory);
                }
            }

            result.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>
        /// Selects the projects to build. <paramref name="projectName"/> may be a project name, a project
        /// file name, or a relative/absolute path to the project file. When it is empty, every buildable
        /// project in the folder is selected.
        /// </summary>
        public static FolderProjectSelection Select(List<FolderProject> discovered, string projectName, string workspaceRoot)
        {
            var selection = new FolderProjectSelection();
            if (discovered == null) return selection;

            foreach (var project in discovered)
            {
                if (project == null) continue;
                if (!project.IsBuildable)
                    selection.Skipped.Add(project.RelativePath);
            }

            if (string.IsNullOrWhiteSpace(projectName))
            {
                foreach (var project in discovered)
                {
                    if (project != null && project.IsBuildable)
                        selection.Selected.Add(project);
                }
                return selection;
            }

            var matches = FindMatches(discovered, projectName, workspaceRoot);

            if (matches.Count == 0)
                return selection;

            if (matches.Count > 1)
            {
                selection.AmbiguousMatches.AddRange(matches.Select(m => m.RelativePath));
                return selection;
            }

            if (matches[0].IsBuildable)
                selection.Selected.Add(matches[0]);
            else
                selection.NonBuildableSelected.Add(matches[0].RelativePath);

            return selection;
        }

        /// <summary>
        /// Matches by project name ("App"), file name ("App.csproj") or path ("src/App/App.csproj").
        /// </summary>
        public static List<FolderProject> FindMatches(List<FolderProject> discovered, string projectName, string workspaceRoot)
        {
            var matches = new List<FolderProject>();
            if (discovered == null || string.IsNullOrWhiteSpace(projectName)) return matches;

            string requested = ProjectFinder.NormalizeProjectName(projectName);
            if (string.IsNullOrEmpty(requested)) return matches;

            // 1) exact path match (absolute, or relative to the workspace root)
            string absoluteRequested = TryGetFullPath(requested, workspaceRoot);
            foreach (var project in discovered)
            {
                if (project == null) continue;

                string absoluteProject = TryGetFullPath(project.Path, workspaceRoot);
                if (absoluteRequested != null && absoluteProject != null &&
                    string.Equals(absoluteRequested, absoluteProject, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(project);
                }
            }
            if (matches.Count > 0) return matches;

            // 2) name / file name / relative path match
            foreach (var project in discovered)
            {
                if (project == null) continue;

                if (ProjectFinder.IsNameMatch(project.Name, requested) ||
                    ProjectFinder.IsNameMatch(Path.GetFileName(project.Path), requested) ||
                    ProjectFinder.IsNameMatch(project.RelativePath, requested))
                {
                    matches.Add(project);
                }
            }

            return matches;
        }

        private static string TryGetFullPath(string path, string workspaceRoot)
        {
            try
            {
                if (Path.IsPathRooted(path))
                    return Path.GetFullPath(path);

                if (!string.IsNullOrEmpty(workspaceRoot))
                    return Path.GetFullPath(Path.Combine(workspaceRoot, path));

                return Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                InternalLogger.Debug($"FolderProjectSelector: could not resolve path '{path}': {ex.Message}");
                return null;
            }
        }

        private static string ToRelativePath(string rootDirectory, string fullPath)
        {
            string root = rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return full;

            if (full.Length == root.Length) return ".";

            string relative = full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        private static IEnumerable<string> SafeGetFiles(string directory)
        {
            try
            {
                return Directory.GetFiles(directory);
            }
            catch (Exception ex)
            {
                InternalLogger.Debug($"FolderProjectSelector: failed to enumerate files in {directory}: {ex.Message}");
                return Enumerable.Empty<string>();
            }
        }

        private static IEnumerable<string> SafeGetDirectories(string directory)
        {
            try
            {
                return Directory.GetDirectories(directory);
            }
            catch (Exception ex)
            {
                InternalLogger.Debug($"FolderProjectSelector: failed to enumerate directories in {directory}: {ex.Message}");
                return Enumerable.Empty<string>();
            }
        }

        private static bool IsReparsePoint(string directoryPath)
        {
            try
            {
                return (File.GetAttributes(directoryPath) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex)
            {
                InternalLogger.Debug($"FolderProjectSelector: failed to read attributes of {directoryPath}: {ex.Message}");
                return false;
            }
        }
    }
}
