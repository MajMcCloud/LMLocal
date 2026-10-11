using LMLocal.Infrastructure.Tooling.BuiltInVs.Common.Projects;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LMLocal.Tests.Unit.Infrastructure.Tooling.BuiltInVs.Common.Projects
{
    [TestFixture]
    public class FolderProjectBuildSupportTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "lmlocal-folderbuild-" + Guid.NewGuid().ToString("N"));
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

        // ---------- FolderBuildArguments ----------

        [Test]
        public void BuildDotnetArguments_DefaultsToDebugAndQuotesPath()
        {
            var args = FolderBuildArguments.BuildDotnetArguments(@"C:\work\src\App\App.csproj");

            Assert.That(args, Does.StartWith("build \"C:\\work\\src\\App\\App.csproj\""));
            Assert.That(args, Does.Contain("-c Debug"));
        }

        [Test]
        public void BuildDotnetArguments_UsesRequestedConfiguration()
        {
            var args = FolderBuildArguments.BuildDotnetArguments(@"C:\work\App.csproj", "Release");

            Assert.That(args, Does.Contain("-c Release"));
        }

        [Test]
        public void BuildMsBuildArguments_QuotesPathAndPassesConfiguration()
        {
            var args = FolderBuildArguments.BuildMsBuildArguments(@"C:\work\App.csproj", "Release");

            Assert.That(args, Does.StartWith("\"C:\\work\\App.csproj\""));
            Assert.That(args, Does.Contain("/p:Configuration=Release"));
        }

        [TestCase(null, "Debug")]
        [TestCase("", "Debug")]
        [TestCase("   ", "Debug")]
        [TestCase("Debug", "Debug")]
        [TestCase("Release|x64\"", "Releasex64")]
        [TestCase("--extra flag", "--extraflag")]
        [TestCase("###", "Debug")]
        public void SanitizeConfiguration_RemovesUnsafeCharacters(string input, string expected)
        {
            Assert.That(FolderBuildArguments.SanitizeConfiguration(input), Is.EqualTo(expected));
        }

        [Test]
        public void BuildDotnetArguments_EmptyPath_Throws()
        {
            Assert.That(() => FolderBuildArguments.BuildDotnetArguments(" "), Throws.ArgumentException);
        }

        // ---------- buildability ----------

        [TestCase("App.csproj", true)]
        [TestCase("App.vbproj", true)]
        [TestCase("App.fsproj", true)]
        [TestCase("Native.vcxproj", false)]
        [TestCase("Legacy.wixproj", false)]
        [TestCase("Script.pyproj", false)]
        [TestCase("notes.txt", false)]
        [TestCase(null, false)]
        public void IsBuildableProjectFile_ManagedProjectsWithoutSolution(string file, bool expected)
        {
            Assert.That(FolderProjectSelector.IsBuildableProjectFile(file), Is.EqualTo(expected));
        }

        [TestCase("Native.vcxproj", true)]
        [TestCase("App.csproj", false)]
        [TestCase("notes.txt", false)]
        public void IsKnownNonBuildableProjectFile_RecognizedButUnsupported(string file, bool expected)
        {
            Assert.That(FolderProjectSelector.IsKnownNonBuildableProjectFile(file), Is.EqualTo(expected));
        }

        // ---------- discovery ----------

        [Test]
        public void Discover_FindsProjectFilesAndPrunesGeneratedFolders()
        {
            CreateFile(@"src\App\App.csproj");
            CreateFile(@"src\Lib\Lib.vbproj");
            CreateFile(@"native\Native.vcxproj");
            CreateFile(@"src\App\obj\Generated.csproj");
            CreateFile(@"src\App\bin\Debug\Published.csproj");
            CreateFile(@"src\App\README.md");

            bool truncated;
            var found = FolderProjectSelector.Discover(_root, out truncated);

            Assert.That(truncated, Is.False);
            Assert.That(found.Select(p => p.RelativePath), Is.EquivalentTo(new[]
            {
                Path.Combine("native", "Native.vcxproj"),
                Path.Combine("src", "App", "App.csproj"),
                Path.Combine("src", "Lib", "Lib.vbproj")
            }));
        }

        [Test]
        public void Discover_MarksBuildabilityPerExtension()
        {
            CreateFile(@"src\App\App.csproj");
            CreateFile(@"native\Native.vcxproj");

            bool truncated;
            var found = FolderProjectSelector.Discover(_root, out truncated);

            Assert.That(found.Single(p => p.Extension == ".csproj").IsBuildable, Is.True);
            Assert.That(found.Single(p => p.Extension == ".vcxproj").IsBuildable, Is.False);
        }

        [Test]
        public void Discover_RespectsMaxProjectsAndFlagsTruncation()
        {
            for (int i = 0; i < 5; i++)
                CreateFile($@"src\P{i}\P{i}.csproj");

            bool truncated;
            var found = FolderProjectSelector.Discover(_root, out truncated, maxProjects: 2);

            Assert.That(found, Has.Count.EqualTo(2));
            Assert.That(truncated, Is.True);
        }

        [Test]
        public void Discover_MissingRoot_ReturnsEmpty()
        {
            bool truncated;
            var found = FolderProjectSelector.Discover(Path.Combine(_root, "does-not-exist"), out truncated);

            Assert.That(found, Is.Empty);
            Assert.That(truncated, Is.False);
        }

        // ---------- selection ----------

        [Test]
        public void Select_NoProjectName_SelectsOnlyBuildableProjects()
        {
            CreateFile(@"src\App\App.csproj");
            CreateFile(@"native\Native.vcxproj");

            var discovered = DiscoverAll();
            var selection = FolderProjectSelector.Select(discovered, null, _root);

            Assert.That(selection.Selected.Select(p => p.Name), Is.EquivalentTo(new[] { "App" }));
            Assert.That(selection.Skipped, Is.EquivalentTo(new[] { Path.Combine("native", "Native.vcxproj") }));
        }

        [Test]
        public void Select_ByProjectName_MatchesSingleProject()
        {
            CreateFile(@"src\App\App.csproj");
            CreateFile(@"src\Lib\Lib.csproj");

            var selection = FolderProjectSelector.Select(DiscoverAll(), "App", _root);

            Assert.That(selection.Selected, Has.Count.EqualTo(1));
            Assert.That(selection.Selected[0].Name, Is.EqualTo("App"));
            Assert.That(selection.AmbiguousMatches, Is.Empty);
        }

        [Test]
        public void Select_ByProjectFileName_MatchesSingleProject()
        {
            CreateFile(@"src\App\App.csproj");
            CreateFile(@"src\Lib\Lib.csproj");

            var selection = FolderProjectSelector.Select(DiscoverAll(), "Lib.csproj", _root);

            Assert.That(selection.Selected, Has.Count.EqualTo(1));
            Assert.That(selection.Selected[0].Name, Is.EqualTo("Lib"));
        }

        [Test]
        public void Select_ByRelativePath_MatchesSingleProject()
        {
            CreateFile(@"src\App\App.csproj");
            CreateFile(@"other\App\App.csproj");

            var selection = FolderProjectSelector.Select(DiscoverAll(), Path.Combine("src", "App", "App.csproj"), _root);

            Assert.That(selection.Selected, Has.Count.EqualTo(1));
            Assert.That(selection.Selected[0].RelativePath, Is.EqualTo(Path.Combine("src", "App", "App.csproj")));
        }

        [Test]
        public void Select_ByAbsolutePath_MatchesSingleProject()
        {
            string projectFile = CreateFile(@"src\App\App.csproj");

            var selection = FolderProjectSelector.Select(DiscoverAll(), projectFile, _root);

            Assert.That(selection.Selected, Has.Count.EqualTo(1));
        }

        [Test]
        public void Select_AmbiguousName_ReportsAllMatches()
        {
            CreateFile(@"src\App\App.csproj");
            CreateFile(@"other\App\App.csproj");

            var selection = FolderProjectSelector.Select(DiscoverAll(), "App", _root);

            Assert.That(selection.Selected, Is.Empty);
            Assert.That(selection.AmbiguousMatches, Has.Count.EqualTo(2));
        }

        [Test]
        public void Select_UnknownProject_ReturnsEmptySelection()
        {
            CreateFile(@"src\App\App.csproj");

            var selection = FolderProjectSelector.Select(DiscoverAll(), "DoesNotExist", _root);

            Assert.That(selection.Selected, Is.Empty);
            Assert.That(selection.NonBuildableSelected, Is.Empty);
            Assert.That(selection.AmbiguousMatches, Is.Empty);
        }

        [Test]
        public void Select_NativeProjectIsMatchedButNotBuildable()
        {
            CreateFile(@"native\Native.vcxproj");

            var selection = FolderProjectSelector.Select(DiscoverAll(), "Native", _root);

            Assert.That(selection.Selected, Is.Empty);
            Assert.That(selection.NonBuildableSelected, Is.EquivalentTo(new[] { Path.Combine("native", "Native.vcxproj") }));
        }

        [Test]
        public void Select_SkippedListContainsNonBuildableProjectTypes()
        {
            CreateFile(@"src\App\App.csproj");
            CreateFile(@"native\Native.vcxproj");
            CreateFile(@"scripts\Tool.pyproj");

            var selection = FolderProjectSelector.Select(DiscoverAll(), null, _root);

            Assert.That(selection.Selected, Has.Count.EqualTo(1));
            Assert.That(selection.Skipped, Has.Count.EqualTo(2));
        }

        private List<FolderProject> DiscoverAll()
        {
            bool truncated;
            return FolderProjectSelector.Discover(_root, out truncated);
        }

        private string CreateFile(string relativePath)
        {
            string fullPath = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            File.WriteAllText(fullPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            return fullPath;
        }
    }
}
