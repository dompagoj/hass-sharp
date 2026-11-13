using HassSharp;

static class VisualValuesTests
{
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    [RuntimeSuite("--visual-values", Order = 7)]
    public static Task Run() => WaitUntilDeadline();
    static async Task WaitUntilDeadline()
    {
        var initialPolls = 0;
        await VisualValues.WaitUntil(() => { initialPolls++; return true; }, 1, 0.01, CancellationToken.None);
        Check(initialPolls == 1, "wait checks an already-true predicate immediately");

        // A synchronous read cannot be interrupted, but a result observed after the
        // deadline must be discarded even when it is the first immediate check.
        try
        {
            await VisualValues.WaitUntil(() => { Thread.Sleep(40); return true; }, 0.01, 0.01, CancellationToken.None);
            throw new Exception("wait accepted a late initial true result");
        }
        catch (TimeoutException) { }

        var laterPolls = 0;
        try
        {
            await VisualValues.WaitUntil(() =>
            {
                if (++laterPolls == 1) return false;
                Thread.Sleep(150);
                return true;
            }, 0.1, 0.01, CancellationToken.None);
            throw new Exception("wait accepted a true result after the deadline on a later poll");
        }
        catch (TimeoutException) { }
        Check(laterPolls is 1 or 2, "wait must not poll again after timeout");

        // Resume the delay after the deadline to prove no extra entity read starts.
        var expiredPolls = 0;
        var previousContext = SynchronizationContext.Current;
        Task expiredWait;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new LatePollContext());
            expiredWait = VisualValues.WaitUntil(() => ++expiredPolls > 1, 0.01, 0.01, CancellationToken.None);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        try
        {
            await expiredWait;
            throw new Exception("wait polled after its delay resumed past the deadline");
        }
        catch (TimeoutException) { }
        Check(expiredPolls == 1, "expired wait must not start another predicate read");

        var successfulPolls = 0;
        await VisualValues.WaitUntil(() => ++successfulPolls == 2, 1, 0.01, CancellationToken.None);
        Check(successfulPolls == 2, "wait accepts a later true observation within its timeout");
    }

    sealed class LatePollContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) =>
            ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(40); callback(state); });
    }

}
