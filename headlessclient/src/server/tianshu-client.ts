import { performance } from "node:perf_hooks";
import { Socket } from "node:net";
import { BinaryReader, ClientFrameWriter, FrameStream, type Frame } from "../protocol/binary.js";
import { HeartbeatState, SERVER_PING } from "../protocol/heartbeat.js";
import {
  OPCODE,
  buildEnterMapFrame,
  buildSelectRoleFrame,
  buildUserPasswordFrame,
  buildUserToken2Frame,
  findPhysicalLine,
  opcodeName,
  parseClientToken,
  parseGameServerList,
  parseOperateFail,
  parseRoleInfoList,
  parseRoleStartPoint,
  type GameServer,
} from "../protocol/login.js";
import type { GatewayEvent, LoginRequest } from "../shared/messages.js";

type EventSink = (event: GatewayEvent) => void;
type ConnectionKind = "entrance" | "game";

interface ActiveOptions {
  line: 1 | 2 | 3 | 4;
  roleSlot: 1 | 2 | 3 | 4 | 5;
  forceLogin: boolean;
  operationCom: number;
}

export class TianshuClient {
  private socket: Socket | undefined;
  private stream = new FrameStream();
  private writer = new ClientFrameWriter();
  private heartbeat = new HeartbeatState();
  private options: ActiveOptions | undefined;
  private token: string | undefined;
  private servers: GameServer[] | undefined;
  private switchingToGame = false;
  private closedByUser = false;
  private generation = 0;
  private heartbeatWatchdog: NodeJS.Timeout | undefined;

  constructor(private readonly emit: EventSink) {}

  async login(request: LoginRequest): Promise<void> {
    this.disconnect("开始新的登录");
    validateLoginRequest(request);
    const generation = ++this.generation;
    this.closedByUser = false;
    this.options = {
      line: request.line,
      roleSlot: request.roleSlot,
      forceLogin: request.forceLogin,
      operationCom: request.operationCom,
    };
    this.token = undefined;
    this.servers = undefined;
    this.switchingToGame = false;
    this.stream = new FrameStream();
    this.writer = new ClientFrameWriter();
    this.heartbeat = new HeartbeatState();

    this.state("连接入口服", `${request.loginHost}:${request.loginPorts.join(",")}`);
    const socket = await connectAny(request.loginHost, request.loginPorts);
    if (generation !== this.generation) {
      socket.destroy();
      return;
    }
    this.attach(socket, "entrance", generation);
    const credentials = buildUserPasswordFrame(this.writer, request.username, request.password);
    this.send(credentials, true);
    this.state("入口服认证", "凭据已发送，等待票据与线路列表");
  }

  disconnect(reason = "用户断开"): void {
    this.closedByUser = true;
    this.generation += 1;
    this.clearWatchdog();
    const socket = this.socket;
    this.socket = undefined;
    if (socket && !socket.destroyed) socket.destroy();
    this.token = undefined;
    this.servers = undefined;
    this.options = undefined;
    this.switchingToGame = false;
    if (reason !== "开始新的登录") this.emit({ type: "disconnected", reason });
  }

  private attach(socket: Socket, kind: ConnectionKind, generation: number): void {
    this.socket = socket;
    this.stream = new FrameStream();
    socket.setNoDelay(true);
    socket.setKeepAlive(true, 30_000);
    socket.on("data", (chunk) => {
      if (generation !== this.generation || socket !== this.socket) return;
      try {
        const bytes = typeof chunk === "string" ? Buffer.from(chunk) : chunk;
        for (const frame of this.stream.push(bytes)) this.handleFrame(frame, kind, generation);
      } catch (error) {
        this.fail(error);
      }
    });
    socket.on("error", (error) => {
      if (generation === this.generation && socket === this.socket) this.fail(error);
    });
    socket.on("close", () => {
      if (generation !== this.generation || socket !== this.socket) return;
      this.socket = undefined;
      this.clearWatchdog();
      if (!this.closedByUser) this.emit({ type: "disconnected", reason: `${kind === "entrance" ? "入口" : "游戏"}连接已关闭` });
    });
  }

  private handleFrame(frame: Frame, kind: ConnectionKind, generation: number): void {
    this.packet("S→C", frame);
    if (frame.opcode === OPCODE.SC_OPERATE_FAIL) {
      const failure = parseOperateFail(frame);
      throw new Error(`服务端拒绝操作（${failure.category}）：${failure.message}`);
    }
    if (kind === "entrance") {
      if (frame.opcode === OPCODE.SC_CLIENT_TOKEN) this.token = parseClientToken(frame);
      if (frame.opcode === OPCODE.SC_GAMESERVER_LIST) this.servers = parseGameServerList(frame);
      if (this.token && this.servers && !this.switchingToGame) {
        this.switchingToGame = true;
        void this.connectGame(generation).catch((error: unknown) => this.fail(error));
      }
      return;
    }

    if (frame.opcode === OPCODE.SC_ROLE_INFO_LIST) {
      const roles = parseRoleInfoList(frame);
      const slot = this.options?.roleSlot;
      if (!slot) throw new Error("角色槽位配置丢失");
      this.emit({
        type: "roles",
        roles: roles.map((role, index) => ({
          slot: index + 1,
          name: role.name,
          job: role.job,
          level: role.level,
          status: role.status,
        })),
        selectedSlot: slot,
      });
      const selected = roles[slot - 1];
      if (!selected) throw new Error(`第 ${slot} 个角色槽位不存在`);
      if (selected.status === 1) throw new Error(`第 ${slot} 个角色处于删除/恢复状态，已拒绝进入`);
      this.send(buildSelectRoleFrame(this.writer, selected.characterId));
      this.state("选择角色", `已选择第 ${slot} 个角色，等待地图起点`);
      return;
    }

    if (frame.opcode === OPCODE.SC_ROLE_START_POINT) {
      const point = parseRoleStartPoint(frame);
      this.emit({ type: "entered", ...point });
      this.send(buildEnterMapFrame(this.writer));
      this.state("在线", `角色已进入游戏，起点 (${point.x}, ${point.y})`);
      return;
    }

    if (frame.opcode === SERVER_PING) {
      const started = performance.now();
      const result = this.heartbeat.respond(frame, this.writer);
      this.send(result.response);
      const latencyMs = performance.now() - started;
      this.resetWatchdog();
      this.emit({
        type: "heartbeat",
        counter: result.ping.counter,
        systemTime: result.ping.systemTime,
        latencyMs,
        authCodeHex: `0x${result.authCode.toString(16).padStart(8, "0").toUpperCase()}`,
      });
    }
  }

