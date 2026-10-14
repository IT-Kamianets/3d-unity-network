using System.Collections.Generic;
using ITKamianets.Engine.Character;
using Unity.Netcode;
using UnityEngine;

namespace ITKamianets.Engine.Network
{
    /// <summary>
    /// Runs a <see cref="CharacterMotor"/> over the network with the server in charge.
    ///
    /// Every network tick:
    /// <list type="bullet">
    /// <item>The owner takes its command, moves at once (prediction) and sends the command to the server.</item>
    /// <item>The server moves the character with that command and sends the result to everyone.</item>
    /// <item>The owner compares the result with what it predicted for that tick. If they differ, it
    /// goes back to the server's state and replays its newer commands.</item>
    /// <item>Everyone else shows the character a few ticks in the past, interpolating between results.</item>
    /// </list>
    /// A host's own character runs directly on the server. Components in <c>ownerOnly</c> (e.g.
    /// player controls) are switched off on every copy except the owner's.
    /// </summary>
    [RequireComponent(typeof(CharacterMotor))]
    public class NetworkCharacter : NetworkBehaviour
    {
        private const int BufferSize = 256;

        [Tooltip("Enabled only on the owner's copy, e.g. the controls that turn input into commands.")]
        [SerializeField] private Behaviour[] ownerOnly = new Behaviour[0];

        [Tooltip("Prediction errors smaller than this (meters) are ignored.")]
        [SerializeField] private float correctionThreshold = 0.05f;

        [Tooltip("How far in the past other players are shown, in ticks. More is smoother on bad connections, but later.")]
        [SerializeField] private float interpolationTicks = 3f;

        /// <summary>How many times the owner had to correct its prediction. For debugging.</summary>
        public int Corrections { get; private set; }

        /// <summary>Commands waiting on the server. Growing means the client sends faster than the server runs.</summary>
        public int QueuedCommands => _queue.Count;

        private CharacterMotor _motor;

        // Owner: what was sent and predicted, by tick.
        private readonly CharacterCommand[] _sent = new CharacterCommand[BufferSize];
        private readonly CharacterState[] _predicted = new CharacterState[BufferSize];
        private readonly int[] _sentTick = new int[BufferSize];
        private int _lastPredictedTick = -1;
        private int _lastConfirmedTick = -1;

        // Server: commands from a remote owner, in order.
        private readonly Queue<CommandMessage> _queue = new Queue<CommandMessage>();
        private int _lastQueuedTick = -1;
        private int _lastProcessedTick = -1;

        // Everyone else: recent server states, oldest first.
        private readonly List<StateMessage> _snapshots = new List<StateMessage>();

        private float TickDelta => 1f / NetworkManager.NetworkConfig.TickRate;

        private void Awake()
        {
            _motor = GetComponent<CharacterMotor>();
            for (var i = 0; i < BufferSize; i++)
            {
                _sentTick[i] = -1;
            }
        }

        public override void OnNetworkSpawn()
        {
            _motor.SimulateInUpdate = false;
            foreach (var behaviour in ownerOnly)
            {
                if (behaviour != null)
                {
                    behaviour.enabled = IsOwner;
                }
            }

            NetworkManager.NetworkTickSystem.Tick += OnTick;
        }

        public override void OnNetworkDespawn()
        {
            if (NetworkManager != null && NetworkManager.NetworkTickSystem != null)
            {
                NetworkManager.NetworkTickSystem.Tick -= OnTick;
            }
        }

        private void OnTick()
        {
            if (IsServer)
            {
                ServerTick();
            }
            else if (IsOwner)
            {
                PredictTick();
            }
        }

        private void Update()
        {
            if (IsSpawned && !IsServer && !IsOwner)
            {
                Interpolate();
            }
        }

        // ---- Owner ----

        private void PredictTick()
        {
            var tick = NetworkManager.LocalTime.Tick;
            var command = _motor.ConsumeCommand();
            _motor.Simulate(command, TickDelta);

            var slot = tick % BufferSize;
            _sent[slot] = command;
            _sentTick[slot] = tick;
            _predicted[slot] = _motor.GetState();
            _lastPredictedTick = tick;

            SubmitCommandRpc(CommandMessage.From(tick, command));
        }

        private void Reconcile(in StateMessage state)
        {
            // Tick -1: the server hasn't applied any of our commands yet.
            if (state.Tick < 0 || state.Tick <= _lastConfirmedTick)
            {
                return;
            }

            _lastConfirmedTick = state.Tick;

            var slot = state.Tick % BufferSize;
            if (_sentTick[slot] == state.Tick && Vector3.Distance(_predicted[slot].Position, state.Position) <= correctionThreshold)
            {
                return;
            }

            // Back to the server's version, then redo every command it hasn't seen yet.
            Corrections++;
            _motor.SetState(state.ToCharacterState());

            for (var tick = state.Tick + 1; tick <= _lastPredictedTick; tick++)
            {
                var replay = tick % BufferSize;
                if (_sentTick[replay] != tick)
                {
                    continue;
                }

                _motor.Simulate(_sent[replay], TickDelta);
                _predicted[replay] = _motor.GetState();
            }
        }

        // ---- Server ----

        [Rpc(SendTo.Server)]
        private void SubmitCommandRpc(CommandMessage command, RpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId != OwnerClientId || command.Tick <= _lastQueuedTick)
            {
                return;
            }

