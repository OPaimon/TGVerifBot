namespace TelegramVerificationBot.Configuration;

public partial class TaskDispatcherSettings {
  public TplDataflowOptions TplDataflow { get; set; } = new();
}

public partial class TplDataflowOptions {
  public int WorkerCount { get; set; } = 4;
}
