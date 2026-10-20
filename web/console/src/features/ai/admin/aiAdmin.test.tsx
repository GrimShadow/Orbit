import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { setLanguage } from '../../../i18n';
import {
  backfillBody,
  estimateCount,
  estimateSeconds,
  fieldErrors,
  initialBackfill,
  languageInvalid,
  needsAcknowledgement,
  parseLimit,
  providerProblem,
  sameDraft,
  toDraft,
  toRequest,
} from './aiAdminModel';
import {
  install,
  json,
  problem,
  providers,
  renderAt,
  settings,
  status,
  testResult,
  validation,
  world,
  type Call,
} from './aiAdminTestKit';

beforeEach(() => {
  setLanguage('en');
  window.localStorage.clear();
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const writes = (calls: Call[], method: string, pathname: string) =>
  calls.filter((c) => c.method === method && c.pathname === pathname);
const enableSwitch = () => screen.findByRole('switch', { name: 'Use AI in this organisation' });
const featureSwitch = (name: string) => screen.getByRole('switch', { name });
const saveButton = () => screen.getByRole('button', { name: 'Save changes' });
const radio = (name: RegExp) => screen.getByRole('radio', { name });
const dialog = (name: string) => screen.findByRole('dialog', { name });
const loaded = async () => {
  await enableSwitch();
  await screen.findByText('Healthy');
};

describe('status', () => {
  it('shows the provider, health, models, capabilities, progress and the index of a healthy installation', async () => {
    install(world());
    renderAt();
    expect(await screen.findByRole('heading', { level: 1, name: 'AI' })).toBeInTheDocument();
    await loaded();

    const card = screen.getByRole('region', { name: 'Status' });
    expect(within(card).getByText('Self-hosted')).toBeInTheDocument();
    expect(within(card).getByText('Healthy')).toBeInTheDocument();
    expect(within(card).getByText('2 ms')).toBeInTheDocument();
    expect(within(card).getByText('clip-vit-base-patch32')).toBeInTheDocument();
    expect(within(card).getByText('rapidocr-pp-ocrv4')).toBeInTheDocument();

    // Capabilities say yes or no in words: this provider can summarise but not translate (ADR-0036).
    expect(within(card).getByText('Suggest tags').closest('li')).toHaveTextContent('available');
    expect(within(card).getByText('Translation').closest('li')).toHaveTextContent('not available');
    expect(within(card).getByText('Summaries').closest('li')).toHaveTextContent('available');

    expect(within(card).getByText('0 of 39 assets analysed')).toBeInTheDocument();
    expect(within(card).getByRole('group', { name: 'Assets analysed' })).toHaveTextContent('Not started');
    expect(within(card).getByText('Nothing is waiting in the queue.')).toBeInTheDocument();
    expect(within(card).getByText('Text vectors: 384 dimensions. Picture vectors: 512 dimensions.')).toBeInTheDocument();
    expect(within(card).getByText('Up to date')).toBeInTheDocument();
    expect(screen.queryByText('Smart search needs the index rebuilt')).not.toBeInTheDocument();
  });

  it.each([
    ['degraded', 'Degraded'],
    ['loading', 'Starting up'],
    ['down', 'Unreachable'],
    ['unreachable', 'Unreachable'],
  ])('labels a %s service "%s" and explains it', async (health, label) => {
    install(
      world({
        status: status({
          health: { status: health, detail: 'The provider did not answer in time.', latencyMs: null, models: {} },
        }),
      }),
    );
    renderAt();
    expect(await screen.findByText(label)).toBeInTheDocument();
    expect(screen.getByText('The provider did not answer in time.')).toBeInTheDocument();
  });

  it('shows the queue and progress while assets are being analysed', async () => {
    install(
      world({
        status: status({
          counts: { eligible: 1234, enriched: 200, pending: 20, running: 2, failed: 3, skipped: 4, notStarted: 1005 },
          queueDepth: 22,
        }),
      }),
    );
    renderAt();
    expect(await screen.findByText('200 of 1,234 assets analysed')).toBeInTheDocument();
    expect(screen.getByText('22 assets waiting or running')).toBeInTheDocument();
    const bar = screen.getByRole('group', { name: 'Assets analysed' });
    expect(within(bar).getByText('Failed').closest('li')).toHaveTextContent('3');
    expect(within(bar).getByText('Running').closest('li')).toHaveTextContent('2');
  });

  it('says AI is off for everyone when the organisation has not turned it on', async () => {
    install(
      world({
        settings: settings({ enabled: false, provider: null }),
        status: status({
          enabled: false,
          health: { status: 'disabled', detail: 'AI is switched off.', latencyMs: null, models: {} },
        }),
      }),
    );
    renderAt();
    expect(await screen.findByText('AI is off for everyone in this organisation')).toBeInTheDocument();
    expect(screen.getByText('AI is off for everyone')).toBeInTheDocument();
    expect(await enableSwitch()).toHaveAttribute('aria-checked', 'false');
    // Switching on and saving remains possible.
    expect(screen.getByRole('button', { name: 'Analyse assets…' })).toBeDisabled();
    expect(screen.getByText('Switch AI on and save before analysing existing assets.')).toBeInTheDocument();
  });

  it('shows a prominent callout when the index needs rebuilding, and rebuilds on request', async () => {
    const w = world({
      status: status({ vectorIndex: { textDim: 384, imageDim: 512, liveTextDim: 1024, liveImageDim: null, needsReindex: true } }),
    });
    const calls = install(w);
    renderAt();
    const callout = (await screen.findByText('Smart search needs the index rebuilt')).closest('.dam-aicallout') as HTMLElement;
    expect(within(callout).getByText(/does not call the AI provider/)).toBeInTheDocument();
    expect(screen.getByText('The live index was built for 1,024 text and not built picture dimensions.')).toBeInTheDocument();
    expect(screen.queryByText('Up to date')).not.toBeInTheDocument();

    await userEvent.click(within(callout).getByRole('button', { name: 'Rebuild index' }));
    await waitFor(() => expect(writes(calls, 'POST', '/api/v1/ai/reindex')).toHaveLength(1));
    expect(await screen.findByText('Rebuilding in the background')).toBeInTheDocument();
    expect(within(callout).getByRole('button', { name: 'Rebuilding…' })).toBeDisabled();
  });

  it('keeps the settings usable when only the status fails, and retries on request', async () => {
    let fail = true;
    install(world(), (c) => (c.pathname === '/api/v1/ai/status' && fail ? problem(500, 'Boom') : undefined));
    renderAt();
    expect(await screen.findByText('Status could not be loaded', {}, { timeout: 8000 })).toBeInTheDocument();
    expect(await enableSwitch()).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('Healthy')).toBeInTheDocument();
  });

  it('refreshes on demand', async () => {
    const calls = install(world());
    renderAt();
    await loaded();
    const before = writes(calls, 'GET', '/api/v1/ai/status').length;
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => expect(writes(calls, 'GET', '/api/v1/ai/status').length).toBeGreaterThan(before));
  });
});

