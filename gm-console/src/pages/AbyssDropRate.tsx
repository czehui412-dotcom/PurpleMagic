import { useCallback, useEffect, useState } from "react";
import { RefreshCw, Swords } from "lucide-react";
import { useLive } from "../api/context";
import { Modal, Pill, type PageProps } from "../ui";

type Mode = "pvf" | "rate" | "count" | "off";
type Tab = "pvf" | "count" | "rate";
type Target = "hell" | "rift";
type DropStatus = {
  rate: number;
  override: boolean;
  pvfBaseline: number;
  scale: number;
  mode: Mode;
  minPieces?: number;
  maxPieces?: number;
};
type Pending = { target: Target; kind: "pvf" | "rate" | "count" | "off"; value: number; text: string };

const countOptions = [2, 3, 4, 5];
const ratePresets = [
  { label: "关闭 (0‰)", rate: 0 },
  { label: "约 1 件 (63‰)", rate: 63 },
  { label: "约 2 件 (125‰)", rate: 125 },
  { label: "约 3 件 (188‰)", rate: 188 },
  { label: "必命中 (1000‰)", rate: 1000 },
];

function tabFor(status: DropStatus): Tab {
  if (status.mode === "rate") return "rate";
  if (status.mode === "count" || status.mode === "off") return "count";
  return "pvf";
}

function effectText(data: DropStatus | null, rollsPerRoom: number): string {
  if (!data) return "—";
  if (data.mode === "count" || data.mode === "off") {
    const min = data.mode === "off" ? 0 : (data.minPieces || 1);
    const max = data.mode === "off" ? 0 : (data.maxPieces || min);
    return max === min ? `${max} 件` : `${min}–${max} 件`;
  }
  if (data.mode === "rate") {
    const expected = ((data.rate / (data.scale || 1000)) * rollsPerRoom).toFixed(2);
    return `${data.rate}‰ · 约 ${expected} 件`;
  }
  return "PVF 原样";
}

function modeText(data: DropStatus | null): string {
  if (!data) return "—";
  switch (data.mode) {
    case "count": return "运行时件数档";
    case "off": return "运行时关闭";
    case "rate": return "旧概率档";
    default: return "PVF 默认";
  }
}

type PanelProps = {
  target: Target;
  title: string;
  description: string;
  countUnit: string;
  rollsPerRoom: number;
  data: DropStatus | null;
  tab: Tab;
  onTab: (tab: Tab) => void;
  rateDraft: number;
  onRateDraft: (value: number) => void;
  error: string;
  busy: boolean;
  canWrite: boolean;
  onRefresh: () => void;
  onPick: (pending: Pending) => void;
};

function DropPanel({ target, title, description, countUnit, rollsPerRoom, data, tab, onTab, rateDraft, onRateDraft, error, busy, canWrite, onRefresh, onPick }: PanelProps) {
  return <div className="content-panel glass">
    <div className="section-heading"><Swords size={20} /><div><h2>{title}</h2><p className="muted">{description}</p></div></div>
    {error && <p className="inline-error" role="alert">{error}</p>}
    <div className="activity-overview glass" aria-label={`${title}概览`}>
      <div><Swords size={19} /><span>当前生效<strong>{effectText(data, rollsPerRoom)}</strong></span></div>
      <div><Pill tone={data?.override ? "orange" : "green"}>{modeText(data)}</Pill></div>
      <div><span>PVF 基线<strong>{data ? `${data.pvfBaseline}‰ / 原表` : "—"}</strong></span></div>
    </div>
    <div className="toolbar activity-toolbar">
      <span className="muted">模式</span>
      <button className={tab === "pvf" ? "primary" : "secondary"} onClick={() => onTab("pvf")}>PVF 原样</button>
      <button className={tab === "count" ? "primary" : "secondary"} onClick={() => onTab("count")}>保底件数</button>
      <button className={tab === "rate" ? "primary" : "secondary"} onClick={() => onTab("rate")}>概率档（旧）</button>
      <button className="secondary" disabled={busy} onClick={onRefresh}><RefreshCw size={15} />刷新</button>
    </div>
    {tab === "pvf" && <>
      <p className="activity-footnote">完全保留当前 PVF 掉落逻辑，不做任何运行时覆盖。普通深渊按房间最后一波结算；时空裂缝按领主结算。</p>
      <div className="toolbar activity-toolbar">
        {canWrite && <button className="primary" disabled={busy} onClick={() => onPick({ target, kind: "pvf", value: -1, text: "恢复 PVF 原样" })}>恢复 PVF 原样</button>}
      </div>
    </>}
    {tab === "count" && <>
      <div className="toolbar activity-toolbar">
        <span className="muted">{countUnit}</span>
        {countOptions.map((max) => <button key={max} className="secondary" disabled={!canWrite || busy} onClick={() => onPick({ target, kind: "count", value: max, text: `设为必出 1–${max} 件` })}>1–{max} 件</button>)}
        {canWrite && <button className="secondary" disabled={busy} onClick={() => onPick({ target, kind: "off", value: 0, text: "关闭史诗装备掉落" })}>关闭</button>}
      </div>
      <p className="activity-footnote">保底 1 件，额外件数在 0 到 N−1 间均匀随机；选得越靠后，平均件数越高。该档会替换概率档，不会叠加。史诗碎片保底不受影响。</p>
    </>}
    {tab === "rate" && <>
      <div className="toolbar activity-toolbar">
        <label htmlFor={`${target}-rate`}>旧概率档（0–1000‰，-1 恢复 PVF）</label>
        <input id={`${target}-rate`} type="number" min={-1} max={1000} value={rateDraft}
          onChange={(e) => onRateDraft(Number(e.target.value))} disabled={!canWrite} />
        {canWrite && <button className="primary" disabled={busy || rateDraft < -1 || rateDraft > 1000} onClick={() => onPick({ target, kind: "rate", value: rateDraft, text: `概率档改为 ${rateDraft}‰` })}>应用</button>}
      </div>
      <div className="toolbar activity-toolbar">
        <span className="muted">快捷预设</span>
        {ratePresets.map((p) => <button key={p.rate} className="secondary" disabled={!canWrite || busy} onClick={() => onPick({ target, kind: "rate", value: p.rate, text: `概率档改为 ${p.rate}‰` })}>{p.label}</button>)}
      </div>
      <p className="activity-footnote">旧概率档按每次投骰命中率计算，可能出现 0 件，也可能高于所选件数；仅用于需要保留随机手感的场景。</p>
    </>}
  </div>;
}

