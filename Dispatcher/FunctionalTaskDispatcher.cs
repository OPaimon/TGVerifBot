using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TelegramVerificationBot.Tasks;

namespace TelegramVerificationBot.Dispatcher
{
    public class FunctionalTaskDispatcher : BackgroundService
    {
        private readonly ILogger<FunctionalTaskDispatcher> _logger;
        private readonly IReadOnlyDictionary<Type, Func<IServiceProvider, object, Task>> _handlers;
        private readonly IServiceProvider _serviceProvider;
        private readonly IRateLimiter _rateLimiter;
        private readonly Channel<object> _queue = Channel.CreateUnbounded<object>();

        public FunctionalTaskDispatcher(
            ILogger<FunctionalTaskDispatcher> logger,
            IReadOnlyDictionary<Type, Func<IServiceProvider, object, Task>> handlers,
            IServiceProvider serviceProvider,
            IRateLimiter rateLimiter)
        {
            _logger = logger;
            _handlers = handlers;
            _serviceProvider = serviceProvider;
            _rateLimiter = rateLimiter;
        }

        // This is the public API for other services to dispatch jobs.
        // It quickly adds the job to the in-memory queue and returns.
        public async Task DispatchAsync(object job)
        {
            await _queue.Writer.WriteAsync(job);
        }

        // This is the long-running background task that processes the queue.
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Queued task dispatcher is starting.");

            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    // Rate limiting logic (the "valve")
                    if (job is StartVerificationJob svj)
                    {
                        while (!await _rateLimiter.AllowStartVerificationAsync(svj.Requester.From.Id, svj.Requester.Chat.Id) && !stoppingToken.IsCancellationRequested)
                        {
                            _logger.LogInformation("Rate limit hit for user {UserId} on StartVerification. Waiting...", svj.Requester.From.Id);
                            await Task.Delay(1000, stoppingToken);
                        }
                    }
                    else if (job is ProcessQuizCallbackJob pcj)
                    {
                        while (!await _rateLimiter.AllowCallbackAsync(pcj.User.Id, pcj.Message.Chat.Id) && !stoppingToken.IsCancellationRequested)
                                            {
                                                _logger.LogInformation("Rate limit hit for user {UserId} on QuizCallback. Waiting...", pcj.User.Id);                            await Task.Delay(500, stoppingToken);
                        }
                    }

                    // Execution logic
                    _logger.LogInformation("Processing job {JobType}...", job.GetType().Name);
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        if (_handlers.TryGetValue(job.GetType(), out var handler))
                        {
                            await handler(scope.ServiceProvider, job);
                        }
                        else
                        {
                            _logger.LogError("No handler found for job type {JobType}", job.GetType().Name);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "An error occurred while processing job {JobType}.", job.GetType().Name);
                }
            }

            _logger.LogInformation("Queued task dispatcher is stopping.");
        }
    }
}
