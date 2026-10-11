using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using LMLocal.Core.Common;

namespace LMLocal.Infrastructure.Tooling.BuiltInVs.Common.Projects
{
    /// <summary>
    /// Locates MSBuild.exe for workspaces that contain projects `dotnet build` cannot handle
    /// (legacy non-SDK .csproj, .vcxproj, ...). Resolution order:
    /// 1. LMLOCAL_MSBUILD_PATH environment variable
    /// 2. vswhere (installed VS instances, including Build Tools)
    /// The result is cached for the lifetime of the process; an empty string means "not available".
    /// </summary>
    internal static class MsBuildLocator
    {
        private const string EnvironmentVariableName = "LMLOCAL_MSBUILD_PATH";

        private static readonly object _syncRoot = new object();
        private static string _cachedPath;

        /// <summary>
        /// Full path to MSBuild.exe, or null when MSBuild cannot be located.
        /// Blocking (starts vswhere) — call from a background thread.
        /// </summary>
        public static string FindMsBuildPath()
        {
            lock (_syncRoot)
            {
                if (_cachedPath != null)
                    return _cachedPath.Length == 0 ? null : _cachedPath;

                _cachedPath = ResolveMsBuildPath() ?? string.Empty;
                return _cachedPath.Length == 0 ? null : _cachedPath;
            }
        }

        private static string ResolveMsBuildPath()
        {
            string fromEnvironment = TryResolveFromEnvironment();
            if (fromEnvironment != null)
                return fromEnvironment;

            return TryResolveFromVsWhere();
        }

        private static string TryResolveFromEnvironment()
        {
            try
            {
                string value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
                if (string.IsNullOrWhiteSpace(value)) return null;

                value = value.Trim().Trim('"');
                if (!File.Exists(value))
                {
                    InternalLogger.Warn($"MsBuildLocator: {EnvironmentVariableName} points to a missing file: {value}");
                    return null;
                }
                return value;
            }
            catch (Exception ex)
            {
                InternalLogger.Debug($"MsBuildLocator: environment lookup failed: {ex.Message}");
                return null;
            }
        }

        private static string TryResolveFromVsWhere()
        {
            try
            {
                string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (string.IsNullOrEmpty(programFilesX86)) return null;

                string vsWhere = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
                if (!File.Exists(vsWhere))
                {
                    InternalLogger.Debug("MsBuildLocator: vswhere.exe not found.");
                    return null;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = vsWhere,
                    Arguments = "-latest -prerelease -products * -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false
                };

                var output = new StringBuilder();

                using (var process = new Process { StartInfo = startInfo })
                {
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data)) output.AppendLine(e.Data);
                    };

                    try
                    {
                        process.Start();
                        process.BeginOutputReadLine();

                        if (!process.WaitForExit(10000))
                        {
                            TryKill(process);
                            InternalLogger.Warn("MsBuildLocator: vswhere timed out.");
                            return null;
                        }
                    }
                    catch (Exception ex)
                    {
                        InternalLogger.Warn($"MsBuildLocator: vswhere could not be started: {ex.Message}");
                        return null;
                    }
                }

                foreach (string line in output.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = line.Trim();
                    if (candidate.Length == 0) continue;
                    if (File.Exists(candidate)) return candidate;
                }

                InternalLogger.Debug("MsBuildLocator: vswhere returned no usable MSBuild path.");
                return null;
            }
            catch (Exception ex)
            {
                InternalLogger.Warn($"MsBuildLocator: vswhere lookup failed: {ex.Message}");
                return null;
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (Exception ex)
            {
                InternalLogger.Debug($"MsBuildLocator: killing vswhere failed: {ex.Message}");
            }
        }
    }
}
