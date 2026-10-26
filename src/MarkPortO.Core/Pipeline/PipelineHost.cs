// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// This file is part of MarkPortO.
//
// MarkPortO is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPortO is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPortO.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using System.Threading.Channels;

namespace MarkPortO.Core.Pipeline;

public enum StepEventKind
{
    Started,
    Completed,
    Failed,

    /// <summary>The run was cancelled while this item was being processed.</summary>
    Interrupted,
}

/// <param name="Item">The item (a document) the event is about.</param>
/// <param name="StepIndex">0-based index of the step.</param>
/// <param name="StepName">Name of the step.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Error">The error of a <see cref="StepEventKind.Failed"/> event.</param>
public sealed record PipelineEvent<TItem>(TItem Item, int StepIndex, string StepName, StepEventKind Kind, Exception? Error = null);

/// <summary>One step of the pipeline: step 1 (text and page images), step 2 (image references), step 3 (VLM reading).</summary>
public interface IPipelineStep<in TItem>
{
    string Name { get; }

    /// <summary>How many items this step may process at the same time (at least 1).</summary>
    int Concurrency { get; }

    Task ProcessAsync(TItem item, CancellationToken cancellationToken);
}

/// <summary>
/// Runs items through the steps one after another, every step in its own workers, so that
/// while one document is in step 3 the next one can already be in step 1 (FR-009).
/// </summary>
/// <remarks>
/// A failure in one item is reported as an event and does not stop the others (FR-029). When the run is
/// cancelled, the item being processed is reported as interrupted and items not yet started are left alone
/// (they stay pending so a later run can resume them).
/// </remarks>
public sealed class PipelineHost<TItem>
{
    private readonly IReadOnlyList<IPipelineStep<TItem>> _steps;
    private readonly int _capacity;

    public PipelineHost(IReadOnlyList<IPipelineStep<TItem>> steps, int channelCapacity = 8)
    {
        ArgumentOutOfRangeException.ThrowIfZero(steps.Count);
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCapacity, 1);
        _steps = steps;
        _capacity = channelCapacity;
    }

    /// <summary>
    /// Raised from worker threads. A handler that throws does not break the pipeline: its exception is ignored.
    /// </summary>
    public event Action<PipelineEvent<TItem>>? EventRaised;

    /// <summary>Processes all items. Returns when every worker has finished; does not throw when cancelled.</summary>
    public async Task RunAsync(IEnumerable<TItem> items, CancellationToken cancellationToken)
    {
        var channels = _steps
            .Select(_ => Channel.CreateBounded<TItem>(new BoundedChannelOptions(_capacity)
            {
                SingleWriter = false,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            }))
            .ToArray();

        var tasks = new List<Task>();

        tasks.Add(Task.Run(async () =>
        {
            try
            {
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await channels[0].Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelled: items not written stay pending.
            }
            finally
            {
                channels[0].Writer.TryComplete();
            }
        }, CancellationToken.None));

        for (var index = 0; index < _steps.Count; index++)
        {
            var stepIndex = index;
            var step = _steps[stepIndex];
            var next = stepIndex + 1 < _steps.Count ? channels[stepIndex + 1] : null;

            var workers = Enumerable.Range(0, Math.Max(1, step.Concurrency))
                .Select(_ => Task.Run(() => WorkAsync(stepIndex, step, channels[stepIndex], next, cancellationToken), CancellationToken.None))
                .ToArray();

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(workers).ConfigureAwait(false);
                }
                finally
                {
                    next?.Writer.TryComplete();
                }
            }, CancellationToken.None));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task WorkAsync(
        int stepIndex,
        IPipelineStep<TItem> step,
        Channel<TItem> input,
        Channel<TItem>? output,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await input.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!input.Reader.TryRead(out var item))
                {
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                Raise(new PipelineEvent<TItem>(item, stepIndex, step.Name, StepEventKind.Started));
                try
                {
                    await step.ProcessAsync(item, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Raise(new PipelineEvent<TItem>(item, stepIndex, step.Name, StepEventKind.Interrupted));
                    return;
                }
                catch (Exception ex)
                {
                    Raise(new PipelineEvent<TItem>(item, stepIndex, step.Name, StepEventKind.Failed, ex));
                    continue;
                }

                Raise(new PipelineEvent<TItem>(item, stepIndex, step.Name, StepEventKind.Completed));

                if (output is not null)
                {
                    try
                    {
                        await output.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled while waiting for the next item: nothing is in progress here.
        }
    }

    private void Raise(PipelineEvent<TItem> pipelineEvent)
    {
        try
        {
            EventRaised?.Invoke(pipelineEvent);
        }
        catch (Exception)
        {
            // A broken observer must not stop the conversion.
        }
    }
}
