import {lazy, Suspense, useEffect, useState} from "react";
import {Trash2, Save, Send, RefreshCw} from "lucide-react";
import {Field, Pill, Tabs, type PageProps} from "../ui";
import {useLive} from "../api/context";
import type {News} from "../domain";
const MarkdownBody = lazy(() => import("./MarkdownBody").then(m => ({default:m.MarkdownBody})));
import "./news.css";

// 三个独立公告位：综合 / 更新 / 指南各一条，互相独立编辑与发布；
// 发布时后端按分类互斥覆盖（exclusive），登录器对应页签即时显示。
const CATS = ["综合", "更新", "指南"] as const;
const fresh = (c: string): News => ({id:"",title:"",body:"",published:false,category:c,date:new Date().toLocaleDateString("sv-SE"),format:"markdown"});

function seeds(news: News[]): Record<string, News> {
  const out: Record<string, News> = {};
  for (const c of CATS) {
    const found = news.find(n => (n.category || "综合") === c);
    out[c] = found ? {...found, category: c, format: found.format || "text"} : fresh(c);
  }
  return out;
}

export function NewsManager({state,request}: PageProps) {
  const live = useLive();
  const [cat,setCat]=useState<(typeof CATS)[number]>("综合");
  const [view,setView]=useState("edit");
  const [dirty,setDirty]=useState<Record<string,boolean>>({});
  const [drafts,setDrafts]=useState<Record<string,News>>(()=>seeds(state.news));
  const hub=state.api?.hub;
  // 服务端列表变化时，只同步未在编辑的公告位（避免覆盖正在输入的内容）。
  useEffect(()=> {
    setDrafts(prev=>{
      const seeded=seeds(state.news);
      const next={...prev};
      for (const c of CATS) if (!dirty[c]) next[c]=seeded[c];
      return next;
    });
  },[state.news]);
  // 发布/删除完成后回填 id 与发布状态，并清除编辑标记。
  useEffect(()=> {
    const pending=state.api?.pending;
    if (!pending?.completed) return;
    const op=pending.steps.find(s=>s.path==="/api/v1/hub/news/entries" || s.path==="local-news-delete" || s.path==="local-draft");
    if (!op) return;
    if (op.path==="local-news-delete" || (op.path==="/api/v1/hub/news/entries" && op.body?.action==="delete")) {
      setDrafts(d=>{
        const next={...d};
        for (const c of CATS) if (next[c].id && next[c].id===op.body.id) next[c]=fresh(c);
        return next;
      });
    } else {
      // 服务器路径正文在 op.body.news 下；本地草稿路径直接在 op.body 下。
      const title=op.body?.news?.title ?? op.body?.title;
      const body=op.body?.news?.body ?? op.body?.body;
      setDrafts(d=>{
        const next={...d};
        for (const c of CATS) {
          const t=next[c];
          if (title===t.title && body===t.body)
            next[c]={...t,id:op.result?.id || t.id,published:op.path==="/api/v1/hub/news/entries"};
        }
        return next;
      });
    }
    setDirty({});
  },[state.api?.pending?.completed]);
  function edit(patch: Partial<News>) {
    setDrafts(d=>({...d,[cat]:{...d[cat],...patch}}));
    setDirty(p=>({...p,[cat]:true}));
  }
  const draft=drafts[cat];
  const revision=draft.sourceRevision || hub?.newsRevision;
  const publish=(published:boolean)=>request({kind:"news",...draft,published,exclusive:true,sourceRevision:revision});
  const bytes=new TextEncoder().encode(draft.body).length;
  return <div className="news-layout">
    <aside className="glass content-panel news-list">
      <div className="panel-heading"><h2>公告位 <small>3 个</small></h2><div className="button-row">
        <button className="icon-button" aria-label="刷新公告" onClick={()=>{live?.http.invalidate(["news"]);setDirty({});setDrafts(seeds(state.news));}}><RefreshCw size={16}/></button>
      </div></div>
      <div className="news-rows">
        {CATS.map(c=>(
          <button key={c} className={c===cat?"active":""} onClick={()=>setCat(c)}>
            <span><strong>{c}</strong><small>{drafts[c].published ? `已发布 · ${drafts[c].title || "未命名"}` : drafts[c].title ? "有未发布修改" : "未设置"}</small></span>
          </button>
        ))}
      </div>
      <p className="muted news-hint">三个公告位各自独立，发布后同分类只保留这一条；登录器对应页签显示该条公告。</p>
    </aside>
    <form className="glass content-panel news-editor" onSubmit={e=>{e.preventDefault();publish(false);}}>
      <div className="panel-heading"><h2>编辑「{cat}」公告</h2><Pill>{draft.published?"已发布":"未发布"}</Pill></div>
      <Field label="标题"><input required maxLength={160} value={draft.title} onChange={e=>edit({title:e.target.value})} placeholder={`${cat}公告标题`}/></Field>
      <div className="news-fields">
        <Field label="分类"><input value={cat} disabled/></Field>
        <Field label="显示日期"><input maxLength={32} value={draft.date} onChange={e=>edit({date:e.target.value})}/></Field>
        <Field label="正文格式"><select value={draft.format} onChange={e=>edit({format:e.target.value as News["format"]})}><option value="markdown">Markdown</option><option value="text">纯文本</option></select></Field>
      </div>
      <Tabs value={view} onChange={setView} label="公告正文" items={[{id:"edit",label:"编辑"},{id:"preview",label:"预览"}]}/>
      <div className="news-editor-body">{view==="edit" ? <textarea aria-label="公告正文" rows={16} value={draft.body} onChange={e=>edit({body:e.target.value})} placeholder={draft.format==="markdown" ? "## 更新内容\n\n- 支持 **加粗**、列表、表格、代码和链接":"输入公告内容"}/> : <Suspense fallback={<p className="muted">正在准备预览…</p>}><MarkdownBody body={draft.body || "暂无正文"} format={draft.format}/></Suspense>}</div>
      <div className="news-hint"><span>{draft.format==="markdown" ? "支持标题、列表、表格、引用、代码、链接及图片":"保留原有文字与换行"}</span><small>{bytes.toLocaleString()} / 65,536 字节</small></div>
      <div className="news-actions"><div className="button-row">
        {draft.id && <button type="button" className="danger secondary" onClick={()=>request({kind:"news-delete",id:draft.id,sourceRevision:revision})}><Trash2 size={15}/>下线此公告</button>}
      </div><div className="button-row"><button className="secondary" disabled={!draft.title.trim()}><Save size={15}/>保存草稿</button><button type="button" className="primary" disabled={!draft.title.trim() || !draft.body.trim() || bytes>65536} onClick={()=>publish(true)}><Send size={15}/>发布到「{cat}」页签</button></div></div>
      <p className="muted news-hint">发布后登录器「{cat}」页签立即更新；「下线」清空该公告位。</p>
    </form>
  </div>;
}
