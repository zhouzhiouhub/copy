import React, { useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { Archive, Clipboard, Copy, FileVideo, Image, Lock, Pause, Play, Search, ShieldCheck, Trash2, Unlock, X } from 'lucide-react'
import './styles.css'

const sampleEntries = [
  { id: 'sample-1', type: 'text', preview: '季度发布会的视觉方向：把“正在发生”做成一种可以被看见的节奏。', value: '季度发布会的视觉方向：把“正在发生”做成一种可以被看见的节奏。', createdAt: Date.now() - 1000 * 60 * 8, source: '微信' },
  { id: 'sample-2', type: 'image', preview: '图片剪贴内容', value: '', createdAt: Date.now() - 1000 * 60 * 31, source: '文件资源管理器' },
  { id: 'sample-3', type: 'video', preview: 'launch-film-v03.mp4', value: 'launch-film-v03.mp4', createdAt: Date.now() - 1000 * 60 * 76, source: '文件资源管理器' },
  { id: 'sample-4', type: 'text', preview: 'https://design.example.com/atlas/board?view=archive', value: 'https://design.example.com/atlas/board?view=archive', createdAt: Date.now() - 1000 * 60 * 128, source: '浏览器' },
  { id: 'sample-5', type: 'text', preview: '“好的工具不会打断思考，它只是让思考有地方落脚。”', value: '“好的工具不会打断思考，它只是让思考有地方落脚。”', createdAt: Date.now() - 1000 * 60 * 191, source: '备忘录' }
]

function formatTime(timestamp) { return new Intl.DateTimeFormat('zh-CN', { hour: '2-digit', minute: '2-digit' }).format(timestamp) }
function formatDay(timestamp) { return new Intl.DateTimeFormat('zh-CN', { month: 'short', day: 'numeric', weekday: 'short' }).format(timestamp) }
function typeLabel(type) { return type === 'image' ? '图片' : type === 'video' ? '视频' : '文本' }

function App() {
  const [entries, setEntries] = useState(sampleEntries)
  const [selected, setSelected] = useState(sampleEntries[0])
  const [paused, setPaused] = useState(false)
  const [locked, setLocked] = useState(false)
  const [query, setQuery] = useState('')
  const [isReal, setIsReal] = useState(false)

  useEffect(() => {
    if (!window.clipboardAtlas) return
    window.clipboardAtlas.getEntries().then(({ entries: saved, paused: savedPaused }) => {
      setIsReal(true)
      setEntries(saved.length ? saved : [])
      setSelected(saved[0] || null)
      setPaused(savedPaused)
    })
    window.clipboardAtlas.onEntriesUpdated((updated) => { setEntries(updated); setSelected((current) => updated.find((item) => item.id === current?.id) || updated[0] || null) })
  }, [])

  const visibleEntries = entries.filter((entry) => !query || entry.preview.toLowerCase().includes(query.toLowerCase()))
  const togglePause = async () => { const next = !paused; setPaused(next); if (isReal) await window.clipboardAtlas.setPaused(next) }
  const selectEntry = async (entry) => { setSelected(entry); if (isReal) await window.clipboardAtlas.copyEntry(entry.id) }
  const clearEntries = async () => { setEntries([]); setSelected(null); if (isReal) await window.clipboardAtlas.clearEntries() }

  return <main className="app-shell">
    <header className="topbar"><div className="brand"><span className="brand-mark"><Clipboard size={18} /></span><span>复制档案</span><span className="brand-en">CLIPBOARD ATLAS</span></div><div className="window-actions"><button title="锁定看板" onClick={() => setLocked(!locked)}>{locked ? <Lock size={16} /> : <Unlock size={16} />}</button><button title="关闭看板" onClick={() => window.close()}><X size={17} /></button></div></header>
    {locked ? <section className="locked-state"><div className="lock-ring"><Lock size={27} /></div><h2>看板已锁定</h2><p>解锁后查看最近两天的剪贴内容</p><button className="primary-button" onClick={() => setLocked(false)}><Unlock size={16} /> 解锁看板</button></section> : <>
      <section className="intro"><div><p className="eyebrow">LOCAL MEMORY / 48 HOURS</p><h1>你复制过的，<em>都在这里。</em></h1><p className="subline">按时间整理的临时记忆库，安静地留住每一次复制。</p></div><div className="capture-status"><span className={paused ? 'status-dot paused' : 'status-dot'}></span><span>{paused ? '已暂停记录' : '正在记录'}</span></div></section>
      <section className="toolbar"><div className="search-field"><Search size={16} /><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="搜索记录内容" /></div><button className="pause-button" onClick={togglePause}>{paused ? <Play size={15} /> : <Pause size={15} />}{paused ? '继续记录' : '暂停记录'}</button></section>
      <section className="content-grid"><aside className="timeline"><div className="section-heading"><span>时间轴</span><span className="count">{visibleEntries.length} 条</span></div>{visibleEntries.length === 0 ? <div className="empty-mini">还没有剪贴内容</div> : <div className="timeline-list">{visibleEntries.map((entry, index) => <button className={`timeline-item ${selected?.id === entry.id ? 'active' : ''}`} key={entry.id} onClick={() => selectEntry(entry)}><span className="timeline-line"></span><span className="timeline-node">{entry.type === 'image' ? <Image size={13} /> : entry.type === 'video' ? <FileVideo size={13} /> : <span className="text-node">Aa</span>}</span><span className="timeline-meta"><strong>{index === 0 ? '刚刚' : formatTime(entry.createdAt)}</strong><small>{formatDay(entry.createdAt)}</small></span></button>)}</div>}</aside><section className="detail-pane">{selected ? <><div className="detail-header"><div><span className="detail-label">{typeLabel(selected.type)}记录</span><h2>{selected.type === 'text' ? '文字内容' : selected.type === 'image' ? '图片内容' : '视频文件'}</h2></div><span className="source-tag">来自 {selected.source}</span></div><div className={`preview-card ${selected.type}`}>{selected.type === 'image' ? <div className="image-preview"><Image size={30} /><span>剪贴板图片</span></div> : selected.type === 'video' ? <div className="video-preview"><FileVideo size={32} /><strong>{selected.preview}</strong><span>视频文件</span></div> : <p>{selected.preview}</p>}</div><div className="detail-footer"><span>{new Date(selected.createdAt).toLocaleString('zh-CN')}</span><button className="copy-button" onClick={() => selectEntry(selected)}><Copy size={15} />复制到剪贴板</button></div></> : <div className="empty-detail"><Archive size={30} /><h2>这里还很安静</h2><p>复制一段文字、图片或视频文件，它会出现在这里。</p></div>}</section></section>
      <footer className="bottom-bar"><div><ShieldCheck size={16} /><span>内容仅保存在此设备，超过 48 小时自动清理</span></div><button className="clear-button" onClick={clearEntries}><Trash2 size={14} />清空全部</button></footer>
    </>}
  </main>
}

createRoot(document.getElementById('root')).render(<App />)
