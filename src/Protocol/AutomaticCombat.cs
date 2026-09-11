using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace TianshuQitanLauncher.Protocol
{
    // Shared by task-driven and standalone combat. Packet cadence and recovery remain
    // owned by the coordinator so both modes use the same connection and sequence.
    internal sealed class AutomaticCombatPatrol
    {
        private readonly RunLoopWalkOptions options;
        private readonly Stopwatch progressWatch = new Stopwatch();
        private readonly Stopwatch mapWaitWatch = new Stopwatch();
        private RunLoopPatrolPath path;
        public int StepCount { get; private set; }
        public bool ProgressTimedOut { get { return progressWatch.ElapsedMilliseconds >= options.NoProgressTimeoutMs; } }

        public AutomaticCombatPatrol(RunLoopWalkOptions options) { this.options = options; }

        public void Restart()
        {
            StepCount = 0;
            path = null;
            progressWatch.Restart();
            mapWaitWatch.Restart();
        }

        public void MarkProgress() { progressWatch.Restart(); }
        public void ResetPath(bool restartWait)
        {
            path = null;
            if (restartWait) mapWaitWatch.Restart();
        }

        public Point? Next(RunLoopMapGrid grid, int mapId, ushort x, ushort y,
            Action<string> fail, Action<string> publish)
        {
            if (path == null)
            {
                if (!RunLoopPatrolPath.TryCreate(grid != null && grid.MapId == mapId ? grid : null,
                    x, y, options.PatrolPointCount, out path))
                {
                    if (mapWaitWatch.ElapsedMilliseconds >= options.MapDataWaitTimeoutMs)
                        fail("未能从当前地图数据找到至少两个相连的可行走点，已停止自动遇怪。");
                    return null;
                }
                publish("已固定 " + path.Points.Count + " 个有效走步点，开始循环往返。");
            }
            if (mapId <= 0) { fail("尚未取得当前地图 ID，无法构造走步包。"); return null; }
            StepCount++;
            return path.Next();
        }
    }

    public sealed class AutomaticCombatControl : UserControl
    {
        private readonly RunLoopAutomationCoordinator coordinator;
        private readonly Button toggle;
        private readonly CheckBox recovery;
        private readonly Label status;

        public AutomaticCombatControl(RunLoopAutomationCoordinator coordinator)
        {
            this.coordinator = coordinator;
            Dock = DockStyle.Fill;
            Label help = new Label
            {
                Dock = DockStyle.Top, Height = 62,
                Text = "在当前地图附近的可走点往返遇怪，复用跑环的走步与自动恢复逻辑。\r\n" +
                    "请先进入游戏并移动一次。独立开关不影响跑环自动战斗；启动跑环会接管独立战斗。"
            };
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
            recovery = new CheckBox { Text = "战斗自动恢复 HP/MP", Checked = true, AutoSize = true };
            toggle = new Button { Text = "开启自动战斗", AutoSize = true };
            toggle.Click += delegate
            {
                try
                {
                    if (coordinator.StandaloneCombatRunning) coordinator.StopStandaloneCombat();
                    else coordinator.StartStandaloneCombat(recovery.Checked);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "自动战斗", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            actions.Controls.Add(toggle);
            actions.Controls.Add(recovery);
            status = new Label { Dock = DockStyle.Top, Height = 80, Text = "独立自动战斗已关闭。" };
            Controls.Add(status);
            Controls.Add(actions);
            Controls.Add(help);
            coordinator.StandaloneCombatStatusChanged += OnStatusChanged;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) coordinator.StandaloneCombatStatusChanged -= OnStatusChanged;
            base.Dispose(disposing);
        }

        private void OnStatusChanged(RunLoopAutomationState state, string message)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<RunLoopAutomationState, string>(OnStatusChanged), state, message); }
                catch (InvalidOperationException) { }
                return;
            }
            bool running = coordinator.StandaloneCombatRunning;
            toggle.Text = running ? "关闭自动战斗" : "开启自动战斗";
            recovery.Enabled = !running;
            status.Text = message;
        }
    }
}
