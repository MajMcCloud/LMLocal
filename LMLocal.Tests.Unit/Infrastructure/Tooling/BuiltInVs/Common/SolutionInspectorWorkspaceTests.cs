using LMLocal.Infrastructure.Tooling.BuiltInVs.Common;
using Microsoft.VisualStudio.Shell.Interop;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LMLocal.Tests.Unit.Infrastructure.Tooling.BuiltInVs.Common
{
    [TestFixture]
    public class SolutionInspectorWorkspaceTests
    {
        private const int S_OK = 0;
        private const int E_NOTIMPL = unchecked((int)0x80004001);

        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "lmlocal-slninspector-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                // best effort cleanup
            }
        }

        // ---------- WorkspaceKind detection ----------

        [Test]
        public void DetermineWorkspaceKind_SolutionFilePresent_IsSolution()
        {
            var solution = new Mock<IVsSolution>();
            object value = false;
            solution.Setup(s => s.GetProperty(It.IsAny<int>(), out value)).Returns(E_NOTIMPL);

            var kind = SolutionInspector.DetermineWorkspaceKind(
                solution.Object,
                Path.Combine(_root, "My.sln"),
                _root);

            Assert.That(kind, Is.EqualTo(WorkspaceKind.Solution));
        }

        [Test]
        public void DetermineWorkspaceKind_OpenFolderMode_IsFolder()
        {
            var solution = new Mock<IVsSolution>();
            object value = true;
            solution.Setup(s => s.GetProperty(It.IsAny<int>(), out value)).Returns(S_OK);

            var kind = SolutionInspector.DetermineWorkspaceKind(solution.Object, null, _root);

            Assert.That(kind, Is.EqualTo(WorkspaceKind.Folder));
        }

        [Test]
        public void DetermineWorkspaceKind_NotOpenFolderModeAndNoSolution_IsNone()
        {
            var solution = new Mock<IVsSolution>();
            object value = false;
            solution.Setup(s => s.GetProperty(It.IsAny<int>(), out value)).Returns(S_OK);

            var kind = SolutionInspector.DetermineWorkspaceKind(solution.Object, null, _root);

            Assert.That(kind, Is.EqualTo(WorkspaceKind.None));
        }

        [Test]
        public void DetermineWorkspaceKind_PropertyUnavailable_FallsBackToDirectory()
        {
            var solution = new Mock<IVsSolution>();
            object value = null;
            solution.Setup(s => s.GetProperty(It.IsAny<int>(), out value)).Returns(E_NOTIMPL);

            Assert.That(
                SolutionInspector.DetermineWorkspaceKind(solution.Object, null, _root),
                Is.EqualTo(WorkspaceKind.Folder));

            Assert.That(
                SolutionInspector.DetermineWorkspaceKind(solution.Object, null, null),
                Is.EqualTo(WorkspaceKind.None));
        }

        [Test]
        public void DetermineWorkspaceKind_PropertyThrows_FallsBackToDirectory()
        {
            var solution = new Mock<IVsSolution>();
            object value = null;
            solution.Setup(s => s.GetProperty(It.IsAny<int>(), out value)).Throws(new InvalidOperationException("not supported"));

            Assert.That(
                SolutionInspector.DetermineWorkspaceKind(solution.Object, null, _root),
                Is.EqualTo(WorkspaceKind.Folder));
        }

        [Test]
        public void DetermineWorkspaceKind_NullSolutionWithFolderRoot_IsFolder()
        {
            // "open a folder" mode where the solution service could not be acquired:
            // a non-empty directory must still be treated as a folder workspace.
            Assert.That(
                SolutionInspector.DetermineWorkspaceKind(null, null, _root),
                Is.EqualTo(WorkspaceKind.Folder));
        }

        [Test]
        public void DetermineWorkspaceKind_NullSolutionWithoutRoot_IsNone()
        {
            Assert.That(
                SolutionInspector.DetermineWorkspaceKind(null, null, null),
                Is.EqualTo(WorkspaceKind.None));
        }

        [Test]
        public void DetermineWorkspaceKind_FolderModeReportingDirectoryAsSolutionFile_IsFolder()
        {
            // "open a folder" mode: GetSolutionInfo reports the open folder itself in the
            // solution-file out-parameter. That must not be mistaken for a loaded solution.
            var solution = new Mock<IVsSolution>();
            object value = true;
            solution.Setup(s => s.GetProperty(It.IsAny<int>(), out value)).Returns(S_OK);

            var kind = SolutionInspector.DetermineWorkspaceKind(solution.Object, _root, _root);

            Assert.That(kind, Is.EqualTo(WorkspaceKind.Folder));
        }

        [Test]
        public void DetermineWorkspaceKind_FolderModeWithProjectLoaded_IsFolder()
        {
            // A project was loaded inside the open folder: the solution-file out-parameter then
            // holds the project's directory (no .sln extension) — still a folder workspace.
            var solution = new Mock<IVsSolution>();
            object value = true;
            solution.Setup(s => s.GetProperty(It.IsAny<int>(), out value)).Returns(S_OK);

            string projectDirectory = Path.Combine(_root, "src", "MyApp");
            Directory.CreateDirectory(projectDirectory);

            var kind = SolutionInspector.DetermineWorkspaceKind(solution.Object, projectDirectory, _root);

            Assert.That(kind, Is.EqualTo(WorkspaceKind.Folder));
        }

        [Test]
        public void DetermineWorkspaceKind_FolderModeWithoutOpenFolderProperty_IsFolder()
        {
            // Shell without VSPROPID_IsInOpenFolderMode: a directory reported as "solution file"
            // must fall back to Folder, not Solution.
            var solution = new Mock<IVsSolution>();
            object value = null;
            solution.Setup(s => s.GetProperty(It.IsAny<int>(), out value)).Returns(E_NOTIMPL);

            var kind = SolutionInspector.DetermineWorkspaceKind(solution.Object, _root, _root);

            Assert.That(kind, Is.EqualTo(WorkspaceKind.Folder));
        }

        [Test]
        public void DetermineWorkspaceKind_NullSolutionWithDirectoryAsSolutionFile_IsFolder()
        {
            Assert.That(
                SolutionInspector.DetermineWorkspaceKind(null, _root, null),
                Is.EqualTo(WorkspaceKind.Folder));
        }

        // ---------- IsSolutionFilePath ----------

        [Test]
        public void IsSolutionFilePath_RealSolutionFile_IsTrue()
        {
            string solutionFile = Path.Combine(_root, "MyApp.sln");
            File.WriteAllText(solutionFile, "Microsoft Visual Studio Solution File");

            Assert.That(SolutionInspector.IsSolutionFilePath(solutionFile, _root), Is.True);
        }

        [Test]
        public void IsSolutionFilePath_SolutionXFile_IsTrue()
        {
            Assert.That(SolutionInspector.IsSolutionFilePath(Path.Combine(_root, "MyApp.slnx"), _root), Is.True);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(@"C:\work\MyApp")]
        [TestCase(@"C:\work\MyApp\MyApp.csproj")]
        [TestCase(@"C:\work\MyApp\MyApp.sln\")]
        public void IsSolutionFilePath_NonSolutionValues_AreFalse(string value)
        {
            Assert.That(SolutionInspector.IsSolutionFilePath(value, @"C:\work\MyApp"), Is.False);
        }

        [Test]
        public void IsSolutionFilePath_DirectoryNamedLikeSolution_IsFalse()
        {
            string directoryNamedLikeSolution = Path.Combine(_root, "MyApp.sln");
            Directory.CreateDirectory(directoryNamedLikeSolution);

            Assert.That(SolutionInspector.IsSolutionFilePath(directoryNamedLikeSolution, _root), Is.False);
        }

        // ---------- Directory pruning ----------

        [TestCase("bin", true)]
        [TestCase("obj", true)]
        [TestCase("node_modules", true)]
        [TestCase(".git", true)]
        [TestCase(".vs", true)]
        [TestCase("", true)]
        [TestCase(null, true)]
        [TestCase("src", false)]
        [TestCase("LMLocal.Tests.Unit", false)]
        public void ShouldSkipDirectory_KnownFolders(string name, bool expected)
        {
            Assert.That(SolutionInspector.ShouldSkipDirectory(name), Is.EqualTo(expected));
        }

        // ---------- Project file discovery on disk ----------

        [Test]
        public void DiscoverProjectFilesOnDisk_FindsProjectsAndPrunesGeneratedFolders()
        {
            WriteFile(Path.Combine(_root, "src", "App", "App.csproj"));
            WriteFile(Path.Combine(_root, "native", "Native.vcxproj"));
            WriteFile(Path.Combine(_root, "bin", "ShouldNotBeFound.csproj"));
            WriteFile(Path.Combine(_root, "node_modules", "pkg", "Ignored.csproj"));

            List<string> found = SolutionInspector.DiscoverProjectFilesOnDisk(_root, 200, out bool truncated);

            Assert.That(truncated, Is.False);
            Assert.That(found.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal),
                Is.EquivalentTo(new[] { "App.csproj", "Native.vcxproj" }));
        }

        [Test]
        public void DiscoverProjectFilesOnDisk_RespectsMaxProjectsAndFlagsTruncation()
        {
            WriteFile(Path.Combine(_root, "a", "A.csproj"));
            WriteFile(Path.Combine(_root, "b", "B.csproj"));
            WriteFile(Path.Combine(_root, "c", "C.csproj"));

            List<string> found = SolutionInspector.DiscoverProjectFilesOnDisk(_root, 2, out bool truncated);

            Assert.That(found.Count, Is.EqualTo(2));
            Assert.That(truncated, Is.True);
        }

        [Test]
        public void DiscoverProjectFilesOnDisk_MissingDirectory_ReturnsEmpty()
        {
            string missing = Path.Combine(_root, "does-not-exist");

            List<string> found = SolutionInspector.DiscoverProjectFilesOnDisk(missing, 200, out bool truncated);

            Assert.That(found, Is.Empty);
            Assert.That(truncated, Is.False);
        }

        // ---------- Source file counting on disk ----------

        [Test]
        public void CountSourceFilesOnDisk_CountsOnlySourceFilesAndPrunesGeneratedFolders()
        {
            string projectDir = Path.Combine(_root, "App");
            WriteFile(Path.Combine(projectDir, "Program.cs"));
            WriteFile(Path.Combine(projectDir, "Services", "Greeter.cs"));
            WriteFile(Path.Combine(projectDir, "notes.txt"));
            WriteFile(Path.Combine(projectDir, "App.csproj"));
            WriteFile(Path.Combine(projectDir, "obj", "Generated.cs"));
            WriteFile(Path.Combine(projectDir, "bin", "Debug", "Leak.cs"));

            Assert.That(SolutionInspector.CountSourceFilesOnDisk(projectDir, "C#"), Is.EqualTo(2));
        }

        [Test]
        public void CountSourceFilesOnDisk_MissingDirectory_ReturnsZero()
        {
            Assert.That(SolutionInspector.CountSourceFilesOnDisk(Path.Combine(_root, "nope"), "C#"), Is.EqualTo(0));
        }

        private static void WriteFile(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "// generated for tests");
        }
    }
}
