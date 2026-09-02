const MAX_FRAME_LENGTH = 0xffff;

export class BinaryReader {
  private offset = 0;

  constructor(private readonly bytes: Buffer) {}

  get remaining(): number {
    return this.bytes.length - this.offset;
  }

  get position(): number {
    return this.offset;
  }

  readUInt16(): number {
    this.require(2);
    const value = this.bytes.readUInt16BE(this.offset);
    this.offset += 2;
    return value;
  }

  readInt16(): number {
    this.require(2);
    const value = this.bytes.readInt16BE(this.offset);
    this.offset += 2;
    return value;
  }

  readUInt32(): number {
    this.require(4);
    const value = this.bytes.readUInt32BE(this.offset);
    this.offset += 4;
    return value;
  }

  readInt32(): number {
    this.require(4);
    const value = this.bytes.readInt32BE(this.offset);
    this.offset += 4;
    return value;
  }

  readString(): string {
    const length = this.readUInt16();
    this.require(length);
    const value = this.bytes.toString("utf8", this.offset, this.offset + length);
    this.offset += length;
    return value;
  }

  assertEnd(label: string): void {
    if (this.remaining !== 0) {
      throw new Error(`${label} has ${this.remaining} trailing byte(s)`);
    }
  }

  private require(length: number): void {
    if (length < 0 || this.offset + length > this.bytes.length) {
      throw new Error(`frame body is truncated at offset ${this.offset}`);
    }
  }
}

export class BinaryWriter {
  private readonly parts: Buffer[] = [];
  private length = 0;

  writeUInt16(value: number): this {
    const bytes = Buffer.allocUnsafe(2);
    bytes.writeUInt16BE(value & 0xffff);
    return this.push(bytes);
  }

  writeInt16(value: number): this {
    const bytes = Buffer.allocUnsafe(2);
    bytes.writeInt16BE(value);
    return this.push(bytes);
  }

  writeUInt32(value: number): this {
    const bytes = Buffer.allocUnsafe(4);
    bytes.writeUInt32BE(value >>> 0);
    return this.push(bytes);
  }

  writeInt32(value: number): this {
    const bytes = Buffer.allocUnsafe(4);
    bytes.writeInt32BE(value | 0);
    return this.push(bytes);
  }

  writeString(value: string): this {
    const bytes = Buffer.from(value, "utf8");
    if (bytes.length > MAX_FRAME_LENGTH) throw new Error("string is too long for u16 framing");
    this.writeUInt16(bytes.length);
    return this.push(bytes);
  }

  toBuffer(): Buffer {
    return Buffer.concat(this.parts, this.length);
  }

  private push(bytes: Buffer): this {
    this.parts.push(bytes);
    this.length += bytes.length;
    return this;
  }
}

export interface Frame {
  opcode: number;
  body: Buffer;
  bytes: Buffer;
}

export class FrameStream {
  private pending = Buffer.alloc(0);

  push(chunk: Buffer): Frame[] {
    this.pending = this.pending.length === 0 ? Buffer.from(chunk) : Buffer.concat([this.pending, chunk]);
    const frames: Frame[] = [];
    let offset = 0;
    while (this.pending.length - offset >= 4) {
      const length = this.pending.readUInt16BE(offset);
      if (length < 4) throw new Error(`invalid frame length ${length}`);
      if (this.pending.length - offset < length) break;
      const bytes = Buffer.from(this.pending.subarray(offset, offset + length));
      frames.push({ opcode: bytes.readUInt16BE(2), body: bytes.subarray(4), bytes });
      offset += length;
    }
    this.pending = offset === 0 ? this.pending : Buffer.from(this.pending.subarray(offset));
    return frames;
  }
}

export class ClientFrameWriter {
  private messageNumber = 0;

  encode(opcode: number, body: Buffer = Buffer.alloc(0)): Buffer {
    const sequenced = opcode >= 20 && opcode < 2000;
    const tailLength = sequenced ? 4 : 0;
    const frameLength = 4 + body.length + tailLength;
    if (frameLength > MAX_FRAME_LENGTH) throw new Error("frame exceeds u16 length limit");
    const frame = Buffer.allocUnsafe(frameLength);
    frame.writeUInt16BE(frameLength, 0);
    frame.writeUInt16BE(opcode & 0xffff, 2);
    body.copy(frame, 4);
    if (sequenced) frame.writeInt32BE(this.nextMessageNumber(), frameLength - 4);
    return frame;
  }

  get currentMessageNumber(): number {
    return this.messageNumber;
  }

  private nextMessageNumber(): number {
    this.messageNumber = this.messageNumber === 0x7fffffff ? 1 : this.messageNumber + 1;
    return this.messageNumber;
  }
}
