using StackExchange.Redis;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Tasks;

namespace TelegramVerificationBot.Services;

public class RedisKeyeventListener : BackgroundService
{
    private readonly ILogger<RedisKeyeventListener> _logger;
    private readonly IConnectionMultiplexer _redis;
    private readonly ITaskDispatcher _dispatcher;
    public RedisKeyeventListener(
        ILogger<RedisKeyeventListener> logger,
        IConnectionMultiplexer redis,
        ITaskDispatcher dispatcher)
    {
        _logger = logger;
        _redis = redis;
        _dispatcher = dispatcher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Redis Key-Event Listener is starting.");
        var subscriber = _redis.GetSubscriber();

        var channelPattern = "__keyevent@*__:expired";


        var queue = await subscriber.SubscribeAsync(new RedisChannel(channelPattern, RedisChannel.PatternMode.Pattern));
        queue.OnMessage(async channelMessage =>
        {
            try
            {
                var key = channelMessage.Message.ToString();
                var eventType = channelMessage.Channel.ToString(); // e.g., "__keyevent@0__:expired"

                _logger.LogDebug("Received Redis expired event. Key: {Key}, Channel: {Channel}", key, eventType);

                var job = new RedisKeyEventJob(key, "expired");
                await _dispatcher.DispatchAsync(job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Redis key-event for channel {Channel}", channelMessage.Channel);
            }
        });

        // Wait until the application is stopping
        stoppingToken.Register(() => _logger.LogInformation("Redis Key-Event Listener is stopping."));
        await Task.Delay(-1, stoppingToken);
    }
}
