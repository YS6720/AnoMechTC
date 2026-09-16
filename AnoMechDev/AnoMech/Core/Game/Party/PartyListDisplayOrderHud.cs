using System;
using System.Collections.Generic;
using AnoMech.Core.SimObjects;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AnoMech.Core.Game.Party;

// Moves only the native row root. MainGroup and the simulated party remain in
// their existing slot/identity order, so native targeting and mechanic lookups
// never consume this display-only mapping.
internal sealed unsafe class PartyListDisplayOrderHud : IDisposable
{
    private const string AddonName = "_PartyList";
    private const int RowCount = PartyListDisplayOrderPlanner.RoleCount;

    private readonly PartyRole?[] slotRoles = new PartyRole?[RowCount];
    private readonly PartyRole[] displayOrder = new PartyRole[RowCount];
    private readonly int[] sourceToTarget = new int[RowCount];
    private readonly nint[] rowNodes = new nint[RowCount];
    private readonly nint[] currentNodes = new nint[RowCount];
    private readonly PartyListRowPosition[] currentPositions = new PartyListRowPosition[RowCount];
    private readonly PartyListRowPosition[] arrangedPositions = new PartyListRowPosition[RowCount];
    private readonly PartyListDisplayOrderLifecyclePlanner lifecycle = new();

    private bool mappingReady;
    private bool captured;
    private bool addonIsLive;
    private nint addonAddress;
    private long nextGeneration;
    private long generation;
    private string status = "未啟用";

    public string Status => status;

