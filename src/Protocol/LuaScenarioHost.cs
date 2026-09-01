using System;
using System.Diagnostics;
using System.IO;
using MoonSharp.Interpreter;

namespace TianshuQitanLauncher.Protocol
{
    public sealed class LuaScenarioHost : IScenarioHost
    {
        private readonly object syncRoot = new object();
        private readonly string scriptPath;
        private readonly int packetTimeoutMs;
        private Script script;
        private bool loaded;

        public LuaScenarioHost(string scriptPath, int packetTimeoutMs)
        {
            this.scriptPath = scriptPath;
            this.packetTimeoutMs = Math.Max(1, packetTimeoutMs);
            Reload();
        }

        public event Action<string> LogProduced;

        public Func<string, int, bool> WaitStateRequested { get; set; }
        public Func<string, int, string> WaitFrameRequested { get; set; }
        public Func<long, byte[], bool> SendRequested { get; set; }
        public Func<long, byte[], bool> InjectReceiveRequested { get; set; }

        public bool IsLoaded
        {
            get
            {
                lock (syncRoot)
                {
                    return loaded;
                }
            }
        }

        public void Reload()
        {
            lock (syncRoot)
            {
                loaded = false;
                script = null;
                if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
                {
                    return;
                }

                try
                {
                    Script next = CreateScript();
                    script = next;
                    loaded = true;
                    EmitLog("Lua script loaded: " + scriptPath);
                }
                catch (Exception ex)
                {
                    EmitLog("Lua load failed: " + ex.Message);
                }
            }
        }

        public RuleDecision EvaluatePacket(PacketContext context, ProtocolFrame frame)
        {
            lock (syncRoot)
            {
                if (!loaded || script == null)
                {
                    return RuleDecision.Pass(frame == null ? null : frame.Bytes);
                }

                DynValue function = script.Globals.Get("on_frame");
                if (function.IsNil() || function.Type != DataType.Function)
                {
                    return RuleDecision.Pass(frame.Bytes);
                }

                try
                {
                    Table contextTable = BuildPacketContext(script, context, frame);
                    bool timedOut;
                    DynValue result = RunCoroutine(script, function, new[] { DynValue.NewTable(contextTable) }, packetTimeoutMs, out timedOut);
                    if (timedOut)
                    {
                        loaded = false;
                        EmitLog("Lua packet callback exceeded " + packetTimeoutMs + "ms and was disabled.");
                        return new RuleDecision
                        {
                            Action = RuleAction.Pass,
                            Bytes = frame.Bytes,
                            Reason = "Lua timeout; fail-open",
                            TimedOut = true
                        };
                    }
                    return ParseDecision(result, frame.Bytes);
                }
                catch (Exception ex)
                {
                    EmitLog("Lua packet callback failed: " + ex.Message);
                    return new RuleDecision
                    {
                        Action = RuleAction.Pass,
                        Bytes = frame.Bytes,
                        Reason = "Lua error; fail-open: " + ex.Message
                    };
                }
            }
        }

        public ScenarioResult Run(string scenarioName)
        {
            ScenarioResult scenarioResult = new ScenarioResult
            {
                Name = string.IsNullOrWhiteSpace(scenarioName) ? "main" : scenarioName,
                StartedUtc = DateTime.UtcNow
            };

            try
            {
                Script scenarioScript = CreateScript();
                DynValue function = scenarioScript.Globals.Get(scenarioResult.Name);
                if (function.IsNil() || function.Type != DataType.Function)
                {
                    throw new InvalidOperationException("Scenario function not found: " + scenarioResult.Name);
                }
                Table api = BuildScenarioApi(scenarioScript, scenarioResult);
                bool timedOut;
                DynValue value = RunCoroutine(scenarioScript, function, new[] { DynValue.NewTable(api) }, 300000, out timedOut);
                if (timedOut)
                {
                    scenarioResult.Status = ScenarioStatus.TimedOut;
                    scenarioResult.Message = "Scenario exceeded 300000ms.";
                }
                else if (value.Type == DataType.Boolean && !value.Boolean)
                {
                    scenarioResult.Status = ScenarioStatus.Failed;
                    scenarioResult.Message = "Scenario returned false.";
                }
                else
                {
                    scenarioResult.Status = ScenarioStatus.Passed;
                    scenarioResult.Message = value.IsNil() ? "Completed." : value.ToPrintString();
                }
            }
            catch (Exception ex)
            {
                scenarioResult.Status = ScenarioStatus.Error;
                scenarioResult.Message = ex.Message;
                scenarioResult.Log.Add(ex.ToString());
            }

            scenarioResult.FinishedUtc = DateTime.UtcNow;
            return scenarioResult;
        }

