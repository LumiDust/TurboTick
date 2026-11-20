using System;
using System.Collections.Generic;
using System.Linq;
using TurboTick.Networking;
using UnityEngine;
using Verse;

namespace TurboTick;

/// <summary>The window that lets an owner change what a visitor may do in their settlement.
///
/// <para><b>Why the feature is not usable without this.</b> The grant table is data, and until now the only ways to write
/// it were a save edit and a console command. That means the entire ownership layer answered from
/// <see cref="SettlementGrant.Default"/> for every player: the four visitor-survival permissions (doors, care, benches,
/// food), forever. A rule nobody can
/// change is indistinguishable from a hard-coded restriction, and the refusals it produces read as bugs - which is why the
/// overlay and the refusal text had to come first.</para>
///
/// <para><b>Why a plain <c>Window</c> and not an inspect tab.</b> The thing being edited is not a <c>Thing</c>: it is a
/// rectangle of land with an owner. An inspect string belongs to a selected object, and there is no object. A window opened
/// from the settlement's own gizmo keeps the editor next to the thing it edits and needs no selection protocol.</para>
///
/// <para><b>Why the flags are edited as checkboxes over <see cref="SettlementGrant"/>, one per bit.</b> The table is stored
/// as an int mask, and a UI that offered named presets ("visitor", "ally", "full") would make the stored value a second
/// concept that has to be kept in step with the checkboxes. One checkbox per flag means the window shows exactly what the
/// mask holds, and a flag added later appears without touching this file's logic - only its label list.</para>
internal sealed class SettlementGrantWindow : Window
{
    /// <summary>The flags the window offers, with the label the player reads. Ordered from least to most consequential, so
    /// the list reads as a ladder rather than as a bit order.</summary>
    private static readonly (SettlementGrant Flag, string Label, string Tip)[] editable =
    [
        (SettlementGrant.Doors, "通行", "穿过这个据点的门。"),
        (SettlementGrant.Care, "使用床位", "睡它的床、用它的医疗设施。"),
        (SettlementGrant.Alerts, "查看警报", "看到这个据点的警报。信息，不是资源。"),
        (SettlementGrant.Benches, "使用工作台", "用它的生产设施。"),
        (SettlementGrant.Items, "取用物品", "捡起地上散落的物资。"),
        (SettlementGrant.Food, "取用食物", "从它的仓库或地里拿吃的。"),
        (SettlementGrant.Equipment, "取用装备", "拿它的武器与护甲。"),
        (SettlementGrant.Storage, "存入仓库", "往它的仓库里放东西。"),
        (SettlementGrant.Repairs, "维修", "修它的建筑。"),
        (SettlementGrant.Construction, "建造与拆除", "在它的地上建造、拆除、卸载。"),
        // **Ten flags that had no checkbox until round 90.** Everything above was written when the layer was a sketch and
        // only the visitor's four were reachable; the ones below were added when the work, bill and joy gates started
        // asking for them, and every one of them is asked for by a gate that already exists:
        // `Cleaning`/`Firefighting`/`Hunting`/`Gathering`/`AnimalHandling` by the work gate since round 85, `Operations`
        // by the bill gate since round 89, `Furniture`/`BuildArea` by the `Use` and `Place` rows, `Traders` by the trade
        // guard, `Amenities` by the joy gate. A flag with a row and no checkbox is the same defect as a row with no gate:
        // the permission exists on paper and no player can grant it, so the gate refuses for a reason the owner cannot
        // fix. Ordered from least to most consequential so the list still reads as a ladder.
        (SettlementGrant.Cleaning, "清洁", "打扫它的地面。"),
        (SettlementGrant.Firefighting, "灭火", "扑灭它地上的火。"),
        (SettlementGrant.Amenities, "娱乐设施", "在它的地上使用台球桌、棋桌等娱乐与舒适设施。"),
        (SettlementGrant.Furniture, "家具", "坐它的椅子、用它的家具。"),
        (SettlementGrant.Hunting, "狩猎", "在它的地上打猎。"),
        (SettlementGrant.Gathering, "采集与采矿", "收割它的植物、开采它的岩石。"),
        (SettlementGrant.AnimalHandling, "动物", "驯服、训练、挤奶、剪毛它的动物。"),
        (SettlementGrant.Operations, "手术", "给它的病人做手术。"),
        (SettlementGrant.Traders, "交易", "和它的商队交易。"),
        (SettlementGrant.BuildArea, "规划区域", "在它的地上画区域、放建筑蓝图。"),
    ];

    private readonly SettlementData data;

    /// <summary>Which settlement's defaults are being edited. 0 means "the unaffiliated visitor", which is the one grant that
    /// is not per-settlement.</summary>
    private int selected;

    /// <summary>Which grant row is being edited: a per-settlement default, or a point grant to one player. Kept as a pair so
    /// the window can show both without a separate mode switch - a settlement has one default and one row per player who has
    /// ever been granted something.</summary>
    private bool editingDefaults = true;

    public SettlementGrantWindow(SettlementData data, int settlement)
    {
        this.data = data;
        selected = settlement;
        forcePause = true;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
        doCloseX = true;
    }

    public override Vector2 InitialSize => new(620f, 560f);

