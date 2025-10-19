using Microsoft.Extensions.Options;
using TelegramVerificationBot.Configuration;
using TelegramVerificationBot.Tasks;

namespace TelegramVerificationBot.Dispatcher;

using System.Threading.Tasks.Dataflow;

public record DataflowPipeline(
  ITargetBlock<object> EntryPoint,
  IEnumerable<IDataflowBlock> CompletionTargets
);


public class DataflowPipelineBuilder(ILogger<DataflowPipelineBuilder> logger, IOptions<TplDataflowOptions> options) {

  public DataflowPipeline Build(
    Func<object, Task> processJobAction,
    CancellationToken cancellationToken) {
    int workerCount = options.Value.WorkerCount;
    logger.LogInformation("Building TPL Dataflow pipeline with {WorkerCount}"
                          + " workers (from configuration).", workerCount);

    var serialExecutionOptions = new ExecutionDataflowBlockOptions {
      MaxDegreeOfParallelism = 1,
      EnsureOrdered = true, // 保证 Job 在分片内按进入顺序执行
      CancellationToken = cancellationToken
    };

    var parallelExecutionOptions = new ExecutionDataflowBlockOptions {
      MaxDegreeOfParallelism = DataflowBlockOptions.Unbounded,
      EnsureOrdered = false, // 不保证顺序
      CancellationToken = cancellationToken
    };

    var keyedSerialShards = new ActionBlock<object>[workerCount];
    for (int i = 0; i < workerCount; i++) {
      // 使用传入的执行 Action，并应用串行配置
      keyedSerialShards[i] = new ActionBlock<object>(
        processJobAction,
        serialExecutionOptions);
    }

    var generalParallelBlock = new ActionBlock<object>(
      processJobAction,
      parallelExecutionOptions);


    var routerBlock = new ActionBlock<object>(job => {
      if (job is IKeyedJob keyedJob) {
        int hashCode = HashCode.Combine(keyedJob.ChatId, keyedJob.UserId);
        int workerIndex = (int)((uint)hashCode % workerCount);

        keyedSerialShards[workerIndex].Post(job);
      } else {
        generalParallelBlock.Post(job);
      }
    }, new ExecutionDataflowBlockOptions {
      // Router 本身无需高并行度，设置为 1 即可，保证分发逻辑串行执行。
      MaxDegreeOfParallelism = 1,
      CancellationToken = cancellationToken
    });

    routerBlock.Completion.ContinueWith(t => {
      if (t.IsFaulted) {
        foreach (var shard in keyedSerialShards) {
          ((IDataflowBlock)shard).Fault(t.Exception.InnerException);
        }
        ((IDataflowBlock)generalParallelBlock).Fault(t.Exception.InnerException);
      } else {
        foreach (var shard in keyedSerialShards) {
          shard.Complete();
        }
        generalParallelBlock.Complete();
      }
    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    var completionTargets = new List<IDataflowBlock>(keyedSerialShards) { generalParallelBlock };

    return new DataflowPipeline(
      EntryPoint: routerBlock,
      CompletionTargets: completionTargets
    );
  }

}
