using System.Net;
using System.Net.Sockets;
using System.Reactive.Disposables;
using System.Threading.Tasks.Dataflow;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Extensions;
using Miningcore.Time;
using NLog;
using ZeroMQ;

namespace Miningcore.Mining;

internal class ShareReceiverSocket
{
    private readonly ShareRelayEndpointConfig relay;
    private readonly IMasterClock clock;
    private readonly TimeSpan reconnectTimeout;
    private readonly TimeSpan timeout;
    private readonly CancellationToken ct;
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly BufferBlock<(string Url, ZMessage Message)> queue;
    private readonly Dictionary<string, ZSocket> sockets;
    private readonly Dictionary<string, DateTime> lastMessageReceived = new Dictionary<string, DateTime>();

    public ShareReceiverSocket(
        ShareRelayEndpointConfig relay,
        IMasterClock clock,
        CancellationToken ct,
        BufferBlock<(string Url, ZMessage Message)> queue)
    {
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(relay);

        this.relay = relay;
        this.clock = clock;
        this.reconnectTimeout = TimeSpan.FromSeconds(60);
        this.timeout = TimeSpan.FromMilliseconds(5000); ;
        this.ct = ct;
        this.queue = queue;

        sockets = SetupSubSockets(relay);
        lastMessageReceived = sockets.Keys.ToDictionary(k => k, k => clock.Now);
    }

    public Task StartPolling()
    {
        return Task.Run(() => {
            while (!ct.IsCancellationRequested)
            {
                Poll();
            }
        });
    }

    public void Poll()
    {
        using (new CompositeDisposable(sockets.Values))
        {
            while (!ct.IsCancellationRequested)
            {
                if (sockets.Values.PollIn(sockets.Values.Select(_ => ZPollItem.CreateReceiver()).ToArray(), out var messages, out var error, timeout))
                {
                    foreach (var message in messages.Select((value, index) => new { value, index }))
                    {
                        var i = message.index;
                        var msg = message.value;
                        
                        // Get ip address to reference the socket
                        var ipAddress = GetIpFromZSocket(sockets.Values.ElementAt(i));

                        if (msg != null)
                        {
                            lastMessageReceived[ipAddress] = clock.Now;
                            queue.Post((relay.Url, msg));
                        }
                        else CheckTimeoutAndRecreateSocket(sockets.Values.ElementAt(i));
                    }

                    if (error != null)
                        logger.Error(() => $"{nameof(ShareReceiver)}: {error.Name} [{error.Name}] during receive");
                }
                else
                {
                    // Check if there is a change in the list of ips
                    // var newIpList = ResolveIpFromDns(relay.Url).Select(url => url.Split(':')[0]).ToArray();
                    var newIpList = ResolveIpFromDns(relay.Url);
                    if(relay.ResolveReplicas && !sockets.Keys.SequenceEqual(newIpList))
                    {
                        foreach (var ip in newIpList)
                        {
                            if (!sockets.ContainsKey(ip))
                            {
                                sockets[ip] = SetupSubSocket(relay, ip);
                                lastMessageReceived[ip] = clock.Now;
                            }
                        }

                        foreach (var ip in sockets.Keys.ToList())
                        {
                            if (!newIpList.Contains(ip))
                            {
                                sockets[ip].Dispose();
                                sockets.Remove(ip);
                                lastMessageReceived.Remove(ip);
                            }
                        }
                    }

                    // Loop in the sockets and refresh if timeout reached
                    foreach (var socket in sockets.Values)
                    {
                        CheckTimeoutAndRecreateSocket(socket);
                    }
                }
            }
        }

        void CheckTimeoutAndRecreateSocket(ZSocket socket)
        {
            var ipAddress = GetIpFromZSocket(socket);
            var socketEndpoint = relay.ResolveReplicas ? ipAddress : relay.Url;
            if(clock.Now - lastMessageReceived[ipAddress] > reconnectTimeout)
            {
                // re-create socket
                sockets[ipAddress].Dispose();
                sockets[ipAddress] = SetupSubSocket(relay, socketEndpoint);

                // reset clock
                lastMessageReceived[ipAddress] = clock.Now;

                logger.Info(() => $"Receive timeout of {reconnectTimeout.TotalSeconds} seconds exceeded. Re-connecting to {relay.Url} ...");
            }
        }
    }

    private static Dictionary<string, ZSocket> SetupSubSockets(ShareRelayEndpointConfig relay, bool silent = false)
    {
        var subSockets = (relay.ResolveReplicas ? ResolveIpFromDns(relay.Url) : new[] { relay.Url })
            .Select(url => SetupSubSocket(relay, url))
            .ToDictionary(x => GetIpFromZSocket(x));

        if(!silent)
        {
            Array.ForEach(subSockets.Values.ToArray(), subSocket =>
            {
                if(subSocket.CurveServerKey != null)
                    logger.Info($"Monitoring external stratum {subSocket.LastEndpoint} using key {subSocket.CurveServerKey.ToHexString()}");
                else
                    logger.Info($"Monitoring external stratum {subSocket.LastEndpoint}");
            });
        }

        return subSockets;
    }

    private static ZSocket SetupSubSocket(ShareRelayEndpointConfig relay, string url)
    {
        var subSocket = new ZSocket(ZSocketType.SUB);
        subSocket.SetupCurveTlsClient(relay.SharedEncryptionKey, logger);
        subSocket.Connect(relay.ResolveReplicas ? $"tcp://{url}" : url);
        subSocket.SubscribeAll();
        return subSocket;
    }

    private static string[] ResolveIpFromDns(string url)
    {
        var host = new Uri(url).Host;
        var port = new Uri(url).Port;
        string[] ips = null;

        try
        {
            ips = Dns.GetHostAddresses(host)
                    .Where(x => x.AddressFamily == AddressFamily.InterNetwork)
                    .Select(x => $"{x}:{port}")
                    .ToArray();
        }
        catch (Exception ex)
        {
            logger.Info($"An error occurred while resolving host addresses {url}: {ex.Message}");
        }

        return ips ?? new[] { $"{host}:{port}" };
    }

    private static string GetIpFromZSocket(ZSocket socket)
    {
        var endpoint = socket.LastEndpoint;
        var ipStart = endpoint.IndexOf("://") + 3;
        var ipEnd = endpoint.IndexOf('\0', ipStart);
        return endpoint.Substring(ipStart, ipEnd - ipStart);
    }
}
