using System.Collections.Generic;
using AnoMech.Scenarios;

namespace AnoMech.Compat.Server;

/// <summary>
/// TC 伺服器層：只在單一伺服器註冊的場景（國際版見 Compat/Server/Global）。
/// 台服客戶端沒有 territory 1363，絕妖星亂舞（UMAD）連型別都不編進台服 DLL（AnoMech.Shared.targets）。
/// </summary>
internal static class ServerScenarios
{
    internal static IEnumerable<IScenario> Create() => [];
}
