export default function Sparkline({ data, color, width = 96, height = 32 }: {
  data: number[]
  color: string
  width?: number
  height?: number
}) {
  if (data.length < 2) return null
  const min = Math.min(...data)
  const max = Math.max(...data)
  const range = max - min || 1
  const step = width / (data.length - 1)
  const points = data.map((v, i) => {
    const x = i * step
    const y = height - 3 - ((v - min) / range) * (height - 6)
    return `${x},${y}`
  }).join(' ')
  const area = `0,${height} ${points} ${width},${height}`
  return (
    <svg width={width} height={height} style={{ display: 'block' }}>
      <polygon points={area} fill={color} opacity={0.15} />
      <polyline points={points} fill="none" stroke={color} strokeWidth={1.8} strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}
