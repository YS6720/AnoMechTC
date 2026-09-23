using System;
using System.Collections.Generic;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios;

// A raid territory: the identity every scenario inside it shares. Authored bottom-up —
// scenarios point at their IPhase, phases point here — and Game derives the downward
// zone -> phase -> scenario view for the menu.
public interface IZone
{
    string Name { get; }                                    // canonical duty name
    uint TerritoryId { get; }
    Vector3 Origin { get; }
    byte Level => 0;
    ushort ItemLevel => 0;

    // 同一個 territory 內選哪一套場地（LoadZone 的第 3 參數 / LayoutManager.LayerFilterKey）。
    // 0＝預設。單一場地的副本（如 TOP）留 0；DSR 這種一圖多場地的要指定。
    uint LayerFilterKey => 0;

    // At least one; [0] is the default.
    IReadOnlyList<WaymarkLayout> WaymarkPresets { get; }

    // Scenario-local positions whose BG SharedGroup colliders are dropped at start.
    IReadOnlyList<Vector3> ColliderRemovalPoints => Array.Empty<Vector3>();

    // Every MapEffect this zone's own Run emits (packetFlags: high16=State, low8=Flags).
    // Multiplayer peers apply only effects the approved scene declares; an undeclared
    // effect aborts the run instead of reaching native ProcessMapEffect.
    IEnumerable<(uint PacketFlags, byte Index)> NetworkMapEffects => [];

    // Zone-wide setup, first in the cascade. Default no-op.
    void Run(SimWorld world) { }
}
