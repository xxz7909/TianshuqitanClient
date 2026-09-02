import { BinaryReader, BinaryWriter, ClientFrameWriter, type Frame } from "./binary.js";

export const SERVER_PING = 0x0053;
export const CLIENT_PING = 0x0042;
export const AUTH_FIRST_SEND = 0;
export const AUTH_SEED_KEY = "The.tianshu.game.need.your.support";
export const AUTH_INCREMENT = 931_209_513;

export interface ServerPing {
  counter: number;
  serverTimeHigh: number;
  serverTimeLow: number;
  serverEpochMs: bigint;
  cityRate: number;
  authFlag: number;
  authText: string;
  systemTime: string;
}

export function parseServerPing(frame: Frame): ServerPing {
  if (frame.opcode !== SERVER_PING) throw new Error("not an SC_SERVER_PING frame");
  const reader = new BinaryReader(frame.body);
  const counter = reader.readInt32();
  const serverTimeHigh = reader.readUInt32();
  const serverTimeLow = reader.readUInt32();
  const cityRate = reader.readInt32();
  const authFlag = reader.readInt16();
  const authText = reader.readString();
  const systemTime = reader.readString();
  reader.assertEnd("SC_SERVER_PING");
  return {
    counter,
    serverTimeHigh,
    serverTimeLow,
    serverEpochMs: (BigInt(serverTimeHigh) << 32n) | BigInt(serverTimeLow),
    cityRate,
    authFlag,
    authText,
    systemTime,
  };
}

export function rc4(input: Buffer, keyText = AUTH_SEED_KEY): Buffer {
  const key = Buffer.from(keyText, "latin1");
  if (key.length === 0) throw new Error("RC4 key must not be empty");
  const state = Array.from({ length: 256 }, (_, index) => index);
  let j = 0;
  for (let i = 0; i < 256; i += 1) {
    j = (j + state[i]! + key[i % key.length]!) & 0xff;
    [state[i], state[j]] = [state[j]!, state[i]!];
  }
  const output = Buffer.allocUnsafe(input.length);
  let i = 0;
  j = 0;
  for (let offset = 0; offset < input.length; offset += 1) {
    i = (i + 1) & 0xff;
    j = (j + state[i]!) & 0xff;
    [state[i], state[j]] = [state[j]!, state[i]!];
    output[offset] = input[offset]! ^ state[(state[i]! + state[j]!) & 0xff]!;
  }
  return output;
}

export function decryptInitialAuthCode(cipherHex: string): number {
  if (!/^(?:[0-9a-fA-F]{2})+$/.test(cipherHex)) {
    throw new Error("initial heartbeat auth text is not an even-length hex string");
  }
  const plainText = rc4(Buffer.from(cipherHex, "hex")).toString("latin1");
  if (!/^\d+$/.test(plainText)) throw new Error("initial heartbeat auth seed is not decimal");
  const value = Number(plainText);
  if (!Number.isSafeInteger(value) || value < 0 || value > 0xffffffff) {
    throw new Error("initial heartbeat auth seed is outside uint32 range");
  }
  return value >>> 0;
}

export function transformAuthCode(authCode: number): number {
  const sum = (authCode + AUTH_INCREMENT) >>> 0;
  return ((sum >>> 3) | ((sum & 0x7) << 29)) >>> 0;
}

export class HeartbeatState {
  private authCode: number | undefined;

  respond(frame: Frame, writer: ClientFrameWriter): { ping: ServerPing; response: Buffer; authCode: number } {
    const ping = parseServerPing(frame);
    if (ping.authFlag === AUTH_FIRST_SEND) this.authCode = decryptInitialAuthCode(ping.authText);
    if (this.authCode === undefined) {
      throw new Error("heartbeat bootstrap was missed: no authentication seed is available");
    }
    this.authCode = transformAuthCode(this.authCode);
    const body = new BinaryWriter()
      .writeUInt32(ping.counter)
      .writeUInt32(ping.serverTimeHigh)
      .writeUInt32(ping.serverTimeLow)
      .writeUInt32(this.authCode)
      .toBuffer();
    return { ping, response: writer.encode(CLIENT_PING, body), authCode: this.authCode };
  }
}
