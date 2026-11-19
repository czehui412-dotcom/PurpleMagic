import { Fragment, useEffect, useMemo, useState } from "react";
import { ChevronDown, KeyRound, RefreshCw } from "lucide-react";
import { useLive } from "../api/context";
import {
  listAllRegistrationRecords,
  listRegistrationInvites,
  listRegistrationRecords,
  registrationUnbind,
  saveRegistrationInvite,
  type RegistrationInvite,
  type RegistrationRecord,
} from "../api/registration";
import { Empty, Field, Pill, Tabs, timestamp, type PageProps } from "../ui";

// 注册限制管理：正常注册 = 一机一号 + 一 IP 一号（服务端默认，玩家无感知）；
// 网吧等共享出口场景由这里发放邀请码绕过双重限制。支持查看注册留痕
// （账号/IP/机器码/邀请码）、按 IP+机器码聚合重复账号、以及重装系统后的解绑。

const PAGE_SIZE = 50;

function inviteStatus(r: RegistrationInvite): { label: string; tone: string } {
  if (r.revoked) return { label: "已作废", tone: "pink" };
  if (r.expiresAt && new Date(r.expiresAt.replace(" ", "T")).getTime() < Date.now())
    return { label: "已过期", tone: "neutral" };
  if (r.used >= r.maxUses) return { label: "已用完", tone: "neutral" };
  return { label: "可用", tone: "green" };
}

type BindGroup = {
  key: string;
  ip: string;
  device: string;
  accounts: RegistrationRecord[];
};

// 按「注册 IP + 机器码」聚合：同 IP 且同机器码的账号并到一组（含只有 1 个账号的组）。
function groupByBinding(rows: RegistrationRecord[]): BindGroup[] {
  const map = new Map<string, BindGroup>();
  for (const r of rows) {
    const ip = (r.ip || "—").trim();
    const device = (r.device || "—").trim();
    const key = `${ip}\u0000${device}`;
    let g = map.get(key);
    if (!g) {
      g = { key, ip, device, accounts: [] };
      map.set(key, g);
    }
    g.accounts.push(r);
  }
  // 多账号组排前，便于发现共用机器码/IP 的账号。
  return [...map.values()].sort(
    (a, b) => b.accounts.length - a.accounts.length || a.ip.localeCompare(b.ip),
  );
}

