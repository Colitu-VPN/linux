using System.Text.Json;

namespace v2rayN.Desktop.Services;

/// <summary>
/// The panel paused this device: the plan allows fewer devices than the account has
/// (for example after a trial ends and the account drops to the free plan's single
/// device). Read from HTTP 403 DEVICE_OVER_LIMIT; every field is optional.
/// </summary>
public sealed class ColituDevicePause
{
    public const string ErrorCode = "DEVICE_OVER_LIMIT";

    public int? DeviceLimit { get; init; }
    public List<ColituActiveDevice> ActiveDevices { get; init; } = [];
    public string? Message { get; init; }

    /// <summary>Reads the 403 body; unknown fields are ignored, a broken body still gives a pause without details.</summary>
    internal static ColituDevicePause Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new ColituDevicePause();
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ColituDevicePause();
            }
            string? message = null;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                message = m.GetString();
            }
            int? limit = root.TryGetProperty("device_limit", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var value) && value >= 0
                ? value
                : null;
            var devices = new List<ColituActiveDevice>();
            if (root.TryGetProperty("active_devices", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    devices.Add(new ColituActiveDevice
                    {
                        Id = StringOf(item, "id"),
                        Name = StringOf(item, "name"),
                        LastSeenAt = StringOf(item, "last_seen_at")
                    });
                }
            }
            return new ColituDevicePause { DeviceLimit = limit, ActiveDevices = devices, Message = message };
        }
        catch (JsonException)
        {
            return new ColituDevicePause();
        }
    }

    private static string? StringOf(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>"This device is paused — your plan allows 1 device. Active: Laptop."</summary>
    public string Describe(Loc loc)
    {
        var names = ActiveDevices.Select(device => device.Name?.Trim()).Where(name => !string.IsNullOrEmpty(name)).Distinct().ToList();
        if (DeviceLimit is not int limit)
        {
            return names.Count > 0
                ? loc.Format("paused.bodyNoLimit", ("names", string.Join(", ", names)))
                : loc["paused.bodyPlain"];
        }
        var allowed = loc.Count("device", limit);
        return names.Count > 0
            ? loc.Format("paused.body", ("limit", allowed), ("names", string.Join(", ", names)))
            : loc.Format("paused.bodyNoNames", ("limit", allowed));
    }
}

public sealed class ColituActiveDevice
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? LastSeenAt { get; init; }
}

/// <summary>
/// The banner shown in the last days of a trial or paid plan that moves to the free plan.
/// Numbers come from the panel (next_device_limit, the free plan's traffic); without them
/// the text says the same without numbers.
/// </summary>
public static class ColituPlanNotice
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(3);

    /// <summary>The banner text, or null when there is nothing to say.</summary>
    public static string? Build(ColituSubscription? subscription, DateTimeOffset now, Loc loc)
    {
        if (subscription is not { Active: true })
        {
            return null;
        }
        var trial = string.Equals(subscription.Status, "trialing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subscription.PlanName, "trial", StringComparison.OrdinalIgnoreCase);
        // Only a plan that moves to the free one: a paid plan that renews says nothing.
        var toFree = subscription.NextPlan is { Length: > 0 } next
            ? string.Equals(next.Trim(), "free", StringComparison.OrdinalIgnoreCase)
            : trial;
        if (!toFree)
        {
            return null;
        }
        var endText = subscription.EndsAt ?? subscription.ExpiresAt;
        if (!DateTimeOffset.TryParse(endText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end))
        {
            return null;
        }
        var left = end - now;
        if (left <= TimeSpan.Zero || left > Window)
        {
            return null;
        }

        var date = end.ToLocalTime().ToString(loc.Language == "en" ? "MMMM d" : "d MMMM", loc.Culture);
        var ends = loc.Format(trial ? "planEnd.trial" : "planEnd.paid", ("date", date));
        var limit = subscription.NextDeviceLimit;
        var count = subscription.DeviceCount;
        if (limit is int nextLimit && count is int devices && devices > nextLimit)
        {
            return loc.Format("planEnd.over", ("ends", ends), ("devices", loc.Count("device", nextLimit)));
        }
        if (count is int known && limit is int allowed && known <= allowed)
        {
            return loc.Format("planEnd.within", ("ends", ends));
        }
        // Device numbers unknown: the general warning.
        return loc.Format("planEnd.overPlain", ("ends", ends));
    }

    /// <summary>10 GB from 10 000 000 000 or 10 737 418 240 bytes; null when unknown.</summary>
    internal static string? FreeGigabytes(long? bytes)
    {
        if (bytes is not long value || value <= 0)
        {
            return null;
        }
        // Decimal gigabytes when the panel counts in round megabytes (10 000 000 000), binary otherwise.
        var gb = value % 1_000_000 == 0 ? value / 1_000_000_000d : value / 1_073_741_824d;
        return gb.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
