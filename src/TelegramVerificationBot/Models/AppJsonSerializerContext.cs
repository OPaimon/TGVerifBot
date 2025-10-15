using System.Text.Json.Serialization;

namespace TelegramVerificationBot.Models;

/// <summary>
/// Configures JSON serialization options for the application.
/// This includes settings for property naming, enum handling, and custom converters.
/// </summary>
[JsonSerializable(typeof(Quiz))]
[JsonSerializable(typeof(List<Quiz>))]
[JsonSerializable(typeof(VerificationState))]
[JsonSerializable(typeof(BVerificationState))]
[JsonSerializable(typeof(VerificationSession))]
[JsonSerializable(typeof(OptionWithToken))]
[JsonSerializable(typeof(CleanupTask))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
public partial class AppJsonSerializerContext : JsonSerializerContext {
}
public record Quiz(int Id, string Question, List<string> Options, int CorrectOptionIndex);

public record OptionWithToken(string Option, string Token);

public record VerificationState(long UserId, long ChatId);

public record BVerificationState(int MessageId, long MessageChatId, bool IsInChat);
public record VerificationSession(
    string SessionId,
    long UserId,
    long TargetChatId,
    VerificationContextType ContextType,
    string CorrectToken,
    List<OptionWithToken> OptionsWithTokens,
    int? VerificationMessageId = null
);

public record CleanupTask(int MessageId, long MessageChatId);

public enum VerificationContextType {
  InGroupRestriction,
  JoinRequest
}
