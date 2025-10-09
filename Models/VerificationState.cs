namespace TelegramVerificationBot.Models;

// public class VerificationStateClass
// {
//     public long UserId { get; set; }
//     public long ChatId { get; set; }

//     public VerificationState(long userId, long chatId)
//     {
//         UserId = userId;
//         ChatId = chatId;
//     }
// }

public record VerificationState(long UserId, long ChatId);