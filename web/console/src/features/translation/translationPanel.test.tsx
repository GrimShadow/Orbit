import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { queryClient } from '../../App';
import { setLanguage } from '../../i18n';
import { READER, aiDto, assetDto, caps, json, newWorld, posts, renderPanel, tag, translation } from './translationTestKit';

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const panel = () => screen.findByRole('region', { name: 'AI insights' });
const SUMMARY = {
  text: 'The Acme brochure sets out the spring range and its prices.',
  language: 'en',
  model: 'extractive-v1',
  createdAt: '2026-01-02T10:05:00Z',
};
const withText = (over: Record<string, unknown> = {}) => aiDto({ ocrText: 'Acme spring range. Prices inside.', ...over });
const doc = () => assetDto({}, { type: 'document' });

describe('AI panel: summary', () => {
  it('shows the summary labelled as AI-generated, with its language, model and age', async () => {
    const w = newWorld({ ai: withText({ summary: SUMMARY }) });
    renderPanel(w, { asset: doc() });
    const card = await panel();
    const section = within(card).getByRole('region', { name: 'AI-generated summary' });
    expect(within(section).getByText(SUMMARY.text)).toHaveAttribute('lang', 'en');
    expect(within(section).getByText(/Language: English · Model: extractive-v1 ·/)).toBeInTheDocument();
    expect(section.querySelector('time')).toHaveAttribute('datetime', SUMMARY.createdAt);
  });

  it('offers to summarise text that has been read but not yet summarised', async () => {
    const w = newWorld({ ai: withText() });
    renderPanel(w, { asset: doc() });
    const card = await panel();
    expect(within(card).getByText(/No summary yet/)).toBeInTheDocument();
    expect(within(card).getByRole('button', { name: 'Summarise' })).toBeEnabled();
  });

  it('asks for a summary, tells the person it is being written, and shows it when it arrives', async () => {
    const w = newWorld({
      ai: withText({ summary: null }),
      override: (c) => (c.pathname.endsWith('/ai/summarise') ? json({ accepted: true }, 202) : undefined),
    });
    renderPanel(w, { asset: doc() });
    await userEvent.click(await screen.findByRole('button', { name: 'Summarise' }));
    await waitFor(() => expect(posts(w, '/ai/summarise')).toHaveLength(1));
    expect(await screen.findByText('Writing the summary…')).toBeInTheDocument();
    expect(await screen.findByText('Summary requested')).toBeInTheDocument();
    w.ai = withText({ summary: SUMMARY });
    expect(await screen.findByText(SUMMARY.text, undefined, { timeout: 6000 })).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText('Writing the summary…')).not.toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Summarise again' })).toBeInTheDocument();
  }, 15_000);

  it('summarising again waits for a newer summary, not the one already shown', async () => {
    const w = newWorld({
      ai: withText({ summary: SUMMARY }),
      override: (c) => (c.pathname.endsWith('/ai/summarise') ? json({ accepted: true }, 202) : undefined),
    });
    renderPanel(w, { asset: doc() });
    await userEvent.click(await screen.findByRole('button', { name: 'Summarise again' }));
    expect(await screen.findByText('Writing the summary…')).toBeInTheDocument();
    expect(screen.getByText(SUMMARY.text)).toBeInTheDocument();
    w.ai = withText({ summary: { ...SUMMARY, text: 'A shorter summary.', createdAt: '2026-01-02T10:30:00Z' } });
    expect(await screen.findByText('A shorter summary.', undefined, { timeout: 6000 })).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText('Writing the summary…')).not.toBeInTheDocument());
  }, 15_000);

  it("shows the server's reason when a summary cannot be made", async () => {
    const w = newWorld({
      ai: withText(),
      override: (c) =>
        c.pathname.endsWith('/ai/summarise')
          ? json({ title: 'Conflict', status: 409, detail: 'The asset has no extracted text.' }, 409)
          : undefined,
    });
    renderPanel(w, { asset: doc() });
    await userEvent.click(await screen.findByRole('button', { name: 'Summarise' }));
    expect(await screen.findByText('Could not request a summary: The asset has no extracted text.')).toBeInTheDocument();
    expect(screen.queryByText('Writing the summary…')).not.toBeInTheDocument();
  });

  it('shows no button when the provider cannot summarise, there is no text yet, or the person cannot edit', async () => {
    renderPanel(newWorld({ ai: withText(), caps: caps({ summarise: false }) }), { asset: doc() });
    await panel();
    expect(screen.queryByRole('button', { name: /Summarise/ })).not.toBeInTheDocument();
    cleanup();
    renderPanel(newWorld({ ai: aiDto() }), { asset: doc() });
    await panel();
    expect(screen.queryByRole('button', { name: /Summarise/ })).not.toBeInTheDocument();
    cleanup();
    renderPanel(newWorld({ ai: withText({ summary: SUMMARY }) }), { asset: doc(), permissions: READER });
    const card = await panel();
    expect(within(card).getByText(SUMMARY.text)).toBeInTheDocument();
    expect(within(card).queryByRole('button', { name: /Summarise/ })).not.toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    renderPanel(newWorld({ ai: withText({ summary: SUMMARY }) }), { asset: doc() });
    const card = await panel();
    await within(card).findByText(SUMMARY.text);
    expect(await axe(card)).toHaveNoViolations();
  });
});

