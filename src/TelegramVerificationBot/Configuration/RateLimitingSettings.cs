namespace TelegramVerificationBot.Configuration;

public partial class RateLimitingSettings {
  public FixedWindowSettings FixedWindow { get; set; } = new();
  public TokenBucketSettings TokenBucket { get; set; } = new();
  public LeakyBucketSettings LeakyBucket { get; set; } = new();
}

public partial class FixedWindowSettings {
  public int StartVerificationLimit { get; set; } = 3;
  public int StartVerificationWindowSeconds { get; set; } = 60;
  public int CallbackLimit { get; set; } = 10;
  public int CallbackWindowSeconds { get; set; } = 60;
}

public partial class TokenBucketSettings {
  public int StartVerificationCapacity { get; set; } = 5;
  public double StartVerificationRefillRatePerSecond { get; set; } = 0.1;
  public int CallbackCapacity { get; set; } = 20;
  public double CallbackRefillRatePerSecond { get; set; } = 2;
}

public partial class LeakyBucketSettings {
  public int StartVerificationIntervalSeconds { get; set; } = 20;
  public int CallbackIntervalMilliseconds { get; set; } = 100;
}
