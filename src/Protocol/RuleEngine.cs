using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace TianshuQitanLauncher.Protocol
{
    public sealed class PacketRule
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Enabled { get; set; }
        public int Priority { get; set; }
        public bool Continue { get; set; }
        public bool AllowNonGame { get; set; }
        public TrafficDirection? Direction { get; set; }
        public ConnectionKind? ConnectionKind { get; set; }
        public string State { get; set; }
        public long? Opcode { get; set; }
        public int MinimumLength { get; set; }
        public int MaximumLength { get; set; }
        public int PatternOffset { get; set; }
        public string PatternHex { get; set; }
        public string MaskHex { get; set; }
        public IDictionary<string, string> FieldEquals { get; set; }
        public RuleAction Action { get; set; }
        public string ReplacementHex { get; set; }
        public int DelayMs { get; set; }
        public string Reason { get; set; }

        [JsonIgnore]
        internal byte[] PatternBytes { get; set; }

        [JsonIgnore]
        internal byte[] MaskBytes { get; set; }

        public PacketRule()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "New rule";
            Enabled = true;
            Action = RuleAction.Pass;
            FieldEquals = new Dictionary<string, string>();
        }

        internal void Compile()
        {
            PatternBytes = ParseOrEmpty(PatternHex);
            MaskBytes = ParseOrEmpty(MaskHex);
            if (MaskBytes.Length != PatternBytes.Length)
            {
                MaskBytes = new byte[PatternBytes.Length];
                for (int i = 0; i < MaskBytes.Length; i++)
                {
                    MaskBytes[i] = 0xFF;
                }
            }
            DelayMs = Math.Max(0, Math.Min(60000, DelayMs));
        }

        private static byte[] ParseOrEmpty(string value)
        {
            try
            {
                return HexCodec.Parse(value);
            }
            catch
            {
                return new byte[0];
            }
        }
    }

    public sealed class RuleSetDocument
    {
        public IList<PacketRule> Rules { get; set; }

        public RuleSetDocument()
        {
            Rules = new List<PacketRule>();
        }
    }

    public sealed class RuleEngine : IRuleEngine
    {
        private readonly object syncRoot = new object();
        private readonly string path;
        private readonly IScenarioHost scenarioHost;
        private IList<PacketRule> rules;

        public RuleEngine(string path, IScenarioHost scenarioHost)
        {
            this.path = path;
            this.scenarioHost = scenarioHost;
            rules = new List<PacketRule>();
            Reload();
        }

        public IList<PacketRule> Rules
        {
            get
            {
                lock (syncRoot)
                {
                    return new List<PacketRule>(rules);
                }
            }
        }

        public void Reload()
        {
            RuleSetDocument document;
            try
            {
                document = File.Exists(path)
                    ? JsonConvert.DeserializeObject<RuleSetDocument>(File.ReadAllText(path))
                    : new RuleSetDocument();
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot load protocol rules from " + path, ex);
                document = new RuleSetDocument();
            }

            if (document == null || document.Rules == null)
            {
                document = new RuleSetDocument();
            }
            List<PacketRule> ordered = new List<PacketRule>(document.Rules);
            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].Compile();
            }
            ordered.Sort(delegate(PacketRule left, PacketRule right)
            {
                int priority = left.Priority.CompareTo(right.Priority);
                return priority != 0 ? priority : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
            });
            lock (syncRoot)
            {
                rules = ordered;
            }
        }

        public RuleDecision Evaluate(PacketContext context, ProtocolFrame frame)
        {
            if (context == null || frame == null || !context.ActiveMode)
            {
                return RuleDecision.Pass(frame == null ? null : frame.Bytes);
            }

            byte[] currentBytes = HexCodec.Copy(frame.Bytes);
            IList<PacketRule> snapshot = Rules;
            RuleDecision last = RuleDecision.Pass(currentBytes);
            for (int i = 0; i < snapshot.Count; i++)
            {
                PacketRule rule = snapshot[i];
                if (!Matches(rule, context, frame, currentBytes))
                {
                    continue;
                }

                RuleDecision decision = BuildDecision(rule, currentBytes);
                last = decision;
                if (decision.Action == RuleAction.Replace)
                {
                    currentBytes = decision.Bytes;
                    frame.Bytes = currentBytes;
                }
                if (!rule.Continue && decision.Action != RuleAction.Pass)
                {
                    return decision;
                }
            }

            if (scenarioHost != null && scenarioHost.IsLoaded &&
                context.Connection != null && context.Connection.Kind == ConnectionKind.Game)
            {
                ProtocolFrame scriptFrame = frame;
                scriptFrame.Bytes = currentBytes;
                RuleDecision scriptDecision = scenarioHost.EvaluatePacket(context, scriptFrame);
                if (scriptDecision != null && scriptDecision.Action != RuleAction.Pass)
                {
                    return scriptDecision;
                }
            }

            if (last.Action == RuleAction.Replace)
            {
                return last;
            }
            return RuleDecision.Pass(currentBytes);
        }

        public void Save(IList<PacketRule> updatedRules)
        {
            RuleSetDocument document = new RuleSetDocument { Rules = updatedRules ?? new List<PacketRule>() };
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonConvert.SerializeObject(document, Formatting.Indented));
            Reload();
        }

        private static bool Matches(PacketRule rule, PacketContext context, ProtocolFrame frame, byte[] bytes)
        {
            if (rule == null || !rule.Enabled)
            {
                return false;
            }
            if (context.Connection != null && context.Connection.Kind != ConnectionKind.Game && !rule.AllowNonGame)
            {
                return false;
            }
            if (rule.Direction.HasValue && rule.Direction.Value != context.Direction)
            {
                return false;
            }
            if (rule.ConnectionKind.HasValue &&
                (context.Connection == null || rule.ConnectionKind.Value != context.Connection.Kind))
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(rule.State) && rule.State != "*" &&
                !string.Equals(rule.State, context.State, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (rule.Opcode.HasValue && frame.Opcode != rule.Opcode)
            {
                return false;
            }
            if (rule.MinimumLength > 0 && bytes.Length < rule.MinimumLength)
            {
                return false;
            }
            if (rule.MaximumLength > 0 && bytes.Length > rule.MaximumLength)
            {
                return false;
            }
            if (!MatchesPattern(rule, bytes) || !MatchesFields(rule, frame))
            {
                return false;
            }
            return true;
        }

        private static bool MatchesPattern(PacketRule rule, byte[] bytes)
        {
            if (rule.PatternBytes == null || rule.PatternBytes.Length == 0)
            {
                return true;
            }
            if (rule.PatternOffset < 0 || rule.PatternOffset + rule.PatternBytes.Length > bytes.Length)
            {
                return false;
            }
            for (int i = 0; i < rule.PatternBytes.Length; i++)
            {
                byte mask = rule.MaskBytes[i];
                if ((bytes[rule.PatternOffset + i] & mask) != (rule.PatternBytes[i] & mask))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool MatchesFields(PacketRule rule, ProtocolFrame frame)
        {
            if (rule.FieldEquals == null || rule.FieldEquals.Count == 0)
            {
                return true;
            }
            foreach (KeyValuePair<string, string> expected in rule.FieldEquals)
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

        private static RuleDecision BuildDecision(PacketRule rule, byte[] currentBytes)
        {
            byte[] bytes = currentBytes;
            if (rule.Action == RuleAction.Replace || rule.Action == RuleAction.Inject)
            {
                try
                {
                    bytes = HexCodec.Parse(rule.ReplacementHex);
                }
                catch (Exception ex)
                {
                    return new RuleDecision
                    {
                        Action = RuleAction.Pass,
                        Bytes = currentBytes,
                        RuleId = rule.Id,
                        Reason = "Invalid replacement: " + ex.Message
                    };
                }
            }
            return new RuleDecision
            {
                Action = rule.Action,
                Bytes = bytes,
                DelayMs = rule.DelayMs,
                RuleId = rule.Id,
                Reason = string.IsNullOrWhiteSpace(rule.Reason) ? rule.Name : rule.Reason,
                Continue = rule.Continue
            };
        }
    }
}
