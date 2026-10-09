export function formatWhen(value?: string | null): string {
  if (!value) return ''
  const hasZone = /[zZ]|[+-]\d{2}:\d{2}$/.test(value)
  const date = new Date(hasZone ? value : `${value}Z`)
  if (Number.isNaN(date.getTime())) return value
  return date.toLocaleString()
}

export function formatBytes(value?: number | null): string {
  if (value == null || Number.isNaN(value)) return ''
  if (value < 1024) return `${value} B`
  const units = ['KB', 'MB', 'GB', 'TB']
  let size = value / 1024
  let index = 0
  while (size >= 1024 && index < units.length - 1) {
    size /= 1024
    index += 1
  }
  return `${size.toFixed(size >= 10 ? 0 : 1)} ${units[index]}`
}
