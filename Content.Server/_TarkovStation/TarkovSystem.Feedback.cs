// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._TarkovStation;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;

namespace Content.Server._TarkovStation;

public sealed partial class TarkovSystem
{
    [Dependency] private SharedAudioSystem _feedbackAudio = default!;

    /// <summary>Private, server-confirmed feedback. Passive UI polling never calls this method.</summary>
    private void Feedback(string user, TarkovFeedback cue, string message = "", string sector = "")
    {
        if (!TrySession(user, out var session) || !session.Channel.IsConnected) return;
        var path = cue switch
        {
            TarkovFeedback.Money => "/Audio/Effects/kaching.ogg",
            TarkovFeedback.Storage => "/Audio/Effects/rustle5.ogg",
            TarkovFeedback.Error or TarkovFeedback.ExtractionCancelled => "/Audio/Machines/custom_deny.ogg",
            TarkovFeedback.Departure => "/Audio/Effects/teleport_departure.ogg",
            TarkovFeedback.ExtractionStarted => "/Audio/Machines/high_tech_confirm.ogg",
            TarkovFeedback.ExtractionTick => "/Audio/Machines/quickbeep.ogg",
            TarkovFeedback.Extracted => "/Audio/Machines/chime.ogg",
            TarkovFeedback.RaidEvent => "/Audio/Machines/high_tech_confirm.ogg",
            TarkovFeedback.Warning => "/Audio/Misc/notice2.ogg",
            TarkovFeedback.Death => "/Audio/Machines/twobeep.ogg",
            _ => "/Audio/Machines/beep.ogg",
        };
        _feedbackAudio.PlayGlobal(new SoundPathSpecifier(path), session, AudioParams.Default.WithVolume(-8f));
        RaiseNetworkEvent(new TarkovFeedbackEvent { Cue = cue, Message = message, Sector = sector }, session);
    }

    private static TarkovFeedback ActionFeedback(TarkovAction action) => action switch
    {
        TarkovAction.Buy or TarkovAction.BuyKit or TarkovAction.Sell or TarkovAction.ConfirmContract
            or TarkovAction.DeliverContract or TarkovAction.AcceptTrade => TarkovFeedback.Money,
        TarkovAction.Deposit or TarkovAction.Withdraw => TarkovFeedback.Storage,
        _ => TarkovFeedback.Confirm,
    };
}
