using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AnoMech.Multiplayer;

/// <summary>
/// Frames used by the TC relay before application messages are considered.
/// The relay is deliberately text-only: neither endpoint negotiates compression or accepts
/// binary frames.  Keeping this vocabulary separate from <see cref="MpMessage"/> means a
/// malformed transport frame can never be mistaken for an application message.
/// </summary>
public enum RelayControlKind
{
    Heartbeat,
    HeartbeatAck,
    PeerJoined,
    PeerLeft,
    RoomClosed,
}

public readonly record struct RelayControlFrame(
    RelayControlKind Kind,
    Guid RoomId,
    Guid PeerId,
    long Sequence);

public sealed record RelayHandshakeRequest(
    string Protocol,
    int Version,
    string[] Capabilities,
    string ClientNonce);

public sealed record RelayHandshakeResponse(
    string Protocol,
    int Version,
    string[] Capabilities,
    Guid RoomId,
    Guid PeerId,
    Guid HostId,
    string RoomCode,
    string ClientNonce,
    string? JoinToken)
{
    // The room's join secret travels to the host exactly once, on this frame. It is host-only
    // and never reaches diagnostics: the generated record ToString would have printed it.
    public override string ToString()
        => $"RelayHandshakeResponse {{ Protocol = {Protocol}, Version = {Version}, RoomId = {RoomId}, " +
           $"PeerId = {PeerId}, HostId = {HostId}, RoomCode = {RoomCode}, ClientNonce = {ClientNonce}, " +
           $"JoinToken = {(JoinToken is null ? "<none>" : "<redacted>")} }}";
}

public static class WireProtocol
{
    public const string PacketKind = "packet";
    public const string ControlKind = "control";
    public const string HandshakeKind = "handshake";
    public const string TextJsonCapability = "text-json";

    public const string RoomInviteCapability = "room-invite-secrets";

    // Protocol 4 requires host-only, per-room invitation secrets as well as text framing.
    // Both capabilities are mandatory; no legacy global-token fallback is negotiated.
    public static IReadOnlyList<string> Capabilities { get; } =
        Array.AsReadOnly(new[] { TextJsonCapability, RoomInviteCapability });

    public const int MaxJsonBytes = MpLimits.FrameBytes <= 1024 * 1024 ? MpLimits.FrameBytes : 1024 * 1024;
    public const int MaxFragments = 256;
    public static TimeSpan AssemblyTimeout => TimeSpan.FromSeconds(MpLimits.IoTimeoutSeconds);
    public const int MaxDepth = 32;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private sealed class ClientPacketFrame
    {
        public ClientPacketFrame() { }
        [JsonPropertyName("frame")]
        public string? Frame { get; set; }
        [JsonPropertyName("protocol")]
        public string? Protocol { get; set; }
        [JsonPropertyName("version")]
        public int Version { get; set; }
        [JsonPropertyName("sequence")]
        public long Sequence { get; set; }
        [JsonPropertyName("runId")]
        public Guid RunId { get; set; }
        [JsonPropertyName("message")]
        public MpMessage? Message { get; set; }
    }

    private sealed class ServerPacketFrame
    {
        public ServerPacketFrame() { }
        [JsonPropertyName("frame")]
        public string? Frame { get; set; }
        [JsonPropertyName("protocol")]
        public string? Protocol { get; set; }
        [JsonPropertyName("version")]
        public int Version { get; set; }
        [JsonPropertyName("sequence")]
        public long Sequence { get; set; }
        [JsonPropertyName("runId")]
        public Guid RunId { get; set; }
        [JsonPropertyName("message")]
        public MpMessage? Message { get; set; }
        [JsonPropertyName("roomId")]
        public Guid RoomId { get; set; }
        [JsonPropertyName("senderId")]
        public Guid SenderId { get; set; }
        [JsonPropertyName("isHost")]
        public bool IsHost { get; set; }
    }

    private sealed class HandshakeRequestFrame
    {
        public HandshakeRequestFrame() { }
        [JsonPropertyName("frame")]
        public string? Frame { get; set; }
        [JsonPropertyName("protocol")]
        public string? Protocol { get; set; }
        [JsonPropertyName("version")]
        public int Version { get; set; }
        [JsonPropertyName("capabilities")]
        public string[]? Capabilities { get; set; }
        [JsonPropertyName("clientNonce")]
        public string? ClientNonce { get; set; }
    }