describe('AI panel: translation suggestions', () => {
  const suggestions = () => [
    translation('s1', 'headline', 'hi', 'वसंत आ गया है'),
    translation('s2', 'blurb', 'ta', 'வசந்தம்', { sourceStale: true }),
    tag('t1', 'sunset'),
  ];

  it('lists translations under their own heading, field and language first, tags apart', async () => {
    renderPanel(newWorld({ ai: aiDto({ suggestions: suggestions() }) }));
    const card = await panel();
    const section = await within(card).findByRole('region', { name: 'Translation suggestions' });
    const cards = within(section).getAllByRole('group');
    expect(cards.map((c) => c.querySelector('p')!.textContent)).toEqual([
      'Headline · HindiAI suggestion',
      'Blurb · TamilAI suggestion',
    ]);
    expect(within(cards[0]!).getByText('वसंत आ गया है')).toBeInTheDocument();
    expect(within(cards[1]!).getByRole('note')).toHaveTextContent('has changed since');
    expect(within(card).getByText('sunset')).toBeInTheDocument();
    expect(within(section).queryByText('sunset')).not.toBeInTheDocument();
  });

  it('accepts one from the panel and the card leaves the list', async () => {
    const w = newWorld({ ai: aiDto({ suggestions: suggestions() }) });
    renderPanel(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Accept the Hindi translation of Headline' }));
    await waitFor(() => expect(posts(w, '/accept')).toHaveLength(1));
    await waitFor(() => expect(screen.queryByText('वसंत आ गया है')).not.toBeInTheDocument());
    // The other is still there, and the heading takes focus so the keyboard user is not dropped.
    expect(screen.getByText('வசந்தம்')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Translation suggestions' })).toHaveFocus());
  });

  it('asks before replacing text, from the panel too', async () => {
    const w = newWorld({ ai: aiDto({ suggestions: suggestions() }), asset: assetDto({ headline: { hi: 'पुराना' } }) });
    renderPanel(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Accept the Hindi translation of Headline' }));
    expect(await screen.findByText('Hindi already has text')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Replace the text' }));
    await waitFor(() => expect(posts(w, '/accept')).toHaveLength(2));
    expect(await posts(w, '/accept')[1]!.json()).toEqual({ replace: true });
  });

  it('shows a reader the suggestions without buttons', async () => {
    renderPanel(newWorld({ ai: aiDto({ suggestions: suggestions() }) }), { permissions: READER });
    const card = await panel();
    expect(await within(card).findByText('वसंत आ गया है')).toBeInTheDocument();
    expect(within(card).queryByRole('button', { name: /Accept the Hindi/ })).not.toBeInTheDocument();
    expect(within(card).getByText(/An editor accepts or rejects each one/)).toBeInTheDocument();
  });

  it('puts keyboard focus on the translations when arriving from the reviewer inbox', async () => {
    renderPanel(newWorld({ ai: aiDto({ suggestions: suggestions() }) }), { route: '/library/assets/a1?focus=translations' });
    await screen.findByText('वसंत आ गया है');
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Translation suggestions' })).toHaveFocus());
  });

  it('lists decided translations in the folded history, by field and language', async () => {
    const w = newWorld({ ai: aiDto({ suggestions: [translation('s1', 'headline', 'hi', 'x', { status: 'accepted' })] }) });
    renderPanel(w);
    await userEvent.click(await screen.findByRole('button', { name: /Decided \(1\)/ }));
    expect(screen.getByText('headline · Hindi')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Translation suggestions' })).not.toBeInTheDocument();
  });

  it('offers the Translate… dialog from the panel to an editor, not a reader', async () => {
    renderPanel(newWorld({ ai: aiDto() }));
    expect(await screen.findByRole('button', { name: 'Translate…' })).toBeEnabled();
    cleanup();
    renderPanel(newWorld({ ai: aiDto() }), { permissions: READER });
    await panel();
    expect(screen.queryByRole('button', { name: 'Translate…' })).not.toBeInTheDocument();
  });

  it('renders in Hindi', async () => {
    setLanguage('hi');
    renderPanel(newWorld({ ai: withText({ summary: SUMMARY, suggestions: suggestions() }) }), { asset: doc() });
    expect(await screen.findByRole('region', { name: 'AI द्वारा बनाया गया सारांश' })).toBeInTheDocument();
    expect(await screen.findByRole('region', { name: 'अनुवाद के सुझाव' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'फिर से सारांश बनाएँ' })).toBeInTheDocument();
  });
});
