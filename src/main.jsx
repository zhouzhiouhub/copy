import React, { useEffect, useMemo, useState } from 'react'
import { createRoot } from 'react-dom/client'
import {
  Archive,
  Check,
  ChevronLeft,
  ChevronRight,
  Clipboard,
  Copy,
  File,
  FileImage,
  FileVideo,
  FolderOpen,
  Image,
  Lock,
  MoreHorizontal,
  Pause,
  Pin,
  PinOff,
  Play,
  Power,
  Search,
  ShieldCheck,
  Trash2,
  Unlock,
  X
} from 'lucide-react'
import './styles.css'

const demoEntries = [
  {
    id: 'demo-text',
    type: 'text',
    title: '项目发布页文案',
    preview: '把最近两天复制过的文本、图片和视频都沉到一个安静的侧边栏里。',
    text: '把最近两天复制过的文本、图片和视频都沉到一个安静的侧边栏里。',
    createdAt: Date.now() - 1000 * 60 * 8,
    source: '演示数据'
  },
  {
    id: 'demo-image',
    type: 'image',
    title: '剪贴板图片',
    preview: '图片内容',
    createdAt: Date.now() - 1000 * 60 * 36,
    source: '演示数据',
    locked: true
  },
  {
    id: 'demo-video',
    type: 'video',
    title: 'launch-film-v03.mp4',
    preview: 'launch-film-v03.mp4',
    createdAt: Date.now() - 1000 * 60 * 75,
    source: '演示数据',
    files: [{ name: 'launch-film-v03.mp4', kind: 'video', extension: 'MP4', size: 38240000 }]
  }
]

const fallbackDock = { side: 'right', pinned: false, expanded: true }
const api = window.clipboardAtlas

function formatClock(timestamp) {
  return new Intl.DateTimeFormat('zh-CN', {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false
  }).format(timestamp)
}

function formatDate(timestamp) {
  return new Intl.DateTimeFormat('zh-CN', {
    month: '2-digit',
    day: '2-digit',
    weekday: 'short'
  }).format(timestamp)
}

function formatFullTime(timestamp) {
  return new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false
  }).format(timestamp)
}

function formatSize(bytes = 0) {
  if (!bytes) return ''
  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let index = 0
  while (value >= 1024 && index < units.length - 1) {
    value /= 1024
    index += 1
  }
  return `${value.toFixed(value >= 10 || index === 0 ? 0 : 1)} ${units[index]}`
}

function typeMeta(type) {
  if (type === 'image') return { label: '图片', Icon: FileImage, className: 'image' }
  if (type === 'video') return { label: '视频', Icon: FileVideo, className: 'video' }
  if (type === 'file') return { label: '文件', Icon: File, className: 'file' }
  return { label: '文本', Icon: Clipboard, className: 'text' }
}

function normalizeEntry(entry) {
  return {
    ...entry,
    text: entry.text ?? entry.value ?? entry.preview ?? '',
    html: entry.html || '',
    title: entry.title || entry.preview || '剪贴板内容',
    files: Array.isArray(entry.files) ? entry.files : [],
    locked: Boolean(entry.locked)
  }
}

function searchableText(entry) {
  const fileNames = entry.files?.map((file) => file.name || file.path).join(' ') || ''
  return `${entry.title || ''} ${entry.preview || ''} ${entry.text || ''} ${entry.source || ''} ${fileNames}`.toLowerCase()
}

function primaryMedia(entry) {
  if (!entry) return ''
  if (entry.dataUrl) return entry.dataUrl
  if (entry.mediaUrl) return entry.mediaUrl
  const mediaFile = entry.files?.find((file) => file.mediaUrl)
  return mediaFile?.mediaUrl || ''
}

function TimelineItem({
  entry,
  selected,
  copied,
  menuOpen,
  onSelect,
  onCopy,
  onToggleLock,
  onOpenPath,
  onToggleMenu
}) {
  const { Icon, label, className } = typeMeta(entry.type)

  return (
    <article
      className={`timeline-item ${selected ? 'active' : ''} ${entry.locked ? 'is-locked' : ''}`}
      onMouseEnter={() => onSelect(entry.id)}
      onClick={() => onCopy(entry.id)}
    >
      <span className="timeline-stem" />
      <span className={`timeline-icon ${className}`}>
        <Icon size={15} />
      </span>

      <span className="timeline-copy">
        <span className="timeline-headline">
          <strong className="timeline-time">{formatClock(entry.createdAt)}</strong>
          <span className={`type-pill ${className}`}>
            <Icon size={12} />
            {label}
          </span>
        </span>
        <span className="timeline-date">{formatDate(entry.createdAt)}</span>
        <strong className="timeline-title">{entry.title}</strong>
        <CardBody entry={entry} />
        <FileList files={entry.files} onOpenPath={onOpenPath} />
      </span>

      <button
        className={`card-more ${menuOpen ? 'is-open' : ''}`}
        title="更多操作"
        onClick={(event) => {
          event.stopPropagation()
          onToggleMenu(entry.id)
        }}
      >
        <MoreHorizontal size={18} />
      </button>

      {menuOpen ? (
        <div className="card-menu" onClick={(event) => event.stopPropagation()}>
          <button onClick={() => onToggleLock(entry.id)}>
            {entry.locked ? <Unlock size={14} /> : <Lock size={14} />}
            {entry.locked ? '取消锁定' : '锁定保留'}
          </button>
          <button onClick={() => onCopy(entry.id)}>
            {copied ? <Check size={14} /> : <Copy size={14} />}
            {copied ? '已粘贴' : '粘贴并隐藏'}
          </button>
          {entry.files?.[0]?.path ? (
            <button onClick={() => onOpenPath(entry.files[0].path)}>
              <FolderOpen size={14} />
              打开所在文件夹
            </button>
          ) : null}
        </div>
      ) : null}
    </article>
  )
}