        private DynValue RunCoroutine(Script owner, DynValue function, DynValue[] arguments, int timeoutMs, out bool timedOut)
        {
            DynValue coroutineValue = owner.CreateCoroutine(function);
            Coroutine coroutine = coroutineValue.Coroutine;
            coroutine.AutoYieldCounter = 10000;
            Stopwatch stopwatch = Stopwatch.StartNew();
            DynValue result = coroutine.Resume(arguments);
            while (coroutine.State == CoroutineState.ForceSuspended || coroutine.State == CoroutineState.Suspended)
            {
                if (stopwatch.ElapsedMilliseconds > timeoutMs)
                {
                    timedOut = true;
                    return DynValue.Nil;
                }
                result = coroutine.Resume();
            }
            timedOut = stopwatch.ElapsedMilliseconds > timeoutMs;
            return result;
        }

        private Script CreateScript()
        {
            if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
            {
                throw new FileNotFoundException("Lua script not found.", scriptPath);
            }
            Script next = new Script(CoreModules.Preset_SoftSandbox);
            next.Options.DebugPrint = EmitLog;
            next.DoString(File.ReadAllText(scriptPath), null, scriptPath);
            return next;
        }

        private Table BuildPacketContext(Script owner, PacketContext context, ProtocolFrame frame)
        {
            Table table = new Table(owner);
            table.Set("session_id", DynValue.NewNumber(context.SessionId));
            table.Set("connection_id", DynValue.NewNumber(frame.ConnectionId));
            table.Set("direction", DynValue.NewString(context.Direction == TrafficDirection.ClientToServer ? "c2s" : "s2c"));
            table.Set("state", DynValue.NewString(context.State ?? string.Empty));
            table.Set("active", DynValue.NewBoolean(context.ActiveMode));
            table.Set("hex", DynValue.NewString(HexCodec.Format(frame.Bytes)));
            table.Set("length", DynValue.NewNumber(frame.Bytes == null ? 0 : frame.Bytes.Length));
            table.Set("signature", DynValue.NewString(frame.Signature));
            table.Set("opcode", frame.Opcode.HasValue ? DynValue.NewNumber(frame.Opcode.Value) : DynValue.Nil);

            if (context.Connection != null)
            {
                table.Set("connection_kind", DynValue.NewString(context.Connection.Kind.ToString()));
                table.Set("remote", DynValue.NewString(context.Connection.RemoteEndPoint ?? string.Empty));
            }

            Table fields = new Table(owner);
            for (int i = 0; i < frame.Fields.Count; i++)
            {
                DecodedField field = frame.Fields[i];
                fields.Set(field.Name, DynValue.NewString(field.Value ?? string.Empty));
            }
            table.Set("fields", DynValue.NewTable(fields));
            return table;
        }

