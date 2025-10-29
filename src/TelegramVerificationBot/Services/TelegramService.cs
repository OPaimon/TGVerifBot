using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramVerificationBot.Configuration;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Tasks;
using TL;
using WTelegram;
using Chat = WTelegram.Types.Chat;

namespace TelegramVerificationBot.Services;

/// <summary>
/// Manages all interactions with the Telegram API (both Bot API and Client API via WTelegram).
/// It listens for updates, dispatches them to the appropriate jobs, and sends messages back to users.
/// </summary>
public class TelegramService(
        ILogger<TelegramService> logger,
        ITaskDispatcher dispatcher,
        IOptions<TelegramSettings> settings,
        IConfiguration configuration) : IDisposable {
  private Bot? _bot;
  private Microsoft.Data.Sqlite.SqliteConnection? _dbConnection;

  /// <summary>
  /// Initializes the WTelegram Bot client if it hasn't been already.
  /// This is done lazily to ensure configuration is loaded.
  /// </summary>
  private void InitializeBot() {
    if (_bot != null) {
      return; // Prevent re-initialization
    }

    var sqliteConnectionString = configuration.GetConnectionString("Sqlite");
    _dbConnection = new Microsoft.Data.Sqlite.SqliteConnection(sqliteConnectionString);
    _dbConnection.Open();
    _bot = new Bot(botToken: settings.Value.BotToken, apiId: int.Parse(settings.Value.ApiId), apiHash: settings.Value.ApiHash, dbConnection: _dbConnection);
    _bot.OnMessage += OnMessage;
    _bot.OnError += async (e, s) => {
      logger.LogError(e, "WTelegram error: {State}", s);
      await Task.CompletedTask;
    };
    _bot.OnUpdate += OnUpdate;
  }

  /// <summary>
  /// Handles raw updates from WTelegram, identifying relevant events like join requests and callbacks.
  /// </summary>
  private async Task OnUpdate(WTelegram.Types.Update update) {
    switch (update) {
      case { ChatJoinRequest: not null } cjr:
        logger.LogInformation("New chat join request from user {UserId} for chat {ChatId}", cjr.ChatJoinRequest.From.Id, cjr.ChatJoinRequest.Chat.Id);
        var startVerfR = dispatcher.DispatchAsync(new StartVerificationJob(
          UserId: cjr.ChatJoinRequest.From.Id,
          ChatId: cjr.ChatJoinRequest.Chat.Id,
          UserChatId: cjr.ChatJoinRequest.From.Id,
          UserFirstName: cjr.ChatJoinRequest.From.FirstName,
          InviteLink: cjr.ChatJoinRequest.InviteLink?.InviteLink ?? string.Empty,
          ChatTitle: cjr.ChatJoinRequest.Chat.Title
        ));
        var logToTGR = dispatcher.DispatchAsync(new SendLogJob(ChatId: cjr.ChatJoinRequest.Chat.Id,
          UserId: cjr.ChatJoinRequest.From.Id, LogType.Request));

        await Task.WhenAll(startVerfR, logToTGR);
        break;

      case {
        ChatMember: {
          NewChatMember.User: { IsBot: false, Id: var userId, FirstName: var firstName },
          NewChatMember.Status: ChatMemberStatus.Member,
          Chat: { Id: var chatId, Title: var chatTitle, Type: ChatType.Group or ChatType.Supergroup},
          From: { Id: var fromId },
        }
      }
        when !ActiveStatuses.Contains(update.ChatMember.OldChatMember.Status)
        : {
          var inviterStatus = fromId != userId
            ? (await _bot!.GetChatMember(chatId, fromId)).Status
            : ChatMemberStatus.Member;
          switch (inviterStatus) {
            case ChatMemberStatus.Administrator:
            case ChatMemberStatus.Creator:
              logger.LogInformation(
                "Invitation from admin or creator for user {UserId} in chat {ChatTitle}, ignoring.",
                userId,
                chatTitle);
              break;

            default:
              logger.LogInformation(
                "Invitation from regular member for user {UserId} in chat {ChatTitle}, starting verification.",
                userId,
                chatTitle);
              var startVerfG = dispatcher.DispatchAsync(new StartVerificationJob(
                UserId: userId,
                ChatId: chatId,
                UserChatId: chatId,
                UserFirstName: firstName,
                InviteLink: string.Empty,
                ChatTitle: chatTitle
              ));
              var logToTGG = dispatcher.DispatchAsync(new SendLogJob(
                ChatId: chatId,
                UserId: userId,
                LogType.Request));
              await Task.WhenAll(startVerfG, logToTGG);
              break;
          }
          break;
        }

      // REFACTOR 1: Combine null-check into the pattern match for a clearer, more declarative style.
      case { CallbackQuery: { Data: not null, Message: not null, Id: var queryId } } cq:
        logger.LogInformation("New callback query from user {UserId} with data: {Data}", cq.CallbackQuery.From.Id, cq.CallbackQuery.Data);
        await dispatcher.DispatchAsync(new ProcessQuizCallbackJob(cq.CallbackQuery.Data, queryId, cq.CallbackQuery.From, cq.CallbackQuery.Message));
        break;

      case { CallbackQuery: not null } cq:
        // This case now specifically handles callbacks with missing data/message.
        logger.LogWarning("Received callback query with null data or message from user {UserId}", cq.CallbackQuery.From.Id);
        break;

      default:
        // logger.LogInformation("Received unhandled update type: {UpdateType}", update.GetType().Name);
        break;
    }
  }

  /// <summary>
  /// Handles incoming messages, currently only looking for "Ping" for health checks.
  /// </summary>
  private async Task OnMessage(WTelegram.Types.Message msg, UpdateType type) {
    switch (msg) {
      case WTelegram.Types.Message { Text: "/ping" } when msg.Type == MessageType.Text:
        logger.LogInformation("Received 'Ping' from chat {ChatId}", msg.Chat.Id);
        // This can be used for a simple health check.
        var pingJob = new RespondToPingJob(msg.Chat.Id);
        await dispatcher.DispatchAsync(pingJob);
        break;
      default:
        // logger.LogInformation("Received message of type {MessageType} from chat {ChatId}", msg.Type, msg.Chat.Id);
        break;
    }
  }

  public void Dispose() {
    _bot?.Dispose();
    _dbConnection?.Dispose();
  }

  /// <summary>
  /// Connects to Telegram and enters a long-running loop to listen for updates.
  /// </summary>
  internal async Task ConnectAndListenAsync(CancellationToken stoppingToken) {
    InitializeBot();
    while (!stoppingToken.IsCancellationRequested) {
      try {
        var me = await _bot!.GetMe();
        logger.LogInformation("Logged in as {User} (id {Id})", me.FirstName, me.Id);

        // WTelegram's client handles the update loop internally, so we just wait indefinitely.
        await Task.Delay(-1, stoppingToken);
      } catch (TaskCanceledException) {
        // This is expected when the application is shutting down.
        break;
      } catch (Exception ex) {
        logger.LogError(ex, "An unhandled exception occurred in the Telegram listener. The application might be unstable.");
        // Depending on the exception, a reconnect strategy could be implemented here.
        // For now, we log and let the hosting environment handle restarts if configured.
      }
    }

    logger.LogInformation("Disconnecting from Telegram...");
  }

  /// <summary>
  /// Sends a "Pong" message back to the chat where "Ping" was received.
  /// </summary>
  public async Task RespondToPingAsync(RespondToPingJob job) {
    logger.LogInformation("Responding to Ping from chat {ChatId}", job.ChatId);
    await _bot!.SendMessage(job.ChatId, "Pong");
  }

  /// <summary>
  /// Sends a quiz message to a user with inline keyboard options.
  /// </summary>
  public async Task SendQuizAsync(SendQuizJob job) {
    (long chatId, string? chatTitle, string question, long userId, string? userFirstName, long userChatId, List<OptionWithToken> optionsWithTokens) =
        (job.ChatId, job.ChatTitle, job.Question, job.UserId, job.UserFirstName, job.UserChatId, job.OptionsWithTokens);

    logger.LogInformation("Sending quiz to user {UserId} for chat {ChatId}: {QuizQuestion}", userId, chatId, question);

    var buttons = optionsWithTokens
        .Select(item => new[] { CreateQuizButton(item.Option, chatId, item.Token) }) // REFACTOR 2: Use helper
        .ToArray();

    var inlineKeyboard = new InlineKeyboardMarkup(buttons);
    var replyMessage = FormatWelcomeMessage(userFirstName, userId, chatTitle, chatId, question); // REFACTOR 3: Use helper

    var result = await _bot!.SendMessage(
      userChatId, replyMessage,
      parseMode: ParseMode.Html,
      protectContent: true,
      replyMarkup: inlineKeyboard);
    await dispatcher.DispatchAsync(new SendQuizCallbackJob(userId, chatId, result!.Id, userChatId, job.SessionId));
  }

  public async Task SendTempMsgAsync(SendTempMsgJob job) {

    var result = await _bot!.SendMessage(
      job.ChatId, job.Text,
      parseMode: ParseMode.Html,
      protectContent: true);
    logger.LogInformation("Sent message to chat {ChatId}: {Text}", job.ChatId, job.Text);

    await Task.Delay(10000);
    await _bot.DeleteMessages(job.ChatId, [result!.Id]);
    logger.LogInformation("Deleted message {MessageId} in chat {ChatId}", result.Id, job.ChatId);
  }

  public async Task SendLogAsync(SendLogJob job) {
    Chat? chat = _bot!.Chat(job.ChatId);
    var user = _bot.User(job.UserId);
    var text = $"""
                #{job.type.ToString()}
                <b>群组:</b> {chat!.Title}
                <b>群组ID:</b> #GID{-chat!.Id}
                <b>用户:</b> #UID{user!.Id}
                <b>昵称:</b> {MentionHTML(user.FirstName, user.Id)}
                <b>OPBot:</b> #bot{_bot.BotId}
                """;
    await _bot.SendMessage(-1003147968094, text, ParseMode.Html);
    logger.LogInformation("Sent Log to chat {ChatId}: {Text}", job.ChatId, job.type.ToString());
  }

  /// <summary>
  /// Answer a queryback from a quiz button press.
  /// </summary>
  public async Task QuizCallbackQueryAsync(QuizCallbackQueryJob job) {
    await _bot!.AnswerCallbackQuery(job.QueryId, job.Text, showAlert: true);
    logger.LogInformation("Answered quiz callback query {QueryId} with text: {Text}", job.QueryId, job.Text);
  }

  /// <summary>
  /// Approves or denies a user's request to join a chat.
  /// </summary>
  public async Task HandleChatJoinRequestAsync(ChatJoinRequestJob job) {
    var result = await _bot!.HideChatJoinRequest(job.ChatId, job.UserId, job.Approve);
    logger.LogInformation("Handled chat join request for user {UserId} in chat {ChatId}, approved: {Approve}", job.UserId, job.ChatId, job.Approve);
  }

  /// <summary>
  /// Edits an existing message, typically to update the status of a quiz.
  /// </summary>
  public async Task HandleEditMessageAsync(EditMessageJob job) {
    var result = await _bot!.EditMessageText(job.ChatId, job.MessageId, job.NewText);
    logger.LogInformation("Edited message {MessageId} in chat {ChatId}", job.MessageId, job.ChatId);
  }

  /// <summary>
  /// Deletes a message
  /// </summary>
  public async Task HandleDeleteMessageAsync(DeleteMessageJob job) {
    await _bot!.DeleteMessages(job.ChatId, [job.MessageId]);
    logger.LogInformation("Deleted message {MessageId} in chat {ChatId}", job.MessageId, job.ChatId);
  }

  ///<summary>
  /// Ban and then uban an user from a chat to kick them out.
  /// </summary>
  public async Task KickUserAsync(KickUserJob job) {
    var (chatId, userId) = (job.ChatId, job.UserId);
    await dispatcher.DispatchAsync(new BanUserJob(chatId, userId));
    // await Task.Delay(1000); // Wait a second to ensure the ban is processed
    // await dispatcher.DispatchAsync(new UnBanUserJob(chatId, userId, OnlyIfBanned: false));
    logger.LogInformation("Kicked user {UserId} from chat {ChatId}", userId, chatId);
  }

  /// <summary>
  /// Ban an user from a chat.
  /// </summary>
  public async Task HnadleBanUserAsync(BanUserJob job) {
    await _bot!.BanChatMember(
      job.ChatId,
      job.UserId,
      untilDate: DateTime.UtcNow.AddSeconds(60)
    );
    logger.LogInformation("Banned user {UserId} from chat {ChatId}", job.UserId, job.ChatId);
  }

  /// <summary>
  /// Unban an user from a chat.
  /// </summary>
  public async Task HnadleUnBanUserAsync(UnBanUserJob job) {
    var channel = await _bot!.InputChannel(job.ChatId);
    var user = _bot!.InputPeerUser(job.UserId);
    await _bot!.Client.Channels_EditBanned(channel, user, new ChatBannedRights { until_date = DateTime.UtcNow });
    logger.LogInformation("Unbanned user {UserId} from chat {ChatId}", job.UserId, job.ChatId);
  }

  /// <summary>
  /// Restrict an user in a chat. (At present, just prevents sending messages.)
  /// </summary>
  public async Task HnadleRestrictUserAsync(RestrictUserJob job) {
    await _bot!.RestrictChatMember(
      job.ChatId,
      job.UserId,
      job.Permissions,
      job.UntilDate
    );
    logger.LogInformation("Restricted user {UserId} in chat {ChatId}", job.UserId, job.ChatId);
  }

  // --- Helper Functions ---

  /// <summary>
  /// Formats a user mention using MarkdownV2 style for Telegram.
  /// </summary>
  public static string MentionMarkdownV2(string username, long userid) =>
      $"[{EscapeMarkdownV2(username)}](tg://user?id={userid})";

  /// <summary>
  /// Formats a user mention using HTML style for Telegram.
  /// </summary>
  public static string MentionHTML(string username, long userid) =>
      $"<a href=\"tg://user?id={userid}\">{username}</a>";

  /// <summary>
  /// Creates an inline keyboard button for a quiz option.
  /// </summary>
  private static InlineKeyboardButton CreateQuizButton(string optionText, long chatId, string token) {
    return new InlineKeyboardButton(optionText) {
      // The callback data is structured as <chatId>_<token>
      CallbackData = token
    };
  }

  /// <summary>
  /// Formats the welcome message sent to a user with a quiz question.
  /// </summary>
  private static string FormatWelcomeMessage(string? userFirstName, long userId, string? chatTitle, long chatId, string question) {

    var chatTitle_ = string.IsNullOrEmpty(chatTitle) ? chatId.ToString() : chatTitle;
    var userFirstName_ = string.IsNullOrEmpty(userFirstName) ? userId.ToString() : userFirstName;
    var mention = MentionHTML(userFirstName_, userId);
    return $"""
      <b>欢迎 {mention} 来到 {chatTitle_} ！</b>
      问题: {question}
      请在 3 分钟内完成验证，否则入群请求将被拒绝。
      <a href="https://t.me/addlist/UEpWJGzDD6A1Y2I1">点我以加入TG米游群组豪华套餐！</a>
      """;

  }

  private static readonly Regex _markdownV2EscapeRegex =
      new Regex(@"([_*\[\]()~`>#+\-=|{}.!])", RegexOptions.Compiled);

  private static string EscapeMarkdownV2(string text) {
    // 健壮性检查：处理 null 或空字符串的输入，避免异常。
    if (string.IsNullOrEmpty(text)) {
      return string.Empty;
    }

    // 使用预编译的静态 Regex 实例执行替换。
    // 替换模式 @"\$1" 会在每个匹配到的特殊字符（捕获组 1）前加上一个反斜杠 `\`。
    return _markdownV2EscapeRegex.Replace(text, @"\$1");
  }

  public static ChatPermissions FullRestrictPermissions() =>
    new() {
      CanSendMessages = false,
      CanAddWebPagePreviews = false,
      CanChangeInfo = false,
      CanInviteUsers = false,
      CanPinMessages = false,
      CanManageTopics = false,
      CanSendAudios = false,
      CanSendDocuments = false,
      CanSendPhotos = false,
      CanSendVideos = false,
      CanSendVideoNotes = false,
      CanSendVoiceNotes = false,
      CanSendPolls = false,
      CanSendOtherMessages = false
    };
  private static readonly ChatMemberStatus[] ActiveStatuses =
  [
    ChatMemberStatus.Member,
    ChatMemberStatus.Administrator,
    ChatMemberStatus.Creator,
    ChatMemberStatus.Restricted
  ];
}
