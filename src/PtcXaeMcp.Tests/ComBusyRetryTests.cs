using System.Runtime.InteropServices;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class ComBusyRetryTests
{
    private const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
    private const int RPC_E_SERVERCALL_RETRYLATER = unchecked((int)0x8001010A);

    private static COMException Busy(int hResult = RPC_E_CALL_REJECTED) =>
        new("server busy", hResult);

    [Fact]
    public void Run_SucceedsImmediately_ReturnsValue()
    {
        int calls = 0;
        var result = ComBusyRetry.Run(() => { calls++; return 42; },
            timeout: TimeSpan.FromSeconds(1));
        Assert.Equal(42, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Run_RetriesOnRpcCallRejected_ThenSucceeds()
    {
        int calls = 0;
        var result = ComBusyRetry.Run<int>(() =>
        {
            calls++;
            if (calls < 3) throw Busy();
            return 99;
        }, timeout: TimeSpan.FromSeconds(5), delay: TimeSpan.FromMilliseconds(1));

        Assert.Equal(99, result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Run_RetriesOnServerCallRetryLater_ThenSucceeds()
    {
        int calls = 0;
        ComBusyRetry.Run(() =>
        {
            calls++;
            if (calls < 2) throw Busy(RPC_E_SERVERCALL_RETRYLATER);
        }, timeout: TimeSpan.FromSeconds(5), delay: TimeSpan.FromMilliseconds(1));

        Assert.Equal(2, calls);
    }

    [Fact]
    public void Run_DoesNotRetryOnOtherComException()
    {
        int calls = 0;
        var ex = Assert.Throws<COMException>(() =>
            ComBusyRetry.Run(() => { calls++; throw new COMException("other", unchecked((int)0x80004005)); },
                timeout: TimeSpan.FromSeconds(5), delay: TimeSpan.FromMilliseconds(1)));

        Assert.Equal(1, calls);
        Assert.Equal(unchecked((int)0x80004005), ex.HResult);
    }

    [Fact]
    public void Run_DoesNotRetryOnPlainException()
    {
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() =>
            ComBusyRetry.Run(() => { calls++; throw new InvalidOperationException("nope"); },
                timeout: TimeSpan.FromSeconds(5), delay: TimeSpan.FromMilliseconds(1)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Run_TimeoutExpired_ThrowsXaeBlockedCarryingTheBusyHResult()
    {
        // Issue #149: exhaustion is a final XaeBlockedException (never retried by an outer scope);
        // its HResult is the last busy code and the busy exception is the inner one.
        int calls = 0;
        var ex = Assert.Throws<XaeBlockedException>(() =>
            // 300 ms: a first attempt slowed by JIT or a loaded machine must not use up the whole window (it did at 30 ms).
            ComBusyRetry.Run(() => { calls++; throw Busy(); },
                timeout: TimeSpan.FromMilliseconds(300), delay: TimeSpan.FromMilliseconds(10)));

        Assert.True(calls >= 2, $"Expected at least 2 attempts before timeout, saw {calls}");
        Assert.Equal(RPC_E_CALL_REJECTED, ex.HResult);
        Assert.IsType<COMException>(ex.InnerException);
    }
}
