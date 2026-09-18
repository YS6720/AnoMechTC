using AnoMech.Compat;
using AnoMech.Core.Native;
using AnoMech.Core.SimObjects;
using AnoMech.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AnoMech.Core.Game.Party;

// Spawns and configures the eight party members around the scenario origin.
// Reads PartyPresets for the player's job (the player's own slot is null in
// the preset list — that role gets the SimPlayer reference instead), builds
// each non-player BattleChara as a Lalafell PC, and stores the resulting
// SimPartyNpc (or SimPlayer) into the supplied SimParty. Doppels are
// inserted into CharacterManager._battleCharas so row-click targeting and
// mouseover tooltips resolve through the engine's normal lookup path; the
// matching unregister lives in SimPartyNpc.Despawn. Scenarios are
// inn-gated upstream (Game.RunScenarioInternal). Game is the entry point —
// it computes the origin and delegates here.
internal static unsafe class PartyCreator
{
    private const byte RaceLalafell = 3;
    private const byte TribePlainsfolk = 5;
    private const byte SexFemale = 1;
    private const byte BodyTypeAdult = 1;

    // Status-loop-VFX size is driven by GameObject.Height (and possibly VfxScale). The engine
    // derives both from a real PC's Customize, but a client-spawned doppel leaves them at the 1.0
    // default — making status VFX render oversized vs the small Lalafell model. There's no cheap
    // way to recompute them (CalculateHeight is a different, larger value), so we hardcode the
    // Lalafell values observed on a live Lalafell player to match the hardcoded Customize below.
    private const float LalafellVfxScale = 0.4f;
    private const float LalafellHeight = 0.6f;

    private const float RingRadius = 2.5f;
    private const float RadiusJitter = 0.6f;
    private const float AngleJitter = 0.4f;

    private static readonly Random Rng = new();

    public static void Populate(
        SimParty party,
        SimPlayer player,
        uint playerJob,
        SimWorld world,
        PartyRole? roleOverride = null,
        bool solo = false,
        IReadOnlyList<PartyMemberPreset?>? presetOverride = null,
        IReadOnlySet<PartyRole>? remoteRoles = null,
        IReadOnlyList<string?>? aliasNames = null,
        IReadOnlyList<AnoMech.Multiplayer.MpAppearance?>? appearances = null,
        Func<bool>? isCurrent = null)
    {
        var presets = presetOverride
            ?? (roleOverride is { } skip
                ? PartyPresets.ForRole(skip)
                : PartyPresets.ForPlayerJob(playerJob));
        if (presets.Count != 8)
            throw new ArgumentException("Party presets must contain exactly eight role slots.", nameof(presetOverride));
        if (remoteRoles != null)
        {
            if (solo || roleOverride is not { } localRole || (uint)localRole >= 8 || presets[(int)localRole] != null)
                throw new ArgumentException("Network party requires the claimed local slot.", nameof(roleOverride));
            var nullSlots = 0;
            foreach (var preset in presets)
                if (preset == null)
                    nullSlots++;
            if (nullSlots != 1 || remoteRoles.Contains(localRole))
                throw new ArgumentException("Network party must have exactly one local slot.", nameof(presetOverride));
            foreach (var role in remoteRoles)
                if ((uint)role >= 8)
                    throw new ArgumentOutOfRangeException(nameof(remoteRoles));
        }
        if (aliasNames != null && aliasNames.Count != 8)
            throw new ArgumentException("Alias projection must cover exactly eight role slots.", nameof(aliasNames));
        if (appearances != null && appearances.Count != 8)
            throw new ArgumentException("Appearance projection must cover exactly eight role slots.", nameof(appearances));
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();

        for (int i = 0; i < presets.Count; i++)
        {
            if (isCurrent?.Invoke() == false)
                throw new AnoMech.Multiplayer.MpProtocolException(AnoMech.Multiplayer.MpError.Cancelled);
            var preset = presets[i];
            var role = (PartyRole)i;
            // Display-name projection only: the slot's preset (job / equipment / level)
            // and the null local slot are untouched, and a role no connected player
            // claimed keeps its AI stand-in name.
            var alias = aliasNames?[i];
            party.SetDisplayName(role, alias);
            if (preset == null)
            {
                // Player's own job slot — wire the SimPlayer in directly so
                // Party.Get(role) returns a uniform SimCharacter. The local
                // player's real character object is never renamed; the alias
                // reaches the party list through SimParty.DisplayName.
                party.SetSlot(role, player);
                continue;
            }

            // Solo mode: only the player's own slot is filled — skip every doppel.
            if (solo)
                continue;

            var angle = (i / (float)presets.Count) * MathF.Tau
                        + ((float)Rng.NextDouble() - 0.5f) * AngleJitter;
            var distance = RingRadius + ((float)Rng.NextDouble() - 0.5f) * RadiusJitter;
            // Local ring around the scenario origin. Y stays at 0 (local floor);
            // Coordinates.ToGlobal lifts it to origin.Y at spawn.
            var localPos = new Vector3(MathF.Sin(angle) * distance, 0f, MathF.Cos(angle) * distance);
            var facingPlayer = MathF.Atan2(-localPos.X, -localPos.Z);

            var member = Spawn(preset, alias ?? preset.Name, world, role, new Placement(localPos, facingPlayer),
                itemSheet, remoteRoles?.Contains(role) == true, appearances?[i], isCurrent);
            if (member != null)
                party.SetSlot(role, member);
            else if (remoteRoles != null)
                throw new AnoMech.Multiplayer.MpProtocolException(AnoMech.Multiplayer.MpError.PrepareFailed);
        }
    }

