import { useCallback, useEffect, useState, type FormEvent } from 'react'
import type { SessionViewProps } from '@nkeeper/sdk'
import type { ToolClient } from './client'
import {
  CONFIRM,
  iscsiRows,
  MULTIPATH_ALIAS,
  multipathTargets,
  type DrbdResource,
  type StorageActionRequest,
  type StorageActionResult,
  type StorageStatus,
} from './storage'
import { useTool } from './tool'
import { Notice, ToolFrame } from './ToolFrame'

/** The Storage tool's tab (docs/architecture.md §7.27), ported from 1.x's StorageView. */
export function StorageView({ connect, host }: SessionViewProps) {
  const { state, reconnect } = useTool(connect, 'Storage')
  return (
    <ToolFrame title="Storage" host={host} state={state} reconnect={reconnect}>
      {state.kind === 'open' ? <Storage client={state.client} hostName={host.name} /> : null}
    </ToolFrame>
  )
}

/** What an action acts on, for the confirmation and messages. */
function subject(request: StorageActionRequest): string {
  return request.target ? `${request.target} (${request.portal})` : (request.name ?? request.portal ?? 'this host')
}

/**
 * A Linux host's shared storage: iSCSI targets and sessions, multipath maps and DRBD resources, with their everyday
 * actions. Actions that take storage away ask first.
 */
