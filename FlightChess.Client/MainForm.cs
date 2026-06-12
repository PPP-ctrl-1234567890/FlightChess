using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>飞行棋联机游戏主窗体 — 十字环形棋盘，52格外环，四角大本营，四条回营路径。</summary>
    public partial class MainForm : Form
    {
        // 网络
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private StreamReader _reader;
        private StreamWriter _writer;
        private Thread _receiveThread;
        private bool _isConnected;

        // 心跳检测
        private DateTime _lastServerMsgTime;
        private System.Windows.Forms.Timer _heartbeatCheckTimer;

        // 断线重连
        private bool _isReconnecting;
        private int _reconnectAttempts;
        private const int MAX_RECONNECT_ATTEMPTS = 15;
        private string _serverAddress;
        private int _serverPort;
        private Thread _reconnectThread;

        // 游戏状态
        private GameState _currentGameState;
        private int _myPlayerId = -1;
        private string _myPlayerName;
        private readonly object _stateLock = new object();

        // 棋盘常数
        private const int BdW = 700, BdH = 700;
        private const int Inset = 40;
        private const int CellSpacing = 1;
        private const int ArmW = 180;
        private const int CenterX = 310, CenterY = 310;
        private const int CenterW = 100, CenterH = 100;
        private const int BaseSize = 140;
        private const int BaseMargin = 30;

        // 棋盘数据
        private PointF[] _gridPos;
        private bool[] _isCorner;
        private float[] _cornerAngle;
        private int[] _cellColorType;
        private Rectangle[] _baseRects;
        private Point[][] _baseSlots;
        private Point[] _startMarkers;
        private PointF[][] _returnPathSpots;
        private PointF[][] _returnArrowDirs;
        private static readonly (int from, int to)[] _flightJumpPairs = {
            (17, 29), (4, 16), (43, 3), (30, 42)
        };
        private PointF[] _medalPositions;
        private Rectangle _centerRect;
        private Point _centerPt;

        private List<(PointF a, PointF b, float angle)> _segments;

        private int _hoverPlayer = -1, _hoverPiece = -1;

        // 动画
        private System.Windows.Forms.Timer _animTimer;
        private int _animPlayer = -1, _animPiece = -1;
        private System.Collections.Generic.List<PointF> _animPath;
        private int _animPathIndex;
        private int _kickedPlayer = -1, _kickedPiece = -1;
        private PointF _kickedScreenPos;

        // 烟花 & 炸裂特效
        private System.Windows.Forms.Timer _fireworkTimer;
        private List<FireworkParticle> _fireworkParticles;
        private bool _fireworksActive;
        private int _fireworkBurstsLeft;
        private Random _fireworkRng;
        private bool _victoryShown;
        private List<FireworkParticle> _explosionParticles;
        private bool _explosionActive;

        private class FireworkParticle
        {
            public float X, Y;
            public float Vx, Vy;
            public Color Color;
            public float Life;    // 剩余生命 0~1（1=满，0=消亡）
            public float Size;    // 粒子半径
        }

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

        public MainForm()
        {
            _myPlayerName = "设计器";
            CommonInit();
        }

        public MainForm(string server, int port, string name) : this()
        {
            _myPlayerName = name;
            // 必须提前创建窗口句柄，否则 JoinGameResponse 到达时 BeginInvoke 会失败
            if (!this.IsHandleCreated)
                CreateHandle();
            ConnectToServer(server, port);
        }

        private void CommonInit()
        {
            InitializeComponent();
            HookEvents();
            // 运行时启用双缓冲，设计时跳过（反射调用会破坏设计器）
            if (!DesignMode)
            {
                typeof(Panel).GetProperty("DoubleBuffered",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.SetValue(boardPanel, true, null);
            }
            InitAnimationTimer();
            InitFireworkTimer();
            InitHeartbeatTimer();
            InitBoardGeometry();
            InitChatPanel();
        }

        private void HookEvents()
        {
            this.FormClosing += MainForm_FormClosing;
            this.btnRollDice.Click += BtnRollDice_Click;
            this.boardPanel.Paint += BoardPanel_Paint;
            this.boardPanel.MouseClick += BoardPanel_MouseClick;
            this.boardPanel.MouseMove += BoardPanel_MouseMove;
            this.boardPanel.MouseLeave += BoardPanel_MouseLeave;
        }

        private void InitAnimationTimer()
        {
            _animTimer = new System.Windows.Forms.Timer();
            _animTimer.Interval = 50;
            _animTimer.Tick += AnimTimer_Tick;
        }

        private void InitHeartbeatTimer()
        {
            _heartbeatCheckTimer = new System.Windows.Forms.Timer();
            _heartbeatCheckTimer.Interval = 3000;
            _heartbeatCheckTimer.Tick += HeartbeatCheckTimer_Tick;
        }

        private void InitFireworkTimer()
        {
            _fireworkTimer = new System.Windows.Forms.Timer();
            _fireworkTimer.Interval = 33;
            _fireworkTimer.Tick += FireworkTimer_Tick;
            _fireworkParticles = new List<FireworkParticle>();
            _explosionParticles = new List<FireworkParticle>();
            _fireworkRng = new Random();
        }

        // 棋盘几何初始化 — 十字环形布局（12段逆时针闭合路径，总长2080，step=40）
        private void InitBoardGeometry()
        {
            int L = CenterX, T = CenterY;
            int R = CenterX + CenterW, B = CenterY + CenterH;
            int cx = (L + R) / 2, cy = (T + B) / 2;
            _centerRect = new Rectangle(L, T, CenterW, CenterH);
            _centerPt = new Point(cx, cy);

            const int OuterR = 620, OuterL = 100;
            const int OuterB = 620, OuterT = 100;

            // 12段逆时针闭合路径：4条边×200px(5格) + 8条臂×160px(4格)
            _segments = new List<(PointF a, PointF b, float angle)>();

            // 从右下角出发，逆时针：S1~S12
            _segments.Add((new PointF(OuterR, 460), new PointF(460, 460), (float)Math.PI));
            _segments.Add((new PointF(460, 460), new PointF(460, OuterB), (float)(Math.PI / 2)));
            _segments.Add((new PointF(460, OuterB), new PointF(260, OuterB), (float)Math.PI));
            _segments.Add((new PointF(260, OuterB), new PointF(260, 460), -(float)(Math.PI / 2)));
            _segments.Add((new PointF(260, 460), new PointF(OuterL, 460), (float)Math.PI));
            _segments.Add((new PointF(OuterL, 460), new PointF(OuterL, 260), -(float)(Math.PI / 2)));
            _segments.Add((new PointF(OuterL, 260), new PointF(260, 260), 0f));
            _segments.Add((new PointF(260, 260), new PointF(260, OuterT), -(float)(Math.PI / 2)));
            _segments.Add((new PointF(260, OuterT), new PointF(460, OuterT), 0f));
            _segments.Add((new PointF(460, OuterT), new PointF(460, 260), (float)(Math.PI / 2)));
            _segments.Add((new PointF(460, 260), new PointF(OuterR, 260), 0f));
            _segments.Add((new PointF(OuterR, 260), new PointF(OuterR, 460), (float)(Math.PI / 2)));

            float totalLen = 0;
            foreach (var (a, b, _) in _segments)
                totalLen += Dist(a.X, a.Y, b.X, b.Y);

            float step = totalLen / 52f;

            _gridPos = new PointF[52];
            _isCorner = new bool[52];
            _cornerAngle = new float[52];
            _cellColorType = new int[52];

            float[] segEndDists = new float[_segments.Count];
            float cumLen = 0;
            for (int s = 0; s < _segments.Count; s++)
            {
                var (a, b, _) = _segments[s];
                cumLen += Dist(a.X, a.Y, b.X, b.Y);
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
                float segLen = Dist(sa.X, sa.Y, sb.X, sb.Y);
                float t = (dist - segStartDist) / segLen;
                if (t < 0) t = 0;
                if (t > 1) t = 1;
                _gridPos[i] = new PointF(
                    sa.X + (sb.X - sa.X) * t,
                    sa.Y + (sb.Y - sa.Y) * t);

                _isCorner[i] = false;
                _cornerAngle[i] = sAng;
                int[] colorSeq = { 1, 0, 3, 2 };  // 绿、红、蓝、黄四色循环
                _cellColorType[i] = colorSeq[i % 4];
            }

            // 大本营（四角）：用内缘坐标定位，BaseSize 向外延伸
            const int BaseInnerRight  = 495;
            const int BaseInnerBottom = 495;
            const int BaseInnerLeft   = 205;
            const int BaseInnerTop    = 205;

            _baseRects = new Rectangle[4];
            _baseRects[0] = new Rectangle(BaseInnerRight, BaseInnerBottom + 20, BaseSize, BaseSize);
            _baseRects[1] = new Rectangle(BaseInnerRight + 20, BaseInnerTop - BaseSize, BaseSize, BaseSize);
            _baseRects[2] = new Rectangle(BaseInnerLeft - BaseSize, BaseInnerTop - BaseSize, BaseSize, BaseSize);
            _baseRects[3] = new Rectangle(BaseInnerLeft - BaseSize, BaseInnerBottom, BaseSize, BaseSize);

            _baseSlots = new Point[4][];
            for (int p = 0; p < 4; p++)
            {
                var r = _baseRects[p];
                int bcx = r.X + r.Width / 2, bcy = r.Y + r.Height / 2;
                int gap = 35;
                _baseSlots[p] = new Point[] {
                    new Point(bcx - gap/2, bcy - gap/2),
                    new Point(bcx + gap/2, bcy - gap/2),
                    new Point(bcx - gap/2, bcy + gap/2),
                    new Point(bcx + gap/2, bcy + gap/2) };
            }

            _medalPositions = new PointF[4];
            _medalPositions[0] = new PointF(_baseRects[0].X - 30, _baseRects[0].Y + 30);
            _medalPositions[1] = new PointF(_baseRects[1].X + 30, _baseRects[1].Bottom + 20);
            _medalPositions[2] = new PointF(_baseRects[2].Right + 30, _baseRects[2].Bottom + 20);
            _medalPositions[3] = new PointF(_baseRects[3].Right + 30, _baseRects[3].Y + 30);

            _startMarkers = new Point[4];
            _startMarkers[0] = new Point(_baseRects[0].X + _baseRects[0].Width / 2 + 53,
                                         _baseRects[0].Y - 18);
            _startMarkers[1] = new Point(_baseRects[1].X - 18,
                                         _baseRects[1].Y + _baseRects[1].Height / 2 - 44);
            _startMarkers[2] = new Point(_baseRects[2].X + _baseRects[2].Width / 2 - 53,
                                         _baseRects[2].Y + _baseRects[2].Height + 18);
            _startMarkers[3] = new Point(_baseRects[3].X + _baseRects[3].Width + 18,
                                         _baseRects[3].Y + _baseRects[3].Height / 2 + 44);

            // 回营路径：6步/40px步长，向中心平移40px避开外环，第6格在中心三角形内
            _returnPathSpots = new PointF[4][];
            _returnArrowDirs = new PointF[4][];
            for (int p = 0; p < 4; p++)
            {
                _returnPathSpots[p] = new PointF[6];
                _returnArrowDirs[p] = new PointF[6];
            }
            const float retStep = 40f;
            const float retOffset = 40f;

            // 红（右臂Y=360，向左）
            for (int i = 0; i < 5; i++)
            {
                _returnPathSpots[0][i] = new PointF(OuterR - retOffset - i * retStep, cy);
                _returnArrowDirs[0][i] = new PointF(-1, 0);
            }
            _returnPathSpots[0][5] = new PointF(R - CenterW / 6f, cy);

            // 绿（上臂X=360，向下）
            for (int i = 0; i < 5; i++)
            {
                _returnPathSpots[1][i] = new PointF(cx, OuterT + retOffset + i * retStep);
                _returnArrowDirs[1][i] = new PointF(0, 1);
            }
            _returnPathSpots[1][5] = new PointF(cx, T + CenterH / 6f);

            // 黄（左臂Y=360，向右）
            for (int i = 0; i < 5; i++)
            {
                _returnPathSpots[2][i] = new PointF(OuterL + retOffset + i * retStep, cy);
                _returnArrowDirs[2][i] = new PointF(1, 0);
            }
            _returnPathSpots[2][5] = new PointF(L + CenterW / 6f, cy);

            // 蓝（下臂X=360，向上）
            for (int i = 0; i < 5; i++)
            {
                _returnPathSpots[3][i] = new PointF(cx, OuterB - retOffset - i * retStep);
                _returnArrowDirs[3][i] = new PointF(0, -1);
            }
            _returnPathSpots[3][5] = new PointF(cx, B - CenterH / 6f);
        }

        // 主绘制入口
        private void BoardPanel_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            DrawBoardBackground(g);
            DrawCrossArms(g);
            DrawOuterRingCells(g);
            DrawFlightJumps(g);
            DrawBases(g);
            DrawReturnPaths(g);
            DrawCenter(g);
            DrawAllPieces(g);
            DrawFireworks(g);
            DrawExplosion(g);
            DrawPodium(g);
        }

        private void DrawBoardBackground(Graphics g)
        {
            using (Brush bg = new SolidBrush(Color.FromArgb(248, 242, 225)))
                g.FillRectangle(bg, 0, 0, BdW, BdH);
        }

        private void DrawCrossArms(Graphics g)
        {
        }

        private void DrawOuterRingCells(Graphics g)
        {
            float cellHalfShort = 12f;
            float cellHalfLong = 20f;
            float circleR = 9f;

            for (int i = 0; i < 52; i++)
            {
                PointF pt = _gridPos[i];
                int colorType = _cellColorType[i];
                Color fill = CellColors[colorType];
                Color dark = CellColorsDark[colorType];

                float ang = _cornerAngle[i];
                bool isHorizontal = Math.Abs(Math.Sin(ang)) < 0.01f;

                float halfW, halfH;
                if (isHorizontal)
                {
                    halfW = cellHalfShort;
                    halfH = cellHalfLong;
                }
                else
                {
                    halfW = cellHalfLong;
                    halfH = cellHalfShort;
                }

                RectangleF rect = new RectangleF(
                    pt.X - halfW, pt.Y - halfH,
                    halfW * 2, halfH * 2);
                using (Brush bg = new SolidBrush(fill))
                    g.FillRectangle(bg, rect);

                using (Brush wb = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
                    g.FillEllipse(wb, pt.X - circleR, pt.Y - circleR, circleR * 2, circleR * 2);
                using (Pen wp = new Pen(Color.FromArgb(180, 210, 205, 195), 0.8f))
                    g.DrawEllipse(wp, pt.X - circleR, pt.Y - circleR, circleR * 2, circleR * 2);
            }
        }

        private void DrawBases(Graphics g)
        {
            for (int p = 0; p < 4; p++)
            {
                var r = _baseRects[p];
                Color baseColor = PlyCol[p];
                Color lightColor = PlyLight[p];

                using (Brush bg = new SolidBrush(baseColor))
                    g.FillRectangle(bg, r);

                int circleR = 14;
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                int gap = 33;
                int[][] offsets = new int[][] {
                    new int[] { -gap/2, -gap/2 },
                    new int[] { gap/2, -gap/2 },
                    new int[] { -gap/2, gap/2 },
                    new int[] { gap/2, gap/2 }
                };
                foreach (var off in offsets)
                {
                    int ccx = cx + off[0], ccy = cy + off[1];
                    using (Brush cb = new SolidBrush(Color.FromArgb(200, lightColor)))
                        g.FillEllipse(cb, ccx - circleR, ccy - circleR, circleR * 2, circleR * 2);
                    using (Pen cp = new Pen(Color.FromArgb(180, PlyDark[p]), 1.5f))
                        g.DrawEllipse(cp, ccx - circleR, ccy - circleR, circleR * 2, circleR * 2);
                }

                var sm = _startMarkers[p];
                int sr = 16;
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

        private void DrawReturnPaths(Graphics g)
        {
            for (int p = 0; p < 4; p++)
            {
                var spots = _returnPathSpots[p];
                var dirs = _returnArrowDirs[p];
                Color pathColor = PlyCol[p];

                float cellHalf = 16f;
                float circleR = 8f;

                for (int i = 0; i < 6; i++)
                {
                    PointF pt = spots[i];
                    bool isCenterStep = (i == 5);

                    if (!isCenterStep)
                    {
                        RectangleF rect = new RectangleF(
                            pt.X - cellHalf, pt.Y - cellHalf,
                            cellHalf * 2, cellHalf * 2);
                        using (Brush bg = new SolidBrush(pathColor))
                            g.FillRectangle(bg, rect);

                        DrawTinyPlane(g, pt, Color.FromArgb(180, PlyLight[p]), 0.6f);
                        DrawTinyArrow(g, pt, dirs[i], Color.FromArgb(220, 255, 255, 255));
                    }

                    using (Brush wb = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                        g.FillEllipse(wb, pt.X - circleR, pt.Y - circleR, circleR * 2, circleR * 2);
                    using (Pen wp = new Pen(Color.FromArgb(150, 200, 195, 185), 0.8f))
                        g.DrawEllipse(wp, pt.X - circleR, pt.Y - circleR, circleR * 2, circleR * 2);
                }
            }
        }

        private void DrawTinyPlane(Graphics g, PointF pt, Color color, float scale)
        {
            float radius = 8f * scale;  
            using (Brush brush = new SolidBrush(color))
            {
                g.FillEllipse(brush, pt.X - radius, pt.Y - radius, radius * 2, radius * 2);
            }
            using (Pen pen = new Pen(Color.Black, 0.8f))
            {
                g.DrawEllipse(pen, pt.X - radius, pt.Y - radius, radius * 2, radius * 2);
            }
        }

        private void DrawTinyArrow(Graphics g, PointF pt, PointF dir, Color color)
        {
            float len = 10f;
            float headSz = 4f;
            float dx = dir.X, dy = dir.Y;
            float mag = (float)Math.Sqrt(dx * dx + dy * dy);
            if (mag < 0.01f) return;
            dx /= mag; dy /= mag;
            float perpX = -dy, perpY = dx;

            PointF start = new PointF(pt.X - dx * len * 0.5f, pt.Y - dy * len * 0.5f);
            PointF end = new PointF(pt.X + dx * len * 0.3f, pt.Y + dy * len * 0.3f);
            using (Pen ap = new Pen(color, 1.5f))
            {
                ap.StartCap = LineCap.Round;
                ap.EndCap = LineCap.Round;
                g.DrawLine(ap, start, end);
            }

            PointF tip = new PointF(pt.X + dx * len * 0.55f, pt.Y + dy * len * 0.55f);
            PointF lw = new PointF(tip.X - dx * headSz + perpX * headSz * 0.6f,
                                    tip.Y - dy * headSz + perpY * headSz * 0.6f);
            PointF rw = new PointF(tip.X - dx * headSz - perpX * headSz * 0.6f,
                                    tip.Y - dy * headSz - perpY * headSz * 0.6f);
            PointF[] tri = { tip, lw, rw };
            using (Brush ab = new SolidBrush(color))
                g.FillPolygon(ab, tri);
        }

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

            using (Brush brush = new SolidBrush(PlyCol[1]))
                g.FillPolygon(brush, new PointF[] { center, topLeft, topRight });
            using (Brush brush = new SolidBrush(PlyCol[3]))
                g.FillPolygon(brush, new PointF[] { center, bottomLeft, bottomRight });
            using (Brush brush = new SolidBrush(PlyCol[2]))
                g.FillPolygon(brush, new PointF[] { center, topLeft, bottomLeft });
            using (Brush brush = new SolidBrush(PlyCol[0]))
                g.FillPolygon(brush, new PointF[] { center, topRight, bottomRight });

            float triCircleR = 10f;
            PointF triCtrUp = new PointF(cx, T + CenterH / 6);
            PointF triCtrDown = new PointF(cx, B - CenterH / 6);
            PointF triCtrLeft = new PointF(L + CenterW / 6, cy);
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

        private void DrawFlightJumps(Graphics g)
        {
            float lineWidth = 24f;

            for (int p = 0; p < 4; p++)
            {
                var (fromAbs, toAbs) = _flightJumpPairs[p];
                PointF from = _gridPos[fromAbs];
                PointF to = _gridPos[toAbs];

                using (Pen linePen = new Pen(Color.FromArgb(240, 255, 255, 255), lineWidth))
                {
                    linePen.StartCap = LineCap.Round;
                    linePen.EndCap = LineCap.Round;
                    g.DrawLine(linePen, from, to);
                }

                using (Pen edgePen = new Pen(Color.FromArgb(160, 200, 195, 185), 1f))
                    g.DrawLine(edgePen, from, to);

                DrawStraightArrow(g, from, to, 0.33f, p);
                DrawStraightArrow(g, from, to, 0.67f, p);
            }
        }

        private void DrawStraightArrow(Graphics g, PointF from, PointF to, float t, int playerIndex)
        {
            float dx = to.X - from.X;
            float dy = to.Y - from.Y;
            float mag = (float)Math.Sqrt(dx * dx + dy * dy);
            if (mag < 0.01f) return;
            float ux = dx / mag;
            float uy = dy / mag;
            float px = -uy;
            float py = ux;

            float cx = from.X + dx * t;
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

        // 棋子绘制
        private void DrawAllPieces(Graphics g)
        {
            GameState st;
            lock (_stateLock) { if (_currentGameState == null) return; st = _currentGameState.DeepCopy(); }

            List<(int p, int q)>[] cellOcc = new List<(int p, int q)>[52];
            for (int i = 0; i < 52; i++) cellOcc[i] = new List<(int, int)>();

            for (int pl = 0; pl < 4; pl++)
            {
                if (!st.Players[pl].HasJoined) continue;
                for (int qi = 0; qi < 4; qi++)
                {
                    int pos = st.Players[pl].Pieces[qi];
                    if (pos < 0 || pos >= 52) continue;
                    int abs = FlightChessEngine.ToAbsoluteIndex(st.Players[pl].StartOffset, pos);
                    if (abs >= 0 && abs < 52) cellOcc[abs].Add((pl, qi));
                }
            }

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
                        continue;
                    var (ox, oy) = StackOff(cellOcc[i].Count, k);
                    bool canMove = (pl == _myPlayerId && IsMyValidMove(st, pl, qi));
                    DrawPiece(g, pl, qi, ctr.X + ox, ctr.Y + oy, canMove);
                }
            }

            for (int pl = 0; pl < 4; pl++)
            {
                if (!st.Players[pl].HasJoined) continue;
                var basePieces = new System.Collections.Generic.List<int>();
                var goalPieces = new System.Collections.Generic.List<int>();
                for (int qi = 0; qi < 4; qi++)
                {
                    int pos = st.Players[pl].Pieces[qi];
                    if (pos == -1) basePieces.Add(qi);
                    else if (pos == 58) goalPieces.Add(qi);
                }

                int si = 0;
                foreach (int qi in basePieces)
                {
                    if (si >= 4) break;
                    if (isAnimating && pl == _animPlayer && qi == _animPiece)
                    { si++; continue; }
                    if (isAnimating && pl == _kickedPlayer && qi == _kickedPiece)
                    { si++; continue; }
                    Point pt = _baseSlots[pl][si];
                    DrawPiece(g, pl, qi, pt.X, pt.Y, false);
                    si++;
                }
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

            for (int pl = 0; pl < 4; pl++)
            {
                if (!st.Players[pl].HasJoined) continue;
                for (int qi = 0; qi < 4; qi++)
                {
                    int pos = st.Players[pl].Pieces[qi];
                    if (pos != FlightChessEngine.StartPosition) continue;
                    if (isAnimating && pl == _animPlayer && qi == _animPiece)
                        continue;
                    Point pt = _startMarkers[pl];
                    bool canMove = (pl == _myPlayerId && IsMyValidMove(st, pl, qi));
                    DrawPiece(g, pl, qi, pt.X, pt.Y, canMove);
                }
            }

            for (int pl = 0; pl < 4; pl++)
            {
                if (!st.Players[pl].HasJoined) continue;
                for (int qi = 0; qi < 4; qi++)
                {
                    int pos = st.Players[pl].Pieces[qi];
                    if (pos < 52 || pos > 57) continue;
                    if (isAnimating && pl == _animPlayer && qi == _animPiece)
                        continue;
                    int fi = pos - 52;
                    if (fi < 0 || fi >= 6) continue;
                    PointF pt = _returnPathSpots[pl][fi];
                    bool canMove = (pl == _myPlayerId && IsMyValidMove(st, pl, qi));
                    DrawPiece(g, pl, qi, pt.X, pt.Y, canMove);
                }
            }

            if (isAnimating && _animPlayer >= 0 && _animPiece >= 0)
            {
                PointF animPos = _animPath[_animPathIndex];
                DrawPiece(g, _animPlayer, _animPiece, animPos.X, animPos.Y, false);
            }

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

        private void DrawPiece(Graphics g, int player, int idx, float x, float y,
            bool canMove)
        {
            float r = 11f;
            Color main = PlyCol[player];
            Color dark = PlyDark[player];
            Color light = PlyLight[player];

            if (canMove)
            {
                using (Pen cp = new Pen(Color.FromArgb(180, 60, 60, 60), 1.8f)
                    { DashStyle = DashStyle.Dash, DashPattern = new float[] { 4, 3 } })
                    g.DrawEllipse(cp, x - r - 5, y - r - 5, (r + 5) * 2, (r + 5) * 2);
            }

            if (_hoverPlayer == player && _hoverPiece == idx)
                using (Pen hp = new Pen(Color.DarkOrange, 2.5f) { DashStyle = DashStyle.Dash })
                    g.DrawEllipse(hp, x - r - 4, y - r - 4, (r + 4) * 2, (r + 4) * 2);

            DrawFlatAirplane(g, x, y, r, main, dark, light);

            using (Font f = new Font("Arial", 6.5f, FontStyle.Bold))
            using (Brush tb = new SolidBrush(Color.White))
            {
                var sz = g.MeasureString((idx + 1).ToString(), f);
                g.DrawString((idx + 1).ToString(), f, tb,
                    x - sz.Width / 2, y - sz.Height / 2 + 1);
            }
        }

        private void DrawGoalPiece(Graphics g, int player, int idx, float x, float y)
        {
            float r = 11f;

            float ringOuterR = r + 3f;
            float ringThickness = 2.5f;
            using (Pen goldRing = new Pen(Color.FromArgb(240, 255, 215, 0), ringThickness))
                g.DrawEllipse(goldRing, x - ringOuterR, y - ringOuterR, ringOuterR * 2, ringOuterR * 2);

            Color brown = Color.FromArgb(220, 139, 90, 43);
            using (Brush brownBg = new SolidBrush(brown))
                g.FillEllipse(brownBg, x - r, y - r, r * 2, r * 2);

            using (Pen brownEdge = new Pen(Color.FromArgb(200, 110, 65, 30), 1.2f))
                g.DrawEllipse(brownEdge, x - r, y - r, r * 2, r * 2);

            DrawGoldStarFilled(g, x, y, r);

            using (Font f = new Font("Arial", 6.5f, FontStyle.Bold))
            using (Brush tb = new SolidBrush(Color.FromArgb(220, 80, 50, 20)))
            {
                var sz = g.MeasureString((idx + 1).ToString(), f);
                g.DrawString((idx + 1).ToString(), f, tb,
                    x - sz.Width / 2, y - sz.Height / 2 + 1);
            }
        }

        private void DrawGoldStarFilled(Graphics g, float cx, float cy, float r)
        {
            int pts = 5;
            float outerR = r * 0.92f;
            float innerR = r * 0.38f;
            PointF[] star = new PointF[pts * 2];
            for (int i = 0; i < pts * 2; i++)
            {
                float rad = (float)(-Math.PI / 2 + i * Math.PI / pts);
                float radius = (i % 2 == 0) ? outerR : innerR;
                star[i] = new PointF(cx + (float)Math.Cos(rad) * radius,
                                     cy + (float)Math.Sin(rad) * radius);
            }
            using (Brush sb = new SolidBrush(Color.FromArgb(250, 255, 215, 0)))
                g.FillPolygon(sb, star);
            using (Pen sp = new Pen(Color.FromArgb(200, 200, 150, 20), 0.8f))
                g.DrawPolygon(sp, star);
        }

        private void DrawFlatAirplane(Graphics g, float x, float y, float r,
            Color main, Color dark, Color light)
        {
            using (Brush bb = new SolidBrush(main))
                g.FillEllipse(bb, x - r, y - r, r * 2, r * 2);

            using (Pen bp = new Pen(dark, 2f))
                g.DrawEllipse(bp, x - r, y - r, r * 2, r * 2);

            float headH = r * 0.55f, headW = r * 0.7f;
            using (Brush hb = new SolidBrush(light))
                g.FillEllipse(hb, x - headW, y - r - headH * 0.7f, headW * 2, headH * 2);
            using (Pen hp = new Pen(dark, 1.2f))
                g.DrawEllipse(hp, x - headW, y - r - headH * 0.7f, headW * 2, headH * 2);

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

        // 鼠标交互
        private PointF PieceScreenPos(GameState st, int pl, int qi)
        {
            int pos = st.Players[pl].Pieces[qi];
            if (pos == FlightChessEngine.StartPosition)
                return _startMarkers[pl];
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
                if (!st.Players[pl].HasJoined) continue;
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
                if (!st.Players[pl].HasJoined) continue;
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

        // 棋子移动动画
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
                _kickedPlayer = -1;
                _kickedPiece = -1;
                boardPanel.Invalidate();
            }
        }

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
            return _baseSlots[player][0];
        }

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

        /// <summary>构建飞跃跳动画路径：骰子移动 + 白色直线飞跃。</summary>
        private System.Collections.Generic.List<PointF> BuildCombinedFlightAnimPath(
            GameState st, int player, int oldPos)
        {
            var path = new System.Collections.Generic.List<PointF>();

            for (int p = oldPos + 1; p <= 17; p++)
                path.Add(GetScreenPosForPosition(st, player, p));

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

        private void DetectKickedPieces(GameState oldState, GameState newState)
        {
            _kickedPlayer = -1;
            _kickedPiece = -1;
            for (int pl = 0; pl < 4; pl++)
            {
                if (!newState.Players[pl].HasJoined) continue;
                for (int qi = 0; qi < 4; qi++)
                {
                    int oldPos = oldState.Players[pl].Pieces[qi];
                    int newPos = newState.Players[pl].Pieces[qi];
                    if (oldPos >= 0 && oldPos < FlightChessEngine.BoardSize && newPos == -1)
                    {
                        _kickedPlayer = pl;
                        _kickedPiece = qi;
                        _kickedScreenPos = GetScreenPosForPosition(oldState, pl, oldPos);
                        SpawnExplosion(_kickedScreenPos.X, _kickedScreenPos.Y, PlyCol[pl]);
                        return;
                    }
                }
            }
        }

        /// <summary>检测棋子移动并触发步进动画。仅在确实有棋子移动时才创建新动画。</summary>
        private void DetectAndAnimate(GameState oldState, GameState newState)
        {
            if (oldState?.Players == null || newState?.Players == null) return;

            for (int pl = 0; pl < 4; pl++)
            {
                if (!newState.Players[pl].HasJoined) continue;
                for (int qi = 0; qi < 4; qi++)
                {
                    int oldPos = oldState.Players[pl].Pieces[qi];
                    int newPos = newState.Players[pl].Pieces[qi];
                    if (oldPos == newPos) continue;

                    if (_animTimer.Enabled)
                    {
                        _animTimer.Stop();
                        _animPlayer = -1; _animPiece = -1; _animPath = null;
                        _kickedPlayer = -1; _kickedPiece = -1;
                    }

                    if (newPos == FlightChessEngine.GoalPosition
                        && oldPos >= FlightChessEngine.FinishStart
                        && oldPos <= FlightChessEngine.FinishEnd)
                    {
                        var finishPath = new System.Collections.Generic.List<PointF>();
                        for (int p = oldPos + 1; p <= FlightChessEngine.FinishEnd; p++)
                            finishPath.Add(GetScreenPosForPosition(newState, pl, p));
                        _animPlayer = pl;
                        _animPiece = qi;
                        _animPath = finishPath;
                        if (_animPath.Count > 0)
                        {
                            _animPathIndex = 0;
                            _animTimer.Start();
                        }
                        return;
                    }

                    if (newPos == FlightChessEngine.GoalPosition
                        && oldPos >= 0 && oldPos < FlightChessEngine.BoardSize)
                    {
                        var finishPath = new System.Collections.Generic.List<PointF>();
                        for (int p = oldPos + 1; p <= FlightChessEngine.FinishEnd; p++)
                            finishPath.Add(GetScreenPosForPosition(newState, pl, p));
                        _animPlayer = pl;
                        _animPiece = qi;
                        _animPath = finishPath;
                        if (_animPath.Count > 0)
                        {
                            _animPathIndex = 0;
                            _animTimer.Start();
                        }
                        DetectKickedPieces(oldState, newState);
                        return;
                    }

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

        // 烟花特效
        private void StartFireworks()
        {
            _fireworksActive = true;
            _fireworkBurstsLeft = 8;
            _fireworkParticles.Clear();
            _fireworkTimer.Start();
            SpawnFireworkBurst();
        }

        private void SpawnFireworkBurst()
        {
            float cx = 260 + (float)_fireworkRng.NextDouble() * 200;
            float cy = 260 + (float)_fireworkRng.NextDouble() * 200;

            Color[] burstColors = new Color[] {
                Color.FromArgb(255, 215, 0),
                Color.FromArgb(255, 80, 80),
                Color.FromArgb(80, 220, 80),
                Color.FromArgb(255, 220, 50),
                Color.FromArgb(70, 140, 255),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(255, 150, 50),
                Color.FromArgb(255, 100, 200),
            };

            int particleCount = 40 + _fireworkRng.Next(30);
            for (int i = 0; i < particleCount; i++)
            {
                float angle = (float)(_fireworkRng.NextDouble() * Math.PI * 2);
                float speed = 1.5f + (float)_fireworkRng.NextDouble() * 4f;
                float life = 0.6f + (float)_fireworkRng.NextDouble() * 0.4f;
                float size = 2f + (float)_fireworkRng.NextDouble() * 3.5f;

                _fireworkParticles.Add(new FireworkParticle
                {
                    X = cx,
                    Y = cy,
                    Vx = (float)Math.Cos(angle) * speed,
                    Vy = (float)Math.Sin(angle) * speed,
                    Color = burstColors[_fireworkRng.Next(burstColors.Length)],
                    Life = life,
                    Size = size
                });
            }
        }

        private void SpawnExplosion(float x, float y, Color kickerColor)
        {
            _explosionActive = true;
            _explosionParticles.Clear();

            Color[] burstColors = new Color[] {
                kickerColor,
                Color.FromArgb(255, 100, 30),   // 橙红
                Color.FromArgb(255, 230, 40),   // 明黄
                Color.FromArgb(255, 255, 240),  // 炽白
                Color.FromArgb(255, 50, 10),    // 深红
                Color.FromArgb(255, 180, 60),   // 橙黄
                Color.FromArgb(255, 200, 20),   // 橙黄亮
            };

            int particleCount = 50 + _fireworkRng.Next(30);
            for (int i = 0; i < particleCount; i++)
            {
                float angle = (float)(_fireworkRng.NextDouble() * Math.PI * 2);
                float speed = 1.5f + (float)_fireworkRng.NextDouble() * 6f;
                float life = 0.5f + (float)_fireworkRng.NextDouble() * 0.6f;
                float size = 2.5f + (float)_fireworkRng.NextDouble() * 6f;

                _explosionParticles.Add(new FireworkParticle
                {
                    X = x, Y = y,
                    Vx = (float)Math.Cos(angle) * speed,
                    Vy = (float)Math.Sin(angle) * speed,
                    Color = burstColors[_fireworkRng.Next(burstColors.Length)],
                    Life = life,
                    Size = size
                });
            }

            int flashCount = 8 + _fireworkRng.Next(5);
            for (int i = 0; i < flashCount; i++)
            {
                float angle = (float)(i * Math.PI * 2 / flashCount);
                _explosionParticles.Add(new FireworkParticle
                {
                    X = x, Y = y,
                    Vx = (float)Math.Cos(angle) * 0.3f,
                    Vy = (float)Math.Sin(angle) * 0.3f,
                    Color = Color.FromArgb(255, 255, 240),
                    Life = 0.4f,
                    Size = 8f + (float)_fireworkRng.NextDouble() * 4f
                });
            }

            if (!_fireworkTimer.Enabled)
                _fireworkTimer.Start();
        }

        private void FireworkTimer_Tick(object sender, EventArgs e)
        {
            bool anyAlive = false;

            foreach (var p in _fireworkParticles)
            {
                if (p.Life <= 0) continue;
                p.Life -= 0.025f;
                p.X += p.Vx;
                p.Y += p.Vy;
                p.Vy += 0.08f;
                p.Vx *= 0.99f;
                p.Size *= 0.995f;
                if (p.Life > 0) anyAlive = true;
            }

            int aliveCount = 0;
            foreach (var fp in _fireworkParticles) { if (fp.Life > 0) aliveCount++; }
            if (_fireworkBurstsLeft > 0 && aliveCount < 20)
            {
                SpawnFireworkBurst();
                _fireworkBurstsLeft--;
            }

            bool anyExplosionAlive = false;
            foreach (var ep in _explosionParticles)
            {
                if (ep.Life <= 0) continue;
                ep.Life -= 0.020f;
                ep.X += ep.Vx;
                ep.Y += ep.Vy;
                ep.Vy += 0.04f;
                ep.Vx *= 0.975f;
                ep.Size *= 0.993f;
                if (ep.Life > 0) anyExplosionAlive = true;
            }
            if (!anyExplosionAlive && _explosionActive)
            {
                _explosionActive = false;
                _explosionParticles.Clear();
            }

            if (!anyAlive && _fireworkBurstsLeft <= 0 && !_explosionActive)
            {
                _fireworkTimer.Stop();
                _fireworksActive = false;
                _fireworkParticles.Clear();
            }

            boardPanel.Invalidate();
        }

        private void DrawFireworks(Graphics g)
        {
            if (!_fireworksActive) return;

            foreach (var p in _fireworkParticles)
            {
                if (p.Life <= 0) continue;

                int alpha = (int)(255 * p.Life);
                if (alpha > 255) alpha = 255;
                if (alpha < 0) alpha = 0;

                Color c = Color.FromArgb(alpha, p.Color);
                float s = p.Size;
                if (s < 0.5f) s = 0.5f;

                using (Brush glow = new SolidBrush(Color.FromArgb(alpha / 3, c)))
                    g.FillEllipse(glow, p.X - s * 2, p.Y - s * 2, s * 4, s * 4);
                using (Brush core = new SolidBrush(c))
                    g.FillEllipse(core, p.X - s, p.Y - s, s * 2, s * 2);
            }
        }

        private void DrawExplosion(Graphics g)
        {
            if (!_explosionActive) return;

            foreach (var p in _explosionParticles)
            {
                if (p.Life <= 0) continue;

                int alpha = (int)(255 * p.Life);
                if (alpha > 255) alpha = 255;
                if (alpha < 0) alpha = 0;

                Color c = Color.FromArgb(alpha, p.Color);
                float s = p.Size;
                if (s < 0.3f) s = 0.3f;

                using (Brush glow = new SolidBrush(Color.FromArgb(alpha / 4, c)))
                    g.FillEllipse(glow, p.X - s * 3, p.Y - s * 3, s * 6, s * 6);
                using (Brush mid = new SolidBrush(Color.FromArgb(alpha / 2, c)))
                    g.FillEllipse(mid, p.X - s * 1.5f, p.Y - s * 1.5f, s * 3, s * 3);
                using (Brush core = new SolidBrush(c))
                    g.FillEllipse(core, p.X - s * 0.6f, p.Y - s * 0.6f, s * 1.2f, s * 1.2f);
            }
        }

        // 颁奖台绘制
        private void DrawPodium(Graphics g)
        {
            GameState st;
            lock (_stateLock) { if (_currentGameState == null) return; st = _currentGameState.DeepCopy(); }
            if (!st.GameOver) return;

            using (Brush overlay = new SolidBrush(Color.FromArgb(195, 10, 12, 30)))
                g.FillRectangle(overlay, 0, 0, BdW, BdH);

            var ranked = new List<(int playerIdx, int rank)>();
            for (int p = 0; p < 4; p++)
                if (st.Players[p].Rank > 0 && st.Players[p].Rank <= 3)
                    ranked.Add((p, st.Players[p].Rank));
            ranked.Sort((a, b) => a.rank.CompareTo(b.rank));

            if (ranked.Count == 0) return;

            float boardCx = BdW / 2f;
            float podiumBaseY = BdH / 2f + 170;
            float stepW = 170f;
            float gap = 45f;
            float stepH1 = 170f, stepH2 = 140f, stepH3 = 115f;

            float titleAreaY = BdH / 2f - 205;

            DrawTrophyIcon(g, boardCx, titleAreaY + 28, 30f);

            using (Font titleFont = new Font("微软雅黑", 26f, FontStyle.Bold))
            using (Brush titleBrush = new SolidBrush(Color.FromArgb(250, 255, 225, 80)))
            {
                string title = "游 戏 结 束";
                var sz = g.MeasureString(title, titleFont);
                g.DrawString(title, titleFont, titleBrush,
                    boardCx - sz.Width / 2, titleAreaY + 72);
            }

            float sepY = titleAreaY + 130;
            using (Pen sepMain = new Pen(Color.FromArgb(140, 255, 215, 0), 2f))
                g.DrawLine(sepMain, boardCx - 180, sepY, boardCx + 180, sepY);
            using (Pen sepThin = new Pen(Color.FromArgb(80, 255, 215, 0), 0.5f))
            {
                g.DrawLine(sepThin, boardCx - 200, sepY - 6, boardCx + 200, sepY - 6);
                g.DrawLine(sepThin, boardCx - 200, sepY + 6, boardCx + 200, sepY + 6);
            }

            float x1 = boardCx - stepW / 2;
            float x2 = boardCx - stepW * 1.5f - gap;
            float x3 = boardCx + stepW * 0.5f + gap;

            for (int i = 0; i < ranked.Count && i < 3; i++)
            {
                var (playerIdx, rank) = ranked[i];
                float px, ph;
                switch (rank)
                {
                    case 1: px = x1; ph = stepH1; break;
                    case 2: px = x2; ph = stepH2; break;
                    default: px = x3; ph = stepH3; break;
                }
                float py = podiumBaseY - ph;
                float cx = px + stepW / 2;

                using (Brush shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                    g.FillRectangle(shadow, px + 4, py + 4, stepW, ph);

                Color topColor, bottomColor, borderColor;
                switch (rank)
                {
                    case 1:
                        topColor = Color.FromArgb(235, 255, 220, 40);
                        bottomColor = Color.FromArgb(235, 210, 160, 15);
                        borderColor = Color.FromArgb(220, 200, 150, 20);
                        break;
                    case 2:
                        topColor = Color.FromArgb(220, 215, 218, 225);
                        bottomColor = Color.FromArgb(220, 170, 172, 180);
                        borderColor = Color.FromArgb(200, 140, 142, 150);
                        break;
                    default:
                        topColor = Color.FromArgb(220, 215, 150, 70);
                        bottomColor = Color.FromArgb(220, 170, 110, 40);
                        borderColor = Color.FromArgb(200, 140, 90, 30);
                        break;
                }

                using (Brush topBrush = new SolidBrush(topColor))
                    g.FillRectangle(topBrush, px, py, stepW, ph * 0.45f);
                using (Brush botBrush = new SolidBrush(bottomColor))
                    g.FillRectangle(botBrush, px, py + ph * 0.45f, stepW, ph * 0.55f);
                using (Brush blend = new SolidBrush(Color.FromArgb(40, topColor)))
                    g.FillRectangle(blend, px, py, stepW, ph);

                using (Pen topEdge = new Pen(Color.FromArgb(220, 255, 255, 255), 2.5f))
                    g.DrawLine(topEdge, px + 2, py, px + stepW - 2, py);
                using (Pen borderPen = new Pen(borderColor, 1.8f))
                    g.DrawRectangle(borderPen, px, py, stepW, ph);

                using (Pen sideLine = new Pen(Color.FromArgb(50, 255, 255, 255), 1f))
                {
                    g.DrawLine(sideLine, px + 6, py + 1, px + 6, py + ph);
                    g.DrawLine(sideLine, px + stepW - 6, py + 1, px + stepW - 6, py + ph);
                }
            }

            for (int i = 0; i < ranked.Count && i < 3; i++)
            {
                var (playerIdx, rank) = ranked[i];
                float px, ph;
                switch (rank)
                {
                    case 1: px = x1; ph = stepH1; break;
                    case 2: px = x2; ph = stepH2; break;
                    default: px = x3; ph = stepH3; break;
                }
                float py = podiumBaseY - ph;
                float cx = px + stepW / 2;

                float medalR = 18f;
                float medalY = py - medalR - 8;
                Color medalColor;
                switch (rank)
                {
                    case 1: medalColor = Color.FromArgb(255, 215, 0); break;   // 金
                    case 2: medalColor = Color.FromArgb(195, 195, 205); break;  // 银
                    default: medalColor = Color.FromArgb(210, 145, 60); break;   // 铜
                }

                using (Brush glow = new SolidBrush(Color.FromArgb(45, medalColor)))
                    g.FillEllipse(glow, cx - medalR - 6, medalY - medalR - 6,
                        (medalR + 6) * 2 + 2, (medalR + 6) * 2 + 2);
                using (Brush medBg = new SolidBrush(medalColor))
                    g.FillEllipse(medBg, cx - medalR, medalY - medalR, medalR * 2, medalR * 2);
                using (Pen medInner = new Pen(Color.FromArgb(160, 255, 255, 255), 2f))
                    g.DrawEllipse(medInner, cx - medalR + 3, medalY - medalR + 3,
                        (medalR - 3) * 2, (medalR - 3) * 2);
                using (Pen medEdge = new Pen(Color.FromArgb(200, 255, 255, 255), 1.8f))
                    g.DrawEllipse(medEdge, cx - medalR, medalY - medalR, medalR * 2, medalR * 2);
                using (Font medFont = new Font("Arial", 13f, FontStyle.Bold))
                using (Brush medText = new SolidBrush(Color.White))
                {
                    string rankNum = rank.ToString();
                    var msz = g.MeasureString(rankNum, medFont);
                    g.DrawString(rankNum, medFont, medText,
                        cx - msz.Width / 2, medalY - msz.Height / 2 + 1);
                }

                float iconR = 20f;
                float iconY = py + 42;
                using (Brush iconShadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                    g.FillEllipse(iconShadow, cx - iconR + 1, iconY - iconR + 1, iconR * 2, iconR * 2);
                using (Brush iconBg = new SolidBrush(PlyCol[playerIdx]))
                    g.FillEllipse(iconBg, cx - iconR, iconY - iconR, iconR * 2, iconR * 2);
                using (Pen iconEdge = new Pen(Color.White, 2.5f))
                    g.DrawEllipse(iconEdge, cx - iconR, iconY - iconR, iconR * 2, iconR * 2);

                string playerName = st.Players[playerIdx].Name;
                float nameY = iconY + iconR + 10;
                using (Font nameFont = new Font("微软雅黑", 12f, FontStyle.Bold))
                using (Brush nameBrush = new SolidBrush(Color.FromArgb(245, 240, 240, 240)))
                {
                    var nsz = g.MeasureString(playerName, nameFont);
                    Font actualFont = nameFont;
                    if (nsz.Width > stepW - 16)
                    {
                        actualFont = new Font("微软雅黑", 10f, FontStyle.Bold);
                        nsz = g.MeasureString(playerName, actualFont);
                    }
                    g.DrawString(playerName, actualFont, nameBrush,
                        cx - nsz.Width / 2, nameY);
                    if (actualFont != nameFont) actualFont.Dispose();
                }

                string rankLabel;
                Color rankLabelColor;
                switch (rank)
                {
                    case 1: rankLabel = "★ 第 一 名"; rankLabelColor = Color.FromArgb(250, 255, 225, 80); break;
                    case 2: rankLabel = "★ 第 二 名"; rankLabelColor = Color.FromArgb(230, 210, 215, 225); break;
                    default: rankLabel = "★ 第 三 名"; rankLabelColor = Color.FromArgb(240, 215, 165, 100); break;
                }
                float rankY = py + ph - 30;
                using (Brush rankBg = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                    g.FillRectangle(rankBg, px + 8, rankY - 2, stepW - 16, 24);
                using (Font rankFont = new Font("微软雅黑", 13f, FontStyle.Bold))
                using (Brush rankBrush = new SolidBrush(rankLabelColor))
                {
                    var rsz = g.MeasureString(rankLabel, rankFont);
                    g.DrawString(rankLabel, rankFont, rankBrush,
                        cx - rsz.Width / 2, rankY + 2);
                }
            }

            float hintY = podiumBaseY + 30;
            using (Font hintFont = new Font("微软雅黑", 10f, FontStyle.Regular))
            using (Brush hintBrush = new SolidBrush(Color.FromArgb(190, 185, 185, 195)))
            {
                string hint = "— 点击「新游戏」按钮重新开始 —";
                var hsz = g.MeasureString(hint, hintFont);
                g.DrawString(hint, hintFont, hintBrush, boardCx - hsz.Width / 2, hintY);
            }
        }

        private void DrawTrophyIcon(Graphics g, float cx, float cy, float r)
        {
            PointF[] cup = {
                new PointF(cx - r * 0.45f, cy - r),
                new PointF(cx + r * 0.45f, cy - r),
                new PointF(cx + r * 0.75f, cy + r * 0.35f),
                new PointF(cx - r * 0.75f, cy + r * 0.35f)
            };
            using (Brush cupFill = new SolidBrush(Color.FromArgb(250, 255, 225, 50)))
                g.FillPolygon(cupFill, cup);
            using (Brush cupHighlight = new SolidBrush(Color.FromArgb(120, 255, 245, 150)))
                g.FillPolygon(cupHighlight, new PointF[] {
                    new PointF(cx - r * 0.38f, cy - r),
                    new PointF(cx + r * 0.38f, cy - r),
                    new PointF(cx + r * 0.65f, cy + r * 0.05f),
                    new PointF(cx - r * 0.65f, cy + r * 0.05f)
                });
            using (Pen cupEdge = new Pen(Color.FromArgb(220, 200, 160, 20), 2.2f))
                g.DrawPolygon(cupEdge, cup);

            using (Pen earPen = new Pen(Color.FromArgb(245, 255, 220, 30), 3.5f))
            {
                g.DrawArc(earPen, cx - r * 0.95f, cy - r * 0.55f, r * 0.65f, r * 1.05f, 270, 120);
                g.DrawArc(earPen, cx + r * 0.30f, cy - r * 0.55f, r * 0.65f, r * 1.05f, 150, 120);
            }

            using (Brush baseFill = new SolidBrush(Color.FromArgb(235, 210, 160, 25)))
                g.FillRectangle(baseFill, cx - r * 0.72f, cy + r * 0.35f, r * 1.44f, r * 0.28f);
            using (Pen baseEdge = new Pen(Color.FromArgb(200, 190, 140, 15), 1.5f))
                g.DrawRectangle(baseEdge, cx - r * 0.72f, cy + r * 0.35f, r * 1.44f, r * 0.28f);

            using (Pen topBar = new Pen(Color.FromArgb(230, 255, 225, 60), 2.5f))
                g.DrawLine(topBar, cx - r * 0.55f, cy - r, cx + r * 0.55f, cy - r);
        }

        // 网络通信
        private void ConnectToServer(string addr, int port)
        {
            try
            {
                _serverAddress = addr;
                _serverPort = port;
                _lastServerMsgTime = DateTime.UtcNow;
                _tcpClient = new TcpClient(); _tcpClient.Connect(addr, port);
                _stream = _tcpClient.GetStream(); _reader = new StreamReader(_stream, Encoding.UTF8);
                _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };
                _isConnected = true;
                Log("已连接 {0}:{1}", addr, port);
                SendMessage(new JoinGameMessage { PlayerName = _myPlayerName });
                _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
                _receiveThread.Start();
                _heartbeatCheckTimer.Start();
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
            catch (ObjectDisposedException) { HandleDisconnect(); }
            catch (Exception ex) { Log("发送失败: {0}", ex.Message); HandleDisconnect(); }
        }

        private void ReceiveLoop()
        {
            try
            {
                while (_isConnected && !_isReconnecting)
                {
                    string l = _reader.ReadLine();
                    if (l == null) break;
                    if (string.IsNullOrWhiteSpace(l)) continue;
                    _lastServerMsgTime = DateTime.UtcNow;
                    ProcessMsg(l);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { LogInvoke("接收错误: {0}", ex.Message); }
            finally
            {
                if (!_isReconnecting)
                    HandleDisconnect();
            }
        }

        private void ProcessMsg(string json)
        {
            try
            {
                var o = JObject.Parse(json);
                var t = o["Type"]?.Value<string>();

                if (t == MessageType.Pong)
                    return;

                if (t == MessageType.Ping)
                {
                    SendMessage(new PongMessage());
                    return;
                }

                // 用 BeginInvoke 避免阻塞接收线程
                this.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        switch (t)
                        {
                            case MessageType.GameStateUpdate: HandleGS(json); break;
                            case MessageType.JoinGameResponse: HandleJR(json); break;
                            case MessageType.Error: HandleErr(json); break;
                            case MessageType.PlayerLeft: HandlePL(json); break;
                            case MessageType.Chat: HandleChat(json); break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("处理消息异常: {0}", ex.Message);
                    }
                }));
            }
            catch (JsonException) { }
            catch (InvalidOperationException)
            {
                System.Diagnostics.Debug.Fail("ProcessMsg: BeginInvoke失败, Handle未创建");
            }
            catch (Exception ex)
            {
                LogInvoke("消息解析异常: {0}", ex.Message);
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
            this.Text = string.Format("飞行棋 - {0}({1}方)", _myPlayerName, FlightChessEngine.PlayerColorNames[_myPlayerId]);
            Log("加入成功！你是 {0} 方", FlightChessEngine.PlayerColorNames[_myPlayerId]);
        }

        private void HandleErr(string json)
        {
            var m = JsonConvert.DeserializeObject<ErrorMessage>(json);
            Log("[服务器] {0}", m.Message);
        }

        private void HandlePL(string json)
        {
            var m = JsonConvert.DeserializeObject<PlayerLeftMessage>(json);
            string msg = string.Format("【{0}】掉线了（可随时重连）。", m.PlayerName);
            Log(msg);
        }

        private void HandleChat(string json)
        {
            var m = JsonConvert.DeserializeObject<ChatMessage>(json);
            if (m == null || string.IsNullOrWhiteSpace(m.Content)) return;

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string line = string.Format("[{0}] {1}: {2}", timestamp, m.SenderName, m.Content);

            // 在聊天区域追加文本
            if (rtbChat.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => AppendChatLine(line)));
            }
            else
            {
                AppendChatLine(line);
            }
        }

        private void AppendChatLine(string line)
        {
            if (rtbChat.IsDisposed) return;

            if (rtbChat.Lines.Length > 200)
            {
                rtbChat.Select(0, rtbChat.GetFirstCharIndexFromLine(rtbChat.Lines.Length - 200));
                rtbChat.SelectedText = "";
            }

            rtbChat.AppendText(line + Environment.NewLine);
            rtbChat.SelectionStart = rtbChat.TextLength;
            rtbChat.ScrollToCaret();
        }

        private void SendChatMessage(string content)
        {
            if (!_isConnected)
            {
                Log("未连接到服务器，无法发送聊天。");
                return;
            }
            if (string.IsNullOrWhiteSpace(content)) return;

            SendMessage(new ChatMessage
            {
                SenderName = _myPlayerName,
                Content = content
            });
        }

        private void InitChatPanel()
        {
            string[] phrases = new string[]
            {
                "加油！💪",
                "好厉害！👏",
                "被踩了，哭😭",
                "起飞啦✈️",
                "胜利在望🏆",
                "运气真好🍀",
                "再来一局🎲",
                "哈哈😄",
                "不好意思😅",
                "厉害厉害🔥",
            };

            foreach (string phrase in phrases)
            {
                Button btn = new Button
                {
                    Text = phrase,
                    Size = new System.Drawing.Size(180, 30),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = System.Drawing.Color.FromArgb(235, 230, 215),
                    Font = new System.Drawing.Font("微软雅黑", 9F),
                    Cursor = System.Windows.Forms.Cursors.Hand,
                    Margin = new Padding(2),
                    TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                };
                btn.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(180, 170, 150);
                btn.Click += (sender, e) =>
                {
                    SendChatMessage(phrase);
                };
                btn.MouseEnter += (sender, e) =>
                {
                    btn.BackColor = System.Drawing.Color.FromArgb(220, 240, 255);
                };
                btn.MouseLeave += (sender, e) =>
                {
                    btn.BackColor = System.Drawing.Color.FromArgb(235, 230, 215);
                };

                flpChatButtons.Controls.Add(btn);
            }
        }

        /// <summary>心跳检测：18 秒无消息则认为断线并启动重连。</summary>
        private void HeartbeatCheckTimer_Tick(object sender, EventArgs e)
        {
            if (!_isConnected || _isReconnecting) return;

            double elapsed = (DateTime.UtcNow - _lastServerMsgTime).TotalSeconds;
            if (elapsed > 18.0)
            {
                Log("服务器响应超时（{0:F0}秒无消息），尝试重连...", elapsed);
                HandleDisconnect();
            }
        }

        private void HandleDisconnect()
        {
            _isConnected = false;
            _heartbeatCheckTimer.Stop();
            try { _stream?.Close(); } catch { }
            try { _tcpClient?.Close(); } catch { }

            if (_myPlayerId >= 0 && !_isReconnecting)
            {
                Log("连接断开，开始后台重连...");
                StartReconnection();
            }
            else
            {
                this.BeginInvoke(new Action(() =>
                {
                    if (_myPlayerId < 0)
                    {
                        Log("无法加入游戏。");
                        lblCurrentPlayer.Text = "连接失败";
                    }
                    else
                    {
                        Log("连接已断开。");
                        lblCurrentPlayer.Text = "连接已断开";
                    }
                    btnRollDice.Enabled = false;
                    boardPanel.Invalidate();
                }));
            }
        }

        // 断线重连

        private void ApplyReconnectResult(TcpClient newClient, NetworkStream newStream,
            StreamReader newReader, StreamWriter newWriter)
        {
            var oldClient = _tcpClient;
            var oldStream = _stream;
            var oldReader = _reader;
            var oldWriter = _writer;

            _tcpClient = newClient;
            _stream = newStream;
            _reader = newReader;
            _writer = newWriter;
            _isConnected = true;
            _isReconnecting = false;
            _lastServerMsgTime = DateTime.UtcNow;

            try { oldWriter?.Dispose(); } catch { }
            try { oldReader?.Dispose(); } catch { }
            try { oldStream?.Close(); } catch { }
            try { oldClient?.Close(); } catch { }

            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
            _receiveThread.Start();
        }

        private void StartReconnection()
        {
            _isReconnecting = true;
            _reconnectAttempts = 0;
            _reconnectThread = new Thread(ReconnectionLoop) { IsBackground = true };
            _reconnectThread.Start();
        }

        private void ReconnectionLoop()
        {
            while (_reconnectAttempts < MAX_RECONNECT_ATTEMPTS && _isReconnecting)
            {
                Thread.Sleep(2000);
                if (!_isReconnecting) return;
                _reconnectAttempts++;

                TcpClient newClient = null;
                StreamReader newReader = null;
                StreamWriter newWriter = null;
                NetworkStream newStream = null;

                try
                {
                    LogInvoke("重连尝试 {0}/{1}...", _reconnectAttempts, MAX_RECONNECT_ATTEMPTS);

                    newClient = new TcpClient();
                    newClient.Connect(_serverAddress, _serverPort);
                    newClient.ReceiveTimeout = 5000;
                    newStream = newClient.GetStream();
                    newReader = new StreamReader(newStream, Encoding.UTF8);
                    newWriter = new StreamWriter(newStream, Encoding.UTF8) { AutoFlush = true };

                    string reconnectJson = JsonConvert.SerializeObject(new ReconnectMessage
                    {
                        PlayerId = _myPlayerId,
                        PlayerName = _myPlayerName
                    });
                    newWriter.WriteLine(reconnectJson);

                    bool reconnected = false;
                    bool shouldFallback = false;
                    DateTime readStart = DateTime.UtcNow;

                    while ((DateTime.UtcNow - readStart).TotalSeconds < 5.0)
                    {
                        string response = newReader.ReadLine();
                        if (response == null || string.IsNullOrWhiteSpace(response))
                            continue;

                        JObject obj = JObject.Parse(response);
                        string msgType = obj["Type"]?.Value<string>();

                        if (msgType == MessageType.JoinGameResponse)
                        {
                            ApplyReconnectResult(newClient, newStream, newReader, newWriter);
                            this.BeginInvoke(new Action(() =>
                            {
                                _heartbeatCheckTimer.Start();
                                Log("重连成功！恢复游戏状态...");
                            }));
                            reconnected = true;
                            break;
                        }
                        else if (msgType == MessageType.Error)
                        {
                            LogInvoke("重连被拒（{0}），尝试以新身份加入...",
                                obj["Message"]?.Value<string>() ?? "未知原因");
                            shouldFallback = true;
                            break;
                        }
                        else if (msgType == MessageType.Ping)
                        {
                            newWriter.WriteLine(JsonConvert.SerializeObject(new PongMessage()));
                        }
                    }

                    if (reconnected)
                        return;

                    if (shouldFallback)
                    {
                        newWriter.WriteLine(JsonConvert.SerializeObject(
                            new JoinGameMessage { PlayerName = _myPlayerName }));

                        readStart = DateTime.UtcNow;
                        while ((DateTime.UtcNow - readStart).TotalSeconds < 5.0)
                        {
                            string response = newReader.ReadLine();
                            if (response == null || string.IsNullOrWhiteSpace(response))
                                continue;

                            JObject obj = JObject.Parse(response);
                            string msgType = obj["Type"]?.Value<string>();

                            if (msgType == MessageType.JoinGameResponse)
                            {
                                ApplyReconnectResult(newClient, newStream, newReader, newWriter);
                                this.BeginInvoke(new Action(() =>
                                {
                                    _heartbeatCheckTimer.Start();
                                    Log("以新身份重新加入游戏！");
                                }));
                                return;
                            }
                            else if (msgType == MessageType.Error)
                            {
                                LogInvoke("加入失败: {0}",
                                    obj["Message"]?.Value<string>() ?? "未知错误");
                                break;
                            }
                            else if (msgType == MessageType.Ping)
                            {
                                newWriter.WriteLine(JsonConvert.SerializeObject(new PongMessage()));
                            }
                        }
                    }

                    try { newWriter?.Dispose(); } catch { }
                    try { newReader?.Dispose(); } catch { }
                    try { newStream?.Close(); } catch { }
                    try { newClient?.Close(); } catch { }
                }
                catch (ObjectDisposedException)
                {
                    _isReconnecting = false;
                    return;
                }
                catch (Exception ex)
                {
                    try { newWriter?.Dispose(); } catch { }
                    try { newReader?.Dispose(); } catch { }
                    try { newStream?.Close(); } catch { }
                    try { newClient?.Close(); } catch { }

                    LogInvoke("重连失败: {0}", ex.Message);
                }
            }

            _isReconnecting = false;
            this.BeginInvoke(new Action(() =>
            {
                Log("重连失败（已超30秒），请重新打开客户端以新身份加入（如还有空位）。");
                btnRollDice.Enabled = false;
                lblCurrentPlayer.Text = "重连失败";
                boardPanel.Invalidate();
            }));
        }

        // UI 更新
        private void UpdateUI(GameState st)
        {
            if (st.GameOver)
            {
                string firstPlace = "";
                for (int i = 0; i < 4; i++)
                    if (st.Players[i].Rank == 1) { firstPlace = FlightChessEngine.PlayerColorNames[i]; break; }
                lblCurrentPlayer.Text = string.Format("游戏结束！{0}方冠军", firstPlace);
                lblCurrentPlayer.ForeColor = Color.FromArgb(255, 215, 0);
                btnRollDice.Enabled = false;
                btnRollDice.Text = "游戏结束";

                if (!_victoryShown)
                {
                    _victoryShown = true;
                    StartFireworks();
                }
            }
            else
            {
                _victoryShown = false;
                _fireworkTimer.Stop();
                _fireworksActive = false;
                _fireworkParticles.Clear();
                _explosionActive = false;
                _explosionParticles.Clear();

                var cp = st.Players[st.CurrentPlayerIndex];
                string extra = cp.Rank > 0
                    ? string.Format(" (第{0}名已归营)", cp.Rank)
                    : "";
                string aiTag = (!cp.IsConnected && cp.HasJoined)
                    ? "[AI托管] " : "";
                lblCurrentPlayer.Text = string.Format("当前: {0}{1}({2}方){3}",
                    aiTag, cp.Name, FlightChessEngine.PlayerColorNames[st.CurrentPlayerIndex], extra);
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
            }
            else if (!st.GameOver)
            {
                btnRollDice.Enabled = false;
                btnRollDice.Text = "等待他人...";
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

        private void MainForm_FormClosing(object s, FormClosingEventArgs e)
        {
            _isReconnecting = false;
            _isConnected = false;
            _heartbeatCheckTimer?.Stop();
            try { _stream?.Close(); } catch { }
            try { _tcpClient?.Close(); } catch { }
        }

        // 日志
        public void Log(string fmt, params object[] args)
        {
            string m = string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, string.Format(fmt, args));
            if (lstLog.InvokeRequired)
                this.BeginInvoke(new Action(() =>
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
                this.BeginInvoke(new Action(() =>
                {
                    lstLog.Items.Add(m);
                    lstLog.TopIndex = lstLog.Items.Count - 1;
                }));
            }
            catch (InvalidOperationException) { }
        }
    }
}
