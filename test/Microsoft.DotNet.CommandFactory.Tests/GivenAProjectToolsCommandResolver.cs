// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.TestFramework;
using Microsoft.DotNet.Tools.Test.Utilities;
using NuGet.Frameworks;
using NuGet.ProjectModel;
using NuGet.Versioning;
using Xunit;
using Microsoft.DotNet.Tools.Tests.Utilities;
using Microsoft.DotNet.CommandFactory;
using LocalizableStrings = Microsoft.DotNet.CommandFactory.LocalizableStrings;
using Microsoft.DotNet.Tools.MSBuild;

namespace Microsoft.DotNet.Tests
{
    public class GivenAProjectToolsCommandResolver : TestBase
    {
        private static readonly NuGetFramework s_toolPackageFramework =
            NuGetFrameworks.NetCoreApp22;

        private const string TestProjectName = "AppWithToolDependency";

        [Fact]
        public void ItReturnsNullWhenCommandNameIsNull()
        {
            var projectToolsCommandResolver = SetupProjectToolsCommandResolver();

            var commandResolverArguments = new CommandResolverArguments()
            {
                CommandName = null,
                CommandArguments = new string[] { "" },
                ProjectDirectory = "/some/directory"
            };

            var result = projectToolsCommandResolver.Resolve(commandResolverArguments);

            result.Should().BeNull();
        }

        [Fact]
        public void ItReturnsNullWhenProjectDirectoryIsNull()
        {
            var projectToolsCommandResolver = SetupProjectToolsCommandResolver();

            var commandResolverArguments = new CommandResolverArguments()
            {
                CommandName = "command",
                CommandArguments = new string[] { "" },
                ProjectDirectory = null
            };

            var result = projectToolsCommandResolver.Resolve(commandResolverArguments);

            result.Should().BeNull();
        }

        [Fact]
        public void ItReturnsNullWhenProjectDirectoryDoesNotContainAProjectFile()
        {
            var projectToolsCommandResolver = SetupProjectToolsCommandResolver();

            var projectDirectory = TestAssets.CreateTestDirectory();

            var commandResolverArguments = new CommandResolverArguments()
            {
                CommandName = "command",
                CommandArguments = new string[] { "" },
                ProjectDirectory = projectDirectory.Root.FullName
            };

            var result = projectToolsCommandResolver.Resolve(commandResolverArguments);

            result.Should().BeNull();
        }

        [Fact]
        public void ItReturnsNullWhenCommandNameDoesNotExistInProjectTools()
        {
            var projectToolsCommandResolver = SetupProjectToolsCommandResolver();

            var testInstance = TestAssets.Get(TestProjectName)
                .CreateInstance()
                .WithSourceFiles()
                .WithRestoreFiles();

            var commandResolverArguments = new CommandResolverArguments()
            {
                CommandName = "nonexistent-command",
                CommandArguments = null,
                ProjectDirectory = testInstance.Root.FullName
            };

            var result = projectToolsCommandResolver.Resolve(commandResolverArguments);

            result.Should().BeNull();
        }

        //  Windows only due to CI failure during repo merge: https://github.com/dotnet/sdk/issues/3684
        [WindowsOnlyFact]
        public void ItReturnsACommandSpecWithDOTNETAsFileNameAndCommandNameInArgsWhenCommandNameExistsInProjectTools()
        {
            var projectToolsCommandResolver = SetupProjectToolsCommandResolver();

            var testInstance = TestAssets.Get(TestProjectName)
                .CreateInstance()
                .WithSourceFiles()
                .WithRestoreFiles();

            var commandResolverArguments = new CommandResolverArguments()
            {
                CommandName = "dotnet-portable",
                CommandArguments = null,
                ProjectDirectory = testInstance.Root.FullName
            };

            var result = projectToolsCommandResolver.Resolve(commandResolverArguments);

            result.Should().NotBeNull();

            var commandFile = Path.GetFileNameWithoutExtension(result.Path);

            commandFile.Should().Be("dotnet");

            result.Args.Should().Contain(commandResolverArguments.CommandName);
        }

