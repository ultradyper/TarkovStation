// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Numerics;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Client.Players.PlayTimeTracking;
using Content.Client._TarkovStation.UI;
using Content.Shared._TarkovStation;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Preferences;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Client.State;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using Robust.Shared.Timing;

namespace Content.Client._TarkovStation;

/// <summary>Onboarding and a passive HUD. Hub service windows can only be opened by server-confirmed interactions.</summary>
public sealed partial class TarkovClientSystem : EntitySystem
{
    [Dependency] private IUriOpener _uri = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IUserInterfaceManager _ui = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IClientPreferencesManager _preferences = default!;
    [Dependency] private IFileDialogManager _dialogs = default!;
    [Dependency] private ILogManager _logs = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IResourceCache _cache = default!;
    [Dependency] private JobRequirementsManager _requirements = default!;
    [Dependency] private MarkingManager _markings = default!;
    [Dependency] private IStateManager _states = default!;
    private TarkovOnboardingWindow? _entry;
    private TarkovServiceWindow? _service;
    private PanelContainer? _hud;
    private PanelContainer? _entryBackdrop;
    private Label? _hudIdentity;
    private Label? _hudStatus;
    private TarkovButton? _cancelReady;
    private TarkovStateEvent? _snapshot;
    private bool _closingFromServer;
    private PanelContainer? _notice;
    private Label? _noticeText;
    private TimeSpan _noticeUntil;
    private string _noticeKey = "";
    /// <summary>The actual rendered lifecycle notice, exposed for native UI verification.</summary>
    public string NoticeText => _noticeText?.Text ?? "";
    /// <summary>Most recent explicit action reply, retained across passive HUD snapshots for diagnostics/tests.</summary>
    public string LastActionMessage { get; private set; } = "";

    public override void Initialize()
    {
        base.Initialize(); SubscribeNetworkEvent<TarkovStateEvent>(OnState);
        SubscribeNetworkEvent<TarkovFeedbackEvent>(OnFeedback);
    }

    public override void Shutdown()
    {
        _closingFromServer = true; _entry?.Dispose(); _service?.Dispose(); _hud?.Dispose(); _entryBackdrop?.Dispose(); _notice?.Dispose(); base.Shutdown();
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        if (!_cfg.GetCVar(TarkovCVars.Enabled))
        {
            _closingFromServer = true;
            _entry?.Dispose(); _entry = null; _service?.Dispose(); _service = null;
            if (_hud != null) _hud.Visible = false;
            if (_entryBackdrop != null) _entryBackdrop.Visible = false;
            if (_notice != null) _notice.Visible = false;
            _noticeKey = "";
            _snapshot = null;
            if (_states.CurrentState is LobbyState normal && normal.Lobby != null) normal.Lobby.Visible = true;
            _closingFromServer = false; return;
        }
        if (_states.CurrentState is LobbyState lobby && lobby.Lobby != null) lobby.Lobby.Visible = false;
        if (_entryBackdrop != null) _entryBackdrop.Visible = _snapshot?.NeedsCharacter == true;
        if (_snapshot?.NeedsCharacter == true && _preferences.ServerDataLoaded)
        {
            if (_entryBackdrop == null)
            {
                _entryBackdrop = new PanelContainer { PanelOverride = new Robust.Client.Graphics.StyleBoxFlat { BackgroundColor = Color.FromHex("#13191B") } };
                _ui.WindowRoot.AddChild(_entryBackdrop);
                LayoutContainer.SetPosition(_entryBackdrop, Vector2.Zero);
            }
            _entryBackdrop.SetSize = _ui.WindowRoot.Size;
            _entry ??= new TarkovOnboardingWindow(MakeEditor, Send, EntityManager, () => _uri.OpenUri(TarkovCVars.DiscordUrl));
            _entry.Update(_snapshot); if (!_entry.IsOpen) _entry.OpenCentered();
        }
        EnsureHud();
        if (_notice != null)
        {
            _notice.Visible = _snapshot?.NeedsCharacter == false
                && (_timing.CurTime < _noticeUntil || _snapshot.ExtractionSeconds > 0);
            if (_noticeText != null)
                _noticeText.Text = _noticeKey == "ts-notice-extraction-started" && _snapshot?.ExtractionSeconds > 0
                    ? Loc.GetString("ts-notice-extraction", ("seconds", _snapshot.ExtractionSeconds))
                    : _noticeKey == "" ? "" : Loc.GetString(_noticeKey);
            LayoutContainer.SetPosition(_notice, new Vector2(MathF.Max(0, (_ui.WindowRoot.Size.X - 500) / 2), 110));
        }
        if (_hud != null)
        {
            _hud.Visible = _snapshot?.NeedsCharacter == false;
            LayoutContainer.SetPosition(_hud, new Vector2(MathF.Max(460, _ui.WindowRoot.Size.X / 2 - 170), 8));
        }
    }

