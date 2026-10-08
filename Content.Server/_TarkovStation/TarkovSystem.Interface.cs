// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server._TarkovStation.Persistence;
using Content.Shared._TarkovStation;
using Content.Shared._TarkovStation.Components;
using Content.Shared._TarkovStation.Prototypes;
using Content.Shared.Interaction;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._TarkovStation;

public sealed partial class TarkovSystem
{
    private void OnRequest(TarkovRequestEvent request, EntitySessionEventArgs args)
    {
        if (!Enabled)
            return;
        EnsureRepository();
        var user = args.SenderSession.UserId.ToString();
        if (_repository == null)
            return;
        if (request.Action != TarkovAction.CloseService && _requestTimes.TryGetValue(user, out var prior)
            && _timing.CurTime - prior < TimeSpan.FromMilliseconds(120))
        {
            if (request.Action != TarkovAction.Refresh) SendState(args.SenderSession, "tarkov-error-too-fast");
            return;
        }
        _requestTimes[user] = _timing.CurTime;
        var data = _repository.Read();
        if (request.Action == TarkovAction.Refresh)
        {
            SendState(args.SenderSession);
            return;
        }
        if (request.Cycle != data.Cycle || request.RequestId is not { Length: >= 8 and <= 96 }
            || request.Id == null || request.Extra == null || request.Text == null || request.Id.Length > 64
            || request.Extra.Length > 128 || request.Text.Length > 500 || request.Amount < 0 || request.Amount > 1_000_000)
        {
            SendState(args.SenderSession, "tarkov-error-request");
            return;
        }
        string? error;
        try
        {
            error = HandleRequest(args.SenderSession, user, request, data);
        }
        catch (ArgumentException exception)
        {
            error = exception.Message.StartsWith("tarkov-") ? exception.Message : "tarkov-error-request";
        }
        catch (Exception exception)
        {
            Log.Error($"Tarkov action {request.Action} failed: {exception}");
            error = "tarkov-error-internal";
        }
        if (request.Action != TarkovAction.CloseService && (request.Action != TarkovAction.Extract || error != null))
            Feedback(user, error == null ? ActionFeedback(request.Action) : TarkovFeedback.Error);
        if (error == null && _repository != null)
        {
            // A contract or trade may pay somebody other than the actor. Confirm their wallet change privately too.
            foreach (var recipient in _repository.Read().Accounts.Values)
                if (recipient.User != user && data.Accounts.TryGetValue(recipient.User, out var previous)
                    && previous.Balance != recipient.Balance) Feedback(recipient.User, TarkovFeedback.Money);
            if (request.Action is TarkovAction.Invite or TarkovAction.OfferTrade)
                Feedback(request.Id, TarkovFeedback.Confirm);
        }
        SendState(args.SenderSession, error ?? "tarkov-success");
    }