            _lastQueuedTick = command.Tick;
            _queue.Enqueue(command);
        }

        private void ServerTick()
        {
            if (IsOwner)
            {
                // The host's own character: no one to predict for.
                _motor.Simulate(_motor.ConsumeCommand(), TickDelta);
                _lastProcessedTick = NetworkManager.LocalTime.Tick;
            }
            else
            {
                // One command per tick; two when the queue has built up, to catch up. A late
                // command is waited for, not guessed: the server runs exactly the steps the
                // owner ran, so their results match and the owner needs no correction.
                var steps = _queue.Count > 4 ? 2 : 1;
                for (var i = 0; i < steps && _queue.Count > 0; i++)
                {
                    var message = _queue.Dequeue();
                    _lastProcessedTick = message.Tick;
                    _motor.Simulate(message.ToCharacterCommand(), TickDelta);
                }
            }

            StateRpc(StateMessage.From(_lastProcessedTick, NetworkManager.ServerTime.Tick, _motor.GetState()));
        }

        // ---- Everyone else ----

        [Rpc(SendTo.NotServer, Delivery = RpcDelivery.Unreliable)]
        private void StateRpc(StateMessage state)
        {
            if (IsOwner)
            {
                Reconcile(state);
                return;
            }

            if (_snapshots.Count > 0 && state.ServerTick <= _snapshots[_snapshots.Count - 1].ServerTick)
            {
                return;
            }

            _snapshots.Add(state);
            if (_snapshots.Count > 32)
            {
                _snapshots.RemoveAt(0);
            }
        }

        private void Interpolate()
        {
            if (_snapshots.Count == 0)
            {
                return;
            }

            var renderTick = NetworkManager.ServerTime.TickWithPartial - interpolationTicks;

            var from = _snapshots[0];
            var to = from;
            for (var i = 1; i < _snapshots.Count; i++)
            {
                to = _snapshots[i];
                if (to.ServerTick >= renderTick)
                {
                    break;
                }

                from = to;
            }

            var span = to.ServerTick - from.ServerTick;
            var t = span > 0 ? Mathf.Clamp01((float)((renderTick - from.ServerTick) / span)) : 1f;

            var state = to.ToCharacterState();
            state.Position = Vector3.Lerp(from.Position, to.Position, t);
            state.Yaw = Mathf.LerpAngle(from.Yaw, to.Yaw, t);
            _motor.SetState(state);
        }

        // ---- Messages ----

        private struct CommandMessage : INetworkSerializable
        {
            public int Tick;
            public Vector3 Move;
            public float Yaw;
            public bool Sprint;
            public bool Jump;
            public bool Crouch;

            public static CommandMessage From(int tick, in CharacterCommand command)
            {
                return new CommandMessage
                {
                    Tick = tick,
                    Move = command.Move,
                    Yaw = command.Yaw,
                    Sprint = command.Sprint,
                    Jump = command.Jump,
                    Crouch = command.Crouch,
                };
            }

            public CharacterCommand ToCharacterCommand()
            {
                return new CharacterCommand { Move = Move, Yaw = Yaw, Sprint = Sprint, Jump = Jump, Crouch = Crouch };
            }

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref Tick);
                serializer.SerializeValue(ref Move);
                serializer.SerializeValue(ref Yaw);
                serializer.SerializeValue(ref Sprint);
                serializer.SerializeValue(ref Jump);
                serializer.SerializeValue(ref Crouch);
            }
        }

        private struct StateMessage : INetworkSerializable
        {
            /// <summary>The owner's tick of the last command applied, for the owner to check its prediction.</summary>
            public int Tick;

            /// <summary>The server's tick when this was sent, for others to place it in time.</summary>
            public int ServerTick;

            public Vector3 Position;
            public float Yaw;
            public Vector3 Velocity;
            public bool IsGrounded;
            public bool IsCrouching;
            public float SinceGrounded;
            public float SinceJumpRequested;

            public static StateMessage From(int tick, int serverTick, in CharacterState state)
            {
                return new StateMessage
                {
                    Tick = tick,
                    ServerTick = serverTick,
                    Position = state.Position,
                    Yaw = state.Yaw,
                    Velocity = state.Velocity,
                    IsGrounded = state.IsGrounded,
                    IsCrouching = state.IsCrouching,
                    SinceGrounded = state.SinceGrounded,
                    SinceJumpRequested = state.SinceJumpRequested,
                };
            }

            public CharacterState ToCharacterState()
            {
                return new CharacterState
                {
                    Position = Position,
                    Yaw = Yaw,
                    Velocity = Velocity,
                    IsGrounded = IsGrounded,
                    IsCrouching = IsCrouching,
                    SinceGrounded = SinceGrounded,
                    SinceJumpRequested = SinceJumpRequested,
                };
            }

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref Tick);
                serializer.SerializeValue(ref ServerTick);
                serializer.SerializeValue(ref Position);
                serializer.SerializeValue(ref Yaw);
                serializer.SerializeValue(ref Velocity);
                serializer.SerializeValue(ref IsGrounded);
                serializer.SerializeValue(ref IsCrouching);
                serializer.SerializeValue(ref SinceGrounded);
                serializer.SerializeValue(ref SinceJumpRequested);
            }
        }
    }
}
