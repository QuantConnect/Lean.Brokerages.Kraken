/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.Kraken;

namespace QuantConnect.Tests.Brokerages.Kraken;

public class DecayingRateLimitTests
{
    [Test]
    public void WaitToProceed_AllowsRequestWithinLimit_WithoutRateLimitMessage()
    {
        using var cts = new CancellationTokenSource();
        using var rateLimit = new DecayingRateLimit(limit: 5, decayRate: 1m, decayIntervalInMs: 10, cts.Token);
        var messageCount = 0;
        rateLimit.Message += (_, _) => Interlocked.Increment(ref messageCount);

        var result = rateLimit.WaitToProceed(weight: 5, identifier: "initial-request");

        Assert.IsTrue(result);
        Assert.AreEqual(0, messageCount);
    }

    [Test]
    public void WaitToProceed_WaitsAndEmitsMessage_WhenLimitExceeded()
    {
        using var cts = new CancellationTokenSource();
        using var rateLimit = new DecayingRateLimit(limit: 5, decayRate: 1m, decayIntervalInMs: 100, cts.Token);
        var messageCount = 0;
        BrokerageMessageEvent lastMessage = null;
        rateLimit.Message += (_, message) =>
        {
            Interlocked.Increment(ref messageCount);
            lastMessage = message;
        };

        Assert.IsTrue(rateLimit.WaitToProceed(weight: 5, identifier: "first"));

        var watch = Stopwatch.StartNew();
        var result = rateLimit.WaitToProceed(weight: 1, identifier: "second");
        watch.Stop();

        Assert.IsTrue(result);
        Assert.GreaterOrEqual(watch.ElapsedMilliseconds, 180);
        Assert.GreaterOrEqual(messageCount, 1);
        Assert.IsNotNull(lastMessage);
        Assert.AreEqual(BrokerageMessageType.Warning, lastMessage!.Type);
        Assert.IsTrue(lastMessage.Message.Contains("second"));
    }

    [Test]
    public void WaitToProceed_ReturnsFalse_WhenCancellationIsRequestedWhileWaiting()
    {
        using var cts = new CancellationTokenSource();
        using var rateLimit = new DecayingRateLimit(limit: 5, decayRate: 1m, decayIntervalInMs: 200, cts.Token);

        Assert.IsTrue(rateLimit.WaitToProceed(weight: 5));
        cts.CancelAfter(20);

        var watch = Stopwatch.StartNew();
        var result = rateLimit.WaitToProceed(weight: 1, identifier: "cancelled-request");
        watch.Stop();

        Assert.IsFalse(result);
        Assert.Less(watch.ElapsedMilliseconds, 180);
    }

    [Test]
    public void WaitToProceed_ReturnFalseAfterTimeout()
    {
        using var cts = new CancellationTokenSource();
        using var rateLimit = new DecayingRateLimit(limit: 5, decayRate: 0.001m, decayIntervalInMs: 1000, cts.Token);

        Assert.IsTrue(rateLimit.WaitToProceed(weight: 5));
        const int timeoutMs = 250;

        var watch = Stopwatch.StartNew();
        var result = rateLimit.WaitToProceed(weight: 1, identifier: "timed-out-request", millisecondsTimeout: timeoutMs);
        watch.Stop();

        Assert.IsFalse(result);
        Assert.GreaterOrEqual(watch.ElapsedMilliseconds, timeoutMs - 30);
        Assert.Less(watch.ElapsedMilliseconds, timeoutMs + 500);
    }

    [Test]
    public void WaitToProceed_ReturnsFalseImmediately_WhenCancellationAlreadyRequested()
    {
        using var cts = new CancellationTokenSource();
        using var rateLimit = new DecayingRateLimit(limit: 5, decayRate: 1m, decayIntervalInMs: 500, cts.Token);

        Assert.IsTrue(rateLimit.WaitToProceed(weight: 5));
        cts.Cancel();

        var watch = Stopwatch.StartNew();
        var result = rateLimit.WaitToProceed(weight: 1, identifier: "already-cancelled", millisecondsTimeout: 10_000);
        watch.Stop();

        Assert.IsFalse(result);
        Assert.Less(watch.ElapsedMilliseconds, 50);
    }

    [Test]
    public void WaitToProceed_CancellationStopsWaiting_BeforeTimeout()
    {
        using var cts = new CancellationTokenSource();
        using var rateLimit = new DecayingRateLimit(limit: 5, decayRate: 0.001m, decayIntervalInMs: 1000, cts.Token);

        Assert.IsTrue(rateLimit.WaitToProceed(weight: 5));
        cts.CancelAfter(100);

        var watch = Stopwatch.StartNew();
        var result = rateLimit.WaitToProceed(weight: 1, identifier: "cancel-before-timeout", millisecondsTimeout: 10_000);
        watch.Stop();

        Assert.IsFalse(result);
        Assert.Less(watch.ElapsedMilliseconds, 1_000);
    }

    [Test]
    public void WaitToProceed_ReturnsFalseImmediately_WhenWeightExceedsLimit()
    {
        using var cts = new CancellationTokenSource();
        using var rateLimit = new DecayingRateLimit(limit: 5, decayRate: 0.001m, decayIntervalInMs: 1000, cts.Token);

        var watch = Stopwatch.StartNew();
        var result = rateLimit.WaitToProceed(weight: 10, identifier: "weight-exceeds-limit");
        watch.Stop();

        Assert.IsFalse(result);
        Assert.Less(watch.ElapsedMilliseconds, 50);
    }
}