import { createServer, type Server, type Socket } from "node:net";
import { afterEach, describe, expect, it } from "vitest";
import { BinaryWriter, FrameStream, type Frame } from "../protocol/binary.js";
import { OPCODE } from "../protocol/login.js";
import type { GatewayEvent, LoginRequest } from "../shared/messages.js";
import { TianshuClient } from "./tianshu-client.js";

const serversToClose: Server[] = [];
const socketsToClose = new Set<Socket>();

afterEach(async () => {
  for (const socket of socketsToClose) socket.destroy();
  socketsToClose.clear();
  await Promise.all(serversToClose.splice(0).map((server) => new Promise<void>((resolve) => {
    server.close(() => resolve());
  })));
});

describe("TianshuClient", () => {
  it("logs in through fake entrance/game servers, enters the map, and answers bootstrap ping", async () => {
    const events: GatewayEvent[] = [];
    const receivedGameFrames: Frame[] = [];
    const token = Buffer.alloc(48, 0x2a).toString("base64");
    let finish!: () => void;
    const completed = new Promise<void>((resolve) => { finish = resolve; });

    const game = await listen((socket) => {
      const stream = new FrameStream();
      socket.on("data", (chunk) => {
        for (const frame of stream.push(Buffer.from(chunk))) {
          receivedGameFrames.push(frame);
          if (frame.opcode === OPCODE.CS_USER_TOKEN2) socket.write(roleListFrame());
          if (frame.opcode === OPCODE.CS_SELECT_ROLE) {
            socket.write(Buffer.concat([roleStartPointFrame(), Buffer.from(BOOTSTRAP_REQUEST, "hex")]));
          }
          if (frame.opcode === 0x0042) finish();
        }
      });
    });

    const entrance = await listen((socket) => {
      const stream = new FrameStream();
      socket.on("data", (chunk) => {
        for (const frame of stream.push(Buffer.from(chunk))) {
          expect(frame.opcode).toBe(OPCODE.CS_USER_PASS);
          socket.write(Buffer.concat([clientTokenFrame(token), serverListFrame(game.port)]));
        }
      });
    });

    const client = new TianshuClient((event) => events.push(event));
    const request: LoginRequest = {
      loginHost: "127.0.0.1",
      loginPorts: [entrance.port],
      username: "test-user",
      password: "test-password",
      line: 4,
      roleSlot: 1,
      forceLogin: false,
      operationCom: 0,
    };
    await client.login(request);
    await Promise.race([completed, rejectAfter(2_000)]);
    client.disconnect("测试结束");

    expect(events.some((event) => event.type === "entered" && event.x === 29 && event.y === 100)).toBe(true);
    expect(events.some((event) => event.type === "heartbeat" && event.authCodeHex === "0x4A8B5968")).toBe(true);
    expect(receivedGameFrames.map((frame) => frame.opcode)).toEqual([0x0006, 0x000a, 0x0047, 0x0042]);
    const heartbeat = receivedGameFrames.at(-1)!;
    expect(heartbeat.bytes.toString("hex").toUpperCase()).toBe(
      "0018004200000000000001A05FFBF5544A8B596800000002",
    );
  });
});

const BOOTSTRAP_REQUEST = "003C005300000000000001A05FFBF554000003E8000000123063643665396665306135313863383663610010323032362D30392D30322031303A3339";

async function listen(onConnection: (socket: Socket) => void): Promise<{ server: Server; port: number }> {
  const server = createServer((socket) => {
    socketsToClose.add(socket);
    socket.once("close", () => socketsToClose.delete(socket));
    onConnection(socket);
  });
  serversToClose.push(server);
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => resolve());
  });
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("fake server has no TCP address");
  return { server, port: address.port };
}

function clientTokenFrame(token: string): Buffer {
  return serverFrame(OPCODE.SC_CLIENT_TOKEN, new BinaryWriter().writeString(token).toBuffer());
}

function serverListFrame(gamePort: number): Buffer {
  const body = new BinaryWriter()
    .writeInt32(1)
    .writeString("9054")
    .writeString("春山如笑四线")
    .writeString("127.0.0.1")
    .writeString(String(gamePort))
    .writeInt32(2)
    .toBuffer();
  return serverFrame(OPCODE.SC_GAMESERVER_LIST, body);
}

function roleListFrame(): Buffer {
  const writer = new BinaryWriter()
    .writeUInt16(1)
    .writeInt32(123456)
    .writeString("测试角色")
    .writeString("男")
    .writeString("侠客")
    .writeInt32(92)
    .writeString("portrait")
    .writeString("body")
    .writeString("")
    .writeString("");
  for (let index = 0; index < 12; index += 1) writer.writeInt32(index);
  writer.writeString("").writeInt16(0);
  return serverFrame(OPCODE.SC_ROLE_INFO_LIST, writer.toBuffer());
}

function roleStartPointFrame(): Buffer {
  return serverFrame(
    OPCODE.SC_ROLE_START_POINT,
    new BinaryWriter().writeInt16(29).writeInt16(100).toBuffer(),
  );
}

function serverFrame(opcode: number, body: Buffer): Buffer {
  const frame = Buffer.alloc(4 + body.length);
  frame.writeUInt16BE(frame.length, 0);
  frame.writeUInt16BE(opcode, 2);
  body.copy(frame, 4);
  return frame;
}

function rejectAfter(milliseconds: number): Promise<never> {
  return new Promise((_, reject) => setTimeout(() => reject(new Error("fake login timed out")), milliseconds));
}
