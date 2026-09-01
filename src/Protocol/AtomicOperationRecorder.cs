using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace TianshuQitanLauncher.Protocol
{
    public sealed class AtomicOperationRecorder : IAtomicOperationRecorder
    {
        private readonly object gate = new object();
        private readonly ICaptureStore store;
        private readonly Func<string> stateSnapshot;
        private readonly HashSet<long> linkedConnections = new HashSet<long>();
        private OperationRecordingState state;
        private AtomicOperationDefinition selectedDefinition;
        private AtomicOperationRun activeRun;
        private long captureOrdinal;
        private int nextStepSequence;

        public AtomicOperationRecorder(ICaptureStore store, bool enabled, Func<string> stateSnapshot)
        {
            if (store == null) throw new ArgumentNullException("store");
            this.store = store;
            this.stateSnapshot = stateSnapshot;
            state = enabled ? OperationRecordingState.Ready : OperationRecordingState.Disabled;
        }

        public event Action<OperationRecordingState> StateChanged;
        public event Action<AtomicOperationRun> RunChanged;
        public event Action<OperationStep> StepAdded;

        public OperationRecordingState State
        {
            get { lock (gate) { return state; } }
        }

        public AtomicOperationDefinition SelectedDefinition
        {
            get { lock (gate) { return selectedDefinition == null ? null : selectedDefinition.Clone(); } }
        }

        public AtomicOperationRun ActiveRun
        {
            get { lock (gate) { return CloneRun(activeRun); } }
        }

        public long CurrentCaptureOrdinal
        {
            get { lock (gate) { return captureOrdinal; } }
        }

        public void SetEnabled(bool enabled)
        {
            AtomicOperationRun stopped = null;
            OperationRecordingState newState;
            lock (gate)
            {
                if (!enabled && activeRun != null)
                {
                    stopped = FinishLocked(OperationRunStatus.Completed, OperationOutcome.Unknown, null,
                        "Recorder was disabled while this operation was active.");
                }
                state = enabled ? OperationRecordingState.Ready : OperationRecordingState.Disabled;
                newState = state;
            }
            if (stopped != null) RaiseRunChanged(stopped);
            RaiseStateChanged(newState);
        }

        public void SelectDefinition(AtomicOperationDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException("definition");
            lock (gate)
            {
                if (activeRun != null)
                {
                    throw new InvalidOperationException("Cannot change the operation template while recording.");
                }
                selectedDefinition = definition.Clone();
            }
        }

        public AtomicOperationRun Start()
        {
            AtomicOperationRun result;
            lock (gate)
            {
                if (state == OperationRecordingState.Disabled)
                {
                    throw new InvalidOperationException("The atomic operation recorder is disabled.");
                }
                if (activeRun != null)
                {
                    throw new InvalidOperationException("An atomic operation is already being recorded.");
                }
                if (selectedDefinition == null)
                {
                    throw new InvalidOperationException("Select or create an operation template before recording.");
                }

                activeRun = new AtomicOperationRun
                {
                    SessionId = store.SessionId,
                    SourceDatabasePath = store.DatabasePath,
                    DefinitionId = selectedDefinition.Id,
                    DefinitionSnapshotJson = JsonConvert.SerializeObject(selectedDefinition),
                    StartedUtc = DateTime.UtcNow,
                    StartBoundaryOrdinal = captureOrdinal,
                    Status = OperationRunStatus.Recording,
                    StartState = GetStateSnapshot()
                };
                linkedConnections.Clear();
                nextStepSequence = 0;
                state = OperationRecordingState.Recording;
                store.EnqueueOperationRun(CloneRun(activeRun));
                result = CloneRun(activeRun);
            }
            RaiseStateChanged(OperationRecordingState.Recording);
            RaiseRunChanged(result);
            return result;
        }

        public OperationStep MarkStep(string name, string description)
        {
            OperationStep step;
            lock (gate)
            {
                EnsureRecording();
                captureOrdinal++;
                nextStepSequence++;
                step = new OperationStep
                {
                    RunId = activeRun.Id,
                    Sequence = nextStepSequence,
                    BoundaryOrdinal = captureOrdinal,
                    TimestampUtc = DateTime.UtcNow,
                    Name = string.IsNullOrWhiteSpace(name) ? "步骤 " + nextStepSequence : name.Trim(),
                    Description = description ?? string.Empty
                };
                store.EnqueueOperationStep(step);
            }
            Action<OperationStep> handler = StepAdded;
            if (handler != null) handler(step);
            return step;
        }

        public AtomicOperationRun Stop(OperationOutcome outcome, string actualResult, string notes)
        {
            AtomicOperationRun result;
            lock (gate)
            {
                EnsureRecording();
                result = FinishLocked(OperationRunStatus.Completed, outcome, actualResult, notes);
                state = OperationRecordingState.Ready;
            }
            RaiseStateChanged(OperationRecordingState.Ready);
            RaiseRunChanged(result);
            return result;
        }

        public AtomicOperationRun Discard(string reason)
        {
            AtomicOperationRun result;
            lock (gate)
            {
                EnsureRecording();
                result = FinishLocked(OperationRunStatus.Discarded, OperationOutcome.Unknown, null, reason);
                state = OperationRecordingState.Ready;
            }
            RaiseStateChanged(OperationRecordingState.Ready);
            RaiseRunChanged(result);
            return result;
        }

        internal void UpdateSourceDatabasePath(string originalPath, string newPath)
        {
            if (string.IsNullOrWhiteSpace(originalPath) || string.IsNullOrWhiteSpace(newPath)) return;
            lock (gate)
            {
                if (activeRun != null && string.Equals(
                    Path.GetFullPath(activeRun.SourceDatabasePath),
                    Path.GetFullPath(originalPath),
                    StringComparison.OrdinalIgnoreCase))
                {
                    activeRun.SourceDatabasePath = Path.GetFullPath(newPath);
                }
            }
        }

        public AtomicOperationRun Interrupt(string reason)
        {
            AtomicOperationRun result = null;
            lock (gate)
            {
                if (activeRun != null)
                {
                    result = FinishLocked(OperationRunStatus.Interrupted, OperationOutcome.Unknown, null, reason);
                    state = OperationRecordingState.Ready;
                }
            }
            if (result != null)
            {
                RaiseStateChanged(OperationRecordingState.Ready);
                RaiseRunChanged(result);
            }
            return result;
        }

        public void ObserveChunk(ConnectionSession connection, TransportChunk chunk)
        {
            if (chunk == null) return;
            lock (gate)
            {
                chunk.CaptureOrdinal = ++captureOrdinal;
                if (ShouldInclude(connection))
                {
                    EnsureConnectionLinked(connection.Id);
                    store.EnqueueOperationChunk(new OperationChunkLink
                    {
                        RunId = activeRun.Id,
                        ChunkId = chunk.Id,
                        CaptureOrdinal = chunk.CaptureOrdinal
                    });
                }
            }
        }

        public void ObserveFrame(ConnectionSession connection, ProtocolFrame frame)
        {
            if (frame == null) return;
            lock (gate)
            {
                frame.CaptureOrdinal = ++captureOrdinal;
                if (ShouldInclude(connection))
                {
                    EnsureConnectionLinked(connection.Id);
                    store.EnqueueOperationFrame(new OperationFrameLink
                    {
                        RunId = activeRun.Id,
                        FrameId = frame.Id,
                        CaptureOrdinal = frame.CaptureOrdinal
                    });
                }
            }
        }

        public void ObserveState(ConnectionSession connection, StateTransition transition)
        {
            if (transition == null) return;
            lock (gate)
            {
                transition.CaptureOrdinal = ++captureOrdinal;
                if (ShouldInclude(connection))
                {
                    EnsureConnectionLinked(connection.Id);
                    store.EnqueueOperationState(new OperationStateLink
                    {
                        RunId = activeRun.Id,
                        ConnectionId = transition.ConnectionId,
                        CaptureOrdinal = transition.CaptureOrdinal,
                        TimestampUtc = transition.TimestampUtc,
                        FromState = transition.FromState,
                        ToState = transition.ToState,
                        Trigger = transition.Trigger
                    });
                }
            }
        }

        public void ObserveBatch(
            ConnectionSession connection,
            TransportChunk chunk,
            IList<ProtocolFrame> frames,
            IList<StateTransition> transitions)
        {
            lock (gate)
            {
                if (chunk != null)
                {
                    chunk.CaptureOrdinal = ++captureOrdinal;
                    if (ShouldInclude(connection))
                    {
                        EnsureConnectionLinked(connection.Id);
                        store.EnqueueOperationChunk(new OperationChunkLink
                        {
                            RunId = activeRun.Id,
                            ChunkId = chunk.Id,
                            CaptureOrdinal = chunk.CaptureOrdinal
                        });
                    }
                }
                if (frames != null)
                {
                    for (int i = 0; i < frames.Count; i++)
                    {
                        ProtocolFrame frame = frames[i];
                        if (frame == null) continue;
                        frame.CaptureOrdinal = ++captureOrdinal;
                        if (ShouldInclude(connection))
                        {
                            EnsureConnectionLinked(connection.Id);
                            store.EnqueueOperationFrame(new OperationFrameLink
                            {
                                RunId = activeRun.Id,
                                FrameId = frame.Id,
                                CaptureOrdinal = frame.CaptureOrdinal
                            });
                        }
                    }
                }
                if (transitions != null)
                {
                    for (int i = 0; i < transitions.Count; i++)
                    {
                        StateTransition transition = transitions[i];
                        if (transition == null) continue;
                        transition.CaptureOrdinal = ++captureOrdinal;
                        if (ShouldInclude(connection))
                        {
                            EnsureConnectionLinked(connection.Id);
                            store.EnqueueOperationState(new OperationStateLink
                            {
                                RunId = activeRun.Id,
                                ConnectionId = transition.ConnectionId,
                                CaptureOrdinal = transition.CaptureOrdinal,
                                TimestampUtc = transition.TimestampUtc,
                                FromState = transition.FromState,
                                ToState = transition.ToState,
                                Trigger = transition.Trigger
                            });
                        }
                    }
                }
            }
        }

        private bool ShouldInclude(ConnectionSession connection)
        {
            return activeRun != null && connection != null && connection.Kind == ConnectionKind.Game;
        }

        private void EnsureConnectionLinked(long connectionId)
        {
            if (linkedConnections.Add(connectionId))
            {
                store.EnqueueOperationConnection(new OperationConnectionLink
                {
                    RunId = activeRun.Id,
                    ConnectionId = connectionId,
                    Automatic = true,
                    Included = true
                });
            }
        }

        private AtomicOperationRun FinishLocked(
            OperationRunStatus status,
            OperationOutcome outcome,
            string actualResult,
            string notes)
        {
            activeRun.StoppedUtc = DateTime.UtcNow;
            activeRun.EndBoundaryOrdinal = captureOrdinal;
            activeRun.Status = status;
            activeRun.Outcome = outcome;
            activeRun.EndState = GetStateSnapshot();
            activeRun.ActualResult = actualResult ?? string.Empty;
            activeRun.Notes = notes ?? string.Empty;
            AtomicOperationRun result = CloneRun(activeRun);
            store.EnqueueOperationRun(result);
            activeRun = null;
            linkedConnections.Clear();
            return result;
        }

        private string GetStateSnapshot()
        {
            return stateSnapshot == null ? string.Empty : (stateSnapshot() ?? string.Empty);
        }

        private void EnsureRecording()
        {
            if (activeRun == null || state != OperationRecordingState.Recording)
            {
                throw new InvalidOperationException("No atomic operation is being recorded.");
            }
        }

        private void RaiseStateChanged(OperationRecordingState value)
        {
            Action<OperationRecordingState> handler = StateChanged;
            if (handler != null) handler(value);
        }

        private void RaiseRunChanged(AtomicOperationRun run)
        {
            Action<AtomicOperationRun> handler = RunChanged;
            if (handler != null) handler(CloneRun(run));
        }

        private static AtomicOperationRun CloneRun(AtomicOperationRun run)
        {
            if (run == null) return null;
            return new AtomicOperationRun
            {
                Id = run.Id,
                SessionId = run.SessionId,
                DefinitionId = run.DefinitionId,
                DefinitionSnapshotJson = run.DefinitionSnapshotJson,
                SourceDatabasePath = run.SourceDatabasePath,
                StartedUtc = run.StartedUtc,
                StoppedUtc = run.StoppedUtc,
                StartBoundaryOrdinal = run.StartBoundaryOrdinal,
                EndBoundaryOrdinal = run.EndBoundaryOrdinal,
                Status = run.Status,
                Outcome = run.Outcome,
                StartState = run.StartState,
                EndState = run.EndState,
                ActualResult = run.ActualResult,
                Notes = run.Notes
            };
        }
    }
}
