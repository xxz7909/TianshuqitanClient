import { createHash } from "node:crypto";
import { BinaryReader, BinaryWriter, ClientFrameWriter, type Frame } from "./binary.js";

export const OPCODE = {
  CS_USER_PASS: 0x0000,
  CS_USER_TOKEN2: 0x0006,
  CS_SELECT_ROLE: 0x000a,
  CS_ENTER_MAP: 0x0047,
  SC_OPERATE_FAIL: 0x0000,
  SC_ACCOUNT_INFO: 0x0001,
  SC_ROLE_INFO_LIST: 0x000c,
  SC_ROLE_START_POINT: 0x0016,
  SC_CLIENT_TOKEN: 0x00c8,
  SC_GAMESERVER_LIST: 0x00c9,
  SC_SERVER_PING: 0x0053,
} as const;

export interface GameServer {
  id: string;
  name: string;
  address: string;
  ports: number[];
  state: number;
}

export interface RoleSummary {
  characterId: number;
  name: string;
  gender: string;
  job: string;
  level: number;
  status: number;
}

export function buildUserPasswordFrame(
  writer: ClientFrameWriter,
  username: string,
  password: string,
): Buffer {
  const body = new BinaryWriter().writeString(username).writeString(password).toBuffer();
  try {
    return writer.encode(OPCODE.CS_USER_PASS, body);
  } finally {
    body.fill(0);
  }
}

export function buildUserToken2Frame(
  writer: ClientFrameWriter,
  token: string,
  forceLogin: boolean,
  operationCom: number,
  serverId: string,
): Buffer {
  const body = new BinaryWriter()
    .writeString(token)
    .writeInt32(forceLogin ? 1 : 0)
    .writeInt32(operationCom)
    .writeString(buildClientSignature(operationCom, serverId))
    .toBuffer();
  try {
    return writer.encode(OPCODE.CS_USER_TOKEN2, body);
  } finally {
    body.fill(0);
  }
}

export function buildSelectRoleFrame(writer: ClientFrameWriter, characterId: number): Buffer {
  return writer.encode(OPCODE.CS_SELECT_ROLE, new BinaryWriter().writeInt32(characterId).toBuffer());
}

export function buildEnterMapFrame(writer: ClientFrameWriter): Buffer {
  const body = new BinaryWriter().writeInt32(0).writeInt32(0).writeInt32(0).writeInt32(0).toBuffer();
  return writer.encode(OPCODE.CS_ENTER_MAP, body);
}

export function buildClientSignature(operationCom: number, serverId: string): string {
  const inner = md5("tspk");
  return `${md5(`${operationCom}${serverId}${inner}`)} `;
}

export function parseClientToken(frame: Frame): string {
  assertOpcode(frame, OPCODE.SC_CLIENT_TOKEN, "SC_CLIENT_TOKEN");
  const reader = new BinaryReader(frame.body);
  const token = reader.readString();
  reader.assertEnd("SC_CLIENT_TOKEN");
  if (!/^[A-Za-z0-9+/]+={0,2}$/.test(token) || token.length % 4 !== 0) {
    throw new Error("SC_CLIENT_TOKEN is not valid Base64 text");
  }
  return token;
}

export function parseGameServerList(frame: Frame): GameServer[] {
  assertOpcode(frame, OPCODE.SC_GAMESERVER_LIST, "SC_GAMESERVER_LIST");
  const reader = new BinaryReader(frame.body);
  const count = reader.readInt32();
  if (count < 0 || count > 100) throw new Error(`unreasonable game server count ${count}`);
  const result: GameServer[] = [];
  for (let index = 0; index < count; index += 1) {
    const id = reader.readString();
    const name = reader.readString();
    const address = reader.readString();
    const portsText = reader.readString();
    const state = reader.readInt32();
    const ports = portsText.split(",").map((part) => Number(part.trim()));
    if (ports.length === 0 || ports.some((port) => !Number.isInteger(port) || port < 1 || port > 65535)) {
      throw new Error(`server ${index + 1} has invalid port data`);
    }
    result.push({ id, name, address, ports, state });
  }
  reader.assertEnd("SC_GAMESERVER_LIST");
  return result;
}

