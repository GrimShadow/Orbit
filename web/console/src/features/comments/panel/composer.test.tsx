import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClientProvider } from '@tanstack/react-query';
import { useState } from 'react';
import { axe } from 'vitest-axe';
import { AuthContext } from '../../../auth/AuthProvider';
import { setLanguage } from '../../../i18n';
import { json, mockApi, signedIn } from '../../workflow/workflowTestKit';
import { PEOPLE, PRIYA, SAM, newQueryClient } from './commentsTestKit';
import { Composer, type ComposerProps } from './Composer';

beforeEach(() => setLanguage('en'));
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function mentionableCalls(extra?: (q: string) => unknown[] | undefined) {
  return mockApi((c) => {
    if (c.pathname === '/api/v1/assets/a1/mentionable') {
      const q = (c.search.get('q') ?? '').toLowerCase();
      return json(extra?.(q) ?? PEOPLE.filter((p) => p.displayName.toLowerCase().includes(q) || p.email.includes(q)));
    }
    return undefined;
  });
}

function Harness(props: Partial<ComposerProps> & { sink: (body: string) => void }) {
  const { sink, ...rest } = props;
  const [, force] = useState(0);
  return (
    <AuthContext.Provider value={signedIn}>
      <QueryClientProvider client={newQueryClient()}>
        <Composer
          assetId="a1"
          label="Add a comment"
          submitLabel="Post"
          onSubmit={async (b) => {
            sink(b);
            force((n) => n + 1);
          }}
          {...rest}
        />
      </QueryClientProvider>
    </AuthContext.Provider>
  );
}

const token = (name: string, id: string) => `@[${name}](user:${id})`;

describe('Composer @mentions', () => {
  it('suggests people after @ and letters, and fills the choice in with the arrow keys and Enter', async () => {
    const calls = mentionableCalls();
    const sink = vi.fn();
    render(<Harness sink={sink} />);
    const user = userEvent.setup();
    const box = screen.getByRole('textbox', { name: 'Add a comment' });
    await user.type(box, 'Hi @a');
    const list = await screen.findByRole('listbox', { name: 'People you can mention' });
    expect(
      within(list)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['Priya Natarajanpriya@acme.example', 'Sam Reviewersam@acme.example']);
    // "a" matches both people; pick the second with ArrowDown.
    expect(box).toHaveAttribute('aria-controls', list.id);
    expect(box).toHaveAttribute('aria-activedescendant', within(list).getAllByRole('option')[0]!.id);
    await user.keyboard('{ArrowDown}');
    expect(within(list).getAllByRole('option')[1]).toHaveAttribute('aria-selected', 'true');
    await user.keyboard('{Enter}');
    expect(box).toHaveValue('Hi @Sam Reviewer ');
    expect(screen.queryByRole('listbox')).toBeNull();
    expect(calls.some((c) => c.pathname.endsWith('/mentionable') && c.search.get('q') === 'a')).toBe(true);

    await user.type(box, 'please look');
    await user.keyboard('{Control>}{Enter}{/Control}');
    await waitFor(() => expect(sink).toHaveBeenCalledWith(`Hi ${token('Sam Reviewer', SAM)} please look`));
  });

  it('wraps the choice in a token for each mention, in order', async () => {
    mentionableCalls();
    const sink = vi.fn();
    render(<Harness sink={sink} />);
    const user = userEvent.setup();
    const box = screen.getByRole('textbox', { name: 'Add a comment' });
    await user.type(box, '@pri');
    await screen.findByRole('listbox');
    await user.keyboard('{Tab}');
    await user.type(box, 'and @sam');
    await screen.findByRole('listbox');
    await user.click(screen.getByRole('option', { name: /Sam Reviewer/ }));
    await user.click(screen.getByRole('button', { name: 'Post' }));
    await waitFor(() =>
      expect(sink).toHaveBeenCalledWith(`${token('Priya Natarajan', PRIYA)} and ${token('Sam Reviewer', SAM)}`),
    );
  });

  it('closes the suggestions with Escape without losing the text, and does not cancel the box', async () => {
    mentionableCalls();
    const cancel = vi.fn();
    render(<Harness sink={vi.fn()} onCancel={cancel} />);
    const user = userEvent.setup();
    const box = screen.getByRole('textbox', { name: 'Add a comment' });
    await user.type(box, 'Hello @pri');
    await screen.findByRole('listbox');
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('listbox')).toBeNull();
    expect(box).toHaveValue('Hello @pri');
    expect(cancel).not.toHaveBeenCalled();
    await user.keyboard('{Escape}');
    expect(cancel).toHaveBeenCalledTimes(1);
  });

  it('turns a mention back into plain text when its name is edited', async () => {
    mentionableCalls();
    const sink = vi.fn();
    render(<Harness sink={sink} />);
    const user = userEvent.setup();
    const box = screen.getByRole('textbox', { name: 'Add a comment' });
    await user.type(box, '@pri');
    await screen.findByRole('listbox');
    await user.keyboard('{Enter}');
    expect(box).toHaveValue('@Priya Natarajan ');
    await user.keyboard('{ArrowLeft}{ArrowLeft}{Backspace}'); // inside the name
    await user.click(screen.getByRole('button', { name: 'Post' }));
    await waitFor(() => expect(sink).toHaveBeenCalledTimes(1));
    expect(sink.mock.calls[0]![0]).toBe('@Priya Natarajn');
    expect(sink.mock.calls[0]![0]).not.toContain('user:');
  });

  it('does not offer suggestions in the middle of an email address, and ignores a lone @', async () => {
    const calls = mentionableCalls();
    render(<Harness sink={vi.fn()} />);
    const user = userEvent.setup();
    const box = screen.getByRole('textbox', { name: 'Add a comment' });
    await user.type(box, 'write to bob@acme and @');
    await new Promise((r) => setTimeout(r, 250));
    expect(screen.queryByRole('listbox')).toBeNull();
    expect(calls.filter((c) => c.pathname.endsWith('/mentionable'))).toHaveLength(0);
  });

  it('shows no list when nobody matches', async () => {
    mentionableCalls(() => []);
    render(<Harness sink={vi.fn()} />);
    const user = userEvent.setup();
    await user.type(screen.getByRole('textbox', { name: 'Add a comment' }), '@zzz');
    await new Promise((r) => setTimeout(r, 300));
    expect(screen.queryByRole('listbox')).toBeNull();
  });

  it('announces how many people are available', async () => {
    mentionableCalls();
    render(<Harness sink={vi.fn()} />);
    await userEvent.setup().type(screen.getByRole('textbox', { name: 'Add a comment' }), '@sam');
    await screen.findByRole('listbox');
    expect(screen.getByText('1 person available')).toBeInTheDocument();
  });
});