    public override void DoWindowContents(Rect inRect)
    {
        var listing = new Listing_Standard();
        listing.Begin(inRect);
        listing.Label("据点授权：谁能在这里做什么");
        listing.GapLine();

        // Settlement picker. A settlement with no owner is listed as such rather than hidden: the world's own settlements
        // are owned by nobody, and the player still needs to see what a visitor may do there.
        listing.Label("据点");
        foreach (var site in data.Sites)
        {
            if (site is null) continue;
            var label = site.OwnerPlayer == 0 ? $"{site.Name}（无主）" : $"{site.Name}（玩家 {site.OwnerPlayer}）";
            if (!listing.RadioButton(label, selected == site.Id)) continue;
            selected = site.Id;
            editingDefaults = true;
        }
        listing.Gap(8f);
        // The visitor default is a single grant that applies wherever a player has no point grant, so it needs its own row
        // rather than being one of the settlements.
        if (listing.RadioButton("无归属访客（对所有据点）", selected == 0))
        {
            selected = 0;
            editingDefaults = true;
        }
        listing.GapLine();

        if (selected == 0)
        {
            listing.Label($"无归属访客始终可以：{Describe(SettlementData.Unaffiliated)}");
            listing.Label("这一项是固定的：它决定一个还没归属任何据点的人能否活下来。");
            listing.End();
            return;
        }

        var site2 = data.Site(selected);
        if (site2 is null)
        {
            listing.Label("这个据点已经不存在了。");
            listing.End();
            return;
        }

        // Default vs point grant. Two rows rather than a dropdown because the difference is the concept the player has to
        // understand: the default applies to everybody, a point grant applies to one player.
        if (listing.RadioButton("默认（所有访客）", editingDefaults)) editingDefaults = true;
        foreach (var (player, home) in data.Members.ToList())
        {
            // The player id is shown with the settlement it resolves to, because the stored row is keyed by the
            // settlement and two players in one settlement therefore share a row. Saying only "玩家 2" would make that
            // shared row look like a personal one.
            var where = home == 0 ? "（无据点：写入会被拒绝）" : $"（按据点 {home} 记录）";
            if (listing.RadioButton($"玩家 {player} 的单独授权{where}", !editingDefaults && editingPlayer == player))
            {
                editingDefaults = false;
                editingPlayer = player;
            }
        }
        listing.GapLine();

        var current = editingDefaults
            ? (data.Defaults.TryGetValue(selected, out var stored) ? (SettlementGrant)stored : SettlementGrant.Default)
            : data.GrantsFor(editingPlayer, selected);
        listing.Label(editingDefaults ? "默认授权（改动立即对所有人生效）" : $"玩家 {editingPlayer} 的授权");
        var flags = (int)current;
        var changed = false;
        foreach (var (flag, label, tip) in editable)
        {
            var on = ((SettlementGrant)flags).HasFlag(flag);
            listing.CheckboxLabeled(label, ref on, tip);
            if (on == ((SettlementGrant)flags).HasFlag(flag)) continue;
            flags = on ? flags | (int)flag : flags & ~(int)flag;
            changed = true;
        }
        if (changed) Write(flags);
        listing.Gap(8f);
        listing.Label($"当前：{Describe((SettlementGrant)flags)}");
        listing.End();
    }

    /// <summary>Which player's point grant is being edited. Only meaningful when <see cref="editingDefaults"/> is false.</summary>
    private int editingPlayer;

    /// <summary>Stores the edited mask and tells the peers.
    ///
    /// <para>Writes on every toggle rather than on a Save button on purpose: the player is looking at the overlay while they
    /// edit, and a pending change that has not been applied yet would make the drawn territory and the checkboxes disagree.
    /// The table is small and bounded, so a write per click is cheap - and it is the same write the version counter is
    /// designed for.</para></summary>
    private void Write(int flags)
    {
        if (editingDefaults)
        {
            data.Defaults[selected] = flags;
        }
        else
        {
            // **`To` is a settlement id, so a player row is written against the settlement whose pawns act for them
            // (round 88).** The window used to write the raw player id here while `GrantsForPawn` read the same field as
            // a settlement id - so "player 2 may use my workbenches" became "settlement 2 may" whenever the two numbers
            // coincided, and the gates (which ask `GrantsForPawn`) acted on the collision. A player who belongs to no
            // settlement has nothing for the gates to key on, and is refused a point row here rather than written one
            // that nothing will read.
            var asSettlement = data.SettlementOf(editingPlayer);
            if (asSettlement == 0) return;
            var record = data.Grants.FirstOrDefault(row => row is not null && row.From == selected && row.To == asSettlement);
            if (record is null)
            {
                record = new SettlementGrantRecord { From = selected, To = asSettlement };
                data.Grants.Add(record);
            }
            record.Flags = flags;
        }
        // Replicas have to hear about it: a gate on another peer would otherwise keep answering from the old mask, which is
        // the one divergence this whole layer exists to prevent.
        SettlementSync.Touch();
    }

    /// <summary>What a grant lets somebody do, as a sentence. Built from the same flag list the checkboxes use, so the
    /// summary cannot drift from what the window offers.</summary>
    private static string Describe(SettlementGrant grant)
    {
        var parts = editable.Where(entry => grant.HasFlag(entry.Flag)).Select(entry => entry.Label).ToList();
        return parts.Count == 0 ? "什么都不能做" : string.Join("、", parts);
    }
}