export function findPhysicalLine(servers: GameServer[], line: 1 | 2 | 3 | 4): GameServer {
  const markers = ["一线", "二线", "三线", "四线"] as const;
  const marker = markers[line - 1]!;
  const available = servers.find((server) => server.state !== 4 && server.name.includes(marker));
  const fallback = servers.find((server) => server.name.includes(marker));
  const selected = available ?? fallback;
  if (!selected) throw new Error(`server list does not contain ${marker}`);
  return selected;
}

export function parseRoleInfoList(frame: Frame): RoleSummary[] {
  assertOpcode(frame, OPCODE.SC_ROLE_INFO_LIST, "SC_ROLE_INFO_LIST");
  const reader = new BinaryReader(frame.body);
  const count = reader.readUInt16();
  if (count > 5) throw new Error(`role count ${count} exceeds the five client slots`);
  const result: RoleSummary[] = [];
  for (let index = 0; index < count; index += 1) {
    const characterId = reader.readInt32();
    const name = reader.readString();
    const gender = reader.readString();
    const job = reader.readString();
    const level = reader.readInt32();
    reader.readString(); // portrait image
    reader.readString(); // body image
    reader.readString(); // appearance
    reader.readString(); // nickname
    for (let attribute = 0; attribute < 12; attribute += 1) reader.readInt32();
    reader.readString(); // honor title
    const status = reader.readInt16();
    result.push({ characterId, name, gender, job, level, status });
  }
  reader.assertEnd("SC_ROLE_INFO_LIST");
  return result;
}

export function parseRoleStartPoint(frame: Frame): { x: number; y: number } {
  assertOpcode(frame, OPCODE.SC_ROLE_START_POINT, "SC_ROLE_START_POINT");
  const reader = new BinaryReader(frame.body);
  const result = { x: reader.readInt16(), y: reader.readInt16() };
  reader.assertEnd("SC_ROLE_START_POINT");
  return result;
}

export function parseOperateFail(frame: Frame): { category: number; message: string } {
  assertOpcode(frame, OPCODE.SC_OPERATE_FAIL, "SC_OPERATE_FAIL");
  const reader = new BinaryReader(frame.body);
  const result = { category: reader.readInt16(), message: reader.readString() };
  reader.assertEnd("SC_OPERATE_FAIL");
  return result;
}

export function opcodeName(opcode: number, direction: "C→S" | "S→C"): string {
  const names: Record<number, string> = direction === "C→S"
    ? {
        [OPCODE.CS_USER_PASS]: "CS_USER_PASS",
        [OPCODE.CS_USER_TOKEN2]: "CS_USER_TOKEN2",
        [OPCODE.CS_SELECT_ROLE]: "CS_SELECT_ROLE",
        [OPCODE.CS_ENTER_MAP]: "CS_ENTER_MAP",
        [0x0042]: "CS_CLIENT_PING",
      }
    : {
        [OPCODE.SC_OPERATE_FAIL]: "SC_OPERATE_FAIL",
        [OPCODE.SC_ACCOUNT_INFO]: "SC_ACCOUNT_INFO",
        [OPCODE.SC_ROLE_INFO_LIST]: "SC_ROLE_INFO_LIST",
        [OPCODE.SC_ROLE_START_POINT]: "SC_ROLE_START_POINT",
        [OPCODE.SC_CLIENT_TOKEN]: "SC_CLIENT_TOKEN",
        [OPCODE.SC_GAMESERVER_LIST]: "SC_GAMESERVER_LIST",
        [OPCODE.SC_SERVER_PING]: "SC_SERVER_PING",
      };
  return names[opcode] ?? `0x${opcode.toString(16).padStart(4, "0")}`;
}

function md5(text: string): string {
  return createHash("md5").update(text, "utf8").digest("hex");
}

function assertOpcode(frame: Frame, expected: number, label: string): void {
  if (frame.opcode !== expected) throw new Error(`not a ${label} frame`);
}
