import { useState } from "react";
import { Download, RefreshCw, Search, Ticket } from "lucide-react";
import { useLive } from "../api/context";
import {
  generateCdk,
  listCdk,
  type CdkCodeRow,
  type CdkGenerateInput,
  type CdkListFilter,
} from "../api/cdk";
import { Empty, Field, Pill, timestamp, type PageProps } from "../ui";

// CDK 管理：生成兑换码（点券/道具/装备）+ 查询（全部/按码/按名称/按兑换角色）。
// 兑换端点在网关公开口（/api/cdk/*，登录器直调），这里只管码。
// 列表默认不加载，需主动查询，避免每次进入页面全量拉表。
const statusPill: Record<string, { label: string; tone: string }> = {
  "未使用": { label: "未使用", tone: "green" },
  "部分使用": { label: "部分使用", tone: "gold" },
  "已用完": { label: "已用完", tone: "neutral" },
  "已过期": { label: "已过期", tone: "pink" },
};

// 查询分页大小：服务端按页返回（1-200），前端固定每页 50 条。
const PAGE_SIZE = 50;

export function CdkPage({}: PageProps) {
  const live = useLive();
  const [kind, setKind] = useState<"cera" | "item" | "equipment">("cera");
  const [cera, setCera] = useState(1000);
  const [itemId, setItemId] = useState(0);
  const [itemCount, setItemCount] = useState(1);
  const [expiresDays, setExpiresDays] = useState(30);
  const [count, setCount] = useState(10);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [generated, setGenerated] = useState<CdkCodeRow[]>([]);
  // 查询区：mode 决定过滤维度，searchText 是当前输入（query 或角色 ID）。
  const [mode, setMode] = useState<CdkListFilter["mode"]>("all");
  const [searchText, setSearchText] = useState("");
  const [filter, setFilter] = useState<CdkListFilter>({ mode: "all" });
  const [rows, setRows] = useState<CdkCodeRow[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [listError, setListError] = useState("");
  const [listBusy, setListBusy] = useState(false);
  const [searched, setSearched] = useState(false);

  // CDK 名称自动生成：类型+参数（点券1000 / 装备12345 / 道具6789×2），存入备注。
  const cdkName =
    kind === "cera"
      ? `点券${Math.trunc(Number(cera)) || 0}`
      : kind === "equipment"
        ? `装备${Math.trunc(Number(itemId)) || 0}`
        : `道具${Math.trunc(Number(itemId)) || 0}×${Math.trunc(Number(itemCount)) || 0}`;

  async function submit() {
    if (!live || busy) return;
    setBusy(true);
    setError("");
    try {
      const input: CdkGenerateInput = {
        kind, cera: Math.trunc(Number(cera)) || 0, itemId: Math.trunc(Number(itemId)) || 0,
        itemCount: Math.trunc(Number(itemCount)) || 0, bind: false,
        maxUses: 1, expiresDays: Math.trunc(Number(expiresDays)) || 0,
        count: Math.trunc(Number(count)) || 1, note: note.trim() || cdkName,
      };
      const out = await generateCdk(live, input);
      setGenerated(out);
      exportTxt(out);
      await load(1, filter);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  // 生成的码自动导出 txt（文件名=当前时间），行格式「,码\t名称」，供直接分发。
  function exportTxt(rowsToExport: CdkCodeRow[]) {
    const content = rowsToExport.map(r => `,${r.code}\t${r.note || r.summary}`).join("\r\n");
    const stamp = new Date();
    const pad = (n: number) => String(n).padStart(2, "0");
    const name =
      `${stamp.getFullYear()}${pad(stamp.getMonth() + 1)}${pad(stamp.getDate())}` +
      `-${pad(stamp.getHours())}${pad(stamp.getMinutes())}${pad(stamp.getSeconds())}.txt`;
    const blob = new Blob(["\ufeff" + content], { type: "text/plain;charset=utf-8" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 5000);
  }

  async function load(target: number, f: CdkListFilter) {
    if (!live || listBusy) return;
    setListBusy(true);
    setListError("");
    try {
      const reply = await listCdk(live, target, f);
      setRows(reply.rows ?? []);
      setTotal(reply.total ?? 0);
      setPage(reply.page ?? target);
      setFilter(f);
      setSearched(true);
    } catch (e) {
      setListError(e instanceof Error ? e.message : String(e));
    } finally {
      setListBusy(false);
    }
  }

  function search() {
    const text = searchText.trim();
    const f: CdkListFilter =
      mode === "character"
        ? { mode, characterId: text }
        : { mode, query: mode === "all" && !text ? undefined : text };
    void load(1, f);
  }

  const kindPill = (value: string) =>
    value === "cera" ? { label: "点券", tone: "gold" } : value === "equipment" ? { label: "装备", tone: "purple" } : { label: "道具", tone: "cyan" };

  return (
    <div className="page" aria-label="CDK 管理">
      <header className="page-head">
        <div>
          <h2>
            <Ticket size={20} /> CDK 兑换码
          </h2>
          <p>生成兑换码，玩家在登录器「CDK 兑换」中用账号密码直接兑换。点券直接入账，道具与装备通过游戏内邮箱（标题「CDK兑换」）发放。</p>
        </div>
        <button className="btn ghost" onClick={() => void load(page, filter)} disabled={!live || listBusy}>
          <RefreshCw size={15} /> 刷新
        </button>
      </header>

      <section className="panel compose">
        <div className="grid">
          <Field label="类型">
            <select value={kind} onChange={e => setKind(e.target.value as typeof kind)}>
              <option value="cera">点券</option>
              <option value="item">道具</option>
              <option value="equipment">装备</option>
            </select>
          </Field>
          {kind === "cera" ? (
            <Field label="点券面额" hint="直充账号共享钱包">
              <input type="number" min={1} max={2147483647} value={cera} onChange={e => setCera(Math.trunc(Number(e.target.value)) || 0)} />
            </Field>
          ) : (
            <>
              <Field label="物品 ID" hint={kind === "equipment" ? "装备在 PVF 中的 itemID" : "道具在 PVF 中的 itemID"}>
                <input type="number" min={1} max={2147483647} value={itemId} onChange={e => setItemId(Math.trunc(Number(e.target.value)) || 0)} />
              </Field>
              {kind === "item" && (
                <Field label="数量">
                  <input type="number" min={1} max={2147483647} value={itemCount} onChange={e => setItemCount(Math.trunc(Number(e.target.value)) || 0)} />
                </Field>
              )}
            </>
          )}
          <Field label="有效期（天）" hint="0 = 永不过期">
            <input type="number" min={0} max={3650} value={expiresDays} onChange={e => setExpiresDays(Math.trunc(Number(e.target.value)) || 0)} />
          </Field>
          <Field label="生成个数" hint="一次最多 100 个">
            <input type="number" min={1} max={100} value={count} onChange={e => setCount(Math.trunc(Number(e.target.value)) || 1)} />
          </Field>
          <Field label="CDK 名称" hint="留空则按类型与面额自动生成；作为查询与导出依据">
            <input value={note} maxLength={120} placeholder={`如：${cdkName}`} onChange={e => setNote(e.target.value)} />
          </Field>
        </div>
        <div className="actions">
          <button className="btn primary" onClick={() => void submit()} disabled={busy || !live}>
            {busy ? "生成中…" : "生成兑换码"}
          </button>
          {error && <span className="error">{error}</span>}
        </div>
      </section>

      {generated.length > 0 && (
        <section className="panel result">
          <div className="panel-head">
            <h3>已生成 {generated.length} 个兑换码（已导出 txt）</h3>
            <button className="btn ghost" onClick={() => exportTxt(generated)}>
              <Download size={14} /> 重新导出 TXT
            </button>
          </div>
          <div className="code-list">
            {generated.map(r => (
              <code key={r.code}>{r.code}</code>
            ))}
          </div>
        </section>
      )}

      <section className="panel history">
        <div className="panel-head">
          <h3>兑换码查询</h3>
        </div>
        <div className="grid" style={{ alignItems: "end" }}>
          <Field label="查询方式">
            <select value={mode} onChange={e => setMode(e.target.value as CdkListFilter["mode"])}>
              <option value="all">全部</option>
              <option value="code">按兑换码（KEY）</option>
              <option value="note">按名称（备注）</option>
              <option value="character">按兑换角色</option>
            </select>
          </Field>
          <Field
            label={mode === "character" ? "角色 ID" : "搜索词"}
            hint={
              mode === "all"
                ? "留空查全部；输入可匹配码 / 名称 / 角色"
                : mode === "character"
                  ? "玩家兑换时选择的角色 ID"
                  : mode === "code"
                    ? "支持部分匹配，可带或不带连字符；旧 PM- 码可直接输入"
                    : "匹配生成时填写的备注名称"
            }
          >
            <input
              value={searchText}
              placeholder={mode === "character" ? "如：2358" : mode === "code" ? "如：AB2C-D3EF-… 或 PM-FBW5…" : "如：周年庆"}
              onChange={e => setSearchText(e.target.value)}
              onKeyDown={e => e.key === "Enter" && search()}
            />
          </Field>
          <div className="actions">
            <button className="btn primary" onClick={search} disabled={listBusy || !live}>
              <Search size={14} /> {listBusy ? "查询中…" : "查询"}
            </button>
          </div>
        </div>
        {listError && <p className="error">{listError}</p>}
        {!searched && !listError ? (
          <Empty title="输入条件后查询" detail="支持查询全部、按兑换码、按名称、按兑换角色。" />
        ) : rows.length === 0 && !listError ? (
          <Empty title="没有符合条件的兑换码" detail="换个条件再试，或用上面的表单生成。" />
        ) : (
          <table className="table">
            <thead>
              <tr>
                <th>兑换码</th>
                <th>内容</th>
                <th>状态</th>
                <th>已用 / 限领</th>
                <th>兑换角色</th>
                <th>过期时间</th>
                <th>备注</th>
                <th>创建时间</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(r => {
                const pill = kindPill(r.kind);
                const status = statusPill[r.status] ?? { label: r.status || "—", tone: "muted" };
                return (
                  <tr key={r.code}>
                    <td><code>{r.code}</code></td>
                    <td>
                      <Pill tone={pill.tone}>{pill.label}</Pill> {r.summary}
                    </td>
                    <td>
                      <Pill tone={status.tone}>{status.label}</Pill>
                    </td>
                    <td>
                      {r.usedCount} / {r.maxUses}
                    </td>
                    <td className="muted">{r.redeemed || "—"}</td>
                    <td>{r.expiresAt || "永久"}</td>
                    <td className="muted">{r.note || "—"}</td>
                    <td className="muted">{timestamp(r.createdAt)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
        {searched && !listError && total > 0 && (
          <div className="actions" style={{ justifyContent: "space-between", alignItems: "center" }}>
            <span className="muted">
              共 {total} 条 · 第 {page} / {Math.max(1, Math.ceil(total / PAGE_SIZE))} 页
            </span>
            <div className="actions">
              <button
                className="btn ghost"
                disabled={listBusy || page <= 1}
                onClick={() => void load(page - 1, filter)}
              >
                上一页
              </button>
              <button
                className="btn ghost"
                disabled={listBusy || page >= Math.ceil(total / PAGE_SIZE)}
                onClick={() => void load(page + 1, filter)}
              >
                下一页
              </button>
            </div>
          </div>
        )}
      </section>
    </div>
  );
}
