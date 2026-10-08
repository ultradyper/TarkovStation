// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using System.Numerics;
using Content.Client.Lobby.UI;
using Content.Client.Stylesheets;
using Content.Shared._TarkovStation;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Localization;

namespace Content.Client._TarkovStation.UI;

/// <summary>Mandatory rules acknowledgement precedes appearance, faction and loadout.</summary>
public sealed class TarkovOnboardingWindow : DefaultWindow
{
    private readonly Func<HumanoidProfileEditor> _makeEditor;
    private readonly Action<TarkovRequestEvent> _send;
    private readonly IEntityManager _entities;
    private readonly Action _openDiscord;
    private bool _showingRules;
    private bool _showingGuide;
    private readonly BoxContainer _content = TarkovTheme.Column(10);
    private readonly BoxContainer _identity = TarkovTheme.Column(10);
    private readonly BoxContainer _factions = TarkovTheme.Column(10);
    private readonly BoxContainer _choices = TarkovTheme.Column(10);
    private readonly Label _feedback = TarkovTheme.Label("");
    private TarkovStateEvent? _state;
    private HumanoidProfileEditor? _editor;
    private string _cycle = "";
    private string _faction = "";
    private string _kit = "";
    private TarkovButton? _enter;
    private CheckBox? _agreement;

    public TarkovOnboardingWindow(Func<HumanoidProfileEditor> makeEditor, Action<TarkovRequestEvent> send, IEntityManager entities, Action openDiscord)
    {
        _makeEditor = makeEditor; _send = send; _entities = entities; _openDiscord = openDiscord;
        Title = Loc.GetString("ts-entry-title"); TitleClass = StyleClass.LabelHeading;
        MinSize = new Vector2(920, 620); SetSize = new Vector2(1100, 680);
        FindControl<Label>("TitleLabel").FontColorOverride = TarkovTheme.Text;
        Contents.AddChild(TarkovTheme.Panel(_content));
    }

    public override void Close()
    {
        if (_state?.NeedsCharacter == true) return;
        base.Close();
    }

    public void Update(TarkovStateEvent state)
    {
        _state = state;
        if (!state.RulesAccepted)
        {
            if (_cycle != state.Cycle || !_showingRules) BuildRules(state);
        }
        else if (!state.GuideRead)
        {
            if (_cycle != state.Cycle || !_showingGuide) BuildGuide(state);
        }
        else if (_cycle != state.Cycle || _editor == null || _showingRules || _showingGuide) Build(state);
        if (state.Message != "")
        {
            _feedback.Text = Loc.GetString(state.Message);
            _feedback.FontColorOverride = state.Message.StartsWith("tarkov-error") ? TarkovTheme.Warning : TarkovTheme.Accent;
        }
        if (_enter != null) _enter.Disabled = !state.RulesAccepted;
    }

    private void BuildRules(TarkovStateEvent state)
    {
        _cycle = state.Cycle;
        _showingRules = true;
        _showingGuide = false;
        _enter = null;
        _editor?.Dispose();
        _editor = null;
        _content.RemoveAllChildren();
        _feedback.Text = "";
        _content.AddChild(TarkovTheme.Label(Loc.GetString("ts-rules-title"), true));
        _content.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-entry-story")));
        var rules = TarkovTheme.Column(14);
        for (var i = 1; i <= 5; i++)
            rules.AddChild(TarkovTheme.Panel(TarkovTheme.Paragraph(Loc.GetString("ts-rule-" + i))));
        rules.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-rules-gameplay")));
        _content.AddChild(TarkovTheme.Scroll(rules));
        _content.AddChild(_feedback);
        _agreement = new CheckBox { Text = Loc.GetString("ts-rules-agreement") };
        var proceed = TarkovTheme.Button(Loc.GetString("ts-rules-continue"), () =>
        {
            if (_agreement.Pressed != true) return;
            _send(new TarkovRequestEvent { Action = TarkovAction.AcceptRules, AcceptedRules = true });
        }, true, true);
        _agreement.OnPressed += _ => proceed.Disabled = !_agreement.Pressed;
        _content.AddChild(_agreement);
        var actions = TarkovTheme.Row(12);
        actions.AddChild(TarkovTheme.Button(Loc.GetString("ts-discord"), _openDiscord));
        actions.AddChild(proceed);
        _content.AddChild(actions);
    }

    private void BuildGuide(TarkovStateEvent state)
    {
        _cycle = state.Cycle;
        _showingRules = false;
        _showingGuide = true;
        _enter = null;
        _editor?.Dispose();
        _editor = null;
        _content.RemoveAllChildren();
        _content.AddChild(new TarkovGuide(() => _send(new TarkovRequestEvent { Action = TarkovAction.AcceptGuide }), true));
        _feedback.Text = "";
        _content.AddChild(_feedback);
    }

