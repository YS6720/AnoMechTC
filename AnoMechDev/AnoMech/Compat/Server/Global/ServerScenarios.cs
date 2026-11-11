using System.Collections.Generic;
using AnoMech.Scenarios;
using AnoMech.Scenarios.Umad;
using AnoMech.Scenarios.Umad.P1TeleTrouncing;
using AnoMech.Scenarios.Umad.P2Forsaken;
using AnoMech.Scenarios.Umad.P3BlackHole;
using AnoMech.Scenarios.Umad.P3LimitCut;
using AnoMech.Scenarios.Umad.P4KefkaSays;
using AnoMech.Scenarios.Umad.P5Celestriad;
using AnoMech.Scenarios.Umad.P5Exaflares;
using AnoMech.Scenarios.Umad.P5Flood;

namespace AnoMech.Compat.Server;

/// <summary>
/// 國際服伺服器層：只在國際版註冊的場景（台服版見 Compat/Server/Tc，回傳空清單）。
/// 絕妖星亂舞（UMAD）為上游 anomek/AnoMech 1646a70 原檔（B10a），順序照上游 ScenarioCatalog；名稱沿用上游英文。
/// 這 7 段沒有宣告 NetworkMapEffects，先只開單人：多人時未宣告的地圖效果會被略過（WorldReplicator.SkipUndeclared），成員看不到。
/// </summary>
internal static class ServerScenarios
{
    internal static IEnumerable<IScenario> Create() =>
    [
        new UmadP1TeleTrouncingScenario(),
        new UmadP2ForsakenScenario(),
        new UmadP3LimitCutScenario(),
        new UmadP3BlackHoleScenario(),
        new UmadP4KefkaSaysScenario(),
        new UmadP5FloodScenario(),
        new UmadP5CelestriadScenario(),
        new UmadP5ExaflaresScenario(),
        new UmadP5ForsakenNull(),
    ];
}
