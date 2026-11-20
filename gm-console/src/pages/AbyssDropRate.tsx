import { useCallback, useEffect, useState } from "react";
import { RefreshCw, Swords } from "lucide-react";
import { useLive } from "../api/context";
import { Modal, Pill, type PageProps } from "../ui";

type DropRate = {
  rate: number;
  override: boolean;
  pvfBaseline: number;
  scale: number;
};

const presets = [
  { label: "PVF 默认", rate: -1 },
  { label: "每房约 1 件 (63‰)", rate: 63 },
  { label: "每房约 2 件 (125‰)", rate: 125 },
  { label: "每房约 3 件 (188‰)", rate: 188 },
  { label: "必掉 (1000‰)", rate: 1000 },
  { label: "关闭 (0‰)", rate: 0 },
];

const riftPresets = [
  { label: "PVF 原表", rate: -1 },
  { label: "25% 触发原表", rate: 250 },
  { label: "50% 触发原表", rate: 500 },
  { label: "75% 触发原表", rate: 750 },
  { label: "每把 1–4 件", rate: 1000 },
  { label: "关闭 (0‰)", rate: 0 },
];

export function AbyssDropRatePage({ state }: PageProps) {
  const live = useLive();
  const [data, setData] = useState<DropRate | null>(null);
  const [draft, setDraft] = useState(125);
  const [error, setError] = useState("");
  const [confirm, setConfirm] = useState<number | null>(null);
  const [riftData, setRiftData] = useState<DropRate | null>(null);
  const [riftDraft, setRiftDraft] = useState(1000);
  const [riftError, setRiftError] = useState("");
  const [riftConfirm, setRiftConfirm] = useState<number | null>(null);

  const load = useCallback(async () => {
    if (!live) return;
    try {
      const res = await live.http.get<DropRate>("/api/v1/hell-drop-rate");
      setData(res.data);
      setDraft(res.data.override ? res.data.rate : (res.data.pvfBaseline || 0));
      setError("");
    } catch (e) {
      setError(e instanceof Error ? e.message : "读取掉率失败");
    }
  }, [live]);

  const loadRift = useCallback(async () => {
    if (!live) return;
    try {
      const res = await live.http.get<DropRate>("/api/v1/rift-drop-rate");
      setRiftData(res.data);
      setRiftDraft(res.data.override ? res.data.rate : (res.data.pvfBaseline || 0));
      setRiftError("");
    } catch (e) {
      setRiftError(e instanceof Error ? e.message : "读取裂缝掉率失败");
    }
  }, [live]);

  useEffect(() => { void load(); void loadRift(); }, [load, loadRift]);

  const apply = async (rate: number) => {
    if (!live) return;
    setConfirm(null);
    try {
      const res = await live.http.post<DropRate>("/api/v1/hell-drop-rate", { rate });
      setData(res);
      setDraft(rate < 0 ? (res.pvfBaseline || 0) : rate);
      setError("");
    } catch (e) {
      setError(e instanceof Error ? e.message : "设置掉率失败");
    }
  };

  const applyRift = async (rate: number) => {
    if (!live) return;
    setRiftConfirm(null);
    try {
      const res = await live.http.post<DropRate>("/api/v1/rift-drop-rate", { rate });
      setRiftData(res);
      setRiftDraft(rate < 0 ? (res.pvfBaseline || 0) : rate);
      setRiftError("");
    } catch (e) {
      setRiftError(e instanceof Error ? e.message : "设置裂缝掉率失败");
    }
  };

  const busy = !!state.api?.busy;
  const canWrite = true; // 登录会话 + CSRF 已在 /api/v1 writes 路由统一强制校验
  const effective = data?.rate ?? 0;
  const perRoom = ((effective / (data?.scale || 1000)) * 16).toFixed(2);
  const riftEffective = riftData?.rate ?? 0;
  const riftExpected = ((riftEffective / (riftData?.scale || 1000)) * 1.75).toFixed(2);

  return <div className="motion-section">
    <div className="content-panel glass">
      <div className="section-heading"><Swords size={20} /><div><h2>深渊史诗掉率</h2><p className="muted">上半部分控制普通深渊房间；下半部分单独控制时空裂缝（316）的 PVF 史诗件数表。均立即生效，无需重启游戏服务。</p></div></div>
      {error && <p className="inline-error" role="alert">{error}</p>}
      <div className="activity-overview glass" aria-label="掉率概览">
        <div><Swords size={19} /><span>当前生效<strong>{data ? `${effective}‰` : "—"}</strong></span></div>
        <div><Pill tone={data?.override ? "orange" : "green"}>{data ? (data.override ? "运行时覆盖" : "PVF 默认") : "—"}</Pill></div>
        <div><span>PVF 基线<strong>{data ? `${data.pvfBaseline}‰` : "—"}</strong></span></div>
        <div><span>每房期望史诗<strong>{data ? perRoom : "—"} 件</strong></span></div>
      </div>
      <div className="toolbar activity-toolbar">
        <label htmlFor="hell-rate">目标掉率（0–1000‰，-1 恢复 PVF）</label>
        <input id="hell-rate" type="number" min={-1} max={1000} value={draft}
          onChange={(e) => setDraft(Number(e.target.value))} disabled={!canWrite} />
        {canWrite && <button className="primary" disabled={busy || draft < -1 || draft > 1000} onClick={() => setConfirm(draft)}>应用</button>}
        {live && <button className="secondary" disabled={busy} onClick={() => void load()}><RefreshCw size={15} />刷新</button>}
      </div>
      <div className="toolbar activity-toolbar">
        <span className="muted">快捷预设</span>
        {presets.map((p) => <button key={p.rate} className="secondary" disabled={!canWrite || busy} onClick={() => setConfirm(p.rate)}>{p.label}</button>)}
      </div>
      <p className="activity-footnote">掉率按每次投骰千分比计算：每个深渊房间 2 次结算 × 8 个投骰位 = 16 次机会。「每房期望史诗」= 16 × 掉率，仅供估算；实际掉落受稀有度与装备池影响。运行时覆盖在游戏服重启后回到 PVF 基线值，需长期生效请同步修改 PVF。</p>
    </div>
    <div className="content-panel glass">
      <div className="section-heading"><Swords size={20} /><div><h2>时空裂缝史诗掉率</h2><p className="muted">独立控制 316 时空裂缝领主结算：是否触发 PVF 件数表（原表 1–4 件，期望约 1.75 件）。不影响普通深渊掉率。</p></div></div>
      {riftError && <p className="inline-error" role="alert">{riftError}</p>}
      <div className="activity-overview glass" aria-label="裂缝掉率概览">
        <div><Swords size={19} /><span>当前触发率<strong>{riftData ? `${riftEffective}‰` : "—"}</strong></span></div>
        <div><Pill tone={riftData?.override ? "orange" : "green"}>{riftData ? (riftData.override ? "运行时覆盖" : "PVF 原表") : "—"}</Pill></div>
        <div><span>PVF 基线<strong>{riftData ? `${riftData.pvfBaseline}‰` : "—"}</strong></span></div>
        <div><span>期望史诗<strong>{riftData ? `${riftExpected} 件` : "—"}</strong></span></div>
      </div>
      <div className="toolbar activity-toolbar">
        <label htmlFor="rift-rate">目标触发率（0–1000‰，-1 恢复 PVF 原表）</label>
        <input id="rift-rate" type="number" min={-1} max={1000} value={riftDraft}
          onChange={(e) => setRiftDraft(Number(e.target.value))} disabled={!canWrite} />
        {canWrite && <button className="primary" disabled={busy || riftDraft < -1 || riftDraft > 1000} onClick={() => setRiftConfirm(riftDraft)}>应用</button>}
        {live && <button className="secondary" disabled={busy} onClick={() => void loadRift()}><RefreshCw size={15} />刷新</button>}
      </div>
      <div className="toolbar activity-toolbar">
        <span className="muted">快捷预设</span>
        {riftPresets.map((p) => <button key={p.rate} className="secondary" disabled={!canWrite || busy} onClick={() => setRiftConfirm(p.rate)}>{p.label}</button>)}
      </div>
      <p className="activity-footnote">1000‰ = 保留 PVF 原行为（每次领主结算必套用 1–4 件表）；500‰ = 约一半结算套用原表，另一半不掉史诗；0‰ = 关闭裂缝史诗。碎片保底仍按原规则发放，运行时覆盖重启后回到 PVF 原表。</p>
    </div>
    {confirm !== null && <Modal title="确认修改深渊掉率" onClose={() => setConfirm(null)} footer={<>
      <button className="secondary" onClick={() => setConfirm(null)}>取消</button>
      <button className="primary" onClick={() => void apply(confirm)}>确认应用</button>
    </>}>
      <p>将<strong>{confirm < 0 ? "清除运行时覆盖，恢复 PVF 默认掉率" : `深渊史诗掉率改为 ${confirm}‰`}</strong>。确认后立即对后续深渊结算生效，无需重启。确认前请知会玩家。</p>
    </Modal>}
    {riftConfirm !== null && <Modal title="确认修改时空裂缝掉率" onClose={() => setRiftConfirm(null)} footer={<>
      <button className="secondary" onClick={() => setRiftConfirm(null)}>取消</button>
      <button className="primary" onClick={() => void applyRift(riftConfirm)}>确认应用</button>
    </>}>
      <p>将<strong>{riftConfirm < 0 ? "清除运行时覆盖，恢复 PVF 原表（1–4 件）" : `时空裂缝 PVF 件数表触发率改为 ${riftConfirm}‰`}</strong>。确认后立即对后续裂缝领主结算生效，无需重启。确认前请知会玩家。</p>
    </Modal>}
  </div>;
}
