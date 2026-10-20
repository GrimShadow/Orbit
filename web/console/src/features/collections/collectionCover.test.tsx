import { cleanup, fireEvent, render } from '@testing-library/react';
import { CollectionCover } from './CollectionCover';

afterEach(cleanup);

describe('CollectionCover', () => {
  it('leaves a thumbnail that fails to load out of the mosaic', () => {
    const { container } = render(
      <CollectionCover images={['https://cdn.test/a.webp', 'https://cdn.test/b.webp']} smart={false} />,
    );
    expect(container.querySelectorAll('img')).toHaveLength(2);
    fireEvent.error(container.querySelector('img[src="https://cdn.test/a.webp"]')!);
    expect(container.querySelectorAll('img')).toHaveLength(1);
    expect(container.querySelector('img')).toHaveAttribute('src', 'https://cdn.test/b.webp');
  });

  it('falls back to the placeholder glyph when every thumbnail fails, never a broken image', () => {
    const { container } = render(<CollectionCover images={['https://cdn.test/a.webp']} smart={false} />);
    fireEvent.error(container.querySelector('img')!);
    expect(container.querySelector('img')).toBeNull();
    expect(container.querySelector('.dam-cover--empty svg')).not.toBeNull();
  });
});
