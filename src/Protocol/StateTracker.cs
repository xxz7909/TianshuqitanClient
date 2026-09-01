using System;
using System.Collections.Generic;

namespace TianshuQitanLauncher.Protocol
{
    public sealed class StateTracker : IStateTracker
    {
        private readonly object syncRoot = new object();
        private readonly ProtocolDefinition definition;
        private readonly Dictionary<long, string> currentStates = new Dictionary<long, string>();
        private readonly Dictionary<long, string> previousSignatures = new Dictionary<long, string>();
        private readonly Dictionary<string, ObservedTransition> observed = new Dictionary<string, ObservedTransition>();

        public StateTracker(ProtocolDefinition definition)
        {
            this.definition = definition ?? new ProtocolDefinition();
        }

        public string GetCurrentState(long connectionId)
        {
            lock (syncRoot)
            {
                string state;
                return currentStates.TryGetValue(connectionId, out state) ? state : "Disconnected";
            }
        }

        public StateTransition Accept(ProtocolFrame frame)
        {
            if (frame == null)
            {
                return null;
            }

            lock (syncRoot)
            {
                RecordObserved(frame);
                string current = GetCurrentStateUnsafe(frame.ConnectionId);
                for (int i = 0; i < definition.StateTransitions.Count; i++)
                {
                    StateTransitionDefinition candidate = definition.StateTransitions[i];
                    if (!Matches(candidate, current, frame))
                    {
                        continue;
                    }

                    string next = string.IsNullOrWhiteSpace(candidate.ToState) ? current : candidate.ToState;
                    currentStates[frame.ConnectionId] = next;
                    return new StateTransition
                    {
                        SessionId = frame.SessionId,
                        ConnectionId = frame.ConnectionId,
                        TimestampUtc = frame.TimestampUtc,
                        FromState = current,
                        ToState = next,
                        Trigger = frame.Signature,
                        Confirmed = true
                    };
                }
            }
            return null;
        }

        public StateTransition AcceptConnectionEvent(long connectionId, string eventName)
        {
            lock (syncRoot)
            {
                string current = GetCurrentStateUnsafe(connectionId);
                string next = current;
                if (string.Equals(eventName, "Connected", StringComparison.OrdinalIgnoreCase))
                {
                    next = "Connected";
                }
                else if (string.Equals(eventName, "Closed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(eventName, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    next = "Disconnected";
                }

                for (int i = 0; i < definition.StateTransitions.Count; i++)
                {
                    StateTransitionDefinition candidate = definition.StateTransitions[i];
                    if (string.IsNullOrWhiteSpace(candidate.Event) ||
                        !string.Equals(candidate.Event, eventName, StringComparison.OrdinalIgnoreCase) ||
                        !MatchesState(candidate.FromState, current))
                    {
                        continue;
                    }
                    next = candidate.ToState;
                    break;
                }

                currentStates[connectionId] = next;
                if (string.Equals(current, next, StringComparison.Ordinal))
                {
                    return null;
                }
                return new StateTransition
                {
                    ConnectionId = connectionId,
                    TimestampUtc = DateTime.UtcNow,
                    FromState = current,
                    ToState = next,
                    Trigger = eventName,
                    Confirmed = true
                };
            }
        }

        public IList<ObservedTransition> GetObservedTransitions()
        {
            lock (syncRoot)
            {
                List<ObservedTransition> result = new List<ObservedTransition>();
                foreach (ObservedTransition item in observed.Values)
                {
                    result.Add(new ObservedTransition
                    {
                        FromSignature = item.FromSignature,
                        ToSignature = item.ToSignature,
                        Count = item.Count
                    });
                }
                return result;
            }
        }

        private void RecordObserved(ProtocolFrame frame)
        {
            string previous;
            if (previousSignatures.TryGetValue(frame.ConnectionId, out previous))
            {
                string key = previous + "->" + frame.Signature;
                ObservedTransition transition;
                if (!observed.TryGetValue(key, out transition))
                {
                    transition = new ObservedTransition
                    {
                        FromSignature = previous,
                        ToSignature = frame.Signature,
                        Count = 0
                    };
                    observed.Add(key, transition);
                }
                transition.Count++;
            }
            previousSignatures[frame.ConnectionId] = frame.Signature;
        }

        private static bool Matches(StateTransitionDefinition definition, string current, ProtocolFrame frame)
        {
            if (!string.IsNullOrWhiteSpace(definition.Event) || !MatchesState(definition.FromState, current))
            {
                return false;
            }
            if (definition.Direction.HasValue && definition.Direction.Value != frame.Direction)
            {
                return false;
            }
            if (definition.Opcode.HasValue && definition.Opcode != frame.Opcode)
            {
                return false;
            }
            if (definition.FieldEquals == null || definition.FieldEquals.Count == 0)
            {
                return true;
            }

            foreach (KeyValuePair<string, string> expected in definition.FieldEquals)
            {
                bool found = false;
                for (int i = 0; i < frame.Fields.Count; i++)
                {
                    DecodedField field = frame.Fields[i];
                    if (string.Equals(field.Name, expected.Key, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(field.Value, expected.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool MatchesState(string expected, string actual)
        {
            return string.IsNullOrWhiteSpace(expected) || expected == "*" ||
                string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }

        private string GetCurrentStateUnsafe(long connectionId)
        {
            string state;
            return currentStates.TryGetValue(connectionId, out state) ? state : "Disconnected";
        }
    }
}
