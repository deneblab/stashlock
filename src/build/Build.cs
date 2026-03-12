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
using Tmds.Ssh;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Docker.DockerTasks;

class Build : NukeBuild
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

    [Parameter("Docker registry URL for pushing images")]
    readonly string DockerRegistry = "mint01.cat-company.ts.net";

    [Parameter("Docker registry username")]
    readonly string DockerUsername;

    [Parameter("Docker registry password")]
    readonly string DockerPassword;


    [Parameter("Docker container name on mint01")]
    readonly string ContainerName = "piratyhealth";

    [Solution] readonly Solution Solution;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    AbsolutePath TmpBuild => TemporaryDirectory / "w";

    Project CliProject => Solution.GetProject("StashLock.Cli");
    Project ServerProject => Solution.GetProject("StashLock.Server");

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

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target BuildCli => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            var outDir = TmpBuild / CliProject.Name;
            outDir.CreateOrCleanDirectory();

            DotNetRestore(s => s
                .SetProjectFile(CliProject));
                ;

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
            //outDir.GlobFiles("*").ForEach(f => f.Copy(ArtifactsDirectory / f.Name));
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
            var contextPath = ServerProject.Directory;
            var dockerfilePath = contextPath / "Dockerfile";
            var imageName = $"{DockerImageName}:{AbcVersion.SemVersion}";
            var imageNameLatest = $"{DockerImageName}:latest";
            var version = AbcVersion.SemVersion;
            var informationalVersion = AbcVersion.InformationalVersion;

            Serilog.Log.Information($"Building Docker image: {imageName}");
            Serilog.Log.Information($"Context: {contextPath}");
            Serilog.Log.Information($"Dockerfile: {dockerfilePath}");
            Serilog.Log.Information($"Version: {version}");
            Serilog.Log.Information($"InformationalVersion: {informationalVersion}");

            DockerTasks.DockerBuild(s => s
                .SetPath(contextPath)
                .SetFile(dockerfilePath)
                .SetTag(imageName)
                .SetBuildArg($"Version={version}", $"InformationalVersion={informationalVersion}")
            );

            DockerTasks.DockerTag(s => s
                .SetSourceImage(imageName)
                .SetTargetImage(imageNameLatest)
            );

            Serilog.Log.Information($"Docker image built successfully: {imageName}");
        });

    Target PushToMint01 => _ => _
        .DependsOn(DockerBuild)
        .Executes(() =>
        {
            var localImageName = $"{DockerImageName}:{AbcVersion.SemVersion}";
            var remoteImageName = $"{DockerRegistry}/{DockerImageName}:{AbcVersion.SemVersion}";
            var remoteImageLatest = $"{DockerRegistry}/{DockerImageName}:latest";
        
            // Login to registry if credentials provided
            if (!string.IsNullOrEmpty(DockerUsername) && !string.IsNullOrEmpty(DockerPassword))
            {
                Serilog.Log.Information($"Logging in to Docker registry: {DockerRegistry}");
                DockerTasks.DockerLogin(s => s
                    .SetServer(DockerRegistry)
                    .SetUsername(DockerUsername)
                    .SetPassword(DockerPassword)
                );
            }

            // Tag image for remote registry
            Serilog.Log.Information($"Tagging image for registry: {remoteImageName}");
            DockerTasks.DockerTag(s => s
                .SetSourceImage(localImageName)
                .SetTargetImage(remoteImageName)
            );

            DockerTasks.DockerTag(s => s
                .SetSourceImage(localImageName)
                .SetTargetImage(remoteImageLatest)
            );

            // Push to registry
            Serilog.Log.Information($"Pushing image to {DockerRegistry}");
            DockerTasks.DockerPush(s => s
                .SetName(remoteImageName)
            );

            DockerTasks.DockerPush(s => s
                .SetName(remoteImageLatest)
            );

            Serilog.Log.Information($"Image pushed successfully to {DockerRegistry}");
        });

    Target RestartOnMint01 => _ => _
        .Executes(async () =>
        {


            var SshHost = "mint01.cat-company.ts.net";
            var SshUsername = "pkudrel";
            Serilog.Log.Information($"Connecting to {SshHost} as {SshUsername} (Tailscale SSH)");
            var adress = $"{SshUsername}@{SshHost}";
            // Tailscale SSH handles authentication automatically
            var settings = new SshClientSettings(adress)
            {

                Credentials = [new NoCredential()]
            };

            // Auto-accept Tailscale host keys (they change dynamically)
            settings.HostAuthentication = (HostAuthenticationContext context, CancellationToken cancellationToken) =>
            {
                Serilog.Log.Information($"Accepting Tailscale host key");
                return new ValueTask<bool>(true);
            };

            using var client = new SshClient(settings);
            await client.ConnectAsync();

            Serilog.Log.Information("Connected successfully");

            // Pull latest image
            Serilog.Log.Information($"Pulling latest Docker image from {DockerRegistry}...");
            var pullCommand = await client.ExecuteAsync($"docker pull {DockerRegistry}/{DockerImageName}:latest");
            var pullOutput = new StringBuilder();
            await foreach (var (isError, line) in pullCommand.ReadAllLinesAsync())
            {
                pullOutput.AppendLine(line);
            }
            var pullExitCode = await pullCommand.GetExitCodeAsync();
            Serilog.Log.Information(pullExitCode == 0 ? "Image pulled successfully" : $"Pull completed with code: {pullExitCode}");

            // Remove old container manually and recreate with docker compose
            Serilog.Log.Information($"Removing old container manually...");
            var rmCommand = await client.ExecuteAsync($"docker rm -f {ContainerName}");
            await foreach (var (isError, line) in rmCommand.ReadAllLinesAsync()) { }

            Serilog.Log.Information($"Creating new container with docker compose...");
            var composeCommand = await client.ExecuteAsync($"cd /home/pkudrel/work/apps/test && docker compose pull test && docker compose up -d test");
            var composeOutput = new StringBuilder();
            await foreach (var (isError, line) in composeCommand.ReadAllLinesAsync())
            {
                composeOutput.AppendLine(line);
            }

            var composeExitCode = await composeCommand.GetExitCodeAsync();
            if (composeExitCode == 0)
            {
                Serilog.Log.Information($"Container updated successfully with new image");
            }
            else
            {
                throw new Exception($"Failed to update container (exit code {composeExitCode}): {composeOutput}");
            }

            // Check container status
            Serilog.Log.Information("Checking container status...");
            var statusCommand = await client.ExecuteAsync($"docker ps --filter name={ContainerName} --format '{{{{.Status}}}}'");
            var statusOutput = new StringBuilder();
            await foreach (var (isError, line) in statusCommand.ReadAllLinesAsync())
            {
                statusOutput.AppendLine(line);
            }
            Serilog.Log.Information($"Container status: {statusOutput.ToString().Trim()}");
        });

}
