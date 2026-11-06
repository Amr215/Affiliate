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

        /// <summary>Exclusive target for Amazon drops above 50% (skipped category/lowest when this chat is set).</summary>
        public string MegaDealsChatId { get; set; } = string.Empty;

        /// <summary>
        /// Exclusive target for Amazon Devices lowest-price drops when not a mega deal
        /// (≥30 days or lowest ever). Food/fashion use <see cref="FoodFashionLowestPriceChatId"/>.
        /// </summary>
        public string LowestPriceChatId { get; set; } = string.Empty;

        /// <summary>
        /// Exclusive target for Amazon FoodDrinksAndCleaning and FashionAndWatches lowest-price
        /// drops when not a mega deal (≥30 days or lowest ever).
        /// </summary>
        public string FoodFashionLowestPriceChatId { get; set; } = string.Empty;

        /// <summary>Default Noon drop chat when the alert is neither mega nor lowest-price.</summary>
        public string NoonChatId { get; set; } = string.Empty;

        /// <summary>Exclusive target for Noon drops above 50% (skipped Noon/lowest when this chat is set).</summary>
        public string NoonMegaDealsChatId { get; set; } = string.Empty;

        /// <summary>Exclusive target for Noon lowest-price drops when not a mega deal (≥30 days or lowest ever).</summary>
        public string NoonLowestPriceChatId { get; set; } = string.Empty;

        /// <summary>Group where posted Amazon links get «نشر عرض / نشر عرض ممتاز / نشر لقطة / نشر ببلاش» buttons.</summary>
        public string PublishChatId { get; set; } = string.Empty;

        /// <summary>Group where posted Noon links get the same publish template buttons (Chromium/proxy screenshots).</summary>
        public string NoonPublishChatId { get; set; } = string.Empty;

        /// <summary>Channel that also receives every post published from the publish group.</summary>
        public string PublishChannelId { get; set; } = string.Empty;

        /// <summary>Chat that receives full prepare-for-publish exception / attempt logs (not the user-facing short error).</summary>
        public string PublishLogChatId { get; set; } = string.Empty;

        /// <summary>
        /// Amazon Associates tracking id applied by the public <c>/{code}</c> redirect
        /// (not shown in publish captions).
        /// </summary>
        public string AffiliateTag { get; set; } = string.Empty;

        /// <summary>
        /// Public site base used in publish captions (e.g. <c>https://el-sindbad.com</c>).
        /// Captions link to <c>{PublishLinkBaseUrl}/{code}</c>, which redirects to Amazon with <see cref="AffiliateTag"/>.
        /// </summary>
        public string PublishLinkBaseUrl { get; set; } = "https://el-sindbad.com";

        /// <summary>
        /// Secret used to encode ASINs into short opaque link codes.
        /// Changing it invalidates previously published links.
        /// </summary>
        public string LinkCodeSecret { get; set; } = string.Empty;

        /// <summary>First configured chat; used for the test message and setup checks.</summary>
        public string? PrimaryChatId =>
            new[] { FoodDrinksAndCleaningChatId, FashionAndWatchesChatId, DevicesChatId, MegaDealsChatId }
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
    }
}
