import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { mockApi, renderAt } from '../workflow/workflowTestKit';
import { axe } from 'vitest-axe';
import { queryClient } from '../../App';
import { setLanguage } from '../../i18n';
import { MetadataFields, type Values } from '../asset/MetadataFields';
import { TranslateAssetButton } from './TranslateAssetButton';
import { reviewHref } from './reviewLink';
import {
  ADMIN,
  EDITOR,
  FIELDS,
  READER,
  aiDto,
  assetDto,
  caps,
  job,
  json,
  newWorld,
  posts,
  backend,
  renderMetadata,
  renderUi,
  tag,
  translation,
  type World,
} from './translationTestKit';

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const headline = () => screen.findByRole('textbox', { name: 'Headline - English' });
const hindi = () => screen.getByRole('textbox', { name: 'Headline - Hindi' });
const translateButton = () => screen.findByRole('button', { name: 'Translate Headline into Hindi with AI' });
const acceptPosts = (w: World) => posts(w, '/accept');

describe('multilingual field: side by side', () => {
  it('shows a source and a target language next to each other, with a character count each', async () => {
    renderMetadata(newWorld());
    expect(await headline()).toHaveValue('Spring is here');
    expect(hindi()).toHaveValue('');
    expect(screen.getByRole('combobox', { name: 'Source language of Headline' })).toHaveValue('en');
    expect(screen.getByRole('combobox', { name: 'Target language of Headline' })).toHaveValue('hi');
    // Headline has a limit of 60 characters, Blurb none.
    expect(screen.getAllByText('14 of 60 characters').length).toBeGreaterThan(0);
    expect(screen.getByText('0 of 60 characters')).toBeInTheDocument();
    expect(screen.getAllByText('0 characters').length).toBeGreaterThan(0);
    expect(screen.getAllByText('1 of 5 languages')[0]).toBeInTheDocument();
    expect(hindi()).toHaveAttribute('lang', 'hi');
  });

  it('offers only the enabled languages and marks the empty ones', async () => {
    renderMetadata(newWorld());
    await headline();
    const target = screen.getByRole('combobox', { name: 'Target language of Headline' });
    const options = within(target)
      .getAllByRole('option')
      .map((o) => o.textContent);
    expect(options).toEqual([
      'English (en)',
      'Hindi (hi) - empty',
      'Marathi (mr) - empty',
      'Tamil (ta) - empty',
      'Telugu (te) - empty',
    ]);
  });

  it('typing in either pane edits the draft and brings up the save bar; counts follow', async () => {
    renderMetadata(newWorld());
    await userEvent.type(await headline(), '!');
    await expect(headline()).resolves.toHaveValue('Spring is here!');
    expect(await screen.findByText('You have unsaved changes')).toBeInTheDocument();
    expect(screen.getByText('15 of 60 characters')).toBeInTheDocument();
    await userEvent.type(hindi(), 'वसंत');
    expect(hindi()).toHaveValue('वसंत');
    expect(screen.getByText('4 of 60 characters')).toBeInTheDocument();
  });

  it('warns when a text is over its limit, in words as well as colour', async () => {
    renderMetadata(newWorld({ asset: assetDto({ headline: { en: 'x'.repeat(61) } }) }));
    await headline();
    expect(screen.getByText(/61 of 60 characters/)).toHaveTextContent('Over the limit.');
    expect(await headline()).toHaveAttribute('aria-invalid', 'true');
  });

  it('shows the same language in only one pane: picking it swaps', async () => {
    renderMetadata(newWorld());
    await headline();
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Target language of Headline' }), 'en');
    expect(screen.getByRole('combobox', { name: 'Source language of Headline' })).toHaveValue('hi');
    expect(screen.getByRole('combobox', { name: 'Target language of Headline' })).toHaveValue('en');
  });

  it('swaps the pair with the swap button, and picks any other pair', async () => {
    renderMetadata(newWorld());
    await headline();
    await userEvent.click(screen.getByRole('button', { name: 'Swap the source and target languages of Headline' }));
    expect(screen.getByRole('combobox', { name: 'Source language of Headline' })).toHaveValue('hi');
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Target language of Headline' }), 'ta');
    expect(screen.getByRole('textbox', { name: 'Headline - Tamil' })).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Headline - Hindi' })).toBeInTheDocument();
  });

  it('can be laid out side by side or stacked', async () => {
    const { container } = renderMetadata(newWorld());
    await headline();
    const panes = container.querySelector('.dam-ml__panes')!;
    expect(panes).toHaveAttribute('data-layout', 'side');
    await userEvent.click(screen.getAllByRole('button', { name: 'Stacked' })[0]!);
    expect(panes).toHaveAttribute('data-layout', 'stacked');
  });

  it('is a plain pair of text boxes where there is no asset (the upload form): no AI controls', async () => {
    function Upload() {
      const [values, setValues] = useState<Values>({ headline: { en: 'Hello' } });
      return (
        <MetadataFields fields={FIELDS as never} values={values} onChange={(n, v) => setValues((x) => ({ ...x, [n]: v }))} />
      );
    }
    renderUi(newWorld(), <Upload />);
    expect(await headline()).toHaveValue('Hello');
    expect(screen.queryByRole('button', { name: /with AI/ })).not.toBeInTheDocument();
    expect(screen.queryByText(/AI is off|cannot translate/)).not.toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const w = newWorld({ ai: aiDto({ suggestions: [translation('s1', 'headline', 'hi', 'वसंत आ गया')] }) });
    const { container } = renderMetadata(w);
    await screen.findByRole('group', { name: 'AI suggestion' });
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('Translate with AI, by capability', () => {
  it('is offered when AI is on and the provider can translate', async () => {
    renderMetadata(newWorld());
    expect(await translateButton()).toBeEnabled();
  });

  it('says the provider cannot translate, instead of offering a button that fails', async () => {
    renderMetadata(newWorld({ caps: caps({ translate: false, provider: 'self-hosted', sendsDataOffSite: false }) }));
    await headline();
    expect((await screen.findAllByText(/provider your organisation uses cannot translate/))[0]).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /with AI/ })).not.toBeInTheDocument();
    // An editor who is not an administrator is not sent to settings they cannot open.
    expect(screen.queryByRole('link', { name: 'AI settings' })).not.toBeInTheDocument();
  });

  it('links an administrator to the AI settings from that hint', async () => {
    renderMetadata(newWorld({ caps: caps({ translate: false }) }), { permissions: ADMIN });
    const links = await screen.findAllByRole('link', { name: 'AI settings' });
    expect(links[0]).toHaveAttribute('href', '/admin/ai');
  });

  it('says AI is off when it is off for the organisation', async () => {
    renderMetadata(newWorld({ caps: caps({ enabled: false, translate: false }) }));
    expect((await screen.findAllByText(/AI is off for your organisation/))[0]).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /with AI/ })).not.toBeInTheDocument();
  });

  it('shows a reader plain text boxes: no button, no hint, no suggestions', async () => {
    const w = newWorld({ ai: aiDto({ suggestions: [translation('s1', 'headline', 'hi', 'वसंत')] }) });
    renderMetadata(w, { permissions: READER });
    await headline();
    await new Promise((r) => setTimeout(r, 100));
    expect(screen.queryByRole('button', { name: /with AI/ })).not.toBeInTheDocument();
    expect(screen.queryByText(/cannot translate|AI is off/)).not.toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'AI suggestion' })).not.toBeInTheDocument();
  });

  it('waits for the saved text: unsaved edits to the source disable it, and an empty source does too', async () => {
    renderMetadata(newWorld());
    await userEvent.type(await headline(), '!');
    expect(await translateButton()).toBeDisabled();
    expect(screen.getByText('Save your changes to the English text first: AI translates the saved text.')).toBeInTheDocument();
    await userEvent.clear(await headline());
    // Blurb has no English text either.
    expect(screen.getAllByText('Write the English text first.')).toHaveLength(2);
  });

  it('starts a translation for this field and language, then reports it as ready to review', async () => {
    const w = newWorld();
    renderMetadata(w);
    await userEvent.click(await translateButton());
    await waitFor(() => expect(posts(w, '/translate')).toHaveLength(1));
    expect(await posts(w, '/translate')[0]!.json()).toEqual({
      fields: ['headline'],
      targetLanguages: ['hi'],
      sourceLanguage: 'en',
      overwrite: true,
    });
    expect(await screen.findByText('The Hindi translation is ready to review.')).toBeInTheDocument();
  });

  it('shows a skipped or failed result with its reason', async () => {
    const w = newWorld({
      nextJob: job({
        status: 'partial',
        items: [{ field: 'headline', language: 'hi', status: 'failed', reason: 'the provider changed a protected term' }],
      }),
    });
    renderMetadata(w);
    await userEvent.click(await translateButton());
    expect(await screen.findByText('The translation failed: the provider changed a protected term')).toBeInTheDocument();
  });

  it('shows the reason the server refused to start one', async () => {
    const w = newWorld({
      override: (c) =>
        c.pathname.endsWith('/translate')
          ? json({ title: 'Conflict', status: 409, detail: 'AI is off for this organisation.' }, 409)
          : undefined,
    });
    renderMetadata(w);
    await userEvent.click(await translateButton());
    expect(await screen.findByText('Could not start the translation: AI is off for this organisation.')).toBeInTheDocument();
  });

  it('keeps checking while the job runs and says when it is done', async () => {
    const w = newWorld({ nextJob: job({ status: 'running', finishedAt: null, items: [] }) });
    renderMetadata(w);
    await userEvent.click(await translateButton());
    expect(await screen.findByText('Translating into Hindi…')).toBeInTheDocument();
    // The next look at the job finds it finished.
    w.jobs = [job()];
    expect(
      await screen.findByText('The Hindi translation is ready to review.', undefined, { timeout: 6000 }),
    ).toBeInTheDocument();
  }, 15_000);
});

