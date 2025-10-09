using System.Threading.Channels;
using TelegramVerificationBot.Tasks;

namespace TelegramVerificationBot.Dispatcher;

public class FunctionalTaskDispatcher(
        ILogger<FunctionalTaskDispatcher> logger,
        IReadOnlyDictionary<Type, Func<IServiceProvider, object, Task>> handlers,
        IServiceProvider serviceProvider,
        IRateLimiter rateLimiter) : BackgroundService
{
    private readonly Channel<object> _queue = Channel.CreateUnbounded<object>();

    // This is the public API for other services to dispatch jobs.
    // It quickly adds the job to the in-memory queue and returns.
    public async Task DispatchAsync(object job)
    {
        await _queue.Writer.WriteAsync(job);
    }

    // This is the long-running background task that processes the queue.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Queued task dispatcher is starting.");

        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                // Rate limiting logic (the "valve")
                if (job is StartVerificationJob svj)
                {
                    while (!await rateLimiter.AllowStartVerificationAsync(svj.Requester.From.Id, svj.Requester.Chat.Id) && !stoppingToken.IsCancellationRequested)
                    {
                        logger.LogInformation("Rate limit hit for user {UserId} on StartVerification. Waiting...", svj.Requester.From.Id);
                        await Task.Delay(1000, stoppingToken);
                    }
                }
                else if (job is ProcessQuizCallbackJob pcj)
                {
                    while (!await rateLimiter.AllowCallbackAsync(pcj.User.Id, pcj.Message.Chat.Id) && !stoppingToken.IsCancellationRequested)
                    {
                        logger.LogInformation("Rate limit hit for user {UserId} on QuizCallback. Waiting...", pcj.User.Id); await Task.Delay(500, stoppingToken);
                    }
                }

                // Execution logic
                logger.LogInformation("Processing job {JobType}...", job.GetType().Name);
                using (var scope = serviceProvider.CreateScope())
                {
                    if (handlers.TryGetValue(job.GetType(), out var handler))
                    {
                        await handler(scope.ServiceProvider, job);
                    }
                    else
                    {
                        logger.LogError("No handler found for job type {JobType}", job.GetType().Name);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "An error occurred while processing job {JobType}.", job.GetType().Name);
            }
        }

        logger.LogInformation("Queued task dispatcher is stopping.");
    }
}

