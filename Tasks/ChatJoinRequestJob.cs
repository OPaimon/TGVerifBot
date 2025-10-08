using Telegram.Bot.Types;

namespace TelegramVerificationBot.Tasks;

public record ChatJoinRequestJob(User User, long Chat, bool Approve);