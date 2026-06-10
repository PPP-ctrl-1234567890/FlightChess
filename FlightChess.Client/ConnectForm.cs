using System;
using System.Drawing;
using System.Windows.Forms;

namespace FlightChess.Client
{
    /// <summary>
    /// 连接对话框，输入服务器 IP 和端口。
    /// </summary>
    public class ConnectForm : Form
    {
        private Label lblTitle;
        private Label lblServer;
        private TextBox txtServer;
        private Label lblPort;
        private TextBox txtPort;
        private Label lblPlayerName;
        private TextBox txtPlayerName;
        private Button btnConnect;
        private Button btnCancel;

        /// <summary>服务器 IP 地址</summary>
        public string ServerAddress { get; private set; }

        /// <summary>服务器端口</summary>
        public int ServerPort { get; private set; }

        /// <summary>玩家名称</summary>
        public string PlayerName { get; private set; }

        public ConnectForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "连接服务器";
            this.Size = new Size(350, 250);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            lblTitle = new Label
            {
                Text = "飞行棋联机游戏 - 连接服务器",
                Font = new Font("微软雅黑", 12F, FontStyle.Bold),
                Location = new Point(20, 15),
                Size = new Size(300, 30),
                TextAlign = ContentAlignment.MiddleCenter
            };

            lblServer = new Label
            {
                Text = "服务器 IP:",
                Location = new Point(30, 60),
                Size = new Size(80, 25),
                TextAlign = ContentAlignment.MiddleRight
            };

            txtServer = new TextBox
            {
                Text = "127.0.0.1",
                Location = new Point(115, 60),
                Size = new Size(180, 25)
            };

            lblPort = new Label
            {
                Text = "端口:",
                Location = new Point(30, 95),
                Size = new Size(80, 25),
                TextAlign = ContentAlignment.MiddleRight
            };

            txtPort = new TextBox
            {
                Text = "8888",
                Location = new Point(115, 95),
                Size = new Size(80, 25)
            };

            lblPlayerName = new Label
            {
                Text = "玩家名称:",
                Location = new Point(30, 130),
                Size = new Size(80, 25),
                TextAlign = ContentAlignment.MiddleRight
            };

            txtPlayerName = new TextBox
            {
                Text = "Player",
                Location = new Point(115, 130),
                Size = new Size(180, 25)
            };

            btnConnect = new Button
            {
                Text = "连接",
                Location = new Point(80, 170),
                Size = new Size(80, 30),
                DialogResult = DialogResult.OK
            };
            btnConnect.Click += BtnConnect_Click;

            btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(180, 170),
                Size = new Size(80, 30),
                DialogResult = DialogResult.Cancel
            };

            this.Controls.AddRange(new Control[]
            {
                lblTitle, lblServer, txtServer, lblPort, txtPort,
                lblPlayerName, txtPlayerName, btnConnect, btnCancel
            });

            this.AcceptButton = btnConnect;
            this.CancelButton = btnCancel;
        }

        private void BtnConnect_Click(object sender, EventArgs e)
        {
            ServerAddress = txtServer.Text.Trim();

            if (string.IsNullOrEmpty(ServerAddress))
            {
                MessageBox.Show("请输入服务器 IP 地址。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                this.DialogResult = DialogResult.None;
                return;
            }

            if (!int.TryParse(txtPort.Text.Trim(), out int port) || port <= 0 || port > 65535)
            {
                MessageBox.Show("请输入有效的端口号（1~65535）。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                this.DialogResult = DialogResult.None;
                return;
            }

            ServerPort = port;
            PlayerName = txtPlayerName.Text.Trim();
            if (string.IsNullOrEmpty(PlayerName))
                PlayerName = "Player";
        }
    }
}