    public PartyListDisplayOrderHud()
    {
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnAddonPostSetup);
        // The game re-lays every row back to its native slot inside OnRequestedUpdate /
        // OnRefresh — which fire on each HP/MP change, i.e. nearly every frame of a
        // simulated fight — and that layout is drawn the same frame. Applying only in
        // PreDraw put our order back one callback too late, so the list flashed to the
        // native order for a frame on every update (maintainer 2026-09-16: 「一直抖動」).
        // Re-apply right after the game's own layout so the frame never shows it.
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnAddonPreDraw);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, AddonName, OnAddonPreDraw);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonName, OnAddonPreDraw);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnAddonPreFinalize);
    }

    public void Refresh(
        bool practicePartyActive,
        bool enabled,
        IReadOnlyList<PartyRole?>? currentSlotRoles,
        IReadOnlyList<PartyRole>? currentDisplayOrder)
    {
        var shouldRun = practicePartyActive && enabled;
        var valid = shouldRun
            && PartyListDisplayOrderPlanner.TryBuildSourceToTargetRows(
                currentSlotRoles, currentDisplayOrder, sourceToTarget);

        if (!valid)
        {
            var restored = !mappingReady && !captured || RestoreCurrentAndDiscard();
            mappingReady = false;
            Array.Clear(slotRoles);
            if (!enabled)
                SetStatus(restored ? "未啟用" : "未還原：_PartyList row node 不再有效");
            else if (!practicePartyActive)
                SetStatus("等待練習小隊");
            else
                SetStatus("未套用：排序設定無效或小隊slot對照不完整");
            return;
        }

        for (var i = 0; i < RowCount; i++)
        {
            slotRoles[i] = currentSlotRoles![i];
            displayOrder[i] = currentDisplayOrder![i];
        }
        mappingReady = true;
        if (!addonIsLive) SetStatus("等待 _PartyList");
    }

    public void Clear()
    {
        var restored = !captured || RestoreCurrentAndDiscard();
        mappingReady = false;
        addonAddress = 0;
        addonIsLive = false;
        Array.Clear(slotRoles);
        SetStatus(restored ? "未啟用" : "未還原：_PartyList row node 不再有效");
    }

    public void Dispose()
    {
        Clear();
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, AddonName, OnAddonPostSetup);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnAddonPreDraw);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, AddonName, OnAddonPreDraw);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, AddonName, OnAddonPreDraw);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, OnAddonPreFinalize);
    }

    private void OnAddonPostSetup(AddonEvent type, AddonArgs args)
    {
        addonIsLive = true;
        if (mappingReady) Apply(args);
    }

    private void OnAddonPreDraw(AddonEvent type, AddonArgs args)
    {
        if (!mappingReady) return;
        addonIsLive = true;
        Apply(args);
    }

    private void OnAddonPreFinalize(AddonEvent type, AddonArgs args)
    {
        var address = (nint)args.Addon.Address;
        if (address == 0 || address != addonAddress) return;

        // The callback marks the generation dead without touching its nodes.
        // Clear/Dispose may restore only while addonIsLive is still true.
        addonIsLive = false;
        DiscardGeneration(clearCurrentNodes: true);
        addonAddress = 0;
        SetStatus(mappingReady ? "等待 _PartyList 重建" : "未啟用");
    }

    private void Apply(AddonArgs args)
    {
        var address = (nint)args.Addon.Address;
        if (address == 0) return;

        var addon = (AddonPartyList*)address;
        if (addonAddress != address)
            BeginGeneration(address);

        if (!TryReadRows(addon, currentNodes, currentPositions))
        {
            SetStatus("未套用：_PartyList row node 尚未就緒");
            return;
        }

        if (!captured)
        {
            if (!lifecycle.Capture(generation, currentPositions))
            {
                SetStatus("未套用：無法保存 _PartyList row 座標");
                return;
            }
            CopyNodes(currentNodes, rowNodes);
            captured = true;
        }
        else if (!SameNodes(currentNodes, rowNodes))
        {
            // A rebuild under the same addon address is a new generation. Drop
            // the old baseline rather than restoring potentially freed nodes.
            BeginGeneration(address);
            if (!lifecycle.Capture(generation, currentPositions))
            {
                SetStatus("未套用：無法保存重建後的 row 座標");
                return;
            }
            CopyNodes(currentNodes, rowNodes);
            captured = true;
        }

        if (!lifecycle.TryArrange(
                generation,
                currentPositions,
                slotRoles,
                displayOrder,
                arrangedPositions,
                sourceToTarget))
        {
            SetStatus("未套用：slot→role對照無效");
            return;
        }

        // Validate every row before changing any row, so a missing node cannot
        // leave a partially reordered party list.
        for (var i = 0; i < RowCount; i++)
        {
            var node = (AtkResNode*)currentNodes[i];
            if (node == null) return;
        }

        for (var i = 0; i < RowCount; i++)
        {
            var node = (AtkResNode*)currentNodes[i];
            var target = arrangedPositions[i];
            if (node->X != target.X || node->Y != target.Y)
                node->SetPositionFloat(target.X, target.Y);
        }
        SetStatus("已套用");
    }

    private bool RestoreCurrentAndDiscard()
    {
        if (!captured)
        {
            DiscardGeneration(clearCurrentNodes: true);
            return true;
        }

        if (!addonIsLive || addonAddress == 0)
        {
            DiscardGeneration(clearCurrentNodes: true);
            return false;
        }

        var addon = (AddonPartyList*)addonAddress;
        if (!TryReadRows(addon, currentNodes, currentPositions)
            || !SameNodes(currentNodes, rowNodes)
            || !lifecycle.TryRestore(generation, currentPositions, out var restored))
        {
            DiscardGeneration(clearCurrentNodes: true);
            return false;
        }

        for (var i = 0; i < RowCount; i++)
        {
            var node = (AtkResNode*)currentNodes[i];
            if (node == null)
            {
                DiscardGeneration(clearCurrentNodes: true);
                return false;
            }
        }

        for (var i = 0; i < RowCount; i++)
        {
            var node = (AtkResNode*)currentNodes[i];
            var original = restored[i];
            if (node->X != original.X || node->Y != original.Y)
                node->SetPositionFloat(original.X, original.Y);
        }

        DiscardGeneration(clearCurrentNodes: true);
        return true;
    }

    private void BeginGeneration(nint address)
    {
        addonAddress = address;
        addonIsLive = true;
        generation = ++nextGeneration;
        DiscardGeneration();
    }

    private void DiscardGeneration(bool clearCurrentNodes = false)
    {
        lifecycle.Discard();
        captured = false;
        Array.Clear(rowNodes);
        if (clearCurrentNodes) Array.Clear(currentNodes);
    }

    private static void CopyNodes(nint[] source, nint[] destination)
    {
        for (var i = 0; i < RowCount; i++) destination[i] = source[i];
    }

    private static bool SameNodes(nint[] left, nint[] right)
    {
        for (var i = 0; i < RowCount; i++)
            if (left[i] != right[i]) return false;
        return true;
    }

    private static bool TryReadRows(
        AddonPartyList* addon,
        nint[] nodes,
        PartyListRowPosition[] positions)
    {
        if (addon == null) return false;
        var members = addon->PartyMembers;
        if (members.Length < RowCount) return false;

        for (var i = 0; i < RowCount; i++)
        {
            var component = members[i].PartyMemberComponent;
            var node = component == null ? null : (AtkResNode*)component->OwnerNode;
            if (node == null) return false;
            nodes[i] = (nint)node;
            positions[i] = new PartyListRowPosition(node->X, node->Y);
        }
        return true;
    }

    private void SetStatus(string value)
    {
        if (status == value) return;
        status = value;
        if (value.StartsWith("未套用", StringComparison.Ordinal)
            || value.StartsWith("未還原", StringComparison.Ordinal))
            Plugin.Log.Warning($"[PartyListDisplayOrder] {value}");
    }
}
