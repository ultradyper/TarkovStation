// SPDX-FileCopyrightText: 2025 Cinkafox <70429757+Cinkafox@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Kirill <kirill@example.com>
// SPDX-FileCopyrightText: 2025 ReserveBot <211949879+ReserveBot@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Client.UserInterface.Systems.Chat;
using Content.Shared._White.Bark;
using Content.Shared._White.Bark.Components;
using Content.Shared._White.Bark.Systems;
using Content.Shared._White.CCVar;
using Content.Shared.Chat;
using Robust.Client.Audio;
using Robust.Client.UserInterface;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Prototypes;
using Content.Shared.Speech;

namespace Content.Client._White.Bark;

public sealed class BarkSystem : SharedBarkSystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly AudioSystem _sharedAudio = default!;
    [Dependency] private readonly IUserInterfaceManager _uiManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;

    private bool _clientSideEnabled;
    private float _volume;
    private int _maxBarkCount;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesOutsidePrediction = true;

        _uiManager.GetUIController<ChatUIController>().MessageAdded += OnMessageAdded;

        _cfg.OnValueChanged(WhiteCVars.BarkVolume, OnBarkVolumeChanged, true);
        _cfg.OnValueChanged(WhiteCVars.VoiceType, OnVoiceTypeChanged, true);
        _cfg.OnValueChanged(WhiteCVars.BarkLimit, OnBarkLimitChanged, true);
        SubscribeNetworkEvent<EntityBarkEvent>(OnEntityBark);
    }

    public override void Shutdown()
    {
        _uiManager.GetUIController<ChatUIController>().MessageAdded -= OnMessageAdded;
        _cfg.UnsubValueChanged(WhiteCVars.BarkVolume, OnBarkVolumeChanged);
        _cfg.UnsubValueChanged(WhiteCVars.VoiceType, OnVoiceTypeChanged);
        _cfg.UnsubValueChanged(WhiteCVars.BarkLimit, OnBarkLimitChanged);
    }

    private void OnMessageAdded(ChatMessage message)
    {
        if (message.Channel is not (ChatChannel.Local or ChatChannel.Whisper))
            return;

        var ent = GetEntity(message.SenderEntity);
        if (!TryComp<BarkComponent>(ent, out var comp))
            return;

        Bark(new(ent, comp), message.Message, message.Channel == ChatChannel.Whisper);
    }

    private void OnVoiceTypeChanged(CharacterVoiceType voice) => _clientSideEnabled = voice == CharacterVoiceType.Bark;
    private void OnBarkVolumeChanged(float volume) => _volume = SharedAudioSystem.GainToVolume(volume); // Reserve fix, + SharedAudioSystem.GainToVolume
    private void OnBarkLimitChanged(int count) => _maxBarkCount = count;

    private void OnEntityBark(EntityBarkEvent ev)
    {
        var ent = GetEntity(ev.Entity);
        if (!TryComp<BarkComponent>(ent, out var comp))
            return;

        Bark(new(ent, comp), ev.Barks);
    }

    public override void Bark(Entity<BarkComponent> entity, List<BarkData> barks)
    {
        // Reserve edit start - If barks are disabled, play regular speech sounds instead
        if (!_clientSideEnabled)
        {
            PlayFallbackSpeechSound(entity);
            return;
        }
        // Reserve edit end

        if (TryComp<BarkSourceComponent>(entity, out var sourceComponent))
            RemComp(entity, sourceComponent);

        if (_maxBarkCount > 0 && barks.Count > _maxBarkCount)
            barks.RemoveRange(_maxBarkCount, barks.Count - _maxBarkCount);

        sourceComponent = AddComp<BarkSourceComponent>(entity);
        sourceComponent.Barks = new(barks);
        sourceComponent.ResolvedSound = entity.Comp.VoiceData.BarkSound;
    }

    // Reserve edit start - Fallback to speech sounds when bark is disabled
    private void PlayFallbackSpeechSound(EntityUid entity)
    {
        // Play a simple speech sound when bark is disabled
        // This uses the entity's speech sound if available
        if (!TryComp<SpeechComponent>(entity, out var speechComp))
            return;

        // Get speech sound prototype
        if (speechComp.SpeechSounds == null)
            return;

        if (!_prototypeManager.TryIndex<SpeechSoundsPrototype>(speechComp.SpeechSounds, out var prototype))
            return;

        // Play the say sound (default speech sound)
        if (prototype.SaySound != null)
        {
            _sharedAudio.PlayEntity(
                prototype.SaySound,
                Filter.Local(),
                entity,
                true,
                prototype.SaySound.Params);
        }
    }
    // Reserve edit end

    private void Bark(EntityUid entity, SoundSpecifier soundSpecifier, BarkData currentBark)
    {
        if (!currentBark.Enabled)
            return;

        _sharedAudio
            .PlayEntity(
                soundSpecifier,
                Filter.Local(),
                entity,
                true,
                new AudioParams()
                    .WithPitchScale(currentBark.Pitch)
                    .WithVolume(currentBark.Volume + _volume));
    }

    public override void Update(float frameTime)
    {
        if (!_gameTiming.IsFirstTimePredicted)
            return;

        var query = EntityQueryEnumerator<BarkSourceComponent>();
        while (query.MoveNext(out var uid, out var barkSource))
        {
            if (barkSource.CurrentBark is null)
            {
                if (!barkSource.Barks.TryDequeue(out var barkData))
                {
                    RemComp(uid, barkSource);
                    continue;
                }

                barkSource.CurrentBark = barkData;
            }

            if (barkSource.CurrentBark.Value.Pause <= barkSource.BarkTime)
            {
                barkSource.BarkTime = 0;
                Bark(uid, barkSource.ResolvedSound, barkSource.CurrentBark.Value);
                barkSource.CurrentBark = null;
            }
            else
            {
                barkSource.BarkTime += frameTime;
            }
        }
    }
}
