using Telegram.Bot.Types;

namespace TelegramVerificationBot.Tasks;

public record EditMessageJob(Message Message, string NewText);