using System.Threading.Channels;
using TelegramVerificationBot.Services;
using TelegramVerificationBot.Tasks;

namespace TelegramVerificationBot.Dispatcher;

// highlight-next-line
public class FunctionalTaskDispatcher(
        ILogger<FunctionalTaskDispatcher> logger,
        IServiceProvider serviceProvider,
// highlight-next-line
        IRateLimiter rateLimiter) : BackgroundService, ITaskDispatcher {
  private readonly Channel<object> _queue = Channel.CreateUnbounded<object>();

  // This method now fulfills the ITaskDispatcher interface contract.
  public async Task DispatchAsync(IJob job) {
    await _queue.Writer.WriteAsync(job);
  }

  // The rest of the class remains the same...
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    logger.LogInformation("Queued task dispatcher is starting.");

    await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken)) {
      try {
        // Rate limiting logic
        if (job is StartVerificationJob svj) {
          while (!await rateLimiter.AllowStartVerificationAsync(svj.UserId, svj.ChatId) && !stoppingToken.IsCancellationRequested) {
            logger.LogInformation("Rate limit hit for user {UserId} on StartVerification. Waiting...", svj.UserId);
            await Task.Delay(1000, stoppingToken);
          }
        } else if (job is ProcessQuizCallbackJob pcj) {
          while (!await rateLimiter.AllowCallbackAsync(pcj.User.Id, pcj.Message.Chat.Id) && !stoppingToken.IsCancellationRequested) {
            logger.LogInformation("Rate limit hit for user {UserId} on QuizCallback. Waiting...", pcj.User.Id);
            await Task.Delay(500, stoppingToken);
          }
        }

        // Execution logic
        logger.LogInformation("Processing job {JobType}...", job.GetType().Name);
        using (var scope = serviceProvider.CreateScope()) {
          if (job is IJob processableJob) {
            await processableJob.ProcessAsync(scope.ServiceProvider);
          } else {
            logger.LogError("No handler found for job type {JobType}", job.GetType().Name);
          }
        }
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        logger.LogError(ex, "An error occurred while processing job {JobType}.", job.GetType().Name);
      }
    }

    logger.LogInformation("Queued task dispatcher is stopping.");
  }
}
