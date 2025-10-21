using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Services;
using Chat = Telegram.Bot.Types.Chat;
using Message = Telegram.Bot.Types.Message;
using User = Telegram.Bot.Types.User;

namespace TelegramVerificationBot.Tasks;

public interface IJob {
  Task ProcessAsync(IServiceProvider serviceProvider);
}

public interface IKeyedJob {
  long UserId { get; }
  long ChatId { get; }
}

public record ChatJoinRequestJob(long UserId, long ChatId, bool Approve) : IKeyedJob, IJob {
  public Task ProcessAsync(IServiceProvider sp) =>
      sp.GetRequiredService<TelegramService>().HandleChatJoinRequestAsync(this);
}
public record EditMessageJob(long ChatId, int MessageId, string NewText) : IJob {
  public Task ProcessAsync(IServiceProvider sp) =>
      sp.GetRequiredService<TelegramService>().HandleEditMessageAsync(this);
}
public record ProcessQuizCallbackJob(string CallbackData, string QueryId, User User, Message Message) : IJob {
  public Task ProcessAsync(IServiceProvider sp) =>
      sp.GetRequiredService<VerificationService>().HandleCallbackAsync(this);
}

public record RespondToPingJob(long ChatId) : IJob {
  public Task ProcessAsync(IServiceProvider sp) =>
      sp.GetRequiredService<TelegramService>().RespondToPingAsync(this);
}
public record SendQuizJob(long ChatId, string? ChatTitle, string Question, long UserId, string UserFirstName, long UserChatId, List<OptionWithToken> OptionsWithTokens, string? SessionId = null) : IKeyedJob, IJob {
  public Task ProcessAsync(IServiceProvider sp) =>
      sp.GetRequiredService<TelegramService>().SendQuizAsync(this);
}
public record SendQuizCallbackJob(long UserId, long ChatId, int MessageId, long MessageChatId, string? SessionId = null) : IKeyedJob, IJob {
  public Task ProcessAsync(IServiceProvider sp) =>
      sp.GetRequiredService<VerificationService>().HandleSendQuizCallback(this);
}
// public record StartVerificationJob(ChatJoinRequest Requester);
public record StartVerificationJob(long UserId, long ChatId, long UserChatId, string UserFirstName, string InviteLink, string? ChatTitle) : IKeyedJob, IJob {
  public Task ProcessAsync(IServiceProvider sp) =>
      sp.GetRequiredService<VerificationService>().HandleStartVerificationAsync(this);
}
public record BanUserJob(long ChatId, long UserId) : IKeyedJob, IJob {
  public Task ProcessAsync(IServiceProvider sp) =>
      sp.GetRequiredService<TelegramService>().HnadleBanUserAsync(this);
}
public record UnBanUserJob(long ChatId, long UserId, bool OnlyIfBanned) : IKeyedJob, IJob {
  public Task ProcessAsync(IServiceProvider sp) =>
      sp.GetRequiredService<TelegramService>().HnadleUnBanUserAsync(this);
}
public record RestrictUserJob(long ChatId, long UserId, ChatPermissions Permissions, DateTime? UntilDate) : IKeyedJob, IJob {
  public Task ProcessAsync(IServiceProvider sp) =>
      sp.GetRequiredService<TelegramService>().HnadleRestrictUserAsync(this);
}
public record DeleteMessageJob(int MessageId, long ChatId) : IJob {
  public Task ProcessAsync(IServiceProvider sp) =>
      sp.GetRequiredService<TelegramService>().HandleDeleteMessageAsync(this);
}
public record QuizCallbackQueryJob(string QueryId, string Text) : IJob {
  public Task ProcessAsync(IServiceProvider sp) =>
      sp.GetRequiredService<TelegramService>().QuizCallbackQueryAsync(this);
}
public record KickUserJob(long ChatId, long UserId) : IKeyedJob, IJob {
  public Task ProcessAsync(IServiceProvider sp) =>
      sp.GetRequiredService<TelegramService>().KickUserAsync(this);
}
public record SendTempMsgJob(long ChatId, string Text) : IJob {
  public Task ProcessAsync(IServiceProvider sp) =>
      sp.GetRequiredService<TelegramService>().SendTempMsgAsync(this);
}

public record ProcessVerificationTimeoutJob(VerificationSession session) : IKeyedJob, IJob {
  public long UserId { get; } = session.UserId;
  public long ChatId { get; } = session.TargetChatId;
  public Task ProcessAsync(IServiceProvider sp) =>
    sp.GetRequiredService<VerificationService>().HandleTimeoutJob(this);

}

public record SendLogJob(long ChatId,long UserId, LogType type) : IKeyedJob, IJob {
  public Task ProcessAsync(IServiceProvider sp) =>
    sp.GetRequiredService<TelegramService>().SendLogAsync(this);

}