        //  Windows only due to CI failure during repo merge: https://github.com/dotnet/sdk/issues/3684
        [WindowsOnlyFact]
        public void ItEscapesCommandArgumentsWhenReturningACommandSpec()
        {
            var projectToolsCommandResolver = SetupProjectToolsCommandResolver();

            var testInstance = TestAssets.Get(TestProjectName)
                .CreateInstance()
                .WithSourceFiles()
                .WithRestoreFiles();

            var commandResolverArguments = new CommandResolverArguments()
            {
                CommandName = "dotnet-portable",
                CommandArguments = new[] { "arg with space" },
                ProjectDirectory = testInstance.Root.FullName
            };

            var result = projectToolsCommandResolver.Resolve(commandResolverArguments);

            result.Should().NotBeNull("Because the command is a project tool dependency");
            result.Args.Should().Contain("\"arg with space\"");
        }

        //  Windows only due to CI failure during repo merge: https://github.com/dotnet/sdk/issues/3684
        [WindowsOnlyFact]
        public void ItReturnsACommandSpecWithArgsContainingCommandPathWhenReturningACommandSpecAndCommandArgumentsAreNull()
        {
            var projectToolsCommandResolver = SetupProjectToolsCommandResolver();

            var testInstance = TestAssets.Get(TestProjectName)
                .CreateInstance()
                .WithSourceFiles()
                .WithRestoreFiles();

            var commandResolverArguments = new CommandResolverArguments()
            {
                CommandName = "dotnet-portable",
                CommandArguments = null,
                ProjectDirectory = testInstance.Root.FullName
            };

            var result = projectToolsCommandResolver.Resolve(commandResolverArguments);

            result.Should().NotBeNull();

            var commandPath = result.Args.Trim('"');
            commandPath.Should().Contain("dotnet-portable.dll");
        }

        //  Windows only due to CI failure during repo merge: https://github.com/dotnet/sdk/issues/3684
        [WindowsOnlyFact]
        public void ItReturnsACommandSpecWithArgsContainingCommandPathWhenInvokingAToolReferencedWithADifferentCasing()
        {
            var projectToolsCommandResolver = SetupProjectToolsCommandResolver();

            var testInstance = TestAssets.Get(TestProjectName)
                .CreateInstance()
                .WithSourceFiles()
                .WithRestoreFiles();

            var commandResolverArguments = new CommandResolverArguments()
            {
                CommandName = "dotnet-prefercliruntime",
                CommandArguments = null,
                ProjectDirectory = testInstance.Root.FullName
            };

            var result = projectToolsCommandResolver.Resolve(commandResolverArguments);

            result.Should().NotBeNull();

            var commandPath = result.Args.Trim('"');
            commandPath.Should().Contain("dotnet-prefercliruntime.dll");
        }

        //  Windows only due to CI failure during repo merge: https://github.com/dotnet/sdk/issues/3684
        [WindowsOnlyFact]
        public void ItWritesADepsJsonFileNextToTheLockfile()
        {
            var projectToolsCommandResolver = SetupProjectToolsCommandResolver();

            var testInstance = TestAssets.Get(TestProjectName)
                .CreateInstance()
                .WithSourceFiles()
                .WithRepoGlobalPackages()
                .WithRestoreFiles();

            var commandResolverArguments = new CommandResolverArguments()
            {
                CommandName = "dotnet-portable",
                CommandArguments = null,
                ProjectDirectory = testInstance.Root.FullName
            };

            var nugetPackagesRoot = RepoDirectoriesProvider.TestGlobalPackagesFolder;

            var toolPathCalculator = new ToolPathCalculator(nugetPackagesRoot);

            var lockFilePath = toolPathCalculator.GetLockFilePath(
                "dotnet-portable",
                new NuGetVersion("1.0.0"),
                s_toolPackageFramework);

            var directory = Path.GetDirectoryName(lockFilePath);

            var depsJsonFile = Directory
                .EnumerateFiles(directory)
                .FirstOrDefault(p => Path.GetFileName(p).EndsWith(FileNameSuffixes.DepsJson));

            if (depsJsonFile != null)
            {
                File.Delete(depsJsonFile);
            }

            var result = projectToolsCommandResolver.Resolve(commandResolverArguments);

            result.Should().NotBeNull();

            new DirectoryInfo(directory)
                .Should().HaveFilesMatching("*.deps.json", SearchOption.TopDirectoryOnly);
        }

