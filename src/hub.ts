import * as signalR from '@microsoft/signalr'
import { getToken, deviceId, getBrowserPlatform, getDefaultDeviceName } from './api'

export interface HubEvents {
  onClipboardUpdated: (entry: unknown) => void
  onClipboardCleared: () => void
  onDevicesChanged: () => void
  onStatusChange: (status: string) => void
  /** 文件互传:其它端/会话上传了新文件(FileItem) */
  onFileTransferred: (file: unknown) => void
  /** 文件互传:某文件被删除(参数为 fileId) */
  onFileDeleted: (fileId: string) => void
}

let connection: signalR.HubConnection | null = null

export function connectHub(events: HubEvents): void {
  if (connection) return
  const hubUrl =
    '/hubs/clipboard?deviceId=' +
    encodeURIComponent(deviceId()) +
    '&platform=' +
    encodeURIComponent(getBrowserPlatform()) +
    '&deviceName=' +
    encodeURIComponent(getDefaultDeviceName())
  connection = new signalR.HubConnectionBuilder()
    .withUrl(hubUrl, {
      accessTokenFactory: () => getToken() ?? '',
      skipNegotiation: false,
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000])
    .build()

  connection.on('ClipboardUpdated', (entry) => {
    events.onClipboardUpdated(entry)
  })

  connection.on('ClipboardCleared', () => {
    events.onClipboardCleared()
  })

  connection.on('DevicesChanged', () => {
    events.onDevicesChanged()
  })

  connection.on('FileTransferred', (file) => {
    events.onFileTransferred(file)
  })

  connection.on('FileDeleted', (fileId) => {
    events.onFileDeleted(fileId)
  })

  connection.onreconnecting(() => events.onStatusChange('reconnecting'))
  connection.onreconnected(() => events.onStatusChange('connected'))
  connection.onclose(() => events.onStatusChange('disconnected'))

  connection.start().then(
    () => events.onStatusChange('connected'),
    () => events.onStatusChange('disconnected'),
  )
}

export function disconnectHub(): void {
  connection?.stop()
  connection = null
}
