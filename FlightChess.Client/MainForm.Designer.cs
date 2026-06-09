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
        private System.Windows.Forms.Button btnRollDice, btnReset;
        private System.Windows.Forms.Panel boardPanel;
        private System.Windows.Forms.ListBox lstLog;
        private System.Windows.Forms.ToolTip _toolTip;

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
            this._toolTip = new System.Windows.Forms.ToolTip(this.components);
            this.topPanel = new System.Windows.Forms.Panel();
            this.lblCurrentPlayer = new System.Windows.Forms.Label();
            this.lblDiceValue = new System.Windows.Forms.Label();
            this.btnRollDice = new System.Windows.Forms.Button();
            this.btnReset = new System.Windows.Forms.Button();
            this.boardPanel = new System.Windows.Forms.Panel();
            this.lstLog = new System.Windows.Forms.ListBox();
            this.topPanel.SuspendLayout();
            this.SuspendLayout();
            // 
            // topPanel
            // 
            this.topPanel.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(250)))), ((int)(((byte)(248)))), ((int)(((byte)(240)))));
            this.topPanel.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.topPanel.Controls.Add(this.lblCurrentPlayer);
            this.topPanel.Controls.Add(this.lblDiceValue);
            this.topPanel.Controls.Add(this.btnRollDice);
            this.topPanel.Controls.Add(this.btnReset);
            this.topPanel.Location = new System.Drawing.Point(10, 8);
            this.topPanel.Name = "topPanel";
            this.topPanel.Size = new System.Drawing.Size(690, 44);
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
            // btnReset
            // 
            this.btnReset.Enabled = false;
            this.btnReset.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnReset.Font = new System.Drawing.Font("微软雅黑", 9F);
            this.btnReset.Location = new System.Drawing.Point(580, 7);
            this.btnReset.Name = "btnReset";
            this.btnReset.Size = new System.Drawing.Size(60, 32);
            this.btnReset.TabIndex = 3;
            this.btnReset.Text = "重置";
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
            this.lstLog.Size = new System.Drawing.Size(690, 18);
            this.lstLog.TabIndex = 2;
            // 
            // MainForm
            // 
            this.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(240)))), ((int)(((byte)(235)))), ((int)(((byte)(220)))));
            this.ClientSize = new System.Drawing.Size(710, 785);
            this.Controls.Add(this.topPanel);
            this.Controls.Add(this.boardPanel);
            this.Controls.Add(this.lstLog);
            this.MinimumSize = new System.Drawing.Size(690, 775);
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "飞行棋联机游戏";
            this.topPanel.ResumeLayout(false);
            this.ResumeLayout(false);

        }
    }
}
