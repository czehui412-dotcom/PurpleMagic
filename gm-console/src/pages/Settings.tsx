import { NewsManager } from "../news/NewsManager";
import { useEnterMotion } from "../motion";
import { useEffect, useState } from "react";
import {
  Download,
  Save,
  Send,
  Server,
  Globe,
  Package,
} from "lucide-react";
import { Tabs, Field, type PageProps } from "../ui";
import { HubStatus } from "../api/controls";
import { useLive } from "../api/context";
function HubSettings({
  state,
  request,
  tab,
  ...props
}: PageProps & { tab: "news" | "source" }) {
  const live = useLive();
  const sources = state.api?.hub?.sources || [];
  const [sourceId, setSourceId] = useState(sources[0]?.id || "");
  const source = sources.find((s: any) => s.id === sourceId);
  const [sourceName, setSourceName] = useState(source?.name || ""),
    [publicKey, setPublicKey] = useState(source?.publicKey || ""),
    [certificate, setCertificate] = useState(source?.certificateSha256 || ""),
    [sourceEnabled, setSourceEnabled] = useState(source?.enabled ?? true);
  const [download, setDownload] = useState(state.sources.download),
    [gameHost, setGameHost] = useState(state.sources.gameHost),
    [gamePort, setGamePort] = useState(state.sources.gamePort);
  function chooseSource(id: string) {
    const s = sources.find((s: any) => s.id === id);
    setSourceId(id);
    setSourceName(s?.name || "");
    setDownload(s?.endpoint || "");
    setPublicKey(s?.publicKey || "");
    setCertificate(s?.certificateSha256 || "");
    setSourceEnabled(s?.enabled ?? true);
  }
  const motion = useEnterMotion(tab);
  return (
    <div ref={motion} className="motion-section">
      {tab === "news" ? (
        <NewsManager state={state} request={request} {...props} />
      ) : (
        <div className="form-layout">
          <form
            className="glass content-panel"
            onSubmit={(e) => {
              e.preventDefault();
              request({
                kind: "sources",
                download,
                gameHost,
                gamePort,
                ...(live && download
                  ? {
                      source: {
                        id: sourceId,
                        name: sourceName,
                        endpoint: download,
                        publicKey,
                        certificateSha256: certificate,
                        enabled: sourceEnabled,
                      },
                    }
                  : {}),
              });
            }}
          >
            <h2>
              <Download size={20} />
              下载源
            </h2>
            {live && (
              <>
                <Field label="下载源列表">
                  <select
                    value={source?.id || ""}
                    onChange={(e) => chooseSource(e.target.value)}
                  >
                    <option value="">新增下载源</option>
                    {sources.map((s: any) => (
                      <option value={s.id} key={s.id}>
                        {s.name}
                      </option>
                    ))}
                  </select>
                </Field>
                <div className="field-grid">
                  <Field label="来源标识">
                    <input
                      value={sourceId}
                      disabled={!!source}
                      pattern="[a-z0-9][a-z0-9-]{0,47}"
                      onChange={(e) => setSourceId(e.target.value)}
                      placeholder="primary"
                    />
                  </Field>
                  <Field label="来源名称">
                    <input
                      value={sourceName}
                      onChange={(e) => setSourceName(e.target.value)}
                      placeholder="主下载源"
                    />
                  </Field>
                </div>
              </>
            )}
            <Field label="更新资源地址">
              <input
                required={!live}
                type="url"
                value={download}
                onChange={(e) => setDownload(e.target.value)}
                placeholder="https://downloads.example.com"
              />
            </Field>
            {live && (
              <details className="source-trust">
                <summary>签名与证书配置</summary>
                <Field label="下载源签名公钥">
                  <textarea
                    rows={5}
                    value={publicKey}
                    onChange={(e) => setPublicKey(e.target.value)}
                    placeholder="-----BEGIN PUBLIC KEY-----"
                  />
                </Field>
                <Field label="证书 SHA256（可选）">
                  <input
                    value={certificate}
                    onChange={(e) => setCertificate(e.target.value)}
                  />
                </Field>
                <label className="checkline">
                  <input
                    type="checkbox"
                    checked={sourceEnabled}
                    onChange={(e) => setSourceEnabled(e.target.checked)}
                  />
                  启用此来源
                </label>
              </details>
            )}
            <div className="form-divider" />
            <h2>
              <Server size={20} />
              游戏连接
            </h2>
            <div className="field-grid">
              <Field label="游戏地址">
                <input
                  required
                  value={gameHost}
                  onChange={(e) => setGameHost(e.target.value)}
                  placeholder="game.example.com"
                />
              </Field>
              <Field label="端口">
                <input
                  type="number"
                  min="1"
                  max="65535"
                  value={gamePort}
                  onChange={(e) => setGamePort(Number(e.target.value))}
                />
              </Field>
            </div>
            <div className="form-footer">
              <span>保存来源；游戏连接发布到登录器配置</span>
              <button className="primary">
                <Save size={15} />
                保存配置
              </button>
            </div>
          </form>
          <aside className="glass content-panel">
            <h3>
              <Globe size={18} />
              当前配置
            </h3>
            <dl className="detail-list stacked">
              <div>
                <dt>下载源</dt>
                <dd>{state.sources.download}</dd>
              </div>
              <div>
                <dt>游戏地址</dt>
                <dd>
                  {state.sources.gameHost}:{state.sources.gamePort}
                </dd>
              </div>
            </dl>
          </aside>
        </div>
      )}
    </div>
  );
}

