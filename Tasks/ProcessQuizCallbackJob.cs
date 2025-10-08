using Telegram.Bot.Types;

namespace TelegramVerificationBot.Tasks;

public record ProcessQuizCallbackJob(string CallbackData, User User, Message Message);