describe('Composer', () => {
  it('starts an edit from a stored body (tokens shown as @Name) and sends tokens back', async () => {
    mentionableCalls();
    const sink = vi.fn();
    render(<Harness sink={sink} initialBody={`Ping ${token('Sam Reviewer', SAM)} ok`} requireChange submitLabel="Save" />);
    const user = userEvent.setup();
    const box = screen.getByRole('textbox', { name: 'Add a comment' });
    expect(box).toHaveValue('Ping @Sam Reviewer ok');
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    await user.type(box, '?');
    await user.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(sink).toHaveBeenCalledWith(`Ping ${token('Sam Reviewer', SAM)} ok?`));
  });

  it('draws the mention highlight behind the text, and not for text that only looks like one', async () => {
    mentionableCalls();
    const { container } = render(<Harness sink={vi.fn()} />);
    const user = userEvent.setup();
    const box = screen.getByRole('textbox', { name: 'Add a comment' });
    await user.type(box, 'hey @pri');
    await screen.findByRole('listbox');
    await user.keyboard('{Enter}');
    const marks = container.querySelectorAll('.dam-composer__mention');
    expect(marks).toHaveLength(1);
    expect(marks[0]).toHaveTextContent('@Priya Natarajan');
    expect(container.querySelector('.dam-composer__mirror')).toHaveAttribute('aria-hidden', 'true');
  });

  it('has no axe violations while suggestions are open', async () => {
    mentionableCalls();
    const { container } = render(<Harness sink={vi.fn()} />);
    await userEvent.setup().type(screen.getByRole('textbox', { name: 'Add a comment' }), 'Hi @sa');
    await screen.findByRole('listbox');
    expect(await axe(container)).toHaveNoViolations();
  });

  it('has no axe violations with an error and a draft anchor', async () => {
    mentionableCalls();
    const { container } = render(
      <Harness
        sink={vi.fn()}
        anchor={{ type: 'region', x: 0.1, y: 0.1, w: 0.2, h: 0.2, page: 2 }}
        onClearAnchor={() => undefined}
        onSubmit={async () => {
          throw new Error('Nope');
        }}
      />,
    );
    const user = userEvent.setup();
    await user.type(screen.getByRole('textbox', { name: 'Add a comment' }), 'x');
    await user.click(screen.getByRole('button', { name: 'Post' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Nope');
    expect(await axe(container)).toHaveNoViolations();
  });
});
