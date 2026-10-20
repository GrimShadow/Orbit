import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { setLanguage } from '../../../i18n';
import { json, mockApi } from '../../workflow/workflowTestKit';
import { ME, SAM, ago, comment, region, renderPanel, serveComments, thread, timecode } from './commentsTestKit';
import { CommentsTab } from './CommentsTab';

beforeEach(() => setLanguage('en'));
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const WRITE = ['assets.read', 'comments.write'];

/** Mounts the Comments tab over a fake backend; returns the backend, recorded calls and the per-test spies. */
function setup(
  threads: ReturnType<typeof thread>[],
  opts: { permissions?: string[]; route?: string; versionId?: string | null; more?: Parameters<typeof serveComments>[1] } = {},
) {
  const { be, handler } = serveComments(threads, opts.more);
  const calls = mockApi(handler, { permissions: opts.permissions ?? WRITE });
  const view = renderPanel(<CommentsTab assetId="a1" />, { route: opts.route, versionId: opts.versionId });
  return { be, calls, ...view, user: userEvent.setup() };
}

const where = () => screen.getByTestId('where').textContent ?? '';
const card = (name: RegExp | string) => screen.getByRole('article', { name });
const listCalls = (calls: ReturnType<typeof mockApi>) =>
  calls.filter((c) => c.pathname === '/api/v1/assets/a1/comments' && c.method === 'GET');

const ROOT = comment({ id: 'r1', body: 'Please brighten the sky.', createdAt: ago(120) });
const REPLY = comment({
  id: 'p1',
  parentId: 'r1',
  authorId: SAM,
  authorName: 'Sam Reviewer',
  body: 'Done in the next version.',
  createdAt: ago(60),
});