    private HumanoidProfileEditor MakeEditor()
    {
        var editor = new HumanoidProfileEditor(_preferences, _cfg, EntityManager, _dialogs, _logs, _players, _proto, _cache, _requirements, _markings);
        editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies("Human"), 0); editor.ConfigureForTarkovAlpha(); return editor;
    }

    private void OnFeedback(TarkovFeedbackEvent feedback)
    {
        var key = feedback.Cue switch
        {
            TarkovFeedback.ExtractionStarted => "ts-notice-extraction-started",
            TarkovFeedback.ExtractionCancelled => "ts-notice-extraction-cancelled",
            TarkovFeedback.Extracted => "ts-notice-extracted",
            TarkovFeedback.Warning => "ts-notice-warning",
            TarkovFeedback.Death => "ts-notice-death",
            TarkovFeedback.Departure => "ts-notice-departure",
            _ => "",
        };
        if (key == "") return;
        if (feedback.Cue == TarkovFeedback.Warning && _noticeKey == "ts-notice-extraction-started"
            && _snapshot?.ExtractionSeconds > 0) return;
        _noticeKey = key;
        EnsureNotice();
        _noticeText!.Text = Loc.GetString(key);
        _noticeText.FontColorOverride = feedback.Cue is TarkovFeedback.Warning or TarkovFeedback.ExtractionCancelled
            ? TarkovTheme.Warning : TarkovTheme.Accent;
        _noticeUntil = _timing.CurTime + TimeSpan.FromSeconds(5);
    }

    private void EnsureNotice()
    {
        if (_notice == null)
        {
            _noticeText = TarkovTheme.Label("", true);
            // A centered, clipped label measures to zero width; lifecycle notices must remain visible.
            _noticeText.ClipText = false;
            _noticeText.HorizontalAlignment = Robust.Client.UserInterface.Control.HAlignment.Center;
            _notice = TarkovTheme.Panel(_noticeText, true, 18);
            _notice.MinWidth = 500;
            _ui.WindowRoot.AddChild(_notice);
        }
    }

    private void EnsureHud()
    {
        if (_hud != null) return;
        var stack = TarkovTheme.Column(3);
        _hudIdentity = TarkovTheme.Label("", muted: true); _hudStatus = TarkovTheme.Label("");
        stack.AddChild(_hudIdentity); stack.AddChild(_hudStatus);
        _cancelReady = TarkovTheme.Button(Loc.GetString("tarkov-cancel-ready"), () => Send(new TarkovRequestEvent { Action = TarkovAction.CancelReady }));
        _cancelReady.Visible = false; _cancelReady.MinHeight = 26; stack.AddChild(_cancelReady);
        _hud = TarkovTheme.Panel(stack, padding: 8); _hud.MinWidth = 320; _hud.HorizontalExpand = false;
        _ui.WindowRoot.AddChild(_hud);
    }

    private void OnState(TarkovStateEvent state)
    {
        _snapshot = state;
        if (state.ExtractionSeconds > 0 && _noticeKey == "")
        {
            _noticeKey = "ts-notice-extraction-started";
            EnsureNotice();
        }
        if (state.Message != "") LastActionMessage = state.Message;
        if (!state.NeedsCharacter && _entry != null)
        {
            _entry.Update(state); _entry.Close(); _entry.Dispose(); _entry = null;
        }
        else _entry?.Update(state);
        if (_service != null && (_service.Page != state.ServicePage || state.NeedsCharacter))
        {
            _closingFromServer = true; _service.Dispose(); _service = null; _closingFromServer = false;
        }
        if (state.OpenPage != "" && state.OpenPage == state.ServicePage && !state.NeedsCharacter)
        {
            if (_service == null)
            {
                _service = new TarkovServiceWindow(state.ServicePage, Send, EntityManager);
                _service.OnClose += () =>
                {
                    if (!_closingFromServer) Send(new TarkovRequestEvent { Action = TarkovAction.CloseService });
                };
            }
            _service.Update(state); if (!_service.IsOpen) _service.OpenCentered();
        }
        else _service?.Update(state);
        EnsureHud();
        if (_hudIdentity != null)
            _hudIdentity.Text = Loc.GetString("ts-hud-wallet", ("name", state.CharacterName), ("money", state.Balance));
        if (_hudStatus != null)
            _hudStatus.Text = state.ExtractionSeconds > 0 ? Loc.GetString("ts-hud-extraction", ("seconds", state.ExtractionSeconds))
                : state.Location == "raid" ? Loc.GetString("ts-hud-raid", ("time", TimeSpan.FromSeconds(state.RaidSeconds).ToString(@"mm\:ss")))
                : state.Ready ? Loc.GetString("ts-queue-" + state.QueuePhase, ("seconds", state.QueueSeconds))
                : Loc.GetString("ts-hud-hub", ("time", TimeSpan.FromSeconds(state.RemainingSeconds).ToString(@"hh\:mm\:ss")));
        if (_cancelReady != null) _cancelReady.Visible = state.Ready && state.Location == "hub";
    }

    /// <summary>Send only intent. The server additionally requires the correct active physical service.</summary>
    public void Send(TarkovRequestEvent request)
    {
        request.RequestId = Guid.NewGuid().ToString("N"); request.Cycle = _snapshot?.Cycle ?? ""; RaiseNetworkEvent(request);
    }
}
