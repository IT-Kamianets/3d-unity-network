using System;
using System.Collections.Generic;
using ITKamianets.Engine.Core;
using ITKamianets.Engine.Network.Model;
using Unity.Collections;
using Unity.Netcode;

namespace ITKamianets.Engine.Network
{
    /// <summary>
    /// The session in 3d-network-schema form, kept in sync between server and clients: who is in
    /// it, its state, and the messages that matter after the fact (joins, leaves, state changes,
    /// game events, the result). Reach it through <see cref="NetworkSession.Messages"/>.
    ///
    /// The server is the authority. It numbers every message it sends (seq 1, 2, 3 ... with no
    /// gaps) and records them from the moment the session starts running, so
    /// <see cref="CreateReplay"/> gives a replay document. A client sends its join request on
    /// connecting and may send requests for game events; they only count once the server
    /// publishes them as its own messages.
    ///
    /// Positions and inputs every tick do not go here: they stay in Netcode's binary stream.
    /// </summary>
    public sealed class SessionChannel
    {
        private const string LogCategory = "Network";
        private const string MessageName = "itk.session";

        private readonly NetworkSession _owner;
        private readonly Dictionary<ulong, string> _playerIds = new Dictionary<ulong, string>();
        private readonly List<MessageData> _record = new List<MessageData>();
        private SessionData _replayStart;
        private long _seq;
        private long _lastReceivedSeq;
        private bool _active;

        internal SessionChannel(NetworkSession owner)
        {
            _owner = owner;
        }

        /// <summary>The session as this side knows it: authoritative on the server, a copy on clients. Null when offline.</summary>
        public SessionData Session { get; private set; }

        /// <summary>This side's player id, or null on a dedicated server.</summary>
        public string LocalPlayerId { get; private set; }

        /// <summary>Every message applied to <see cref="Session"/>: published on the server, received on clients.</summary>
        public event Action<MessageData> MessageReceived;

        /// <summary>Server: a client asked for a game event. Validate it, then <see cref="Publish"/> it (or not).</summary>
        public event Action<MessageData> RequestReceived;

        public event Action<SessionData> SessionChanged;

        private NetworkManager Manager => _owner.Manager;

        private bool IsServer => Manager != null && Manager.IsServer;

        /// <summary>The player id the server uses for a Netcode client.</summary>
        public static string PlayerIdFor(ulong clientId)
        {
            return $"player-{clientId}";
        }

        /// <summary>The Netcode client of a player id, for sending it something directly.</summary>
        public bool TryGetClientId(string playerId, out ulong clientId)
        {
            foreach (var pair in _playerIds)
            {
                if (pair.Value == playerId)
                {
                    clientId = pair.Key;
                    return true;
                }
            }

            clientId = 0;
            return false;
        }

        // ---- Server ----

        /// <summary>Moves the session to a new state, optionally choosing its scene. Running starts the replay recording.</summary>
        public void SetState(SessionState state, string sceneId = null)
        {
            if (!RequireServer(nameof(SetState)))
            {
                return;
            }

            Publish(MessageData.StateChanged, new StateChangedPayload { State = state, SceneId = sceneId });
        }

        /// <summary>Sends a server message to everyone: a game event "&lt;game&gt;.&lt;name&gt;" with game-defined data, or a session.* message.</summary>
        public MessageData Publish(string type, object payload, long? tick = null)
        {
            if (!RequireServer(nameof(Publish)))
            {
                return null;
            }

            var message = NewMessage(type, MessageData.FromServer, payload);
            message.Seq = ++_seq;
            message.Tick = tick ?? Manager.ServerTime.Tick;

            Apply(message);
            if (_replayStart != null)
            {
                _record.Add(message);
            }

            SendToClients(message);
            MessageReceived?.Invoke(message);
            return message;
        }

        /// <summary>Ends the session with its result (session.ended).</summary>
        public void End(ResultData result)
        {
            if (!RequireServer(nameof(End)))
            {
                return;
            }

            result.EndedAt ??= NetworkJson.Now();
            Publish(MessageData.Ended, result);
        }

        /// <summary>Sets the teams and sends everyone a fresh snapshot.</summary>
        public void SetTeams(List<TeamData> teams)
        {
            if (RequireServer(nameof(SetTeams)))
            {
                Session.Teams = teams;
                Publish(MessageData.Snapshot, new SnapshotPayload { Session = Session });
            }
        }

