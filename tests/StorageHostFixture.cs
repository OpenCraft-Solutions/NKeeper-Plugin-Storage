using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace NKeeper.Plugins.Storage.Tests;

/// <summary>
/// An OpenSSH server made to look like a storage host (ported from 1.x's StorageServerFixture): fake <c>iscsiadm</c>
/// (two known targets, one logged in), <c>multipath</c> (two maps) and <c>drbdadm</c>/<c>drbdsetup</c> (r0 up, r1
/// down). Like the real tools, the fakes need root, which the (non-root) test user gets with sudo. Actions change the
/// state, so each test starts from <see cref="ResetAsync"/>. A fake <c>uname</c> can say the host is Windows.
/// </summary>
public sealed class StorageHostFixture : IAsyncLifetime
{
    public const string UserName = "tester";
    public const string Password = "Storage-Test-Passw0rd";
    public const string Address = "127.0.0.1";
    private const int SshPort = 2222;

    private const UnixFileModes Executable = UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.UserExecute
        | UnixFileModes.GroupRead | UnixFileModes.GroupExecute | UnixFileModes.OtherRead | UnixFileModes.OtherExecute;

    private readonly IContainer _container = new ContainerBuilder("lscr.io/linuxserver/openssh-server:10.3_p1-r1-ls239")
        .WithHostname("san-test")
        .WithEnvironment("PASSWORD_ACCESS", "true")
        .WithEnvironment("USER_NAME", UserName)
        .WithEnvironment("USER_PASSWORD", Password)
        .WithPortBinding(SshPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(@"\[ls\.io-init\] done\."))
        .Build();

    public int Port => _container.GetMappedPublicPort(SshPort);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        foreach (var tool in new[] { "iscsiadm", "multipath", "multipathd", "drbdadm", "drbdsetup", "uname" })
        {
            await _container.CopyAsync(
                await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Storage", $"fake-{tool}.sh")), $"/usr/local/bin/{tool}", fileMode: Executable);
        }

        await ResetAsync();
    }

    /// <summary>Puts the storage, the test user's sudo and the fake <c>uname</c> back as they were.</summary>
    public async Task ResetAsync()
    {
        var setup = await _container.ExecAsync([
            "sh", "-c",
            "set -e; rm -rf /var/lib/fake-iscsi /var/lib/fake-multipath /var/lib/fake-drbd /var/lib/fake-linux /etc/multipath /etc/multipath.conf"
            + " && mkdir -p /etc/iscsi /var/lib/fake-iscsi /var/lib/fake-multipath /var/lib/fake-drbd /var/lib/fake-linux"
            + " && echo 'InitiatorName=iqn.1993-08.org.debian:01:san-test' > /etc/iscsi/initiatorname.iscsi && chmod 600 /etc/iscsi/initiatorname.iscsi"
            + " && printf '10.0.0.5:3260,1 iqn.2003-01.org.example.san:disk1\\n10.0.0.5:3260,1 iqn.2003-01.org.example.san:disk2\\n' > /var/lib/fake-iscsi/nodes"
            + " && echo '10.0.0.5:3260 iqn.2003-01.org.example.san:disk1' > /var/lib/fake-iscsi/sessions"
            + " && echo Secondary > /var/lib/fake-drbd/r0"
            + $" && echo '{UserName} ALL=(ALL) NOPASSWD: ALL' > /etc/sudoers.d/nkeeper-test && chmod 440 /etc/sudoers.d/nkeeper-test",
        ]);
        if (setup.ExitCode != 0)
        {
            throw new InvalidOperationException($"Preparing the fake storage host failed: {setup.Stderr}");
        }
    }

    /// <summary>Runs a shell script on the host as root; returns what it printed.</summary>
    public async Task<string> ExecAsync(string script)
    {
        var result = await _container.ExecAsync(["sh", "-c", script]);
        return result.ExitCode == 0 ? result.Stdout : throw new InvalidOperationException($"{script} failed: {result.Stderr}");
    }

    /// <summary>Makes iSCSI logins exit 0 without logging in.</summary>
    public Task MakeLoginsSilentAsync() => _container.ExecAsync(["touch", "/var/lib/fake-iscsi/silent"]);

    /// <summary>Takes away the test user's sudo, as on a host where it needs a password.</summary>
    public Task RemoveSudoAsync() => _container.ExecAsync(["rm", "-f", "/etc/sudoers.d/nkeeper-test"]);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>
/// The released ssh plugin, whose <c>ssh</c> service type the tool opens on: downloaded once, and checked against the
/// SHA-256 NKeeper's <c>deploy/plugins.lock</c> pins for it. It's signed with the official key, which the server trusts.
/// </summary>
public static class SshPluginRelease
{
    public const string Url = "https://github.com/OpenCraft-Solutions/NKeeper-Plugin-SSH/releases/download/v1.0.0/ssh-1.0.0.nkp";
    public const string Sha256 = "f8b43ee0181feba84f5281a55f3589676a8b8df78a1bc311c15777e30d63e113";

    private static readonly SemaphoreSlim Once = new(1, 1);

    public static async Task<string> PathAsync(CancellationToken cancellationToken)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "deps", "ssh-1.0.0.nkp");
        await Once.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path) || !Matches(await File.ReadAllBytesAsync(path, cancellationToken)))
            {
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(new Uri(Url), cancellationToken);
                if (!Matches(bytes))
                {
                    throw new InvalidOperationException($"{Url} doesn't match the SHA-256 pinned for it.");
                }

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            }

            return path;
        }
        finally
        {
            Once.Release();
        }
    }

    private static bool Matches(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes)) == Sha256;
}
