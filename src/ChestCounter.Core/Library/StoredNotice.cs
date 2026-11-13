using System.Text.Json;

namespace ChestCounter.Core.Library;

/// <summary>A "never miss a match" notice, persisted in the database meta table.</summary>
public sealed record StoredNotice(int Count, DateTime FromUtc, DateTime ToUtc, DateTime AtUtc)
{
    /// <summary>Matches that were played while the app was closed and counted when it started.</summary>
    public const string CatchUpKey = "notice_catchup";

    /// <summary>Replays Fortnite may have deleted before the app could read them.</summary>
    public const string GapKey = "notice_gap";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static StoredNotice? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var n = JsonSerializer.Deserialize<StoredNotice>(json, Options);
            return n is null ? null : n with
            {
                FromUtc = DateTime.SpecifyKind(n.FromUtc, DateTimeKind.Utc),
                ToUtc = DateTime.SpecifyKind(n.ToUtc, DateTimeKind.Utc),
                AtUtc = DateTime.SpecifyKind(n.AtUtc, DateTimeKind.Utc),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
