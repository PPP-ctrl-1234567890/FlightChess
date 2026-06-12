using System;
using System.Collections.Generic;

namespace FlightChess.Common
{
    /// <summary>
    /// 移动操作的结果。
    /// </summary>
    public class MoveResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        /// <summary>被踩回基地的棋子信息（玩家索引, 棋子索引）</summary>
        public List<Tuple<int, int>> KickedPieces { get; set; }
        /// <summary>是否获得额外回合（掷出 6 点）</summary>
        public bool ExtraTurn { get; set; }
        public bool GameOver { get; set; }
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
    /// 飞行棋核心引擎。
    /// 位置编码：-1=基地, -2=START, 0~51=主路径, 52~57=归营路径, 58=终点。
    /// 绝对格子 = (玩家偏移 + 位置) % 52。安全格：{0,13,26,39,50}。
    /// </summary>
    public class FlightChessEngine
    {
        public const int BoardSize = 52;
        public const int FinishStart = 52;
        public const int FinishEnd = 57;
        public const int StartPosition = -2;
        public const int GoalPosition = 58;

        /// <summary>绝对格子索引上的安全格（不可被踩）</summary>
        public static readonly HashSet<int> SafeCells = new HashSet<int> { 0, 13, 26, 39, 50 };

        /// <summary>四名玩家的起始偏移（红绿黄蓝，逆时针）</summary>
        public static readonly int[] PlayerStartOffsets = { 0, 39, 26, 13 };

        /// <summary>四名玩家的颜色名称</summary>
        public static readonly string[] PlayerColorNames = { "红", "绿", "黄", "蓝" };

        /// <summary>主路径格子颜色序列：绿→红→蓝→黄，与客户端 _cellColorType 一致。</summary>
        private static readonly int[] ColorSeq = { 1, 0, 3, 2 };

        /// <summary>获取主路径指定步数位置的格子颜色。0=红,1=绿,2=黄,3=蓝。</summary>
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

        /// <summary>步数位置→绝对格子索引。仅主路径(0~51)有效，否则返回-1。</summary>
        public static int ToAbsoluteIndex(int playerOffset, int piecePosition)
        {
            if (piecePosition < 0 || piecePosition >= BoardSize)
                return -1;
            return (playerOffset + piecePosition) % BoardSize;
        }

        public int RollDice()
        {
            return _random.Next(1, 7);
        }

        /// <summary>获取当前骰子点数下可移动的棋子索引列表。</summary>
        public List<int> GetValidMoves(Player player, int diceValue)
        {
            List<int> validMoves = new List<int>();

            // 已完成归营的玩家无需移动
            if (player.Rank > 0)
                return validMoves;

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

        /// <summary>执行移动操作。调用前应确保移动合法。</summary>
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
                    DoKickCheck(gameState, playerIndex, pieceIndex, result);
                }

                ApplyPostMoveEffects(gameState, playerIndex, pieceIndex, diceValue,
                    newPos < BoardSize, result);
                return result;
            }

            // ===== 在主路径上（0~51）=====
            if (currentPos >= 0 && currentPos < BoardSize)
            {
                int oldPos = currentPos;
                int newPos = currentPos + diceValue;

                if (newPos > FinishEnd)
                {
                    result.Success = false;
                    result.Message = "移动后会超出终点，无法移动。";
                    return result;
                }

                // 恰好到达 FinishEnd=57：直接完成（途经格子不触发踩子）
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
                    DoKickCheck(gameState, playerIndex, pieceIndex, result);
                }

                ApplyPostMoveEffects(gameState, playerIndex, pieceIndex, diceValue,
                    newPos < BoardSize, result);
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

                ApplyPostMoveEffects(gameState, playerIndex, pieceIndex, diceValue,
                    false, result);
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
            DoKickCheckAt(gameState, playerIndex, movingAbsIndex, result);
        }

        /// <summary>在指定绝对格子索引上执行踩子检查（用于逐格检查中间路径）</summary>
        private void DoKickCheckAt(GameState gameState, int playerIndex, int absIndex, MoveResult result)
        {
            if (SafeCells.Contains(absIndex))
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
                    if (otherAbsIndex == absIndex)
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

        /// <summary>检查玩家是否完成归营（排名制：先完成者排名靠前）</summary>
        private void DoFinishCheck(GameState gameState, int playerIndex, MoveResult result)
        {
            Player player = gameState.Players[playerIndex];
            if (player.HasWon() && player.Rank == 0)
            {
                gameState.FinishCount++;
                player.Rank = gameState.FinishCount;
                gameState.WinnerIndex = playerIndex;  // 保持最后一个完成者为 WinnerIndex
                result.ExtraTurn = false;  // 完成者无额外回合

                string rankName = player.Rank == 1 ? "第一名" :
                                  player.Rank == 2 ? "第二名" :
                                  player.Rank == 3 ? "第三名" :
                                  string.Format("第{0}名", player.Rank);
                result.Message += string.Format(" {0} 获得{1}！", player.Name, rankName);

                // 所有已连接玩家都完成时游戏结束
                if (gameState.FinishCount >= gameState.ConnectedPlayerCount)
                {
                    result.GameOver = true;
                    result.Message += " 游戏结束！";
                }
            }
        }

        /// <summary>
        /// 统一处理移动后的骰子6额外回合、飞跃/同色跳、完成检查。
        /// </summary>
        private void ApplyPostMoveEffects(GameState gameState, int playerIndex, int pieceIndex,
            int diceValue, bool isOnMainPath, MoveResult result)
        {
            if (diceValue == 6)
                result.ExtraTurn = true;

            if (isOnMainPath && !DoFlightJump(gameState, playerIndex, pieceIndex, result))
                DoSameColorJump(gameState, playerIndex, pieceIndex, result);

            DoFinishCheck(gameState, playerIndex, result);
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
        /// 飞跃跳：当棋子走到本地位置 17 时，直接飞到 29（前进+12格）。
        /// 四名玩家的触发条件在本地坐标系下完全一致（对称设计）。
        /// 优先于同色跳触发。
        /// </summary>
        /// <returns>true 如果发生了飞跃</returns>
        private bool DoFlightJump(GameState gameState, int playerIndex, int pieceIndex, MoveResult result)
        {
            Player player = gameState.Players[playerIndex];
            int pos = player.Pieces[pieceIndex];

            // 仅当棋子恰好在主路径本地位置 17 时触发
            if (pos != 17)
                return false;

            int jumpPos = 29;
            player.Pieces[pieceIndex] = jumpPos;
            result.Message += string.Format(" 飞跃！从 {0} 飞到 {1}！", pos, jumpPos);

            // 跳跃后踩子检查
            DoKickCheck(gameState, playerIndex, pieceIndex, result);

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
                // 跳过从未加入和已完成归营的玩家（掉线玩家 AI 托管，不跳过）
                if (gameState.Players[next].HasJoined && gameState.Players[next].Rank == 0)
                    return next;
            }
            return gameState.CurrentPlayerIndex; // 不应发生
        }

    }
}