export function RegistrationPage({}: PageProps) {
  const live = useLive();
  // 邀请码生成表单
  const [count, setCount] = useState(1);
  const [maxUses, setMaxUses] = useState(1);
  const [expiresDays, setExpiresDays] = useState(30);
  const [note, setNote] = useState("");
  const [created, setCreated] = useState<string[]>([]);
  const [createMsg, setCreateMsg] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  // 邀请码列表
  const [invites, setInvites] = useState<RegistrationInvite[]>([]);
  const [inviteTotal, setInviteTotal] = useState(0);
  const [invitePage, setInvitePage] = useState(1);
  // 注册记录（明细分页）
  const [records, setRecords] = useState<RegistrationRecord[]>([]);
  const [recordTotal, setRecordTotal] = useState(0);
  const [recordPage, setRecordPage] = useState(1);
  const [recordView, setRecordView] = useState<"detail" | "grouped">("detail");
  // 绑定分组（全量）
  const [allRows, setAllRows] = useState<RegistrationRecord[]>([]);
  const [groupLoading, setGroupLoading] = useState(false);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  // 解绑
  const [unbindKind, setUnbindKind] = useState<"device" | "ip">("device");
  const [unbindValue, setUnbindValue] = useState("");
  const [unbindMsg, setUnbindMsg] = useState("");

  const invitePages = Math.max(1, Math.ceil(inviteTotal / PAGE_SIZE));
  const recordPages = Math.max(1, Math.ceil(recordTotal / PAGE_SIZE));
  const groups = useMemo(() => groupByBinding(allRows), [allRows]);
  const duplicateGroups = groups.filter(g => g.accounts.length > 1);

  async function loadInvites(target: number) {
    if (!live) return;
    const out = await listRegistrationInvites(live, target);
    setInvites(out.rows);
    setInviteTotal(out.total);
    setInvitePage(out.page ?? target);
  }

  async function loadRecords(target: number) {
    if (!live) return;
    const out = await listRegistrationRecords(live, target);
    setRecords(out.rows);
    setRecordTotal(out.total);
    setRecordPage(out.page ?? target);
  }

  async function loadGroups() {
    if (!live || groupLoading) return;
    setGroupLoading(true);
    setError("");
    try {
      setAllRows(await listAllRegistrationRecords(live));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setGroupLoading(false);
    }
  }

  // 进入页面即拉取邀请码 + 明细分页；切到分组视图时再按需拉全量。
  useEffect(() => {
    void loadInvites(1).catch(e => setError(e instanceof Error ? e.message : String(e)));
    void loadRecords(1).catch(e => setError(e instanceof Error ? e.message : String(e)));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [live]);

  useEffect(() => {
    if (recordView === "grouped" && allRows.length === 0 && !groupLoading) void loadGroups();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [recordView]);

  function changeView(v: "detail" | "grouped") {
    setRecordView(v);
    setError("");
    if (v === "grouped" && allRows.length === 0) void loadGroups();
  }

  function toggleGroup(key: string) {
    setExpanded(prev => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  async function createInvites() {
    if (!live || busy) return;
    setBusy(true);
    setError("");
    setCreateMsg("");
    setCreated([]);
    try {
      const out = await saveRegistrationInvite(live, {
        op: "create",
        count: Math.trunc(Number(count)) || 1,
        maxUses: Math.trunc(Number(maxUses)) || 1,
        expiresDays: Math.trunc(Number(expiresDays)) || 0,
        note: note.trim(),
      });
      if (!out.codes?.length) throw new Error("网关未返回邀请码，请确认游戏服运行时通道正常后重试。");
      setCreated(out.codes);
      setCreateMsg(`已生成 ${out.codes.length} 个邀请码：`);
      await loadInvites(1);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function revoke(code: string) {
    if (!live || busy) return;
    setBusy(true);
    setError("");
    try {
      await saveRegistrationInvite(live, { op: "revoke", code });
      await loadInvites(invitePage);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function unbind() {
    if (!live || busy) return;
    setBusy(true);
    setError("");
    setUnbindMsg("");
    try {
      await registrationUnbind(live, unbindKind, unbindValue.trim());
      setUnbindMsg("解绑成功，该设备/IP 现在可以重新注册。");
      setUnbindValue("");
      await loadRecords(recordPage);
      if (recordView === "grouped") await loadGroups();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="page" aria-label="注册管理">
      <header className="page-head">
        <div>
          <h2>
            <KeyRound size={20} /> 注册管理
          </h2>
          <p>
            正常注册 = 一台电脑一个账号 + 一个 IP 一个账号（双重限制）。网吧等共享出口的玩家用下方邀请码注册即可绕过；
            所有注册（含邀请码）都会留痕，可按 IP + 机器码聚合查看重复账号，重装系统的玩家按记录解绑机器码。
          </p>
        </div>
        <button
          className="btn ghost"
          onClick={() => {
            void loadInvites(invitePage);
            void loadRecords(recordPage);
            if (recordView === "grouped") void loadGroups();
          }}
          disabled={!live || busy}
        >
          <RefreshCw size={15} /> 刷新
        </button>
      </header>

      <section className="panel compose">
        <div className="panel-head">
          <h3>生成邀请码</h3>
        </div>
        <div className="grid" style={{ alignItems: "end" }}>
          <Field label="数量" hint="1-50">
            <input type="number" min={1} max={50} value={count} onChange={e => setCount(Math.trunc(Number(e.target.value)) || 1)} />
          </Field>
          <Field label="每码次数" hint="该码最多可注册的账号数（1-100）">
            <input type="number" min={1} max={100} value={maxUses} onChange={e => setMaxUses(Math.trunc(Number(e.target.value)) || 1)} />
          </Field>
          <Field label="有效天数" hint="0 = 永不过期">
            <input type="number" min={0} max={3650} value={expiresDays} onChange={e => setExpiresDays(Math.trunc(Number(e.target.value)) || 0)} />
          </Field>
          <Field label="备注" hint="如：XX网吧-11月">
            <input value={note} maxLength={120} placeholder="选填" onChange={e => setNote(e.target.value)} />
          </Field>
          <div className="actions">
            <button className="btn primary" onClick={() => void createInvites()} disabled={busy || !live}>
              {busy ? "生成中…" : "生成"}
            </button>
          </div>
        </div>
        {error && <p className="inline-error" role="alert">{error}</p>}
        {createMsg && (
          <div role="status" style={{ marginTop: 10 }}>
            <strong>{createMsg}</strong>
            <div className="code-list" style={{ marginTop: 6 }}>
              {created.map(c => <code key={c}>{c}</code>)}
            </div>
          </div>
        )}
      </section>

      <section className="panel history">
        <div className="panel-head">
          <h3>邀请码列表</h3>
          <span className="muted">
            共 {inviteTotal} 条 · 第 {invitePage} / {invitePages} 页
          </span>
        </div>
        {invites.length === 0 ? (
          <Empty title="还没有邀请码" detail="用上方表单生成第一批。" />
        ) : (
          <div className="table-scroll"><table className="table">
            <thead>
              <tr>
                <th>邀请码</th>
                <th>用量</th>
                <th>到期</th>
                <th>状态</th>
                <th>备注</th>
                <th>创建时间</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {invites.map(r => {
                const status = inviteStatus(r);
                return (
                  <tr key={r.code}>
                    <td><code>{r.code}</code></td>
                    <td>{r.used}/{r.maxUses}</td>
                    <td className="muted">{r.expiresAt || "永不"}</td>
                    <td><Pill tone={status.tone}>{status.label}</Pill></td>
                    <td className="muted">{r.note || "—"}</td>
                    <td className="muted">{timestamp(r.createdAt)}</td>
                    <td>
                      {!r.revoked && (
                        <button className="btn ghost" disabled={busy} onClick={() => void revoke(r.code)}>
                          作废
                        </button>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table></div>
        )}
        {inviteTotal > PAGE_SIZE && (
          <div className="actions">
            <button className="btn ghost" disabled={busy || invitePage <= 1} onClick={() => void loadInvites(invitePage - 1)}>
              上一页
            </button>
            <button className="btn ghost" disabled={busy || invitePage >= invitePages} onClick={() => void loadInvites(invitePage + 1)}>
              下一页
            </button>
          </div>
        )}
      </section>

      <section className="panel history">
        <div className="panel-head">
          <h3>账号绑定 · IP 与机器码</h3>
          <span className="muted">
            {recordView === "detail"
              ? `共 ${recordTotal} 条 · 第 ${recordPage} / ${recordPages} 页`
              : `全部 ${allRows.length} 条 · ${groups.length} 组 · ${duplicateGroups.length} 组重复`}
          </span>
        </div>
        <Tabs
          label="注册记录视图"
          value={recordView}
          onChange={changeView}
          items={[
            { id: "detail", label: "注册明细" },
            { id: "grouped", label: "按 IP + 机器码分组" },
          ]}
        />

        {recordView === "detail" ? (
          <>
            {records.length === 0 ? (
              <Empty title="还没有注册记录" detail="玩家注册后会在这里留痕（账号/IP/机器码/邀请码）。" />
            ) : (
              <div className="table-scroll"><table className="table">
                <thead>
                  <tr>
                    <th>账号</th>
                    <th>账号 ID</th>
                    <th>注册 IP</th>
                    <th>机器码</th>
                    <th>邀请码</th>
                    <th>时间</th>
                  </tr>
                </thead>
                <tbody>
                  {records.map(r => (
                    <tr key={r.accountId}>
                      <td>{r.account || "—"}</td>
                      <td className="muted">{r.accountId}</td>
                      <td className="muted">{r.ip || "—"}</td>
                      <td className="muted" title={r.device || undefined}>
                        {r.device ? `${r.device.slice(0, 12)}…` : "—"}
                      </td>
                      <td>{r.invite ? <code>{r.invite}</code> : "—"}</td>
                      <td className="muted">{r.createdAt || "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table></div>
            )}
            {recordTotal > PAGE_SIZE && (
              <div className="actions">
                <button className="btn ghost" disabled={busy || recordPage <= 1} onClick={() => void loadRecords(recordPage - 1)}>
                  上一页
                </button>
                <button className="btn ghost" disabled={busy || recordPage >= recordPages} onClick={() => void loadRecords(recordPage + 1)}>
                  下一页
                </button>
              </div>
            )}
          </>
        ) : (
          <>
            {groupLoading ? (
              <p className="muted" role="status">正在读取全部注册留痕…</p>
            ) : groups.length === 0 ? (
              <Empty title="还没有注册留痕" detail="玩家注册后会按 IP + 机器码在这里聚合。" />
            ) : (
              <div className="table-scroll"><table className="table">
                <thead>
                  <tr>
                    <th style={{ width: 32 }} />
                    <th>注册 IP</th>
                    <th>机器码</th>
                    <th>账号数</th>
                    <th>账号</th>
                    <th>最近注册</th>
                  </tr>
                </thead>
                <tbody>
                  {groups.map(g => {
                    const isOpen = expanded.has(g.key);
                    const latest = g.accounts.reduce((a, b) => (a.createdAt || "") >= (b.createdAt || "") ? a : b);
                    return (
                      <Fragment key={g.key}>
                        <tr>
                          <td>
                            <button
                              className="btn ghost"
                              aria-label={isOpen ? "收起" : "展开"}
                              aria-expanded={isOpen}
                              onClick={() => toggleGroup(g.key)}
                              style={{ padding: "2px 6px" }}
                            >
                              <ChevronDown size={14} style={{ transform: isOpen ? "rotate(180deg)" : "none" }} />
                            </button>
                          </td>
                          <td className="muted">{g.ip}</td>
                          <td className="muted" title={g.device === "—" ? undefined : g.device}>
                            {g.device === "—" ? "—" : `${g.device.slice(0, 12)}…`}
                          </td>
                          <td>
                            <Pill tone={g.accounts.length > 1 ? "orange" : "neutral"}>
                              {g.accounts.length} 个账号
                            </Pill>
                          </td>
                          <td>
                            {g.accounts.length === 1
                              ? (g.accounts[0].account || g.accounts[0].accountId || "—")
                              : g.accounts.slice(0, 3).map(a => a.account || a.accountId).join("、") + (g.accounts.length > 3 ? "…" : "")}
                          </td>
                          <td className="muted">{latest.createdAt || "—"}</td>
                        </tr>
                        {isOpen && (
                          <tr>
                            <td colSpan={6}>
                              <div className="table-scroll" style={{ margin: "6px 0 10px 28px" }}>
                                <table className="table">
                                  <thead>
                                    <tr>
                                      <th>账号</th>
                                      <th>账号 ID</th>
                                      <th>邀请码</th>
                                      <th>注册时间</th>
                                    </tr>
                                  </thead>
                                  <tbody>
                                    {g.accounts.map(a => (
                                      <tr key={a.accountId}>
                                        <td>{a.account || "—"}</td>
                                        <td className="muted">{a.accountId}</td>
                                        <td>{a.invite ? <code>{a.invite}</code> : "—"}</td>
                                        <td className="muted">{a.createdAt || "—"}</td>
                                      </tr>
                                    ))}
                                  </tbody>
                                </table>
                              </div>
                            </td>
                          </tr>
                        )}
                      </Fragment>
                    );
                  })}
                </tbody>
              </table></div>
            )}
            <div className="actions" style={{ marginTop: 8 }}>
              <button className="btn ghost" disabled={busy || groupLoading} onClick={() => void loadGroups()}>
                {groupLoading ? "读取中…" : "重新读取全部"}
              </button>
            </div>
          </>
        )}
      </section>

      <section className="panel history">
        <div className="panel-head">
          <h3>解绑</h3>
        </div>
        <p className="muted">
          玩家重装系统 / 更换硬件后机器码变化无法注册时，按「注册记录」里的机器码解绑；
          共享出口 IP 名额耗尽时也可按 IP 清零重计。
        </p>
        <div className="grid" style={{ alignItems: "end" }}>
          <Field label="类型">
            <select value={unbindKind} onChange={e => setUnbindKind(e.target.value as "device" | "ip")}>
              <option value="device">机器码</option>
              <option value="ip">IP</option>
            </select>
          </Field>
          <Field label={unbindKind === "device" ? "机器码（64 位哈希）" : "IP 地址"}>
            <input
              value={unbindValue}
              onChange={e => setUnbindValue(e.target.value)}
              placeholder={unbindKind === "device" ? "从注册记录复制" : "如：1.2.3.4"}
            />
          </Field>
          <div className="actions">
            <button className="btn ghost" disabled={busy || !unbindValue.trim()} onClick={() => void unbind()}>
              解绑
            </button>
          </div>
        </div>
        {unbindMsg && <p role="status">{unbindMsg}</p>}
      </section>
    </div>
  );
}