export function AbyssDropRatePage({ state }: PageProps) {
  const live = useLive();
  const [hell, setHell] = useState<DropStatus | null>(null);
  const [hellTab, setHellTab] = useState<Tab>("count");
  const [hellRate, setHellRate] = useState(125);
  const [hellError, setHellError] = useState("");
  const [rift, setRift] = useState<DropStatus | null>(null);
  const [riftTab, setRiftTab] = useState<Tab>("count");
  const [riftRate, setRiftRate] = useState(1000);
  const [riftError, setRiftError] = useState("");
  const [pending, setPending] = useState<Pending | null>(null);

  const loadOne = useCallback(async (target: Target) => {
    if (!live) return;
    const endpoint = target === "hell" ? "/api/v1/hell-drop-rate" : "/api/v1/rift-drop-rate";
    try {
      const res = await live.http.get<DropStatus>(endpoint);
      if (target === "hell") {
        setHell(res.data);
        setHellTab(tabFor(res.data));
        if (res.data.mode === "rate") setHellRate(res.data.rate);
        setHellError("");
      } else {
        setRift(res.data);
        setRiftTab(tabFor(res.data));
        if (res.data.mode === "rate") setRiftRate(res.data.rate);
        setRiftError("");
      }
    } catch (e) {
      const message = e instanceof Error ? e.message : "读取掉率失败";
      if (target === "hell") setHellError(message); else setRiftError(message);
    }
  }, [live]);

  const load = useCallback(async () => {
    await Promise.all([loadOne("hell"), loadOne("rift")]);
  }, [loadOne]);

  useEffect(() => { void load(); }, [load]);

  const apply = async (item: Pending) => {
    if (!live) return;
    setPending(null);
    const isHell = item.target === "hell";
    const base = isHell ? "/api/v1/hell" : "/api/v1/rift";
    try {
      const res = item.kind === "pvf" || item.kind === "rate"
        ? await live.http.post<DropStatus>(`${base}-drop-rate`, { rate: item.kind === "pvf" ? -1 : item.value })
        : await live.http.post<DropStatus>(`${base}-drop-count`, { count: item.kind === "off" ? 0 : item.value });
      const nextTab: Tab = item.kind === "rate" ? "rate" : (item.kind === "pvf" ? "pvf" : "count");
      if (isHell) {
        setHell(res);
        setHellTab(nextTab);
        setHellError("");
      } else {
        setRift(res);
        setRiftTab(nextTab);
        setRiftError("");
      }
    } catch (e) {
      const message = e instanceof Error ? e.message : "设置掉率失败";
      if (isHell) setHellError(message); else setRiftError(message);
    }
  };

  const busy = !!state.api?.busy;
  const canWrite = true; // 登录会话 + CSRF 已在 /api/v1 writes 路由统一强制校验

  return <div className="motion-section">
    <DropPanel target="hell" title="深渊史诗掉率" description="普通深渊按房间最后一波结算。推荐用保底件数档；旧概率档保留随机手感。" countUnit="每房最终史诗装备" rollsPerRoom={8}
      data={hell} tab={hellTab} onTab={setHellTab} rateDraft={hellRate} onRateDraft={setHellRate} error={hellError}
      busy={busy} canWrite={canWrite} onRefresh={() => void loadOne("hell")} onPick={setPending} />
    <DropPanel target="rift" title="时空裂缝史诗掉率" description="316/时间裂缝按领主结算。PVF 原表为 1–4 件，也可直接指定 1–N 件档。" countUnit="每次领主史诗装备" rollsPerRoom={1}
      data={rift} tab={riftTab} onTab={setRiftTab} rateDraft={riftRate} onRateDraft={setRiftRate} error={riftError}
      busy={busy} canWrite={canWrite} onRefresh={() => void loadOne("rift")} onPick={setPending} />
    {pending && <Modal title="确认修改史诗掉落" onClose={() => setPending(null)} footer={<>
      <button className="secondary" onClick={() => setPending(null)}>取消</button>
      <button className="primary" onClick={() => void apply(pending)}>确认应用</button>
    </>}>
      <p>将<strong>{pending.text}</strong>。确认后立即对后续结算生效，无需重启。确认前请知会玩家。</p>
    </Modal>}
  </div>;
}
