using System;

namespace FlightChess.Server
{
    class Program
    {
        static void Main(string[] args)
        {
            int port = 8888;

            if (args.Length > 0)
            {
                if (!int.TryParse(args[0], out port))
                {
                    Console.WriteLine("用法: FlightChess.Server.exe [端口号]");
                    Console.WriteLine("示例: FlightChess.Server.exe 8888");
                    return;
                }
            }

            GameServer server = new GameServer();

            // 注册 Ctrl+C 事件以优雅退出
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\n正在关闭服务器...");
                server.Stop();
                Console.WriteLine("服务器已关闭。按任意键退出...");
            };

            server.Start(port);

            Console.WriteLine("按 Ctrl+C 停止服务器...");

            // 主线程等待
            while (true)
            {
                string input = Console.ReadLine();
                if (input?.ToLower() == "exit" || input?.ToLower() == "quit")
                {
                    break;
                }
                if (input?.ToLower() == "status")
                {
                    Console.WriteLine("服务器正在运行，端口: {0}", port);
                }
                if (input != null && input.ToLower().StartsWith("kick"))
                {
                    // 用法: kick 0 1  或  kick red green（踩子者 被踩者）
                    var parts = input.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        int kicker = ParsePlayerIndex(parts[1]);
                        int kicked = ParsePlayerIndex(parts[2]);
                        if (kicker >= 0 && kicked >= 0)
                        {
                            server.ForceKick(kicker, kicked);
                        }
                        else
                        {
                            Console.WriteLine("用法: kick <kicker> <kicked>  如 kick red green 或 kick 0 1");
                        }
                    }
                    else
                    {
                        Console.WriteLine("用法: kick <kicker> <kicked>  如 kick red green 或 kick 0 1");
                        Console.WriteLine("  kicker: 踩子方, kicked: 被踩方");
                    }
                }
                if (input != null && input.ToLower().StartsWith("win"))
                {
                    // 用法: win 0  或  win red
                    var parts = input.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        int idx = ParsePlayerIndex(parts[1]);
                        if (idx >= 0)
                        {
                            server.ForceWin(idx);
                        }
                        else
                        {
                            Console.WriteLine("用法: win <0-3>  或  win <red/green/yellow/blue>");
                        }
                    }
                    else
                    {
                        Console.WriteLine("用法: win <0-3>  或  win <red/green/yellow/blue>");
                    }
                }
            }

            server.Stop();
            Console.WriteLine("服务器已关闭。");
        }

        /// <summary>解析玩家索引：0-3 数字 或 red/green/yellow/blue/红/绿/黄/蓝</summary>
        private static int ParsePlayerIndex(string s)
        {
            if (int.TryParse(s, out int idx) && idx >= 0 && idx <= 3)
                return idx;
            switch (s.ToLower())
            {
                case "red": case "红": return 0;
                case "green": case "绿": return 1;
                case "yellow": case "黄": return 2;
                case "blue": case "蓝": return 3;
                default: return -1;
            }
        }
    }
}
