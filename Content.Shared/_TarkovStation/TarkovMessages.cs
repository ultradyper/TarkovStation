// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Preferences;
using Robust.Shared.Serialization;

namespace Content.Shared._TarkovStation;

[Serializable, NetSerializable]
public enum TarkovFeedback : byte
{
    Confirm, Money, Storage, Error, Departure, ExtractionStarted, ExtractionCancelled,
    ExtractionTick, Extracted, Warning, Death,
}

/// <summary>One-shot notification, separate from periodically refreshed interface state.</summary>
[Serializable, NetSerializable]
public sealed class TarkovFeedbackEvent : EntityEventArgs
{
    public TarkovFeedback Cue;
}

[Serializable, NetSerializable]
public enum TarkovAction : byte
{
    Refresh, Create, Buy, BuyKit, Deposit, Withdraw, Sell, Invite, AcceptInvite, LeaveParty,
    Ready, CancelReady, Extract, CreateContract, AcceptContract, DeliverContract, ConfirmContract,
    CancelContract, OfferTrade, AcceptTrade, CancelTrade, SetTradeMoney, AddTradeItem,
    RemoveTradeItem, EmergencyKit, ReturnToHub, TestPartner, CloseService,
    AcceptRules,
}

/// <summary>Requests carry intent only. Sender, prices, ownership and outcomes come from the server.</summary>
[Serializable, NetSerializable]
public sealed class TarkovRequestEvent : EntityEventArgs
{
    public TarkovAction Action;
    public string RequestId = "";
    public string Cycle = "";
    public string Id = "";
    public string Text = "";
    public string Extra = "";
    public int Amount;
    public HumanoidCharacterProfile? Profile;
    public bool AcceptedRules;
}

[Serializable, NetSerializable]
public sealed class TarkovRow
{
    public string Id = "";
    public string Name = "";
    public string Detail = "";
    public string Prototype = "";
    public string Owner = "";
    public long Price;
    public bool Flag;
    public string Kind = "";
    public string State = "";
    public int Count;
    public int Rarity;
    public string Category = "";
}

/// <summary>A private UI snapshot, sent only to its owning authenticated session.</summary>
[Serializable, NetSerializable]
public sealed class TarkovStateEvent : EntityEventArgs
{
    public bool Enabled;
    public bool NeedsCharacter;
    public bool RulesAccepted;
    public string Cycle = "";
    public long RemainingSeconds;
    public string User = "";
    public string CharacterName = "";
    public string Faction = "";
    public string Branch = "";
    public string Location = "hub";
    public string Message = "";
    public string OpenPage = "";
    public long Balance;
    public long Reserved;
    public long QueueSeconds;
    public long RaidSeconds;
    public bool ActiveRaid;
    public long ActiveRaidSeconds;
    public TarkovDayPhase DayPhase;
    public long ExtractionSeconds;
    public bool Ready;
    public bool TestMode;
    public string QueuePhase = "idle";
    public string QueueNotice = "";
    public int ReadyCount;
    public int PartyCount;
    public string ServicePage = "";
    public NetEntity? ServiceSource;
    public List<TarkovRow> Factions = new();
    public List<TarkovRow> Kits = new();
    public List<TarkovRow> Shop = new();
    public List<TarkovRow> Catalogue = new();
    public List<TarkovRow> Stash = new();
    public List<TarkovRow> Inventory = new();
    public List<TarkovRow> Players = new();
    public List<TarkovRow> Party = new();
    public List<TarkovRow> Invites = new();
    public List<TarkovRow> Contracts = new();
    public List<TarkovRow> Leaderboard = new();
    public List<TarkovRow> LastResults = new();
    public List<TarkovRow> Trade = new();
    public string TradeId = "";
    public bool TradeAccepted;
    public bool PartnerAccepted;
}