describe('AI suggestion in the target pane', () => {
  const pending = () => newWorld({ ai: aiDto({ suggestions: [translation('s1', 'headline', 'hi', 'वसंत आ गया है')] }) });

  it('shows the suggested text, where it came from and its size', async () => {
    renderMetadata(pending());
    const card = await screen.findByRole('group', { name: 'AI suggestion' });
    expect(within(card).getByText('वसंत आ गया है')).toHaveAttribute('lang', 'hi');
    expect(within(card).getByText(/Translated from English · 13 characters · chat-model/)).toBeInTheDocument();
    expect(within(card).queryByText(/Glossary terms were kept/)).not.toBeInTheDocument();
    expect(within(card).queryByRole('note')).not.toBeInTheDocument();
    // It sits in the target pane, not the source's.
    expect(card.closest('.dam-ml__pane')).toHaveAttribute('data-role', 'target');
  });

  it('only shows the suggestion for the language and field being looked at', async () => {
    const w = newWorld({
      ai: aiDto({
        suggestions: [
          translation('s1', 'headline', 'ta', 'தமிழ்'),
          translation('s2', 'blurb', 'hi', 'ब्लर्ब'),
          translation('s3', 'headline', 'hi', 'rejected one', { status: 'rejected' }),
        ],
      }),
    });
    renderMetadata(w);
    await headline();
    await new Promise((r) => setTimeout(r, 100));
    expect(screen.queryByRole('group', { name: 'AI suggestion' })).toBeInTheDocument(); // blurb / hindi
    expect(screen.queryByText('தமிழ்')).not.toBeInTheDocument();
    expect(screen.queryByText('rejected one')).not.toBeInTheDocument();
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Target language of Headline' }), 'ta');
    expect(await screen.findByText('தமிழ்')).toBeInTheDocument();
  });

  it('warns when the source text has changed since', async () => {
    const w = newWorld({ ai: aiDto({ suggestions: [translation('s1', 'headline', 'hi', 'वसंत', { sourceStale: true })] }) });
    renderMetadata(w);
    const card = await screen.findByRole('group', { name: 'AI suggestion' });
    expect(within(card).getByRole('note')).toHaveTextContent('The English text has changed since this was suggested');
  });

  it('says glossary terms were protected when the glossary has any', async () => {
    const w = pending();
    w.glossaryHasTerms = true;
    renderMetadata(w);
    const card = await screen.findByRole('group', { name: 'AI suggestion' });
    expect(await within(card).findByText('Glossary terms were kept exactly as defined.')).toBeInTheDocument();
  });

  it('accepts: the text lands in the target box, the card goes away, nothing is sent but the decision', async () => {
    const w = pending();
    renderMetadata(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Accept the Hindi translation of Headline' }));
    await waitFor(() => expect(acceptPosts(w)).toHaveLength(1));
    expect(acceptPosts(w)[0]!.pathname).toBe('/api/v1/assets/a1/ai/suggestions/s1/accept');
    await waitFor(() => expect(hindi()).toHaveValue('वसंत आ गया है'));
    expect(screen.queryByRole('group', { name: 'AI suggestion' })).not.toBeInTheDocument();
    expect(await screen.findByText('Added the Hindi translation of Headline')).toBeInTheDocument();
    // The server holds it now: nothing is left unsaved.
    expect(screen.queryByText('You have unsaved changes')).not.toBeInTheDocument();
    // Focus lands in the box the text went into.
    await waitFor(() => expect(hindi()).toHaveFocus());
  });

  it('does not undo unsaved edits to other fields, and saving keeps the accepted translation', async () => {
    const w = pending();
    renderMetadata(w);
    await userEvent.type(await screen.findByRole('textbox', { name: 'Credit' }), 'Acme Studio');
    expect(await screen.findByText('You have unsaved changes')).toBeInTheDocument();

    await userEvent.click(await screen.findByRole('button', { name: 'Accept the Hindi translation of Headline' }));
    await waitFor(() => expect(hindi()).toHaveValue('वसंत आ गया है'));
    expect(screen.getByRole('textbox', { name: 'Credit' })).toHaveValue('Acme Studio');
    expect(screen.getByText('You have unsaved changes')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(w.calls.filter((c) => c.method === 'PATCH')).toHaveLength(1));
    const sent = (await w.calls.find((c) => c.method === 'PATCH')!.json()) as { metadata: Record<string, unknown> };
    expect(sent.metadata).toEqual({ headline: { en: 'Spring is here', hi: 'वसंत आ गया है' }, credit: 'Acme Studio' });
  });

  it('keeps an unsaved edit to the same field in another language when a translation is accepted', async () => {
    const w = pending();
    renderMetadata(w);
    await userEvent.type(await headline(), '!');
    await userEvent.click(await screen.findByRole('button', { name: 'Accept the Hindi translation of Headline' }));
    await waitFor(() => expect(hindi()).toHaveValue('वसंत आ गया है'));
    expect(await headline()).toHaveValue('Spring is here!');
  });

  it('asks before replacing text the target already has, then retries with replace', async () => {
    const w = pending();
    w.asset = assetDto({ headline: { en: 'Spring is here', hi: 'पुराना पाठ' } });
    renderMetadata(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Accept the Hindi translation of Headline' }));
    const confirm = await screen.findByRole('alert');
    expect(confirm).toHaveTextContent('Hindi already has text');
    expect(confirm).toHaveTextContent('Headline already has Hindi text. Accepting replaces it with this suggestion.');
    expect(acceptPosts(w)).toHaveLength(1);
    expect(hindi()).toHaveValue('पुराना पाठ');

    await userEvent.click(within(confirm).getByRole('button', { name: 'Replace the text' }));
    await waitFor(() => expect(acceptPosts(w)).toHaveLength(2));
    expect(await acceptPosts(w)[1]!.json()).toEqual({ replace: true });
    await waitFor(() => expect(hindi()).toHaveValue('वसंत आ गया है'));
    expect(screen.queryByRole('group', { name: 'AI suggestion' })).not.toBeInTheDocument();
  });

  it('keeps the current text when the person says so, without a second request', async () => {
    const w = pending();
    w.asset = assetDto({ headline: { en: 'Spring is here', hi: 'पुराना पाठ' } });
    renderMetadata(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Accept the Hindi translation of Headline' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Keep the current text' }));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Accept the Hindi translation of Headline' })).toBeInTheDocument();
    expect(acceptPosts(w)).toHaveLength(1);
    expect(hindi()).toHaveValue('पुराना पाठ');
  });

  it('lets the person edit before accepting, and sends their text', async () => {
    const w = pending();
    renderMetadata(w);
    await userEvent.click(
      await screen.findByRole('button', { name: 'Edit the Hindi suggestion for Headline before accepting it' }),
    );
    const box = screen.getByRole('textbox', { name: 'Edited Hindi translation of Headline' });
    expect(box).toHaveValue('वसंत आ गया है');
    await userEvent.clear(box);
    await userEvent.type(box, 'वसंत ऋतु');
    await userEvent.click(screen.getByRole('button', { name: 'Accept the edited Hindi translation of Headline' }));
    await waitFor(() => expect(acceptPosts(w)).toHaveLength(1));
    expect(await acceptPosts(w)[0]!.json()).toEqual({ text: 'वसंत ऋतु' });
    await waitFor(() => expect(hindi()).toHaveValue('वसंत ऋतु'));
  });

  it('cannot accept an emptied edit, and cancel restores the suggestion', async () => {
    renderMetadata(pending());
    await userEvent.click(await screen.findByRole('button', { name: /Edit the Hindi suggestion/ }));
    await userEvent.clear(screen.getByRole('textbox', { name: 'Edited Hindi translation of Headline' }));
    expect(screen.getByRole('button', { name: 'Accept the edited Hindi translation of Headline' })).toBeDisabled();
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.getByText('वसंत आ गया है')).toBeInTheDocument();
  });

  it('rejects without touching the field', async () => {
    const w = pending();
    renderMetadata(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Reject the Hindi translation of Headline' }));
    await waitFor(() => expect(posts(w, '/reject')).toHaveLength(1));
    await waitFor(() => expect(screen.queryByRole('group', { name: 'AI suggestion' })).not.toBeInTheDocument());
    expect(hindi()).toHaveValue('');
    expect(await screen.findByText('Suggestion dismissed')).toBeInTheDocument();
  });

  it('says so when the decision fails, and keeps the card', async () => {
    const w = pending();
    w.override = (c) =>
      c.pathname.endsWith('/accept') ? json({ title: 'Boom', status: 500, detail: 'The store is unavailable.' }, 500) : undefined;
    renderMetadata(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Accept the Hindi translation of Headline' }));
    expect(await screen.findByText('Could not save your decision: The store is unavailable.')).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'AI suggestion' })).toBeInTheDocument();
  });

  it('says someone else decided it when a 409 arrives for a suggestion that is no longer pending', async () => {
    const w = pending();
    w.override = (c) => {
      if (!c.pathname.endsWith('/accept')) return undefined;
      w.ai.suggestions[0]!.status = 'accepted';
      return json({ title: 'Conflict', status: 409 }, 409);
    };
    renderMetadata(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Accept the Hindi translation of Headline' }));
    await waitFor(() => expect(screen.queryByRole('group', { name: 'AI suggestion' })).not.toBeInTheDocument());
  });
});

