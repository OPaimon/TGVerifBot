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
  UserPending,
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

public readonly struct VerificationError(VerificationErrorKind kind, string message) {
  public VerificationErrorKind Kind { get; } = kind;
  public string Message { get; } = message;

  public override string ToString() => Message;
}


public class VerificationService(
    ILogger<VerificationService> logger,
    ITaskDispatcher dispatcher,
    IDatabase redisDb,
    IQuizService quizService,
    AppJsonSerializerContext jsonContext) {
  // --- Internal records for data transfer ---
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
      // .Bind(data => StoreStateAsync(data.Job.UserId, data.Job.ChatId, data.Quiz.CorrectToken, data));
      .Bind(data => StoreStateAsync(data.Job, data.Quiz, data));

    await result.Match(
        // onSuccess: data => DispatchSendQuizJob(data.Job, data.Quiz),
        onSuccess: storedInfo => {
          var originalData = storedInfo.PassThroughValue;
          return DispatchSendQuizJob(originalData.Job, originalData.Quiz, storedInfo.SessionId); // 保持不变
        },
        onFailure: error => HandleStartVerificationFailure(job, error)
    );
  }

  internal async Task<Result<StartVerificationJob, VerificationError>> CheckUserStatusAsync(StartVerificationJob job) {
    var cooldownKey = $"verify:cooldown:{job.ChatId}:{job.UserId}";
    if (await redisDb.KeyExistsAsync(cooldownKey)) {
      var errorMessage = $"User {job.UserId} is on cooldown in chat {job.ChatId}.";
      return Result.Failure<StartVerificationJob, VerificationError>(new VerificationError(VerificationErrorKind.UserOnCooldown, errorMessage));
    }

    var lookupKey = $"verify:lookup:{job.ChatId}:{job.UserId}";
    if (await redisDb.KeyExistsAsync(lookupKey)) {
      var errorMessage = $"User {job.UserId} already has a pending verification in chat {job.ChatId}.";
      return Result.Failure<StartVerificationJob, VerificationError>(new VerificationError(VerificationErrorKind.UserPending, errorMessage));
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
            var shuffledOptions = optionsWithTokens.OrderBy(_ => random.Next()).ToList();

            return new VerificationQuizData(shuffledOptions, correctToken, quiz.Question);
          });

  internal record StoredStateInfo<T>(string SessionId, string CallbackToken, T PassThroughValue);

  internal async Task<Result<StoredStateInfo<T>, VerificationError>> StoreStateAsync<T>(
    StartVerificationJob job,
    VerificationQuizData quiz,
    T passThroughValue
  ) {
    try {
      var sessionId = Guid.NewGuid().ToString();
      var correctToken = quiz.CorrectToken;
      var contextType = job.UserChatId == job.ChatId
        ? VerificationContextType.InGroupRestriction
        : VerificationContextType.JoinRequest;
      var session = new VerificationSession(
          SessionId: sessionId,
          UserId: job.UserId,
          TargetChatId: job.ChatId,
          ContextType: contextType,
          CorrectToken: correctToken, // 正确答案的 Token
          OptionsWithTokens: quiz.OptionsWithTokens // 存储所有选项和它们的 Token
      );
      var sessionJson = JsonSerializer.Serialize(session, jsonContext.VerificationSession);
      var sessionKey = $"verify:session:{sessionId}";
      var correctTokenMapKey = $"verify:token_map:{correctToken}";
      var tokenMapKeyList = quiz.OptionsWithTokens
        .Where(o => o.Token != correctToken)
        .Select(o => $"verify:token_map:{o.Token}")
        .ToList();
      var lookupKey = $"verify:lookup:{job.ChatId}:{job.UserId}";
      var timeoutKey = $"verify:timeout:{job.ChatId}:{job.UserId}";

      var tran = redisDb.CreateTransaction();

      var triggerExpiry = TimeSpan.FromMinutes(VerificationTimeoutMinutes);
      var dataExpiry = triggerExpiry + TimeSpan.FromSeconds(MessageDeletionDelaySeconds);

      _ = tran.StringSetAsync(sessionKey, sessionJson, dataExpiry);
      _ = tran.StringSetAsync(correctTokenMapKey, sessionId, dataExpiry);
      _ = tran.StringSetAsync(lookupKey, sessionId, dataExpiry);
      _ = tran.StringSetAsync(timeoutKey, sessionId, triggerExpiry);
      foreach (var key in tokenMapKeyList) {
        _ = tran.StringSetAsync(key, sessionId, dataExpiry);
      }

      if (await tran.ExecuteAsync()) {
        var storedInfo = new StoredStateInfo<T>(sessionId, correctToken, passThroughValue);
        return Result.Success<StoredStateInfo<T>, VerificationError>(storedInfo);
      }
      return Result.Failure<StoredStateInfo<T>, VerificationError>(new VerificationError(
          VerificationErrorKind.StateStorageFailed, "Failed to execute Redis transaction."));


    } catch (Exception ex) {
      var errorMessage = $"Failed to store verification state in Redis: {ex.Message}";
      return Result.Failure<StoredStateInfo<T>, VerificationError>(new VerificationError(VerificationErrorKind.StateStorageFailed, errorMessage));
    }
  }

  internal Task DispatchSendQuizJob(StartVerificationJob originalJob, VerificationQuizData quizData, string sessionId) {
    logger.LogInformation("Dispatching SendQuizJob for user {UserId}", originalJob.UserId);
    return dispatcher.DispatchAsync(new SendQuizJob(
        originalJob.ChatId,
        originalJob.ChatTitle,
        quizData.Question,
        originalJob.UserId,
        originalJob.UserFirstName,
        originalJob.UserChatId,
        quizData.OptionsWithTokens,
        sessionId
    // originalJob.InviteLink
    ));
  }

  internal Task HandleStartVerificationFailure(StartVerificationJob job, VerificationError error) {
    logger.LogWarning("Failed to start verification for user {UserId}: {Error}", job.UserId, error);

    var contextType = job.UserChatId == job.ChatId
      ? VerificationContextType.InGroupRestriction
      : VerificationContextType.JoinRequest;

    string messageText;

    switch (error.Kind) {
      case VerificationErrorKind.UserPending: // 假设我们有一个更精确的错误类型
        messageText = "❌ 您有一个正在进行的验证，请先完成它。";
        dispatcher.DispatchAsync(new SendMessageVerfJob(job.ChatId, messageText));
        return Task.CompletedTask;
      case VerificationErrorKind.UserOnCooldown:
        messageText = "❌ 您处于冷却时间内，请稍后再试。";
        break;
      case VerificationErrorKind.NoQuizzesAvailable:
      case VerificationErrorKind.StateStorageFailed:
        messageText = "❌ 验证服务当前不可用，我们无法处理您的请求。";
        break;

      default:
        logger.LogError("为用户 {UserId} 启动验证时遇到未处理的错误: {Error}", job.UserId, error);
        return Task.CompletedTask;
    }

    dispatcher.DispatchAsync(new SendMessageVerfJob(job.ChatId, messageText));

    switch (contextType) {
      case VerificationContextType.InGroupRestriction:
        Task.Delay(TimeSpan.FromSeconds(5))
            .ContinueWith(_ => dispatcher.DispatchAsync(new KickUserJob(job.ChatId, job.UserId)));
        break;

      case VerificationContextType.JoinRequest:
        dispatcher.DispatchAsync(new ChatJoinRequestJob(job.UserId, job.ChatId, false));
        break;
    }

    return Task.CompletedTask;
  }

  public async Task HandleSendQuizCallback(SendQuizCallbackJob job) {
    if (string.IsNullOrEmpty(job.SessionId)) {
      logger.LogError("Cannot update MessageId, SessionId is missing in SendQuizCallbackJob.");
      return;
    }

    var sessionKey = $"verify:session:{job.SessionId}";
    var sessionJson = await redisDb.StringGetAsync(sessionKey);
    if (sessionJson.IsNullOrEmpty) {
      logger.LogWarning("Could not find session {SessionId} to update MessageId.", job.SessionId);
      return;
    }
    try {
      var session = JsonSerializer.Deserialize(sessionJson.ToString(), jsonContext.VerificationSession);
      if (session is null) {
        return;
      }

      var updatedSession = session with { VerificationMessageId = job.MessageId };
      var updatedSessionJson = JsonSerializer.Serialize(updatedSession, jsonContext.VerificationSession);

      var ttl = await redisDb.KeyTimeToLiveAsync(sessionKey);

      await redisDb.StringSetAsync(sessionKey, updatedSessionJson, ttl);

      logger.LogInformation("Successfully updated MessageId for session {SessionId}.", job.SessionId);

    } catch (Exception ex) {
      logger.LogError(ex, "Failed to update MessageId for session {SessionId}.", job.SessionId);
    }
  }

  #endregion

  #region Handle Callback

  public async Task HandleCallbackAsync(ProcessQuizCallbackJob job) {
    var findSessionResult = await FindSessionByTokenAsync(job.CallbackData);
    var actionsPlan = findSessionResult.Match(
      onSuccess: session => {
        return Result.Success<VerificationSession, VerificationError>(session)
          .Ensure(s => s.UserId == job.User.Id,
              new VerificationError(VerificationErrorKind.UserNotMatch, "该验证不适用于你。"))
          .Ensure(s => s.OptionsWithTokens.Any(o => o.Token == job.CallbackData),
              new VerificationError(VerificationErrorKind.StateValidationFailed, "Session data validation failed."))
          .Ensure(s => s.CorrectToken == job.CallbackData,
              new VerificationError(VerificationErrorKind.IncorrectOptionOrExpiredToken, "Incorrect option selected."))
          .Match(
            onSuccess: _ => {
              logger.LogInformation("Verification approved for user {UserId} in chat {ChatId}. Creating success plan.", job.User.Id, session.TargetChatId);
              return CreateSuccessPlan(job.User, job.Message, session);
            },
            onFailure: error => {
              logger.LogWarning("Verification failed for user {UserId} in chat {ChatId}: {Error}. Creating failure plan.", job.User.Id, session.TargetChatId, error.Message);
              return CreateFailurePlan(error, job, session);
            }
          );
      },
      onFailure: error => {

        logger.LogWarning("Verification failed for user {UserId}: {Error}. Creating failure plan without session.", job.User.Id, error.Message);
        return CreateFailurePlan(error, job, new VerificationSession(
          SessionId: "unknown",
          UserId: job.User.Id,
          TargetChatId: job.Message.Chat.Id,
          ContextType: job.User.Id == job.Message.Chat.Id
            ? VerificationContextType.JoinRequest
            : VerificationContextType.InGroupRestriction,
          CorrectToken: "",
          OptionsWithTokens: []
        ));
      }
    );

    foreach (var action in actionsPlan) {
      await action();
    }
  }

  internal async Task<Result<VerificationSession, VerificationError>> FindSessionByTokenAsync(string callbackToken) {
    var tokenMapKey = $"verify:token_map:{callbackToken}";

    var sessionId = await redisDb.StringGetDeleteAsync(tokenMapKey);

    if (sessionId.IsNullOrEmpty) {
      return Result.Failure<VerificationSession, VerificationError>(new VerificationError(
          VerificationErrorKind.IncorrectOptionOrExpiredToken, "Invalid, expired, or already used token."));
    }

    var sessionKey = $"verify:session:{sessionId}";
    var sessionJson = await redisDb.StringGetAsync(sessionKey);

    if (sessionJson.IsNullOrEmpty) {
      return Result.Failure<VerificationSession, VerificationError>(new VerificationError(
          VerificationErrorKind.StateDeserializationFailed, "Session data not found for a valid token."));
    }

    try {
      var session = JsonSerializer.Deserialize(sessionJson.ToString(), jsonContext.VerificationSession);
      return session != null
          ? Result.Success<VerificationSession, VerificationError>(session)
          : Result.Failure<VerificationSession, VerificationError>(new VerificationError(
              VerificationErrorKind.StateDeserializationFailed, "Deserialized session is null."));
    } catch (JsonException ex) {
      return Result.Failure<VerificationSession, VerificationError>(new VerificationError(
          VerificationErrorKind.StateDeserializationFailed, $"Failed to deserialize session: {ex.Message}"));
    }
  }

  internal IEnumerable<Func<Task>> CreateSuccessPlan(User user, Message message, VerificationSession session) {
    const string resultText = "✅ 验证通过！欢迎加入！";
    yield return () => dispatcher.DispatchAsync(new EditMessageJob(message.Chat.Id, message.Id, resultText));

    switch (session.ContextType) {
      case VerificationContextType.InGroupRestriction:
        yield return () => dispatcher.DispatchAsync(new RestrictUserJob(
            ChatId: session.TargetChatId,
            UserId: user.Id,
            Permissions: TelegramService.FullRestrictPermissions(),
            UntilDate: DateTime.UtcNow + TimeSpan.FromMinutes(1)
          ));
        break;

      case VerificationContextType.JoinRequest:
        yield return () => dispatcher.DispatchAsync(new ChatJoinRequestJob(user.Id, session.TargetChatId, true));
        break;
    }
    var sessionKey = $"verify:session:{session.SessionId}";
    var lookupKey = $"verify:lookup:{session.TargetChatId}:{user.Id}";
    var timeoutKey = $"verify:timeout:{session.TargetChatId}:{user.Id}";
    yield return () => redisDb.KeyDeleteAsync([sessionKey, lookupKey, timeoutKey]);

    yield return () => {
      var cleanupTask = new CleanupTask(message.Id, message.Chat.Id);
      var taskJson = JsonSerializer.Serialize(cleanupTask, jsonContext.CleanupTask);
      var executeAtTimestamp = DateTimeOffset.UtcNow.AddSeconds(MessageDeletionDelaySeconds).ToUnixTimeSeconds();
      return redisDb.SortedSetAddAsync("cleanup_queue", taskJson, executeAtTimestamp);
    };
  }

  internal IEnumerable<Func<Task>> CreateFailurePlan(VerificationError error, ProcessQuizCallbackJob job, VerificationSession session) {
    switch (error.Kind) {
      case VerificationErrorKind.UserOnCooldown:
        yield return () => dispatcher.DispatchAsync(new EditMessageJob(job.Message.Chat.Id, job.Message.Id, "❌ 你在冷却时间内，请稍后再试。"));
        break;

      case VerificationErrorKind.UserNotMatch:
        yield return () => dispatcher.DispatchAsync(new QuizCallbackQueryJob(job.QueryId, "❌ 该验证不适用于你。"));
        break;

      case VerificationErrorKind.IncorrectOptionOrExpiredToken:
        // This plan has two parts: setting a cooldown, then running the generic failure actions.
        var cooldownKey = $"verify:cooldown:{session.TargetChatId}:{job.User.Id}";
        yield return () => redisDb.StringSetAsync(cooldownKey, "1", TimeSpan.FromMinutes(CooldownMinutes));

        var sessionKey = $"verify:session:{session.SessionId}";
        var lookupKey = $"verify:lookup:{session.TargetChatId}:{job.User.Id}";
        var timeoutKey = $"verify:timeout:{session.TargetChatId}:{job.User.Id}";
        yield return () => redisDb.KeyDeleteAsync([sessionKey, lookupKey, timeoutKey]);


        // Chain to the generic failure plan.
        foreach (var action in CreateGenericFailurePlan(job.User, job.Message, session)) {
          yield return action;
        }

        yield return () => {
          var cleanupTask = new CleanupTask(job.Message.Id, job.Message.Chat.Id);
          var taskJson = JsonSerializer.Serialize(cleanupTask, jsonContext.CleanupTask);
          var executeAtTimestamp = DateTimeOffset.UtcNow.AddSeconds(MessageDeletionDelaySeconds).ToUnixTimeSeconds();

          return redisDb.SortedSetAddAsync("cleanup_queue", taskJson, executeAtTimestamp);
        };
        break;

      default: // StateDeserializationFailed, StateValidationFailed, etc.
        foreach (var action in CreateGenericFailurePlan(job.User, job.Message, session)) {
          yield return action;
        }
        break;
    }
  }

  internal IEnumerable<Func<Task>> CreateGenericFailurePlan(User user, Message message, VerificationSession session) {
    const string resultText = "❌ 验证失败，入群请求已被拒绝。";

    yield return () => dispatcher.DispatchAsync(new EditMessageJob(message.Chat.Id, message.Id, resultText));

    switch (session.ContextType) {
      case VerificationContextType.InGroupRestriction:
        yield return () => dispatcher.DispatchAsync(new KickUserJob(session.TargetChatId, user.Id));
        break;
      case VerificationContextType.JoinRequest:
        yield return () => dispatcher.DispatchAsync(new ChatJoinRequestJob(user.Id, session.TargetChatId, false));
        break;
    }
  }

  #endregion
}
