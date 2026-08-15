import { Card, Avatar, theme } from 'antd'
import type { ReactNode } from 'react'
import Sparkline from './Sparkline'

export interface StatCardProps {
  /** 元素 id(可选) */
  id?: string
  /** 附加类名(可选) */
  className?: string
  title: string
  value: string | number
  valueColor?: string
  helper?: string
  helperColor?: string
  icon: ReactNode
  iconBg: string
  iconColor: string
  sparkline?: number[]
  sparklineColor?: string
}

/** 统一结构的统计卡片:标题/数值/辅助 + 图标 + 底部图表区,所有卡片等高。 */
export default function StatCard(props: StatCardProps) {
  const { token } = theme.useToken()
  const { title, value, valueColor, helper, helperColor, icon, iconBg, iconColor, sparkline, sparklineColor, id, className } = props
  return (
    <Card
      id={id}
      className={`clipsync-stat-card ${className ?? ''}`}
      style={{ borderRadius: 14, height: '100%' }}
      styles={{ body: { padding: 18, display: 'flex', flexDirection: 'column' } }}
    >
      <div className="clipsync-stat-card-body" style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: 8 }}>
        <div style={{ minWidth: 0 }}>
          <div style={{ color: token.colorTextTertiary, fontSize: 13, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
            {title}
          </div>
          <div
            style={{
              fontSize: 24, fontWeight: 700, color: valueColor ?? token.colorText,
              marginTop: 6, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            }}
          >
            {value}
          </div>
          {helper ? (
            <div style={{ color: helperColor ?? token.colorTextTertiary, fontSize: 12, marginTop: 4, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
              {helper}
            </div>
          ) : null}
        </div>
        <Avatar shape="square" size={40} style={{ background: iconBg, color: iconColor, flexShrink: 0 }} icon={icon} />
      </div>
      {/* 底部图表区:固定高度占位,保证所有卡片底部对齐 */}
      <div className="clipsync-stat-card-chart" style={{ marginTop: 'auto', paddingTop: 12, height: 44, display: 'flex', alignItems: 'flex-end' }}>
        {sparkline && sparkline.length > 1 ? (
          <Sparkline data={sparkline} color={sparklineColor ?? '#2563EB'} width={120} height={32} />
        ) : null}
      </div>
    </Card>
  )
}
