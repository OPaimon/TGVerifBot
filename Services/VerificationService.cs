using StackExchange.Redis;
using Telegram.Bot.Types;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Tasks;

namespace TelegramVerificationBot;

/// <summary>
/// Handles the core logic of user verification, including starting the process and handling quiz callbacks.
/// </summary>
public class VerificationService
{
    private readonly ILogger<VerificationService> _logger;
    private readonly FunctionalTaskDispatcher _dispatcher;
    private readonly IDatabase _redisDb;
    private readonly QuizService _quizService;

    public VerificationService(ILogger<VerificationService> logger, FunctionalTaskDispatcher dispatcher, IDatabase redisDatabase, QuizService quizService)
    {
        _logger = logger;
        _dispatcher = dispatcher;
        _redisDb = redisDatabase;
        _quizService = quizService;
    }

    /// <summary>
    /// Initiates a new verification process for a user joining a chat.
    /// </summary>
    public async Task HandleStartVerificationAsync(StartVerificationJob job)
    {
        _logger.LogInformation("Received verification request for user {UserId} in chat {ChatId}", job.Requester.From.Id, job.Requester.Chat.Id);
        var user = job.Requester.From;
        var chat = job.Requester.Chat;
        var userChat = job.Requester.UserChatId;
        var link = job.Requester.InviteLink;

        var userStatusKey = $"user_status:{user.Id}:{chat.Id}";

        // Check if the user already has a pending status or is blacklisted.
        if (await _redisDb.KeyExistsAsync(userStatusKey))
        {
            _logger.LogWarning("User {UserId} has an existing status (possibly blacklisted or pending) in chat {ChatId}, rejecting join request.", user.Id, chat.Id);
            return; // Direct reject to avoid duplicate workflows.
        }

        var quiz = await _quizService.GetQuizRandomAsync();

        // Create a unique token for each quiz option.
        List<OptionWithToken> optionsWithTokens = quiz.Options
            .Select(optionText => new OptionWithToken(Option: optionText, Token: Guid.NewGuid().ToString()))
            .ToList();

        var correctToken = optionsWithTokens[quiz.CorrectOptionIndex].Token;
        var verificationTokenKey = $"verification_token:{correctToken}";

        var stateObj = new VerificationState(
            userId: user.Id,
            chatId: chat.Id
        );
        var stateJson = System.Text.Json.JsonSerializer.Serialize(stateObj);

        // Store a temporary lock for the user to prevent concurrent verifications. The value is the correct token.
        await _redisDb.StringSetAsync(userStatusKey, correctToken, TimeSpan.FromMinutes(5));
        // Store the verification state, keyed by the correct token.
        await _redisDb.StringSetAsync(verificationTokenKey, stateJson, TimeSpan.FromMinutes(5));

        _logger.LogInformation("Dispatching SendQuizJob for user {UserId}", user.Id);
        // Pass the options with tokens to the next job.
        await _dispatcher.DispatchAsync(new SendQuizJob(userChat, user, chat, quiz.Question, optionsWithTokens, link));
    }

    /// <summary>
    /// Handles the callback query when a user clicks a quiz option.
    /// </summary>
    public async Task HandleCallbackAsync(ProcessQuizCallbackJob job)
    {
        var user = job.User;
        var callbackData = job.CallbackData; // Expected format: <chatId>_<token>
        var parts = callbackData.Split('_', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var chatId))
        {
            _logger.LogError("Invalid callback data format: {CallbackData}", callbackData);
            return;
        }
        var clickedToken = parts[1];

        var userStatusKey = $"user_status:{user.Id}:{chatId}";

        // First, check if the user status lock is 'Cooldown'.
        var status = await _redisDb.StringGetAsync(userStatusKey);
        if (status.HasValue && status.ToString().Equals("Cooldown", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("User {UserId} is in cooldown period in chat {ChatId}, ignoring callback.", user.Id, chatId);
            var cooldownText = "❌ 你在冷却时间内，请稍后再试。";
            await _dispatcher.DispatchAsync(new EditMessageJob(job.Message, cooldownText));
            return;
        }

        if (!status.HasValue)
        {
            _logger.LogWarning("No active verification found for user {UserId} in chat {ChatId}, ignoring callback.", user.Id, chatId);
            var outdateText = "❌ 验证以过期，入群请求已被拒绝。";
            await _dispatcher.DispatchAsync(new EditMessageJob(job.Message, outdateText));
            return;
        }

        var verificationTokenKey = $"verification_token:{clickedToken}";
        var stateJson = await _redisDb.StringGetDeleteAsync(verificationTokenKey);

        bool approve;
        if (stateJson.IsNullOrEmpty)
        {
            _logger.LogWarning("Verification failed for user {UserId}: incorrect option or expired token ({Token})", user.Id, clickedToken);
            approve = false;

            // On failure, set a cooldown to slow down brute-force attempts.
            await _redisDb.StringSetAsync(userStatusKey, "Cooldown", TimeSpan.FromMinutes(1));
            _logger.LogInformation("Set 1-minute cooldown for user {UserId} on chat {ChatId}", user.Id, chatId);
        }
        else
        {
            var state = System.Text.Json.JsonSerializer.Deserialize<VerificationState>(stateJson.ToString());

            if (state != null && state.UserId == user.Id && state.ChatId == chatId)
            {
                _logger.LogInformation("User {UserId} passed verification for chat {ChatId}", user.Id, chatId);
                approve = true;

                // On success, remove the user status lock to release the lock.
                await _redisDb.KeyDeleteAsync(userStatusKey);
            }
            else
            {
                // Handle edge case where the state is valid but doesn't match the user/chat.
                _logger.LogWarning("Verification state mismatch for user {UserId} in chat {ChatId}. State: {@State}", user.Id, chatId, state);
                approve = false;
                throw new InvalidOperationException("Verification state mismatch.");
            }
        }

        await _dispatcher.DispatchAsync(new ChatJoinRequestJob(user, chatId, approve));
        _logger.LogInformation("Handled verification result for user {UserId} in chat {ChatId}, approved: {Approve}", user.Id, chatId, approve);

        var newText = approve ? "✅ 验证通过！欢迎加入！" : "❌ 验证失败，入群请求已被拒绝。";
        await _dispatcher.DispatchAsync(new EditMessageJob(job.Message, newText));
        _logger.LogInformation("Edited verification message for user {UserId} in chat {ChatId}", user.Id, chatId);
    }
}
