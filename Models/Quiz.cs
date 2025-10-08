namespace TelegramVerificationBot.Models;

public class Quiz
{
    public int Id { get; set; }
    public string Question { get; set; }
    public List<string> Options { get; set; }
    public int CorrectOptionIndex { get; set; }

    // Parameterless ctor required for JSON deserialization
    public Quiz()
    {
        Question = string.Empty;
        Options = new List<string>();
    }

    public Quiz(int id, string question, List<string> options, int correctOptionIndex)
    {
        Id = id;
        Question = question;
        Options = options;
        CorrectOptionIndex = correctOptionIndex;
    }
}

public record OptionWithToken(string Option, string Token);