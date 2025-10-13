namespace TelegramVerificationBot;

/// <summary>
/// The main background service that hosts the application's long-running processes.
/// </summary>
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly TelegramService _telegramService;

    public Worker(ILogger<Worker> logger, TelegramService telegramService)
    {
        _logger = logger;
        _telegramService = telegramService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker starting at: {time}", DateTimeOffset.Now);

        try
        {
            // This is the single core task of the Worker:
            // to start the Telegram service and wait for it to complete.
            // We pass the stoppingToken down so the TelegramService knows when to stop.
            await _telegramService.ConnectAndListenAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not TaskCanceledException)
        {
            // If an unhandled fatal exception occurs in the main loop of TelegramService,
            // it's caught and logged here, and the application will then stop.
            _logger.LogCritical(ex, "The main application loop has crashed unexpectedly.");
        }
        finally
        {
            _logger.LogInformation("Worker gracefully stopping at: {time}", DateTimeOffset.Now);
        }
    }
}