using System.Text.Json;
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

// CSharpFunctionalExtensions.FluentAssertions using 已被移除

namespace TelegramVerificationBot.Tests;

public class VerificationServiceROPTests {
  #region Setup

  private readonly Mock<ILogger<VerificationServiceROP>> _loggerMock;
  private readonly Mock<ITaskDispatcher> _dispatcherMock;
  private readonly Mock<IDatabase> _redisDbMock;
  private readonly Mock<IQuizService> _quizServiceMock;
  private readonly AppJsonSerializerContext _jsonContext;
  private readonly VerificationServiceROP _sut; // System Under Test

  public VerificationServiceROPTests() {
    _loggerMock = new Mock<ILogger<VerificationServiceROP>>();
    _dispatcherMock = new Mock<ITaskDispatcher>();
    _redisDbMock = new Mock<IDatabase>();
    _quizServiceMock = new Mock<IQuizService>();

    _jsonContext = new AppJsonSerializerContext(new JsonSerializerOptions());

    _sut = new VerificationServiceROP(
        _loggerMock.Object,
        _dispatcherMock.Object,
        _redisDbMock.Object,
        _quizServiceMock.Object,
        _jsonContext
    );
  }

  #endregion

  #region Pure Function: PrepareQuiz Tests

  [Fact]
  public void PrepareQuiz_ShouldSucceed_WhenQuizServiceReturnsAQuiz() {
    // Arrange
    var quiz = new Quiz(
        Id: 1,
        Question: "What is 2+2?",
        Options: ["3", "4", "5"],
        CorrectOptionIndex: 1
    );
    _quizServiceMock.Setup(s => s.GetRandomQuiz()).Returns(quiz);

    // Act
    Result<VerificationServiceROP.VerificationQuizData, VerificationError> result = _sut.PrepareQuiz();

    // Assert
    result.IsSuccess.Should().BeTrue();

    var quizData = result.Value;
    quizData.Question.Should().Be(quiz.Question);
    quizData.OptionsWithTokens.Should().HaveCount(quiz.Options.Count);
    quizData.OptionsWithTokens.Select(o => o.Option).Should().BeEquivalentTo(quiz.Options);

    var originalCorrectOptionText = quiz.Options[quiz.CorrectOptionIndex];
    var correctOptionWithToken = quizData.OptionsWithTokens.Single(o => o.Token == quizData.CorrectToken);
    correctOptionWithToken.Option.Should().Be(originalCorrectOptionText);

    foreach (var option in quizData.OptionsWithTokens) {
      Guid.TryParse(option.Token, out _).Should().BeTrue();
    }
  }

  [Fact]
  public void PrepareQuiz_ShouldFail_WhenQuizServiceReturnsNoQuiz() {
    // Arrange
    _quizServiceMock.Setup(s => s.GetRandomQuiz()).Returns((Quiz?)null);

    // Act
    Result<VerificationServiceROP.VerificationQuizData, VerificationError> result = _sut.PrepareQuiz();

    // Assert
    result.IsFailure.Should().BeTrue();
    result.Error.Kind.Should().Be(VerificationErrorKind.NoQuizzesAvailable);
    result.Error.Message.Should().Be("No quizzes available.");
  }

  #endregion

  #region Pure Function: ParseCallbackData Tests

  [Fact]
  public void ParseCallbackData_WithValidData_ShouldSucceedAndReturnCorrectInfo() {
    // Arrange
    long expectedChatId = -100123456789;
    string expectedToken = Guid.NewGuid().ToString();
    string callbackData = $"{expectedChatId}_{expectedToken}";

    // Act
    var result = _sut.ParseCallbackData(callbackData);

    // Assert
    result.IsSuccess.Should().BeTrue();
    result.Value.ChatId.Should().Be(expectedChatId);
    result.Value.Token.Should().Be(expectedToken);
  }

