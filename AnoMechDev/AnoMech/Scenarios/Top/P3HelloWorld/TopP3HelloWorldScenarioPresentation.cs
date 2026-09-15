using System.Collections.Generic;
using System.Numerics;
using AnoMech.Core;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using static AnoMech.Scenarios.Top.TopConstants;

namespace AnoMech.Scenarios.Top.P3HelloWorld;

/// <summary>Native status/VFX projection for the state-authoritative scenario.</summary>
public sealed partial class TopP3HelloWorldScenario
{
    private void ApplyContactStatus(TopP3HelloWorldContact contact, float now)
    {
        if (contact.VaccineBlocked)
        {
            SetStackedStatus(contact.Receiver,
                contact.Color == TopP3HelloWorldColor.Blue
                    ? TopP3HelloWorldRules.StatusId.BlueVaccine
                    : TopP3HelloWorldRules.StatusId.RedVaccine,
                contact.Color == TopP3HelloWorldColor.Blue
                    ? state.Members[contact.Receiver].BlueVaccine
                    : state.Members[contact.Receiver].RedVaccine);
            return;
        }
        var remaining = state.ColorRemainingFor(contact, now);
        if (remaining <= 0f) return;
        var id = contact.Color == TopP3HelloWorldColor.Blue
            ? TopP3HelloWorldRules.StatusId.BlueRot
            : TopP3HelloWorldRules.StatusId.RedRot;
        AddStatus(contact.Receiver, id, remaining);
    }

    private void ResolveColorExplosion(
        PartyRole role,
        IReadOnlyDictionary<PartyRole, Vector3> snapshot)
    {
        var member = party.Get(role);
        var resolvedColor = state.Members[role].LastRotationColor;
        member?.RemoveStatus(TopP3HelloWorldRules.StatusId.BlueRot);
        member?.RemoveStatus(TopP3HelloWorldRules.StatusId.RedRot);
        member?.RemoveStatus(resolvedColor == TopP3HelloWorldColor.Red
            ? TopP3HelloWorldRules.StatusId.RedLatent
            : TopP3HelloWorldRules.StatusId.BlueLatent);
        SetStackedStatus(role,
            resolvedColor == TopP3HelloWorldColor.Red
                ? TopP3HelloWorldRules.StatusId.RedVaccine
                : TopP3HelloWorldRules.StatusId.BlueVaccine,
            resolvedColor == TopP3HelloWorldColor.Red
                ? state.Members[role].RedVaccine
                : state.Members[role].BlueVaccine);
        var action = resolvedColor == TopP3HelloWorldColor.Red
            ? TopP3HelloWorldRules.ActionId.RedRot
            : TopP3HelloWorldRules.ActionId.BlueRot;
        var center = snapshot.TryGetValue(role, out var position)
            ? position : member?.Position ?? Vector3.Zero;
        SpawnInstant(action, center, member);
    }

    private void ApplyMechanicStatuses(float now)
    {
        foreach (var role in TopP3HelloWorldRules.RecordingOrder)
        {
            var member = state.Members[role];
            SetStackedStatus(role, TopP3HelloWorldRules.StatusId.StackVaccine, member.StackVaccine);
            SetStackedStatus(role, TopP3HelloWorldRules.StatusId.DefamationVaccine,
                member.DefamationVaccine);
            SetPresentationStatus(role, TopP3HelloWorldRules.StatusId.Stack,
                member.ActiveStack, member.ActiveStackExpiresAt - now);
            SetPresentationStatus(role, TopP3HelloWorldRules.StatusId.Defamation,
                member.ActiveDefamation, member.ActiveDefamationExpiresAt - now);
            if (!member.NeedStack)
                party.Get(role)?.RemoveStatus(TopP3HelloWorldRules.StatusId.NeedStack);
            if (!member.NeedDefamation)
                party.Get(role)?.RemoveStatus(TopP3HelloWorldRules.StatusId.NeedDefamation);
        }
    }

    private void CompleteRun()
    {
        if (state.FinalizeRun(TopP3HelloWorldRules.RuntimeAt(TopP3HelloWorldRules.EndAt)))
        {
            ChatOutput.Coach("[AnoMech] P3 Hello World 四輪完成（31573 → 31588 前）");
            world.CompleteScenario();
        }
        else
        {
            ReportFailure();
        }
    }

