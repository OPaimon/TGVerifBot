namespace TelegramVerificationBot.Models;

public record Quiz(int Id, string Question, List<string> Options, int CorrectOptionIndex);

public record OptionWithToken(string Option, string Token);
