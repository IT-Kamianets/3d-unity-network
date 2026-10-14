using System;
using ITKamianets.Engine.Core;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace ITKamianets.Engine.Network
{
    public enum NetworkRole
    {
        Offline,
        Host,
        Server,
        Client,
    }

    /// <summary>
    /// Starts and stops a multiplayer session: a host (server and player in one), a dedicated
    /// server, or a client. Put one in the first scene; it sets up Netcode's NetworkManager and
    /// transport on its own object, so a scene needs nothing else.
    ///
    /// A dedicated server or a test client can start itself from the command line:
    /// <c>-server</c>, <c>-host</c>, <c>-client 1.2.3.4</c>, optionally <c>-port 7777</c> and <c>-name Ira</c>.
    /// </summary>
    [DisallowMultipleComponent]
    public class NetworkSession : MonoBehaviour
    {
        [Tooltip("Spawned for every player that joins. Needs a NetworkObject.")]
        [SerializeField] private GameObject playerPrefab;

        [Tooltip("Other prefabs spawned over the network at runtime (e.g. bots, projectiles). Each needs a NetworkObject.")]
        [SerializeField] private GameObject[] networkPrefabs = new GameObject[0];

        [SerializeField] private ushort port = 7777;

        [Tooltip("Simulation steps per second on the server and in client prediction.")]
        [SerializeField] private uint tickRate = 60;

        [Tooltip("Start as server, host or client when the matching command-line flag is given.")]
        [SerializeField] private bool startFromCommandLine = true;

        [Tooltip("Seconds without contact before a player counts as disconnected (a crash or lost connection, not a clean quit).")]
        [SerializeField] private float disconnectTimeout = 10f;

        [Header("Session")]
        [Tooltip("Game and mode, \"<game>.<mode>\", e.g. temnorid-arena.competitive. Written into the session document.")]
        [SerializeField] private string mode = "game.default";

        [Tooltip("This player's name in the session. -name <name> on the command line overrides it.")]
        [SerializeField] private string displayName = "Player";

        [Tooltip("0 for no limit.")]
        [SerializeField] private int maxPlayers;

        /// <summary>The running session, or null.</summary>
        public static NetworkSession Current { get; private set; }

        public event Action<NetworkRole> Started;
        public event Action Stopped;

        /// <summary>Raised on the server when a player connects, with its client id.</summary>
        public event Action<ulong> PlayerJoined;

        /// <summary>Raised on the server when a player disconnects, with its client id.</summary>
        public event Action<ulong> PlayerLeft;

        public NetworkRole Role { get; private set; } = NetworkRole.Offline;

        public NetworkManager Manager { get; private set; }

        /// <summary>The session document, its messages and replay (3d-network-schema).</summary>
        public SessionChannel Messages { get; private set; }

        public string Mode
        {
            get => mode;
            set => mode = value;
        }

        public string DisplayName
        {
            get => displayName;
            set => displayName = value;
        }

        public int MaxPlayers
        {
            get => maxPlayers;
            set => maxPlayers = value;
        }

        public ushort Port => port;

        public uint TickRate => tickRate;

        /// <summary>Seconds per simulation tick.</summary>
        public float TickDelta => 1f / tickRate;

        private const string LogCategory = "Network";

        private UnityTransport _transport;

        private void Awake()
        {
            if (Current != null && Current != this)
            {
                EngineLog.Warning(LogCategory, "A NetworkSession already exists; this one is removed.", this);
                Destroy(gameObject);
                return;
            }

            Current = this;
            DontDestroyOnLoad(gameObject);
            Messages = new SessionChannel(this);

            // TryGetComponent, not "?? AddComponent": in the Editor a missing component is a fake null.
            if (!TryGetComponent(out _transport))
            {
                _transport = gameObject.AddComponent<UnityTransport>();
            }

            if (!TryGetComponent<NetworkManager>(out var manager))
            {
                manager = gameObject.AddComponent<NetworkManager>();
            }

            Manager = manager;
            Manager.NetworkConfig ??= new NetworkConfig();
            Manager.NetworkConfig.NetworkTransport = _transport;
            _transport.DisconnectTimeoutMS = Mathf.RoundToInt(disconnectTimeout * 1000f);
            Manager.NetworkConfig.TickRate = tickRate;

            if (playerPrefab != null)
            {
                Manager.NetworkConfig.PlayerPrefab = playerPrefab;
            }

            foreach (var prefab in networkPrefabs)
            {
                if (prefab != null)
                {
                    Manager.AddNetworkPrefab(prefab);
                }
            }

            Manager.OnClientConnectedCallback += OnClientConnected;
            Manager.OnClientDisconnectCallback += OnClientDisconnected;
            Manager.OnServerStopped += OnStopped;
            Manager.OnClientStopped += OnStopped;
        }

        private void Start()
        {
            if (startFromCommandLine)
            {
                StartFromCommandLine(Environment.GetCommandLineArgs());
            }
        }

        private void OnDestroy()
        {
            if (Current == this)
            {
                Current = null;
            }

            if (Manager != null)
            {
                Manager.OnClientConnectedCallback -= OnClientConnected;
                Manager.OnClientDisconnectCallback -= OnClientDisconnected;
                Manager.OnServerStopped -= OnStopped;
                Manager.OnClientStopped -= OnStopped;
            }
        }

        /// <summary>Server and local player in one process. Others join with <see cref="StartClient"/>.</summary>
        public bool StartHost()
        {
            _transport.SetConnectionData("127.0.0.1", port, "0.0.0.0");
            return Begin(NetworkRole.Host, Manager.StartHost());
        }

        /// <summary>A dedicated server: runs the match, has no local player.</summary>
        public bool StartServer()
        {
            _transport.SetConnectionData("127.0.0.1", port, "0.0.0.0");
            return Begin(NetworkRole.Server, Manager.StartServer());
        }

        public bool StartClient(string address)
        {
            _transport.SetConnectionData(address, port);
            return Begin(NetworkRole.Client, Manager.StartClient());
        }

        public void Stop()
        {
            if (Role != NetworkRole.Offline)
            {
                Manager.Shutdown();
            }
        }

        /// <summary>Reads -server, -host, -client &lt;address&gt;, -port &lt;port&gt; and -name &lt;name&gt;. Returns true if a session started.</summary>
        public bool StartFromCommandLine(string[] args)
        {
            string client = null;
            var server = false;
            var host = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-server":
                        server = true;
                        break;
                    case "-host":
                        host = true;
                        break;
                    case "-client" when i + 1 < args.Length:
                        client = args[++i];
                        break;
                    case "-name" when i + 1 < args.Length:
                        displayName = args[++i];
                        break;
                    case "-port" when i + 1 < args.Length && ushort.TryParse(args[i + 1], out var value):
                        port = value;
                        i++;
                        break;
                }
            }

            if (server)
            {
                return StartServer();
            }

            if (host)
            {
                return StartHost();
            }

            return client != null && StartClient(client);
        }

        private bool Begin(NetworkRole role, bool started)
        {
            if (!started)
            {
                EngineLog.Error(LogCategory, $"Could not start the network session as {role}.", this);
                return false;
            }

            EngineLog.Info(LogCategory, $"Started as {role} on port {port}.");
            Role = role;
            Messages.Begin(role);
            Started?.Invoke(role);
            return true;
        }

        private void OnClientConnected(ulong clientId)
        {
            Messages.ClientConnected(clientId);
            if (Manager.IsServer)
            {
                EngineLog.Info(LogCategory, $"Player {clientId} joined.");
                PlayerJoined?.Invoke(clientId);
            }
        }

        private void OnClientDisconnected(ulong clientId)
        {
            Messages.ClientDisconnected(clientId);
            if (Manager.IsServer)
            {
                EngineLog.Info(LogCategory, $"Player {clientId} left.");
                PlayerLeft?.Invoke(clientId);
            }
        }

        private void OnStopped(bool wasHost)
        {
            if (Role == NetworkRole.Offline)
            {
                return;
            }

            Role = NetworkRole.Offline;
            Messages.End();
            EngineLog.Info(LogCategory, "Stopped.");
            Stopped?.Invoke();
        }
    }
}
