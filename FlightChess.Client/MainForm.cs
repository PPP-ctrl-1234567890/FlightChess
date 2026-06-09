using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using FlightChess.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FlightChess.Client
{
    /// <summary>
    /// 飞行棋联机游戏 — 传统十字环形棋盘，复古纸质印刷风格。
    /// 52格外环路径，四角大本营，四条回营路径，中心四色拼接"机"字区。
    /// </summary>
    public partial class MainForm : Form
    {
        // ========== 网络 ==========
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private StreamReader _reader;
        private StreamWriter _writer;
        private Thread _receiveThread;
        private bool _isConnected;

        // ========== 状态 ==========
        private GameState _currentGameState;
        private int _myPlayerId = -1;
        private string _myPlayerName;
        private readonly object _stateLock = new object();

        // ========== 棋盘常数 ==========
        private const int BdW = 700, BdH = 700;
        private const int Inset = 40;                     // 路径距棋盘边缘的内缩
        private const int CellSpacing = 1;                // 格子间距（紧挨）
        private const int ArmW = 180;                     // 十字臂宽度
        private const int CenterX = 310, CenterY = 310;   // 中心区块左上角（100×100，缩小为原来一半）
        private const int CenterW = 100, CenterH = 100;   // 中心区块宽高（臂210px=5.25格，边100px=2.5格）
        private const int BaseSize = 160;                  // 大本营尺寸
        private const int BaseMargin = 30;                 // 大本营距边缘

        // ========== 棋盘数据 ==========
        /// <summary>52格外环格中心坐标</summary>
        private PointF[] _gridPos;
        /// <summary>外环格是否为转角格</summary>
        private bool[] _isCorner;
        /// <summary>转角格的箭头方向角（弧度）</summary>
        private float[] _cornerAngle;
        /// <summary>外环格的颜色类型 0=红 1=黄 2=蓝 3=绿 4=白</summary>
        private int[] _cellColorType;
        /// <summary>4个大本营矩形</summary>
        private Rectangle[] _baseRects;
        /// <summary>每个大本营4个棋位</summary>
        private Point[][] _baseSlots;
        /// <summary>每个大本营START圆标位置</summary>
        private Point[] _startMarkers;
        /// <summary>回营路径6格(每个玩家) — 索引52~57</summary>
        private PointF[][] _returnPathSpots;
        /// <summary>回营路径段距离外环和中心的方向</summary>
        private PointF[][] _returnArrowDirs;
        /// <summary>飞跃跳配对：[(fromAbs, toAbs)] 对应红绿黄蓝四名玩家</summary>
        private static readonly (int from, int to)[] _flightJumpPairs = {
            (17, 29),  // 红: abs 17 → 29
            (4, 16),   // 绿: abs 4 → 16
            (43, 3),   // 黄: abs 43 → 3
            (30, 42)   // 蓝: abs 30 → 42
        };
        /// <summary>中心四色区块矩形</summary>
        private Rectangle _centerRect;
        private Point _centerPt;

        // 路径段定义（12段顺时针闭合多边形）
        private List<(PointF a, PointF b, float angle)> _segments;

        private int _hoverPlayer = -1, _hoverPiece = -1;

        // ========== 动画 ==========
        private System.Windows.Forms.Timer _animTimer;
        private int _animPlayer = -1, _animPiece = -1;
        private System.Collections.Generic.List<PointF> _animPath;
        private int _animPathIndex;
        /// <summary>被踩棋子动画：在被踩回基地前短暂保留在棋盘原位</summary>
        private int _kickedPlayer = -1, _kickedPiece = -1;
        private PointF _kickedScreenPos;

        // P0=红(右下), P1=绿(右上), P2=黄(左上), P3=蓝(左下)
        private static readonly Color[] PlyCol = {
            Color.FromArgb(215, 50, 50),   // 红
            Color.FromArgb(30, 170, 40),   // 绿
            Color.FromArgb(215, 175, 15),  // 黄
            Color.FromArgb(40, 95, 210)    // 蓝
        };
        private static readonly Color[] PlyLight = {
            Color.FromArgb(255, 235, 235),
            Color.FromArgb(225, 255, 225),
            Color.FromArgb(255, 252, 220),
            Color.FromArgb(225, 238, 255)
        };
        private static readonly Color[] PlyDark = {
            Color.FromArgb(155, 25, 25),
            Color.FromArgb(20, 115, 35),
            Color.FromArgb(155, 125, 5),
            Color.FromArgb(20, 70, 165)
        };
        private static readonly string[] PlyName = { "红", "绿", "黄", "蓝" };
        private static readonly int[] StartCells = { 0, 39, 26, 13 };

        // 格子颜色：红绿黄蓝四色固定循环（与阵营色对应）
        private static readonly Color[] CellColors = {
            Color.FromArgb(215, 60, 60),    // 红格
            Color.FromArgb(35, 175, 45),    // 绿格
            Color.FromArgb(220, 185, 20),   // 黄格
            Color.FromArgb(45, 100, 215),   // 蓝格
        };
        private static readonly Color[] CellColorsDark = {
            Color.FromArgb(160, 25, 25),    // 红
            Color.FromArgb(20, 125, 35),    // 绿
            Color.FromArgb(160, 130, 5),    // 黄
            Color.FromArgb(20, 70, 170),    // 蓝
        };

        /// <summary>设计器用无参构造函数</summary>
        public MainForm()
        {
            _myPlayerName = "设计器";
            InitializeComponent();
            HookEvents();  // 事件绑定独立于设计器生成的 InitializeComponent
            // 双缓冲在运行时启用，设计时跳过（反射调用会破坏设计器解析器）
            if (!DesignMode)
            {
                typeof(Panel).GetProperty("DoubleBuffered",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.SetValue(boardPanel, true, null);
            }
            InitAnimationTimer();
            InitBoardGeometry();
        }

        public MainForm(string server, int port, string name)
        {
            _myPlayerName = name;
            InitializeComponent();
            HookEvents();  // 事件绑定独立于设计器生成的 InitializeComponent
            // 双缓冲在运行时启用
            typeof(Panel).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(boardPanel, true, null);
            InitAnimationTimer();
            InitBoardGeometry();
            ConnectToServer(server, port);
        }

        /// <summary>绑定所有控件事件（独立于设计器生成的 InitializeComponent，防止设计器覆盖）</summary>
        private void HookEvents()
        {
            this.FormClosing += MainForm_FormClosing;
            this.btnRollDice.Click += BtnRollDice_Click;
            this.btnReset.Click += BtnReset_Click;
            this.boardPanel.Paint += BoardPanel_Paint;
            this.boardPanel.MouseClick += BoardPanel_MouseClick;
            this.boardPanel.MouseMove += BoardPanel_MouseMove;
            this.boardPanel.MouseLeave += BoardPanel_MouseLeave;
        }

        /// <summary>初始化棋子移动动画计时器</summary>
        private void InitAnimationTimer()
        {
            _animTimer = new System.Windows.Forms.Timer();
            _animTimer.Interval = 100;  // 每步 100ms
            _animTimer.Tick += AnimTimer_Tick;
        }

        // =================================================================
        //  棋盘几何初始化 — 十字环形布局
        // =================================================================
        /// <summary>
        /// 棋盘几何初始化 — 标准飞行棋十字环形布局（严格顺时针）。
        /// centerBlock(260,260)-(460,460) center=(360,360)。对称十字：
        ///   4条外环边各200px=5格，8条臂段各160px=4格，总2080px÷40=52格。
        /// cell 0 位于右边外缘底部 (620,460)，向上行进。
        /// </summary>
        private void InitBoardGeometry()
        {
            int L = CenterX, T = CenterY;                              // 310, 310
            int R = CenterX + CenterW, B = CenterY + CenterH;          // 410, 410
            int cx = (L + R) / 2, cy = (T + B) / 2;                   // 360, 360（圆心不变）
            _centerRect = new Rectangle(L, T, CenterW, CenterH);  // (310,310,100,100)
            _centerPt = new Point(cx, cy);

            const int OuterR = 620, OuterL = 100;
            const int OuterB = 620, OuterT = 100;

            // ---- 12段逆时针闭合路径（外环不变：边200px×4，臂160px×8，总长2080，step=40） ----
            _segments = new List<(PointF a, PointF b, float angle)>();

            // ---- 逆时针：从右下角(620,460)出发 ----
            // S1:  右臂下侧 ←     (620,460)→(460,460)  160px  ← cell 0  红 起点
            _segments.Add((new PointF(OuterR, 460), new PointF(460, 460), (float)Math.PI));
            // S2:  下臂右侧 ↓     (460,460)→(460,620)  160px
            _segments.Add((new PointF(460, 460), new PointF(460, OuterB), (float)(Math.PI / 2)));
            // S3:  下边外缘 ←     (460,620)→(260,620)  200px  ← cell 13 蓝 起点
            _segments.Add((new PointF(460, OuterB), new PointF(260, OuterB), (float)Math.PI));
            // S4:  下臂左侧 ↑     (260,620)→(260,460)  160px
            _segments.Add((new PointF(260, OuterB), new PointF(260, 460), -(float)(Math.PI / 2)));
            // S5:  左臂下侧 ←     (260,460)→(100,460)  160px
            _segments.Add((new PointF(260, 460), new PointF(OuterL, 460), (float)Math.PI));
            // S6:  左边外缘 ↑     (100,460)→(100,260)  200px  ← cell 26 黄 起点
            _segments.Add((new PointF(OuterL, 460), new PointF(OuterL, 260), -(float)(Math.PI / 2)));
            // S7:  左臂上侧 →     (100,260)→(260,260)  160px
            _segments.Add((new PointF(OuterL, 260), new PointF(260, 260), 0f));
            // S8:  上臂左侧 ↑     (260,260)→(260,100)  160px
            _segments.Add((new PointF(260, 260), new PointF(260, OuterT), -(float)(Math.PI / 2)));
            // S9:  上边外缘 →     (260,100)→(460,100)  200px  ← cell 39 绿 起点
            _segments.Add((new PointF(260, OuterT), new PointF(460, OuterT), 0f));
            // S10: 上臂右侧 ↓     (460,100)→(460,260)  160px
            _segments.Add((new PointF(460, OuterT), new PointF(460, 260), (float)(Math.PI / 2)));
            // S11: 右臂上侧 →     (460,260)→(620,260)  160px
            _segments.Add((new PointF(460, 260), new PointF(OuterR, 260), 0f));
            // S12: 右边外缘 ↓     (620,260)→(620,460)  200px  ← 闭合回 cell 0
            _segments.Add((new PointF(OuterR, 260), new PointF(OuterR, 460), (float)(Math.PI / 2)));

            // 总长 = 4×200 + 8×160 = 2080，step = 40
            float totalLen = 0;
            foreach (var (a, b, _) in _segments)
                totalLen += DistF(a.X, a.Y, b.X, b.Y);

            float step = totalLen / 52f;   // 40.0

            // ---- 52格坐标 ----
            _gridPos = new PointF[52];
            _isCorner = new bool[52];
            _cornerAngle = new float[52];
            _cellColorType = new int[52];

            float[] segEndDists = new float[_segments.Count];
            float cumLen = 0;
            for (int s = 0; s < _segments.Count; s++)
            {
                var (a, b, _) = _segments[s];
                cumLen += DistF(a.X, a.Y, b.X, b.Y);
                segEndDists[s] = cumLen;
            }

            for (int i = 0; i < 52; i++)
            {
                float dist = i * step;
                int segIdx = 0;
                float segStartDist = 0;
                for (int s = 0; s < _segments.Count; s++)
                {
                    if (dist <= segEndDists[s] + 0.01f)
                    {
                        segIdx = s;
                        break;
                    }
                    segStartDist = segEndDists[s];
                }
                var (sa, sb, sAng) = _segments[segIdx];
                float segLen = DistF(sa.X, sa.Y, sb.X, sb.Y);
                float t = (dist - segStartDist) / segLen;
                if (t < 0) t = 0;
                if (t > 1) t = 1;
                _gridPos[i] = new PointF(
                    sa.X + (sb.X - sa.X) * t,
                    sa.Y + (sb.Y - sa.Y) * t);

                _isCorner[i] = false;
                _cornerAngle[i] = sAng;
                // 颜色序列：绿(1)→红(0)→蓝(3)→黄(2) → 绿(1)→... ，四色循环
                // 红方从 cell 0 出发遇到的第一格是绿色
                int[] colorSeq = { 1, 0, 3, 2 };  // 绿、红、蓝、黄
                _cellColorType[i] = colorSeq[i % 4];
            }

            // ---- 大本营（四角） ----
            _baseRects = new Rectangle[4];
            _baseRects[0] = new Rectangle(BdW - BaseMargin - BaseSize, BdH - BaseMargin - BaseSize, BaseSize, BaseSize);  // 红 右下
            _baseRects[1] = new Rectangle(BdW - BaseMargin - BaseSize, BaseMargin, BaseSize, BaseSize);                   // 绿 右上
            _baseRects[2] = new Rectangle(BaseMargin, BaseMargin, BaseSize, BaseSize);                                    // 黄 左上
            _baseRects[3] = new Rectangle(BaseMargin, BdH - BaseMargin - BaseSize, BaseSize, BaseSize);                   // 蓝 左下

            _baseSlots = new Point[4][];
            for (int p = 0; p < 4; p++)
            {
                var r = _baseRects[p];
                int bcx = r.X + r.Width / 2, bcy = r.Y + r.Height / 2;
                int gap = 40;
                _baseSlots[p] = new Point[] {
                    new Point(bcx - gap/2, bcy - gap/2),
                    new Point(bcx + gap/2, bcy - gap/2),
                    new Point(bcx - gap/2, bcy + gap/2),
                    new Point(bcx + gap/2, bcy + gap/2) };
            }

            _startMarkers = new Point[4];
            _startMarkers[0] = new Point(_baseRects[0].X + _baseRects[0].Width / 2 + 60,
                                         _baseRects[0].Y - 20);
            _startMarkers[1] = new Point(_baseRects[1].X - 20,
                                         _baseRects[1].Y + _baseRects[1].Height / 2 - 50);
            _startMarkers[2] = new Point(_baseRects[2].X + _baseRects[2].Width / 2 - 60,
                                         _baseRects[2].Y + _baseRects[2].Height + 20);
            _startMarkers[3] = new Point(_baseRects[3].X + _baseRects[3].Width + 20,
                                         _baseRects[3].Y + _baseRects[3].Height / 2 + 50);

            // ---- 回营路径（6步，40px步长，向中心平移40px避开外环格子，第6格在缩小后中心三角形内） ----
            _returnPathSpots = new PointF[4][];
            _returnArrowDirs = new PointF[4][];
            for (int p = 0; p < 4; p++)
            {
                _returnPathSpots[p] = new PointF[6];
                _returnArrowDirs[p] = new PointF[6];
            }
            const float retStep = 40f;  // 步长
            const float retOffset = 40f; // 向中心平移距离，避开外环主路径格子

            // 红回营：右臂中央 Y=360，从外缘向内平移40px (620→580→...)，第5格在臂内缘(460)，第6格在中心三角形
            for (int i = 0; i < 5; i++)
            {
                _returnPathSpots[0][i] = new PointF(OuterR - retOffset - i * retStep, cy);
                _returnArrowDirs[0][i] = new PointF(-1, 0);
            }
            _returnPathSpots[0][5] = new PointF(R - CenterW / 6f, cy);   // 右三角区域内 (393,360)

            // 绿回营：上臂中央 X=360，从外缘向下平移40px (100→140→...)，第5格在臂内缘(260)
            for (int i = 0; i < 5; i++)
            {
                _returnPathSpots[1][i] = new PointF(cx, OuterT + retOffset + i * retStep);
                _returnArrowDirs[1][i] = new PointF(0, 1);
            }
            _returnPathSpots[1][5] = new PointF(cx, T + CenterH / 6f);   // 上三角区域内 (360,327)

            // 黄回营：左臂中央 Y=360，从左向右平移40px (100→140→...)，第5格在臂内缘(260)
            for (int i = 0; i < 5; i++)
            {
                _returnPathSpots[2][i] = new PointF(OuterL + retOffset + i * retStep, cy);
                _returnArrowDirs[2][i] = new PointF(1, 0);
            }
            _returnPathSpots[2][5] = new PointF(L + CenterW / 6f, cy);   // 左三角区域内 (327,360)

            // 蓝回营：下臂中央 X=360，从下向上平移40px (620→580→...)，第5格在臂内缘(460)
            for (int i = 0; i < 5; i++)
            {
                _returnPathSpots[3][i] = new PointF(cx, OuterB - retOffset - i * retStep);
                _returnArrowDirs[3][i] = new PointF(0, -1);
            }
            _returnPathSpots[3][5] = new PointF(cx, B - CenterH / 6f);   // 下三角区域内 (360,393)
        }

        // =================================================================
        //  主绘制入口
        // =================================================================
        private void BoardPanel_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            DrawBoardBackground(g);
            DrawCrossArms(g);
            DrawOuterRingCells(g);
            DrawFlightJumps(g);      // 飞跃连线（在归营路径和中心之前绘制，不覆盖归营路径）
            DrawBases(g);
            DrawReturnPaths(g);
            DrawCenter(g);
            DrawAllPieces(g);
        }

        // ---- 整体棋盘底色：浅米黄色（复古纸质） ----
        private void DrawBoardBackground(Graphics g)
        {
            using (Brush bg = new SolidBrush(Color.FromArgb(248, 242, 225)))
                g.FillRectangle(bg, 0, 0, BdW, BdH);
        }

        // ---- 十字臂底色（与棋盘底色统一，不区分功能区域） ----
        private void DrawCrossArms(Graphics g)
        {
            // 十字臂与棋盘背景色统一，仅作为路径定位引导
            // 实际不绘制任何分隔线或色块区分
            // 此方法保留作为架构占位，实际绘制在 DrawBoardBackground 中统一完成
        }

        // ---- 外环52格绘制（长方形格子：长边∥路径，短边⟂路径，长:短≈1.7:1） ----
        private void DrawOuterRingCells(Graphics g)
        {
            // 格子长方形：垂直路径方向=长边(宽)，平行路径方向=短边（长:短≈1.7:1）
            float cellHalfShort = 12f;  // 沿路径方向半边长（短边）
            float cellHalfLong = 20f;   // 垂直路径方向半边长（长边）
            float circleR = 9f;         // 白色圆形半径

            for (int i = 0; i < 52; i++)
            {
                PointF pt = _gridPos[i];
                int colorType = _cellColorType[i];
                Color fill = CellColors[colorType];
                Color dark = CellColorsDark[colorType];

                // 根据行进角度判断水平/垂直：sin≈0 → 水平段
                float ang = _cornerAngle[i];
                bool isHorizontal = Math.Abs(Math.Sin(ang)) < 0.01f;

                float halfW, halfH;
                if (isHorizontal)
                {
                    // 水平段：沿路径=X(短)，垂直路径=Y(长)
                    halfW = cellHalfShort;
                    halfH = cellHalfLong;
                }
                else
                {
                    // 垂直段：沿路径=Y(短)，垂直路径=X(长)
                    halfW = cellHalfLong;
                    halfH = cellHalfShort;
                }

                RectangleF rect = new RectangleF(
                    pt.X - halfW, pt.Y - halfH,
                    halfW * 2, halfH * 2);
                using (Brush bg = new SolidBrush(fill))
                    g.FillRectangle(bg, rect);

                // 每格绘制白色圆形
                using (Brush wb = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
                    g.FillEllipse(wb, pt.X - circleR, pt.Y - circleR, circleR * 2, circleR * 2);
                using (Pen wp = new Pen(Color.FromArgb(180, 210, 205, 195), 0.8f))
                    g.DrawEllipse(wp, pt.X - circleR, pt.Y - circleR, circleR * 2, circleR * 2);
            }
        }

        // ---- 大本营（四角实心正方形） ----
        private void DrawBases(Graphics g)
        {
            for (int p = 0; p < 4; p++)
            {
                var r = _baseRects[p];
                Color baseColor = PlyCol[p];
                Color lightColor = PlyLight[p];

                // 实心正方形色块（无圆角、无描边）
                using (Brush bg = new SolidBrush(baseColor))
                    g.FillRectangle(bg, r);

                // 内部4个同色系圆形图案（2×2均匀排布）
                int circleR = 16;
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                int gap = 38;
                int[][] offsets = new int[][] {
                    new int[] { -gap/2, -gap/2 },
                    new int[] { gap/2, -gap/2 },
                    new int[] { -gap/2, gap/2 },
                    new int[] { gap/2, gap/2 }
                };
                foreach (var off in offsets)
                {
                    int ccx = cx + off[0], ccy = cy + off[1];
                    // 稍浅的填充
                    using (Brush cb = new SolidBrush(Color.FromArgb(200, lightColor)))
                        g.FillEllipse(cb, ccx - circleR, ccy - circleR, circleR * 2, circleR * 2);
                    // 描边
                    using (Pen cp = new Pen(Color.FromArgb(180, PlyDark[p]), 1.5f))
                        g.DrawEllipse(cp, ccx - circleR, ccy - circleR, circleR * 2, circleR * 2);
                }

                // START圆形标识（大本营旁边，阵营颜色，半径扩大）
                var sm = _startMarkers[p];
                int sr = 16;
                // 底色使用阵营的浅色
                using (Brush wb = new SolidBrush(Color.FromArgb(220, PlyLight[p])))
                    g.FillEllipse(wb, sm.X - sr, sm.Y - sr, sr * 2, sr * 2);
                using (Pen bp = new Pen(PlyDark[p], 1.8f))
                    g.DrawEllipse(bp, sm.X - sr, sm.Y - sr, sr * 2, sr * 2);
                using (Font sf = new Font("Arial", 7f, FontStyle.Bold))
                using (Brush sb = new SolidBrush(Color.Black))
                {
                    var sz = g.MeasureString("START", sf);
                    g.DrawString("START", sf, sb, sm.X - sz.Width / 2, sm.Y - sz.Height / 2 + 1);
                }
            }
        }

        // ---- 四条回营路径 ----
        private void DrawReturnPaths(Graphics g)
        {
            for (int p = 0; p < 4; p++)
            {
                var spots = _returnPathSpots[p];
                var dirs = _returnArrowDirs[p];
                Color pathColor = PlyCol[p];

                // 格子半边长16px（总宽32px），40px步长下留8px间隙，不重叠
                float cellHalf = 16f;
                float circleR = 8f;

                for (int i = 0; i < 6; i++)
                {
                    PointF pt = spots[i];
                    // 第6步(索引5)为中心三角形位置，只绘制白色圆形不绘制方格
                    bool isCenterStep = (i == 5);

                    if (!isCenterStep)
                    {
                        // 实心正方形格子（阵营专色，紧密相连无缝隙）
                        RectangleF rect = new RectangleF(
                            pt.X - cellHalf, pt.Y - cellHalf,
                            cellHalf * 2, cellHalf * 2);
                        using (Brush bg = new SolidBrush(pathColor))
                            g.FillRectangle(bg, rect);

                        // 小型飞机图案（同色系稍浅）
                        DrawTinyPlane(g, pt, Color.FromArgb(180, PlyLight[p]), 0.6f);

                        // 小型方向箭头（指向中心）
                        DrawTinyArrow(g, pt, dirs[i], Color.FromArgb(220, 255, 255, 255));
                    }

                    // 每格绘制白色圆形（包括中心三角形位置，标识这也是路径的一部分）
                    using (Brush wb = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                        g.FillEllipse(wb, pt.X - circleR, pt.Y - circleR, circleR * 2, circleR * 2);
                    using (Pen wp = new Pen(Color.FromArgb(150, 200, 195, 185), 0.8f))
                        g.DrawEllipse(wp, pt.X - circleR, pt.Y - circleR, circleR * 2, circleR * 2);
                }
            }
        }

        /// <summary>绘制小型飞机图案（回营路径格内）</summary>
        private void DrawTinyPlane(Graphics g, PointF pt, Color color, float scale)
        {
            float radius = 8f * scale;  
            using (Brush brush = new SolidBrush(color))
            {
                g.FillEllipse(brush, pt.X - radius, pt.Y - radius, radius * 2, radius * 2);
            }
            // 可选：添加细边框
            using (Pen pen = new Pen(Color.Black, 0.8f))
            {
                g.DrawEllipse(pen, pt.X - radius, pt.Y - radius, radius * 2, radius * 2);
            }
        }

        /// <summary>绘制小型方向箭头（回营路径格内）</summary>
        private void DrawTinyArrow(Graphics g, PointF pt, PointF dir, Color color)
        {
            float len = 10f;
            float headSz = 4f;
            float dx = dir.X, dy = dir.Y;
            float mag = (float)Math.Sqrt(dx * dx + dy * dy);
            if (mag < 0.01f) return;
            dx /= mag; dy /= mag;
            float perpX = -dy, perpY = dx;

            // 箭杆
            PointF start = new PointF(pt.X - dx * len * 0.5f, pt.Y - dy * len * 0.5f);
            PointF end = new PointF(pt.X + dx * len * 0.3f, pt.Y + dy * len * 0.3f);
            using (Pen ap = new Pen(color, 1.5f))
            {
                ap.StartCap = LineCap.Round;
                ap.EndCap = LineCap.Round;
                g.DrawLine(ap, start, end);
            }

            // 箭头
            PointF tip = new PointF(pt.X + dx * len * 0.55f, pt.Y + dy * len * 0.55f);
            PointF lw = new PointF(tip.X - dx * headSz + perpX * headSz * 0.6f,
                                    tip.Y - dy * headSz + perpY * headSz * 0.6f);
            PointF rw = new PointF(tip.X - dx * headSz - perpX * headSz * 0.6f,
                                    tip.Y - dy * headSz - perpY * headSz * 0.6f);
            PointF[] tri = { tip, lw, rw };
            using (Brush ab = new SolidBrush(color))
                g.FillPolygon(ab, tri);
        }

       //中心区域：四色三角形 + "机"字 + 白色圆形
        private void DrawCenter(Graphics g)
        {
            int L = CenterX, T = CenterY;
            int R = CenterX + CenterW, B = CenterY + CenterH;
            int cx = (L + R) / 2, cy = (T + B) / 2;

            PointF topLeft = new PointF(L, T);
            PointF topRight = new PointF(R, T);
            PointF bottomRight = new PointF(R, B);
            PointF bottomLeft = new PointF(L, B);
            PointF center = new PointF(cx, cy);

            // 上三角形（绿）
            using (Brush brush = new SolidBrush(PlyCol[1]))
                g.FillPolygon(brush, new PointF[] { center, topLeft, topRight });
            // 下三角形（蓝）
            using (Brush brush = new SolidBrush(PlyCol[3]))
                g.FillPolygon(brush, new PointF[] { center, bottomLeft, bottomRight });
            // 左三角形（黄）
            using (Brush brush = new SolidBrush(PlyCol[2]))
                g.FillPolygon(brush, new PointF[] { center, topLeft, bottomLeft });
            // 右三角形（红）
            using (Brush brush = new SolidBrush(PlyCol[0]))
                g.FillPolygon(brush, new PointF[] { center, topRight, bottomRight });

            // 每个三角形上绘制白色圆形（标识这也是路径的一部分）
            float triCircleR = 10f;
            // 上三角圆心（绿）
            PointF triCtrUp = new PointF(cx, T + CenterH / 6);
            // 下三角圆心（蓝）
            PointF triCtrDown = new PointF(cx, B - CenterH / 6);
            // 左三角圆心（黄）
            PointF triCtrLeft = new PointF(L + CenterW / 6, cy);
            // 右三角圆心（红）
            PointF triCtrRight = new PointF(R - CenterW / 6, cy);

            PointF[] triCenters = { triCtrRight, triCtrUp, triCtrLeft, triCtrDown };
            for (int p = 0; p < 4; p++)
            {
                var tc = triCenters[p];
                using (Brush wb = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                    g.FillEllipse(wb, tc.X - triCircleR, tc.Y - triCircleR, triCircleR * 2, triCircleR * 2);
                using (Pen wp = new Pen(Color.FromArgb(150, 200, 195, 185), 0.8f))
                    g.DrawEllipse(wp, tc.X - triCircleR, tc.Y - triCircleR, triCircleR * 2, triCircleR * 2);
            }

            // 对角线分割线
            using (Pen sep = new Pen(Color.FromArgb(160, 140, 120), 1.5f))
            {
                g.DrawLine(sep, topLeft, bottomRight);
                g.DrawLine(sep, topRight, bottomLeft);
            }
        }

        // ---- 飞跃跳连接线（与主路径格子等宽的纯白直线，从归营路径下方穿过） ----
        private void DrawFlightJumps(Graphics g)
        {
            float lineWidth = 24f;  // 与主路径格子短边等宽（cellHalfShort*2）

            for (int p = 0; p < 4; p++)
            {
                var (fromAbs, toAbs) = _flightJumpPairs[p];
                PointF from = _gridPos[fromAbs];
                PointF to = _gridPos[toAbs];

                // 纯白直线条（与格子等宽，圆角端点）
                using (Pen linePen = new Pen(Color.FromArgb(240, 255, 255, 255), lineWidth))
                {
                    linePen.StartCap = LineCap.Round;
                    linePen.EndCap = LineCap.Round;
                    g.DrawLine(linePen, from, to);
                }

                // 细边框勾勒
                using (Pen edgePen = new Pen(Color.FromArgb(160, 200, 195, 185), 1f))
                {
                    g.DrawLine(edgePen, from, to);
                }

                // 在直线 1/3 和 2/3 处绘制同色方向箭头
                DrawStraightArrow(g, from, to, 0.33f, p);
                DrawStraightArrow(g, from, to, 0.67f, p);
            }
        }

        /// <summary>在直线上指定位置绘制方向箭头（playerIndex 决定颜色）</summary>
        private void DrawStraightArrow(Graphics g, PointF from, PointF to, float t, int playerIndex)
        {
            float dx = to.X - from.X;
            float dy = to.Y - from.Y;
            float mag = (float)Math.Sqrt(dx * dx + dy * dy);
            if (mag < 0.01f) return;
            float ux = dx / mag;    // 单位方向
            float uy = dy / mag;
            float px = -uy;         // 垂直方向
            float py = ux;

            float cx = from.X + dx * t;   // 箭头中心
            float cy = from.Y + dy * t;

            float arrowLen = 14f;
            float arrowW = 6f;

            PointF tip = new PointF(cx + ux * arrowLen * 0.5f, cy + uy * arrowLen * 0.5f);
            PointF left = new PointF(cx - ux * arrowLen * 0.5f + px * arrowW,
                                      cy - uy * arrowLen * 0.5f + py * arrowW);
            PointF right = new PointF(cx - ux * arrowLen * 0.5f - px * arrowW,
                                       cy - uy * arrowLen * 0.5f - py * arrowW);

            using (Brush ab = new SolidBrush(PlyCol[playerIndex]))
                g.FillPolygon(ab, new PointF[] { tip, left, right });
            using (Pen ap = new Pen(Color.FromArgb(180, PlyDark[playerIndex]), 0.8f))
                g.DrawPolygon(ap, new PointF[] { tip, left, right });
        }

        // =================================================================
        //  棋子绘制（平面卡通飞机，无光晕、无特效）
        // =================================================================
        private void DrawAllPieces(Graphics g)
        {
            GameState st;
            lock (_stateLock) { if (_currentGameState == null) return; st = _currentGameState.DeepCopy(); }

            // 收集各外环格上棋子
            List<(int p, int q)>[] cellOcc = new List<(int p, int q)>[52];
            for (int i = 0; i < 52; i++) cellOcc[i] = new List<(int, int)>();

            for (int pl = 0; pl < 4; pl++)
            {
                if (!st.Players[pl].IsConnected) continue;
                for (int qi = 0; qi < 4; qi++)
                {
                    int pos = st.Players[pl].Pieces[qi];
                    if (pos < 0 || pos >= 52) continue;
                    int abs = FlightChessEngine.ToAbsoluteIndex(st.Players[pl].StartOffset, pos);
                    if (abs >= 0 && abs < 52) cellOcc[abs].Add((pl, qi));
                }
            }

            // 绘外环棋子
            bool isAnimating = _animTimer.Enabled && _animPath != null
                && _animPathIndex >= 0 && _animPathIndex < _animPath.Count;
            for (int i = 0; i < 52; i++)
            {
                if (cellOcc[i].Count == 0) continue;
                PointF ctr = _gridPos[i];
                for (int k = 0; k < cellOcc[i].Count; k++)
                {
                    var (pl, qi) = cellOcc[i][k];
                    if (isAnimating && pl == _animPlayer && qi == _animPiece)
                        continue;  // 动画中跳过实际位置
                    var (ox, oy) = StackOff(cellOcc[i].Count, k);
                    bool canMove = (pl == _myPlayerId && IsMyValidMove(st, pl, qi));
                    DrawPiece(g, pl, qi, ctr.X + ox, ctr.Y + oy, canMove);
                }
            }

            // 绘基地棋子（未起飞 pos==-1 普通样式）和归营棋子（pos==58 金色星标样式）
            // 走完全程的棋子回到大本营，以归营棋子的样式显示
            for (int pl = 0; pl < 4; pl++)
            {
                if (!st.Players[pl].IsConnected) continue;

                // 收集棋子：先基地后归营，保证基地棋子填充前面的槽位
                var basePieces = new System.Collections.Generic.List<int>();
                var goalPieces = new System.Collections.Generic.List<int>();
                for (int qi = 0; qi < 4; qi++)
                {
                    int pos = st.Players[pl].Pieces[qi];
                    if (pos == -1) basePieces.Add(qi);
                    else if (pos == 58) goalPieces.Add(qi);
                }

                int si = 0;
                // 未起飞棋子（普通样式）
                foreach (int qi in basePieces)
                {
                    if (si >= 4) break;
                    if (isAnimating && pl == _animPlayer && qi == _animPiece)
                    { si++; continue; }  // 动画中跳过实际位置，但槽位仍占用
                    // 被踩棋子动画中：暂不绘在基地（稍后在原位绘制）
                    if (isAnimating && pl == _kickedPlayer && qi == _kickedPiece)
                    { si++; continue; }
                    Point pt = _baseSlots[pl][si];
                    DrawPiece(g, pl, qi, pt.X, pt.Y, false);
                    si++;
                }
                // 归营棋子（金色星标特殊样式）
                foreach (int qi in goalPieces)
                {
                    if (si >= 4) break;
                    if (isAnimating && pl == _animPlayer && qi == _animPiece)
                    { si++; continue; }
                    if (isAnimating && pl == _kickedPlayer && qi == _kickedPiece)
                    { si++; continue; }
                    Point pt = _baseSlots[pl][si];
                    DrawGoalPiece(g, pl, qi, pt.X, pt.Y);
                    si++;
                }
            }

            // 绘START位置棋子（起飞后等待进入主路径）
            for (int pl = 0; pl < 4; pl++)
            {
                if (!st.Players[pl].IsConnected) continue;
                for (int qi = 0; qi < 4; qi++)
                {
                    int pos = st.Players[pl].Pieces[qi];
                    if (pos != FlightChessEngine.StartPosition) continue;
                    if (isAnimating && pl == _animPlayer && qi == _animPiece)
                        continue;  // 动画中跳过实际位置
                    Point pt = _startMarkers[pl];
                    bool canMove = (pl == _myPlayerId && IsMyValidMove(st, pl, qi));
                    DrawPiece(g, pl, qi, pt.X, pt.Y, canMove);
                }
            }

            // 绘回营路径棋子（52~57）
            for (int pl = 0; pl < 4; pl++)
            {
                if (!st.Players[pl].IsConnected) continue;
                for (int qi = 0; qi < 4; qi++)
                {
                    int pos = st.Players[pl].Pieces[qi];
                    if (pos < 52 || pos > 57) continue;
                    if (isAnimating && pl == _animPlayer && qi == _animPiece)
                        continue;  // 动画中跳过实际位置
                    int fi = pos - 52;
                    if (fi < 0 || fi >= 6) continue;
                    PointF pt = _returnPathSpots[pl][fi];
                    bool canMove = (pl == _myPlayerId && IsMyValidMove(st, pl, qi));
                    DrawPiece(g, pl, qi, pt.X, pt.Y, canMove);
                }
            }

            // 动画叠加：在动画路径当前位置绘制棋子
            if (isAnimating && _animPlayer >= 0 && _animPiece >= 0)
            {
                PointF animPos = _animPath[_animPathIndex];
                bool canMove = false;  // 动画中不可点击移动
                DrawPiece(g, _animPlayer, _animPiece, animPos.X, animPos.Y, canMove);
            }

            // 被踩棋子：动画期间保留在原位（与新棋子重叠），动画结束后消失回基地
            if (isAnimating && _kickedPlayer >= 0 && _kickedPiece >= 0)
            {
                DrawPiece(g, _kickedPlayer, _kickedPiece,
                    _kickedScreenPos.X, _kickedScreenPos.Y, false);
            }
        }

        private static (int ox, int oy) StackOff(int cnt, int idx)
        {
            if (cnt == 2) return (idx == 0 ? -7 : 7, 0);
            if (cnt == 3)
            {
                if (idx == 0) return (-6, -5);
                if (idx == 1) return (6, -5);
                return (0, 8);
            }
            if (cnt == 4) return ((idx % 2 == 0 ? -6 : 6), (idx < 2 ? -6 : 6));
            return (0, 0);
        }

        /// <summary>绘制一枚棋子（传统平面飞机）</summary>
        private void DrawPiece(Graphics g, int player, int idx, float x, float y,
            bool canMove)
        {
            float r = 11f;
            Color main = PlyCol[player];
            Color dark = PlyDark[player];
            Color light = PlyLight[player];

            // 可移动提示：仅虚线圆圈，无光晕
            if (canMove)
            {
                using (Pen cp = new Pen(Color.FromArgb(180, 60, 60, 60), 1.8f)
                    { DashStyle = DashStyle.Dash, DashPattern = new float[] { 4, 3 } })
                    g.DrawEllipse(cp, x - r - 5, y - r - 5, (r + 5) * 2, (r + 5) * 2);
            }

            // 悬停效果：橙色虚线
            if (_hoverPlayer == player && _hoverPiece == idx)
                using (Pen hp = new Pen(Color.DarkOrange, 2.5f) { DashStyle = DashStyle.Dash })
                    g.DrawEllipse(hp, x - r - 4, y - r - 4, (r + 4) * 2, (r + 4) * 2);

            // 平面飞机造型（无论是否完成，统一外观，无状态标识）
            DrawFlatAirplane(g, x, y, r, main, dark, light);

            // 棋子小序号
            using (Font f = new Font("Arial", 6.5f, FontStyle.Bold))
            using (Brush tb = new SolidBrush(Color.White))
            {
                var sz = g.MeasureString((idx + 1).ToString(), f);
                g.DrawString((idx + 1).ToString(), f, tb,
                    x - sz.Width / 2, y - sz.Height / 2 + 1);
            }
        }

        /// <summary>绘制已归营棋子（金色光环 + 星标，区别于普通棋子）</summary>
        private void DrawGoalPiece(Graphics g, int player, int idx, float x, float y)
        {
            float r = 11f;
            Color main = PlyCol[player];
            Color dark = PlyDark[player];
            Color light = PlyLight[player];

            // 金色外环（双环效果）
            using (Pen goldOuter = new Pen(Color.FromArgb(220, 218, 165, 32), 3f))
                g.DrawEllipse(goldOuter, x - r - 6, y - r - 6, (r + 6) * 2, (r + 6) * 2);
            using (Pen goldInner = new Pen(Color.FromArgb(200, 255, 215, 0), 1.5f))
                g.DrawEllipse(goldInner, x - r - 3, y - r - 3, (r + 3) * 2, (r + 3) * 2);

            // 平面飞机
            DrawFlatAirplane(g, x, y, r, main, dark, light);

            // 金色五角星（归营标志）
            DrawGoldStar(g, x, y, r);

            // 棋子小序号
            using (Font f = new Font("Arial", 6.5f, FontStyle.Bold))
            using (Brush tb = new SolidBrush(Color.White))
            {
                var sz = g.MeasureString((idx + 1).ToString(), f);
                g.DrawString((idx + 1).ToString(), f, tb,
                    x - sz.Width / 2, y - sz.Height / 2 + 1);
            }
        }

        /// <summary>绘制金色五角星（归营标记）</summary>
        private void DrawGoldStar(Graphics g, float cx, float cy, float r)
        {
            int pts = 5;
            float outerR = r * 0.45f, innerR = r * 0.2f;
            PointF[] star = new PointF[pts * 2];
            for (int i = 0; i < pts * 2; i++)
            {
                float rad = (float)(-Math.PI / 2 + i * Math.PI / pts);
                float radius = (i % 2 == 0) ? outerR : innerR;
                star[i] = new PointF(cx + (float)Math.Cos(rad) * radius,
                                     cy + (float)Math.Sin(rad) * radius);
            }
            using (Brush sb = new SolidBrush(Color.FromArgb(240, 255, 215, 0)))
                g.FillPolygon(sb, star);
            using (Pen sp = new Pen(Color.FromArgb(200, 184, 134, 11), 0.8f))
                g.DrawPolygon(sp, star);
        }

        /// <summary>绘制平面飞机棋子（无渐变、无光效）</summary>
        private void DrawFlatAirplane(Graphics g, float x, float y, float r,
            Color main, Color dark, Color light)
        {
            // 主体圆形（纯色填充）
            using (Brush bb = new SolidBrush(main))
                g.FillEllipse(bb, x - r, y - r, r * 2, r * 2);

            // 轮廓
            using (Pen bp = new Pen(dark, 2f))
                g.DrawEllipse(bp, x - r, y - r, r * 2, r * 2);

            // 机头（上半部稍浅的半椭球）
            float headH = r * 0.55f, headW = r * 0.7f;
            using (Brush hb = new SolidBrush(light))
                g.FillEllipse(hb, x - headW, y - r - headH * 0.7f, headW * 2, headH * 2);
            using (Pen hp = new Pen(dark, 1.2f))
                g.DrawEllipse(hp, x - headW, y - r - headH * 0.7f, headW * 2, headH * 2);

            // 两侧机翼
            PointF[] wingL = {
                new PointF(x - r - 2, y),
                new PointF(x - r + 4, y - 4),
                new PointF(x - r + 4, y + 4)
            };
            PointF[] wingR = {
                new PointF(x + r + 2, y),
                new PointF(x + r - 4, y - 4),
                new PointF(x + r - 4, y + 4)
            };
            using (Brush wb = new SolidBrush(dark))
            {
                g.FillPolygon(wb, wingL);
                g.FillPolygon(wb, wingR);
            }

            // 高光小点（平面风格的小白点）
            using (Brush hl = new SolidBrush(Color.FromArgb(160, 255, 255, 255)))
                g.FillEllipse(hl, x - 3, y - 5, 4, 3);
        }

        private bool IsMyValidMove(GameState st, int pid, int pidx)
        {
            if (st.GameOver || _myPlayerId < 0) return false;
            if (pid != _myPlayerId) return false;
            if (_myPlayerId != st.CurrentPlayerIndex) return false;
            if (st.DiceValue <= 0) return false;
            var eng = new FlightChessEngine();
            return eng.GetValidMoves(st.Players[_myPlayerId], st.DiceValue).Contains(pidx);
        }

        // =================================================================
        //  鼠标交互
        // =================================================================
        private PointF PieceScreenPos(GameState st, int pl, int qi)
        {
            int pos = st.Players[pl].Pieces[qi];
            // START标记位置
            if (pos == FlightChessEngine.StartPosition)
                return _startMarkers[pl];
            // 基地或已完成
            if (pos == -1)
            {
                int si = 0;
                for (int j = 0; j < qi; j++)
                {
                    int pj = st.Players[pl].Pieces[j];
                    if (pj == -1) si++;
                }
                return si < 4 ? _baseSlots[pl][si] : _baseSlots[pl][0];
            }
            if (pos == 58)
            {
                // 归营棋子回到大本营显示，与基地棋子共用槽位
                int si = 0;
                for (int j = 0; j < qi; j++)
                {
                    int pj = st.Players[pl].Pieces[j];
                    if (pj == -1 || pj == 58) si++;
                }
                return si < 4 ? _baseSlots[pl][si] : _baseSlots[pl][0];
            }
            if (pos >= 52 && pos <= 57)
                return _returnPathSpots[pl][pos - 52];
            int abs = FlightChessEngine.ToAbsoluteIndex(st.Players[pl].StartOffset, pos);
            return (abs >= 0 && abs < 52) ? _gridPos[abs] : new PointF(350, 350);
        }

        private void BoardPanel_MouseClick(object sender, MouseEventArgs e)
        {
            if (_myPlayerId < 0) return;
            GameState st;
            lock (_stateLock) { if (_currentGameState == null) return; st = _currentGameState.DeepCopy(); }
            (int p, int q)? best = null; float bestD = 18f;
            for (int pl = 0; pl < 4; pl++)
            {
                if (!st.Players[pl].IsConnected) continue;
                for (int qi = 0; qi < 4; qi++)
                {
                    var sc = PieceScreenPos(st, pl, qi);
                    float d = Dist(e.X, e.Y, sc.X, sc.Y);
                    if (d < bestD) { bestD = d; best = (pl, qi); }
                }
            }
            if (!best.HasValue) return;
            var hit = best.Value;
            if (hit.p != _myPlayerId) { Log("不是你的棋子。"); return; }
            if (st.GameOver) { Log("游戏已结束。"); return; }
            if (_myPlayerId != st.CurrentPlayerIndex) { Log("还没轮到你。"); return; }
            if (st.DiceValue <= 0) { Log("请先掷骰子。"); return; }
            var eng = new FlightChessEngine();
            if (!eng.GetValidMoves(st.Players[_myPlayerId], st.DiceValue).Contains(hit.q))
            { Log("无法移动。"); return; }
            SendMessage(new MovePieceMessage { PieceIndex = hit.q });
            Log("移动棋子 {0}", hit.q + 1);
        }

        private void BoardPanel_MouseMove(object sender, MouseEventArgs e)
        {
            GameState st;
            lock (_stateLock) { if (_currentGameState == null) return; st = _currentGameState.DeepCopy(); }
            float bestD = 18f; (int p, int q) best = (-1, -1);
            for (int pl = 0; pl < 4; pl++)
            {
                if (!st.Players[pl].IsConnected) continue;
                for (int qi = 0; qi < 4; qi++)
                {
                    var sc = PieceScreenPos(st, pl, qi);
                    float d = Dist(e.X, e.Y, sc.X, sc.Y);
                    if (d < bestD) { bestD = d; best = (pl, qi); }
                }
            }
            if (best.p != _hoverPlayer || best.q != _hoverPiece)
            {
                _hoverPlayer = best.p; _hoverPiece = best.q;
                boardPanel.Invalidate();
            }
        }

        private void BoardPanel_MouseLeave(object sender, EventArgs e)
        {
            _hoverPlayer = -1; _hoverPiece = -1; boardPanel.Invalidate();
        }

        private static float Dist(float x1, float y1, float x2, float y2)
        {
            float dx = x1 - x2, dy = y1 - y2;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private static float DistF(float x1, float y1, float x2, float y2)
        {
            float dx = x1 - x2, dy = y1 - y2;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        // =================================================================
        //  动画：棋子依次走过沿途格子 / 飞跃直飞 / 踩子重叠
        // =================================================================
        private void AnimTimer_Tick(object sender, EventArgs e)
        {
            if (_animPath != null && _animPathIndex < _animPath.Count - 1)
            {
                _animPathIndex++;
                boardPanel.Invalidate();
            }
            else
            {
                _animTimer.Stop();
                _animPlayer = -1;
                _animPiece = -1;
                _animPath = null;
                // 动画结束后清除踩子残留（被踩棋子已回到基地）
                _kickedPlayer = -1;
                _kickedPiece = -1;
                boardPanel.Invalidate();
            }
        }

        /// <summary>根据位置值获取对应的屏幕坐标（用于动画路径）</summary>
        private PointF GetScreenPosForPosition(GameState st, int player, int pos)
        {
            if (pos == FlightChessEngine.StartPosition)
                return _startMarkers[player];
            if (pos >= 52 && pos <= 57)
                return _returnPathSpots[player][pos - 52];
            if (pos >= 0 && pos < 52)
            {
                int abs = FlightChessEngine.ToAbsoluteIndex(st.Players[player].StartOffset, pos);
                return (abs >= 0 && abs < 52) ? _gridPos[abs] : new PointF(360, 360);
            }
            // 基地或终点：返回大本营中心附近
            return _baseSlots[player][0];
        }

        /// <summary>构建棋子从 fromPos 到 toPos 沿途的屏幕坐标路径（沿主路径逐格）</summary>
        private System.Collections.Generic.List<PointF> BuildAnimPath(GameState st, int player,
            int fromPos, int toPos)
        {
            var path = new System.Collections.Generic.List<PointF>();
            for (int p = fromPos + 1; p <= toPos; p++)
            {
                path.Add(GetScreenPosForPosition(st, player, p));
            }
            return path;
        }

        /// <summary>
        /// 构建飞跃跳组合动画路径：
        /// 第一阶段从 oldPos+1 沿主路径走到位置 17（骰子移动部分）；
        /// 第二阶段从 17 沿白色直线飞到 29（飞跃部分）。
        /// </summary>
        private System.Collections.Generic.List<PointF> BuildCombinedFlightAnimPath(
            GameState st, int player, int oldPos)
        {
            var path = new System.Collections.Generic.List<PointF>();

            // Phase 1: 沿主路径从 oldPos+1 走到 17（骰子移动）
            for (int p = oldPos + 1; p <= 17; p++)
            {
                path.Add(GetScreenPosForPosition(st, player, p));
            }

            // Phase 2: 沿白色直线从 17 飞到 29（飞跃跳）
            PointF from = GetScreenPosForPosition(st, player, 17);
            PointF to = GetScreenPosForPosition(st, player, 29);
            int flightSteps = 10;
            for (int i = 1; i <= flightSteps; i++)
            {
                float t = (float)i / (flightSteps + 1);
                path.Add(new PointF(
                    from.X + (to.X - from.X) * t,
                    from.Y + (to.Y - from.Y) * t));
            }
            return path;
        }

        /// <summary>检测被踩回基地的棋子（旧状态在主路径 → 新状态在基地）</summary>
        private void DetectKickedPieces(GameState oldState, GameState newState)
        {
            _kickedPlayer = -1;
            _kickedPiece = -1;
            for (int pl = 0; pl < 4; pl++)
            {
                if (!newState.Players[pl].IsConnected) continue;
                for (int qi = 0; qi < 4; qi++)
                {
                    int oldPos = oldState.Players[pl].Pieces[qi];
                    int newPos = newState.Players[pl].Pieces[qi];
                    // 从主路径上被踩回基地
                    if (oldPos >= 0 && oldPos < FlightChessEngine.BoardSize && newPos == -1)
                    {
                        _kickedPlayer = pl;
                        _kickedPiece = qi;
                        _kickedScreenPos = GetScreenPosForPosition(oldState, pl, oldPos);
                        return;
                    }
                }
            }
        }

        /// <summary>检测新旧状态之间的棋子移动，触发步进动画</summary>
        private void DetectAndAnimate(GameState oldState, GameState newState)
        {
            if (_animTimer.Enabled) return;  // 动画进行中，跳过
            if (oldState?.Players == null || newState?.Players == null) return;

            for (int pl = 0; pl < 4; pl++)
            {
                if (!newState.Players[pl].IsConnected) continue;
                for (int qi = 0; qi < 4; qi++)
                {
                    int oldPos = oldState.Players[pl].Pieces[qi];
                    int newPos = newState.Players[pl].Pieces[qi];
                    if (oldPos == newPos) continue;

                    // 飞跃跳检测：骰子移动 + 飞跃跳的总位移 ≥ 12
                    // （最大普通移动 = 骰子6 + 同色跳4 = 10；飞跃跳 = 骰子1~6 + 12）
                    bool isFlightJump = newPos >= 0 && newPos < FlightChessEngine.BoardSize
                        && oldPos >= 0 && oldPos < FlightChessEngine.BoardSize
                        && newPos - oldPos >= 12;

                    if (isFlightJump)
                    {
                        _animPlayer = pl;
                        _animPiece = qi;
                        _animPath = BuildCombinedFlightAnimPath(newState, pl, oldPos);
                        if (_animPath.Count > 0)
                        {
                            _animPathIndex = 0;
                            _animTimer.Start();
                        }
                        DetectKickedPieces(oldState, newState);
                        return;
                    }

                    // 主路径和归营路径上的前进移动
                    if (newPos > oldPos && newPos <= FlightChessEngine.FinishEnd
                        && oldPos >= FlightChessEngine.StartPosition)
                    {
                        _animPlayer = pl;
                        _animPiece = qi;
                        _animPath = BuildAnimPath(newState, pl, oldPos, newPos);
                        if (_animPath.Count > 0)
                        {
                            _animPathIndex = 0;
                            _animTimer.Start();
                        }
                        DetectKickedPieces(oldState, newState);
                        return;
                    }

                    // 归营路径回退动画（超出 57 反弹回来）
                    if (oldPos >= FlightChessEngine.FinishStart
                        && oldPos <= FlightChessEngine.FinishEnd
                        && newPos >= FlightChessEngine.FinishStart
                        && newPos <= FlightChessEngine.FinishEnd
                        && newPos < oldPos)
                    {
                        var bouncePath = new System.Collections.Generic.List<PointF>();
                        for (int p = oldPos + 1; p <= FlightChessEngine.FinishEnd; p++)
                            bouncePath.Add(GetScreenPosForPosition(newState, pl, p));
                        for (int p = FlightChessEngine.FinishEnd - 1; p >= newPos; p--)
                            bouncePath.Add(GetScreenPosForPosition(newState, pl, p));
                        _animPlayer = pl;
                        _animPiece = qi;
                        _animPath = bouncePath;
                        if (_animPath.Count > 0)
                        {
                            _animPathIndex = 0;
                            _animTimer.Start();
                        }
                        // 归营路径无踩子
                        return;
                    }
                }
            }
        }

        // =================================================================
        //  网络通信（维持原有逻辑不变）
        // =================================================================
        private void ConnectToServer(string addr, int port)
        {
            try
            {
                _tcpClient = new TcpClient(); _tcpClient.Connect(addr, port);
                _stream = _tcpClient.GetStream(); _reader = new StreamReader(_stream, Encoding.UTF8);
                _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };
                _isConnected = true;
                Log("已连接 {0}:{1}", addr, port);
                SendMessage(new JoinGameMessage { PlayerName = _myPlayerName });
                _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
                _receiveThread.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法连接: " + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
            }
        }

        private void SendMessage(object m)
        {
            if (!_isConnected) return;
            try { _writer.WriteLine(JsonConvert.SerializeObject(m)); }
            catch (Exception ex) { Log("发送失败: {0}", ex.Message); HandleDisconnect(); }
        }

        private void ReceiveLoop()
        {
            try
            {
                while (_isConnected)
                {
                    string l = _reader.ReadLine();
                    if (l == null) break;
                    if (string.IsNullOrWhiteSpace(l)) continue;
                    ProcessMsg(l);
                }
            }
            catch (IOException) { }
            catch (Exception ex) { LogInvoke("接收错误: {0}", ex.Message); }
            finally { HandleDisconnect(); }
        }

        private void ProcessMsg(string json)
        {
            try
            {
                var o = JObject.Parse(json);
                var t = o["Type"]?.Value<string>();
                this.Invoke(new Action(() =>
                {
                    try
                    {
                        switch (t)
                        {
                            case MessageType.GameStateUpdate: HandleGS(json); break;
                            case MessageType.JoinGameResponse: HandleJR(json); break;
                            case MessageType.Error: HandleErr(json); break;
                            case MessageType.PlayerLeft: HandlePL(json); break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // 防止 UI 线程异常导致接收线程崩溃
                        Log("处理消息异常: {0}", ex.Message);
                    }
                }));
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
            catch (Exception ex)
            {
                Log("消息解析异常: {0}", ex.Message);
            }
        }

        private void HandleGS(string json)
        {
            var m = JsonConvert.DeserializeObject<GameStateUpdateMessage>(json);
            GameState oldState = null;
            lock (_stateLock)
            {
                if (_currentGameState != null)
                    oldState = _currentGameState.DeepCopy();
                _currentGameState = m.State;
            }
            // 检测棋子移动并触发步进动画（异常不影响 UI 状态更新）
            if (oldState != null)
            {
                try { DetectAndAnimate(oldState, m.State); }
                catch (Exception ex) { Log("动画异常: {0}", ex.Message); }
            }
            UpdateUI(m.State);
        }

        private void HandleJR(string json)
        {
            var m = JsonConvert.DeserializeObject<JoinGameResponseMessage>(json);
            _myPlayerId = m.PlayerId;
            this.Text = string.Format("飞行棋 - {0}({1}方)", _myPlayerName, PlyName[_myPlayerId]);
            Log("加入成功！你是 {0} 方", PlyName[_myPlayerId]);
        }

        private void HandleErr(string json)
        {
            var m = JsonConvert.DeserializeObject<ErrorMessage>(json);
            MessageBox.Show(m.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Log("[服务器] {0}", m.Message);
        }

        private void HandlePL(string json)
        {
            var m = JsonConvert.DeserializeObject<PlayerLeftMessage>(json);
            Log("{0} 离开了。", m.PlayerName);
        }

        private void HandleDisconnect()
        {
            _isConnected = false;
            try { _stream?.Close(); } catch { }
            try { _tcpClient?.Close(); } catch { }
            this.Invoke(new Action(() =>
            {
                Log("连接已断开。");
                btnRollDice.Enabled = false;
                lblCurrentPlayer.Text = "连接已断开";
                boardPanel.Invalidate();
            }));
        }

        // =================================================================
        //  UI 更新
        // =================================================================
        private void UpdateUI(GameState st)
        {
            if (st.GameOver)
            {
                lblCurrentPlayer.Text = string.Format("{0}({1}方) 获胜！",
                    st.Players[st.WinnerIndex].Name, PlyName[st.WinnerIndex]);
                lblCurrentPlayer.ForeColor = PlyCol[st.WinnerIndex];
                btnRollDice.Enabled = false;
                btnRollDice.Text = "游戏结束";
            }
            else
            {
                var cp = st.Players[st.CurrentPlayerIndex];
                lblCurrentPlayer.Text = string.Format("当前: {0}({1}方)",
                    cp.Name, PlyName[st.CurrentPlayerIndex]);
                lblCurrentPlayer.ForeColor = PlyCol[st.CurrentPlayerIndex];
            }
            lblDiceValue.Text = st.DiceValue > 0 ? string.Format("{0} 点", st.DiceValue) : "骰子: -";

            if (!st.GameOver && _myPlayerId >= 0 && _myPlayerId == st.CurrentPlayerIndex)
            {
                if (st.DiceValue == 0)
                {
                    btnRollDice.Enabled = true;
                    btnRollDice.Text = "掷骰子";
                    btnRollDice.BackColor = Color.FromArgb(180, 255, 180);
                }
                else
                {
                    btnRollDice.Enabled = false;
                    btnRollDice.Text = "点击棋子";
                    btnRollDice.BackColor = Color.FromArgb(255, 255, 200);
                }
                btnReset.Enabled = true;
            }
            else if (!st.GameOver)
            {
                btnRollDice.Enabled = false;
                btnRollDice.Text = "等待他人...";
                btnReset.Enabled = true;
            }

            if (st.LogMessages != null)
            {
                int cur = lstLog.Items.Count;
                for (int i = cur; i < st.LogMessages.Length; i++)
                    lstLog.Items.Add(st.LogMessages[i]);
                if (lstLog.Items.Count > 0) lstLog.TopIndex = lstLog.Items.Count - 1;
            }
            boardPanel.Invalidate();
        }

        private void BtnRollDice_Click(object s, EventArgs e)
        {
            if (_myPlayerId < 0) { MessageBox.Show("尚未加入游戏。"); return; }
            SendMessage(new RollDiceMessage());
        }

        private void BtnReset_Click(object s, EventArgs e)
        {
            if (MessageBox.Show("重置需重启客户端。", "确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                HandleDisconnect();
                MessageBox.Show("请重启客户端。");
            }
        }

        private void MainForm_FormClosing(object s, FormClosingEventArgs e)
        {
            _isConnected = false;
            try { _stream?.Close(); } catch { }
            try { _tcpClient?.Close(); } catch { }
        }

        // =================================================================
        //  日志
        // =================================================================
        public void Log(string fmt, params object[] args)
        {
            string m = string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, string.Format(fmt, args));
            if (lstLog.InvokeRequired)
                this.Invoke(new Action(() =>
                {
                    lstLog.Items.Add(m);
                    lstLog.TopIndex = lstLog.Items.Count - 1;
                }));
            else
            {
                lstLog.Items.Add(m);
                lstLog.TopIndex = lstLog.Items.Count - 1;
            }
        }

        private void LogInvoke(string fmt, params object[] args)
        {
            try
            {
                string m = string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, string.Format(fmt, args));
                this.Invoke(new Action(() =>
                {
                    lstLog.Items.Add(m);
                    lstLog.TopIndex = lstLog.Items.Count - 1;
                }));
            }
            catch (InvalidOperationException) { }
        }
    }
}
