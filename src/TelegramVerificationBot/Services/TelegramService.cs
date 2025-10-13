using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramVerificationBot.Configuration;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Tasks;
using TL;
using WTelegram;

namespace TelegramVerificationBot;

/// <summary>
/// Manages all interactions with the Telegram API (both Bot API and Client API via WTelegram).
/// It listens for updates, dispatches them to the appropriate jobs, and sends messages back to users.
/// </summary>
public class TelegramService(
        ILogger<TelegramService> logger,
        ITaskDispatcher dispatcher,
        IOptions<TelegramSettings> settings,
        IConfiguration configuration) : IDisposable
{
    private Bot? _bot;
    private Microsoft.Data.Sqlite.SqliteConnection? _dbConnection;

    /// <summary>
    /// Initializes the WTelegram Bot client if it hasn't been already.
    /// This is done lazily to ensure configuration is loaded.
    /// </summary>
    private void InitializeBot()
    {
        if (_bot != null) return; // Prevent re-initialization

        var sqliteConnectionString = configuration.GetConnectionString("Sqlite");
        _dbConnection = new Microsoft.Data.Sqlite.SqliteConnection(sqliteConnectionString);
        _dbConnection.Open();
        _bot = new Bot(botToken: settings.Value.BotToken, apiId: int.Parse(settings.Value.ApiId), apiHash: settings.Value.ApiHash, dbConnection: _dbConnection);
        _bot.OnMessage += OnMessage;
        _bot.OnError += async (e, s) =>
        {
            logger.LogError(e, "WTelegram error: {State}", s);
            await Task.CompletedTask;
        };
        _bot.OnUpdate += OnUpdate;
    }

    /// <summary>
    /// Handles raw updates from WTelegram, identifying relevant events like join requests and callbacks.
    /// </summary>
    private async Task OnUpdate(WTelegram.Types.Update update)
    {
        switch (update)
        {
            case { ChatJoinRequest: not null } cjr:
                logger.LogInformation("New chat join request from user {UserId} for chat {ChatId}", cjr.ChatJoinRequest.From.Id, cjr.ChatJoinRequest.Chat.Id);
                await dispatcher.DispatchAsync(new StartVerificationJob(cjr.ChatJoinRequest));
                break;
            
            // REFACTOR 1: Combine null-check into the pattern match for a clearer, more declarative style.
            case { CallbackQuery: { Data: not null, Message: not null } } cq:
                logger.LogInformation("New callback query from user {UserId} with data: {Data}", cq.CallbackQuery.From.Id, cq.CallbackQuery.Data);
                await dispatcher.DispatchAsync(new ProcessQuizCallbackJob(cq.CallbackQuery.Data, cq.CallbackQuery.From, cq.CallbackQuery.Message));
                break;

            case { CallbackQuery: not null } cq:
                // This case now specifically handles callbacks with missing data/message.
                logger.LogWarning("Received callback query with null data or message from user {UserId}", cq.CallbackQuery.From.Id);
                break;

            default:
                logger.LogInformation("Received unhandled update type: {UpdateType}", update.GetType().Name);
                break;
        }
    }

    /// <summary>
    /// Handles incoming messages, currently only looking for "Ping" for health checks.
    /// </summary>
    private async Task OnMessage(WTelegram.Types.Message msg, UpdateType type)
    {
        switch (msg)
        {
            case WTelegram.Types.Message { Text: "/ping" } when msg.Type == MessageType.Text:
                logger.LogInformation("Received 'Ping' from chat {ChatId}", msg.Chat.Id);
                // This can be used for a simple health check.
                var pingJob = new RespondToPingJob(msg.Chat.Id);
                await dispatcher.DispatchAsync(pingJob);
                break;
            default:
                logger.LogInformation("Received message of type {MessageType} from chat {ChatId}", msg.Type, msg.Chat.Id);
                break;
        }
    }

    public void Dispose()
    {
        _bot?.Dispose();
        _dbConnection?.Dispose();
    }

    /// <summary>
    /// Connects to Telegram and enters a long-running loop to listen for updates.
    /// </summary>
    internal async Task ConnectAndListenAsync(CancellationToken stoppingToken)
    {
        InitializeBot();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var me = await _bot!.GetMe();
                logger.LogInformation("Logged in as {User} (id {Id})", me.FirstName, me.Id);

                // WTelegram's client handles the update loop internally, so we just wait indefinitely.
                await Task.Delay(-1, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // This is expected when the application is shutting down.
                break;
            }
            catch (Exception ex)
            {
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
    public async Task RespondToPingAsync(RespondToPingJob job)
    {
        logger.LogInformation("Responding to Ping from chat {ChatId}", job.ChatId);
        await _bot!.SendMessage(job.ChatId, "Pong");
    }

    /// <summary>
    /// Sends a quiz message to a user with inline keyboard options.
    /// </summary>
    public async Task SendQuizAsync(SendQuizJob job)
    {
        var (chat, question, user, userChatId, optionsWithTokens) = 
            (job.Chat, job.Question, job.User, job.UserChatId, job.OptionsWithTokens);
        
        logger.LogInformation("Sending quiz to user {UserId} for chat {ChatId}: {QuizQuestion}", user.Id, chat.Id, question);

        var buttons = optionsWithTokens
            .Select(item => new[] { CreateQuizButton(item.Option, chat.Id, item.Token) }) // REFACTOR 2: Use helper
            .ToArray();

        var inlineKeyboard = new InlineKeyboardMarkup(buttons);
        var replyMessage = FormatWelcomeMessage(user, chat, question); // REFACTOR 3: Use helper

        var result = await _bot!.SendMessage(userChatId, replyMessage, parseMode: ParseMode.MarkdownV2, replyMarkup: inlineKeyboard);
        await dispatcher.DispatchAsync(new SendQuizCallbackJob(user.Id, chat.Id, result!.Id, userChatId));
    }

    /// <summary>
    /// Approves or denies a user's request to join a chat.
    /// </summary>
    public async Task HandleChatJoinRequestAsync(ChatJoinRequestJob job)
    {
        var chat = await _bot!.GetChat(job.Chat);
        // var user = job.User;
        var user = _bot.User(job.User);
        if (user == null)
        {
            logger.LogError("Failed to retrieve user {UserId} for chat join request in chat {ChatId}", job.User, job.Chat);
            return;
        }
        var result = await _bot.Client.Messages_HideChatJoinRequest(chat, user.TLUser(), job.Approve);
        logger.LogInformation("Handled chat join request for user {UserId} in chat {ChatId}, approved: {Approve}", user.Id, chat.Id, job.Approve);
    }

    /// <summary>
    /// Edits an existing message, typically to update the status of a quiz.
    /// </summary>
    public async Task HandleEditMessageAsync(EditMessageJob job)
    {
        var result = await _bot!.EditMessageText(job.ChatId, job.MessageId, job.NewText);
        logger.LogInformation("Edited message {MessageId} in chat {ChatId}", job.MessageId, job.ChatId);
    }
    
    // --- Helper Functions ---

    /// <summary>
    /// Formats a user mention using MarkdownV2 style for Telegram.
    /// </summary>
    public static string MentionMarkdownV2(string username, long userid) =>
        $"[{EscapeMarkdownV2(username)}](tg://user?id={userid})";

    /// <summary>
    /// Creates an inline keyboard button for a quiz option.
    /// </summary>
    private static InlineKeyboardButton CreateQuizButton(string optionText, long chatId, string token)
    {
        return new InlineKeyboardButton(optionText)
        {
            // The callback data is structured as <chatId>_<token>
            CallbackData = $"{chatId}_{token}"
        };
    }

    /// <summary>
    /// Formats the welcome message sent to a user with a quiz question.
    /// </summary>
    private static string FormatWelcomeMessage(Telegram.Bot.Types.User user, Telegram.Bot.Types.Chat chat, string question)
    {
        var chatTitle = string.IsNullOrEmpty(chat.Title) ? chat.Id.ToString() : chat.Title;
        var mention = MentionMarkdownV2(EscapeMarkdownV2(user.FirstName), user.Id);
        return $"*欢迎 {mention} 来到 {chatTitle} ！* \n问题: {EscapeMarkdownV2(question)}";
    }

    private static readonly Regex MarkdownV2EscapeRegex =
        new Regex(@"([_*\[\]()~`>#+\-=|{}.!])", RegexOptions.Compiled);
        
    private static string EscapeMarkdownV2(string text)
    {
        // 健壮性检查：处理 null 或空字符串的输入，避免异常。
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        
        // 使用预编译的静态 Regex 实例执行替换。
        // 替换模式 @"\$1" 会在每个匹配到的特殊字符（捕获组 1）前加上一个反斜杠 `\`。
        return MarkdownV2EscapeRegex.Replace(text, @"\$1");
    }
}