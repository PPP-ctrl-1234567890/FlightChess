using System;

namespace FlightChess.Common
{
    /// <summary>
    /// 表示一个玩家，包含玩家 ID、名称、4 个棋子的位置以及连接状态。
    /// 棋子位置：-1 = 未起飞，0~51 = 主路径步数，52~57 = 归营路径，58 = 已到达终点。
    /// </summary>
    [Serializable]
    public class Player
    {
        /// <summary>玩家 ID（0~3），对应颜色：0=红, 1=绿, 2=黄, 3=蓝</summary>
        public int Id { get; set; }

        /// <summary>玩家名称</summary>
        public string Name { get; set; }

        /// <summary>
        /// 4 个棋子的位置。
        /// -1 = 未起飞，0~51 = 主路径步数，52~57 = 归营路径，58 = 已到达终点。
        /// </summary>
        public int[] Pieces { get; set; }

        /// <summary>是否曾经加入过游戏（区分"从未加入"和"掉线"）</summary>
        public bool HasJoined { get; set; }

        /// <summary>是否仍然连接</summary>
        public bool IsConnected { get; set; }

        /// <summary>排名：0=未完成，1=第一名，2=第二名，3=第三名，4=第四名</summary>
        public int Rank { get; set; }

        /// <summary>棋盘路径起始偏移（绝对格子索引）</summary>
        public int StartOffset { get; set; }

        public Player()
        {
            Pieces = new int[4] { -1, -1, -1, -1 };
            IsConnected = true;
        }

        public Player(int id, string name, int startOffset)
        {
            Id = id;
            Name = name;
            Pieces = new int[4] { -1, -1, -1, -1 };
            IsConnected = true;
            StartOffset = startOffset;
        }

        /// <summary>该玩家是否已经获胜（4 个棋子全部到达终点）</summary>
        public bool HasWon()
        {
            foreach (int p in Pieces)
            {
                if (p != FlightChessEngine.GoalPosition)
                    return false;
            }
            return true;
        }
    }
}