        /// <summary>Puts a player in a team and sends everyone a fresh snapshot.</summary>
        public void AssignTeam(string playerId, string teamId)
        {
            if (!RequireServer(nameof(AssignTeam)))
            {
                return;
            }

            var player = Session.FindPlayer(playerId);
            if (player == null)
            {
                return;
            }

            player.TeamId = teamId;
            Publish(MessageData.Snapshot, new SnapshotPayload { Session = Session });
        }

        /// <summary>The session as it started running, then every server message since: a replay document.</summary>
        public ReplayData CreateReplay()
        {
            return new ReplayData
            {
                Session = _replayStart ?? Clone(Session),
                Messages = new List<MessageData>(_record),
            };
        }

        // ---- Client ----

        /// <summary>Client: asks the server for a game event. It counts only if the server publishes it.</summary>
        public void Request(string type, object payload)
        {
            if (!_active || Manager == null)
            {
                return;
            }

            var message = NewMessage(type, LocalPlayerId, payload);
            if (IsServer)
            {
                // A host's own requests go straight to its server side.
                OnRequest(Manager.LocalClientId, message);
                return;
            }

            Send(message, NetworkManager.ServerClientId);
        }

        // ---- Lifecycle (called by NetworkSession) ----

        internal void Begin(NetworkRole role)
        {
            _active = true;
            _seq = 0;
            _lastReceivedSeq = 0;
            _record.Clear();
            _replayStart = null;
            _playerIds.Clear();
            Manager.CustomMessagingManager.RegisterNamedMessageHandler(MessageName, OnNamedMessage);

            LocalPlayerId = role == NetworkRole.Server ? null : PlayerIdFor(Manager.LocalClientId);

            if (role == NetworkRole.Host || role == NetworkRole.Server)
            {
                Session = new SessionData
                {
                    SessionId = Guid.NewGuid().ToString("N"),
                    Mode = _owner.Mode,
                    State = SessionState.Lobby,
                    MaxPlayers = _owner.MaxPlayers > 0 ? _owner.MaxPlayers : (int?)null,
                    CreatedAt = NetworkJson.Now(),
                };

                if (role == NetworkRole.Host)
                {
                    AddPlayer(Manager.LocalClientId, new JoinPayload { DisplayName = _owner.DisplayName });
                }
            }
        }

        internal void ClientConnected(ulong clientId)
        {
            // A client introduces itself once connected; the server answers with joined + snapshot.
            if (!IsServer && clientId == Manager.LocalClientId)
            {
                LocalPlayerId = PlayerIdFor(clientId);
                Send(NewMessage(MessageData.Join, LocalPlayerId, new JoinPayload { DisplayName = _owner.DisplayName }), NetworkManager.ServerClientId);
            }
        }

        internal void ClientDisconnected(ulong clientId)
        {
            if (!IsServer || !_playerIds.TryGetValue(clientId, out var playerId))
            {
                return;
            }

            _playerIds.Remove(clientId);
            Publish(MessageData.Left, new LeftPayload { PlayerId = playerId, Reason = LeaveReason.Disconnected });
        }

        internal void End()
        {
            if (_active && Manager != null && Manager.CustomMessagingManager != null)
            {
                Manager.CustomMessagingManager.UnregisterNamedMessageHandler(MessageName);
            }

            _active = false;
            Session = null;
            LocalPlayerId = null;
        }

        // ---- Internals ----

        private void OnNamedMessage(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out string json);
            MessageData message;
            try
            {
                message = NetworkJson.Deserialize<MessageData>(json);
            }
            catch (Exception exception)
            {
                EngineLog.Warning(LogCategory, $"Unreadable session message from {senderId}: {exception.Message}");
                return;
            }

            if (IsServer)
            {
                OnRequest(senderId, message);
            }
            else if (senderId == NetworkManager.ServerClientId)
            {
                OnServerMessage(message);
            }
        }

        private void OnRequest(ulong senderId, MessageData message)
        {
            // The sender is who Netcode says it is, whatever the message claims.
            message.From = PlayerIdFor(senderId);
            message.Seq = null;

            if (message.Type == MessageData.Join)
            {
                AddPlayer(senderId, message.GetPayload<JoinPayload>() ?? new JoinPayload());
                return;
            }

            if (message.IsSessionMessage)
            {
                EngineLog.Warning(LogCategory, $"{message.From} may not send {message.Type}; ignored.");
                return;
            }

            RequestReceived?.Invoke(message);
        }

