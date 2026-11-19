import type { ConsoleAPI } from "./console";

// 注册限制管理（/api/v1/registration/*，鉴权走 ConsoleAPI 现有会话）。
// 数据在游戏服，网关经管理通道转发；邀请码用于网吧等共享出口绕过
// 一机一号 + 一 IP 一号的双重限制。

export interface RegistrationInvite {
  code: string;
  maxUses: number;
  used: number;
  expiresAt?: string;
  revoked?: boolean;
  note?: string;
  createdAt: string;
}

export interface RegistrationRecord {
  accountId: string;
  account?: string;
  ip?: string;
  device?: string;
  invite?: string;
  createdAt?: string;
}

export interface RegistrationInviteInput {
  op: "create" | "revoke";
  code?: string;
  count?: number;
  maxUses?: number;
  expiresDays?: number;
  note?: string;
}

function requestId(): string {
  const raw = new Uint8Array(16);
  crypto.getRandomValues(raw);
  return Array.from(raw, b => b.toString(16).padStart(2, "0")).join("");
}

export async function listRegistrationInvites(
  api: ConsoleAPI,
  page = 1,
  pageSize = 50,
): Promise<{ rows: RegistrationInvite[]; total: number; page: number }> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
  const { data } = await api.http.get<{ rows: RegistrationInvite[]; total: number; page: number }>(
    `/api/v1/registration/invites?${params.toString()}`,
  );
  return data;
}

export async function saveRegistrationInvite(
  api: ConsoleAPI,
  input: RegistrationInviteInput,
): Promise<{ codes: string[]; changed: boolean }> {
  return await api.http.post<{ codes: string[]; changed: boolean }>(
    "/api/v1/registration/invite",
    input,
    requestId(),
  );
}

export async function listRegistrationRecords(
  api: ConsoleAPI,
  page = 1,
  pageSize = 50,
): Promise<{ rows: RegistrationRecord[]; total: number; page: number }> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
  const { data } = await api.http.get<{ rows: RegistrationRecord[]; total: number; page: number }>(
    `/api/v1/registration/records?${params.toString()}`,
  );
  return data;
}

export async function registrationUnbind(
  api: ConsoleAPI,
  kind: "device" | "ip",
  value: string,
): Promise<{ changed: boolean }> {
  return await api.http.post<{ changed: boolean }>(
    "/api/v1/registration/unbind",
    { kind, value },
    requestId(),
  );
}