describe('Translate dialog', () => {
  const asset = assetDto();
  const open = async () => {
    await userEvent.click(await screen.findByRole('button', { name: 'Translate…' }));
    return screen.findByRole('dialog');
  };

  it('is not offered to a reader, or for a deleted asset', async () => {
    renderUi(newWorld(), <TranslateAssetButton asset={asset} />, { permissions: READER });
    await new Promise((r) => setTimeout(r, 100));
    expect(screen.queryByRole('button', { name: 'Translate…' })).not.toBeInTheDocument();
    cleanup();
    renderUi(newWorld(), <TranslateAssetButton asset={assetDto({}, { status: 'deleted' })} />);
    await new Promise((r) => setTimeout(r, 100));
    expect(screen.queryByRole('button', { name: 'Translate…' })).not.toBeInTheDocument();
  });

  it('is disabled with an explanation when the provider cannot translate, and links admins to settings', async () => {
    renderUi(newWorld({ caps: caps({ translate: false }) }), <TranslateAssetButton asset={asset} />, { permissions: ADMIN });
    const button = await screen.findByRole('button', { name: 'Translate…' });
    expect(button).toBeDisabled();
    expect(button).toHaveAccessibleDescription(/cannot translate text/);
    expect(screen.getByRole('link', { name: 'AI settings' })).toHaveAttribute('href', '/admin/ai');
  });

  it('is disabled with an explanation when AI is off', async () => {
    renderUi(newWorld({ caps: caps({ enabled: false, translate: false }) }), <TranslateAssetButton asset={asset} />);
    const button = await screen.findByRole('button', { name: 'Translate…' });
    expect(button).toBeDisabled();
    expect(button).toHaveAccessibleDescription(/AI is off for your organisation/);
  });

  it('offers every multilingual field and no other kind, with all of them chosen', async () => {
    renderUi(newWorld(), <TranslateAssetButton asset={asset} />);
    const dialog = await open();
    const fields = within(await within(dialog).findByRole('group', { name: 'Fields to translate' }));
    expect(fields.getAllByRole('checkbox').map((c) => (c as HTMLInputElement).labels?.[0]?.textContent)).toEqual([
      'Headline',
      'Blurb',
    ]);
    expect(fields.getAllByRole('checkbox').every((c) => (c as HTMLInputElement).checked)).toBe(true);
    expect(within(dialog).getByRole('combobox', { name: 'Translate from' })).toHaveValue('');
  });

  it('asks for at least one field and one language before anything is sent', async () => {
    const w = newWorld();
    renderUi(w, <TranslateAssetButton asset={asset} />);
    const dialog = await open();
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Translate' }));
    expect(within(dialog).getByText('Choose at least one language.')).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Headline' }));
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Blurb' }));
    expect(within(dialog).getByText('Choose at least one field.')).toBeInTheDocument();
    expect(posts(w, '/translate')).toHaveLength(0);
  });

  it('leaves the source language out of the targets', async () => {
    renderUi(newWorld(), <TranslateAssetButton asset={asset} />);
    const dialog = await open();
    const targets = () => within(within(dialog).getByRole('group', { name: 'Translate into' })).getAllByRole('checkbox').length;
    expect(targets()).toBe(5);
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: 'Translate from' }), 'en');
    expect(targets()).toBe(4);
    expect(within(dialog).queryByRole('checkbox', { name: 'English (en)' })).not.toBeInTheDocument();
  });

  it('sends the chosen fields, languages, source and overwrite flag, then lists each result with its reason', async () => {
    const w = newWorld({
      nextJob: job({
        status: 'partial',
        items: [
          { field: 'headline', language: 'hi', status: 'ok', reason: null },
          { field: 'headline', language: 'mr', status: 'skipped', reason: 'Marathi already has text' },
          { field: 'blurb', language: 'hi', status: 'failed', reason: 'the provider changed a protected term' },
        ],
      }),
    });
    renderUi(w, <TranslateAssetButton asset={asset} />);
    const dialog = await open();
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Blurb' }));
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: 'Translate from' }), 'en');
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Hindi (hi)' }));
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Marathi (mr)' }));
    await userEvent.click(within(dialog).getByRole('checkbox', { name: /Overwrite existing text/ }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Translate' }));

    await waitFor(() => expect(posts(w, '/translate')).toHaveLength(1));
    expect(await posts(w, '/translate')[0]!.json()).toEqual({
      fields: ['headline'],
      targetLanguages: ['hi', 'mr'],
      sourceLanguage: 'en',
      overwrite: true,
    });

    const results = await within(dialog).findByRole('list', { name: 'Results by field and language' });
    const rows = within(results).getAllByRole('listitem');
    expect(rows).toHaveLength(3);
    expect(rows[0]).toHaveTextContent('Headline → Hindi');
    expect(rows[0]).toHaveTextContent('Translated');
    expect(rows[1]).toHaveTextContent('Headline → Marathi');
    expect(rows[1]).toHaveTextContent('Skipped');
    expect(rows[1]).toHaveTextContent('Marathi already has text');
    expect(rows[2]).toHaveTextContent('Blurb → Hindi');
    expect(rows[2]).toHaveTextContent('Failed');
    expect(rows[2]).toHaveTextContent('the provider changed a protected term');
    expect(within(dialog).getByRole('status')).toHaveTextContent(
      'Finished with some problems: 1 translations are ready to review.',
    );
    expect(within(dialog).getByRole('link', { name: 'Review the suggestions' })).toHaveAttribute(
      'href',
      '/library/assets/a1?focus=translations',
    );
  });

  it('sends no source language when left on automatic, and no overwrite by default', async () => {
    const w = newWorld();
    renderUi(w, <TranslateAssetButton asset={asset} />);
    const dialog = await open();
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Tamil (ta)' }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Translate' }));
    await waitFor(() => expect(posts(w, '/translate')).toHaveLength(1));
    expect(await posts(w, '/translate')[0]!.json()).toEqual({
      fields: ['headline', 'blurb'],
      targetLanguages: ['ta'],
      sourceLanguage: null,
      overwrite: false,
    });
  });

  it('shows progress while the job runs and the results when it finishes', async () => {
    const w = newWorld({ nextJob: job({ status: 'running', finishedAt: null, items: [] }) });
    renderUi(w, <TranslateAssetButton asset={asset} />);
    const dialog = await open();
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Hindi (hi)' }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Translate' }));
    expect(await within(dialog).findByText('Translating')).toBeInTheDocument();
    w.jobs = [job()];
    expect(
      await within(dialog).findByText(/Finished: 1 translations are ready to review/, undefined, { timeout: 6000 }),
    ).toBeInTheDocument();
  }, 15_000);

  it('shows the server reason when the request is refused', async () => {
    const w = newWorld({
      override: (c) =>
        c.pathname.endsWith('/translate')
          ? json({ title: 'Conflict', status: 409, detail: 'The provider cannot translate.' }, 409)
          : undefined,
    });
    renderUi(w, <TranslateAssetButton asset={asset} />);
    const dialog = await open();
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Hindi (hi)' }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Translate' }));
    expect(
      await within(dialog).findByText('Could not start the translation: The provider cannot translate.'),
    ).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    renderUi(newWorld(), <TranslateAssetButton asset={asset} />);
    await open();
    expect(await axe(document.body)).toHaveNoViolations();
  });
});

