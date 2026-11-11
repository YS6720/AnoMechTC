using AnoMech.Core.EnemyActions;
using AnoMech.Compat;
using AnoMech.Core.Game;
using AnoMech.Helpers;
using AnoMech.Pointers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AnoMech.Core.SimObjects;

// Placement.Position is scenario-local (offset from SimWorld.ScenarioOrigin), same
// coordinate space as the rest of the SimXxx API: +X = east, +Z = south.
// Placement.Rotation is absolute radians: 0 = south, π/2 = east, π = north, -π/2 = west.
// ModelCharaId (non-zero) overrides the BNpcBase visual, e.g. a no-shield variant.
// Hitbox radius = BNpcBase.Scale × ModelChara's unscaled radius, unless HitboxRadius
// (non-zero) overrides it — decoupling the clickable/targetable hitbox from Scale.

// Whether a SimEnemy shows in the _EnemyList HUD (read each frame by EnmityHud.Refresh).
// Always          — listed while alive.
// OnlyWhenVisible — follows the engine's DrawObject.IsVisible; for adds that warp
//                   in/out. Don't combine with SetModelState (its rebuild briefly
//                   DisableDraws and flaps the list); transforming bosses use Always.
// Never           — never listed (AOE-source dummies, tether endpoints).
// Manual          — scenario drives it via SetInEnemyList(bool); default false.
public enum EnemyListMode
{
    Always,
    OnlyWhenVisible,
    Never,
    Manual,
}

public record struct EnemySpawnConfig(
    uint BNpcBaseId,
    uint NameId = 0,
    byte Level = 0,
    bool Targetable = false,
    EnemyListMode EnemyList = EnemyListMode.Always,
    bool IsVisible = true,
    Placement Placement = default,
    uint ModelCharaId = 0,
    float Scale = 0f,    // 0 = use BNpcBase.Scale
    float HitboxRadius = 0f,    // 0 = ModelChara unscaled radius × Scale
    byte? InitialModeAttributeFlags = null, // null = engine default; override canonical body sub-mesh variants only.
    ulong? MainHandModel = null, // null = BNpcBase/NpcEquip; 0 = explicitly unarmed.
    ulong? OffHandModel = null,
    // 上游 1646a70 同名（尾端加，位置參數不位移）。Visibility 見 SpawnVisibility；IsVisible:false 仍照舊隱形。
    SpawnVisibility Visibility = SpawnVisibility.Visible,
    // 上游同名：BNpc 在副本 layout 裡的實例 id；非 0 才寫 GameObject.LayoutId（上游自標效果 UNVERIFIED）。
    uint LayoutId = 0);

public sealed unsafe class SimEnemy : SimNpc
{
    // Cast bar, action-effect release, omen telegraph, and animation lock live in
    // SimCast. SimEnemy just converts target coords to world space and reads IsBusy.
    private readonly SimCast cast;
    private bool targetable;
    private byte modelState;

    public uint ModelCharaId { get; private set; }
    public ulong MainHandModel => GetWeaponModel(DrawDataContainer.WeaponSlot.MainHand);
    public ulong OffHandModel => GetWeaponModel(DrawDataContainer.WeaponSlot.OffHand);
    public float Scale { get; private set; }
    public float ConfiguredHitboxRadius { get; private set; }
    public bool IsCasting => cast.IsCasting;
    public float CastProgress => cast.Progress;
    public uint CastActionId => cast.ActionId;
    // state; Tick's reconciler fires EnableDraw/DisableDraw once per change, gated on
    // IsReadyToDraw so toggles can't race the async model load. RenderFlags writes
    // were tried and don't reliably keep enemies visible — only this path does.
    // currentVisible starts true because base SimNpc.Tick fires the initial EnableDraw.
    // FIXME: this is not valid, but good enough i guess
    private bool desiredVisible = true;
    private bool currentVisible = true;
    
    public bool Visible => desiredVisible;
    public bool Targetable => targetable;
    public byte ModelState => modelState;

    public uint BNpcBaseId { get; }
    // 上游同名：生成時的設定（Visibility 決定何時顯形，InvisibleHelper 決定釋放時直接轉向）。
    public EnemySpawnConfig SpawnConfig { get; internal set; }


    private uint resolvedNameId = uint.MaxValue;
    private string? resolvedCanonicalName;

