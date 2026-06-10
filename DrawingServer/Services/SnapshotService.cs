using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrawingServer.Network;
using DrawingServer.Database;
using SharedLib.Config;
using SharedLib.Logging;

namespace DrawingServer.Services
{
    /// <summary>
    /// Dinh ky chup snapshot (checkpoint JSON cua board) cho cac phong dang hoat dong.
    /// Muc dich: client vao phong sau render nhanh hon (fast-join nap snapshot + delta),
    /// va cho phep nguoi dung xem lai cac trang thai cu (view-only time-travel).
    /// Snapshot la mang JSON cac DrawAction, tai su dung pipeline replay SYNC_BOARD co san.
    /// </summary>
    public static class SnapshotService
    {
        private static CancellationTokenSource _cts = new CancellationTokenSource();
        private static int _intervalMinutes = 5;
        private static int _retainPerRoom = 20;

        public static void Start()
        {
            _intervalMinutes = Math.Max(1, EnvLoader.GetInt("SNAPSHOT_INTERVAL_MINUTES", 5));
            _retainPerRoom = Math.Max(1, EnvLoader.GetInt("SNAPSHOT_RETAIN_PER_ROOM", 20));
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(_cts.Token));
            Logger.Info("Snapshot", $"SnapshotService start: moi {_intervalMinutes} phut, giu {_retainPerRoom} snapshot/phong.");
        }

        public static void Stop() => _cts?.Cancel();

        private static async Task LoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(_intervalMinutes), token); }
                catch (TaskCanceledException) { break; }

                try { await CaptureActiveRoomsAsync(); }
                catch (Exception ex) { Logger.Warning("Snapshot", $"Loi capture: {ex.Message}"); }
            }
        }

        private static async Task CaptureActiveRoomsAsync()
        {
            // Chi snapshot cac phong co it nhat 1 client dang ket noi tren node nay.
            var rooms = SecureTcpServer.Clients.Values
                .Select(c => c.RoomCode)
                .Where(r => !string.IsNullOrEmpty(r))
                .Distinct()
                .ToList();

            foreach (var roomCode in rooms)
            {
                // GetRoomHistoryAsync da gop ca pending stroke chua flush nen snapshot khong thieu net.
                var history = await DbManager.GetRoomHistoryAsync(roomCode);
                if (history == null || history.Count == 0)
                    continue;

                string snapshotJson = "[" + string.Join(",", history) + "]";
                int id = await DbManager.SaveSnapshotAsync(roomCode, snapshotJson);
                if (id > 0)
                {
                    await DbManager.PruneSnapshotsAsync(roomCode, _retainPerRoom);
                    Logger.Info("Snapshot", $"Snapshot #{id} phong {roomCode} ({history.Count} stroke).");
                }
            }
        }
    }
}