describe('Hindi', () => {
  it('renders the editor, the suggestion card and the dialog in Hindi', async () => {
    setLanguage('hi');
    const w = newWorld({
      ai: aiDto({ suggestions: [translation('s1', 'headline', 'hi', 'वसंत आ गया है', { sourceStale: true })] }),
    });
    renderMetadata(w);
    expect((await screen.findAllByText('स्रोत'))[0]).toBeInTheDocument();
    expect((await screen.findAllByText('लक्ष्य'))[0]).toBeInTheDocument();
    expect((await screen.findAllByRole('button', { name: /AI से अनुवाद करें/ }))[0]).toBeInTheDocument();
    const card = await screen.findByRole('group', { name: 'AI सुझाव' });
    expect(within(card).getByRole('button', { name: /^Headline का .* अनुवाद स्वीकार करें$/ })).toBeInTheDocument();
    expect(within(card).getByRole('note')).toHaveTextContent('बदल गया है');
    await userEvent.click(await screen.findByRole('button', { name: 'अनुवाद करें…' }));
    expect(await screen.findByText('अनुवाद के लिए फ़ील्ड')).toBeInTheDocument();
    // Language names follow the console language too.
    expect(screen.getAllByText(/हिन्दी \(hi\)/).length).toBeGreaterThan(0);
  });
});