        private Table BuildScenarioApi(Script owner, ScenarioResult result)
        {
            Table api = new Table(owner);
            api.Set("mark", DynValue.NewCallback(delegate(ScriptExecutionContext execution, CallbackArguments arguments)
            {
                CallbackArguments values = arguments.SkipMethodCall();
                string message = values.Count > 0 ? values[0].CastToString() : string.Empty;
                result.Log.Add(message);
                EmitLog("Scenario: " + message);
                return DynValue.Nil;
            }));
            api.Set("assert", DynValue.NewCallback(delegate(ScriptExecutionContext execution, CallbackArguments arguments)
            {
                CallbackArguments values = arguments.SkipMethodCall();
                bool condition = values.Count > 0 && values[0].CastToBool();
                if (!condition)
                {
                    string message = values.Count > 1 ? values[1].CastToString() : "Assertion failed.";
                    throw new ScriptRuntimeException(message);
                }
                return DynValue.True;
            }));
            api.Set("sleep", DynValue.NewCallback(delegate(ScriptExecutionContext execution, CallbackArguments arguments)
            {
                CallbackArguments values = arguments.SkipMethodCall();
                int milliseconds = values.Count > 0 ? (int)values[0].Number : 0;
                System.Threading.Thread.Sleep(Math.Max(0, Math.Min(60000, milliseconds)));
                return DynValue.Nil;
            }));
            api.Set("wait_state", DynValue.NewCallback(delegate(ScriptExecutionContext execution, CallbackArguments arguments)
            {
                CallbackArguments values = arguments.SkipMethodCall();
                string state = values.Count > 0 ? values[0].CastToString() : string.Empty;
                int timeout = values.Count > 1 ? (int)values[1].Number : 5000;
                bool matched = WaitStateRequested != null && WaitStateRequested(state, timeout);
                return DynValue.NewBoolean(matched);
            }));
            api.Set("wait_frame", DynValue.NewCallback(delegate(ScriptExecutionContext execution, CallbackArguments arguments)
            {
                CallbackArguments values = arguments.SkipMethodCall();
                string signature = values.Count > 0 ? values[0].CastToString() : string.Empty;
                int timeout = values.Count > 1 ? (int)values[1].Number : 5000;
                string hex = WaitFrameRequested == null ? null : WaitFrameRequested(signature, timeout);
                return hex == null ? DynValue.Nil : DynValue.NewString(hex);
            }));
            api.Set("send", CreateSendCallback(false));
            api.Set("inject_receive", CreateSendCallback(true));
            return api;
        }

        private DynValue CreateSendCallback(bool injectReceive)
        {
            return DynValue.NewCallback(delegate(ScriptExecutionContext execution, CallbackArguments arguments)
            {
                CallbackArguments values = arguments.SkipMethodCall();
                long connectionId = values.Count > 0 ? (long)values[0].Number : 0;
                string hex = values.Count > 1 ? values[1].CastToString() : string.Empty;
                byte[] bytes = HexCodec.Parse(hex);
                Func<long, byte[], bool> callback = injectReceive ? InjectReceiveRequested : SendRequested;
                return DynValue.NewBoolean(callback != null && callback(connectionId, bytes));
            });
        }

        private static RuleDecision ParseDecision(DynValue value, byte[] original)
        {
            if (value == null || value.IsNil() || value.Type != DataType.Table)
            {
                return RuleDecision.Pass(original);
            }

            Table table = value.Table;
            string actionText = table.Get("action").CastToString();
            RuleAction action;
            if (!Enum.TryParse(actionText, true, out action))
            {
                action = RuleAction.Pass;
            }
            byte[] bytes = original;
            DynValue hexValue = table.Get("hex");
            if (!hexValue.IsNil())
            {
                bytes = HexCodec.Parse(hexValue.CastToString());
            }
            DynValue delayValue = table.Get("delay_ms");
            DynValue reasonValue = table.Get("reason");
            return new RuleDecision
            {
                Action = action,
                Bytes = bytes,
                DelayMs = delayValue.Type == DataType.Number ? Math.Max(0, Math.Min(60000, (int)delayValue.Number)) : 0,
                RuleId = "lua:on_frame",
                Reason = reasonValue.IsNil() ? "Lua on_frame" : reasonValue.CastToString()
            };
        }

        private void EmitLog(string message)
        {
            Action<string> handler = LogProduced;
            if (handler != null)
            {
                handler(message);
            }
        }
    }
}
