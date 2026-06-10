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
                    Console.WriteLine("用法: FlightChess.Server.exe [端口]");
                    Console.WriteLine("示例: FlightChess.Server.exe 8888");
                    return;
                }
            }

            GameServer server = new GameServer();

            // 注册 Ctrl+C 事件以优雅退出
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\n正在关闭...");
                server.Stop();
                Console.WriteLine("服务器已关闭。");
            };

            server.Start(port);

            Console.WriteLine("输入 help 查看命令, Ctrl+C 停止服务器...");

            // 主线程等待
            while (true)
            {
                string input = Console.ReadLine();
                if (input == null) continue;
                input = input.Trim().ToLower();

                if (input == "exit" || input == "quit")
                    break;
                if (input == "status" || input == "s")
                {
                    Console.WriteLine("服务器运行中，端口: {0}", port);
                    continue;
                }
                if (input == "help" || input == "h" || input == "?")
                {
                    Console.WriteLine("  命令:");
                    Console.WriteLine("    status / s         - 查看状态");
                    Console.WriteLine("    kick <A> <B>       - 强制 A 踩 B (如: kick 红 蓝)");
                    Console.WriteLine("    win  <A>           - 强制 A 获胜 (如: win 红)");
                    Console.WriteLine("    exit / quit        - 退出服务器");
                    Console.WriteLine("  颜色: 红(0) 绿(1) 黄(2) 蓝(3)");
                    continue;
                }
                if (input.StartsWith("kick"))
                {
                    var parts = input.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        int kicker = ParsePlayerIndex(parts[1]);
                        int kicked = ParsePlayerIndex(parts[2]);
                        if (kicker >= 0 && kicked >= 0)
                            server.ForceKick(kicker, kicked);
                        else
                            Console.WriteLine("用法: kick <踩方> <被踩方>  如 kick 红 蓝");
                    }
                    else
                        Console.WriteLine("用法: kick <踩方> <被踩方>  如 kick 红 蓝");
                    continue;
                }
                if (input.StartsWith("win"))
                {
                    var parts = input.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        int idx = ParsePlayerIndex(parts[1]);
                        if (idx >= 0)
                            server.ForceWin(idx);
                        else
                            Console.WriteLine("用法: win <颜色/编号>  如 win 红 或 win 0");
                    }
                    else
                        Console.WriteLine("用法: win <颜色/编号>  如 win 红 或 win 0");
                    continue;
                }
                Console.WriteLine("未知命令。输入 help 查看帮助。");
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
