namespace TelegramVerificationBot.Models;

public class VerificationState
{
    public long UserId { get; set; }
    public long ChatId { get; set; }
    public int QuizId { get; set; }
    public int OptionIndex { get; set; }

    public VerificationState(long userId, long chatId)
    {
        UserId = userId;
        ChatId = chatId;
    }
}