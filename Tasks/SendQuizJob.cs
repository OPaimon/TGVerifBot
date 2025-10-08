namespace TelegramVerificationBot.Tasks;

using Telegram.Bot.Types;
using TelegramVerificationBot.Models;

public record SendQuizJob(long UserChatId, User User,Chat Chat, string Question, List<OptionWithToken> OptionsWithTokens, ChatInviteLink? Link);