    private static ISimPartyMember? Spawn(PartyMemberPreset preset, string displayName, SimWorld world,
        PartyRole role, Placement placement, ExcelSheet<Item> itemSheet, bool remote,
        AnoMech.Multiplayer.MpAppearance? appearance, Func<bool>? isCurrent)
    {
        if (isCurrent?.Invoke() == false)
            throw new AnoMech.Multiplayer.MpProtocolException(AnoMech.Multiplayer.MpError.Cancelled);
        if (!remote)
            displayName = $"[{role.ShortName()}] {displayName}";
        if (!CharacterManagerHelper.CreateCharacter(out var idx, out var obj))
            return null;
        SimNpc member = remote
            ? new SimNetworkPuppet(idx, world.Coordinates, role, preset.ClassJob, displayName)
            : new SimPartyNpc(idx, world.Coordinates, role, preset.ClassJob, displayName);
        try
        {

            var gameObj = (GameObject*)obj;
            var chara = (BattleChara*)obj;
            chara->ObjectKind = ObjectKind.Pc;
            chara->Position = world.Coordinates.ToGlobal(placement.Position);
            chara->Rotation = MathUtil.NormalizeRotation(placement.Rotation);
            chara->Scale = 1f;
            chara->VfxScale = LalafellVfxScale;
            chara->Height = LalafellHeight;
            chara->ModelContainer.ModelCharaId = 0;
            chara->ModelContainer.ModelSkeletonId = 0;

            // 真人外觀（連線且該成員有送、且驗得過）優先；否則沿用 Lalafell preset。
            // 只在這裡套一次——run 中換外觀＝換骨架，要全重繪。
            if (appearance != null && PlayerAppearance.IsValid(appearance))
            {
                PlayerAppearance.Apply(chara, appearance);
            }
            else
            {
                WriteCustomize(chara);
                WriteEquipment(chara, preset, itemSheet);
            }
            WriteName(gameObj, displayName);
            obj->RenderFlags = 0;

            chara->TargetableStatus = ObjectTargetableFlags.IsTargetable;
            chara->HitboxRadius = 0.5f;
            chara->MaxHealth = 100_000;
            chara->Health = 100_000;
            chara->MaxMana = 10_000;
            chara->Mana = 10_000;
            chara->Battalion = 0;
            // 台服 CS 0.0.6966 這些屬性是 get-only ⇒ 走 CharacterFlags 直寫底層位元
            // （遮罩來源＝getter 的 IL，見 docs/upstream/TC-CS-BITFIELDS.md）。
            // 刻意保留設為 false 的幾行：沒有實機證據證明新建物件這些位元必為 0，
            // 移除等於在無依據下改變行為。
            CharacterFlags.SetHostile(chara, false);
            CharacterFlags.SetInCombat(chara, false);
            CharacterFlags.SetPartyMember(chara, true);
            CharacterFlags.SetAllianceMember(chara, false);
            CharacterFlags.SetFriend(chara, false);
            CharacterFlags.SetOffhandDrawn(chara, false);
            CharacterFlags.SetWeaponDrawn(chara, false);
            chara->CastInfo.IsCasting = false;
            chara->Mode = CharacterModes.Normal;
            chara->ModeParam = 0;
            chara->ClassJob = preset.ClassJob;
            chara->Level = preset.Level;

            var player = Plugin.ObjectTable.LocalPlayer;
            if (player != null)
            {
                var localChara = (Character*)player.Address;
                chara->HomeWorld = localChara->HomeWorld;
                chara->CurrentWorld = localChara->CurrentWorld;
            }

            Plugin.Log.Info($"PartyCreator: spawned {displayName} ({role}, job {preset.ClassJob}) at index {idx}");
            // Bots steer around the scenario's geometry; only doppels get the live
            // field (bosses/player keep ObstacleField.Empty and move in straight lines).
            member.Obstacles = world.Obstacles;
            // Seed the stored Position/Rotation to match the spawn placement so
            // anything reading SimCharacter.Position before the first Tick sees
            // the correct value (the Tick re-sync only kicks in next frame).
            member.SetPosition(placement);
            return (ISimPartyMember)member;
        }
        catch
        {
            member.Despawn();
            throw;
        }
    }

