using Nightweaver.Background;
using Nightweaver.Cli;
using Nightweaver.Configuration;
using Nightweaver.Supervision;
using Nightweaver.Upgrade;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class UpgradeLifecycleTests
{
    [Theory]
    [InlineData("manual")]
    [InlineData("background")]
    [InlineData("foreground")]
    [InlineData("unknown-runtime")]
    [InlineData("unknown-config")]
    [InlineData("malformed-owner")]
    [InlineData("unsupported")]
    public async Task NativeInventoryAdapterRefusesUnknownConsumersAndPreservesManualOptOut(string scenario)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var fixture = new Fixture();
        await fixture.Prepare();
        fixture.Temp.WriteFile("repo/.beads/metadata.json", "{}");
        using var user = System.Security.Principal.WindowsIdentity.GetCurrent();
        var project = fixture.Scope.Projects[0] with { Repository = Path.Combine(fixture.Temp.Path, "repo"),
            PhysicalRepositoryIdentity = ProcessLifetimeTargetRepositoryLock.ResolveIdentity(fixture.Scope.Projects[0].GitCommonDirectory) };
        var expected = fixture.Scope with { UserSid = user.User!.Value, Projects = [project] };
        using var payload = scenario is "foreground" or "background" ? UpgradeInterlock.AcquirePayload(fixture.Paths, fixture.Slug) : null;
        using var owner = scenario == "background" ? BackgroundFileLock.AcquireOwner(fixture.Paths, fixture.Slug) : null;
        if (scenario == "background") await BackgroundOwnerRecord.WriteAsync(BackgroundOwnerRecord.PathFor(fixture.Paths, fixture.Slug), fixture.Owner, fixture.Token);
        if (scenario == "unknown-runtime") fixture.Temp.WriteFile("run/foreign/retained", "preserve");
        if (scenario == "unknown-config") fixture.Temp.WriteFile("projects/foreign.json", "{}");
        if (scenario == "malformed-owner") fixture.Temp.WriteFile("run/demo/background/owner.json", "{");
        var runner = new InventoryRunner(project.GitCommonDirectory);
        async Task<UpgradeLifecycleInventory> Read() => await UpgradeLifecycleInventoryReader.ReadAsync(fixture.Paths,
            expected, scenario != "unsupported", runner, fixture.Token);
        if (scenario is "manual" or "background")
        {
            var inventory = await Read();
            Assert.Equal(expected.Projects, inventory.Scope.Projects);
            Assert.Equal(scenario == "background" ? 1 : 0, inventory.Owners.Length);
        }
        else await Assert.ThrowsAnyAsync<Exception>(() => Read());
        Assert.False(Directory.Exists(fixture.Paths.SupervisionRoot(fixture.Slug)));
        if (scenario == "unknown-runtime") Assert.Equal("preserve", File.ReadAllText(Path.Combine(fixture.Temp.Path, "run", "foreign", "retained")));
    }

    private sealed class InventoryRunner(string common) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, request.Arguments[0] == "check-ref-format" ? "main\n" : $"true\n\n{common}\n{common}\n", ""));
    }

    [Fact]
    public async Task ForegroundAdmissionRefusesBeforeRepositoryOwnershipAndPayloadLease()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        await fixture.Coordinator.BeginDrainAsync(1, fixture.Token);
        Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.AcquireRuntime(fixture.Paths, fixture.Slug,
            new Nightweaver.Inspection.ValidatedTargetRepository(fixture.Temp.Path, "main")));
        Assert.False(File.Exists(Path.Combine(fixture.Temp.Path, "run", "demo", "payload.lock")));
    }

    [Fact]
    public async Task ConfigurationChangesAndConcurrentWritesCannotCrossMutationBoundary()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        await fixture.Coordinator.BeginDrainAsync(1, fixture.Token);
        fixture.Native = new(OwnerProcessState.Exited, true);
        await fixture.Coordinator.ConfirmSettlementAsync(2, fixture.Token);
        await fixture.Coordinator.WithMutationAsync(3, _ =>
        {
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.Scope.Projects[0].Configuration, "changed"));
            return Task.CompletedTask;
        }, fixture.Token);
        File.WriteAllText(fixture.Scope.Projects[0].Configuration, "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Coordinator.WithMutationAsync(3,
            _ => throw new IOException("mutation"), fixture.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeOwnerControlRequiresReservedOperationAndExactMaintenanceToken(bool supervised)
    {
        using var fixture = new Fixture(supervised);
        await fixture.Prepare();
        var lifecycle = supervised ? new SupervisionLifecycle(fixture.Store) : null;
        using var state = new BackgroundOwnerState(fixture.Owner);
        var options = new BackgroundOwnerOptions(fixture.Temp.Path, fixture.Owner.ConfigPath, "demo", fixture.Owner.RunId, fixture.Owner.WebUrl);
        var project = ProjectConfigLoader.Load(fixture.Owner.ConfigPath, fixture.Slug, fixture.Paths);
        var request = new BackgroundControlRequest("upgrade", fixture.Owner.RunId, Maintenance: true,
            UpgradeOperationId: new('a', 32));
        var recordPath = BackgroundOwnerRecord.PathFor(fixture.Paths, fixture.Slug);
        async Task<BackgroundControlResponse> Send(BackgroundControlRequest value) => await BackgroundOwnerApplication.HandleRequestAsync(
            value, options, fixture.Paths, project, state, recordPath, () => null, lifecycle, fixture.Token);
        Assert.False((await Send(request)).Accepted);
        await fixture.Coordinator.BeginDrainAsync(1, fixture.Token);
        Assert.False((await Send(request with { UpgradeOperationId = new('f', 32) })).Accepted);
        if (supervised)
        {
            Assert.False((await Send(request)).Accepted);
            request = request with { MaintenanceToken = fixture.Store.Read().State!.MaintenanceToken };
        }
        Assert.True((await Send(request)).Accepted);
        Assert.Equal("Maintenance", state.Current.StopReason);
        if (supervised) Assert.True(fixture.Store.Read().State!.DesiredRunning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainRetainsIntentAndRequiresNativeSettlementBeforeMutation(bool supervised)
    {
        using var fixture = new Fixture(supervised);
        await fixture.Prepare();
        var initial = fixture.Store.Read().State;
        var draining = await fixture.Coordinator.BeginDrainAsync(1, fixture.Token);
        Assert.Equal(UpgradePhase.Draining, draining.Phase);
        Assert.Equal(1, fixture.Drains);
        Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.RequireStartupAllowed(fixture.Paths));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.ConfirmSettlementAsync(2, fixture.Token));
        Assert.Equal(2, fixture.Journal.Read().Progress!.Revision);
        fixture.Native = new(OwnerProcessState.Exited, true);
        var settled = await fixture.Coordinator.ConfirmSettlementAsync(2, fixture.Token);
        Assert.Equal(UpgradePhase.Settled, settled.Phase);
        var mutations = 0;
        await fixture.Coordinator.WithMutationAsync(3, _ =>
        {
            mutations++;
            Assert.Throws<IOException>(() => BackgroundFileLock.AcquireStart(fixture.Paths, fixture.Slug));
            Assert.Throws<IOException>(() => UpgradeInterlock.AcquireManager(fixture.Paths));
            return Task.CompletedTask;
        }, fixture.Token);
        Assert.Equal(1, mutations);
        if (supervised)
        {
            var current = fixture.Store.Read().State!;
            Assert.Equal(initial!.Enabled, current.Enabled);
            Assert.Equal(initial.DesiredRunning, current.DesiredRunning);
            Assert.Equal(initial.Attempts, current.Attempts);
            Assert.Equal(initial.History, current.History);
            Assert.NotNull(current.MaintenanceToken);
        }
        else Assert.False(Directory.Exists(fixture.Paths.SupervisionRoot(fixture.Slug)));
        await fixture.Coordinator.ReleaseAsync(3, fixture.Token);
        UpgradeInterlock.RequireStartupAllowed(fixture.Paths);
        Assert.Equal(1, fixture.Drains); // release starts nothing
        if (supervised) Assert.Null(fixture.Store.Read().State!.MaintenanceToken);
    }

    [Theory]
    [InlineData("alive")]
    [InlineData("descendants")]
    [InlineData("reused")]
    [InlineData("unavailable")]
    [InlineData("owner-lock")]
    [InlineData("unknown-lock")]
    [InlineData("foreign")]
    [InlineData("missing")]
    [InlineData("scope")]
    [InlineData("revision")]
    public async Task FreshSettlementAndAuthorityAreRequiredAtEveryMutation(string change)
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        await fixture.Coordinator.BeginDrainAsync(1, fixture.Token);
        fixture.Native = new(OwnerProcessState.Exited, true);
        await fixture.Coordinator.ConfirmSettlementAsync(2, fixture.Token);
        var revision = 3L;
        switch (change)
        {
            case "alive": fixture.Native = new(OwnerProcessState.Alive, false); break;
            case "descendants": fixture.Native = new(OwnerProcessState.Exited, false); break;
            case "reused": fixture.Native = new(OwnerProcessState.Reused, null); break;
            case "unavailable": fixture.Native = new(OwnerProcessState.Unavailable, null); break;
            case "owner-lock": fixture.Active = true; break;
            case "unknown-lock": fixture.Active = null; break;
            case "foreign": fixture.Inventory = fixture.Inventory with { Owners = [fixture.Owner with { RunId = new('e', 32) }] }; break;
            case "missing": fixture.Inventory = fixture.Inventory with { Owners = [] }; break;
            case "scope": fixture.Inventory = fixture.Inventory with { Scope = fixture.Scope with { UserSid = "S-1-5-21-1002" } }; break;
            case "revision": revision = 2; break;
        }
        var mutations = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.WithMutationAsync(revision,
            _ => { mutations++; return Task.CompletedTask; }, fixture.Token));
        Assert.Equal(0, mutations);
        Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.RequireStartupAllowed(fixture.Paths));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterStopOrDisablePreventsMutationAndStaleMaintenanceRelease(bool disable)
    {
        using var fixture = new Fixture(true);
        await fixture.Prepare();
        await fixture.Coordinator.BeginDrainAsync(1, fixture.Token);
        fixture.Native = new(OwnerProcessState.Exited, true);
        await fixture.Coordinator.ConfirmSettlementAsync(2, fixture.Token);
        var old = fixture.Store.Read().State!;
        if (disable)
            await fixture.Store.SaveAsync(old.Revision, SupervisionPolicy.SetIntent(old, false, old.DesiredRunning, old.MaintenanceToken), fixture.Token);
        else
            await new SupervisionLifecycle(fixture.Store).ApplyAsync(SupervisionLifecycleAction.Stop, null, null, fixture.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.WithMutationAsync(3, _ => throw new IOException("mutation"), fixture.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.ReleaseAsync(3, fixture.Token));
        var current = fixture.Store.Read().State!;
        Assert.Equal(old.MaintenanceToken, current.MaintenanceToken);
        Assert.True(disable ? !current.Enabled : !current.DesiredRunning);
        Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.RequireStartupAllowed(fixture.Paths));
    }

    [Fact]
    public async Task DrainRequestsRunWithoutCommandLocksAndLostRepliesRetainBoundary()
    {
        using var fixture = new Fixture(true);
        await fixture.Prepare();
        fixture.OnDrain = async () =>
        {
            using var command = BackgroundFileLock.AcquireStart(fixture.Paths, fixture.Slug);
            await new SupervisionLifecycle(fixture.Store).ApplyAsync(SupervisionLifecycleAction.Stop, null, null, fixture.Token);
            throw new IOException("lost drain reply");
        };
        await Assert.ThrowsAsync<IOException>(() => fixture.Coordinator.BeginDrainAsync(1, fixture.Token));
        Assert.Equal(UpgradePhase.Draining, fixture.Journal.Read().Progress!.Phase);
        Assert.False(fixture.Store.Read().State!.DesiredRunning);
        Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.RequireStartupAllowed(fixture.Paths));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.BeginDrainAsync(2, fixture.Token));
    }

    [Theory]
    [InlineData("command")]
    [InlineData("admission")]
    [InlineData("manager")]
    [InlineData("unsupported")]
    [InlineData("maintenance")]
    public async Task ContentionAndUnsupportedSourcesCannotReserve(string scenario)
    {
        using var fixture = new Fixture(scenario == "maintenance");
        await fixture.Prepare();
        using var held = scenario switch
        {
            "command" => BackgroundFileLock.AcquireStart(fixture.Paths, fixture.Slug),
            "admission" => UpgradeInterlock.AcquireAdmission(fixture.Paths),
            "manager" => UpgradeInterlock.AcquireManager(fixture.Paths),
            _ => null
        };
        if (scenario == "unsupported") fixture.Inventory = fixture.Inventory with { SourceBoundarySupported = false };
        if (scenario == "maintenance")
            await new SupervisionLifecycle(fixture.Store).ApplyAsync(SupervisionLifecycleAction.EnterMaintenance, null, null, fixture.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Coordinator.BeginDrainAsync(1, fixture.Token));
        Assert.Equal(0, fixture.Drains);
        UpgradeInterlock.RequireStartupAllowed(fixture.Paths);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("partial")]
    [InlineData("bad-release")]
    public async Task InterruptedInterlocksFailClosedWithoutRemovingEvidence(string scenario)
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        var directory = Path.Combine(UpgradeInterlock.Root(fixture.Paths), new string('a', 32));
        if (scenario == "empty") Directory.CreateDirectory(directory);
        else
        {
            await fixture.Coordinator.BeginDrainAsync(1, fixture.Token);
            File.WriteAllText(Path.Combine(directory, scenario == "partial" ? "reservation.json.partial.tmp" : "released.json"), "{");
        }
        var files = Directory.GetFileSystemEntries(directory);
        Assert.ThrowsAny<Exception>(() => UpgradeInterlock.RequireStartupAllowed(fixture.Paths));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Coordinator.BeginDrainAsync(fixture.Journal.Read().Progress!.Revision, fixture.Token));
        Assert.Equal(files, Directory.GetFileSystemEntries(directory));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("restart")]
    [InlineData("tick")]
    public async Task ManualLifecycleAndFiniteTickRejectReservedBoundaryBeforeOpeningSupervision(string action)
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        await fixture.Coordinator.BeginDrainAsync(1, fixture.Token);
        // Use the existing command fixture's strict configuration shape.
        fixture.Temp.WriteFile("projects/demo.json", System.Text.Json.JsonSerializer.Serialize(new
        {
            name = "demo", target_repository = new { path = fixture.Temp.CreateDirectory("repo") }
        }));
        var environment = new AppEnvironment(fixture.Temp.Path, fixture.Temp.Path, new StringWriter(), new StringWriter());
        var options = BackgroundCommandParser.Parse(["background", action, "--project", "demo"], out _);
        var opened = false;
        var exit = await BackgroundManagerApplication.RunAsync(options, environment, fixture.Token,
            (_, _, _) => { opened = true; throw new IOException("opened supervision"); });
        Assert.Equal(1, exit);
        Assert.False(opened);
    }

    private sealed class Fixture : IDisposable
    {
        internal TempDirectory Temp { get; } = new();
        internal HostPaths Paths { get; }
        internal ProjectSlug Slug { get; } = ProjectSlug.Parse("demo");
        internal SupervisionStore Store { get; }
        internal UpgradeScope Scope { get; }
        internal UpgradeJournal Journal { get; }
        internal UpgradeLifecycle Coordinator { get; }
        internal BackgroundOwnerRecord Owner { get; }
        internal UpgradeLifecycleInventory Inventory { get; set; }
        internal OwnerProcessObservation Native { get; set; } = new(OwnerProcessState.Alive, false);
        internal bool? Active { get; set; } = false;
        internal int Drains { get; private set; }
        internal Func<Task>? OnDrain { get; set; }
        internal CancellationToken Token { get; } = TestContext.Current.CancellationToken;
        private readonly bool supervised;
        internal Fixture(bool supervised = false)
        {
            this.supervised = supervised;
            Paths = new(Temp.Path);
            var identity = SupervisionPolicyTests.Identity(Temp.Path);
            Temp.CreateDirectory("repo/.git");
            Temp.CreateDirectory("projects");
            Temp.WriteFile("projects/demo.json", System.Text.Json.JsonSerializer.Serialize(new
            {
                name = "demo", target_repository = new { path = Path.Combine(Temp.Path, "repo") }
            }));
            Store = new(Paths, Slug, identity);
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(identity.ConfigPath)));
            Scope = new(Temp.Path, identity.UserSid, [new("demo", identity.ConfigPath, digest, Temp.Path, identity.GitCommonDirectory, "physical")]);
            Journal = new(Scope, new('a', 32));
            Owner = new(1, "running", new('c', 32), 123, SupervisionPolicyTests.Start, Temp.Path, identity.ConfigPath,
                "demo", Temp.Path, new('d', 32), "http://127.0.0.1:5000", "healthy", "healthy", "host.log", "web.log", null)
                { ContainmentVersion = 1, GitCommonDirectory = identity.GitCommonDirectory };
            Inventory = new(Scope, [Owner], true);
            Coordinator = new(Paths, Journal, new(_ => Task.FromResult(Inventory),
                (_, _) => Task.FromResult<SupervisionLifecycle?>(supervised ? new(Store) : null),
                async (_, _, _, _) => { Drains++; if (OnDrain is not null) await OnDrain(); return true; },
                _ => Native, _ => Active));
        }
        internal async Task Prepare()
        {
            if (supervised) await Store.SaveAsync(null, SupervisionPolicy.SetIntent(SupervisionState.Manual(
                SupervisionPolicyTests.Identity(Temp.Path)), true, true, null), Token);
            await Journal.SaveAsync(null, new(1, new('a', 32), 0, Scope, new('b', 64), new('c', 64), new('d', 64),
                UpgradePhase.Prepared, null, null, new('e', 32), [], [], [], []), Token);
        }
        public void Dispose() => Temp.Dispose();
    }
}
