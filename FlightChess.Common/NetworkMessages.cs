using System;

namespace FlightChess.Common
{
    /// <summary>
    /// 网络消息类型常量
    /// </summary>
    public static class MessageType
    {
        public const string JoinGame = "JoinGame";
        public const string RollDice = "RollDice";
        public const string MovePiece = "MovePiece";
        public const string GameStateUpdate = "GameStateUpdate";
        public const string Error = "Error";
        public const string PlayerLeft = "PlayerLeft";
        public const string JoinGameResponse = "JoinGameResponse";
        public const string ResetGame = "ResetGame";
    }

    /// <summary>
    /// 客户端 → 服务器：加入游戏
    /// </summary>
    [Serializable]
    public class JoinGameMessage
    {
        public string Type { get; set; }
        public string PlayerName { get; set; }

        public JoinGameMessage()
        {
            Type = MessageType.JoinGame;
        }
    }

    /// <summary>
    /// 客户端 → 服务器：掷骰子
    /// </summary>
    [Serializable]
    public class RollDiceMessage
    {
        public string Type { get; set; }

        public RollDiceMessage()
        {
            Type = MessageType.RollDice;
        }
    }

    /// <summary>
    /// 客户端 → 服务器：移动棋子
    /// </summary>
    [Serializable]
    public class MovePieceMessage
    {
        public string Type { get; set; }
        public int PieceIndex { get; set; }

        public MovePieceMessage()
        {
            Type = MessageType.MovePiece;
        }
    }

    /// <summary>
    /// 服务器 → 所有客户端：游戏状态更新
    /// </summary>
    [Serializable]
    public class GameStateUpdateMessage
    {
        public string Type { get; set; }
        public GameState State { get; set; }

        public GameStateUpdateMessage()
        {
            Type = MessageType.GameStateUpdate;
        }
    }

    /// <summary>
    /// 服务器 → 特定客户端：错误消息
    /// </summary>
    [Serializable]
    public class ErrorMessage
    {
        public string Type { get; set; }
        public string Message { get; set; }

        public ErrorMessage()
        {
            Type = MessageType.Error;
        }
    }

    /// <summary>
    /// 服务器 → 所有客户端：玩家离开
    /// </summary>
    [Serializable]
    public class PlayerLeftMessage
    {
        public string Type { get; set; }
        public int PlayerIndex { get; set; }
        public string PlayerName { get; set; }

        public PlayerLeftMessage()
        {
            Type = MessageType.PlayerLeft;
        }
    }

    /// <summary>
    /// 服务器 → 特定客户端：加入游戏响应（告知分配的玩家 ID）
    /// </summary>
    [Serializable]
    public class JoinGameResponseMessage
    {
        public string Type { get; set; }
        public int PlayerId { get; set; }

        public JoinGameResponseMessage()
        {
            Type = MessageType.JoinGameResponse;
        }
    }

    /// <summary>
    /// 客户端 → 服务器：请求重新开始游戏
    /// </summary>
    [Serializable]
    public class ResetGameMessage
    {
        public string Type { get; set; }

        public ResetGameMessage()
        {
            Type = MessageType.ResetGame;
        }
    }
}
