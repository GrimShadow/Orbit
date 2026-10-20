// Test-only scaffolding for the AI admin page: fixtures, a small in-memory API behind a fetch mock (it enforces the
// server's rules for the privacy acknowledgement and an unconfigured provider), and the console rendered at /admin/ai.
// Nothing in the app imports this.
import { render } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { App, queryClient } from '../../../App';
import { AuthContext } from '../../../auth/AuthProvider';
import { json, mockApi, signedIn, type Call } from '../../workflow/workflowTestKit';
import type { AiSettings, AiStatus } from '../aiApi';

export { json, type Call };

export const providers = (over: { openaiConfigured?: boolean } = {}): AiSettings['providers'] => [
  { kind: 'self-hosted', configured: true, sendsDataOffSite: false, description: 'Models run in this installation.' },
  {
    kind: 'openai-compatible',
    configured: over.openaiConfigured ?? false,
    sendsDataOffSite: true,
    description: 'A cloud service.',
  },
  { kind: 'none', configured: true, sendsDataOffSite: false, description: 'No AI.' },
];

export const settings = (over: Partial<AiSettings> = {}): AiSettings => ({
  enabled: true,
  provider: 'self-hosted',
  defaultProvider: 'self-hosted',
  effectiveProvider: 'self-hosted',
  sendsDataOffSite: false,
  features: { tags: true, ocr: true, transcribe: true, embeddings: true },
  ocrLanguage: null,
  providers: providers(),
  updatedBy: null,
  updatedAt: new Date(Date.now() - 2 * 3_600_000).toISOString(),
  ...over,
});

export const status = (over: Partial<AiStatus> = {}): AiStatus => ({
  enabled: true,
  provider: 'self-hosted',
  providerConfigured: true,
  health: {
    status: 'ok',
    detail: 'sidecar 0.1.0',
    latencyMs: 2,
    models: {
      clip: 'clip-vit-base-patch32',
      text: 'multilingual-e5-small',
      whisper: 'faster-whisper-base',
      ocr: 'rapidocr-pp-ocrv4',
    },
  },
  capabilities: {
    tags: true,
    ocr: true,
    transcribe: true,
    textEmbeddings: true,
    imageEmbeddings: true,
    detectLanguage: true,
    translate: false,
    summarise: false,
  },
  counts: { eligible: 39, enriched: 0, pending: 0, running: 0, failed: 0, skipped: 0, notStarted: 39 },
  queueDepth: 0,
  vectorIndex: { textDim: 384, imageDim: 512, liveTextDim: 384, liveImageDim: 512, needsReindex: false },
  ...over,
});

export const testResult = (
  over: Partial<{ ok: boolean; steps: { name: string; ok: boolean; durationMs: number; detail: string | null }[] }> = {},
) => ({
  provider: 'self-hosted',
  ok: true,
  totalMs: 412,
  steps: [
    { name: 'health', ok: true, durationMs: 3, detail: null },
    { name: 'embed-image', ok: true, durationMs: 280, detail: null },
    { name: 'embed-text', ok: true, durationMs: 6, detail: null },
    { name: 'embed-text-for-images', ok: true, durationMs: 123, detail: null },
  ],
  ...over,
});

export const folder = (id: string, name: string, path: string, parentId: string | null = null) => ({
  id,
  parentId,
  path,
  label: path.split('.').pop()!,
  name,
  inheritedMetadata: {},
  defaultWorkflowId: null,
  createdAt: '2026-01-01T00:00:00Z',
});

export interface World {
  settings: AiSettings;
  status: AiStatus;
  permissions: string[];
  test: ReturnType<typeof testResult>;
  folders: ReturnType<typeof folder>[];
}

export const ADMIN = ['ai.manage'];

export function world(over: Partial<World> = {}): World {
  return {
    settings: settings(),
    status: status(),
    permissions: ADMIN,
    test: testResult(),
    folders: [folder('f1', 'Brand', 'brand'), folder('f2', 'Campaigns', 'brand.campaigns', 'f1')],
    ...over,
  };
}

export const problem = (statusCode: number, title: string, extra: Record<string, unknown> = {}) =>
  json({ title, status: statusCode, ...extra }, statusCode);
export const validation = (errors: Record<string, string[]>) =>
  problem(422, 'One or more validation errors occurred.', { errors });

interface SettingsBody {
  enabled: boolean;
  provider: string | null;
  features: AiSettings['features'] | null;
  ocrLanguage: string | null;
  acknowledgeOffSite: boolean;
}

/** Installs the fetch mock. `extra` answers first (use it to fail or observe a call); the rest follows the server's rules. */
export function install(w: World, extra: (c: Call) => Response | Promise<Response> | undefined = () => undefined) {
  return mockApi(
    (c) => {
      const custom = extra(c);
      if (custom) return custom;
      const { method, pathname: p } = c;
      if (p.startsWith('/api/v1/ai/') && !w.permissions.includes('ai.manage') && p !== '/api/v1/ai/capabilities')
        return problem(403, 'Forbidden');
      if (method === 'GET' && p === '/api/v1/ai/settings') return json(w.settings);
      if (method === 'GET' && p === '/api/v1/ai/status') return json(w.status);
      if (method === 'GET' && p === '/api/v1/folders') return json(w.folders);
      if (method === 'PUT' && p === '/api/v1/ai/settings') {
        return c.json().then((b) => {
          const body = b as SettingsBody;
          const kind = body.provider ?? w.settings.defaultProvider;
          const info = w.settings.providers.find((x) => x.kind === kind);
          if (body.enabled) {
            if (!info || kind === 'none')
              return validation({ provider: ['Choose a provider: the deployment has none by default.'] });
            if (!info.configured)
              return validation({ provider: [`The '${kind}' provider is not configured in this deployment.`] });
            const already = w.settings.enabled && w.settings.effectiveProvider === kind;
            if (info.sendsDataOffSite && !body.acknowledgeOffSite && !already)
              return validation({
                acknowledgeOffSite: ['This provider sends the tenant’s pictures and audio to an external service.'],
              });
          }
          w.settings = {
            ...w.settings,
            enabled: body.enabled,
            provider: body.provider,
            effectiveProvider: kind,
            sendsDataOffSite: info?.sendsDataOffSite ?? false,
            features: body.features ?? w.settings.features,
            ocrLanguage: body.ocrLanguage,
            updatedAt: new Date().toISOString(),
          };
          return json(w.settings);
        });
      }
      if (method === 'POST' && p === '/api/v1/ai/test') return json(w.test);
      if (method === 'POST' && p === '/api/v1/ai/reindex') return json({ accepted: true, message: 'Reindex requested.' }, 202);
      if (method === 'POST' && p === '/api/v1/ai/backfill') return json({ matched: 7, enqueued: 7, remaining: 0 });
      return undefined;
    },
    { permissions: w.permissions },
  );
}

export function renderAt(route = '/admin/ai') {
  queryClient.clear();
  return render(
    <AuthContext.Provider value={signedIn}>
      <MemoryRouter initialEntries={[route]}>
        <App />
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}