    // Cache resolved runtime names across frames, but retry unresolved RSV rows:
    // their text may arrive after the actor was created.
    public string DisplayName
    {
        get
        {
            var chara = BattleCharaPtr;
            if (chara == null) return field;
            if (resolvedNameId != chara->NameId || resolvedCanonicalName == null)
            {
                resolvedCanonicalName = CanonicalName(chara->NameId);
                resolvedNameId = chara->NameId;
            }
            if (resolvedCanonicalName is { } canonicalName) return canonicalName;
            var name = ((GameObject*)chara)->GetName().ToString();
            // 殘缺 SeString（帶 '<' 佔位符，如「<=」）＝解析失敗 → 回落 spawn 時的正名
            return string.IsNullOrEmpty(name) || name.Contains('<') ? field : name;
        }
    }

    private static string? CanonicalName(uint nameId)
    {
        if (nameId == 0 ||
            !Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.BNpcName>().TryGetRow(nameId, out var row))
            return null;
        var name = row.Singular.ExtractText();
        return string.IsNullOrWhiteSpace(name) || name.StartsWith("_rsv_", StringComparison.Ordinal) ? null : name;
    }

    public EnemyListMode EnemyListMode { get; }
    private bool manualInEnemyList;

    // OnlyWhenVisible reads the live DrawObject.IsVisible flag, so any draw-lifecycle
    // toggle is reflected without extra plumbing; Manual lets the scenario drive it.
    public bool InEnemyList => EnemyListMode switch
    {
        EnemyListMode.Always          => true,
        EnemyListMode.Never           => false,
        EnemyListMode.Manual          => manualInEnemyList,
        EnemyListMode.OnlyWhenVisible => IsEngineVisible(),
        _ => false,
    };

    internal SimEnemy(int index, uint bNpcBaseId, string displayName, EnemyListMode enemyListMode, Coordinates coordinates,
        uint modelCharaId = 0, float scale = 1f, float hitboxRadius = 0f,
        bool requiresDrawObject = true) : base(index, coordinates, requiresDrawObject)
    {
        BNpcBaseId = bNpcBaseId;
        DisplayName = displayName;
        EnemyListMode = enemyListMode;
        ModelCharaId = modelCharaId;
        Scale = scale;
        ConfiguredHitboxRadius = hitboxRadius;
        cast = new SimCast(this, coordinates);
    }
    
    public override void SetModelState(byte value)
    {
        modelState = value;
        base.SetModelState(value);
    }
    
    internal void ApplyNetworkHealth(uint current, uint max)
    {
        var chara = BattleCharaPtr;
        if (chara == null) return;
        if (max > 0) chara->MaxHealth = max;
        chara->Health = Math.Min(current, chara->MaxHealth);
    }

    // NpcEquip 的武器欄是一個 64-bit 打包值：低 16 位 id、次 16 位 type、再 16 位 variant。
    private static void SetWeaponModel(BattleChara* chara, DrawDataContainer.WeaponSlot slot, ulong packed)
    {
        ref var model = ref chara->DrawData.Weapon(slot).ModelId;
        model.Id = (ushort)packed;
        model.Type = (ushort)(packed >> 16);
        model.Variant = (ushort)(packed >> 32);
    }

    private ulong GetWeaponModel(DrawDataContainer.WeaponSlot slot)
    {
        var chara = BattleCharaPtr;
        if (chara == null) return 0;
        return chara->DrawData.Weapon(slot).ModelId.Value;
    }

    // Allocates a BattleChara, configures it as a BattleNpc per the supplied
    // config, and returns a SimEnemy wrapping it. Caller is responsible for
    // registering the result in the world's children list (so reset/teardown
    // covers it). Returns null on missing LocalPlayer, BNpcBase miss, or
    // CreateBattleChara failure.
    internal static SimEnemy? Spawn(EnemySpawnConfig config, SimWorld world)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return null;

        var bnpcSheet = Plugin.DataManager.GetExcelSheet<BNpcBase>();
        if (!bnpcSheet.TryGetRow(config.BNpcBaseId, out var bnpc))
        {
            Plugin.Log.Warning($"BNpcBase row {config.BNpcBaseId} (0x{config.BNpcBaseId:X}) not found");
            return null;
        }

