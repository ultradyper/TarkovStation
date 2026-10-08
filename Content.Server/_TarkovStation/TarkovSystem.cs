// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Linq;
using System.Numerics;
using Content.Server._TarkovStation.Persistence;
using Content.Server.GameTicking;
using Content.Server.Mind;
using Content.Server.Station.Systems;
using Content.Shared._TarkovStation;
using Content.Shared._TarkovStation.Components;
using Content.Shared._TarkovStation.Prototypes;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Content.Shared.Humanoid;
using Content.Shared.Preferences;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using YamlDotNet.RepresentationModel;

namespace Content.Server._TarkovStation;

/// <summary>Coordinates the alpha mode. Account data is durable; per-entity state lives in components.</summary>
public sealed partial class TarkovSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private ISerializationManager _serialization = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private MindSystem _mind = default!;
    [Dependency] private StationSpawningSystem _spawn = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private HungerSystem _hunger = default!;
    [Dependency] private ThirstSystem _thirst = default!;

    private TarkovRepository? _repository;
    private EntityUid? _hub;
    private TimeSpan _nextRefresh;
    private TimeSpan _nextCheckpoint;
    private TimeSpan? _queueEnds;
    private readonly HashSet<string> _ready = new();
    // Connection request throttles are transient and cleared when a session leaves.
    private readonly Dictionary<string, TimeSpan> _requestTimes = new();
    private readonly HashSet<string> _pendingJoins = new();
    // Rules acknowledgement belongs to the authenticated connection and current cycle.
    private readonly HashSet<string> _rulesAccepted = new();
    private readonly HashSet<string> _guideRead = new();
    private bool Enabled => _cfg.GetCVar(TarkovCVars.Enabled);
    private static long Utc => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public TarkovRepository? Repository => _repository;
    public EntityUid? Hub => _hub;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerBeforeSpawnEvent>(OnBeforeSpawn);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnCleanup);
        SubscribeLocalEvent<TarkovRuleComponent, GameRuleStartedEvent>(OnModeStarted);
        SubscribeNetworkEvent<TarkovRequestEvent>(OnRequest);
        _players.PlayerStatusChanged += OnPlayerStatus;
        InitializeItems();
        InitializeRaids();
        InitializeHubPolicy();
        InitializeCommands();
    }

    public override void Shutdown()
    {
        _players.PlayerStatusChanged -= OnPlayerStatus;
        EntityManager.EntityInitialized -= OnRaidStackInitialized;
        if (_repository != null)
        {
            CheckpointAll();
            _repository.Dispose();
            _repository = null;
        }
        base.Shutdown();
    }

    private void EnsureRepository()
    {
        if (_repository != null)
            return;
        var root = _resources.UserData.RootDir;
        var path = root == null ? ":memory:" : Path.Combine(root, _cfg.GetCVar(TarkovCVars.Database));
        _repository = new TarkovRepository(path, Utc, Math.Max(60, _cfg.GetCVar(TarkovCVars.CycleSeconds)));
        // A process restart cannot safely restore an in-flight physics world. Never duplicate its gear.
        Write("system", "recover-" + Guid.NewGuid().ToString("N"), "recovery", data =>
        {
            if (data.Version < 2)
            {
                // Legacy alpha did not persist helper ownership. Infer only unambiguous historical pairs.
                var humans = data.Accounts.Values.Where(a => !a.TestBot).ToArray();
                foreach (var contract in data.Contracts.Values.Where(c => c.Kind == "kill"))
                {
                    if (!data.Accounts.TryGetValue(contract.Issuer, out var partner) || !partner.TestBot
                        || !data.Accounts.TryGetValue(contract.Target, out var target) || !target.TestBot) continue;
                    var owner = contract.Assignee != "" ? contract.Assignee : humans.Length == 1 ? humans[0].User : "";
                    if (owner == "" || !data.Accounts.ContainsKey(owner)) continue;
                    partner.TestOwner = target.TestOwner = owner;
                    target.TestTarget = true;
                }
                data.Version = 2;
            }
            foreach (var account in data.Accounts.Values.Where(a => a.Location == "raid").ToArray())
                TarkovEconomy.LoseLife(data, account.User);
            foreach (var trade in data.Trades.Values.Where(t => t.Status == "open").ToArray())
                TarkovEconomy.CancelTrade(data, trade.A, trade.Id);
            TarkovEconomy.Expire(data, Utc);
            return null;
        });
    }

    private void OnModeStarted(Entity<TarkovRuleComponent> ent, ref GameRuleStartedEvent args)
    {
        if (!Enabled)
            return;
        EnsureRepository();
        SetupHub();
    }

    private void OnCleanup(RoundRestartCleanupEvent args)
    {
        if (_repository != null)
            CheckpointAll();
        _hub = null;
        _ready.Clear();
        _queueEnds = null;
        _pendingJoins.Clear();
        _rulesAccepted.Clear();
        _guideRead.Clear();
    }

    /// <summary>Route station join/observe requests through this mode's onboarding and saved character.</summary>
    public void RequestEntry(ICommonSession session)
    {
        EnsureRepository();
        _pendingJoins.Add(session.UserId.ToString());
        SendState(session);
    }

    private void OnBeforeSpawn(PlayerBeforeSpawnEvent args)
    {
        if (!Enabled)
            return;
        args.Handled = true;
        EnsureRepository();
        _pendingJoins.Add(args.Player.UserId.ToString());
        SendState(args.Player, "", "");
    }

    private void OnPlayerStatus(object? sender, SessionStatusEventArgs args)
    {
        if (!Enabled)
            return;
        var user = args.Session.UserId.ToString();
        if (args.NewStatus == SessionStatus.InGame)
        {
            EnsureRepository();
            _pendingJoins.Add(user);
        }
        if (args.NewStatus == SessionStatus.Disconnected)
        {
            _pendingJoins.Remove(user);
            _requestTimes.Remove(user);
            CancelReady(user, "tarkov-queue-disconnected");
            CapturePlayer(user);
            CancelUserTrade(user);
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!Enabled)
            return;
        EnsureRepository();
        if (_hub == null || Deleted(_hub))
            SetupHub();
        UpdateRaids();
        if (_timing.CurTime < _nextRefresh)
            return;
        _nextRefresh = _timing.CurTime + TimeSpan.FromSeconds(1);
        ProtectHub();
        if (_repository == null)
            return;
        var data = _repository.Read();
        if (Utc >= data.EndsUtc)
        {
            EndCycle();
            return;
        }
        if (_hub != null)
        {
            foreach (var user in _pendingJoins.ToArray())
            {
                if (!TrySession(user, out var session))
                    continue;
                if (data.Accounts.TryGetValue(user, out var account) && account.Created && FindPlayer(user) == null)
                    SpawnHubPlayer(session, account);
                SendState(session);
                _pendingJoins.Remove(user);
            }
        }
        if (_timing.CurTime >= _nextCheckpoint)
        {
            _nextCheckpoint = _timing.CurTime + TimeSpan.FromSeconds(10);
            CheckpointAll();
            Write("system", Guid.NewGuid().ToString("N"), "expire", d => { TarkovEconomy.Expire(d, Utc); return null; });
        }
        foreach (var session in _players.Sessions.Where(s => s.Status == SessionStatus.InGame))
            SendState(session);
    }

    private void SetupHub()
    {
        if (_ticker.RunLevel != GameRunLevel.InRound || !_maps.TryGetMap(_ticker.DefaultMap, out var map))
            return;
        _hub = map;
        EnsureComp<TarkovHubComponent>(map.Value);
        if (!TryComp(map, out MapGridComponent? grid))
        {
            var grids = _maps.GetAllGrids(_ticker.DefaultMap).ToArray();
            if (grids.Length == 0)
                return;
        }
        SetupServices();
        MarkHubSupplies();
        ProtectHub();
    }

    private EntityUid? FindPlayer(string user)
    {
        var query = AllEntityQuery<TarkovPlayerComponent>();
        while (query.MoveNext(out var uid, out var player))
        {
            if (player.User == user && !player.Closed && !Deleted(uid))
                return uid;
        }
        return null;
    }

    private bool TrySession(string user, out ICommonSession session)
    {
        session = default!;
        return Guid.TryParse(user, out var id) && _players.TryGetSessionById(new NetUserId(id), out session!);
    }

    private EntityCoordinates HubCoordinates()
    {
        if (_hub == null)
            throw new InvalidOperationException("Hub not ready");
        var grid = HasComp<MapGridComponent>(_hub) ? _hub.Value : _maps.GetAllGrids(_ticker.DefaultMap).First().Owner;
        return new EntityCoordinates(grid, new Vector2(0.5f, 0.5f));
    }

    private void SpawnHubPlayer(ICommonSession session, TarkovAccount account)
    {
        if (_hub == null || FindPlayer(account.User) != null)
            return;
        var storedProfile = ReadProfile(account.Profile);
        var profile = storedProfile.WithCharacterAppearance(storedProfile.Appearance.WithMarkings(new()));
        var mob = _spawn.SpawnPlayerMob(HubCoordinates(), null, profile, null);
        var player = EnsureComp<TarkovPlayerComponent>(mob);
        player.User = account.User;
        player.Faction = account.Faction;
        player.Branch = account.Branch;
        player.Life = account.Life;
        EnsureComp<TarkovCombatCreditComponent>(mob);
        if (TryComp<HungerComponent>(mob, out var hunger))
        {
            _hunger.SetBaseDecayRate((mob, hunger), _cfg.GetCVar(TarkovCVars.HungerRate));
            _hunger.SetHunger(mob, hunger.Thresholds[HungerThreshold.Okay], hunger);
        }
        if (TryComp<ThirstComponent>(mob, out var thirst))
        {
            _thirst.SetBaseDecayRate((mob, thirst), _cfg.GetCVar(TarkovCVars.ThirstRate));
            _thirst.SetThirst(mob, thirst, thirst.ThirstThresholds[ThirstThreshold.Okay]);
        }
        EnsureComp<TarkovHubProtectedComponent>(mob);
        Dirty(mob, player);
        var mind = _mind.GetOrCreateMind(session.UserId);
        _mind.TransferTo(mind, mob);
        RestoreEquipment(mob, account.User);
        _ticker.PlayerJoinGame(session, silent: true);
    }

    private HumanoidCharacterProfile SanitizeProfile(HumanoidCharacterProfile input, ICommonSession session)
    {
        var name = new string(input.Name.Trim().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '\'').Take(32).ToArray());
        if (name.Length < 2 || input.Appearance.Markings.Count > 32)
            throw new ArgumentException("tarkov-error-name");
        // Alpha characters use the normal human body. No traits, jobs or donor loadouts are imported.
        var flavor = (input.FlavorText ?? "").Trim();
        if (flavor.Length > 1000) flavor = flavor[..1000];
        var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithName(name)
            .WithAge(input.Age).WithFlavorText(flavor).WithSex(input.Sex).WithGender(input.Gender)
            .WithCharacterAppearance(input.Appearance.WithMarkings(new()));
        profile.EnsureValid(session, IoCManager.Instance!);
        return profile;
    }

    private string WriteProfile(HumanoidCharacterProfile profile)
    {
        var node = _serialization.WriteValue<HumanoidCharacterProfile?>(profile, alwaysWrite: true);
        using var writer = new StringWriter();
        node.Write(writer);
        return writer.ToString();
    }

    private HumanoidCharacterProfile ReadProfile(string text)
    {
        using var reader = new StringReader(text);
        var stream = new YamlStream();
        stream.Load(reader);
        var node = stream.Documents[0].RootNode.ToDataNodeCast<MappingDataNode>();
        return _serialization.Read<HumanoidCharacterProfile?>(node) ?? throw new InvalidDataException("Empty profile");
    }

    private string? Write(string actor, string operation, string kind, Func<TarkovData, string?> mutation)
        => _repository?.Execute(actor, operation, kind, mutation, Utc) ?? (_repository == null ? "tarkov-error-starting" : null);

    private void EndCycle()
    {
        if (_repository == null)
            return;
        LoseAllRaids();
        CheckpointAll();
        Write("system", Guid.NewGuid().ToString("N"), "cycle", data =>
        {
            TarkovEconomy.RollCycle(data, Utc, Math.Max(60, _cfg.GetCVar(TarkovCVars.CycleSeconds)));
            return null;
        });
        _ticker.EndRound(Loc.GetString("tarkov-cycle-ended"));
        _ticker.RestartRound();
    }
}
