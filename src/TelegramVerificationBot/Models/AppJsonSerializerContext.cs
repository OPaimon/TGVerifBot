using System.Text.Json.Serialization;

namespace TelegramVerificationBot.Models;

/// <summary>
/// Configures JSON serialization options for the application.
/// This includes settings for property naming, enum handling, and custom converters.
/// </summary>
[JsonSerializable(typeof(Quiz))]
[JsonSerializable(typeof(List<Quiz>))]
[JsonSerializable(typeof(VerificationState))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
public partial class AppJsonSerializerContext : JsonSerializerContext {
}
