const { contextBridge, ipcRenderer } = require('electron')

function subscribe(channel, callback) {
  const listener = (_event, payload) => callback(payload)
  ipcRenderer.on(channel, listener)
  return () => ipcRenderer.removeListener(channel, listener)
}

contextBridge.exposeInMainWorld('clipboardAtlas', {
  getEntries: () => ipcRenderer.invoke('history:get'),
  copyEntry: (id) => ipcRenderer.invoke('history:copy', id),
  clearEntries: () => ipcRenderer.invoke('history:clear'),
  setPaused: (paused) => ipcRenderer.invoke('history:pause', paused),
  openPath: (filePath) => ipcRenderer.invoke('history:open-path', filePath),
  onEntriesUpdated: (callback) => subscribe('history:updated', callback),

  getDock: () => ipcRenderer.invoke('dock:get'),
  expandDock: () => ipcRenderer.invoke('dock:expand'),
  collapseDock: () => ipcRenderer.invoke('dock:collapse'),
  setDockSide: (side) => ipcRenderer.invoke('dock:set-side', side),
  setDockPinned: (pinned) => ipcRenderer.invoke('dock:set-pinned', pinned),
  onDockUpdated: (callback) => subscribe('dock:updated', callback),

  quit: () => ipcRenderer.invoke('app:quit')
})
