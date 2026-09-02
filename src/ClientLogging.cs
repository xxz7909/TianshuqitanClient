using System;
using System.Collections.Generic;

namespace TianshuQitanLauncher
{
    public enum ClientLogLevel
    {
        Info = 0,
        Warning = 1,
        Error = 2
    }

    public sealed class ClientLogEntry
    {
        public long Sequence { get; internal set; }
        public DateTime TimestampUtc { get; internal set; }
        public ClientLogLevel Level { get; internal set; }
        public string Module { get; internal set; }
        public string State { get; internal set; }
        public string Message { get; internal set; }
    }

    public interface IClientLogSink
    {
        void Publish(string module, ClientLogLevel level, string state, string message);
    }

    public sealed class ClientLogHub : IClientLogSink
    {
        public const int DefaultCapacity = 10000;

        private readonly object syncRoot = new object();
        private readonly Queue<ClientLogEntry> entries;
        private readonly int capacity;
        private long nextSequence;

        public ClientLogHub()
            : this(DefaultCapacity)
        {
        }

        public ClientLogHub(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException("capacity");
            this.capacity = capacity;
            entries = new Queue<ClientLogEntry>(Math.Min(capacity, 1024));
        }

        public event Action<ClientLogEntry> EntryPublished;
        public event Action EntriesCleared;

        public int Capacity { get { return capacity; } }

        public int Count
        {
            get
            {
                lock (syncRoot) return entries.Count;
            }
        }

        public void Publish(string module, ClientLogLevel level, string state, string message)
        {
            ClientLogEntry entry;
            lock (syncRoot)
            {
                entry = new ClientLogEntry
                {
                    Sequence = ++nextSequence,
                    TimestampUtc = DateTime.UtcNow,
                    Level = level,
                    Module = string.IsNullOrWhiteSpace(module) ? "系统" : module.Trim(),
                    State = state == null ? string.Empty : state.Trim(),
                    Message = message ?? string.Empty
                };
                entries.Enqueue(entry);
                while (entries.Count > capacity) entries.Dequeue();
            }

            Action<ClientLogEntry> handler = EntryPublished;
            if (handler != null)
            {
                Delegate[] subscribers = handler.GetInvocationList();
                for (int i = 0; i < subscribers.Length; i++)
                {
                    try { ((Action<ClientLogEntry>)subscribers[i])(entry); }
                    catch { }
                }
            }
        }

        public IList<ClientLogEntry> Snapshot()
        {
            lock (syncRoot) return new List<ClientLogEntry>(entries);
        }

        public void Clear()
        {
            bool changed;
            lock (syncRoot)
            {
                changed = entries.Count != 0;
                entries.Clear();
            }
            if (!changed) return;
            Action handler = EntriesCleared;
            if (handler != null)
            {
                Delegate[] subscribers = handler.GetInvocationList();
                for (int i = 0; i < subscribers.Length; i++)
                {
                    try { ((Action)subscribers[i])(); }
                    catch { }
                }
            }
        }
    }

    public sealed class NullClientLogSink : IClientLogSink
    {
        public static readonly NullClientLogSink Instance = new NullClientLogSink();

        private NullClientLogSink()
        {
        }

        public void Publish(string module, ClientLogLevel level, string state, string message)
        {
        }
    }
}
