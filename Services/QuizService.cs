using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramVerificationBot.Models;

namespace TelegramVerificationBot;

/// <summary>
/// JSON-backed quiz repository/service.
/// Loads quizzes from a JSON file on construction, provides thread-safe CRUD and random selection,
/// and merges/persists changes back to the file when disposed.
/// </summary>
public class QuizService : IAsyncDisposable, IDisposable
{
    private readonly ILogger<QuizService> _logger;
    private readonly string _filePath;
    private readonly ConcurrentDictionary<int, Quiz> _quizzes = new();
    private readonly HashSet<int> _deletedIds = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly AppJsonSerializerContext _jsonContext;
    private bool _disposed;

    public QuizService(ILogger<QuizService> logger, IConfiguration configuration, AppJsonSerializerContext jsonContext)
    {
        _jsonContext = jsonContext;
        _logger = logger;
        // allow file path from configuration, else default to "quizzes.json" in app base
        _filePath = configuration?["QuizFilePath"] ?? Path.Combine(AppContext.BaseDirectory, "data/quizzes.json");
        // If file missing, create a default file with a sample quiz so startup has at least one question.
        try
        {
            if (!File.Exists(_filePath))
            {
                var sample = new List<Quiz>
                {
                    new Quiz(1, "What is the capital of France?", new List<string>{"Berlin","Madrid","Paris","Rome"}, 2)
                };
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(_filePath, JsonSerializer.Serialize(sample, _jsonContext.ListQuiz));
                foreach (var q in sample)
                    _quizzes[q.Id] = q;
            }
            else
            {
                var text = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize(text, _jsonContext.ListQuiz) ?? new List<Quiz>();
                foreach (var q in list)
                    _quizzes[q.Id] = q;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load or create quizzes file at {path}", _filePath);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    // Contract: methods are async and thread-safe. Ids are ints and must be unique.

    public Task<List<Quiz>> GetAllAsync()
    {
        return Task.FromResult(_quizzes.Values.Select(CloneQuiz).ToList());
    }

    public Task<Quiz?> GetByIdAsync(int id)
    {
        return Task.FromResult(_quizzes.TryGetValue(id, out var q) ? CloneQuiz(q) : null);
    }

    public Task<Quiz> CreateAsync(Quiz quiz)
    {
        // assign id if empty (0) -> pick one greater than max
        if (quiz.Id == 0)
        {
            var next = _quizzes.Keys.DefaultIfEmpty(0).Max() + 1;
            quiz = quiz with { Id = next };
        }

        _quizzes[quiz.Id] = CloneQuiz(quiz);
        return Task.FromResult(CloneQuiz(quiz));
    }

    public Task<bool> UpdateAsync(Quiz quiz)
    {
        if (!_quizzes.ContainsKey(quiz.Id))
            return Task.FromResult(false);

        _quizzes[quiz.Id] = CloneQuiz(quiz);
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(int id)
    {
        var removed = _quizzes.TryRemove(id, out _);
        if (removed)
            lock (_deletedIds)
                _deletedIds.Add(id);
        return Task.FromResult(removed);
    }

    public Task<Quiz?> GetRandomAsync()
    {
        if (_quizzes.IsEmpty) return Task.FromResult<Quiz?>(null);

        int count = _quizzes.Count;
        var idx = Random.Shared.Next(count);
        var randomQuiz = _quizzes.Values.ElementAt(idx);
        return Task.FromResult<Quiz?>(CloneQuiz(randomQuiz));
    }

    // Compatibility shim for older name used in codebase
    public Task<Quiz> GetQuizRandomAsync()
    {
        var task = GetRandomAsync();
        return task.ContinueWith(t => t.Result ?? throw new InvalidOperationException("No quizzes available"), TaskScheduler.Current);
    }

    private static Quiz CloneQuiz(Quiz q)
    {
        return new Quiz(q.Id, q.Question, new List<string>(q.Options), q.CorrectOptionIndex);
    }

    // Merge and persist changes to file. Strategy: load on-disk list, apply in-memory changes (adds/updates/deletes), then write back.
    private async Task PersistAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            List<Quiz> onDisk = new();
            if (File.Exists(_filePath))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(_filePath);
                    onDisk = JsonSerializer.Deserialize(text, _jsonContext.ListQuiz) ?? new List<Quiz>();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read/deserialize existing quiz file {path}, starting fresh", _filePath);
                    onDisk = new List<Quiz>();
                }
            }

            var map = onDisk.ToDictionary(q => q.Id);

            // Apply deletions
            lock (_deletedIds)
            {
                foreach (var id in _deletedIds)
                    map.Remove(id);
            }

            // Apply current in-memory quizzes (add or replace)
            foreach (var kv in _quizzes)
                map[kv.Key] = CloneQuiz(kv.Value);

            var merged = map.Values.OrderBy(q => q.Id).ToList();
            var outText = JsonSerializer.Serialize(merged, _jsonContext.ListQuiz);
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllTextAsync(_filePath, outText);
            _logger.LogInformation("Persisted {count} quizzes to {path}", merged.Count, _filePath);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            PersistAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while persisting quizzes during Dispose");
        }
        _fileLock.Dispose();
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try
        {
            await PersistAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while persisting quizzes during DisposeAsync");
        }
        _fileLock.Dispose();
        _disposed = true;
    }
}