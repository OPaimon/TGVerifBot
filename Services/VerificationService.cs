using StackExchange.Redis;
using Telegram.Bot.Types;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Tasks;

namespace TelegramVerificationBot;

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

    public async Task HandleStartVerificationAsync(StartVerificationJob job)
    {
        _logger.LogInformation("Received verification request: {Requester}", job.Requester);
        var user = job.Requester.From;
        var chat = job.Requester.Chat;
        var userChat = job.Requester.UserChatId;
        var link = job.Requester.InviteLink;

        var userStatusKey = $"user_status:{user.Id}:{chat.Id}";
        // Rate-limit repeated start-verification attempts per user per chat.
        // if (!await _rateLimiter.AllowStartVerificationAsync(user.Id, chat.Id))
        // {
        //     _logger.LogWarning("Rate limit: too many start-verification attempts for user {UserId} in chat {ChatId}", user.Id, chat.Id);
        //     return;
        // }

        if (await _redisDb.KeyExistsAsync(userStatusKey))
        {
            _logger.LogWarning("User {userId} has an existing status (possibly blacklisted or pending) in chat {chatId}, rejecting join request.", user.Id, chat.Id);
            return; // direct reject to avoid duplicate workflows
        }

        _logger.LogInformation("Received verification request: {Requester}", job.Requester);

        // var redisKey = $"verification:{user.Id}:{chat.Id}";
        // if (await _redisDb.KeyExistsAsync(redisKey))
        // {
        //     _logger.LogWarning("用户 {userId} 已在验证 {chatId}中，忽略新的入群申请。", user.Id, chat.Id);
        //     return;
        // }

        var quiz = await _quizService.GetQuizRandomAsync();

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

        // Store the temporary status/key. Use consistent casing for cooldown markers.
        await _redisDb.StringSetAsync(userStatusKey, correctToken, TimeSpan.FromMinutes(5));
        await _redisDb.StringSetAsync(verificationTokenKey, stateJson, TimeSpan.FromMinutes(5));

        _logger.LogInformation("Dispatching SendQuizJob for user {UserId}", user.Id);
        await _dispatcher.DispatchAsync(new SendQuizJob(userChat, user, chat, quiz.Question, optionsWithTokens, link)); // 传递带令牌的选项

        // bool added = await _redisDb.StringSetAsync(redisKey, stateJson, TimeSpan.FromMinutes(5));
        // if (added)
        // {
        //     _logger.LogInformation("Dispatching SendQuizJob to chat {ChatId} with quiz {QuizId}", chat.Id, quiz.Id);
        //     await _dispatcher.DispatchAsync(new SendQuizJob(userChat, user, chat, quiz, link));
        //     _logger.LogInformation("Stored verification state in Redis under {Key}", redisKey);
        // }
        // else
        // {
        //     _logger.LogWarning("Failed to store verification state in Redis under {Key}", redisKey);
        // }
    }

    public async Task HandleCallbackAsync(ProcessQuizCallbackJob job)
    {
        var user = job.User;
        var clickedToken = job.CallbackData; // <chatId>_<token>
        var chatIdStr = clickedToken.Split('_')[0];
        clickedToken = clickedToken.Substring(chatIdStr.Length + 1);
        var chatId = long.Parse(chatIdStr);
        var verificationTokenKey = $"verification_token:{clickedToken}";
        var stateJson = await _redisDb.StringGetDeleteAsync(verificationTokenKey);

        // 首先检查状态锁值是否为Cooldown
        var userStatusKey = $"user_status:{user.Id}:{chatId}";
        var status = await _redisDb.StringGetAsync(userStatusKey);
        if (status.HasValue && status.ToString().Equals("Cooldown", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("User {UserId} is in cooldown period in chat {ChatId}, ignoring callback.", user.Id, chatId);
            return;
        }


        bool approve;
        if (stateJson.IsNullOrEmpty)
        {
            _logger.LogWarning("No verification state found in Redis for token {Token}", clickedToken);
            approve = false;

            // Use consistent 'Cooldown' marker and shorter TTL to slow down brute-force attempts
            await _redisDb.StringSetAsync(userStatusKey, "Cooldown", TimeSpan.FromMinutes(1));
            _logger.LogInformation("Set 1-minute cooldown for user {UserId} on chat {ChatId}", user.Id, chatId);
        }
        else
        {
            var state = System.Text.Json.JsonSerializer.Deserialize<VerificationState>(stateJson.ToString());

            if (state != null && state.UserId == user.Id && state.ChatId == chatId)
            {
                _logger.LogInformation("User {UserId} passed verification", user.Id);
                approve = true;

                // 3. 验证成功：删除“用户状态锁”，解除锁定
                // On success: remove the pending token/status to avoid reuse
                await _redisDb.KeyDeleteAsync(userStatusKey);
            }
            else
            {
                // 用户ID或chatID不匹配的极端情况处理...
                _logger.LogWarning("Verification state user ID mismatch or null for user {UserId}", user.Id);
                approve = false;
            }
        }

        await _dispatcher.DispatchAsync(new ChatJoinRequestJob(user, chatId, approve));
        _logger.LogInformation("Handled verification result for user {UserId} in chat {ChatId}, approved: {Approve}", job.User, chatId, approve);

        var newText = approve ? "✅ 验证通过！欢迎加入！" : "❌ 验证失败，入群请求已被拒绝。";
        await _dispatcher.DispatchAsync(new EditMessageJob(job.Message, newText));
        _logger.LogInformation("Edited verification message for user {UserId} in chat {ChatId}", job.User, chatId);
    }
}