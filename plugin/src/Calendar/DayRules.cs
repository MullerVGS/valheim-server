namespace ValheimMetrics.Calendar
{
    // What the dawn message says. Outside the countdown to a new year the day shown is fixed.
    // Years are YearDays game days long; year 1 starts at day 0.
    public static class DayRules
    {
        public const string Variable = "VALHEIM_SHOWN_DAY";
        public const int YearDays = 365;
        public const int CountdownDays = 7;

        public static int? Parse(string raw) =>
            int.TryParse(raw?.Trim(), out var day) && day >= 0 ? day : (int?)null;

        // Days until the next year starts: 1 on its eve, YearDays on the new year's day itself.
        public static int DaysToNewYear(int day) => YearDays - day % YearDays;

        public static int NextYear(int day) => day / YearDays + 2;

        // The center message is a single TMP text; a line break avoids glyphs the game font may lack.
        public static string Text(int day, int shownDay)
        {
            int left = DaysToNewYear(day);
            return left <= CountdownDays
                ? $"Dia {day}\nD-{left} para o Ano {NextYear(day)}"
                : $"Dia {shownDay}";
        }
    }
}
