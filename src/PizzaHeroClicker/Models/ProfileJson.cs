using System.Text.Json;
using System.Text.Json.Serialization;

namespace PizzaHeroClicker.Models;

/// <summary>The one place that defines how profiles and settings are written to JSON.</summary>
public static class ProfileJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // Computed, get-only properties (Summary, TypeName, ...) are display helpers, not data.
        IgnoreReadOnlyProperties = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Keep "Ctrl+F6" and non-ASCII window titles readable instead of unicode escapes.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Deep copy via a JSON round trip. Used to hand the engine an isolated snapshot.</summary>
    public static Profile Clone(Profile profile)
    {
        var copy = Deserialize<Profile>(Serialize(profile))!;
        copy.Normalize();
        return copy;
    }

    public static ActionBase Clone(ActionBase action) => Deserialize<ActionBase>(Serialize(action))!;
}