    private void Build(TarkovStateEvent state)
    {
        _cycle = state.Cycle;
        _showingRules = false;
        _showingGuide = false;
        _identity.Visible = true; _feedback.Text = "";
        _content.RemoveAllChildren(); _identity.RemoveAllChildren(); _factions.RemoveAllChildren(); _choices.RemoveAllChildren(); _editor?.Dispose();
        var intro = TarkovTheme.Column(4);
        intro.AddChild(TarkovTheme.Label("ПЕРЕВАЛ / 17", true));
        intro.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-entry-story")));
        var introPanel = TarkovTheme.Panel(intro, true);
        _content.AddChild(introPanel); _content.AddChild(_feedback);
        _identity.VerticalExpand = true;
        _identity.AddChild(TarkovTheme.Label(Loc.GetString("ts-entry-step-identity")));
        _editor = _makeEditor(); _editor.HorizontalExpand = true; _editor.VerticalExpand = true;
        _identity.AddChild(_editor);
        _identity.AddChild(TarkovTheme.Button(Loc.GetString("ts-entry-next"), () =>
        {
            _identity.Visible = false; _factions.Visible = true; introPanel.Visible = false;
        }, true));
        _content.AddChild(_identity);
        _factions.Visible = false; _factions.VerticalExpand = true;
        _factions.AddChild(TarkovTheme.Label(Loc.GetString("ts-entry-step-faction")));
        var selection = TarkovTheme.Column(10);
        selection.AddChild(TarkovTheme.Label(Loc.GetString("tarkov-faction"), true));
        var factions = new TarkovColumns(10);
        var factionButtons = new List<TarkovButton>();
        _faction = state.Factions.FirstOrDefault()?.Id ?? "";
        foreach (var faction in state.Factions)
        {
            var box = TarkovTheme.Column(8); box.AddChild(TarkovTheme.Label(faction.Name));
            box.AddChild(TarkovTheme.Paragraph(faction.Detail));
            var choose = TarkovTheme.Button(Loc.GetString("ts-select"), () =>
            {
                _faction = faction.Id;
                foreach (var button in factionButtons) button.Disabled = false;
                factionButtons[state.Factions.IndexOf(faction)].Disabled = true;
            });
            factionButtons.Add(choose); box.AddChild(choose); factions.AddChild(TarkovTheme.Panel(box));
        }
        if (factionButtons.Count > 0) factionButtons[0].Disabled = true;
        selection.AddChild(factions);
        _factions.AddChild(TarkovTheme.Scroll(selection));
        var factionActions = TarkovTheme.Row(10);
        factionActions.AddChild(TarkovTheme.Button(Loc.GetString("ts-entry-back"), () =>
        { _factions.Visible = false; _identity.Visible = true; introPanel.Visible = true; }));
        factionActions.AddChild(TarkovTheme.Button(Loc.GetString("ts-entry-next-kit"), () =>
        { _factions.Visible = false; _choices.Visible = true; }, true));
        _factions.AddChild(factionActions); _content.AddChild(_factions);
        _choices.Visible = false; _choices.VerticalExpand = true;
        _choices.AddChild(TarkovTheme.Label(Loc.GetString("ts-entry-step-equipment")));
        selection = TarkovTheme.Column(10);
        selection.AddChild(TarkovTheme.Label(Loc.GetString("ts-entry-kit"), true));
        var kits = new TarkovColumns(10); var kitButtons = new List<TarkovButton>();
        _kit = state.Kits.FirstOrDefault()?.Id ?? "";
        foreach (var kit in state.Kits)
        {
            var box = TarkovTheme.Column(8); var title = TarkovTheme.Row(8);
            title.AddChild(TarkovTheme.Icon(kit.Prototype, _entities, 48)); title.AddChild(TarkovTheme.Label(kit.Name));
            box.AddChild(title); box.AddChild(TarkovTheme.Paragraph(kit.Detail));
            var choose = TarkovTheme.Button(Loc.GetString("ts-select"), () =>
            {
                _kit = kit.Id;
                foreach (var button in kitButtons) button.Disabled = false;
                kitButtons[state.Kits.IndexOf(kit)].Disabled = true;
            });
            kitButtons.Add(choose); box.AddChild(choose); kits.AddChild(TarkovTheme.Panel(box));
        }
        if (kitButtons.Count > 0) kitButtons[0].Disabled = true;
        selection.AddChild(kits);
        selection.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-entry-kit-note")));
        _choices.AddChild(TarkovTheme.Scroll(selection));
        _choices.AddChild(TarkovTheme.Paragraph(Loc.GetString("ts-entry-rules")));
        var actions = TarkovTheme.Row(10);
        actions.AddChild(TarkovTheme.Button(Loc.GetString("ts-entry-back-faction"), () => { _choices.Visible = false; _factions.Visible = true; }));
        _enter = TarkovTheme.Button(Loc.GetString("ts-entry-confirm"), () =>
        {
            if (_editor?.Profile == null || _state?.RulesAccepted != true) return;
            _send(new TarkovRequestEvent { Action = TarkovAction.Create, Profile = _editor.Profile,
                Id = _faction, Extra = _kit, AcceptedRules = true });
        }, true);
        _enter.SizeFlagsStretchRatio = 2; _enter.MinHeight = 44; actions.AddChild(_enter);
        _choices.AddChild(actions); _content.AddChild(_choices);
    }
}
