import {
  addToQueue,
  compactMetadata,
  inferType,
  MAX_FILES,
  problemFor,
  titleFromName,
  typeChoices,
  withType,
} from './uploadQueue';

describe('type rules (mirror of the server policy)', () => {
  it('infers the type from the extension and leaves a zip for the uploader to choose', () => {
    expect(inferType('a.JPG')).toBe('image');
    expect(inferType('a.mp4')).toBe('video');
    expect(inferType('a.pdf')).toBe('document');
    expect(inferType('a.glb')).toBe('3d');
    expect(inferType('a.zip')).toBe('other');
    expect(inferType('a.weird')).toBe('other');
    expect(inferType('a.exe')).toBeNull();
    expect(inferType('noextension')).toBeNull();
  });
  it('offers every type an extension fits, best guess first and "other" last', () => {
    expect(typeChoices('a.pdf')).toEqual(['document', 'manual', 'other']);
    expect(typeChoices('a.zip')).toEqual(['manual', '3d', 'html5', 'other']);
    expect(typeChoices('a.jpg')).toEqual(['image', 'other']);
    expect(typeChoices('a.exe')).toEqual([]);
  });
  it('flags files the server would refuse', () => {
    expect(problemFor('a.exe', 10, 'other')).toBe('blocked');
    expect(problemFor('noext', 10, 'other')).toBe('noExtension');
    expect(problemFor('a.jpg', 0, 'image')).toBe('empty');
    expect(problemFor('a.jpg', 3 * 1024 ** 3, 'image')).toBe('tooLarge');
    expect(problemFor('a.mp4', 3 * 1024 ** 3, 'video')).toBeNull(); // videos may be up to 20 GB
    expect(problemFor('a.jpg', 100, 'image')).toBeNull();
  });
  it('derives a title from the file name', () => {
    expect(titleFromName('Spring hero.final.jpg')).toBe('Spring hero.final');
    expect(titleFromName('.hidden')).toBe('.hidden');
  });
});

describe('queue', () => {
  it('skips a file that is already queued and re-checks a file when its type changes', () => {
    const a = new File(['x'], 'a.pdf', { lastModified: 5 });
    const first = addToQueue([], [a]);
    expect(first.queue).toHaveLength(1);
    const again = addToQueue(first.queue, [new File(['x'], 'a.pdf', { lastModified: 5 }), new File(['yy'], 'b.png')]);
    expect(again.duplicates).toBe(1);
    expect(again.queue.map((q) => q.file.name)).toEqual(['a.pdf', 'b.png']);
    expect(withType(first.queue[0]!, 'manual').type).toBe('manual');
  });
  it('stops at the per-batch limit and reports how many were left out', () => {
    const many = Array.from({ length: MAX_FILES + 3 }, (_, i) => new File(['x'], `f${i}.png`, { lastModified: i }));
    const { queue, overLimit } = addToQueue([], many);
    expect(queue).toHaveLength(MAX_FILES);
    expect(overLimit).toBe(3);
  });
});

describe('compactMetadata', () => {
  it('keeps only what the user actually filled in', () => {
    expect(compactMetadata({ credit: '', tags: [], title: { en: '', hi: '' }, year: null })).toBeNull();
    expect(compactMetadata({ credit: 'Acme', tags: [], title: { en: 'Hi', hi: '' }, ok: false })).toEqual({
      credit: 'Acme',
      title: { en: 'Hi' },
      ok: false,
    });
  });
});
