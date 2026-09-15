namespace AnoMech;

// Compile-time identity shared by every UI/service owner. The build profile is
// selected by AnoMechDev/Directory.Build.props; no runtime edition toggle exists.
internal static class BuildEdition
{
    public const bool IsDeveloper = false;
    public const string DisplayName = "使用者版";
}
