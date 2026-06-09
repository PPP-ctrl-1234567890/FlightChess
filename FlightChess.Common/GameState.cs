using System;

namespace FlightChess.Common
{
    /// <summary>
    /// 完整的游戏状态，包含所有玩家信息、当前回合、骰子点数、胜负状态。
    /// 由服务器维护并通过广播同步到所有客户端。
    /// </summary>
    [Serializable]
    public class GameState
    {
        /// <summary>4 个玩家（索引 0~3）</summary>
        public Player[] Players { get; set; }

        /// <summary>当前轮到哪个玩家（索引 0~3）</summary>
        public int CurrentPlayerIndex { get; set; }

        /// <summary>当前骰子点数（0 表示尚未掷骰子）</summary>
        public int DiceValue { get; set; }

        /// <summary>获胜玩家索引（-1 表示尚无胜者）</summary>
        public int WinnerIndex { get; set; }

        /// <summary>游戏是否已结束（所有已连接玩家均完成）</summary>
        public bool GameOver => FinishCount >= ConnectedPlayerCount && ConnectedPlayerCount > 0;

        /// <summary>已完成归营的玩家数量</summary>
        public int FinishCount { get; set; }

        /// <summary>当前已连接玩家数量</summary>
        public int ConnectedPlayerCount { get; set; }

        /// <summary>游戏日志消息列表</summary>
        public string[] LogMessages { get; set; }

        public GameState()
        {
            // 四个玩家的起始偏移：红0, 绿13, 黄26, 蓝39
            Players = new Player[4];
            int[] offsets = new int[] { 0, 39, 26, 13 };
            string[] colors = new string[] { "红", "绿", "黄", "蓝" };
            for (int i = 0; i < 4; i++)
            {
                Players[i] = new Player(i, colors[i], offsets[i]);
                Players[i].IsConnected = false; // 初始无人连接
            }
            CurrentPlayerIndex = 0;
            DiceValue = 0;
            WinnerIndex = -1;
            FinishCount = 0;
            ConnectedPlayerCount = 0;
            LogMessages = new string[0];
        }

        /// <summary>深拷贝（用于客户端本地模拟）</summary>
        public GameState DeepCopy()
        {
            GameState copy = new GameState();
            copy.CurrentPlayerIndex = this.CurrentPlayerIndex;
            copy.DiceValue = this.DiceValue;
            copy.WinnerIndex = this.WinnerIndex;
            copy.FinishCount = this.FinishCount;
            copy.ConnectedPlayerCount = this.ConnectedPlayerCount;
            for (int i = 0; i < 4; i++)
            {
                copy.Players[i].Id = this.Players[i].Id;
                copy.Players[i].Name = this.Players[i].Name;
                copy.Players[i].StartOffset = this.Players[i].StartOffset;
                copy.Players[i].IsConnected = this.Players[i].IsConnected;
                copy.Players[i].Rank = this.Players[i].Rank;
                for (int j = 0; j < 4; j++)
                {
                    copy.Players[i].Pieces[j] = this.Players[i].Pieces[j];
                }
            }
            if (this.LogMessages != null)
            {
                copy.LogMessages = new string[this.LogMessages.Length];
                Array.Copy(this.LogMessages, copy.LogMessages, this.LogMessages.Length);
            }
            return copy;
        }
    }
}
