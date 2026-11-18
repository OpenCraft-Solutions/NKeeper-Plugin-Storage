import type { ReactNode } from 'react'
import type { ToolState } from './tool'

/**
 * The tool's styles, adopted when the plugin activates: 1.x's session look (a status bar over the tool, cards for
 * opening and failing), on the shell's design tokens, each with a fallback (docs/architecture.md §7.14).
 */
export const toolStyles = `
.nk-storage { display: flex; flex-direction: column; height: 100%; min-height: 0; background: var(--color-bg, #0b1220);
  color: var(--color-text, #e5ebf5); font-family: var(--font-sans, system-ui, sans-serif); font-size: 0.875rem; }
.nk-storage-status { display: flex; align-items: center; gap: 0.5rem; min-height: 2.25rem; padding: 0.25rem 1rem;
  background: var(--color-surface, #111a2e); border-bottom: 1px solid var(--color-border, #233150); font-size: 0.8125rem; }
.nk-storage-dot { width: 0.5rem; height: 0.5rem; flex-shrink: 0; border-radius: 999px; background: var(--color-text-subtle, #7d8ba6); }
.nk-storage-dot[data-state='connecting'] { background: var(--color-warning, #f5b84b); }
.nk-storage-dot[data-state='open'] { background: #4ade80; }
.nk-storage-dot[data-state='ended'] { background: #f87171; }
.nk-storage-status-text { font-weight: 550; }
.nk-storage-target { overflow: hidden; color: var(--color-text-muted, #9aa8c1); text-overflow: ellipsis; white-space: nowrap; }
.nk-storage-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5rem; margin-left: auto; }
.nk-storage-body { position: relative; flex: 1; min-height: 0; overflow: auto; padding: 0.75rem 1rem; }
.nk-storage-overlay { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; padding: 1rem; }
.nk-storage-card { display: flex; flex-direction: column; gap: 0.75rem; max-width: 32rem; padding: 1.25rem 1.5rem;
  background: var(--color-surface, #111a2e); border: 1px solid var(--color-border-strong, #33456b); border-radius: var(--radius-lg, 12px);
  box-shadow: var(--shadow-card, 0 1px 2px rgb(0 0 0 / 0.3), 0 8px 24px rgb(0 0 0 / 0.25)); font-size: 0.9375rem; }
.nk-storage-card[data-error] { border-color: var(--color-danger-border, #b91c1c); }
.nk-storage-card-row { flex-direction: row; align-items: center; }
.nk-storage-card h3 { margin: 0; font-size: 1.0625rem; font-weight: 620; }
.nk-storage-card p { margin: 0; }
.nk-storage-card-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 0.5rem; }
.nk-storage-spinner { width: 1rem; height: 1rem; border: 2px solid var(--color-border-strong, #33456b); border-top-color: var(--color-accent-text, #8fb4ff);
  border-radius: 999px; animation: nk-storage-spin 0.8s linear infinite; }
@keyframes nk-storage-spin { to { transform: rotate(360deg); } }
.nk-storage-button { display: inline-flex; align-items: center; justify-content: center; gap: 0.375rem; min-height: 2rem; padding: 0 0.75rem;
  border: 1px solid var(--color-border-strong, #33456b); border-radius: var(--radius-md, 8px); background: transparent;
  color: var(--color-text, #e5ebf5); font: inherit; font-size: 0.8125rem; font-weight: 600; cursor: pointer; white-space: nowrap; }
.nk-storage-button:hover:not(:disabled) { background: var(--color-surface-raised, #16213a); border-color: var(--color-border-hover, #46598a); }
.nk-storage-button:disabled { opacity: 0.5; cursor: default; }
.nk-storage-button[data-primary] { background: var(--color-accent, #2563eb); border-color: var(--color-accent, #2563eb); color: var(--color-on-accent, #fff); }
.nk-storage-button[data-danger] { border-color: var(--color-danger-border, #b91c1c); color: var(--color-danger-text, #fca5a5); }
.nk-storage-button[data-ghost] { border-color: transparent; }
.nk-storage-button[data-small] { min-height: 1.625rem; padding: 0 0.5rem; }
.nk-storage-input, .nk-storage-select, .nk-storage-textarea { min-height: 2rem; padding: 0 0.5rem; border: 1px solid var(--color-border-strong, #33456b);
  border-radius: var(--radius-md, 8px); background: var(--color-input-bg, #0b1220); color: var(--color-text, #e5ebf5); font: inherit; }
.nk-storage-textarea { width: 100%; min-height: 16rem; padding: 0.5rem; font-family: ui-monospace, 'Cascadia Mono', Consolas, monospace; font-size: 0.8125rem; }
.nk-storage-notice { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5rem; margin-bottom: 0.75rem; padding: 0.5rem 0.75rem;
  border: 1px solid var(--color-border-strong, #33456b); border-radius: var(--radius-md, 8px); background: var(--color-surface, #111a2e); }
.nk-storage-notice[data-error] { border-color: var(--color-danger-border, #b91c1c); color: var(--color-danger-text, #fca5a5); }
.nk-storage-notice > span { flex: 1; }
.nk-storage-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5rem; margin-bottom: 0.75rem; }
.nk-storage-form { display: flex; flex-wrap: wrap; align-items: flex-end; gap: 0.5rem; margin-bottom: 0.75rem; }
.nk-storage-form label { display: flex; flex-direction: column; gap: 0.25rem; font-size: 0.75rem; color: var(--color-text-muted, #9aa8c1); }
.nk-storage-table { width: 100%; border-collapse: collapse; }
.nk-storage-table th, .nk-storage-table td { padding: 0.375rem 0.5rem; border-bottom: 1px solid var(--color-border, #233150); text-align: left; vertical-align: top; }
.nk-storage-table th { position: sticky; top: -0.75rem; background: var(--color-bg, #0b1220); font-size: 0.75rem; font-weight: 600;
  color: var(--color-text-muted, #9aa8c1); }
.nk-storage-table tr[data-selected] td { background: var(--color-surface-raised, #16213a); }
.nk-storage-sort { padding: 0; border: 0; background: none; color: inherit; font: inherit; cursor: pointer; }
.nk-storage-link { padding: 0; border: 0; background: none; color: var(--color-accent-text, #8fb4ff); font: inherit; cursor: pointer; text-align: left; }
.nk-storage-link:hover { text-decoration: underline; }
.nk-storage-mono { font-family: ui-monospace, 'Cascadia Mono', Consolas, monospace; font-size: 0.8125rem; }
.nk-storage-muted { color: var(--color-text-muted, #9aa8c1); }
.nk-storage-crumbs { display: flex; flex-wrap: wrap; align-items: center; gap: 0.125rem; flex: 1; min-width: 0;
  font-family: ui-monospace, 'Cascadia Mono', Consolas, monospace; }
.nk-storage-row-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 0.25rem; }
.nk-storage-message { white-space: pre-wrap; word-break: break-word; }
.nk-storage-priority { display: inline-block; min-width: 4.5rem; padding: 0 0.375rem; border-radius: 999px; font-size: 0.75rem; text-align: center;
  background: var(--color-surface-raised, #16213a); }
.nk-storage-priority[data-level='0'], .nk-storage-priority[data-level='1'], .nk-storage-priority[data-level='2'], .nk-storage-priority[data-level='3'] {
  background: #7f1d1d; color: #fecaca; }
.nk-storage-priority[data-level='4'] { background: #78350f; color: #fde68a; }
.nk-storage-section { margin: 1rem 0 0.5rem; font-size: 0.9375rem; font-weight: 620; }
.nk-storage-progress { width: 12rem; }
.nk-storage-field { display: flex; flex-direction: column; gap: 0.25rem; font-size: 0.75rem; color: var(--color-text-muted, #9aa8c1); }
`