  private async connectGame(generation: number): Promise<void> {
    const token = this.token;
    const servers = this.servers;
    const options = this.options;
    if (!token || !servers || !options) throw new Error("入口服登录状态不完整");
    const selected = findPhysicalLine(servers, options.line);
    this.emit({
      type: "servers",
      servers: servers.map((server) => ({ ...server })),
      selectedId: selected.id,
    });
    this.state("连接游戏服", `${selected.name} ${selected.address}:${selected.ports.join(",")}`);

    const entrance = this.socket;
    this.socket = undefined;
    if (entrance && !entrance.destroyed) entrance.destroy();
    const gameSocket = await connectAny(selected.address, selected.ports);
    if (generation !== this.generation) {
      gameSocket.destroy();
      return;
    }
    this.attach(gameSocket, "game", generation);
    const authFrame = buildUserToken2Frame(
      this.writer,
      token,
      options.forceLogin,
      options.operationCom,
      selected.id,
    );
    this.token = undefined;
    this.send(authFrame, true);
    this.state("游戏服认证", "票据已发送，等待角色列表");
  }

  private send(frame: Buffer, sensitive = false): void {
    const socket = this.socket;
    if (!socket || socket.destroyed) {
      if (sensitive) frame.fill(0);
      throw new Error("当前没有可写的服务器连接");
    }
    const metadata = { opcode: frame.readUInt16BE(2), length: frame.length };
    this.packet("C→S", { ...metadata, bytes: frame });
    socket.write(frame, () => {
      if (sensitive) frame.fill(0);
    });
  }

  private packet(direction: "C→S" | "S→C", frame: Pick<Frame, "opcode" | "bytes">): void {
    this.emit({
      type: "packet",
      direction,
      opcode: frame.opcode,
      length: frame.bytes.length,
      name: opcodeName(frame.opcode, direction),
    });
  }

  private state(state: string, detail: string): void {
    this.emit({ type: "state", state, detail });
  }

  private fail(error: unknown): void {
    const message = error instanceof Error ? error.message : String(error);
    this.emit({ type: "error", message });
    this.disconnect("发生错误，连接已停止");
  }

  private resetWatchdog(): void {
    this.clearWatchdog();
    this.heartbeatWatchdog = setTimeout(() => {
      this.emit({ type: "warning", message: "65 秒内未收到新的 SC_SERVER_PING；连接可能已失活。" });
    }, 65_000);
  }

  private clearWatchdog(): void {
    if (this.heartbeatWatchdog) clearTimeout(this.heartbeatWatchdog);
    this.heartbeatWatchdog = undefined;
  }
}

async function connectAny(host: string, ports: number[]): Promise<Socket> {
  const errors: string[] = [];
  for (const port of ports) {
    try {
      return await connectOne(host, port);
    } catch (error) {
      errors.push(`${port}: ${error instanceof Error ? error.message : String(error)}`);
    }
  }
  throw new Error(`无法连接 ${host} 的候选端口：${errors.join("；")}`);
}

function connectOne(host: string, port: number): Promise<Socket> {
  return new Promise((resolve, reject) => {
    const socket = new Socket();
    const timer = setTimeout(() => finish(new Error("连接超时")), 10_000);
    const finish = (error?: Error): void => {
      clearTimeout(timer);
      socket.removeListener("connect", onConnect);
      socket.removeListener("error", onError);
      if (error) {
        socket.destroy();
        reject(error);
      } else resolve(socket);
    };
    const onConnect = (): void => finish();
    const onError = (error: Error): void => finish(error);
    socket.once("connect", onConnect);
    socket.once("error", onError);
    socket.connect(port, host);
  });
}

function validateLoginRequest(request: LoginRequest): void {
  if (!request.loginHost.trim()) throw new Error("入口服地址不能为空");
  if (!request.username) throw new Error("账号不能为空");
  if (!request.password) throw new Error("密码不能为空");
  if (request.loginPorts.length < 1 || request.loginPorts.some((port) => !Number.isInteger(port) || port < 1 || port > 65535)) {
    throw new Error("入口服端口无效");
  }
  if (![1, 2, 3, 4].includes(request.line)) throw new Error("线路必须为 1 到 4");
  if (![1, 2, 3, 4, 5].includes(request.roleSlot)) throw new Error("角色槽位必须为 1 到 5");
  if (!Number.isInteger(request.operationCom)) throw new Error("运营商编号必须是整数");
}
