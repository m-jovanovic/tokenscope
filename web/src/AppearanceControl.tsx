import { useEffect, useState } from 'react'

type Appearance = 'system' | 'light' | 'dark'
const storageKey = 'tokenscope-appearance'

function savedAppearance(): Appearance {
  try {
    const saved = window.localStorage.getItem(storageKey)
    return saved === 'light' || saved === 'dark' ? saved : 'system'
  } catch {
    return 'system'
  }
}

export function AppearanceControl() {
  const [appearance, setAppearance] = useState<Appearance>(savedAppearance)

  useEffect(() => {
    const system = window.matchMedia('(prefers-color-scheme: dark)')
    const applyTheme = () => {
      const theme = appearance === 'system' ? (system.matches ? 'dark' : 'light') : appearance
      const root = document.documentElement
      root.dataset.theme = theme
      root.classList.toggle('dark', theme === 'dark')
      root.style.colorScheme = theme
    }

    applyTheme()
    system.addEventListener('change', applyTheme)
    return () => system.removeEventListener('change', applyTheme)
  }, [appearance])

  useEffect(() => {
    const syncPreference = (event: StorageEvent) => {
      if (event.key === storageKey || event.key === null) setAppearance(savedAppearance())
    }
    window.addEventListener('storage', syncPreference)
    return () => window.removeEventListener('storage', syncPreference)
  }, [])

  const changeAppearance = (next: Appearance) => {
    setAppearance(next)
    try {
      window.localStorage.setItem(storageKey, next)
    } catch {
      // The selected appearance still works when browser storage is unavailable.
    }
  }

  return <label className="appearance-control">
    <span>Appearance</span>
    <select className="appearance-select" aria-label="Appearance" value={appearance} onChange={event => changeAppearance(event.target.value as Appearance)}>
      <option value="system">System</option>
      <option value="light">Light</option>
      <option value="dark">Dark</option>
    </select>
  </label>
}
