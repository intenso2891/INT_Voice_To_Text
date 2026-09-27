import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  AudioLines, Check, Clipboard, Copy, Cpu, HelpCircle, Keyboard, Languages, Loader2, Mic,
  Settings2, Square, Trash2, VolumeX, WifiOff, Wrench,
} from "lucide-react";
import i18n, { setAppLanguage } from "./i18n";
import { backend, hotkeyLabel, type AppStateDto, type ResultPayload, type UpdatePayload, type Backend as BackendType, type ModelFile } from "./lib/api";

type Status = AppStateDto["status"];

// ---------------------------------------------------------------------------
// <Tip/> — "?" icon with hover tooltip (user requirement: hints on question marks)
// ---------------------------------------------------------------------------
function Tip({ text }: { text: string }) {
  return (
    <span className="tip-wrap">
      <HelpCircle size={14} aria-label="?" />
      <span role="tooltip" className="tip-bubble">{text}</span>
    </span>
  );
}

// ---------------------------------------------------------------------------
// ModelCatalog — choose and download Whisper model manually
// ---------------------------------------------------------------------------
interface ModelCatalogItem { id: string; name: string; sizeMB: number; desc: string; installed: boolean }

function fmtBytes(n: number): string {
  if (n > 1073741824) return (n / 1073741824).toFixed(1) + " ГБ";
  if (n > 1048576) return (n / 1048576).toFixed(0) + " МБ";
  return (n / 1024).toFixed(0) + " КБ";
}

