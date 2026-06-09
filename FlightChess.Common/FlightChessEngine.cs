using System;
using System.Collections.Generic;

namespace FlightChess.Common
{
    /// <summary>
    /// 移动操作的结果
    /// </summary>
    public class MoveResult
    {
        /// <summary>操作是否成功</summary>
        public bool Success { get; set; }

        /// <summary>结果描述消息</summary>
        public string Message { get; set; }

        /// <summary>被踩回起点的棋子信息（玩家索引, 棋子索引）</summary>
        public List<Tuple<int, int>> KickedPieces { get; set; }

        /// <summary>是否获得额外回合（掷出 6 点且有合法移动）</summary>
        public bool ExtraTurn { get; set; }

        /// <summary>游戏是否结束</summary>
        public bool GameOver { get; set; }

        /// <summary>获胜玩家索引</summary>
        public int WinnerIndex { get; set; }

        public MoveResult()
        {
            Success = false;
            Message = string.Empty;
            KickedPieces = new List<Tuple<int, int>>();
            ExtraTurn = false;
            GameOver = false;
            WinnerIndex = -1;
        }
    }

    /// <summary>
    /// 飞行棋核心引擎，实现完整的飞行棋规则。
    /// 棋子位置定义：
    ///   -1 = 未起飞（在基地内）
    ///   -2 = StartPosition（在 START 标记上，等待进入主路径）
    ///    0~51 = 在主路径上前进了 N 步（从各自起点的偏移）
    ///   52~57 = 在归营路径上（6 格，从外缘经臂中央通向中心）
    ///   58 = GoalPosition（到达终点/完成）
    ///
    /// 任意棋子所在的绝对格子索引 = (玩家偏移 + 棋子步数位置) % 52（仅主路径0~51）
    /// 安全格（绝对索引）：0, 13, 26, 39, 50
    /// </summary>
    public class FlightChessEngine
    {
        /// <summary>棋盘主路径格子数</summary>
        public const int BoardSize = 52;

        /// <summary>归营路径起始位置</summary>
        public const int FinishStart = 52;

        /// <summary>归营路径结束位置（最后一格，恰好走到即完成）</summary>
        public const int FinishEnd = 57;

        /// <summary>起点（START标记）位置</summary>
        public const int StartPosition = -2;

        /// <summary>终点位置（完成）</summary>
        public const int GoalPosition = 58;

        /// <summary>安全格（绝对格子索引），不会被踩</summary>
        public static readonly HashSet<int> SafeCells = new HashSet<int> { 0, 13, 26, 39, 50 };

        /// <summary>
        /// 主路径格子颜色序列：绿(1)→红(0)→蓝(3)→黄(2) 循环。
        /// 与客户端 MainForm._cellColorType 保持一致。
        /// </summary>
        private static readonly int[] ColorSeq = { 1, 0, 3, 2 };

        /// <summary>
        /// 获取主路径上指定步数位置的格子颜色类型。
        /// </summary>
        /// <param name="position">棋子步数位置（0~51）</param>
        /// <returns>颜色类型：0=红, 1=绿, 2=黄, 3=蓝</returns>
        public static int GetCellColorType(int position)
        {
            if (position < 0 || position >= BoardSize)
                return -1;
            return ColorSeq[position % 4];
        }

        private Random _random;

        public FlightChessEngine()
        {
            _random = new Random();
        }

        public FlightChessEngine(int seed)
        {
            _random = new Random(seed);
        }

        /// <summary>
        /// 将棋子的"步数位置"转换为绝对格子索引。
        /// 仅对主路径位置（0~51）有效；未起飞、已到终点或归营路径返回 -1。
        /// </summary>
        public static int ToAbsoluteIndex(int playerOffset, int piecePosition)
        {
            if (piecePosition < 0 || piecePosition >= BoardSize)
                return -1;
            return (playerOffset + piecePosition) % BoardSize;
        }

        /// <summary>
        /// 掷骰子，返回 1~6 的随机值。
        /// </summary>
        public int RollDice()
        {
            return _random.Next(1, 7);
        }

