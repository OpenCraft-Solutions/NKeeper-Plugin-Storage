using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NKeeper.Testing;
using Npgsql;

namespace NKeeper.Plugins.Storage.Tests;

/// <summary>
/// docs/architecture.md §7.27: the real <c>storage</c> plugin, packaged by its build and loaded with the released ssh
/// plugin into NKeeper's test server, reading and changing a (fake) host's iSCSI sessions, multipath maps and DRBD
/// resources over core's stream sessions. Ported from 1.x's <c>StorageTests</c>.
/// </summary>
public sealed class StoragePluginTests(PostgresFixture postgres, StorageHostFixture san) : IClassFixture<PostgresFixture>, IClassFixture<StorageHostFixture>, IAsyncLifetime
{
    private const string Disk1 = "iqn.2003-01.org.example.san:disk1";
    private const string Disk2 = "iqn.2003-01.org.example.san:disk2";
    private const string Disk3 = "iqn.2003-01.org.example.san:disk3";
    private const string BobPassword = "Bob-Passw0rd-123";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private TestServer _server = null!;
    private string _cookie = string.Empty;
    private string _connectionString = string.Empty;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await san.ResetAsync();
        _connectionString = await postgres.CreateDatabaseAsync();
        var options = new TestServerOptions { ConnectionString = _connectionString };
        foreach (var package in DevelopmentKeys.BuiltPackages())
        {
            options.PluginPackages.Add(package);
        }

        options.PluginPackages.Add(await SshPluginRelease.PathAsync(Token));
        _server = await TestServer.StartAsync(options, Token);
        _cookie = await SignInAsync(TestServer.AdminUserName, TestServer.AdminPassword);
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task The_plugin_contributes_its_tool_on_ssh_services()
    {
        using var admin = await _server.SignedInClientAsync(Token);
        var plugins = await admin.Http.GetFromJsonAsync<JsonElement>(new Uri("/api/plugins", UriKind.Relative), Token);
        var plugin = plugins.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "storage");
        Assert.Equal("loaded", plugin.GetProperty("state").GetString());

        var type = Assert.Single(plugin.GetProperty("sessionTypes").EnumerateArray());
        Assert.Equal(
            ("storage", "Storage", "ssh"),
            (type.GetProperty("id").GetString(), type.GetProperty("label").GetString(), Assert.Single(type.GetProperty("serviceTypes").EnumerateArray()).GetString()));
        Assert.Equal("storage.open", Assert.Single(plugin.GetProperty("hostActions").EnumerateArray()).GetProperty("id").GetString());

        using var module = await admin.Http.GetAsync(new Uri(plugin.GetProperty("frontendEntry").GetString()!, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, module.StatusCode);
    }

    [Fact]
    public async Task Status_reads_iscsi_multipath_and_drbd()
    {
        await using var tool = await OpenAsync();
        var storage = Storage(Data(await tool.RequestAsync("status")));

        var iscsi = storage.Iscsi!;
        Assert.Equal("iqn.1993-08.org.debian:01:san-test", iscsi.InitiatorName);
        var session = Assert.Single(iscsi.Sessions);
        Assert.Equal((Disk1, "10.0.0.5:3260", "LOGGED_IN", "sdb"), (session.Target, session.Portal, session.State, string.Join(',', session.Disks)));
        Assert.Equal([new IscsiNode(Disk1, "10.0.0.5:3260", true), new IscsiNode(Disk2, "10.0.0.5:3260", false)], iscsi.Nodes);

        // A map without an alias is named by its WWID.
        Assert.Equal(["36001405bbbbbbbbbbbbbbbbbbbbbbbbb", "mpatha"], storage.Multipath!.Select(m => m.Name));
        var mpatha = storage.Multipath![1];
        Assert.Equal(("36001405aaaaaaaaaaaaaaaaaaaaaaaaa", "dm-0", "LIO-ORG,disk01", "10G"), (mpatha.Wwid, mpatha.DmDevice, mpatha.Product, mpatha.Size));
        Assert.Equal(
            [new MultipathPath("3:0:0:0", "sdb", "active", "ready", "running"), new MultipathPath("4:0:0:0", "sdc", "failed", "faulty", "offline")],
            mpatha.Paths);

        // r1 is configured but down.
        Assert.Equal(["r0", "r1"], storage.Drbd!.Select(r => r.Name));
        var r0 = storage.Drbd![0];
        Assert.Equal((true, "Secondary"), (r0.Up, r0.Role));
        Assert.Equal([new DrbdVolume(0, 0, "UpToDate")], r0.Volumes);
        var peer = Assert.Single(r0.Peers);
        Assert.Equal(("node2", "Connected", "Secondary"), (peer.Name, peer.ConnectionState, peer.Role));
        Assert.Equal([new DrbdPeerVolume(0, "SyncSource", "Inconsistent", 42.5)], peer.Volumes);
        Assert.Equal((false, (string?)null), (storage.Drbd![1].Up, storage.Drbd[1].Role));
    }

