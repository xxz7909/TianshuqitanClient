import "./style.css";
import type { BrowserCommand, GatewayEvent, PublicRole, PublicServer } from "../shared/messages.js";

const form = required<HTMLFormElement>("login-form");
const loginButton = required<HTMLButtonElement>("login-button");
const gatewayPill = required<HTMLElement>("gateway-pill");
const stateText = required<HTMLElement>("session-state");
const detailText = required<HTMLElement>("session-detail");
const packetLog = required<HTMLElement>("packet-log");
const serverList = required<HTMLElement>("server-list");
const roleList = required<HTMLElement>("role-list");
const heartbeatCount = required<HTMLElement>("heartbeat-count");
const heartbeatLatency = required<HTMLElement>("heartbeat-latency");
const packetCount = required<HTMLElement>("packet-count");
let socket: WebSocket | undefined;
let packetTotal = 0;
let reconnectTimer: number | undefined;

connectGateway();

form.addEventListener("submit", (event) => {
  event.preventDefault();
  const password = required<HTMLInputElement>("password");
  const command: BrowserCommand = {
    type: "login",
    request: {
      loginHost: value("login-host"),
      loginPorts: value("login-ports").split(",").map((part) => Number(part.trim())),
      username: value("username"),
      password: password.value,
      line: Number(value("line")) as 1 | 2 | 3 | 4,
      roleSlot: Number(value("role-slot")) as 1 | 2 | 3 | 4 | 5,
      forceLogin: required<HTMLInputElement>("force-login").checked,
      operationCom: Number(value("operation-com")),
    },
  };
  if (!send(command)) return;
  password.value = "";
  loginButton.disabled = true;
  updateState("正在登录", "请求已交给本机协议网关。", "working");
});

required<HTMLButtonElement>("disconnect-button").addEventListener("click", () => {
  send({ type: "disconnect" });
  loginButton.disabled = false;
});

required<HTMLButtonElement>("clear-log").addEventListener("click", () => {
  packetLog.replaceChildren();
});

function connectGateway(): void {
  if (reconnectTimer !== undefined) window.clearTimeout(reconnectTimer);
  const development = window.location.port === "5173";
  const gatewayHost = development ? `${window.location.hostname}:3210` : window.location.host;
  socket = new WebSocket(`ws://${gatewayHost}/ws`);
  socket.addEventListener("open", () => {
    gatewayPill.className = "connection-pill online";
    gatewayPill.querySelector("b")!.textContent = "本地网关在线";
    loginButton.disabled = false;
  });
  socket.addEventListener("message", (event) => {
    try {
      handleEvent(JSON.parse(String(event.data)) as GatewayEvent);
    } catch {
      appendMessage("error", "收到无法解析的网关消息");
    }
  });
  socket.addEventListener("close", () => {
    gatewayPill.className = "connection-pill offline";
    gatewayPill.querySelector("b")!.textContent = "网关已断开";
    loginButton.disabled = true;
    reconnectTimer = window.setTimeout(connectGateway, 1_500);
  });
}

function handleEvent(event: GatewayEvent): void {
  switch (event.type) {
    case "state":
      updateState(event.state, event.detail, event.state === "在线" ? "online" : "working");
      appendMessage("state", `${event.state} · ${event.detail}`);
      break;
    case "servers":
      renderServers(event.servers, event.selectedId);
      break;
    case "roles":
      renderRoles(event.roles, event.selectedSlot);
      break;
    case "entered":
      appendMessage("success", `角色起点 (${event.x}, ${event.y})，已发送进图请求`);
      loginButton.disabled = false;
      break;
    case "heartbeat":
      heartbeatCount.textContent = String(event.counter);
      heartbeatLatency.textContent = `${event.latencyMs.toFixed(2)} ms`;
      appendMessage("heartbeat", `PING #${event.counter} ${event.authCodeHex} · ${event.systemTime}`);
      break;
    case "packet":
      packetTotal += 1;
      packetCount.textContent = String(packetTotal);
      appendPacket(event.direction, event.opcode, event.name, event.length);
      break;
    case "warning":
      appendMessage("warning", event.message);
      break;
    case "error":
      updateState("发生错误", event.message, "error");
      appendMessage("error", event.message);
      loginButton.disabled = false;
      break;
    case "disconnected":
      updateState("已断开", event.reason, "idle");
      appendMessage("state", event.reason);
      loginButton.disabled = false;
      break;
  }
}

function renderServers(servers: PublicServer[], selectedId: string): void {
  serverList.replaceChildren();
  serverList.className = "item-list";
  for (const server of servers) {
    const item = document.createElement("div");
    item.className = `data-item${server.id === selectedId ? " selected" : ""}`;
    const title = document.createElement("b");
    title.textContent = server.name;
    const detail = document.createElement("span");
    detail.textContent = `${server.address}:${server.ports.join(",")} · ID ${server.id}`;
    item.append(title, detail);
    serverList.append(item);
  }
}

function renderRoles(roles: PublicRole[], selectedSlot: number): void {
  roleList.replaceChildren();
  roleList.className = "item-list";
  for (const role of roles) {
    const item = document.createElement("div");
    item.className = `data-item${role.slot === selectedSlot ? " selected" : ""}`;
    const title = document.createElement("b");
    title.textContent = `${role.slot}. ${role.name}`;
    const detail = document.createElement("span");
    detail.textContent = `${role.job} · Lv.${role.level}${role.status === 1 ? " · 删除恢复中" : ""}`;
    item.append(title, detail);
    roleList.append(item);
  }
}

function appendPacket(direction: "C→S" | "S→C", opcode: number, name: string, length: number): void {
  const row = document.createElement("div");
  row.className = "log-row";
  const values = [
    new Date().toLocaleTimeString("zh-CN", { hour12: false }),
    direction,
    `${name} / 0x${opcode.toString(16).padStart(4, "0").toUpperCase()}`,
    `${length} B`,
  ];
  for (const text of values) {
    const cell = document.createElement("span");
    cell.textContent = text;
    row.append(cell);
  }
  prependCapped(row);
}

function appendMessage(kind: string, text: string): void {
  const row = document.createElement("div");
  row.className = `log-message ${kind}`;
  const time = document.createElement("time");
  time.textContent = new Date().toLocaleTimeString("zh-CN", { hour12: false });
  const message = document.createElement("span");
  message.textContent = text;
  row.append(time, message);
  prependCapped(row);
}

function prependCapped(row: HTMLElement): void {
  packetLog.prepend(row);
  while (packetLog.childElementCount > 200) packetLog.lastElementChild?.remove();
}

function updateState(state: string, detail: string, mode: string): void {
  stateText.textContent = state;
  detailText.textContent = detail;
  stateText.dataset.mode = mode;
}

function send(command: BrowserCommand): boolean {
  if (!socket || socket.readyState !== WebSocket.OPEN) {
    appendMessage("error", "本地协议网关尚未连接");
    return false;
  }
  socket.send(JSON.stringify(command));
  return true;
}

function value(id: string): string {
  return required<HTMLInputElement | HTMLSelectElement>(id).value;
}

function required<T extends HTMLElement>(id: string): T {
  const element = document.getElementById(id);
  if (!element) throw new Error(`missing element #${id}`);
  return element as T;
}