        var modelCharaId = config.ModelCharaId != 0 ? config.ModelCharaId : bnpc.ModelChara.RowId;
        var modelCharaSheet = Plugin.DataManager.GetExcelSheet<ModelChara>();
        if (!modelCharaSheet.TryGetRow(modelCharaId, out var modelChara))
        {
            Plugin.Log.Warning($"ModelChara row {config.BNpcBaseId} (0x{config.BNpcBaseId:X}) not found");
            return null;
        }

        if (!CharacterManagerHelper.CreateCharacter(out var idx, out var obj)) return null;

        var gameObj = (GameObject*)obj;
        var chara = (BattleChara*)obj;
        // Engine's canonical BNpc initializer — populates ModelContainer from BNpcBase,
        // including ModeAttributeFlags (body sub-mesh, e.g. Omega-M's shield). Must run
        // before our overrides below.
        chara->CharacterSetup.SetupBNpc(config.BNpcBaseId, config.NameId);
        chara->ObjectKind = ObjectKind.BattleNpc;
        chara->Position = world.Coordinates.ToGlobal(config.Placement.Position);
        chara->SetRotation(MathUtil.NormalizeRotation(config.Placement.Rotation));
        var scale = config.Scale > 0f ? config.Scale : bnpc.Scale;
        chara->Scale = scale;
        chara->ModelContainer.ModelCharaId = (int)modelCharaId;
        chara->SEPack = bnpc.SEPack;

        var nativeHitbox = true;

        // From Client::Game::Character::CharacterSetupContainer_SetupRaw
        switch (modelChara.Type)
        {
            case 1:
                // TODO: This Type in the game's .exe is a bit complex, for now we just fallback to the previous solving method
                var hitboxRadius = config.HitboxRadius > 0f ? config.HitboxRadius : ResolveHitboxRadius(modelCharaId, scale);
                chara->HitboxRadius = hitboxRadius;
                nativeHitbox = false;
                break;
            case 2:
                chara->ModelContainer.ModelSkeletonId = modelChara.Model + 10000;
                break;
            case 3:
                chara->ModelContainer.ModelSkeletonId = modelChara.Model;
                break;
        }

        if (nativeHitbox)
        {
            chara->ModelContainer.UnscaledRadius = ModelContainerPointers.CalculateUnscaledRadius(&chara->ModelContainer);
            chara->HitboxRadius = config.HitboxRadius > 0f
                ? config.HitboxRadius
                : chara->Scale * chara->ModelContainer.UnscaledRadius;
        }