  [Theory]
  [InlineData("invalid_format")]
  [InlineData("12345")]
  [InlineData("_sometoken")]
  [InlineData("not_a_long_sometoken")]
  [InlineData("")]
  public void ParseCallbackData_WithInvalidFormat_ShouldReturnFailure(string invalidCallbackData) {
    // Arrange & Act
    var result = _sut.ParseCallbackData(invalidCallbackData);

    // Assert
    result.IsFailure.Should().BeTrue();
    result.Error.Kind.Should().Be(VerificationErrorKind.InvalidCallbackData);
  }

  [Fact]
  public void ParseCallbackData_WithNullInput_ShouldThrowArgumentNullException() {
    // Arrange
    Action act = () => _sut.ParseCallbackData(null!); // 使用 ! 来告诉编译器我们故意这么做

    // Assert
    act.Should().Throw<ArgumentNullException>()
       .WithParameterName("callbackData"); // 进一步断言是哪个参数出的问题
  }

  #endregion

  #region Pure Function: DeserializeState Tests

  [Fact]
  public void DeserializeState_WithValidJson_ShouldSucceed() {
    // Arrange
    var expectedState = new VerificationState(UserId: 12345, ChatId: 67890);
    var stateJson = JsonSerializer.Serialize(expectedState, _jsonContext.VerificationState);

    // Act
    var result = _sut.DeserializeState(stateJson);

    // Assert
    result.IsSuccess.Should().BeTrue();
    result.Value.Should().Be(expectedState);
  }

  [Fact]
  public void DeserializeState_WithInvalidJson_ShouldFail() {
    // Arrange
    var invalidJson = "{\"UserId\":123, \"ChatId\":\"not_a_long\"}";

    // Act
    var result = _sut.DeserializeState(invalidJson);

    // Assert
    result.IsFailure.Should().BeTrue();
    result.Error.Kind.Should().Be(VerificationErrorKind.StateDeserializationFailed);
  }

  [Fact]
  public void DeserializeState_WithNullJson_ShouldFail() {
    // Arrange
    var nullJson = "null";

    // Act
    var result = _sut.DeserializeState(nullJson);

    // Assert
    result.IsFailure.Should().BeTrue();
    result.Error.Kind.Should().Be(VerificationErrorKind.StateDeserializationFailed);
    result.Error.Message.Should().Be("Deserialized state is null.");
  }

  #endregion

  #region Pure Function: ValidateState Tests

  [Fact]
  public void ValidateState_WithMatchingUserAndChat_ShouldSucceed() {
    // Arrange
    long userId = 12345;
    long chatId = 67890;
    var user = new User { Id = userId };
    var state = new VerificationState(UserId: userId, ChatId: chatId);

    // Act
    var result = _sut.ValidateState(state, user, chatId);

    // Assert
    result.IsSuccess.Should().BeTrue();
    result.Value.Should().Be(state);
  }

  [Fact]
  public void ValidateState_WithMismatchedUserId_ShouldFail() {
    // Arrange
    long stateUserId = 12345;
    long actualUserId = 99999; // Mismatch
    long chatId = 67890;
    var user = new User { Id = actualUserId };
    var state = new VerificationState(UserId: stateUserId, ChatId: chatId);

    // Act
    var result = _sut.ValidateState(state, user, chatId);

    // Assert
    result.IsFailure.Should().BeTrue();
    result.Error.Kind.Should().Be(VerificationErrorKind.StateValidationFailed);
  }

  [Fact]
  public void ValidateState_WithMismatchedChatId_ShouldFail() {
    // Arrange
    long userId = 12345;
    long stateChatId = 67890;
    long actualChatId = 88888; // Mismatch
    var user = new User { Id = userId };
    var state = new VerificationState(UserId: userId, ChatId: stateChatId);

    // Act
    var result = _sut.ValidateState(state, user, actualChatId);

    // Assert
    result.IsFailure.Should().BeTrue();
    result.Error.Kind.Should().Be(VerificationErrorKind.StateValidationFailed);
  }

  #endregion
}
