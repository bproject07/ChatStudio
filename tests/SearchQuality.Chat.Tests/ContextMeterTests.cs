using SearchQuality.Chat;
using SearchQuality.Domain;

namespace SearchQuality.Chat.Tests;

public sealed class ContextMeterTests
{
    [Theory]
    [InlineData(0, 12000, 0.00, false, false)]
    [InlineData(8000, 12000, 0.6667, false, false)]
    [InlineData(10199, 12000, 0.8499, false, false)]   // точно под 85% — без предупреждение
    [InlineData(10200, 12000, 0.85, true, false)]      // 85% — предупреждение
    [InlineData(10800, 12000, 0.90, true, false)]      // 90% — само предупреждение
    [InlineData(11999, 12000, 0.9999, true, false)]    // точно под 100% — още не
    [InlineData(12000, 12000, 1.00, true, true)]       // 100% — автоматично компактиране
    [InlineData(20000, 12000, 1.00, true, true)]       // над бюджета — clamp до 100%
    public void Meter_thresholds_match_warning_and_compaction_rules(
        int used, int budget, double expectedPercent, bool expectedWarning, bool expectedImminent)
    {
        var meter = ChatService.CreateMeter(used, budget);

        Assert.Equal(expectedPercent, meter.Percent, precision: 4);
        Assert.Equal(expectedWarning, meter.Warning);
        Assert.Equal(expectedImminent, meter.CompactImminent);
    }

    [Fact]
    public void Meter_with_zero_budget_is_safe()
    {
        var meter = ChatService.CreateMeter(500, 0);

        Assert.Equal(0, meter.Percent, precision: 6);
        Assert.False(meter.Warning);
        Assert.False(meter.CompactImminent);
    }

    [Fact]
    public void Meter_uses_final_model_context_not_aggregate_tool_loop_cost()
    {
        var answer = new GeneratedAnswer(
            "готово",
            new TokenUsage(49_648, 6_436, 56_084),
            ContextInputTokens: 18_498);

        Assert.Equal(18_498, ChatService.ContextTokensFor(answer));
    }

    [Fact]
    public void Meter_falls_back_to_usage_for_single_generation()
    {
        var answer = new GeneratedAnswer(
            "готово",
            new TokenUsage(8_000, 400, 8_400));

        Assert.Equal(8_000, ChatService.ContextTokensFor(answer));
    }
}
