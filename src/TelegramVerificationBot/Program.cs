
using Serilog;
using Serilog.Events;
using StackExchange.Redis;
using TelegramVerificationBot;
using TelegramVerificationBot.Configuration;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Services;
using WTelegram;




try {
  Log.Information("Starting application host");

  HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
  string logsPath = builder.Configuration.GetSection("PathSettings")?.GetValue<string>("LogPath") ?? "logs";

  // Configure Serilog logger programmatically
  Log.Logger = new LoggerConfiguration()
      .MinimumLevel.Information()
      .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
      .MinimumLevel.Override("System", LogEventLevel.Warning)
      .MinimumLevel.Override("WTelegram", LogEventLevel.Warning) // Filter WTelegram logs
      .Enrich.FromLogContext()
      .WriteTo.Console()
      .WriteTo.File($"{logsPath}/bot-.log",
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

  //     0 => LogLevel.Trace,
  //     1 => LogLevel.Debug,
  //     2 => LogLevel.Information,
  //     3 => LogLevel.Warning,
  //     4 => LogLevel.Error,
  //     5 => LogLevel.Critical,
  //     _ => LogLevel.None

  // Replace default logging with Serilog
  builder.Logging.ClearProviders();
  builder.Logging.AddSerilog();

  // Your existing service configurations
  builder.Services.AddOptions<TelegramSettings>()
      .BindConfiguration("TelegramSettings");

  builder.Services.AddOptions<RateLimitingSettings>()
      .BindConfiguration("RateLimiting");

  builder.Services.AddOptions<TplDataflowOptions>()
    .BindConfiguration("TaskDispatcher:TplDataflow");

  builder.Services.AddOptions<TaskDispatcherSettings>()
    .BindConfiguration("TaskDispatcher");

  builder.Services.AddSingleton<TelegramService>();
  builder.Services.AddSingleton<VerificationService>();
  builder.Services.AddQuizService();
  builder.Services.AddSingleton<ExpiredStateService>();



  builder.Services.AddSingleton<DataflowPipelineBuilder>();
  builder.Services.AddSingleton<DataflowTaskDispatcher>();
  builder.Services.AddSingleton<ITaskDispatcher>(sp =>
    sp.GetRequiredService<DataflowTaskDispatcher>());
  builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<DataflowTaskDispatcher>());
  // builder.Services.AddSingleton<ITaskDispatcher, FunctionalTaskDispatcher>();
  // builder.Services.AddHostedService(sp => (FunctionalTaskDispatcher)sp.GetRequiredService<ITaskDispatcher>());

  var redisConnectionString = builder.Configuration.GetConnectionString("Redis");

  if (string.IsNullOrEmpty(redisConnectionString)) {
    throw new Exception("Redis connection string is not configured.");
  }

  builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
      ConnectionMultiplexer.Connect(redisConnectionString)
  );
  builder.Services.AddSingleton(sp =>
      sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase());

  builder.Services.AddSingleton<IRateLimiter, RedisTokenBucketRateLimiter>();
  builder.Services.AddSingleton<AppJsonSerializerContext>();
  builder.Services.AddHostedService<RedisKeyeventListener>();
  builder.Services.AddHostedService<CleanupWorkerService>();
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
