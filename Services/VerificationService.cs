using StackExchange.Redis;
using Telegram.Bot.Types;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Tasks;

namespace TelegramVerificationBot;

/// <summary>
/// Handles the core logic of user verification, including starting the process and handling quiz callbacks.
/// </summary>
public class VerificationService(ILogger<VerificationService> logger, FunctionalTaskDispatcher dispatcher, IDatabase redisDb, QuizService quizService, AppJsonSerializerContext jsonContext)
{

    /// <summary>
    /// Initiates a new verification process for a user joining a chat.
    /// </summary>
    public async Task HandleStartVerificationAsync(StartVerificationJob job)
    {
        logger.LogInformation("Received verification request for user {UserId} in chat {ChatId}", job.Requester.From.Id, job.Requester.Chat.Id);
        var user = job.Requester.From;
        var chat = job.Requester.Chat;
        var userChat = job.Requester.UserChatId;
        var link = job.Requester.InviteLink;

        var userStatusKey = $"user_status:{user.Id}:{chat.Id}";

        // Check if the user already has a pending status or is blacklisted.
        if (await redisDb.KeyExistsAsync(userStatusKey))
        {
            logger.LogWarning("User {UserId} has an existing status (possibly blacklisted or pending) in chat {ChatId}, rejecting join request.", user.Id, chat.Id);
            return; // Direct reject to avoid duplicate workflows.
        }

        var quiz = await quizService.GetQuizRandomAsync();

        // Create a unique token for each quiz option.
        List<OptionWithToken> optionsWithTokens = quiz.Options
            .Select(optionText => new OptionWithToken(Option: optionText, Token: Guid.NewGuid().ToString()))
            .ToList();

        var correctToken = optionsWithTokens[quiz.CorrectOptionIndex].Token;
        var verificationTokenKey = $"verification_token:{correctToken}";

        var stateObj = new VerificationState(
            UserId: user.Id,
            ChatId: chat.Id
        );
        var stateJson = System.Text.Json.JsonSerializer.Serialize(stateObj, jsonContext.VerificationState);

        // Store a temporary lock for the user to prevent concurrent verifications. The value is the correct token.
        await redisDb.StringSetAsync(userStatusKey, correctToken, TimeSpan.FromMinutes(5));
        // Store the verification state, keyed by the correct token.
        await redisDb.StringSetAsync(verificationTokenKey, stateJson, TimeSpan.FromMinutes(5));

        logger.LogInformation("Dispatching SendQuizJob for user {UserId}", user.Id);
        // Pass the options with tokens to the next job.
        await dispatcher.DispatchAsync(new SendQuizJob(userChat, user, chat, quiz.Question, optionsWithTokens, link));
    }

    /// <summary>
    /// Handles the callback query when a user clicks a quiz option.
    /// </summary>
    public async Task HandleCallbackAsync(ProcessQuizCallbackJob job)
    {
        var user = job.User;
        var callbackData = job.CallbackData; // Expected format: <chatId>_<token>
        ReadOnlySpan<char> callbackSpan = callbackData;
        int separatorIndex = callbackSpan.IndexOf('_');
        if (separatorIndex == -1)
        {
            logger.LogError("Invalid callback data format: {CallbackData}", callbackData);
            // TODO: Optionally notify the user about the invalid format.
            return;
        }
        
        var chatIdSpan = callbackSpan.Slice(0, separatorIndex);
        var tokenSpan = callbackSpan.Slice(separatorIndex + 1); // The rest is the token

        if (!long.TryParse(chatIdSpan, out var chatId))
        {
            logger.LogError("Invalid chat ID in callback data: {ChatIdPart}", chatIdSpan.ToString());
            return;
        }

        var clickedToken = tokenSpan.ToString();

        var userStatusKey = $"user_status:{user.Id}:{chatId}";

        // First, check if the user status lock is 'Cooldown'.
        var status = await redisDb.StringGetAsync(userStatusKey);
        if (status.HasValue && status.ToString().Equals("Cooldown", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("User {UserId} is in cooldown period in chat {ChatId}, ignoring callback.", user.Id, chatId);
            var cooldownText = "❌ 你在冷却时间内，请稍后再试。";
            await dispatcher.DispatchAsync(new EditMessageJob(job.Message, cooldownText));
            return;
        }

        if (!status.HasValue)
        {
            logger.LogWarning("No active verification found for user {UserId} in chat {ChatId}, ignoring callback.", user.Id, chatId);
            var outdateText = "❌ 验证以过期，入群请求已被拒绝。";
            var joinRequestTask_ = dispatcher.DispatchAsync(new ChatJoinRequestJob(user, chatId, false));
            var editMessageTask_ = dispatcher.DispatchAsync(new EditMessageJob(job.Message, outdateText));
            await Task.WhenAll(joinRequestTask_, editMessageTask_);
            return;
        }

        var verificationTokenKey = $"verification_token:{clickedToken}";
        var stateJson = await redisDb.StringGetDeleteAsync(verificationTokenKey);

        bool approve;
        if (stateJson.IsNullOrEmpty)
        {
            logger.LogWarning("Verification failed for user {UserId}: incorrect option or expired token ({Token})", user.Id, clickedToken);
            approve = false;

            // On failure, set a cooldown to slow down brute-force attempts.
            await redisDb.StringSetAsync(userStatusKey, "Cooldown", TimeSpan.FromMinutes(1));
            logger.LogInformation("Set 1-minute cooldown for user {UserId} on chat {ChatId}", user.Id, chatId);
        }
        else
        {
            var state = System.Text.Json.JsonSerializer.Deserialize(stateJson.ToString(), jsonContext.VerificationState);

            if (state != null && state.UserId == user.Id && state.ChatId == chatId)
            {
                logger.LogInformation("User {UserId} passed verification for chat {ChatId}", user.Id, chatId);
                approve = true;

                // On success, remove the user status lock to release the lock.
                await redisDb.KeyDeleteAsync(userStatusKey);
            }
            else
            {
                // Handle edge case where the state is valid but doesn't match the user/chat.
                logger.LogWarning("Verification state mismatch for user {UserId} in chat {ChatId}. State: {@State}", user.Id, chatId, state);
                approve = false;
                throw new InvalidOperationException("Verification state mismatch.");
            }
        }

        var joinRequestTask = dispatcher.DispatchAsync(new ChatJoinRequestJob(user, chatId, approve));
        var editMessageTask = dispatcher.DispatchAsync(new EditMessageJob(job.Message, approve ? "✅ 验证通过！欢迎加入！" : "❌ 验证失败，入群请求已被拒绝。"));

        await Task.WhenAll(joinRequestTask, editMessageTask);
        logger.LogInformation("Handled verification result for user {UserId} in chat {ChatId}, approved: {Approve}", user.Id, chatId, approve);
    }
}
