# IT-Kamianets 3D Engine: Unity Network

`3d-unity-network` provides real-time multiplayer for the **IT-Kamianets 3D Engine** in Unity.

It covers server-authoritative game state, player sync, lag compensation, and lobbies.

### Logic

The server decides what happened; clients send input and show the result. A game never trusts a client about hits, health, or score, so cheating by editing the client does not work.

The package knows nothing about game rules. It syncs players and whatever state a game registers, so a team arena, a co-op game, and a shared VR room all use the same networking.

Session messages (joins, state changes, game events, results) follow `3d-network-schema`, so servers, spectators and stats outside Unity can read them. The per-tick stream of positions and inputs stays in the backend's own binary format.

`3d-unity-firebase` stores accounts and saved data between matches; this package runs the match itself.

### Status

Version 0.1, on **Unity Netcode for GameObjects 2.13** with Unity Transport.

* `NetworkSession`: one component in the first scene. Sets up Netcode's `NetworkManager` and transport itself; `StartHost()`, `StartServer()` (dedicated, no local player), `StartClient(address)`, `Stop()`; `Started`, `Stopped`, `PlayerJoined`, `PlayerLeft` events; player prefab, port and tick rate (default 60). Starts from the command line with `-server`, `-host`, `-client <address>`, `-port <port>`, so a dedicated server or a test client needs no UI.
* `NetworkCharacter`: runs a `CharacterMotor` (`3d-unity-character`) with the server in charge.
  * Every network tick the owner takes its command, moves at once (client prediction) and sends the command to the server.
  * The server applies the commands in order and sends the result to everyone. A late command is waited for, never guessed, so the server runs exactly the owner's steps; a backed-up queue is drained two commands per tick.
  * The owner compares the result with its prediction for that tick; beyond 5 cm it returns to the server's state (including jump timers) and replays its newer commands.
  * Other players are shown 3 ticks in the past, interpolated between results.
  * A host's own character runs directly on the server.
  * Components listed as owner-only (e.g. the constructor's controls) are switched off on every other copy, so this package knows nothing about controls or cameras.

Checked with a Windows build as client (`-client 127.0.0.1 -bot`, a dev bot in `temnorid-arena` walking circles and jumping) against the Editor as host: the client joins, predicts, and the host sees it move and jump with 1–2 commands queued. 2 prediction corrections in 90 s of walking and jumping every 2 s (it was ~18 in 25 s before the server stopped guessing late commands and before jump timers were part of the state).

The per-tick stream stays in Netcode's binary RPCs; session messages go through the session channel below.

* `SessionChannel` (`NetworkSession.Messages`): the session in `3d-network-schema` form, kept in sync over Netcode named messages.
  * C# models of the schema: `SessionData`, `MessageData` (typed payloads for every `session.*` message), `ResultData`, `ReplayData`, `NetworkJson`.
  * Server: numbers every message (seq 1, 2, 3 ... no gaps), announces joins and leaves (disconnects after `disconnectTimeout`, default 10 s), `SetState` (lobby → starting → running → ended, with the scene), `Publish` game events `<game>.<name>`, `End(result)`, `SetTeams`/`AssignTeam`, and records a replay from the moment the session runs (`CreateReplay()`).
  * A newcomer gets a snapshot of the whole session numbered as of the latest message, so numbering stays gapless.
  * Client: joins automatically with its display name (`-name` on the command line), keeps a copy of the session, may `Request` a game event; it only counts once the server publishes it (`RequestReceived` on the server decides).
  * `NetworkSession` settings: `mode` (e.g. `temnorid-arena.competitive`), display name, max players.

Checked: the schema's example replay loads into the models and saves back identical (and valid); live, a built client joins the Editor host by name, its game-event request is accepted and published, the session goes lobby → starting → running → ended with a result, both sides end with the same session, the server's replay and session validate against the schema, and a killed client is marked disconnected with `session.left` within the timeout.

```csharp
// First scene: a NetworkSession with the player prefab
// (prefab: NetworkObject + CharacterMotor + NetworkCharacter, controls listed as owner-only).
NetworkSession.Current.StartHost();
NetworkSession.Current.StartClient("192.168.0.10");
NetworkSession.Current.PlayerJoined += clientId => { /* server: assign a team */ };
```

### Roadmap

* Host and dedicated server modes
* Player spawn and sync
* Synced game state
* Server-side hit checks with lag compensation
* Client prediction for movement
* Unreliable commands with redundancy (reliable in 0.1)
* Smoothing the local player between ticks on high-refresh screens
* Writing results and replays to storage from a trusted server
* Unity Relay / Lobby / Matchmaker (Multiplayer Services)
* Lobbies and matchmaking
* Reconnect
* Stable v1 network API
