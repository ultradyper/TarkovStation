using Content.Shared._White.Bark;
using Robust.Shared.Configuration;

namespace Content.Shared._White.CCVar;

public sealed partial class WhiteCVars
{
    /// <summary>
    /// Default volume setting of bark sound
    /// </summary>
    public static readonly CVarDef<float> BarkVolume =
        CVarDef.Create("bark.volume", 0.3f, CVar.CLIENTONLY | CVar.ARCHIVE); // Reserve edit

    public static readonly CVarDef<int> BarkLimit =
        CVarDef.Create("bark.limit",12, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// Voice type for characters in-game
    /// </summary>
    public static readonly CVarDef<CharacterVoiceType> VoiceType =
        CVarDef.Create("voice.type", CharacterVoiceType.Bark, CVar.CLIENTONLY | CVar.ARCHIVE);
}