        // Prefer the current runtime sheet name; only ask the native resolver
        // when the row has no usable name.
        if (config.NameId != 0) chara->NameId = config.NameId;
        if (config.LayoutId != 0) gameObj->LayoutId = config.LayoutId;
        var displayName = CanonicalName(chara->NameId) ?? gameObj->GetName().ToString();
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Contains('<') ||
            displayName.StartsWith("_rsv_", StringComparison.Ordinal))
            displayName = $"BNpc {config.BNpcBaseId:X}";
        GameObjectHelper.WriteName(gameObj, displayName);
        obj->RenderFlags = 0;

        chara->CharacterSetup.CopyFromCharacter((Character*)chara, CharacterSetupContainer.CopyFlags.None);

        chara->BattleNpcSubKind = BattleNpcSubKind.Combatant;
        chara->MaxHealth = 1_000_000;
        chara->Health = 1_000_000;
        chara->Battalion = 4;
        // 台服 CS 0.0.6966：get-only ⇒ 直寫位元（見 docs/upstream/TC-CS-BITFIELDS.md）
        CharacterFlags.SetHostile(chara, true);
        CharacterFlags.SetInCombat(chara, true);
        // 拔武器。戰鬥中的敵人本來就是拔刀狀態，而且**武器是辨識身分的依據**——
        // DSR P5 靠「杖＝夏利貝爾（法師）／槌＝格里諾」判斷該往哪邊引導俯衝，
        // 收著刀就看不出誰是誰（維護者 2026-08-28）。旗標對沒有武器的純怪物模型無作用。
        CharacterFlags.SetWeaponDrawn(chara, true);
        CharacterFlags.SetOffhandDrawn(chara, true);
        chara->CombatTagType = 1;
        chara->CombatTaggerId = ((GameObject*)player.Address)->GetGameObjectId();
        chara->Mode = CharacterModes.Normal;
        chara->ModeParam = 0;
        if (config.InitialModeAttributeFlags is { } maf)
            ModelContainerCompat.TrySetModeAttributeFlags(chara, maf);
        chara->CastInfo.IsCasting = false;
        if (config.Level != 0) chara->Level = config.Level;

        // SetupBNpc 不保證把 NpcEquip 的武器填進 DrawData；沒填時光設 WeaponDrawn 旗標
        // 是不會有東西的 ⇒ 主手為 0 就自己補（武器 model 打包＝ id | type<<16 | variant<<32）
        if (chara->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).ModelId.Id == 0
            && bnpc.NpcEquip.ValueNullable is { } eq)
        {
            if (eq.ModelMainHand != 0)
                SetWeaponModel(chara, DrawDataContainer.WeaponSlot.MainHand, eq.ModelMainHand);
            if (eq.ModelOffHand != 0)
                SetWeaponModel(chara, DrawDataContainer.WeaponSlot.OffHand, eq.ModelOffHand);
        }
        if (config.MainHandModel is { } recordedMainHand)
            chara->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).ModelId.Value = recordedMainHand;
        if (config.OffHandModel is { } recordedOffHand)
            chara->DrawData.Weapon(DrawDataContainer.WeaponSlot.OffHand).ModelId.Value = recordedOffHand;
        var mainHand = chara->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).ModelId;
        Plugin.Log.Info($"SimEnemy: spawned BNpcBase {config.BNpcBaseId} (ModelChara {bnpc.ModelChara.RowId}, "
            + $"scale {bnpc.Scale}, 主手 {mainHand.Id}/{mainHand.Type}/{mainHand.Variant}) at index {idx}");
        var enemy = new SimEnemy(idx, config.BNpcBaseId, displayName, config.EnemyList, world.Coordinates,
            modelCharaId, scale, config.HitboxRadius, requiresDrawObject: modelChara.Type != 0);
        // Mirror the native position/rotation writes above into the C#-side fields.
        enemy.SetPosition(config.Placement);
        enemy.SetTargetable(config.Targetable);
        enemy.SpawnConfig = config;
        if (SpawnVisibilityRules.HiddenAtSpawn(config.IsVisible, config.Visibility)) enemy.SetVisible(false);
        return enemy;
    }

    private static float ResolveHitboxRadius(uint modelCharaId, float scale)
    {
        const float DefaultUnscaledRadius = 0.5f;
        var sheet = Plugin.DataManager.GetExcelSheet<ModelChara>();
        var unscaled = DefaultUnscaledRadius;
        if (sheet.TryGetRow(modelCharaId, out var row) && row.Unknown0 > 0f)
            unscaled = row.Unknown0;
        return unscaled * scale;
    }

    public override void Despawn()
    {
        Movement.Follow(null);
        // 先把武器（角色 DrawObject 的子物件）收掉再刪本體：native 刪除不保證回收它們，
        // 玩家會看到「敵人消失了，杖／斧還插在原地」（維護者 2026-09-18 死刻實測）。
        var chara = BattleCharaPtr;
        if (chara != null) SetWeaponsVisible(chara, false);
        actions?.Despawned();
        cast.Despawn();
        base.Despawn();
    }

    /// <summary>
    /// Sets the targetable status of this <see cref="SimEnemy"/>, which will reflect in their Nameplate and in the Enemy List (if visible there).
    /// </summary>
    /// <param name="targetable">
    /// If <see langword="true"/>, then the Nameplate will be visible, and able to target them using the Enemy List.
    /// If <see langword="false"/>, then the Nameplate will not be visible, and not able to target them using the Enemy List.
    public void SetTargetable(bool value)
    {
        targetable = value;
        var chara = BattleCharaPtr;
        if (chara == null) return;
        if (value)
        {
            chara->TargetableStatus |= (ObjectTargetableFlags)1 | ObjectTargetableFlags.IsTargetable;
        }
        else
        {
            chara->TargetableStatus &= ~((ObjectTargetableFlags)1 | ObjectTargetableFlags.IsTargetable);
        }
    }

    /// <summary>
    /// Only executed when <see cref="EnemyListMode"/> is <see cref="EnemyListMode.Manual"/>
    /// </summary>
    /// <param name="inEnemyList">Will make the Enemy appear or not in the Enemy List (Enmity List)</param>
    public void SetVisibleInEnemyList(bool inEnemyList)
    {
        if (EnemyListMode != EnemyListMode.Manual)
        {
            Plugin.Log.Warning($"SetInEnemyList({inEnemyList}) ignored: SimEnemy {DisplayName} has mode {EnemyListMode}; declare EnemyListMode.Manual in EnemySpawnConfig to use explicit toggles.");
            return;
        }
        manualInEnemyList = inEnemyList;
    }

    /// <summary>
    /// Sets the target of this <see cref="SimEnemy"/>.
    /// </summary>
    /// <remarks>For now, this is purely visual and does not contain any logic relating to auto-attacks or similar.</remarks>
    /// <param name="target">The <see cref="SimCharacter.GameObjectId"/> will be retrieved and used as the TargetId. If <see langword="null"/>, then the target is cleared.</param>
    /// <param name="follow">If <paramref name="target"/> is valid, this will determine if the <see cref="SimEnemy"/> should now follow <paramref name="target"/> or not.</param>
    /// <param name="speed">If <paramref name="target"/> is valid and <paramref name="follow"/> is <see langword="true"/>, this will be the speed that the <see cref="SimEnemy"/> will follow the <paramref name="target"/></param>
    public void SetTarget(SimCharacter? target, bool follow = true, float speed = 6f)
    {
        if (target == null)
        {
            BattleCharaPtr->TargetId = 0xE0000000;
        }
        else
        {
            BattleCharaPtr->TargetId = target.GameObjectId;

            if (follow)
            {
                Follow(target);
            }
        }
    }

    public void SetVisible(bool visible) => desiredVisible = visible;

    // HiddenUntilShown：播到出場類時間軸就顯形（規則見 SpawnVisibilityRules.RevealsOnTimeline）。
    public override void PlayActionTimeline(ushort timelineId, ushort loopId = 0, ushort baseOverride = 0)
    {
        base.PlayActionTimeline(timelineId, loopId, baseOverride);
        RevealOnTimeline(timelineId);
    }

    // EnemyAction 釋放時：招式的 AnimationEnd 是出場時間軸（例如分身的 show／hide 組）就顯形。
    internal void RevealOnAction(uint actionId)
    {
        if (SpawnConfig.Visibility != SpawnVisibility.HiddenUntilShown) return;
        if (Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var action))
            RevealOnTimeline((ushort)action.AnimationEnd.RowId);
    }

    // HiddenUntilPopIn：ActorControl.PopIn 送出後顯形。
    internal void RevealOnPopIn()
    {
        if (SpawnConfig.Visibility == SpawnVisibility.HiddenUntilPopIn) SetVisible(true);
    }

    private void RevealOnTimeline(ushort timelineId)
    {
        if (SpawnConfig.Visibility != SpawnVisibility.HiddenUntilShown || timelineId == 0) return;
        if (!Plugin.DataManager.GetExcelSheet<ActionTimeline>().TryGetRow(timelineId, out var row)) return;
        if (SpawnVisibilityRules.RevealsOnTimeline(SpawnConfig.Visibility, row.Key.ExtractText())) SetVisible(true);
    }

    public void SetAnimationState(byte slot, byte value) => ActorControl.SetAnimationState(slot, value);

    private const float FadeOutDuration = 1.3f;

    // 上游同名：A spent add or a boss leaving at a phase transition: gone without dying.
    public void FadeOut()
    {
        ActorControl.FadeOut();
        if (World is { } world) world.Events.Add(FadeOutDuration, Despawn);
        else Despawn();
    }

    // 上游 1646a70：Stays turned toward the target, holding still while casting or animation-locked; null releases it.
    public void KeepFacing(SimCharacter? target) => Movement.KeepFacing(target);

    private void ReconcileVisibility()
    {
        if (desiredVisible == currentVisible)
        {
            return;
        }

        var obj = BattleCharaPtr;
        if (obj == null || obj->DrawObject == null)
        {
            return;
        }

        obj->DrawObject->IsVisible = desiredVisible;
        SetWeaponsVisible(obj, desiredVisible);
        currentVisible = desiredVisible;

        Plugin.Log.Debug($"[SimEnemy.ReconcileVisibility] {DisplayName}'s visibility was set to {desiredVisible}");
    }

    /// <summary>
    /// 武器是**獨立的 DrawObject**（DrawData.WeaponData[s].DrawObject），不掛在角色 DrawObject 底下——
    /// 只關角色那一個，武器會留在半空中（維護者 2026-09-02 風槍實測：「敵方消失了 武器還停留在空中」；
    /// 0.22.4.0 起敵人拔刀，從那時起每次 SetVisible(false) 都在漏這個）。三個槽位一起切。
    /// 武器模型是非同步載入的，隱形期間每幀再壓一次（見 Tick），免得晚到的武器自己亮起來。
    /// </summary>
    private static void SetWeaponsVisible(BattleChara* chara, bool visible)
    {
        for (var s = 0; s < 3; s++)
        {
            var w = chara->DrawData.WeaponData[s].DrawObject;
            // 2026-09-02 實測這條路徑關不掉武器（維護者：「還是一樣」）。診斷結論（9/16 撤除）：
            // 三個槽位的 DrawObject 指標一律為空（draw=0x0 model=0）——武器不在 WeaponData 底下。
            if (w != null && w->IsVisible != visible) w->IsVisible = visible;
        }
        // 武器真正掛的地方＝角色 DrawObject 的**子物件環狀鏈**（Object.ChildObject →
        // NextSiblingObject，繞回頭）。只關角色那一個，杖／斧就留在原地不動
        //（維護者 2026-09-18 死刻：隱形的格里諾／努德內留下武器；退場的演員也一樣）。
        var draw = chara->DrawObject;
        if (draw == null) return;
        var child = draw->Object.ChildObject;
        if (child == null) return;
        var first = child;
        for (var guard = 0; guard < 16; guard++)
        {
            var childDraw = (DrawObject*)child;
            if (childDraw->IsVisible != visible) childDraw->IsVisible = visible;
            child = child->NextSiblingObject;
            if (child == null || child == first) break;
        }
    }

    // Authoritative draw state (DrawObject.Flags bits 0 and 3, set by Enable/DisableDraw).
    // False during the async model-load window where DrawObject is still null.
    private bool IsEngineVisible()
    {
        var obj = BattleCharaPtr;
        if (obj == null) return false;
        var draw = obj->DrawObject;
        return draw != null && draw->IsVisible;
    }

    // Engine doesn't expose post-action animation-lock duration via EXD — the
    // real value only ships in the server's ActionEffect packet. 0.6s is a
    // reasonable approximation for most boss abilities; if a scenario needs
    // tighter timing we can derive per-action values from captured ACT logs.
    public bool Cast(uint actionId, Vector3? targetLocation = null, float? castSeconds = null, GameObjectId? targetId = null, float omenDelay = 0f, float omenRotate = 0f, byte animationVariation = 0, float animationLock = 0.6f, float? fireDelay = null)
    {
        // targetLocation stays scenario-local; SimCast lifts to world at native boundaries.
        return cast.Start(actionId, targetLocation, castSeconds, targetId, omenDelay, omenRotate, animationVariation, animationLock, fireDelay);
    }

    public void NativeCast(uint actionId, ActionType actionType, float omenDelay, float castTime, bool interruptible, float? rotation = null, Vector3? position = null, GameObjectId? targetId = null, GameObjectId? ballistaId = null)
    {
        cast.NativeCast(actionId, actionType, omenDelay, castTime, interruptible, rotation, position, targetId, ballistaId);
    }

    public void NativeActionEffect(uint actionId, float animationLock, ushort spellId, byte animationVariaton, ActionType actionType, byte flags, float? rotation = null, Vector3? position = null, GameObjectId? animationTargetId = null, GameObjectId? actionTargetId = null, GameObjectId? ballistaId = null)
    {
        cast.NativeActionEffect(actionId, animationLock, spellId, animationVariaton, actionType, flags, rotation, position, animationTargetId, actionTargetId, ballistaId);
    }

    public void BeginRecordedCast(uint actionId, ActionType actionType, float castTime,
        float omenDelay, float? rotation, Vector3? position, GameObjectId? targetId)
        => cast.BeginRecordedCast(actionId, actionType, castTime, omenDelay, rotation, position, targetId);

    public void NativeActionEffect(uint actionId, float animationLock, ushort spellId,
        byte animationVariation, ActionType actionType, byte flags,
        ReadOnlySpan<GameObjectId> actionTargets, float? rotation, Vector3? position,
        GameObjectId? animationTargetId, GameObjectId? ballistaId)
        => cast.NativeActionEffect(actionId, animationLock, spellId, animationVariation,
            actionType, flags, actionTargets, rotation, position, animationTargetId, ballistaId);

    // ── EnemyAction（上游 1646a70 新式招式，與上面的舊 Cast／NativeCast 並存）──────────

    // 生成它的世界（SimWorld.SpawnEnemy 設定）；EnemyAction 的判定、排程與事件都在這裡。
    internal SimWorld? World { get; set; }

    // SimWorld.SpawnHelperPool 生成的隱形 helper：釋放時直接轉向目標（看不見的模型要下一次刷新才轉）。
    internal bool IsPooledHelper { get; set; }

    private EnemyActionHandler? actions;
    private EnemyActionHandler Actions
        => actions ??= new EnemyActionHandler(this, cast,
            World ?? throw new InvalidOperationException($"{DisplayName} was not spawned through SimWorld.SpawnEnemy"));

    // An action with effects (area, damage, statuses, ...) or configuration beyond the sheet's.
    // 上游另有 Cast(uint, CastTarget) 純視覺 overload；Vector3 可隱式轉 CastTarget，會和既有 Cast(uint, Vector3?) 歧義，不搬，
    // 純視覺招式寫 Cast(new EnemyAction(id), target)。
    public void Cast(EnemyAction action, CastTarget target = default, byte animationVariation = 0) =>
        Actions.Start(action, target, animationVariation);

    // The BNpcName row its pooled helpers are shown under where retail follows a form it took rather
    // than its spawn name; null = its own NameId.
    public uint? HelperName { get; set; }

    internal uint NameId => BattleCharaPtr == null ? 0 : BattleCharaPtr->NameId;

    // The action cast by one of the zone's pooled helpers acting for this enemy, standing at `at`
    // (this enemy's placement by default). A dead or despawned enemy still casts, unlinked.
    public void CastFromHelper(EnemyAction action, CastTarget target = default, Placement? at = null, byte animationVariation = 0) =>
        (World ?? throw new InvalidOperationException($"{DisplayName} was not spawned through SimWorld.SpawnEnemy"))
            .CastFromHelper(this, action, target, at ?? this.Placement(), animationVariation);

    public void CastFromHelper(uint actionId, CastTarget target = default, Placement? at = null, float animationLock = 0.6f) =>
        CastFromHelper(new EnemyAction(actionId) { Cast = new() { AnimationLock = animationLock } }, target, at);

    // Interrupts an EnemyAction cast still on its bar: neither its effect nor its mechanics go out.
    public void CancelCast(CastCancelReason reason)
    {
        if (actions?.CancelCast(reason) == true) World?.HelperCastCancelled(this);
    }

    public override bool AnimationLock => cast.IsBusy || actions?.AwaitingRelease == true;

    // Seconds until AnimationLock ends; infinite while the bar is up or the ActionEffect pending.
    internal float BusyFor => cast.IsCasting || actions?.AwaitingRelease == true ? float.PositiveInfinity : cast.RemainingAnimationLock;

    // 自爆類招式打倒自己（DefeatCasterEffect）：招式落地時倒下，動作鎖結束後播死亡動畫、屍體淡出後移除。
    // 只有 EnemyAction 路徑會走到；既有場景的敵人仍照舊以 Despawn 退場。
    public bool Defeated { get; private set; }

    private const float CorpseFadeDelay = 8f;
    private const float CorpseFadeDuration = 1.7f;

    internal void DefeatByOwnAction(EnemyAction action)
    {
        if (Defeated || World is not { } world) return;
        actions?.CancelCast(CastCancelReason.Cancelled);
        Defeated = true;
        // 倒下的屍體不再走：清掉進行中的移動、追隨與轉向。
        StopMoving();
        Movement.Follow(null);
        Movement.KeepFacing(null);
        ActorControl.SetMode(CharacterModes.Dead, 2);
        var animationDelay = action.Cast.AnimationLock;
        world.Events.Add(animationDelay, ActorControl.PlayDeathAnimation);
        world.Events.Add(animationDelay + CorpseFadeDelay, ActorControl.FadeCorpse);
        world.Events.Add(animationDelay + CorpseFadeDelay + CorpseFadeDuration, Despawn);
    }

    public override void Tick(float deltaSeconds)
    {
        base.Tick(deltaSeconds);
        ReconcileVisibility();
        if (!desiredVisible && BattleCharaPtr != null) SetWeaponsVisible(BattleCharaPtr, false);
        cast.Tick(deltaSeconds);
    }

    public CharacterFind<T> Find<T>(List<T> targets) where T : IPositioned
    {
        return new CharacterFind<T>(targets);
    }
}
