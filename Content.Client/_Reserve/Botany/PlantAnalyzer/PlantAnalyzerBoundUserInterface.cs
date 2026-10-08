// SPDX-FileCopyrightText: 2025 Kirill <kirill@example.com>
// SPDX-FileCopyrightText: 2025 ReserveBot <211949879+ReserveBot@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Tim <timfalken@hotmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

// based on https://github.com/space-wizards/space-station-14/pull/34600
using Content.Shared._Reserve.Botany.PlantAnalyzer;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._Reserve.Botany.PlantAnalyzer;

[UsedImplicitly]
public sealed class PlantAnalyzerBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private PlantAnalyzerWindow? _window;

    public PlantAnalyzerBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<PlantAnalyzerWindow>();
        _window.Title = EntMan.GetComponent<MetaDataComponent>(Owner).EntityName;
        _window.Print.OnPressed += _ => Print();
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        if (_window == null)
            return;

        if (message is not PlantAnalyzerScannedUserMessage cast)
            return;

        _window.Populate(cast);
    }

    private void Print()
    {
        SendMessage(new PlantAnalyzerPrintMessage());
        if (_window != null)
            _window.Print.Disabled = true;
    }
}