describe('a tag suggestion is not mistaken for a translation', () => {
  it('shows no translation card for tags', async () => {
    renderMetadata(newWorld({ ai: aiDto({ suggestions: [tag('t1', 'sunset')] }) }));
    await headline();
    await new Promise((r) => setTimeout(r, 100));
    expect(screen.queryByRole('group', { name: 'AI suggestion' })).not.toBeInTheDocument();
  });
});

describe('arriving from the reviewer inbox', () => {
  it('puts focus on the suggestion beside its field on the Metadata tab', async () => {
    const w = newWorld({ ai: aiDto({ suggestions: [translation('s1', 'headline', 'hi', 'वसंत आ गया है')] }) });
    renderMetadata(w, { route: '/library/assets/a1?tab=metadata&focus=translations' });
    const card = await screen.findByRole('group', { name: 'AI suggestion' });
    await waitFor(() => expect(card).toHaveFocus());
  });

  it('links to Details for kinds of file the AI panel covers, and to Metadata for the rest', () => {
    expect(reviewHref('a1', 'image')).toBe('/library/assets/a1?focus=translations');
    expect(reviewHref('a1', 'archive')).toBe('/library/assets/a1?tab=metadata&focus=translations');
  });
});

describe('on the asset page', () => {
  it('offers Translate… and the side-by-side editor on the Metadata tab, and the suggestion in the AI panel on Details', async () => {
    const w = newWorld({
      asset: assetDto({ headline: { en: 'Spring is here' } }, {}),
      ai: aiDto({ suggestions: [translation('s1', 'headline', 'hi', 'वसंत आ गया है')] }),
    });
    mockApi(backend(w), { permissions: EDITOR });
    renderAt('/library/assets/a1?tab=metadata');
    expect(await screen.findByRole('button', { name: 'Translate…' })).toBeEnabled();
    expect(await screen.findByRole('textbox', { name: 'Headline - English' })).toHaveValue('Spring is here');
    expect(await screen.findByRole('group', { name: 'AI suggestion' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('tab', { name: 'Details' }));
    // The AI panel is for kinds of file the AI reads; this image is one.
    expect(await screen.findByRole('region', { name: 'Translation suggestions' })).toBeInTheDocument();
  });
});
