namespace Affiliate.Services
{
    /// <summary>Display tweaks for tracker product descriptions (RTL arrows, etc.).</summary>
    public static class TrackerDescriptionFormat
    {
        /// <summary>
        /// Amazon Arabic copy often uses LTR arrows (➜) between label and detail; mirror for RTL reading.
        /// </summary>
        public static string ForDisplay(string? text, bool arabic)
        {
            if (string.IsNullOrWhiteSpace(text) || !arabic)
                return text ?? string.Empty;

            return text
                .Replace("➜", "⬅", StringComparison.Ordinal)
                .Replace("➔", "⬅", StringComparison.Ordinal)
                .Replace("→", "←", StringComparison.Ordinal)
                .Replace("►", "◄", StringComparison.Ordinal)
                .Replace("»", "«", StringComparison.Ordinal)
                .Replace("=>", "←", StringComparison.Ordinal);
        }

        /// <summary>Splits description into bullet-like parts for list layout (period-separated only).</summary>
        public static IReadOnlyList<string> SplitForList(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return [];

            return text
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => p.Length >= 6)
                .ToList();
        }
    }
}