    private string? HandleRequest(ICommonSession session, string user, TarkovRequestEvent request, TarkovData before)
    {
        if (request.Action == TarkovAction.AcceptRules)
        {
            if (!request.AcceptedRules) return "tarkov-error-rules";
            _rulesAccepted.Add(before.Cycle + ":" + user);
            return null;
        }
        if (request.Action == TarkovAction.AcceptGuide)
        {
            if (!_rulesAccepted.Contains(before.Cycle + ":" + user)) return "tarkov-error-rules";
            _guideRead.Add(before.Cycle + ":" + user);
            return null;
        }
        if (request.Action == TarkovAction.Create)
        {
            if (!_guideRead.Contains(before.Cycle + ":" + user)) return "tarkov-error-guide";
            if (!request.AcceptedRules || !_rulesAccepted.Contains(before.Cycle + ":" + user)) return "tarkov-error-rules";
            if (_hub == null || request.Profile == null || !_proto.HasIndex<TarkovFactionPrototype>(request.Id)
                || !_proto.TryIndex<TarkovKitPrototype>(request.Extra, out var kit) || kit.Emergency)
                return "tarkov-error-starting";
            var profile = SanitizeProfile(request.Profile, session);
            var error = Write(user, request.RequestId, "create", d => TarkovEconomy.Create(d, user, profile.Name,
                WriteProfile(profile), request.Id, request.Extra, Math.Clamp(_cfg.GetCVar(TarkovCVars.StartingMoney), 0, 100000)));
            if (error == null && _repository != null)
                SpawnHubPlayer(session, _repository.Read().Accounts[user]);
            return error;
        }
        if (!before.Accounts.TryGetValue(user, out var account) || !account.Created || FindPlayer(user) is not { } body)
            return "tarkov-error-character";
        if (request.Action == TarkovAction.CloseService)
        {
            Comp<TarkovPlayerComponent>(body).ActiveTerminal = null;
            CancelUserTrade(user);
            return null;
        }
        if (request.Action == TarkovAction.Extract)
            return StartExtraction(body);
        if (account.Location != "hub")
            return "tarkov-error-hub";
        if (request.Action == TarkovAction.CancelReady)
        {
            CancelReady(user);
            return null;
        }
        if (PendingDeployment(user) || ((request.Action is TarkovAction.OfferTrade or TarkovAction.Invite or TarkovAction.AcceptInvite)
            && PendingDeployment(request.Id))) return "tarkov-error-preparing";
        var requiredService = TarkovServiceAccess.PageFor(request.Action);
        if (requiredService != "" && ActiveService(body) != requiredService) return "tarkov-error-service";
        CapturePlayer(user);
        switch (request.Action)
        {
            case TarkovAction.Buy: return Buy(user, request.Id, request.RequestId);
            case TarkovAction.BuyKit: return DeliverKit(user, request.Id, request.RequestId);
            case TarkovAction.EmergencyKit: return DeliverKit(user, "Emergency", request.RequestId);
            case TarkovAction.Deposit: return Deposit(body, user, request.Id, request.RequestId);
            case TarkovAction.Withdraw: return Withdraw(body, user, request.Id, request.RequestId);
            case TarkovAction.Sell: return Write(user, request.RequestId, "sell", d => TarkovEconomy.Sell(d, user, request.Id));
            case TarkovAction.Invite: return Invite(user, request.Id, request.RequestId);
            case TarkovAction.AcceptInvite: return AcceptInvite(user, request.Id, request.RequestId);
            case TarkovAction.LeaveParty:
                CancelReady(user, "tarkov-queue-party-changed");
                return Write(user, request.RequestId, "leave-party", d => { d.Accounts[user].Party = ""; return null; });
            case TarkovAction.Ready: return Ready(user);
            case TarkovAction.CancelReady: CancelReady(user); return null;
            case TarkovAction.CreateContract: return CreateContract(user, request);
            case TarkovAction.AcceptContract:
                return Write(user, request.RequestId, "accept-contract", d => TarkovEconomy.AcceptContract(d, user, request.Id, Utc));
            case TarkovAction.CancelContract:
                return Write(user, request.RequestId, "cancel-contract", d => TarkovEconomy.CancelContract(d, user, request.Id));
            case TarkovAction.DeliverContract: return DeliverContract(user, request.Id, request.RequestId);
            case TarkovAction.ConfirmContract:
                return Write(user, request.RequestId, "confirm-contract", d =>
                {
                    if (!d.Contracts.TryGetValue(request.Id, out var c) || c.Issuer != user || c.Kind != "custom"
                        || c.Status != "accepted" || c.EndsUtc <= Utc) return "tarkov-error-contract";
                    TarkovEconomy.Pay(d, c);
                    return null;
                });
            case TarkovAction.OfferTrade:
                if (!before.Accounts.TryGetValue(request.Id, out var tradePartner)
                    || (tradePartner.TestBot ? tradePartner.TestTarget || tradePartner.TestOwner != user : !AtTradeService(request.Id, body))) return "tarkov-error-service";
                return Write(user, request.RequestId, "start-trade", d => TarkovEconomy.StartTrade(d, user, request.Id, Guid.NewGuid().ToString("N")));
            case TarkovAction.SetTradeMoney:
                return Write(user, request.RequestId, "trade-money", d => TarkovEconomy.TradeMoney(d, user, request.Id, request.Amount));
            case TarkovAction.AddTradeItem:
            case TarkovAction.RemoveTradeItem:
                return Write(user, request.RequestId, "trade-item", d => TarkovEconomy.TradeItem(d, user, request.Id,
                    request.Extra, request.Action == TarkovAction.AddTradeItem));
            case TarkovAction.AcceptTrade:
                return Write(user, request.RequestId, "accept-trade", d => TarkovEconomy.AcceptTrade(d, user, request.Id));
            case TarkovAction.CancelTrade:
                return Write(user, request.RequestId, "cancel-trade", d => TarkovEconomy.CancelTrade(d, user, request.Id));
            case TarkovAction.TestPartner: return CreateTestPartner(user);
        }
        return "tarkov-error-request";
    }

