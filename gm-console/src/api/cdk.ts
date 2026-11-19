import type { ConsoleAPI } from "./console";

// CDK 兑换码：生成与管理（/api/v1/cdk*，鉴权走 ConsoleAPI 现有会话）。
export interface CdkCodeRow {
  code: string;
  kind: string;
  summary: string;
  maxUses: number;
  usedCount: number;
  status: string;
  redeemed?: string;
  expiresAt?: string;
  note?: string;
  createdAt: string;
}

export interface CdkListFilter {
  mode: "all" | "code" | "note" | "character";
  query?: string;
  characterId?: string;
}

export interface CdkGenerateInput {
  kind: "cera" | "item" | "equipment";
  cera: number;
  itemId: number;
  itemCount: number;
  bind: boolean;
  maxUses: number;
  expiresDays: number;
  count: number;
  note: string;
}

function requestId(): string {
  const raw = new Uint8Array(16);
  crypto.getRandomValues(raw);
  return Array.from(raw, b => b.toString(16).padStart(2, "0")).join("");
}

export async function generateCdk(api: ConsoleAPI, input: CdkGenerateInput): Promise<CdkCodeRow[]> {
  return await api.http.post<CdkCodeRow[]>("/api/v1/cdk", input, requestId());
}

export async function listCdk(
  api: ConsoleAPI,
  page: number,
  filter: CdkListFilter = { mode: "all" },
  pageSize = 50,
): Promise<{ rows: CdkCodeRow[]; total: number; page: number }> {
  const params = new URLSearchParams({
    page: String(page),
    pageSize: String(pageSize),
    mode: filter.mode,
  });
  if (filter.mode === "character" && filter.characterId) {
    params.set("characterId", filter.characterId);
  } else if (filter.query) {
    params.set("q", filter.query);
  }
  const { data } = await api.http.get<{ rows: CdkCodeRow[]; total: number; page: number }>(
    `/api/v1/cdks?${params.toString()}`,
  );
  return data;
}
