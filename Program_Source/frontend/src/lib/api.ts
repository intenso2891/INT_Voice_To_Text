// ============================================================================
// INT VoiceToText — backend client (REST + SignalR realtime)
// ============================================================================

import { HubConnection, HubConnectionBuilder } from "@microsoft/signalr";

export interface AppStateDto {
  version: string;
  status: "idle" | "recording" | "transcribing";
  hotkey: { modifiers: string[]; key: string; label?: string };
  language: string;
  pasteResult: boolean;
  microphones: { index: number; name: string }[];
  microphoneDevice: number;
  microphoneSensitivity: number;
  debugMode: boolean;
  startWithWindows: boolean;
  minimizeToTrayOnClose: boolean;
  computeMode: string; // "cpu" | "gpu" | "hybrid"
  gpuDevice: number;
  modelReady: boolean;
  modelDownloading: boolean;
  modelProgress: { bytes: number; totalBytes: number; percent: number; completed: boolean };
}

export interface ResultPayload {
  text: string;
  durationMs: number;
  pasted: boolean;
}

export interface UpdatePayload {
  version: string;
  releaseUrl: string;
  assetUrl: string;
  assetName: string;
}

export function hotkeyLabel(modifiers: string[], key: string): string {
  const names: Record<string, string> = { ctrl: "Ctrl", shift: "Shift", alt: "Alt", win: "Win" };
  const parts = modifiers.map((m) => names[m] ?? m);
  if (key) parts.push(key.toUpperCase());
  return parts.join("+");
}

export class Backend {
  private connection?: HubConnection;
  onState?: (s: AppStateDto["status"]) => void;
  onResult?: (r: ResultPayload) => void;
  onError?: (message: string) => void;
  onLevel?: (level: number) => void;
  onDebug?: (line: string) => void;
  onUpdate?: (update: UpdatePayload) => void;
  onModelProgress?: (progress: AppStateDto["modelProgress"]) => void;

  async connect(): Promise<void> {
    if (this.connection && this.connection.state === "Connected") return;
    const conn = new HubConnectionBuilder()
      .withUrl(`${window.location.origin}/hubs/voice`)
      .withAutomaticReconnect([0, 1500, 5000])
      .configureLogging(3 /* Warning */)
      .build();

    conn.on("state", (payload: { status: string }) => this.onState?.(payload.status as AppStateDto["status"]));
    conn.on("text", (payload: ResultPayload) => this.onResult?.(payload));
    conn.on("error", (payload: { message: string }) => this.onError?.(payload.message));
    conn.on("level", (payload: { level: number }) => this.onLevel?.(payload.level));
    conn.on("debug", (payload: { line: string }) => this.onDebug?.(payload.line));
    conn.on("update", (payload: UpdatePayload) => this.onUpdate?.(payload));
    conn.on("modelProgress", (payload: AppStateDto["modelProgress"]) => this.onModelProgress?.(payload));

    try {
      await conn.start();
    } catch {
      // First launch can race the server startup — retry briefly.
      await new Promise((r) => setTimeout(r, 800));
      await conn.start();
    }
    this.connection = conn;
  }

  async getState(): Promise<AppStateDto> {
    const res = await fetch("/api/state");
    if (!res.ok) throw new Error("state http " + res.status);
    return (await res.json()) as AppStateDto;
  }

  async saveSettings(body: {
    hotkey?: { modifiers: string[]; key: string };
    language?: string;
    pasteResult?: boolean;
    microphoneDevice?: number;
    microphoneSensitivity?: number;
    debugMode?: boolean;
    startWithWindows?: boolean;
    minimizeToTrayOnClose?: boolean;
    computeMode?: string;
    gpuDevice?: number;
  }): Promise<{ ok: boolean; label: string }> {
    const res = await fetch("/api/settings", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    if (!res.ok) throw new Error((await res.json().catch(() => ({}))).error ?? "settings http " + res.status);
    return (await res.json()) as { ok: boolean; label: string };
  }

  async toggle(): Promise<void> {
    await fetch("/api/toggle", { method: "POST" });
  }

  async checkUpdate(): Promise<UpdatePayload | null> {
    const res = await fetch("/api/update/check");
    if (!res.ok) return null;
    return (await res.json()) as UpdatePayload | null;
  }

  async installUpdate(update: UpdatePayload): Promise<void> {
    const res = await fetch("/api/update/install", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(update) });
    if (!res.ok) throw new Error("update http " + res.status);
  }
}

export const backend = new Backend();
