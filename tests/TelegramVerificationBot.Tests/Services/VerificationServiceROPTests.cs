using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using CSharpFunctionalExtensions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using Telegram.Bot.Types;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Services;
using TelegramVerificationBot.Tasks;
using Xunit;

namespace TelegramVerificationBot.Tests.Services;

public class VerificationServiceROPTests {
  // --- Mocks for all dependencies ---
  private readonly Mock<ILogger<VerificationServiceROP>> _loggerMock;
  private readonly Mock<ITaskDispatcher> _dispatcherMock;
  private readonly Mock<IDatabase> _redisDbMock;
  private readonly Mock<IQuizService> _quizServiceMock;
  private readonly AppJsonSerializerContext _jsonContext;

  // --- The Service Under Test ---
  private readonly VerificationServiceROP _service;

  public VerificationServiceROPTests() {
    _loggerMock = new Mock<ILogger<VerificationServiceROP>>();
    _dispatcherMock = new Mock<ITaskDispatcher>();
    _redisDbMock = new Mock<IDatabase>();
    _quizServiceMock = new Mock<IQuizService>();

    _jsonContext = new AppJsonSerializerContext(new JsonSerializerOptions());

    _service = new VerificationServiceROP(
        _loggerMock.Object,
        _dispatcherMock.Object,
        _redisDbMock.Object,
        _quizServiceMock.Object,
        _jsonContext
    );
  }

  // Test Case 1: Happy Path
  [Fact]
  public async Task HandleStartVerificationAsync_WhenUserIsNewAndQuizIsAvailable_ShouldDispatchSendQuizJob() {
    // Arrange
    var user = new User { Id = 123, FirstName = "Test" };
    var chat = new Chat { Id = 456, Title = "Test Chat" };
    var job = new StartVerificationJob(new ChatJoinRequest { From = user, Chat = chat, UserChatId = 789 });

    var quiz = new Quiz(1, "Question?", new List<string> { "A", "B" }, 0);

    _redisDbMock.Setup(db => db.KeyExistsAsync($"user_status:{user.Id}:{chat.Id}", CommandFlags.None))
                .ReturnsAsync(false);

    _quizServiceMock.Setup(qs => qs.GetRandomQuiz()).Returns(Maybe<Quiz>.From(quiz));

    _redisDbMock.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);

    // Act
    await _service.HandleStartVerificationAsync(job);

    // Assert
    _dispatcherMock.Verify(
        d => d.DispatchAsync(It.Is<SendQuizJob>(j => j.User.Id == user.Id && j.Chat.Id == chat.Id)),
        Times.Once
    );
  }

  // Test Case 2: Failure Path
  [Fact]
  public async Task HandleStartVerificationAsync_WhenUserIsAlreadyPending_ShouldNotDispatchAnyJob() {
    // Arrange
    var user = new User { Id = 123, FirstName = "Test" };
    var chat = new Chat { Id = 456, Title = "Test Chat" };
    var job = new StartVerificationJob(new ChatJoinRequest { From = user, Chat = chat, UserChatId = 789 });

    _redisDbMock.Setup(db => db.KeyExistsAsync($"user_status:{user.Id}:{chat.Id}", CommandFlags.None))
                .ReturnsAsync(true);

    // Act
    await _service.HandleStartVerificationAsync(job);

    // Assert
    _dispatcherMock.Verify(
        d => d.DispatchAsync(It.IsAny<SendQuizJob>()),
        Times.Never
    );
  }

  // Test Case 3: Callback Happy Path
  [Fact]
  public async Task HandleCallbackAsync_WhenAnswerIsCorrect_ShouldApproveJoinRequestAndEditMessage() {
    // Arrange
    var user = new User { Id = 123, FirstName = "Test" };
    var message = new Message { Id = 555, Chat = new Chat { Id = 789 } };
    long chatId = 456;
    string correctToken = "test-token";
    var job = new ProcessQuizCallbackJob($"{chatId}_{correctToken}", user, message);

    var verificationState = new VerificationState(user.Id, chatId);
    var stateJson = JsonSerializer.Serialize(verificationState, _jsonContext.VerificationState);

    // 1. Mock user status check to show user is pending
    _redisDbMock.Setup(db => db.StringGetAsync($"user_status:{user.Id}:{chatId}", CommandFlags.None))
                .ReturnsAsync(new RedisValue("some-pending-value"));

    // 2. Mock token check to return the correct state, simulating a correct answer
    _redisDbMock.Setup(db => db.StringGetDeleteAsync($"verification_token:{correctToken}", CommandFlags.None))
                .ReturnsAsync(new RedisValue(stateJson));

    // 3. Mock user status cleanup
    _redisDbMock.Setup(db => db.KeyDeleteAsync($"user_status:{user.Id}:{chatId}", CommandFlags.None))
                .ReturnsAsync(true);

    // Act
    await _service.HandleCallbackAsync(job);

    // Assert
    // 1. Verify that the join request was APPROVED
    _dispatcherMock.Verify(
        d => d.DispatchAsync(It.Is<ChatJoinRequestJob>(j => j.User == user.Id && j.Chat == chatId && j.Approve)),
        Times.Once
    );

    // 2. Verify that the message was edited with a success text
    _dispatcherMock.Verify(
        d => d.DispatchAsync(It.Is<EditMessageJob>(j => j.MessageId == message.Id && j.NewText.Contains("验证通过"))),
        Times.Once
    );
  }
}
