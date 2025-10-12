using StackExchange.Redis;
using Telegram.Bot.Types;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Tasks;
using System.Text.Json;

namespace TelegramVerificationBot;

/// <summary>
/// Handles the core logic of user verification, including starting the process and handling quiz callbacks.
/// </summary>
public class VerificationService(ILogger<VerificationService> logger, FunctionalTaskDispatcher dispatcher, IDatabase redisDb, QuizService quizService, AppJsonSerializerContext jsonContext)
{

    private async Task<bool> IsUserStatusPendingOrBlacklistedAsync(long userId, long chatId)
    {
        var userStatusKey = $"user_status:{userId}:{chatId}";
        if (await redisDb.KeyExistsAsync(userStatusKey))
        {
            logger.LogWarning("User {UserId} has an existing status (possibly blacklisted or pending) in chat {ChatId}, rejecting join request.", userId, chatId);
            return true;
        }
        return false;
    }

    private record VerificationQuizData(List<OptionWithToken> OptionsWithTokens, string CorrectToken, string Question);

    private async Task<VerificationQuizData> PrepareVerificationQuizAsync()
    {
        var quiz = await quizService.GetQuizRandomAsync();
        List<OptionWithToken> optionsWithTokens = [.. quiz.Options.Select(optionText => new OptionWithToken(Option: optionText, Token: Guid.NewGuid().ToString()))];
        var correctToken = optionsWithTokens[quiz.CorrectOptionIndex].Token;

        var random = new Random();
        var shuffledOptions = optionsWithTokens.OrderBy(option => random.Next()).ToList();

        return new VerificationQuizData(shuffledOptions, correctToken, quiz.Question);
    }

