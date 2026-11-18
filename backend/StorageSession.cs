using NKeeper.Abstractions;

namespace NKeeper.Plugins.Storage;

/// <summary>Opens the Storage tool on an <c>ssh</c> service: core connects and signs in, and the tool checks it's Linux.</summary>
public sealed class StorageSessionHandler(IConnectivity connectivity, IPluginAudit audit) : IStreamSessionHandler
{
    public async Task<IStreamSession> OpenAsync(StreamSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ssh = request.Credential is { } credential
            ? await connectivity.OpenSshAsync(request.Host, request.ServiceId, credential, cancellationToken)
            : throw new ConnectException(ConnectFailure.NoSecret, "Choose a credential, or type one, to open Storage.");
        try
        {
            return new StorageSession(ssh, audit, request.Host.Id, await Shell.NotLinuxAsync(ssh, "Storage", cancellationToken));
        }
        catch
        {
            await ssh.DisposeAsync();
            throw;
        }
    }
}

/// <summary>
/// The Storage tool (docs/architecture.md §7.27), ported from 1.x's <c>StorageController</c>: <c>status</c>, and
/// <c>action</c>, which runs one of <see cref="StorageActions"/> on what the host has now and answers with its storage
/// afterwards. Success is judged by that state, not by the tool's exit code.
/// </summary>
internal sealed class StorageSession : ToolSession
{
    private static readonly HashSet<string> Actions =
    [
        StorageActions.IscsiDiscover, StorageActions.IscsiLogin, StorageActions.IscsiLogout, StorageActions.IscsiRescan,
        StorageActions.MultipathReload, StorageActions.MultipathFlush, StorageActions.MultipathRename,
        StorageActions.DrbdUp, StorageActions.DrbdDown, StorageActions.DrbdPrimary, StorageActions.DrbdSecondary,
        StorageActions.DrbdConnect, StorageActions.DrbdDisconnect, StorageActions.DrbdAdjust,
    ];

    private readonly ISshConnection _ssh;
    private readonly IPluginAudit _audit;
    private readonly Guid _hostId;

    public StorageSession(ISshConnection ssh, IPluginAudit audit, Guid hostId, string? unsupported)
    {
        _ssh = ssh;
        _audit = audit;
        _hostId = hostId;
        Unsupported = unsupported;
    }

    protected override async Task HandleAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case "status":
                await ReplyAsync(request.Id, new { storage = await StorageHost.GetStatusAsync(_ssh, cancellationToken) }, cancellationToken);
                break;

            case "action":
                await ReplyAsync(request.Id, await RunAsync(request, cancellationToken), cancellationToken);
                break;

            default:
                throw new ToolException($"Storage doesn't know “{request.Type}”.");
        }
    }

    private async Task<object> RunAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        var action = request.RequiredText("action", 32);
        if (!Actions.Contains(action))
        {
            throw new ToolException("Choose an action.");
        }

        var target = request.Text("target", 223)?.Trim();
        var portal = request.Text("portal", 260);
        var name = request.Text("name", 128);
        var newName = request.Text("newName", 128)?.Trim();

        var before = await StorageHost.GetStatusAsync(_ssh, cancellationToken);
        var command = StorageHost.CommandFor(action, target, portal, name, before, newName);
        var output = await StorageHost.RunActionAsync(_ssh, command, cancellationToken);
        await _audit.RecordAsync("storage.action", _hostId, command.Description, cancellationToken);

        var after = await StorageHost.GetStatusAsync(_ssh, cancellationToken);
        if (StorageHost.Unachieved(action, target, portal, name, after, newName) is { } problem)
        {
            throw new ToolException($"{command.Description} didn't take effect: {problem}. {Printed(output)}");
        }

        return new { storage = after, message = Message(action, output, before, after) ?? StorageHost.Achieved(action, target, portal, name, after, newName) };
    }

    public override ValueTask DisposeAsync() => _ssh.DisposeAsync();

    /// <summary>What the tool printed, for an action that didn't take effect.</summary>
    private static string Printed(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4).ToList();
        return lines.Count == 0 ? "The tool printed nothing and reported success." : $"The tool printed: “{string.Join(" ", lines)}”";
    }

    /// <summary>Discovery's targets aren't obvious from the new state, so it says how many it found.</summary>
    private static string? Message(string action, string output, StorageStatus before, StorageStatus after)
    {
        if (action != StorageActions.IscsiDiscover)
        {
            return null;
        }

        var found = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        var added = (after.Iscsi?.Nodes.Count ?? 0) - (before.Iscsi?.Nodes.Count ?? 0);
        return found == 0
            ? "The portal answered, but offers no targets to this initiator."
            : $"Found {found} {(found == 1 ? "target" : "targets")}{(added > 0 ? $", {added} new" : "")}.";
    }
}
