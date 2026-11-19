import { useEffect, useState } from "react";
import { RefreshCw } from "lucide-react";
import { useLive } from "./context";
import type { DTO } from "./model";

export function BenefitReconciliation() {
  const live = useLive();
  const [status, setStatus] = useState<DTO>(), [error, setError] = useState("");
  useEffect(() => {
    if (!live?.meta.features.backgroundBenefits) return;
    const controller = new AbortController();
    let active = false;
    const read = async () => {
      if (active) return;
      active = true;
      try {
        const r = await live.http.get("/api/v1/benefits/reconciliation", controller.signal);
        if (!controller.signal.aborted) { setStatus(r.data); setError(""); }
      } catch (e) { if (!controller.signal.aborted) setError((e as Error).message); }
      finally { active = false; }
    };
    void read();
    const timer = setInterval(() => void read(), 15000);
    return () => { clearInterval(timer); controller.abort(); };
  }, [live]);
  const date = (s: string) => new Date(s).toLocaleString("zh-CN", { hour12: false });
  return <div className="glass benefit-reconciliation" role="status">
    {status?.status === "active" && <RefreshCw size={15} className="is-spinning" />}
    <strong>新账号与新角色 15 秒内自动发放</strong><span>存量与失败补发由后台每 3 小时兜底校验</span>
    {status && <span>{!status.enabled ? "后台校验已暂停" : status.status === "active" ? `校验中 · 已检查 ${status.checked} 项` : status.nextAt ? `下次 ${date(status.nextAt)}` : "等待首次校验"}</span>}
    {status?.completedAt && <span>上轮完成 {date(status.completedAt)}</span>}
    {status && status.blocked?.length > 0 && <span>有 {status.blocked.length} 条规则的物品版本待核对</span>}
    {(error || status?.error) && <span role="alert">{error || status?.error}</span>}
  </div>;
}
