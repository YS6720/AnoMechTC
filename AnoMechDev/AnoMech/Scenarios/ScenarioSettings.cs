using System;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace AnoMech.Scenarios;

/// <summary>
/// Builds <see cref="IScenario.SettingsIdentity"/> from a scenario's override object.
/// Reflection keeps a newly added override field part of the identity automatically —
/// a hand-written list would silently stop covering the very setting that was added.
/// Called once per run start / completion / retry dispatch, never per frame.
/// </summary>
public static class ScenarioSettings
{
    public static string Identity(object? overrides)
    {
        if (overrides is null) return "";
        var type = overrides.GetType();
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Array.Sort(properties, static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        var text = new StringBuilder(type.Name);
        foreach (var property in properties)
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
            text.Append('|').Append(property.Name).Append('=');
            text.Append(Convert.ToString(property.GetValue(overrides), CultureInfo.InvariantCulture) ?? "null");
        }
        return text.ToString();
    }
}