    [Fact]
    public async Task Iscsi_discovers_logs_in_and_logs_out_and_audits_each_action()
    {
        await using var tool = await OpenAsync();

        var discovered = Data(await ActAsync(tool, "iscsi.discover", portal: "10.0.0.9"));
        Assert.Equal("Found 1 target, 1 new.", discovered.GetProperty("message").GetString());
        Assert.Contains(new IscsiNode(Disk3, "10.0.0.9:3260", false), Storage(discovered).Iscsi!.Nodes);

        var login = Data(await ActAsync(tool, "iscsi.login", target: Disk3, portal: "10.0.0.9:3260,1"));
        Assert.Equal($"Logged in to {Disk3} at 10.0.0.9:3260; its disks here: sdc.", login.GetProperty("message").GetString());
        Assert.Equal([Disk1, Disk3], Storage(login).Iscsi!.Sessions.Select(s => s.Target));

        var loggedOut = Storage(Data(await ActAsync(tool, "iscsi.logout", target: Disk1, portal: "10.0.0.5:3260")));
        Assert.Equal([Disk3], loggedOut.Iscsi!.Sessions.Select(s => s.Target));
        Assert.Contains(new IscsiNode(Disk1, "10.0.0.5:3260", false), loggedOut.Iscsi.Nodes);

        Assert.Equal(
            ["iSCSI discovery · 10.0.0.9", $"iSCSI login · {Disk3} · 10.0.0.9:3260", $"iSCSI logout · {Disk1} · 10.0.0.5:3260"],
            await AuditAsync());
    }

    [Fact]
    public async Task Iscsi_refuses_targets_the_host_doesnt_know_and_says_what_the_tool_said()
    {
        await using var tool = await OpenAsync();

        Assert.Contains("Discover it first", Error(await ActAsync(tool, "iscsi.login", target: Disk3, portal: "10.0.0.9:3260")), StringComparison.Ordinal);
        Assert.Contains("already logged in", Error(await ActAsync(tool, "iscsi.login", target: Disk1, portal: "10.0.0.5:3260")), StringComparison.Ordinal);
        Assert.Equal(
            "iSCSI discovery · 10.0.0.66:3260 failed: iscsiadm: cannot make connection to 10.0.0.66:3260: Connection refused.",
            Error(await ActAsync(tool, "iscsi.discover", portal: "10.0.0.66:3260")));
    }

