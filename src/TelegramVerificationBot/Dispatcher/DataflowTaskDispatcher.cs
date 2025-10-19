namespace TelegramVerificationBot.Dispatcher;

using System.Threading.Tasks.Dataflow;
using TelegramVerificationBot.Tasks;


public class DataflowTaskDispatcher(
  ILogger<DataflowTaskDispatcher> logger,
  IServiceProvider services,
  IRateLimiter rateLimiter,
  DataflowPipelineBuilder pipelineBuilder
  ) : BackgroundService, ITaskDispatcher {
  private ITargetBlock<object>? _entryPoint;
  private IEnumerable<IDataflowBlock>? _completionTargets;
  private CancellationToken _cancellationToken;

  public async Task DispatchAsync(IJob job) {
    await _entryPoint!.SendAsync(job);
  }

  public async Task ProcessJobAsync(object job) {
    try {
      if (!await IsAllowedByRateLimiter(job)) {
        return;
      }

      using IServiceScope scope = services.CreateScope();
      if (job is IJob processableJob) {
        await processableJob.ProcessAsync(scope.ServiceProvider);
      } else {
        logger.LogInformation("No handler registered for job type {GetType}", job.GetType());
      }
    } catch (Exception e) when (e is not OperationCanceledException) {
      logger.LogError(e, "An error occured during processing job {JobType}", job.GetType().Name);
    }
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    logger.LogInformation("TPL Dataflow Task Dispatcher is starting.");

    var pipeline = pipelineBuilder.Build(ProcessJobAsync, stoppingToken);

    _entryPoint = pipeline.EntryPoint;
    _completionTargets = pipeline.CompletionTargets;
    _cancellationToken = stoppingToken;

    await Task.WhenAll(_completionTargets.Select(b => b.Completion));

    logger.LogInformation("TPL Dataflow Task Dispatcher is finished.");
  }

  private async Task<bool> IsAllowedByRateLimiter(object job) {
    while (!_cancellationToken.IsCancellationRequested) {
      bool allowed;
      int delayMs;

      switch (job) {
        case StartVerificationJob svj:
          allowed = await rateLimiter.AllowStartVerificationAsync(svj.UserId, svj.ChatId);
          delayMs = 1000;
          break;
        case ProcessQuizCallbackJob pcj:
          allowed = await rateLimiter.AllowCallbackAsync(pcj.User.Id, pcj.Message.Chat.Id);
          delayMs = 500;
          break;
        default:
          return true;
      }

      if (allowed) {
        return true;
      }

      logger.LogInformation("Rate limit hot for job {JobType}. Waiting...", job.GetType().Name);

      try {
        await Task.Delay(delayMs, _cancellationToken);
      } catch (OperationCanceledException) {
        break;
      }
    }
    return false;
  }
}

