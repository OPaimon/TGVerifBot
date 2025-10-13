using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramVerificationBot.Models;

namespace TelegramVerificationBot;


public interface IQuizService {
  /// <summary>
  /// Gets a random quiz from the available set.
  /// </summary>
  /// <returns>A Maybe containing a Quiz, or None if no quizzes are available.</returns>
  Maybe<Quiz> GetRandomQuiz();

  /// <summary>
  /// Backwards-compatibility shim for existing code that expects a Task that throws on failure.
  /// </summary>
  Task<Quiz> GetQuizRandomAsync();

  /// <summary>
  /// Hot-reloads the quiz data from the underlying data source.
  /// </summary>
  /// <returns>A Result indicating if the reload was successful.</returns>
  Task<Result> ReloadQuizzesAsync();
}

/// <summary>
/// A thread-safe, reloadable, in-memory provider for quizzes.
/// It loads quizzes from a JSON file at startup and provides a method to hot-reload them.
/// </summary>
public class QuizService : IQuizService {
  private readonly ILogger<QuizService> _logger;
  private readonly string _filePath;
  private readonly AppJsonSerializerContext _jsonContext;
  private readonly object _lock = new object();

  private IReadOnlyList<Quiz> _quizzes;

  // Constructor is private to enforce creation via the factory method.
  private QuizService(
      ILogger<QuizService> logger,
      string filePath,
      AppJsonSerializerContext jsonContext,
      IReadOnlyList<Quiz> initialQuizzes) {
    _logger = logger;
    _filePath = filePath;
    _jsonContext = jsonContext;
    _quizzes = initialQuizzes;
  }

  /// <summary>
  /// Safely creates an instance of QuizService by loading quizzes from the configured file path.
  /// </summary>
  public static async Task<Result<QuizService, string>> CreateAsync(
      ILogger<QuizService> logger,
      IConfiguration configuration,
      AppJsonSerializerContext jsonContext) {
    var filePath = configuration?["QuizFilePath"] ?? Path.Combine(AppContext.BaseDirectory, "data/quizzes.json");

    var loadResult = await LoadQuizzesFromFileAsync(filePath, jsonContext, logger);

    return loadResult.Map(quizzes => new QuizService(logger, filePath, jsonContext, quizzes));
  }

  /// <summary>
  /// Gets a random quiz from the in-memory list in a thread-safe manner.
  /// </summary>
  /// <returns>A Maybe containing a Quiz, or None if no quizzes are available.</returns>
  public Maybe<Quiz> GetRandomQuiz() {
    lock (_lock) {
      if (_quizzes.Count == 0) {
        return Maybe<Quiz>.None;
      }

      var randomQuiz = _quizzes[Random.Shared.Next(_quizzes.Count)];
      return Maybe.From(randomQuiz);
    }
  }

  /// <summary>
  /// Backwards-compatibility shim for existing code that expects a Task that throws on failure.
  /// </summary>
  public Task<Quiz> GetQuizRandomAsync() {
    return GetRandomQuiz().ToResult("No quizzes available.")
        .Match(
            onSuccess: Task.FromResult,
            onFailure: error => Task.FromException<Quiz>(new InvalidOperationException(error))
        );
  }

  /// <summary>
  /// Hot-reloads the quiz data from the source file.
  /// This operation is atomic and thread-safe.
  /// </summary>
  /// <returns>A Result indicating if the reload was successful.</returns>
  public async Task<Result> ReloadQuizzesAsync() {
    _logger.LogInformation("Attempting to hot-reload quizzes from {FilePath}", _filePath);

    var loadResult = await LoadQuizzesFromFileAsync(_filePath, _jsonContext, _logger);

    if (loadResult.IsFailure) {
      _logger.LogWarning("Quiz reload failed: {Error}. The existing quiz set will be kept.", loadResult.Error);
      return Result.Failure(loadResult.Error);
    }

    lock (_lock) {
      _quizzes = loadResult.Value;
    }

    _logger.LogInformation("Successfully reloaded {Count} quizzes.", loadResult.Value.Count);
    return Result.Success();
  }

  // Shared helper for loading data from the JSON file.
  private static async Task<Result<IReadOnlyList<Quiz>, string>> LoadQuizzesFromFileAsync(
      string filePath,
      AppJsonSerializerContext jsonContext,
      ILogger logger) {
    try {
      if (!File.Exists(filePath)) {
        logger.LogWarning("Quiz file not found at {FilePath}. Creating a default file with one sample quiz.", filePath);
        var sample = new List<Quiz>
        {
                    new Quiz(1, "What is the capital of France?", new List<string> { "Berlin", "Madrid", "Paris", "Rome" }, 2)
                };
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) {
          Directory.CreateDirectory(directory);
        }
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(sample, jsonContext.ListQuiz));
        return Result.Success<IReadOnlyList<Quiz>, string>(sample);
      }

      var text = await File.ReadAllTextAsync(filePath);
      var quizzes = JsonSerializer.Deserialize<List<Quiz>>(text, jsonContext.ListQuiz);

      if (quizzes == null) {
        return Result.Failure<IReadOnlyList<Quiz>, string>("Failed to deserialize quizzes file: result was null.");
      }

      return Result.Success<IReadOnlyList<Quiz>, string>(quizzes);
    } catch (Exception ex) {
      logger.LogError(ex, "An exception occurred while loading quizzes from {FilePath}", filePath);
      return Result.Failure<IReadOnlyList<Quiz>, string>($"An exception occurred: {ex.Message}");
    }
  }
}


public static class QuizServiceCollectionExtensions // 通常会改名为包含服务名的扩展类
{
  public static IServiceCollection AddQuizService(this IServiceCollection services) {
    services.AddSingleton<IQuizService>(sp => {
      var logger = sp.GetRequiredService<ILogger<QuizService>>();
      var config = sp.GetRequiredService<IConfiguration>();
      var jsonContext = sp.GetRequiredService<AppJsonSerializerContext>();

      var createResult = QuizService.CreateAsync(logger, config, jsonContext)
                                    .GetAwaiter()
                                    .GetResult();

      if (createResult.IsFailure) {
        var startupLogger = sp.GetRequiredService<ILogger<Program>>();
        string error = $"FATAL: QuizService initialization failed: {createResult.Error}";
        startupLogger.LogCritical(error);
        throw new InvalidOperationException(error);
      }

      return createResult.Value;
    });

    return services;
  }
}
