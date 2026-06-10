using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using DrawingClient.Network;
using SharedLib.Config;
using SharedLib.Payloads;

namespace DrawingClient.Forms
{
    public class LoginForm : Form
    {
        private readonly ClientNetwork _network;
        private TextBox txtUsername;
        private TextBox txtPassword;
        private TextBox txtServer;
        private Label lblStatus;
        private Button btnLogin;
        private Button btnRegister;

        public LoginForm()
        {
            _network = new ClientNetwork();
            txtUsername = new TextBox();
            this.Text = "Đăng nhập";
            this.Size = new Size(420, 290);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;

            BuildUi();
            SubscribeEvents();
            this.FormClosed += LoginForm_FormClosed;
        }

        private void BuildUi()
        {
            Label lblTitle = new Label
            {
                Text = "Drawing App - Login",
                AutoSize = true,
                Font = new Font("Segoe UI", 13, FontStyle.Bold),
                Location = new Point(20, 18)
            };

            Label lblServer = new Label { Text = "Server:", AutoSize = true, Location = new Point(20, 65) };
            txtServer = new TextBox
            {
                Location = new Point(120, 60),
                Width = 250,
                Text = EnvLoader.Get("USE_LOAD_BALANCER_ROUTING", "1") == "0"
                    ? EnvLoader.Get("SERVER_PUBLIC_HOST", "127.0.0.1")
                    : EnvLoader.Get("LOAD_BALANCER_HOST", "127.0.0.1")
            };

            Label lblUser = new Label { Text = "Tài khoản:", AutoSize = true, Location = new Point(20, 102) };
            txtUsername = new TextBox { Location = new Point(120, 97), Width = 250 };

            Label lblPass = new Label { Text = "Mật khẩu:", AutoSize = true, Location = new Point(20, 139) };
            txtPassword = new TextBox { Location = new Point(120, 134), Width = 250, UseSystemPasswordChar = true };

            btnLogin = new Button
            {
                Text = "Đăng nhập",
                Location = new Point(120, 178),
                Size = new Size(120, 34)
            };
            btnLogin.Click += BtnLogin_Click;

            btnRegister = new Button
            {
                Text = "Đăng ký",
                Location = new Point(250, 178),
                Size = new Size(120, 34)
            };
            btnRegister.Click += async (s, e) =>
            {
                // AUTH FLOW - BUOC 1A (REGISTER UI): user bam Dang ky, khoa nut de tranh gui trung nhieu request.
                SetAuthButtonsEnabled(false);
                try
                {
                    // AUTH FLOW - BUOC 2: dam bao da ket noi TCP/TLS toi server/LB truoc khi gui REGISTER.
                    if (!await EnsureConnectedAsync())
                        return;

                    // AUTH FLOW - BUOC 3A: dong goi username/password tren form va gui REGISTER qua ClientNetwork.
                    _network.SendRegister(txtUsername.Text.Trim(), txtPassword.Text);
                }
                finally
                {
                    SetAuthButtonsEnabled(true);
                }
            };

            lblStatus = new Label
            {
                AutoSize = false,
                Width = 350,
                Height = 28,
                Location = new Point(20, 220),
                ForeColor = Color.DimGray,
                Text = "Sẵn sàng"
            };

            this.Controls.Add(lblTitle);
            this.Controls.Add(lblServer);
            this.Controls.Add(txtServer);
            this.Controls.Add(lblUser);
            this.Controls.Add(txtUsername);
            this.Controls.Add(lblPass);
            this.Controls.Add(txtPassword);
            this.Controls.Add(btnLogin);
            this.Controls.Add(btnRegister);
            this.Controls.Add(lblStatus);
        }

        private void SubscribeEvents()
        {
            NetworkEvents.OnLoginResponse += NetworkEvents_OnLoginResponse;
            NetworkEvents.OnRegisterResponse += NetworkEvents_OnRegisterResponse;
            NetworkEvents.OnDisconnected += NetworkEvents_OnDisconnected;
        }

