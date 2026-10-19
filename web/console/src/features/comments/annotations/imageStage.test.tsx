import { act, fireEvent, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { setLanguage } from '../../../i18n';
import { mockImageBox, pin, probe, region, renderStage, resetHarness, thread } from './stageHarness';

// The picture sits at (100, 50) in the viewport, 400 x 200, and (30, 20) inside the scrolling canvas.
const BOX = { left: 100, top: 50, width: 400, height: 200, offsetLeft: 30, offsetTop: 20 };
let restoreBox: () => void;
beforeEach(() => {
  setLanguage('en');
  restoreBox = mockImageBox(BOX);
});
afterEach(() => {
  restoreBox();
  resetHarness();
  vi.restoreAllMocks();
});

const layer = () => screen.findByTestId('annotation-layer');
const ptr = (clientX: number, clientY: number, extra: Record<string, unknown> = {}) => ({
  clientX,
  clientY,
  pointerId: 1,
  isPrimary: true,
  pointerType: 'mouse',
  button: 0,
  ...extra,
});
/** A drag from one viewport point to another on the layer. */
function drag(el: Element, from: [number, number], to: [number, number], extra: Record<string, unknown> = {}) {
  fireEvent.pointerDown(el, ptr(...from, extra));
  fireEvent.pointerMove(el, ptr(...to, extra));
  fireEvent.pointerUp(el, ptr(...to, extra));
}
const annotateButton = () => screen.getByRole('button', { name: 'Annotate' });

describe('annotation overlay on an image', () => {
  const threads = [
    region('r1', 0.1, 0.2, 0.3, 0.4, { body: 'Crop this tighter' }),
    pin('p1', 0.75, 0.5, { body: 'Dust spot here', resolved: true }),
    thread('c1', null, { body: 'General comment' }),
    region('other', 0.5, 0.5, 0.2, 0.2, { versionId: 'v1', body: 'Old version' }),
  ];

  it('draws a numbered, named button for each annotation of the current version, in percentages of the picture', async () => {
    renderStage({ threads });
    const el = await layer();
    const one = within(el).getByRole('button', { name: 'Annotation 1: Crop this tighter' });
    expect(one).toHaveStyle({ left: '10%', top: '20%', width: '30%', height: '40%' });
    expect(one).toHaveTextContent('1');
    expect(one).toHaveAttribute('title', 'Annotation 1: Crop this tighter');
    const two = within(el).getByRole('button', { name: 'Annotation 2 (resolved): Dust spot here' });
    expect(two).toHaveStyle({ left: '75%', top: '50%' });
    expect(two).toHaveAttribute('data-resolved', 'true');
    // another version's annotation and a plain comment are not drawn
    expect(within(el).getAllByRole('button')).toHaveLength(2);
    expect(screen.queryByText(/Old version/)).not.toBeInTheDocument();
  });

  it('sizes the layer to exactly the picture inside the scrolling canvas', async () => {
    renderStage({ threads });
    expect(await layer()).toHaveStyle({ left: '30px', top: '20px', width: '400px', height: '200px' });
  });

  it('numbers with the shared numbering even when a timecode thread comes first', async () => {
    renderStage({ threads: [thread('t', { type: 'timecode', startMs: 5 }), region('r', 0, 0, 0.5, 0.5)] });
    expect(await screen.findByRole('button', { name: /^Annotation 2:/ })).toBeInTheDocument();
  });

  it('selects the thread on click, toggles it off on a second click, and on Enter / Space for keyboard users', async () => {
    renderStage({ threads });
    const one = await screen.findByRole('button', { name: /^Annotation 1/ });
    await userEvent.click(one);
    expect(probe.ui?.selectedThreadId).toBe('r1');
    expect(one).toHaveAttribute('aria-pressed', 'true');
    expect(one).toHaveAttribute('data-selected', 'true');
    await userEvent.click(one);
    expect(probe.ui?.selectedThreadId).toBeNull();

    const two = screen.getByRole('button', { name: /^Annotation 2/ });
    two.focus();
    await userEvent.keyboard('{Enter}');
    expect(probe.ui?.selectedThreadId).toBe('p1');
    await userEvent.keyboard(' ');
    expect(probe.ui?.selectedThreadId).toBeNull();
  });

  it('follows the tab order of the numbers', async () => {
    renderStage({ threads });
    await layer();
    const names = screen.getAllByRole('button').map((b) => b.getAttribute('aria-label'));
    expect(names.filter((n) => n?.startsWith('Annotation')).map((n) => n?.slice(0, 12))).toEqual([
      'Annotation 1',
      'Annotation 2',
    ]);
  });

  it('shares hover and selection with the thread cards both ways', async () => {
    renderStage({ threads });
    const one = await screen.findByRole('button', { name: /^Annotation 1/ });
    await userEvent.hover(one);
    expect(probe.ui?.hoveredThreadId).toBe('r1');
    await userEvent.unhover(one);
    expect(probe.ui?.hoveredThreadId).toBeNull();
    act(() => one.focus());
    expect(probe.ui?.hoveredThreadId).toBe('r1');
    act(() => one.blur());
    expect(probe.ui?.hoveredThreadId).toBeNull();

    // from the panel
    act(() => probe.ui?.hoverThread('p1'));
    expect(screen.getByRole('button', { name: /^Annotation 2/ })).toHaveAttribute('data-hovered', 'true');
    act(() => probe.ui?.selectThread('r1'));
    expect(screen.getByRole('button', { name: /^Annotation 1/ })).toHaveAttribute('data-selected', 'true');
  });

  it('scrolls the selected annotation into the middle of the canvas at 100% only', async () => {
    renderStage({ threads });
    const one = await screen.findByRole('button', { name: /^Annotation 1/ });
    const canvas = one.closest('.dam-stage__canvas') as HTMLElement;
    const scrollBy = vi.fn();
    canvas.scrollBy = scrollBy;
    act(() => probe.ui?.selectThread('r1'));
    expect(scrollBy).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole('button', { name: '100%' }));
    expect(scrollBy).toHaveBeenCalled();
  });

  it('shows nothing for annotations when the version is unknown', async () => {
    renderStage({ threads, versionId: null });
    await layer();
    expect(screen.queryByRole('button', { name: /^Annotation/ })).not.toBeInTheDocument();
  });

  it('hides and shows all annotations, and remembers the choice per browser', async () => {
    const first = renderStage({ threads });
    await layer();
    const toggle = screen.getByRole('button', { name: 'Show annotations (2)' });
    expect(toggle).toHaveAttribute('aria-pressed', 'true');
    await userEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-pressed', 'false');
    expect(screen.queryByRole('button', { name: /^Annotation 1/ })).not.toBeInTheDocument();
    expect(localStorage.getItem('dam.annotations.visible')).toBe('false');

    first.unmount();
    renderStage({ threads });
    await layer();
    expect(screen.queryByRole('button', { name: /^Annotation 1/ })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Show annotations (2)' }));
    expect(await screen.findByRole('button', { name: /^Annotation 1/ })).toBeInTheDocument();
  });

  it('works when storage is unavailable', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    renderStage({ threads });
    expect(await screen.findByRole('button', { name: /^Annotation 1/ })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Show annotations (2)' }));
    expect(screen.queryByRole('button', { name: /^Annotation 1/ })).not.toBeInTheDocument();
  });

  it('offers no show/hide control when there is nothing to show', async () => {
    renderStage({ threads: [thread('c', null)] });
    await layer();
    expect(screen.queryByRole('button', { name: /Show annotations/ })).not.toBeInTheDocument();
  });

  it('has no accessibility violations with annotations, one selected and one resolved', async () => {
    const { container } = renderStage({ threads });
    await layer();
    act(() => probe.ui?.selectThread('r1'));
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('permissions', () => {
  it('shows existing annotations but no annotate controls without comments.write, and ignores the shortcut', async () => {
    renderStage({ permissions: ['assets.read'], threads: [region('r1', 0.1, 0.1, 0.2, 0.2)] });
    expect(await screen.findByRole('button', { name: /^Annotation 1/ })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Annotate' })).not.toBeInTheDocument();
    fireEvent.keyDown(screen.getByRole('region', { name: 'Preview' }), { key: 'a' });
    expect(probe.ui?.annotating).toBe(false);
  });

  it('stays read-only when the permissions cannot be loaded', async () => {
    renderStage({ failMe: true, threads: [region('r1', 0.1, 0.1, 0.2, 0.2)] });
    expect(await screen.findByRole('button', { name: /^Annotation 1/ })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Annotate' })).not.toBeInTheDocument();
  });

  it('offers no annotation controls on a file type with no preview', async () => {
    renderStage({ type: 'archive' });
    await screen.findByText('No preview for this file type');
    expect(screen.queryByRole('button', { name: 'Annotate' })).not.toBeInTheDocument();
  });
});

describe('annotate mode and drawing', () => {
  async function enter() {
    renderStage();
    const button = await screen.findByRole('button', { name: 'Annotate' });
    await layer();
    await userEvent.click(button);
    return screen.getByTestId('annotation-layer');
  }

  it('toggles from the button (aria-pressed) and from the A key while the stage has focus', async () => {
    renderStage();
    const button = await screen.findByRole('button', { name: 'Annotate' });
    expect(button).toHaveAttribute('aria-pressed', 'false');
    await userEvent.click(button);
    expect(button).toHaveAttribute('aria-pressed', 'true');
    expect(probe.ui?.annotating).toBe(true);
    expect(await layer()).toHaveAttribute('data-annotating', 'true');

    const canvas = screen.getByRole('region', { name: 'Preview' });
    canvas.focus();
    await userEvent.keyboard('a');
    expect(probe.ui?.annotating).toBe(false);
    await userEvent.keyboard('a');
    expect(probe.ui?.annotating).toBe(true);
  });

  it('never hijacks typing: A inside a text field in the stage, or with a modifier, does nothing', async () => {
    renderStage();
    await screen.findByRole('button', { name: 'Annotate' });
    const canvas = screen.getByRole('region', { name: 'Preview' });
    const input = document.createElement('input');
    canvas.appendChild(input);
    fireEvent.keyDown(input, { key: 'a' });
    fireEvent.keyDown(canvas, { key: 'a', ctrlKey: true });
    fireEvent.keyDown(canvas, { key: 'a', metaKey: true });
    expect(probe.ui?.annotating).toBe(false);
  });

  it('turns a drag into a region of the PICTURE, calls focusComposer, and shows a pending outline', async () => {
    const el = await enter();
    // from 25% / 20% to 75% / 80% of the picture: x 100 + 400 * .25 = 200, y 50 + 200 * .2 = 90
    drag(el, [200, 90], [400, 210]);
    expect(probe.ui?.draftAnchor).toEqual({ type: 'region', x: 0.25, y: 0.2, w: 0.5, h: 0.6 });
    expect(probe.focus).toHaveBeenCalledTimes(1);
    const pending = el.querySelector('.dam-annot__pending') as HTMLElement;
    expect(pending).toHaveStyle({ left: '25%', top: '20%', width: '50%', height: '60%' });
    expect(pending).toHaveAttribute('aria-hidden', 'true');
  });

  it('draws the live rectangle while dragging, whichever direction', async () => {
    const el = await enter();
    fireEvent.pointerDown(el, ptr(400, 210));
    fireEvent.pointerMove(el, ptr(200, 90));
    expect(el.querySelector('.dam-annot__pending')).toHaveStyle({ left: '25%', top: '20%', width: '50%', height: '60%' });
    expect(probe.ui?.draftAnchor).toBeNull();
    fireEvent.pointerUp(el, ptr(200, 90));
    expect(probe.ui?.draftAnchor).toEqual({ type: 'region', x: 0.25, y: 0.2, w: 0.5, h: 0.6 });
  });

  it('clamps a drag that runs off the picture to its edges (x + w and y + h never exceed 1)', async () => {
    const el = await enter();
    drag(el, [300, 150], [5000, 5000]);
    expect(probe.ui?.draftAnchor).toEqual({ type: 'region', x: 0.5, y: 0.5, w: 0.5, h: 0.5 });
    drag(el, [-100, -100], [300, 150]);
    expect(probe.ui?.draftAnchor).toEqual({ type: 'region', x: 0, y: 0, w: 0.5, h: 0.5 });
  });

  it('turns a click, or a drag under the minimum, into a pin', async () => {
    const el = await enter();
    fireEvent.pointerDown(el, ptr(300, 150));
    fireEvent.pointerUp(el, ptr(300, 150));
    expect(probe.ui?.draftAnchor).toEqual({ type: 'point', x: 0.5, y: 0.5 });
    expect(el.querySelector('.dam-annot__pending--pin')).toHaveStyle({ left: '50%', top: '50%' });
    // 4 px of 400 is 1%: under the 1.5% minimum
    drag(el, [200, 90], [204, 94]);
    expect(probe.ui?.draftAnchor).toEqual({ type: 'point', x: 0.25, y: 0.2 });
    // 6 px of 400 is exactly 1.5%, and 3 px of 200 is 1.5%
    drag(el, [200, 90], [206, 93]);
    expect(probe.ui?.draftAnchor).toMatchObject({ type: 'region', w: 0.015, h: 0.015 });
  });

  it('replaces the pending anchor when a new gesture ends', async () => {
    const el = await enter();
    drag(el, [200, 90], [300, 150]);
    drag(el, [110, 60], [150, 100]);
    expect(probe.ui?.draftAnchor).toEqual({ type: 'region', x: 0.025, y: 0.05, w: 0.1, h: 0.2 });
    expect(el.querySelectorAll('.dam-annot__pending')).toHaveLength(1);
    expect(probe.focus).toHaveBeenCalledTimes(2);
  });

  it('works with touch and pen pointers, ignores secondary buttons and non-primary pointers', async () => {
    const el = await enter();
    drag(el, [200, 90], [300, 150], { pointerType: 'touch', button: -1 });
    expect(probe.ui?.draftAnchor).toMatchObject({ type: 'region' });
    probe.ui?.setDraftAnchor(null);
    drag(el, [200, 90], [300, 150], { pointerType: 'pen', button: 0 });
    expect(probe.ui?.draftAnchor).toMatchObject({ type: 'region' });
    act(() => probe.ui?.setDraftAnchor(null));
    drag(el, [200, 90], [300, 150], { button: 2 });
    drag(el, [200, 90], [300, 150], { isPrimary: false });
    expect(probe.ui?.draftAnchor).toBeNull();
  });

  it('captures the pointer so a drag can leave the picture', async () => {
    const el = await enter();
    const setPointerCapture = vi.fn();
    const releasePointerCapture = vi.fn();
    el.setPointerCapture = setPointerCapture;
    el.releasePointerCapture = releasePointerCapture;
    drag(el, [200, 90], [300, 150]);
    expect(setPointerCapture).toHaveBeenCalledWith(1);
    expect(releasePointerCapture).toHaveBeenCalledWith(1);
  });

  it('drops a cancelled gesture (the browser took the touch for scrolling)', async () => {
    const el = await enter();
    fireEvent.pointerDown(el, ptr(200, 90));
    fireEvent.pointerMove(el, ptr(300, 150));
    fireEvent.pointerCancel(el, ptr(300, 150));
    fireEvent.pointerUp(el, ptr(300, 150));
    expect(probe.ui?.draftAnchor).toBeNull();
    expect(el.querySelector('.dam-annot__pending')).toBeNull();
  });

  it('does not draw when annotate mode is off', async () => {
    renderStage();
    const el = await layer();
    drag(el, [200, 90], [300, 150]);
    expect(probe.ui?.draftAnchor).toBeNull();
  });

  it('measures against the picture where it is NOW, so scrolling or resizing the window cannot skew a drawing', async () => {
    const el = await enter();
    restoreBox();
    restoreBox = mockImageBox({ left: -200, top: 50, width: 800, height: 400 }); // zoomed to 200% and scrolled
    drag(el, [200, 250], [600, 450]);
    expect(probe.ui?.draftAnchor).toEqual({ type: 'region', x: 0.5, y: 0.5, w: 0.5, h: 0.5 });
  });

  it('Escape cancels the pending anchor and leaves annotate mode', async () => {
    const el = await enter();
    drag(el, [200, 90], [300, 150]);
    expect(probe.ui?.draftAnchor).not.toBeNull();
    fireEvent.keyDown(screen.getByRole('region', { name: 'Preview' }), { key: 'Escape' });
    expect(probe.ui?.draftAnchor).toBeNull();
    expect(probe.ui?.annotating).toBe(false);
    expect(annotateButton()).toHaveAttribute('aria-pressed', 'false');
  });

  it('Escape during a drag cancels only the drag', async () => {
    const el = await enter();
    fireEvent.pointerDown(el, ptr(200, 90));
    fireEvent.pointerMove(el, ptr(300, 150));
    expect(el.querySelector('.dam-annot__pending')).not.toBeNull();
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(el.querySelector('.dam-annot__pending')).toBeNull();
    fireEvent.pointerUp(el, ptr(300, 150));
    expect(probe.ui?.draftAnchor).toBeNull();
    expect(probe.ui?.annotating).toBe(true);
  });

  it('clears the pending outline when the panel clears the draft, and keeps annotate mode', async () => {
    const el = await enter();
    drag(el, [200, 90], [300, 150]);
    act(() => probe.ui?.setDraftAnchor(null));
    expect(el.querySelector('.dam-annot__pending')).toBeNull();
    expect(probe.ui?.annotating).toBe(true);
  });

  it('does not select an existing annotation by starting a drag on it', async () => {
    renderStage({ threads: [region('r1', 0.1, 0.1, 0.5, 0.5)] });
    await userEvent.click(await screen.findByRole('button', { name: 'Annotate' }));
    const shape = await screen.findByRole('button', { name: /^Annotation 1/ });
    fireEvent.pointerDown(shape, ptr(200, 90));
    fireEvent.pointerUp(shape, ptr(200, 90));
    expect(probe.ui?.draftAnchor).toBeNull();
  });

  it('has no accessibility violations in annotate mode with a pending region', async () => {
    const { container } = renderStage();
    await userEvent.click(await screen.findByRole('button', { name: 'Annotate' }));
    drag(await layer(), [200, 90], [300, 150]);
    expect(await axe(container)).toHaveNoViolations();
  });
});
