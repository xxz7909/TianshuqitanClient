import { readFile } from "node:fs/promises";
import { createServer, type IncomingMessage } from "node:http";
import { dirname, extname, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { WebSocket, WebSocketServer } from "ws";
import type { BrowserCommand, GatewayEvent, LoginRequest } from "../shared/messages.js";
import { TianshuClient } from "./tianshu-client.js";

const host = "127.0.0.1";
const port = parsePort(process.env.HEADLESS_CLIENT_PORT ?? "3210");
const clientDirectory = resolve(dirname(fileURLToPath(import.meta.url)), "../../client");
const allowedOrigins = new Set([
  `http://${host}:${port}`,
  `http://localhost:${port}`,
  "http://127.0.0.1:5173",
  "http://localhost:5173",
]);

const server = createServer(async (request, response) => {
  try {
    const requestPath = new URL(request.url ?? "/", `http://${host}:${port}`).pathname;
    const relative = requestPath === "/" ? "index.html" : decodeURIComponent(requestPath.slice(1));
    const filePath = resolve(clientDirectory, relative);
    if (filePath !== clientDirectory && !filePath.startsWith(`${clientDirectory}${sep}`)) {
      response.writeHead(403).end("Forbidden");
      return;
    }
    const bytes = await readFile(filePath);
    response.writeHead(200, {
      "Content-Type": mimeType(extname(filePath)),
      "Cache-Control": relative === "index.html" ? "no-store" : "public, max-age=31536000, immutable",
      "X-Content-Type-Options": "nosniff",
      "Content-Security-Policy": `default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self' ws://127.0.0.1:${port} ws://localhost:${port}`,
      "Referrer-Policy": "no-referrer",
    });
    response.end(bytes);
  } catch {
    response.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" });
    response.end("先运行 npm run build，或在开发模式打开 http://127.0.0.1:5173");
  }
});

const sockets = new WebSocketServer({
  server,
  path: "/ws",
  maxPayload: 32 * 1024,
  verifyClient: ({ origin, req }: { origin: string; req: IncomingMessage }) => {
    const address = req.socket.remoteAddress;
    const loopback = address === "127.0.0.1" || address === "::1" || address === "::ffff:127.0.0.1";
    return loopback && Boolean(origin && allowedOrigins.has(origin));
  },
});

sockets.on("connection", (webSocket) => {
  const send = (event: GatewayEvent): void => {
    if (webSocket.readyState === WebSocket.OPEN) webSocket.send(JSON.stringify(event));
  };
  const client = new TianshuClient(send);
  send({ type: "state", state: "就绪", detail: "本地协议网关已连接" });

  webSocket.on("message", (data, isBinary) => {
    if (isBinary) {
      send({ type: "error", message: "网关只接受 JSON 文本消息" });
      return;
    }
    try {
      const command = JSON.parse(data.toString()) as BrowserCommand;
      if (command.type === "disconnect") {
        client.disconnect();
        return;
      }
      if (command.type !== "login") throw new Error("未知的网关命令");
      const request = normalizeLoginRequest(command.request);
      void client.login(request).catch((error: unknown) => {
        send({ type: "error", message: error instanceof Error ? error.message : String(error) });
        client.disconnect("登录失败");
      });
    } catch (error) {
      send({ type: "error", message: error instanceof Error ? error.message : String(error) });
    }
  });
  webSocket.on("close", () => client.disconnect("网页已关闭"));
});

server.listen(port, host, () => {
  process.stdout.write(`Tianshu headless client: http://${host}:${port}\n`);
});

function normalizeLoginRequest(value: unknown): LoginRequest {
  if (!value || typeof value !== "object") throw new Error("登录参数格式错误");
  const input = value as Record<string, unknown>;
  const line = Number(input.line);
  const roleSlot = Number(input.roleSlot);
  return {
    loginHost: String(input.loginHost ?? "").trim(),
    loginPorts: Array.isArray(input.loginPorts) ? input.loginPorts.map(Number) : [],
    username: String(input.username ?? ""),
    password: String(input.password ?? ""),
    line: line as LoginRequest["line"],
    roleSlot: roleSlot as LoginRequest["roleSlot"],
    forceLogin: input.forceLogin === true,
    operationCom: Number(input.operationCom),
  };
}

function parsePort(text: string): number {
  const value = Number(text);
  if (!Number.isInteger(value) || value < 1 || value > 65535) throw new Error("HEADLESS_CLIENT_PORT 无效");
  return value;
}

function mimeType(extension: string): string {
  const types: Record<string, string> = {
    ".html": "text/html; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".svg": "image/svg+xml",
    ".json": "application/json; charset=utf-8",
  };
  return types[extension] ?? "application/octet-stream";
}
