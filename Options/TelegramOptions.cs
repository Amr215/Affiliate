namespace Affiliate.Options
{
    public class TelegramOptions
    {
        public const string SectionName = "Telegram";

        /// <summary>When false, alerts are logged only and not sent.</summary>
        public bool Enabled { get; set; }

        /// <summary>Bot token from @BotFather (e.g. 123456:ABC...).</summary>
        public string BotToken { get; set; } = string.Empty;

        /// <summary>Chat for price drops from 3% (inclusive) up to 10% (exclusive).</summary>
        public string ChatId3To10 { get; set; } = string.Empty;

        /// <summary>Chat for price drops from 10% (inclusive) up to 20% (exclusive).</summary>
        public string ChatId10To20 { get; set; } = string.Empty;

        /// <summary>Chat for price drops from 20% (inclusive) up to 40% (exclusive).</summary>
        public string ChatId20To40 { get; set; } = string.Empty;

        /// <summary>Chat for price drops from 40% (inclusive) up to 60% (exclusive).</summary>
        public string ChatId40To60 { get; set; } = string.Empty;

        /// <summary>Chat for price drops from 60% (inclusive) up to 80% (exclusive).</summary>
        public string ChatId60To80 { get; set; } = string.Empty;

        /// <summary>Chat for price drops from 80% (inclusive) up to 100%.</summary>
        public string ChatId80To100 { get; set; } = string.Empty;

        /// <summary>Lowest configured tier chat; used for the test message and setup checks.</summary>
        public string? PrimaryChatId =>
            new[] { ChatId3To10, ChatId10To20, ChatId20To40, ChatId40To60, ChatId60To80, ChatId80To100 }
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
    }
}
