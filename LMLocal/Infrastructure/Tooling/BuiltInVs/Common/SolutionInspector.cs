using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LMLocal.Core.Common;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace LMLocal.Infrastructure.Tooling.BuiltInVs.Common
{
    /// <summary>
    /// High-level workspace structure inspector.
    /// Provides a cached overview of what Visual Studio has open: a real solution file or a plain folder
    /// ("open a folder" mode). For solutions the projects are enumerated from <see cref="IVsSolution"/>;
    /// for folders the project files are discovered by scanning the directory on disk.
    /// </summary>
    internal static class SolutionInspector
    {
        /// <summary>Maximum directory depth scanned on disk (folder mode / project file counting).</summary>
        private const int MaxScanDepth = 8;

        /// <summary>Safety cap for source files counted per project when scanning disk.</summary>
        private const int MaxCountedSourceFiles = 10000;

        /// <summary>Cache lifetime for folder workspaces — there is no single file whose timestamp can invalidate the cache.</summary>
        private static readonly TimeSpan FolderCacheLifetime = TimeSpan.FromSeconds(30);

        /// <summary>Project file names recognized when scanning a folder workspace.</summary>
        private static readonly string[] ProjectFilePatterns =
        {
            "*.csproj", "*.vbproj", "*.fsproj", "*.vcxproj", "*.sqlproj", "*.shproj", "*.wixproj",
            "*.njsproj", "*.jsproj", "*.pyproj"
        };

        /// <summary>Extensions that actually identify a solution file (as opposed to a directory path).</summary>
        private static readonly string[] SolutionFileExtensions = { ".sln", ".slnx" };

        private static (string workspaceKey, DateTime timestamp, SolutionInfo overview)? _cache;

        /// <summary>
        /// Gets a high-level overview of the active workspace (solution file or open folder).
        /// </summary>
        public static SolutionInfo GetSolutionOverview(IVsSolution solution, int maxProjects = 200)
        {
            return GetSolutionOverview(solution, null, maxProjects);
        }

        /// <summary>
        /// Gets a high-level overview of the active workspace. <paramref name="fallbackWorkspaceRoot"/> is
        /// used only when <paramref name="solution"/> is unavailable (e.g. "open a folder" mode where the
        /// solution service could not be acquired) so that a folder overview can still be produced.
        /// </summary>
        public static SolutionInfo GetSolutionOverview(IVsSolution solution, string fallbackWorkspaceRoot, int maxProjects = 200)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (maxProjects <= 0)
                maxProjects = 200;

            string solutionDirectory = null;
            string solutionFile = null;

            if (solution != null)
                solution.GetSolutionInfo(out solutionDirectory, out solutionFile, out string _);

            // No solution service at all: fall back to a caller-provided folder root (folder mode),
            // otherwise there is nothing to report.
            if (solution == null)
            {
                string root = NormalizeDirectoryPath(fallbackWorkspaceRoot);
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                    return GetFolderOverview(root, maxProjects);

                return CreateEmptyOverview(WorkspaceKind.None, null);
            }

            WorkspaceKind workspaceKind = DetermineWorkspaceKind(solution, solutionFile, solutionDirectory);

            // In folder mode the workspace root can show up in the solution-file out-parameter instead
            // of the directory one, so resolve the root from both values.
            string workspaceRoot = ResolveWorkspaceDirectory(solutionDirectory, solutionFile);

            switch (workspaceKind)
            {
                case WorkspaceKind.Folder:
                    return GetFolderOverview(workspaceRoot, maxProjects);

                case WorkspaceKind.None:
                    return CreateEmptyOverview(WorkspaceKind.None, null);

                default:
                    return GetLoadedSolutionOverview(solution, workspaceRoot, solutionFile, maxProjects);
            }
        }

        /// <summary>
        /// Determines whether VS has a solution file loaded, is in "open a folder" mode, or has nothing open.
        /// Note: in "open a folder" mode <c>GetSolutionInfo</c> may report the folder (or the directory of a
        /// project loaded inside the folder) in the solution-file out-parameter, so that value alone is not
        /// proof of a loaded solution — see <see cref="IsSolutionFilePath"/>.
        /// </summary>
        internal static WorkspaceKind DetermineWorkspaceKind(IVsSolution solution, string solutionFile, string solutionDirectory)
        {
            bool hasSolutionFile = IsSolutionFilePath(solutionFile, solutionDirectory);
            bool hasWorkspaceDirectory = !string.IsNullOrEmpty(solutionDirectory) || !string.IsNullOrEmpty(solutionFile);

            if (hasSolutionFile)
                return WorkspaceKind.Solution;

            if (solution == null)
                return hasWorkspaceDirectory ? WorkspaceKind.Folder : WorkspaceKind.None;

            try
            {
                if (solution.GetProperty((int)__VSPROPID7.VSPROPID_IsInOpenFolderMode, out object openFolderObj) == VSConstants.S_OK
                    && openFolderObj is bool isOpenFolder)
                {
                    if (isOpenFolder)
                        return WorkspaceKind.Folder;

                    // Explicitly reported as "not a folder" and no solution file → nothing is open.
                    return WorkspaceKind.None;
                }
            }
            catch (Exception ex)
            {
                InternalLogger.Debug($"SolutionInspector: VSPROPID_IsInOpenFolderMode is not available: {ex.Message}");
            }

            // Fallback for shells that do not expose the property: a non-empty directory means a folder workspace.
            return hasWorkspaceDirectory ? WorkspaceKind.Folder : WorkspaceKind.None;
        }

        /// <summary>
        /// True when <paramref name="solutionFile"/> really points to a solution file.
        /// In "open a folder" mode <c>IVsSolution.GetSolutionInfo</c> can return the open folder itself —
        /// or the directory of a project loaded inside that folder — in the solution-file out-parameter.
        /// The value must therefore not be trusted blindly, otherwise a folder workspace would be
        /// misdetected as a loaded solution.
        /// </summary>
        internal static bool IsSolutionFilePath(string solutionFile, string solutionDirectory)
        {
            if (string.IsNullOrEmpty(solutionFile))
                return false;

            string extension = Path.GetExtension(solutionFile);
            if (string.IsNullOrEmpty(extension) ||
                !SolutionFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                return false;

            // A directory can never be a solution file, even if its name happens to end in ".sln".
            if (IsExistingDirectory(solutionFile))
                return false;

            // Some shells report the workspace directory in both out-parameters.
            string fileAsDirectory = NormalizeDirectoryPath(solutionFile);
            string directory = NormalizeDirectoryPath(solutionDirectory);
            if (!string.IsNullOrEmpty(directory) &&
                string.Equals(fileAsDirectory, directory, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        /// <summary>
        /// Root directory of the workspace. Folder mode sometimes reports the folder only in the
        /// solution-file out-parameter, so that value is used as a fallback.
        /// </summary>
        private static string ResolveWorkspaceDirectory(string solutionDirectory, string solutionFile)
        {
            string directory = NormalizeDirectoryPath(solutionDirectory);
            if (!string.IsNullOrEmpty(directory) && IsExistingDirectory(directory))
                return directory;

            string fromSolutionFile = NormalizeDirectoryPath(solutionFile);
            if (!string.IsNullOrEmpty(fromSolutionFile) && IsExistingDirectory(fromSolutionFile))
                return fromSolutionFile;

            return directory;
        }

        private static bool IsExistingDirectory(string path)
        {
            try
            {
                return Directory.Exists(path);
            }
            catch (Exception ex)
            {
                InternalLogger.Debug($"SolutionInspector: Failed to check directory '{path}': {ex.Message}");
                return false;
            }
        }


        /// <summary>
        /// Overview built from the projects loaded in the solution file.
        /// </summary>
        private static SolutionInfo GetLoadedSolutionOverview(IVsSolution solution, string solutionDirectory, string solutionFile, int maxProjects)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Check cache validity (keyed by solution file, invalidated when the .sln changes)
            DateTime lastWrite = DateTime.MinValue;
            try
            {
                if (File.Exists(solutionFile))
                    lastWrite = File.GetLastWriteTime(solutionFile);
            }
            catch (Exception ex)
            {
                InternalLogger.Warn($"SolutionInspector: Failed to get last write time for {solutionFile}: {ex.Message}");
            }

            bool cacheValid = _cache.HasValue &&
                              _cache.Value.workspaceKey.Equals(solutionFile, StringComparison.OrdinalIgnoreCase) &&
                              _cache.Value.timestamp >= lastWrite;

            if (cacheValid)
            {
                return _cache.Value.overview;
            }
            else
            {
                ProjectMetadataProvider.ClearAll(); // Clear project metadata cache when solution changes
            }

            var projectsList = new List<ProjectInfo>();
            var solutionFolders = new List<string>();

            int totalFilesCount = 0;
            int projectsAddedCount = 0;

            bool limitExceeded = false;

            uint flags = (uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION;
            Guid guid = Guid.Empty;
            solution.GetProjectEnum(flags, ref guid, out IEnumHierarchies enumHierarchies);

            var hierarchy = new IVsHierarchy[1];
            while (enumHierarchies.Next(1, hierarchy, out uint fetched) == VSConstants.S_OK && fetched == 1)
            {
                IVsHierarchy projectHierarchy = hierarchy[0];

                try
                {
                    if (IsSolutionFolder(projectHierarchy))
                    {
                        string folderName = GetProjectName(projectHierarchy);
                        if (!string.IsNullOrEmpty(folderName))
                            solutionFolders.Add(folderName);
                        continue;
                    }

                    string projectName = GetProjectName(projectHierarchy);
                    string projectFilePath = GetProjectFilePath(projectHierarchy);
                    if (string.IsNullOrEmpty(projectFilePath))
                    {
                        InternalLogger.Warn($"SolutionInspector: Project '{projectName}' has no file path, skipping.");
                        continue;
                    }

                    if (projectsAddedCount >= maxProjects)
                    {
                        limitExceeded = true;
                        continue;
                    }

                    string relativePath = GetRelativePath(solutionDirectory, projectFilePath);
                    (string language, string framework, bool isNativeTest) = ProjectMetadataProvider.GetMetadata(projectFilePath);
                    int fileCount = CountSourceFilesInProject(projectHierarchy, language);
                    bool isTest = IsTestProject(projectName, isNativeTest);

                    projectsList.Add(new ProjectInfo(projectName, language, relativePath, fileCount, isTest, framework));
                    totalFilesCount += fileCount;
                    projectsAddedCount++;
                }
                catch (Exception ex)
                {
                    InternalLogger.Warn($"SolutionInspector: Failed to process project: {ex.Message}");
                    continue;
                }
            }

            bool truncated = limitExceeded;

            var result = new SolutionInfo(
                Path.GetFileName(solutionFile),
                solutionFile,
                projectsAddedCount,
                totalFilesCount,
                projectsList,
                truncated,
                solutionFolders,
                WorkspaceKind.Solution,
                solutionDirectory,
                ProjectDiscoveryMode.LoadedInSolution,
                new List<string>()
            );

            _cache = (solutionFile, DateTime.Now, result);
            return result;
        }

        /// <summary>
        /// Overview for "open a folder" mode: no solution file exists, so projects are discovered by
        /// scanning the folder for project files (*.csproj, *.vcxproj, ...).
        /// </summary>
        private static SolutionInfo GetFolderOverview(string folderPath, int maxProjects)
        {
            string root = NormalizeDirectoryPath(folderPath);

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                InternalLogger.Warn($"SolutionInspector: Folder workspace '{folderPath}' does not exist.");
                return CreateEmptyOverview(WorkspaceKind.Folder, folderPath);
            }

            string cacheKey = "folder:" + root.TrimEnd(Path.DirectorySeparatorChar);

            bool sameWorkspace = _cache.HasValue &&
                                 string.Equals(_cache.Value.workspaceKey, cacheKey, StringComparison.OrdinalIgnoreCase);

            bool cacheValid = sameWorkspace && DateTime.Now - _cache.Value.timestamp < FolderCacheLifetime;
            if (cacheValid)
                return _cache.Value.overview;

            if (!sameWorkspace)
                ProjectMetadataProvider.ClearAll();

            var projectsList = new List<ProjectInfo>();
            int totalFilesCount = 0;

            List<string> projectFiles = DiscoverProjectFilesOnDisk(root, maxProjects, out bool truncated);

            foreach (string projectFile in projectFiles)
            {
                try
                {
                    string projectName = Path.GetFileNameWithoutExtension(projectFile);
                    (string language, string framework, bool isNativeTest) = ProjectMetadataProvider.GetMetadata(projectFile);
                    int fileCount = CountSourceFilesOnDisk(Path.GetDirectoryName(projectFile), language);

                    projectsList.Add(new ProjectInfo(
                        projectName,
                        language,
                        GetRelativePath(root, projectFile),
                        fileCount,
                        IsTestProject(projectName, isNativeTest),
                        framework));

                    totalFilesCount += fileCount;
                }
                catch (Exception ex)
                {
                    InternalLogger.Warn($"SolutionInspector: Failed to process project file '{projectFile}': {ex.Message}");
                }
            }

            var result = new SolutionInfo(
                new DirectoryInfo(root).Name,
                root,
                projectsList.Count,
                totalFilesCount,
                projectsList,
                truncated,
                new List<string>(),
                WorkspaceKind.Folder,
                root,
                ProjectDiscoveryMode.ProjectFilesOnDisk,
                GetTopLevelDirectories(root)
            );

            _cache = (cacheKey, DateTime.Now, result);
            return result;
        }

        private static SolutionInfo CreateEmptyOverview(WorkspaceKind workspaceKind, string workspaceRoot)
        {
            return new SolutionInfo(
                null,
                workspaceRoot,
                0,
                0,
                new List<ProjectInfo>(),
                false,
                new List<string>(),
                workspaceKind,
                workspaceRoot,
                ProjectDiscoveryMode.Unknown,
                new List<string>());
        }

        /// <summary>
        /// Recursively finds project files below <paramref name="rootDirectory"/>, pruning build / dependency folders.
        /// </summary>
        internal static List<string> DiscoverProjectFilesOnDisk(string rootDirectory, int maxProjects, out bool truncated)
        {
            truncated = false;
            var results = new List<string>();

            if (string.IsNullOrEmpty(rootDirectory) || !Directory.Exists(rootDirectory))
                return results;

            var stack = new Stack<DirectoryEntry>();
            stack.Push(new DirectoryEntry(rootDirectory, 0));

            while (stack.Count > 0)
            {
                DirectoryEntry entry = stack.Pop();

                foreach (string pattern in ProjectFilePatterns)
                {
                    string[] found;
                    try
                    {
                        found = Directory.GetFiles(entry.Path, pattern);
                    }
                    catch (Exception ex)
                    {
                        InternalLogger.Debug($"SolutionInspector: Failed to enumerate '{pattern}' in {entry.Path}: {ex.Message}");
                        continue;
                    }

                    foreach (string file in found)
                    {
                        if (results.Count >= maxProjects)
                        {
                            truncated = true;
                            break;
                        }
                        results.Add(file);
                    }

                    if (truncated)
                        break;
                }

                if (truncated || entry.Depth >= MaxScanDepth)
                    continue;

                string[] subDirectories;
                try
                {
                    subDirectories = Directory.GetDirectories(entry.Path);
                }
                catch (Exception ex)
                {
                    InternalLogger.Debug($"SolutionInspector: Failed to enumerate directories in {entry.Path}: {ex.Message}");
                    continue;
                }

                foreach (string subDirectory in subDirectories)
                {
                    string name = Path.GetFileName(subDirectory);
                    if (ShouldSkipDirectory(name) || IsReparsePoint(subDirectory))
                        continue;

                    stack.Push(new DirectoryEntry(subDirectory, entry.Depth + 1));
                }
            }

            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }

        /// <summary>
        /// Counts source files below <paramref name="directory"/> without going through a VS hierarchy.
        /// </summary>
        internal static int CountSourceFilesOnDisk(string directory, string language)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return 0;

            int count = 0;
            var stack = new Stack<DirectoryEntry>();
            stack.Push(new DirectoryEntry(directory, 0));

            while (stack.Count > 0 && count < MaxCountedSourceFiles)
            {
                DirectoryEntry entry = stack.Pop();

                string[] files;
                try
                {
                    files = Directory.GetFiles(entry.Path);
                }
                catch (Exception ex)
                {
                    InternalLogger.Debug($"SolutionInspector: Failed to enumerate files in {entry.Path}: {ex.Message}");
                    continue;
                }

                foreach (string file in files)
                {
                    string fileName = Path.GetFileName(file);
                    if (SourceFileFilter.IsExcludedFile(fileName))
                        continue;

                    if (SourceFileExtensions.IsSourceFile(Path.GetExtension(fileName), language))
                    {
                        count++;
                        if (count >= MaxCountedSourceFiles)
                            break;
                    }
                }

                if (entry.Depth >= MaxScanDepth)
                    continue;

                string[] subDirectories;
                try
                {
                    subDirectories = Directory.GetDirectories(entry.Path);
                }
                catch (Exception ex)
                {
                    InternalLogger.Debug($"SolutionInspector: Failed to enumerate directories in {entry.Path}: {ex.Message}");
                    continue;
                }

                foreach (string subDirectory in subDirectories)
                {
                    if (ShouldSkipDirectory(Path.GetFileName(subDirectory)) || IsReparsePoint(subDirectory))
                        continue;

                    stack.Push(new DirectoryEntry(subDirectory, entry.Depth + 1));
                }
            }

            return count;
        }

        /// <summary>
        /// Immediate sub-directories of the workspace root (helps the model orient itself in folder mode).
        /// </summary>
        private static List<string> GetTopLevelDirectories(string rootDirectory)
        {
            var result = new List<string>();

            try
            {
                foreach (string subDirectory in Directory.GetDirectories(rootDirectory))
                {
                    string name = Path.GetFileName(subDirectory);
                    if (ShouldSkipDirectory(name))
                        continue;

                    result.Add(name);
                    if (result.Count >= 200)
                        break;
                }
            }
            catch (Exception ex)
            {
                InternalLogger.Debug($"SolutionInspector: Failed to enumerate top-level directories in {rootDirectory}: {ex.Message}");
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>
        /// True when a directory name must never be scanned (build output, dependencies, hidden folders).
        /// </summary>
        internal static bool ShouldSkipDirectory(string directoryName)
        {
            if (string.IsNullOrEmpty(directoryName))
                return true;

            if (directoryName.StartsWith(".", StringComparison.Ordinal))
                return true;

            return SourceFileFilter.ExcludedDirectories.Contains(directoryName);
        }

        private static bool IsReparsePoint(string directoryPath)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(directoryPath);
                return (attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex)
            {
                InternalLogger.Debug($"SolutionInspector: Failed to read attributes of {directoryPath}: {ex.Message}");
                return false;
            }
        }

        private static string NormalizeDirectoryPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex)
            {
                InternalLogger.Warn($"SolutionInspector: Invalid workspace directory '{path}': {ex.Message}");
                return null;
            }
        }

        private struct DirectoryEntry
        {
            public string Path { get; }
            public int Depth { get; }

            public DirectoryEntry(string path, int depth)
            {
                Path = path;
                Depth = depth;
            }
        }

        /// <summary>
        /// Checks if a hierarchy represents a solution folder (virtual container).
        /// </summary>
        private static bool IsSolutionFolder(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            const string solutionFolderGuid = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";
            if (hierarchy.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_TypeGuid, out object typeGuidObj) == VSConstants.S_OK)
            {
                if (typeGuidObj is string typeGuid && typeGuid.Equals(solutionFolderGuid, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Gets the project name from hierarchy.
        /// </summary>
        private static string GetProjectName(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string name = null;
            if (hierarchy.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_Name, out object nameObj) == VSConstants.S_OK)
                name = nameObj as string;
            if (string.IsNullOrEmpty(name))
            {
                if (hierarchy.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_Caption, out object captionObj) == VSConstants.S_OK)
                    name = captionObj as string;
            }
            return name ?? "Unknown";
        }

        /// <summary>
        /// Gets the project file path from hierarchy.
        /// </summary>
        private static string GetProjectFilePath(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (hierarchy is IVsProject project)
            {
                try
                {
                    if (project.GetMkDocument(VSConstants.VSITEMID_ROOT, out string projectFilePath) == VSConstants.S_OK)
                    {
                        if (!string.IsNullOrEmpty(projectFilePath))
                            return projectFilePath;
                    }
                }
                catch (Exception ex)
                {
                    InternalLogger.Warn($"SolutionInspector: Failed to get project file via GetMkDocument: {ex.Message}");
                }
            }

            if (hierarchy.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_ProjectDir, out object pathObj) == VSConstants.S_OK)
                return pathObj as string;

            return null;
        }

        /// <summary>
        /// Converts an absolute path to a relative path from the given base directory.
        /// </summary>
        private static string GetRelativePath(string basePath, string fullPath)
        {
            if (string.IsNullOrEmpty(basePath)) return fullPath;
            string baseDir = basePath.TrimEnd('\\', '/');
            string full = fullPath.TrimEnd('\\', '/');
            if (!full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                return full;
            if (full.Length == baseDir.Length) return ".";
            string rel = full.Substring(baseDir.Length + 1);
            return rel.Replace('\\', Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// Counts source files recursively in a project hierarchy.
        /// </summary>
        private static int CountSourceFilesInProject(IVsHierarchy hierarchy, string language)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int count = 0;
            CountFilesRecursive(hierarchy, VSConstants.VSITEMID_ROOT, language, ref count);
            return count;
        }

        private static void CountFilesRecursive(IVsHierarchy hierarchy, uint itemId, string language, ref int count)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (hierarchy.GetProperty(itemId, (int)__VSHPROPID.VSHPROPID_FirstChild, out object childObj) != VSConstants.S_OK)
                return;
            if (!TryGetItemId(childObj, out uint childId))
                return;

            while (childId != VSConstants.VSITEMID_NIL)
            {
                bool hasChildren = hierarchy.GetProperty(childId, (int)__VSHPROPID.VSHPROPID_FirstChild, out object maybeChildObj) == VSConstants.S_OK && TryGetItemId(maybeChildObj, out uint _);
                if (hasChildren)
                {
                    CountFilesRecursive(hierarchy, childId, language, ref count);
                }
                else
                {
                    if (hierarchy.GetProperty(childId, (int)__VSHPROPID.VSHPROPID_Name, out object nameObj) == VSConstants.S_OK)
                    {
                        string fileName = nameObj as string;
                        if (!string.IsNullOrEmpty(fileName))
                        {
                            string ext = Path.GetExtension(fileName);
                            if (SourceFileExtensions.IsSourceFile(ext, language))
                                count++;
                        }
                    }
                }

                if (hierarchy.GetProperty(childId, (int)__VSHPROPID.VSHPROPID_NextSibling, out object nextObj) != VSConstants.S_OK)
                    break;
                if (!TryGetItemId(nextObj, out childId))
                    break;
            }
        }


        private static bool TryGetItemId(object value, out uint id)
        {
            id = VSConstants.VSITEMID_NIL;
            if (value == null) return false;
            if (value is uint v)
            {
                id = v;
                return true;
            }
            if (value is int i)
            {
                if (i >= 0)
                {
                    id = (uint)i;
                    return true;
                }
            }
            if (value is long l)
            {
                if (l >= 0)
                {
                    id = (uint)l;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Determines if a project is a test project.
        /// </summary>
        internal static bool IsTestProject(string projectName, bool isNativeTestProject)
        {
            if (isNativeTestProject)
                return true;

            if (string.IsNullOrEmpty(projectName))
                return false;

            return projectName.IndexOf("test", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Project information for solution overview.
        /// </summary>
        public class ProjectInfo
        {
            public string Name { get; set; }
            public string Language { get; set; }
            public string Path { get; set; }
            public int FileCount { get; set; }
            public bool IsTestProject { get; set; }
            public string TargetFramework { get; set; }

            public ProjectInfo(string name, string language, string path, int fileCount, bool isTestProject, string targetFramework)
            {
                Name = name;
                Language = language;
                Path = path;
                FileCount = fileCount;
                IsTestProject = isTestProject;
                TargetFramework = targetFramework;
            }
        }

        /// <summary>
        /// Workspace structure summary (solution file or open folder).
        /// </summary>
        public class SolutionInfo
        {
            public string SolutionName { get; set; }
            public string SolutionPath { get; set; }
            public int TotalProjects { get; set; }
            public int TotalFiles { get; set; }
            public List<ProjectInfo> Projects { get; set; }
            public bool Truncated { get; set; }
            public List<string> SolutionFolders { get; set; }

            /// <summary>Whether a solution file is loaded, only a folder is open, or nothing is open.</summary>
            public WorkspaceKind WorkspaceKind { get; set; }

            /// <summary>Root directory of the workspace (solution directory or the open folder).</summary>
            public string WorkspaceRoot { get; set; }

            /// <summary>How the project list was produced (loaded projects vs. project files found on disk).</summary>
            public ProjectDiscoveryMode DiscoveryMode { get; set; }

            /// <summary>Immediate sub-directories of the workspace root; populated for folder workspaces only.</summary>
            public List<string> TopLevelDirectories { get; set; }

            public SolutionInfo(string solutionName, string solutionPath, int totalProjects, int totalFiles,
                List<ProjectInfo> projects, bool truncated, List<string> solutionFolders,
                WorkspaceKind workspaceKind = WorkspaceKind.Unknown,
                string workspaceRoot = null,
                ProjectDiscoveryMode discoveryMode = ProjectDiscoveryMode.Unknown,
                List<string> topLevelDirectories = null)
            {
                SolutionName = solutionName;
                SolutionPath = solutionPath;
                TotalProjects = totalProjects;
                TotalFiles = totalFiles;
                Projects = projects;
                Truncated = truncated;
                SolutionFolders = solutionFolders;
                WorkspaceKind = workspaceKind;
                WorkspaceRoot = workspaceRoot;
                DiscoveryMode = discoveryMode;
                TopLevelDirectories = topLevelDirectories ?? new List<string>();
            }
        }
    }
}