export function Settings(props: PageProps) {
  const live = useLive();
  const [tab, setTab] = useState<"news" | "source" | "pvf" | "archive">("news");
  useEffect(() => {
    if (!live || tab === "pvf" || tab === "archive" || live.meta.hub) return;
    void live.guard(() => live.hub());
    return () => live.cancelScope("hub");
  }, [live, tab]);
  return (
    <>
      <div className="section-tabs">
        <Tabs
          label="公告与连接设置"
          items={[
            { id: "news", label: "公告管理" },
            { id: "source", label: "下载源与游戏连接" },
            ...(live ? [{ id: "archive" as const, label: "游戏资源直链" }, { id: "pvf" as const, label: "PVF 数据模式" }] : []),
          ]}
          value={tab}
          onChange={(value) => {
            live?.navigate("settings", true);
            setTab(value);
          }}
        />
      </div>
      {tab === "pvf" ? (
        <PVFSettings {...props} />
      ) : tab === "archive" ? (
        <ArchiveSettings {...props} />
      ) : live && !props.state.api?.hub ? (
        <HubStatus />
      ) : (
        <HubSettings {...props} tab={tab} />
      )}
    </>
  );
}
function PVFSettings({ state }: PageProps) {
  const live = useLive()!;
  const mode = state.api?.resources.pvfMode;
  const [saving, setSaving] = useState(false);
  useEffect(() => {
    void live.guard(() => live.pvfMode());
    return () => live.cancelScope("pvfMode");
  }, [live]);
  useEffect(() => {
    if (!mode?.loading) return;
    const timer = setTimeout(() => void live.guard(() => live.pvfMode()), 1500);
    return () => clearTimeout(timer);
  }, [live, mode]);
  const change = (value: "disk" | "memory") => {
    setSaving(true);
    void live.guard(async () => {
      try {
        await live.pvfMode(value);
      } finally {
        setSaving(false);
      }
    });
  };
  return (
    <section className="glass content-panel">
      <h3>PVF 数据模式</h3>
      <p>
        磁盘索引使用较少内存。直接加载 PVF
        将完整文件与目录放入内存，加快物品、任务等定义的读取。
      </p>
      <p>
        直接加载很快，但内存开销较高：完整文件约{" "}
        {Math.ceil((mode?.archiveBytes || 0) / 1048576)} MiB，另需目录和最多 96
        MiB 的解码缓存。
      </p>
      <div className="button-row">
        <button
          className={mode?.desired === "disk" ? "primary" : "secondary"}
          disabled={saving || !mode}
          onClick={() => change("disk")}
        >
          磁盘索引
        </button>
        <button
          className={mode?.desired === "memory" ? "primary" : "secondary"}
          disabled={saving || !mode || !!mode.loading}
          onClick={() => change("memory")}
        >
          直接加载 PVF
        </button>
        <button
          className="secondary"
          onClick={() => void live.guard(() => live.pvfMode())}
        >
          刷新状态
        </button>
      </div>
      {mode && (
        <p role="status">
          当前使用：{mode.mode === "memory" ? "内存 PVF" : "磁盘索引"}
          {mode.loading ? " · 正在后台加载 PVF" : ""}
        </p>
      )}
      {mode?.error && (
        <p className="inline-error" role="alert">
          {mode.error}
        </p>
      )}
      <p className="muted">
        设置自动保存。加载期间继续使用磁盘索引，切回后释放内存归档。
      </p>
      {mode && (
        <small className="muted" style={{ overflowWrap: "anywhere" }}>
          PVF {mode.pvfChecksum}
        </small>
      )}
    </section>
  );
}

