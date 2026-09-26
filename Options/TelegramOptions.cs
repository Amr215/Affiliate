namespace Affiliate.Options
{
    public class TelegramOptions
    {
        public const string SectionName = "Telegram";

        /// <summary>When false, alerts are logged only and not sent.</summary>
        public bool Enabled { get; set; }

        /// <summary>Bot token from @BotFather (e.g. 123456:ABC...).</summary>
        public string BotToken { get; set; } = string.Empty;

        /// <summary>
        /// When true (default), a background service long-polls getUpdates for
        /// «تجهيز للنشر» callbacks. Set false if you use an external webhook instead.
        /// </summary>
        public bool PollUpdates { get; set; } = true;

        /// <summary>Chat for drops on food, drinks and cleaning URLs.</summary>
        public string FoodDrinksAndCleaningChatId { get; set; } = string.Empty;

        /// <summary>Chat for drops on fashion and watches URLs.</summary>
        public string FashionAndWatchesChatId { get; set; } = string.Empty;

        /// <summary>Chat for drops on devices URLs.</summary>
        public string DevicesChatId { get; set; } = string.Empty;

        /// <summary>Chat that also receives every drop above 50%, whatever the category.</summary>
        public string MegaDealsChatId { get; set; } = string.Empty;

        /// <summary>First configured chat; used for the test message and setup checks.</summary>
        public string? PrimaryChatId =>
            new[] { FoodDrinksAndCleaningChatId, FashionAndWatchesChatId, DevicesChatId, MegaDealsChatId }
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
    }
}