describe('thread rendering', () => {
  it('shows a thread with its replies nested, relative times with the exact time on hover, and the reply count in the list', async () => {
    setup([thread(ROOT, [REPLY])]);
    const t = await screen.findByRole('article', { name: 'Thread by Priya Natarajan' });
    expect(within(t).getByText('Please brighten the sky.')).toBeInTheDocument();
    const replies = within(t).getByRole('list', { name: 'Replies' });
    expect(within(replies).getByText('Sam Reviewer')).toBeInTheDocument();
    expect(within(replies).getByText('Done in the next version.')).toBeInTheDocument();
    const time = within(t).getAllByText('2 hours ago')[0]!;
    expect(time.tagName).toBe('TIME');
    expect(time).toHaveAttribute('title', new Date(ROOT.createdAt).toLocaleString('en'));
  });

  it('renders mention tokens as @Name chips using the current name, and never as HTML', async () => {
    setup([
      thread(
        comment({
          id: 'r1',
          body: `Hi @[Old Name](user:${SAM}) <img src=x onerror="alert(1)"> https://example.com`,
          mentions: [{ userId: SAM, displayName: 'Sam Reviewer' }],
        }),
      ),
    ]);
    const t = await screen.findByRole('article');
    const chip = within(t).getByText('@Sam Reviewer');
    expect(chip).toHaveClass('dam-mention');
    expect(t.querySelector('img')).toBeNull();
    expect(t.querySelector('a')).toBeNull();
    expect(t.textContent).toContain('<img src=x onerror="alert(1)">');
  });

  it('marks edited comments, and shows a resolved thread muted with who resolved it', async () => {
    setup([
      thread(
        comment({
          id: 'r1',
          edited: true,
          editedAt: ago(5),
          resolved: true,
          resolvedBy: SAM,
          resolvedByName: 'Sam Reviewer',
          resolvedAt: ago(10),
        }),
      ),
    ]);
    const t = await screen.findByRole('article');
    expect(within(t).getByText('edited')).toBeInTheDocument();
    expect(within(t).getByText(/Resolved by Sam Reviewer/)).toBeInTheDocument();
    expect(t).toHaveAttribute('data-resolved', 'true');
    expect(within(t).getByRole('button', { name: /Reopen the thread/ })).toBeInTheDocument();
  });

  it('shows a deleted first comment as a placeholder with its replies still visible and no actions on it', async () => {
    setup([thread(comment({ id: 'r1', deleted: true, body: '[deleted]' }), [REPLY])]);
    const t = await screen.findByRole('article');
    expect(within(t).getByText('This comment was deleted')).toBeInTheDocument();
    expect(within(t).queryByText('[deleted]')).toBeNull();
    expect(within(t).getByText('Done in the next version.')).toBeInTheDocument();
    expect(within(t).queryByRole('button', { name: /^Reply to/ })).toBeNull();
    expect(within(t).queryByRole('button', { name: /Resolve/ })).toBeNull();
  });

  it('shows the version badge on each comment only when looking at all versions', async () => {
    const v1 = comment({ id: 'r0', versionId: 'v1', versionNo: 1, body: 'Old note' });
    const { user } = setup([thread(v1), thread(ROOT)]);
    await screen.findByText('Please brighten the sky.');
    expect(screen.queryByText('v2')).toBeNull();
    expect(screen.queryByText('Old note')).toBeNull(); // this version only
    await user.selectOptions(screen.getByLabelText('Versions shown'), 'all');
    expect(await screen.findByText('Old note')).toBeInTheDocument();
    expect(screen.getByText('v1')).toBeInTheDocument();
    expect(screen.getByText('v2')).toBeInTheDocument();
  });

  it("numbers annotations the way the preview does (from the version's unfiltered list) and names the anchor", async () => {
    const note = comment({ id: 'n1', body: 'Plain note' });
    const a1 = comment({ id: 'a1', body: 'First mark', anchor: region({ page: 2 }) });
    const a2 = comment({ id: 'a2', body: 'Second mark', anchor: timecode(83500) });
    const { user } = setup([thread(note), thread(a1), thread(a2)]);
    expect(
      await screen.findByRole('button', { name: 'Annotation 1: Region on page 2. Show on the preview.' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Annotation 2: At 1:23. Show on the preview.' })).toBeInTheDocument();
    // Filtering to the second one keeps its number 2.
    await user.selectOptions(screen.getByLabelText('Type'), 'annotation');
    await user.click(screen.getByRole('button', { name: /Resolved/ }));
    await user.click(screen.getByRole('button', { name: 'Open (3)' }));
    expect(await screen.findByRole('button', { name: /Annotation 2:/ })).toBeInTheDocument();
  });

  it('collapses long reply lists behind "Show N more replies"', async () => {
    const replies = Array.from({ length: 6 }, (_, i) =>
      comment({ id: `p${i}`, parentId: 'r1', body: `Reply number ${i}`, authorName: 'Sam Reviewer' }),
    );
    const { user } = setup([thread(ROOT, replies)]);
    await screen.findByText('Reply number 5');
    expect(screen.queryByText('Reply number 0')).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Show 3 more replies' }));
    expect(screen.getByText('Reply number 0')).toBeInTheDocument();
  });

  it('shows skeletons while loading, then an empty state with a hint about annotating', async () => {
    setup([]);
    expect(screen.getByText('Loading comments')).toBeInTheDocument();
    expect(await screen.findByText('No comments yet - start the conversation')).toBeInTheDocument();
    expect(screen.getByText(/draw on the image to annotate/)).toBeInTheDocument();
  });

  it('shows an error note with a retry that loads the list', async () => {
    let fail = true;
    const { be, handler } = serveComments([thread(ROOT)]);
    mockApi(
      (c) => (fail && c.pathname === '/api/v1/assets/a1/comments' ? json({ title: 'Boom', status: 500 }, 500) : handler(c)),
      { permissions: WRITE },
    );
    renderPanel(<CommentsTab assetId="a1" />);
    const alert = await screen.findByText('Comments could not be loaded.');
    fail = false;
    await userEvent.click(within(alert.parentElement!).getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('Please brighten the sky.')).toBeInTheDocument();
    expect(be.threads).toHaveLength(1);
  });
});

describe('filters', () => {
  it('queries by state, type and scope, keeps them in the URL, and shows counts that ignore the filters', async () => {
    const resolved = comment({
      id: 'r2',
      body: 'Already handled',
      resolved: true,
      resolvedByName: 'Sam Reviewer',
      resolvedAt: ago(3),
    });
    const { user, calls } = setup([thread(ROOT), thread(resolved)]);
    await screen.findByText('Please brighten the sky.');
    expect(screen.getByRole('button', { name: 'Open (1)' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Resolved (1)' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Resolved (1)' }));
    expect(await screen.findByText('Already handled')).toBeInTheDocument();
    expect(screen.queryByText('Please brighten the sky.')).toBeNull();
    expect(where()).toContain('state=resolved');
    expect(listCalls(calls).some((c) => c.search.get('state') === 'resolved' && c.search.get('versionId') === 'v2')).toBe(true);
    expect(screen.getByRole('button', { name: 'Open (1)' })).toBeInTheDocument(); // counts did not follow the filter

    await user.selectOptions(screen.getByLabelText('Type'), 'annotation');
    expect(where()).toContain('kind=annotation');
    expect(await screen.findByText('No threads match these filters')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Show everything' }));
    expect(where()).not.toContain('state=');
    expect(where()).not.toContain('kind=');

    await user.selectOptions(screen.getByLabelText('Versions shown'), 'all');
    expect(where()).toContain('scope=all');
    await waitFor(() =>
      expect(listCalls(calls).some((c) => !c.search.get('versionId') && c.search.get('state') === 'all')).toBe(true),
    );
  });

  it('starts from the URL', async () => {
    const resolved = comment({ id: 'r2', body: 'Already handled', resolved: true });
    const { calls } = setup([thread(ROOT), thread(resolved)], {
      route: '/library/assets/a1?state=resolved&kind=comment&scope=all',
    });
    expect(await screen.findByText('Already handled')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Resolved/ })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByLabelText('Type')).toHaveValue('comment');
    expect(screen.getByLabelText('Versions shown')).toHaveValue('all');
    expect(listCalls(calls).some((c) => c.search.get('state') === 'resolved' && c.search.get('kind') === 'comment')).toBe(true);
  });
});

describe('writing', () => {
  it('posts a comment against the current version, clears the field and keeps focus in it', async () => {
    const { user, be } = setup([]);
    const box = await screen.findByRole('textbox', { name: 'Add a comment' });
    await user.type(box, 'Looks great');
    await user.click(screen.getByRole('button', { name: 'Post' }));
    await waitFor(() => expect(be.posts).toHaveLength(1));
    expect(await be.posts[0]!.json()).toEqual({ body: 'Looks great', versionId: 'v2', parentId: null, anchor: null });
    expect(await screen.findByText('Looks great')).toBeInTheDocument();
    expect(box).toHaveValue('');
    expect(box).toHaveFocus();
  });

  it('attaches the anchor drawn on the stage, shows it as a removable chip, and clears it after posting', async () => {
    const { user, be } = setup([]);
    const box = await screen.findByRole('textbox', { name: 'Add a comment' });
    await user.click(screen.getByRole('button', { name: 'stage: draw region' }));
    expect(await screen.findByTestId('draft-anchor')).toHaveTextContent('Annotating: Region on page 2');
    await user.type(box, 'Crop this');
    await user.click(screen.getByRole('button', { name: 'Post' }));
    await waitFor(() => expect(be.posts).toHaveLength(1));
    expect(await be.posts[0]!.json()).toEqual({
      body: 'Crop this',
      versionId: 'v2',
      parentId: null,
      anchor: { type: 'region', x: 0.25, y: 0.5, w: 0.125, h: 0.25, page: 2, startMs: null, endMs: null },
    });
    await waitFor(() => expect(screen.queryByTestId('draft-anchor')).toBeNull());
    expect(screen.getByTestId('ui-draft')).toHaveTextContent('');
  });

  it('lets the person drop the draft anchor and post a plain comment', async () => {
    const { user, be } = setup([]);
    await screen.findByRole('textbox', { name: 'Add a comment' });
    await user.click(screen.getByRole('button', { name: 'stage: capture time' }));
    expect(await screen.findByTestId('draft-anchor')).toHaveTextContent('Annotating: At 1:23');
    await user.click(screen.getByRole('button', { name: 'Remove the annotation, post a plain comment' }));
    expect(screen.queryByTestId('draft-anchor')).toBeNull();
    await user.type(screen.getByRole('textbox', { name: 'Add a comment' }), 'Hello');
    await user.click(screen.getByRole('button', { name: 'Post' }));
    await waitFor(() => expect(be.posts).toHaveLength(1));
    expect(((await be.posts[0]!.json()) as { anchor: unknown }).anchor).toBeNull();
  });

  it("shows the server's 422 message inline, keeps the text, and lets the person fix it", async () => {
    const { user, be } = setup([]);
    be.failPost = json(
      { title: 'Validation failed', status: 422, errors: { body: ['Comments can be at most 4000 characters.'] } },
      422,
    );
    const box = await screen.findByRole('textbox', { name: 'Add a comment' });
    await user.type(box, 'Some words');
    await user.click(screen.getByRole('button', { name: 'Post' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Comments can be at most 4000 characters.');
    expect(box).toHaveValue('Some words');
    expect(box).toHaveAttribute('aria-invalid', 'true');
    be.failPost = undefined;
    await user.type(box, '!');
    expect(screen.queryByRole('alert')).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Post' }));
    expect(await screen.findByText('Some words!')).toBeInTheDocument();
  });

  it('posts with Ctrl+Enter and Cmd+Enter', async () => {
    const { user, be } = setup([]);
    const box = await screen.findByRole('textbox', { name: 'Add a comment' });
    await user.type(box, 'First{Control>}{Enter}{/Control}');
    await waitFor(() => expect(be.posts).toHaveLength(1));
    await waitFor(() => expect(box).toHaveValue(''));
    await user.type(box, 'Second{Meta>}{Enter}{/Meta}');
    await waitFor(() => expect(be.posts).toHaveLength(2));
  });

  it('keeps Post disabled for empty text and past 4000 characters, with a counter near the limit', async () => {
    const { user } = setup([]);
    const box = await screen.findByRole('textbox', { name: 'Add a comment' });
    const post = screen.getByRole('button', { name: 'Post' });
    expect(post).toBeDisabled();
    await user.type(box, '   ');
    expect(post).toBeDisabled();
    await user.click(box);
    await user.paste('x'.repeat(3700));
    expect(screen.getByText('3700 / 4000')).toBeInTheDocument();
    expect(post).toBeEnabled();
    await user.paste('y'.repeat(301));
    expect(post).toBeDisabled();
    expect(screen.getByText('1 character too many.')).toBeInTheDocument();
  });

  it('replies inline to a thread with the thread as parent', async () => {
    const { user, be } = setup([thread(ROOT)]);
    await user.click(await screen.findByRole('button', { name: 'Reply to Priya Natarajan' }));
    const box = screen.getByRole('textbox', { name: 'Reply to Priya Natarajan' });
    expect(box).toHaveFocus();
    await user.type(box, 'Fixed it');
    await user.click(within(card(/Thread by/)).getByRole('button', { name: 'Reply' }));
    await waitFor(() => expect(be.posts).toHaveLength(1));
    expect(await be.posts[0]!.json()).toEqual({ body: 'Fixed it', versionId: null, parentId: 'r1', anchor: null });
    expect(await screen.findByText('Fixed it')).toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: 'Reply to Priya Natarajan' })).toBeNull();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Reply to Priya Natarajan' })).toHaveFocus());
  });

  it('cancels a reply with Escape', async () => {
    const { user } = setup([thread(ROOT)]);
    await user.click(await screen.findByRole('button', { name: 'Reply to Priya Natarajan' }));
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('textbox', { name: 'Reply to Priya Natarajan' })).toBeNull();
  });

  it('resolves and reopens a thread, showing the change at once', async () => {
    const { user, be } = setup([thread(ROOT)]);
    await user.click(await screen.findByRole('button', { name: 'Resolve the thread started by Priya Natarajan' }));
    expect(await screen.findByText(/Resolved by Dev Admin/)).toBeInTheDocument();
    await waitFor(() => expect(be.patches).toEqual([{ id: 'r1', body: { body: null, resolved: true } }]));
    await user.click(screen.getByRole('button', { name: 'Reopen the thread started by Priya Natarajan' }));
    await waitFor(() => expect(screen.queryByText(/Resolved by/)).toBeNull());
    await waitFor(() => expect(be.patches[1]).toEqual({ id: 'r1', body: { body: null, resolved: false } }));
  });

  it('puts the change back and says so when resolving fails', async () => {
    const { user } = setup([thread(ROOT)], {
      more: (c) =>
        c.method === 'PATCH' ? json({ title: 'Forbidden', detail: 'You cannot resolve this.', status: 403 }, 403) : undefined,
    });
    await user.click(await screen.findByRole('button', { name: /^Resolve the thread/ }));
    expect(await screen.findByText('You cannot resolve this.')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText(/Resolved by/)).toBeNull());
  });

  it('edits your own comment: prefilled, Save only when changed, shows "edited"', async () => {
    const mine = comment({ id: 'r1', authorId: ME, authorName: 'Dev Admin', body: `Ask @[Sam Reviewer](user:${SAM}) about it` });
    const { user, be } = setup([thread(mine)]);
    await user.click(await screen.findByRole('button', { name: 'Edit the comment by Dev Admin' }));
    const box = screen.getByRole('textbox', { name: 'Edit comment' });
    expect(box).toHaveValue('Ask @Sam Reviewer about it');
    expect(box).toHaveFocus();
    const save = screen.getByRole('button', { name: 'Save' });
    expect(save).toBeDisabled();
    await user.type(box, ' today');
    expect(save).toBeEnabled();
    await user.click(save);
    await waitFor(() => expect(be.patches).toHaveLength(1));
    expect(be.patches[0]).toEqual({
      id: 'r1',
      body: { body: `Ask @[Sam Reviewer](user:${SAM}) about it today`, resolved: null },
    });
    expect(await screen.findByText('edited')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Edit the comment by Dev Admin' })).toHaveFocus());
  });

  it('deletes after confirming, and keeps replies when the first comment is deleted', async () => {
    const mine = comment({ id: 'r1', authorId: ME, authorName: 'Dev Admin', body: 'My note' });
    const { user, be } = setup([thread(mine, [REPLY])]);
    await user.click(await screen.findByRole('button', { name: 'Delete the comment by Dev Admin' }));
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getByText(/The replies stay in the thread/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(be.deletes).toEqual([]);
    await user.click(screen.getByRole('button', { name: 'Delete the comment by Dev Admin' }));
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(be.deletes).toEqual(['r1']));
    expect(await screen.findByText('This comment was deleted')).toBeInTheDocument();
    expect(screen.getByText('Done in the next version.')).toBeInTheDocument();
  });
});

describe('permissions', () => {
  it("offers the author Reply, Resolve, Edit and Delete, and another person's comment only Reply and Resolve", async () => {
    const mine = comment({ id: 'r2', authorId: ME, authorName: 'Dev Admin', body: 'Mine' });
    setup([thread(ROOT), thread(mine)]);
    const theirs = await screen.findByRole('article', { name: 'Thread by Priya Natarajan' });
    expect(within(theirs).getByRole('button', { name: /^Reply to/ })).toBeInTheDocument();
    expect(within(theirs).getByRole('button', { name: /^Resolve/ })).toBeInTheDocument();
    expect(within(theirs).queryByRole('button', { name: /^Edit/ })).toBeNull();
    expect(within(theirs).queryByRole('button', { name: /^Delete/ })).toBeNull();
    const own = screen.getByRole('article', { name: 'Thread by Dev Admin' });
    for (const n of [/^Reply to/, /^Resolve/, /^Edit/, /^Delete/])
      expect(within(own).getByRole('button', { name: n })).toBeInTheDocument();
  });

  it("lets someone with assets.update delete other people's comments, but still not edit them", async () => {
    setup([thread(ROOT)], { permissions: [...WRITE, 'assets.update'] });
    const theirs = await screen.findByRole('article');
    expect(within(theirs).getByRole('button', { name: /^Delete the comment by Priya/ })).toBeInTheDocument();
    expect(within(theirs).queryByRole('button', { name: /^Edit/ })).toBeNull();
  });

  it('without comments.write: reads fine, no composer, no reply/resolve/edit, and a calm note', async () => {
    const mine = comment({ id: 'r2', authorId: ME, authorName: 'Dev Admin', body: 'Mine' });
    setup([thread(ROOT), thread(mine)], { permissions: ['assets.read'] });
    await screen.findByText('Please brighten the sky.');
    expect(screen.queryByRole('textbox')).toBeNull();
    expect(screen.getByText('You can read comments on this asset but not add to them.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Reply/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /^Resolve the thread/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /^Edit/ })).toBeNull();
    // Deleting your own comment needs no comments.write.
    expect(
      within(screen.getByRole('article', { name: 'Thread by Dev Admin' })).getByRole('button', { name: /^Delete/ }),
    ).toBeInTheDocument();
  });
});

describe('sync with the preview', () => {
  const stageAnchored = () => [
    thread(comment({ id: 'r1', body: 'Page note', anchor: region({ page: 3 }) })),
    thread(comment({ id: 'r2', body: 'Time note', anchor: timecode(83500, 90000) })),
    thread(comment({ id: 'r3', body: 'Plain note' })),
  ];

  it('selects the thread and asks the stage for the page / timecode when an anchor chip is clicked', async () => {
    const { user, spies } = setup(stageAnchored());
    await user.click(await screen.findByRole('button', { name: /Annotation 1:/ }));
    expect(spies.page).toHaveBeenCalledWith(3);
    expect(screen.getByTestId('ui-selected')).toHaveTextContent('r1');
    await user.click(screen.getByRole('button', { name: /Annotation 2: 1:23 to 1:30/ }));
    expect(spies.seek).toHaveBeenCalledWith(83500);
    expect(screen.getByTestId('ui-selected')).toHaveTextContent('r2');
  });

  it("does not seek or page for an annotation that belongs to another version's file", async () => {
    const old = comment({ id: 'r9', versionId: 'v1', versionNo: 1, body: 'Old mark', anchor: timecode(5000) });
    const { user, spies } = setup([thread(old)], { route: '/library/assets/a1?scope=all' });
    await user.click(await screen.findByRole('button', { name: /At 0:05/ }));
    expect(spies.seek).not.toHaveBeenCalled();
    expect(screen.getByTestId('ui-selected')).toHaveTextContent('r9');
  });

  it('selects on a click anywhere on the card, reports hover, and clears hover on leave', async () => {
    const { user } = setup(stageAnchored());
    await screen.findByText('Plain note');
    const cards = screen.getAllByRole('article');
    await user.click(within(cards[2]!).getByText('Plain note'));
    expect(screen.getByTestId('ui-selected')).toHaveTextContent('r3');
    expect(cards[2]).toHaveAttribute('data-selected', 'true');
    await user.hover(cards[0]!);
    expect(screen.getByTestId('ui-hovered')).toHaveTextContent('r1');
    expect(cards[0]).toHaveAttribute('data-hovered', 'true');
    await user.unhover(cards[0]!);
    expect(screen.getByTestId('ui-hovered')).toHaveTextContent('');
  });

  it('highlights the card the stage selected or hovered', async () => {
    const { user } = setup([thread(ROOT)].map((t) => ({ ...t, root: { ...t.root, id: 'r1' } })));
    const c = await screen.findByRole('article');
    await user.click(screen.getByRole('button', { name: 'stage: hover r1' }));
    expect(c).toHaveAttribute('data-hovered', 'true');
    await user.click(screen.getByRole('button', { name: 'stage: select r1' }));
    expect(c).toHaveAttribute('data-selected', 'true');
  });
});

describe('deep link', () => {
  it('opens a reply on another version: switches to all versions, selects the thread, lights the reply and drops the param', async () => {
    const old = comment({ id: 'r1', versionId: 'v1', versionNo: 1, body: 'Version one note' });
    const reply = comment({
      id: 'p1',
      parentId: 'r1',
      versionId: 'v1',
      versionNo: 1,
      body: 'Target reply',
      authorName: 'Sam Reviewer',
      authorId: SAM,
    });
    setup([thread(old, [reply]), thread(comment({ id: 'r2', body: 'Current note' }))], {
      route: '/library/assets/a1?comment=p1&state=open',
    });
    const target = await screen.findByText('Target reply');
    await waitFor(() => expect(screen.getByTestId('ui-selected')).toHaveTextContent('r1'));
    expect(where()).toContain('scope=all');
    expect(where()).toContain('tab=comments');
    expect(target.closest('[data-flash]')).not.toBeNull();
    // The list now shows both versions, with version badges.
    expect(screen.getByText('Current note')).toBeInTheDocument();
    expect(screen.getAllByText('v1').length).toBeGreaterThan(0);
  });

  it('opens a resolved thread that the open filter hid', async () => {
    const resolved = comment({
      id: 'r1',
      body: 'Resolved note',
      resolved: true,
      resolvedByName: 'Sam Reviewer',
      resolvedAt: ago(4),
    });
    setup([thread(resolved)], { route: '/library/assets/a1?comment=r1&state=open&kind=annotation' });
    expect(await screen.findByText('Resolved note')).toBeInTheDocument();
    expect(where()).not.toContain('state=open');
    expect(where()).not.toContain('kind=annotation');
  });

  it('says so quietly when the comment is gone, and removes the param', async () => {
    setup([thread(ROOT)], { route: '/library/assets/a1?comment=gone' });
    expect(await screen.findByText('That comment is no longer available.')).toBeInTheDocument();
    await waitFor(() => expect(where()).not.toContain('comment=gone'));
    expect(where()).toContain('tab=comments');
    expect(await screen.findByText('Please brighten the sky.')).toBeInTheDocument();
  });
});

describe('accessibility', () => {
  it('has no axe violations with threads, replies, filters and the composer', async () => {
    const mine = comment({ id: 'r2', authorId: ME, authorName: 'Dev Admin', body: 'Mine', anchor: region({ page: 2 }) });
    const { container } = setup([thread(ROOT, [REPLY]), thread(mine)]);
    await screen.findByText('Mine');
    expect(await axe(container)).toHaveNoViolations();
  });

  it('has no axe violations with the reply box and the delete dialog open', async () => {
    const mine = comment({ id: 'r2', authorId: ME, authorName: 'Dev Admin', body: 'Mine' });
    const { container, user } = setup([thread(mine)]);
    await user.click(await screen.findByRole('button', { name: /^Reply to/ }));
    expect(await axe(container)).toHaveNoViolations();
    await user.click(screen.getByRole('button', { name: /^Delete the comment/ }));
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('Hindi', () => {
  it('renders the thread, its actions and the composer in Hindi', async () => {
    setLanguage('hi');
    const { user } = setup([thread(ROOT, [REPLY])]);
    const t = await screen.findByRole('article', { name: 'Priya Natarajan का थ्रेड' });
    expect(within(t).getByRole('list', { name: 'जवाब' })).toBeInTheDocument();
    expect(within(t).getByRole('button', { name: 'Priya Natarajan द्वारा शुरू किया गया थ्रेड हल करें' })).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'टिप्पणी जोड़ें' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'पोस्ट करें' })).toBeInTheDocument();
    await user.click(within(t).getByRole('button', { name: /^Priya Natarajan को जवाब दें/ }));
    expect(await screen.findByRole('textbox', { name: 'Priya Natarajan को जवाब दें' })).toBeInTheDocument();
  });
});