        private void LoginForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            NetworkEvents.OnLoginResponse -= NetworkEvents_OnLoginResponse;
            NetworkEvents.OnRegisterResponse -= NetworkEvents_OnRegisterResponse;
            NetworkEvents.OnDisconnected -= NetworkEvents_OnDisconnected;
        }

        private async void BtnLogin_Click(object sender, EventArgs e)
        {
            // AUTH FLOW - BUOC 1B (LOGIN UI): lay du lieu nguoi dung nhap tren form.
            string username = txtUsername.Text?.Trim();
            string password = txtPassword.Text ?? string.Empty;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show("Vui lòng nhập tài khoản và mật khẩu.");
                return;
            }

            SetAuthButtonsEnabled(false);
            try
            {
                // AUTH FLOW - BUOC 2: tao ket noi TCP/TLS neu client chua ket noi server.
                if (!await EnsureConnectedAsync())
                    return;

                lblStatus.Text = "Đang gửi thông tin xác thực...";
                // AUTH FLOW - BUOC 3B: gui LOGIN packet gom username/password sang server.
                _network.SendLogin(username, password);
            }
            finally
            {
                SetAuthButtonsEnabled(true);
            }
        }

        private async Task<bool> EnsureConnectedAsync()
        {
            if (_network.IsConnected)
                return true;

            string serverIp = txtServer.Text?.Trim();
            if (string.IsNullOrWhiteSpace(serverIp))
                serverIp = "127.0.0.1";

            int lbPort = EnvLoader.GetInt("LOAD_BALANCER_PORT", 9000);
            int lbUdpPort = EnvLoader.GetInt("LOAD_BALANCER_UDP_PORT", 9001);
            int directTcpPort = EnvLoader.GetInt("SERVER_TCP_PORT", 8888);
            int directUdpPort = EnvLoader.GetInt("SERVER_UDP_PORT", 8889);
            string lbMode = EnvLoader.Get("LOAD_BALANCER_CLIENT_MODE", "relay").Trim().ToLowerInvariant();
            bool useLoadBalancer = EnvLoader.Get("USE_LOAD_BALANCER_ROUTING", "1") != "0";
            bool useLbUdpProxy = EnvLoader.Get("LOAD_BALANCER_UDP_PROXY", "0") == "1";
            bool allowDirectFallback = EnvLoader.Get("CLIENT_ALLOW_DIRECT_FALLBACK", "0") == "1";
            bool forceTcpRealtime = EnvLoader.Get("CLIENT_FORCE_TCP_REALTIME", "0") == "1";

            bool connected = false;
            _network.PreferTcpRealtime = forceTcpRealtime;
            lblStatus.Text = "Đang kết nối...";

            if (useLoadBalancer && lbMode == "direct")
            {
                try
                {
                    // XU LY BAT DONG BO: hoi LoadBalancer va ket noi server tren task nen de UI login khong bi treo.
                    var route = await LoadBalancerRouteClient.ResolveAsync(serverIp, lbPort);
                    _network.SetAssignedServer(route.Host, route.TcpPort, route.UdpPort);
                    connected = await Task.Run(() => _network.Connect(route.Host, route.TcpPort, true));
                    if (connected)
                        lblStatus.Text = $"Da route toi {route.ServerName} ({route.Host}:{route.TcpPort})";
                }
                catch (Exception)
                {
                    connected = false;
                }
            }
            else if (useLoadBalancer)
            {
                string serverId = "";
                try
                {
                    // XU LY BAT DONG BO: resolve server id cua LB truoc khi relay TLS den backend.
                    var route = await LoadBalancerRouteClient.ResolveAsync(serverIp, lbPort);
                    serverId = route.ServerId ?? "";
                }
                catch
                {
                    serverId = "";
                }

                _network.SetAssignedServer(serverIp, lbPort, lbUdpPort, serverId);
                _network.PreferTcpRealtime = !useLbUdpProxy;
                // XU LY BAT DONG BO: ConnectRelay co the mat thoi gian handshake TCP/TLS, nen chay ngoai UI thread.
                connected = await Task.Run(() => _network.ConnectRelay(serverIp, lbPort, serverId));
                if (connected)
                    lblStatus.Text = useLbUdpProxy
                        ? $"Da ket noi LB relay {serverIp}:{lbPort}, UDP proxy {serverIp}:{lbUdpPort}"
                        : $"Da ket noi LB relay {serverIp}:{lbPort}";
            }

            if (!connected && (!useLoadBalancer || allowDirectFallback))
            {
                _network.PreferTcpRealtime = forceTcpRealtime;
                _network.SetAssignedServer(serverIp, directTcpPort, directUdpPort);
                // XU LY BAT DONG BO: fallback direct cung duoc dua vao Task.Run de form van phan hoi.
                connected = await Task.Run(() => _network.Connect(serverIp, directTcpPort, true));
            }

            if (!connected)
            {
                lblStatus.Text = "Không thể kết nối máy chủ.";
                return false;
            }

            return true;
        }

        private void SetAuthButtonsEnabled(bool enabled)
        {
            if (btnLogin != null) btnLogin.Enabled = enabled;
            if (btnRegister != null) btnRegister.Enabled = enabled;
        }

        private void NetworkEvents_OnLoginResponse(LoginResponse response)
        {
            if (this.IsHandleCreated && this.InvokeRequired)
            {
                // AUTH FLOW - BUOC 7: LOGIN_RESPONSE den tu network thread nen marshal ve UI thread truoc khi mo Lobby.
                this.BeginInvoke(new Action(() => NetworkEvents_OnLoginResponse(response)));
                return;
            }

            if (response != null && response.IsSuccess)
            {
                // AUTH FLOW - BUOC 8: dang nhap thanh cong, bo subscribe auth event va chuyen sang LobbyForm.
                lblStatus.ForeColor = Color.ForestGreen;
                lblStatus.Text = "Đăng nhập thành công.";
                NetworkEvents.OnLoginResponse -= NetworkEvents_OnLoginResponse;
                NetworkEvents.OnRegisterResponse -= NetworkEvents_OnRegisterResponse;
                NetworkEvents.OnDisconnected -= NetworkEvents_OnDisconnected;
                LobbyForm lobby = new LobbyForm(_network, response.Username ?? txtUsername.Text.Trim());
                this.Hide();
                lobby.FormClosed += (s, e) => this.Close();
                lobby.Show();
            }
            else
            {
                // AUTH FLOW - LOGIN UI: hien thi dung message server tra ve, vi du "Sai mat khau!" hoac "Tai khoan chua ton tai.".
                lblStatus.ForeColor = Color.Firebrick;
                lblStatus.Text = response?.Message ?? "Đăng nhập thất bại.";
            }
        }

        private void NetworkEvents_OnRegisterResponse(RegisterResponse response)
        {
            if (this.IsHandleCreated && this.InvokeRequired)
            {
                // AUTH FLOW - BUOC 7A: REGISTER_RESPONSE den tu network thread, cap nhat label phai ve UI thread.
                this.BeginInvoke(new Action(() => NetworkEvents_OnRegisterResponse(response)));
                return;
            }

            // AUTH FLOW - BUOC 8A: dang ky chi hien ket qua; user dang nhap lai bang nut Dang nhap.
            // Message nay di thang tu DbManager.RegisterAsync, nen co the bao trung ten hoac dang ky thanh cong.
            lblStatus.ForeColor = response != null && response.IsSuccess ? Color.ForestGreen : Color.Firebrick;
            lblStatus.Text = response?.Message ?? "Đăng ký xong.";
        }

        private void NetworkEvents_OnDisconnected()
        {
            if (this.IsHandleCreated && this.InvokeRequired)
            {
                this.BeginInvoke(new Action(NetworkEvents_OnDisconnected));
                return;
            }

            lblStatus.Text = "Mất kết nối máy chủ.";
        }
    }
}