    private sealed class HandshakeResponseFrame
    {
        public HandshakeResponseFrame() { }
        [JsonPropertyName("frame")]
        public string? Frame { get; set; }
        [JsonPropertyName("protocol")]
        public string? Protocol { get; set; }
        [JsonPropertyName("version")]
        public int Version { get; set; }
        [JsonPropertyName("capabilities")]
        public string[]? Capabilities { get; set; }
        [JsonPropertyName("clientNonce")]
        public string? ClientNonce { get; set; }
        [JsonPropertyName("roomId")]
        public Guid RoomId { get; set; }
        [JsonPropertyName("peerId")]
        public Guid PeerId { get; set; }
        [JsonPropertyName("hostId")]
        public Guid HostId { get; set; }
        [JsonPropertyName("roomCode")]
        public string? RoomCode { get; set; }
        [JsonPropertyName("joinToken")]
        public string? JoinToken { get; set; }
    }

    private sealed class ControlFrame
    {
        public ControlFrame() { }
        [JsonPropertyName("frame")]
        public string? Frame { get; set; }
        [JsonPropertyName("protocol")]
        public string? Protocol { get; set; }
        [JsonPropertyName("version")]
        public int Version { get; set; }
        [JsonPropertyName("control")]
        public string? Control { get; set; }
        [JsonPropertyName("roomId")]
        public Guid RoomId { get; set; }
        [JsonPropertyName("peerId")]
        public Guid PeerId { get; set; }
        [JsonPropertyName("sequence")]
        public long Sequence { get; set; }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static typeInfo =>
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object) return;
            foreach (var property in typeInfo.Properties)
                property.IsRequired = true;
        });
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = MaxDepth,
            // Do not accept NaN/Infinity spellings or other non-standard JSON numbers.
            NumberHandling = JsonNumberHandling.Strict,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            TypeInfoResolver = resolver,
        };
    }

    public static string CreateClientNonce()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public static byte[] EncodeHandshakeRequest(string clientNonce)
    {
        if (!IsNonce(clientNonce)) throw new ArgumentException("Invalid handshake nonce.", nameof(clientNonce));
        var frame = new HandshakeRequestFrame
        {
            Frame = HandshakeKind,
            Protocol = MpLimits.ProtocolName,
            Version = MpLimits.ProtocolVersion,
            Capabilities = Capabilities.ToArray(),
            ClientNonce = clientNonce,
        };
        return Serialize(frame);
    }

    public static byte[] EncodeHandshakeResponse(RelayHandshakeResponse response)
    {
        if (!IsValidHandshakeResponse(response)) throw new ArgumentException("Invalid handshake response.", nameof(response));
        var frame = new HandshakeResponseFrame
        {
            Frame = HandshakeKind,
            Protocol = response.Protocol,
            Version = response.Version,
            Capabilities = response.Capabilities,
            RoomId = response.RoomId,
            PeerId = response.PeerId,
            HostId = response.HostId,
            RoomCode = response.RoomCode,
            ClientNonce = response.ClientNonce,
            JoinToken = response.JoinToken,
        };
        return Serialize(frame);
    }

    public static bool TryDecodeHandshakeRequest(ReadOnlySpan<byte> bytes, out RelayHandshakeRequest request)
    {
        request = default!;
        if (!HasExactProperties(bytes, FrameShape.HandshakeRequest) ||
            !HasRequiredProperties(bytes, FrameShape.HandshakeRequest) ||
            !TryDeserialize(bytes, out HandshakeRequestFrame? frame) || frame is null ||
            frame.Frame != HandshakeKind || frame.Protocol is null || frame.Capabilities is null ||
            frame.ClientNonce is null)
            return false;

        request = new RelayHandshakeRequest(frame.Protocol, frame.Version, frame.Capabilities, frame.ClientNonce);
        return IsValidHandshakeRequest(request);
    }

    public static bool TryDecodeHandshakeResponse(ReadOnlySpan<byte> bytes, out RelayHandshakeResponse response)
    {
        response = default!;
        if (!HasExactProperties(bytes, FrameShape.HandshakeResponse) ||
            !HasRequiredProperties(bytes, FrameShape.HandshakeResponse) ||
            !TryDeserialize(bytes, out HandshakeResponseFrame? frame) || frame is null ||
            frame.Frame != HandshakeKind || frame.Protocol is null || frame.Capabilities is null ||
            frame.RoomCode is null || frame.ClientNonce is null)
            return false;

        response = new RelayHandshakeResponse(frame.Protocol, frame.Version, frame.Capabilities,
            frame.RoomId, frame.PeerId, frame.HostId, frame.RoomCode, frame.ClientNonce, frame.JoinToken);
        return IsValidHandshakeResponse(response);
    }

    public static bool IsValidHandshakeRequest(RelayHandshakeRequest request)
        => request.Protocol == MpLimits.ProtocolName &&
           request.Version == MpLimits.ProtocolVersion &&
           HasExactCapabilities(request.Capabilities) &&
           IsNonce(request.ClientNonce);

    public static bool IsValidHandshakeResponse(RelayHandshakeResponse response)
        => response.Protocol == MpLimits.ProtocolName &&
           response.Version == MpLimits.ProtocolVersion &&
           HasExactCapabilities(response.Capabilities) &&
           response.RoomId != Guid.Empty && response.PeerId != Guid.Empty &&
           response.HostId != Guid.Empty && IsRoomCode(response.RoomCode) &&
           IsNonce(response.ClientNonce) &&
           // The join secret is host-only: a peer that receives one, or a host that receives a
           // malformed one, is talking to something that is not this relay.
           (response.PeerId == response.HostId
               ? HostGrantFormat.IsSecret(response.JoinToken)
               : response.JoinToken is null);

    public static bool HasExactCapabilities(IReadOnlyList<string>? capabilities)
    {
        if (capabilities is null || capabilities.Count != Capabilities.Count) return false;
        for (var i = 0; i < Capabilities.Count; i++)
            if (capabilities[i] != Capabilities[i]) return false;
        return true;
    }

    public static byte[] EncodeClientPacket(ClientPacket packet)
    {
        ValidateClientPacketShape(packet);
        return Serialize(new ClientPacketFrame
        {
            Frame = PacketKind,
            Protocol = MpLimits.ProtocolName,
            Version = packet.Version,
            Sequence = packet.Sequence,
            RunId = packet.RunId,
            Message = packet.Message,
        });
    }

    public static bool TryDecodeClientPacket(ReadOnlySpan<byte> bytes, out ClientPacket packet)
    {
        packet = default!;
        if (!HasExactProperties(bytes, FrameShape.ClientPacket) ||
            !HasRequiredProperties(bytes, FrameShape.ClientPacket) ||
            !TryDeserialize(bytes, out ClientPacketFrame? frame) || frame is null ||
            frame.Frame != PacketKind || frame.Protocol != MpLimits.ProtocolName ||
            frame.Message is null)
            return false;

        packet = new ClientPacket(frame.Version, frame.Sequence, frame.RunId, frame.Message);
        return IsValidPacketShape(packet);
    }

    public static byte[] EncodeServerPacket(ReceivedPacket packet)
    {
        if (packet.RoomId == Guid.Empty || packet.SenderId == Guid.Empty)
            throw new ArgumentException("Server packet identity is required.", nameof(packet));
        if (packet.IsHost && packet.SenderId == Guid.Empty)
            throw new ArgumentException("Host identity is required.", nameof(packet));
        ValidateClientPacketShape(new ClientPacket(MpLimits.ProtocolVersion, packet.Sequence, packet.RunId, packet.Message));
        return Serialize(new ServerPacketFrame
        {
            Frame = PacketKind,
            Protocol = MpLimits.ProtocolName,
            Version = MpLimits.ProtocolVersion,
            Sequence = packet.Sequence,
            RunId = packet.RunId,
            Message = packet.Message,
            RoomId = packet.RoomId,
            SenderId = packet.SenderId,
            IsHost = packet.IsHost,
        });
    }

    public static bool TryDecodeServerPacket(ReadOnlySpan<byte> bytes, out ReceivedPacket packet)
    {
        packet = default!;
        if (!HasExactProperties(bytes, FrameShape.ServerPacket) ||
            !HasRequiredProperties(bytes, FrameShape.ServerPacket) ||
            !TryDeserialize(bytes, out ServerPacketFrame? frame) || frame is null ||
            frame.Frame != PacketKind || frame.Protocol != MpLimits.ProtocolName ||
            frame.Message is null || frame.RoomId == Guid.Empty || frame.SenderId == Guid.Empty)
            return false;

        packet = new ReceivedPacket(frame.RoomId, frame.SenderId, frame.IsHost,
            frame.Sequence, frame.RunId, frame.Message);
        return IsValidPacketShape(new ClientPacket(frame.Version, frame.Sequence, frame.RunId, frame.Message));
    }

    public static byte[] EncodeControl(Guid roomId, RelayControlKind kind, Guid peerId, long sequence)
    {
        if (kind == RelayControlKind.Heartbeat)
        {
            if (roomId != Guid.Empty || peerId != Guid.Empty)
                throw new ArgumentException("Heartbeat identity must be empty.");
        }
        else if (roomId == Guid.Empty ||
                 (kind is (RelayControlKind.PeerJoined or RelayControlKind.PeerLeft) && peerId == Guid.Empty) ||
                 (kind is (RelayControlKind.HeartbeatAck or RelayControlKind.RoomClosed) && peerId != Guid.Empty))
        {
            throw new ArgumentException("Control identity is incomplete.");
        }
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        return Serialize(new ControlFrame
        {
            Frame = ControlKind,
            Protocol = MpLimits.ProtocolName,
            Version = MpLimits.ProtocolVersion,
            Control = ToWireControl(kind),
            RoomId = roomId,
            PeerId = peerId,
            Sequence = sequence,
        });
    }

    public static bool TryDecodeControl(ReadOnlySpan<byte> bytes, out RelayControlFrame control)
    {
        control = default;
        if (!HasExactProperties(bytes, FrameShape.Control) ||
            !HasRequiredProperties(bytes, FrameShape.Control) ||
            !TryDeserialize(bytes, out ControlFrame? frame) || frame is null ||
            frame.Frame != ControlKind || frame.Protocol != MpLimits.ProtocolName ||
            frame.Version != MpLimits.ProtocolVersion || frame.Sequence <= 0 ||
            !TryParseWireControl(frame.Control, out var kind))
            return false;

        if (kind == RelayControlKind.Heartbeat)
        {
            if (frame.RoomId != Guid.Empty || frame.PeerId != Guid.Empty) return false;
        }
        else if (frame.RoomId == Guid.Empty ||
                 (kind is (RelayControlKind.PeerJoined or RelayControlKind.PeerLeft) && frame.PeerId == Guid.Empty) ||
                 (kind is (RelayControlKind.HeartbeatAck or RelayControlKind.RoomClosed) && frame.PeerId != Guid.Empty))
            return false;

        control = new RelayControlFrame(kind, frame.RoomId, frame.PeerId, frame.Sequence);
        return true;
    }

    /// <summary>Reads only the discriminator, after enforcing a strict JSON root object.</summary>
    public static bool TryGetFrameType(ReadOnlySpan<byte> bytes, out string? frameType)
    {
        frameType = null;
        if (bytes.Length == 0 || bytes.Length > MaxJsonBytes) return false;
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                MaxDepth = MaxDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            using var document = JsonDocument.ParseValue(ref reader);
            if (reader.Read()) return false;
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("frame", out var value) ||
                value.ValueKind != JsonValueKind.String)
                return false;
            frameType = value.GetString();
            return frameType is PacketKind or ControlKind or HandshakeKind;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private enum FrameShape
    {
        ClientPacket,
        ServerPacket,
        HandshakeRequest,
        HandshakeResponse,
        Control,
    }

    private static bool HasExactProperties(ReadOnlySpan<byte> bytes, FrameShape shape)
    {
        if (bytes.Length == 0 || bytes.Length > MaxJsonBytes) return false;
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                MaxDepth = MaxDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            using var document = JsonDocument.ParseValue(ref reader);
            if (reader.Read()) return false;
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name) || !IsAllowedProperty(property.Name, shape)) return false;
            }
            return HasNoDuplicateProperties(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool HasRequiredProperties(ReadOnlySpan<byte> bytes, FrameShape shape)
    {
        if (bytes.Length == 0 || bytes.Length > MaxJsonBytes) return false;
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                MaxDepth = MaxDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            using var document = JsonDocument.ParseValue(ref reader);
            if (reader.Read()) return false;
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var required = shape switch
            {
                FrameShape.ClientPacket => new[] { "frame", "protocol", "version", "sequence", "runId", "message" },
                FrameShape.ServerPacket => new[] { "frame", "protocol", "version", "sequence", "runId", "message", "roomId", "senderId", "isHost" },
                FrameShape.HandshakeRequest => new[] { "frame", "protocol", "version", "capabilities", "clientNonce" },
                FrameShape.HandshakeResponse => new[] { "frame", "protocol", "version", "capabilities", "clientNonce", "roomId", "peerId", "hostId", "roomCode", "joinToken" },
                FrameShape.Control => new[] { "frame", "protocol", "version", "control", "roomId", "peerId", "sequence" },
                _ => Array.Empty<string>(),
            };
            foreach (var property in required)
                if (!document.RootElement.TryGetProperty(property, out _)) return false;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsAllowedProperty(string name, FrameShape shape) => shape switch
    {
        FrameShape.ClientPacket => name is "frame" or "protocol" or "version" or "sequence" or "runId" or "message",
        FrameShape.ServerPacket => name is "frame" or "protocol" or "version" or "sequence" or "runId" or "message"
            or "roomId" or "senderId" or "isHost",
        FrameShape.HandshakeRequest => name is "frame" or "protocol" or "version" or "capabilities" or "clientNonce",
        FrameShape.HandshakeResponse => name is "frame" or "protocol" or "version" or "capabilities" or "clientNonce"
            or "roomId" or "peerId" or "hostId" or "roomCode" or "joinToken",
        FrameShape.Control => name is "frame" or "protocol" or "version" or "control" or "roomId" or "peerId" or "sequence",
        _ => false,
    };

    private static bool HasNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name) || !HasNoDuplicateProperties(property.Value))
                    return false;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (!HasNoDuplicateProperties(item)) return false;
        }
        return true;
    }

    public static bool IsValidPacketShape(ClientPacket packet)
    {
        if (packet.Version != MpLimits.ProtocolVersion || packet.Sequence <= 0 || packet.Message is null)
            return false;
        if (packet.Message is IRunMessage)
            return packet.RunId != Guid.Empty;
        return packet.RunId == Guid.Empty;
    }

    public static void ValidateClientPacketShape(ClientPacket packet)
    {
        if (!IsValidPacketShape(packet)) throw new ArgumentException("Invalid packet envelope.", nameof(packet));
    }

    public static bool IsRoomCode(string? code)
    {
        if (code is null || code.Length != 6) return false;
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        foreach (var c in code)
            if (alphabet.IndexOf(c) < 0) return false;
        return true;
    }

    private static bool IsNonce(string? nonce)
    {
        if (nonce is null || nonce.Length != 32) return false;
        try
        {
            _ = Convert.FromHexString(nonce);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ToWireControl(RelayControlKind kind) => kind switch
    {
        RelayControlKind.Heartbeat => "heartbeat",
        RelayControlKind.HeartbeatAck => "heartbeat-ack",
        RelayControlKind.PeerJoined => "peer-joined",
        RelayControlKind.PeerLeft => "peer-left",
        RelayControlKind.RoomClosed => "room-closed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static bool TryParseWireControl(string? value, out RelayControlKind kind)
    {
        kind = value switch
        {
            "heartbeat" => RelayControlKind.Heartbeat,
            "heartbeat-ack" => RelayControlKind.HeartbeatAck,
            "peer-joined" => RelayControlKind.PeerJoined,
            "peer-left" => RelayControlKind.PeerLeft,
            "room-closed" => RelayControlKind.RoomClosed,
            _ => default,
        };
        return value is "heartbeat" or "heartbeat-ack" or "peer-joined" or "peer-left" or "room-closed";
    }

    private static byte[] Serialize<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > MaxJsonBytes)
            throw new InvalidDataException("Wire frame exceeds the protocol size limit.");
        return bytes;
    }

    private static bool TryDeserialize<T>(ReadOnlySpan<byte> bytes, out T? value) where T : class
    {
        value = default;
        if (bytes.Length == 0 || bytes.Length > MaxJsonBytes) return false;
        try
        {
            // Decode once with a strict UTF-8 decoder before handing bytes to STJ.  The default
            // reader replaces malformed sequences, which could otherwise make two peers parse
            // different text than the relay's admission check.
            _ = StrictUtf8.GetString(bytes);
            value = JsonSerializer.Deserialize<T>(bytes, JsonOptions);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
