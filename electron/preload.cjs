const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('clipboardAtlas', {
  getEntries: () => ipcRenderer.invoke('history:get'),
  copyEntry: (id) => ipcRenderer.invoke('history:copy', id),
  clearEntries: () => ipcRenderer.invoke('history:clear'),
  setPaused: (paused) => ipcRenderer.invoke('history:pause', paused),
  setLocked: (locked) => ipcRenderer.invoke('history:lock', locked),
  onEntriesUpdated: (callback) => ipcRenderer.on('history:updated', (_event, entries) => callback(entries))
})
