using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>Switches the app language: runs apart from other tests that read or switch it.</summary>
[Collection("Language")]
public class ColituPlanNoticeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static ColituSubscription Map(string json) =>
        ColituAuthService.MapSubscription(JsonSerializer.Deserialize<ColituEntitlementDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));

    private static T InEnglish<T>(Func<T> action)
    {
        var previous = Loc.I.Language;
        try
        {
            Loc.I.SetLanguage("en");
            return action();
        }
        finally
        {
            Loc.I.SetLanguage(previous);
        }
    }

    private static ColituSubscription Trial(int? devices, int? nextLimit, long? nextBytes, double daysLeft = 2.2, string status = "trialing", string? nextPlan = "free") => new()
    {
        Active = true,
        Status = status,
        PlanName = "trial",
        EndsAt = Now.AddDays(daysLeft).ToString("O"),
        NextPlan = nextPlan,
        NextDeviceLimit = nextLimit,
        DeviceCount = devices,
        NextTrafficLimitBytes = nextBytes
    };

    [Fact]
    public void Entitlement_ReadsTheNewFieldsTolerantly()
    {
        var subscription = Map("""
            {"status":"trialing","plan":"trial","expires_at":"2026-10-08T00:00:00Z","device_limit":5,
             "ends_at":"2026-10-08T12:00:00Z","next_plan":"free","next_device_limit":1,"device_count":3,
             "next_traffic_limit_bytes":10000000000,"something_new":{"a":1}}
            """);
        subscription.EndsAt.Should().Be("2026-10-08T12:00:00Z");
        subscription.NextPlan.Should().Be("free");
        subscription.NextDeviceLimit.Should().Be(1);
        subscription.DeviceCount.Should().Be(3);
        subscription.NextTrafficLimitBytes.Should().Be(10_000_000_000);
    }

    [Fact]
    public void Entitlement_AcceptsNextPlanAsAnObjectAndMissingFields()
    {
        var subscription = Map("""
            {"status":"active","plan":"premium","devices":{"used":2,"limit":5},
             "next_plan":{"name":"free","device_limit":1,"traffic_limit_gb":10}}
            """);
        subscription.NextPlan.Should().Be("free");
        subscription.NextDeviceLimit.Should().Be(1);
        subscription.NextTrafficLimitBytes.Should().Be(10_000_000_000);
        subscription.DeviceCount.Should().Be(2, "devices.used is the fallback count");
        subscription.EndsAt.Should().BeNull();

        Map("""{"status":"active","next_device_limit":"oops"}""").NextDeviceLimit.Should().BeNull();
    }

    /// <summary>The end date as the banner writes it in English (local time, like the app).</summary>
    private static string EndDate(double daysLeft) =>
        Now.AddDays(daysLeft).ToLocalTime().ToString("MMMM d", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    [Fact]
    public void Banner_OverTheLimit_SaysWhichDeviceStays()
    {
        InEnglish(() => ColituPlanNotice.Build(Trial(devices: 3, nextLimit: 1, nextBytes: 10_000_000_000), Now, Loc.I))
            .Should().Be($"Your trial ends on {EndDate(2.2)}. After that only 1 device stays active: the one you used most recently. The others are paused, not signed out.");
        InEnglish(() => ColituPlanNotice.Build(Trial(devices: 4, nextLimit: 2, nextBytes: null), Now, Loc.I))
            .Should().Contain("only 2 devices stays active");
    }

    [Fact]
    public void Banner_WithoutDeviceNumbers_IsTheGeneralWarning()
    {
        InEnglish(() => ColituPlanNotice.Build(Trial(devices: null, nextLimit: null, nextBytes: null), Now, Loc.I))
            .Should().Be($"Your trial ends on {EndDate(2.2)}. You’ll move to the free plan; the device you used most recently stays active, the others are paused, not signed out.");
    }

    [Fact]
    public void Banner_WithinTheLimit_IsTheSimpleOne()
    {
        InEnglish(() => ColituPlanNotice.Build(Trial(devices: 1, nextLimit: 1, nextBytes: 10_000_000_000, daysLeft: 0.2), Now, Loc.I))
            .Should().Be($"Your trial ends on {EndDate(0.2)}. You’ll move to the free plan.");
    }

    [Fact]
    public void Banner_PaidPlanMovingToFree()
    {
        InEnglish(() => ColituPlanNotice.Build(Trial(devices: 1, nextLimit: 1, nextBytes: null, status: "active"), Now, Loc.I))
            .Should().StartWith("Your trial ends", "the plan name 'trial' still marks a trial");
        var paid = Trial(devices: 1, nextLimit: 1, nextBytes: null, status: "active");
        paid.PlanName = "premium";
        InEnglish(() => ColituPlanNotice.Build(paid, Now, Loc.I)).Should().StartWith($"Your plan ends on {EndDate(2.2)}.");
    }

    [Fact]
    public void Entitlement_ReadsTheContractDeviceCounts()
    {
        var subscription = Map("""
            {"status":"trialing","plan":"trial","ends_at":"2026-10-08T12:00:00Z","next_plan":"free","next_device_limit":1,
             "devices":{"active":2,"suspended":1,"registered":3,"limit":5}}
            """);
        subscription.DeviceCount.Should().Be(3, "registered devices decide the warning");
        subscription.DevicesUsed.Should().Be(2);
    }

    [Fact]
    public void Banner_StaysHiddenOutsideTheLastThreeDaysOrWhenThePlanRenews()
    {
        ColituPlanNotice.Build(Trial(3, 1, null, daysLeft: 3.5), Now, Loc.I).Should().BeNull();
        ColituPlanNotice.Build(Trial(3, 1, null, daysLeft: -0.1), Now, Loc.I).Should().BeNull();
        var renewing = Trial(3, 1, null, status: "active", nextPlan: "premium");
        renewing.PlanName = "premium";
        ColituPlanNotice.Build(renewing, Now, Loc.I).Should().BeNull();
        var paidWithoutNext = Trial(3, 1, null, status: "active", nextPlan: null);
        paidWithoutNext.PlanName = "premium";
        ColituPlanNotice.Build(paidWithoutNext, Now, Loc.I).Should().BeNull();
        ColituPlanNotice.Build(null, Now, Loc.I).Should().BeNull();
    }

    [Theory]
    [InlineData(10_000_000_000L, "10")]
    [InlineData(10_737_418_240L, "10")]
    [InlineData(5_500_000_000L, "5.5")]
    public void FreeTraffic_IsShownInGigabytes(long bytes, string expected)
    {
        ColituPlanNotice.FreeGigabytes(bytes).Should().Be(expected);
    }

    [Fact]
    public void Pause_IsReadFromTheDeviceOverLimitBody()
    {
        var pause = ColituDevicePause.Parse("""
            {"error":{"code":"DEVICE_OVER_LIMIT","message":"Device limit reached"},"device_limit":1,
             "active_devices":[{"id":"d1","name":"Work laptop","last_seen_at":"2026-10-06T10:00:00Z"}],"extra":true}
            """);
        pause.DeviceLimit.Should().Be(1);
        pause.ActiveDevices.Should().ContainSingle(d => d.Id == "d1" && d.Name == "Work laptop");
        pause.Message.Should().Be("Device limit reached");
        InEnglish(() => pause.Describe(Loc.I)).Should().Be("Your plan allows 1 device. Active: Work laptop.");
    }

    [Fact]
    public void Pause_ToleratesMissingOrBrokenDetails()
    {
        var empty = ColituDevicePause.Parse("not json");
        empty.DeviceLimit.Should().BeNull();
        empty.ActiveDevices.Should().BeEmpty();
        InEnglish(() => empty.Describe(Loc.I)).Should().Be("Your plan allows fewer devices than you use, so this one is paused.");
        InEnglish(() => ColituDevicePause.Parse("""{"device_limit":2}""").Describe(Loc.I))
            .Should().Be("Your plan allows 2 devices; another device is active right now.");
    }

    [Fact]
    public void Pause_HasAFriendlyMessage()
    {
        ColituAuthService.FriendlyMessage("DEVICE_OVER_LIMIT", "raw", System.Net.HttpStatusCode.Forbidden).Should().Be(Loc.I["err.devicePaused"]);
    }
}
