using System.Collections.Generic;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios;

// The mechanic timeline for one encounter fragment. Shared identity lives on the owning
// IZone (via Phase.Zone) and IPhase; a scenario declares only what is its own.
public interface IScenario
{
    string Name { get; }

    /// <summary>
    /// Menu label for this scenario, drawn after the phase name (so it must not repeat
    /// it). Display only: <see cref="Name"/> stays the stored identity — scene key, run
    /// identity and the practice-position document all key off it — so a label change
    /// never moves saved practice data or a scene fingerprint. Defaults to the name.
    /// </summary>
    string MenuName => Name;

    // Its phase — usually `=> TopZone.P5`.
    IPhase Phase { get; }

    bool SupportsSolo => false;

    // Native channeling progress is fixed scene metadata, not a new wire field.
    // Peers use the same approved build/scene; legacy scenarios retain progress 1.
    byte TetherProgress => 1;

    // Selectable strats. Run's selectedAi indexes this (null = solo); region buttons derive
    // from each strat's IScenarioAi.Group.
    IReadOnlyList<IScenarioAi> AiStrats { get; }

    void Run(SimWorld world, int? selectedAi);
    void Tick(float delta, float elapsed) { }
    void DrawSettings() { }

    /// <summary>
    /// Whether <see cref="DrawSettings"/> actually draws anything. The default is a
    /// no-op, so the UI must be told explicitly: guessing would either show an empty
    /// "場景設定" header for every scenario that has none, or hide a real panel.
    /// </summary>
    bool HasSettings => false;

    /// <summary>
    /// Identity of the mutable settings this scenario will consume on the next Run.
    /// A run latches it, so an auto-retry or a win streak can never silently switch
    /// to values the user changed afterwards. Empty = the scenario has no settings.
    /// Computed only when a run starts, completes or a retry fires — never per frame.
    /// </summary>
    string SettingsIdentity => "";
}

/// <summary>One stable, user-facing starting point in a progress-capable scenario.</summary>
public sealed record ScenarioProgress(string Key, string Name);

/// <summary>
/// Optional scenario capability for selecting a named progress starting point.
/// The two-argument <see cref="IScenario.Run(SimWorld, int?)"/> entry remains the
/// full-scenario compatibility entry; callers selecting progress must use the
/// explicit three-argument dispatch.
/// </summary>
public interface IProgressScenario : IScenario
{
    IReadOnlyList<ScenarioProgress> Progresses { get; }

    void Run(SimWorld world, int? selectedAi, string progressKey);
}
