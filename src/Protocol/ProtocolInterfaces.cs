using System;
using System.Collections.Generic;

namespace TianshuQitanLauncher.Protocol
{
    public interface IFrameDecoder
    {
        ProtocolDefinition Definition { get; }
        bool TryDecode(
            TrafficDirection direction,
            byte[] buffer,
            int offset,
            int count,
            long streamOffset,
            out ProtocolFrame frame,
            out int consumed);
    }

    public interface IRuleEngine
    {
        IList<PacketRule> Rules { get; }
        RuleDecision Evaluate(PacketContext context, ProtocolFrame frame);
        void Reload();
    }

    public interface IStateTracker
    {
        string GetCurrentState(long connectionId);
        StateTransition Accept(ProtocolFrame frame);
        StateTransition AcceptConnectionEvent(long connectionId, string eventName);
        IList<ObservedTransition> GetObservedTransitions();
    }

    public interface IScenarioHost
    {
        bool IsLoaded { get; }
        RuleDecision EvaluatePacket(PacketContext context, ProtocolFrame frame);
        ScenarioResult Run(string scenarioName);
        void Reload();
    }

    public interface ICaptureStore : IDisposable
    {
        string DatabasePath { get; }
        long SessionId { get; }
        long StartSession(string profileName, bool activeMode);
        void EnqueueConnection(ConnectionSession connection);
        void EnqueueChunk(TransportChunk chunk);
        void EnqueueFrame(ProtocolFrame frame);
        void EnqueueStateTransition(StateTransition transition);
        void EnqueueEvent(WorkbenchEvent workbenchEvent);
        void EnqueueScenarioResult(ScenarioResult result);
        void EnqueueOperationRun(AtomicOperationRun run);
        void EnqueueOperationStep(OperationStep step);
        void EnqueueOperationConnection(OperationConnectionLink link);
        void EnqueueOperationChunk(OperationChunkLink link);
        void EnqueueOperationFrame(OperationFrameLink link);
        void EnqueueOperationState(OperationStateLink link);
        void EnqueueFrameAnnotation(FrameSemanticAnnotation annotation);
        void EnqueueFieldAnnotation(FieldSemanticAnnotation annotation);
        void EnqueueOperationAttachment(OperationAttachment attachment);
        void EnqueueOperationComparison(OperationComparison comparison);
        IList<TransportChunk> ReadChunks(int maximumCount);
        void Flush();
        void CompleteSession();
    }

    public interface IAtomicOperationRecorder
    {
        OperationRecordingState State { get; }
        AtomicOperationDefinition SelectedDefinition { get; }
        AtomicOperationRun ActiveRun { get; }
        void SetEnabled(bool enabled);
        void SelectDefinition(AtomicOperationDefinition definition);
        AtomicOperationRun Start();
        OperationStep MarkStep(string name, string description);
        AtomicOperationRun Stop(OperationOutcome outcome, string actualResult, string notes);
        AtomicOperationRun Discard(string reason);
    }

    public interface IOperationRepository
    {
        IList<AtomicOperationDefinition> GetDefinitions();
        void SaveDefinition(AtomicOperationDefinition definition);
        void DeleteDefinition(string definitionId);
        SessionDatabaseRenameResult RenameSessionDatabase(string sourceDatabasePath, string semanticName);
        IList<AtomicOperationRun> GetRuns(string definitionId);
        IList<OperationStep> GetSteps(AtomicOperationRun run);
        IList<ConnectionSession> GetConnections(AtomicOperationRun run);
        IList<OperationConnectionCandidate> GetConnectionCandidates(AtomicOperationRun run);
        IList<OperationFrameSample> GetFrames(AtomicOperationRun run, bool includeNoise);
        IList<FieldSemanticAnnotation> GetFieldAnnotations(AtomicOperationRun run, long frameId);
        void SaveRun(AtomicOperationRun run);
        void SaveStep(AtomicOperationRun run, OperationStep step);
        void SaveFrameAnnotation(AtomicOperationRun run, FrameSemanticAnnotation annotation);
        void SaveFieldAnnotation(AtomicOperationRun run, FieldSemanticAnnotation annotation);
        void SaveAttachment(AtomicOperationRun run, OperationAttachment attachment);
        void SetConnectionIncluded(AtomicOperationRun run, long connectionId, bool included);
    }

    public interface IOperationAnalyzer
    {
        OperationComparison Compare(IList<AtomicOperationRun> runs);
    }

    public interface IOperationBundleExporter
    {
        void Export(IList<AtomicOperationRun> runs, OperationComparison comparison, string destinationPath);
    }

    public interface IPcapngExporter
    {
        void Export(string databasePath, string destinationPath);
        void WriteWiresharkDissector(string destinationPath);
    }

    public interface ITransportCaptureEngine : IDisposable
    {
        bool IsRunning { get; }
        void Start();
        void Stop();
        bool Send(long connectionId, byte[] bytes);
        bool InjectReceive(long connectionId, byte[] bytes);
    }
}