/** A tool's tab: the status bar, then the tool once it's open, or a card while it opens or after it failed. */
export function ToolFrame({
  title,
  host,
  state,
  reconnect,
  actions,
  children,
}: {
  title: string
  host: { name: string }
  state: ToolState
  reconnect: () => void
  actions?: ReactNode
  children?: ReactNode
}) {
  const dot = state.kind === 'open' ? 'open' : state.kind
  const status = state.kind === 'connecting' ? 'Connecting…' : state.kind === 'open' ? title : state.failed ? "Couldn't open" : 'Ended'
  return (
    <div className="nk-storage">
      <div className="nk-storage-status">
        <span className="nk-storage-dot" data-state={dot} />
        <span className="nk-storage-status-text">{status}</span>
        <span className="nk-storage-target">{host.name}</span>
        <div className="nk-storage-actions">
          {state.kind === 'open' ? actions : null}
          {state.kind === 'ended' && !state.failed ? (
            <span className="nk-storage-muted">{state.message}</span>
          ) : null}
          {state.kind === 'ended' && !state.failed ? (
            <button type="button" className="nk-storage-button" onClick={reconnect}>
              Reconnect
            </button>
          ) : null}
        </div>
      </div>
      <div className="nk-storage-body">
        {state.kind === 'connecting' ? (
          <div className="nk-storage-overlay">
            <div className="nk-storage-card nk-storage-card-row" role="status">
              <span className="nk-storage-spinner" aria-hidden="true" />
              <span>Opening {title.toLowerCase()} on {host.name}…</span>
            </div>
          </div>
        ) : null}
        {state.kind === 'ended' && state.failed ? (
          <div className="nk-storage-overlay">
            <div className="nk-storage-card" data-error="" role="alert">
              <h3>{title} couldn't be opened</h3>
              <p>{state.message}</p>
              <div className="nk-storage-card-actions">
                <button type="button" className="nk-storage-button" data-primary="" onClick={reconnect}>
                  Try again
                </button>
              </div>
            </div>
          </div>
        ) : null}
        {state.kind === 'open' ? children : null}
      </div>
    </div>
  )
}

/** A notice above a tool: what happened, or what went wrong. */
export function Notice({ text, error, onClose, children }: { text: string; error?: boolean; onClose?: () => void; children?: ReactNode }) {
  return (
    <div className="nk-storage-notice" data-error={error ? '' : undefined} role={error ? 'alert' : 'status'}>
      <span>{text}</span>
      {children}
      {onClose ? (
        <button type="button" className="nk-storage-button" data-ghost="" data-small="" onClick={onClose}>
          Dismiss
        </button>
      ) : null}
    </div>
  )
}
