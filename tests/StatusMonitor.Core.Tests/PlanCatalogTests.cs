using StatusMonitor.Core.Tenancy;
using Xunit;

namespace StatusMonitor.Core.Tests;

public class PlanCatalogTests
{
    [Fact]
    public void Unknown_or_missing_plan_ids_fall_back_to_free()
    {
        Assert.Same(PlanCatalog.Free, PlanCatalog.Resolve(null));
        Assert.Same(PlanCatalog.Free, PlanCatalog.Resolve("legacy-plan"));
    }

    [Fact]
    public void Plan_lookup_is_case_insensitive()
    {
        Assert.Same(PlanCatalog.Pro, PlanCatalog.Resolve("PRO"));
    }

    [Fact]
    public void Free_tier_is_the_cheapest_and_most_limited()
    {
        foreach (var plan in PlanCatalog.All)
        {
            Assert.True(plan.MaxUrls >= PlanCatalog.Free.MaxUrls);
            Assert.True(plan.PollIntervalMinutes <= PlanCatalog.Free.PollIntervalMinutes);
            Assert.True(plan.MonthlyPriceUsd >= PlanCatalog.Free.MonthlyPriceUsd);
        }
    }
}
