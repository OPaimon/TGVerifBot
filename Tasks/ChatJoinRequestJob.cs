
namespace TelegramVerificationBot.Tasks;

public record ChatJoinRequestJob(long User, long Chat, bool Approve);