describe('page states', () => {
  it('shows a calm no-permission state for a 403', async () => {
    install(world({ permissions: [] }));
    renderAt();
    expect(await screen.findByText('AI settings are not available to your role')).toBeInTheDocument();
    expect(screen.queryByRole('switch')).not.toBeInTheDocument();
  });

  it('shows a skeleton while loading, and an error with a retry that recovers', async () => {
    let fail = true;
    install(world(), (c) => (c.pathname === '/api/v1/ai/settings' && fail ? problem(500, 'Boom') : undefined));
    renderAt();
    expect(await screen.findByText('AI settings could not be loaded', {}, { timeout: 8000 })).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await enableSwitch()).toBeInTheDocument();
  });

  it('announces loading before the settings arrive', async () => {
    let release: (r: Response) => void = () => undefined;
    const gate = new Promise<Response>((r) => (release = r));
    install(world(), (c) => (c.pathname === '/api/v1/ai/settings' ? gate : undefined));
    renderAt();
    expect(await screen.findByText('Loading AI settings…')).toBeInTheDocument();
    release(json(settings()));
    expect(await enableSwitch()).toBeInTheDocument();
  });
});

describe('provider and privacy', () => {
  it('lists the providers, disabling one that is not configured on the server', async () => {
    install(world());
    renderAt();
    await loaded();
    expect(radio(/^Self-hosted/)).toBeChecked();
    expect(radio(/^Self-hosted/)).toBeEnabled();
    const off = radio(/^OpenAI-compatible/);
    expect(off).toBeDisabled();
    expect(off).not.toBeChecked();
    expect(screen.getByText('Not configured on the server (set DAM_AI_* variables).')).toBeInTheDocument();
    expect(screen.queryByRole('radio', { name: /^None/ })).not.toBeInTheDocument();

    const self = radio(/^Self-hosted/).closest('label')!;
    expect(within(self).getByText('Stays on your servers')).toBeInTheDocument();
    expect(within(self).getByText('Server default')).toBeInTheDocument();
    expect(within(self).getByText('In use now')).toBeInTheDocument();
    expect(within(radio(/^OpenAI-compatible/).closest('label')!).getByText('Sends content off-site')).toBeInTheDocument();
    expect(screen.getByText('The server default is Self-hosted.', { exact: false })).toBeInTheDocument();
  });

  it('asks for the acknowledgement before an off-site provider is switched on, and sends it', async () => {
    const w = world({
      settings: settings({ enabled: false, provider: null, providers: providers({ openaiConfigured: true }) }),
      status: status({ enabled: false }),
    });
    const calls = install(w);
    renderAt();
    await userEvent.click(await enableSwitch());
    await userEvent.click(radio(/^OpenAI-compatible/));

    expect(screen.getByText('Content will leave your installation')).toBeInTheDocument();
    expect(
      screen.getByText(/pictures, documents, audio and the text read from them are sent to an external service/),
    ).toBeInTheDocument();
    // Blocked until acknowledged.
    expect(saveButton()).toBeDisabled();
    expect(screen.getByText('Confirm that you understand before saving.')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('checkbox', { name: 'I understand that content will be sent to OpenAI-compatible' }));
    expect(saveButton()).toBeEnabled();
    await userEvent.click(saveButton());

    await waitFor(() => expect(writes(calls, 'PUT', '/api/v1/ai/settings')).toHaveLength(1));
    expect(await writes(calls, 'PUT', '/api/v1/ai/settings')[0]!.json()).toEqual({
      enabled: true,
      provider: 'openai-compatible',
      features: { tags: true, ocr: true, transcribe: true, embeddings: true, summaries: true },
      ocrLanguage: null,
      acknowledgeOffSite: true,
    });
    expect(await screen.findByText('AI settings saved')).toBeInTheDocument();
    // The page now shows what the server holds; the acknowledgement is not asked for again.
    await waitFor(() => expect(screen.getByText('All changes are saved.')).toBeInTheDocument());
    expect(screen.getByText('Content is already being sent to OpenAI-compatible (acknowledged earlier).')).toBeInTheDocument();
    expect(screen.queryByRole('checkbox', { name: /I understand/ })).not.toBeInTheDocument();
  });

  it('does not ask again for a change to an off-site provider that is already in use', async () => {
    const w = world({
      settings: settings({
        enabled: true,
        provider: 'openai-compatible',
        effectiveProvider: 'openai-compatible',
        sendsDataOffSite: true,
        providers: providers({ openaiConfigured: true }),
      }),
      status: status({ provider: 'openai-compatible' }),
    });
    const calls = install(w);
    renderAt();
    const toggle = await enableSwitch();
    await userEvent.click(featureSwitch('Suggest tags'));
    // Off and on again within one edit is no change as far as the server is concerned.
    await userEvent.click(toggle);
    await userEvent.click(toggle);
    expect(screen.queryByRole('checkbox', { name: /I understand/ })).not.toBeInTheDocument();
    expect(saveButton()).toBeEnabled();
    await userEvent.click(saveButton());
    await waitFor(() => expect(writes(calls, 'PUT', '/api/v1/ai/settings')).toHaveLength(1));
    expect(await writes(calls, 'PUT', '/api/v1/ai/settings')[0]!.json()).toMatchObject({ acknowledgeOffSite: false });
  });

  it('asks again once AI has been saved off and is then switched on with the off-site provider', async () => {
    const w = world({
      settings: settings({
        enabled: true,
        provider: 'openai-compatible',
        effectiveProvider: 'openai-compatible',
        sendsDataOffSite: true,
        providers: providers({ openaiConfigured: true }),
      }),
      status: status({ provider: 'openai-compatible' }),
    });
    install(w);
    renderAt();
    const toggle = await enableSwitch();
    await userEvent.click(toggle);
    await userEvent.click(saveButton());
    await screen.findByText('AI settings saved');
    await userEvent.click(toggle);
    expect(await screen.findByRole('checkbox', { name: /I understand/ })).not.toBeChecked();
    expect(saveButton()).toBeDisabled();
  });

  it('does not ask while AI stays off, even with an off-site provider chosen', async () => {
    const w = world({ settings: settings({ enabled: false, provider: null, providers: providers({ openaiConfigured: true }) }) });
    install(w);
    renderAt();
    await enableSwitch();
    await userEvent.click(radio(/^OpenAI-compatible/));
    expect(screen.queryByText('Content will leave your installation')).not.toBeInTheDocument();
    expect(saveButton()).toBeEnabled();
  });

  it('will not save a switched-on organisation without a configured provider', async () => {
    const w = world({
      settings: settings({ enabled: false, provider: null, defaultProvider: 'none', effectiveProvider: 'none' }),
      status: status({ enabled: false }),
    });
    install(w);
    renderAt();
    await userEvent.click(await enableSwitch());
    expect(screen.getByText('Choose a provider before switching AI on.')).toBeInTheDocument();
    expect(saveButton()).toBeDisabled();
    await userEvent.click(radio(/^Self-hosted/));
    expect(screen.queryByText('Choose a provider before switching AI on.')).not.toBeInTheDocument();
    expect(saveButton()).toBeEnabled();
  });

  it('sends an explicit provider only when it differs from the server default', async () => {
    const w = world({
      settings: settings({ enabled: false, provider: null, providers: providers({ openaiConfigured: true }) }),
      status: status({ enabled: false }),
    });
    const calls = install(w);
    renderAt();
    await enableSwitch();
    await userEvent.click(radio(/^OpenAI-compatible/));
    await userEvent.click(radio(/^Self-hosted/)); // back to the default: nothing differs but the switch
    expect(screen.queryByText('You have unsaved changes.')).not.toBeInTheDocument();
    await userEvent.click(await enableSwitch());
    await userEvent.click(saveButton());
    await waitFor(() => expect(writes(calls, 'PUT', '/api/v1/ai/settings')).toHaveLength(1));
    expect(await writes(calls, 'PUT', '/api/v1/ai/settings')[0]!.json()).toMatchObject({
      enabled: true,
      provider: null,
      acknowledgeOffSite: false,
    });
  });
});

