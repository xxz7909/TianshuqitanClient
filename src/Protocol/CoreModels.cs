using System;
using System.Collections.Generic;

namespace TianshuQitanLauncher.Protocol
{
    public enum TrafficDirection
    {
        ClientToServer = 0,
        ServerToClient = 1
    }

    public enum ConnectionKind
    {
        Unknown = 0,
        FlashPolicy = 1,
        HttpProxy = 2,
        Game = 3
    }

    public enum TransportOperation
    {
        Socket = 0,
        Connect = 1,
        Send = 2,
        Receive = 3,
        SendTo = 4,
        ReceiveFrom = 5,
        Close = 6,
        EventSelect = 7,
        CaptureGap = 8,
        Inject = 9
    }

    public enum FrameStatus
    {
        Raw = 0,
        Decoded = 1,
        Incomplete = 2,
        Invalid = 3
    }

    public enum RuleAction
    {
        Pass = 0,
        Drop = 1,
        Delay = 2,
        Replace = 3,
        Duplicate = 4,
        Inject = 5
    }

    public enum ScenarioStatus
    {
        NotRun = 0,
        Passed = 1,
        Failed = 2,
        TimedOut = 3,
        Error = 4
    }

    public enum OperationRecordingState
    {
        Disabled = 0,
        Ready = 1,
        Recording = 2
    }

    public enum OperationRunStatus
    {
        Recording = 0,
        Completed = 1,
        Discarded = 2,
        Interrupted = 3
    }

    public enum OperationOutcome
    {
        Unknown = 0,
        Succeeded = 1,
        Failed = 2
    }

    public enum FrameSemanticRole
    {
        Unknown = 0,
        Request = 1,
        Response = 2,
        ServerPush = 3,
        Noise = 4
    }

    public enum ComparisonPresence
    {
        Required = 0,
        Optional = 1,
        Unique = 2,
        Reordered = 3
    }

    public sealed class ConnectionSession
    {
        public long Id { get; set; }
        public long SessionId { get; set; }
        public IntPtr SocketHandle { get; set; }
        public string LocalEndPoint { get; set; }
        public string RemoteEndPoint { get; set; }
        public int RemotePort { get; set; }
        public ConnectionKind Kind { get; set; }
        public DateTime OpenedUtc { get; set; }
        public DateTime? ClosedUtc { get; set; }
        public string State { get; set; }
        public int LastError { get; set; }
        public long ClientStreamOffset { get; set; }
        public long ServerStreamOffset { get; set; }

        public ConnectionSession Clone()
        {
            return (ConnectionSession)MemberwiseClone();
        }
    }

    public sealed class TransportChunk
    {
        public long Id { get; set; }
        public long SessionId { get; set; }
        public long ConnectionId { get; set; }
        public DateTime TimestampUtc { get; set; }
        public TrafficDirection Direction { get; set; }
        public TransportOperation Operation { get; set; }
        public long StreamOffset { get; set; }
        public int ThreadId { get; set; }
        public int NativeResult { get; set; }
        public int NativeError { get; set; }
        public byte[] OriginalBytes { get; set; }
        public byte[] EffectiveBytes { get; set; }
        public RuleAction RuleAction { get; set; }
        public string RuleId { get; set; }
        public string Note { get; set; }
        public long CaptureOrdinal { get; set; }

        public TransportChunk()
        {
            OriginalBytes = new byte[0];
            EffectiveBytes = new byte[0];
            RuleAction = RuleAction.Pass;
        }

        public int Length
        {
            get { return EffectiveBytes == null ? 0 : EffectiveBytes.Length; }
        }
    }

