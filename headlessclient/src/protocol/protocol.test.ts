import { describe, expect, it } from "vitest";
import { ClientFrameWriter, FrameStream } from "./binary.js";
import { HeartbeatState, decryptInitialAuthCode, transformAuthCode } from "./heartbeat.js";
import { buildClientSignature, buildUserToken2Frame } from "./login.js";

const BOOTSTRAP_REQUEST = "003C005300000000000001A05FFBF554000003E8000000123063643665396665306135313863383663610010323032362D30392D30322031303A3339";
const BOOTSTRAP_RESPONSE = "0018004200000000000001A05FFBF5544A8B59680000000A";
const SECOND_REQUEST = "0032005300000001000001A05FFC6A84000000090009000837323434313738370010323032362D30392D30322031303A3339";
const SECOND_RESPONSE = "0018004200000001000001A05FFC6A8430418F520000000B";

const CAPTURED_AUTH_CODES = [
  0x4a8b5968, 0x30418f52, 0x6cf8560f, 0x148f2ee7, 0x09820a02,
  0x68206565, 0xd3f430d1, 0x416eaa3f, 0x0f1df96d, 0xc8d3e352,
  0x600aa08f, 0x12f17837, 0x094e532c, 0xa819ee8a, 0x7bf361f6,
  0xf66e9063, 0x85bdf631, 0x57a7e2eb, 0x91e52082, 0x792cc835,
  0xd615bd2b,
];

describe("heartbeat protocol", () => {
  it("decrypts the bootstrap seed and reproduces every recorded auth value", () => {
    let authCode = decryptInitialAuthCode("0cd6e9fe0a518c86ca");
    expect(authCode).toBe(484_026_905);
    for (const expected of CAPTURED_AUTH_CODES) {
      authCode = transformAuthCode(authCode);
      expect(authCode).toBe(expected);
    }
  });

  it("builds byte-exact counter 0 and counter 1 responses", () => {
    const stream = new FrameStream();
    const [bootstrap] = stream.push(Buffer.from(BOOTSTRAP_REQUEST, "hex"));
    const [second] = stream.push(Buffer.from(SECOND_REQUEST, "hex"));
    expect(bootstrap).toBeDefined();
    expect(second).toBeDefined();

    const writer = new ClientFrameWriter();
    for (let index = 0; index < 9; index += 1) writer.encode(20);
    const state = new HeartbeatState();
    expect(state.respond(bootstrap!, writer).response.toString("hex").toUpperCase()).toBe(BOOTSTRAP_RESPONSE);
    expect(state.respond(second!, writer).response.toString("hex").toUpperCase()).toBe(SECOND_RESPONSE);
  });

  it("buffers split TCP data and emits complete frames only", () => {
    const bytes = Buffer.from(SECOND_REQUEST, "hex");
    const stream = new FrameStream();
    expect(stream.push(bytes.subarray(0, 3))).toEqual([]);
    expect(stream.push(bytes.subarray(3, 17))).toEqual([]);
    const frames = stream.push(bytes.subarray(17));
    expect(frames).toHaveLength(1);
    expect(frames[0]!.opcode).toBe(0x0053);
  });
});

describe("login protocol", () => {
  it("reproduces the original client signature formula", () => {
    expect(buildClientSignature(0, "9051")).toBe("5373df0c4d11c90d419e8a6b16f2ce02 ");
  });

  it("does not append a message sequence to CS_USER_TOKEN2", () => {
    const writer = new ClientFrameWriter();
    const frame = buildUserToken2Frame(writer, "A".repeat(64), true, 0, "9051");
    expect(frame.length).toBe(113);
    expect(frame.readUInt16BE(0)).toBe(113);
    expect(frame.readUInt16BE(2)).toBe(6);
    expect(frame.subarray(frame.length - 35).toString("utf8")).toBe("\u0000!5373df0c4d11c90d419e8a6b16f2ce02 ");
    expect(writer.currentMessageNumber).toBe(0);
  });
});
