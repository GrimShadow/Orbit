import { act, fireEvent, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { setLanguage } from '../../../i18n';
import { mockImageBox, pin, probe, region, renderStage, resetHarness } from './stageHarness';

const BOX = { left: 100, top: 50, width: 400, height: 200, offsetLeft: 30, offsetTop: 20 };
let restoreBox: () => void;
beforeEach(() => {
  setLanguage('en');
  restoreBox = mockImageBox(BOX);
});
afterEach(() => {
  restoreBox();
  resetHarness();
});

const threads = [
  region('a', 0.1, 0.1, 0.2, 0.2, { page: 1, body: 'On page one' }),
  pin('b', 0.5, 0.5, { page: 2, body: 'On page two' }),
  pin('c', 0.3, 0.3, { body: 'No page recorded' }),
  pin('d', 0.6, 0.6, { page: 3, body: 'On page three', resolved: true }),
];
const doc = { type: 'document', status: 'published' } as const;
const ptr = (clientX: number, clientY: number) => ({
  clientX,
  clientY,
  pointerId: 1,
  isPrimary: true,
  pointerType: 'mouse',
  button: 0,
});
const names = () =>
  within(screen.getByTestId('annotation-layer'))
    .getAllByRole('button')
    .map((b) => b.getAttribute('aria-label'));
const next = () => userEvent.click(screen.getByRole('button', { name: 'Next page' }));

describe('annotations on a paged document', () => {
  it('draws only the annotations of the page showing, with their global numbers, and an anchor without a page counts as page 1', async () => {
    renderStage({ ...doc, threads });
    await screen.findByRole('img', { name: 'Hero, page 1' });
    await screen.findByTestId('annotation-layer');
    expect(names()).toEqual(['Annotation 1: On page one', 'Annotation 3: No page recorded']);
    await next();
    await screen.findByRole('img', { name: 'Hero, page 2' });
    await screen.findByTestId('annotation-layer');
    expect(names()).toEqual(['Annotation 2: On page two']);
    await next();
    await screen.findByRole('img', { name: 'Hero, page 3' });
    await screen.findByTestId('annotation-layer');
    expect(names()).toEqual(['Annotation 4 (resolved): On page three']);
  });

  it('records the page being shown in the anchor of what is drawn', async () => {
    renderStage({ ...doc });
    await userEvent.click(await screen.findByRole('button', { name: 'Annotate' }));
    let layer = await screen.findByTestId('annotation-layer');
    fireEvent.pointerDown(layer, ptr(200, 90));
    fireEvent.pointerMove(layer, ptr(300, 150));
    fireEvent.pointerUp(layer, ptr(300, 150));
    expect(probe.ui?.draftAnchor).toEqual({ type: 'region', x: 0.25, y: 0.2, w: 0.25, h: 0.3, page: 1 });

    await next();
    await screen.findByRole('img', { name: 'Hero, page 2' });
    layer = await screen.findByTestId('annotation-layer');
    // the pending outline belongs to page 1 and is not drawn on page 2; annotate mode survives paging
    expect(layer.querySelector('.dam-annot__pending')).toBeNull();
    expect(layer).toHaveAttribute('data-annotating', 'true');
    fireEvent.pointerDown(layer, ptr(300, 150));
    fireEvent.pointerUp(layer, ptr(300, 150));
    expect(probe.ui?.draftAnchor).toEqual({ type: 'point', x: 0.5, y: 0.5, page: 2 });
    expect(layer.querySelector('.dam-annot__pending--pin')).not.toBeNull();
  });

  it('registers showPage so a thread card can jump to its page, and ignores pages that do not exist', async () => {
    renderStage({ ...doc, threads });
    await screen.findByRole('img', { name: 'Hero, page 1' });
    act(() => probe.ui?.showPage(3));
    expect(await screen.findByRole('img', { name: 'Hero, page 3' })).toBeInTheDocument();
    expect(screen.getByText('Page 3 of 3')).toBeInTheDocument();
    act(() => probe.ui?.showPage(9));
    expect(screen.getByText('Page 3 of 3')).toBeInTheDocument();
  });

  it('brings the page forward when a thread on another page is selected (deep link or click)', async () => {
    renderStage({ ...doc, threads });
    await screen.findByRole('img', { name: 'Hero, page 1' });
    act(() => probe.ui?.selectThread('b'));
    expect(await screen.findByRole('img', { name: 'Hero, page 2' })).toBeInTheDocument();
    await screen.findByTestId('annotation-layer');
    expect(screen.getByRole('button', { name: /^Annotation 2/ })).toHaveAttribute('data-selected', 'true');
    // paging away by hand afterwards does not snap back
    await userEvent.click(screen.getByRole('button', { name: 'Previous page' }));
    expect(await screen.findByRole('img', { name: 'Hero, page 1' })).toBeInTheDocument();
  });

  it('has no accessibility violations', async () => {
    const { container } = renderStage({ ...doc, threads });
    await screen.findByTestId('annotation-layer');
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('an unpublished document', () => {
  it('shows page 1 only: annotations of page 1 are drawn and new ones are recorded as page 1', async () => {
    renderStage({ type: 'document', status: 'draft', threads });
    await screen.findByRole('img', { name: 'Hero' });
    expect(screen.queryByRole('button', { name: 'Next page' })).not.toBeInTheDocument();
    await userEvent.click(await screen.findByRole('button', { name: 'Annotate' }));
    const layer = await screen.findByTestId('annotation-layer');
    expect(names()).toEqual(['Annotation 1: On page one', 'Annotation 3: No page recorded']);
    fireEvent.pointerDown(layer, ptr(200, 90));
    fireEvent.pointerUp(layer, ptr(200, 90));
    expect(probe.ui?.draftAnchor).toEqual({ type: 'point', x: 0.25, y: 0.2, page: 1 });
  });
});
