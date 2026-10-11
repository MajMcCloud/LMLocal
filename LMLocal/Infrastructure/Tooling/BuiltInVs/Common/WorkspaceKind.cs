namespace LMLocal.Infrastructure.Tooling.BuiltInVs.Common
{
    /// <summary>
    /// Describes what Visual Studio currently has open as the active workspace.
    /// A real solution file is not the only possibility: VS can also run in "open a folder" mode,
    /// where no project is loaded and only a directory (with arbitrary sub-directories) is browsed.
    /// </summary>
    public enum WorkspaceKind
    {
        /// <summary>Could not be determined (older VS shell, property not available).</summary>
        Unknown = 0,

        /// <summary>A solution file (.sln / .slnx) is loaded. Projects are available through <c>IVsSolution</c>.</summary>
        Solution = 1,

        /// <summary>Only a folder is open ("file system" / folder mode). No solution file, projects must be discovered on disk.</summary>
        Folder = 2,

        /// <summary>Nothing is open at all (no solution file, no folder).</summary>
        None = 3
    }

    /// <summary>
    /// How the project list of an overview was produced. Makes it clear whether the entries are
    /// loaded VS projects or project files that were found by scanning the workspace directory.
    /// </summary>
    public enum ProjectDiscoveryMode
    {
        /// <summary>Could not be determined.</summary>
        Unknown = 0,

        /// <summary>Projects were enumerated from the loaded solution (<c>IVsSolution.GetProjectEnum</c>).</summary>
        LoadedInSolution = 1,

        /// <summary>Project files were discovered by scanning the open folder on disk (e.g. *.csproj, *.vcxproj).</summary>
        ProjectFilesOnDisk = 2
    }
}