function FileList({ files, onOpenPath }) {
  if (!files?.length) return null

  return (
    <div className="file-list">
      {files.map((file) => {
        const Icon = file.kind === 'video' ? FileVideo : file.kind === 'image' ? FileImage : file.kind === 'folder' ? FolderOpen : File
        return (
          <button
            className="file-row"
            key={file.path || file.name}
            onClick={(event) => {
              event.stopPropagation()
              if (file.path) onOpenPath(file.path)
            }}
          >
            <span className={`file-kind ${file.kind || 'file'}`}>
              <Icon size={16} />
            </span>
            <span className="file-main">
              <strong>{file.name || file.path}</strong>
              <small>{[file.extension, formatSize(file.size)].filter(Boolean).join(' · ') || '本地项目'}</small>
            </span>
            {file.path ? <FolderOpen size={15} /> : null}
          </button>
        )
      })}
    </div>
  )
}

function CardBody({ entry }) {
  const mediaSrc = primaryMedia(entry)

  if (entry.type === 'text') {
    return <p className="timeline-preview-text">{entry.text}</p>
  }

  if (entry.type === 'image' && mediaSrc) {
    return <img className="timeline-media" src={mediaSrc} alt={entry.title} />
  }

  if (entry.type === 'video' && mediaSrc) {
    return (
      <video
        className="timeline-media"
        src={mediaSrc}
        controls
        preload="metadata"
        onClick={(event) => event.stopPropagation()}
      />
    )
  }

  if (entry.type === 'image') {
    return (
      <div className="media-placeholder">
        <Image size={28} />
        <span>图片文件预览不可用</span>
      </div>
    )
  }

  const { Icon } = typeMeta(entry.type)

  return (
    <div className="media-placeholder">
      <Icon size={28} />
      <span>{entry.preview || entry.title}</span>
    </div>
  )
}

