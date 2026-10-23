#if TWINCAT
using TwinCAT.Ads;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>Raw ADS index-group reads against one AMS NetId and port.</summary>
internal sealed class AdsRawClient : IAdsRawReader, IDisposable
{
    private readonly AdsClient _client;
    private readonly string _netId;
    private readonly int _port;
    private bool _connected;

    // The connection is opened by the first read, so that a failing connect is an ADS error result (code and name)
    // like any other failed read instead of an exception from the constructor.
    public AdsRawClient(string netId, int port, int timeoutMs = AdsTimeout.DefaultMs)
    {
        _client = new AdsClient { Timeout = timeoutMs };
        _netId = netId;
        _port = port;
    }

    public AdsRawReadResult Read(uint indexGroup, uint indexOffset, int maxLength)
    {
        byte[] buffer = new byte[maxLength];
        try
        {
            if (!_connected)
            {
                _client.Connect(_netId, _port);
                _connected = true;
            }
            AdsErrorCode error = _client.TryRead(indexGroup, indexOffset, buffer.AsMemory(), out int read);
            if (error != AdsErrorCode.NoError) return AdsRawReadResult.Failure((uint)error, error.ToString());
            return AdsRawReadResult.Success(buffer.AsSpan(0, Math.Clamp(read, 0, maxLength)).ToArray());
        }
        catch (AdsErrorException ex)
        {
            return AdsRawReadResult.Failure((uint)ex.ErrorCode, ex.ErrorCode.ToString());
        }
    }

    public void Dispose() => _client.Dispose();
}
#endif
