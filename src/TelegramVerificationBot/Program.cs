
using Serilog;
using Serilog.Events;
using StackExchange.Redis;
using TelegramVerificationBot;
using TelegramVerificationBot.Configuration;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Services;
using TelegramVerificationBot.Tasks;
using WTelegram;

// Configure Serilog logger programmatically
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .MinimumLevel.Override("WTelegram", LogEventLevel.Warning) // Filter WTelegram logs
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/bot-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// Redirect WTelegram's static logger to the Serilog pipeline
var wtelegramLogger = Log.ForContext("SourceContext", "WTelegram");
Helpers.Log = (level, message) => {
  var logLevel = level switch {
    0 => LogEventLevel.Debug,
    1 => LogEventLevel.Information,
    2 => LogEventLevel.Warning,
    3 => LogEventLevel.Error,
    _ => LogEventLevel.Information
  };
  wtelegramLogger.Write(logLevel, message);
};

try {
  Log.Information("Starting application host");

  var builder = Host.CreateApplicationBuilder(args);

  // Replace default logging with Serilog
  builder.Logging.ClearProviders();
  builder.Logging.AddSerilog();

  // Your existing service configurations
  builder.Services.AddOptions<TelegramSettings>()
      .BindConfiguration("TelegramSettings");

  builder.Services.AddOptions<RateLimitingSettings>()
      .BindConfiguration("RateLimiting");

  builder.Services.AddSingleton<TelegramService>();
  builder.Services.AddSingleton<VerificationServiceROP>();
  builder.Services.AddQuizService();
  builder.Services.AddSingleton<ExpiredStateService>();

  var handlers = new Dictionary<Type, Func<IServiceProvider, object, Task>> {
    [typeof(RespondToPingJob)] = (sp, job) =>
        sp.GetRequiredService<TelegramService>().RespondToPingAsync((RespondToPingJob)job)
      ,
    [typeof(StartVerificationJob)] = (sp, job) =>
        sp.GetRequiredService<VerificationServiceROP>().HandleStartVerificationAsync((StartVerificationJob)job)
      ,
    [typeof(SendQuizJob)] = (sp, job) =>
        sp.GetRequiredService<TelegramService>().SendQuizAsync((SendQuizJob)job)
      ,
    [typeof(ProcessQuizCallbackJob)] = (sp, job) =>
        sp.GetRequiredService<VerificationServiceROP>().HandleCallbackAsync((ProcessQuizCallbackJob)job)
      ,
    [typeof(ChatJoinRequestJob)] = (sp, job) =>
        sp.GetRequiredService<TelegramService>().HandleChatJoinRequestAsync((ChatJoinRequestJob)job)
      ,
    [typeof(EditMessageJob)] = (sp, job) =>
        sp.GetRequiredService<TelegramService>().HandleEditMessageAsync((EditMessageJob)job)
      ,
    [typeof(RedisKeyEventJob)] = (sp, job) =>
        sp.GetRequiredService<ExpiredStateService>().HandleRedisKeyEventAsync((RedisKeyEventJob)job)
      ,
    [typeof(SendQuizCallbackJob)] = (sp, job) =>
        sp.GetRequiredService<VerificationServiceROP>().HandleSendQuizCallback((SendQuizCallbackJob)job)
      ,
    [typeof(BanUserJob)] = (sp, job) =>
        sp.GetRequiredService<TelegramService>().HnadleBanUserAsync((BanUserJob)job)
      ,
    [typeof(UnBanUserJob)] = (sp, job) =>
        sp.GetRequiredService<TelegramService>().HnadleUnBanUserAsync((UnBanUserJob)job)
      ,
    [typeof(RestrictUserJob)] = (sp, job) =>
        sp.GetRequiredService<TelegramService>().HnadleRestrictUserAsync((RestrictUserJob)job)
      ,
    [typeof(DeleteMessageJob)] = (sp, job) =>
        sp.GetRequiredService<TelegramService>().HandleDeleteMessageAsync((DeleteMessageJob)job)
      ,
    [typeof(QuizCallbackQueryJob)] = (sp, job) =>
        sp.GetRequiredService<TelegramService>().QuizCallbackQueryAsync((QuizCallbackQueryJob)job)
  };

  builder.Services.AddSingleton<IReadOnlyDictionary<Type, Func<IServiceProvider, object, Task>>>(handlers);

  builder.Services.AddSingleton<ITaskDispatcher, FunctionalTaskDispatcher>();
  builder.Services.AddHostedService(sp => (FunctionalTaskDispatcher)sp.GetRequiredService<ITaskDispatcher>());

  var redisConnectionString = builder.Configuration.GetConnectionString("Redis");

  if (string.IsNullOrEmpty(redisConnectionString)) {
    throw new Exception("Redis connection string is not configured.");
  }

  builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
      ConnectionMultiplexer.Connect(redisConnectionString)
  );
  builder.Services.AddSingleton(sp =>
      sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase());

  builder.Services.AddSingleton<IRateLimiter, RedisTokenBucketRateLimiter>();
  builder.Services.AddSingleton<AppJsonSerializerContext>();
  builder.Services.AddHostedService<RedisKeyeventListener>();
  builder.Services.AddHostedService<Worker>();

  var host = builder.Build();
  host.Run();

  return 0;
} catch (Exception ex) {
  Log.Fatal(ex, "Host terminated unexpectedly");
  return 1;
} finally {
  Log.CloseAndFlush();
}