function Storage({ client, hostName }: { client: ToolClient; hostName: string }) {
  const [storage, setStorage] = useState<StorageStatus | null>(null)
  const [refreshing, setRefreshing] = useState(true)
  const [busy, setBusy] = useState<StorageActionRequest | null>(null)
  const [confirming, setConfirming] = useState<StorageActionRequest | null>(null)
  const [notice, setNotice] = useState<{ text: string; error?: boolean } | null>(null)
  const [portal, setPortal] = useState('')
  const [renaming, setRenaming] = useState<{ map: string; newName: string } | null>(null)

  const refresh = useCallback(async () => {
    setRefreshing(true)
    try {
      setStorage((await client.request<{ storage: StorageStatus }>('status')).storage)
    } catch (e) {
      setNotice({ text: e instanceof Error ? e.message : 'The host’s storage could not be read.', error: true })
    } finally {
      setRefreshing(false)
    }
  }, [client])

  useEffect(() => {
    // The first load, once the effect has run: it sets state as it goes.
    queueMicrotask(() => void refresh())
  }, [refresh])

  /** Runs an action (asking first for those in CONFIRM), then shows the host's storage afterwards. */
  async function act(request: StorageActionRequest, confirmed = false) {
    if (!confirmed && CONFIRM[request.action]) {
      setConfirming(request)
      return
    }
    setConfirming(null)
    setBusy(request)
    setNotice(null)
    try {
      const result = await client.request<StorageActionResult>('action', { ...request })
      setStorage(result.storage)
      if (request.action === 'iscsi.discover') {
        setPortal('')
      }
      setNotice({ text: result.message || 'Done.' })
    } catch (e) {
      setNotice({ text: e instanceof Error ? e.message : `Couldn’t change ${subject(request)}.`, error: true })
    } finally {
      setBusy(null)
    }
  }

  function discover(event: FormEvent) {
    event.preventDefault()
    if (portal.trim()) {
      void act({ action: 'iscsi.discover', portal: portal.trim() })
    }
  }

  const newName = renaming?.newName.trim() ?? ''
  const renameProblem = !newName || newName === renaming?.map ? '' : MULTIPATH_ALIAS.test(newName) ? null : 'Use up to 64 letters, digits, dots, dashes or underscores, starting with a letter or digit.'

  function rename(event: FormEvent) {
    event.preventDefault()
    if (renaming && renameProblem === null) {
      setRenaming(null)
      void act({ action: 'multipath.rename', name: renaming.map, newName })
    }
  }

  const disabled = busy !== null || refreshing
  const nothing = storage && !storage.iscsi && !storage.multipath && !storage.drbd
  const confirmInfo = confirming ? CONFIRM[confirming.action] : undefined

  return (
    <>
      <div className="nk-storage-toolbar">
        <button type="button" className="nk-storage-button" onClick={() => void refresh()} disabled={disabled}>
          {refreshing ? 'Refreshing…' : 'Refresh'}
        </button>
        {busy ? <span className="nk-storage-muted">Working on {subject(busy)}…</span> : null}
      </div>
      {notice ? <Notice text={notice.text} error={notice.error} onClose={() => setNotice(null)} /> : null}
      {confirming && confirmInfo ? (
        <Notice text={`${confirmInfo.label} ${subject(confirming)} on ${hostName}? ${confirmInfo.explanation}`} error>
          <button type="button" className="nk-storage-button" data-danger="" data-small="" onClick={() => void act(confirming, true)}>
            {confirmInfo.label}
          </button>
          <button type="button" className="nk-storage-button" data-ghost="" data-small="" onClick={() => setConfirming(null)}>
            Cancel
          </button>
        </Notice>
      ) : null}
      {renaming ? (
        <form className="nk-storage-notice" onSubmit={rename} aria-label={`Rename ${renaming.map}`}>
          <label className="nk-storage-field">
            New name for {renaming.map}
            <input
              className="nk-storage-input"
              value={renaming.newName}
              onChange={(e) => setRenaming({ ...renaming, newName: e.target.value })}
              autoComplete="off"
              spellCheck={false}
              aria-invalid={renameProblem ? true : undefined}
              autoFocus
            />
          </label>
          <span>
            {renameProblem ||
              `NKeeper saves the name in this host’s multipath configuration and reloads the maps. Whatever has the map open keeps working, but /dev/mapper/${renaming.map} goes away: change anything that refers to it (fstab, LVM filters, DRBD, VM disks), and rename it on this LUN’s other hosts too if they should match.`}
          </span>
          <button type="submit" className="nk-storage-button" data-primary="" data-small="" disabled={renameProblem !== null}>
            Rename and reload
          </button>
          <button type="button" className="nk-storage-button" data-ghost="" data-small="" onClick={() => setRenaming(null)}>
            Cancel
          </button>
        </form>
      ) : null}

      {nothing ? <p className="nk-storage-muted">This host has none of iSCSI (iscsiadm), multipath or DRBD (drbdadm) installed.</p> : null}

      {storage?.iscsi ? (
        <section aria-label="iSCSI">
          <div className="nk-storage-toolbar">
            <h2 className="nk-storage-section">iSCSI</h2>
            {storage.iscsi.initiatorName ? <span className="nk-storage-muted nk-storage-mono">Initiator {storage.iscsi.initiatorName}</span> : null}
            <button type="button" className="nk-storage-button" data-small="" onClick={() => void act({ action: 'iscsi.rescan' })} disabled={disabled || storage.iscsi.sessions.length === 0}>
              Rescan sessions
            </button>
          </div>
          <form className="nk-storage-form" onSubmit={discover}>
            <label>
              Discover targets at portal
              <input className="nk-storage-input" placeholder="10.0.0.5 or 10.0.0.5:3260" value={portal} onChange={(e) => setPortal(e.target.value)} disabled={disabled} />
            </label>
            <button type="submit" className="nk-storage-button" disabled={disabled || !portal.trim()}>
              Discover
            </button>
          </form>
          {iscsiRows(storage.iscsi).length === 0 ? (
            <p className="nk-storage-muted">No targets yet. Discover them at the storage’s portal.</p>
          ) : (
            <table className="nk-storage-table">
              <thead>
                <tr>
                  <th scope="col">Target</th>
                  <th scope="col">Portal</th>
                  <th scope="col">State</th>
                  <th scope="col">Disks</th>
                  <th scope="col">
                    <span hidden>Actions</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {iscsiRows(storage.iscsi).map((row) => {
                  const request = { target: row.target, portal: row.portal }
                  const loggedIn = row.session?.state === 'LOGGED_IN'
                  return (
                    <tr key={`${row.target} ${row.portal}`}>
                      <td className="nk-storage-mono">{row.target}</td>
                      <td className="nk-storage-mono">
                        {row.portal}
                        {row.session?.connectedTo?.length ? <div className="nk-storage-muted">connected via {row.session.connectedTo.join(', ')}</div> : null}
                      </td>
                      <td>
                        <span className="nk-storage-dot" data-state={loggedIn ? 'open' : row.session ? 'ended' : undefined} />{' '}
                        {row.session ? row.session.state.replace('_', ' ').toLowerCase() : 'not logged in'}
                      </td>
                      <td>{row.session?.disks.join(', ') || '—'}</td>
                      <td>
                        <div className="nk-storage-row-actions">
                          {row.session ? (
                            <button type="button" className="nk-storage-button" data-small="" onClick={() => void act({ action: 'iscsi.logout', ...request })} disabled={disabled}>
                              Log out
                            </button>
                          ) : (
                            <button type="button" className="nk-storage-button" data-small="" onClick={() => void act({ action: 'iscsi.login', ...request })} disabled={disabled}>
                              Log in
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          )}
        </section>
      ) : null}

      {storage?.multipath ? (
        <section aria-label="Multipath">
          <div className="nk-storage-toolbar">
            <h2 className="nk-storage-section">Multipath</h2>
            <button type="button" className="nk-storage-button" data-small="" onClick={() => void act({ action: 'multipath.reload' })} disabled={disabled}>
              Reload maps
            </button>
          </div>
          {storage.multipath.length === 0 ? (
            <p className="nk-storage-muted">No multipath maps.</p>
          ) : (
            <table className="nk-storage-table">
              <thead>
                <tr>
                  <th scope="col">Map</th>
                  <th scope="col">Device</th>
                  <th scope="col">iSCSI target</th>
                  <th scope="col">Size</th>
                  <th scope="col">Paths</th>
                  <th scope="col">
                    <span hidden>Actions</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {storage.multipath.map((map) => {
                  const healthy = map.paths.filter((p) => p.dmState === 'active' && p.pathState === 'ready').length
                  const targets = multipathTargets(map, storage.iscsi)
                  return (
                    <tr key={map.name}>
                      <td className="nk-storage-mono">
                        {map.name}
                        {map.wwid !== map.name ? <div className="nk-storage-muted">{map.wwid}</div> : null}
                      </td>
                      <td>
                        {map.dmDevice}
                        {map.product ? <div className="nk-storage-muted">{map.product}</div> : null}
                      </td>
                      <td>
                        {targets.length === 0
                          ? '—'
                          : targets.map((t) => (
                              <div key={t.target} className="nk-storage-mono">
                                {t.target}
                                <div className="nk-storage-muted">via {t.portals.join(', ')}</div>
                              </div>
                            ))}
                      </td>
                      <td>{map.size ?? '—'}</td>
                      <td>
                        <span className="nk-storage-dot" data-state={healthy === map.paths.length && healthy > 0 ? 'open' : 'ended'} /> {healthy} of {map.paths.length} ready
                        {map.paths.map((p) => (
                          <div key={p.address} className="nk-storage-muted">
                            {p.device} ({p.address}) {p.dmState} · {p.pathState} · {p.onlineState}
                          </div>
                        ))}
                      </td>
                      <td>
                        <div className="nk-storage-row-actions">
                          <button type="button" className="nk-storage-button" data-small="" onClick={() => setRenaming({ map: map.name, newName: map.name })} disabled={disabled}>
                            Rename
                          </button>
                          <button type="button" className="nk-storage-button" data-small="" onClick={() => void act({ action: 'multipath.flush', name: map.name })} disabled={disabled}>
                            Flush
                          </button>
                        </div>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          )}
        </section>
      ) : null}

      {storage?.drbd ? (
        <section aria-label="DRBD">
          <h2 className="nk-storage-section">DRBD</h2>
          {storage.drbd.length === 0 ? (
            <p className="nk-storage-muted">No DRBD resources are configured on this host.</p>
          ) : (
            <table className="nk-storage-table">
              <thead>
                <tr>
                  <th scope="col">Resource</th>
                  <th scope="col">Role</th>
                  <th scope="col">Disk</th>
                  <th scope="col">Peers</th>
                  <th scope="col">
                    <span hidden>Actions</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {storage.drbd.map((resource) => (
                  <DrbdRow key={resource.name} resource={resource} disabled={disabled} onAct={(request) => void act(request)} />
                ))}
              </tbody>
            </table>
          )}
        </section>
      ) : null}
    </>
  )
}

function DrbdRow({ resource, disabled, onAct }: { resource: DrbdResource; disabled: boolean; onAct: (request: StorageActionRequest) => void }) {
  const button = (action: StorageActionRequest['action'], label: string) => (
    <button type="button" className="nk-storage-button" data-small="" onClick={() => onAct({ action, name: resource.name })} disabled={disabled}>
      {label}
    </button>
  )
  const standAlone = resource.peers.length > 0 && resource.peers.every((p) => p.connectionState === 'StandAlone')
  const healthy =
    resource.up &&
    resource.volumes.every((v) => v.diskState === 'UpToDate') &&
    resource.peers.every((p) => p.connectionState === 'Connected' && p.volumes.every((v) => v.diskState === 'UpToDate'))

  return (
    <tr>
      <td className="nk-storage-mono">
        <span className="nk-storage-dot" data-state={healthy ? 'open' : resource.up ? 'connecting' : undefined} /> {resource.name}
      </td>
      <td>{resource.up ? (resource.role ?? 'Unknown') : 'Down'}</td>
      <td>
        {resource.volumes.map((v) => (
          <div key={v.volume}>
            {resource.volumes.length > 1 ? `${v.volume}: ` : ''}
            {v.diskState ?? '—'}
            {v.minor !== null ? <span className="nk-storage-muted"> · /dev/drbd{v.minor}</span> : null}
          </div>
        ))}
      </td>
      <td>
        {resource.peers.map((peer) => (
          <div key={peer.name}>
            {peer.name}: {peer.connectionState}
            {peer.role ? ` · ${peer.role}` : ''}
            {peer.volumes.map((v) => (
              <div key={v.volume} className="nk-storage-muted">
                {[v.replicationState, v.diskState, v.percentInSync !== null && v.percentInSync < 100 ? `${v.percentInSync}% in sync` : null].filter(Boolean).join(' · ')}
              </div>
            ))}
          </div>
        ))}
      </td>
      <td>
        <div className="nk-storage-row-actions">
          {resource.up ? (
            <>
              {resource.role === 'Primary' ? button('drbd.secondary', 'Secondary') : button('drbd.primary', 'Primary')}
              {standAlone ? button('drbd.connect', 'Connect') : button('drbd.disconnect', 'Disconnect')}
              {button('drbd.adjust', 'Adjust')}
              {button('drbd.down', 'Down')}
            </>
          ) : (
            button('drbd.up', 'Up')
          )}
        </div>
      </td>
    </tr>
  )
}
