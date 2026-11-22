import { useCallback, useEffect, useState } from "react";
import { Coins, Play, RefreshCw, Save } from "lucide-react";
import { useLive } from "../api/context";
import type { PageProps } from "../ui";

type Config = {
  enabled: boolean;
  intervalSeconds: number;
  amount: number;
  currency: "cera" | "token" | "joytoken";
};
type Cycle = {
  cycleId: string;
  ranAt: string;
  onlineCount: number;
  accountCount: number;
  granted: number;
  skipped: number;
  currency: string;
  amount: number;
};
type Payload = { config: Config; lastCycle: Cycle; lastAt: string };

const defaults: Config = { enabled: false, intervalSeconds: 60, amount: 30, currency: "token" };
const currencyLabel: Record<Config["currency"], string> = {
  cera: "点券",
  token: "代币券",
  joytoken: "欢乐代币券",
};

export function PaodianPage(_props: PageProps) {
  const live = useLive();
  const [cfg, setCfg] = useState<Config>(defaults);
  const [last, setLast] = useState<Cycle | null>(null);
  const [lastAt, setLastAt] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  const load = useCallback(async () => {
    if (!live) return;
    try {
      const envelope = await live.http.get<Payload>("/api/v1/paodian");
      const data = envelope.data;
      setCfg(data.config || defaults);
      setLast(data.lastCycle || null);
      setLastAt(data.lastAt || "");
    } catch (e: any) {
      setError(String(e?.message || e));
    }
  }, [live]);

  useEffect(() => { void load(); }, [load]);

  async function save(next: Config) {
    if (!live || busy) return;
    setBusy(true); setError(""); setMessage("");
    try {
      const data = await live.http.post<{ config: Config }>("/api/v1/paodian", { op: "save", ...next });
      setCfg(data.config);
      setMessage("泡点配置已保存");
    } catch (e: any) {
      setError(String(e?.message || e));
    } finally { setBusy(false); }
  }

  async function runNow() {
    if (!live || busy) return;
    setBusy(true); setError(""); setMessage("");
    try {
      const data = await live.http.post<{ cycle: Cycle }>("/api/v1/paodian", { op: "run" });
      setLast(data.cycle);
      setLastAt(data.cycle?.ranAt || "");
      setMessage(`本轮完成：在线 ${data.cycle?.onlineCount ?? 0} 人 / 账号 ${data.cycle?.accountCount ?? 0} 个，发放 ${data.cycle?.granted ?? 0} 份，跳过 ${data.cycle?.skipped ?? 0} 份`);
    } catch (e: any) {
      setError(String(e?.message || e));
    } finally { setBusy(false); }
  }

  const patch = (p: Partial<Config>) => setCfg((c) => ({ ...c, ...p }));

  return (
    <section className="page-stack">
      <header className="page-head">
        <div className="page-title"><Coins size={20} /><div><h2>在线泡点</h2><p>按在线账号发放代币，网关直接写入钱包；每个账号每轮最多一份。</p></div></div>
        <div className="row-actions">
          <button type="button" className="btn ghost" onClick={() => void load()} disabled={busy}><RefreshCw size={15} /> 刷新</button>
        </div>
      </header>

      <div className="card">
        <h3>调度配置</h3>
        <div className="form-grid">
          <label className="field">
            <span>启用自动泡点</span>
            <input type="checkbox" checked={cfg.enabled} onChange={(e) => patch({ enabled: e.target.checked })} />
          </label>
          <label className="field">
            <span>发放间隔（秒，10~86400）</span>
            <input type="number" min={10} max={86400} value={cfg.intervalSeconds} onChange={(e) => patch({ intervalSeconds: Number(e.target.value) })} />
          </label>
          <label className="field">
            <span>每轮数量（1~1000000）</span>
            <input type="number" min={1} max={1000000} value={cfg.amount} onChange={(e) => patch({ amount: Number(e.target.value) })} />
          </label>
          <label className="field">
            <span>币种</span>
            <select value={cfg.currency} onChange={(e) => patch({ currency: e.target.value as Config["currency"] })}>
              {(Object.keys(currencyLabel) as Config["currency"][]).map((k) => <option key={k} value={k}>{currencyLabel[k]}</option>)}
            </select>
          </label>
        </div>
        <div className="row-actions">
          <button type="button" className="btn primary" onClick={() => void save(cfg)} disabled={busy}><Save size={15} /> 保存配置</button>
          <button type="button" className="btn" onClick={() => void runNow()} disabled={busy}><Play size={15} /> 立即执行一次</button>
        </div>
        {message && <p className="hint">{message}</p>}
        {error && <p className="hint error">{error}</p>}
      </div>

      <div className="card">
        <h3>最近一轮</h3>
        {last ? (
          <div className="kv-grid">
            <div><span>轮次</span><strong>{last.cycleId}</strong></div>
            <div><span>时间</span><strong>{last.ranAt}</strong></div>
            <div><span>在线角色</span><strong>{last.onlineCount}</strong></div>
            <div><span>去重账号</span><strong>{last.accountCount}</strong></div>
            <div><span>成功发放</span><strong>{last.granted}</strong></div>
            <div><span>跳过</span><strong>{last.skipped}</strong></div>
            <div><span>币种 / 数量</span><strong>{currencyLabel[last.currency as Config["currency"]] || last.currency} / {last.amount}</strong></div>
          </div>
        ) : <p className="hint">尚未执行过泡点。{lastAt ? `上次：${lastAt}` : ""}</p>}
        <p className="hint">说明：本阶段只写网关钱包余额，在线会话不即时刷新、无系统公告；玩家重开商城或重登后可见。</p>
      </div>
    </section>
  );
}