function App() {
  const [entries, setEntries] = useState(api ? [] : demoEntries)
  const [selectedId, setSelectedId] = useState(api ? null : demoEntries[0].id)
  const [paused, setPaused] = useState(false)
  const [dock, setDock] = useState(fallbackDock)
  const [query, setQuery] = useState('')
  const [privacyLocked, setPrivacyLocked] = useState(false)
  const [copiedId, setCopiedId] = useState('')
  const [menuId, setMenuId] = useState('')

  useEffect(() => {
    if (!api) return undefined

    let disposeHistory = () => {}
    let disposeDock = () => {}

    api.getEntries().then(({ entries: savedEntries, paused: savedPaused, dock: savedDock }) => {
      const normalized = (savedEntries || []).map(normalizeEntry)
      setEntries(normalized)
      setSelectedId(normalized[0]?.id || null)
      setPaused(Boolean(savedPaused))
      setDock(savedDock || fallbackDock)
    })

    disposeHistory = api.onEntriesUpdated((updated) => {
      const normalized = (updated || []).map(normalizeEntry)
      setEntries(normalized)
      setSelectedId((current) => normalized.find((entry) => entry.id === current)?.id || normalized[0]?.id || null)
    })

    disposeDock = api.onDockUpdated((updated) => {
      setDock(updated || fallbackDock)
    })

    return () => {
      disposeHistory()
      disposeDock()
    }
  }, [])

  useEffect(() => {
    if (!menuId) return undefined
    const close = () => setMenuId('')
    window.addEventListener('click', close)
    return () => window.removeEventListener('click', close)
  }, [menuId])

  const visibleEntries = useMemo(() => {
    const normalized = entries.map(normalizeEntry)
    const needle = query.trim().toLowerCase()
    if (!needle) return normalized
    return normalized.filter((entry) => searchableText(entry).includes(needle))
  }, [entries, query])

  const counts = useMemo(() => {
    return visibleEntries.reduce(
      (summary, entry) => {
        summary.total += 1
        summary[entry.type] = (summary[entry.type] || 0) + 1
        return summary
      },
      { total: 0, text: 0, image: 0, video: 0, file: 0 }
    )
  }, [visibleEntries])

  async function togglePaused() {
    const next = !paused
    setPaused(next)
    if (api) setPaused(await api.setPaused(next))
  }

  async function togglePinned() {
    const next = !dock.pinned
    setDock({ ...dock, pinned: next, expanded: next || dock.expanded })
    if (api) setDock(await api.setDockPinned(next))
  }

  async function switchSide() {
    const next = dock.side === 'left' ? 'right' : 'left'
    setDock({ ...dock, side: next, expanded: true })
    if (api) setDock(await api.setDockSide(next))
  }

  async function copyEntry(id) {
    setMenuId('')
    if (!api) return
    const ok = await api.copyEntry(id)
    if (!ok) return
    setCopiedId(id)
    window.setTimeout(() => setCopiedId(''), 1100)
  }

  function toggleMenu(id) {
    setMenuId((current) => (current === id ? '' : id))
  }

  async function toggleLock(id) {
    if (!api) {
      setEntries((current) => current.map((item) => (item.id === id ? { ...item, locked: !item.locked } : item)))
      return
    }
    await api.toggleLock(id)
  }

  async function clearEntries() {
    const unlockedCount = entries.filter((entry) => !entry.locked).length
    if (!unlockedCount) return
    if (!window.confirm('清空未锁定的复制记录？已锁定的记录会保留。')) return
    if (api) {
      const remaining = await api.clearEntries()
      const normalized = (remaining || []).map(normalizeEntry)
      setEntries(normalized)
      setSelectedId(normalized[0]?.id || null)
      return
    }
    const remaining = entries.filter((entry) => entry.locked)
    setEntries(remaining)
    setSelectedId(remaining[0]?.id || null)
  }

  function openPath(filePath) {
    if (api) api.openPath(filePath)
  }

  return (
    <main className={`app-shell dock-${dock.side} ${dock.expanded ? 'is-expanded' : 'is-collapsed'}`}>
      <span className="edge-grip" />

      <header className="topbar">
        <div className="brand">
          <span className="brand-mark">
            <Clipboard size={18} />
          </span>
          <span>
            <strong>复制档案</strong>
            <small>48 小时剪贴板</small>
          </span>
        </div>

        <div className="window-actions">
          <button title={dock.side === 'left' ? '停靠到右侧' : '停靠到左侧'} onClick={switchSide}>
            {dock.side === 'left' ? <ChevronRight size={17} /> : <ChevronLeft size={17} />}
          </button>
          <button title={dock.pinned ? '取消固定展开' : '固定展开'} onClick={togglePinned}>
            {dock.pinned ? <PinOff size={16} /> : <Pin size={16} />}
          </button>
          <button title={privacyLocked ? '显示内容' : '隐藏内容'} onClick={() => setPrivacyLocked(!privacyLocked)}>
            {privacyLocked ? <Unlock size={16} /> : <Lock size={16} />}
          </button>
          <button title="收起到边缘" onClick={() => api?.collapseDock()}>
            <X size={17} />
          </button>
          <button title="退出程序" onClick={() => api?.quit()}>
            <Power size={16} />
          </button>
        </div>
      </header>

      {privacyLocked ? (
        <section className="privacy-screen">
          <div className="privacy-ring">
            <Lock size={30} />
          </div>
          <h2>内容已隐藏</h2>
          <p>解除隐藏后继续查看最近两天的复制记录。</p>
          <button className="primary-action" onClick={() => setPrivacyLocked(false)}>
            <Unlock size={16} />
            显示看板
          </button>
        </section>
      ) : (
        <>
          <section className="status-band">
            <div>
              <p className="eyebrow">LOCAL CLIPBOARD</p>
              <h2>复制记录</h2>
            </div>
            <button className={`record-toggle ${paused ? 'paused' : ''}`} onClick={togglePaused}>
              {paused ? <Play size={15} /> : <Pause size={15} />}
              {paused ? '继续' : '暂停'}
            </button>
          </section>

          <section className="toolbar">
            <label className="search-field">
              <Search size={16} />
              <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="搜索" />
            </label>
            <div className="metrics">
              <span>{counts.total} 条</span>
            </div>
          </section>

          <section className="board">
            <aside className="timeline">
              <div className="section-title">
                <span>时间轴</span>
                <small>{paused ? '已暂停' : `${counts.total} 条`}</small>
              </div>
              <div className="timeline-list">
                {visibleEntries.length ? (
                  visibleEntries.map((entry) => (
                    <TimelineItem
                      entry={entry}
                      key={entry.id}
                      selected={selectedId === entry.id}
                      copied={copiedId === entry.id}
                      menuOpen={menuId === entry.id}
                      onSelect={setSelectedId}
                      onCopy={copyEntry}
                      onToggleLock={toggleLock}
                      onOpenPath={openPath}
                      onToggleMenu={toggleMenu}
                    />
                  ))
                ) : (
                  <div className="timeline-empty">暂无匹配记录</div>
                )}
              </div>
            </aside>
          </section>

          <footer className="bottom-bar">
            <span>
              <ShieldCheck size={15} />
              本机保存 · 两天清理
            </span>
            <button onClick={clearEntries}>
              <Trash2 size={14} />
              清空
            </button>
          </footer>
        </>
      )}
    </main>
  )
}

createRoot(document.getElementById('root')).render(<App />)