describe('features and OCR language', () => {
  it('saves the feature switches and reflects the server answer', async () => {
    const calls = install(world());
    renderAt();
    await loaded();
    expect(featureSwitch('Suggest tags')).toHaveAttribute('aria-checked', 'true');
    await userEvent.click(featureSwitch('Suggest tags'));
    await userEvent.click(featureSwitch('Transcribe audio and video'));
    expect(featureSwitch('Suggest tags')).toHaveAttribute('aria-checked', 'false');
    expect(screen.getByText('You have unsaved changes.')).toBeInTheDocument();

    await userEvent.click(saveButton());
    await waitFor(() => expect(writes(calls, 'PUT', '/api/v1/ai/settings')).toHaveLength(1));
    expect(await writes(calls, 'PUT', '/api/v1/ai/settings')[0]!.json()).toEqual({
      enabled: true,
      provider: 'self-hosted',
      features: { tags: false, ocr: true, transcribe: false, embeddings: true, summaries: true },
      ocrLanguage: null,
      acknowledgeOffSite: false,
    });
    expect(await screen.findByText('AI settings saved')).toBeInTheDocument();
    expect(featureSwitch('Suggest tags')).toHaveAttribute('aria-checked', 'false');
    expect(screen.getByText(/All changes are saved\./)).toBeInTheDocument();
  });

  it('has a switch for summaries, on by default, that is saved with the others', async () => {
    const calls = install(world());
    renderAt();
    await loaded();
    expect(featureSwitch('Summarise documents')).toHaveAttribute('aria-checked', 'true');
    await userEvent.click(featureSwitch('Summarise documents'));
    expect(featureSwitch('Summarise documents')).toHaveAttribute('aria-checked', 'false');
    await userEvent.click(saveButton());
    await waitFor(() => expect(writes(calls, 'PUT', '/api/v1/ai/settings')).toHaveLength(1));
    expect(await writes(calls, 'PUT', '/api/v1/ai/settings')[0]!.json()).toMatchObject({
      features: { summaries: false, tags: true },
    });
  });

  it('treats a server that does not send the summaries switch as on, and does not call that an edit', async () => {
    const old = settings();
    delete (old.features as { summaries?: boolean }).summaries;
    install(world({ settings: old }));
    renderAt();
    await loaded();
    expect(featureSwitch('Summarise documents')).toHaveAttribute('aria-checked', 'true');
    expect(saveButton()).toBeDisabled();
  });

  it('warns when the provider cannot summarise and the switch is on', async () => {
    install(world({ status: status({ capabilities: { ...status().capabilities, summarise: false } }) }));
    renderAt();
    await loaded();
    expect(
      screen.getByText(/Writes a short summary.*The provider in use cannot do this, so the step is skipped\./),
    ).toBeInTheDocument();
  });

  it('turning a switch off and on again leaves nothing to save', async () => {
    install(world());
    renderAt();
    await loaded();
    await userEvent.click(featureSwitch('Enable smart search'));
    expect(saveButton()).toBeEnabled();
    await userEvent.click(featureSwitch('Enable smart search'));
    expect(saveButton()).toBeDisabled();
  });

  it('sends the OCR language lower-cased, and empty as automatic', async () => {
    const calls = install(world());
    renderAt();
    await loaded();
    const field = screen.getByLabelText('OCR language');
    expect(field).toHaveValue('');
    await userEvent.type(field, 'PT-BR');
    await userEvent.click(saveButton());
    await waitFor(() => expect(writes(calls, 'PUT', '/api/v1/ai/settings')).toHaveLength(1));
    expect(await writes(calls, 'PUT', '/api/v1/ai/settings')[0]!.json()).toMatchObject({ ocrLanguage: 'pt-br' });

    await screen.findByText('AI settings saved');
    await waitFor(() => expect(screen.getByLabelText('OCR language')).toHaveValue('pt-br'));
    await userEvent.clear(screen.getByLabelText('OCR language'));
    await userEvent.click(saveButton());
    await waitFor(() => expect(writes(calls, 'PUT', '/api/v1/ai/settings')).toHaveLength(2));
    expect(await writes(calls, 'PUT', '/api/v1/ai/settings')[1]!.json()).toMatchObject({ ocrLanguage: null });
  });

  it('refuses a language that is not a code before asking the server', async () => {
    const calls = install(world());
    renderAt();
    await loaded();
    await userEvent.type(screen.getByLabelText('OCR language'), 'english');
    expect(screen.getByText('Use a language code such as en, hi or pt-br.')).toBeInTheDocument();
    expect(saveButton()).toBeDisabled();
    expect(writes(calls, 'PUT', '/api/v1/ai/settings')).toHaveLength(0);
  });

  it('warns when the provider in use cannot do a step that is switched on', async () => {
    install(world({ status: status({ capabilities: { ...status().capabilities, transcribe: false } }) }));
    renderAt();
    await loaded();
    expect(screen.getByText(/The provider in use cannot do this, so the step is skipped\./)).toBeInTheDocument();
  });
});