        [Fact]
        public void GenerateDepsJsonMethodDoesntOverwriteWhenDepsFileAlreadyExists()
        {
            var testInstance = TestAssets.Get(TestProjectName)
                .CreateInstance()
                .WithSourceFiles()
                .WithRepoGlobalPackages()
                .WithRestoreFiles();


            var toolPathCalculator = new ToolPathCalculator(RepoDirectoriesProvider.TestGlobalPackagesFolder);

            var lockFilePath = toolPathCalculator.GetLockFilePath(
                "dotnet-portable",
                new NuGetVersion("1.0.0"),
                s_toolPackageFramework);

            var lockFile = new LockFileFormat().Read(lockFilePath);

            // NOTE: We must not use the real deps.json path here as it will interfere with tests running in parallel.
            var depsJsonFile = Path.GetTempFileName();
            File.WriteAllText(depsJsonFile, "temp");

            var projectToolsCommandResolver = SetupProjectToolsCommandResolver();
            projectToolsCommandResolver.GenerateDepsJsonFile(
                lockFile,
                s_toolPackageFramework,
                depsJsonFile,
                new SingleProjectInfo("dotnet-portable", "1.0.0", Enumerable.Empty<ResourceAssemblyInfo>()),
                GetToolDepsJsonGeneratorProject());

            File.ReadAllText(depsJsonFile).Should().Be("temp");
            File.Delete(depsJsonFile);
        }

        //  Windows only due to CI failure during repo merge: https://github.com/dotnet/sdk/issues/3684
        [WindowsOnlyFact]
        public void ItDoesNotAddFxVersionAsAParamWhenTheToolDoesNotHaveThePrefercliruntimeFile()
        {
            var projectToolsCommandResolver = SetupProjectToolsCommandResolver();

            var testInstance = TestAssets.Get(TestProjectName)
                .CreateInstance()
                .WithSourceFiles()
                .WithRestoreFiles();

            var commandResolverArguments = new CommandResolverArguments()
            {
                CommandName = "dotnet-portable",
                CommandArguments = null,
                ProjectDirectory = testInstance.Root.FullName
            };

            var result = projectToolsCommandResolver.Resolve(commandResolverArguments);

            result.Should().NotBeNull();

            result.Args.Should().NotContain("--fx-version");
        }

        [Fact]
        public void ItFindsToolsLocatedInTheNuGetFallbackFolder()
        {
            var testInstance = TestAssets.Get("AppWithFallbackFolderToolDependency")
                .CreateInstance("NF") // use shorter name since path could be too long
                .WithSourceFiles()
                .WithNuGetConfig(RepoDirectoriesProvider.TestPackages);
            var testProjectDirectory = testInstance.Root.FullName;
            var fallbackFolder = Path.Combine(testProjectDirectory, "fallbackFolder");

            PopulateFallbackFolder(testProjectDirectory, fallbackFolder);

            var nugetConfig = UseNuGetConfigWithFallbackFolder(testInstance, fallbackFolder);

            new RestoreCommand()
                .WithWorkingDirectory(testProjectDirectory)
                .Execute($"--configfile {nugetConfig}")
                .Should()
                .Pass();

            new DotnetCommand()
            .WithWorkingDirectory(testProjectDirectory)
            .Execute($"fallbackfoldertool").Should().Pass();
        }

