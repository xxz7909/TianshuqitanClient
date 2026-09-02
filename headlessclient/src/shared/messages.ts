export interface LoginRequest {
  loginHost: string;
  loginPorts: number[];
  username: string;
  password: string;
  line: 1 | 2 | 3 | 4;
  roleSlot: 1 | 2 | 3 | 4 | 5;
  forceLogin: boolean;
  operationCom: number;
}

export type BrowserCommand =
  | { type: "login"; request: LoginRequest }
  | { type: "disconnect" };

export interface PublicServer {
  id: string;
  name: string;
  address: string;
  ports: number[];
  state: number;
}

export interface PublicRole {
  slot: number;
  name: string;
  job: string;
  level: number;
  status: number;
}

export type GatewayEvent =
  | { type: "state"; state: string; detail: string }
  | { type: "servers"; servers: PublicServer[]; selectedId: string }
  | { type: "roles"; roles: PublicRole[]; selectedSlot: number }
  | { type: "entered"; x: number; y: number }
  | { type: "heartbeat"; counter: number; systemTime: string; latencyMs: number; authCodeHex: string }
  | { type: "packet"; direction: "C→S" | "S→C"; opcode: number; length: number; name: string }
  | { type: "warning"; message: string }
  | { type: "error"; message: string }
  | { type: "disconnected"; reason: string };