describe('saving, discarding and the unsaved guard', () => {
  it('discards edits and restores the saved values', async () => {
    const calls = install(world());
    renderAt();
    await loaded();
    await userEvent.click(featureSwitch('Read text in pictures and pages (OCR)'));
    await userEvent.type(screen.getByLabelText('OCR language'), 'hi');
    await userEvent.click(screen.getByRole('button', { name: 'Discard changes' }));
    expect(featureSwitch('Read text in pictures and pages (OCR)')).toHaveAttribute('aria-checked', 'true');
    expect(screen.getByLabelText('OCR language')).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Discard changes' })).toBeDisabled();
    expect(writes(calls, 'PUT', '/api/v1/ai/settings')).toHaveLength(0);
  });

  it('shows when it was last saved while nothing is pending', async () => {
    install(world());
    renderAt();
    await loaded();
    expect(screen.getByText('All changes are saved.')).toBeInTheDocument();
    expect(screen.getByText('Last saved 2 hours ago')).toBeInTheDocument();
    expect(saveButton()).toBeDisabled();
  });

  it('asks before an in-app link throws unsaved edits away', async () => {
    install(world());
    renderAt();
    await loaded();
    await userEvent.click(featureSwitch('Suggest tags'));

    await userEvent.click(screen.getAllByRole('link', { name: /Library/ })[0]!);
    const ask = await dialog('Leave without saving?');
    await userEvent.click(within(ask).getByRole('button', { name: 'Keep editing' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Leave without saving?' })).not.toBeInTheDocument());
    expect(featureSwitch('Suggest tags')).toHaveAttribute('aria-checked', 'false');

    await userEvent.click(screen.getAllByRole('link', { name: /Library/ })[0]!);
    const again = await dialog('Leave without saving?');
    await userEvent.click(within(again).getByRole('button', { name: 'Leave without saving' }));
    await waitFor(() => expect(screen.queryByRole('switch', { name: 'Suggest tags' })).not.toBeInTheDocument());
  });

  it('does not ask when there is nothing to lose', async () => {
    install(world());
    renderAt();
    await loaded();
    await userEvent.click(screen.getAllByRole('link', { name: /Library/ })[0]!);
    await waitFor(() => expect(screen.queryByRole('switch', { name: 'Suggest tags' })).not.toBeInTheDocument());
    expect(screen.queryByRole('dialog', { name: 'Leave without saving?' })).not.toBeInTheDocument();
  });

  it('maps the server’s 422 answers onto the provider and OCR language fields', async () => {
    install(world(), (c) =>
      c.method === 'PUT'
        ? validation({
            provider: ['The provider is not configured in this deployment. Set DAM_AI_SIDECAR_URL.'],
            ocrLanguage: ['Use a language code such as en, hi or pt-br.'],
          })
        : undefined,
    );
    renderAt();
    await loaded();
    await userEvent.type(screen.getByLabelText('OCR language'), 'xx');
    await userEvent.click(saveButton());

    const fieldset = screen.getByRole('group', { name: 'Provider' });
    expect(
      await within(fieldset).findByText('The provider is not configured in this deployment. Set DAM_AI_SIDECAR_URL.'),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('OCR language')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getAllByText('Use a language code such as en, hi or pt-br.').length).toBeGreaterThan(0);
    // The edits stay, and editing the field clears its message.
    expect(screen.getByLabelText('OCR language')).toHaveValue('xx');
    await userEvent.type(screen.getByLabelText('OCR language'), 'y');
    expect(screen.getByLabelText('OCR language')).not.toHaveAttribute('aria-invalid');
  });

  it('shows the acknowledgement when the server asks for one the page did not expect', async () => {
    const w = world({
      settings: settings({
        enabled: true,
        provider: 'openai-compatible',
        effectiveProvider: 'openai-compatible',
        sendsDataOffSite: true,
        providers: providers({ openaiConfigured: true }),
      }),
      status: status({ provider: 'openai-compatible' }),
    });
    install(w, (c) =>
      c.method === 'PUT'
        ? validation({ acknowledgeOffSite: ['This provider sends the tenant pictures and audio to an external service.'] })
        : undefined,
    );
    renderAt();
    await enableSwitch();
    await userEvent.click(featureSwitch('Suggest tags'));
    await userEvent.click(saveButton());
    const box = await screen.findByRole('checkbox', { name: /I understand/ });
    expect(box).toHaveAttribute('aria-invalid', 'true');
    expect((await screen.findAllByText(/sends the tenant pictures and audio/)).length).toBeGreaterThan(0);
    expect(saveButton()).toBeDisabled();
  });

  it('tells the user when saving fails for another reason, and keeps the edits', async () => {
    install(world(), (c) => (c.method === 'PUT' ? problem(500, 'Boom') : undefined));
    renderAt();
    await loaded();
    await userEvent.click(featureSwitch('Suggest tags'));
    await userEvent.click(saveButton());
    expect(await screen.findByText('AI settings could not be saved')).toBeInTheDocument();
    expect(featureSwitch('Suggest tags')).toHaveAttribute('aria-checked', 'false');
    expect(saveButton()).toBeEnabled();
  });
});

describe('test connection', () => {
  it('runs the test and shows each step with its duration', async () => {
    const calls = install(world());
    renderAt();
    await loaded();
    await userEvent.click(screen.getByRole('button', { name: 'Run test' }));
    expect(await screen.findByText('The connection works')).toBeInTheDocument();
    expect(writes(calls, 'POST', '/api/v1/ai/test')).toHaveLength(1);
    expect(screen.getByText('Self-hosted answered every check in 412 ms.')).toBeInTheDocument();
    const steps = screen.getByRole('list', { name: 'Test steps' });
    expect(within(steps).getAllByRole('listitem')).toHaveLength(4);
    expect(within(steps).getByText('Service is reachable').closest('li')).toHaveTextContent('3 ms');
    expect(within(steps).getByText('Picture vectors').closest('li')).toHaveTextContent('280 ms');
    expect(within(steps).getByText('Text-to-picture vectors').closest('li')).toHaveTextContent('passed');
  });

  it('explains a failed step in plain words and keeps the technical detail small', async () => {
    install(
      world({
        test: testResult({
          ok: false,
          steps: [
            { name: 'health', ok: false, durationMs: 5003, detail: 'Connection refused (localhost:18090)' },
            { name: 'embed-image', ok: true, durationMs: 10, detail: null },
          ],
        }),
      }),
    );
    renderAt();
    await loaded();
    await userEvent.click(screen.getByRole('button', { name: 'Run test' }));
    expect(await screen.findByText('Something is not working')).toBeInTheDocument();
    const failed = screen.getByText('Service is reachable').closest('li')!;
    expect(failed).toHaveTextContent('failed');
    expect(within(failed).getByText(/Check that the AI service is running/)).toBeInTheDocument();
    expect(within(failed).getByText('Connection refused (localhost:18090)')).toBeInTheDocument();
    expect(within(failed).getByText('5,003 ms')).toBeInTheDocument();
  });

  it('reports when the test itself could not be run', async () => {
    install(world(), (c) => (c.pathname === '/api/v1/ai/test' ? problem(500, 'Boom') : undefined));
    renderAt();
    await loaded();
    await userEvent.click(screen.getByRole('button', { name: 'Run test' }));
    expect(await screen.findByText('The test could not be run')).toBeInTheDocument();
  });
});

describe('analyse existing assets', () => {
  const openDialog = async () => {
    await userEvent.click(screen.getByRole('button', { name: 'Analyse assets…' }));
    return dialog('Analyse existing assets?');
  };

  it('estimates from the not-started count, and the estimate follows the limit and filters', async () => {
    install(
      world({
        status: status({
          counts: { eligible: 1000, enriched: 400, pending: 0, running: 0, failed: 0, skipped: 0, notStarted: 600 },
        }),
      }),
    );
    renderAt();
    await loaded();
    // Default limit 500 is below 600 not-started: about 500 assets, 750 s = 13 minutes.
    expect(screen.getByText('About 500 assets, taking about 13 minutes at roughly 1.5 seconds per image.')).toBeInTheDocument();

    const limit = screen.getByLabelText('Most assets to queue now');
    await userEvent.clear(limit);
    await userEvent.type(limit, '20');
    expect(screen.getByText('About 20 assets, taking under a minute at roughly 1.5 seconds per image.')).toBeInTheDocument();

    await userEvent.clear(limit);
    await userEvent.type(limit, '5000');
    expect(screen.getByText('About 600 assets, taking about 15 minutes at roughly 1.5 seconds per image.')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('checkbox', { name: 'Only assets not yet analysed' }));
    expect(screen.getByText('About 1,000 assets, taking about 25 minutes at roughly 1.5 seconds per image.')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('checkbox', { name: 'Videos' }));
    expect(screen.getByText(/^Up to 1,000 assets with these filters/)).toBeInTheDocument();
  });

  it('refuses a limit outside 1 to 5,000 and cannot start with no asset type', async () => {
    install(world());
    renderAt();
    await loaded();
    const start = screen.getByRole('button', { name: 'Analyse assets…' });
    expect(start).toBeEnabled();
    const limit = screen.getByLabelText('Most assets to queue now');
    await userEvent.clear(limit);
    await userEvent.type(limit, '6000');
    expect(screen.getByText('Enter a whole number between 1 and 5,000.')).toBeInTheDocument();
    expect(start).toBeDisabled();
    await userEvent.clear(limit);
    await userEvent.type(limit, '10');
    expect(start).toBeEnabled();
    for (const type of ['Images', 'Videos', 'Audio', 'Documents', 'Manuals'])
      await userEvent.click(screen.getByRole('checkbox', { name: type }));
    expect(start).toBeDisabled();
  });

  it('confirms first, says it processes real content, and cancelling sends nothing', async () => {
    const calls = install(world());
    renderAt();
    await loaded();
    const ask = await openDialog();
    expect(within(ask).getByText('This will queue about 39 assets for analysis.')).toBeInTheDocument();
    expect(within(ask).getByText(/processes real content from your library with Self-hosted/)).toBeInTheDocument();
    expect(within(ask).queryByText(/This provider is off-site/)).not.toBeInTheDocument();
    await userEvent.click(within(ask).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Analyse existing assets?' })).not.toBeInTheDocument());
    expect(writes(calls, 'POST', '/api/v1/ai/backfill')).toHaveLength(0);
  });

  it('sends the defaults as an unfiltered request and reports what was queued', async () => {
    const calls = install(world());
    renderAt();
    await loaded();
    const ask = await openDialog();
    await userEvent.click(within(ask).getByRole('button', { name: 'Start analysis' }));
    await waitFor(() => expect(writes(calls, 'POST', '/api/v1/ai/backfill')).toHaveLength(1));
    expect(await writes(calls, 'POST', '/api/v1/ai/backfill')[0]!.json()).toEqual({
      types: null,
      folderId: null,
      includeSubfolders: null,
      onlyMissing: true,
      limit: 500,
    });
    expect(await screen.findByText('Queued 7 assets')).toBeInTheDocument();
    expect(screen.getByText('7 matched, 7 queued, 0 remaining. Progress shows in the status above.')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Analyse existing assets?' })).not.toBeInTheDocument());
    // The status is read again so the counts move.
    await waitFor(() => expect(writes(calls, 'GET', '/api/v1/ai/status').length).toBeGreaterThan(1));
  });

  it('sends the chosen types, folder, subfolders flag, option and limit', async () => {
    const calls = install(world());
    renderAt();
    await loaded();
    await userEvent.click(screen.getByRole('checkbox', { name: 'Audio' }));
    await userEvent.click(screen.getByRole('checkbox', { name: 'Manuals' }));
    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Folder' }), 'f1');
    await userEvent.click(screen.getByRole('checkbox', { name: 'Include subfolders' }));
    await userEvent.click(screen.getByRole('checkbox', { name: 'Only assets not yet analysed' }));
    const limit = screen.getByLabelText('Most assets to queue now');
    await userEvent.clear(limit);
    await userEvent.type(limit, '25');

    const ask = await openDialog();
    expect(within(ask).getByText('This will queue about 25 assets for analysis.')).toBeInTheDocument();
    await userEvent.click(within(ask).getByRole('button', { name: 'Start analysis' }));
    await waitFor(() => expect(writes(calls, 'POST', '/api/v1/ai/backfill')).toHaveLength(1));
    expect(await writes(calls, 'POST', '/api/v1/ai/backfill')[0]!.json()).toEqual({
      types: ['image', 'video', 'document'],
      folderId: 'f1',
      includeSubfolders: false,
      onlyMissing: false,
      limit: 25,
    });
  });

  it('warns in the dialog when the content goes off-site', async () => {
    const w = world({
      settings: settings({
        enabled: true,
        provider: 'openai-compatible',
        effectiveProvider: 'openai-compatible',
        sendsDataOffSite: true,
        providers: providers({ openaiConfigured: true }),
      }),
      status: status({ provider: 'openai-compatible' }),
    });
    install(w);
    renderAt();
    await enableSwitch();
    const ask = await openDialog();
    expect(
      within(ask).getByText('This provider is off-site: the content of these assets will be sent to it.'),
    ).toBeInTheDocument();
    expect(within(ask).getByText(/with OpenAI-compatible/)).toBeInTheDocument();
  });

  it('shows the server’s refusal inside the dialog and keeps it open', async () => {
    install(world(), (c) =>
      c.pathname === '/api/v1/ai/backfill'
        ? problem(409, 'Conflict', { detail: 'AI is not enabled for this tenant.' })
        : undefined,
    );
    renderAt();
    await loaded();
    const ask = await openDialog();
    await userEvent.click(within(ask).getByRole('button', { name: 'Start analysis' }));
    expect(await within(ask).findByText('AI is not enabled for this tenant.')).toBeInTheDocument();
    expect(screen.getByRole('dialog', { name: 'Analyse existing assets?' })).toBeInTheDocument();
  });

  it('offers the folders as an indented list', async () => {
    install(world());
    renderAt();
    await loaded();
    const select = await screen.findByRole('combobox', { name: 'Folder' });
    await waitFor(() => expect(within(select).getAllByRole('option')).toHaveLength(3));
    expect(
      within(select)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['All folders', 'Brand', '— Campaigns']);
    expect(screen.queryByRole('checkbox', { name: 'Include subfolders' })).not.toBeInTheDocument();
    fireEvent.change(select, { target: { value: 'f2' } });
    expect(screen.getByRole('checkbox', { name: 'Include subfolders' })).toBeChecked();
  });
});

describe('accessibility', () => {
  it('has no axe violations on the page, with the callouts and the test result showing', async () => {
    install(
      world({
        status: status({
          vectorIndex: { textDim: 384, imageDim: 512, liveTextDim: 1024, liveImageDim: 512, needsReindex: true },
        }),
      }),
    );
    const { container } = renderAt();
    await loaded();
    await userEvent.click(screen.getByRole('button', { name: 'Run test' }));
    await screen.findByText('The connection works');
    expect(await axe(container)).toHaveNoViolations();
  });

  it('has no axe violations for a switched-off organisation, nor with the privacy panel open', async () => {
    const w = world({
      settings: settings({ enabled: false, provider: null, providers: providers({ openaiConfigured: true }) }),
      status: status({ enabled: false, health: { status: 'disabled', detail: null, latencyMs: null, models: {} } }),
    });
    install(w);
    const { container } = renderAt();
    await enableSwitch();
    expect(await axe(container)).toHaveNoViolations();
    await userEvent.click(await enableSwitch());
    await userEvent.click(radio(/^OpenAI-compatible/));
    await screen.findByText('Content will leave your installation');
    expect(await axe(container)).toHaveNoViolations();
  });

  it('has no axe violations in the backfill and leave dialogs', async () => {
    install(world());
    const { container } = renderAt();
    await loaded();
    await userEvent.click(screen.getByRole('button', { name: 'Analyse assets…' }));
    await dialog('Analyse existing assets?');
    expect(await axe(container)).toHaveNoViolations();
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    await userEvent.click(featureSwitch('Suggest tags'));
    await userEvent.click(screen.getAllByRole('link', { name: /Library/ })[0]!);
    await dialog('Leave without saving?');
    expect(await axe(container)).toHaveNoViolations();
  });

  it('has no axe violations while the server\u2019s refusals are showing', async () => {
    install(world(), (c) =>
      c.method === 'PUT'
        ? validation({
            provider: ['The provider is not configured.'],
            ocrLanguage: ['Use a language code such as en, hi or pt-br.'],
          })
        : undefined,
    );
    const { container } = renderAt();
    await loaded();
    await userEvent.type(screen.getByLabelText('OCR language'), 'xx');
    await userEvent.click(saveButton());
    await screen.findByText('The server did not accept some settings. Check the highlighted fields.');
    expect(await axe(container)).toHaveNoViolations();
  });

  it('has no axe violations in the 403 and error states', async () => {
    install(world({ permissions: [] }));
    const { container } = renderAt();
    await screen.findByText('AI settings are not available to your role');
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('model helpers', () => {
  const s = settings({ providers: providers({ openaiConfigured: true }) });

  it('compares drafts, ignoring blanks around the language', () => {
    const d = toDraft(s);
    expect(sameDraft(d, { ...d })).toBe(true);
    expect(sameDraft(d, { ...d, ocrLanguage: ' ' })).toBe(true);
    expect(sameDraft(d, { ...d, features: { ...d.features, ocr: false } })).toBe(false);
    expect(sameDraft(d, { ...d, provider: 'openai-compatible' })).toBe(false);
  });

  it('builds the request from the draft', () => {
    expect(toRequest({ ...toDraft(s), ocrLanguage: ' HI ' }, true)).toEqual({
      enabled: true,
      provider: 'self-hosted',
      features: { tags: true, ocr: true, transcribe: true, embeddings: true, summaries: true },
      ocrLanguage: 'hi',
      acknowledgeOffSite: true,
    });
  });

  it('validates language hints like the server', () => {
    for (const ok of ['', 'en', 'hi', 'pt-br', 'zh_hans', ' EN ']) expect(languageInvalid(ok)).toBe(false);
    for (const bad of ['e', 'english', 'en-', 'en-us-x', '12']) expect(languageInvalid(bad)).toBe(true);
  });

  it('needs the acknowledgement only for a newly enabled off-site provider', () => {
    const d = toDraft(s);
    expect(needsAcknowledgement({ ...d, provider: 'openai-compatible' }, s)).toBe(true);
    expect(needsAcknowledgement({ ...d, provider: 'openai-compatible', enabled: false }, s)).toBe(false);
    expect(needsAcknowledgement(d, s)).toBe(false);
    const on = settings({ ...s, effectiveProvider: 'openai-compatible', provider: 'openai-compatible' });
    expect(needsAcknowledgement(toDraft(on), on)).toBe(false);
    expect(needsAcknowledgement({ ...toDraft(on), provider: 'self-hosted' }, on)).toBe(false);
  });

  it('reports why a draft cannot be switched on', () => {
    const d = toDraft(s);
    expect(providerProblem(d, s)).toBeNull();
    expect(providerProblem({ ...d, provider: 'openai-compatible' }, settings())).toBe('unconfigured');
    expect(providerProblem({ ...d, provider: null }, settings({ defaultProvider: 'none' }))).toBe('choose');
    expect(providerProblem({ ...d, enabled: false, provider: null }, settings({ defaultProvider: 'none' }))).toBeNull();
  });

  it('reads field errors in either casing', () => {
    expect(fieldErrors({ Provider: ['a', 'b'], acknowledgeOffSite: ['c'], ocrLanguage: [] })).toEqual({
      provider: 'a',
      acknowledgeOffSite: 'c',
    });
  });

  it('estimates from the counts, within the limit', () => {
    const base = initialBackfill();
    const st = status();
    expect(estimateCount(base, st)).toBe(39);
    expect(estimateCount({ ...base, limit: '10' }, st)).toBe(10);
    expect(estimateCount({ ...base, limit: 'x' }, st)).toBeNull();
    expect(estimateCount(base, undefined)).toBeNull();
    expect(
      estimateCount({ ...base, onlyMissing: false }, status({ counts: { ...st.counts, eligible: 50, notStarted: 5 } })),
    ).toBe(50);
    expect(estimateSeconds(40)).toBe(60);
  });

  it('parses the limit strictly', () => {
    expect(parseLimit('500')).toBe(500);
    expect(parseLimit(' 5000 ')).toBe(5000);
    for (const bad of ['', '0', '5001', '1.5', '-1', 'abc']) expect(parseLimit(bad)).toBeNull();
  });

  it('builds the backfill body, leaving out what is not filtered', () => {
    expect(backfillBody(initialBackfill())).toEqual({
      types: null,
      folderId: null,
      includeSubfolders: null,
      onlyMissing: true,
      limit: 500,
    });
    expect(
      backfillBody({
        ...initialBackfill(),
        types: ['audio'],
        folderId: 'f9',
        includeSubfolders: false,
        onlyMissing: false,
        limit: '3',
      }),
    ).toEqual({
      types: ['audio'],
      folderId: 'f9',
      includeSubfolders: false,
      onlyMissing: false,
      limit: 3,
    });
  });
});

describe('Hindi', () => {
  it('renders the settings page, the status card and the provider card in Hindi', async () => {
    setLanguage('hi');
    install(world());
    renderAt();
    expect(await screen.findByRole('switch', { name: 'इस संगठन में AI का उपयोग करें' })).toBeInTheDocument();
    const card = await screen.findByRole('region', { name: 'स्थिति' });
    expect(await within(card).findByText('स्वस्थ')).toBeInTheDocument();
    expect(within(card).getByText('टैग सुझाना').closest('li')).toHaveTextContent('उपलब्ध');
    expect(within(card).getByText('2 ms')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'परीक्षण चलाएँ' })).toBeInTheDocument();
    expect(screen.queryByText('Healthy')).not.toBeInTheDocument();
  });
});
