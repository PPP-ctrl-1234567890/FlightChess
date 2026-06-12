namespace FlightChess.Client
{
    partial class MainForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        // ---- 控件字段 ----
        private System.Windows.Forms.Panel topPanel;
        private System.Windows.Forms.Label lblCurrentPlayer, lblDiceValue;
        private System.Windows.Forms.Button btnRollDice;
        private System.Windows.Forms.Panel boardPanel;
        private System.Windows.Forms.ListBox lstLog;
        private System.Windows.Forms.GroupBox chatPanel;
        private System.Windows.Forms.RichTextBox rtbChat;
        private System.Windows.Forms.FlowLayoutPanel flpChatButtons;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.topPanel = new System.Windows.Forms.Panel();
            this.lblCurrentPlayer = new System.Windows.Forms.Label();
            this.lblDiceValue = new System.Windows.Forms.Label();
            this.btnRollDice = new System.Windows.Forms.Button();
            this.boardPanel = new System.Windows.Forms.Panel();
            this.lstLog = new System.Windows.Forms.ListBox();
            this.chatPanel = new System.Windows.Forms.GroupBox();
            this.rtbChat = new System.Windows.Forms.RichTextBox();
            this.flpChatButtons = new System.Windows.Forms.FlowLayoutPanel();
            this.topPanel.SuspendLayout();
            this.chatPanel.SuspendLayout();
            this.SuspendLayout();
            //
            // topPanel
            //
            this.topPanel.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(250)))), ((int)(((byte)(248)))), ((int)(((byte)(240)))));
            this.topPanel.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.topPanel.Controls.Add(this.lblCurrentPlayer);
            this.topPanel.Controls.Add(this.lblDiceValue);
            this.topPanel.Controls.Add(this.btnRollDice);
            this.topPanel.Location = new System.Drawing.Point(10, 8);
            this.topPanel.Name = "topPanel";
            this.topPanel.Size = new System.Drawing.Size(910, 44);
            this.topPanel.TabIndex = 0;
            //
            // lblCurrentPlayer
            //
            this.lblCurrentPlayer.Font = new System.Drawing.Font("微软雅黑", 11F, System.Drawing.FontStyle.Bold);
            this.lblCurrentPlayer.Location = new System.Drawing.Point(12, 10);
            this.lblCurrentPlayer.Name = "lblCurrentPlayer";
            this.lblCurrentPlayer.Size = new System.Drawing.Size(340, 24);
            this.lblCurrentPlayer.TabIndex = 0;
            this.lblCurrentPlayer.Text = "等待连接...";
            //
            // lblDiceValue
            //
            this.lblDiceValue.BackColor = System.Drawing.Color.WhiteSmoke;
            this.lblDiceValue.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.lblDiceValue.Font = new System.Drawing.Font("微软雅黑", 13F, System.Drawing.FontStyle.Bold);
            this.lblDiceValue.Location = new System.Drawing.Point(365, 8);
            this.lblDiceValue.Name = "lblDiceValue";
            this.lblDiceValue.Size = new System.Drawing.Size(75, 28);
            this.lblDiceValue.TabIndex = 1;
            this.lblDiceValue.Text = "骰子: -";
            this.lblDiceValue.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            //
            // btnRollDice
            //
            this.btnRollDice.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(220)))), ((int)(((byte)(240)))), ((int)(((byte)(255)))));
            this.btnRollDice.Enabled = false;
            this.btnRollDice.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnRollDice.Font = new System.Drawing.Font("微软雅黑", 10F, System.Drawing.FontStyle.Bold);
            this.btnRollDice.Location = new System.Drawing.Point(455, 7);
            this.btnRollDice.Name = "btnRollDice";
            this.btnRollDice.Size = new System.Drawing.Size(110, 32);
            this.btnRollDice.TabIndex = 2;
            this.btnRollDice.Text = "掷骰子";
            this.btnRollDice.UseVisualStyleBackColor = false;
            //
            // boardPanel
            //
            this.boardPanel.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(248)))), ((int)(((byte)(242)))), ((int)(((byte)(225)))));
            this.boardPanel.Location = new System.Drawing.Point(10, 58);
            this.boardPanel.Name = "boardPanel";
            this.boardPanel.Size = new System.Drawing.Size(700, 700);
            this.boardPanel.TabIndex = 1;
            //
            // lstLog
            //
            this.lstLog.BackColor = System.Drawing.Color.White;
            this.lstLog.Font = new System.Drawing.Font("微软雅黑", 9F);
            this.lstLog.HorizontalScrollbar = true;
            this.lstLog.Location = new System.Drawing.Point(10, 765);
            this.lstLog.Name = "lstLog";
            this.lstLog.Size = new System.Drawing.Size(910, 100);
            this.lstLog.TabIndex = 2;
            //
            // chatPanel
            //
            this.chatPanel.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(248)))), ((int)(((byte)(245)))), ((int)(((byte)(238)))));
            this.chatPanel.Controls.Add(this.rtbChat);
            this.chatPanel.Controls.Add(this.flpChatButtons);
            this.chatPanel.Font = new System.Drawing.Font("微软雅黑", 9F);
            this.chatPanel.Location = new System.Drawing.Point(720, 58);
            this.chatPanel.Name = "chatPanel";
            this.chatPanel.Size = new System.Drawing.Size(200, 700);
            this.chatPanel.TabIndex = 3;
            this.chatPanel.TabStop = false;
            this.chatPanel.Text = "聊天";
            //
            // rtbChat
            //
            this.rtbChat.BackColor = System.Drawing.Color.White;
            this.rtbChat.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.rtbChat.Font = new System.Drawing.Font("微软雅黑", 9F);
            this.rtbChat.Location = new System.Drawing.Point(6, 22);
            this.rtbChat.Name = "rtbChat";
            this.rtbChat.ReadOnly = true;
            this.rtbChat.ScrollBars = System.Windows.Forms.RichTextBoxScrollBars.Vertical;
            this.rtbChat.Size = new System.Drawing.Size(188, 460);
            this.rtbChat.TabIndex = 0;
            this.rtbChat.Text = "";
            //
            // flpChatButtons
            //
            this.flpChatButtons.AutoScroll = true;
            this.flpChatButtons.BackColor = System.Drawing.Color.Transparent;
            this.flpChatButtons.Location = new System.Drawing.Point(6, 490);
            this.flpChatButtons.Name = "flpChatButtons";
            this.flpChatButtons.Size = new System.Drawing.Size(188, 204);
            this.flpChatButtons.TabIndex = 1;
            //
            // MainForm
            //
            this.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(240)))), ((int)(((byte)(235)))), ((int)(((byte)(220)))));
            this.ClientSize = new System.Drawing.Size(930, 885);
            this.Controls.Add(this.topPanel);
            this.Controls.Add(this.boardPanel);
            this.Controls.Add(this.lstLog);
            this.Controls.Add(this.chatPanel);
            this.MinimumSize = new System.Drawing.Size(930, 875);
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "飞行棋联机游戏";
            this.topPanel.ResumeLayout(false);
            this.chatPanel.ResumeLayout(false);
            this.ResumeLayout(false);

        }
    }
}