    [Fact]
    public async Task A_login_the_san_redirects_is_known_by_the_portal_it_was_made_through()
    {
        const string Nimble = "iqn.2007-11.com.nimblestorage:vol1-v03854e0c5f768377.000004be.5cf3cd7b";
        await using var tool = await OpenAsync();
        Data(await ActAsync(tool, "iscsi.discover", portal: "10.0.0.7"));

        // The array connects the session to its data address, 10.0.1.7.
        var login = Data(await ActAsync(tool, "iscsi.login", target: Nimble, portal: "10.0.0.7:3260,2460"));
        Assert.Equal($"Logged in to {Nimble} at 10.0.0.7:3260; its disks here: sdc.", login.GetProperty("message").GetString());
        var session = Storage(login).Iscsi!.Sessions.Single(s => s.Target == Nimble);
        Assert.Equal(("10.0.0.7:3260", "10.0.1.7:3260"), (session.Portal, Assert.Single(session.ConnectedTo!)));

        var loggedOut = Storage(Data(await ActAsync(tool, "iscsi.logout", target: Nimble, portal: "10.0.0.7:3260")));
        Assert.DoesNotContain(loggedOut.Iscsi!.Sessions, s => s.Target == Nimble);
    }

    [Fact]
    public async Task A_login_that_exits_cleanly_but_doesnt_log_in_is_reported_as_failed()
    {
        await using var tool = await OpenAsync();
        await san.MakeLoginsSilentAsync();

        Assert.Equal(
            $"iSCSI login · {Disk2} · 10.0.0.5:3260 didn't take effect: the host has no session to {Disk2} at 10.0.0.5:3260 afterwards. The tool printed nothing and reported success.",
            Error(await ActAsync(tool, "iscsi.login", target: Disk2, portal: "10.0.0.5:3260")));
    }

    [Theory]
    [InlineData("10.0.0.9; reboot")]
    [InlineData("10.0.0.9' && reboot '")]
    [InlineData("$(reboot)")]
    [InlineData("-p 10.0.0.9")]
    public async Task Discovery_refuses_portals_that_arent_an_address(string portal)
    {
        await using var tool = await OpenAsync();

        Assert.Contains("Enter the portal", Error(await ActAsync(tool, "iscsi.discover", portal: portal)), StringComparison.Ordinal);
        Assert.Empty(await AuditAsync());
    }

    [Fact]
    public async Task Multipath_flushes_a_map_and_reports_a_map_in_use()
    {
        await using var tool = await OpenAsync();

        var flushed = Storage(Data(await ActAsync(tool, "multipath.flush", name: "mpatha")));
        Assert.Equal(["36001405bbbbbbbbbbbbbbbbbbbbbbbbb"], flushed.Multipath!.Select(m => m.Name));
        Assert.Contains("map in use", Error(await ActAsync(tool, "multipath.flush", name: "36001405bbbbbbbbbbbbbbbbbbbbbbbbb")), StringComparison.Ordinal);
        Error(await ActAsync(tool, "multipath.flush", name: "mpatha"));
        Assert.NotNull(Storage(Data(await ActAsync(tool, "multipath.reload"))).Multipath);
    }

    [Fact]
    public async Task Multipath_renames_a_map_without_a_configured_block_through_its_own_file_and_again()
    {
        const string Own = "/etc/multipath/conf.d/nkeeper-alias-36001405aaaaaaaaaaaaaaaaaaaaaaaaa.conf";
        await using var tool = await OpenAsync();

        var renamed = Data(await ActAsync(tool, "multipath.rename", name: "mpatha", newName: "db01"));
        Assert.Equal("Renamed mpatha to db01 and reloaded the maps. It's now /dev/mapper/db01.", renamed.GetProperty("message").GetString());
        Assert.Equal(("db01", "dm-0"), Storage(renamed).Multipath!.Where(m => m.Wwid == "36001405aaaaaaaaaaaaaaaaaaaaaaaaa").Select(m => (m.Name, m.DmDevice)).Single());
        await san.ExecAsync($"grep -q 'alias db01' {Own}");

        // Its own file now has the block, so a second rename edits that instead of adding another.
        Assert.Contains(Storage(Data(await ActAsync(tool, "multipath.rename", name: "db01", newName: "db02"))).Multipath!, m => m.Name == "db02");
        await san.ExecAsync($"grep -q 'alias db02' {Own} && ! grep -q db01 {Own}");

        // A map named by its WWID gets its first alias, and survives a reload.
        Data(await ActAsync(tool, "multipath.rename", name: "36001405bbbbbbbbbbbbbbbbbbbbbbbbb", newName: "db03"));
        Assert.Equal(["db02", "db03"], Storage(Data(await ActAsync(tool, "multipath.reload"))).Multipath!.Select(m => m.Name));
        Assert.Contains("Multipath rename · db01 → db02", await AuditAsync());
    }

