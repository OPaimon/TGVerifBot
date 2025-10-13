namespace TelegramVerificationBot.Dispatcher;

/// <summary>
/// Defines a contract for dispatching jobs to a background queue.
/// </summary>
public interface ITaskDispatcher {
  /// <summary>
  /// Asynchronously adds a job to the processing queue.
  /// </summary>
  /// <param name="job">The job object to be processed.</param>
  /// <returns>A task that represents the asynchronous write operation.</returns>
  Task DispatchAsync(object job);
}
