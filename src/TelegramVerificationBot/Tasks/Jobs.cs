using Telegram.Bot.Types;
using TelegramVerificationBot.Models;
using Chat = Telegram.Bot.Types.Chat;
using Message = Telegram.Bot.Types.Message;
using User = Telegram.Bot.Types.User;

namespace TelegramVerificationBot.Tasks;

public record ChatJoinRequestJob(long User, long Chat, bool Approve);
public record EditMessageJob(long ChatId, int MessageId, string NewText);
public record ProcessQuizCallbackJob(string CallbackData, User User, Message Message);
public record RedisKeyEventJob(string Key, string Event);
public record RespondToPingJob(long ChatId);
public record SendQuizJob(long UserChatId, User User, Chat Chat, string Question, List<OptionWithToken> OptionsWithTokens, ChatInviteLink? Link);
public record SendQuizCallbackJob(long UserId, long ChatId, int MessageId, long MessageChatId);
public record StartVerificationJob(ChatJoinRequest Requester);