        /// <summary>
        /// 获取当前骰子点数下，指定玩家可以移动的棋子索引列表。
        /// 支持主路径、归营路径（含回退逻辑）。
        /// </summary>
        public List<int> GetValidMoves(Player player, int diceValue)
        {
            List<int> validMoves = new List<int>();

            if (diceValue <= 0 || diceValue > 6)
                return validMoves;

            for (int i = 0; i < 4; i++)
            {
                int pos = player.Pieces[i];

                // 已在终点，不可移动
                if (pos == GoalPosition)
                    continue;

                // 未起飞：只有掷出 6 才能起飞
                if (pos == -1)
                {
                    if (diceValue == 6)
                        validMoves.Add(i);
                    continue;
                }

                // 在START位置：可以进入主路径（任何点数）
                if (pos == StartPosition)
                {
                    // 从START出发走diceValue步，第一步到达位置0
                    // diceValue-1 <= 51+6=57，但在主路径最多到51，
                    // 到达52~57即进入归营路径，最大可接受值是57（恰好完成）
                    if (diceValue - 1 <= FinishEnd)
                        validMoves.Add(i);
                    continue;
                }

                // 在主路径上（0~51）
                if (pos >= 0 && pos < BoardSize)
                {
                    // 进入归营路径或留在主路径
                    // 最大前进到 FinishEnd=57（恰好完成）
                    if (pos + diceValue <= FinishEnd)
                        validMoves.Add(i);
                    continue;
                }

                // 在归营路径上（52~57）
                if (pos >= FinishStart && pos <= FinishEnd)
                {
                    // 归营路径上任何骰子点数都可移动（超出则回退）
                    validMoves.Add(i);
                    continue;
                }
            }

            return validMoves;
        }