        private void AddPlayer(ulong clientId, JoinPayload join)
        {
            var playerId = PlayerIdFor(clientId);
            _playerIds[clientId] = playerId;

            var player = Session.FindPlayer(playerId);
            if (player == null)
            {
                player = new PlayerData { PlayerId = playerId };
                Session.Players.Add(player);
            }

            player.DisplayName = string.IsNullOrEmpty(join.DisplayName) ? playerId : join.DisplayName;
            player.Role = join.Role;
            player.Connected = true;

            Publish(MessageData.Joined, new PlayerPayload { Player = player });

            // The newcomer gets the whole session, numbered as of the latest message, so the
            // numbering stays gapless for everyone. Not recorded: it repeats what is known.
            if (clientId != Manager.LocalClientId)
            {
                var snapshot = NewMessage(MessageData.Snapshot, MessageData.FromServer, new SnapshotPayload { Session = Session });
                snapshot.Seq = _seq;
                snapshot.Tick = Manager.ServerTime.Tick;
                Send(snapshot, clientId);
            }
        }

        private void OnServerMessage(MessageData message)
        {
            if (message.Seq.HasValue)
            {
                // A newcomer starts counting from its first message: it never saw what came before.
                if (_lastReceivedSeq == 0 || (message.Type == MessageData.Snapshot && message.Seq >= _lastReceivedSeq))
                {
                    _lastReceivedSeq = message.Seq.Value;
                }
                else if (message.Seq.Value != _lastReceivedSeq + 1)
                {
                    EngineLog.Warning(LogCategory, $"Session message {message.Seq} arrived after {_lastReceivedSeq}; something was missed.");
                    _lastReceivedSeq = Math.Max(_lastReceivedSeq, message.Seq.Value);
                }
                else
                {
                    _lastReceivedSeq = message.Seq.Value;
                }
            }

            Apply(message);
            MessageReceived?.Invoke(message);
        }

        // What each session.* message does to the session document, the same on both sides.
        private void Apply(MessageData message)
        {
            switch (message.Type)
            {
                case MessageData.Snapshot:
                    Session = message.GetPayload<SnapshotPayload>()?.Session ?? Session;
                    break;

                case MessageData.Joined when Session != null:
                    var joined = message.GetPayload<PlayerPayload>()?.Player;
                    if (joined != null)
                    {
                        Session.Players.RemoveAll(player => player.PlayerId == joined.PlayerId);
                        Session.Players.Add(joined);
                    }

                    break;

                case MessageData.Left when Session != null:
                    var left = Session.FindPlayer(message.GetPayload<LeftPayload>()?.PlayerId);
                    if (left != null)
                    {
                        left.Connected = false;
                    }

                    break;

                case MessageData.StateChanged when Session != null:
                    var change = message.GetPayload<StateChangedPayload>();
                    Session.State = change.State;
                    if (change.SceneId != null)
                    {
                        Session.SceneId = change.SceneId;
                    }

                    if (change.State == SessionState.Running && Session.StartedAt == null)
                    {
                        Session.StartedAt = message.SentAt;
                        if (IsServer)
                        {
                            // The replay starts from the session as it began running.
                            _replayStart = Clone(Session);
                            _record.Clear();
                        }
                    }

                    break;

                case MessageData.Ended when Session != null:
                    Session.State = SessionState.Ended;
                    Session.EndedAt = message.GetPayload<ResultData>()?.EndedAt ?? message.SentAt;
                    break;

                default:
                    return;
            }

            SessionChanged?.Invoke(Session);
        }

        private MessageData NewMessage(string type, string from, object payload)
        {
            var message = new MessageData
            {
                MessageId = Guid.NewGuid().ToString("N"),
                SessionId = Session?.SessionId ?? "pending",
                SentAt = NetworkJson.Now(),
                From = from ?? MessageData.FromServer,
                Type = type,
            };
            message.SetPayload(payload);
            return message;
        }

        private void SendToClients(MessageData message)
        {
            foreach (var clientId in Manager.ConnectedClientsIds)
            {
                if (clientId != Manager.LocalClientId)
                {
                    Send(message, clientId);
                }
            }
        }

        private void Send(MessageData message, ulong clientId)
        {
            var json = NetworkJson.Serialize(message);
            using var writer = new FastBufferWriter(FastBufferWriter.GetWriteSize(json) + 8, Allocator.Temp);
            writer.WriteValueSafe(json);
            Manager.CustomMessagingManager.SendNamedMessage(MessageName, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }

        private bool RequireServer(string operation)
        {
            if (_active && IsServer && Session != null)
            {
                return true;
            }

            EngineLog.Warning(LogCategory, $"{operation} is for the server of a running session.");
            return false;
        }

        private static SessionData Clone(SessionData session)
        {
            return session == null ? null : NetworkJson.Deserialize<SessionData>(NetworkJson.Serialize(session));
        }
    }
}