    private string? Invite(string user, string target, string operation)
        => Write(user, operation, "invite", d =>
        {
            if (user == target || !d.Accounts.TryGetValue(target, out var b) || b.Location != "hub" || b.TestBot)
                return "tarkov-error-party";
            if (!b.Invites.Contains(user) && b.Invites.Count < 10) b.Invites.Add(user);
            return null;
        });

    private string? AcceptInvite(string user, string target, string operation)
        => Write(user, operation, "accept-invite", d =>
        {
            if (!d.Accounts.TryGetValue(target, out var leader) || leader.Location != "hub"
                || !d.Accounts[user].Invites.Contains(target)) return "tarkov-error-party";
            var party = leader.Party == "" ? Guid.NewGuid().ToString("N") : leader.Party;
            if (d.Accounts.Values.Count(a => a.Party == party) >= 4) return "tarkov-error-party-full";
            leader.Party = party;
            d.Accounts[user].Party = party;
            d.Accounts[user].Invites.Clear();
            return null;
        });

    private void CancelUserTrade(string user)
    {
        if (_repository == null) return;
        foreach (var trade in _repository.Read().Trades.Values.Where(t => t.Status == "open" && (t.A == user || t.B == user)))
            Write(user, Guid.NewGuid().ToString("N"), "disconnect-trade", d => TarkovEconomy.CancelTrade(d, user, trade.Id));
    }

    private string? CreateContract(string user, TarkovRequestEvent request)
    {
        var parts = request.Extra.Split(':');
        var kind = parts.ElementAtOrDefault(0) ?? "";
        var count = int.TryParse(parts.ElementAtOrDefault(1), out var quantity) ? quantity : 1;
        if ((kind == "item" && Goods(request.Id) == null) || (kind == "kill" && (_repository == null
            || !_repository.Read().Accounts.ContainsKey(request.Id) || request.Id == user)))
            return "tarkov-error-contract";
        return Write(user, request.RequestId, "create-contract", d => TarkovEconomy.CreateContract(d, new TarkovContract
        {
            Issuer = user, Kind = kind, Target = request.Id, Text = request.Text,
            Reward = request.Amount, Count = count, EndsUtc = Math.Min(d.EndsUtc, Utc + 3600),
        }, Utc));
    }

    private string? DeliverContract(string user, string id, string operation)
        => Write(user, operation, "deliver-contract", d =>
        {
            if (!d.Contracts.TryGetValue(id, out var c) || c.Assignee != user || c.Kind != "item"
                || c.Status != "accepted" || c.EndsUtc <= Utc) return "tarkov-error-contract";
            var items = d.Items.Values.Where(i => i.Owner == user && i.Location == "stash" && i.Parent == ""
                && Goods(i.Prototype)?.Product.Id == c.Target && !i.Emergency && i.FoundRaid != "").Take(c.Count).ToArray();
            if (items.Length != c.Count) return "tarkov-error-delivery";
            foreach (var item in items) TarkovEconomy.TransferTree(d, item.Id, c.Issuer);
            TarkovEconomy.Pay(d, c);
            return null;
        });

