
using StackExchange.Redis;
using TelegramVerificationBot;
using TelegramVerificationBot.Configuration;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Services;
using TelegramVerificationBot.Tasks;



var builder = Host.CreateApplicationBuilder(args);
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

// Register rate limiter
builder.Services.AddSingleton<IRateLimiter, RedisTokenBucketRateLimiter>();

builder.Services.AddSingleton<AppJsonSerializerContext>();

builder.Services.AddHostedService<RedisKeyeventListener>();
builder.Services.AddHostedService<Worker>();


var host = builder.Build();
host.Run();
