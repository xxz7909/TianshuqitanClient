using System;
using System.Collections.Generic;
using System.Linq;

namespace TianshuQitanLauncher.Protocol
{
    public sealed class OperationAnalyzer : IOperationAnalyzer
    {
        private readonly IOperationRepository repository;

        public OperationAnalyzer(IOperationRepository repository)
        {
            if (repository == null) throw new ArgumentNullException("repository");
            this.repository = repository;
        }

        public OperationComparison Compare(IList<AtomicOperationRun> runs)
        {
            if (runs == null || runs.Count == 0)
            {
                throw new ArgumentException("Select at least one operation run.", "runs");
            }
            string definitionId = runs[0].DefinitionId;
            if (runs.Any(item => !string.Equals(item.DefinitionId, definitionId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Only runs of the same operation template can be compared.");
            }

            List<IList<OperationFrameSample>> sequences = runs
                .Select(item => repository.GetFrames(item, false)).ToList();
            OperationComparison comparison = new OperationComparison
            {
                DefinitionId = definitionId,
                RunIds = runs.Select(item => item.Id).ToList()
            };

            IList<OperationFrameSample> baseline = sequences[0];
            List<AggregateFrame> aggregates = new List<AggregateFrame>();
            for (int i = 0; i < baseline.Count; i++)
            {
                AggregateFrame aggregate = new AggregateFrame(baseline[i], i);
                aggregate.Samples.Add(baseline[i]);
                aggregates.Add(aggregate);
            }

            for (int runIndex = 1; runIndex < sequences.Count; runIndex++)
            {
                IList<OperationFrameSample> sequence = sequences[runIndex];
                IList<Match> matches = LongestCommonSubsequence(baseline, sequence);
                HashSet<int> matchedRight = new HashSet<int>();
                for (int i = 0; i < matches.Count; i++)
                {
                    Match match = matches[i];
                    aggregates[match.Left].Samples.Add(sequence[match.Right]);
                    matchedRight.Add(match.Right);
                }

                for (int i = 0; i < sequence.Count; i++)
                {
                    if (matchedRight.Contains(i)) continue;
                    OperationFrameSample sample = sequence[i];
                    AggregateFrame existing = aggregates.FirstOrDefault(item =>
                        item.BaselineIndex < 0 && item.Sample.Signature == sample.Signature &&
                        !item.Samples.Any(value => value.RunId == sample.RunId));
                    if (existing == null)
                    {
                        existing = new AggregateFrame(sample, -1);
                        aggregates.Add(existing);
                    }
                    existing.Samples.Add(sample);
                    if (baseline.Any(item => item.Signature == sample.Signature)) existing.Reordered = true;
                }
            }

            int sequenceNumber = 0;
            foreach (AggregateFrame aggregate in aggregates.OrderBy(item => item.BaselineIndex < 0 ? int.MaxValue : item.BaselineIndex))
            {
                int present = aggregate.Samples.Select(item => item.RunId).Distinct().Count();
                ComparisonPresence presence = present == runs.Count
                    ? ComparisonPresence.Required
                    : (present == 1 ? ComparisonPresence.Unique : ComparisonPresence.Optional);
                if (aggregate.Reordered) presence = ComparisonPresence.Reordered;
                OperationComparisonFrame frame = new OperationComparisonFrame
                {
                    Sequence = ++sequenceNumber,
                    Signature = aggregate.Sample.Signature,
                    Direction = aggregate.Sample.Direction,
                    Opcode = aggregate.Sample.Opcode,
                    Name = aggregate.Sample.Name,
                    Length = aggregate.Sample.Bytes == null ? 0 : aggregate.Sample.Bytes.Length,
                    Presence = presence,
                    PresentRunCount = present,
                    Reordered = aggregate.Reordered,
                    RunIds = aggregate.Samples.Select(item => item.RunId).Distinct().ToList()
                };
                CalculateMasks(aggregate.Samples, frame);
                comparison.Frames.Add(frame);
            }
            return comparison;
        }

        private static IList<Match> LongestCommonSubsequence(
            IList<OperationFrameSample> left,
            IList<OperationFrameSample> right)
        {
            if ((long)left.Count * (long)right.Count > 4000000L)
            {
                return GreedySequenceAlignment(left, right);
            }
            int[,] lengths = new int[left.Count + 1, right.Count + 1];
            for (int i = left.Count - 1; i >= 0; i--)
            {
                for (int j = right.Count - 1; j >= 0; j--)
                {
                    lengths[i, j] = left[i].Signature == right[j].Signature
                        ? lengths[i + 1, j + 1] + 1
                        : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
                }
            }
            List<Match> result = new List<Match>();
            int x = 0;
            int y = 0;
            while (x < left.Count && y < right.Count)
            {
                if (left[x].Signature == right[y].Signature)
                {
                    result.Add(new Match(x, y));
                    x++;
                    y++;
                }
                else if (lengths[x + 1, y] >= lengths[x, y + 1]) x++;
                else y++;
            }
            return result;
        }

        private static IList<Match> GreedySequenceAlignment(
            IList<OperationFrameSample> left,
            IList<OperationFrameSample> right)
        {
            Dictionary<string, Queue<int>> positions = new Dictionary<string, Queue<int>>();
            for (int i = 0; i < right.Count; i++)
            {
                Queue<int> queue;
                if (!positions.TryGetValue(right[i].Signature, out queue))
                {
                    queue = new Queue<int>();
                    positions.Add(right[i].Signature, queue);
                }
                queue.Enqueue(i);
            }
            List<Match> result = new List<Match>();
            int lastRight = -1;
            for (int i = 0; i < left.Count; i++)
            {
                Queue<int> queue;
                if (!positions.TryGetValue(left[i].Signature, out queue)) continue;
                while (queue.Count > 0 && queue.Peek() <= lastRight) queue.Dequeue();
                if (queue.Count == 0) continue;
                lastRight = queue.Dequeue();
                result.Add(new Match(i, lastRight));
            }
            return result;
        }

        private static void CalculateMasks(IList<OperationFrameSample> samples, OperationComparisonFrame output)
        {
            int length = samples.Count == 0 || samples[0].Bytes == null ? 0 : samples[0].Bytes.Length;
            byte[] stable = new byte[length];
            byte[] dynamic = new byte[length];
            for (int i = 0; i < length; i++)
            {
                bool same = samples.All(sample => sample.Bytes != null && sample.Bytes.Length == length && sample.Bytes[i] == samples[0].Bytes[i]);
                stable[i] = same ? (byte)0xFF : (byte)0;
                dynamic[i] = same ? (byte)0 : (byte)0xFF;
            }
            output.StableMaskHex = HexCodec.Format(stable);
            output.DynamicMaskHex = HexCodec.Format(dynamic);
            output.CandidateKinds = DescribeDynamicRanges(dynamic);
        }

        private static string DescribeDynamicRanges(byte[] mask)
        {
            List<string> ranges = new List<string>();
            int start = -1;
            for (int i = 0; i <= mask.Length; i++)
            {
                bool changed = i < mask.Length && mask[i] != 0;
                if (changed && start < 0) start = i;
                if (!changed && start >= 0)
                {
                    int length = i - start;
                    string kind = length == 4 ? "序号/时间戳候选" :
                        (length == 8 ? "时间戳/会话标识候选" :
                        (length >= 16 ? "会话标识/令牌候选" : "动态字节候选"));
                    ranges.Add(start + "+" + length + " " + kind);
                    start = -1;
                }
            }
            return string.Join("; ", ranges.ToArray());
        }

        private sealed class AggregateFrame
        {
            public AggregateFrame(OperationFrameSample sample, int baselineIndex)
            {
                Sample = sample;
                BaselineIndex = baselineIndex;
                Samples = new List<OperationFrameSample>();
            }

            public OperationFrameSample Sample { get; private set; }
            public int BaselineIndex { get; private set; }
            public bool Reordered { get; set; }
            public IList<OperationFrameSample> Samples { get; private set; }
        }

        private sealed class Match
        {
            public Match(int left, int right) { Left = left; Right = right; }
            public int Left { get; private set; }
            public int Right { get; private set; }
        }
    }
}
