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
  OnWhiteList,

  // Callback
  InvalidCallbackData,
  UserOnCooldown,
  UserNotMatch,
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

  public int VerificationTimeoutMinutes { get; set; } = 3;
  public int MessageDeletionDelaySeconds { get; set; } = 10;
  public static int CooldownMinutes { get; set; } = 1;

  #region Handle Start Verification

  public async Task HandleStartVerificationAsync(StartVerificationJob job) {
    logger.LogInformation("Starting ROP verification for user {UserId} in chat {ChatId}", job.UserId, job.ChatId);

    var result = await CheckUserStatusAsync(job)
      .TapIf(j => j.UserChatId == j.ChatId, j => dispatcher.DispatchAsync(new RestrictUserJob(
        ChatId: j.ChatId,
        UserId: j.UserId,
        Permissions: TelegramService.FullRestrictPermissions(),
        UntilDate: null
      )))
      .Bind(j => Task.FromResult(PrepareQuiz().Map(quizData => (Job: j, Quiz: quizData))))
      .Bind(data => StoreStateAsync(data.Job.UserId, data.Job.ChatId, data.Quiz.CorrectToken, data));

    await result.Match(
        onSuccess: data => DispatchSendQuizJob(data.Job, data.Quiz),
        onFailure: error => HandleStartVerificationFailure(job, error)
    );
  }

  internal async Task<Result<StartVerificationJob, VerificationError>> CheckUserStatusAsync(StartVerificationJob job) {
    var userStatusKey = $"user_status:{job.UserId}:{job.ChatId}";
    var status = await redisDb.StringGetAsync(userStatusKey);

    if (status.HasValue) {
      var errorMessage = $"User {job.UserId} has an existing status (pending or cooldown) in chat {job.ChatId}.";
      if (status.ToString().Equals("WhiteList", StringComparison.OrdinalIgnoreCase)) {
        errorMessage = $"User {job.UserId} is on the whitelist in chat {job.ChatId}.";
        return Result.Failure<StartVerificationJob, VerificationError>(new VerificationError(VerificationErrorKind.OnWhiteList, errorMessage));
      }
      return Result.Failure<StartVerificationJob, VerificationError>(new VerificationError(VerificationErrorKind.UserPendingOrOnCooldown, errorMessage));
    }

    return Result.Success<StartVerificationJob, VerificationError>(job);
  }

  internal Result<VerificationQuizData, VerificationError> PrepareQuiz() =>
      quizService.GetRandomQuiz()
          .ToResult(new VerificationError(VerificationErrorKind.NoQuizzesAvailable, "No quizzes available."))
          .Map(quiz => {
            List<OptionWithToken> optionsWithTokens = [.. quiz.Options.Select(optionText => new OptionWithToken(Option: optionText, Token: Guid.NewGuid().ToString()))];
            var correctToken = optionsWithTokens[quiz.CorrectOptionIndex].Token;

            var random = new Random();
            var shuffledOptions = optionsWithTokens.OrderBy(option => random.Next()).ToList();

            return new VerificationQuizData(shuffledOptions, correctToken, quiz.Question);
          });


  internal async Task<Result<T, VerificationError>> StoreStateAsync<T>(long userId, long chatId, string correctToken, T passThroughValue) {
    try {
      var userStatusKey = $"user_status:{userId}:{chatId}";
      var userStatusKeyE = $"user_statusE:{userId}:{chatId}";
      var userStatusKeyD = $"user_statusD:{userId}:{chatId}";
      var verificationTokenKey = $"verification_token:{correctToken}";

      var stateObj = new VerificationState(UserId: userId, ChatId: chatId);
      var stateJson = JsonSerializer.Serialize(stateObj, jsonContext.VerificationState);

      await Task.WhenAll(
          redisDb.StringSetAsync(userStatusKey, correctToken, TimeSpan.FromMinutes(VerificationTimeoutMinutes + CooldownMinutes)),
          redisDb.StringSetAsync(verificationTokenKey, stateJson, TimeSpan.FromMinutes(VerificationTimeoutMinutes)),
          redisDb.StringSetAsync(userStatusKeyE, "E", TimeSpan.FromMinutes(VerificationTimeoutMinutes)),
          redisDb.StringSetAsync(userStatusKeyD, "D", TimeSpan.FromMinutes(VerificationTimeoutMinutes) + TimeSpan.FromSeconds(MessageDeletionDelaySeconds + 20))
      );
      return Result.Success<T, VerificationError>(passThroughValue);
    } catch (Exception ex) {
      var errorMessage = $"Failed to store verification state in Redis: {ex.Message}";
      return Result.Failure<T, VerificationError>(new VerificationError(VerificationErrorKind.StateStorageFailed, errorMessage));
    }
  }

  internal Task DispatchSendQuizJob(StartVerificationJob originalJob, VerificationQuizData quizData) {
    logger.LogInformation("Dispatching SendQuizJob for user {UserId}", originalJob.UserId);
    return dispatcher.DispatchAsync(new SendQuizJob(
        originalJob.ChatId,
        originalJob.ChatTitle,
        quizData.Question,
        originalJob.UserId,
        originalJob.UserFirstName,
        originalJob.UserChatId,
        quizData.OptionsWithTokens
    // originalJob.InviteLink
    ));
  }

  internal Task HandleStartVerificationFailure(StartVerificationJob job, VerificationError error) {
    logger.LogWarning("Failed to start verification for user {UserId}: {Error}", job.UserId, error);

    var shouldUnbanLater = job.UserChatId == job.ChatId;
    string messageText = "";

    switch (error.Kind) {
      case VerificationErrorKind.UserPendingOrOnCooldown:
        messageText = "❌ 你已经在等待验证或处于冷却时间内，请稍后再试。";
        break;
      case VerificationErrorKind.NoQuizzesAvailable:
      case VerificationErrorKind.StateStorageFailed:
        messageText = "❌ 验证服务当前不可用，请稍后再试。";
        break;
      default:
        logger.LogError("Unhandled verification error for user {UserId}: {Error}", job.UserId, error);
        return Task.CompletedTask;
    }

    dispatcher.DispatchAsync(new SendQuizJob(
        job.ChatId, job.ChatTitle, messageText, job.UserId,
        job.UserFirstName, job.UserChatId, new List<OptionWithToken>()
    ));

    if (shouldUnbanLater) {
      Task.Delay(TimeSpan.FromSeconds(5))
          .ContinueWith(_ => dispatcher.DispatchAsync(new UnBanUserJob(job.ChatId, job.UserId, true)));
    }

    return Task.CompletedTask;
  }

  public async Task HandleSendQuizCallback(SendQuizCallbackJob job) {
    var userStatusKeyBackup = $"b_user_status:{job.UserId}:{job.ChatId}";
    var json = new BVerificationState(job.MessageId, job.MessageChatId, job.MessageChatId == job.ChatId);
    await redisDb.StringSetAsync(userStatusKeyBackup, JsonSerializer.Serialize(json, jsonContext.BVerificationState), TimeSpan.FromMinutes(VerificationTimeoutMinutes) + TimeSpan.FromSeconds(MessageDeletionDelaySeconds + 20));
  }

  #endregion

  #region Handle Callback

  public async Task HandleCallbackAsync(ProcessQuizCallbackJob job) {

    var result = await Task.FromResult(ParseCallbackData(job.CallbackData))
        .Bind(callbackInfo => CheckPreVerificationStatus(job.User, callbackInfo.ChatId, callbackInfo))
        .Bind(callbackInfo => VerifyAnswerAndCleanup(job.User, callbackInfo.ChatId, callbackInfo.Token));

    var chatId = GetChatIdFromCallback(job.CallbackData);
    if (chatId == 0 && result.IsFailure) {
      logger.LogError("Could not process callback due to invalid ChatId in callback data: {CallbackData}", job.CallbackData);
      return;
    }

    var isInChat = job.Message.Chat.Id == GetChatIdFromCallback(job.CallbackData);

    var actionsPlan = result.Match(
        onSuccess: _ => {
          logger.LogInformation("Verification approved for user {UserId} in chat {ChatId}. Creating success plan.", job.User.Id, chatId);
          return CreateSuccessPlan(job.User, job.Message, chatId, isInChat);
        },
        onFailure: error => {
          logger.LogWarning("Verification failed for user {UserId} in chat {ChatId}: {Error}. Creating failure plan.", job.User.Id, chatId, error.Message);
          return CreateFailurePlan(error, job, chatId, isInChat);
        }
    );

    foreach (var action in actionsPlan) {
      await action();
    }
  }

  internal IEnumerable<Func<Task>> CreateSuccessPlan(User user, Message message, long chatId, bool isInChat) {
    const string resultText = "✅ 验证通过！欢迎加入！";


    yield return () => dispatcher.DispatchAsync(new EditMessageJob(message.Chat.Id, message.Id, resultText));

    if (isInChat) {
      yield return () => dispatcher.DispatchAsync(new RestrictUserJob(
            ChatId: chatId,
            UserId: user.Id,
            Permissions: TelegramService.FullRestrictPermissions(),
            UntilDate: DateTime.UtcNow + TimeSpan.FromMinutes(1)
          ));
    } else {
      yield return () => dispatcher.DispatchAsync(new ChatJoinRequestJob(user.Id, chatId, true));
    }

    yield return () => redisDb.KeyDeleteAsync($"user_statusE:{user.Id}:{chatId}");
  }

  internal IEnumerable<Func<Task>> CreateFailurePlan(VerificationError error, ProcessQuizCallbackJob job, long chatId, bool isInChat) {
    switch (error.Kind) {
      case VerificationErrorKind.UserOnCooldown:
        yield return () => dispatcher.DispatchAsync(new EditMessageJob(job.Message.Chat.Id, job.Message.Id, "❌ 你在冷却时间内，请稍后再试。"));
        break;

      case VerificationErrorKind.UserNotMatch:
        yield return () => dispatcher.DispatchAsync(new QuizCallbackQueryJob(job.QueryId, "❌ 该验证不适用于你。"));
        break;

      case VerificationErrorKind.IncorrectOptionOrExpiredToken:
        // This plan has two parts: setting a cooldown, then running the generic failure actions.
        var userStatusKey = $"user_status:{job.User.Id}:{chatId}";
        yield return () => redisDb.StringSetAsync(userStatusKey, "Cooldown", TimeSpan.FromMinutes(1));

        // Chain to the generic failure plan.
        foreach (var action in CreateGenericFailurePlan(job.User, job.Message, chatId, isInChat)) {
          yield return action;
        }
        break;

      default: // StateDeserializationFailed, StateValidationFailed, etc.
        foreach (var action in CreateGenericFailurePlan(job.User, job.Message, chatId, isInChat)) {
          yield return action;
        }
        break;
    }
  }

  internal IEnumerable<Func<Task>> CreateGenericFailurePlan(User user, Message message, long chatId, bool isInChat) {
    const string resultText = "❌ 验证失败，入群请求已被拒绝。";

    yield return () => dispatcher.DispatchAsync(new EditMessageJob(message.Chat.Id, message.Id, resultText));

    if (isInChat) {
      yield return () => dispatcher.DispatchAsync(new UnBanUserJob(chatId, user.Id, false));
    } else {
      yield return () => dispatcher.DispatchAsync(new ChatJoinRequestJob(user.Id, chatId, false));
    }

    yield return () => redisDb.KeyDeleteAsync($"user_statusE:{user.Id}:{chatId}");
  }

  internal Result<CallbackInfo, VerificationError> ParseCallbackData(string callbackData) {
    ArgumentNullException.ThrowIfNull(callbackData);
    var parts = callbackData.Split('_');
    if (parts.Length != 2 || !long.TryParse(parts[0], out var chatId)) {
      var errorMessage = $"Invalid callback data format: {callbackData}";
      return Result.Failure<CallbackInfo, VerificationError>(new VerificationError(VerificationErrorKind.InvalidCallbackData, errorMessage));
    }
    return Result.Success<CallbackInfo, VerificationError>(new CallbackInfo(chatId, parts[1]));
  }

  internal async Task<Result<CallbackInfo, VerificationError>> CheckPreVerificationStatus(User user, long chatId, CallbackInfo passThroughInfo) {
    var userStatusKey = $"user_status:{user.Id}:{chatId}";
    var status = await redisDb.StringGetAsync(userStatusKey);

    if (status.HasValue && status.ToString().Equals("Cooldown", StringComparison.OrdinalIgnoreCase)) {
      var errorMessage = $"User {user.Id} is in cooldown.";
      return Result.Failure<CallbackInfo, VerificationError>(new VerificationError(VerificationErrorKind.UserOnCooldown, errorMessage));
    }

    if (!status.HasValue) {
      var errorMessage = $"No active verification for user {user.Id}.";
      return Result.Failure<CallbackInfo, VerificationError>(new VerificationError(VerificationErrorKind.UserNotMatch, errorMessage));
    }

    return Result.Success<CallbackInfo, VerificationError>(passThroughInfo);
  }

  internal async Task<Result<bool, VerificationError>> VerifyAnswerAndCleanup(User user, long chatId, string clickedToken) {
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

  internal Result<VerificationState, VerificationError> DeserializeState(string stateJson) {
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

  internal Result<VerificationState, VerificationError> ValidateState(VerificationState state, User user, long chatId) {
    if (state.UserId == user.Id && state.ChatId == chatId) {
      return Result.Success<VerificationState, VerificationError>(state);
    }

    var errorMessage = $"State mismatch. Expected User/Chat: {user.Id}/{chatId}, but got {state.UserId}/{state.ChatId}";
    return Result.Failure<VerificationState, VerificationError>(new VerificationError(VerificationErrorKind.StateValidationFailed, errorMessage));
  }

  internal Task ClearUserStatusLockAsync(User user, long chatId) {
    var userStatusKey = $"user_status:{user.Id}:{chatId}";
    // var userStatusKeyE = $"user_statusE:{user.Id}:{chatId}";
    // return redisDb.KeyDeleteAsync(userStatusKey);
    return redisDb.StringSetAsync(userStatusKey, "WhiteList", TimeSpan.FromMinutes(1));
  }

  internal long GetChatIdFromCallback(string callbackData) {
    // Helper to extract ChatId for logging even on failure
    return ParseCallbackData(callbackData).Match(
        onSuccess: info => info.ChatId,
        onFailure: _ => 0
    );
  }

  #endregion
}