function ModelCatalog({ backend }: { backend: BackendType }) {
  const { t } = useTranslation();
  const [models, setModels] = useState<ModelCatalogItem[] | null>(null);
  const [open, setOpen] = useState(false);
  const [downloading, setDownloading] = useState<string | null>(null);
  const [deleting, setDeleting] = useState(false);
  const [progress, setProgress] = useState({ percent: 0, bytes: 0, totalBytes: 0 });

  // Poll /api/state for model download progress while downloading
  useEffect(() => {
    if (!downloading) return;
    const iv = setInterval(async () => {
      try {
        const res = await fetch("/api/state");
        const st = await res.json();
        if (st.modelProgress) {
          setProgress({ percent: st.modelProgress.percent, bytes: st.modelProgress.bytes, totalBytes: st.modelProgress.totalBytes });
          if (st.modelProgress.completed) {
            setDownloading(null);
            fetch("/api/models/catalog").then(r => r.json()).then((d: { models: ModelCatalogItem[] }) => setModels(d.models)).catch(() => {});
          }
        }
      } catch {}
    }, 1000);
    return () => clearInterval(iv);
  }, [downloading]);

  const load = async () => {
    if (models !== null) { setOpen(!open); return; }
    try {
      const res = await fetch("/api/models/catalog");
      const data = await res.json() as { models: ModelCatalogItem[] };
      setModels(data.models);
      setOpen(true);
    } catch { setModels([]); setOpen(true); }
  };

  const download = (id: string) => {
    setDownloading(id);
    setProgress({ percent: 0, bytes: 0, totalBytes: 0 });
    fetch("/api/models/download", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ modelId: id }),
    }).catch(() => setDownloading(null));
  };

  const deleteAll = async () => {
    if (!confirm(t("modelDeleteConfirm"))) return;
    setDeleting(true);
    try {
      await fetch("/api/models/delete", { method: "POST" });
      const res = await fetch("/api/models/catalog");
      const data = await res.json() as { models: ModelCatalogItem[] };
      setModels(data.models);
    } catch {}
    setDeleting(false);
  };

  return (
    <div className="model-files-wrap">
      <button className="retry-btn" onClick={load} style={{ marginTop: 4 }}>
        <Loader2 size={13} className={downloading ? "spin" : ""} /> {t("modelCatalogTitle")}
      </button>
      {open && (
        <div className="model-files-list">
          {models === null && <span className="runtime-dll">…</span>}
          {models && models.map(m => (
            <div key={m.id} className="model-catalog-item">
              <div className="model-catalog-info">
                <span className={"model-catalog-name" + (m.installed ? " installed" : "")}>
                  {m.installed ? "✓ " : ""}{m.name} <small>({m.sizeMB > 1024 ? `${(m.sizeMB / 1024).toFixed(1)} ГБ` : `${m.sizeMB} МБ`})</small>
                </span>
                <small className="model-catalog-desc">{m.desc}</small>
              </div>
              {!m.installed && downloading !== m.id && (
                <button className="retry-btn" style={{margin: 0, fontSize: 11}} disabled={downloading !== null}
                  onClick={() => download(m.id)}>
                  {t("modelDownload")}
                </button>
              )}
              {downloading === m.id && (
                <div className="model-dl-progress">
                  <div className="model-progress-track" style={{width: 100}}>
                    <div className="model-progress-fill" style={{ width: `${Math.min(progress.percent, 99)}%` }} />
                  </div>
                  <small>{progress.percent}% · {fmtBytes(progress.bytes)} / {fmtBytes(progress.totalBytes)}</small>
                </div>
              )}
            </div>
          ))}
          <button className="retry-btn danger" style={{marginTop: 6}} disabled={deleting || downloading !== null}
            onClick={deleteAll}>
            <Trash2 size={13} /> {deleting ? "…" : t("modelDeleteAll")}
          </button>
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// ModelFilesList — lazy-loaded list of model files in the models/ directory
// ---------------------------------------------------------------------------
function ModelFilesList({ backend }: { backend: BackendType }) {
  const { t } = useTranslation();
  const [files, setFiles] = useState<ModelFile[] | null>(null);
  const [open, setOpen] = useState(false);

  const load = async () => {
    if (files !== null) { setOpen(!open); return; }
    try {
      const list = await backend.fetchModelList();
      setFiles(list.files);
      setOpen(true);
    } catch { setFiles([]); setOpen(true); }
  };

  return (
    <div className="model-files-wrap">
      <button className="retry-btn" onClick={load} style={{ marginTop: 4 }}>
        {t("modelFilesTitle")}
      </button>
      {open && (
        <div className="model-files-list">
          {files === null && <span className="runtime-dll">…</span>}
          {files && files.length === 0 && <span className="runtime-dll missing">{t("modelFilesEmpty")}</span>}
          {files && files.map(f => (
            <span key={f.name + f.location} className={"runtime-dll" + (f.valid ? " ok" : " missing")}>
              {f.valid ? "✓" : "✗"} {f.name} <small>({f.sizeMB} МБ)</small>
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Animated recording indicator: mic + pulsing ring + live equalizer bars
// ---------------------------------------------------------------------------
function RecordingIndicator({ seconds, level }: { seconds: number; level: number }) {
  const { t } = useTranslation();
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const levelRef = useRef(level);
  useEffect(() => { levelRef.current = level; }, [level]);

  // Scrolling "oscilloscope": flat line in silence, peaks while speaking.
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext("2d");
    if (!ctx) return;
    const barCount = 72;
    const amps = new Float32Array(barCount);
    let written = 0;
    const id = window.setInterval(() => {
      const lv = levelRef.current;
      amps[written % barCount] = lv;
      written++;
      const W = canvas.width, H = canvas.height;
      ctx.clearRect(0, 0, W, H);
      ctx.fillStyle = "rgba(94,231,209,.12)";
      ctx.fillRect(0, Math.floor(H / 2) - 1, W, 2);
      for (let i = 0; i < barCount; i++) {
        const idx = (written + i + barCount) % barCount;
        const a = amps[idx] ?? 0;
        const h = Math.max(1, a * H * 0.92);
        const x = i * (W / barCount);
        const y = a > 0.005 ? (H - h) / 2 : H / 2 - 1;
        ctx.fillStyle = a > 0.005
          ? `rgba(${94 + a * 140},${231 - a * 90},${209 - a * 60},${0.25 + a * 0.75})`
          : "rgba(94,231,209,.28)";
        ctx.fillRect(x, y, Math.max(1, W / barCount - 1.5), a > 0.005 ? h : 2);
      }
    }, 50);
    return () => window.clearInterval(id);
  }, []);

  return (
    <div className="rec-block">
      <div className="mic-ring">
        <div className="mic-ring pulse" />
        <Mic size={44} strokeWidth={1.8} />
      </div>
      <div className="meter">
        <div className="meter-track"><div className="meter-fill" style={{ height: `${Math.round(Math.max(2, level * 100))}%` }} /></div>
        <canvas ref={canvasRef} width={300} height={72} className="osc" />
      </div>
      <div className="rec-meta">
        <span className="rec-dot" /> {t("recording")}
        <b>{Math.floor(seconds / 60)}:{String(Math.floor(seconds % 60)).padStart(2, "0")}</b>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Hotkey capture field: click → press combo → saved immediately
// ---------------------------------------------------------------------------
function HotkeyField({
  initial, onSaved, onError,
}: {
  initial: AppStateDto["hotkey"];
  onSaved: (value: { modifiers: string[]; key: string }) => void;
  onError: () => void;
}) {
  const { t } = useTranslation();
  const [listening, setListening] = useState(false);
  const [pressedMods, setPressedMods] = useState<string[]>(initial.modifiers);
  const [value, setValue] = useState({ modifiers: initial.modifiers, key: initial.key });

  useEffect(() => {
    if (!listening) return;
    const handler = (e: KeyboardEvent) => {
      e.preventDefault();
      e.stopPropagation();

      // Browser/Chromium reports the Windows key as Meta (sometimes OS).
      const mods: string[] = [];
      if (e.ctrlKey || e.getModifierState("Control")) mods.push("ctrl");
      if (e.shiftKey || e.getModifierState("Shift")) mods.push("shift");
      if (e.altKey || e.getModifierState("Alt")) mods.push("alt");
      if (e.metaKey || e.getModifierState("Meta") || e.key === "Meta" || e.key === "OS") mods.push("win");
      setPressedMods(mods);

      // Escape cancels capture; modifier-only keydowns must NOT cancel it.
      if (e.key === "Escape") { setListening(false); setPressedMods(value.modifiers); return; }
      if (["Control", "Shift", "Alt", "Meta", "OS", "Win", "Windows"].includes(e.key)) return;

      // Use physical code for letters/digits so RU keyboard layout cannot turn A into Cyrillic "ф".
      let key = /^Key[A-Z]$/.test(e.code) ? e.code.slice(3).toLowerCase()
        : /^Digit[0-9]$/.test(e.code) ? e.code.slice(5)
        : e.key;
      if (key === " ") key = "space";
      else if (key === "Enter") key = "enter";
      else if (key === "Tab") key = "tab";
      else if (key === "Backspace") key = "backspace";
      else if (key === "Delete") key = "del";
      else if (key === "Insert") key = "insert";
      else if (/^F\d{1,2}$/.test(key)) { /* F-keys as-is */ }
      else key = key.length === 1 ? key.toLowerCase() : key;

      const next = { modifiers: mods, key };
      setValue(next);
      setListening(false);
      backend.saveSettings({ hotkey: next })
        .then(() => onSaved(next))
        .catch(() => onError());
    };

    window.addEventListener("keydown", handler, true);
    return () => window.removeEventListener("keydown", handler, true);
  }, [listening, onSaved, onError, value.modifiers]);

  return (
    <div className="field-row">
      <span className="row-label"><Keyboard size={15} /> {t("hotkeyTitle")}</span>
      <Tip text={t("hotkeyHint")} />
      <button
        type="button"
        className={"hotkey-capture" + (listening ? " listening" : "")}
        onClick={() => setListening(true)}
      >
        {listening ? (
          <span className="capture-hint">
            {pressedMods.length ? <b>{hotkeyLabel(pressedMods, "")}…</b> : t("pressKeysHere")}
          </span>
        ) : hotkeyLabel(value.modifiers, value.key)}
      </button>
    </div>
  );
}

function MiniOsc({ width, height, level }: { width: number; height: number; level: number }) {
  const ref = useRef<HTMLCanvasElement>(null);
  const lv = useRef(level);
  lv.current = level;
  useEffect(() => {
    const can = ref.current;
    if (!can) return;
    const ctx = can.getContext("2d");
    if (!ctx) return;
    const store = new Float32Array(48);
    let pos = 0;
    const id = setInterval(() => {
      // Exaggerate quiet speech visually; recording sensitivity itself is unchanged.
      const raw = Math.max(0, Math.min(1, lv.current));
      const boosted = raw < 0.012 ? 0 : Math.min(1, Math.pow((raw - 0.012) / 0.988, 0.38) * 1.12);
      const slot = pos % 48;
      store[slot] = Math.max(boosted, store[slot] * 0.72); // short peak hold / smooth decay
      pos++;
      ctx.clearRect(0, 0, width, height);
      for (let i = 0; i < 48; i++) {
        const a = store[(pos + i) % 48] || 0;
        const barW = width / 48;
        const hh = a > 0.005 ? Math.max(3, a * height * 0.96) : 2;
        ctx.fillStyle = a > 0.005 ? `rgba(94,231,209,${0.38 + a * 0.62})` : "rgba(94,231,209,.28)";
        ctx.fillRect(i * barW, a > 0.005 ? (height - hh) / 2 : height / 2 - 1, Math.max(1, barW - 1), hh);
      }
    }, 50);
    return () => clearInterval(id);
  }, [width, height]);
  return <canvas ref={ref} width={width} height={height} className="overlay-wave" />;
}

function HistoryPanel({ items, onCopy, onClear }: { items: string[]; onCopy: (text: string) => void; onClear: () => void }) {
  const { t } = useTranslation();
  return (
    <section className="history-card">
      <div className="history-head">
        <span><Clipboard size={15} /> {t("historyTitle")}</span>
        <button className="history-clear" onClick={onClear}><Trash2 size={13} /> {t("clearHistory")}</button>
      </div>
      <div className="history-list">
        {items.length ? items.map((text, i) => (
          <div className="history-item" key={`${i}-${text.slice(0, 12)}`}>
            <span>{text}</span>
            <button className="history-copy" onClick={() => onCopy(text)} title={t("copyText")}><Copy size={14} /></button>
          </div>
        )) : (
          <div className="history-empty">{t("historyEmpty")}</div>
        )}
      </div>
    </section>
  );
}

// ---------------------------------------------------------------------------
// Main app
// ---------------------------------------------------------------------------
export default function App() {
  const { t } = useTranslation();
  const [status, setStatus] = useState<Status>("idle");
  const [result, setResult] = useState<ResultPayload | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [info, setInfo] = useState<AppStateDto | null>(null);
  const [runtimeRepairReport, setRuntimeRepairReport] = useState<string | null>(null);
  const [showSettings, setShowSettings] = useState(false);
  const [copied, setCopied] = useState(false);
  const [seconds, setSeconds] = useState(0);
  const [level, setLevel] = useState(0);
  const [overlay, setOverlay] = useState<"off" | "recording" | "transcribing" | "ready">("off");
  const [compactView, setCompactView] = useState(window.innerHeight < 260);
  const [overlayText, setOverlayText] = useState<string | null>(null);
  const [availableUpdate, setAvailableUpdate] = useState<UpdatePayload | null>(null);
  const [updating, setUpdating] = useState(false);
  const [updateProgress, setUpdateProgress] = useState({ bytes: 0, totalBytes: 0, percent: 0, phase: "" });
  const [modelProgress, setModelProgress] = useState({ bytes: 0, totalBytes: 3095033483, percent: 0, completed: false });
  const [history, setHistory] = useState<string[]>(() => {
    try { return JSON.parse(localStorage.getItem("intvtt_history") ?? "[]") as string[]; } catch { return []; }
  });


  // Recording timer (purely visual)
  useEffect(() => {
    if (status !== "recording") return;
    setSeconds(0);
    const id = window.setInterval(() => setSeconds((s) => s + 1), 1000);
    return () => window.clearInterval(id);
  }, [status]);

  // Wire backend
  useEffect(() => {
    let disposed = false;

    (async () => {
      try {
        const st = await backend.getState();
        if (!disposed) { setInfo(st); setModelProgress(st.modelProgress); }
        setStatus(st.status === "idle" ? "idle" : st.status);
        if (st.status === "recording") setOverlay("recording");
        else if (st.status === "transcribing") setOverlay("transcribing");

        backend.onState = (s) => {
          setStatus(s);
          if (s === "recording") { setOverlay("recording"); setOverlayText(null); }
          else if (s === "transcribing") { setOverlay("transcribing"); }
          else if (s === "idle") { /* result handler sets ready; timer clears it */ }
          if (s !== "recording") setLevel(0);
          if (s !== "transcribing") setError(null);
        };
        backend.onLevel = (v) => setLevel(Math.max(0, Math.min(1, v)));
        backend.onDebug = (line) => setDebugLines((old) => [...old, line].slice(-300));
        backend.onResult = (r) => {
          setResult(r); setError(null);
          if (r.text.trim()) {
            setOverlayText(r.text.trim());
            setOverlay("ready");
            window.setTimeout(() => setOverlay("off"), 5000);
            setHistory((old) => { const next = [r.text.trim(), ...old.filter((x) => x !== r.text.trim())].slice(0, 30); try { localStorage.setItem("intvtt_history", JSON.stringify(next)); } catch {} return next; });
          }
          setTimeout(() => setCopied(false), 1500);
        };
        backend.onError = (msg) => {
          const friendly = msg.startsWith("TOO_SHORT") ? t("tooShort")
            : msg.startsWith("MODEL_DOWNLOADING") ? t("modelBusy")
            : msg.startsWith("MODEL_NOT_READY") ? t("modelNotReady")
            : msg.startsWith("MICROPHONE_ERROR") || msg.includes("NoDefault") ? t("errorMic")
            : msg.replace(/^[A-Z_]+:\s*/, "");
          setError(friendly);
        };
        backend.onUpdate = (u) => setAvailableUpdate(u);
        backend.onModelProgress = (p) => {
          setModelProgress(p);
          // After download completes, re-fetch /api/state to update modelReady
          if (p.completed) {
            setTimeout(async () => {
              try {
                const res = await fetch("/api/state");
                const st = await res.json() as AppStateDto;
                setInfo(prev => prev ? { ...prev, modelReady: st.modelReady, modelDownloading: st.modelDownloading, modelLastError: st.modelLastError } : prev);
              } catch {}
            }, 500);
          }
        };
        backend.onUpdateProgress = (p) => setUpdateProgress(p);

        await backend.connect();
        try {
          const update = await backend.checkUpdate();
          if (update && !disposed) setAvailableUpdate(update);
        } catch { /* offline update check is optional */ }
      } catch {
        // server still booting — retry loop until it answers
        const poll = window.setInterval(async () => {
          try {
            const st = await backend.getState();
            if (!disposed) setInfo(st);
            window.clearInterval(poll);
            await backend.connect();
          } catch { /* keep polling */ }
        }, 1000);
      }
    })();
    return () => { disposed = true; };
  }, [t]);

  const formatBytes = (n: number) => {
    if (!n) return "0 МБ";
    const mb = n / (1024 * 1024);
    return mb >= 1024 ? `${(mb / 1024).toFixed(2)} ГБ` : `${Math.round(mb)} МБ`;
  };

  const toggleLanguage = useCallback(() => {
    const next = i18n.language === "ru" ? "en" : "ru";
    setAppLanguage(next);
    backend.saveSettings({ language: next }).catch(() => { /* offline-safe */ });
  }, []);

  const copyResult = useCallback(async () => {
    if (!result) return;
    try { await navigator.clipboard.writeText(result.text); } catch { /* webview permission */ }
    setCopied(true);
  }, [result]);

  const copyHistory = useCallback(async (text: string) => {
    try { await navigator.clipboard.writeText(text); } catch { /* webview permission */ }
  }, []);

  const clearHistory = useCallback(() => {
    setHistory([]);
    try { localStorage.removeItem("intvtt_history"); } catch { /* ignore */ }
  }, []);

  const togglePaste = useCallback((v: boolean) => {
    backend.saveSettings({ pasteResult: v }).catch(() => undefined);
    setInfo((p) => (p ? { ...p, pasteResult: v } : p));
  }, []);

  const setMicrophoneDevice = useCallback((index: number) => {
    backend.saveSettings({ microphoneDevice: index }).catch(() => undefined);
    setInfo((p) => (p ? { ...p, microphoneDevice: index } : p));
  }, []);

  const setMicrophoneSensitivity = useCallback((v: number) => {
    backend.saveSettings({ microphoneSensitivity: v }).catch(() => undefined);
    setInfo((p) => (p ? { ...p, microphoneSensitivity: v } : p));
  }, []);

  const [debugLines, setDebugLines] = useState<string[]>([]);

  const toggleDebug = useCallback((v: boolean) => {
    backend.saveSettings({ debugMode: v }).catch(() => undefined);
    setInfo((p) => (p ? { ...p, debugMode: v } : p));
  }, []);

  const toggleStartup = useCallback((v: boolean) => {
    backend.saveSettings({ startWithWindows: v }).catch(() => undefined);
    setInfo((p) => (p ? { ...p, startWithWindows: v } : p));
  }, []);

  const toggleTrayClose = useCallback((v: boolean) => {
    backend.saveSettings({ minimizeToTrayOnClose: v }).catch(() => undefined);
    setInfo((p) => (p ? { ...p, minimizeToTrayOnClose: v } : p));
  }, []);

  const setComputeMode = useCallback((v: string) => {
    backend.saveSettings({ computeMode: v }).catch(() => undefined);
    setInfo((p) => (p ? { ...p, computeMode: v } : p));
  }, []);

  const refreshDebugLog = useCallback(async () => {
    try {
      const res = await fetch("/api/debug/log");
      if (res.ok) setDebugLines((await res.text()).split(/\r?\n/).filter(Boolean));
    } catch { /* offline-safe */ }
  }, []);

  useEffect(() => { if (info?.debugMode) refreshDebugLog(); }, [info?.debugMode, refreshDebugLog]);
  useEffect(() => { const onResize = () => setCompactView(window.innerHeight < 260); window.addEventListener("resize", onResize); return () => window.removeEventListener("resize", onResize); }, []);

  if (compactView) {
    return (
      <div className="app overlay-app">
        <div className={`overlay-card overlay-bar ${overlay}`}>
          <span className="overlay-light" />
          <div className="overlay-copy">
            <b>{overlay === "recording" ? t("overlayRecording") : overlay === "transcribing" ? t("overlayTranscribing") : overlay === "ready" ? t("overlayReady") : t("ready")}</b>
            {overlay === "recording"
              ? <small>{t("overlaySpeak")}</small>
              : overlay === "transcribing"
                ? <small>{t("overlayTranscribingLabel")}</small>
                : overlay === "ready"
                  ? <small>{t("overlayPaste")}</small> : null}
            {overlay === "ready" && overlayText && <span className="overlay-text">{overlayText}</span>}
          </div>
          {overlay === "recording" ? <MiniOsc width={150} height={54} level={level} /> : overlay === "transcribing" ? <Loader2 size={18} className="spin" /> : overlay === "ready" ? <Check size={22} /> : null}
        </div>
      </div>
    );
  }

  return (
    <div className="app">
      {/* ---------- header ---------- */}
      <header className="hdr">
        <span className="brand"><AudioLines size={18} /> {t("appTitle")}</span>
        <span className="hdr-actions">
          {info && (
            <button className="icon-btn" onClick={toggleLanguage} title={t("languageTitle")}>
              <Languages size={15} /> {i18n.language === "ru" ? "RU" : "EN"}
            </button>
          )}
          <button
            className={"icon-btn" + (showSettings ? " active" : "")}
            onClick={() => setShowSettings((v) => !v)}
            title={t("settings")}
          >
            <Settings2 size={16} />
          </button>
        </span>
      </header>

      {/* ---------- update banner ---------- */}
      {availableUpdate && (
        <section className="update-card">
          {!updating ? (
            <>
              <span>{t("updateAvailable")} <b>v{availableUpdate.version}</b> ({t("currentVersion")} {info?.version})</span>
              <button className="update-btn" onClick={async () => {
                setUpdating(true);
                try { await backend.installUpdate(availableUpdate); } catch { setUpdating(false); setError(t("updateFailed")); }
              }}>{t("updateNow")}</button>
              <button className="update-dismiss" onClick={() => setAvailableUpdate(null)}>✕</button>
            </>
          ) : (
            <div className="update-progress-wrap">
              <span>{t("updateInstalling")} <b>v{availableUpdate.version}</b></span>
              <div className="model-progress-track"><div className="model-progress-fill" style={{ width: `${updateProgress.percent || 0}%` }} /></div>
              <span className="model-progress-label">{updateProgress.percent || 0}% · {formatBytes(updateProgress.bytes)} / {updateProgress.totalBytes > 0 ? formatBytes(updateProgress.totalBytes) : "???"}</span>
            </div>
          )}
        </section>
      )}

      {/* ---------- status card ---------- */}
      <section className={"status-card " + status}>
        {status === "recording" && (
          <RecordingIndicator seconds={seconds} level={level} />
        )}

        {status !== "recording" && (
          <div className="idle-block">
            <span className={"state-dot" + (status === "transcribing" ? " busy" : "")} />
            <div>
              <b>{t(status === "transcribing" ? "transcribing" : "ready")}</b>
              {info && (
                <small>{t("pressHint")} · {hotkeyLabel(info.hotkey.modifiers, info.hotkey.key)}</small>
              )}
            </div>
          </div>
        )}

        <button className="main-btn" onClick={() => backend.toggle().catch(() => undefined)}>
          {status === "recording" ? (
            <><Square size={16} /> {t("stopRecording")}</>
          ) : status === "transcribing" ? (
            <><Loader2 size={16} className="spin" /> {t("transcribing")}</>
          ) : (
            <><Mic size={16} /> {t("startRecording")}</>
          )}
        </button>
      </section>

      {/* ---------- result ---------- */}
      {result && (
        <section className="result-card">
          <div className="result-head">
            <span>{t("yourResult")}</span>
            {(result.pasted ? (
              <span className="ok-badge"><Check size={13} /> {t("insertedAtCursor")}</span>
            ) : (
              <button className="copy-btn" onClick={copied ? undefined : copyResult}>
                {copied ? <><Check size={13} /> {t("copiedToClipboard")}</> : <><Copy size={13} /> {t("copyText")}</>}
              </button>
            ))}
          </div>
          <p className="result-text">{result.text || t("noSpeechDetected")}</p>
        </section>
      )}

      <HistoryPanel
        items={history}
        onCopy={copyHistory}
        onClear={clearHistory}
      />

      {/* ---------- errors ---------- */}
      {error && (
        <section className="err-card">
          <VolumeX size={15} /> {error}
        </section>
      )}

      {/* ---------- settings panel ---------- */}
      {showSettings && info && (
        <section className="settings-panel">
          <h3>{t("settings")}</h3>

          <HotkeyField
            initial={info.hotkey}
            onSaved={(next) => setInfo((p) => (p ? { ...p, hotkey: next } : p))}
            onError={() => setError(t("saveHotkeyError"))}
          />

          <div className="field-row">
            <span className="row-label"><Languages size={15} /> {t("languageTitle")}</span>
            <select
              value={info.language}
              onChange={(e) => toggleLanguage()}
            >
              <option value="ru">{t("russian")}</option>
              <option value="en">{t("english")}</option>
            </select>
          </div>

          <label className={"field-row check" + (info.pasteResult ? " on" : "")}>
            <input type="checkbox" checked={info.pasteResult} onChange={(e) => togglePaste(e.target.checked)} />
            <span>{t("pasteTitle")}</span>
            <Tip text={t("pasteHint")} />
          </label>

          <div className="audio-settings">
            <div className="field-row">
              <span className="row-label"><Mic size={15} /> {t("microphoneTitle")}</span>
              <select value={info.microphoneDevice} onChange={(e) => setMicrophoneDevice(Number(e.target.value))}>
                {(info.microphones?.length ? info.microphones : [{ index: 0, name: t("defaultMicrophone") }]).map((m) => <option key={m.index} value={m.index}>{m.name}</option>)}
              </select>
            </div>
            <div className="sensitivity-row">
              <span className="row-label">{t("sensitivityTitle")}</span>
              <input type="range" min="0.25" max="10" step="0.05" value={info.microphoneSensitivity} onChange={(e) => setMicrophoneSensitivity(Number(e.target.value))} />
              <b>{info.microphoneSensitivity.toFixed(2)}×</b>
              <Tip text={t("sensitivityHint")} />
            </div>
          </div>

          <div className="compute-settings">
            <div className="field-row">
              <span className="row-label"><Cpu size={15} /> {t("computeTitle")}</span><Tip text={t("computeHint")} />
              <select value={info.computeMode} onChange={(e) => setComputeMode(e.target.value)}>
                <option value="cpu">CPU</option>
                <option value="gpu">GPU</option>
                <option value="hybrid">CPU + GPU</option>
              </select>
            </div>
          </div>

          <label className="field-row check debug-toggle">
            <input type="checkbox" checked={info.startWithWindows} onChange={(e) => toggleStartup(e.target.checked)} />
            <span>{t("startupTitle")}</span><Tip text={t("startupHint")} />
          </label>
          <label className="field-row check debug-toggle">
            <input type="checkbox" checked={info.minimizeToTrayOnClose} onChange={(e) => toggleTrayClose(e.target.checked)} />
            <span>{t("trayCloseTitle")}</span><Tip text={t("trayCloseHint")} />
          </label>

          <label className="field-row check debug-toggle">
            <input type="checkbox" checked={info.debugMode} onChange={(e) => toggleDebug(e.target.checked)} />
            <span>{t("debugTitle")}</span><Tip text={t("debugHint")} />
          </label>

          <div className="model-row">
            <span className="row-label"><WifiOff size={15} /> {t("modelTitle")}</span>
            <Tip text={t("modelHint")} />
            <small className={"model-status" + (info.modelReady ? " ready" : "")}>
              {info.modelReady ? t("modelReady") : info.modelDownloading ? t("modelDownloading") : modelProgress.completed ? t("modelCompleted") : t("modelNotReady")}
            </small>
            {!info.modelReady && (
              <div className="model-progress-wrap">
                <div className="model-progress-track"><div className="model-progress-fill" style={{ width: `${modelProgress.completed ? 100 : Math.min(modelProgress.percent, 99)}%` }} /></div>
                <span className="model-progress-label">{modelProgress.completed ? "100" : Math.min(modelProgress.percent, 99)}% · {formatBytes(modelProgress.bytes)} / {formatBytes(modelProgress.totalBytes)}</span>
              </div>
            )}
            {!info.modelReady && modelProgress.completed && <small className="model-status">{t("modelFinalizing")}</small>}
            {info.modelLastError && <small className="runtime-dll missing">⚠ {info.modelLastError}</small>}
            {!info.modelReady && !info.modelDownloading && (
              <button className="retry-btn" onClick={async () => {
                try {
                  await backend.retryModelDownload();
                } catch { setError(t("updateFailed")); }
              }} disabled={info.modelDownloading}>
                <Loader2 size={13} className={info.modelDownloading ? "spin" : ""} /> {t("modelRetry")}
              </button>
            )}
            {/* Model files list */}
            <ModelCatalog backend={backend} />
            <ModelFilesList backend={backend} />
          </div>

          {/* ---------- Whisper Runtime diagnostics ---------- */}
          {info?.runtime && (
            <div className="model-row runtime-row">
              <span className="row-label"><Cpu size={15} /> {t("runtimeTitle")}</span>
              <small className={"model-status" + (info.runtime.dlls.every(d => d.exists) ? " ready" : "")}>
                {info.runtime.dlls.every(d => d.exists) ? t("runtimeOk") : t("runtimeMissing")}
              </small>
              <div className="runtime-dlls">
                {info.runtime.dlls.map(d => (
                  <span key={d.name} className={"runtime-dll" + (d.exists ? " ok" : " missing")}>
                    {d.exists ? "✓" : "✗"} {d.name}
                    {d.exists && <small> ({d.sizeKB > 1024 ? `${(d.sizeKB / 1024).toFixed(0)} МБ` : `${d.sizeKB} КБ`})</small>}
                  </span>
                ))}
              </div>
              <div className="runtime-dlls" style={{marginTop: 4}}>
                <span className={"runtime-dll" + (info.runtime.runtimesDirExists ? " ok" : " missing")}>
                  {info.runtime.runtimesDirExists ? "✓" : "✗"} runtimes/win-x64/native/
                </span>
              </div>
              {/* Repair button — deploys DLLs to the correct search paths */}
              <button className="retry-btn" style={{marginTop: 6}} onClick={async () => {
                try {
                  const r = await backend.repairRuntime();
                  setRuntimeRepairReport(r.report);
                  setInfo(prev => prev ? { ...prev } : prev);
                } catch (e) {
                  setRuntimeRepairReport("Error: " + (e as Error).message);
                }
              }}>
                <Wrench size={13} /> {t("runtimeRepair")}
              </button>
              {runtimeRepairReport && <pre className="runtime-report">{runtimeRepairReport}</pre>}
            </div>
          )}

          <footer className="ver">
            {t("versionLabel")} <b>{info.version}</b> · INT VoiceToText
            <small className="ver-credit">Created by INTENSO.Dev</small>
          </footer>
        </section>
      )}

      {info?.debugMode && showSettings && (
        <section className="debug-card">
          <div className="debug-head">
            <span>{t("debugConsole")}</span>
            <div className="debug-actions">
              <button onClick={refreshDebugLog}>{t("refreshDebug")}</button>
              <button onClick={async () => {
                try {
                  await fetch("/api/debug/clear", { method: "POST" });
                  setDebugLines([]);
                  refreshDebugLog();
                } catch {}
              }}>{t("clearDebug")}</button>
            </div>
          </div>
          <pre>{debugLines.length ? debugLines.join("\n") : t("debugEmpty")}</pre>
        </section>
      )}

      {!showSettings && info && (
        <footer className="ver ver-foot">{t("versionLabel")} {info.version}<span className="ver-credit">Created by INTENSO.Dev</span></footer>
      )}
    </div>
  );
}