    [Theory]
    [InlineData("bad name", "1 to 64 letters")]
    [InlineData("x'; reboot; '", "1 to 64 letters")]
    [InlineData("-f", "1 to 64 letters")]
    [InlineData("mpatha", "already called")]
    [InlineData("36001405bbbbbbbbbbbbbbbbbbbbbbbbb", "Another multipath map")]
    public async Task Multipath_refuses_a_bad_new_name(string newName, string message)
    {
        await using var tool = await OpenAsync();

        Assert.Contains(message, Error(await ActAsync(tool, "multipath.rename", name: "mpatha", newName: newName)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("refuse-reload daemon-down", "device or resource busy")]
    [InlineData("ignore-aliases", "didn't take the new name")]
    public async Task Multipath_puts_the_configuration_back_when_a_rename_doesnt_take_effect(string faults, string message)
    {
        const string Before = "printf 'multipaths {\\n\\tmultipath {\\n\\t\\twwid 36001405aaaaaaaaaaaaaaaaaaaaaaaaa\\n\\t\\talias mpatha\\n\\t}\\n}\\n'";
        await san.ExecAsync($"{Before} > /etc/multipath.conf && cd /var/lib/fake-multipath && touch {faults}");
        await using var tool = await OpenAsync();

        Assert.Contains(message, Error(await ActAsync(tool, "multipath.rename", name: "mpatha", newName: "db01")), StringComparison.Ordinal);
        await san.ExecAsync($"{Before} | cmp - /etc/multipath.conf");

        // A file NKeeper added for a map without a block is removed again.
        Error(await ActAsync(tool, "multipath.rename", name: "36001405bbbbbbbbbbbbbbbbbbbbbbbbb", newName: "db03"));
        await san.ExecAsync("[ ! -e /etc/multipath/conf.d/nkeeper-alias-36001405bbbbbbbbbbbbbbbbbbbbbbbbb.conf ]");
    }

    [Fact]
    public async Task Drbd_brings_resources_up_changes_roles_and_connections()
    {
        await using var tool = await OpenAsync();

        var up = Storage(Data(await ActAsync(tool, "drbd.up", name: "r1")));
        Assert.Equal((true, "Secondary"), (up.Drbd![1].Up, up.Drbd[1].Role));
        Assert.Equal("Primary", Storage(Data(await ActAsync(tool, "drbd.primary", name: "r0"))).Drbd![0].Role);
        Assert.Equal("StandAlone", Storage(Data(await ActAsync(tool, "drbd.disconnect", name: "r0"))).Drbd![0].Peers[0].ConnectionState);
        Assert.False(Storage(Data(await ActAsync(tool, "drbd.down", name: "r1"))).Drbd![1].Up);
        Assert.Contains("no DRBD resource", Error(await ActAsync(tool, "drbd.up", name: "r9")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_root_it_says_sudo_is_needed()
    {
        await san.RemoveSudoAsync();
        await using var tool = await OpenAsync();

        Assert.Contains("sudo without a password", Error(await tool.RequestAsync("status")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_actions_are_refused()
    {
        await using var tool = await OpenAsync();

        Assert.Equal("Choose an action.", Error(await ActAsync(tool, "drbd.invalidate", name: "r0")));
    }

    [Fact]
    public async Task On_a_host_that_isnt_linux_it_says_so_and_ends()
    {
        await san.ExecAsync("touch /var/lib/fake-linux/not-linux");
        await using var tool = await OpenAsync();

        var fatal = await tool.NextAsync();
        Assert.Equal("fatal", fatal.GetProperty("type").GetString());
        Assert.Contains("isn't Linux (it says it's MINGW64_NT-10.0-26100)", fatal.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_without_access_to_the_host_cant_open_it()
    {
        var host = await CreateHostAsync();
        using (var admin = await _server.SignedInClientAsync(Token))
        {
            using var created = await admin.SendAsync(
                HttpMethod.Post, "/api/users", new { userName = "bob", email = "bob@nkeeper.test", password = BobPassword, role = "user", mustChangePassword = false }, Token);
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Token));
        }

        await using var tool = await SessionClient.ConnectAsync(_server, await SignInAsync("bob", BobPassword), "storage");
        await tool.SendAsync(new { type = "start", hostId = Id(host), serviceId = ServiceId(host), credentialId = CredentialId(host) });

        var error = await tool.NextAsync();
        Assert.Equal(("error", "host_not_found"), (error.GetProperty("type").GetString(), error.GetProperty("code").GetString()));
    }

    // ---- Helpers ----

    private static StorageStatus Storage(JsonElement data) => data.GetProperty("storage").Deserialize<StorageStatus>(Json)!;

    private static Task<JsonElement> ActAsync(SessionClient tool, string action, string? target = null, string? portal = null, string? name = null, string? newName = null) =>
        tool.RequestAsync("action", new { action, target, portal, name, newName });

    private static JsonElement Data(JsonElement reply)
    {
        Assert.True(reply.GetProperty("type").GetString() == "reply", reply.GetRawText());
        return reply.GetProperty("data");
    }

    private static string Error(JsonElement reply)
    {
        Assert.True(reply.GetProperty("type").GetString() == "error", reply.GetRawText());
        return reply.GetProperty("message").GetString()!;
    }

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();

    private static Guid ServiceId(JsonElement host) => host.GetProperty("services")[0].GetProperty("id").GetGuid();

    private static Guid CredentialId(JsonElement host) => host.GetProperty("credentials")[0].GetProperty("id").GetGuid();

    /// <summary>The details of the <c>storage.action</c> events, oldest first.</summary>
    private async Task<List<string>> AuditAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand("SELECT detail FROM core.audit_events WHERE action = 'storage.action' ORDER BY occurred_at_utc", connection);
        await using var reader = await command.ExecuteReaderAsync(Token);
        var details = new List<string>();
        while (await reader.ReadAsync(Token))
        {
            details.Add(reader.GetString(0));
        }

        return details;
    }

    private async Task<JsonElement> CreateHostAsync()
    {
        using var admin = await _server.SignedInClientAsync(Token);
        using var created = await admin.SendAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"san-{Guid.NewGuid():N}"[..20],
            ipAddress = StorageHostFixture.Address,
            services = new[] { new { type = "ssh", port = san.Port, settings = new { } } },
            credentials = new[] { new { username = StorageHostFixture.UserName, password = StorageHostFixture.Password } },
        }, Token);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Token));
        return await created.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    /// <summary>Opens the tool on a new host's SSH service with its credential, trusting the host key when asked.</summary>
    private async Task<SessionClient> OpenAsync()
    {
        var host = await CreateHostAsync();
        var tool = await SessionClient.ConnectAsync(_server, _cookie, "storage");
        await tool.SendAsync(new { type = "start", hostId = Id(host), serviceId = ServiceId(host), credentialId = CredentialId(host) });
        while (true)
        {
            var message = await tool.NextAsync();
            switch (message.GetProperty("type").GetString())
            {
                case "hostKey":
                    await tool.SendAsync(new { type = "hostKeyDecision", trust = true });
                    break;
                case "connected":
                    return tool;
                case "error":
                    Assert.Fail(message.GetRawText());
                    break;
            }
        }
    }

    private async Task<string> SignInAsync(string userName, string password)
    {
        using var client = _server.CreateClient();
        using var login = await client.SendAsync(HttpMethod.Post, "/api/auth/login", new { userNameOrEmail = userName, password, rememberMe = false }, Token);
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync(Token));
        return string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
    }
}
