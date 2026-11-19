import { useEffect, useState } from "react";
import { KeyRound, RefreshCw } from "lucide-react";
import { useLive } from "../api/context";
import {
  listRegistrationInvites,
  listRegistrationRecords,
  registrationUnbind,
  saveRegistrationInvite,
  type RegistrationInvite,
  type RegistrationRecord,
} from "../api/registration";
import { Empty, Field, Pill, timestamp, type PageProps } from "../ui";

// 注册限制管理：正常注册 = 一机一号 + 一 IP 一号（服务端默认，玩家无感知）；
// 网吧等共享出口场景由这里发放邀请码绕过双重限制。支持查看注册留痕
// （账号/IP/机器码/邀请码）与重装系统后的机器码解绑。

const PAGE_SIZE = 50;

function inviteStatus(r: RegistrationInvite): { label: string; tone: string } {
  if (r.revoked) return { label: "已作废", tone: "pink" };
  if (r.expiresAt && new Date(r.expiresAt.replace(" ", "T")).getTime() < Date.now())
    return { label: "已过期", tone: "neutral" };
  if (r.used >= r.maxUses) return { label: "已用完", tone: "neutral" };
  return { label: "可用", tone: "green" };
}

export function RegistrationPage({}: PageProps) {
  const live = useLive();
  // 邀请码生成表单
  const [count, setCount] = useState(1);
  const [maxUses, setMaxUses] = useState(1);
  const [expiresDays, setExpiresDays] = useState(30);
  const [note, setNote] = useState("");
  const [created, setCreated] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  // 邀请码列表
  const [invites, setInvites] = useState<RegistrationInvite[]>([]);
  const [inviteTotal, setInviteTotal] = useState(0);
  const [invitePage, setInvitePage] = useState(1);
  // 注册记录
  const [records, setRecords] = useState<RegistrationRecord[]>([]);
  const [recordTotal, setRecordTotal] = useState(0);
  const [recordPage, setRecordPage] = useState(1);
  // 解绑
  const [unbindKind, setUnbindKind] = useState<"device" | "ip">("device");
  const [unbindValue, setUnbindValue] = useState("");
  const [unbindMsg, setUnbindMsg] = useState("");

  const invitePages = Math.max(1, Math.ceil(inviteTotal / PAGE_SIZE));
  const recordPages = Math.max(1, Math.ceil(recordTotal / PAGE_SIZE));

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

  // 进入页面即拉取（两个列表都是小数据量分页）。
  useEffect(() => {
    void loadInvites(1).catch(e => setError(e instanceof Error ? e.message : String(e)));
    void loadRecords(1).catch(e => setError(e instanceof Error ? e.message : String(e)));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [live]);

  async function createInvites() {
    if (!live || busy) return;
    setBusy(true);
    setError("");
    try {
      const out = await saveRegistrationInvite(live, {
        op: "create",
        count: Math.trunc(Number(count)) || 1,
        maxUses: Math.trunc(Number(maxUses)) || 1,
        expiresDays: Math.trunc(Number(expiresDays)) || 0,
        note: note.trim(),
      });
      setCreated(out.codes);
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
            所有注册（含邀请码）都会在注册记录里留痕，重装系统的玩家按记录解绑机器码。
          </p>
        </div>
        <button
          className="btn ghost"
          onClick={() => {
            void loadInvites(invitePage);
            void loadRecords(recordPage);
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
        {error && <p className="error">{error}</p>}
        {created.length > 0 && (
          <div className="code-list" style={{ marginTop: 10 }}>
            {created.map(c => (
              <code key={c}>{c}</code>
            ))}
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
          <table className="table">
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
          </table>
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
        {unbindMsg && <p className="error">{unbindMsg}</p>}
      </section>

      <section className="panel history">
        <div className="panel-head">
          <h3>注册记录</h3>
          <span className="muted">
            共 {recordTotal} 条 · 第 {recordPage} / {recordPages} 页
          </span>
        </div>
        {records.length === 0 ? (
          <Empty title="还没有注册记录" detail="玩家注册后会在这里留痕（账号/IP/机器码/邀请码）。" />
        ) : (
          <table className="table">
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
          </table>
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
      </section>
    </div>
  );
}
