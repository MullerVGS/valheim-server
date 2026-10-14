using ValheimMetrics.Calendar;
using Xunit;

public class DayRulesTests
{
    [Theory]
    [InlineData(750)]
    [InlineData(0)]
    [InlineData(357)]
    [InlineData(365)]
    [InlineData(1087)]
    public void OutsideTheCountdownShowsTheFixedDay(int day) =>
        Assert.Equal("Dia 420", DayRules.Text(day, 420));

    [Theory]
    [InlineData(1088, "Dia 1088\nD-7 para o Ano 4")]
    [InlineData(1094, "Dia 1094\nD-1 para o Ano 4")]
    [InlineData(358, "Dia 358\nD-7 para o Ano 2")]
    public void WeekBeforeTheNewYearShowsTheRealDayAndCountdown(int day, string expected) =>
        Assert.Equal(expected, DayRules.Text(day, 420));

    [Fact]
    public void NewYearsDayStartsTheNextYear()
    {
        Assert.Equal(DayRules.YearDays, DayRules.DaysToNewYear(1095));
        Assert.Equal(5, DayRules.NextYear(1095));
    }

    [Theory]
    [InlineData("420", 420)]
    [InlineData(" 0 ", 0)]
    [InlineData("-1", null)]
    [InlineData("dia", null)]
    [InlineData("", null)]
    public void ParsesAWholeNumberOfDays(string raw, int? expected) =>
        Assert.Equal(expected, DayRules.Parse(raw));
}