    public sealed class DecodedField
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public int Offset { get; set; }
        public int Length { get; set; }
        public string Value { get; set; }
        public string DisplayValue { get; set; }
    }

    public sealed class ProtocolFrame
    {
        public long Id { get; set; }
        public long SessionId { get; set; }
        public long ConnectionId { get; set; }
        public long FirstChunkId { get; set; }
        public DateTime TimestampUtc { get; set; }
        public TrafficDirection Direction { get; set; }
        public long StreamOffset { get; set; }
        public byte[] Bytes { get; set; }
        public long? Opcode { get; set; }
        public string Name { get; set; }
        public FrameStatus Status { get; set; }
        public string ParseError { get; set; }
        public IList<DecodedField> Fields { get; set; }
        public long CaptureOrdinal { get; set; }

        public ProtocolFrame()
        {
            Bytes = new byte[0];
            Fields = new List<DecodedField>();
            Status = FrameStatus.Raw;
        }

        public string Signature
        {
            get
            {
                string opcode = Opcode.HasValue ? Opcode.Value.ToString("X") : "raw";
                return Direction + ":" + opcode + ":" + (Bytes == null ? 0 : Bytes.Length);
            }
        }
    }

    public sealed class PacketContext
    {
        public long SessionId { get; set; }
        public ConnectionSession Connection { get; set; }
        public TrafficDirection Direction { get; set; }
        public string State { get; set; }
        public DateTime TimestampUtc { get; set; }
        public bool ActiveMode { get; set; }
    }

    public sealed class RuleDecision
    {
        public RuleAction Action { get; set; }
        public byte[] Bytes { get; set; }
        public int DelayMs { get; set; }
        public string RuleId { get; set; }
        public string Reason { get; set; }
        public bool Continue { get; set; }
        public bool TimedOut { get; set; }

        public static RuleDecision Pass(byte[] bytes)
        {
            return new RuleDecision { Action = RuleAction.Pass, Bytes = bytes ?? new byte[0] };
        }
    }

    public sealed class StateTransition
    {
        public long SessionId { get; set; }
        public long ConnectionId { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string FromState { get; set; }
        public string ToState { get; set; }
        public string Trigger { get; set; }
        public bool Confirmed { get; set; }
        public long CaptureOrdinal { get; set; }
    }

    public sealed class ObservedTransition
    {
        public string FromSignature { get; set; }
        public string ToSignature { get; set; }
        public long Count { get; set; }
    }

    public sealed class ScenarioResult
    {
        public string Name { get; set; }
        public ScenarioStatus Status { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime FinishedUtc { get; set; }
        public string Message { get; set; }
        public IList<string> Log { get; set; }

        public ScenarioResult()
        {
            Log = new List<string>();
            Status = ScenarioStatus.NotRun;
        }
    }

    public sealed class WorkbenchEvent
    {
        public DateTime TimestampUtc { get; set; }
        public string Category { get; set; }
        public string Level { get; set; }
        public string Message { get; set; }
        public long? ConnectionId { get; set; }
    }

    public sealed class OfflineReplayResult
    {
        public int ChunkCount { get; set; }
        public int FrameCount { get; set; }
        public int IncompleteStreamCount { get; set; }
        public string SuggestedFraming { get; set; }
        public double BigEndianLengthPrefixConfidence { get; set; }
        public IDictionary<string, long> SignatureCounts { get; set; }
        public IList<ObservedTransition> ObservedTransitions { get; set; }

        public OfflineReplayResult()
        {
            SignatureCounts = new Dictionary<string, long>();
            ObservedTransitions = new List<ObservedTransition>();
        }
    }

    public sealed class AtomicOperationDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Module { get; set; }
        public string Purpose { get; set; }
        public string Preconditions { get; set; }
        public string ExpectedResult { get; set; }
        public IList<string> Tags { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public AtomicOperationDefinition()
        {
            Id = Guid.NewGuid().ToString("D");
            Tags = new List<string>();
            UpdatedUtc = DateTime.UtcNow;
        }

        public AtomicOperationDefinition Clone()
        {
            return new AtomicOperationDefinition
            {
                Id = Id,
                Name = Name,
                Module = Module,
                Purpose = Purpose,
                Preconditions = Preconditions,
                ExpectedResult = ExpectedResult,
                Tags = new List<string>(Tags ?? new List<string>()),
                UpdatedUtc = UpdatedUtc
            };
        }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(Name) ? Id : Name;
        }
    }

    public sealed class AtomicOperationRun
    {
        public string Id { get; set; }
        public long SessionId { get; set; }
        public string DefinitionId { get; set; }
        public string DefinitionSnapshotJson { get; set; }
        public string SourceDatabasePath { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? StoppedUtc { get; set; }
        public long StartBoundaryOrdinal { get; set; }
        public long? EndBoundaryOrdinal { get; set; }
        public OperationRunStatus Status { get; set; }
        public OperationOutcome Outcome { get; set; }
        public string StartState { get; set; }
        public string EndState { get; set; }
        public string ActualResult { get; set; }
        public string Notes { get; set; }

        public AtomicOperationRun()
        {
            Id = Guid.NewGuid().ToString("D");
            Status = OperationRunStatus.Recording;
            Outcome = OperationOutcome.Unknown;
        }
    }

    public sealed class OperationStep
    {
        public string Id { get; set; }
        public string RunId { get; set; }
        public int Sequence { get; set; }
        public long BoundaryOrdinal { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }

        public OperationStep()
        {
            Id = Guid.NewGuid().ToString("D");
        }
    }

    public sealed class OperationConnectionLink
    {
        public string RunId { get; set; }
        public long ConnectionId { get; set; }
        public bool Automatic { get; set; }
        public bool Included { get; set; }
    }

    public sealed class OperationConnectionCandidate
    {
        public ConnectionSession Connection { get; set; }
        public bool Included { get; set; }
        public bool Automatic { get; set; }
    }

    public sealed class OperationChunkLink
    {
        public string RunId { get; set; }
        public long ChunkId { get; set; }
        public long CaptureOrdinal { get; set; }
    }

    public sealed class OperationFrameLink
    {
        public string RunId { get; set; }
        public long FrameId { get; set; }
        public long CaptureOrdinal { get; set; }
    }

    public sealed class OperationStateLink
    {
        public string RunId { get; set; }
        public long ConnectionId { get; set; }
        public long CaptureOrdinal { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string FromState { get; set; }
        public string ToState { get; set; }
        public string Trigger { get; set; }
    }

    public sealed class FrameSemanticAnnotation
    {
        public string RunId { get; set; }
        public long FrameId { get; set; }
        public FrameSemanticRole Role { get; set; }
        public string Meaning { get; set; }
        public bool Confirmed { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public sealed class FieldSemanticAnnotation
    {
        public string RunId { get; set; }
        public long FrameId { get; set; }
        public string FieldName { get; set; }
        public int Offset { get; set; }
        public int Length { get; set; }
        public string SemanticName { get; set; }
        public string Meaning { get; set; }
        public string ValueType { get; set; }
        public string DynamicKind { get; set; }
        public string Notes { get; set; }
        public bool Confirmed { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public sealed class OperationAttachment
    {
        public string Id { get; set; }
        public string RunId { get; set; }
        public string StepId { get; set; }
        public string FileName { get; set; }
        public string MediaType { get; set; }
        public byte[] Bytes { get; set; }
        public DateTime CreatedUtc { get; set; }

        public OperationAttachment()
        {
            Id = Guid.NewGuid().ToString("D");
            Bytes = new byte[0];
        }
    }

    public sealed class OperationFrameSample
    {
        public string RunId { get; set; }
        public string SourceDatabasePath { get; set; }
        public long FrameId { get; set; }
        public int Sequence { get; set; }
        public TrafficDirection Direction { get; set; }
        public long? Opcode { get; set; }
        public string Name { get; set; }
        public byte[] Bytes { get; set; }
        public FrameSemanticRole Role { get; set; }
        public string Meaning { get; set; }
        public bool Confirmed { get; set; }
        public IList<DecodedField> Fields { get; set; }

        public OperationFrameSample()
        {
            Bytes = new byte[0];
            Fields = new List<DecodedField>();
        }

        public string Signature
        {
            get
            {
                return ((int)Direction) + ":" + (Opcode.HasValue ? Opcode.Value.ToString("X") : "raw") + ":" +
                    (Name ?? string.Empty) + ":" + (Bytes == null ? 0 : Bytes.Length);
            }
        }
    }

    public sealed class OperationComparisonFrame
    {
        public int Sequence { get; set; }
        public string Signature { get; set; }
        public TrafficDirection Direction { get; set; }
        public long? Opcode { get; set; }
        public string Name { get; set; }
        public int Length { get; set; }
        public ComparisonPresence Presence { get; set; }
        public int PresentRunCount { get; set; }
        public bool Reordered { get; set; }
        public string StableMaskHex { get; set; }
        public string DynamicMaskHex { get; set; }
        public string CandidateKinds { get; set; }
        public IList<string> RunIds { get; set; }

        public OperationComparisonFrame()
        {
            RunIds = new List<string>();
        }
    }

    public sealed class OperationComparison
    {
        public string Id { get; set; }
        public string DefinitionId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public IList<string> RunIds { get; set; }
        public IList<OperationComparisonFrame> Frames { get; set; }

        public OperationComparison()
        {
            Id = Guid.NewGuid().ToString("D");
            CreatedUtc = DateTime.UtcNow;
            RunIds = new List<string>();
            Frames = new List<OperationComparisonFrame>();
        }
    }

    public sealed class OperationCatalogDocument
    {
        public int Version { get; set; }
        public IList<AtomicOperationDefinition> Operations { get; set; }

        public OperationCatalogDocument()
        {
            Version = 1;
            Operations = new List<AtomicOperationDefinition>();
        }
    }

    public sealed class SessionDatabaseRenameResult
    {
        public string OriginalPath { get; set; }
        public string NewPath { get; set; }
        public bool CurrentSession { get; set; }
    }
}
