namespace StatusMonitor.Core.Tenancy;

/// <summary>
/// A subscription plan and the hard limits it enforces.
/// Prices are billed through Stripe; the IDs here map 1:1 to Stripe Price lookup keys.
/// </summary>
public sealed record PlanDefinition(
    string Id,
    string Name,
    int MaxUrls,
    int PollIntervalMinutes,
    int MaxAlertRecipients,
    int HistoryRetentionDays,
    decimal MonthlyPriceUsd);

public static class PlanCatalog
{
    public const string FreePlanId = "free";

    public static readonly PlanDefinition Free = new(
        Id: FreePlanId, Name: "Free", MaxUrls: 10, PollIntervalMinutes: 60,
        MaxAlertRecipients: 1, HistoryRetentionDays: 7, MonthlyPriceUsd: 0m);

    public static readonly PlanDefinition Starter = new(
        Id: "starter", Name: "Starter", MaxUrls: 50, PollIntervalMinutes: 15,
        MaxAlertRecipients: 5, HistoryRetentionDays: 30, MonthlyPriceUsd: 9m);

    public static readonly PlanDefinition Pro = new(
        Id: "pro", Name: "Pro", MaxUrls: 250, PollIntervalMinutes: 5,
        MaxAlertRecipients: 15, HistoryRetentionDays: 90, MonthlyPriceUsd: 29m);

    public static readonly PlanDefinition Business = new(
        Id: "business", Name: "Business", MaxUrls: 1000, PollIntervalMinutes: 1,
        MaxAlertRecipients: 50, HistoryRetentionDays: 365, MonthlyPriceUsd: 99m);

    private static readonly Dictionary<string, PlanDefinition> ById =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [Free.Id] = Free,
            [Starter.Id] = Starter,
            [Pro.Id] = Pro,
            [Business.Id] = Business,
        };

    public static IReadOnlyCollection<PlanDefinition> All => ById.Values;

    /// <summary>Unknown/legacy plan ids degrade gracefully to the free tier.</summary>
    public static PlanDefinition Resolve(string? planId) =>
        planId is not null && ById.TryGetValue(planId, out var plan) ? plan : Free;
}
