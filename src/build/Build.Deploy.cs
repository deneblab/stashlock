using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nuke.Common;
using Nuke.Common.Tools.Docker;
using Tmds.Ssh;

// Optional deployment to a private host: push the image to a registry, then pull it and
// restart the compose service over SSH. Every host-specific value is a parameter.
partial class Build
{
    [Parameter("Docker registry for PushImage, e.g. registry.example.com")]
    readonly string DockerRegistry;

    [Parameter("Docker registry username")]
    readonly string DockerUsername;

    [Parameter("Docker registry password")] [Secret]
    readonly string DockerPassword;

    [Parameter("SSH target for RestartRemote, e.g. user@host")]
    readonly string DeployTarget;

    [Parameter("Directory on the remote host that holds docker-compose.yml")]
    readonly string DeployComposeDir;

    [Parameter("Compose service to restart - Default is 'stashlock-server'")]
    readonly string DeployService = "stashlock-server";

    [Parameter("Expected SSH host key SHA256 fingerprint (as printed by ssh-keygen -lf). Without it the key is accepted with a warning.")]
    readonly string SshHostKeySha256;

    Target PushImage => _ => _
        .DependsOn(DockerBuild)
        .Requires(() => DockerRegistry)
        .Executes(() =>
        {
            var localImageName = $"{DockerImageName}:{AbcVersion.SemVersion}";
            var remoteImageName = $"{DockerRegistry}/{DockerImageName}:{AbcVersion.SemVersion}";
            var remoteImageLatest = $"{DockerRegistry}/{DockerImageName}:latest";

            if (!string.IsNullOrEmpty(DockerUsername) && !string.IsNullOrEmpty(DockerPassword))
            {
                Serilog.Log.Information($"Logging in to Docker registry: {DockerRegistry}");
                DockerTasks.DockerLogin(s => s
                    .SetServer(DockerRegistry)
                    .SetUsername(DockerUsername)
                    .SetPassword(DockerPassword)
                );
            }

            DockerTasks.DockerTag(s => s.SetSourceImage(localImageName).SetTargetImage(remoteImageName));
            DockerTasks.DockerTag(s => s.SetSourceImage(localImageName).SetTargetImage(remoteImageLatest));

            Serilog.Log.Information($"Pushing image to {DockerRegistry}");
            DockerTasks.DockerPush(s => s.SetName(remoteImageName));
            DockerTasks.DockerPush(s => s.SetName(remoteImageLatest));
            Serilog.Log.Information($"Image pushed successfully to {DockerRegistry}");
        });

    Target RestartRemote => _ => _
        .Requires(() => DeployTarget)
        .Requires(() => DeployComposeDir)
        .Executes(async () =>
        {
            Serilog.Log.Information($"Connecting to {DeployTarget}");
            var settings = new SshClientSettings(DeployTarget)
            {
                // Tailscale SSH (or an agent) handles authentication
                Credentials = [new NoCredential()],
                HostAuthentication = (HostAuthenticationContext context, CancellationToken _) =>
                {
                    var fingerprint = context.ConnectionInfo.ServerKey.Key.SHA256FingerPrint;
                    if (string.IsNullOrEmpty(SshHostKeySha256))
                    {
                        Serilog.Log.Warning($"Accepting unverified SSH host key {fingerprint}. Pass --ssh-host-key-sha256 to pin it.");
                        return new ValueTask<bool>(true);
                    }
                    var expected = SshHostKeySha256.Replace("SHA256:", "");
                    var ok = string.Equals(fingerprint.Replace("SHA256:", ""), expected, StringComparison.Ordinal);
                    if (!ok)
                        Serilog.Log.Error($"SSH host key mismatch: got {fingerprint}, expected {SshHostKeySha256}");
                    return new ValueTask<bool>(ok);
                }
            };

            using var client = new SshClient(settings);
            await client.ConnectAsync();
            Serilog.Log.Information("Connected");

            await Run(client, $"cd {Quote(DeployComposeDir)} && docker compose pull {DeployService} && docker compose up -d {DeployService}");
            var status = await Run(client, $"cd {Quote(DeployComposeDir)} && docker compose ps {DeployService} --format '{{{{.Status}}}}'");
            Serilog.Log.Information($"Service status: {status.Trim()}");
        });

    static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    static async Task<string> Run(SshClient client, string command)
    {
        Serilog.Log.Information($"$ {command}");
        using var process = await client.ExecuteAsync(command);
        var output = new StringBuilder();
        await foreach (var (_, line) in process.ReadAllLinesAsync())
            output.AppendLine(line);
        var exitCode = await process.GetExitCodeAsync();
        if (exitCode != 0)
            throw new Exception($"Remote command failed (exit code {exitCode}): {output}");
        return output.ToString();
    }
}
