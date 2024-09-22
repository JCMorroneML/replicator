using System.Text.Json;
using EventStore.Replicator.Shared.Logging;
using EventStore.Replicator.Observers;
using EventStore.Replicator.Partitioning;
using EventStore.Replicator.Shared;
using EventStore.Replicator.Shared.Observe;
using GreenPipes;
using GreenPipes.Partitioning;

namespace EventStore.Replicator.Sink;

public class SinkPipe {
    readonly IPipe<SinkContext> _pipe;

    public SinkPipe(IEventWriter writer, SinkPipeOptions options, ICheckpointStore checkpointStore)
        => _pipe = Pipe.New<SinkContext>(
            cfg => {
                cfg.UseRetry(
                    r => {
                        r.Incremental(10, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
                        r.ConnectRetryObserver(new LoggingRetryObserver());
                    }
                );

                var useJsPartitioner = !string.IsNullOrWhiteSpace(options.Partitioner);

                if (options.PartitionCount > 1) {
                    var keyProvider = !useJsPartitioner
                        ? x => KeyProvider.ByStreamName(x.ProposedEvent)
                        : GetJsPartitioner();

                    cfg.UsePartitioner(
                        new HashPartitioner(options.PartitionCount, new Murmur3UnsafeHashGenerator()),
                        keyProvider
                    );
                }
                else if (useJsPartitioner) {
                    cfg.UsePartitioner(new ValuePartitioner(), GetJsPartitioner());
                }

                cfg.UseLog();

                cfg.UseEventWriter(writer);
                cfg.UseCheckpointStore(checkpointStore);

                cfg.UseExecute(
                    ctx => ReplicationMetrics.ProcessedPosition.Set(
                        ctx.ProposedEvent.SourcePosition.EventPosition
                    )
                );

                Func<SinkContext, string> GetJsPartitioner() {
                    var jsProvider = new JsKeyProvider(options.Partitioner!);
                    return x => jsProvider.GetPartitionKey(x.ProposedEvent);
                }
            }
        );

    public Task Send(SinkContext context) => _pipe.Send(context);
}

static class SinkPipelineExtensions {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();
    public static void UseEventWriter(this IPipeConfigurator<SinkContext> cfg, IEventWriter writer)
        => cfg.UseExecuteAsync(
            async ctx => {
	        Log.Info("Sinking: {context}", JsonSerializer.Serialize(ctx.EventDetails));
                try {
                    var position = await writer.WriteEvent(ctx.ProposedEvent, ctx.CancellationToken)
                        .ConfigureAwait(false);

                    if (position != -1)
                        ReplicationMetrics.WriterPosition.Set(position);
                } 
                catch (Exception e) {
                    Log.Error(e.Message, "Error writing event: {Event}", ctx.ProposedEvent);
                    throw;
                }
            }
        );

    public static void UseCheckpointStore(this IPipeConfigurator<SinkContext> cfg, ICheckpointStore checkpointStore)
        => cfg.UseExecuteAsync(
            async ctx => await checkpointStore.StoreCheckpoint(
                    ctx.ProposedEvent.SourcePosition,
                    ctx.CancellationToken
                )
                .ConfigureAwait(false)
        );
}
