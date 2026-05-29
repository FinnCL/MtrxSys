import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'

// Diferencia a aba do navegador quando rodando em multi-ambiente: cada Stack injeta
// VITE_APP_LABEL ("A", "B"...) no build/dev e o titulo da aba vira "MtrxSys A", "MtrxSys B".
// Espelha o <h1> dos cards da landing pra ficar facil identificar qual ambiente e qual.
const label = import.meta.env.VITE_APP_LABEL as string | undefined
if (label) {
  document.title = `MtrxSys ${label}`
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