    private void ReportFailure()
    {
        if (failureReported) return;
        failureReported = true;

        var reason = state.Failure switch
        {
            TopP3HelloWorldFailure.WrongTower => "Hello World：塔位判定錯誤",
            TopP3HelloWorldFailure.EmptyTower => "Hello World：塔位無人",
            TopP3HelloWorldFailure.OverlappingTower => "Hello World：塔位重疊",
            TopP3HelloWorldFailure.WrongColor => "Hello World：顏色不符",
            TopP3HelloWorldFailure.WrongDefamation => "Hello World：大圈承傷錯誤",
            TopP3HelloWorldFailure.WrongStack => "Hello World：分攤承傷錯誤",
            TopP3HelloWorldFailure.MissingContact => "Hello World：連線接觸失敗",
            TopP3HelloWorldFailure.WrongTetherBreak => "Hello World：連線未正確拉斷",
            TopP3HelloWorldFailure.ColorExplosion => "Hello World：顏色爆炸",
            TopP3HelloWorldFailure.LatentExpired => "Hello World：顏色效果逾時",
            TopP3HelloWorldFailure.Incomplete => "Hello World：機制未完成",
            TopP3HelloWorldFailure.PlayerDied => "Hello World：玩家死亡",
            _ => "Hello World：機制失敗",
        };
        if (state.Failure != TopP3HelloWorldFailure.PlayerDied)
            world.FailScenario(reason, state.FailureRole);
        Plugin.Log.Info($"[AnoMech] {reason}（診斷：{state.FailureMessage ?? "unknown"}）");

        var action = state.Failure is TopP3HelloWorldFailure.WrongTetherBreak
            or TopP3HelloWorldFailure.MissingContact
            ? TopP3HelloWorldRules.ActionId.TetherFail
            : TopP3HelloWorldRules.ActionId.HelloWorldFail;
        SpawnInstant(action, Vector3.Zero, null);
    }

    private void AddInitialLinePair(
        IReadOnlyList<PartyRole> pair,
        ushort status,
        float duration)
    {
        foreach (var member in pair) AddStatus(member, status, duration);
    }

    private void AddRoundPrepTethers(int round, float startAt)
    {
        var duration = TopP3HelloWorldRules.RoundTimes(round).LineStatusAt - startAt;
        AddPrepPair(round, TopP3HelloWorldRole.LocalTether,
            TopP3HelloWorldRules.TetherId.PrepLocal, duration);
        AddPrepPair(round, TopP3HelloWorldRole.RemoteTether,
            TopP3HelloWorldRules.TetherId.PrepRemote, duration);
    }

    private void AddPrepPair(
        int round,
        TopP3HelloWorldRole role,
        ushort tetherId,
        float duration)
    {
        var pair = state.Pattern.RoleGroup(round, role);
        if (party.Get(pair[0]) is { } first && party.Get(pair[1]) is { } second)
            prepTethers[(round, role)] = world.Tether(first, second, tetherId, duration);
    }

    private void AddRoundPair(
        int round,
        TopP3HelloWorldRole role,
        ushort status,
        ushort tetherId,
        float duration)
    {
        if (prepTethers.Remove((round, role), out var prep)) prep.Despawn();
        var pair = state.Pattern.RoleGroup(round, role);
        foreach (var member in pair) AddStatus(member, status, duration);
        if (party.Get(pair[0]) is { } first && party.Get(pair[1]) is { } second)
            tethers[(round, role)] = world.Tether(first, second, tetherId, duration);
    }

    private void AddStatus(PartyRole role, ushort status, float duration)
        => party.Get(role)?.AddStatusParam(status, param: 0, duration);

    private void SetPresentationStatus(
        PartyRole role,
        ushort status,
        bool present,
        float duration)
    {
        var member = party.Get(role);
        if (member == null) return;
        member.RemoveStatus(status);
        if (present && duration > 0f)
            member.AddStatusParam(status, param: 0, duration);
    }

    private void SetStackedStatus(PartyRole role, ushort status, int stacks)
    {
        var member = party.Get(role);
        if (member == null) return;
        if (stacks <= 0)
        {
            member.RemoveStatus(status);
            return;
        }
        member.AddStatus(status, duration: 0f, stacks: stacks, overrideStacks: true);
    }

    private void SpawnInstant(uint actionId, Vector3 center, SimCharacter? target)
    {
        var helper = SpawnHelper(center, 2f);
        helper?.Cast(actionId, targetLocation: center, targetId: target?.GameObjectId,
            castSeconds: 0f, omenDelay: 0f, fireDelay: 0f);
    }

    private SimEnemy? SpawnHelper(Vector3 position, float lifetime)
    {
        var helper = world.SpawnEnemy(new EnemySpawnConfig(
            BNpcBaseId: BNpcBaseId.P3MonitorHelper,
            NameId: BNpcNameId.P3MonitorHelper,
            Level: Level,
            Targetable: false,
            EnemyList: EnemyListMode.Never,
            Placement: new Placement(position, 0f)));
        if (helper != null) world.Events.Add(lifetime, helper.Despawn);
        return helper;
    }

    private Dictionary<PartyRole, Vector3> Snapshot()
    {
        var snapshot = new Dictionary<PartyRole, Vector3>();
        foreach (var role in TopP3HelloWorldRules.RecordingOrder)
        {
            if (party.Get(role) is { } member && member.IsAlive())
                snapshot[role] = member.Position;
        }
        return snapshot;
    }
}
