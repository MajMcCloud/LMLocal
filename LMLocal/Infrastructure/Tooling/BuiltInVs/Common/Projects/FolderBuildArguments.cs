using System;

namespace LMLocal.Infrastructure.Tooling.BuiltInVs.Common.Projects
{
    /// <summary>
    /// Builds command-line arguments for building a single project file outside of a solution
    /// ("open a folder" mode). The workspace root is used as working directory, the project file
    /// is always passed as a quoted absolute path, and the configuration is sanitized so it
    /// cannot break or inject into the command line.
    /// </summary>
    internal static class FolderBuildArguments
    {
        /// <summary>Configuration used when the caller does not request one.</summary>
        public const string DefaultConfiguration = "Debug";

        private const string AllowedConfigurationChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-";

        /// <summary>
        /// Strips everything that is not safe in a configuration name; falls back to <see cref="DefaultConfiguration"/>.
        /// </summary>
        public static string SanitizeConfiguration(string value)
        {
            if (string.IsNullOrEmpty(value)) return DefaultConfiguration;

            var trimmed = value.Trim().Trim('"');
            if (trimmed.Length == 0) return DefaultConfiguration;

            var sb = new System.Text.StringBuilder(trimmed.Length);
            foreach (char c in trimmed)
            {
                if (AllowedConfigurationChars.IndexOf(c) >= 0)
                    sb.Append(c);
            }

            string result = sb.ToString().Trim();
            return result.Length == 0 ? DefaultConfiguration : result;
        }

        /// <summary>Arguments for `dotnet build` (SDK-style projects).</summary>
        public static string BuildDotnetArguments(string projectPath, string configuration = null)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                throw new ArgumentException("Project path must be a non-empty string.", nameof(projectPath));

            return $"build \"{projectPath}\" -c {SanitizeConfiguration(configuration)} --nologo -v minimal";
        }

        /// <summary>Arguments for `MSBuild.exe` (legacy / native projects).</summary>
        public static string BuildMsBuildArguments(string projectPath, string configuration = null)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                throw new ArgumentException("Project path must be a non-empty string.", nameof(projectPath));

            return $"\"{projectPath}\" /p:Configuration={SanitizeConfiguration(configuration)} /nologo /v:minimal";
        }
    }
}
