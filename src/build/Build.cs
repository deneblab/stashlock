using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deneblab.AbcVersion;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Docker.DockerTasks;

partial class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode
    readonly DateTime BuildDate = DateTime.UtcNow;
    public static int Main() => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution Solution;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath ServerDirectory => SourceDirectory / "StashLock.Server";
    AbsolutePath TestsProject => SourceDirectory / "StashLock.Tests" / "StashLock.Tests.csproj";

    AbsolutePath TmpBuild => TemporaryDirectory / "w";

    Project CliProject => Solution.GetProject("StashLock.Cli");

    string DockerImageName => "stashlock-server";

    AbcVersion AbcVersion => AbcVersionFactory.CreateOneBuilder()
        .SetDateTime(BuildDate)
        .SetRepositoryRoot(RootDirectory)
        .Build(); // Creates new instance

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(d => d.DeleteDirectory());
            ArtifactsDirectory.CreateOrCleanDirectory();
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    // .NET projects only; the Go server is built by GoBuild
    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(s => s
                .SetProjectFile(TestsProject)
                .SetConfiguration(Configuration)
                .EnableNoRestore()
                .EnableNoBuild());
        });

    Target GoBuild => _ => _
        .Executes(() =>
        {
            var output = ArtifactsDirectory / "server" / (OperatingSystem.IsWindows() ? "stashlock-server.exe" : "stashlock-server");
            Go($"build -ldflags \"-s -w -X github.com/deneblab/stashlock-servergo/config.BuildVersion={AbcVersion.SemVersion}\" -o \"{output}\" .");
            Serilog.Log.Information($"Server binary: {output}");
        });

    Target GoTest => _ => _
        .Executes(() =>
        {
            Go("vet ./...");
            Go("test -race ./...");
        });

    // Everything CI runs, locally
    Target TestAll => _ => _
        .DependsOn(Test, GoTest);

    void Go(string arguments) =>
        ProcessTasks.StartProcess("go", arguments, ServerDirectory).AssertZeroExitCode();

    Target BuildCli => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            var outDir = TmpBuild / CliProject.Name;
            outDir.CreateOrCleanDirectory();

            DotNetPublish(o => o
                .SetProject(CliProject.Path)
                .EnableNoRestore()
                .SetConfiguration(Configuration)
                .SetOutput(outDir)
                .SetSelfContained(false)
                .SetVersion(AbcVersion.SemVersion)
                .SetFileVersion(AbcVersion.SemVersion)
                .SetAssemblyVersion(AbcVersion.SemVersion)
                .SetInformationalVersion(AbcVersion.InformationalVersion)
            );

            // Copy published output to artifacts directory
            ArtifactsDirectory.CreateOrCleanDirectory();
            outDir.CopyToDirectory(ArtifactsDirectory);
        });

    Target PackCliTool => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            ArtifactsDirectory.CreateOrCleanDirectory();

            DotNetPack(s => s
                .SetProject(CliProject.Path)
                .SetConfiguration(Configuration)
                .EnableNoRestore()
                .EnableNoBuild()
                .SetVersion(AbcVersion.SemVersion)
                .SetOutputDirectory(ArtifactsDirectory)
            );
        });

    Target DockerBuild => _ => _
        .Executes(() =>
        {
            var imageName = $"{DockerImageName}:{AbcVersion.SemVersion}";
            var imageNameLatest = $"{DockerImageName}:latest";

            Serilog.Log.Information($"Building Docker image {imageName} from {ServerDirectory}");

            DockerTasks.DockerBuild(s => s
                .SetPath(ServerDirectory)
                .SetFile(ServerDirectory / "Dockerfile")
                .SetTag(imageName)
                .SetBuildArg($"VERSION={AbcVersion.SemVersion}")
            );

            DockerTasks.DockerTag(s => s
                .SetSourceImage(imageName)
                .SetTargetImage(imageNameLatest)
            );

            Serilog.Log.Information($"Docker image built successfully: {imageName}");
        });
}
