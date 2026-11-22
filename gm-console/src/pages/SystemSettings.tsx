import { useEffect, useState } from "react";
import { AlertTriangle, Atom, LoaderCircle, Settings2, Wrench } from "lucide-react";
import { useLive } from "../api/context";
import type { PageProps } from "../ui";

type Maintenance = { enabled: boolean; message?: string; since?: string; kicked?: number };

export function SystemSettings({ state }: PageProps) {
  const live = useLive();
  const [demoMode, setDemoMode] = useState(false);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState("");
  const [maint, setMaint] = useState<Maintenance>({ enabled: false });
  const [maintMsg, setMaintMsg] = useState("服务器维护中，请稍后再试");
  const [maintBusy, setMaintBusy] = useState(false);
  const supported = !live || !!state.api?.features["system.settings"];
  const policy = state.api?.systemSettings;
  const enabled = live ? !!policy?.carbonMode : demoMode;
  useEffect(() => {
    if (live && supported) void live.guard(() => live.systemSettings());
    return () => live?.cancelScope("systemSettings");
  }, [live, supported]);
  useEffect(() => {
    if (!live) return;
    void live.maintenanceStatus().then(setMaint).catch(() => {});
  }, [live]);
  async function change(enabled: boolean) {
    setSaving(true); setMessage("");
    try {
      if (live) await live.systemSettings(enabled);
      else setDemoMode(enabled);
      setMessage(enabled ? "碳铀模式已开启，全局生效" : "碳铀模式已关闭");
    } catch (e) { setMessage((e as Error).message); }
    finally { setSaving(false); }
  }
  async function toggleMaintenance(enter: boolean) {
    if (!live) { setMessage("维护开关需要连接真实网关"); return; }
    setMaintBusy(true); setMessage("");
    try {
      const st = await live.setMaintenance(enter ? "enter" : "leave", maintMsg);
      setMaint(prev => ({ ...prev, ...st }));
      setMessage(enter
        ? `维护已开启，已踢 ${st.kicked ?? 0} 名在线玩家。可以安全更换 PVF / 重启服务。`
        : "维护已结束，玩家可以重新登录。");
    } catch (e) { setMessage("维护切换失败：" + (e as Error).message); }
    finally { setMaintBusy(false); }
  }
  return <div className="form-layout">
    <section className="glass content-panel">
      <div className="panel-heading"><h2><Settings2 size={18} /> 系统设置</h2></div>
      <div className="form-section">
        <h3><Atom size={18} /> 碳铀模式</h3>
        <p>开启后，角色在线时也可修改物品、任务、名字、成长、金币、邮件和仓库。保存后对所有 GM 会话生效。</p>
        <p className="muted">直接写入数据库，不踢下线。游戏内存不会同步刷新，后续游戏存档可能覆盖修改。</p>
        <label className="checkline">
          <input className="checkbox" type="checkbox" role="switch" aria-label="碳铀模式"
            checked={enabled} disabled={!supported || saving || (!!live && !policy)}
            onChange={e => void change(e.target.checked)} />
          {enabled ? "已开启" : "已关闭"}
          {saving && <LoaderCircle size={16} className="spin" />}
        </label>
        {!supported && <p className="muted">更新管理网关后可使用系统设置。</p>}
      </div>
      <div className="form-section">
        <h3><Wrench size={18} /> 服务器维护</h3>
        <p>开启维护会<strong>踢掉全部在线玩家</strong>并阻止新登录，同时通知登录器关闭客户端、显示维护页。用于安全更换 PVF / 重启服务端或网关。</p>
        <p className="muted">当前状态：<strong style={{color: maint.enabled ? "#e8a33d" : undefined}}>{maint.enabled ? "维护中" : "正常"}</strong>{maint.enabled && maint.since ? `（自 ${maint.since}）` : ""}</p>
        <label className="checkline">
          维护提示：<input value={maintMsg} disabled={maint.enabled || maintBusy}
            onChange={e => setMaintMsg(e.target.value)} style={{flex: 1, minWidth: 200}} />
        </label>
        {!maint.enabled ? (
          <button disabled={!live || maintBusy} onClick={() => void toggleMaintenance(true)}>
            {maintBusy ? <LoaderCircle size={16} className="spin" /> : <AlertTriangle size={16} />} 开启维护并踢下线
          </button>
        ) : (
          <button disabled={!live || maintBusy} onClick={() => void toggleMaintenance(false)}>
            {maintBusy ? <LoaderCircle size={16} className="spin" /> : <Wrench size={16} />} 结束维护
          </button>
        )}
        {!live && <p className="muted">演示模式不连接真实网关，维护开关不可用。</p>}
        {message && <p role="status">{message}</p>}
      </div>
    </section>
  </div>;
}