type ArchiveManifest = {
  sha256: string;
  size: number;
  files: { path: string; length: number; sha256: string }[];
};
function ArchiveSettings(_props: PageProps) {
  const live = useLive()!;
  const [primaryUrl, setPrimaryUrl] = useState("");
  const [fallbackUrl, setFallbackUrl] = useState("");
  const [manifestText, setManifestText] = useState("");
  const [revision, setRevision] = useState("");
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  const load = async () => {
    const data = (await live.http.get("/api/v1/hub/state")).data;
    const archive = data?.published?.archive;
    setRevision(data?.archiveRevision || "");
    setPrimaryUrl(archive?.primaryUrl || "");
    setFallbackUrl(archive?.fallbackUrl || "");
    if (archive?.sha256 && archive?.files) {
      setManifestText(JSON.stringify(
        { sha256: archive.sha256, size: archive.size, files: archive.files },
        null,
        2,
      ));
    }
  };
  useEffect(() => {
    void live.guard(async () => {
      try { await load(); } catch { /* hub 不可用时静默 */ }
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(""); setMessage("");
    let manifest: ArchiveManifest;
    try {
      manifest = JSON.parse(manifestText);
      if (!manifest.sha256 || !manifest.size || !Array.isArray(manifest.files) || !manifest.files.length)
        throw new Error("清单缺少 sha256 / size / files");
    } catch (err) {
      setError("清单 JSON 无效：" + (err instanceof Error ? err.message : String(err)));
      return;
    }
    setSaving(true);
    try {
      await live.guard(async () => {
        await live.http.put(
          "/api/v1/hub/archive",
          {
            primaryUrl: primaryUrl.trim(),
            fallbackUrl: fallbackUrl.trim(),
            sha256: manifest.sha256,
            size: manifest.size,
            files: manifest.files,
          },
          { headers: { "If-Match": `"${revision}"` } },
        );
        await load();
        setMessage("已发布。玩家下次启动将按新直链下载资源包。");
      });
    } catch (err) {
      setError("发布失败：" + (err instanceof Error ? err.message : String(err)));
    } finally {
      setSaving(false);
    }
  };

  const disable = async () => {
    setError(""); setMessage("");
    setSaving(true);
    try {
      await live.guard(async () => {
        await live.http.put(
          "/api/v1/hub/archive",
          { primaryUrl: "", fallbackUrl: "", sha256: "0".repeat(64), size: 1, files: [{ path: "DNF.exe", length: 1, sha256: "0".repeat(64) }] },
          { headers: { "If-Match": `"${revision}"` } },
        );
        await load();
        setMessage("已停用资源包直链，玩家回退到原有的逐文件更新通道。");
      });
    } catch (err) {
      setError("停用失败：" + (err instanceof Error ? err.message : String(err)));
    } finally {
      setSaving(false);
    }
  };

  return (
    <form className="glass content-panel" onSubmit={save}>
      <h2><Package size={20} />游戏资源直链</h2>
      <p className="muted">
        配置整包更新的两条直链。玩家启动登录器时若 DNF.exe 或 Script.pvf 与签名版本不一致，
        会自动从主链下载 zip，主链 5 秒无响应切备用链，下载校验后覆盖并重启登录器。
        登录器本体更新不受影响。
      </p>
      <Field label="主直链地址 (A)">
        <input value={primaryUrl} onChange={(e) => setPrimaryUrl(e.target.value)} placeholder="http://example.com/UrlHeader.php?d=..." />
      </Field>
      <Field label="备用直链地址 (B)">
        <input value={fallbackUrl} onChange={(e) => setFallbackUrl(e.target.value)} placeholder="http://backup.example.com/UrlHeader.php?d=..." />
      </Field>
      <Field label="资源清单 JSON（由 pack_game_archive.py 生成）">
        <textarea rows={10} value={manifestText} onChange={(e) => setManifestText(e.target.value)} placeholder='{"sha256":"...","size":0,"files":[{"path":"DNF.exe","length":0,"sha256":"..."}]}' />
      </Field>
      {message && <p role="status" className="muted">{message}</p>}
      {error && <p role="alert" className="inline-error">{error}</p>}
      <div className="form-footer">
        <button type="button" className="secondary" onClick={() => void disable()} disabled={saving || !revision}>
          停用直链
        </button>
        <button type="button" className="secondary" onClick={() => void live.guard(load)} disabled={saving}>
          刷新
        </button>
        <button className="primary" disabled={saving || !revision}>
          <Save size={15} />发布直链
        </button>
      </div>
    </form>
  );
}
