using NLog;

namespace Miningcore.Crypto.Hashing.Ethash;

public interface IEthashLight : IDisposable
{
    void Setup(int numCaches, ulong hardForkBlock);
    Task<IEthashCache> GetCacheAsync(ILogger logger, ulong block);
    string AlgoName { get; }
}

public interface IEthashCache : IDisposable
{
    bool Compute(ILogger logger, byte[] hash, ulong nonce, out byte[] mixDigest, out byte[] result);
}