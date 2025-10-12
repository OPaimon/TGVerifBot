using CSharpFunctionalExtensions;
using StackExchange.Redis;
using System.Text.Json;
using Telegram.Bot.Types;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Tasks;

namespace TelegramVerificationBot.Services;

public class VerificationServiceROP(
    ILogger<VerificationServiceROP> logger,
    FunctionalTaskDispatcher dispatcher,
    IDatabase redisDb,
    QuizService quizService,
    AppJsonSerializerContext jsonContext)
{
    // --- Internal records for data transfer ---
    internal record CallbackInfo(long ChatId, string Token);
    internal record VerificationQuizData(List<OptionWithToken> OptionsWithTokens, string CorrectToken, string Question);

    #region Handle Start Verification

    public async Task HandleStartVerificationAsync(StartVerificationJob job)
    {
        logger.LogInformation("Starting ROP verification for user {UserId} in chat {ChatId}", job.Requester.From.Id, job.Requester.Chat.Id);

        var result = await Task.FromResult(Result.Success(job))
            .Bind(j => CheckIfNotPendingOrBlacklistedAsync(j))
            .Bind(j => Task.FromResult(PrepareQuizAsync().Map(quizData => (Job: j, Quiz: quizData)))) // Combine job and quiz data
            .Bind(data => StoreStateAsync(data.Job.Requester.From.Id, data.Job.Requester.Chat.Id, data.Quiz.CorrectToken, data)); // Pass data through

        await result.Tap(data => DispatchSendQuizJob(data.Job, data.Quiz))
              .TapError(error => logger.LogWarning("Failed to start verification for user {UserId}: {Error}", job.Requester.From.Id, error));
    }

    private async Task<Result<StartVerificationJob>> CheckIfNotPendingOrBlacklistedAsync(StartVerificationJob job)
    {
        var userStatusKey = $"user_status:{job.Requester.From.Id}:{job.Requester.Chat.Id}";
        if (await redisDb.KeyExistsAsync(userStatusKey))
        {
            return Result.Failure<StartVerificationJob>($"User {job.Requester.From.Id} has an existing status (pending or cooldown) in chat {job.Requester.Chat.Id}.");
        }
        return Result.Success(job);
    }

    private Result<VerificationQuizData> PrepareQuizAsync() =>
        quizService.GetRandomQuiz().ToResult("No quizzes available.")
            .Map(quiz =>
            {
                List<OptionWithToken> optionsWithTokens = [.. quiz.Options.Select(optionText => new OptionWithToken(Option: optionText, Token: Guid.NewGuid().ToString()))];
                var correctToken = optionsWithTokens[quiz.CorrectOptionIndex].Token;

                var random = new Random();
                var shuffledOptions = optionsWithTokens.OrderBy(option => random.Next()).ToList();

                return new VerificationQuizData(shuffledOptions, correctToken, quiz.Question);
            });
    

    private async Task<Result<T>> StoreStateAsync<T>(long userId, long chatId, string correctToken, T passThroughValue)
    {
        try
        {
            var userStatusKey = $"user_status:{userId}:{chatId}";
            var verificationTokenKey = $"verification_token:{correctToken}";

            var stateObj = new VerificationState(UserId: userId, ChatId: chatId);
            var stateJson = JsonSerializer.Serialize(stateObj, jsonContext.VerificationState);

            await Task.WhenAll(
                redisDb.StringSetAsync(userStatusKey, correctToken, TimeSpan.FromMinutes(5)),
                redisDb.StringSetAsync(verificationTokenKey, stateJson, TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(10)))
            );
            return Result.Success<T>(passThroughValue);
        }
        catch (Exception ex)
        {
            return Result.Failure<T>($"Failed to store verification state in Redis: {ex.Message}");
        }
    }

    private Task DispatchSendQuizJob(StartVerificationJob originalJob, VerificationQuizData quizData)
    {
        logger.LogInformation("Dispatching SendQuizJob for user {UserId}", originalJob.Requester.From.Id);
        return dispatcher.DispatchAsync(new SendQuizJob(
            originalJob.Requester.UserChatId,
            originalJob.Requester.From,
            originalJob.Requester.Chat,
            quizData.Question,
            quizData.OptionsWithTokens,
            originalJob.Requester.InviteLink
        ));
    }

    #endregion

    #region Handle Callback

    public async Task HandleCallbackAsync(ProcessQuizCallbackJob job)
    {
        var finalResult = await Task.FromResult(ParseCallbackData(job.CallbackData))
            .Bind(callbackInfo => CheckPreVerificationStatus(job.User, callbackInfo.ChatId, job.Message, callbackInfo))
            .Bind(callbackInfo => VerifyAnswerAndCleanup(job.User, callbackInfo.ChatId, callbackInfo.Token));

        await finalResult.Finally(result => DispatchFinalActions(result, job.User, job.Message, GetChatIdFromCallback(job.CallbackData)));
    }

    private Result<CallbackInfo, string> ParseCallbackData(string callbackData)
    {
        var parts = callbackData.Split('_');
        if (parts.Length != 2 || !long.TryParse(parts[0], out var chatId))
        {
            return Result.Failure<CallbackInfo, string>($"Invalid callback data format: {callbackData}");
        }
        return Result.Success<CallbackInfo, string>(new CallbackInfo(chatId, parts[1]));
    }

    private async Task<Result<CallbackInfo, string>> CheckPreVerificationStatus(User user, long chatId, Message message, CallbackInfo passThroughInfo)
    {
        var userStatusKey = $"user_status:{user.Id}:{chatId}";
        var status = await redisDb.StringGetAsync(userStatusKey);

        if (status.HasValue && status.ToString().Equals("Cooldown", StringComparison.OrdinalIgnoreCase))
        {
            await dispatcher.DispatchAsync(new EditMessageJob(message, "❌ 你在冷却时间内，请稍后再试。"));
            return Result.Failure<CallbackInfo, string>($"User {user.Id} is in cooldown.");
        }

        if (!status.HasValue)
        {
            var outdateText = "❌ 验证已过期，入群请求已被拒绝。";
            await Task.WhenAll(
                dispatcher.DispatchAsync(new ChatJoinRequestJob(user.Id, chatId, false)),
                dispatcher.DispatchAsync(new EditMessageJob(message, outdateText))
            );
            return Result.Failure<CallbackInfo, string>($"No active verification for user {user.Id}.");
        }

        return Result.Success<CallbackInfo, string>(passThroughInfo);
    }

    private async Task<Result<bool, string>> VerifyAnswerAndCleanup(User user, long chatId, string clickedToken)
    {
        var verificationTokenKey = $"verification_token:{clickedToken}";
        var stateJson = await redisDb.StringGetDeleteAsync(verificationTokenKey);

        if (stateJson.IsNullOrEmpty)
        {
            var userStatusKey = $"user_status:{user.Id}:{chatId}";
            await redisDb.StringSetAsync(userStatusKey, "Cooldown", TimeSpan.FromMinutes(1));
            return Result.Failure<bool, string>("Incorrect option or expired token.");
        }

        var validationResult = DeserializeState(stateJson.ToString())
            .Bind(state => ValidateState(state, user, chatId));

        var tappedResult = await validationResult.Tap(() => ClearUserStatusLockAsync(user, chatId));

        return tappedResult.Map(_ => true);
    }

    private Result<VerificationState, string> DeserializeState(string stateJson)
    {
        try
        {
            var state = JsonSerializer.Deserialize(stateJson, jsonContext.VerificationState);
            return state != null
                ? Result.Success<VerificationState, string>(state)
                : Result.Failure<VerificationState, string>("Deserialized state is null.");
        }
        catch (JsonException ex)
        {
            return Result.Failure<VerificationState, string>($"Failed to deserialize state: {ex.Message}");
        }
    }

    private Result<VerificationState, string> ValidateState(VerificationState state, User user, long chatId)
    {
        return (state.UserId == user.Id && state.ChatId == chatId)
            ? Result.Success<VerificationState, string>(state)
            : Result.Failure<VerificationState, string>($"State mismatch. Expected User/Chat: {user.Id}/{chatId}, but got {state.UserId}/{state.ChatId}");
    }
    
    private Task ClearUserStatusLockAsync(User user, long chatId)
    {
        var userStatusKey = $"user_status:{user.Id}:{chatId}";
        return redisDb.KeyDeleteAsync(userStatusKey);
    }

    private Task DispatchFinalActions(Result<bool, string> result, User user, Message message, long chatId)
    {
        bool isApproved = result.IsSuccess;
        var resultText = isApproved ? "✅ 验证通过！欢迎加入！" : "❌ 验证失败，入群请求已被拒绝。";
        
        logger.LogInformation("Verification result for user {UserId} in chat {ChatId}. Approved: {IsApproved}. Reason: {Reason}", 
            user.Id, chatId, isApproved, result.IsFailure ? result.Error : "Success");

        return Task.WhenAll(
            dispatcher.DispatchAsync(new ChatJoinRequestJob(user.Id, chatId, isApproved)),
            dispatcher.DispatchAsync(new EditMessageJob(message, resultText))
        );
    }
    
    private long GetChatIdFromCallback(string callbackData)
    {
        // Helper to extract ChatId for logging even on failure
        return ParseCallbackData(callbackData).Match(
            onSuccess: info => info.ChatId,
            onFailure: _ => 0
        );
    }

    #endregion
}