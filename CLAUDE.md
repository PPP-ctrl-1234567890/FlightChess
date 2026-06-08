# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```powershell
# Build all three projects (Debug)
dotnet build FlightChess.sln

# Build individual projects
dotnet build FlightChess.Common\FlightChess.Common.csproj
dotnet build FlightChess.Server\FlightChess.Server.csproj
dotnet build FlightChess.Client\FlightChess.Client.csproj

# Publish standalone client (optional, framework-dependent)
dotnet publish FlightChess.Client\FlightChess.Client.csproj -c Release
```

There is no test project. Verification is done by building (0 errors, 0 warnings expected) and visually inspecting the board.

## Project Architecture

Three-project solution targeting .NET Framework 4.7.2 on Windows Forms:

```
FlightChess.sln
├── FlightChess.Common    (class library, references Newtonsoft.Json 13.0.3)
├── FlightChess.Server    (console app → .exe, references Common)
└── FlightChess.Client    (WinForms app → WinExe, references Common)
```

**`FlightChess.Common`** — Shared game logic and network protocol:
- `FlightChessEngine.cs` — Core game engine: dice rolling, move validation, piece kicking, win detection. Position model: `-1`=base, `-2`=START marker, `0-51`=main path, `52-57`=return path, `58`=goal. Safe cells: `{0, 13, 26, 39, 50}`. Return path has bounce-back mechanic (overshoot past 57 bounces back).
- `GameState.cs` — Full game state (4 players, current turn, dice value, winner). Player start offsets: `{0, 39, 26, 13}` (Red, Green, Yellow, Blue — counter-clockwise order).
- `Player.cs` — Player data: ID, name, 4 piece positions, connection status.
- `NetworkMessages.cs` — JSON message types: `JoinGame`, `RollDice`, `MovePiece`, `GameStateUpdate`, `Error`, `PlayerLeft`, `JoinGameResponse`. Each has a C# class with `[Serializable]` attribute.

**`FlightChess.Server`** — TCP game server (console app, default port 8888):
- `Program.cs` — Entry point. `Ctrl+C` to stop, type `status`/`exit`/`quit` in console.
- `GameServer.cs` — TCP listener, accepts up to 4 clients. Routes messages by `Type` field. Maintains authoritative `GameState`. Thread-safe with locks on clients list, game state, and log list. Auto-advances turn when no valid moves exist.
- `ClientConnection.cs` — One per connected client. Reads JSON lines on a background thread. Write via `SendMessage()` which serializes to JSON.

**`FlightChess.Client`** — WinForms UI (manual drawing, no designer for MainForm):
- `Program.cs` — STA thread, shows `ConnectForm` then `MainForm`.
- `ConnectForm.cs` — Connection dialog (server IP, port, player name).
- `MainForm.cs` — **The largest file (~1200 lines).** All board geometry, rendering, and network client logic in one file. No designer file for layout (it's all in `InitializeComponent()`). Double-buffered Panel for rendering.
- `MainForm.Designer.cs` — Standard WinForms partial class (minimal).

## Board Geometry (MainForm.cs critical constants)

The board is a traditional cross-shaped flight chess board with a 52-cell outer ring:

```
Constants (line ~44-51):
  BdW=700, BdH=700           — board panel size
  Inset=40                   — path inset from board edge
  CenterX=310, CenterY=310   — center square top-left
  CenterW=100, CenterH=100   — center square (half of original 200×200)
  BaseSize=160               — corner base size

Derived:
  Outer edges: L=100, R=620, T=100, B=620
  Arm inner edges: 260 / 460
  Center: (310,310)-(410,410), center point (360,360)
  Arm width: 160px (460-260 or 620-460)
  Gap between arms and center: 50px
```

**Outer ring**: 12 segments, counter-clockwise, total 2080px, step=40px/cell:
- 4 edge segments × 200px (5 cells each) — along the outer rectangle
- 8 arm segments × 160px (4 cells each) — connecting edges to center arms

Player start cells (counter-clockwise): Red=0 (bottom-right), Blue=13 (bottom-left), Yellow=26 (top-left), Green=39 (top-right).

**Return paths**: 6 cells per player (positions 52-57), along the center line of each arm (Y=360 or X=360), spaced 40px, shifted 40px inward from outer edge to avoid main-path overlap. The 6th cell sits inside the center triangle.

**Cell rendering**: Main path cells are colored rectangles (cellHalfShort=12, cellHalfLong=20) with white circles (r=9). Return path cells are 32px squares (cellHalf=16) with white circles (r=8).

## Communication Protocol

JSON messages sent as single lines (`\n` delimited) over TCP. Client ↔ Server message flow:

1. Client connects → Server assigns a free player slot (0-3)
2. Client sends `JoinGame` → Server responds with `JoinGameResponse` (player ID), then broadcasts `GameStateUpdate` to all
3. On their turn, client sends `RollDice` → Server rolls, broadcasts `GameStateUpdate`
4. Client sends `MovePiece { PieceIndex }` → Server executes move, broadcasts `GameStateUpdate`
5. If a player disconnects, server broadcasts `PlayerLeft` and `GameStateUpdate`

The server is authoritative — all game logic runs server-side. Clients only render state and send intent.

## Key Rules (in FlightChessEngine)

- Roll 6 to bring a piece out of base (position -1 → -2 START)
- From START, any dice value advances into main path (position 0-51)
- 6 grants an extra turn
- Landing on an opponent's piece (non-safe cell, main path only) sends it back to base
- Safe cells: absolute indices {0, 13, 26, 39, 50}
- Flight jumps: {12→24, 25→37, 38→50, 51→11}
- Return path (52-57): must reach exactly 57 to goal; overshoot bounces back
- Player offsets: `{0, 39, 26, 13}` — must match the counter-clockwise path direction

## Common Pitfalls

- **StartCells, offsets, and segment order must agree on direction.** Currently counter-clockwise. Changing direction requires updating all three.
- **MainForm has two constructors** — the parameterless one (for designer) and the real one `MainForm(server, port, name)`. The parameterless one skips network connection.
- **Board geometry is entirely hand-calculated** in `InitBoardGeometry()`. Any change to constants (CenterX, CenterW, etc.) cascades to segment endpoints, return paths, and cell placement. Run a build after any change to catch errors early.
- **No auto-reconnect** — if the server disconnects, the client must be restarted.
- **Newtonsoft.Json** is used, not System.Text.Json. Messages use the `Type` discriminator field for routing.