        /// <summary>
        /// 执行移动操作。调用前应确保该移动合法。
        /// 支持：起飞、主路径移动、进入归营路径、归营路径内移动与回退。
        /// </summary>
        /// <param name="gameState">当前游戏状态（会被修改）</param>
        /// <param name="playerIndex">移动的玩家索引</param>
        /// <param name="pieceIndex">移动的棋子索引（0~3）</param>
        /// <returns>移动结果</returns>
        public MoveResult MovePiece(GameState gameState, int playerIndex, int pieceIndex)
        {
            MoveResult result = new MoveResult();
            Player player = gameState.Players[playerIndex];
            int diceValue = gameState.DiceValue;

            if (diceValue <= 0)
            {
                result.Success = false;
                result.Message = "尚未掷骰子，无法移动。";
                return result;
            }

            if (pieceIndex < 0 || pieceIndex > 3)
            {
                result.Success = false;
                result.Message = "棋子索引无效。";
                return result;
            }

            int currentPos = player.Pieces[pieceIndex];

            // 已到终点
            if (currentPos == GoalPosition)
            {
                result.Success = false;
                result.Message = "该棋子已到达终点，无法移动。";
                return result;
            }

            // ===== 未起飞 =====
            if (currentPos == -1)
            {
                if (diceValue != 6)
                {
                    result.Success = false;
                    result.Message = "只有掷出 6 点才能起飞。";
                    return result;
                }
                // 起飞：先移动到START标记位置
                player.Pieces[pieceIndex] = StartPosition;
                result.Success = true;
                result.Message = string.Format("{0} 的棋子 {1} 起飞到START位置！", player.Name, pieceIndex + 1);
                result.ExtraTurn = true; // 掷出 6 点获得额外回合
                return result;
            }

            // ===== 在START位置：进入主路径 =====
            if (currentPos == StartPosition)
            {
                int newPos = diceValue - 1; // 第一步到达位置0

                if (newPos > FinishEnd)
                {
                    result.Success = false;
                    result.Message = "移动后会超出终点，无法移动。";
                    return result;
                }

                // 若恰好到达 FinishEnd=57，直接完成
                if (newPos == FinishEnd)
                {
                    player.Pieces[pieceIndex] = GoalPosition;
                    result.Success = true;
                    result.Message = string.Format("{0} 的棋子 {1} 从START起飞并直接到达终点！",
                        player.Name, pieceIndex + 1);
                }
                else
                {
                    player.Pieces[pieceIndex] = newPos;
                    result.Success = true;
                    result.Message = string.Format("{0} 的棋子 {1} 从START进入主路径位置 {2}。",
                        player.Name, pieceIndex + 1, newPos);
                }

                if (diceValue == 6)
                    result.ExtraTurn = true;

                // 踩子检查
                DoKickCheck(gameState, playerIndex, pieceIndex, result);

                // 同色跳（仅当棋子仍在主路径上时触发）
                DoSameColorJump(gameState, playerIndex, pieceIndex, result);

                // 胜利检查
                DoWinCheck(gameState, playerIndex, result);

                return result;
            }

            // ===== 在主路径上（0~51）=====
            if (currentPos >= 0 && currentPos < BoardSize)
            {
                int newPos = currentPos + diceValue;

                if (newPos > FinishEnd)
                {
                    result.Success = false;
                    result.Message = "移动后会超出终点，无法移动。";
                    return result;
                }

                // 恰好到达 FinishEnd=57：直接完成
                if (newPos == FinishEnd)
                {
                    player.Pieces[pieceIndex] = GoalPosition;
                    result.Success = true;
                    result.Message = string.Format("{0} 的棋子 {1} 从 {2} 移动到终点！",
                        player.Name, pieceIndex + 1, currentPos);
                }
                else
                {
                    player.Pieces[pieceIndex] = newPos;
                    result.Success = true;
                    result.Message = string.Format("{0} 的棋子 {1} 从 {2} 移动到 {3}。",
                        player.Name, pieceIndex + 1, currentPos, newPos);
                }

                // 掷出 6 点获得额外回合
                if (diceValue == 6)
                    result.ExtraTurn = true;

                // 踩子检查（仅当在主路径上时）
                if (newPos < BoardSize)
                {
                    DoKickCheck(gameState, playerIndex, pieceIndex, result);
                }

                // 同色跳（仅当棋子仍在主路径上时触发）
                DoSameColorJump(gameState, playerIndex, pieceIndex, result);

                // 胜利检查
                DoWinCheck(gameState, playerIndex, result);

                return result;
            }

            // ===== 在归营路径上（52~57）=====
            if (currentPos >= FinishStart && currentPos <= FinishEnd)
            {
                int newPos = currentPos + diceValue;

                if (newPos == FinishEnd)
                {
                    // 恰好走到 57：完成
                    player.Pieces[pieceIndex] = GoalPosition;
                    result.Success = true;
                    result.Message = string.Format("{0} 的棋子 {1} 在归营路径上从 {2} 走到终点！",
                        player.Name, pieceIndex + 1, currentPos);
                }
                else if (newPos > FinishEnd)
                {
                    // 超出终点 57：回退
                    int overshoot = newPos - FinishEnd;
                    newPos = FinishEnd - overshoot;
                    // 确保回退后仍在归营路径内（dice最大6，从52出发最多到58，回退后最少56，不会低于52）
                    if (newPos < FinishStart)
                        newPos = FinishStart;

                    player.Pieces[pieceIndex] = newPos;
                    result.Success = true;
                    result.Message = string.Format("{0} 的棋子 {1} 在归营路径上从 {2} 移动（超出回退）到 {3}。",
                        player.Name, pieceIndex + 1, currentPos, newPos);
                }
                else
                {
                    // 正常归营路径内移动
                    player.Pieces[pieceIndex] = newPos;
                    result.Success = true;
                    result.Message = string.Format("{0} 的棋子 {1} 在归营路径上从 {2} 移动到 {3}。",
                        player.Name, pieceIndex + 1, currentPos, newPos);
                }

                // 掷出 6 点获得额外回合
                if (diceValue == 6)
                    result.ExtraTurn = true;

                // 归营路径无踩子

                // 胜利检查
                DoWinCheck(gameState, playerIndex, result);

                return result;
            }

            // 不应到达这里
            result.Success = false;
            result.Message = "未知的棋子位置。";
            return result;
        }

