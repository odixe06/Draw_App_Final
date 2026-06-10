using System;
using System.Threading.Tasks;
using DrawingServer.Network;
using DrawingServer.Services;
using SharedLib.Config;
using SharedLib.Logging;

namespace DrawingServer
{
    class Program
    {
        static async Task Main(string[] args)
        {
            EnvLoader.Load();
            string serverId = EnvLoader.Get("SERVER_ID", "server-1");
            string logFile = EnvLoader.Get("SERVER_LOG_FILE", $"server_logs_{serverId}.txt");
            Logger.Initialize(logFile);
            Console.WriteLine("Khoi dong Drawing Server...");

            string pfxPath = EnvLoader.Get("SERVER_CERT_PATH", "server.pfx");
            string pfxPassword = EnvLoader.Get("SERVER_CERT_PASSWORD", "123456");
            int tcpPort = EnvLoader.GetInt("SERVER_TCP_PORT", 8888);
            int udpPort = EnvLoader.GetInt("SERVER_UDP_PORT", 8889);

            SecureTcpServer tcpServer = new SecureTcpServer();
            SecureUdpServer udpServer = new SecureUdpServer();

            CrossServerSyncService.Start();
            ServerNodeHeartbeatService.Start(tcpPort, udpPort);
            SnapshotService.Start();
            _ = Task.Run(() => udpServer.StartAsync(udpPort));
            await tcpServer.StartAsync(pfxPath, pfxPassword, tcpPort);
        }
    }
}