    // Party names may be a connected player's alias, which is not ASCII-only, so the
    // name goes into the engine's fixed buffer as UTF-8 rather than through the
    // ASCII-narrowing GameObjectHelper.WriteName. MpValidation.Alias caps an alias at
    // 63 UTF-8 bytes; a name that still doesn't fit is a bug, not something to truncate.
    private static void WriteName(GameObject* obj, string name)
    {
        Span<byte> encoded = stackalloc byte[NetworkPartyIdentity.NativeNameBytes];
        if (!NetworkPartyIdentity.TryWriteName(name, encoded))
            throw new ArgumentException(
                $"Party member name does not fit the {NetworkPartyIdentity.NativeNameBytes}-byte native name buffer.",
                nameof(name));
        for (var i = 0; i < encoded.Length; i++)
            obj->Name[i] = encoded[i];
    }

    private static void WriteCustomize(BattleChara* chara)
    {
        ref var c = ref chara->DrawData.CustomizeData;
        c.Race = RaceLalafell;
        c.Sex = SexFemale;
        c.BodyType = BodyTypeAdult;
        c.Height = 50;
        c.Tribe = TribePlainsfolk;
        c.Face = 1;
        c.Hairstyle = 1;
        c.SkinColor = 1;
        c.EyeColorRight = 1;
        c.EyeColorLeft = 1;
        c.HairColor = 1;
        c.HighlightsColor = 1;
        c.TattooColor = 1;
        c.Eyebrows = 1;
        c.Nose = 1;
        c.Jaw = 1;
        c.LipColorFurPattern = 1;
        c.MuscleMass = 50;
        c.TailShape = 1;
        c.BustSize = 50;
        c.FacePaintColor = 1;
    }

    private static void WriteEquipment(BattleChara* chara, PartyMemberPreset preset, ExcelSheet<Item> itemSheet)
    {
        ApplyItem(chara, DrawDataContainer.EquipmentSlot.Head, preset.Head, itemSheet);
        ApplyItem(chara, DrawDataContainer.EquipmentSlot.Body, preset.Body, itemSheet);
        ApplyItem(chara, DrawDataContainer.EquipmentSlot.Hands, preset.Hands, itemSheet);
        ApplyItem(chara, DrawDataContainer.EquipmentSlot.Legs, preset.Legs, itemSheet);
        ApplyItem(chara, DrawDataContainer.EquipmentSlot.Feet, preset.Feet, itemSheet);
    }

    private static void ApplyItem(BattleChara* chara, DrawDataContainer.EquipmentSlot slot, uint itemRowId, ExcelSheet<Item> itemSheet)
    {
        if (itemRowId == 0)
            return;
        if (!itemSheet.TryGetRow(itemRowId, out var item))
        {
            Plugin.Log.Warning($"PartyCreator: Item row {itemRowId} for slot {slot} not found");
            return;
        }
        chara->DrawData.Equipment(slot).Value = item.ModelMain;
    }
}