        /// <summary>执行踩子检查（主路径上）</summary>
        private void DoKickCheck(GameState gameState, int playerIndex, int pieceIndex, MoveResult result)
        {
            Player player = gameState.Players[playerIndex];
            int movingPos = player.Pieces[pieceIndex];

            if (movingPos < 0 || movingPos >= BoardSize)
                return;

            int movingAbsIndex = ToAbsoluteIndex(player.StartOffset, movingPos);

            // 安全格不会被踩
            if (SafeCells.Contains(movingAbsIndex))
                return;

            for (int p = 0; p < 4; p++)
            {
                if (p == playerIndex) continue;

                Player other = gameState.Players[p];
                for (int q = 0; q < 4; q++)
                {
                    int otherPos = other.Pieces[q];
                    if (otherPos < 0 || otherPos >= BoardSize) continue;

                    int otherAbsIndex = ToAbsoluteIndex(other.StartOffset, otherPos);
                    if (otherAbsIndex == movingAbsIndex)
                    {
                        // 踩到！送回基地
                        other.Pieces[q] = -1;
                        result.KickedPieces.Add(new Tuple<int, int>(p, q));
                        result.Message += string.Format(" 踩到了 {0} 的棋子 {1}，将其送回基地！",
                            other.Name, q + 1);
                    }
                }
            }
        }

        /// <summary>检查胜利条件</summary>
        private void DoWinCheck(GameState gameState, int playerIndex, MoveResult result)
        {
            Player player = gameState.Players[playerIndex];
            if (player.HasWon())
            {
                result.GameOver = true;
                result.WinnerIndex = playerIndex;
                gameState.WinnerIndex = playerIndex;
                result.Message += string.Format(" {0} 获得胜利！", player.Name);
                result.ExtraTurn = false;
            }
        }

        /// <summary>
        /// 同色跳：如果棋子落在与自身颜色相同的主路径格子上，向前跳到下一个同色格（+4）。
        /// 仅在主路径（0~51）上触发，可能跳入归营路径（52~55）。
        /// </summary>
        /// <returns>true 如果发生了跳跃</returns>
        private bool DoSameColorJump(GameState gameState, int playerIndex, int pieceIndex, MoveResult result)
        {
            Player player = gameState.Players[playerIndex];
            int pos = player.Pieces[pieceIndex];

            if (pos < 0 || pos >= BoardSize)
                return false;

            // 使用绝对格子索引匹配棋盘上的实际颜色（而非相对玩家的步数位置）
            int absIndex = ToAbsoluteIndex(player.StartOffset, pos);
            if (GetCellColorType(absIndex) != playerIndex)
                return false;

            int jumpPos = pos + 4;

            if (jumpPos >= BoardSize)
            {
                // 跳入归营路径（52~55）
                player.Pieces[pieceIndex] = jumpPos;
                result.Message += string.Format(" 落在同色格上，从 {0} 跳到归营路径 {1}！", pos, jumpPos);
            }
            else
            {
                // 仍在主路径
                player.Pieces[pieceIndex] = jumpPos;
                result.Message += string.Format(" 落在同色格上，从 {0} 跳到 {1}！", pos, jumpPos);
                // 跳跃后踩子检查
                DoKickCheck(gameState, playerIndex, pieceIndex, result);
            }

            return true;
        }

        /// <summary>
        /// 切换到下一个连接的玩家。
        /// </summary>
        public int GetNextPlayerIndex(GameState gameState)
        {
            int count = gameState.Players.Length;
            for (int i = 1; i <= count; i++)
            {
                int next = (gameState.CurrentPlayerIndex + i) % count;
                if (gameState.Players[next].IsConnected)
                    return next;
            }
            return gameState.CurrentPlayerIndex; // 不应发生
        }

        /// <summary>
        /// 将 GameState 加载到引擎（客户端用于本地计算合法移动）
        /// </summary>
        public static GameState ApplyGameState(GameState source)
        {
            // 直接返回深拷贝供客户端使用
            return source.DeepCopy();
        }
    }
}
