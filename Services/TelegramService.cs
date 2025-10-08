using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
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
public class TelegramService : IDisposable
{
    private Bot? _bot;
    private Microsoft.Data.Sqlite.SqliteConnection? _dbConnection;
    private readonly FunctionalTaskDispatcher _dispatcher;
    private readonly ILogger<TelegramService> _logger;
    private readonly IOptions<TelegramSettings> _settings;
    private readonly IConfiguration _configuration;

    public TelegramService(
        ILogger<TelegramService> logger,
        FunctionalTaskDispatcher dispatcher,
        IOptions<TelegramSettings> settings,
        IConfiguration configuration)
    {
        _logger = logger;
        _dispatcher = dispatcher;
        _settings = settings; // Save the configuration
        _configuration = configuration;
    }

    /// <summary>
    /// Initializes the WTelegram Bot client if it hasn't been already.
    /// This is done lazily to ensure configuration is loaded.
    /// </summary>
    private void InitializeBot()
    {
        if (_bot != null) return; // Prevent re-initialization

        var sqliteConnectionString = _configuration.GetConnectionString("Sqlite");
        _dbConnection = new Microsoft.Data.Sqlite.SqliteConnection(sqliteConnectionString);
        _dbConnection.Open();
        _bot = new Bot(botToken: _settings.Value.BotToken, apiId: int.Parse(_settings.Value.ApiId), apiHash: _settings.Value.ApiHash, dbConnection: _dbConnection);
        _bot.OnMessage += OnMessage;
        _bot.OnError += async (e, s) =>
        {
            _logger.LogError(e, "WTelegram error: {State}", s);
            await Task.CompletedTask;
        };
        _bot.OnUpdate += OnUpdate;
    }

    /// <summary>
    /// Handles raw updates from WTelegram, identifying relevant events like join requests and callbacks.
    /// </summary>
    private async Task OnUpdate(WTelegram.Types.Update update)
    {
        if (update.ChatJoinRequest is not null)
        {
            _logger.LogInformation("New chat join request from user {UserId} for chat {ChatId}", update.ChatJoinRequest.From.Id, update.ChatJoinRequest.Chat.Id);
            await _dispatcher.DispatchAsync(new StartVerificationJob(update.ChatJoinRequest));
            return;
        }
        else if (update.CallbackQuery is not null)
        {
            _logger.LogInformation("New callback query from user {UserId} with data: {Data}", update.CallbackQuery.From.Id, update.CallbackQuery.Data);
            await _dispatcher.DispatchAsync(new ProcessQuizCallbackJob(update.CallbackQuery.Data, update.CallbackQuery.From, update.CallbackQuery.Message));
            return;
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
                _logger.LogInformation("Received 'Ping' from chat {ChatId}", msg.Chat.Id);
                // This can be used for a simple health check.
                var pingJob = new RespondToPingJob(msg.Chat);
                await _dispatcher.DispatchAsync(pingJob);
                break;
            default:
                _logger.LogInformation("Received message of type {MessageType} from chat {ChatId}", msg.Type, msg.Chat.Id);
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
                var me = await _bot.GetMe();
                _logger.LogInformation("Logged in as {User} (id {Id})", me.FirstName, me.Id);

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
                 _logger.LogError(ex, "An unhandled exception occurred in the Telegram listener. The application might be unstable.");
                 // Depending on the exception, a reconnect strategy could be implemented here.
                 // For now, we log and let the hosting environment handle restarts if configured.
            }
        }

        _logger.LogInformation("Disconnecting from Telegram...");
    }

    /// <summary>
    /// Sends a "Pong" message back to the chat where "Ping" was received.
    /// </summary>
    public async Task RespondToPingAsync(RespondToPingJob job)
    {
        var chat = job.Chat;
        _logger.LogInformation("Responding to Ping from chat {ChatId}", chat.Id);
        await _bot.SendMessage(chat, "Pong");
    }

    /// <summary>
    /// Sends a quiz message to a user with inline keyboard options.
    /// </summary>
    public async Task SendQuizAsync(SendQuizJob job)
    {
        var chat = job.Chat;
        var question = job.Question;
        var user = job.User;
        var userChat = job.UserChatId;
        var optionsWithTokens = job.OptionsWithTokens;
        _logger.LogInformation("Sending quiz to user {UserId} for chat {ChatId}: {QuizQuestion}", user.Id, chat.Id, question);

        var buttons = optionsWithTokens
            .Select((item, index) => new InlineKeyboardButton(item.Option)
            {
                // The callback data is structured as <chatId>_<token>
                CallbackData = chat.Id.ToString()+ "_" + item.Token
            })
            .ToArray();

        var inlineKeyboard = new InlineKeyboardMarkup(buttons);
        var chatTitle = string.IsNullOrEmpty(chat.Title) ? chat.Id.ToString() : chat.Title;
        var replyMessage = $"*欢迎 {MentionMarkdownV2(user.FirstName, user.Id)} 来到 {chatTitle} ！* \n问题: {question}";

        await _bot.SendMessage(userChat, replyMessage, parseMode: ParseMode.MarkdownV2, replyMarkup: inlineKeyboard);
    }

    /// <summary>
    /// Approves or denies a user's request to join a chat.
    /// </summary>
    public async Task HandleChatJoinRequestAsync(ChatJoinRequestJob job)
    {
        var chat = await _bot.GetChat(job.Chat);
        var user = job.User;
        var result = await _bot.Client.Messages_HideChatJoinRequest(chat, user.TLUser(), job.Approve);
        _logger.LogInformation("Handled chat join request for user {UserId} in chat {ChatId}, approved: {Approve}", user.Id, chat.Id, job.Approve);
    }

    /// <summary>
    /// Edits an existing message, typically to update the status of a quiz.
    /// </summary>
    public async Task HandleEditMessageAsync(EditMessageJob job)
    {
        var message = job.Message;
        var chat = message.Chat;
        var messageId = message.MessageId;
        var newText = job.NewText;
        var result = await _bot.EditMessageText(chat, messageId, newText);
        _logger.LogInformation("Edited message {MessageId} in chat {ChatId}", messageId, chat.Id);
    }

    /// <summary>
    /// Formats a user mention using MarkdownV2 style for Telegram.
    /// </summary>
    /// <param name="username">The user's display name.</param>
    /// <param name="userid">The user's unique Telegram ID.</param>
    /// <returns>A MarkdownV2 formatted string for mentioning the user.</returns>
    public static String MentionMarkdownV2(string username, long userid) =>
        $"[{username}](tg://user?id={userid})";
}
