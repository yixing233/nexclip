import * as signalR from '@microsoft/signalr'
import { getToken, deviceId } from './api'

export interface HubEvents {
  onClipboardUpdated: (entry: unknown) => void
  onClipboardCleared: () => void
  onStatusChange: (status: string) => void
}

let connection: signalR.HubConnection | null = null

export function connectHub(events: HubEvents): void {
  if (connection) return
  connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/clipboard?deviceId=' + encodeURIComponent(deviceId()), {
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