    private void SendState(ICommonSession session, string message = "", string page = "")
    {
        if (_repository == null || !session.Channel.IsConnected) return;
        var d = _repository.Read();
        var user = session.UserId.ToString();
        d.Accounts.TryGetValue(user, out var account);
        var state = new TarkovStateEvent
        {
            Enabled = Enabled, NeedsCharacter = account?.Created != true, Cycle = d.Cycle,
            RulesAccepted = account?.Created == true || _rulesAccepted.Contains(d.Cycle + ":" + user),
            GuideRead = account?.Created == true || _guideRead.Contains(d.Cycle + ":" + user),
            RemainingSeconds = Math.Max(0, d.EndsUtc - Utc), User = user, Message = message, OpenPage = page,
            CharacterName = account?.Name ?? "", Faction = account?.Faction ?? "", Branch = account?.Branch ?? "",
            Balance = account?.Balance ?? 0, Reserved = account?.Reserved ?? 0, Location = account?.Location ?? "hub",
            Ready = _ready.Contains(user) || PendingDeployment(user), QueueSeconds = _queueEnds == null ? 0 : Math.Max(1, (long)(_queueEnds.Value - _timing.CurTime).TotalSeconds),
            TestMode = _cfg.GetCVar(TarkovCVars.TestBots),
        };
        var plan = TarkovRaidPlan.Create(d.Cycle, d.RaidSequence);
        state.RaidRadius = plan.Radius;
        state.RaidBiome = plan.Biome;
        state.RaidEvent = plan.Event;
        state.DayPhase = TarkovRaidConditions.Phase(d.RaidSequence);
        if (CurrentRaid() is { } activeRaid)
        {
            state.ActiveRaid = true;
            state.DayPhase = activeRaid.Comp.DayPhase;
            state.RaidRadius = activeRaid.Comp.Radius;
            state.RaidBiome = activeRaid.Comp.Biome;
            state.RaidEvent = activeRaid.Comp.EventKind;
            state.EventStage = activeRaid.Comp.EventStage;
            state.EventSeconds = Math.Max(0, (long)(activeRaid.Comp.EventAt - _timing.CurTime).TotalSeconds);
            state.EventSector = EventSector(activeRaid.Comp.EventPosition);
            state.RaidReentryBlocked = activeRaid.Comp.DeadParticipants.Contains(user);
            state.ActiveRaidSeconds = HasComp<TarkovGenerationComponent>(activeRaid.Owner) ? 0
                : Math.Max(0, (long)(activeRaid.Comp.EndsAt - _timing.CurTime).TotalSeconds);
        }
        if (FindPlayer(user) is { } queueBody)
        {
            var pc = Comp<TarkovPlayerComponent>(queueBody);
            state.QueueNotice = pc.QueueNotice;
            state.ServicePage = ActiveService(queueBody);
            if (pc.ActiveTerminal is { } terminal) state.ServiceSource = GetNetEntity(terminal);
            if (page != "" && state.ServicePage != page) state.OpenPage = "";
        }
        var partyUsers = account == null ? Array.Empty<string>() : d.Accounts.Values
            .Where(a => a.Location == "hub" && (a.User == user || (account.Party != "" && a.Party == account.Party))).Select(a => a.User).ToArray();
        state.PartyCount = partyUsers.Length;
        state.ReadyCount = partyUsers.Count(_ready.Contains);
        state.QueuePhase = account?.Location == "raid" ? "raid" : PendingDeployment(user) ? "generating"
            : !_ready.Contains(user) ? "idle" : partyUsers.Any(u => !_ready.Contains(u)) ? "party" : "countdown";
        if (state.QueuePhase == "countdown" && _queueEnds != null && _queueEnds <= _timing.CurTime)
        {
            if (CurrentRaid() is { } preparing && HasComp<TarkovGenerationComponent>(preparing.Owner))
                state.QueuePhase = "capacity";
        }
        state.Factions = _proto.EnumeratePrototypes<TarkovFactionPrototype>().Select(f => new TarkovRow
            { Id = f.ID, Name = Loc.GetString(f.Name), Detail = Loc.GetString(f.Description) }).ToList();
        state.Kits = _proto.EnumeratePrototypes<TarkovKitPrototype>().Where(k => !k.Emergency).Select(k => new TarkovRow
            { Id = k.ID, Name = Loc.GetString(k.Name), Detail = Loc.GetString(k.Description), Price = k.Price, Prototype = k.Icon.Id }).ToList();
        state.Catalogue = _proto.EnumeratePrototypes<TarkovGoodsPrototype>().OrderBy(g => g.Buy).Select(g => new TarkovRow
        {
            Id = g.ID, Name = _proto.Index(g.Product).Name, Prototype = g.Product.Id, Price = g.Buy,
            Category = g.Category, Rarity = g.Rarity, Flag = g.Purchasable,
            Detail = Loc.GetString("tarkov-shop-prices", ("buy", g.Buy), ("sell", g.Sell)),
        }).ToList();
        state.Shop = state.Catalogue.Where(g => g.Flag).ToList();
        state.Stash = d.Items.Values.Where(i => i.Owner == user && i.Location == "stash" && i.Parent == "").Select(ItemRow).ToList();
        if (FindPlayer(user) is { } body)
        {
            state.Inventory = CarriedItems(body).Where(entry => TryComp<TarkovItemComponent>(entry.Item, out var item) && item.Id != "")
                .Select(entry =>
                {
                    var row = ItemRow(LiveItem(entry.Item));
                    if (entry.Path != "")
                    {
                        row.Kind = "contained";
                        var container = Loc.GetString("ts-item-container", ("container", entry.Path));
                        row.Detail = row.Detail == "" ? container : container + " · " + row.Detail;
                    }
                    return row;
                }).ToList();
            var pc = Comp<TarkovPlayerComponent>(body);
            state.ExtractionSeconds = pc.ExtractAt == TimeSpan.Zero ? 0 : Math.Max(0, (long)Math.Ceiling((pc.ExtractAt - _timing.CurTime).TotalSeconds));
            var raids = AllEntityQuery<TarkovRaidComponent>();
            while (raids.MoveNext(out _, out var raid))
                if (raid.Id == pc.Raid) state.RaidSeconds = Math.Max(0, (long)(raid.EndsAt - _timing.CurTime).TotalSeconds);
        }
        state.Players = d.Accounts.Values.Where(a => a.Created).Select(a => new TarkovRow
            { Id = a.User, Name = a.Name, Detail = FactionName(a.Faction) + " · " + Loc.GetString("tarkov-location-" + a.Location), Flag = a.Location == "hub" && ((a.TestBot && state.ServicePage == "trade") || (TrySession(a.User, out var online) && online.Status == Robust.Shared.Enums.SessionStatus.InGame))
                && (state.ServicePage != "trade" || (a.TestBot ? !a.TestTarget && a.TestOwner == user
                    : FindPlayer(user) is { } trader && AtTradeService(a.User, trader))) }).ToList();
        if (account != null)
        {
            state.Party = d.Accounts.Values.Where(a => a.User == user || (account.Party != "" && a.Party == account.Party))
                .Select(a => new TarkovRow { Id = a.User, Name = a.Name, Detail = FactionName(a.Faction), Flag = _ready.Contains(a.User) }).ToList();
            state.Invites = account.Invites.Where(d.Accounts.ContainsKey).Select(id => new TarkovRow { Id = id, Name = d.Accounts[id].Name }).ToList();
        }
        state.Contracts = d.Contracts.Values.Where(c => c.Status is "open" or "accepted" or "proof")
            .Select(c => new TarkovRow
            {
                Id = c.Id, Name = c.Text == "" ? Loc.GetString("tarkov-contract-kind-" + c.Kind) : c.Text,
                Kind = c.Kind, State = c.Status,
                Detail = ContractDetail(d, c), Owner = c.Issuer, Price = c.Reward, Flag = c.Assignee == user,
            }).ToList();
        state.Leaderboard = d.Accounts.Values.Where(a => !a.TestBot).OrderByDescending(a => TarkovEconomy.Wealth(d, a)).Select(a => new TarkovRow
            { Name = a.Name, Count = a.Kills, Price = TarkovEconomy.Wealth(d, a), Detail = Loc.GetString("tarkov-score-detail", ("kills", a.Kills), ("exits", a.Extractions)) }).ToList();
        state.LastResults = d.Results.Select(a => new TarkovRow
            { Name = a.Name, Count = a.Kills, Price = a.Wealth, Detail = Loc.GetString("tarkov-score-detail", ("kills", a.Kills), ("exits", a.Extractions)) }).ToList();
        var trade = d.Trades.Values.FirstOrDefault(t => t.Status == "open" && (t.A == user || t.B == user));
        if (trade != null)
        {
            state.TradeId = trade.Id;
            state.TradeAccepted = trade.A == user ? trade.AcceptedA : trade.AcceptedB;
            state.PartnerAccepted = trade.A == user ? trade.AcceptedB : trade.AcceptedA;
            foreach (var (owner, money, items) in new[] { (trade.A, trade.MoneyA, trade.ItemsA), (trade.B, trade.MoneyB, trade.ItemsB) })
            {
                state.Trade.Add(new TarkovRow { Owner = owner, Name = d.Accounts[owner].Name, Price = money });
                state.Trade.AddRange(items.Where(d.Items.ContainsKey).Select(i => { var row = ItemRow(d.Items[i]); row.Owner = owner; return row; }));
            }
        }
        RaiseNetworkEvent(state, session);
    }

