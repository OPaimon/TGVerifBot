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

public class VerificationServiceTests {
  #region Setup

  private readonly Mock<ILogger<VerificationService>> _loggerMock;
  private readonly Mock<ITaskDispatcher> _dispatcherMock;
  private readonly Mock<IDatabase> _redisDbMock;
  private readonly Mock<IQuizService> _quizServiceMock;
  private readonly AppJsonSerializerContext _jsonContext;
  private readonly VerificationService _sut; // System Under Test

  public VerificationServiceTests() {
    _loggerMock = new Mock<ILogger<VerificationService>>();
    _dispatcherMock = new Mock<ITaskDispatcher>();
    _redisDbMock = new Mock<IDatabase>();
    _quizServiceMock = new Mock<IQuizService>();

    _jsonContext = new AppJsonSerializerContext(new JsonSerializerOptions());

    _sut = new VerificationService(
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
    Result<VerificationService.VerificationQuizData, VerificationError> result = _sut.PrepareQuiz();

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
    Result<VerificationService.VerificationQuizData, VerificationError> result = _sut.PrepareQuiz();

    // Assert
    result.IsFailure.Should().BeTrue();
    result.Error.Kind.Should().Be(VerificationErrorKind.NoQuizzesAvailable);
    result.Error.Message.Should().Be("No quizzes available.");
  }

  #endregion
}
