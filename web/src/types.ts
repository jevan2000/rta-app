// Mirrors the Hub's JSON (camelCase, enums as strings).

export type Quality = 'Unknown' | 'High' | 'Medium' | 'Low' | 'VeryLow';
export type Acceptance = 'Pending' | 'Accepted' | 'Rejected';
export type AlertMetric = 'LCS' | 'LAS' | 'LCeq1m' | 'LAeq1m' | 'LCeq15m' | 'LAeq15m' | 'LCeqService' | 'LAeqService' | 'LCpeakMax1m';

export interface LiveMic {
  ch: number;
  las: number; lcs: number; laf: number; lcf: number;
  lAeq1m: number; lCeq1m: number; lAeq15m: number; lCeq15m: number;
  lCpeakMax1m: number; cMinusA: number; coverage1m: number;
  calibrated: boolean; clipped: boolean;
  bands: number[];
  warnings: string[];
}

export interface MicView {
  key: string; nodeId: string; nodeName: string; channelIndex: number;
  name: string; label: string; micLabel: string | null; profileName: string | null;
  quality: Quality | null; uncertaintyDb: number | null; calibrated: boolean; hidden: boolean;
  online: boolean; live: LiveMic | null;
  lAeqService: number | null; lCeqService: number | null; serviceSeconds: number;
}

export interface NodeView {
  id: string; name: string; acceptance: Acceptance; connected: boolean; address: string | null;
  lastSeen: number; version: string | null; running: boolean; error: string | null; warnings: string[];
  micCount: number; sampleRate: number | null;
}

export interface ActiveAlert {
  ruleId: number; ruleName: string; micKey: string; level: 'warn' | 'alarm';
  metric: AlertMetric; value: number; threshold: number; since: number;
}

export interface ServiceLive { id: number; name: string; startTs: number; source: string }

export interface HubLive {
  t: number; hubId: string; hubName: string; service: ServiceLive | null;
  nodes: NodeView[]; mics: MicView[]; alerts: ActiveAlert[]; bandsHz: number[];
}

export interface Series {
  mic: string; resolution: 'second' | 'minute';
  ts: number[]; la: number[]; lc: number[]; lz: number[]; lCpeak: number[]; lcSmax: number[]; coverage: number[];
}

export interface ServiceRow { id: number; name: string; startTs: number; endTs: number | null; source: string; scheduleId: number | null }

export interface ScheduleRow { id: number; name: string; day: number; startMinute: number; durationMinutes: number; enabled: boolean }

export interface AlertRule {
  id: number; name: string; target: string; metric: AlertMetric;
  warn: number; alarm: number; hysteresisDb: number; minDurationSeconds: number; enabled: boolean;
}

export interface AlertEvent {
  id: number; ruleId: number; ruleName: string; micKey: string; level: string;
  startTs: number; endTs: number | null; peakValue: number; metric: string;
}

export interface MicSummary {
  micKey: string; label: string; lAeq: number | null; lCeq: number | null;
  maxLAeq1m: number | null; maxLCeq1m: number | null; lCpeakMax: number | null;
  seconds: number; secondsAboveWarnC: number; secondsAboveAlarmC: number;
}

export interface ServiceSummary { service: ServiceRow; mics: MicSummary[]; alerts: AlertEvent[]; warnC: number; alarmC: number }

export interface HubSettings {
  hubId: string; name: string; secondsRetentionDays: number; autoAcceptNodes: boolean; alertRules: AlertRule[];
}

export interface AuthStatus { passwordSet: boolean; signedIn: boolean; canEdit: boolean; local: boolean }

export interface MicRow {
  id: number; key: string; nodeId: string; channelIndex: number; name: string; displayName: string | null;
  micLabel: string | null; hidden: boolean; lastSeen: number;
}
