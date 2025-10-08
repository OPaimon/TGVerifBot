namespace TelegramVerificationBot;

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
            // 这是 Worker 唯一的核心任务：
            // 启动 Telegram 服务，并等待它完成。
            // 我们将 stoppingToken 传递下去，以便 TelegramService 知道何时需要停止。
            await _telegramService.ConnectAndListenAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not TaskCanceledException)
        {
            // 如果 TelegramService 的主循环中出现未处理的致命异常，
            // 在这里捕获并记录，然后应用程序会随之停止。
            _logger.LogCritical(ex, "The main application loop has crashed unexpectedly.");
        }
        finally
        {
            _logger.LogInformation("Worker gracefully stopping at: {time}", DateTimeOffset.Now);
        }
    }
}