    private string ContractDetail(TarkovData data, TarkovContract contract)
    {
        var detail = Loc.GetString("tarkov-contract-state-" + contract.Status);
        if (contract.Kind != "custom")
        {
            var target = data.Accounts.TryGetValue(contract.Target, out var account) ? account.Name
                : _proto.TryIndex<EntityPrototype>(contract.Target, out var product) ? product.Name : "";
            detail += " · " + target + (contract.Kind == "item" ? " ×" + contract.Count : "");
        }
        return detail + " · " + Loc.GetString("ts-price", ("price", contract.Reward)) + " · "
            + Loc.GetString("ts-contract-deadline", ("minutes", Math.Max(1, (contract.EndsUtc - Utc + 59) / 60)));
    }

    private string FactionName(string id)
        => _proto.TryIndex<TarkovFactionPrototype>(id, out var faction) ? Loc.GetString(faction.Name) : id;

    private TarkovRow ItemRow(TarkovStoredItem item)
    {
        var goods = Goods(item.Prototype);
        var detail = item.Emergency ? Loc.GetString("tarkov-loan") : item.FoundRaid != "" ? Loc.GetString("tarkov-found") : "";
        if (item.Condition != "") detail += (detail == "" ? "" : " · ") + item.Condition;
        return new TarkovRow
        {
            Id = item.Id, Name = item.Name + (item.Quantity > 1 ? " ×" + item.Quantity : ""),
            Prototype = item.Prototype, Price = item.Emergency ? 0 : item.Value, Detail = detail,
            Count = item.Quantity, Rarity = goods?.Rarity ?? 0, Category = goods?.Category ?? "ts-category-scrap",
        };
    }
}
