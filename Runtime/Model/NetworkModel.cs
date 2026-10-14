using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace ITKamianets.Engine.Network.Model
{
    // C# mirror of 3d-network-schema v1. Field names match the schema exactly. Game-defined
    // content ("data", game event payloads, settings) is kept as JSON objects, since the schema
    // does not look inside it.

    /// <summary>Mirrors session.schema.json#/$defs/SessionState.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum SessionState
    {
        [EnumMember(Value = "lobby")] Lobby,
        [EnumMember(Value = "starting")] Starting,
        [EnumMember(Value = "running")] Running,
        [EnumMember(Value = "ended")] Ended,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum PlayerRole
    {
        [EnumMember(Value = "player")] Player,
        [EnumMember(Value = "spectator")] Spectator,
        [EnumMember(Value = "bot")] Bot,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum LeaveReason
    {
        [EnumMember(Value = "quit")] Quit,
        [EnumMember(Value = "disconnected")] Disconnected,
        [EnumMember(Value = "kicked")] Kicked,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ResultReason
    {
        [EnumMember(Value = "completed")] Completed,
        [EnumMember(Value = "abandoned")] Abandoned,
        [EnumMember(Value = "cancelled")] Cancelled,
    }

    /// <summary>Mirrors common.schema.json#/$defs/Metadata.</summary>
    public class NetworkMetadata
    {
        [JsonProperty("tags", NullValueHandling = NullValueHandling.Ignore)] public List<string> Tags;
        [JsonProperty("properties", NullValueHandling = NullValueHandling.Ignore)] public JObject Properties;
    }

    /// <summary>Mirrors session.schema.json.</summary>
    public class SessionData
    {
        public const string CurrentSchemaVersion = "1.0.0";

        [JsonProperty("schemaVersion")] public string SchemaVersion = CurrentSchemaVersion;
        [JsonProperty("sessionId")] public string SessionId;

        /// <summary>Game and mode, e.g. temnorid-arena.competitive.</summary>
        [JsonProperty("mode")] public string Mode;

        /// <summary>The 3d-scene-schema scene played on; null while none is chosen.</summary>
        [JsonProperty("sceneId", NullValueHandling = NullValueHandling.Ignore)] public string SceneId;

        [JsonProperty("state")] public SessionState State;
        [JsonProperty("maxPlayers", NullValueHandling = NullValueHandling.Ignore)] public int? MaxPlayers;
        [JsonProperty("teams", NullValueHandling = NullValueHandling.Ignore)] public List<TeamData> Teams;
        [JsonProperty("players")] public List<PlayerData> Players = new List<PlayerData>();
        [JsonProperty("createdAt")] public string CreatedAt;
        [JsonProperty("startedAt", NullValueHandling = NullValueHandling.Ignore)] public string StartedAt;
        [JsonProperty("endedAt", NullValueHandling = NullValueHandling.Ignore)] public string EndedAt;
        [JsonProperty("settings", NullValueHandling = NullValueHandling.Ignore)] public JObject Settings;
        [JsonProperty("metadata", NullValueHandling = NullValueHandling.Ignore)] public NetworkMetadata Metadata;

        public PlayerData FindPlayer(string playerId)
        {
            return Players.Find(player => player.PlayerId == playerId);
        }
    }

    /// <summary>Mirrors session.schema.json#/$defs/Team.</summary>
    public class TeamData
    {
        [JsonProperty("teamId")] public string TeamId;
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)] public string Name;
        [JsonProperty("metadata", NullValueHandling = NullValueHandling.Ignore)] public NetworkMetadata Metadata;
    }

    /// <summary>Mirrors session.schema.json#/$defs/Player.</summary>
    public class PlayerData
    {
        [JsonProperty("playerId")] public string PlayerId;
        [JsonProperty("displayName", NullValueHandling = NullValueHandling.Ignore)] public string DisplayName;
        [JsonProperty("teamId", NullValueHandling = NullValueHandling.Ignore)] public string TeamId;
        [JsonProperty("role")] public PlayerRole Role;
        [JsonProperty("connected")] public bool Connected = true;
        [JsonProperty("metadata", NullValueHandling = NullValueHandling.Ignore)] public NetworkMetadata Metadata;
    }

    /// <summary>Mirrors message.schema.json: an envelope and a payload typed by <see cref="Type"/>.</summary>
    public class MessageData
    {
        public const string CurrentSchemaVersion = "1.0.0";
        public const string FromServer = "server";

        public const string Join = "session.join";
        public const string Joined = "session.joined";
        public const string Left = "session.left";
        public const string Snapshot = "session.snapshot";
        public const string StateChanged = "session.state-changed";
        public const string Ended = "session.ended";

        [JsonProperty("schemaVersion")] public string SchemaVersion = CurrentSchemaVersion;
        [JsonProperty("messageId")] public string MessageId;
        [JsonProperty("sessionId")] public string SessionId;

        /// <summary>Set by the server: 1, 2, 3 ... with no gaps. Null on a client's request.</summary>
        [JsonProperty("seq", NullValueHandling = NullValueHandling.Ignore)] public long? Seq;

        [JsonProperty("tick", NullValueHandling = NullValueHandling.Ignore)] public long? Tick;
        [JsonProperty("sentAt")] public string SentAt;

        /// <summary>"server", or the sending client's playerId.</summary>
        [JsonProperty("from")] public string From;

        /// <summary>A session.* type, or a game event "&lt;game&gt;.&lt;name&gt;".</summary>
        [JsonProperty("type")] public string Type;

        [JsonProperty("payload")] public JObject Payload = new JObject();

        [JsonIgnore] public bool IsSessionMessage => Type != null && Type.StartsWith("session.");

        /// <summary>The payload as a typed object, e.g. <c>GetPayload&lt;LeftPayload&gt;()</c> or a game's own class.</summary>
        public T GetPayload<T>()
        {
            return Payload == null ? default : Payload.ToObject<T>(NetworkJson.Serializer);
        }

        public void SetPayload(object payload)
        {
            Payload = payload == null ? new JObject() : JObject.FromObject(payload, NetworkJson.Serializer);
        }
    }

    /// <summary>Payload of session.join.</summary>
    public class JoinPayload
    {
        [JsonProperty("displayName", NullValueHandling = NullValueHandling.Ignore)] public string DisplayName;
        [JsonProperty("role")] public PlayerRole Role = PlayerRole.Player;
        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)] public JObject Data;
    }

    /// <summary>Payload of session.joined.</summary>
    public class PlayerPayload
    {
        [JsonProperty("player")] public PlayerData Player;
    }

    /// <summary>Payload of session.left.</summary>
    public class LeftPayload
    {
        [JsonProperty("playerId")] public string PlayerId;
        [JsonProperty("reason")] public LeaveReason Reason;
    }

    /// <summary>Payload of session.snapshot.</summary>
    public class SnapshotPayload
    {
        [JsonProperty("session")] public SessionData Session;
    }

    /// <summary>Payload of session.state-changed.</summary>
    public class StateChangedPayload
    {
        [JsonProperty("state")] public SessionState State;
        [JsonProperty("sceneId", NullValueHandling = NullValueHandling.Ignore)] public string SceneId;
    }

    /// <summary>Mirrors result.schema.json#/$defs/Result; the payload of session.ended.</summary>
    public class ResultData
    {
        [JsonProperty("endedAt")] public string EndedAt;
        [JsonProperty("reason")] public ResultReason Reason;

        /// <summary>teamIds or playerIds; empty for a draw.</summary>
        [JsonProperty("winners")] public List<string> Winners = new List<string>();

        [JsonProperty("scores")] public List<ScoreData> Scores = new List<ScoreData>();
        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)] public JObject Data;
    }

    /// <summary>Mirrors result.schema.json#/$defs/Score: exactly one of TeamId and PlayerId.</summary>
    public class ScoreData
    {
        [JsonProperty("teamId", NullValueHandling = NullValueHandling.Ignore)] public string TeamId;
        [JsonProperty("playerId", NullValueHandling = NullValueHandling.Ignore)] public string PlayerId;
        [JsonProperty("score")] public double Score;
        [JsonProperty("stats", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, double> Stats;
    }

    /// <summary>Mirrors replay.schema.json: the session as it started running, then every server message.</summary>
    public class ReplayData
    {
        public const string CurrentSchemaVersion = "1.0.0";

        [JsonProperty("schemaVersion")] public string SchemaVersion = CurrentSchemaVersion;
        [JsonProperty("session")] public SessionData Session;
        [JsonProperty("messages")] public List<MessageData> Messages = new List<MessageData>();
    }

    /// <summary>Reads and writes 3d-network-schema documents.</summary>
    public static class NetworkJson
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
            DateParseHandling = DateParseHandling.None,
        };

        public static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

        public static string Serialize(object document, bool indented = false)
        {
            return JsonConvert.SerializeObject(document, indented ? Formatting.Indented : Formatting.None, Settings);
        }

        public static T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json, Settings);
        }

        /// <summary>The current time in the schema's date-time format.</summary>
        public static string Now()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }
    }
}
