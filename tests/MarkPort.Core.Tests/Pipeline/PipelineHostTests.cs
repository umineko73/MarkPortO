// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// This file is part of MarkPort.
//
// MarkPort is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPort is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPort.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using System.Collections.Concurrent;
using MarkPort.Core.Pipeline;

namespace MarkPort.Core.Tests.Pipeline;

public class PipelineHostTests
{
    private sealed class DelegateStep(string name, int concurrency, Func<string, CancellationToken, Task> work)
        : IPipelineStep<string>
    {
        public string Name { get; } = name;

        public int Concurrency { get; } = concurrency;

        public Task ProcessAsync(string item, CancellationToken cancellationToken) => work(item, cancellationToken);
    }

    private static DelegateStep Step(string name, Func<string, CancellationToken, Task>? work = null, int concurrency = 1) =>
        new(name, concurrency, work ?? ((_, _) => Task.CompletedTask));

    private static ConcurrentQueue<PipelineEvent<string>> Collect(PipelineHost<string> host)
    {
        var events = new ConcurrentQueue<PipelineEvent<string>>();
        host.EventRaised += events.Enqueue;
        return events;
    }

    [Fact]
    public async Task EveryItemPassesEveryStepInOrder()
    {
        var host = new PipelineHost<string>([Step("s1"), Step("s2"), Step("s3")]);
        var events = Collect(host);

        await host.RunAsync(["a", "b", "c"], CancellationToken.None);

        foreach (var item in new[] { "a", "b", "c" })
        {
            var completed = events.Where(e => e.Item == item && e.Kind == StepEventKind.Completed)
                .Select(e => e.StepIndex).ToArray();
            Assert.Equal([0, 1, 2], completed);
        }

        Assert.DoesNotContain(events, e => e.Kind is StepEventKind.Failed or StepEventKind.Interrupted);
    }

    [Fact]
    public async Task AFailingItemDoesNotStopTheOthers_AndIsNotForwarded()
    {
        var host = new PipelineHost<string>(
        [
            Step("s1"),
            Step("s2", (item, _) => item == "b" ? throw new InvalidOperationException("broken file") : Task.CompletedTask),
            Step("s3"),
        ]);
        var events = Collect(host);

        await host.RunAsync(["a", "b", "c"], CancellationToken.None);

        var failed = Assert.Single(events, e => e.Kind == StepEventKind.Failed);
        Assert.Equal("b", failed.Item);
        Assert.Equal(1, failed.StepIndex);
        Assert.Equal("broken file", failed.Error!.Message);
        Assert.DoesNotContain(events, e => e.Item == "b" && e.StepIndex == 2);
        Assert.Contains(events, e => e.Item == "a" && e.StepIndex == 2 && e.Kind == StepEventKind.Completed);
        Assert.Contains(events, e => e.Item == "c" && e.StepIndex == 2 && e.Kind == StepEventKind.Completed);
    }

    [Fact]
    public async Task Cancelling_InterruptsTheRunningItem_AndLeavesUnstartedItemsAlone()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var host = new PipelineHost<string>(
        [
            Step("s1", async (item, ct) =>
            {
                if (item == "a")
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
            }),
            Step("s2"),
        ]);
        var events = Collect(host);

        var run = host.RunAsync(["a", "b", "c"], cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var interrupted = Assert.Single(events, e => e.Kind == StepEventKind.Interrupted);
        Assert.Equal("a", interrupted.Item);
        Assert.DoesNotContain(events, e => e.Item is "b" or "c" && e.Kind == StepEventKind.Started);
        Assert.DoesNotContain(events, e => e.Kind == StepEventKind.Failed);
    }

    [Fact]
    public async Task StepsRunConcurrently_ADocumentCanBeInStep2WhileTheNextIsInStep1()
    {
        // "a" stays in step 2 until "b" has entered step 1. This can only finish if the steps overlap.
        var bEnteredStep1 = new TaskCompletionSource();
        var host = new PipelineHost<string>(
        [
            Step("s1", (item, _) =>
            {
                if (item == "b")
                {
                    bEnteredStep1.TrySetResult();
                }

                return Task.CompletedTask;
            }),
            Step("s2", async (item, ct) =>
            {
                if (item == "a")
                {
                    await bEnteredStep1.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                }
            }),
        ]);
        var events = Collect(host);

        await host.RunAsync(["a", "b"], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.DoesNotContain(events, e => e.Kind == StepEventKind.Failed);
        Assert.Equal(2, events.Count(e => e.StepIndex == 1 && e.Kind == StepEventKind.Completed));
    }

    [Fact]
    public async Task AStepWithConcurrencyOne_NeverRunsTwoItemsAtOnce()
    {
        var running = 0;
        var maxRunning = 0;
        var host = new PipelineHost<string>(
        [
            Step("office-like", async (_, _) =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref maxRunning, now);
                await Task.Delay(20);
                Interlocked.Decrement(ref running);
            }, concurrency: 1),
        ]);

        await host.RunAsync(Enumerable.Range(0, 8).Select(i => $"d{i}"), CancellationToken.None);

        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task AStepWithConcurrencyTwo_RunsTwoItemsAtOnce()
    {
        var running = 0;
        var maxRunning = 0;
        var host = new PipelineHost<string>(
        [
            Step("libreoffice-like", async (_, _) =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref maxRunning, now);
                await Task.Delay(100);
                Interlocked.Decrement(ref running);
            }, concurrency: 2),
        ]);

        await host.RunAsync(Enumerable.Range(0, 6).Select(i => $"d{i}"), CancellationToken.None);

        Assert.Equal(2, maxRunning);
    }

    [Fact]
    public async Task ABrokenObserverDoesNotStopTheRun()
    {
        var host = new PipelineHost<string>([Step("s1")]);
        var completed = 0;
        host.EventRaised += e =>
        {
            if (e.Kind == StepEventKind.Completed)
            {
                Interlocked.Increment(ref completed);
            }

            throw new InvalidOperationException("observer bug");
        };

        await host.RunAsync(["a", "b", "c"], CancellationToken.None);

        Assert.Equal(3, completed);
    }

    [Fact]
    public async Task EmptyInput_CompletesImmediately()
    {
        var host = new PipelineHost<string>([Step("s1"), Step("s2")]);

        await host.RunAsync([], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}
