using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Map;
using AnoMech.Core.SimObjects;
using static AnoMech.Scenarios.Top.TopConstants;

namespace AnoMech.Scenarios.Top.P6WaveCannon2;

public sealed class TopP6WaveCannon2Scenario : IScenario
{
    public string Name => "Exasquares/WC2";
    public IPhase Phase => TopZone.P6;
    public bool SupportsSolo => true;

    public void DrawSettings() => settingsWindow.Draw();
    public string SettingsIdentity => ScenarioSettings.Identity(settingsWindow.Overrides);
    public bool HasSettings => true;
    private readonly TopP6WaveCannon2SettingsWindow settingsWindow = new();

    public IReadOnlyList<IScenarioAi> AiStrats => [new TopP6WaveCannon2Ai()];

    private TopUtils topUtils = null!;

    private TopP6WaveCannon2State state = null!;
    private SimWorld world = null!;
    private SimParty party = null!;
    private DamageSolver damage = null!;

    public void Run(SimWorld worldParam, int? selectedAi)
    {
        world = worldParam;
        party = worldParam.Party;
        state = new TopP6WaveCannon2State(party, settingsWindow.Overrides);
        var solo = selectedAi is null;
        if (selectedAi is { } idx && idx < AiStrats.Count)
            ((IScenarioAi<TopP6WaveCannon2State>)AiStrats[idx]).Run(state, world);
        topUtils = new TopUtils(world);
        damage = new DamageSolver(party);
        damage.SetStatuses(DamageType.Magic, StatusId.MagicVulnerabilityUp);

        world.Events.Add(1f, () => state.ProteanOrder.ForEach(c => c.AddStatus(StatusId.BrilliantDynamis)));
        Run_Alpha_Omega_4000A771(solo);
        TopP6CosmoArrowSequence.Run(world, damage, state.InFirst);
        if (!solo) Run_Alpha_Omega_4000A40C();
        // The final native release is the last CosmoArrowDamage in solo mode
        // (21.91s or 23.91s by InFirst), or the Wild Charge release at 27.40s
        // for the party route. Do not infer success from queue exhaustion.
        var completionAt = solo
            ? (state.InFirst ? 23.91f : 21.91f)
            : 27.40f;
        world.Events.Add(completionAt, world.CompleteScenario);
        
    }

    public void Tick(float delta, float elapsed) { }

    private void Run_Alpha_Omega_4000A771(bool solo)
    {
        SimEnemy? alpha_Omega_4000A771 = null;
        world.Events.Add(0f, () => alpha_Omega_4000A771 = world.SpawnEnemy(new EnemySpawnConfig(BNpcBaseId: BNpcBaseId.AlphaOmega, NameId: BNpcNameId.AlphaOmega, Level: 90, Targetable: true, EnemyList: EnemyListMode.Always, IsVisible: true, Placement: new Placement(new Vector3(0.000f, -0.000f, 0.000f), -MathF.PI))));
        world.Events.Add(0.5f, () => alpha_Omega_4000A771?.AddStatus(StatusId.CodeMi));
        world.Events.Add(1.90f, () => alpha_Omega_4000A771?.Cast(ActionId.CosmoArrow, targetLocation: new Vector3(-0.008f, -0.015f, -0.008f), targetId: alpha_Omega_4000A771?.GameObjectId));
        if (solo) return;
        world.Events.Add(16.03f, () => alpha_Omega_4000A771?.Cast(ActionId.WaveCannon_7BA9, targetLocation: new Vector3(-0.008f, -0.015f, -0.008f), targetId: alpha_Omega_4000A771?.GameObjectId));
        world.Events.Add(27.30f, () => alpha_Omega_4000A771?.Face(party.Get(state.WildChargeTarget)!));
        world.Events.Add(27.40f, () => alpha_Omega_4000A771?.Cast(ActionId.WaveCannonWildCharge, castSeconds: 0f, targetId:party.Get(state.WildChargeTarget)?.GameObjectId));
        world.Events.Add(27.40f, () => damage.Resolve(alpha_Omega_4000A771, ActionId.WaveCannonWildCharge, [DamageType.Magic], [], stackMinTargets: 8, wildChargeTargets: 2, wildChargeDamageType: [DamageType.TankBuster]));
    }
    
    
    

    private void Run_Alpha_Omega_4000A40C()
    {
        for (int index = 0; index < 4; index++)
        {
            SimEnemy? alpha_Omega_4000A40C = null;
            var i = index;
            world.Events.Add(0f, () => alpha_Omega_4000A40C = world.SpawnEnemy(new EnemySpawnConfig(BNpcBaseId: BNpcBaseId.OmegaHelper, NameId: BNpcNameId.AlphaOmega, Level: 1, Targetable: false, EnemyList: EnemyListMode.Never, IsVisible: false, Placement: new Placement(new Vector3(0f, -0.000f, 0.000f), 1.570f))));
            world.Events.Add(19.00f, () => alpha_Omega_4000A40C?.Face(state.ProteanOrder.Get(i)));
            world.Events.Add(19.07f, () => alpha_Omega_4000A40C?.Cast(ActionId.WaveCannonProtean, castSeconds: 0f, targetId: state.ProteanOrder.Get(i)?.GameObjectId));
            world.Events.Add(19.07f, () => damage.Resolve(alpha_Omega_4000A40C, ActionId.WaveCannonProtean, [DamageType.Magic], [(StatusId.MagicVulnerabilityUp, 2.5f)]));
            world.Events.Add(21.00f, () => alpha_Omega_4000A40C?.Face(state.ProteanOrder.Get(i + 4)));
            world.Events.Add(21.07f, () => alpha_Omega_4000A40C?.Cast(ActionId.WaveCannonProtean, castSeconds: 0f, targetId: state.ProteanOrder.Get(i + 4)?.GameObjectId));
            world.Events.Add(21.07f, () => damage.Resolve(alpha_Omega_4000A40C, ActionId.WaveCannonProtean, [DamageType.Magic], [(StatusId.MagicVulnerabilityUp, 2.5f)]));
        }
        
    }
}
