// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Content.Shared._TarkovStation;

/// <summary>The physical hub service authorized to perform an action. Cancellation never traps escrow.</summary>
public static class TarkovServiceAccess
{
    public static string PageFor(TarkovAction action) => action switch
    {
        TarkovAction.Buy or TarkovAction.BuyKit or TarkovAction.EmergencyKit or TarkovAction.Sell => "shop",
        TarkovAction.Deposit or TarkovAction.Withdraw => "stash",
        TarkovAction.Invite or TarkovAction.AcceptInvite or TarkovAction.LeaveParty or TarkovAction.Ready or TarkovAction.TestPartner => "raids",
        TarkovAction.CreateContract or TarkovAction.AcceptContract or TarkovAction.DeliverContract
            or TarkovAction.ConfirmContract or TarkovAction.CancelContract => "contracts",
        TarkovAction.OfferTrade or TarkovAction.AcceptTrade or TarkovAction.SetTradeMoney
            or TarkovAction.AddTradeItem or TarkovAction.RemoveTradeItem => "trade",
        _ => "",
    };
}
