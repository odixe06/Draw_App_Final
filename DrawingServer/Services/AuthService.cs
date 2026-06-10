// ============================================================
// DrawingServer/Services/AuthService.cs
// Person C (Server) — User Authentication Service
// Handles user login/registration, password validation
// ============================================================
using System;
using System.Collections.Generic;
using DrawingServer.Database;

namespace DrawingServer.Services
{
    /// <summary>
    /// Manages user authentication and profile operations.
    /// Delegates database operations to DbManager.
    /// </summary>
    public static class AuthService
    {
        // In-memory cache of logged-in users (username -> session info)
        // Cleared when server restarts
        private static Dictionary<string, UserSession> _activeSessions = new Dictionary<string, UserSession>();

        public class UserSession
        {
            public string Username { get; set; } = "";
            public int UserId { get; set; }
            public string AssignedColor { get; set; } = "";
            public DateTime LoginTime { get; set; }
            public string CurrentRoomCode { get; set; } = "";
        }

        /// <summary>
        /// Authenticate user with username and password hash.
        /// Returns success status and user info.
        /// </summary>
        public static async System.Threading.Tasks.Task<(bool IsSuccess, int UserId, string Username, string Message)> AuthenticateUserAsync(string username, string passwordHash)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(passwordHash))
                    return (false, 0, "", "Tên đăng nhập hoặc mật khẩu không hợp lệ");

                // Login chi xac thuc user da ton tai; dang ky di qua DbManager.RegisterAsync.
                var (success, message) = await DbManager.LoginAsync(username, passwordHash);

                if (!success)
                    return (false, 0, "", message);

                // Get or create user session
                int userId = await DbManager.GetUserIdAsync(username);
                if (userId <= 0)
                    return (false, 0, "", "Không thể tạo user ID");

                CreateUserSession(username, userId);
                return (true, userId, username, message);
            }
            catch (Exception ex)
            {
                return (false, 0, "", "Lỗi xác thực: " + ex.Message);
            }
        }

        /// <summary>
        /// Create a new session for logged-in user.
        /// </summary>
        private static void CreateUserSession(string username, int userId)
        {
            lock (_activeSessions)
            {
                if (!_activeSessions.ContainsKey(username))
                {
                    _activeSessions[username] = new UserSession
                    {
                        Username = username,
                        UserId = userId,
                        AssignedColor = GenerateUserColor(userId),
                        LoginTime = DateTime.Now,
                        CurrentRoomCode = ""
                    };
                }
            }
        }

        /// <summary>
        /// Get assigned color for user based on user ID.
        /// Ensures consistent colors per user.
        /// </summary>
        public static string GetUserColor(int userId)
        {
            // Color palette: 8 distinct colors for first 8 users
            string[] colors = new string[]
            {
                "#FF0000", // Red
                "#00FF00", // Green
                "#0000FF", // Blue
                "#FFFF00", // Yellow
                "#FF00FF", // Magenta
                "#00FFFF", // Cyan
                "#FFA500", // Orange
                "#800080"  // Purple
            };

            int colorIndex = (userId - 1) % colors.Length;
            return colors[colorIndex];
        }

        /// <summary>
        /// Generate user color (called during session creation).
        /// </summary>
        private static string GenerateUserColor(int userId)
        {
            return GetUserColor(userId);
        }

        /// <summary>
        /// Remove user session when they disconnect.
        /// </summary>
        public static void LogoutUser(string username)
        {
            lock (_activeSessions)
            {
                if (_activeSessions.ContainsKey(username))
                {
                    _activeSessions.Remove(username);
                }
            }
        }

        /// <summary>
        /// Get active user session info.
        /// </summary>
        public static UserSession? GetUserSession(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return null;

            lock (_activeSessions)
            {
                if (_activeSessions.TryGetValue(username, out var session))
                    return session;
                return null;
            }
        }

        /// <summary>
        /// Update user's current room code in session.
        /// </summary>
        public static void SetUserRoom(string username, string roomCode)
        {
            lock (_activeSessions)
            {
                if (_activeSessions.TryGetValue(username, out var session))
                {
                    session.CurrentRoomCode = roomCode;
                }
            }
        }

        /// <summary>
        /// Check if username is logged in.
        /// </summary>
        public static bool IsUserOnline(string username)
        {
            lock (_activeSessions)
            {
                return _activeSessions.ContainsKey(username);
            }
        }

        /// <summary>
        /// Get all active users in a specific room.
        /// </summary>
        public static List<UserSession> GetRoomUsers(string roomCode)
        {
            var roomUsers = new List<UserSession>();
            lock (_activeSessions)
            {
                foreach (var session in _activeSessions.Values)
                {
                    if (session.CurrentRoomCode == roomCode)
                        roomUsers.Add(session);
                }
            }
            return roomUsers;
        }
    }
}
