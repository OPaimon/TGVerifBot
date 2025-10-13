using System.Text.Json;
using CSharpFunctionalExtensions;
using StackExchange.Redis;
using Telegram.Bot.Types;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Tasks;

namespace TelegramVerificationBot.Services;

public enum VerificationErrorKind {
  // Start
  UserPendingOrOnCooldown,
  NoQuizzesAvailable,
  StateStorageFailed,

  // Callback
  InvalidCallbackData,
  UserOnCooldown,
  VerificationExpired,
  IncorrectOptionOrExpiredToken,
  StateDeserializationFailed,
  StateValidationFailed
}

public readonly struct VerificationError {
  public VerificationErrorKind Kind { get; }
  public string Message { get; }

  public VerificationError(VerificationErrorKind kind, string message) {
    Kind = kind;
    Message = message;
  }

  public override string ToString() => Message;
}


public class VerificationServiceROP(
    ILogger<VerificationServiceROP> logger,
    ITaskDispatcher dispatcher,
    IDatabase redisDb,
    IQuizService quizService,
    AppJsonSerializerContext jsonContext) {
  // --- Internal records for data transfer ---
  internal record CallbackInfo(long ChatId, string Token);
  internal record VerificationQuizData(List<OptionWithToken> OptionsWithTokens, string CorrectToken, string Question);

  #region Handle Start Verification

  public async Task HandleStartVerificationAsync(StartVerificationJob job) {
    logger.LogInformation("Starting ROP verification for user {UserId} in chat {ChatId}", job.Requester.From.Id, job.Requester.Chat.Id);

    var result = await Task.FromResult(Result.Success<StartVerificationJob, VerificationError>(job))
        .Bind(j => CheckIfNotPendingOrBlacklistedAsync(j))
        .Bind(j => Task.FromResult(PrepareQuizAsync().Map(quizData => (Job: j, Quiz: quizData)))) // Combine job and quiz data
        .Bind(data => StoreStateAsync(data.Job.Requester.From.Id, data.Job.Requester.Chat.Id, data.Quiz.CorrectToken, data)); // Pass data through

    await result.Tap(data => DispatchSendQuizJob(data.Job, data.Quiz))
          .TapError(error => logger.LogWarning("Failed to start verification for user {UserId}: {Error}", job.Requester.From.Id, error));
  }

  private async Task<Result<StartVerificationJob, VerificationError>> CheckIfNotPendingOrBlacklistedAsync(StartVerificationJob job) {
    var userStatusKey = $"user_status:{job.Requester.From.Id}:{job.Requester.Chat.Id}";
    if (await redisDb.KeyExistsAsync(userStatusKey)) {
      var errorMessage = $"User {job.Requester.From.Id} has an existing status (pending or cooldown) in chat {job.Requester.Chat.Id}.";
      return Result.Failure<StartVerificationJob, VerificationError>(new VerificationError(VerificationErrorKind.UserPendingOrOnCooldown, errorMessage));
    }
    return Result.Success<StartVerificationJob, VerificationError>(job);
  }

  private Result<VerificationQuizData, VerificationError> PrepareQuizAsync() =>
      quizService.GetRandomQuiz()
          .ToResult(new VerificationError(VerificationErrorKind.NoQuizzesAvailable, "No quizzes available."))
          .Map(quiz => {
            List<OptionWithToken> optionsWithTokens = [.. quiz.Options.Select(optionText => new OptionWithToken(Option: optionText, Token: Guid.NewGuid().ToString()))];
            var correctToken = optionsWithTokens[quiz.CorrectOptionIndex].Token;

            var random = new Random();
            var shuffledOptions = optionsWithTokens.OrderBy(option => random.Next()).ToList();

            return new VerificationQuizData(shuffledOptions, correctToken, quiz.Question);
          });


  private async Task<Result<T, VerificationError>> StoreStateAsync<T>(long userId, long chatId, string correctToken, T passThroughValue) {
    try {
      var userStatusKey = $"user_status:{userId}:{chatId}";
      var verificationTokenKey = $"verification_token:{correctToken}";

      var stateObj = new VerificationState(UserId: userId, ChatId: chatId);
      var stateJson = JsonSerializer.Serialize(stateObj, jsonContext.VerificationState);

      await Task.WhenAll(
          redisDb.StringSetAsync(userStatusKey, correctToken, TimeSpan.FromMinutes(1)),
          redisDb.StringSetAsync(verificationTokenKey, stateJson, TimeSpan.FromMinutes(1))
      );
      return Result.Success<T, VerificationError>(passThroughValue);
    } catch (Exception ex) {
      var errorMessage = $"Failed to store verification state in Redis: {ex.Message}";
      return Result.Failure<T, VerificationError>(new VerificationError(VerificationErrorKind.StateStorageFailed, errorMessage));
    }
  }

  private Task DispatchSendQuizJob(StartVerificationJob originalJob, VerificationQuizData quizData) {
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

  public async Task HandleSendQuizCallback(SendQuizCallbackJob job) {
    var userStatusKeyBackup = $"b_user_status:{job.UserId}:{job.ChatId}";
    await redisDb.StringSetAsync(userStatusKeyBackup, $"{job.MessageId}_{job.MessageChatId}", TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(10));
  }

  #endregion

  #region Handle Callback

  public async Task HandleCallbackAsync(ProcessQuizCallbackJob job) {
    var result = await Task.FromResult(ParseCallbackData(job.CallbackData))
        .Bind(callbackInfo => CheckPreVerificationStatus(job.User, callbackInfo.ChatId, callbackInfo))
        .Bind(callbackInfo => VerifyAnswerAndCleanup(job.User, callbackInfo.ChatId, callbackInfo.Token));

    await result.Match(
        onSuccess: async _ => {
          var chatId = GetChatIdFromCallback(job.CallbackData);
          await DispatchFinalActions(true, job.User, job.Message, chatId, "Success");
        },
        onFailure: async error => {
          var chatId = GetChatIdFromCallback(job.CallbackData);
          logger.LogWarning("Verification failed for user {UserId} in chat {ChatId}: {Error}", job.User.Id, chatId, error.Message);

          if (chatId == 0 && error.Kind == VerificationErrorKind.InvalidCallbackData) {
            logger.LogError("Could not process callback due to invalid ChatId in callback data: {CallbackData}", job.CallbackData);
            return;
          }

          switch (error.Kind) {
            case VerificationErrorKind.UserOnCooldown:
              await dispatcher.DispatchAsync(new EditMessageJob(job.Message.Chat.Id, job.Message.Id, "❌ 你在冷却时间内，请稍后再试。"));
              break;
            case VerificationErrorKind.VerificationExpired:
              var outdateText = "❌ 验证已过期，入群请求已被拒绝。";
              await Task.WhenAll(
                      dispatcher.DispatchAsync(new ChatJoinRequestJob(job.User.Id, chatId, false)),
                      dispatcher.DispatchAsync(new EditMessageJob(job.Message.Chat.Id, job.Message.Id, outdateText))
                  );
              break;
            case VerificationErrorKind.IncorrectOptionOrExpiredToken:
              var userStatusKey = $"user_status:{job.User.Id}:{chatId}";
              await redisDb.StringSetAsync(userStatusKey, "Cooldown", TimeSpan.FromMinutes(1));
              await DispatchFinalActions(false, job.User, job.Message, chatId, error.Message);
              break;
            default:
              await DispatchFinalActions(false, job.User, job.Message, chatId, error.Message);
              break;
          }
        });
  }

  private Result<CallbackInfo, VerificationError> ParseCallbackData(string callbackData) {
    var parts = callbackData.Split('_');
    if (parts.Length != 2 || !long.TryParse(parts[0], out var chatId)) {
      var errorMessage = $"Invalid callback data format: {callbackData}";
      return Result.Failure<CallbackInfo, VerificationError>(new VerificationError(VerificationErrorKind.InvalidCallbackData, errorMessage));
    }
    return Result.Success<CallbackInfo, VerificationError>(new CallbackInfo(chatId, parts[1]));
  }

  private async Task<Result<CallbackInfo, VerificationError>> CheckPreVerificationStatus(User user, long chatId, CallbackInfo passThroughInfo) {
    var userStatusKey = $"user_status:{user.Id}:{chatId}";
    var status = await redisDb.StringGetAsync(userStatusKey);

    if (status.HasValue && status.ToString().Equals("Cooldown", StringComparison.OrdinalIgnoreCase)) {
      var errorMessage = $"User {user.Id} is in cooldown.";
      return Result.Failure<CallbackInfo, VerificationError>(new VerificationError(VerificationErrorKind.UserOnCooldown, errorMessage));
    }

    if (!status.HasValue) {
      var errorMessage = $"No active verification for user {user.Id}.";
      return Result.Failure<CallbackInfo, VerificationError>(new VerificationError(VerificationErrorKind.VerificationExpired, errorMessage));
    }

    return Result.Success<CallbackInfo, VerificationError>(passThroughInfo);
  }

  private async Task<Result<bool, VerificationError>> VerifyAnswerAndCleanup(User user, long chatId, string clickedToken) {
    var verificationTokenKey = $"verification_token:{clickedToken}";
    var stateJson = await redisDb.StringGetDeleteAsync(verificationTokenKey);

    if (stateJson.IsNullOrEmpty) {
      return Result.Failure<bool, VerificationError>(new VerificationError(VerificationErrorKind.IncorrectOptionOrExpiredToken, "Incorrect option or expired token."));
    }

    var validationResult = DeserializeState(stateJson.ToString())
        .Bind(state => ValidateState(state, user, chatId));

    var tappedResult = await validationResult.Tap(() => ClearUserStatusLockAsync(user, chatId));

    return tappedResult.Map(_ => true);
  }

  private Result<VerificationState, VerificationError> DeserializeState(string stateJson) {
    try {
      var state = JsonSerializer.Deserialize(stateJson, jsonContext.VerificationState);
      if (state != null) {
        return Result.Success<VerificationState, VerificationError>(state);
      }

      var errorMessage = "Deserialized state is null.";
      return Result.Failure<VerificationState, VerificationError>(new VerificationError(VerificationErrorKind.StateDeserializationFailed, errorMessage));
    } catch (JsonException ex) {
      var errorMessage = $"Failed to deserialize state: {ex.Message}";
      return Result.Failure<VerificationState, VerificationError>(new VerificationError(VerificationErrorKind.StateDeserializationFailed, errorMessage));
    }
  }

  private Result<VerificationState, VerificationError> ValidateState(VerificationState state, User user, long chatId) {
    if (state.UserId == user.Id && state.ChatId == chatId) {
      return Result.Success<VerificationState, VerificationError>(state);
    }

    var errorMessage = $"State mismatch. Expected User/Chat: {user.Id}/{chatId}, but got {state.UserId}/{state.ChatId}";
    return Result.Failure<VerificationState, VerificationError>(new VerificationError(VerificationErrorKind.StateValidationFailed, errorMessage));
  }

  private Task ClearUserStatusLockAsync(User user, long chatId) {
    var userStatusKey = $"user_status:{user.Id}:{chatId}";
    return redisDb.KeyDeleteAsync(userStatusKey);
  }

  private Task DispatchFinalActions(bool isApproved, User user, Message message, long chatId, string reason) {
    var resultText = isApproved ? "✅ 验证通过！欢迎加入！" : "❌ 验证失败，入群请求已被拒绝。";

    logger.LogInformation("Verification result for user {UserId} in chat {ChatId}. Approved: {IsApproved}. Reason: {Reason}",
        user.Id, chatId, isApproved, reason);

    return Task.WhenAll(
        dispatcher.DispatchAsync(new ChatJoinRequestJob(user.Id, chatId, isApproved)),
        dispatcher.DispatchAsync(new EditMessageJob(message.Chat.Id, message.Id, resultText))
    );
  }

  private long GetChatIdFromCallback(string callbackData) {
    // Helper to extract ChatId for logging even on failure
    return ParseCallbackData(callbackData).Match(
        onSuccess: info => info.ChatId,
        onFailure: _ => 0
    );
  }

  #endregion
}