    private async Task StoreVerificationStateAsync(long userId, long chatId, string correctToken)
    {
        var userStatusKey = $"user_status:{userId}:{chatId}";
        var verificationTokenKey = $"verification_token:{correctToken}";

        var stateObj = new VerificationState(
            UserId: userId,
            ChatId: chatId
        );
        var stateJson = JsonSerializer.Serialize(stateObj, jsonContext.VerificationState);

        await Task.WhenAll(
            // This key expires first and triggers the notification with user/chat info in its name.
            redisDb.StringSetAsync(userStatusKey, correctToken, TimeSpan.FromMinutes(1)),
            // This key lives slightly longer to prevent race conditions and for potential debugging.
            redisDb.StringSetAsync(verificationTokenKey, stateJson, TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(10)))
        );
    }
    /// <summary>
    /// Initiates a new verification process for a user joining a chat.
    /// </summary>
    public async Task HandleStartVerificationAsync(StartVerificationJob job)
    {
        logger.LogInformation("Received verification request for user {UserId} in chat {ChatId}", job.Requester.From.Id, job.Requester.Chat.Id);

        var user = job.Requester.From;
        var chat = job.Requester.Chat;

        if (await IsUserStatusPendingOrBlacklistedAsync(user.Id, chat.Id))
        {
            return;
        }

        var quizData = await PrepareVerificationQuizAsync();

        await StoreVerificationStateAsync(user.Id, chat.Id, quizData.CorrectToken);

        logger.LogInformation("Dispatching SendQuizJob for user {UserId}", user.Id);
        await dispatcher.DispatchAsync(new SendQuizJob(
            job.Requester.UserChatId, 
            user, 
            chat, 
            quizData.Question,
            quizData.OptionsWithTokens, 
            job.Requester.InviteLink
        ));
    }

    private record CallbackInfo(long ChatId, string Token);

    private CallbackInfo? ParseCallbackData(string callbackData)
    {
        ReadOnlySpan<char> callbackSpan = callbackData;
        int separatorIndex = callbackSpan.IndexOf('_');

        if (separatorIndex == -1)
        {
            logger.LogError("Invalid callback data format: {CallbackData}", callbackData);
            return null;
        }

        var chatIdSpan = callbackSpan.Slice(0, separatorIndex);
        var tokenSpan = callbackSpan.Slice(separatorIndex + 1);

        if (!long.TryParse(chatIdSpan, out var chatId))
        {
            logger.LogError("Invalid chat ID in callback data: {ChatIdPart}", chatIdSpan.ToString());
            return null;
        }

        return new CallbackInfo(chatId, tokenSpan.ToString());
    }

    private async Task<bool> HandlePreVerificationStatusAsync(User user, long chatId, Message message)
    {
        var userStatusKey = $"user_status:{user.Id}:{chatId}";
        var status = await redisDb.StringGetAsync(userStatusKey);

        if (status.HasValue && status.ToString().Equals("Cooldown", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("User {UserId} is in cooldown period in chat {ChatId}, ignoring callback.", user.Id, chatId);
            var cooldownText = "❌ 你在冷却时间内，请稍后再试。";
            await dispatcher.DispatchAsync(new EditMessageJob(message, cooldownText));
            return true; // 流程已处理，应终止
        }

        if (!status.HasValue)
        {
            logger.LogWarning("No active verification found for user {UserId} in chat {ChatId}, ignoring callback.", user.Id, chatId);
            var outdateText = "❌ 验证已过期，入群请求已被拒绝。";
            var joinRequestTask = dispatcher.DispatchAsync(new ChatJoinRequestJob(user, chatId, false));
            var editMessageTask = dispatcher.DispatchAsync(new EditMessageJob(message, outdateText));
            await Task.WhenAll(joinRequestTask, editMessageTask);
            return true; // 流程已处理，应终止
        }

        return false; // 用户状态正常，继续主流程
    }


    private async Task<bool> VerifyAnswerAndCleanupAsync(User user, long chatId, string clickedToken)
    {
        var verificationTokenKey = $"verification_token:{clickedToken}";
        var stateJson = await redisDb.StringGetDeleteAsync(verificationTokenKey); // 原子操作

        if (stateJson.IsNullOrEmpty)
        {
            logger.LogWarning("Verification failed for user {UserId}: incorrect option or expired token ({Token})", user.Id, clickedToken);
            // 回答错误，设置冷却
            var userStatusKey = $"user_status:{user.Id}:{chatId}";
            await redisDb.StringSetAsync(userStatusKey, "Cooldown", TimeSpan.FromMinutes(1));
            logger.LogInformation("Set 1-minute cooldown for user {UserId} on chat {ChatId}", user.Id, chatId);
            return false;
        }

        var state = System.Text.Json.JsonSerializer.Deserialize<VerificationState>(stateJson.ToString(), jsonContext.VerificationState);
        if (state?.UserId == user.Id && state?.ChatId == chatId)
        {
            logger.LogInformation("User {UserId} passed verification for chat {ChatId}", user.Id, chatId);
            // 回答正确，移除状态锁
            var userStatusKey = $"user_status:{user.Id}:{chatId}";
            await redisDb.KeyDeleteAsync(userStatusKey);
            return true;
        }

        // 状态不匹配，这是一个严重的警告，可能表示逻辑错误或恶意行为
        logger.LogWarning("Verification state mismatch for user {UserId} in chat {ChatId}. State: {@State}", user.Id, chatId, state);
        // 也可以选择不抛出异常，而是静默失败
        return false;
    }

    private async Task DispatchFinalActionsAsync(bool approve, User user, long chatId, Message message)
    {
        var resultText = approve ? "✅ 验证通过！欢迎加入！" : "❌ 验证失败，入群请求已被拒绝。";

        var joinRequestTask = dispatcher.DispatchAsync(new ChatJoinRequestJob(user, chatId, approve));
        var editMessageTask = dispatcher.DispatchAsync(new EditMessageJob(message, resultText));

        await Task.WhenAll(joinRequestTask, editMessageTask);

        logger.LogInformation("Handled verification result for user {UserId} in chat {ChatId}, approved: {Approve}", user.Id, chatId, approve);
    }

    /// <summary>
    /// Handles the callback query when a user clicks a quiz option.
    /// </summary>
    public async Task HandleCallbackAsync(ProcessQuizCallbackJob job)
    {
        var callbackInfo = ParseCallbackData(job.CallbackData);
        if (callbackInfo is null)
        {
            return;
        }

        if (await HandlePreVerificationStatusAsync(job.User, callbackInfo.ChatId, job.Message))
        {
            return;
        }

        bool isApproved = await VerifyAnswerAndCleanupAsync(job.User, callbackInfo.ChatId, callbackInfo.Token);


        await DispatchFinalActionsAsync(isApproved, job.User, callbackInfo.ChatId, job.Message);
    }
}
