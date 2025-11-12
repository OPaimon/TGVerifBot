// using System;
// using System.Threading.Tasks;
// using FluentAssertions;
// using Microsoft.Extensions.Logging;
// using Microsoft.Extensions.Options;
// using Moq;
// using StackExchange.Redis;
// using TelegramVerificationBot;
// using TelegramVerificationBot.Configuration;
// using Xunit;
//
// namespace TelegramVerificationBot.Tests.Services;
//
// public class RedisRateLimiterTests {
//   // --- 依赖 ---
//   private readonly Mock<IDatabase> _redisDbMock;
//   private readonly Mock<ILogger<RedisRateLimiter>> _loggerMock;
//   private readonly IOptions<RateLimitingSettings> _settingsOptions;
//
//   // --- 被测试的对象 (System Under Test) ---
//   private readonly RedisRateLimiter _rateLimiter;
//
//   // --- 构造函数, 用于通用的 Arrange ---
//   public RedisRateLimiterTests() {
//     _redisDbMock = new Mock<IDatabase>();
//     _loggerMock = new Mock<ILogger<RedisRateLimiter>>();
//
//     var settings = new RateLimitingSettings {
//       FixedWindow = new FixedWindowSettings {
//         StartVerificationLimit = 5, // 限制5次
//         StartVerificationWindowSeconds = 60
//       }
//     };
//     _settingsOptions = Options.Create(settings);
//
//     _rateLimiter = new RedisRateLimiter(
//         _redisDbMock.Object,
//         _loggerMock.Object,
//         _settingsOptions
//     );
//   }
//
//   [Fact]
//   public async Task AllowStartVerificationAsync_WhenCountIsBelowLimit_ShouldReturnTrue() {
//     // Arrange
//     long userId = 123;
//     long chatId = 456;
//     var redisKey = $"rl:start:{userId}:{chatId}";
//
//     _redisDbMock.Setup(db => db.StringIncrementAsync(redisKey, 1, CommandFlags.None))
//                 .ReturnsAsync(3); // 返回值 < 5
//
//     // Act
//     var result = await _rateLimiter.AllowStartVerificationAsync(userId, chatId);
//
//     // Assert
//     result.Should().BeTrue();
//   }
//
//   [Fact]
//   public async Task AllowStartVerificationAsync_WhenCountIsAboveLimit_ShouldReturnFalse() {
//     // Arrange
//     long userId = 123;
//     long chatId = 456;
//     var redisKey = $"rl:start:{userId}:{chatId}";
//
//     _redisDbMock.Setup(db => db.StringIncrementAsync(redisKey, 1, CommandFlags.None))
//                 .ReturnsAsync(6); // 返回值 > 5
//
//     // Act
//     var result = await _rateLimiter.AllowStartVerificationAsync(userId, chatId);
//
//     // Assert
//     result.Should().BeFalse();
//   }
//
//   [Fact]
//   public async Task AllowStartVerificationAsync_WhenItIsFirstRequest_ShouldSetExpirationAndReturnTrue() {
//     // Arrange
//     long userId = 123;
//     long chatId = 456;
//     var redisKey = $"rl:start:{userId}:{chatId}";
//     var expectedWindow = TimeSpan.FromSeconds(60);
//
//     _redisDbMock.Setup(db => db.StringIncrementAsync(redisKey, 1, CommandFlags.None))
//                 .ReturnsAsync(1); // 返回值 == 1
//
//     // Act
//     var result = await _rateLimiter.AllowStartVerificationAsync(userId, chatId);
//
//     // Assert
//     result.Should().BeTrue();
//
//     // 验证行为: KeyExpireAsync 是否被以正确的参数调用了恰好一次
//     _redisDbMock.Verify(
//         db => db.KeyExpireAsync(redisKey, expectedWindow, It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()),
//         Times.Once
//     );
//   }
//
//   [Fact]
//   public async Task AllowStartVerificationAsync_WhenRedisThrowsException_ShouldReturnTrueAndLogWarning() {
//     // Arrange
//     long userId = 123;
//     long chatId = 456;
//     var redisKey = $"rl:start:{userId}:{chatId}";
//
//     // 设置 Mock 在被调用时抛出异常
//     _redisDbMock.Setup(db => db.StringIncrementAsync(redisKey, 1, CommandFlags.None))
//                 .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Cannot connect to Redis"));
//
//     // Act
//     var result = await _rateLimiter.AllowStartVerificationAsync(userId, chatId);
//
//     // Assert
//     // 1. 验证在异常情况下，服务依然放行
//     result.Should().BeTrue();
//
//     // 2. 验证服务记录了一条警告日志
//     _loggerMock.Verify(
//         x => x.Log(
//             LogLevel.Warning,
//             It.IsAny<EventId>(),
//             It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("RateLimiter: Redis unavailable, defaulting to allow")),
//             It.IsAny<Exception>(),
//             It.IsAny<Func<It.IsAnyType, Exception, string>>()),
//         Times.Once
//     );
//   }
// }