        [Fact]
        public void ItShowsAnErrorWhenTheToolDllIsNotFound()
        {
            var testInstance = TestAssets.Get("AppWithFallbackFolderToolDependency")
                .CreateInstance("DN") // use shorter name since path could be too long
                .WithSourceFiles()
                .WithNuGetConfig(RepoDirectoriesProvider.TestPackages);
            var testProjectDirectory = testInstance.Root.FullName;
            var fallbackFolder = Path.Combine(testProjectDirectory, "fallbackFolder");
            var nugetPackages = Path.Combine(testProjectDirectory, "nugetPackages");

            PopulateFallbackFolder(testProjectDirectory, fallbackFolder);

            var nugetConfig = UseNuGetConfigWithFallbackFolder(testInstance, fallbackFolder);

            new RestoreCommand()
                .WithWorkingDirectory(testProjectDirectory)
                .Execute($"--configfile {nugetConfig} /p:RestorePackagesPath={nugetPackages}")
                .Should()
                .Pass();

            // We need to run the tool once to generate the deps.json
            // otherwise we end up with a different error message.

            new DotnetCommand()
            .WithWorkingDirectory(testProjectDirectory)
            .Execute($"fallbackfoldertool /p:RestorePackagesPath={nugetPackages}").Should().Pass();

            Directory.Delete(Path.Combine(fallbackFolder, "dotnet-fallbackfoldertool"), true);

            new DotnetCommand()
            .WithWorkingDirectory(testProjectDirectory)
            .Execute($"fallbackfoldertool /p:RestorePackagesPath={nugetPackages}")
            .Should().Fail().And.NotHaveStdOutContaining(string.Format(LocalizableStrings.CommandAssembliesNotFound, "dotnet-fallbackfoldertool"));
        }

        private void PopulateFallbackFolder(string testProjectDirectory, string fallbackFolder)
        {
            var nugetConfigPath = Path.Combine(testProjectDirectory, "NuGet.Config");
            new RestoreCommand()
                .WithWorkingDirectory(testProjectDirectory)
                .Execute($"--configfile {nugetConfigPath} --packages {fallbackFolder}")
                .Should()
                .Pass();

            Directory.Delete(Path.Combine(fallbackFolder, ".tools"), true);
        }

        private string UseNuGetConfigWithFallbackFolder(TestAssetInstance testInstance, string fallbackFolder)
        {
            var nugetConfig = testInstance.Root.GetFile("NuGet.Config").FullName;
            File.WriteAllText(
                nugetConfig,
                $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <configuration>
                  <fallbackPackageFolders>
                        <add key=""MachineWide"" value=""{fallbackFolder}""/>
                    </fallbackPackageFolders>
                </configuration>
                ");

            return nugetConfig;
        }

        private ProjectToolsCommandResolver SetupProjectToolsCommandResolver()
        {
            Environment.SetEnvironmentVariable(
                Constants.MSBUILD_EXE_PATH,
                Path.Combine(RepoDirectoriesProvider.SdkFolderUnderTest, "MSBuild.dll"));

            Environment.SetEnvironmentVariable(
                "MSBuildSDKsPath",
                Path.Combine(RepoDirectoriesProvider.SdkFolderUnderTest, "Sdks"));

            MSBuildForwardingAppWithoutLogging.MSBuildExtensionsPathTestHook = RepoDirectoriesProvider.SdkFolderUnderTest;

            var packagedCommandSpecFactory = new PackagedCommandSpecFactoryWithCliRuntime();

            var projectToolsCommandResolver =
                new ProjectToolsCommandResolver(packagedCommandSpecFactory, new EnvironmentProvider());

            return projectToolsCommandResolver;
        }

        private string GetToolDepsJsonGeneratorProject()
        {
            //  When using the product, the ToolDepsJsonGeneratorProject property is used to get this path, but for testing
            //  we'll hard code the path inside the SDK since we don't have a project to evaluate here
            return Path.Combine(RepoDirectoriesProvider.SdkFolderUnderTest, "Sdks", "Microsoft.NET.Sdk", "targets", "GenerateDeps", "GenerateDeps.proj");
        }
    }
}
